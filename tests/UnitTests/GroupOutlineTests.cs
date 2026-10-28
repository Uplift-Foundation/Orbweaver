using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-268, GroupOutline: the silhouette drawn behind a group of orbs.
//
// The disc sets are not invented. They come out of OrbArrangement.Compute — the
// code that places the orbs the outline has to hold — because a hand-made set of
// discs on a tidy circle would pass for a reason that has nothing to do with a
// heart that has fanned team members hanging off it.
public class GroupOutlineTests
{
    private const double Padding = 6;
    private const double Epsilon = 1e-6;

    public static IEnumerable<object[]> Cases()
    {
        foreach (var shape in new[] { "circle", "diamond", "star", "heart", "line", "vline", "grid" })
            foreach (var n in new[] { 1, 2, 5, 12, 30 })
                foreach (var mixed in new[] { false, true })
                    foreach (var fanned in new[] { false, true })
                        yield return new object[] { shape, n, mixed, fanned };
    }

    // Discs for n orbs as the arrangement really lays them out. `fanned` makes the
    // back half of the orbs team members of the front half, so they hang off their
    // leads instead of sitting on the shape; `mixed` gives every orb its own size.
    private static List<(double X, double Y, double R)> Discs(string shape, int n, bool mixed, bool fanned)
    {
        var leadOf = Enumerable.Repeat(-1, n).ToArray();
        if (fanned)
            for (var i = n / 2; i < n; i++) leadOf[i] = i - n / 2 < n / 2 ? i - n / 2 : -1;

        var sizeOf = mixed ? Enumerable.Range(0, n).Select(i => 0.6 + 1.4 * (i % 5) / 4.0).ToArray() : null;

        var layout = new OrbArrangement.Layout(new PixelRect(0, 0, 1920, 1080), 1.0, shape, 1.0);
        var at = OrbArrangement.Compute(n, leadOf, layout, sizeOf);

        var discs = new List<(double, double, double)>();
        for (var i = 0; i < n; i++)
        {
            var size = sizeOf?[i] ?? 1.0;
            var window = OrbArrangement.WindowFor(1.0, size);
            discs.Add((at[i].X + window / 2.0, at[i].Y + window / 2.0, OrbArrangement.CircleDip * size / 2));
        }
        return discs;
    }

