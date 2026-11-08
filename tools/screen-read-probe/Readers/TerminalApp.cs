namespace Orbweaver.ScreenReadProbe;

// Terminal.app: the sdef says `contents` of a tab is "the currently visible
// contents" and `history` the scrollback; `contents` is the one read here.
// Addressed by tty, the handle TerminalTyping uses. Needs Automation consent.
internal static class TerminalApp
{
    internal static string Read(string tty)
    {
        // `contents` spelled by name is AppleScript's own dereference and returns the
        // tab itself, so the property goes by its four-char code (pcnt, from the sdef) - and on a
        // *variable* holding the tab that still returns the tab, so it is addressed in place.
        // Indexed access, not `repeat with t in tabs`: a loop variable is a
        // reference, and `contents of t` on a reference returns the tab
        // object ("tab 1 of window id N") instead of its text - measured.
        var script = $@"tell application ""Terminal""
  repeat with wi from 1 to (count of windows)
    repeat with ti from 1 to (count of tabs of window wi)
      if (tty of tab ti of window wi) is ""{tty}"" then return («property pcnt» of tab ti of window wi)
    end repeat
  end repeat
  error ""no tab with tty {tty}""
end tell";
        return Proc.Run("/usr/bin/osascript", "-e", script);
    }
}
