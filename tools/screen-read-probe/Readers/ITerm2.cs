namespace Orbweaver.ScreenReadProbe;

// iTerm2, addressed by session id. Runs the app's own script and arguments
// (ScreenReaderCommands.ITerm2): indexed access, not a loop variable.
internal static class ITerm2
{
    internal static string Read(string sessionId) =>
        Proc.Run("/usr/bin/osascript", ScreenReaderCommands.ITerm2(sessionId));
}
