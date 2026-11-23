using System.Diagnostics.CodeAnalysis;

namespace Orbweaver
{
    // Which platform arm RealLaunch took, so Decide (below) can be asked about
    // every branch as a table lookup rather than by re-running RealLaunch on a
    // real machine of each kind.
    internal enum NewChatPlatform
    {
        Windows,
        MacOS,
        Other
    }

    // Starts one of the three local CLIs in a terminal, on a chosen folder —
    // CB-168's "start a new chat" action. No new orb plumbing is needed for
    // these three: the CLI's own installed hook writes the status file the
    // moment it starts, and the existing scan draws the orb, the same way it
    // does for a CLI a user started by hand.
    internal static class NewChatLauncher
    {
        // The dialog's own seam. A UI test drives Start without spawning a
        // real terminal, the same FakeChatSession-shaped seam CLAUDE.md asks
        // for elsewhere in this app — see NewChatAvailability.CurrentForTests
        // for the matching seam on the other half of the dialog.
        internal static Func<NewChatCli, string, string?, LaunchResult>? LaunchForTests;

        internal static LaunchResult Launch(NewChatCli cli, string cwd, string? profileDir = null) =>
            LaunchForTests?.Invoke(cli, cwd, profileDir) ?? RealLaunch(cli, cwd, profileDir);

        // The real launch: locate the binary fresh (never the cached
        // ClaudeBinary/CodexBinary/GrokBinary.Path — see NewChatAvailability
        // for why a fresh Locate() matters here), resolve the working
        // directory, run the real I/O for whichever platform this is, and
        // hand every outcome to Decide, which is the only place the actual
        // LaunchResult gets built.
        //
        // Excluded from coverage as a whole, the same way
        // AgentTeamViewer.AttachSessionOnWindows is: every line below either
        // starts a real subprocess, touches the real filesystem (LocateFresh,
        // Directory.Exists), or calls into TerminalLauncher, which is itself
        // excluded for running ps/tmux/osascript/wt.exe. QA (CB-168) flagged
        // an earlier version of this method for folding its *decisions* —
        // which message a NotFound/SpawnFailed/Launched outcome carries, the
        // cwd fallback, the tmux-before-window preference — into that same
        // excluded method, where nothing could assert a swapped message or a
        // flipped preference. Decide and ResolveDirectory below are the fix:
        // every decision this method makes is now a call to one of them, and
        // both are plain functions a test can drive directly.
        [ExcludeFromCodeCoverage]
        private static LaunchResult RealLaunch(NewChatCli cli, string cwd, string? profileDir)
        {
            var name = DisplayName(cli);
            var binary = LocateFresh(cli);

            if (binary is null)
            {
                return Decide(name, null, cwd, NewChatPlatform.Other, null, null, null);
            }

            var directory = ResolveDirectory(cwd, Directory.Exists(cwd), Environment.CurrentDirectory);

            // Computed once and handed to both platform arms below, so
            // there's exactly one place per launch that decides which
            // account this chat runs under.
            var configDir = ConfigDirFor(
                cli, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), profileDir);

            if (OperatingSystem.IsWindows())
            {
                var started = TerminalLauncher.StartWindowsProcess(useWindowsTerminal =>
                    NewChatCommand.WindowsProcessStartInfo(cli, binary, directory, useWindowsTerminal, configDir));

                return Decide(name, binary, directory, NewChatPlatform.Windows, started, null, null);
            }

            if (!OperatingSystem.IsMacOS())
            {
                return Decide(name, binary, directory, NewChatPlatform.Other, null, null, null);
            }

            // NewChatCommand.ExecLine owns the "exec " — see its comment for
            // why it is not written here any more (CB-232).
            var command = NewChatCommand.ExecLine(cli, binary, configDir);

            // In the user's tmux first, for the same reason
            // AgentTeamViewer.AttachSession prefers tmux over a bare window:
            // someone who lives in tmux gets the chat inside the thing they use
            // to move between windows, rather than a window outside it. But in
            // a tmux window of its own, not split beside them the way an orb
            // attach is — a new chat is new work, and halving the window they
            // are in to make room for it was reported as the wrong thing the
            // first time it happened (see TerminalScripts.NewChatPlacementFor).
            // The terminal-window fallback is only even attempted when
            // tmux didn't take the command, which is why terminalLaunched
            // stays null rather than false on the path that never runs it —
            // Decide treats "never tried" and "tried and failed" as the same
            // failure, but RealLaunch itself still only opens one window.
            var tmuxPane = TerminalLauncher.PlaceInOwnTmuxWindow(command, directory);
            bool? terminalLaunched = null;

