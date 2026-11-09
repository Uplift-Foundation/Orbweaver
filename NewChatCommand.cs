using System.Diagnostics;

namespace Orbweaver
{
    // Which of the three local CLIs CB-168's "start a new chat" dialog can
    // launch. OpenClaw is a fourth entry in the dialog itself, but it never
    // reaches this file: it has no binary to shell out to and no terminal to
    // open — see NewChatAvailability and the OpenClaw arm of the plan.
    internal enum NewChatCli
    {
        ClaudeCode,
        Codex,
        Grok
    }

    // What happened when NewChatLauncher.Launch tried to start one.
    //
    // A launch is reported, never silent (CB-168's acceptance criterion): a
    // click that opens nothing and says nothing is indistinguishable from the
    // app being broken, which is the exact complaint AgentTeamViewer's whole
    // click-ladder history is about.
    internal enum LaunchOutcome
    {
        Launched,
        NotFound,
        SpawnFailed
    }

    // Outcome plus the message the dialog shows inline. Message is always
    // present — even Launched carries one, which is what lets the dialog
    // print an inline confirmation instead of asking the caller to invent
    // wording for the success case.
    internal sealed record LaunchResult(LaunchOutcome Outcome, string Message);

    // The command line and the Windows launch description for one CLI — the
    // parts of "start a new chat" that are pure and decide nothing about the
    // OS. Built on TerminalScripts.ShellQuote/ShellCommandLine, the same
    // quoting rule AgentTeamViewer has carried since `claude attach` was first
    // wired up, so a directory or CLI install path with a space or an
    // apostrophe in it is handled once rather than reinvented here.
    internal static class NewChatCommand
    {
        // The shell command that starts a fresh conversation: the bare binary,
        // quoted, with no verb — `claude attach <id>` names a session to
        // rejoin, and starting a new chat names nothing, it just runs the CLI.
        //
        // configDir null (the default account — see
        // NewChatLauncher.ConfigDirFor) leaves the command exactly as it was:
        // the quoted binary alone, never anything that names the default
        // directory (CB-42's whole point). Set, it becomes
        // `env <VAR>='<dir>' '<binary>'` — CLAUDE_CONFIG_DIR, CODEX_HOME or
        // GROK_HOME, whichever NewChatAccountHome says this CLI reads (CB-203).
        //
        // **Always a program first, never a variable assignment** (CB-232).
        // This used to be `CLAUDE_CONFIG_DIR='<dir>' '<binary>'`, a POSIX
        // prefix assignment — fine on its own, but RealLaunch prefixed it with
        // "exec ", and `exec CLAUDE_CONFIG_DIR=… claude` hands the assignment
        // to exec as the program name. Every named-account launch on macOS
        // died at once: exit 127 in zsh, dash and ksh, 126 in sh and bash,
        // measured on this repo's MacBook, while the default account (no
        // assignment) worked, which is why it shipped. `env` keeps the first
        // word a program whatever is put in front of it, and it does not
        // depend on how a given shell exports an assignment written before
        // the `exec` builtin. Both forms were measured working in sh, bash,
        // zsh, dash and ksh; this one was chosen for not having a grammar to
        // get wrong. env then execs the CLI itself, so the process a terminal
        // ends up running is the CLI, the same as the default account's.
        internal static string For(NewChatCli cli, string binaryPath, string? configDir = null) =>
            configDir is null
                ? TerminalScripts.ShellQuote(binaryPath)
                : "env " + NewChatAccountHome.For(cli).EnvVar + "=" + TerminalScripts.ShellQuote(configDir)
                    + " " + TerminalScripts.ShellQuote(binaryPath);

        // The line a terminal or tmux window actually runs: For's command with
        // "exec " in front, so the terminal's own shell becomes the CLI rather
        // than waiting behind it — see AgentTeamViewer.AttachSession for the
        // pattern. The one place a new chat's `exec` is written (CB-232):
        // RealLaunch used to add it itself, out of sight of the tests that
        // pinned For, which is how an assignment ended up after it.
        internal static string ExecLine(NewChatCli cli, string binaryPath, string? configDir = null) =>
            "exec " + For(cli, binaryPath, configDir);

        // The general Windows launch shape: wt.exe when it's installed
        // (`-d <cwd> cmd.exe /k <exe> [args...]`, which gives the CLI an
        // ordinary tab), cmd.exe when it isn't (`/k <exe> [args...]`, still a
        // visible window). Both pass the exe and its arguments separately,
        // never through a shell string, so a directory or CLI path containing
        // spaces remains one argument — the same guarantee
        // AgentTeamViewer.WindowsAttachStartInfo already gave for `claude
        // attach`.
        //
        // General rather than copied: AgentTeamViewer.WindowsAttachStartInfo
        // now calls this with extraArgs = ["attach", jobId], and
        // WindowsProcessStartInfo below calls it with extraArgs = null (a new
        // chat has no verb) — one argument-layout builder instead of two.
        //
        // Pure: it only builds a ProcessStartInfo and starts nothing, so a
        // test can assert on FileName/ArgumentList/WorkingDirectory directly,
        // the way WindowsAttachLaunchTests already does for the attach case.
        // CB-316: the arguments a phone's launch adds after the binary — the
        // model, and for Claude Code the lead it joins. Each CLI's own spelling
        // (claude --model, codex -m, grok -m, all verified against their
        // --help), and nothing for an empty value, so a launch with neither is
        // exactly the desk's New Chat. --parent-session-id is the flag
        // AgentTeam reads off the process to link the new orb to its lead
        // (measured on a hand launch, CB-316); the gate refuses it for the
        // other two CLIs before this is reached.
        internal static List<string> LaunchArgs(NewChatCli cli, string model, string parentSessionId)
        {
            var args = new List<string>();
            if (model.Length > 0)
            {
                args.Add(cli == NewChatCli.ClaudeCode ? "--model" : "-m");
                args.Add(model);
            }
            if (parentSessionId.Length > 0 && cli == NewChatCli.ClaudeCode)
            {
                args.Add("--parent-session-id");
                args.Add(parentSessionId);
            }
            return args;
        }

