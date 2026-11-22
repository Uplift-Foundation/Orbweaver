using System.Runtime.CompilerServices;
using Orbweaver;

namespace Orbweaver.Tests
{
    // CB-412: real tmux captures of `/login` (Captures/cb412), loaded from disk like
    // Cb407Captures, and the checks the console suite runs over them: the reader on
    // every screen, and the row `/login` leaves in the transcript.
    internal static class Cb412Captures
    {
        private static readonly string Dir = Path.Combine(
            Path.GetDirectoryName(Where())!, "Captures", "cb412");

        private static string Where([CallerFilePath] string path = "") => path;

        internal static string Screen(string name) => File.ReadAllText(Path.Combine(Dir, name + ".txt"));

        internal static string PathOf(string file) => Path.Combine(Dir, file);
    }
}
