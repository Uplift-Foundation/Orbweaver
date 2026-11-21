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
            Func<TimeSpan, Task> Delay);

        // Code is RelayReplies.Launched with the new session's id, or the
        // refusal's code with an optional sentence a person can act on.
        internal sealed record Outcome(string Code, string? Why, string? SessionId);

        private readonly Seams _seams;

        internal LaunchRunner(Seams seams) => _seams = seams;

        // What LaunchGate needs to know about this desk. A parent session must
        // be one of this desk's own local CLI sessions.
        internal LaunchGate.Environment Environment() => new(
            _seams.Launch.IsWindows,
            cli => _seams.Launch.Locate(cli) is not null,
            _seams.Launch.DirectoryExists,
            sid => Local(sid) is not null);

        internal SessionStatus? Local(string sessionId)
        {
            foreach (var (sid, status) in _seams.Snapshot())
                if (sid == sessionId && status.IsLocalCli) return status;
            return null;
        }

        // Account, start, check-in, in that order. The roster is read before
        // the start, so a session that was already running in that folder is
        // never mistaken for the new one.
        internal async Task<Outcome> RunAsync(LaunchFields f)
        {
            if (!TryAccount(f, out var profileDir)) return new(RelayReplies.UnknownAccount, null, null);

            var before = _seams.Snapshot().Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);
            if (Start(f, profileDir) is { } failed) return new(failed.Code, failed.Why, null);

            return await AwaitNewSessionAsync(f, before).ConfigureAwait(false) is { } sessionId
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
        private (string Code, string Why)? Start(LaunchFields f, string? profileDir)
        {
            var launch = _seams.Launch;
            var cli = LaunchGate.CliFor(f.Cli);
            var binary = launch.Locate(f.Cli)!;
            var args = NewChatCommand.LaunchArgs(cli, f.Model, f.ParentSessionId, f.FirstMessage);
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
            var (pane, why) = launch.PlaceMac(NewChatCommand.LaunchLine(launch.Shell(), binary, args, profileDir is null ? null : (cli, profileDir)), f.Cwd);
            if (pane is not null) return null;
            return why == TerminalScripts.NoTmuxWhy
                ? (RelayReplies.NoTerminal, why)
                : (RelayReplies.LaunchFailed, why ?? "Couldn't open a terminal for " + name + ".");
        }

        // A new local session of that CLI in that folder, one that was not in
        // the book before the launch. Polled every 500 ms for up to 30 s.
        private async Task<string?> AwaitNewSessionAsync(LaunchFields f, HashSet<string> before)
        {
            var deadline = _seams.Now() + RelayRemote.LaunchCheckIn;
            while (true)
            {
                foreach (var (sid, status) in _seams.Snapshot())
                    if (!before.Contains(sid) && status.IsLocalCli && MirrorProtocol.CliFor(status.Source) == f.Cli
                        && SameFolder(status.Cwd, f.Cwd, _seams.Launch.IsWindows))
                        return sid;
                if (_seams.Now() >= deadline) return null;
                await _seams.Delay(RelayRemote.LaunchPoll).ConfigureAwait(false);
            }
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
            UserHome);

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
