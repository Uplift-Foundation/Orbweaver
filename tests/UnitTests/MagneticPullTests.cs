using Avalonia;
using Xunit;

namespace Orbweaver.Tests;

// CB-268 M1: the pull is up to ~30% of the remaining gap towards the target,
// easing in from the edge of join range, and never crosses the target.
public class MagneticPullTests
{
    private static SnapCandidate Disc(double x, double y, double r = 18) =>
        new("g-t", System.Array.Empty<Point>(), new[] { (x, y, r) });

    private static (double X, double Y, double R)[] Team(double x, double y, double r = 18) =>
        new[] { (x, y, r) };

    [Fact]
    public void OutOfJoinRangeThereIsNoPull()
    {
        // Gap 30 against a 24 range.
        Assert.Equal((0.0, 0.0), MagneticPull.Offset(Team(0, 0), Disc(66, 0), 24));
    }

    [Fact]
    public void AtTheEdgeOfRangeThePullIsZeroAndItEasesInAfterThat()
    {
        var edge = MagneticPull.Offset(Team(0, 0), Disc(60, 0), 24);          // gap 24
        var inside = MagneticPull.Offset(Team(0, 0), Disc(56, 0), 24);        // gap 20
        var deeper = MagneticPull.Offset(Team(0, 0), Disc(52, 0), 24);        // gap 16

        Assert.Equal(0.0, edge.X, 9);
        Assert.True(inside.X > edge.X);
        Assert.True(deeper.X > inside.X);
    }

    [Fact]
    public void ThePullIsTowardsTheTargetAndAtMostThirtyPercentOfTheGap()
    {
        for (var gap = 1.0; gap <= 24; gap += 0.5)
        {
            var (x, y) = MagneticPull.Offset(Team(0, 0), Disc(36 + gap, 0), 24);

            Assert.True(x >= 0);
            Assert.Equal(0.0, y, 9);
            Assert.True(x <= 0.30 * gap + 1e-9, $"gap {gap}: pulled {x}");
            Assert.True(x < gap, "never crosses the target");
        }
    }

    [Fact]
    public void PastTheRampDepthItIsTheFullThirtyPercent()
    {
        // Gap 12: depth 50% of range, past the 35% ramp.
        var (x, _) = MagneticPull.Offset(Team(0, 0), Disc(48, 0), 24);

        Assert.Equal(0.30 * 12, x, 9);
    }

    [Fact]
    public void ThePullPointsAlongTheLineBetweenCentres()
    {
        var (x, y) = MagneticPull.Offset(Team(0, 0), Disc(0, -48), 24);   // above, gap 12

        Assert.Equal(0.0, x, 9);
        Assert.Equal(-0.30 * 12, y, 9);
    }

    [Fact]
    public void TouchingOrOverlappingThereIsNothingLeftToClose()
    {
        Assert.Equal((0.0, 0.0), MagneticPull.Offset(Team(0, 0), Disc(36, 0), 24));   // gap 0
        Assert.Equal((0.0, 0.0), MagneticPull.Offset(Team(0, 0), Disc(10, 0), 24));   // overlapping
    }

    [Fact]
    public void ATeamIsPulledByItsNearestDiscToTheNearestTarget()
    {
        // The lead is far; a member is 16 from the target disc.
        var team = new[] { (0.0, 0.0, 18.0), (200.0, 0.0, 10.0) };
        var target = Disc(226, 0, 0);   // gap = 26 - 10 = 16

        var (x, _) = MagneticPull.Offset(team, target, 24);

        Assert.True(x > 0);
        Assert.True(x <= 0.30 * 16 + 1e-9);
    }

    [Fact]
    public void AnOutlineTargetPullsTowardsItsNearestPoint()
    {
        var square = new[] { new Point(100, 0), new Point(200, 0), new Point(200, 100), new Point(100, 100) };
        var target = new SnapCandidate("g-t", square, System.Array.Empty<(double, double, double)>());

        // Centre 10 DIP left of the wall at x=100 with radius 18 would overlap; use 40 left: gap 22 - wait 40-18 = 22.
        var (x, y) = MagneticPull.Offset(Team(60, 50), target, 24);

        Assert.True(x > 0);
        Assert.Equal(0.0, y, 9);
        Assert.True(x <= 0.30 * 22 + 1e-9);
    }

    [Fact]
    public void AnOrbInsideTheOutlineHasNothingToClose()
    {
        var square = new[] { new Point(0, 0), new Point(200, 0), new Point(200, 200), new Point(0, 200) };
        var target = new SnapCandidate("g-t", square, System.Array.Empty<(double, double, double)>());

        Assert.Equal((0.0, 0.0), MagneticPull.Offset(Team(100, 100), target, 24));
    }

    [Fact]
    public void TheNearerOfOutlineAndDiscWins()
    {
        var square = new[] { new Point(100, 0), new Point(200, 0), new Point(200, 100), new Point(100, 100) };
        var target = new SnapCandidate("g-t", square, new[] { (200.0, 400.0, 18.0) });

        var (x, y) = MagneticPull.Offset(Team(60, 50), target, 24);

        Assert.True(x > 0);
        Assert.Equal(0.0, y, 9);
    }

    [Fact]
    public void DegenerateInputsPullNothing()
    {
        Assert.Equal((0.0, 0.0), MagneticPull.Offset(System.Array.Empty<(double, double, double)>(), Disc(40, 0), 24));
        Assert.Equal((0.0, 0.0), MagneticPull.Offset(Team(0, 0), Disc(40, 0), 0));
        Assert.Equal((0.0, 0.0), MagneticPull.Offset(Team(0, 0), new SnapCandidate("g", System.Array.Empty<Point>(), System.Array.Empty<(double, double, double)>()), 24));
        // Concentric centres have no direction to pull in.
        Assert.Equal((0.0, 0.0), MagneticPull.Offset(Team(0, 0, 0), Disc(0, 0, 0), 24));
    }

    [Fact]
    public void SmoothIsZeroAtZeroOneAtOneAndClamped()
    {
        Assert.Equal(0.0, MagneticPull.Smooth(-1));
        Assert.Equal(0.0, MagneticPull.Smooth(0));
        Assert.Equal(0.5, MagneticPull.Smooth(0.5), 9);
        Assert.Equal(1.0, MagneticPull.Smooth(1));
        Assert.Equal(1.0, MagneticPull.Smooth(7));
    }

    [Fact]
    public void NonFiniteGeometryPullsNothingAndNeverNaN()
    {
        var n = double.NaN;
        Assert.Equal((0.0, 0.0), MagneticPull.Offset(Team(n, 0), Disc(40, 0), 24));
        Assert.Equal((0.0, 0.0), MagneticPull.Offset(Team(0, 0), Disc(40, n), 24));
        Assert.Equal((0.0, 0.0), MagneticPull.Offset(Team(0, 0, double.PositiveInfinity), Disc(40, 0), 24));
        Assert.Equal((0.0, 0.0), MagneticPull.Offset(Team(0, 0), Disc(40, 0), double.NaN));
        var badOutline = new SnapCandidate("g", new[] { new Point(0, 0), new Point(n, 5), new Point(9, 9) }, System.Array.Empty<(double, double, double)>());
        Assert.Equal((0.0, 0.0), MagneticPull.Offset(Team(-50, 0), badOutline, 24));
    }
}
