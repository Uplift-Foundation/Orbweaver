namespace Orbweaver.ScreenReadProbe;

// kitty: remote control is off unless launched with
//   -o allow_remote_control=yes --listen-on unix:/tmp/kitty-cb313
// (a `kitten` binary on PATH proves nothing). The arguments are the app's own
// (ScreenReaderCommands.Kitty); the probe adds --to for its private socket.
internal static class Kitty
{
    internal static string Read(string windowId)
    {
        var to = Environment.GetEnvironmentVariable("KITTY_LISTEN_ON") ?? "unix:/tmp/kitty-cb313";
        var a = ScreenReaderCommands.Kitty(windowId);
        return Proc.Run("kitten", a.Take(1).Concat(new[] { "--to", to }).Concat(a.Skip(1)).ToArray());
    }
}
