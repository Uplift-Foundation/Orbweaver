using System;
using System.IO;
using System.Text;

namespace Orbweaver
{
    // Where this app's own "something was slow or failed" lines go, in a file
    // that exists on every platform. The slow-scan diagnostic and the Paperclip
    // transport's failure lines used to write to stderr, and the Windows build is
    // a WinExe: it has no console, so the line was written into the void (a launch
    // where the local orbs took about eleven seconds left the captured stderr
    // empty). On macOS stderr under launchd is the keep-alive log, which has no
    // rotation. A file beside crash.log and persona.log is where somebody already
    // looks: ~/Library/Logs/Orbweaver on macOS, %LOCALAPPDATA%\Orbweaver\Logs on
    // Windows.
    //
    // It is not telemetry and nothing leaves the machine. It only ever receives
    // lines a caller has decided are worth a line (a phase over a second, a failed
    // request), so it is silent when the app is healthy, and it is bounded: past
    // MaxBytes the file is moved aside to .1, replacing the one before it.
    //
    // Every entry point swallows everything, as CrashLog does: a diagnostic that
    // can break the thing it is diagnosing is worse than none.
    internal static class DiagnosticsLog
    {
        internal const string FileName = "diagnostics.log";
        internal const long MaxBytes = 256 * 1024;

        private static readonly object Gate = new();

        internal static string PathIn(string directory) => System.IO.Path.Combine(directory, FileName);

        internal static string Format(DateTimeOffset when, string line) =>
            when.ToString("yyyy-MM-dd HH:mm:ss.fff zzz") + "  " + line + Environment.NewLine;

        internal static void Write(string line) => Write(DateTimeOffset.Now, line);

        internal static void Write(DateTimeOffset when, string line)
        {
            lock (Gate)
            {
                try
                {
                    var directory = CrashLog.Directory;
                    Directory.CreateDirectory(directory);
                    var path = PathIn(directory);
                    RotateIfFull(path);
                    File.AppendAllText(path, Format(when, line), Encoding.UTF8);
                }
                catch
                {
                    // Nothing written is the worst outcome allowed.
                }
            }
        }

        internal static void RotateIfFull(string path, long maxBytes = MaxBytes)
        {
            if (new FileInfo(path) is not { Exists: true } info || info.Length <= maxBytes) return;
            File.Move(path, path + ".1", overwrite: true);
        }
    }
}
