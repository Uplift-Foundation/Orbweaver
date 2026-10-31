using System;

namespace Orbweaver
{
    // CB-268 motion: the one finite-value guard every motion entry point uses.
    // A clock glitch gives a NaN or infinite dt, an unmeasured orb gives a NaN
    // disc, and a NaN that reaches a position or a render transform puts an orb
    // at int.MinValue or nowhere. So each entry point checks its inputs with
    // this and answers with its safe value — no pull, the identity, or the
    // target itself — and never lets a NaN through.
    internal static class Finite
    {
        internal static bool All(double a) => double.IsFinite(a);
        internal static bool All(double a, double b) => double.IsFinite(a) && double.IsFinite(b);
        internal static bool All(double a, double b, double c) =>
            double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c);
        internal static bool All(double a, double b, double c, double d) =>
            double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c) && double.IsFinite(d);

        /// <summary><paramref name="value"/> if it is finite, otherwise <paramref name="fallback"/>.</summary>
        internal static double Or(double value, double fallback) => double.IsFinite(value) ? value : fallback;
    }
}
