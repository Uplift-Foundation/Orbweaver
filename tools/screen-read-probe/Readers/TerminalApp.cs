namespace Orbweaver.ScreenReadProbe;

// Terminal.app, addressed by tty. Runs the app's own script and arguments
// (ScreenReaderCommands.TerminalApp): `contents` by name returns the tab, so the
// script uses «property pcnt» in place. Needs Automation consent.
internal static class TerminalApp
{
    internal static string Read(string tty) =>
        Proc.Run("/usr/bin/osascript", ScreenReaderCommands.TerminalApp(tty));
}
