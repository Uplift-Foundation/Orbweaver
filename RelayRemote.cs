using System.Diagnostics;

namespace Orbweaver
{
    // Where a phone's v4 requests are carried out (CB-316): watch / unwatch,
    // input, launch. RelayLink decides what a frame says and seals what goes
    // back; this decides what happens on the desk, through seams, so every arm
    // — a refusal per code, a launch that never checks in, a watch that dies
    // with its phone — is a test rather than a hope.
    //
    // Threads: requests arrive on RelayClient's event pump, the poll loop on
    // its own task, the scan's snapshot on the UI thread. The watch table is
    // under one lock; nothing here is ever called under RelayClient's lock, and
    // replies go out through IRemoteSender, which takes that lock itself.
    internal interface IRemoteSender
    {
        void Reply(string phoneId, string? sessionId, string plaintext);
        void Window(string phoneId, string sessionId, string plaintext);

        // CB-321: one persona_image window; false when nothing was produced
        // (the phone is offline or the link is down), which ends the transfer.
        bool ImageWindow(string phoneId, string hash, string plaintext);

        // CB-404: a frames-v13 run state frame to a phone watching the session.
        void Run(string phoneId, string sessionId, string plaintext);
    }

    internal sealed class RelayRemote
    {
        internal static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(125);
        internal static readonly TimeSpan LaunchCheckIn = TimeSpan.FromSeconds(30);
        internal static readonly TimeSpan FirstMessageWait = TimeSpan.FromSeconds(60);
        internal static readonly TimeSpan LaunchPoll = TimeSpan.FromMilliseconds(500);

        internal const string UntypeableWhy = "that terminal can't be typed into from a phone";

        internal sealed record LaunchSeams(
            bool IsWindows,
            Func<string, string?> Locate,
            Func<string, bool> DirectoryExists,
            Func<string> Shell,
            Func<string, string, (string? Pane, string? Why)> PlaceMac,
            Func<ProcessStartInfo, int?> StartWindows,
            Func<string>? Home = null,
            LaunchRunner.TrustSeams? Trust = null,
            Func<string, string>? Resolve = null);

        internal sealed record Seams(
            Func<DateTimeOffset> Now,
            Func<TimeSpan, Task> Delay,
            Func<SessionStatus, TranscriptWatcher.Seams> WatchSeams,
            RelayInput Input,
            LaunchSeams Launch,
            Action<string> Log)
        {
            // CB-321: the finished bundle offered under a hash, or null. Off by
            // default so a test that does not care about pictures gets
            // unknown_image; the app's seams point it at PersonaImageStore.
            internal Func<string, PersonaImageBundle?> Images { get; init; } = _ => null;

            // The desktop's account choices per CLI and the one the user designated for
            // phone launches. A launch that names none runs under the designated one, else
            // the CLI's default, exactly as it always did (the last-used account of New
            // Chat is deliberately not consulted). Null means no accounts: a launch naming
            // one is unknown_account.
            internal AccountSeams? Accounts { get; init; }

            // The few spoken-style sentences a finished turn's reply comes to: the
            // desktop's own summariser, off by default so a test that does not care
            // gets no summaries.
            internal Func<SessionStatus, string, Task<string?>>? Summarize { get; init; }

            // Lets the transcript settle after a turn ends before it is read.
            internal Func<Task>? Settle { get; init; }

            // CB-339: the folder picker. Off by default so a test that does not care gets
            // forbidden_path for a list_dir, as a desk with nothing to share would.
            internal BrowseSeams? Browse { get; init; }

            // CB-404: the run controls a set_run drives and a run frame reports.
            // Null means none: no run frames, and every set_run is unavailable.
            internal IRunControls? RunControls { get; init; }
        }

        // The filesystem a list_dir reads, the settings it reads them under (resolved on each
        // request: the automatic roots follow the sessions), and how long a read may take before
        // the phone is told `unreadable` rather than left waiting (its own timeout is 10 s).
        internal sealed record BrowseSeams(IBrowseFileSystem Fs, Func<BrowseSettings> Settings, TimeSpan ReadTimeout);

