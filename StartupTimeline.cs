using System;
using System.Diagnostics;

namespace Orbweaver
{
    // CB-424: orbs took 7 to 20 s to appear on the Windows PC and the slow-phase
    // log (SlowPhase) named only the phases over a second, which left the launch's
    // own gaps unexplained. This writes one line per step from process start to
    // the first orb window shown, so a launch can be read end to end.
    //
    // Limited to startup on purpose: it stops after the first scan has applied
    // and the first orb has been shown (or after MaxLines), so a running app
    // writes nothing more. Lines hold step names and millisecond offsets, never
    // a path, a title or a command line.
    internal sealed class StartupTimeline
    {
        internal const int MaxLines = 60;

        private readonly Func<double> _sinceProcessStart;
        private readonly Action<string> _sink;
        private readonly object _gate = new();
        private int _lines;
        private bool _applied;
        private bool _orbShown;

        internal StartupTimeline(Func<double> sinceProcessStart, Action<string> sink)
        {
            _sinceProcessStart = sinceProcessStart;
            _sink = sink;
        }

        internal static StartupTimeline Default { get; } = new(
            () =>
            {
                try { return (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds; }
                catch { return 0; }
            },
            DiagnosticsLog.Write);

        internal bool Finished
        {
            get { lock (_gate) return _lines >= MaxLines || (_applied && _orbShown); }
        }

        // Thread-safe: the steps come from the UI thread and the scan's worker.
        internal void Mark(string step)
        {
            lock (_gate)
            {
                if (_lines >= MaxLines || (_applied && _orbShown)) return;
                _lines++;
                _sink($"startup: +{(long)_sinceProcessStart()} ms {step}");
            }
        }

        // The first apply finished. The timeline ends once an orb has also shown.
        internal void FirstApplyDone()
        {
            Mark("first apply done");
            lock (_gate) _applied = true;
        }

        internal void OrbShown(int orbsSoFar)
        {
            Mark($"orb window shown (#{orbsSoFar})");
            lock (_gate) _orbShown = true;
        }
    }
}
