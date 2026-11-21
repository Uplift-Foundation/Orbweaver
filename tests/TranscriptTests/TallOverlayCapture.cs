using System.Runtime.CompilerServices;

namespace Orbweaver.Tests
{
    // CB-400: a real tall blocking screen (`/mcp`, 50 rows by 100 columns, a tmux capture-pane of
    // Claude Code) read from disk, for the panel tests and screenshot that need a body taller than
    // the panel. One copy compiled into both suites. The capture's one home path is the only edit
    // (the user name became `user`); every other byte is as captured.
    internal static class TallOverlayCapture
    {
        private static string SourceFile([CallerFilePath] string path = "") => path;

        internal static string Mcp => File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(SourceFile())!, "Captures", "cb400", "other-mcp.txt"));

        // The rows as the reader would hand them over: trailing blanks off, bounded as the wire is.
        internal static IReadOnlyList<string> McpBody() =>
            Mcp.Split('\n').Select(l => l.TrimEnd()).Reverse().SkipWhile(l => l.Length == 0).Reverse().ToList();
    }
}
