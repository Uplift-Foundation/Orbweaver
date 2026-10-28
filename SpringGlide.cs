using System;
using Avalonia;

namespace Orbweaver
{
    // CB-268 M5: one orb gliding to a slot. With motion on it is a pair of
    // springs (Spring.Pop, about 8% overshoot) that start with whatever velocity
    // the drag released with, so a flung orb carries its momentum into the
    // settle; with reduce motion on it is the plain eased glide of about 150 ms
    // and no overshoot (M6). Either way it ends exactly on the target, never a
    // pixel short, because the layout slot is where the orb is *supposed* to be.
    //
    // Pure and driven by timestamps, so tests step it with a fake clock.
    internal sealed class SpringGlide
    {
        private readonly PixelPoint _from;
        private readonly PixelPoint _target;
        private readonly long _startMs;
        private readonly bool _reduce;
        private SpringState _x;
        private SpringState _y;
        private long _lastMs;

        internal bool Done { get; private set; }
        internal PixelPoint Target => _target;

        /// <param name="velocity">The orb's velocity when released, in pixels per second.</param>
        internal SpringGlide(
            PixelPoint from, PixelPoint target, (double X, double Y) velocity, long startMs, bool reduceMotion)
        {
            _from = from;
            _target = target;
            _startMs = startMs;
            _lastMs = startMs;
            _reduce = reduceMotion;
            _x = new SpringState(from.X, velocity.X);
            _y = new SpringState(from.Y, velocity.Y);
            Done = from == target && !reduceMotion && velocity == (0, 0);
        }

        /// <summary>Where the orb is at <paramref name="nowMs"/>. Exactly the target once it is done.</summary>
        internal PixelPoint Step(long nowMs)
        {
            if (Done) return _target;

            if (_reduce)
            {
                var elapsed = (nowMs - _startMs) / 1000.0;
                if (elapsed >= MotionPolicy.ReducedGlideSeconds)
                {
                    Done = true;
                    return _target;
                }

                return new PixelPoint(
                    (int)Math.Round(MotionPolicy.ReducedGlide(_from.X, _target.X, elapsed)),
                    (int)Math.Round(MotionPolicy.ReducedGlide(_from.Y, _target.Y, elapsed)));
            }

            var dt = Math.Max(0, (nowMs - _lastMs) / 1000.0);
            _lastMs = nowMs;
            _x = Spring.Step(_x, _target.X, dt, Spring.Pop);
            _y = Spring.Step(_y, _target.Y, dt, Spring.Pop);

            if (Spring.AtRest(_x, _target.X) && Spring.AtRest(_y, _target.Y))
            {
                Done = true;
                return _target;
            }

            return new PixelPoint((int)Math.Round(_x.Value), (int)Math.Round(_y.Value));
        }
    }
}
