using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Orbweaver
{
    // What the mirror actually did with a frame, written down.
    //
    // **This exists because six separate faults in this subsystem have produced
    // the same sentence on screen.** "The other machine didn't answer in time"
    // has meant, at different times: a deadline shorter than one relay turn
    // (CB-54), a queue the waiting itself built (CB-55), a poll starving the
    // fetch (CB-56), an idle timer retiring the relay under an open panel
    // (CB-57), a relay that stopped with nothing to restart it (CB-60), and a
    // far machine receiving well-formed frames and answering none of them.
    //
    // Every one of those was diagnosed by reading relay transcripts and
    // inferring, because nothing on the serving side ever recorded a decision.
    // That is a slow way to find a fault and a very easy way to be confident and
    // wrong: this session alone killed five plausible theories that way, each of
    // which would have shipped as a fix.
    //
    // Deliberately cheap and deliberately dull. One line per decision, no
    // levels, no categories, no formatting — a frame arrived, this is what
    // happened to it. The value is entirely in there being a record at all.
    internal static class MirrorLog
    {
        // Off unless asked for, in one of two ways.
        //
        // The env var is for a terminal. The marker file is for a machine where
        // there is no terminal to set one in: the headless mini runs Buddy from
        // a launchd agent, and adding an environment variable to a plist to
        // diagnose a fault is more ceremony than the fault deserves. Touch the
        // file, restart, read the log.
        private static readonly Lazy<string?> Path = new(Resolve);

        private static readonly object Gate = new();

        // Rotated at this size: the full file becomes mirror-log.txt.1 (replacing
        // any older one) and a fresh one starts. This used to be a ceiling that
        // simply stopped writing, and it stopped on 2 Nov on both Macs — so the
        // "peer-listen-failed" line of the CB-306 regression was never recorded
        // and the fault looked like nothing at all. Dropping the oldest history
        // is the right way for a diagnostic to be full; going deaf is not.
        private const long MaxBytes = 1024 * 1024;

        internal static bool On => Path.Value is not null;

        [ExcludeFromCodeCoverage]
        private static string? Resolve()
        {
            try
            {
                var dir = OrbweaverSettings.Directory;
                if (string.IsNullOrEmpty(dir)) return null;

                var asked = !string.IsNullOrWhiteSpace(BrandEnv.Get(BrandEnv.MirrorLog))
                            || File.Exists(System.IO.Path.Combine(dir, "mirror-log"));

                if (!asked) return null;

                return System.IO.Path.Combine(dir, "mirror-log.txt");
            }
            catch
            {
                return null;
            }
        }

        // The line, given the parts. Pure so the shape can be asserted without
        // touching a disk — and the shape is the whole contract, since the only
        // consumer is a person reading it.
        internal static string Line(DateTime whenUtc, string what, string detail) =>
            $"{whenUtc:HH:mm:ss.fff} {what} {detail}".TrimEnd();

        // What each repeating line last said, and how many times it has said it.
        private static readonly Dictionary<string, (string Detail, int Since)> Repeats = new();

        // How many identical occurrences to swallow before saying it again.
        //
        // Not silence: a fault that is still happening an hour later should still
        // be visible, and a log that goes quiet reads as a fault that stopped.
        // Thirty ten-second ticks is five minutes, which is often enough to see
        // it is ongoing and rare enough not to bury anything.
        internal const int RepeatEvery = 30;

        // Far more than any real machine has peers or sessions, and small enough
        // that forgetting the lot costs nothing.
        internal const int MostRepeatsRemembered = 500;

        // Whether a repeating line should be written this time.
        //
        // **The mini made this necessary within a minute of being deployed.** It
        // cannot dial without a Local Network grant it has no way to obtain, so
        // it logged the identical connect failure every ten seconds — about
        // 8,600 lines a day, into a log with a 1MB ceiling. The failure was real
        // and worth knowing once; what it actually did was evict the history
        // that made the log worth reading, which is the opposite of what the
        // log is for.
        //
        // Pure, and separate from the writing, so the rule is a test rather than
        // a counter buried in an IO path. This repository has fixed this exact
        // shape before — "Say a failure once, not once per attempt".
        internal static bool WorthSaying(string? lastDetail, int since, string detail) =>
            lastDetail is null || lastDetail != detail || since >= RepeatEvery;

        // Says something that may repeat forever, without letting it fill the
        // log. The first occurrence always lands; identical ones after it are
        // counted and restated every RepeatEvery.
        internal static void SayOnce(string what, string detail = "")
        {
            bool worth;
            int since;

            lock (Gate)
            {
                // Keys are per machine and per session name, so a long-running
                // Buddy accumulates one entry for every session it has ever been
                // asked about. Small, and unbounded — which in a process that
                // stays up for weeks is a leak rather than a rounding error.
                // Cleared wholesale rather than evicted cleverly: the cost of
                // being wrong is one extra line in a log.
                if (Repeats.Count > MostRepeatsRemembered) Repeats.Clear();

                Repeats.TryGetValue(what, out var last);

                worth = WorthSaying(last.Detail, last.Since, detail);
                since = last.Since;

                Repeats[what] = worth ? (detail, 0) : (detail, last.Since + 1);
            }

            if (!worth) return;

            Say(what, since >= RepeatEvery ? $"{detail} (still, x{since})" : detail);
        }

        // Keeps exactly one previous generation, so the disk cost is bounded at
        // twice MaxBytes. Internal with the limit as an argument so a test can
        // drive it without writing a megabyte.
        internal static void RotateIfFull(string path, long maxBytes = MaxBytes)
        {
            if (new FileInfo(path) is not { Exists: true } info || info.Length <= maxBytes) return;

            File.Move(path, path + ".1", overwrite: true);
        }

        [ExcludeFromCodeCoverage]
        internal static void Say(string what, string detail = "")
        {
            var path = Path.Value;
            if (path is null) return;

            try
            {
                lock (Gate)
                {
                    RotateIfFull(path);

                    File.AppendAllText(
                        path, Line(DateTime.UtcNow, what, detail) + "\n", Encoding.UTF8);
                }
            }
            catch
            {
                // A diagnostic that can break the thing it is diagnosing is
                // worse than no diagnostic.
            }
        }
    }
}
