using System;
using System.Collections.Generic;
using Avalonia;

namespace Orbweaver;

// CB-268: the outline drawn behind a group of orbs — the group's OWN shape, so a
// heart arrangement is wrapped in a heart, a line in a horizontal stadium, a
// column in a vertical one, a grid in a rounded rectangle.
//
// Pure, like OrbArrangement: points in, points out, in whatever units the caller
// uses (the backdrop passes DIPs), so none of it needs a window.
//
// The curves are the arrangement's own (OrbArrangement.CircleAt/DiamondAt/
// StarAt/HeartAt), not look-alikes: the orbs were placed on exactly these, so the
// outline reads as the shape the arrangement drew rather than as a second shape
// that happens to resemble it.
//
// Enclosure is a measurement, not a hope. The base shape is scaled uniformly by a
// binary search to the smallest factor at which every disc is inside by at least
// its radius plus the padding, judged by SignedDistanceInside against the actual
// polygon. That is what makes it hold for a concave heart or star, for team
// members fanned far from the rest, and for mixed radii, without a per-shape
// special case. Scaling about a point that sees the whole boundary makes each
// scaled shape contain the smaller ones, so "inside by ≥ r" is monotone in the
// scale and bisection is sound.
internal static class GroupOutline
{
    internal const int CurveSamples = 360;
    internal const int Iterations = 24;

    // Corner arcs on the rounded rectangle and each end cap of a stadium.
    private const int CornerSamples = 24;
    private const int CapSamples = 60;

    // How round the grid's corners are, as a share of its shorter half-side.
    private const double GridCorner = 0.3;

    // Closed polygon (first vertex is not repeated) around `discs`, each of which
    // ends up at least padding clear of the edge.
    //
    // Where it is centred, which a caller refitting during a drag will want to
    // know: curves (circle, diamond, star, heart) put the curve's own perimeter
    // centroid on the discs' centroid — the discs were spread along that curve,
    // so for an even arrangement the two coincide, which the curve's origin does
    // not do for a heart (its origin sits well off its middle). The stadium and
    // the rounded rectangle are instead centred on the bounding box of the discs
    // and sized from it, because their discs are lines and lattices whose last
    // row may be short; the centroid would hang the box off to one side.
    //
    // One disc is a circle of exactly R + padding about it (the inscribed radius,
    // not the vertex radius — a polygon's edges sag inside its vertices). No
    // discs is an empty outline. A non-finite or negative number throws
    // ArgumentException/ArgumentOutOfRangeException rather than producing a
    // plausible-looking polygon of NaNs.
    internal static Point[] Silhouette(
        string? shape, IReadOnlyList<(double X, double Y, double R)> discs, double padding,
        PlacedCurve? placed = null)
        => Fit(shape, discs, padding, placed).Polygon;

    // Silhouette plus how it was got: the unit-sized base, where it was centred
    // and the factor it was scaled by (polygon[i] = centre + base[i] * scale).
    // Exposed so a test can shrink the scale by two percent and watch enclosure
    // fail, which is what "smallest" means, without guessing the centre.
    internal readonly record struct Fitted(
        Point[] Polygon, Point[] Base, Point Centre, double Scale, double Offset = 0);

    // The curve the arrangement actually placed a group's orbs on: where unit
    // (0,0) of OrbArrangement.CircleAt/DiamondAt/StarAt/HeartAt landed on screen
    // and how many screen units one unit became (GroupPlacement.CurveCentre and
    // CurveScale). With it the outline is that very curve pushed outward, rather
    // than a stand-in fitted around the discs.
    internal readonly record struct PlacedCurve(Point Centre, double Scale);

