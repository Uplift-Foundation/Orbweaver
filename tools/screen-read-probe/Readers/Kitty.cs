namespace Orbweaver.ScreenReadProbe;

// kitty: remote control is off unless launched with
//   -o allow_remote_control=yes --listen-on unix:/tmp/kitty-cb313
// (a `kitten` binary on PATH proves nothing). Target is a window id.
internal static class Kitty
{
    internal static string Read(string windowId) =>
        Proc.Run("kitten", "@", "--to", Environment.GetEnvironmentVariable("KITTY_LISTEN_ON") ?? "unix:/tmp/kitty-cb313",
            "get-text", "--match", "id:" + windowId, "--extent", "screen");
}
