namespace Orbweaver.ScreenReadProbe;

// WezTerm: the app's own command (ScreenReaderCommands.WezTerm), run here.
// WEZTERM_UNIX_SOCKET selects the mux when set.
internal static class WezTerm
{
    internal static string Read(string pane) => Proc.Run("wezterm", ScreenReaderCommands.WezTerm(pane));
}
