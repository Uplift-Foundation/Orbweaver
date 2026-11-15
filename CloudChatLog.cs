using System.Text;

namespace Orbweaver
{
    // What a cloud chat panel decided about a turn, written down (CB-285).
    //
    // **This exists because a reply that never reached an open panel left no
    // trace of why.** The live loop could have ended the turn on an idle read
    // that landed before the session woke, or a trusted stream could have said
    // nothing about the send and left nothing polling — two different faults
    // with one symptom, and the app kept no record of which path a real send
    // took. CB-285 was diagnosed by measuring a throwaway session from outside
    // and reading the code; the next fault of this shape should be readable off
    // the user's own machine instead.
    //
    // One line per *decision*, never per tick: a send was accepted and who is
    // watching it, a loop saw the turn start, ignored an idle, ended the turn
    // or gave up. Session id and durations only — **never message text and
    // never a token**, because this file sits in a logs directory a user may
    // attach to a bug report without reading it first.
    //
    // Its own file beside persona.log, for persona.log's reason: crash.log says
    // it only holds crashes, and these lines are ordinary.
    //
    // **Rotated, not capped and not deduplicated** — MirrorLog's choice, and
    // for MirrorLog's reason. These lines legitimately repeat (every send says
    // "sent"), so PersonaLog's said-once set would swallow exactly the record
    // wanted, and a ceiling that stops writing goes deaf on the day it fills,
    // which is the day somebody is reading it. At the cap the file becomes
    // cloud-chat.log.1, replacing any older one, and a fresh one starts — so
    // the disk cost is bounded at twice MaxBytes.
    //
    // Never throws, like CrashLog and PersonaLog: a send that failed to write a
    // diagnostic about itself must still be a send.
    internal static class CloudChatLog
    {
        internal const string FileName = "cloud-chat.log";

        // Large enough for a few thousand turns of history, small enough that
        // nobody notices it. Not measured, and nothing depends on the figure
        // beyond the bound it sets.
        internal const long MaxBytes = 256 * 1024;

        private static readonly object Gate = new();

        internal static string Path_ => System.IO.Path.Combine(CrashLog.Directory, FileName);

        internal static void Record(string message) => Write(DateTimeOffset.Now, message);

        // Split from Record so a test can pin the timestamp and the cap without
        // writing a quarter of a megabyte.
        internal static void Write(DateTimeOffset when, string message, long maxBytes = MaxBytes)
        {
            lock (Gate)
            {
                try
                {
                    // Read once, so the file rotated is the file written.
                    var directory = CrashLog.Directory;
                    System.IO.Directory.CreateDirectory(directory);

                    var path = System.IO.Path.Combine(directory, FileName);
                    if (new FileInfo(path) is { Exists: true } file && file.Length >= maxBytes)
                    {
                        File.Move(path, path + ".1", overwrite: true);
                    }

                    File.AppendAllText(path, PersonaLog.Format(when, message), Encoding.UTF8);
                }
                catch
                {
                    // A full disk, a read-only home, a log directory that is
                    // somebody's file. The panel goes on exactly as it would
                    // have with no log at all.
                }
            }
        }

        // Seconds, whole, for a duration in a line. Whole seconds because every
        // interval the loop runs on is two seconds or more, and a reader asking
        // "how long after the send" wants a number, not a timestamp to subtract.
        internal static string Seconds(TimeSpan span) => ((int)Math.Round(span.TotalSeconds)) + "s";
    }
}
