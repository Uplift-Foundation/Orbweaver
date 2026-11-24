namespace Orbweaver
{
    // What happens on this desk once a launch has passed LaunchGate: the
    // account it runs under, the terminal it starts in, and the wait for the
    // new session to check in. CB-357 took it out of RelayRemote, where it was
    // private to the phone path, so that a launch from another desktop over
    // the peer link runs through exactly the same steps — RelayClient exists
    // only once a phone is paired, so the peer path could not borrow it there.
    //
    // Who asked, and how they are answered, stays with the caller: the phone's
    // replies and its first-message typing in RelayRemote, the peer's
    // launch-result in PeerLaunchServer. Pure apart from its seams, so each arm
    // is a unit case.
    internal sealed class LaunchRunner
    {
        internal sealed record Seams(
            RelayRemote.LaunchSeams Launch,
            RelayRemote.AccountSeams? Accounts,
            Func<IReadOnlyList<(string SessionId, SessionStatus Status)>> Snapshot,
            Func<DateTimeOffset> Now,
            Func<TimeSpan, Task> Delay)
        {
            // CB-434: what a resume needs beyond a launch. Null means this desk
            // cannot resume, and a resume is refused as resume_unsupported.
            internal ResumeSeams? Resume { get; init; }
        }

        // Find: the session in this desk's own index for that CLI and account
        // (the account's resolved profile directory, null for the default),
        // older included; null when it is not there. Support: whether the
        // installed binary can resume by id (ResumeSupport.Probe, which may run
        // the CLI's --help and --version: blocking).
        internal sealed record ResumeSeams(
            Func<NewChatCli, string?, string, PastSession?> Find,
            Func<NewChatCli, string, ResumeCheck> Support)
        {
            // CB-434 (amendment 2): the phone's browse scope, null for a peer. A
            // folder outside it is unknown_resume, the same answer as an id this
            // desk does not have, before the index is consulted.
            internal Func<string, bool>? InScope { get; init; }
        }

        // Code is RelayReplies.Launched with the new session's id, or the
        // refusal's code with an optional sentence a person can act on.
        // Held is set only with PeerLaunchCodes.TrustNeeded: the pane the CLI
        // is asking in and the roster as it was before the start.
        internal sealed record Outcome(string Code, string? Why, string? SessionId, HeldTrust? Held = null);

        internal sealed record HeldTrust(string Pane, IReadOnlySet<string> Before, bool Hooks = false);

        // CB-397: what to do when the pane the CLI was started in is asking
        // whether to trust the folder. None never reads the pane (Windows,
        // and any caller with no trust seams). Report says so and leaves the
        // pane for the desk (the phone). Ask hands the question to the
        // launching desktop. Close is Ask for a desktop that cannot ask: it
        // says so and closes the pane.
        internal enum TrustPolicy { None, Report, Ask, Close }

        // The three things that touch a real terminal, kept behind seams so a
        // suite never starts or reads one. CaptureAndPress is CB-315's: the
        // approved look, then the key, as one tmux invocation.
        internal sealed record TrustSeams(
            Func<string, Task<string?>> ReadPane,
            Func<string, string, Func<string?, bool>, Task<(bool Pressed, string? Before)>> CaptureAndPress,
            Func<string, Task> ClosePane);

        private readonly Seams _seams;

        internal LaunchRunner(Seams seams) => _seams = seams;

        // What LaunchGate needs to know about this desk. A parent session must
        // be one of this desk's own local CLI sessions.
        internal LaunchGate.Environment Environment() => new(
            _seams.Launch.IsWindows,
            cli => _seams.Launch.Locate(cli) is not null,
            _seams.Launch.DirectoryExists,
            sid => Local(sid) is not null,
            sid => _seams.Snapshot().Any(e => e.SessionId == sid && e.Status.Source == SessionSource.Paperclip));

        internal SessionStatus? Local(string sessionId)
        {
            foreach (var (sid, status) in _seams.Snapshot())
                if (sid == sessionId && status.IsLocalCli) return status;
            return null;
        }

        // Account, start, check-in, in that order. The roster is read before
        // the start, so a session that was already running in that folder is
        // never mistaken for the new one.
        internal async Task<Outcome> RunAsync(LaunchFields f, TrustPolicy policy = TrustPolicy.None)
        {
            var resume = f.ResumeSessionId;

            // CB-434 (amendment 3): the phone's browse scope comes first of all
            // the resume checks, so a phone learns nothing about a session
            // outside its roots, not even that it is running. unknown_resume
            // is the same answer as an id this desk does not have.
            if (resume.Length > 0 && _seams.Resume?.InScope is { } inScope && !inScope(f.Cwd))
                return new(RelayReplies.UnknownResume, null, null);

            // CB-434: already running, before anything else is looked at. A
            // second process on one conversation would fight the first, so the
            // caller is sent to the one that exists and nothing is started.
            // Matched on CLI and id, not account (contract amendment 2, section
            // 4): account roots can share a projects folder, so the same session
            // is listed under each, and an account match would start a second
            // process on a conversation live under another. A live id under
            // another CLI is not this session and falls through.
            if (resume.Length > 0 && Local(resume) is { } live && MirrorProtocol.CliFor(live.Source) == f.Cli)
                return new(RelayReplies.AlreadyRunning, null, resume);

            if (!TryAccount(f, out var profileDir)) return new(RelayReplies.UnknownAccount, null, null);

            if (resume.Length > 0 && ResumeRefusal(f, profileDir) is { } refused) return refused;

            var before = _seams.Snapshot().Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);
            if (Start(f, profileDir, out var pane) is { } failed) return new(failed.Code, failed.Why, null);

            // Codex fires no hook until the first prompt of a resumed session
            // (CB-361, measured on the mini), so with no message there is
            // nothing to wait for: the caller says the orb appears when a
            // message is sent. With one, its hook fires on that turn, and it
            // waits like the others.
            if (resume.Length > 0 && f.FirstMessage.Length == 0 && LaunchGate.CliFor(f.Cli) == NewChatCli.Codex)
                return new(PeerLaunchCodes.ResumeStarted, null, resume);

            // Windows never reads: there is no seam that can read a console by
            // the pid StartWindows returns (CB-397 plan, point 6).
            var reads = policy != TrustPolicy.None && !_seams.Launch.IsWindows && _seams.Launch.Trust is not null && pane is not null;
            var (sessionId, prompt) = await AwaitNewSessionAsync(f, before, reads ? pane : null).ConfigureAwait(false);
            if (sessionId is not null) return new(RelayReplies.Launched, null, sessionId);
            if (prompt is null) return new(RelayReplies.NoSession, null, null);

            var cli = LaunchGate.CliFor(f.Cli);
            var name = NewChatLauncher.DisplayName(cli);
            if (policy == TrustPolicy.Report)
                return new(RelayReplies.LaunchFailed, name + " is asking whether to trust that folder. Answer it on the desk.", null);

            if (prompt.Kind == TrustPrompt.Kind.Answerable && policy == TrustPolicy.Ask)
                return new(PeerLaunchCodes.TrustNeeded, null, null, new HeldTrust(pane!, before, prompt.Hooks));

            await CloseIfStillAskingAsync(f.Cwd, cli, pane!, prompt.Kind).ConfigureAwait(false);
            return new(RelayReplies.LaunchFailed, prompt.Kind == TrustPrompt.Kind.Answerable
                ? name + " is asking whether to trust that folder. Update this machine to answer from here."
                : name + " is asking whether to trust that folder. Answer it once on that machine.", null);
        }

        // The session must be in this desk's own index for that CLI and account,
        // recorded in the folder the frame names — the folder is signed, so
        // what runs is what was asked for — and the installed CLI must be able
        // to resume by id. A missing folder never gets here: it is the gate's
        // unknown_cwd, so a conversation is never resumed somewhere else.
        private Outcome? ResumeRefusal(LaunchFields f, string? profileDir)
        {
            var cli = LaunchGate.CliFor(f.Cli);
            if (_seams.Resume is not { } resume)
                return new(RelayReplies.ResumeUnsupported, "This machine can't resume sessions.", null);

            if (resume.Find(cli, profileDir, f.ResumeSessionId) is not { } past
                || !SameFolder(past.Cwd, f.Cwd, _seams.Launch.IsWindows))
                return new(RelayReplies.UnknownResume, null, null);

            // The gate checked the CLI is installed, but that was another look;
            // one gone since is the same answer the gate gives.
            if (_seams.Launch.Locate(f.Cli) is not { } binary) return new(RelayReplies.UnknownCli, null, null);

            var support = resume.Support(cli, binary);
            if (support.Supported) return null;
            // A probe that could not read the help text is a failed attempt to
            // retry, not a CLI to update.
            return new(support.Checked ? RelayReplies.ResumeUnsupported : RelayReplies.LaunchFailed, support.Message, null);
        }

        // The physical spelling of a folder, once per use; a resolver that
        // throws or is absent leaves the string as asked. What is started is
        // never changed: only the comparison gets a second spelling.
        private string ResolveFolder(string cwd)
        {
            try { return _seams.Launch.Resolve?.Invoke(cwd) ?? cwd; }
            catch (Exception) { return cwd; }
        }

        // Closes the pane only while it still shows a trust prompt for the
        // launched folder. If the screen shows anything else, somebody answered it at that desk and
        // the session is theirs; the same look also guards a pane id reused
        // after a tmux server restart. Reads and kills are two processes
        // apart, which is the named residual window.
        internal async Task CloseIfStillAskingAsync(string cwd, NewChatCli cli, string pane, TrustPrompt.Kind expected = TrustPrompt.Kind.Answerable)
        {
            if (_seams.Launch.Trust is not { } trust) return;
            var screen = await trust.ReadPane(pane).ConfigureAwait(false);
            // The reading that led here, not "any trust prompt": Answerable for
            // the launched folder (either spelling) for a held question, and
            // the same Unanswerable reading for the close RunAsync makes on a
            // pane this very launch started. A reused pane id showing
            // somebody else's Claude prompt is Unanswerable, so it is never
            // killed on the held-question paths.
            if (TrustPrompt.Read(screen, cwd, cli, ResolveFolder(cwd)).Kind == expected)
                await trust.ClosePane(pane).ConfigureAwait(false);
        }

        // Answers Trust, never trusting where the cursor happens to be: Enter
        // is sent only after a fresh capture shows the marker on "Yes, I trust
        // this folder" for the launched folder.
        internal async Task<Outcome> AnswerTrustAsync(LaunchFields f, HeldTrust held)
        {
            var trust = _seams.Launch.Trust!;
            var cli = LaunchGate.CliFor(f.Cli);
            var resolved = ResolveFolder(f.Cwd);
            TrustPrompt.Reading Look(string? screen) => TrustPrompt.Read(screen, f.Cwd, cli, resolved);
            static Outcome Failed(string why) => new(RelayReplies.LaunchFailed, why, null);

            var first = Look(await trust.ReadPane(held.Pane).ConfigureAwait(false));
            if (first.Kind != TrustPrompt.Kind.Answerable)
            {
                // A read that merely failed also lands here, and the record is
                // already taken: nothing else will ever close a pane still on the prompt.
                await CloseIfStillAskingAsync(f.Cwd, cli, held.Pane).ConfigureAwait(false);
                return Failed("The trust question was no longer on screen.");
            }

            if (first.Marker == TrustPrompt.Option.No)
            {
                await trust.CaptureAndPress(held.Pane, "Down", s => Look(s) is { Kind: TrustPrompt.Kind.Answerable, Marker: TrustPrompt.Option.No }).ConfigureAwait(false);

                var onYes = false;
                for (var i = 0; i < 20 && !onYes; i++)
                {
                    onYes = Look(await trust.ReadPane(held.Pane).ConfigureAwait(false)) is { Kind: TrustPrompt.Kind.Answerable, Marker: TrustPrompt.Option.Yes };
                    if (!onYes) await _seams.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
                }
                if (!onYes)
                {
                    await CloseIfStillAskingAsync(f.Cwd, cli, held.Pane).ConfigureAwait(false);
                    return Failed("Couldn't select Yes on that machine.");
                }
            }

            var (pressed, _) = await trust.CaptureAndPress(held.Pane, "Enter", s => Look(s) is { Kind: TrustPrompt.Kind.Answerable, Marker: TrustPrompt.Option.Yes }).ConfigureAwait(false);
            if (!pressed)
            {
                await CloseIfStillAskingAsync(f.Cwd, cli, held.Pane).ConfigureAwait(false);
                return Failed("The trust question changed before it could be answered.");
            }

            // After Enter the pane is never closed by us: it is past the
            // question and belongs to the session.
            return await AwaitNewSessionAsync(f, held.Before, null).ConfigureAwait(false) is { SessionId: { } sessionId }
                ? new(RelayReplies.Launched, null, sessionId)
                : new(RelayReplies.NoSession, null, null);
        }

        // A launch that names no account runs under the designated one, else
        // the CLI's default. With no account seams at all, only a launch that
        // names none may proceed.
        private bool TryAccount(LaunchFields f, out string? profileDir)
        {
            profileDir = null;
            if (_seams.Accounts is not { } accounts) return f.Account.Length == 0;
            var cli = LaunchGate.CliFor(f.Cli);
            if (!RelayAccounts.TryResolve(cli, f.Account, accounts.Choices(cli), accounts.Designated(cli), null, out profileDir)) return false;
            // CB-397: the settings spelling (".claude-board") is relative, and
            // the CLI would resolve it against the project folder it starts in.
            // The local New Chat path makes it absolute against home first, and
            // null (the CLI's own default folder) means no variable at all.
            if (profileDir is not null)
                profileDir = NewChatLauncher.ConfigDirFor(cli, (_seams.Launch.Home ?? UserHome)(), profileDir);
            return true;
        }

        private static string UserHome() => System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

        // Starts the CLI where the contract says — tmux on the Mac, its own
        // conhost on Windows — or names why it could not.
        private (string Code, string Why)? Start(LaunchFields f, string? profileDir, out string? pane)
        {
            pane = null;
            var launch = _seams.Launch;
            var cli = LaunchGate.CliFor(f.Cli);
            var binary = launch.Locate(f.Cli)!;
            // ResumeRefusal has already held the id to IsSafeId, so ResumeArgs is never null here.
            var args = f.ResumeSessionId.Length > 0
                ? NewChatCommand.ResumeArgs(cli, f.ResumeSessionId, f.FirstMessage)!
                : NewChatCommand.LaunchArgs(cli, f.Model, f.ParentSessionId, f.FirstMessage);
            var name = NewChatLauncher.DisplayName(cli);
            if (launch.IsWindows)
            {
                var inArgv = NewChatCommand.PromptAsArgument(cli);
                // A batch shim (an npm-style codex.cmd) would be run by cmd
                // after all, and cmd would re-parse the message: refused rather
                // than started.
                if (inArgv && f.FirstMessage.Length > 0 && !binary.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return (RelayReplies.LaunchFailed, name + " here is a script, which cannot be handed a message safely.");
                var start = profileDir is null
                    ? NewChatCommand.ConsoleStartInfo(binary, f.Cwd, args, viaCmd: !inArgv)
                    : NewChatCommand.ConsoleStartInfoForAccount(cli, binary, f.Cwd, args, profileDir, viaCmd: !inArgv);
                if (start is null)
                    return (RelayReplies.LaunchFailed, "That account's folder can't be carried safely into a console for " + name + ".");
                return launch.StartWindows(start) is null
                    ? (RelayReplies.LaunchFailed, "Couldn't open a console for " + name + ".")
                    : null;
            }
            var (placed, why) = launch.PlaceMac(NewChatCommand.LaunchLine(launch.Shell(), binary, args, profileDir is null ? null : (cli, profileDir)), f.Cwd);
            pane = placed;
            if (pane is not null) return null;
            return why == TerminalScripts.NoTmuxWhy
                ? (RelayReplies.NoTerminal, why)
                : (RelayReplies.LaunchFailed, why ?? "Couldn't open a terminal for " + name + ".");
        }

        // A new local session of that CLI in that folder, one that was not in
        // the book before the launch. Polled every 500 ms for up to 30 s.
        //
        // With a pane, the pane is also read once a second from 1 s in, after
        // the roster check so a session that checks in always wins, and a trust
        // prompt ends the wait with the reading (CB-397).
        private async Task<(string? SessionId, TrustPrompt.Reading? Prompt)> AwaitNewSessionAsync(
            LaunchFields f, IReadOnlySet<string> before, string? pane)
        {
            var deadline = _seams.Now() + RelayRemote.LaunchCheckIn;
            var cli = LaunchGate.CliFor(f.Cli);
            var resolved = ResolveFolder(f.Cwd);
            for (var tick = 1; ; tick++)
            {
                foreach (var (sid, status) in _seams.Snapshot())
                    if (CheckedIn(f, before, resolved, sid, status)) return (sid, null);

                if (pane is not null && tick >= 3 && tick % 2 == 1)
                {
                    var reading = TrustPrompt.Read(await _seams.Launch.Trust!.ReadPane(pane).ConfigureAwait(false), f.Cwd, cli, resolved);
                    if (reading.Kind != TrustPrompt.Kind.None) return (null, reading);
                }

                if (_seams.Now() >= deadline) return (null, null);
                await _seams.Delay(RelayRemote.LaunchPoll).ConfigureAwait(false);
            }
        }

        // A launch is any new local session of that CLI in that folder. A resume
        // is exactly the resumed id (CB-434): another session starting in the
        // same folder meanwhile is somebody else's, never the one asked for.
        private bool CheckedIn(LaunchFields f, IReadOnlySet<string> before, string resolved, string sid, SessionStatus status)
        {
            if (before.Contains(sid) || !status.IsLocalCli) return false;
            if (f.ResumeSessionId.Length > 0) return sid == f.ResumeSessionId;
            return MirrorProtocol.CliFor(status.Source) == f.Cli
                && (SameFolder(status.Cwd, f.Cwd, _seams.Launch.IsWindows)
                    || SameFolder(status.Cwd, resolved, _seams.Launch.IsWindows));
        }

        internal static bool SameFolder(string? a, string b, bool isWindows)
        {
            if (string.IsNullOrEmpty(a)) return false;
            static string Trim(string p) => p.Length > 1 ? p.TrimEnd('/', '\\') : p;
            return string.Equals(Trim(a), Trim(b), isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        // Exists only so a suite can never start a real terminal: the loopback
        // tests drive PeerLaunchServer.Shared, whose start goes through
        // DefaultLaunchSeams, and a gate bug that let a launch through would
        // otherwise open tmux or conhost on whoever ran them. Null in
        // production and unset by anything but a test.
        internal static StartOverride? StartForTests;

        internal sealed record StartOverride(
            Func<string, string, (string? Pane, string? Why)> PlaceMac,
            Func<System.Diagnostics.ProcessStartInfo, int?> StartWindows);

        // The real desk's launch seams, shared by the phone path and the peer
        // path so the two can never start a CLI differently. Excluded: each is
        // one line into something that runs real processes.
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static RelayRemote.LaunchSeams DefaultLaunchSeams() => new(
            OperatingSystem.IsWindows(),
            cli => NewChatLauncher.LocateFresh(LaunchGate.CliFor(cli)),
            Directory.Exists,
            () => System.Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } sh ? sh : "/bin/zsh",
            PlaceMac,
            StartWindows,
            UserHome,
            DefaultTrustSeams(),
            FolderResolver.ResolveReal);

        // The three real calls: read a pane, press a key under CB-315's
        // approval, close a pane. A status carrying only a pane id routes to
        // tmux on its default socket, which is what PlaceForPhone starts on.
        // Each looks TrustForTests up when it is called, like the start does.
        // CB-434: this desk's own index and version gate. Older included: a
        // session the caller saw in an "Older" listing must still be found.
        // The recent window is read first, so the common case never pays for
        // a walk of every transcript ever written.
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static ResumeSeams DefaultResumeSeams() => new(
            (cli, profileDir, id) =>
            {
                var now = DateTimeOffset.UtcNow;
                return PastSessionIndex.Load(cli, profileDir, false, now).FirstOrDefault(s => s.Id == id)
                    ?? PastSessionIndex.Load(cli, profileDir, true, now).FirstOrDefault(s => s.Id == id);
            },
            ResumeSupport.Probe);

        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static TrustSeams DefaultTrustSeams() => new(ReadPane, PressKey, ClosePane);

        // Null in production and unset by anything but a test, so a suite can
        // never read or kill a real pane.
        internal static TrustSeams? TrustForTests;

        private static Task<string?> ReadPane(string pane) =>
            TrustForTests is { } fake ? fake.ReadPane(pane) : TmuxScreenReader.Instance.ReadAsync(PaneStatus(pane));

        private static Task<(bool Pressed, string? Before)> PressKey(string pane, string key, Func<string?, bool> approved) =>
            TrustForTests is { } fake ? fake.CaptureAndPress(pane, key, approved) : TerminalFocuser.CaptureAndPress(PaneStatus(pane), key, approved);

        private static Task ClosePane(string pane) =>
            TrustForTests is { } fake ? fake.ClosePane(pane) : Task.Run(() => TerminalFocuser.TryRun(
                TerminalFocuser.ResolveTmuxBinary(null) ?? "tmux", out _, TerminalScripts.TmuxKillPaneArgs(pane)));

        internal static SessionStatus PaneStatus(string pane) =>
            new() { Source = SessionSource.ClaudeCode, TmuxPane = pane };

        // Looked up when the start is called, not when the seams are built:
        // PeerLaunchServer.Shared builds its seams once per process, so an
        // override read at that moment would either miss a test that set it
        // later or leave a stale fake behind for every class after it.
        private static (string? Pane, string? Why) PlaceMac(string command, string cwd) =>
            StartForTests is { } fake ? fake.PlaceMac(command, cwd) : TerminalLauncher.PlaceForPhone(command, cwd);

        private static int? StartWindows(System.Diagnostics.ProcessStartInfo start) =>
            StartForTests is { } fake ? fake.StartWindows(start) : TerminalLauncher.StartConsole(start);
    }
}
