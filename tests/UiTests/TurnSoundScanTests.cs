using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-167's turn sounds over a real scan, the way SessionScanTests proves the
// orb lifecycle over one: TurnSignalsTests and TurnSoundPolicyTests already
// cover the rule in isolation, but nothing there proves the scan actually
// wires Observe/Prune/Deliver together correctly — that a real
// generating→idle transition read off two real status files plays a real
// chime exactly once, and that the cases the plan calls out by name (a
// startup burst, a backgrounded husk, a manual reset) genuinely stay quiet
// rather than merely being argued to.
//
// ChimePlayer.PlayForTests stands in for the sound card, same as
// SpeechRequest.UtteranceForTests does for the speech engine elsewhere in
// this suite — every test here proves what *would* have played without a
// process, a speaker or /usr/bin/afplay anywhere near the test host.
[Collection("Settings")]
public class TurnSoundScanTests : IDisposable
{
    private static readonly int LivePid = Environment.ProcessId;

    private readonly object _lock = new();
    private readonly List<string> _played = new();
    private TaskCompletionSource<bool>? _chimeSignal;

    // QA (CB-167) found ChimePlayer.Play blocking the UI thread and the fix
    // is to run it fire-and-forget on a background task — which means a
    // test can no longer assume a chime has already "played" the instant
    // ScanAndUpdate() returns. _chimeSignal is what closes that gap: set
    // right before an assertion needs it, completed by the seam below the
    // moment a real background Play call actually lands, awaited with a
    // bounded timeout so a genuine regression (nothing ever plays) fails
    // this test in five seconds instead of hanging the suite. The timeout
    // is a hang-guard, not the thing being waited on — the completion
    // source is. _spoken has no matching completion source: see
    // WaitForSpeechAsync below for why that path is polled instead.
    private string? _spoken;

    public TurnSoundScanTests()
    {
        // A fresh settings directory per test, the same isolation
        // SessionScanTests' sibling suites use — this class does not touch
        // it directly, but Snapshot() inside TurnSounds reads
        // OrbweaverSettings, and a stale TurnSoundsEnabled=false left by
        // another test would make every case here read as "silent for the
        // wrong reason".
        Environment.SetEnvironmentVariable(
            "ORBWEAVER_SETTINGS_DIR",
            Path.Combine(Path.GetTempPath(), "cb-turnsound-settings-" + Guid.NewGuid()));
        OrbweaverSettings.ReloadForTests();

        // The rate-limit clock TurnSounds owns is process-wide, so a test
        // left mid-gap by whichever case ran before this one would make an
        // otherwise-correct chime silently vanish into "too soon".
        TurnSounds.ResetForTests();

        ChimePlayer.PlayForTests = path =>
        {
            lock (_lock)
            {
                _played.Add(path);
                _chimeSignal?.TrySetResult(true);
            }
        };

        SpeechRequest.UtteranceForTests = (text, _, _) =>
        {
            lock (_lock) _spoken = text;
        };
    }

    public void Dispose()
    {
        ChimePlayer.PlayForTests = null;
        SpeechRequest.UtteranceForTests = null;
    }

    // Waits for the next background ChimePlayer.Play call, or returns
    // immediately if one already landed (the live, non-deferred path can
    // finish before this is even called). Five seconds is generous against
    // ChimePlayer's own five-second cap plus scheduling slack, and still
    // fails a genuine regression fast rather than hanging CI.
    //
    // `baseline` is how many chimes must already have played before this
    // call is satisfied by count alone — the caller reads _played.Count
    // *before* triggering the scan it is about to wait on, not this method
    // reading it after being called. QA's own fix (CB-167) had this
    // backwards for one case and broke a passing one to fix it: capturing
    // the baseline *inside* this method, after the triggering scan had
    // already run, raced the background chain — EnqueueChime's continuation
    // can land in well under a millisecond on a warm thread pool, faster
    // than this method's own two lock acquisitions apart. A single-chime
    // test whose chime had already fired by the time this ran would then
    // capture that 1 as its own baseline and wait forever for a second one
    // that was never coming. The check and the arm are one atomic lock
    // now, which closes that gap; a baseline supplied by the caller closes
    // the other.
    private async Task WaitForChimeAsync(int baseline = 0)
    {
        TaskCompletionSource<bool> signal;
        lock (_lock)
        {
            if (_played.Count > baseline) return;
            signal = _chimeSignal = new TaskCompletionSource<bool>();
        }

        var winner = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(winner == signal.Task, "Timed out waiting for a chime to play in the background.");
    }

