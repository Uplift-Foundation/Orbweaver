using System;
using System.Diagnostics;

namespace Orbweaver
{
    // The instrument this app has been missing every time orbs "froze": a scan
    // pass runs its disk read, each subprocess probe and its UI apply one after
    // another, nothing draws until the slowest has finished, and a tick that lands
    // mid-pass is skipped. So one slow source delays every orb, and until now
    // nothing said which. A phase that takes about a second or more writes one
    // line, and a healthy pass writes nothing, so it costs nothing in the log.
    //
    // The line names the phase, how long it took and how many sessions or pids it
    // covered. Never a path, a title or a command line: those are somebody's work.
    internal static class SlowPhase
    {
        internal static readonly TimeSpan DefaultThreshold = TimeSpan.FromSeconds(1);

        // Where a line goes by default: diagnostics.log beside the crash log, on
        // every platform. Not stderr: the Windows build has no console, so a line
        // written there was lost. Only reached for a slow phase.
        internal static readonly Action<string> DefaultSink = DiagnosticsLog.Write;

        internal static Func<TimeSpan> StopwatchClock()
        {
            var sw = Stopwatch.StartNew();
            return () => sw.Elapsed;
        }

        // The whole decision, pure: null (log nothing) under the threshold.
        internal static string? Line(string phase, TimeSpan elapsed, int count, TimeSpan threshold) =>
            elapsed < threshold
                ? null
                : $"slow scan phase: {phase} took {(long)elapsed.TotalMilliseconds} ms"
                  + (count < 0 ? "" : $" ({count} {(count == 1 ? "session" : "sessions")})");

        // For a duration the caller measured itself.
        internal static void Report(string phase, int count, TimeSpan elapsed, TimeSpan threshold, Action<string> sink)
        {
            var line = Line(phase, elapsed, count, threshold);
            if (line != null) sink(line);
        }

        internal static T Measure<T>(string phase, Func<T, int> countOf, Func<T> work,
            Func<TimeSpan> now, TimeSpan threshold, Action<string> sink)
        {
            var started = now();
            var result = work();
            var line = Line(phase, now() - started, countOf(result), threshold);
            if (line != null) sink(line);
            return result;
        }

        // For a phase whose size is known before it runs. A phase that throws
        // still reports how long it took: a slow failure is the interesting kind.
        internal static T Measure<T>(string phase, int count, Func<T> work,
            Func<TimeSpan> now, TimeSpan threshold, Action<string> sink)
        {
            var started = now();
            try
            {
                return work();
            }
            finally
            {
                var line = Line(phase, now() - started, count, threshold);
                if (line != null) sink(line);
            }
        }
    }
}
