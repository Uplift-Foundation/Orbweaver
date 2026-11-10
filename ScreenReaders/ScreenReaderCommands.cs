namespace Orbweaver
{
    // The exact commands each screen reader runs, with no dependency on the rest
    // of the app, so tools/screen-read-probe compiles this one file and runs the
    // SAME scripts and arguments the app does (CB-313 wrote them as a probe;
    // CB-315 moved them here, and the probe now references them: one
    // implementation, not two that drift).
    internal static class ScreenReaderCommands
    {
        // `osascript -e <line> -e <line> … -- <wanted> <body>`: arguments go through
        // `on run argv` and never into the script's source (see TerminalFocuser).
        internal static string[] Osascript(string[] script, string wanted, string body)
        {
            var args = new List<string>();
            foreach (var line in script)
            {
                args.Add("-e");
                args.Add(line);
            }

            args.Add("--");
            args.Add(wanted);
            args.Add(body);
            return args.ToArray();
        }

        // iTerm2: indexed access, because `contents of <loop variable>` returns the
        // session object, not its text (CB-313).
        internal static readonly string[] ITerm2Script =
        {
            "on run argv",
            "set wanted to item 1 of argv",
            "tell application \"iTerm2\"",
            "repeat with wi from 1 to (count of windows)",
            "try",
            "repeat with ti from 1 to (count of tabs of window wi)",
            "repeat with si from 1 to (count of sessions of tab ti of window wi)",
            "if ((id of session si of tab ti of window wi) as string) is wanted then return (contents of session si of tab ti of window wi)",
            "end repeat",
            "end repeat",
            "end try",
            "end repeat",
            "end tell",
            "return \"\"",
            "end run",
        };

        // Both scripts guard each window with `try`: a window that is mid-close or
        // otherwise unaddressable raises "AppleEvent handler failed" (-10000) when
        // asked anything at all, even how many tabs it has, and without the guard ONE such window made every read
        // fail, whichever tab was wanted (seen live on Terminal.app with four of
        // nine windows in that state, CB-315).
        //
        // Terminal.app: `contents` written by name returns the TAB (it collides with
        // AppleScript's own dereference); the property goes by its four-character
        // code «property pcnt», on the tab expression in place (CB-313, finding 5).
        internal static readonly string[] TerminalAppScript =
        {
            "on run argv",
            "set wanted to item 1 of argv",
            "tell application \"Terminal\"",
            "repeat with wi from 1 to (count of windows)",
            "try",
            "repeat with ti from 1 to (count of tabs of window wi)",
            "if ((tty of tab ti of window wi) as string) is wanted then return («property pcnt» of tab ti of window wi)",
            "end repeat",
            "end try",
            "end repeat",
            "end tell",
            "return \"\"",
            "end run",
        };

        internal static string[] ITerm2(string sessionId) => Osascript(ITerm2Script, sessionId, "");

        internal static string[] TerminalApp(string devicePath) => Osascript(TerminalAppScript, devicePath, "");

        // `wezterm cli get-text --pane-id <n>`: the visible viewport, no escapes.
        internal static string[] WezTerm(string paneId) => new[] { "cli", "get-text", "--pane-id", paneId };

        // `kitty @ get-text --match id:<n> --extent screen`: the visible screen, not the scrollback.
        internal static string[] Kitty(string windowId) =>
            new[] { "@", "get-text", "--match", "id:" + windowId, "--extent", "screen" };
    }
}
