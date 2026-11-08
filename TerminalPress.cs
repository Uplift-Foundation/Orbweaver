using System.Collections.Generic;

namespace Orbweaver
{
    // CB-315: what a one-key press from a phone sends on each route — the pure
    // half of TerminalFocuser.PressKey / TypeQuietly, kept out of that class
    // because it is excluded from coverage as a whole (it shells out), and the
    // bytes and argv are exactly the part worth pinning. Same split as
    // TerminalScripts.
    internal static class TerminalPress
    {
        // The routes that write into a session without bringing anything
        // forward. Terminal.app is not one: a keystroke there needs
        // Accessibility consent and focus, so its prompts go "Open on desk".
        internal static bool Silent(TerminalTyping.Route route) => route is
            TerminalTyping.Route.Tmux or TerminalTyping.Route.Kitty or TerminalTyping.Route.WezTerm
            or TerminalTyping.Route.ITerm2 or TerminalTyping.Route.WindowsConsole;

        // The bytes a key is: a single ASCII digit as itself, Enter as CR (not
        // LF, which Claude Code and Codex read as "insert a newline"; see
        // TerminalFocuser.KittySend). Anything else is not a key this presses.
        internal static string? Bytes(string key) =>
            key == "Enter" ? "\r" : key.Length == 1 && char.IsAsciiDigit(key[0]) ? key : null;

        // kitty and WezTerm: exactly the bytes on stdin, no paste markers, no
        // trailing Enter.
        internal static (string Stdin, string[] Args) Kitty(SessionStatus status, string bytes) =>
            (bytes, new[] { "@", "send-text", "--match", "id:" + status.TermId, "--stdin" });

        internal static (string Stdin, string[] Args) WezTerm(SessionStatus status, string bytes) =>
            (bytes, new[] { "cli", "send-text", "--pane-id", status.TermId, "--no-paste" });

        // iTerm2: `write text … newline NO` puts exactly the body in; an empty
        // body with the default newline is Enter. The session and the body come
        // in through argv, never into the source.
        internal static readonly string[] ITerm2Script =
        {
            "on run argv",
            "set wanted to item 1 of argv",
            "set body to item 2 of argv",
            "tell application \"iTerm2\"",
            "repeat with w in windows",
            "repeat with t in tabs of w",
            "repeat with s in sessions of t",
            "if ((id of s) as string) is wanted then",
            "if body is \"\" then",
            "tell s to write text \"\"",
            "else",
            "tell s to write text body newline NO",
            "end if",
            "return \"ok\"",
            "end if",
            "end repeat",
            "end repeat",
            "end repeat",
            "end tell",
            "return \"no\"",
            "end run",
        };

        internal static string[] ITerm2Args(SessionStatus status, string body) =>
            ScreenReaderCommands.Osascript(ITerm2Script, status.TermId, body);

        // The Windows console: each character as a key-down/key-up pair, CR as
        // VK_RETURN. Nothing after the last one.
        internal static List<TerminalFocuser.NativeMethods.InputRecord> ConsoleRecords(string bytes)
        {
            var records = new List<TerminalFocuser.NativeMethods.InputRecord>(bytes.Length * 2);
            foreach (var c in bytes)
                for (var down = 1; down >= 0; down--)
                    records.Add(new TerminalFocuser.NativeMethods.InputRecord
                    {
                        EventType = TerminalFocuser.NativeMethods.KeyEvent,
                        KeyDown = down,
                        RepeatCount = 1,
                        VirtualKeyCode = c == '\r' ? (ushort)0x0D : (ushort)0,
                        Char = c,
                    });
            return records;
        }
    }
}
