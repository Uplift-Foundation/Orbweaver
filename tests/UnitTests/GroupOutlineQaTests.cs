using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-268 QA: cases GroupOutlineTests does not reach — nested teams, one lead with
// many members, spacing extremes, and discs drawn at their true member size.
public class GroupOutlineQaTests
{
    private const double Padding = 6;

    public enum Teams { Chain, OneLeadManyMembers, TwoLevels }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var shape in new[] { "circle", "diamond", "star", "heart", "line", "vline", "grid" })
            foreach (var teams in Enum.GetValues<Teams>())
                foreach (var spacing in new[] { 0.25, 1.0, 3.0 })
                    foreach (var n in new[] { 4, 9, 20 })
                        yield return new object[] { shape, teams, spacing, n };
    }

    private static int[] LeadOf(Teams teams, int n)
    {
        var leadOf = Enumerable.Repeat(-1, n).ToArray();
        switch (teams)
        {
            case Teams.Chain:
                for (var i = 1; i < n; i++) leadOf[i] = i - 1;
                break;
            case Teams.OneLeadManyMembers:
                for (var i = 1; i < n; i++) leadOf[i] = 0;
                break;
            case Teams.TwoLevels:
                for (var i = 1; i < n; i++) leadOf[i] = i <= 3 ? 0 : (i - 1) % 3 + 1;
                break;
        }
        return leadOf;
    }

    // Discs at their TRUE drawn radius: a member is MemberScale of its lead's
    // circle, so a test that gives every disc a lead-sized radius is conservative.
    private static List<(double X, double Y, double R)> Discs(string shape, Teams teams, double spacing, int n)
    {
        var leadOf = LeadOf(teams, n);
        var layout = new OrbArrangement.Layout(new PixelRect(0, 0, 3840, 2160), 1.0, shape, spacing);
        var at = OrbArrangement.Compute(n, leadOf, layout, null);

        var window = OrbArrangement.WindowFor(1.0, 1.0);
        var discs = new List<(double, double, double)>();
        for (var i = 0; i < n; i++)
        {
            var scale = leadOf[i] >= 0 ? OrbArrangement.MemberScale : 1.0;
            discs.Add((at[i].X + window / 2.0, at[i].Y + window / 2.0, OrbArrangement.CircleDip * scale / 2));
        }
        return discs;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EnclosesRealTeamArrangements(string shape, Teams teams, double spacing, int n)
    {
        var discs = Discs(shape, teams, spacing, n);
        var outline = GroupOutline.Silhouette(shape, discs, Padding);

        var slack = discs.Min(d => GroupOutline.SignedDistanceInside(new Point(d.X, d.Y), outline) - d.R);
        Assert.True(slack >= Padding - 0.5, $"{shape} {teams} spacing={spacing} n={n}: slack {slack}");
    }

    // Negative control: the same measuring stick must FAIL for an outline that is
    // too small, or the test above proves nothing about the setup.
    [Theory]
    [InlineData("heart")]
    [InlineData("grid")]
    [InlineData("line")]
    public void ControlAShrunkOutlineFailsTheEnclosureMeasure(string shape)
    {
        var discs = Discs(shape, Teams.TwoLevels, 1.0, 9);
        var fit = GroupOutline.Fit(shape, discs, Padding);
        var shrunk = fit.Base
            .Select(p => new Point(fit.Centre.X + p.X * fit.Scale * 0.5, fit.Centre.Y + p.Y * fit.Scale * 0.5))
            .ToArray();

        var slack = discs.Min(d => GroupOutline.SignedDistanceInside(new Point(d.X, d.Y), shrunk) - d.R);
        Assert.True(slack < Padding - 0.5);
    }

    [Fact]
    public void CoincidentDiscsDoNotProduceNaN()
    {
        var discs = new List<(double X, double Y, double R)> { (100, 100, 36), (100, 100, 36), (100, 100, 26) };
        foreach (var shape in new[] { "circle", "heart", "star", "diamond", "line", "vline", "grid" })
        {
            var outline = GroupOutline.Silhouette(shape, discs, Padding);
            Assert.All(outline, p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y), shape));
            Assert.True(GroupOutline.SignedDistanceInside(new Point(100, 100), outline) >= 36 + Padding - 0.5, shape);
        }
    }

    [Fact]
    public void FarApartOutlierStillEnclosed()
    {
        var discs = new List<(double X, double Y, double R)> { (0, 0, 36), (40, 0, 36), (5000, 3000, 36) };
        foreach (var shape in new[] { "heart", "star", "line", "grid" })
        {
            var outline = GroupOutline.Silhouette(shape, discs, Padding);
            foreach (var d in discs)
                Assert.True(GroupOutline.SignedDistanceInside(new Point(d.X, d.Y), outline) >= d.R + Padding - 0.5, shape);
        }
    }
}
