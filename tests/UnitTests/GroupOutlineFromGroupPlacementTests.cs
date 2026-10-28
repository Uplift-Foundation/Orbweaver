using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Xunit;
using static Orbweaver.OrbArrangement;

namespace Orbweaver.UnitTests;

// CB-268: GroupOutline fed by what ComputeGroups really reports — each group's
// DrawnShape, its orbs and GroupPlacement.CurveCentre/CurveScale — rather than a
// curve this suite recovered itself (GroupOutlineOffsetTests does that). The fit
// there is within a few px of the placement (a star measured about 7.5), so these
// assert enclosure and that the outline is as small as the discs allow, not an
// exact offset.
public class GroupOutlineFromGroupPlacementTests
{
    private const double Padding = 6;

    private static (List<(double X, double Y, double R)> Discs, GroupPlacement Placement)[] Groups(
        int[] leadOf, int[] groupOf, PixelRect work, params string[] shapes)
    {
        var n = leadOf.Length;
        var table = shapes.Select((sh, i) => new GroupLayout(sh, new PixelPoint(700 + 900 * i, 700))).ToArray();
        var layout = new Layout(work, 1.0, shapes[0], 1.0);

        var (positions, groups) = ComputeGroups(n, leadOf, groupOf, table, layout);
        var w = WindowFor(1.0, 1.0);

        return groups.Select(g => (
            g.Orbs.Select(i => (
                positions[i].X + w / 2.0,
                positions[i].Y + w / 2.0,
                CircleDip * (leadOf[i] >= 0 ? MemberScale : 1.0) / 2)).ToList(),
            g)).ToArray();
    }

    public static IEnumerable<object[]> Shapes() =>
        new[] { "circle", "diamond", "star", "heart" }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Shapes))]
    public void EveryGroupIsEnclosedUsingTheReportedCurve(string shape)
    {
        // Two groups of the same shape; the first holds a team (lead 0 with members).
        var leadOf = new[] { -1, 0, 0, 0, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1 };
        var groupOf = new[] { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1 };

        var groups = Groups(leadOf, groupOf, new PixelRect(0, 0, 3024, 1890), shape, shape);
        Assert.Equal(2, groups.Length);

        foreach (var (discs, placement) in groups)
        {
            var curve = placement.CurveScale > 0
                ? new GroupOutline.PlacedCurve(placement.CurveCentre, placement.CurveScale)
                : (GroupOutline.PlacedCurve?)null;

            var fit = GroupOutline.Fit(placement.DrawnShape, discs, Padding, curve);
            var slack = discs.Min(d => GroupOutline.SignedDistanceInside(new Point(d.X, d.Y), fit.Polygon) - d.R);

            Assert.True(slack >= Padding - 1e-6, $"{shape}/{placement.DrawnShape}: slack {slack}");

            if (placement.DrawnShape == shape)
            {
                Assert.True(curve is not null);
                Assert.True(fit.Offset > 0);                     // the offset path, not the fallback
                Assert.True(slack <= Padding + 2.0, $"{shape}: not tight, slack {slack}");
            }
        }
    }

    [Fact]
    public void AGroupOfOneHasNoCurveAndStillGetsACircle()
    {
        var groups = Groups(new[] { -1 }, new[] { 0 }, new PixelRect(0, 0, 3024, 1890), "heart");

        var (discs, placement) = groups[0];
        Assert.Equal(0, placement.CurveScale);

        var outline = GroupOutline.Silhouette(placement.DrawnShape, discs, Padding,
            placement.CurveScale > 0 ? new GroupOutline.PlacedCurve(placement.CurveCentre, placement.CurveScale) : null);
        Assert.True(GroupOutline.SignedDistanceInside(new Point(discs[0].X, discs[0].Y), outline) >= discs[0].R + Padding - 1e-6);
    }

    [Fact]
    public void ASqueezedGroupDrawnAsAGridTakesTheRoundedRectangleNotACurve()
    {
        // Fifty orbs in one group on a screen that cannot hold a heart of them.
        var n = 50;
        var groups = Groups(Enumerable.Repeat(-1, n).ToArray(), new int[n], new PixelRect(0, 0, 420, 320), "heart");

        var (discs, placement) = groups[0];
        Assert.Equal("grid", placement.DrawnShape);
        var curve = placement.CurveScale > 0 ? new GroupOutline.PlacedCurve(placement.CurveCentre, placement.CurveScale) : (GroupOutline.PlacedCurve?)null;
        var fit = GroupOutline.Fit(placement.DrawnShape, discs, Padding, curve);

        Assert.Equal(0, fit.Offset);   // box shapes and fallbacks never use the offset
        Assert.True(discs.Min(d => GroupOutline.SignedDistanceInside(new Point(d.X, d.Y), fit.Polygon) - d.R) >= Padding - 1e-6);
    }

    // Hana's real output for shapes with no curve: line, vline, a grid asked for, a
    // group squeezed to a grid and a lone orb. Whatever ComputeGroups put in
    // CurveCentre/CurveScale (a lattice step, or 0), handing it to the outline
    // unfiltered must neither switch on the offset nor produce a degenerate
    // polygon: box shapes are picked on the shape, and a scale of 0 is refused.
    [Theory]
    [InlineData("line", 8, 3024)]
    [InlineData("vline", 8, 3024)]
    [InlineData("grid", 12, 3024)]
    [InlineData("heart", 50, 420)]    // squeezed: reported as "grid"
    [InlineData("heart", 1, 3024)]    // a lone orb
    public void ShapesWithNoCurveTakeTheBoxFitWhateverTheReportedScale(string shape, int n, int workWidth)
    {
        var groups = Groups(Enumerable.Repeat(-1, n).ToArray(), new int[n],
            new PixelRect(0, 0, workWidth, workWidth == 420 ? 320 : 1890), shape);

        var (discs, placement) = groups[0];

        // Not filtered on CurveScale on purpose: the outline must cope with it.
        var raw = new GroupOutline.PlacedCurve(placement.CurveCentre, placement.CurveScale);
        var fit = GroupOutline.Fit(placement.DrawnShape, discs, Padding, raw);
        var without = GroupOutline.Fit(placement.DrawnShape, discs, Padding);

        Assert.Equal(0, fit.Offset);                         // never the offset path
        Assert.Equal(without.Polygon, fit.Polygon);          // identical to having passed no curve
        Assert.All(fit.Polygon, p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y)));
        Assert.True(discs.Min(d => GroupOutline.SignedDistanceInside(new Point(d.X, d.Y), fit.Polygon) - d.R) >= Padding - 1e-6);

        if (n == 1) Assert.Equal(0, placement.CurveScale);
        if (placement.DrawnShape == "grid") Assert.True(placement.CurveScale >= 0);
    }
}
