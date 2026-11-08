namespace Orbweaver.ScreenReadProbe;

// The app's own route (TerminalTyping / chat session): capture-pane -p, no -e,
// so no escapes. Target is a pane id such as %12.
internal static class Tmux
{
    // PROBE_TMUX_SOCKET names a private server (tmux -L), so a probe session never
    // shares a server with anyone else's panes.
    internal static string Read(string pane)
    {
        var sock = Environment.GetEnvironmentVariable("PROBE_TMUX_SOCKET");
        return sock is null
            ? Proc.Run("tmux", "capture-pane", "-p", "-t", pane)
            : Proc.Run("tmux", "-L", sock, "capture-pane", "-p", "-t", pane);
    }
}
