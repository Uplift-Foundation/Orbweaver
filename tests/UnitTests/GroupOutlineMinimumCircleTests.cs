using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-268: the minimum enclosing circle of a set of discs, which is what the Pair
// preview draws around two teams. A circle centred on the window or on the
// centroid, scaled until it holds everything, is bigger than it needs to be and
// off-centre on the orbs; this is the smallest one that holds them.
public class GroupOutlineMinimumCircleTests
{
    private const double Padding = 6;

    private static double Reach(Point c, IEnumerable<(double X, double Y, double R)> discs) =>
        discs.Max(d => Math.Sqrt((c.X - d.X) * (c.X - d.X) + (c.Y - d.Y) * (c.Y - d.Y)) + d.R);

    // ---- known answers ------------------------------------------------------------

    [Fact]
    public void TwoDiscsAreEnclosedByTheCircleThroughTheirFarSides()
    {
        var (c, r) = GroupOutline.MinimumEnclosingCircle(new[] { (0.0, 0.0, 10.0), (100.0, 0.0, 20.0) });

        Assert.Equal(65, r, 6);              // (100 + 10 + 20) / 2
        Assert.Equal(55, c.X, 5);            // 65 - 10 from the first disc
        Assert.Equal(0, c.Y, 5);
    }

    [Fact]
    public void ThreeEqualDiscsAtTheCornersOfAnEquilateralTriangle()
    {
        var side = 120.0;
        var h = side * Math.Sqrt(3) / 2;
        var (c, r) = GroupOutline.MinimumEnclosingCircle(new[]
        {
            (0.0, 0.0, 15.0), (side, 0.0, 15.0), (side / 2, h, 15.0)
        });

        Assert.Equal(side / Math.Sqrt(3) + 15, r, 5);
        Assert.Equal(side / 2, c.X, 4);
        Assert.Equal(h / 3, c.Y, 4);
    }

    [Fact]
    public void ADiscThatSwallowsTheOthersIsItsOwnCircle()
    {
        var (c, r) = GroupOutline.MinimumEnclosingCircle(new[] { (0.0, 0.0, 100.0), (10.0, 5.0, 5.0), (-20.0, 0.0, 8.0) });

        Assert.Equal(100, r, 5);
        Assert.Equal(0, c.X, 3);
        Assert.Equal(0, c.Y, 3);
    }

    [Fact]
    public void OneDiscIsItselfAndNoDiscsIsNothing()
    {
        var (c, r) = GroupOutline.MinimumEnclosingCircle(new[] { (40.0, 50.0, 18.0) });
        Assert.Equal(18, r, 6);
        Assert.Equal(40, c.X, 6);
        Assert.Equal(50, c.Y, 6);

        Assert.Equal((default(Point), 0.0), GroupOutline.MinimumEnclosingCircle(Array.Empty<(double, double, double)>()));
    }