        internal sealed record AccountSeams(
            Func<NewChatCli, IReadOnlyList<NewChatAccounts.Choice>> Choices,
            Func<NewChatCli, string?> Designated);

        // CB-321: frames-v5 limits.image_windows_per_second, per phone.
        internal static readonly TimeSpan ImageWindowEvery = TimeSpan.FromSeconds(1.0 / PersonaFrames.ImageWindowsPerSecond);

        private readonly Seams _seams;
        private readonly IRemoteSender _send;
        private readonly NonceWindow _nonces;
        private readonly object _gate = new();
        private readonly Dictionary<(string Phone, string Session), TranscriptWatcher> _watches = new();
        private IReadOnlyList<(string SessionId, SessionStatus Status)> _snapshot = [];

        // CB-404: the run frame each watch was last sent, so one goes only when
        // its bytes change (the SendCaps rule). Dropped with the watch, so a
        // re-watch after a reconnect is sent the current state at once.
        private readonly Dictionary<(string Phone, string Session), string> _runSent = new();
        private int _ticks;

        // Run state is read from transcripts and panes, so it is looked at on
        // every eighth tick (one second) rather than at the window cadence.
        internal const int RunEveryTicks = 8;

        // CB-321: one picture per phone at a time, later requests queued, and a
        // generation per phone that going offline bumps, so a transfer in
        // flight notices it was abandoned at its next window.
        private readonly Dictionary<string, Queue<PersonaImageRequest>> _imageQueues = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _imageGeneration = new(StringComparer.Ordinal);

        internal RelayRemote(Seams seams, IRemoteSender send, NonceWindow nonces)
        {
            _seams = seams;
            _send = send;
            _nonces = nonces;
        }

        internal int WatchCount { get { lock (_gate) return _watches.Count; } }

        internal bool IsWatching(string phoneId, string sessionId)
        {
            lock (_gate) return _watches.ContainsKey((phoneId, sessionId));
        }

        // ---- the desk's sessions -----------------------------------------------

        // The scan's snapshot. A watched session that has left the roster ends
        // its watch: the roster removal is the phone's signal, so no final
        // window is sent.
        internal void OnStatuses(IReadOnlyList<(string SessionId, SessionStatus Status)> snapshot)
        {
            lock (_gate)
            {
                _snapshot = snapshot;
                foreach (var key in _watches.Keys.Where(k => Local(k.Session) is null).ToList()) Drop(key);
            }
        }

        // A local CLI session this desktop's book holds — never an OpenClaw
        // conversation, a cloud session or a peer's mirrored orb.
        internal SessionStatus? Local(string sessionId)
        {
            lock (_gate)
                foreach (var (sid, status) in _snapshot)
                    if (sid == sessionId && status.IsLocalCli) return status;
            return null;
        }

        // CB-422 (frames-v15): a Paperclip agent this desk is drawing. It is a
        // row the phone can see, so a request naming it is answered rather than
        // told the session is gone, but it has no transcript, terminal or run
        // controls, which is why Local() does not and must not include it.
        internal bool Listed(string sessionId)
        {
            lock (_gate)
                foreach (var (sid, status) in _snapshot)
                    if (sid == sessionId && status.Source == SessionSource.Paperclip) return true;
            return false;
        }

        private IReadOnlyList<(string SessionId, SessionStatus Status)> Snapshot()
        {
            lock (_gate) return _snapshot;
        }

        // ---- the summary ------------------------------------------------------

        // The phones watching a session.
        internal IReadOnlyList<string> Watchers(string sessionId)
        {
            lock (_gate) return _watches.Keys.Where(k => k.Session == sessionId).Select(k => k.Phone).Distinct().ToList();
        }

