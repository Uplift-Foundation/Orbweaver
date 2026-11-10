using System.Threading.Tasks;

namespace Orbweaver
{
    // Reads the text a session's terminal is showing right now (CB-315).
    //
    // A permission dialog is drawn by the CLI's TUI and never reaches a
    // transcript, so the only place its wording exists is the screen. Before
    // this the app could read a screen from tmux alone; CB-313 measured that
    // the dialog is readable on every terminal the app can type into, and this
    // is the seam that does it, one reader per terminal behind one interface.
    //
    // The contract is deliberately narrow, because a prompt pipeline presses a
    // key on the strength of it:
    //
    //  - ReadAsync returns the visible text of the session's own pane, window
    //    or console, LF-separated, with no escape sequences. Never scrollback.
    //  - It is a FRESH capture on every call. Nothing is cached, because a
    //    stale screen is how a keypress lands on a dialog that is no longer
    //    there.
    //  - It returns null when it could not read: the tool is missing, the
    //    terminal refused, the session has gone. Null is "unknown", and every
    //    caller treats it as "send the person to the terminal".
    //  - It never throws.
    internal interface IScreenReader
    {
        Task<string?> ReadAsync(SessionStatus? status);
    }

    // Picks the reader for a session.
    //
    // By the SAME rule that picks where a keypress goes (TerminalTyping.RouteFor),
    // so a session the app can answer is a session the app can read, and the two
    // can never disagree about which terminal owns it. A route with no reader, or
    // no route at all, is no reader: null, which a caller reads as "Open on desk".
    internal static class ScreenReaders
    {
        internal static IScreenReader? For(SessionStatus? status) =>
            For(TerminalFocuser.RouteFor(status));

        // The dispatch alone, on a route, so every arm is a test.
        internal static IScreenReader? For(TerminalTyping.Route route) => route switch
        {
            TerminalTyping.Route.Tmux => TmuxScreenReader.Instance,
            TerminalTyping.Route.ITerm2 => ITerm2ScreenReader.Instance,
            TerminalTyping.Route.TerminalApp => TerminalAppScreenReader.Instance,
            TerminalTyping.Route.WezTerm => WezTermScreenReader.Instance,
            TerminalTyping.Route.Kitty => KittyScreenReader.Instance,
            TerminalTyping.Route.WindowsConsole => WindowsConsoleScreenReader.Instance,
            _ => null,
        };

        // What every reader does to what it read: one LF between rows, no CRs.
        // A reader that gets nothing back (an empty or whitespace-only screen is
        // still a screen, so only null means failure) passes it through.
        internal static string? Normalise(string? raw) =>
            raw is null ? null : raw.Replace("\r\n", "\n").Replace('\r', '\n');
    }
}
