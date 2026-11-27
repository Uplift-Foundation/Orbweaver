using System.Runtime.CompilerServices;

namespace Orbweaver.Tests
{
    // CB-373: the real capture of a single-select AskUserQuestion dialog in a
    // detached tmux session on the Mac mini (Captures/cb373), loaded from disk
    // like Cb407Captures. The files are made by the machines runner, never typed.
    internal static class Cb373Captures
    {
        private static readonly string Dir = Path.Combine(
            Path.GetDirectoryName(Where())!, "Captures", "cb373");

        private static string Where([CallerFilePath] string path = "") => path;

        internal static string Screen(string name) => File.ReadAllText(Path.Combine(Dir, name + ".txt"));

        internal static string PathOf(string file) => Path.Combine(Dir, file);
    }
}
