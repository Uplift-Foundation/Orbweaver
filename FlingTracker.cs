using System;
using System.Collections.Generic;

namespace Orbweaver
{
    // CB-283 M5: how fast a drag was going when it was let go, so the orbs it
    // dropped carry that momentum into their settle instead of starting it dead.
    //
    // Measured across the last WindowMs of travel rather than from the last two
    // pointer samples. One pair of samples a millisecond apart, which a
    // high-poll mouse produces, reads as tens of thousands of DIP per second.
    // A window smooths that the way a hand actually throws. SpringGlide caps
    // whatever comes out anyway, but a cap that clips every release would make
    // every fling the same.
    //
    // A pointer that stopped before it was released threw nothing. A drag that
    // halts sends no more moves, so without the stale rule the last speed would
    // still count however long the orb was held still.
    //
    // Pure and driven by timestamps the caller passes, so a test steps it with
    // a fake clock. Units are whatever the caller observes in; OrbDragSession
    // observes DIP.
    internal sealed class FlingTracker
    {
        /// <summary>The stretch of travel the velocity is measured over.</summary>
        internal const long WindowMs = 80;

        /// <summary>A pointer that has not moved for longer than this before the release threw nothing.</summary>
        internal const long StaleMs = DragSquash.StaleMilliseconds;

        private readonly List<(double X, double Y, long T)> _samples = new();

        /// <summary>
        /// Records the pointer at (<paramref name="x"/>, <paramref name="y"/>)
        /// at <paramref name="nowMs"/>. A non-finite position, or a time
        /// earlier than the last sample, is ignored.
        /// </summary>
        internal void Observe(double x, double y, long nowMs)
        {
            if (!Finite.All(x, y)) return;
            if (_samples.Count > 0 && nowMs < _samples[^1].T) return;

            _samples.Add((x, y, nowMs));

            // Keep one sample at or before the window's start as the base, so a
            // steady drag sampled less often than WindowMs still has two.
            while (_samples.Count > 2 && _samples[1].T <= nowMs - WindowMs) _samples.RemoveAt(0);
        }

        /// <summary>
        /// The velocity, per second, at a release at <paramref name="nowMs"/>:
        /// the travel across the window, or zero when there is not enough of it
        /// or the pointer had stopped.
        /// </summary>
        internal (double X, double Y) VelocityAt(long nowMs)
        {
            if (_samples.Count < 2) return (0, 0);

            var last = _samples[^1];
            if (nowMs - last.T > StaleMs) return (0, 0);

            var first = _samples[0];
            var dt = (last.T - first.T) / 1000.0;
            if (dt <= 0) return (0, 0);

            return ((last.X - first.X) / dt, (last.Y - first.Y) / dt);
        }
    }
}