    private static double Slack(Point[] polygon, List<(double X, double Y, double R)> discs) =>
        discs.Min(d => GroupOutline.SignedDistanceInside(new Point(d.X, d.Y), polygon) - d.R);

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryDiscIsInsideByItsRadiusPlusPadding(string shape, int n, bool mixed, bool fanned)
    {
        var discs = Discs(shape, n, mixed, fanned);
        var outline = GroupOutline.Silhouette(shape, discs, Padding);

        Assert.True(Slack(outline, discs) >= Padding - Epsilon,
            $"{shape} n={n} mixed={mixed} fanned={fanned}: worst slack {Slack(outline, discs)}");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheOutlineIsTheSmallestThatEncloses(string shape, int n, bool mixed, bool fanned)
    {
        var discs = Discs(shape, n, mixed, fanned);
        var fit = GroupOutline.Fit(shape, discs, Padding);

        // The scale the search settled on holds…
        Assert.True(GroupOutline.Encloses(fit.Base, fit.Centre, fit.Scale, discs, Padding));
        // …and two percent less does not, so it was not merely a generous one.
        Assert.False(GroupOutline.Encloses(fit.Base, fit.Centre, fit.Scale * 0.98, discs, Padding),
            $"{shape} n={n} mixed={mixed} fanned={fanned}: 2% smaller still encloses");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ThePolygonIsTheBaseAtTheReportedCentreAndScale(string shape, int n, bool mixed, bool fanned)
    {
        var discs = Discs(shape, n, mixed, fanned);
        var fit = GroupOutline.Fit(shape, discs, Padding);

        Assert.Equal(fit.Base.Length, fit.Polygon.Length);
        for (var i = 0; i < fit.Base.Length; i += 17)
        {
            Assert.Equal(fit.Centre.X + fit.Base[i].X * fit.Scale, fit.Polygon[i].X, 9);
            Assert.Equal(fit.Centre.Y + fit.Base[i].Y * fit.Scale, fit.Polygon[i].Y, 9);
        }
    }

    [Theory]
    [InlineData("circle", 360)]
    [InlineData("diamond", 360)]
    [InlineData("star", 360)]
    [InlineData("heart", 360)]
    [InlineData("nonsense", 360)]
    [InlineData(null, 360)]
    public void CurvesAreSampledAtThreeHundredSixtyPoints(string? shape, int expected)
    {
        var outline = GroupOutline.Silhouette(shape, Discs("circle", 5, false, false), Padding);
        Assert.Equal(expected, outline.Length);
    }

    [Fact]
    public void AnUnknownShapeIsDrawnAsAHeart_AsTheArrangementDoes()
    {
        var discs = Discs("heart", 12, false, false);
        Assert.Equal(GroupOutline.Silhouette("heart", discs, Padding), GroupOutline.Silhouette("zigzag", discs, Padding));
    }

    [Fact]
    public void ALineIsAHorizontalStadiumAndAColumnIsAVerticalOne()
    {
        var line = GroupOutline.Bounds(GroupOutline.Silhouette("line", Discs("line", 8, false, false), Padding));
        var column = GroupOutline.Bounds(GroupOutline.Silhouette("vline", Discs("vline", 8, false, false), Padding));

        Assert.True(line.Width > line.Height * 2, $"line {line}");
        Assert.True(column.Height > column.Width * 2, $"column {column}");
    }

    [Fact]
    public void AStadiumIsRoundedAtBothEnds()
    {
        var discs = Discs("line", 8, false, false);
        var outline = GroupOutline.Silhouette("line", discs, Padding);
        var box = GroupOutline.Bounds(outline);

        // A rectangle would have a vertex in each corner; a stadium has nothing
        // within the cap radius of it.
        var corner = new Point(box.Right, box.Top);
        Assert.True(outline.Min(p => Math.Sqrt(Math.Pow(p.X - corner.X, 2) + Math.Pow(p.Y - corner.Y, 2))) > box.Height / 10);
    }

    [Fact]
    public void AGridIsARoundedRectangleAndAGridFallbackIsTheSame()
    {
        var discs = Discs("grid", 12, false, false);
        var outline = GroupOutline.Silhouette("grid", discs, Padding);
        var box = GroupOutline.Bounds(outline);

        // Flat sides: points exist along the top edge at its full height.
        Assert.True(outline.Count(p => Math.Abs(p.Y - box.Top) < 1e-6) >= 2);
        // Rounded corners: none sits in the bounding box's corner.
        var corner = new Point(box.Left, box.Bottom);
        Assert.True(outline.Min(p => Math.Sqrt(Math.Pow(p.X - corner.X, 2) + Math.Pow(p.Y - corner.Y, 2))) > 1);

        // A heart arrangement that was squeezed into a grid by the arrangement is
        // passed as "grid" by the caller and drawn as one: the same call.
        Assert.Equal(outline, GroupOutline.Silhouette("grid", discs, Padding));
    }

    [Fact]
    public void ASingleDiscIsACircleOfExactlyItsRadiusPlusPadding()
    {
        var outline = GroupOutline.Silhouette("heart", new[] { (100.0, 200.0, 18.0) }, Padding);

        Assert.Equal(360, outline.Length);
        // Inscribed radius: the centre is that far from the nearest edge.
        Assert.Equal(24.0, GroupOutline.SignedDistanceInside(new Point(100, 200), outline), 6);
        // And the polygon is centred on the disc whatever shape was asked for.
        var box = GroupOutline.Bounds(outline);
        Assert.Equal(100.0, box.Center.X, 3);
        Assert.Equal(200.0, box.Center.Y, 3);
    }

    [Fact]
    public void NoDiscsIsNoOutline()
    {
        Assert.Empty(GroupOutline.Silhouette("heart", Array.Empty<(double, double, double)>(), Padding));
        Assert.Equal(default, GroupOutline.Bounds(Array.Empty<Point>()));
    }

    [Fact]
    public void ZeroRadiusAndZeroPaddingAreAllowed()
    {
        var outline = GroupOutline.Silhouette("circle", new[] { (0.0, 0.0, 0.0), (50.0, 0.0, 0.0) }, 0);
        Assert.True(Slack(outline, new() { (0, 0, 0), (50, 0, 0) }) >= -Epsilon);
    }

    [Fact]
    public void CoincidentDiscsStillGetAnOutline()
    {
        var discs = new List<(double X, double Y, double R)> { (10, 10, 18), (10, 10, 18) };
        foreach (var shape in new[] { "line", "vline", "grid", "heart" })
            Assert.True(Slack(GroupOutline.Silhouette(shape, discs, Padding), discs) >= Padding - Epsilon, shape);
    }

    [Fact]
    public void TwoDiscsInADiagonalAreEnclosedByAStadium()
    {
        // halfH > halfW for "line" would make the stadium thicker than long.
        var discs = new List<(double X, double Y, double R)> { (0, 0, 18), (10, 200, 18) };
        foreach (var shape in new[] { "line", "vline" })
            Assert.True(Slack(GroupOutline.Silhouette(shape, discs, Padding), discs) >= Padding - Epsilon, shape);
    }

    [Theory]
    [InlineData(double.NaN, 0, 18, 6)]
    [InlineData(0, double.PositiveInfinity, 18, 6)]
    [InlineData(0, 0, double.NaN, 6)]
    [InlineData(0, 0, double.NegativeInfinity, 6)]
    [InlineData(0, 0, -1, 6)]
    public void NonFiniteOrNegativeDiscsAreRejected(double x, double y, double r, double padding)
    {
        Assert.Throws<ArgumentException>(() =>
            GroupOutline.Silhouette("heart", new[] { (x, y, r), (50.0, 50.0, 18.0) }, padding));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.5)]
    public void BadPaddingIsRejected(double padding)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GroupOutline.Silhouette("heart", new[] { (0.0, 0.0, 18.0) }, padding));
    }

    [Fact]
    public void ANullDiscListIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => GroupOutline.Silhouette("heart", null!, Padding));
    }

    // Both curves are concave, which is where a hull or a bounding-circle shortcut
    // would pass the easy cases and fail here: a disc sitting in the heart's cleft
    // or a star's notch is NOT inside by its distance to the far wall.
    [Fact]
    public void ADiscInAStarNotchIsMeasuredAgainstTheNearestWall()
    {
        var star = GroupOutline.Fit("star", Discs("star", 10, false, false), Padding);
        var notch = star.Polygon.OrderBy(p => Math.Sqrt(Math.Pow(p.X - star.Centre.X, 2) + Math.Pow(p.Y - star.Centre.Y, 2))).First();

        // The notch vertex is on the edge; a point just outside it along the
        // radial direction is outside, one just inside is inside.
        var toward = new Point(star.Centre.X + (notch.X - star.Centre.X) * 0.9, star.Centre.Y + (notch.Y - star.Centre.Y) * 0.9);
        var away = new Point(star.Centre.X + (notch.X - star.Centre.X) * 1.1, star.Centre.Y + (notch.Y - star.Centre.Y) * 1.1);
        Assert.True(GroupOutline.SignedDistanceInside(toward, star.Polygon) > 0);
        Assert.True(GroupOutline.SignedDistanceInside(away, star.Polygon) < 0);
    }

    [Fact]
    public void SignedDistanceIsPositiveInsideNegativeOutsideZeroOnTheEdge()
    {
        var square = new[] { new Point(0, 0), new Point(10, 0), new Point(10, 10), new Point(0, 10) };

        Assert.Equal(5, GroupOutline.SignedDistanceInside(new Point(5, 5), square), 12);
        Assert.Equal(2, GroupOutline.SignedDistanceInside(new Point(2, 5), square), 12);
        Assert.Equal(-3, GroupOutline.SignedDistanceInside(new Point(13, 5), square), 12);
        Assert.Equal(0, GroupOutline.SignedDistanceInside(new Point(10, 4), square), 12);
        // Past a corner the nearest thing is the corner, not either side's line.
        Assert.Equal(-5, GroupOutline.SignedDistanceInside(new Point(13, 14), square), 12);
    }

    [Fact]
    public void SignedDistanceHandlesAConcavePocket()
    {
        // A "U": the pocket between the arms is outside, and 1 from the nearest arm.
        var u = new[]
        {
            new Point(0, 0), new Point(10, 0), new Point(10, 10), new Point(7, 10),
            new Point(7, 3), new Point(3, 3), new Point(3, 10), new Point(0, 10)
        };

        Assert.Equal(-1.0, GroupOutline.SignedDistanceInside(new Point(5, 4), u), 12);
        Assert.True(GroupOutline.SignedDistanceInside(new Point(1.5, 5), u) > 0);
    }

    [Fact]
    public void SignedDistanceToADegenerateEdgeIsStillADistance()
    {
        // A repeated vertex is a zero-length edge: no division by zero, same answer.
        var poly = new[] { new Point(0, 0), new Point(0, 0), new Point(10, 0), new Point(10, 10), new Point(0, 10) };
        Assert.Equal(5, GroupOutline.SignedDistanceInside(new Point(5, 5), poly), 12);
    }

    [Fact]
    public void FewerThanThreeVerticesEncloseNothing()
    {
        Assert.Equal(double.NegativeInfinity, GroupOutline.SignedDistanceInside(new Point(0, 0), Array.Empty<Point>()));
        Assert.Equal(double.NegativeInfinity, GroupOutline.SignedDistanceInside(new Point(0, 0), new[] { new Point(0, 0), new Point(1, 1) }));
    }

    [Fact]
    public void BoundsIsTheExtentOfTheVertices()
    {
        var box = GroupOutline.Bounds(new[] { new Point(3, -2), new Point(9, 4), new Point(-1, 7) });
        Assert.Equal(new Rect(-1, -2, 10, 9), box);
    }

    [Fact]
    public void EnclosesRefusesANonPositiveScale()
    {
        var discs = new List<(double X, double Y, double R)> { (0, 0, 1) };
        Assert.False(GroupOutline.Encloses(new[] { new Point(0, 0) }, default, 0, discs, 0));
    }

    [Fact]
    public void ResultsAreDeterministic()
    {
        var discs = Discs("heart", 12, true, true);
        Assert.Equal(GroupOutline.Silhouette("heart", discs, Padding), GroupOutline.Silhouette("heart", discs, Padding));
    }

    [Fact]
    public void ThirtyDiscsFitInWellUnderAFrameBudget()
    {
        // The backdrop refits on every drag move. Not a benchmark — a tripwire
        // for the search going accidentally quadratic.
        var discs = Discs("heart", 30, true, true);
        GroupOutline.Silhouette("heart", discs, Padding);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) GroupOutline.Silhouette("heart", discs, Padding);
        Assert.True(sw.Elapsed.TotalMilliseconds / 20 < 8, $"{sw.Elapsed.TotalMilliseconds / 20} ms per call");
    }
}
