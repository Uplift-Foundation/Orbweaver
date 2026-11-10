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

        // tmux: the capture and the key as one command list. ";" as its own
        // argument is tmux's separator, so no shell is involved.
        internal static string[] TmuxCaptureThenKey(string pane, string key) =>
            ["capture-pane", "-p", "-t", pane, ";", "send-keys", "-t", pane, key];

        // The bytes a key is: a single ASCII digit as itself, Enter as CR (not
        // LF, which Claude Code and Codex read as "insert a newline"; see
        // TerminalFocuser.KittySend), and — CB-323, for a question form's
        // "Type something." row and a multi-select's commit — Up, Down and
        // Right as the VT cursor sequences a terminal sends for those keys.
        // Anything else is not a key this presses.
        internal static string? Bytes(string key) => key switch
        {
            "Enter" => "\r",
            "Up" => ArrowUp,
            "Down" => ArrowDown,
            "Right" => ArrowRight,
            _ => key.Length == 1 && char.IsAsciiDigit(key[0]) ? key : null,
        };

        internal const string ArrowUp = "\u001b[A", ArrowDown = "\u001b[B", ArrowRight = "\u001b[C";

        // The Windows virtual-key code for an arrow's sequence, or 0. A console
        // reads an arrow as one key event carrying VK_UP / VK_DOWN / VK_RIGHT,
        // not as the three characters of its VT spelling.
        internal static ushort ArrowVirtualKey(string bytes) => bytes switch
        {
            ArrowUp => 0x26,
            ArrowDown => 0x28,
            ArrowRight => 0x27,
            _ => 0,
        };

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
            if (ArrowVirtualKey(bytes) is var vk and > 0)
                return [ArrowRecord(vk, 1), ArrowRecord(vk, 0)];

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

        private static TerminalFocuser.NativeMethods.InputRecord ArrowRecord(ushort vk, int down) => new()
        {
            EventType = TerminalFocuser.NativeMethods.KeyEvent,
            KeyDown = down,
            RepeatCount = 1,
            VirtualKeyCode = vk,
            Char = '\0',
        };
    }
}