        // A session went generating -> idle: what it did, in the few sentences the desktop
        // would speak, and the phones that should hear it (those watching when it is ready).
        // Only watched sessions are summarised, since each one costs a model call, and only
        // the last assistant reply is. Null when there is nothing to say or no one to say it to.
        internal async Task<(string Text, IReadOnlyList<string> Phones)?> SummaryForAsync(string sessionId)
        {
            if (_seams.Summarize is not { } summarize || Watchers(sessionId).Count == 0) return null;
            if (Local(sessionId) is not { } status) return null;

            if (_seams.Settle is { } settle) await settle().ConfigureAwait(false);
            var seams = _seams.WatchSeams(status);
            var lines = seams.PathOf() is { } path ? seams.Parse(path) : null;
            var reply = lines?.LastOrDefault(l => l.Role == "assistant" && !string.IsNullOrWhiteSpace(l.Text)).Text;
            if (string.IsNullOrWhiteSpace(reply)) return null;

            string? text;
            try { text = await summarize(status, reply).ConfigureAwait(false); }
            catch (Exception e) { _seams.Log($"summary {sessionId}: summariser threw {e.GetType().Name}"); return null; }
            if (string.IsNullOrWhiteSpace(text)) return null;

            // Whoever is still watching after the wait: a phone that left meanwhile is not sent it.
            var phones = Watchers(sessionId);
            return phones.Count == 0 ? null : (text, phones);
        }

        // ---- watch lifetime ----------------------------------------------------

        internal void PhoneOffline(string phoneId)
        {
            lock (_gate)
            {
                foreach (var key in _watches.Keys.Where(k => k.Phone == phoneId).ToList()) Drop(key);
                AbandonImages(phoneId);
            }
        }

        // Under _gate.
        private void Drop((string Phone, string Session) key)
        {
            _watches.Remove(key);
            _runSent.Remove(key);
        }

        // Under _gate. The queue goes, and the transfer in flight stops at its
        // next window; the phone asks again when it is back (frames-v5: an
        // incomplete picture is discarded on the phone after 30 s).
        private void AbandonImages(string phoneId)
        {
            _imageQueues.Remove(phoneId);
            _imageGeneration[phoneId] = _imageGeneration.GetValueOrDefault(phoneId) + 1;
        }

        internal bool IsSendingImage(string phoneId)
        {
            lock (_gate) return _imageQueues.ContainsKey(phoneId);
        }

        // The socket closed: every watch dies, and the phone re-watches on
        // reconnect (frames-v4 watch.rules).
        internal void Disconnected()
        {
            lock (_gate)
            {
                _watches.Clear();
                _runSent.Clear();
                foreach (var phone in _imageQueues.Keys.ToList()) AbandonImages(phone);
            }
        }

        // One tick of every watch: what each owes its phone.
        internal void Tick(DateTimeOffset now)
        {
            List<TranscriptWatcher> watchers;
            lock (_gate) watchers = _watches.Values.ToList();
            foreach (var w in watchers)
            {
                IReadOnlyList<string> windows;
                try { windows = w.Poll(now); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _seams.Log($"watch {w.SessionId}: {ex.GetType().Name}");
                    continue;
                }
                foreach (var window in windows) _send.Window(w.PhoneId, w.SessionId, window);
            }
            if (++_ticks % RunEveryTicks == 0) SendRunStates();
        }

        // CB-404: each watched session's run frame, to the phones watching it,
        // when its bytes changed since that phone was last sent one. The host
        // is read once per session, however many phones watch it.
        internal void SendRunStates(string? onlySession = null)
        {
            if (_seams.RunControls is not { } run) return;
            List<(string Phone, string Session)> keys;
            lock (_gate) keys = _watches.Keys.Where(k => onlySession is null || k.Session == onlySession).ToList();
            foreach (var session in keys.Select(k => k.Session).Distinct())
            {
                if (Local(session) is not { } status) continue;
                RunState? state;
                try { state = run.Read(status); }
                catch (Exception e)
                {
                    _seams.Log($"run {session}: {e.GetType().Name}");
                    continue;
                }
                if (state is null) continue;
                var frame = RunFrames.State(session, state);
                foreach (var key in keys.Where(k => k.Session == session))
                {
                    lock (_gate)
                    {
                        // A watch that ended while the host was being read is not sent to.
                        if (!_watches.ContainsKey(key)) continue;
                        if (_runSent.TryGetValue(key, out var last) && last == frame) continue;
                        _runSent[key] = frame;
                    }
                    _send.Run(key.Phone, session, frame);
                }
            }
        }