    internal static Fitted Fit(
        string? shape, IReadOnlyList<(double X, double Y, double R)> discs, double padding,
        PlacedCurve? placed = null)
    {
        ArgumentNullException.ThrowIfNull(discs);

        if (!double.IsFinite(padding) || padding < 0)
            throw new ArgumentOutOfRangeException(nameof(padding), padding, "padding must be finite and >= 0");

        foreach (var d in discs)
        {
            if (!double.IsFinite(d.X) || !double.IsFinite(d.Y))
                throw new ArgumentException("disc centre must be finite", nameof(discs));
            if (!double.IsFinite(d.R) || d.R < 0)
                throw new ArgumentException("disc radius must be finite and >= 0", nameof(discs));
        }

        if (discs.Count == 0) return new Fitted(Array.Empty<Point>(), Array.Empty<Point>(), default, 1);

        if (discs.Count == 1)
        {
            var only = discs[0];
            // Sample at the vertex radius that makes the *edges* sit R + padding out.
            var vertex = (only.R + padding + 1e-9) / Math.Cos(Math.PI / CurveSamples);
            var unit = new Point[CurveSamples];
            var circle = new Point[CurveSamples];
            for (var i = 0; i < CurveSamples; i++)
            {
                var a = 2 * Math.PI * i / CurveSamples;
                unit[i] = new Point(vertex * Math.Sin(a), -vertex * Math.Cos(a));
                circle[i] = new Point(only.X + unit[i].X, only.Y + unit[i].Y);
            }
            return new Fitted(circle, unit, new Point(only.X, only.Y), 1);
        }

        if (placed is { } curve && IsCurveShape(shape) &&
            double.IsFinite(curve.Centre.X) && double.IsFinite(curve.Centre.Y) &&
            double.IsFinite(curve.Scale) && curve.Scale > 0)
            return FitOffset(shape, discs, padding, curve);

        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        double sumX = 0, sumY = 0, maxR = 0;

        foreach (var d in discs)
        {
            minX = Math.Min(minX, d.X); maxX = Math.Max(maxX, d.X);
            minY = Math.Min(minY, d.Y); maxY = Math.Max(maxY, d.Y);
            sumX += d.X; sumY += d.Y;
            maxR = Math.Max(maxR, d.R);
        }

        var basePoly = BaseShape(shape, (maxX - minX) / 2 + maxR, (maxY - minY) / 2 + maxR);

        Point centre;
        if (IsBoxShape(shape))
        {
            centre = new Point((minX + maxX) / 2, (minY + maxY) / 2);
        }
        else
        {
            var anchor = PerimeterCentroid(basePoly);
            centre = new Point(sumX / discs.Count, sumY / discs.Count);
            basePoly = Translate(basePoly, -anchor.X, -anchor.Y);
        }

        // A ball of radius `inner` about the centre lies wholly inside the base
        // shape, so scaling until that ball holds every disc plus its margin is
        // a factor that certainly encloses — an upper bound for the bisection,
        // with no guessing about how large is large enough.
        var inner = SignedDistanceInside(default, basePoly);
        // Unreachable with the shapes that exist (each base contains its own centre);
        // kept so a future shape that breaks that fails loudly instead of dividing by zero.
        if (!(inner > 0)) throw new InvalidOperationException("base outline does not contain its own centre");

        double hi = 0;
        foreach (var d in discs)
        {
            var reach = Math.Sqrt((d.X - centre.X) * (d.X - centre.X) + (d.Y - centre.Y) * (d.Y - centre.Y)) + d.R + padding;
            hi = Math.Max(hi, reach / inner);
        }

        // The bound is exact when a disc lies straight out toward the nearest wall,
        // so rounding can put it a hair short of enclosing. A hair of headroom keeps
        // the starting point itself an enclosing scale, which the loop relies on.
        hi *= 1 + 1e-9;

        double lo = 0;
        for (var i = 0; i < Iterations; i++)
        {
            var mid = (lo + hi) / 2;
            if (Encloses(basePoly, centre, mid, discs, padding)) hi = mid; else lo = mid;
        }

        var result = new Point[basePoly.Length];
        for (var i = 0; i < result.Length; i++)
            result[i] = new Point(centre.X + basePoly[i].X * hi, centre.Y + basePoly[i].Y * hi);
        return new Fitted(result, basePoly, centre, hi);
    }

    // Whether every disc, with its padding, is inside the base shape scaled by
    // `scale` about `centre`. Done by scaling the disc positions the other way
    // instead of rebuilding the polygon — same answer, one multiply per disc.
    internal static bool Encloses(
        Point[] basePoly, Point centre, double scale,
        IReadOnlyList<(double X, double Y, double R)> discs, double padding)
    {
        if (scale <= 0) return false;

        foreach (var d in discs)
        {
            var p = new Point((d.X - centre.X) / scale, (d.Y - centre.Y) / scale);
            if (SignedDistanceInside(p, basePoly) * scale < d.R + padding) return false;
        }
        return true;
    }

