using System;
using Avalonia;

namespace Orbweaver
{
    // CB-268 M3/M1: the pure maths behind a group outline that springs from its old
    // polygon to its new one instead of jumping, and breathes while it is a snap
    // target. No window, no clock of its own: a frame is a function of two
    // resampled polygons and a progress number, so it is tested with plain numbers.
    //
    // A morph never fits a new silhouette per frame. Both polygons are resampled
    // ONCE to the same number of vertices, one per angle about their own centroid,
    // and each frame is a per-vertex interpolation of centre and radius.
    internal static class OutlineMorph
    {
        // One vertex per 1.5 degrees: fine enough that a chord sags by well under a
        // pixel on the largest outline, and the same count for every polygon so any
        // two can be paired off.
        internal const int Samples = 240;

        internal const double BreathMin = 1.00;
        internal const double BreathMax = 1.04;
        internal const double BreathPeriodSeconds = 1.4;

        // A polygon as its centre and one radius per angle (angle k points along
        // (sin a, -cos a), a = 2 pi k / Samples, the way GroupOutline sweeps a circle).
        internal readonly record struct Polar(Point Centre, double[] Radii);

        // Centre of the polygon: its area centroid, or the vertex mean for a
        // degenerate (zero-area) one. A polygon nothing is inside has no centre; the
        // caller does not morph from or to nothing.
        internal static Point Centroid(Point[] poly)
        {
            double a = 0, cx = 0, cy = 0;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                var cross = poly[j].X * poly[i].Y - poly[i].X * poly[j].Y;
                a += cross;
                cx += (poly[j].X + poly[i].X) * cross;
                cy += (poly[j].Y + poly[i].Y) * cross;
            }

            if (Math.Abs(a) < 1e-9)
            {
                double mx = 0, my = 0;
                foreach (var p in poly) { mx += p.X; my += p.Y; }
                return new Point(mx / poly.Length, my / poly.Length);
            }

            return new Point(cx / (3 * a), cy / (3 * a));
        }

        // The polygon as radii about its centroid, one per angle, taking the
        // OUTERMOST crossing along each ray — so a notch that a ray passes through
        // twice (a star's, a heart's cleft) is read as the outline's outer boundary,
        // the same reading the offset outline already takes of its own fold.
        internal static Polar Resample(Point[] poly)
        {
            var c = Centroid(poly);
            var radii = new double[Samples];

            for (var k = 0; k < Samples; k++)
            {
                var ang = 2 * Math.PI * k / Samples;
                double ux = Math.Sin(ang), uy = -Math.Cos(ang);
                var best = 0.0;

                for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                {
                    // Ray c + t*u against segment a + s*(b-a), t >= 0, 0 <= s <= 1.
                    double ax = poly[j].X - c.X, ay = poly[j].Y - c.Y;
                    double ex = poly[i].X - poly[j].X, ey = poly[i].Y - poly[j].Y;
                    var den = ux * ey - uy * ex;
                    if (Math.Abs(den) < 1e-12) continue;

                    var t = (ax * ey - ay * ex) / den;
                    var s = (ax * uy - ay * ux) / den;
                    if (t > best && s >= -1e-9 && s <= 1 + 1e-9) best = t;
                }

                radii[k] = best;
            }

            return new Polar(c, radii);
        }

        // The polygon at progress `s` between two resampled ones: 0 is `from`, 1 is
        // `to`, and a value outside 0..1 (a spring's overshoot) extrapolates, so a
        // swell can swell past its target and settle back. Radii never go negative.
        internal static Point[] Frame(Polar from, Polar to, double s)
        {
            var cx = from.Centre.X + (to.Centre.X - from.Centre.X) * s;
            var cy = from.Centre.Y + (to.Centre.Y - from.Centre.Y) * s;
            var pts = new Point[Samples];

            for (var k = 0; k < Samples; k++)
            {
                var r = Math.Max(0, from.Radii[k] + (to.Radii[k] - from.Radii[k]) * s);
                var ang = 2 * Math.PI * k / Samples;
                pts[k] = new Point(cx + r * Math.Sin(ang), cy - r * Math.Cos(ang));
            }

            return pts;
        }

        // Whether the new outline encloses more than the old one: a join swells, a
        // leave contracts. Judged by area, which is what the eye reads.
        internal static bool IsGrowing(Point[] from, Point[] to) => Area(to) >= Area(from);

        internal static double Area(Point[] poly)
        {
            double a = 0;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                a += poly[j].X * poly[i].Y - poly[i].X * poly[j].Y;
            return Math.Abs(a) / 2;
        }

        // Spring progress at `elapsed` seconds: Spring.Step (juno's closed-form damped
        // oscillator) from 0 toward 1, evaluated in one step from rest so any clock can
        // ask for any instant and a dropped frame changes nothing. `damping` below 1
        // overshoots and wobbles (a leave); near 1 it swells and settles (a join). Once
        // within its rest thresholds the spring reports exactly 1.
        internal static double SpringProgress(double elapsed, double omega, double damping) =>
            Spring.Evaluate(elapsed, Params(omega, damping));

        // Whether that spring has landed exactly on 1 and stopped.
        internal static bool AtRest(double elapsed, double omega, double damping) =>
            Spring.AtRest(Spring.Step(default, 1, elapsed, Params(omega, damping)), 1);

        // Progress is a fraction of the way, so rest is measured in thousandths of it,
        // not Spring's default of 0.05 (which suits pixels, not a 0..1 ratio).
        private static SpringParams Params(double omega, double damping) => new(omega, damping, 0.002, 0.05);

        // The breathing scale of a highlighted target at time `t` seconds: a smooth
        // sweep between BreathMin and BreathMax, starting at the minimum so a
        // highlight begins at its true size.
        internal static double Breath(double t) =>
            BreathMin + (BreathMax - BreathMin) * (0.5 - 0.5 * Math.Cos(2 * Math.PI * t / BreathPeriodSeconds));
    }
}
