using System;

namespace Orbweaver
{
    /// <summary>
    /// One spring's parameters. <paramref name="Omega"/> is the undamped natural
    /// frequency in radians per second (bigger is snappier; the spring's period
    /// is 2*pi/Omega). <paramref name="Zeta"/> is the damping ratio: 1 is
    /// critically damped (settles with no overshoot), below 1 overshoots, above
    /// 1 creeps in. <paramref name="RestDelta"/> and <paramref name="RestVelocity"/>
    /// are how close to the target, and how slow, counts as arrived; the spring
    /// then lands exactly on the target.
    /// </summary>
    internal readonly record struct SpringParams(
        double Omega, double Zeta, double RestDelta = 0.05, double RestVelocity = 0.05);

    /// <summary>A spring's position and velocity along one axis.</summary>
    internal readonly record struct SpringState(double Value, double Velocity);

    // CB-268 motion: the pure maths under every "pop" and "squish". No window, no
    // clock — callers hand it elapsed time, so a test drives it with a fake one.
    //
    // It is the closed-form solution of a damped harmonic oscillator, not a
    // numerical integration. That is a choice, not a flourish: the answer is the
    // same whether a frame is 8 ms or 80 ms, so a dropped frame does not change
    // where an animation ends up or how far it overshoots, and a spring cannot
    // blow up at a large timestep the way an explicit Euler step does. One step
    // of 100 ms and a hundred steps of 1 ms land on the same value.
    internal static class Spring
    {
        /// <summary>Roughly 8% overshoot — the pop on a drop, the settle of a spring back.</summary>
        internal static readonly SpringParams Pop = new(Omega: 26, Zeta: DampingRatioForOvershoot(0.08));

        /// <summary>A gentle, nearly critical settle with about 1% overshoot, for things that should relax rather than bounce.</summary>
        internal static readonly SpringParams Relax = new(Omega: 18, Zeta: DampingRatioForOvershoot(0.01));

        /// <summary>
        /// The damping ratio whose first overshoot is <paramref name="overshoot"/>
        /// (a fraction of the distance travelled: 0.08 is 8%). 0 or less is
        /// critical damping; 1 or more is clamped just under 1.
        /// </summary>
        internal static double DampingRatioForOvershoot(double overshoot)
        {
            if (overshoot <= 0) return 1;
            if (overshoot >= 1) return 0.0001;

            var ln = Math.Log(overshoot);
            return -ln / Math.Sqrt(Math.PI * Math.PI + ln * ln);
        }

        /// <summary>The first overshoot, as a fraction of the distance travelled, of a spring with this damping ratio.</summary>
        internal static double Overshoot(double zeta) =>
            zeta >= 1 ? 0 : Math.Exp(-Math.PI * Math.Max(0, zeta) / Math.Sqrt(1 - Math.Max(0, zeta) * Math.Max(0, zeta)));

        /// <summary>
        /// Advances the spring <paramref name="dt"/> seconds towards
        /// <paramref name="target"/>. A non-positive step changes nothing; a
        /// non-positive frequency is an instant jump to the target. Once the
        /// spring is within its rest thresholds it returns exactly the target
        /// with zero velocity, so an animation ends on the layout slot to the
        /// pixel rather than a hair short of it.
        /// </summary>
        internal static SpringState Step(SpringState state, double target, double dt, SpringParams p)
        {
            if (dt <= 0) return state;
            if (p.Omega <= 0) return new SpringState(target, 0);

            var w = p.Omega;
            var z = Math.Max(0, p.Zeta);
            var d = state.Value - target;
            var v = state.Velocity;

            double d2, v2;

            if (Math.Abs(z - 1) < 1e-9)
            {
                var e = Math.Exp(-w * dt);
                var b = v + w * d;
                d2 = e * (d + b * dt);
                v2 = e * (v - w * b * dt);
            }
            else if (z < 1)
            {
                var wd = w * Math.Sqrt(1 - z * z);
                var e = Math.Exp(-z * w * dt);
                var c = Math.Cos(wd * dt);
                var s = Math.Sin(wd * dt);
                var b = (v + z * w * d) / wd;
                d2 = e * (d * c + b * s);
                v2 = e * ((b * wd - z * w * d) * c - (d * wd + z * w * b) * s);
            }
            else
            {
                var root = w * Math.Sqrt(z * z - 1);
                var r1 = -w * z + root;
                var r2 = -w * z - root;
                var c1 = (v - r2 * d) / (r1 - r2);
                var c2 = d - c1;
                var e1 = Math.Exp(r1 * dt);
                var e2 = Math.Exp(r2 * dt);
                d2 = c1 * e1 + c2 * e2;
                v2 = c1 * r1 * e1 + c2 * r2 * e2;
            }

            return Math.Abs(d2) <= p.RestDelta && Math.Abs(v2) <= p.RestVelocity
                ? new SpringState(target, 0)
                : new SpringState(target + d2, v2);
        }

        /// <summary>True when the state is exactly at rest on the target.</summary>
        internal static bool AtRest(SpringState state, double target) =>
            state.Value == target && state.Velocity == 0;
    }
}