        // The Mac command line for a phone's launch: the binary and its
        // arguments, each shell-quoted, run by the user's login shell so a
        // tmux server this app started (with launchd's bare PATH) still finds
        // node for Codex and whatever else the user's profile puts on PATH.
        internal static string LaunchLine(string shell, string binaryPath, IReadOnlyList<string> args)
        {
            var inner = "exec " + string.Join(" ", new[] { binaryPath }.Concat(args).Select(TerminalScripts.ShellQuote));
            return TerminalScripts.ShellQuote(shell) + " -lc " + TerminalScripts.ShellQuote(inner);
        }

        // CB-316: a phone's launch on Windows, in a console window conhost
        // itself owns: `conhost.exe cmd.exe /k <cli> <args>`. Not a bare
        // `cmd.exe /k`: where Windows' default terminal is set to Windows
        // Terminal, that hands the console to WT through a pseudo console, so
        // cmd owns no window, WT is not an ancestor and WT_SESSION is not in
        // the CLI's environment. The hook's walk then finds no terminal,
        // records term_pid 0, and the scan drops the session as NoTerminal —
        // a launch that always ends `no_session` (QA, CB-316). Measured in
        // session 2 on the Windows box: cmd's window handle was 0 under a bare
        // cmd.exe and non-zero under conhost.exe. Naming conhost.exe is also
        // what the contract's "conhost, not wt.exe" means in practice.
        internal static ProcessStartInfo ConsoleStartInfo(string exe, string cwd, IReadOnlyList<string> args)
        {
            var start = new ProcessStartInfo("conhost.exe") { UseShellExecute = true, WorkingDirectory = cwd };
            start.ArgumentList.Add("cmd.exe");
            start.ArgumentList.Add("/k");
            start.ArgumentList.Add(exe);
            foreach (var arg in args) start.ArgumentList.Add(arg);
            return start;
        }

        internal static ProcessStartInfo? GeneralWindowsStartInfo(
            string? exe, string cwd, IReadOnlyList<string>? extraArgs, bool useWindowsTerminal)
        {
            if (string.IsNullOrEmpty(exe)) return null;

            var start = new ProcessStartInfo(useWindowsTerminal ? "wt.exe" : "cmd.exe")
            {
                UseShellExecute = true,
                WorkingDirectory = cwd
            };

            if (useWindowsTerminal)
            {
                start.ArgumentList.Add("-d");
                start.ArgumentList.Add(cwd);
                start.ArgumentList.Add("cmd.exe");
            }

            start.ArgumentList.Add("/k");
            start.ArgumentList.Add(exe);

            if (extraArgs is not null)
            {
                foreach (var arg in extraArgs) start.ArgumentList.Add(arg);
            }

            return start;
        }

        // The Windows launch for a new chat specifically: the general builder
        // with no extra arguments, since there is no verb and no session id —
        // just the CLI, in the chosen folder.
        //
        // configDir null reproduces the general builder's output byte for
        // byte. Set, the start info additionally switches to
        // UseShellExecute = false and carries the CLI's own account variable
        // (NewChatAccountHome — CLAUDE_CONFIG_DIR, CODEX_HOME or GROK_HOME) on
        // Environment — true, wt.exe/cmd.exe's shared default, launches
        // through ShellExecuteEx and ignores ProcessStartInfo.Environment
        // entirely, so the variable would silently never leave this process.
        //
        // **Measured on the `windows` box (192.168.1.24), 26 Sep 2026,
        // confirmed rather than assumed:** a scheduled task run in the
        // logged-on interactive session (console, session 2 — an SSH session
        // lands in session 0 and can start no visible window, so this could
        // not be read directly off an SSH-launched process) set
        // CLAUDE_CONFIG_DIR on its own environment and launched `wt.exe -d
        // ... cmd.exe /c "echo %CLAUDE_CONFIG_DIR%> file"`. The file showed
        // the real value, twice, with two different values across two runs —
        // and a WindowsTerminal.exe/OpenConsole.exe pair was already running
        // (since 23 Sep, well before this test), so the result covers the
        // case that actually worried this ticket: wt.exe's single-instance
        // model, where a new invocation hands its request to an
        // already-running "monarch" process over IPC rather than becoming the
        // tab's parent itself. CLAUDE_CONFIG_DIR was not set as a User or
        // Machine environment variable, which rules out the file simply
        // showing an ambient value that had nothing to do with this
        // mechanism. wt.exe does carry the launching process's environment
        // through to a new tab, monarch delegation included, so a profiled
        // launch keeps exactly the same wt-first-then-cmd.exe preference an
        // unprofiled one already has — no separate routing decision was
        // needed here after all.
        internal static ProcessStartInfo? WindowsProcessStartInfo(
            NewChatCli cli, string? binaryPath, string cwd, bool useWindowsTerminal, string? configDir = null)
        {
            var start = GeneralWindowsStartInfo(binaryPath, cwd, extraArgs: null, useWindowsTerminal);

            if (start is not null && configDir is not null)
            {
                start.UseShellExecute = false;
                start.Environment[NewChatAccountHome.For(cli).EnvVar] = configDir;
            }

            return start;
        }
    }
}