        internal async Task RunAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try { await _seams.Delay(PollEvery).WaitAsync(stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                Tick(_seams.Now());
            }
        }

        // ---- requests ------------------------------------------------------------

        internal Task HandleAsync(string phoneId, byte[] phoneIdPublic, RelayRequest request) => request switch
        {
            WatchRequest w => Task.Run(() => Watch(phoneId, w)),
            UnwatchRequest u => Task.Run(() => Unwatch(phoneId, u)),
            InputRequest i => InputAsync(phoneId, i),
            LaunchRequest l => LaunchAsync(phoneId, phoneIdPublic, l),
            // Off the event pump: chunking a picture is milliseconds, but the
            // pump is the one place CB-322 says must never wait on a picture.
            PersonaImageRequest p => Task.Run(() => ImageAsync(phoneId, p)),
            // CB-339: a folder listing, built off the pump with its own clock on the read.
            ListDirRequest d => ListDirAsync(phoneId, d),
            // CB-404: a run change, off the pump — a control request waits on the CLI's answer.
            SetRunRequest r => Task.Run(() => SetRunAsync(phoneId, r)),
            // The only other kind RelayRequests makes.
            _ => Task.Run(() => Err(phoneId, request.Ref, RelayReplies.BadFrame, ((BadFrameRequest)request).SessionId)),
        };

        private readonly BrowseRateLimiter _browseRate = new();

        // frames-v11 resolve.steps 1 and 2 here (the rate limit, then the request's shape), 3 to 10 in
        // BrowseRules. One reply per request: the listing's dir frames or exactly one err. The desk
        // logs the ref and the outcome, never a path or a folder name.
        private async Task ListDirAsync(string phoneId, ListDirRequest r)
        {
            if (!_browseRate.TryAccept(phoneId, _seams.Now().ToUnixTimeMilliseconds()))
            {
                Err(phoneId, r.Ref, RelayReplies.RateLimited);
                return;
            }
            if (r.Path is null)
            {
                Err(phoneId, r.Ref, RelayReplies.BadFrame);
                return;
            }
            if (_seams.Browse is not { } browse)
            {
                Err(phoneId, r.Ref, RelayReplies.ForbiddenPath);
                return;
            }

            BrowseOutcome outcome;
            try
            {
                outcome = await Task.Run(() => BrowseRules.Resolve(r.Path, r.Hidden, browse.Settings(), browse.Fs))
                    .WaitAsync(browse.ReadTimeout).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // A read that did not finish in time, or one the filesystem refused in a way the
                // seam did not name: the phone is told the folder could not be read.
                _seams.Log($"list_dir {RelayLink.Safe(r.Ref)} {e.GetType().Name}");
                Err(phoneId, r.Ref, RelayReplies.Unreadable);
                return;
            }

            if (outcome.Err is { } code)
            {
                Err(phoneId, r.Ref, code);
                return;
            }
            var frames = BrowseFrames.Dir(r.Ref, outcome.Listing!);
            _seams.Log($"list_dir {RelayLink.Safe(r.Ref)} {outcome.Listing!.Items.Count} entries");
            foreach (var frame in frames) _send.Reply(phoneId, null, frame);
        }

