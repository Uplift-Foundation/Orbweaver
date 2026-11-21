using System.Runtime.CompilerServices;

namespace Orbweaver.Tests
{
    // CB-407: real tmux captures of an AskUserQuestion form in a pane 12 and 24 columns wide
    // (Captures/cb407), with the one tool_use row each form's transcript holds. The screens are
    // byte-for-byte tmux output except that one first name in an option's text became "Morgan"
    // (same length, so every wrap is as captured); the rows are the tool_use alone, with none of
    // the session around it, which carried account ids and local paths. Loaded from disk the way
    // Cb327Captures is, found from this source file's own path.
    internal static class Cb407Captures
    {
        private static readonly string Dir = Path.Combine(
            Path.GetDirectoryName(Where())!, "Captures", "cb407");

        private static string Where([CallerFilePath] string path = "") => path;

        internal static string Screen(string name) => File.ReadAllText(Path.Combine(Dir, name + ".txt"));

        internal static string PathOf(string file) => Path.Combine(Dir, file);
    }
}
