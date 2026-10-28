using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-268: the outline as the OUTWARD OFFSET of the curve the orbs were placed on.
//
// The discs come from the real OrbArrangement.Compute, and so does the curve: the
// placed curve (centre and scale of unit (0,0)) is recovered from where Compute
// put the anchors, by least squares against OrbArrangement.Unit, and asserted to
// reproduce them within integer rounding. Once ComputeGroups reports
// GroupPlacement.CurveCentre/CurveScale directly, the recovery goes away.
public class GroupOutlineOffsetTests
{
    private const double Padding = 6;
    private static readonly string[] Curves = { "circle", "diamond", "star", "heart" };

    private static (List<(double X, double Y, double R)> Discs, GroupOutline.PlacedCurve Curve, int[] Anchors)
        Arrange(string shape, int[] leadOf, double[]? sizeOf = null) => TryArrange(shape, leadOf, sizeOf)!.Value;

    // Null when the arrangement did not actually draw `shape` on a curve: fewer
    // than three anchors have no curve to speak of, and a squeezed arrangement
    // falls back to a grid. Those are the cases ComputeGroups reports no placed
    // curve for, and they take the old fit — covered separately below.
    private static (List<(double X, double Y, double R)> Discs, GroupOutline.PlacedCurve Curve, int[] Anchors)?
        TryArrange(string shape, int[] leadOf, double[]? sizeOf = null)
    {
        var n = leadOf.Length;
        var layout = new OrbArrangement.Layout(new PixelRect(0, 0, 3024, 1890), 1.0, shape, 1.0);
        var at = OrbArrangement.Compute(n, leadOf, layout, sizeOf);

        var discs = new List<(double, double, double)>();
        for (var i = 0; i < n; i++)
        {
            var size = sizeOf?[i] ?? 1.0;
            var w = OrbArrangement.WindowFor(1.0, size);
            var member = leadOf[i] >= 0 ? OrbArrangement.MemberScale : 1.0;
            discs.Add((at[i].X + w / 2.0, at[i].Y + w / 2.0, OrbArrangement.CircleDip * size * member / 2));
        }

        var anchors = Enumerable.Range(0, n).Where(i => leadOf[i] < 0).ToArray();
        if (anchors.Length < 3) return null;
        var unit = OrbArrangement.Unit(shape, anchors.Length);

        double ux = unit.Average(u => u.X), uy = unit.Average(u => u.Y);
        double px = anchors.Average(i => discs[i].Item1), py = anchors.Average(i => discs[i].Item2);
        double num = 0, den = 0;
        for (var k = 0; k < anchors.Length; k++)
        {
            num += (unit[k].X - ux) * (discs[anchors[k]].Item1 - px) + (unit[k].Y - uy) * (discs[anchors[k]].Item2 - py);
            den += Math.Pow(unit[k].X - ux, 2) + Math.Pow(unit[k].Y - uy, 2);
        }
        var scale = num / den;
        var centre = new Point(px - scale * ux, py - scale * uy);

        for (var k = 0; k < anchors.Length; k++)
        {
            var d = discs[anchors[k]];
            if (Math.Abs(centre.X + scale * unit[k].X - d.Item1) >= 1.5 ||
                Math.Abs(centre.Y + scale * unit[k].Y - d.Item2) >= 1.5)
                return null;
        }

        return (discs, new GroupOutline.PlacedCurve(centre, scale), anchors);
    }

