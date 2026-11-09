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
            Func<ProcessStartInfo, int?> StartWindows);

        internal sealed record Seams(
            Func<DateTimeOffset> Now,
            Func<TimeSpan, Task> Delay,
            Func<SessionStatus, TranscriptWatcher.Seams> WatchSeams,
            RelayInput Input,
            LaunchSeams Launch,
            Action<string> Log);

        private readonly Seams _seams;
        private readonly IRemoteSender _send;
        private readonly NonceWindow _nonces;
        private readonly object _gate = new();
        private readonly Dictionary<(string Phone, string Session), TranscriptWatcher> _watches = new();
        private IReadOnlyList<(string SessionId, SessionStatus Status)> _snapshot = [];

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
                foreach (var key in _watches.Keys.Where(k => Local(k.Session) is null).ToList()) _watches.Remove(key);
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

        private IReadOnlyList<(string SessionId, SessionStatus Status)> Snapshot()
        {
            lock (_gate) return _snapshot;
        }

        // ---- watch lifetime ----------------------------------------------------

        internal void PhoneOffline(string phoneId)
        {
            lock (_gate)
                foreach (var key in _watches.Keys.Where(k => k.Phone == phoneId).ToList()) _watches.Remove(key);
        }

        // The socket closed: every watch dies, and the phone re-watches on
        // reconnect (frames-v4 watch.rules).
        internal void Disconnected()
        {
            lock (_gate) _watches.Clear();
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
            // The only other kind RelayRequests makes.
            _ => Task.Run(() => Err(phoneId, request.Ref, RelayReplies.BadFrame, ((BadFrameRequest)request).SessionId)),
        };

        private void Err(string phoneId, string reference, string code, string? sessionId = null, string? why = null)
        {
            _seams.Log($"err {RelayLink.Safe(reference)} {code}");
            _send.Reply(phoneId, sessionId is not null && Local(sessionId) is not null ? sessionId : null,
                RelayReplies.Err(reference, code, sessionId, why));
        }

        private void Ok(string phoneId, string reference, string code, string sessionId, long? total = null)
        {
            _seams.Log($"ok {RelayLink.Safe(reference)} {code}");
            _send.Reply(phoneId, sessionId, RelayReplies.Ok(reference, code, sessionId, total));
        }

        private void Watch(string phoneId, WatchRequest w)
        {
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
            lock (_gate) _watches[(phoneId, w.SessionId)] = watcher;
            Ok(phoneId, w.Ref, RelayReplies.Watching, w.SessionId, total);
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
            if (Local(u.SessionId) is null)
            {
                Err(phoneId, u.Ref, RelayReplies.UnknownSession, u.SessionId);
                return;
            }
            lock (_gate) _watches.Remove((phoneId, u.SessionId));
            Ok(phoneId, u.Ref, RelayReplies.Unwatched, u.SessionId);
        }

        private async Task InputAsync(string phoneId, InputRequest i)
        {
            if (Local(i.SessionId) is not { } status)
            {
                Err(phoneId, i.Ref, RelayReplies.UnknownSession, i.SessionId);
                return;
            }
            var outcome = await _seams.Input.TypeAsync(status, i.Text).ConfigureAwait(false);
            if (outcome.Typed) Ok(phoneId, i.Ref, RelayReplies.Typed, i.SessionId);
            else Err(phoneId, i.Ref, outcome.Code!, i.SessionId, outcome.Why);
        }

        // ---- launch ----------------------------------------------------------------

        private async Task LaunchAsync(string phoneId, byte[] phoneIdPublic, LaunchRequest r)
        {
            var f = r.Fields;
            var env = new LaunchGate.Environment(
                _seams.Launch.IsWindows,
                cli => _seams.Launch.Locate(cli) is not null,
                _seams.Launch.DirectoryExists,
                sid => Local(sid) is not null);
            if (LaunchGate.Decide(r, phoneId, phoneIdPublic, _seams.Now(), _nonces, env) is { } refused)
            {
                Err(phoneId, r.Ref, refused);
                return;
            }

            var before = Snapshot().Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);
            if (Start(f) is { } failed)
            {
                Err(phoneId, r.Ref, failed.Code, why: failed.Why);
                return;
            }

            var sessionId = await AwaitNewSessionAsync(f, before).ConfigureAwait(false);
            if (sessionId is null)
            {
                Err(phoneId, r.Ref, RelayReplies.NoSession);
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

        // Starts the CLI where the contract says — tmux on the Mac, its own
        // conhost on Windows — or names why it could not.
        private (string Code, string Why)? Start(LaunchFields f)
        {
            var cli = LaunchGate.CliFor(f.Cli);
            var binary = _seams.Launch.Locate(f.Cli)!;
            var args = NewChatCommand.LaunchArgs(cli, f.Model, f.ParentSessionId, f.FirstMessage);
            var name = NewChatLauncher.DisplayName(cli);
            if (_seams.Launch.IsWindows)
            {
                var inArgv = NewChatCommand.PromptAsArgument(cli);
                // A batch shim (an npm-style codex.cmd) would be run by cmd
                // after all, and cmd would re-parse the message: refused rather
                // than started.
                if (inArgv && f.FirstMessage.Length > 0 && !binary.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return (RelayReplies.LaunchFailed, name + " here is a script, which cannot be handed a message safely.");
                var start = NewChatCommand.ConsoleStartInfo(binary, f.Cwd, args, viaCmd: !inArgv);
                return _seams.Launch.StartWindows(start) is null
                    ? (RelayReplies.LaunchFailed, "Couldn't open a console for " + name + ".")
                    : null;
            }
            var (pane, why) = _seams.Launch.PlaceMac(NewChatCommand.LaunchLine(_seams.Launch.Shell(), binary, args), f.Cwd);
            if (pane is not null) return null;
            return why == TerminalScripts.NoTmuxWhy
                ? (RelayReplies.NoTerminal, why)
                : (RelayReplies.LaunchFailed, why ?? "Couldn't open a terminal for " + name + ".");
        }

        // A new local session of that CLI in that folder, one that was not in
        // the book before the launch. Polled every 500 ms for up to 30 s.
        private async Task<string?> AwaitNewSessionAsync(LaunchFields f, HashSet<string> before)
        {
            var deadline = _seams.Now() + LaunchCheckIn;
            while (true)
            {
                foreach (var (sid, status) in Snapshot())
                    if (!before.Contains(sid) && status.IsLocalCli && MirrorProtocol.CliFor(status.Source) == f.Cli
                        && SameFolder(status.Cwd, f.Cwd, _seams.Launch.IsWindows))
                        return sid;
                if (_seams.Now() >= deadline) return null;
                await _seams.Delay(LaunchPoll).ConfigureAwait(false);
            }
        }

        internal static bool SameFolder(string? a, string b, bool isWindows)
        {
            if (string.IsNullOrEmpty(a)) return false;
            static string Trim(string p) => p.Length > 1 ? p.TrimEnd('/', '\\') : p;
            return string.Equals(Trim(a), Trim(b), isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

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
