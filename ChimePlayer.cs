using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Orbweaver
{
    // Plays one short sound file to completion, capped at five seconds, as a
    // process of its own — never TextToSpeech's tracked process, and never
    // stoppable by the same Cancel() a speak button reaches for.
    //
    // Play is the single entry point every caller in this app uses to make
    // any kind of chime — a scan chime via TurnSounds, a Settings preview,
    // the summary-fallback chime, whichever — so tracking every live
    // process centrally here, rather than in whichever caller happened to
    // start it, is what lets StopAll below mean all of them at once.
    //
    // That separation is not tidiness. A chime and a spoken reply are allowed
    // to overlap by design — TurnSoundPolicy's busy-speech fallback exists
    // for the one case that would be jarring (a vibe summary cutting in on
    // speech already playing), and everywhere else a Glass chime landing
    // half a second into an unrelated utterance is fine. Sharing
    // TextToSpeech's process slot would make the speak button's stop control
    // silently kill chimes too, which is a coupling nobody asked for and the
    // opposite of what "a chime never cancels speech" means.
    //
    // The child-process shape below is deliberately the same one
    // TextToSpeech.Speak uses — /usr/bin/afplay on macOS, PowerShell's
    // Media.SoundPlayer on Windows, a pid captured before anything that could
    // dispose the Process object out from under it, KillTree rather than a
    // bare Kill(). Copying it rather than inventing a second shape means a
    // fix to one process-management bug here is recognisable as the same fix
    // TextToSpeech already needed, not a new kind of bug to learn.
    internal static class ChimePlayer
    {
        internal static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(5);

        // Named rather than a literal repeated in two places, and public
        // enough for a test to reference it without duplicating the string.
        internal const string ChimeEnvVar = "CLAUDEBUDDY_CHIME";

        // QA (CB-167): the whole Windows script, as a value rather than
        // built inline inside Play() — that is what lets
        // WindowsStartInfoFor below be asserted on without a Windows
        // machine to run it on. Two things are deliberate about its shape.
        // First, the path is read back only through $env: and is never
        // interpolated into this string — see WindowsStartInfoFor's own
        // comment for what that closes. Second, the empty-variable guard
        // comes before anything else: a bug that ever calls this with
        // nothing to play (the C# side already only calls Play with a
        // resolved path, but the script is its own contract) exits loudly
        // with a real error instead of either doing nothing silently or
        // leaving Media.SoundPlayer's own opaque failure as the only sign
        // anything went wrong.
        internal const string WindowsScript =
            "if ([string]::IsNullOrEmpty($env:CLAUDEBUDDY_CHIME)) { " +
            "Write-Error 'CLAUDEBUDDY_CHIME is not set'; exit 1 }; " +
            "(New-Object Media.SoundPlayer $env:CLAUDEBUDDY_CHIME).PlaySync()";

        // The seam. A scan-level test can assert exactly which path was
        // decided on without a sound card, a process, or even /usr/bin/afplay
        // existing on the runner — the same pattern as
        // SpeechRequest.UtteranceForTests and SpeechSummary.SummarizerForTests.
        // Set and cleared by the test, never by production code.
        internal static Action<string>? PlayForTests;

        // CB-168 (Ines, round 2 of this pass): the narrow seam this class
        // was missing. Everything in this file that decides WHICH processes
        // are live, WHICH ONE gets killed WHEN, and IN WHAT ORDER — StopAll,
        // the preview channel's kill-then-replace, the superseded check —
        // is ordinary code with no OS dependency at all. The OS dependency
        // is exactly three operations on a running child: ask its id, ask
        // whether it has exited, and kill its whole tree. IChimeProcess is
        // those three operations and nothing else.
        //
        // Before this seam, the only way to prove StopAll (or the preview
        // channel) killed the right process was to spawn a REAL process and
        // watch it actually die — which is what made
        // AStopAllSweptAcrossPlaysSpawnLeavesNoChimeRunning and its three
        // siblings racy under load in the first place (see
        // ChimePlayerTests' own history): the thing under test (which
        // process gets a kill request) and the thing making the test flaky
        // (how long the OS takes to tear a process down) were the same
        // process, so there was no way to assert on one without also
        // waiting on the other. Splitting them apart is what actually fixes
        // that, rather than widening the margin again: ChimePlayerTests now
        // asserts the logic against FakeChimeProcess (a kill is
        // *requested*, on the right target, in the right order — no wall
        // clock anywhere), and only ChimePlayerIntegrationTests below still
        // spawns a real afplay, to prove RealChimeProcess's three OS-facing
        // members actually do what they claim.
        internal interface IChimeProcess : IDisposable
        {
            int Id { get; }
            bool HasExited { get; }
            bool WaitForExit(int milliseconds);
            void Kill();
        }

        // The real implementation, wrapping a Process that has already been
        // started (StartProcess below is the only production caller, and it
        // only wraps one once Process.Start has succeeded). Every member
        // touches the OS, so every member is excluded from coverage — the
        // same reasoning TryStart/WaitAndKillIfStillRunning/KillTree used
        // before this seam existed; this class is what their OS-facing
        // halves became. Kill() is copied from the old KillTree rather than
        // shared with TextToSpeech.KillTree, for the same reason the old
        // comment gave: this one has no _speaking field to clear and no
        // SpeakState to move back to Idle.
        private sealed class RealChimeProcess : IChimeProcess
        {
            private readonly Process _proc;
            internal RealChimeProcess(Process proc) => _proc = proc;

            [ExcludeFromCodeCoverage]
            public int Id => _proc.Id;

            [ExcludeFromCodeCoverage]
            public bool HasExited => _proc.HasExited;

            [ExcludeFromCodeCoverage]
            public bool WaitForExit(int milliseconds) => _proc.WaitForExit(milliseconds);

            [ExcludeFromCodeCoverage]
            public void Kill()
            {
                int pid;
                try
                {
                    pid = _proc.Id;
                }
                catch
                {
                    return;
                }

                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        using var taskkill = Process.Start(new ProcessStartInfo("taskkill")
                        {
                            ArgumentList = { "/PID", pid.ToString(), "/T", "/F" },
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        });
                        taskkill?.WaitForExit(3000);
                    }
                    catch { /* fall through to Kill below */ }
                }

                try
                {
                    if (!_proc.HasExited) _proc.Kill(entireProcessTree: true);
                }
                catch { /* already gone, or the object is disposed — both fine here */ }
            }

            [ExcludeFromCodeCoverage]
            public void Dispose() => _proc.Dispose();
        }

        // A second, distinct seam from PlayForTests above: PlayForTests
        // bypasses BuildProcess/tracking entirely (there is no real process
        // behind a substituted chime to track), so it has nothing to offer
        // a test of StopAll's or the preview channel's own kill/track
        // decisions. This one instead substitutes what "the process"
        // Play/PlayOnePreview go on to track and (maybe) kill actually is —
        // every decision around it still runs for real. Left null in
        // production, where StartProcess always builds a real
        // RealChimeProcess.
        internal static Func<string, IChimeProcess?>? ProcessFactoryForTests;

        // CB-168: the real-process boundary, guarded the same way and for
        // the same reason as TextToSpeech.SilenceForTests — see that
        // field's own comment. Placed here, after ProcessFactoryForTests
        // rather than at Play/PlayOnePreview's own entry points, because
        // StartProcess is now the one place either of them actually reaches
        // BuildProcess/TryStart — a fake-based ChimePlayerTests case that
        // sets ProcessFactoryForTests never gets here at all, so this only
        // silences the path nothing has opted into. Every test assembly's
        // TestBootstrap sets this true; ChimePlayerIntegrationTests' two
        // genuinely real-afplay tests opt back in explicitly (silent WAVs,
        // same as ChimePlayerTests' own real-path cases did before this
        // class grew a fake seam).
        //
        // Defaults false: production code never sets this.
        internal static bool SilenceForTests;

        private static IChimeProcess? StartProcess(string path)
        {
            var factory = ProcessFactoryForTests;
            if (factory is not null) return factory(path);

            if (SilenceForTests) return null;

            var proc = BuildProcess(path);
            if (proc is null) return null;

            return FinishStarting(proc, TryStart(proc));
        }

        // What StartProcess does with TryStart's real answer — pulled out so
        // a test can drive both outcomes (started, and failed-to-start)
        // without a real OS process launch. TryStart itself is the only
        // remaining line here that needs the real OS (see its own
        // exclusion); everything this method decides from its answer is
        // ordinary code. Takes the already-built Process rather than a path,
        // so a test can hand it a plain, never-started `new Process()` —
        // RealChimeProcess's own constructor only stores the reference, it
        // never touches Process.Id (which throws before Start) — and assert
        // on the failure branch without disposing anything real.
        internal static IChimeProcess? FinishStarting(Process proc, bool started)
        {
            if (!started)
            {
                proc.Dispose();
                return null;
            }

            return new RealChimeProcess(proc);
        }

        // Round 3(d): a second test seam, distinct from PlayForTests above.
        // PlayForTests returns before Play ever reaches the process-
        // tracking code at all — there is no real process behind a
        // substituted chime for it to track — so it has nothing to offer a
        // test of StopAll's own new behaviour: that it kills every
        // concurrently-tracked process, not just one. This registers a real
        // process through the exact same Add path Play uses, so a test can
        // hand it something harmless and long-running (never real audio)
        // and prove StopAll actually kills it.
        //
        // CB-168: the Process-typed overload this used to have alongside
        // the IChimeProcess one below is gone — ChimePlayerIntegrationTests
        // never called it (it builds real processes through StartProcess
        // itself, not this seam), and nothing else did either. A test seam
        // with zero callers is clutter with a comment attached, not
        // coverage.
        internal static void TrackForTests(IChimeProcess proc)
        {
            lock (PlayingGate) _live.Add(proc);
        }

        // QA (CB-167), reworked in round 2 (finding 6): every process this
        // class currently has running, not just one. A single Process?
        // slot was only ever correct while every caller funnelled through
        // TurnSounds' own _chimeChain, which serialises scan chimes so
        // there is never more than one in flight — but Play is the shared
        // entry point for every kind of chime this app makes, and two of
        // them do not go through that chain at all: a Settings preview
        // (SettingsWindow.cs) fires directly off a UI click, and the
        // summary-fallback chime (TurnSounds.SpeakSummaryOrFallbackAsync)
        // can start while an unrelated scan chime is still mid-playback on
        // the chain. A single slot silently dropped tracking of whichever
        // one wasn't "current" — StopAll on Quit would kill one process and
        // leave the other one making noise. A set is what actually holds
        // "everything Play is running right now," so StopAll can mean all
        // of it regardless of which caller started which process.
        private static readonly HashSet<IChimeProcess> _live = new();
        private static readonly object PlayingGate = new();

        // QA round 3, finding 4 (LOW): a Settings preview used to go
        // straight through Play() the same as any other chime, so stepping
        // through the sound picker with ordinary, human-paced pauses
        // between choices — well past SettingsWindow's 250ms debounce, each
        // one its own flush — started a fresh, independent process every
        // time with nothing to stop the previous one still playing
        // underneath it. Four choices a person actually paused on sounded
        // like four overlapping chimes in a real run.
        //
        // PlayPreview below is a channel rather than a second Play(): only
        // one worker is ever calling into an actual preview, and a request
        // that arrives while it is busy replaces whatever the worker was
        // going to play next rather than queuing alongside it — so several
        // requests landing while one is playing collapse into "play
        // whichever was requested most recently, the moment the current
        // one is free." When the worker is genuinely blocked inside a real
        // process's WaitForExit, "free" is hurried along: a new request
        // kills the process already playing (KillTree) before the worker
        // moves on, which is also why this still needs its own slot here
        // rather than reusing whichever Process a scan chime happens to be
        // running — the two must never contend to kill each other.
        private static string? _nextPreviewPath;
        private static bool _previewWorkerRunning;
        private static IChimeProcess? _currentPreview;

        // The one place that both kills a preview already playing and
        // records its replacement, so PlayOnePreview's production path and
        // SetCurrentPreviewForTests below can share it rather than risk the
        // two drifting apart. Still added to _live via the same field
        // every other kind of chime uses, so StopAll reaches a preview
        // exactly as it already reaches a scan chime or the summary
        // fallback — this only adds a second, earlier kill path ahead of
        // it, not a second set to keep in sync.
        //
        // Round 4, item 2: `next` must already have started before this is
        // called — every real caller (PlayOnePreview) now calls this only
        // after its own Start succeeds, and the test seam below hands it
        // an already-started process for the same reason. `previous` is
        // safe to KillTree here precisely because IT went through this
        // same call, after ITS OWN Start, whenever it became current.
        private static void KillPreviousAndTrackNewPreview(IChimeProcess next)
        {
            lock (PlayingGate)
            {
                if (_currentPreview is { } previous) previous.Kill();
                _currentPreview = next;
                _live.Add(next);
            }
        }

        // Round 3 finding 4's version of TrackForTests above, for the
        // preview slot specifically: a test hands this a real, harmless,
        // long-running process (never real audio) through the exact path
        // PlayPreview itself uses to decide whether to kill a predecessor,
        // and can then prove that registering a second one kills the
        // first — see ChimePlayerTests.
        //
        // CB-168: the Process-typed overload this used to have is gone, for
        // the same reason TrackForTests' was — zero callers, real or test.
        internal static void SetCurrentPreviewForTests(IChimeProcess next) =>
            KillPreviousAndTrackNewPreview(next);

        // Round 4 (CB-167): once StopAll has run, this class must never
        // start anything new — a chime already queued behind it (TurnSounds'
        // own _chimeChain, or a FirePending timer that happens to fire
        // during the app's own unwind) starting a fresh process right after
        // StopAll just killed every existing one would defeat the entire
        // point of finding 5/round 3(d): the app would still be making
        // noise after Quit. Sticky for the rest of the process's life by
        // design — there is no "un-stop" in production, only
        // ResetStoppedForTests below.
        private static bool _stopped;

        // Its own property, kept out of Play's own exclusion, so this one
        // guard is measured directly rather than folded into a method that
        // starts a real subprocess and cannot be run under coverage.
        internal static bool IsStopped { get { lock (PlayingGate) return _stopped; } }

        // Test seam: _stopped is deliberately sticky in production, but a
        // test suite runs many cases in one process, and the next test's
        // Play calls must not silently no-op forever just because an
        // earlier test proved StopAll works.
        internal static void ResetStoppedForTests() { lock (PlayingGate) _stopped = false; }

        // Builds the Windows ProcessStartInfo on its own, callable and
        // assertable from a test even though Play itself is excluded from
        // coverage. The path never appears in ArgumentList or in
        // WindowsScript — it only ever reaches PowerShell through the
        // environment, which is what makes this safe with any character a
        // filesystem allows a path to contain. The interpolated version
        // this replaced escaped a bare U+0027 apostrophe by doubling it, but
        // PowerShell's tokenizer also accepts the Unicode "smart" quotes
        // U+2018 through U+201B as string delimiters — a path containing
        // one of those (a OneDrive folder renamed with a curly quote, a
        // copy-pasted file name) could close the quoted string early and run
        // whatever followed as a second command. A leading '-' is the other
        // shape QA asked this be checked against: because the path is a
        // variable's *value*, substituted after PowerShell has already
        // parsed $env:CLAUDEBUDDY_CHIME as one positional argument token,
        // the runtime value is never re-parsed as a flag the way a literal
        // "-something" written directly in the script text could be.
        // TextToSpeech's PowerShell escaping had the identical smart-quote
        // hole; CB-184 fixed it separately the same way (text and voice
        // now travel in the environment).
        internal static ProcessStartInfo WindowsStartInfoFor(string path)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                ArgumentList = { "-NoProfile", "-Command", WindowsScript },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            startInfo.EnvironmentVariables[ChimeEnvVar] = path;
            return startInfo;
        }

        // Round 5, finding 3: no longer excluded — the real subprocess
        // work this used to be excluded along with now lives entirely in
        // TryStart/WaitAndKillIfStillRunning/KillTree, each still excluded
        // on its own for genuinely starting, waiting on or killing a real
        // OS process. Everything else here is a decision (which seam, is
        // this stopped, track or don't), and belongs measured.
        internal static void Play(string path)
        {
            // Checked once up front for both paths: a chime queued behind
            // StopAll must not start at all, rather than start and be
            // killed by the re-check below a moment later (which on the
            // real path is an audible click after Quit). The re-check after
            // TryStart still has to exist — this one only closes the easy
            // case, not a StopAll landing between here and the tracking.
            if (IsStopped) return;

            var seam = PlayForTests;
            if (seam is not null)
            {
                seam(path);
                return;
            }

            // CB-168: StartProcess is BuildProcess+TryStart, wrapped as an
            // IChimeProcess — the seam that lets ChimePlayerTests substitute
            // a FakeChimeProcess here and assert on the decisions below with
            // no real process and no wall clock. See StartProcess's own
            // comment.
            var proc = StartProcess(path);
            if (proc is null) return;

            // Round 5, finding 2: tracked only now that Start has actually
            // succeeded — mirrors PlayOnePreview's own round-4, item-2 fix,
            // which this method never got. The old order (Add, then Start)
            // left a real ~20ms window where a concurrent StopAll's own
            // kill call read a pid off a Process that had not actually
            // started yet — Process.Id throws in that state, silently
            // caught — so the chime, once it did start moments later, ran
            // to completion unkilled even though _stopped had already gone
            // true. Measured 40/40 at 0-6ms. Re-checked again right here,
            // the same as PlayOnePreview does, rather than trusted from
            // whatever the seam-side check (or nothing, on the real path)
            // saw before StartProcess ran.
            lock (PlayingGate) _live.Add(proc);

            if (IsStopped)
            {
                proc.Kill();
            }
            else
            {
                WaitAndKillIfStillRunning(proc);
            }

            ClearIfCurrent(proc);
            proc.Dispose();
        }

        // Round 3 finding 4: the preview channel. Cheap and quick to call —
        // a lock, at most one KillTree, and either a field assignment or
        // (only the first time in a while) starting the worker — so unlike
        // Play() above there is no need for a caller to wrap this in its
        // own Task.Run the way SettingsWindow used to; see
        // PlayPendingPreview's own comment there.
        //
        // Round 5, finding 3: no longer excluded, same reasoning as Play's
        // — the decisions here (which process is the victim, whether the
        // worker needs starting) are ordinary code; only the two Task.Run
        // targets actually touch a real process, and KillTree/
        // RunPreviewWorker (via PlayOnePreview) carry their own exclusions.
        internal static void PlayPreview(string path)
        {
            // Round 4: the preview channel is exactly the kind of "queued
            // chime" that must not start anything once StopAll has run —
            // it holds its own request behind a lock the same way
            // TurnSounds' _chimeChain holds a scan chime, and a request
            // queued (or already sitting queued) during the app's own
            // unwind must not still spawn a fresh process afterward.
            if (IsStopped) return;

            // Round 4, item 3: PlayPreview is called straight from
            // SettingsWindow's SelectionChanged/Click, on the UI thread —
            // and KillTree, on Windows, runs taskkill and can wait up to
            // 3 s for it. The lock below only ever swaps state (which
            // process is the victim, whether the worker needs starting);
            // the actual kill runs on its own background task, never on
            // this thread.
            //
            // Round 5, finding 1: round 4 briefly chained the kill and the
            // worker-start onto one ordered _previewChain task instead of
            // two independent ones, to additionally guarantee their
            // relative order — but that chain had a real regression.
            // `startWorker`'s continuation runs RunPreviewWorker itself,
            // which is long-running: it blocks inside WaitAndKillIfStill-
            // Running for as long as whatever it's currently playing takes
            // (up to the 5 s cap). A kill queued onto the SAME chain while
            // the worker was already mid-loop had to wait for that entire
            // loop to finish first, turning a ~45 ms cutoff into one that
            // waited out almost the full cap — measured about 4.7 s versus
            // about 45 ms. The two independent tasks below don't have that
            // risk, and don't need chaining to be correct: `victim` can
            // only be non-null while the worker already holds it as
            // _currentPreview, which by construction means
            // _previewWorkerRunning is already true — so `startWorker` and
            // `victim is not null` can never both be true from the same
            // snapshot, and there is nothing for the two tasks to actually
            // race over in the first place.
            IChimeProcess? victim;
            bool startWorker;
            lock (PlayingGate)
            {
                _nextPreviewPath = path;
                victim = _currentPreview;
                startWorker = !_previewWorkerRunning;
                _previewWorkerRunning = true;
            }

            if (victim is not null) _ = Task.Run(() => victim.Kill());
            if (startWorker) _ = Task.Run(RunPreviewWorker);
        }

        // The worker: picks up whichever path is currently the most
        // recently requested, plays it to completion (or until the next
        // request kills it early), and loops — stopping only once nothing
        // new arrived while it was busy. Never more than one of these
        // running at a time (PlayPreview only starts it when
        // _previewWorkerRunning was false), which is what makes "only one
        // preview ever calls into ChimePlayer.Play/the test seam at once"
        // true by construction rather than by timing.
        // Round 5, finding 3: no longer excluded — the loop and its exit
        // condition are ordinary decisions; PlayOnePreview and KillTree
        // carry their own exclusions for the real process work.
        private static void RunPreviewWorker()
        {
            while (true)
            {
                string path;
                lock (PlayingGate)
                {
                    if (_nextPreviewPath is null)
                    {
                        _previewWorkerRunning = false;
                        return;
                    }

                    path = _nextPreviewPath;
                    _nextPreviewPath = null;
                }

                PlayOnePreview(path);
            }
        }

        // One iteration of the worker's loop. Seam-checked the same way
        // Play() is — a test's seam call still runs on this same
        // background worker thread, never the caller's — but with no real
        // process behind it there is nothing for KillPreviousAndTrackNewPreview
        // to register or for a later request to kill; that half of this
        // fix is proven with real processes instead (see
        // ChimePlayerTests.PlayPreviewKillsTheLivePreviewProcessBeforeTrackingTheNext),
        // and SteppingThroughPreviewsDoesNotStackOverlappingPlayback proves
        // the coalescing above, which needs no real process to be true.
        // Round 5, finding 3: no longer excluded, same reasoning as
        // Play's — TryStart/WaitAndKillIfStillRunning/KillTree carry their
        // own exclusions for the real process work; this method's own
        // lines are all decisions.
        private static void PlayOnePreview(string path)
        {
            // Same up-front check as Play's, for the same reason.
            if (IsStopped) return;

            var seam = PlayForTests;
            if (seam is not null)
            {
                seam(path);
                return;
            }

            var proc = StartProcess(path);
            if (proc is null) return;

            // Round 4, item 2: tracked — and the previous preview killed —
            // only now that Start has actually succeeded. The old order
            // called this (and so set _currentPreview, added to _live)
            // BEFORE Start, so a kill call landing in that exact gap read a
            // pid off a Process that had never really started at all —
            // Process.Id throws before Start runs, and that catch swallows
            // it silently, leaving the "previous" preview to run to
            // completion unkilled.
            KillPreviousAndTrackNewPreview(proc);

            // QA round 6, F1: a request that landed while this preview was
            // still inside BuildProcess/TryStart found nothing tracked to
            // kill (victim null) and the worker already running (no
            // startWorker), so nothing cut this one off — it played its full
            // length with the newer choice queued behind it. A non-null
            // _nextPreviewPath once tracking is done means exactly that
            // happened; this preview is already stale. Anything arriving
            // after this read finds proc as _currentPreview and kills it
            // through PlayPreview's own victim path instead. Read under the
            // lock, killed outside it, like PlayPreview's.
            bool superseded;
            lock (PlayingGate) superseded = _nextPreviewPath is not null;

            // The other half of item 2: a stop can land in the gap between
            // Start succeeding and the tracking lock just above — checked
            // again right here, rather than trusted from whatever
            // PlayPreview's own entry check saw a moment earlier, so a
            // process that only just started is killed immediately instead
            // of being left to run its full duration.
            if (IsStopped || superseded)
            {
                proc.Kill();
            }
            else
            {
                WaitAndKillIfStillRunning(proc);
            }

            lock (PlayingGate)
            {
                _live.Remove(proc);
                if (ReferenceEquals(_currentPreview, proc)) _currentPreview = null;
            }

            proc.Dispose();
        }

        // The per-OS process shape Play and PlayOnePreview both start —
        // pulled out once there were two call sites, so a fix to one
        // doesn't have to be remembered for the other. Null off any
        // platform this app doesn't make a sound on.
        //
        // Round 5, finding 3: no longer excluded, and internal rather than
        // private so a test can assert on it directly the same way
        // WindowsStartInfoFor already is — building a Process/
        // ProcessStartInfo has no OS side effect of its own (nothing here
        // calls Start), so there is nothing about it that needs a real
        // machine to prove. Only the branch this runner can actually take
        // (macOS) is asserted on directly; the Windows branch is
        // WindowsStartInfoFor's own tests, and the "no sound on this
        // platform" branch has no real machine to run it on either.
        //
        // CB-200: every chime goes through here, previews and the summary
        // fallback included, so this is the one place the Alert level is
        // applied — read per chime, so a slider move is heard on the very
        // next one, including the preview beside it.
        internal static Process? BuildProcess(string path) =>
            BuildProcess(path, OrbweaverSettings.AlertVolume);

        internal static Process? BuildProcess(string path, double volume)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/afplay",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                // Nothing at full volume, so the argv is exactly the one every
                // earlier build ran.
                foreach (var argument in AudioVolume.AfplayArguments(volume))
                {
                    startInfo.ArgumentList.Add(argument);
                }

                startInfo.ArgumentList.Add(path);
                return new Process { StartInfo = startInfo };
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new Process { StartInfo = WindowsStartInfoFor(WindowsPlayablePath(path, volume)) };
            }

            return null;
        }

        // What Media.SoundPlayer should open for `path` at `volume`: a scaled
        // copy (see AudioVolume.ScaleWav for why the samples are scaled rather
        // than the mixer), or the original when no copy could be made.
        //
        // Falling back to the original plays that one chime at full volume,
        // which is the lesser failure: a quieter chime that fails to play at
        // all is silence, and silence is what a turn sound exists to not be.
        // It says so on stderr rather than doing it quietly. Only a file
        // ScaleWav cannot read reaches it — every sound Windows itself ships
        // is 16-bit PCM, which it can.
        //
        // Not platform-gated, so both CI legs run it; only BuildProcess's one
        // Windows line above is reachable on Windows alone.
        internal static string WindowsPlayablePath(string path, double volume, string? cacheDirectory = null)
        {
            var playable = AudioVolume.ScaledCopy(path, volume, cacheDirectory ?? AudioVolume.ChimeCacheDirectory);
            if (playable is not null) return playable;

            Console.Error.WriteLine(
                $"{Brand.DisplayName}: {Path.GetFileName(path)} is not a WAV this can make quieter; playing it at full volume");
            return path;
        }

        // Starts `proc` — the one line in this whole class that actually
        // launches a real OS process, which is why this stays excluded
        // even though Play/PlayOnePreview around it no longer are. Split
        // out in round 4 so PlayOnePreview could fit its own tracking
        // logic between Start succeeding and the wait beginning; Play now
        // does the same.
        [ExcludeFromCodeCoverage]
        private static bool TryStart(Process proc)
        {
            try
            {
                return proc.Start();
            }
            catch
            {
                return false;
            }
        }

        // CB-168: no longer excluded. WaitForExit/Kill are IChimeProcess
        // members — RealChimeProcess's implementations of those stay
        // excluded for touching the OS, but the decision made here ("still
        // running after MaxDuration? kill it") is ordinary code, reachable
        // and asserted on through FakeChimeProcess with no real wait at all
        // (a fake's WaitForExit returns immediately).
        private static void WaitAndKillIfStillRunning(IChimeProcess proc)
        {
            if (!proc.WaitForExit((int)MaxDuration.TotalMilliseconds))
            {
                proc.Kill();
            }
        }

        // Removing from a set rather than nulling a slot means this is safe
        // to call once per process regardless of how many others are still
        // live at the same moment — no "only the current one" ambiguity to
        // get wrong the way a single Process? slot had. Round 5, finding 3:
        // no longer excluded — a lock and a Remove call, nothing that
        // touches the OS.
        private static void ClearIfCurrent(IChimeProcess proc)
        {
            lock (PlayingGate) _live.Remove(proc);
        }

        // QA (CB-167), renamed in round 2 (finding 6) from Cancel to StopAll
        // to say what it now actually does: every process Play currently has
        // running, not one. Still reached from the same place — the app's
        // own Quit path, TrayController.Shutdown — so a chime still
        // mid-playback (a scan chime, a Settings preview, the
        // summary-fallback chime, whichever) does not keep the machine
        // making noise once the app itself is gone. A sudden SIGKILL or a
        // crash cannot be caught here, the same limit TextToSpeech.Cancel
        // already lives with, but an ordinary Quit now can be, for all of
        // them at once.
        //
        // Round 5, finding 3: no longer excluded — setting the flag and
        // calling KillEverythingLive are both ordinary code; the real
        // process work KillEverythingLive triggers is RealChimeProcess.Kill's
        // own exclusion, not this method's.
        internal static void StopAll()
        {
            // Round 4: _stopped is set in its own lock acquisition, ahead of
            // KillEverythingLive's separate one below. That is safe as two
            // steps because Play and PlayOnePreview each re-check IsStopped
            // *after* adding to _live (round 5): if this line runs before
            // their add, their re-check sees it and they kill their own
            // process; if it runs after, KillEverythingLive's snapshot
            // already holds that process. Either way nothing started is
            // left running.
            lock (PlayingGate) _stopped = true;

            KillEverythingLive();
        }

        // Round 4, item 4: the app's desktop.ShutdownRequested handler
        // calls this instead of StopAll. ShutdownRequested fires for a
        // quit that can still be cancelled — the event's own Cancel
        // property, or macOS refusing for a reason of its own — unlike
        // Exit, which fires only once shutdown is genuinely proceeding.
        // Killing whatever is currently playing is still the right thing
        // to do here: the user asked to quit, and a chime shouldn't
        // outlive that choice even if the app itself does. But setting the
        // sticky _stopped flag would leave a *cancelled* quit permanently
        // deaf for the rest of the process's life — silencing every chime
        // from then on for a quit that never actually happened — which is
        // wrong in a way StopAll's own sticky contract is not: only a
        // shutdown that actually goes through may make that call.
        //
        // Round 5, finding 3: no longer excluded — a one-line delegation
        // to KillEverythingLive.
        internal static void KillCurrentlyPlayingForCancellableShutdown() => KillEverythingLive();

        // CB-298: the mute's stop. Same kill as the shutdown path above, under
        // a name that says what it is for — and, like it, non-sticky: StopAll
        // would leave the app deaf after the user unmutes.
        internal static void StopPlaying() => KillEverythingLive();

        // CB-168: no longer mentions KillTree — that method's real-OS half
        // moved onto RealChimeProcess.Kill (excluded there); the
        // snapshot-and-clear-and-call-Kill sequence here is ordinary code,
        // and is exactly what ChimePlayerTests' fake-based StopAll tests
        // exercise directly.
        private static void KillEverythingLive()
        {
            IChimeProcess[] victims;
            lock (PlayingGate)
            {
                victims = _live.ToArray();
                _live.Clear();
            }

            foreach (var victim in victims) victim.Kill();
        }
    }
}
