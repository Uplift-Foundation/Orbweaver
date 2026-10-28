using System;
using Avalonia;

namespace Orbweaver
{
    // CB-268 M4: a dragged orb stretches up to about 10% along its direction of
    // travel, scaled by how fast it is going, and relaxes back when it stops.
    //
    // Pure and driven by timestamps the caller passes in, so a test steps it
    // with a fake clock. What it produces is a matrix to put on a render
    // transform: it moves pixels only, and never anything about the window.
    //
    // The stretch is a spring rather than a direct function of speed, for two
    // reasons. A pointer's velocity is noisy frame to frame, and a spring (with
    // the observed speed as its target) smooths that without lagging a visible
    // amount; and when the pointer stops the spring is what makes the orb settle
    // back with a small wobble instead of snapping flat. Perpendicular to the
    // stretch the orb narrows by the reciprocal, so it reads as squashing a
    // blob rather than scaling it.
    internal sealed class DragSquash
    {
        /// <summary>The most it stretches along the direction of travel (10%).</summary>
        internal const double MaxStretch = 0.10;

        /// <summary>The speed, in DIP per second, at which it reaches <see cref="MaxStretch"/>. A brisk drag is 1000 to 2000.</summary>
        internal const double FullSpeed = 1200;

        // A pointer that has not moved for this long is at rest, however fast the
        // last move was: a drag that stops dead sends no more events, so the last
        // velocity would otherwise stay "current" for ever.
        internal const long StaleMilliseconds = 60;

        private const double Zeta = 0.55;
        private static readonly SpringParams Params = new(Omega: 32, Zeta: Zeta, RestDelta: 0.0005, RestVelocity: 0.005);

        // The spring overshoots its target (Zeta 0.55 by about 12%), so the
        // target is scaled down until the PEAK is MaxStretch: "up to 10%" is
        // meant literally.
        private static readonly double TargetScale = 1 / (1 + Spring.Overshoot(Zeta));

        private SpringState _stretch;
        private double _lastX, _lastY;
        private long _lastObservedAt;
        private long _lastStepAt;
        private bool _hasObserved;
        private bool _active;
        private double _speed;
        private double _dirX = 1, _dirY;

        internal bool AtRest => _stretch.Value == 0 && _stretch.Velocity == 0 && !_active;

        /// <summary>The current stretch as a fraction (0.10 is 10%).</summary>
        internal double Stretch => _stretch.Value;

        /// <summary>
        /// Records where the pointer is, in DIP, at <paramref name="nowMs"/>. The
        /// first call only remembers the position; speed needs two.
        /// </summary>
        internal void Observe(double x, double y, long nowMs)
        {
            // A pointer sample that is not a number is ignored: the last good
            // one still stands and nothing non-finite reaches the matrix.
            if (!Finite.All(x, y)) return;

            if (_hasObserved && nowMs > _lastObservedAt)
            {
                var dt = (nowMs - _lastObservedAt) / 1000.0;
                var vx = (x - _lastX) / dt;
                var vy = (y - _lastY) / dt;
                var speed = Math.Sqrt(vx * vx + vy * vy);

                if (speed > 1)
                {
                    _dirX = vx / speed;
                    _dirY = vy / speed;
                }

                _speed = speed;
                _active = true;
            }

            if (!_hasObserved) _lastStepAt = nowMs;

            _lastX = x;
            _lastY = y;
            _lastObservedAt = nowMs;
            _hasObserved = true;
        }

        /// <summary>
        /// Advances to <paramref name="nowMs"/>. Returns the matrix for the
        /// orb's render transform, which is the identity whenever motion is
        /// reduced or the orb is at rest.
        /// </summary>
        internal Matrix Step(long nowMs)
        {
            var dt = Math.Max(0, (nowMs - _lastStepAt) / 1000.0);
            _lastStepAt = nowMs;

            var moving = _active && nowMs - _lastObservedAt <= StaleMilliseconds;
            var target = moving && MotionPolicy.AllowsSquash
                ? Math.Min(1, _speed / FullSpeed) * MaxStretch * TargetScale
                : 0;

            if (!moving) _active = false;

            _stretch = Spring.Step(_stretch, target, dt, Params);

            return Current();
        }

        /// <summary>The drag is over: the observed speed no longer counts, and the stretch relaxes.</summary>
        internal void Release()
        {
            _active = false;
            _hasObserved = false;
            _speed = 0;
        }

        /// <summary>Straight back to rest, with no wobble.</summary>
        internal void Reset()
        {
            _stretch = default;
            Release();
        }

        internal Matrix Current()
        {
            // Clamped as well: a spring driven by a changing speed can overshoot
            // a little more than one started from rest, and the stretch is
            // never to exceed MaxStretch in either direction.
            var s = Math.Clamp(_stretch.Value, -MaxStretch, MaxStretch);
            if (s == 0) return Matrix.Identity;

            // Scale by (1+s) along the direction of travel and 1/(1+s) across it:
            // M = I + (a - 1) d d^T + (b - 1) n n^T, with d the unit direction and
            // n its normal, written out as the 2x2 it is.
            var a = 1 + s;
            var b = 1 / (1 + s);
            var dx = _dirX;
            var dy = _dirY;
            var m11 = a * dx * dx + b * dy * dy;
            var m12 = (a - b) * dx * dy;
            var m22 = a * dy * dy + b * dx * dx;

            return new Matrix(m11, m12, m12, m22, 0, 0);
        }
    }
}
