using System.Text;

namespace Orbweaver
{
    // What each hook-installer run actually did (CB-258).
    //
    // HookInstaller used to throw away the exit code, both output streams and
    // every exception, on the argument that if wiring failed "the next scan
    // simply produces no orb". That is true, and it is the whole problem: a
    // profile added in Settings that was never wired looked exactly like one
    // that was, and the one run that could have said why had already finished
    // and kept nothing.
    //
    // So this keeps it. Its own file in CrashLog's directory rather than
    // crash.log, for the reason PersonaLog gives — crash.log's own comment says
    // it only ever holds crashes. Unlike PersonaLog there is no dedupe: every
    // run is an event worth a block, and the second failure of the same
    // installer ten minutes later is exactly what somebody reading this wants
    // to see. Like both it never throws, since a log that can take the Settings
    // window down is worse than no log.
    internal static class HookInstallerLog
    {
        internal static string Path_ => System.IO.Path.Combine(CrashLog.Directory, "hook-installer.log");

        // Rotated rather than capped-and-silent. A cap that stops writing hides
        // the newest failure, which is the one being looked for; one previous
        // generation is plenty for a file that gains a few lines per Settings
        // edit.
        internal const long MaxBytes = 256 * 1024;

        private static readonly object Gate = new();

        internal static void Record(string label, HookInstallResult result) =>
            Write(DateTimeOffset.Now, label, result);

        internal static void Write(DateTimeOffset when, string label, HookInstallResult result)
        {
            lock (Gate)
            {
                try
                {
                    var directory = CrashLog.Directory;
                    System.IO.Directory.CreateDirectory(directory);

                    var path = System.IO.Path.Combine(directory, "hook-installer.log");
                    var file = new FileInfo(path);
                    if (file.Exists && file.Length >= MaxBytes)
                    {
                        File.Move(path, path + ".1", overwrite: true);
                    }

                    File.AppendAllText(path, Format(when, label, result), Encoding.UTF8);
                }
                catch
                {
                    // A full disk or a read-only home. The run's outcome still
                    // reaches the Settings card; only the long form is lost.
                }
            }
        }

        // A header line that can be scanned for, then the streams indented under
        // it so a multi-line installer transcript cannot be mistaken for the
        // start of the next entry.
        internal static string Format(DateTimeOffset when, string label, HookInstallResult result)
        {
            var text = new StringBuilder();
            text.Append(when.ToString("yyyy-MM-dd HH:mm:ss zzz"))
                .Append("  ").Append(label)
                .Append("  ").Append(result.Outcome)
                .Append(result.ExitCode is int code ? $"  exit {code}" : "")
                .Append(result.OutputMayBeCutShort ? "  (output may be cut short: a descendant still held the pipe)" : "")
                .AppendLine();

            Indent(text, "stdout", result.Output);
            Indent(text, "stderr", result.Error);
            return text.ToString();
        }

        private static void Indent(StringBuilder text, string stream, string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return;

            text.Append("    ").Append(stream).AppendLine(":");
            foreach (var line in content.TrimEnd().Split('\n'))
            {
                text.Append("      ").AppendLine(line.TrimEnd('\r'));
            }
        }
    }
}
