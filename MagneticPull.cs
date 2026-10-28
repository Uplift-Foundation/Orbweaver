using System;
using System.Collections.Generic;
using Avalonia;

namespace Orbweaver
{
    // CB-268 M1: while a drop would Join or Pair, the dragged team is pulled
    // towards its target by up to about 30% of the gap that remains, easing in
    // as it enters join range and out as it leaves, and never past the target.
    //
    // Pure: it answers "how far, and which way, should the team be nudged from
    // where the pointer has it", in DIP. The smoothing over time is the
    // caller's spring, so entering and leaving range cannot jump.
    internal static class MagneticPull
    {
        /// <summary>The most of the remaining gap the pull closes.</summary>
        internal const double MaxFraction = 0.30;

        // The pull reaches full strength this far (as a fraction of join range)
        // inside the range, and is zero at its edge: the ease-in. A pull that
        // jumped to full strength the moment the gap crossed 24 DIP would be a
        // tug, not a magnet.
        private const double RampDepth = 0.35;

        /// <summary>
        /// The offset, in DIP, to add to the dragged team's position. Zero when
        /// the candidate is out of range or already touching.
        /// </summary>
        internal static (double X, double Y) Offset(
            IReadOnlyList<(double X, double Y, double R)> moving, SnapCandidate target, double joinDip)
        {
            if (moving.Count == 0 || joinDip <= 0) return (0, 0);

            var best = (Gap: double.PositiveInfinity, Dx: 0.0, Dy: 0.0);

            foreach (var (x, y, r) in moving)
            {
                foreach (var (ox, oy, or) in target.Discs)
                    Consider(ref best, x, y, r, ox, oy, or);

                if (target.Outline.Length >= 3)
                    ConsiderOutline(ref best, new Point(x, y), r, target.Outline);
            }

            if (double.IsInfinity(best.Gap) || best.Gap <= 0 || best.Gap > joinDip) return (0, 0);

            var depth = (joinDip - best.Gap) / joinDip;
            var weight = Smooth(Math.Clamp(depth / RampDepth, 0, 1));
            var pull = MaxFraction * best.Gap * weight;

            return (best.Dx * pull, best.Dy * pull);
        }

        /// <summary>The pull's smoothstep: 0 at 0, 1 at 1, flat at both ends.</summary>
        internal static double Smooth(double t)
        {
            t = Math.Clamp(t, 0, 1);
            return t * t * (3 - 2 * t);
        }

        // Edge to edge between two discs, and the unit vector from the first
        // towards the second.
        private static void Consider(
            ref (double Gap, double Dx, double Dy) best,
            double x, double y, double r, double ox, double oy, double or)
        {
            var vx = ox - x;
            var vy = oy - y;
            var dist = Math.Sqrt(vx * vx + vy * vy);
            var gap = dist - r - or;
            if (gap >= best.Gap) return;

            best = dist < 1e-9 ? (gap, 0, 0) : (gap, vx / dist, vy / dist);
        }

        // The gap from the disc's edge to the polygon from outside, towards the
        // nearest point on it. Inside the polygon there is nothing to close.
        private static void ConsiderOutline(
            ref (double Gap, double Dx, double Dy) best, Point centre, double radius, Point[] polygon)
        {
            if (GroupOutline.SignedDistanceInside(centre, polygon) >= 0) return;

            var nearest = default(Point);
            var nearestDist = double.PositiveInfinity;
            for (var i = 0; i < polygon.Length; i++)
            {
                var p = Closest(centre, polygon[i], polygon[(i + 1) % polygon.Length]);
                var d = Math.Sqrt((p.X - centre.X) * (p.X - centre.X) + (p.Y - centre.Y) * (p.Y - centre.Y));
                if (d < nearestDist)
                {
                    nearestDist = d;
                    nearest = p;
                }
            }

            var gap = nearestDist - radius;
            if (gap >= best.Gap) return;

            best = nearestDist < 1e-9
                ? (gap, 0, 0)
                : (gap, (nearest.X - centre.X) / nearestDist, (nearest.Y - centre.Y) / nearestDist);
        }

        private static Point Closest(Point p, Point a, Point b)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var len2 = dx * dx + dy * dy;
            var t = len2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
            return new Point(a.X + t * dx, a.Y + t * dy);
        }
    }
}
