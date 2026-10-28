using System;

namespace Orbweaver
{
    // CB-283 M2: the squash-and-stretch an arriving orb does when it is dropped
    // into a group (a Join or a Pair). Its disc swells to 1.15, dips to 0.92 and
    // settles on 1.0, about 280 ms in all, while it springs into its slot.
    // SessionManager's arrangement animation hands the scale to
    // OrbWindow.SetPopScale each frame, which composes it with the drag squash.
    //
    // Keyframes rather than a spring, deliberately. A spring cannot pass through
    // those three numbers and also stop on 1.0 at a fixed time. A pop that ends
    // when a rest threshold agrees is the same open-ended finish that had to be
    // capped on the glides. Each leg is a half-cosine, so the scale is still at
    // every keyframe: no kink at the peak or the dip, and none where it meets
    // rest at either end.
    //
    // It rises from 1.0 rather than starting on 1.15. A disc jumping straight to
    // 1.15 from rest is a one-frame snap, which reads as a glitch rather than a
    // pop.
    internal static class DropPop
    {
        internal const double Peak = 1.15;
        internal const double Dip = 0.92;
        internal const int PeakMs = 70;
        internal const int DipMs = 170;
        internal const int DurationMs = 280;

        /// <summary>
        /// The disc's scale <paramref name="elapsedMs"/> into the pop: 1.0
        /// before it starts and from <see cref="DurationMs"/> on, exactly. A
        /// non-finite time reads as rest.
        /// </summary>
        internal static double ScaleAt(double elapsedMs)
        {
            if (!double.IsFinite(elapsedMs) || elapsedMs <= 0 || elapsedMs >= DurationMs) return 1.0;

            if (elapsedMs < PeakMs) return Ease(1.0, Peak, elapsedMs / PeakMs);
            if (elapsedMs < DipMs) return Ease(Peak, Dip, (elapsedMs - PeakMs) / (DipMs - PeakMs));
            return Ease(Dip, 1.0, (elapsedMs - DipMs) / (DurationMs - DipMs));
        }

        private static double Ease(double from, double to, double t) =>
            from + (to - from) * (1 - Math.Cos(Math.PI * t)) / 2;
    }
}