    private static Point[] CurvePolyline(string shape, GroupOutline.PlacedCurve c)
    {
        Func<double, (double X, double Y)> at = shape switch
        {
            "circle" => OrbArrangement.CircleAt,
            "diamond" => OrbArrangement.DiamondAt,
            "star" => OrbArrangement.StarAt,
            _ => OrbArrangement.HeartAt
        };
        return Enumerable.Range(0, 720).Select(i =>
        {
            var (x, y) = at(2 * Math.PI * i / 720);
            return new Point(c.Centre.X + x * c.Scale, c.Centre.Y + y * c.Scale);
        }).ToArray();
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var shape in Curves)
            foreach (var n in new[] { 5, 8, 12, 30 })
                foreach (var mixed in new[] { false, true })
                    if (TryArrange(shape, Enumerable.Repeat(-1, n).ToArray(), Sizes(n, mixed)) is not null)
                        yield return new object[] { shape, n, mixed };
    }

    // The filter above must not quietly empty the suite.
    [Fact]
    public void EveryCurveShapeHasRecoverableCasesAtTheSizesThatMatter()
    {
        foreach (var shape in Curves)
            foreach (var n in new[] { 5, 8, 12 })
                Assert.NotNull(TryArrange(shape, Enumerable.Repeat(-1, n).ToArray()));

        Assert.True(Cases().Count() >= 20, $"{Cases().Count()} cases");
        Assert.True(Teams().Count() >= 6, $"{Teams().Count()} team cases");
    }

    private static double[]? Sizes(int n, bool mixed) =>
        mixed ? Enumerable.Range(0, n).Select(i => 0.6 + 1.4 * (i % 5) / 4.0).ToArray() : null;

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryDiscIsEnclosedByItsRadiusPlusPadding(string shape, int n, bool mixed)
    {
        var (discs, curve, _) = Arrange(shape, Enumerable.Repeat(-1, n).ToArray(), Sizes(n, mixed));
        var outline = GroupOutline.Silhouette(shape, discs, Padding, curve);

        Assert.Equal(240, outline.Length);
        foreach (var d in discs)
            Assert.True(GroupOutline.SignedDistanceInside(new Point(d.X, d.Y), outline) >= d.R + Padding - 1e-6,
                $"{shape} n={n} mixed={mixed}");
    }

    // The invariant that makes it hug: every point of the outline is the same
    // distance from the placed curve, the offset, and that offset is just the
    // largest orb plus the padding. (Not "within reach of some disc": with orbs a
    // few diameters apart a point between two of them is rightly farther than that
    // from either.)
    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryPointOfTheOutlineIsOneOffsetFromThePlacedCurve(string shape, int n, bool mixed)
    {
        var (discs, curve, _) = Arrange(shape, Enumerable.Repeat(-1, n).ToArray(), Sizes(n, mixed));
        var fit = GroupOutline.Fit(shape, discs, Padding, curve);
        var line = CurvePolyline(shape, curve);

        // Orbs sit on whole pixels, up to a pixel off the true curve, so the offset
        // can differ from the ideal by that much either way (a disc a hair inside the
        // curve needs a hair less).
        Assert.InRange(fit.Offset, discs.Max(d => d.R) + Padding - 1.0, discs.Max(d => d.R) + Padding + 1.5);

        foreach (var p in fit.Polygon)
        {
            var away = -GroupOutline.SignedDistanceInside(p, line);   // positive outside the curve
            Assert.True(Math.Abs(away - fit.Offset) <= 0.75,
                $"{shape} n={n}: a point is {away:F2} from the curve, offset {fit.Offset:F2}");
        }
    }

    // Negative control: the same discs fitted the old way (a scaled copy of the
    // curve) must FAIL that invariant for the heart, or the test above would be
    // passing for a reason that has nothing to do with hugging.
    [Fact]
    public void TheOldFitDoesNotHugTheHeart()
    {
        var (discs, curve, _) = Arrange("heart", Enumerable.Repeat(-1, 10).ToArray());
        var offsetFit = GroupOutline.Fit("heart", discs, Padding, curve);
        var oldFit = GroupOutline.Fit("heart", discs, Padding);
        var line = CurvePolyline("heart", curve);

        double Worst(Point[] poly) => poly.Max(p => Math.Abs(-GroupOutline.SignedDistanceInside(p, line) - offsetFit.Offset));

        Assert.True(Worst(oldFit.Polygon) > 3 * offsetFit.Offset, $"old fit worst {Worst(oldFit.Polygon):F1}");
        Assert.True(Worst(offsetFit.Polygon) <= 0.75);

        // And it is much smaller: the point of the change.
        var a = GroupOutline.Bounds(oldFit.Polygon);
        var b = GroupOutline.Bounds(offsetFit.Polygon);
        Assert.True(b.Width * b.Height < a.Width * a.Height * 0.6, $"{b} vs {a}");
    }

    [Fact]
    public void TheHeartsCleftIsFilledWithARoundedDipNotFollowedIn()
    {
        var (discs, curve, _) = Arrange("heart", Enumerable.Repeat(-1, 12).ToArray());
        var fit = GroupOutline.Fit("heart", discs, Padding, curve);
        var line = CurvePolyline("heart", curve);

        // The cusp of the curve is a point the outline passes well above: the
        // outline over it is at least `offset` higher, never dipping to the cusp.
        var cusp = new Point(curve.Centre.X, curve.Centre.Y - 5 * curve.Scale);
        var over = fit.Polygon.Where(p => Math.Abs(p.X - cusp.X) < 2 && p.Y < cusp.Y).ToArray();

        Assert.NotEmpty(over);
        Assert.True(over.Max(p => p.Y) <= cusp.Y - fit.Offset + 0.75);
    }

    public static IEnumerable<object[]> Teams()
    {
        foreach (var shape in Curves)
            foreach (var (name, leadOf) in new (string, int[])[]
            {
                ("one team", new[] { -1, 0, 0, 0, 0, 0 }),
                ("several teams", new[] { -1, 0, 0, -1, 3, 3, -1, 6, -1, -1 }),
                ("big team", new[] { -1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -1, -1, -1 }),
                ("team plus solos", new[] { -1, -1, 1, 1, 1, -1, -1, 6, 6, -1, -1, -1 }),
            })
                if (TryArrange(shape, leadOf) is not null)
                    yield return new object[] { shape, name, leadOf };
    }

    // Fanned members sit off the curve, so the offset swells to hold them: still
    // enclosed, and tight — the discs that set the offset touch the padding.
    [Theory]
    [MemberData(nameof(Teams))]
    public void FannedTeamMembersAreEnclosedAndTheOffsetIsNoBiggerThanNeeded(string shape, string name, int[] leadOf)
    {
        var (discs, curve, _) = Arrange(shape, leadOf);
        var fit = GroupOutline.Fit(shape, discs, Padding, curve);

        var slack = discs.Min(d => GroupOutline.SignedDistanceInside(new Point(d.X, d.Y), fit.Polygon) - d.R);

        Assert.True(slack >= Padding - 1e-6, $"{shape}/{name}: slack {slack}");
        Assert.True(slack <= Padding + 0.75, $"{shape}/{name}: not tight, slack {slack}");
        Assert.True(fit.Offset >= discs.Max(d => d.R) + Padding - 1.0);
    }

    [Fact]
    public void ADiscFarOffTheCurveSwellsTheOffsetByExactlyWhatItNeeds()
    {
        var (discs, curve, _) = Arrange("circle", Enumerable.Repeat(-1, 8).ToArray());
        var baseline = GroupOutline.Fit("circle", discs, Padding, curve).Offset;

        var far = new List<(double X, double Y, double R)>(discs) { (curve.Centre.X + 10 * curve.Scale * 3, curve.Centre.Y, 13) };
        var fit = GroupOutline.Fit("circle", far, Padding, curve);

        Assert.True(fit.Offset > baseline + 50);
        Assert.True(GroupOutline.SignedDistanceInside(new Point(far[^1].X, far[^1].Y), fit.Polygon) >= 13 + Padding - 1e-6);
    }

    // ---- falling back ------------------------------------------------------------

    [Fact]
    public void WithoutAPlacedCurveTheFitIsTheOldOne()
    {
        var (discs, _, _) = Arrange("heart", Enumerable.Repeat(-1, 10).ToArray());
        Assert.Equal(GroupOutline.Silhouette("heart", discs, Padding), GroupOutline.Silhouette("heart", discs, Padding, null));
        Assert.Equal(0, GroupOutline.Fit("heart", discs, Padding).Offset);
    }

    [Theory]
    [InlineData("line")]
    [InlineData("vline")]
    [InlineData("grid")]
    public void BoxShapesIgnoreAPlacedCurve(string shape)
    {
        var (discs, _, _) = Arrange(shape, Enumerable.Repeat(-1, 6).ToArray());
        var bogus = new GroupOutline.PlacedCurve(new Point(1, 2), 3);
        Assert.Equal(GroupOutline.Silhouette(shape, discs, Padding), GroupOutline.Silhouette(shape, discs, Padding, bogus));
    }

    [Theory]
    [InlineData(double.NaN, 5.0)]
    [InlineData(100.0, 0.0)]
    [InlineData(100.0, -2.0)]
    [InlineData(100.0, double.PositiveInfinity)]
    public void AnUnusableCurveFallsBackToTheOldFit(double cx, double scale)
    {
        var (discs, _, _) = Arrange("heart", Enumerable.Repeat(-1, 10).ToArray());
        var bad = new GroupOutline.PlacedCurve(new Point(cx, 100), scale);

        Assert.Equal(GroupOutline.Silhouette("heart", discs, Padding), GroupOutline.Silhouette("heart", discs, Padding, bad));
    }

    [Fact]
    public void ANonFiniteCentreYIsAlsoUnusable()
    {
        var (discs, _, _) = Arrange("heart", Enumerable.Repeat(-1, 10).ToArray());
        var bad = new GroupOutline.PlacedCurve(new Point(100, double.NaN), 5);
        Assert.Equal(GroupOutline.Silhouette("heart", discs, Padding), GroupOutline.Silhouette("heart", discs, Padding, bad));
    }

    [Fact]
    public void ASingleDiscIsStillACircleWhateverTheCurve()
    {
        var one = new List<(double X, double Y, double R)> { (50, 60, 18) };
        var curve = new GroupOutline.PlacedCurve(new Point(50, 60), 10);
        Assert.Equal(GroupOutline.Silhouette("heart", one, Padding), GroupOutline.Silhouette("heart", one, Padding, curve));
    }

    [Fact]
    public void DiscsDeepInsideTheCurveDoNotShrinkTheOffsetBelowPadding()
    {
        var curve = new GroupOutline.PlacedCurve(new Point(0, 0), 20);
        var inner = new List<(double X, double Y, double R)> { (0, 0, 2), (3, 3, 2) };
        Assert.Equal(Padding, GroupOutline.Fit("circle", inner, Padding, curve).Offset);
    }
}