        // frames-v13 set_run, after the parser's bad_frame: unknown_session, then
        // the field's state (unavailable when it is off or the host is running
        // another change to the session,
        // bad_value when the value is not one of its choices as last written),
        // then the route, whose failure is set_failed. `ok set` says the route
        // ran, not that it took: the confirming run frame says that.
        private async Task SetRunAsync(string phoneId, SetRunRequest r)
        {
            if (Listed(r.SessionId))
            {
                RunErr(phoneId, r, RunFrames.Unavailable, NoRunControlsWhy);
                return;
            }
            if (Local(r.SessionId) is not { } status)
            {
                RunErr(phoneId, r, RelayReplies.UnknownSession);
                return;
            }
            if (_seams.RunControls is not { } run || run.Read(status) is not { } state)
            {
                RunErr(phoneId, r, RunFrames.Unavailable, NoRunControlsWhy);
                return;
            }
            if (RunFrames.Refusal(state, r.Field, r.Value, run.IsRunning(status)) is { } refused)
            {
                RunErr(phoneId, r, refused.Code, refused.Why);
                return;
            }

            var result = await run.SetAsync(status, r.Field, r.Value).ConfigureAwait(false);
            if (result.Kind != RunSetKind.Sent)
            {
                var (code, why) = RunFrames.Answer(result);
                RunErr(phoneId, r, code, why);
                return;
            }
            _seams.Log($"ok {RelayLink.Safe(r.Ref)} {RunFrames.SetOk} {r.Field}");
            _send.Reply(phoneId, r.SessionId, RelayReplies.Ok(r.Ref, RunFrames.SetOk, r.SessionId));
            // The pending frame now, rather than at the next one-second look.
            SendRunStates();
        }

        // frames-v13: every set_run reply travels under the session's ck, the
        // unknown_session one included, since the request named it.
        private void RunErr(string phoneId, SetRunRequest r, string code, string? why = null)
        {
            _seams.Log($"err {RelayLink.Safe(r.Ref)} {code}");
            _send.Reply(phoneId, r.SessionId, RelayReplies.Err(r.Ref, code, r.SessionId, why));
        }

        internal const string NoRunControlsWhy = "This desk can't change that session's model or effort.";

        private void Err(string phoneId, string reference, string code, string? sessionId = null, string? why = null)
        {
            _seams.Log($"err {RelayLink.Safe(reference)} {code}");
            _send.Reply(phoneId, sessionId is not null && (Local(sessionId) is not null || Listed(sessionId)) ? sessionId : null,
                RelayReplies.Err(reference, code, sessionId, why));
        }

        private void Ok(string phoneId, string reference, string code, string sessionId, long? total = null)
        {
            _seams.Log($"ok {RelayLink.Safe(reference)} {code}");
            _send.Reply(phoneId, sessionId, RelayReplies.Ok(reference, code, sessionId, total));
        }

        private void Watch(string phoneId, WatchRequest w)
        {
            if (Listed(w.SessionId))
            {
                // A Paperclip row has no conversation: watching it succeeds with
                // nothing in it, no window, no run frame and no subscription, so
                // a phone that opens one shows an empty conversation instead of
                // "That session is gone from the desk."
                Ok(phoneId, w.Ref, RelayReplies.Watching, w.SessionId, 0);
                return;
            }
            if (Local(w.SessionId) is null)
            {
                Err(phoneId, w.Ref, RelayReplies.UnknownSession, w.SessionId);
                return;
            }
            var watcher = new TranscriptWatcher(phoneId, w.SessionId, WatchSeamsFor(w.SessionId));
            if (w.Before is { } before)
            {
                // A page: one window run, and no change to the subscription.
                foreach (var window in watcher.Page(before)) _send.Window(phoneId, w.SessionId, window);
                return;
            }
            // Subscribe (idempotent): a fresh watcher and a fresh backfill, so a
            // phone that re-watches after a reconnect starts from a clean slate.
            var (total, windows) = watcher.Start();
            lock (_gate)
            {
                _watches[(phoneId, w.SessionId)] = watcher;
                _runSent.Remove((phoneId, w.SessionId));
            }
            Ok(phoneId, w.Ref, RelayReplies.Watching, w.SessionId, total);
            // frames-v13: the run frame between `ok watching` and the backfill,
            // always, so a phone that re-watches is never left holding a stale one.
            SendRunStates(w.SessionId);
            foreach (var window in windows) _send.Window(phoneId, w.SessionId, window);
        }