    // Distance from `p` to the polygon's edge: positive inside, negative outside,
    // zero on it. Even-odd for the side, nearest edge for the size — so concave
    // pockets (a heart's cleft, a star's notches) report how far the point is
    // from the NEAREST wall rather than from the hull. Fewer than three vertices
    // enclose nothing: negative infinity.
    internal static double SignedDistanceInside(Point p, Point[] polygon)
    {
        if (polygon.Length < 3) return double.NegativeInfinity;

        var inside = false;
        var best = double.MaxValue;

        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var a = polygon[j];
            var b = polygon[i];

            if ((a.Y > p.Y) != (b.Y > p.Y) &&
                p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;

            var ex = b.X - a.X;
            var ey = b.Y - a.Y;
            var len2 = ex * ex + ey * ey;
            var t = len2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * ex + (p.Y - a.Y) * ey) / len2, 0, 1);
            var dx = p.X - (a.X + t * ex);
            var dy = p.Y - (a.Y + t * ey);
            best = Math.Min(best, dx * dx + dy * dy);
        }

        var dist = Math.Sqrt(best);
        return inside ? dist : -dist;
    }

    // The rectangle the polygon occupies; the backdrop window is sized from it.
    // An empty polygon is an empty rectangle at the origin.
    internal static Rect Bounds(Point[] polygon)
    {
        if (polygon.Length == 0) return default;

        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var p in polygon)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static bool IsCurveShape(string? shape) => !IsBoxShape(shape);

    private const int RaySamples = 240;
    private const int OffsetCurveSamples = 160;

    // The curve the orbs were placed on, pushed outward by just enough to hold
    // every disc, with round joins; where the curve folds in on itself (a heart's
    // cleft, a star's notches) the outer boundary of the offset is taken, which
    // fills the fold with a rounded dip instead of following it in.
    //
    // The offset region is R_d = { p : dist(p, curve) <= d } plus the curve's own
    // inside, which is exactly { p : SignedDistanceInside(p, curve) >= -d }. Its
    // outer boundary is found by walking in from far away along rays from the
    // curve's middle: that function is 1-Lipschitz, so stepping inward by how far
    // outside it reads can never skip over the region, and the first hit is the
    // outermost boundary.
    //
    // How far to push is closed-form, not searched: a disc's padded circle lies
    // inside R_d exactly when SignedDistanceInside(centre) + d >= R + padding (the
    // nearest point outside R_d is d further out than the curve, straight away
    // from the nearest curve point), so d = max over discs of
    // R + padding - SignedDistanceInside(centre). A disc on the curve needs
    // R + padding; a team member fanned well off it needs more, and the whole
    // outline swells just enough to hold it.
    private static Fitted FitOffset(
        string? shape, IReadOnlyList<(double X, double Y, double R)> discs, double padding, PlacedCurve curve)
    {
        var at = CurveFor(shape);
        var path = new Point[OffsetCurveSamples];
        for (var i = 0; i < path.Length; i++)
        {
            var (x, y) = at(2 * Math.PI * i / path.Length);
            path[i] = new Point(curve.Centre.X + x * curve.Scale, curve.Centre.Y + y * curve.Scale);
        }

        var origin = PerimeterCentroid(path);

        var offset = padding;
        foreach (var d in discs)
            offset = Math.Max(offset, d.R + padding - SignedDistanceInside(new Point(d.X, d.Y), path));

        // The polygon cuts each arc of the boundary slightly inside it; if that
        // leaves a disc a hair short, push a little further until it does not.
        Point[] outline = Array.Empty<Point>();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            outline = OffsetOutline(path, origin, offset);

            var deficit = 0.0;
            foreach (var d in discs)
                deficit = Math.Max(deficit, d.R + padding - SignedDistanceInside(new Point(d.X, d.Y), outline));

            if (deficit <= 1e-6) break;
            offset += deficit + 1e-6;
        }

        return new Fitted(outline, outline, default, 1, offset);
    }

    private static Func<double, (double X, double Y)> CurveFor(string? shape) => shape switch
    {
        "circle" => OrbArrangement.CircleAt,
        "diamond" => OrbArrangement.DiamondAt,
        "star" => OrbArrangement.StarAt,
        _ => OrbArrangement.HeartAt
    };

    private static Point[] OffsetOutline(Point[] path, Point origin, double offset)
    {
        double far = 0;
        foreach (var p in path)
            far = Math.Max(far, Math.Sqrt((p.X - origin.X) * (p.X - origin.X) + (p.Y - origin.Y) * (p.Y - origin.Y)));
        far += offset + 1;

        var minStep = Math.Max(offset * 0.05, 1e-3);
        var outline = new Point[RaySamples];

        for (var k = 0; k < RaySamples; k++)
        {
            var a = 2 * Math.PI * k / RaySamples;
            double ux = Math.Sin(a), uy = -Math.Cos(a);

            double Reach(double r) =>
                SignedDistanceInside(new Point(origin.X + ux * r, origin.Y + uy * r), path) + offset;

            double outside = far;   // known to be outside the region
            double r = far;
            var inside = false;

            for (var step = 0; step < 200 && r > 0; step++)
            {
                var f = Reach(r);
                if (f >= 0) { inside = true; break; }
                outside = r;
                r -= Math.Max(-f, minStep);
            }

            if (!inside) r = 0;

            // r is inside (or the origin) and `outside` is the last point known to be
            // outside: the boundary is between them.
            double lo = r, hi = outside;
            for (var i = 0; i < 8 && hi > lo; i++)
            {
                var mid = (lo + hi) / 2;
                if (Reach(mid) >= 0) lo = mid; else hi = mid;
            }

            outline[k] = new Point(origin.X + ux * lo, origin.Y + uy * lo);
        }

        return outline;
    }

    private static bool IsBoxShape(string? shape) => shape is "line" or "vline" or "grid";

    // The unscaled outline. Curves come from the arrangement's own helpers;
    // box shapes get their proportions from the discs they must hold
    // (halfW/halfH already include the largest radius).
    private static Point[] BaseShape(string? shape, double halfW, double halfH)
    {
        switch (shape)
        {
            case "line": return Stadium(Math.Max(halfW, halfH), halfH);
            case "vline": return Stadium(halfW, Math.Max(halfW, halfH), vertical: true);
            case "grid": return RoundedRect(Math.Max(halfW, 1e-6), Math.Max(halfH, 1e-6));
            case "circle": return Curve(OrbArrangement.CircleAt);
            case "diamond": return Curve(OrbArrangement.DiamondAt);
            case "star": return Curve(OrbArrangement.StarAt);
            default: return Curve(OrbArrangement.HeartAt); // same default as OrbArrangement.Unit
        }
    }

    private static Point[] Curve(Func<double, (double X, double Y)> at)
    {
        var pts = new Point[CurveSamples];
        for (var i = 0; i < CurveSamples; i++)
        {
            var (x, y) = at(2 * Math.PI * i / CurveSamples);
            pts[i] = new Point(x, y);
        }
        return pts;
    }

    // Horizontal stadium of half-length `halfLong` and half-thickness `halfShort`
    // (end caps are semicircles of radius halfShort), or its vertical twin.
    private static Point[] Stadium(double halfW, double halfH, bool vertical = false)
    {
        halfW = Math.Max(halfW, 1e-6);
        halfH = Math.Max(halfH, 1e-6);

        var pts = new List<Point>(2 * CapSamples + 2);

        if (!vertical)
        {
            var r = halfH;
            var straight = Math.Max(halfW - r, 0);
            for (var i = 0; i <= CapSamples; i++)       // right cap, top to bottom
            {
                var a = -Math.PI / 2 + Math.PI * i / CapSamples;
                pts.Add(new Point(straight + r * Math.Cos(a), r * Math.Sin(a)));
            }
            for (var i = 0; i <= CapSamples; i++)       // left cap, bottom to top
            {
                var a = Math.PI / 2 + Math.PI * i / CapSamples;
                pts.Add(new Point(-straight + r * Math.Cos(a), r * Math.Sin(a)));
            }
        }
        else
        {
            var r = halfW;
            var straight = Math.Max(halfH - r, 0);
            for (var i = 0; i <= CapSamples; i++)       // bottom cap, right to left
            {
                var a = Math.PI * i / CapSamples;
                pts.Add(new Point(r * Math.Cos(a), straight + r * Math.Sin(a)));
            }
            for (var i = 0; i <= CapSamples; i++)       // top cap, left to right
            {
                var a = Math.PI + Math.PI * i / CapSamples;
                pts.Add(new Point(r * Math.Cos(a), -straight + r * Math.Sin(a)));
            }
        }

        return pts.ToArray();
    }

    private static Point[] RoundedRect(double halfW, double halfH)
    {
        var r = GridCorner * Math.Min(halfW, halfH);
        var pts = new List<Point>(4 * (CornerSamples + 1));

        // Corner centres, clockwise from top-right, each sweeping its own quarter.
        var corners = new[]
        {
            (cx: halfW - r, cy: -(halfH - r), start: -Math.PI / 2),
            (cx: halfW - r, cy: halfH - r, start: 0.0),
            (cx: -(halfW - r), cy: halfH - r, start: Math.PI / 2),
            (cx: -(halfW - r), cy: -(halfH - r), start: Math.PI),
        };

        foreach (var c in corners)
            for (var i = 0; i <= CornerSamples; i++)
            {
                var a = c.start + Math.PI / 2 * i / CornerSamples;
                pts.Add(new Point(c.cx + r * Math.Cos(a), c.cy + r * Math.Sin(a)));
            }

        return pts.ToArray();
    }

    // Edge-length-weighted midpoint of the closed polyline.
    private static Point PerimeterCentroid(Point[] poly)
    {
        double sx = 0, sy = 0, total = 0;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            var len = Math.Sqrt(Math.Pow(poly[i].X - poly[j].X, 2) + Math.Pow(poly[i].Y - poly[j].Y, 2));
            sx += len * (poly[i].X + poly[j].X) / 2;
            sy += len * (poly[i].Y + poly[j].Y) / 2;
            total += len;
        }
        return new Point(sx / total, sy / total);
    }

    private static Point[] Translate(Point[] poly, double dx, double dy)
    {
        var moved = new Point[poly.Length];
        for (var i = 0; i < poly.Length; i++) moved[i] = new Point(poly[i].X + dx, poly[i].Y + dy);
        return moved;
    }
}
