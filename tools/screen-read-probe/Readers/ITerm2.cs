namespace Orbweaver.ScreenReadProbe;

// iTerm2: `contents` of a session is "the currently visible contents of the
// session" (its sdef). Addressed by the session's unique id, as TerminalTyping does.
internal static class ITerm2
{
    internal static string Read(string sessionId)
    {
        // Indexed access for the reason given in TerminalApp.cs.
        var script = $@"tell application ""iTerm2""
  repeat with wi from 1 to (count of windows)
    repeat with ti from 1 to (count of tabs of window wi)
      repeat with si from 1 to (count of sessions of tab ti of window wi)
        set sn to session si of tab ti of window wi
        if (unique id of sn) is ""{sessionId}"" then return (contents of sn)
      end repeat
    end repeat
  end repeat
  error ""no session {sessionId}""
end tell";
        return Proc.Run("/usr/bin/osascript", "-e", script);
    }
}