        // The watcher reads the session's latest status every time it looks,
        // so a transcript path the hook names later — or a rotated one — is
        // picked up without re-watching.
        private TranscriptWatcher.Seams WatchSeamsFor(string sessionId)
        {
            var initial = _seams.WatchSeams(Local(sessionId)!);
            return initial with
            {
                PathOf = () => Local(sessionId) is { } now ? _seams.WatchSeams(now).PathOf() : null,
            };
        }

        private void Unwatch(string phoneId, UnwatchRequest u)
        {
            if (Listed(u.SessionId))
            {
                Ok(phoneId, u.Ref, RelayReplies.Unwatched, u.SessionId);
                return;
            }
            if (Local(u.SessionId) is null)
            {
                Err(phoneId, u.Ref, RelayReplies.UnknownSession, u.SessionId);
                return;
            }
            lock (_gate) Drop((phoneId, u.SessionId));
            Ok(phoneId, u.Ref, RelayReplies.Unwatched, u.SessionId);
        }

        private async Task InputAsync(string phoneId, InputRequest i)
        {
            if (Listed(i.SessionId))
            {
                // frames-v4: untypeable carries no `why` (only not_at_prompt, typing_failed,
                // no_terminal and launch_failed do), so the phone's own copy says it.
                Err(phoneId, i.Ref, RelayReplies.Untypeable, i.SessionId);
                return;
            }
            if (Local(i.SessionId) is not { } status)
            {
                Err(phoneId, i.Ref, RelayReplies.UnknownSession, i.SessionId);
                return;
            }
            var outcome = await _seams.Input.TypeAsync(status, i.Text).ConfigureAwait(false);
            if (outcome.Typed) Ok(phoneId, i.Ref, RelayReplies.Typed, i.SessionId);
            else Err(phoneId, i.Ref, outcome.Code!, i.SessionId, outcome.Why);
        }

        // ---- persona pictures (CB-321) ---------------------------------------------