            if (string.IsNullOrEmpty(tmuxPane))
            {
                terminalLaunched = TerminalLauncher.LaunchInTerminal(
                    TerminalLauncher.TerminalApp(), directory, command);
            }

            return Decide(name, binary, directory, NewChatPlatform.MacOS, null, tmuxPane, terminalLaunched);
        }

        // The account directory to hand this launch, given the account the
        // dialog's picker chose (CB-201, and CB-203 for Codex and Grok) — null
        // for the default account, so the CLI's variable is left exactly as
        // Buddy's own environment has it. Which variable that is, and which
        // directory counts as the default, comes from NewChatAccountHome.
        //
        // Delegates to ClaudeProfile.ConfigDirFor rather than repeating its
        // CB-42 logic — there is exactly one place in this app that decides
        // "does this profile name the default account", and this is not it.
        internal static string? ConfigDirFor(NewChatCli cli, string home, string? profileDir) =>
            ClaudeProfile.ConfigDirFor(home, profileDir, NewChatAccountHome.For(cli).DefaultDirName);

        // The working directory to actually launch into: the requested one
        // when it's real, the process's own current directory otherwise —
        // the same fallback AgentTeamViewer.AttachSessionOnWindows already
        // uses for an orb whose recorded cwd no longer exists.
        internal static string ResolveDirectory(string cwd, bool cwdExists, string currentDirectory) =>
            cwdExists ? cwd : currentDirectory;

        // Every decision behind the LaunchResult a click on Start sees: which
        // outcome it is, and what the message says. Takes the *results* of
        // whichever I/O RealLaunch already ran — never runs any itself — so
        // a test can hand it every combination directly rather than needing a
        // Windows box, a Mac, a missing binary and a dead tmux server to prove
        // each branch is wired to the right message.
        //
        // windowsLaunched only means anything when platform is Windows;
        // macTmuxPane/macTerminalLaunched only when platform is MacOS. Passing
        // the "wrong" ones for a given platform is harmless — the switch below
        // never reads them — which is why Decide takes all of them rather than
        // being three separate overloads RealLaunch would have to pick between.
        internal static LaunchResult Decide(
            string displayName, string? binary, string directory, NewChatPlatform platform,
            bool? windowsLaunched, string? macTmuxPane, bool? macTerminalLaunched, string verb = "started")
        {
            if (binary is null)
            {
                return new LaunchResult(
                    LaunchOutcome.NotFound,
                    displayName + " " + NewChatAvailability.NotFoundReasonSuffix + ".");
            }

            return platform switch
            {
                NewChatPlatform.Windows => windowsLaunched == true
                    ? Launched(displayName, directory, verb)
                    : SpawnFailed(displayName),

                NewChatPlatform.MacOS => !string.IsNullOrEmpty(macTmuxPane) || macTerminalLaunched == true
                    ? Launched(displayName, directory, verb)
                    : SpawnFailed(displayName),

                _ => new LaunchResult(
                    LaunchOutcome.SpawnFailed, "Starting a new chat isn't supported on this platform yet.")
            };
        }

        private static LaunchResult Launched(string displayName, string directory, string verb) =>
            new(LaunchOutcome.Launched, displayName + " " + verb + " in " + directory + ".");

        // ---- CB-361: resume a past session --------------------------------------

        // The dialog's seam for Resume, the twin of LaunchForTests: the session
        // chosen and the account's profile directory.
        internal static Func<PastSession, string?, LaunchResult>? ResumeForTests;

        // Starts the session's own CLI with its resume arguments, in the
        // session's own folder, under the chosen account. Never in a fallback
        // folder: a conversation resumed in the wrong project would run its
        // tools against the wrong files.
        internal static LaunchResult Resume(PastSession session, string? profileDir = null) =>
            ResumeForTests?.Invoke(session, profileDir) ?? RealResume(session, profileDir);