    // ---- properties, on random sets ---------------------------------------------------

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 12).Select(i => new object[] { i });

    private static List<(double X, double Y, double R)> RandomDiscs(int seed)
    {
        var rng = new Random(seed);
        var n = 2 + rng.Next(10);
        return Enumerable.Range(0, n)
            .Select(_ => (rng.NextDouble() * 600, rng.NextDouble() * 400, 8 + rng.NextDouble() * 20))
            .ToList();
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void EveryDiscIsInsideAndAtLeastTwoTouchTheBoundary(int seed)
    {
        var discs = RandomDiscs(seed);
        var (c, r) = GroupOutline.MinimumEnclosingCircle(discs);

        var gaps = discs.Select(d => r - (Math.Sqrt((c.X - d.X) * (c.X - d.X) + (c.Y - d.Y) * (c.Y - d.Y)) + d.R)).ToList();

        Assert.All(gaps, g => Assert.True(g >= -1e-6, $"a disc pokes out by {-g}"));

        // At least two discs touch; a single disc that swallows the rest is the one
        // exception, and then it is the one touching.
        var touching = gaps.Count(g => g <= 1e-4);
        var swallowed = discs.Any(d => Math.Abs(d.R - r) < 1e-4);
        Assert.True(touching >= (swallowed ? 1 : 2), $"seed {seed}: {touching} touching");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void NoOtherCentreDoesBetter(int seed)
    {
        var discs = RandomDiscs(seed);
        var (c, r) = GroupOutline.MinimumEnclosingCircle(discs);

        var rng = new Random(seed * 7);
        for (var i = 0; i < 2000; i++)
        {
            var spread = i < 1000 ? 3.0 : 60.0;
            var other = new Point(c.X + (rng.NextDouble() - 0.5) * spread, c.Y + (rng.NextDouble() - 0.5) * spread);
            Assert.True(Reach(other, discs) >= r - 1e-6, $"seed {seed}: a better centre exists");
        }
    }

    // ---- the outline ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TheOutlineEnclosesEveryDiscByItsRadiusPlusPaddingAndTouchesTwo(int seed)
    {
        var discs = RandomDiscs(seed);
        var outline = GroupOutline.Silhouette(GroupOutline.MinimumCircleShape, discs, Padding);

        Assert.Equal(360, outline.Length);

        var slack = discs.Select(d => GroupOutline.SignedDistanceInside(new Point(d.X, d.Y), outline) - d.R - Padding).ToList();

        Assert.All(slack, s => Assert.True(s >= -1e-6, $"seed {seed}: short by {-s}"));
        var touching = slack.Count(s => s <= 0.05);
        var swallowed = discs.Any(d => Math.Abs(d.R - GroupOutline.MinimumEnclosingCircle(discs).Radius) < 1e-4);
        Assert.True(touching >= (swallowed ? 1 : 2), $"seed {seed}: {touching} touch the padded boundary");
    }

    // The case that was returned: a team of three and a solo orb, with the right-hand
    // orb far out. The old circle (centred on the discs' centroid and scaled until
    // it holds them) is the negative control: it must be visibly bigger and must NOT
    // touch two discs, or these assertions would pass for any circle at all.
    private static readonly (double X, double Y, double R)[] TwoTeams =
    {
        (188, 188, 18), (128, 278, 12.96), (248, 278, 12.96), (428, 218, 18)
    };

    [Fact]
    public void TheMinimumCircleHugsTwoTeamsWhereTheOldCircleDoesNot()
    {
        var min = GroupOutline.Silhouette(GroupOutline.MinimumCircleShape, TwoTeams, Padding);
        var old = GroupOutline.Silhouette("circle", TwoTeams, Padding);

        double Radius(Point[] poly) => GroupOutline.Bounds(poly).Width / 2;
        int Touching(Point[] poly) => TwoTeams.Count(d =>
            GroupOutline.SignedDistanceInside(new Point(d.X, d.Y), poly) - d.R - Padding <= 0.05);

        Assert.True(Touching(min) >= 2, $"minimum touches {Touching(min)}");
        Assert.True(Radius(min) < Radius(old) * 0.9, $"{Radius(min):F1} vs old {Radius(old):F1}");
        Assert.True(Touching(old) < 2, $"the old circle touched {Touching(old)}: it was not a useful control");

        // And it is centred on the orbs, not between the extremes of the window: the
        // far right orb and the far left one are about equally close to the edge.
        var box = GroupOutline.Bounds(min);
        var right = box.Right - (428 + 18);
        var left = (128 - 12.96) - box.Left;
        Assert.True(Math.Abs(right - left) < 40, $"margins {left:F1} vs {right:F1}");
    }

    [Fact]
    public void ASingleDiscAndNoDiscsStillWorkAsAMinimumCircle()
    {
        var one = GroupOutline.Silhouette(GroupOutline.MinimumCircleShape, new[] { (100.0, 100.0, 18.0) }, Padding);
        Assert.True(GroupOutline.SignedDistanceInside(new Point(100, 100), one) >= 24 - 1e-6);

        Assert.Empty(GroupOutline.Silhouette(GroupOutline.MinimumCircleShape, Array.Empty<(double, double, double)>(), Padding));
    }

    [Fact]
    public void CoincidentDiscsGiveTheLargerOnesCircle()
    {
        var (c, r) = GroupOutline.MinimumEnclosingCircle(new[] { (50.0, 50.0, 10.0), (50.0, 50.0, 10.0), (50.0, 50.0, 14.0) });
        Assert.Equal(14, r, 5);
        Assert.Equal(50, c.X, 3);
    }
}
