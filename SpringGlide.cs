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

        /// <summary>The overshoot the carried velocity may cause, as a fraction of the distance to the slot...</summary>
        internal const double MaxOvershootFraction = 0.08;

        /// <summary>...or this many DIP, whichever is larger.</summary>
        internal const double MaxOvershootDip = 12;

        /// <param name="velocity">The orb's velocity when released, in screen units per second.</param>
        /// <param name="unitsPerDip">Screen units in one DIP, so the overshoot cap is in DIP whatever the display.</param>
        internal SpringGlide(
            PixelPoint from, PixelPoint target, (double X, double Y) velocity, long startMs, bool reduceMotion,
            double unitsPerDip = 1)
        {
            // A velocity that is not a number carries nothing.
            velocity = (Finite.Or(velocity.X, 0), Finite.Or(velocity.Y, 0));
            var units = Finite.All(unitsPerDip) && unitsPerDip > 0 ? unitsPerDip : 1;
            velocity = (
                CapVelocity(from.X, target.X, velocity.X, units),
                CapVelocity(from.Y, target.Y, velocity.Y, units));

            _from = from;
            _target = target;
            _startMs = startMs;
            _lastMs = startMs;
            _reduce = reduceMotion;
            _x = new SpringState(from.X, velocity.X);
            _y = new SpringState(from.Y, velocity.Y);
            Done = from == target && !reduceMotion && velocity == (0, 0);
        }

        // The largest velocity, on one axis, that does not throw the orb further
        // than max(8% of the distance, 12 DIP) beyond where the spring would
        // take it anyway: past the slot when it was flung at it, or out beyond
        // where it was released when it was flung away. A 20 px slot at 30,000
        // px/s would otherwise overshoot by hundreds of pixels (a high-poll
        // mouse spike or a touch fling can produce that), and the orb would
        // cross the screen. Found by search, not a formula: the excursion is
        // monotone in the speed, so the largest scale in [0, 1] that stays under
        // the limit is the one to use.
        internal static double CapVelocity(double from, double target, double velocity, double unitsPerDip)
        {
            if (velocity == 0) return 0;

            var distance = Math.Abs(from - target);
            var limit = Math.Max(MaxOvershootFraction * distance, MaxOvershootDip * unitsPerDip);

            double lo = 0, hi = 1;
            for (var i = 0; i < 30; i++)
            {
                var mid = (lo + hi) / 2;
                if (Excursion(from, target, velocity * mid) <= limit) lo = mid; else hi = mid;
            }

            return velocity * lo;
        }

        // How far, beyond what the spring does with no velocity at all, the
        // orb gets: the larger of the overshoot past the slot and the excursion
        // out beyond its start. Simulated with Spring.Pop at 2 ms for a second.
        private static double Excursion(double from, double target, double velocity)
        {
            var side = from >= target ? 1.0 : -1.0;
            var distance = Math.Abs(from - target);
            var state = new SpringState(from, velocity);
            var past = 0.0;      // beyond the slot, on the far side
            var beyond = 0.0;    // beyond the start, away from the slot
            var rest = new SpringParams(Spring.Pop.Omega, Spring.Pop.Zeta, 0, 0);

            for (var t = 0.0; t < 1.0; t += 0.002)
            {
                state = Spring.Step(state, target, 0.002, rest);
                var along = (state.Value - target) * side;   // + : on the start's side
                past = Math.Max(past, -along);
                beyond = Math.Max(beyond, along - distance);
            }

            // The spring's own 8% overshoot with no velocity is expected and is
            // what the 8% allowance is for; the cap is on the total.
            return Math.Max(past, beyond);
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
