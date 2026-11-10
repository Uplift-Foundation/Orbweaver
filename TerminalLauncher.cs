using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Orbweaver
{
    // The "open a terminal window and run this in it" mechanism, extracted out
    // of AgentTeamViewer so a second feature that needs it — CB-168's "start a
    // new chat" — does not grow its own copy.
    //
    // AgentTeamViewer needed this to attach an existing session to a terminal;
    // this file is the same four operations with no session-attach vocabulary
    // baked in, so a caller that only wants "run this command, in this
    // directory, in a terminal" doesn't have to go through `claude attach`.
    // AgentTeamViewer itself is repointed at these methods with no behaviour
    // change — see the comment on each one for what moved and why it is safe.
    //
    // Everything here shells out to `ps`, `osascript`, tmux or `wt.exe`/
    // `cmd.exe`, so it is excluded from coverage the same way AgentTeamViewer
    // was: as a class, because a field initializer belongs to the type
    // initializer rather than any method and is reported unhit whenever
    // nothing touches the class at all.
    [ExcludeFromCodeCoverage]
    internal static class TerminalLauncher
    {
        // Whichever terminal is already running, so a new window opens where
        // the user's other terminals are rather than waking a second app.
        // Ordered by specificity: Terminal.app is last because it's the
        // fallback that always exists, not a preference.
        //
        // Moved verbatim from AgentTeamViewer.TerminalApp, which is now a
        // one-line forwarder — the candidate list and the `ps` scan are
        // unchanged, so every caller sees the same terminal it saw before.
        [ExcludeFromCodeCoverage]
        public static string TerminalApp()
        {
            string[] candidates =
            {
                "/Applications/iTerm.app",
                "/Applications/Ghostty.app",
                "/Applications/WezTerm.app"
            };

            if (TryRun("/bin/ps", out var listing, "-eo", "args="))
            {
                foreach (var candidate in candidates)
                {
                    if (listing.Contains(candidate, StringComparison.Ordinal)
                        && System.IO.Directory.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return "/System/Applications/Utilities/Terminal.app";
        }

        // Runs `command` in a brand-new window of `app`, on the directory
        // `cwd` names.
        //
        // Moved verbatim from AgentTeamViewer.LaunchInTerminal — see CB-80 in
        // that file's history for why this goes through
        // TerminalScripts.RunScriptFor's AppleScript "run this" verb for
        // iTerm2 and Terminal.app rather than `open -a <app> <script file>`,
        // and a GUID-named script file for the two terminals with no
        // comparable scripting surface.
        [ExcludeFromCodeCoverage]
        public static bool LaunchInTerminal(string app, string? cwd, string command)
        {
            if (TerminalScripts.RunScriptFor(app, cwd, command) is { } appleScript)
            {
                return TryRun("/usr/bin/osascript", out _, "-e", appleScript);
            }

            try
            {
                System.IO.Directory.CreateDirectory(OrbweaverSettings.Directory);
                var script = Path.Combine(OrbweaverSettings.Directory, Guid.NewGuid() + ".sh");

                File.WriteAllText(script,
                    "#!/bin/sh\n" + TerminalScripts.ShellCommandLine(cwd, command) + "\n");
                File.SetUnixFileMode(script,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

                var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                psi.ArgumentList.Add("-a");
                psi.ArgumentList.Add(app);
                psi.ArgumentList.Add(script);
                Process.Start(psi);

                return true;
            }
            catch
            {
                // Same contract as everything else on this path: failing to
                // open a window is a click that did nothing, never a crash.
                return false;
            }
        }

        // Runs the attach beside the user in tmux — split into the window their
        // most recent client is showing, or a new window in its session when
        // that window cannot be resolved — and hands back its pane.
        //
        // Moved verbatim from AgentTeamViewer.PlaceInTmux, including its
        // reliance on TerminalScripts.PlacementFor/TmuxSplitArgs/
        // TmuxNewWindowArgs and ResolveTmux, both of which stayed put in
        // AgentTeamViewer since they are private helpers this file would
        // otherwise have to duplicate — see the note on ResolveTmux below.
        // Every AgentTeamViewer launch and orb attach comes through here.
        public static string? PlaceInTmux(string command, string cwd) =>
            Place(command, cwd, TerminalScripts.OrbAttachPlacement);

        // Runs a new chat in a tmux window of its own, in the session of the
        // user's most recent client, and hands back its pane — never a split.
        // TerminalScripts.NewChatPlacementFor has the reasoning.
        public static string? PlaceInOwnTmuxWindow(string command, string cwd) =>
            Place(command, cwd, TerminalScripts.NewChatPlacement);

        [ExcludeFromCodeCoverage]
        private static string? Place(
            string command, string cwd, Func<string?, string?, TerminalScripts.AttachPlacement> rule)
        {
            var tmux = ResolveTmux();
            if (tmux is null) return null;

            var session = TerminalScripts.MostRecentClient(AttachedClients(tmux, ""))?.Session;
            var activeWindow = session is null ? null : CurrentWindowOf(tmux, "", session);

            var args = TerminalScripts.TmuxPlacementArgs(rule(session, activeWindow), session, activeWindow, cwd, command);
            if (args is null) return null;

            if (!TryRun(tmux, out var pane, args)) return null;

            var id = pane.Trim();
            return id.StartsWith('%') ? id : null;
        }

        // Where tmux actually is. The app can't count on PATH — launched from
        // Finder it gets the bare system one.
        //
        // Moved verbatim from AgentTeamViewer.ResolveTmux.
        // CB-316: a phone's launch on the Mac — tmux, always, never Terminal.app
        // (refinement decision 2). A new window in the attached client's
        // session, the desk's own New Chat rule; with no attached client, the
        // detached `orbweaver` session. The pane id, or a why for the phone.
        // Excluded: every line runs tmux; the decisions are TerminalScripts'.
        [ExcludeFromCodeCoverage]
        public static (string? Pane, string? Why) PlaceForPhone(string command, string cwd)
        {
            var tmux = ResolveTmux();
            if (tmux is null) return (null, TerminalScripts.NoTmuxWhy);
            if (PlaceInOwnTmuxWindow(command, cwd) is { } pane) return (pane, null);

            var exists = TryRun(tmux, out _, "has-session", "-t", "=" + TerminalScripts.DetachedSession);
            if (!TryRun(tmux, out var id, TerminalScripts.TmuxDetachedArgs(exists, cwd, command)))
                return (null, "tmux could not start a session");
            var trimmed = id.Trim();
            return trimmed.StartsWith('%') ? (trimmed, null) : (null, "tmux did not name the new pane");
        }

        // CB-316: a phone's launch on Windows — its own conhost through
        // cmd.exe /k, never wt.exe (whose single-instance handoff drops the
        // launching process's environment). The started process's id, or null.
        [ExcludeFromCodeCoverage]
        public static int? StartConsole(ProcessStartInfo start)
        {
            try { return Process.Start(start)?.Id; }
            catch { return null; }
        }

        [ExcludeFromCodeCoverage]
        public static string? ResolveTmux()
        {
            string[] candidates =
            {
                "/opt/homebrew/bin/tmux",
                "/usr/local/bin/tmux",
                "/usr/bin/tmux",
                "/opt/local/bin/tmux"
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        [ExcludeFromCodeCoverage]
        private static List<TerminalScripts.TmuxClient> AttachedClients(string tmux, string socket)
        {
            if (!TryRun(tmux, out var listing, TerminalScripts.TmuxArgs(
                    socket, "list-clients", "-F", TerminalScripts.ClientListFormat)))
            {
                return new List<TerminalScripts.TmuxClient>();
            }

            return TerminalScripts.ParseClients(listing);
        }

        [ExcludeFromCodeCoverage]
        private static string? CurrentWindowOf(string tmux, string socket, string session)
        {
            if (string.IsNullOrEmpty(session)) return null;

            return TryRun(tmux, out var found, TerminalScripts.TmuxArgs(
                    socket, "display-message", "-p",
                    "-t", TerminalScripts.PaneTargetForSession(session),
                    "#{session_name}:#{window_index}"))
                ? FirstLine(found)
                : null;
        }

        private static string? FirstLine(string output)
        {
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0) return trimmed;
            }

            return null;
        }

        // Windows launch of a Windows ProcessStartInfo built by
        // NewChatCommand.WindowsProcessStartInfo/
        // AgentTeamViewer.WindowsAttachStartInfo — wt.exe first, cmd.exe if
        // that throws (no Windows Terminal installed). Both callers used to
        // inline this try/wt/catch/cmd shape; this is the one copy of it.
        [ExcludeFromCodeCoverage]
        public static bool StartWindowsProcess(Func<bool, ProcessStartInfo?> startInfoFor)
        {
            try
            {
                if (startInfoFor(true) is not { } wt) return false;
                Process.Start(wt);
                return true;
            }
            catch
            {
                try
                {
                    if (startInfoFor(false) is not { } cmd) return false;
                    Process.Start(cmd);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        [ExcludeFromCodeCoverage]
        private static bool TryRun(string exe, out string stdout, params string[] args)
        {
            stdout = "";

            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var arg in args) psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi);
                if (process is null) return false;

                stdout = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(3000)) return false;

                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