        // A hash this desktop does not hold is refused at once; one it does is
        // queued behind any picture already going to this phone, and the first
        // request to find the phone idle carries the queue.
        private async Task ImageAsync(string phoneId, PersonaImageRequest r)
        {
            if (_seams.Images(r.Hash) is null)
            {
                UnknownImage(phoneId, r);
                return;
            }
            lock (_gate)
            {
                if (_imageQueues.TryGetValue(phoneId, out var queue))
                {
                    queue.Enqueue(r);
                    return;
                }
                _imageQueues[phoneId] = new Queue<PersonaImageRequest>();
            }

            for (PersonaImageRequest? next = r; next is not null;)
            {
                await SendImageAsync(phoneId, next).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_imageQueues.TryGetValue(phoneId, out var queue) && queue.Count > 0) next = queue.Dequeue();
                    else
                    {
                        _imageQueues.Remove(phoneId);
                        next = null;
                    }
                }
            }
        }

        private void UnknownImage(string phoneId, PersonaImageRequest r)
        {
            _seams.Log($"unknown_image {PersonaFrames.Short(r.Hash)}");
            _send.Reply(phoneId, null, RelayReplies.Err(r.Ref, RelayReplies.UnknownImage));
        }

        // The bundle's windows at ten a second. Looked up again here, since a
        // queued request can outlive the bundle's place in the bounded store.
        // Stops when the phone went offline (its generation moved) or the link
        // produced nothing for a window.
        private async Task SendImageAsync(string phoneId, PersonaImageRequest r)
        {
            if (_seams.Images(r.Hash) is not { } bundle)
            {
                UnknownImage(phoneId, r);
                return;
            }
            int generation;
            lock (_gate) generation = _imageGeneration.GetValueOrDefault(phoneId);
            foreach (var window in bundle.Windows)
            {
                if (window.From > 0) await _seams.Delay(ImageWindowEvery).ConfigureAwait(false);
                lock (_gate)
                    if (_imageGeneration.GetValueOrDefault(phoneId) != generation)
                    {
                        _seams.Log($"persona_image {PersonaFrames.Short(r.Hash)} abandoned at frame {window.From}: phone offline");
                        return;
                    }
                if (!_send.ImageWindow(phoneId, r.Hash, window.Json))
                {
                    _seams.Log($"persona_image {PersonaFrames.Short(r.Hash)} abandoned at frame {window.From}: not sent");
                    return;
                }
                _seams.Log($"persona_image {PersonaFrames.Short(r.Hash)} frames {window.From}..{window.From + window.Count - 1}/{bundle.Frames.Count}");
            }
        }

        // ---- launch ----------------------------------------------------------------

        // The gate here, with the phone as signer; the account, the start and
        // the check-in in LaunchRunner, which the peer path shares (CB-357).
        private async Task LaunchAsync(string phoneId, byte[] phoneIdPublic, LaunchRequest r)
        {
            var f = r.Fields;
            var runner = Runner();
            if (LaunchGate.Decide(r, phoneId, phoneIdPublic, _seams.Now(), _nonces, runner.Environment()) is { } refused)
            {
                Err(phoneId, r.Ref, refused);
                return;
            }

            var outcome = await runner.RunAsync(f, LaunchRunner.TrustPolicy.Report).ConfigureAwait(false);
            if (outcome.SessionId is not { } sessionId)
            {
                Err(phoneId, r.Ref, outcome.Code, why: outcome.Why);
                return;
            }
            Ok(phoneId, r.Ref, RelayReplies.Launched, sessionId);
            if (f.FirstMessage.Length == 0) return;

            // Codex and Grok were handed the message as their launch prompt, so
            // it is delivered the moment the session exists: `typed` here means
            // "delivered as the launch prompt" (PM decision, frames-v4 note).
            if (NewChatCommand.PromptAsArgument(LaunchGate.CliFor(f.Cli)))
            {
                Ok(phoneId, r.Ref, RelayReplies.Typed, sessionId);
                return;
            }

            var (typed, why) = await TypeFirstMessageAsync(sessionId, f.FirstMessage).ConfigureAwait(false);
            if (typed) Ok(phoneId, r.Ref, RelayReplies.Typed, sessionId);
            else Err(phoneId, r.Ref, RelayReplies.NotAtPrompt, sessionId, why);
        }

        private LaunchRunner Runner() =>
            new(new LaunchRunner.Seams(_seams.Launch, _seams.Accounts, Snapshot, _seams.Now, _seams.Delay));

        // Kept where RelayRemote's callers already look for it.
        internal static bool SameFolder(string? a, string b, bool isWindows) => LaunchRunner.SameFolder(a, b, isWindows);

        // The first message: once the new session is at its prompt (a
        // first-run trust dialog holds it until someone at the desk clears it),
        // typed through the same route as `input`. Up to 60 s, polled every
        // 500 ms; a session that can never be typed into stops at once.
        private async Task<(bool Typed, string? Why)> TypeFirstMessageAsync(string sessionId, string message)
        {
            var deadline = _seams.Now() + FirstMessageWait;
            string? why = null;
            while (true)
            {
                if (Local(sessionId) is not { } status) return (false, "the session ended");
                var outcome = await _seams.Input.TypeAsync(status, message).ConfigureAwait(false);
                if (outcome.Typed) return (true, null);
                why = outcome.Why;
                if (outcome.Code == RelayReplies.Untypeable) return (false, UntypeableWhy);
                if (outcome.Code != RelayReplies.NotAtPrompt) return (false, why);
                if (_seams.Now() >= deadline) return (false, why);
                await _seams.Delay(LaunchPoll).ConfigureAwait(false);
            }
        }
    }
}
