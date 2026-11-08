namespace Orbweaver.ScreenReadProbe;

// WezTerm: `cli get-text` returns the visible viewport by default; no --escapes.
// Target is a pane id. WEZTERM_UNIX_SOCKET selects the mux when set.
internal static class WezTerm
{
    internal static string Read(string pane) => Proc.Run("wezterm", "cli", "get-text", "--pane-id", pane);
}