        // Every reason a resume is refused before anything starts, in the order
        // they are checked; null when it may go ahead. Pure, so each refusal's
        // wording is a test rather than a trip to a machine without the CLI.
        internal static LaunchResult? ResumeRefusal(PastSession session, string? binary, bool folderExists, ResumeCheck? support)
        {
            var name = DisplayName(session.Cli);
            if (binary is null)
                return new LaunchResult(LaunchOutcome.NotFound, name + " " + NewChatAvailability.NotFoundReasonSuffix + ".");
            if (NewChatCommand.ResumeArgs(session.Cli, session.Id) is null)
                return new LaunchResult(LaunchOutcome.Unsupported, "That session's id can't be resumed from here.");
            if (session.Cwd is null || !folderExists)
                return new LaunchResult(LaunchOutcome.Unsupported,
                    "That session's folder is missing" + (session.Cwd is null ? "" : " (" + session.Cwd + ")") + ".");
            if (support is { Supported: false })
                return new LaunchResult(LaunchOutcome.Unsupported, support.Message);
            return null;
        }

        // Excluded for the same reason RealLaunch is: every line is real IO,
        // and every decision is ResumeRefusal, ConfigDirFor, ResumeArgs,
        // ExecLine or Decide, each tested directly.
        [ExcludeFromCodeCoverage]
        private static LaunchResult RealResume(PastSession session, string? profileDir)
        {
            var cli = session.Cli;
            var name = DisplayName(cli);
            var binary = LocateFresh(cli);
            var folderExists = session.Cwd is not null && Directory.Exists(session.Cwd);
            var support = binary is null ? null : ResumeSupport.Probe(cli, binary);

            if (ResumeRefusal(session, binary, folderExists, support) is { } refused) return refused;

            var directory = session.Cwd!;
            var args = NewChatCommand.ResumeArgs(cli, session.Id)!;
            var configDir = ConfigDirFor(
                cli, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), profileDir);

            if (OperatingSystem.IsWindows())
            {
                var started = TerminalLauncher.StartWindowsProcess(useWindowsTerminal =>
                    NewChatCommand.WindowsProcessStartInfo(cli, binary, directory, useWindowsTerminal, configDir, args));
                return Decide(name, binary, directory, NewChatPlatform.Windows, started, null, null, "resumed");
            }

            if (!OperatingSystem.IsMacOS())
                return Decide(name, binary, directory, NewChatPlatform.Other, null, null, null, "resumed");

            var command = NewChatCommand.ExecLine(cli, binary!, configDir, args);
            var tmuxPane = TerminalLauncher.PlaceInOwnTmuxWindow(command, directory);
            bool? terminalLaunched = null;
            if (string.IsNullOrEmpty(tmuxPane))
                terminalLaunched = TerminalLauncher.LaunchInTerminal(TerminalLauncher.TerminalApp(), directory, command);

            return Decide(name, binary, directory, NewChatPlatform.MacOS, null, tmuxPane, terminalLaunched, "resumed");
        }

        private static LaunchResult SpawnFailed(string displayName) =>
            new(LaunchOutcome.SpawnFailed, "Couldn't open a terminal for " + displayName + ".");

        // CB-357: lets a test say which CLIs are installed, process-wide, so a
        // gate that checks the CLI before the folder answers the same on a
        // runner with no claude as on a developer's machine. When set, its
        // answer is final, including null for "not installed". Anything that
        // sets it belongs in the Settings collection and clears it afterwards.
        internal static Func<NewChatCli, string?>? LocateFreshForTests;

        [ExcludeFromCodeCoverage]
        internal static string? LocateFresh(NewChatCli cli) =>
            LocateFreshForTests is { } fake
                ? fake(cli)
                : cli switch
                {
                    NewChatCli.ClaudeCode => ClaudeBinary.Locate(),
                    NewChatCli.Codex => CodexBinary.Locate(),
                    NewChatCli.Grok => GrokBinary.Locate(),
                    _ => null
                };

        // Pure and used by both this file and NewChatAvailability's reason
        // text, so the two never drift into naming a CLI differently.
        internal static string DisplayName(NewChatCli cli) => cli switch
        {
            NewChatCli.ClaudeCode => "Claude Code",
            NewChatCli.Codex => "Codex",
            NewChatCli.Grok => "Grok",
            _ => cli.ToString()
        };
    }
}