    // A polling pump rather than a plain await: the vibe-summary path
    // crosses the UI thread twice more after this call returns (once to
    // resolve the OrbWindow, once to post the actual utterance), each via
    // Dispatcher.UIThread — and unlike the chime path above, which is pure
    // background-thread work, nothing drives a headless test's dispatcher
    // queue on its own between awaits. RunJobs() is what
    // SpeakScopeUiTests calls this "pumping"; this just does it in a loop
    // until the seam actually fires or five seconds pass.
    private async Task WaitForSpeechAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (_lock)
            {
                if (_spoken is not null) return;
            }

            Dispatcher.UIThread.RunJobs();

            lock (_lock)
            {
                if (_spoken is not null) return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("Timed out waiting for a turn summary to be spoken.");
    }

    private sealed class Scratch : IDisposable
    {
        public string Dir { get; } =
            Path.Combine(Path.GetTempPath(), "cb-turnsound-scan-" + Guid.NewGuid());

        public Scratch() => Directory.CreateDirectory(Dir);

        public void Write(
            string sessionId, string state, string title = "", string cwd = "/Users/user/project",
            int? pid = null, string termProgram = "iTerm.app", string tty = "/dev/ttys004",
            string transcriptPath = "", string cli = "")
        {
            var path = Path.Combine(Dir, sessionId + ".txt");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new SessionStatus
            {
                State = state,
                Title = title,
                Cwd = cwd,
                SessionPid = pid ?? LivePid,
                TermProgram = termProgram,
                Tty = tty,
                TranscriptPath = transcriptPath,
                Cli = cli,
            }));
        }

        public string WriteTranscript(string sessionId, params string[] rows)
        {
            var path = Path.Combine(Dir, sessionId + ".jsonl");
            var lines = new List<string> { "{\"type\":\"user\",\"message\":{\"content\":\"hi\"}}" };
            lines.AddRange(rows);
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
            return path;
        }

        // Deleting the status file is the whole of "this session is gone" —
        // the same SessionEnd path SessionScanTests exercises, and QA's own
        // route for making a session a husk the scan simply no longer sees.
        public void Delete(string sessionId) =>
            File.Delete(Path.Combine(Dir, sessionId + ".txt"));

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private static SessionManager Scan(Scratch scratch)
    {
        OrbweaverSettings.ClaudeCodeEnabled = true;
        OrbweaverSettings.CodexEnabled = true;

        var manager = new SessionManager(scratch.Dir);
        manager.ScanAndUpdate();
        return manager;
    }

    private static IReadOnlyCollection<string> OrbIds(SessionManager manager)
    {
        var field = typeof(SessionManager).GetField(
            "_windows", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);

        var windows = (System.Collections.IDictionary)field!.GetValue(manager)!;
        return windows.Keys.Cast<string>().ToList();
    }

    // --- the UI thread ---

    // QA (CB-167), measured on a real Mac: ChimePlayer.Play blocks its
    // caller on WaitForExit for the length of the sound — ~2.45s for Glass
    // or Ping, 5.05s (the cap) for a long user file. TurnSounds.Deliver used
    // to call it synchronously from ScanAndUpdateCore, which ScheduleScan
    // (the real production entry point, used here rather than the
    // synchronous ScanAndUpdate the other cases in this file call) deliberately
    // resumes on the Avalonia UI thread — CB-106's own fix for a different bug
    // depends on that being true. So every chime used to freeze the whole UI
    // for up to five seconds. The seam fires exactly where the real Play
    // would run, so the thread it fires on is the thread a real process wait
    // would have blocked.
    [AvaloniaFact]
    public async Task AChimeIsNeverPlayedOnTheUiThread()
    {
        using var scratch = new Scratch();
        scratch.Write("session-a", state: "generating");

        // Overrides the constructor's seam locally, since this case cares
        // about *which thread* called Play, not whether one did — signalled
        // the same way WaitForChimeAsync signals for every other case here.
        var onUiThread = new List<bool>();
        var signal = new TaskCompletionSource<bool>();
        ChimePlayer.PlayForTests = _ =>
        {
            onUiThread.Add(Dispatcher.UIThread.CheckAccess());
            signal.TrySetResult(true);
        };

        OrbweaverSettings.ClaudeCodeEnabled = true;
        var manager = new SessionManager(scratch.Dir);

        await manager.ScheduleScan();
        scratch.Write("session-a", state: "idle");
        await manager.ScheduleScan();

        await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        var single = Assert.Single(onUiThread);
        Assert.False(single, "ChimePlayer.Play ran on the UI thread; the real one blocks it for up to 5 s");
    }

    // --- generating -> idle ---

    [AvaloniaFact]
    public async Task GeneratingToIdlePlaysExactlyOnce()
    {
        using var scratch = new Scratch();
        scratch.Write("session-a", state: "generating");

        var manager = Scan(scratch);
        Assert.Empty(_played); // the first scan of a generating session is a baseline

        scratch.Write("session-a", state: "idle");
        manager.ScanAndUpdate();
        await WaitForChimeAsync();

        Assert.Single(_played);
    }

    // CB-298: the same transition, muted, plays nothing and stamps nothing —
    // so the identical one after unmuting plays exactly what it always did,
    // with no backlog of stale chimes released and no rate-limit gap spent.
    [AvaloniaFact]
    public async Task AFinishedTurnWhileMutedIsSilentAndTheSameOneAfterUnmutingPlaysOnce()
    {
        using var scratch = new Scratch();
        scratch.Write("session-a", state: "generating");
        var manager = Scan(scratch);

        try
        {
            AudioMute.Set(true);
            scratch.Write("session-a", state: "idle");
            manager.ScanAndUpdate();
            await TurnSounds.ChimesEnqueuedSoFar();
            Assert.Empty(_played);

            AudioMute.Set(false);
            scratch.Write("session-a", state: "generating");
            manager.ScanAndUpdate();
            scratch.Write("session-a", state: "idle");
            manager.ScanAndUpdate();
            await WaitForChimeAsync();
            Assert.Single(_played);
        }
        finally { AudioMute.Set(false); }
    }

    [AvaloniaFact]
    public async Task ARepeatScanOfTheSameIdleStateDoesNotReplay()
    {
        using var scratch = new Scratch();
        scratch.Write("session-a", state: "generating");
        var manager = Scan(scratch);

        scratch.Write("session-a", state: "idle");
        manager.ScanAndUpdate();
        await WaitForChimeAsync();
        Assert.Single(_played);

        // The file on disk is unchanged, so this scan finds idle again — the
        // no-repeat rule TurnSignalsTests already covers in isolation, now
        // proven over a real second ScanAndUpdate().
        manager.ScanAndUpdate();
        Assert.Single(_played);
    }

    // A real permission prompt appearing, not just a baseline being absent
    // of one — this is what exercises TurnSounds.Snapshot's
    // ResolveAttentionSound closure over a real ScanAndUpdate() rather than
    // only ResolveFinishedSound, which the generating→idle cases above
    // already cover on their own.
    [AvaloniaFact]
    public async Task GeneratingToWaitingPlaysTheAttentionChime()
    {
        using var scratch = new Scratch();
        scratch.Write("session-a", state: "generating");
        var manager = Scan(scratch);

        scratch.Write("session-a", state: "waiting");
        manager.ScanAndUpdate();
        await WaitForChimeAsync();

        Assert.Single(_played);
    }

    // --- startup burst ---

    [AvaloniaFact]
    public void AStartupScanWithIdleAndWaitingOrbsAlreadyOnScreenStaysSilent()
    {
        using var scratch = new Scratch();
        scratch.Write("idle-one", state: "idle");
        scratch.Write("waiting-one", state: "waiting");

        Scan(scratch);

        Assert.Empty(_played);
    }

    // --- a backgrounded husk ---

    // Verbatim off the same real Claude Code row SessionScanTests' own husk
    // test uses, for the same reason: this is what a real backgrounded turn
    // actually writes, not an invented fixture.
    private const string BackgroundingMarker =
        """{"parentUuid":"1b5cf160-79bf-4e2b-a01f-6511aee6b36b","isSidechain":false,"type":"system","subtype":"informational","content":"Backgrounding after the current tool finishes…","isMeta":false,"timestamp":"2026-08-28T17:53:15.295Z","uuid":"4f19d42a-80a5-4f9e-afe6-f234587acbf5","level":"warning","userType":"external","entrypoint":"cli","cwd":"/Users/w/project","sessionId":"6d3a9d57-10c6-4e9d-bf25-38194fae23c0","version":"2.1.251","gitBranch":"develop"}""";

    [AvaloniaFact]
    public void ABackgroundedHuskFrozenAtWaitingStaysSilent()
    {
        using var scratch = new Scratch();

        var huskMarker = BackgroundingMarker.Replace(
            "6d3a9d57-10c6-4e9d-bf25-38194fae23c0", "husk", StringComparison.Ordinal);
        var huskTail = scratch.WriteTranscript("husk", huskMarker);
        scratch.Write("husk", state: "generating", transcriptPath: huskTail);

        var manager = Scan(scratch);
        Assert.DoesNotContain("husk", OrbIds(manager));

        // The husk's own file moves from generating to waiting while it is
        // hidden — exactly what a permission prompt appearing behind a
        // backgrounded turn looks like on disk. "Husks can't chime by
        // construction" means the scan never even calls Observe for a
        // session JudgeReachability has already dropped, so this must never
        // signal regardless of what the file says.
        scratch.Write("husk", state: "waiting", transcriptPath: huskTail);
        manager.ScanAndUpdate();

        Assert.Empty(_played);
    }

    // --- reset to idle ---

    [AvaloniaFact]
    public void ResetToIdlePlaysNothingEvenThoughTheSessionWasGenerating()
    {
        using var scratch = new Scratch();
        scratch.Write("session-a", state: "generating");
        var manager = Scan(scratch);

        manager.ResetSessionToIdle("session-a");

        Assert.Empty(_played);

        // And the tracker now reads "idle" the way Settle recorded it, so
        // the very next scan finding idle on disk is not a transition either
        // — the manual reset must not leave a live generating→idle armed for
        // the next ordinary tick to fire.
        scratch.Write("session-a", state: "idle");
        manager.ScanAndUpdate();

        Assert.Empty(_played);
    }

    // --- vibe summary ---

    // The end-to-end path TurnSoundPolicyTests and TurnSummarySpeechTests
    // each cover half of: a real scan decides Summary, SessionManager's
    // callback finds the real OrbWindow for the session, and
    // OrbWindow.SpeakTurnSummary reads its real transcript and speaks it —
    // no chime anywhere in this case, which is the assertion that proves the
    // Summary branch ran rather than silently falling back to one.
    [AvaloniaFact]
    public async Task ATurnFinishedSetToSummarySpeaksTheSessionsLastTurnRatherThanChiming()
    {
        OrbweaverSettings.TurnFinishedSound = "summary";

        using var scratch = new Scratch();
        const string AssistantSaid =
            """{"type":"assistant","uuid":"a1","timestamp":"2026-08-16T10:00:09Z","message":{"role":"assistant","content":[{"type":"text","text":"Fixed the nested-team case."}]}}""";
        var transcriptPath = scratch.WriteTranscript("session-a", AssistantSaid);
        scratch.Write("session-a", state: "generating", transcriptPath: transcriptPath);
        var manager = Scan(scratch);

        scratch.Write("session-a", state: "idle", transcriptPath: transcriptPath);
        manager.ScanAndUpdate();
        await WaitForSpeechAsync();

        Assert.Empty(_played);
        Assert.Equal("Fixed the nested-team case.", _spoken);
    }

    // QA (CB-167): a summary that found no text used to go silent —
    // RemoteControl/ClaudeCloud orbs and a local orb whose transcript has no
    // assistant text both get false back from SpeakTurnSummaryAsync, and
    // _lastPlayed had already been stamped by the time that was discovered.
    // The fix falls back to the ordinary finished chime instead: the turn
    // still finished, and silence is not an acceptable answer to that.
    [AvaloniaFact]
    public async Task ATurnFinishedSetToSummaryFallsBackToTheChimeWhenThereIsNoTextToSpeak()
    {
        OrbweaverSettings.TurnFinishedSound = "summary";

        using var scratch = new Scratch();
        // No transcript at all: FindSpeakableText has nothing to read, so
        // SpeakTurnSummaryAsync reports false and TurnSounds must fall back.
        scratch.Write("session-a", state: "generating");
        var manager = Scan(scratch);

        scratch.Write("session-a", state: "idle");
        manager.ScanAndUpdate();
        await WaitForChimeAsync();

        Assert.Single(_played);
        Assert.Null(_spoken);
    }

    // --- a deferred signal for a session that stops mattering ---

    // QA (CB-167): "the deferral must never let a husk chime late." A real
    // scan, using ScanAndUpdate's own real clock rather than a fake one
    // (ScanAndUpdate has no override), so the wait below is a genuine two
    // real seconds — slower than the pure TurnSoundsTests cases, and the
    // only way to prove SessionManager's own Prune-then-CancelPending
    // wiring rather than just TurnSounds' half of it.
    [AvaloniaFact]
    public async Task APrunedHuskNeverPlaysASignalThatWasStillWaitingOnTheGap()
    {
        using var scratch = new Scratch();
        scratch.Write("session-a", state: "generating");
        // codex, not the default ClaudeCode: two files sharing both a pid
        // and a CLI put Superseded's job-list lookup in play (see
        // SessionScanTests' own header), which is not what this case is
        // about.
        scratch.Write("session-b", state: "generating", cli: "codex");
        var manager = Scan(scratch);

        // A finishes and plays immediately — the rate limit's clock starts
        // here.
        scratch.Write("session-a", state: "idle");
        manager.ScanAndUpdate();
        await WaitForChimeAsync();
        Assert.Single(_played);

        // B hits a permission prompt right away, well inside A's 2 s gap —
        // deferred, not dropped, per the earlier QA fix.
        scratch.Write("session-b", state: "waiting", cli: "codex");
        manager.ScanAndUpdate();

        // B is pruned before its two seconds are up: SessionEnd's own path,
        // its status file simply gone, so the next scan's `seen` no longer
        // names it at all.
        scratch.Delete("session-b");
        manager.ScanAndUpdate();

        // Past the real gap. If cancelling the pending signal had not
        // worked, B's Ping would land here.
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.Single(_played);
    }

    // The Settle-shaped sibling of the case above: a person manually
    // resetting the *other* orb while B's Ping is still waiting on the gap
    // must not leave B's deferred signal armed either.
    [AvaloniaFact]
    public async Task AManualResetOfAnUnrelatedSessionDoesNotCancelThisSessionsPendingSignal()
    {
        // The negative control: resetting session-a (unrelated to the
        // pending signal) must NOT clear session-b's — only Cancel
        // targeting the actual pending session id should. Otherwise this
        // fix could not be told apart from ClearPending firing on anything.
        using var scratch = new Scratch();
        scratch.Write("session-a", state: "generating");
        // codex, not the default ClaudeCode: two files sharing both a pid
        // and a CLI put Superseded's job-list lookup in play (see
        // SessionScanTests' own header), which is not what this case is
        // about.
        scratch.Write("session-b", state: "generating", cli: "codex");
        var manager = Scan(scratch);

        scratch.Write("session-a", state: "idle");
        manager.ScanAndUpdate();
        await WaitForChimeAsync();
        Assert.Single(_played);

        scratch.Write("session-b", state: "waiting", cli: "codex");
        manager.ScanAndUpdate();

        // Resetting a *different*, unrelated session (one with nothing
        // pending) must leave session-b's deferred Ping alone.
        manager.ResetSessionToIdle("session-a");

        // Baseline supplied explicitly: exactly one chime has played so
        // far (asserted above), so this waits for a genuinely *second* one
        // rather than being satisfied by the first all over again.
        await WaitForChimeAsync(baseline: 1);
        Assert.Equal(2, _played.Count);
    }

    // The real Settle path: resetting the *same* session the pending
    // signal belongs to drops it.
    [AvaloniaFact]
    public async Task ResettingTheSessionThePendingSignalBelongsToCancelsIt()
    {
        using var scratch = new Scratch();
        scratch.Write("session-a", state: "generating");
        // codex, not the default ClaudeCode: two files sharing both a pid
        // and a CLI put Superseded's job-list lookup in play (see
        // SessionScanTests' own header), which is not what this case is
        // about.
        scratch.Write("session-b", state: "generating", cli: "codex");
        var manager = Scan(scratch);

        scratch.Write("session-a", state: "idle");
        manager.ScanAndUpdate();
        await WaitForChimeAsync();
        Assert.Single(_played);

        scratch.Write("session-b", state: "waiting", cli: "codex");
        manager.ScanAndUpdate();

        // A person clears the stuck orb by hand before its two seconds are
        // up.
        manager.ResetSessionToIdle("session-b");

        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.Single(_played);
    }

    // QA round 2, finding 5, folded in from Marguerite's demonstrating test
    // (Cb167QaRound2UiTests.ADeferredAttentionIsDroppedWhenItsSessionIsResetBeforeItFires,
    // worktree cb-wt-turn-sounds-qa2). Same shape as
    // ResettingTheSessionThePendingSignalBelongsToCancelsIt above, but
    // asserts on the actual sound *path* rather than only a count — the
    // count-only assertion could not tell "the Ping never played" apart
    // from "the Ping played but something else also failed to," and
    // finding 5's fix touches exactly which event survives fire-time
    // validation, not just how many chimes land. session-b needs its own
    // real, live pid (a genuine harmless subprocess — /bin/sleep, or cmd running ping on Windows, which has no /bin/sleep) rather than the
    // cli:"codex" trick the other two-session cases above use — two status
    // files sharing one pid would make every WriteLocal-equivalent call a
    // fresh Superseded baseline instead of a state change on the same orb.
    [AvaloniaFact]
    public async Task ADeferredAttentionIsDroppedWhenItsSessionIsResetBeforeItFires()
    {
        var attention = Path.Combine(Path.GetTempPath(), "cb-turnsound-attention-" + Guid.NewGuid() + ".aiff");
        File.WriteAllText(attention, "");
        try
        {
            OrbweaverSettings.NeedsAttentionSound = attention;

            using var scratch = new Scratch();
            using var other = StartLongRunningProcess();
            try
            {
                var bPid = other.Id;
                scratch.Write("session-a", state: "generating");
                scratch.Write("session-b", state: "generating", pid: bPid);
                var manager = Scan(scratch);

                scratch.Write("session-a", state: "idle");
                manager.ScanAndUpdate(); // A finishes live: Glass now, gap opens
                await WaitForChimeAsync();
                Assert.Single(_played);

                scratch.Write("session-b", state: "waiting", pid: bPid);
                manager.ScanAndUpdate(); // B's prompt: inside the gap, deferred

                manager.ResetSessionToIdle("session-b"); // the user clears B before it fires

                await Task.Delay(TimeSpan.FromSeconds(3));
            }
            finally
            {
                try { other.Kill(entireProcessTree: true); } catch { }
            }

            lock (_lock)
            {
                Assert.Contains(
                    SystemSoundCatalog.Resolve(SystemSoundCatalog.DefaultFinishedSoundName,
                        SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions),
                    _played); // the setup really did chime for A
                Assert.DoesNotContain(attention, _played);
            }
        }
        finally
        {
            try { File.Delete(attention); } catch { }
        }
    }

    // The positive control the test above needs, and the only scan-level
    // case that lets a deferred event reach FirePending's real fire time
    // through SessionManager's own currentStateFor wiring untouched — every
    // other case here either plays live (currentStateFor is never invoked
    // at all) or has its pending event reactively cancelled first (Settled
    // or Pruned), so currentStateFor's "the tracker still has this session,
    // and it says waiting" arm has nothing else that reaches it. B's status
    // file is left exactly as written; nothing resets or removes it before
    // the real ~2 s gap closes.
    [AvaloniaFact]
    public async Task ADeferredAttentionLeftUntouchedStillPlaysOnceItsOwnGapOpens()
    {
        using var scratch = new Scratch();
        scratch.Write("session-a", state: "generating");
        scratch.Write("session-b", state: "generating", cli: "codex");
        var manager = Scan(scratch);

        scratch.Write("session-a", state: "idle");
        manager.ScanAndUpdate(); // A finishes live: Glass now, gap opens
        await WaitForChimeAsync();
        Assert.Single(_played);

        scratch.Write("session-b", state: "waiting", cli: "codex");
        manager.ScanAndUpdate(); // B's prompt: inside the gap, deferred

        // Baseline supplied explicitly, the same reason
        // AManualResetOfAnUnrelatedSessionDoesNotCancelThisSessionsPendingSignal
        // does: one chime has already played, so this waits for a genuinely
        // second one rather than being satisfied by the first all over
        // again.
        await WaitForChimeAsync(baseline: 1);
        Assert.Equal(2, _played.Count);
    }

    // Same shape as ChimePlayerTests' helper: a real, harmless process that
    // stays alive long enough to own a pid. /bin/sleep does not exist on
    // Windows, which is how this test failed on the win-x64 leg.
    private static System.Diagnostics.Process StartLongRunningProcess()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "ping -n 30 127.0.0.1 >NUL" } }
            : new System.Diagnostics.ProcessStartInfo("/bin/sleep") { ArgumentList = { "30" } };

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        return System.Diagnostics.Process.Start(startInfo)!;
    }
}
