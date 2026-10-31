using System.Collections.Generic;
using Avalonia;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-268 QA: edges of the 24/72 hysteresis and team measurement.
public class OrbSnapQaTests
{
    private static SnapCandidate Disc(string id, double x, double y, double r = 18) =>
        new(id, System.Array.Empty<Point>(), new[] { (x, y, r) });

    [Theory]
    [InlineData(24.0, true)]
    [InlineData(24.01, false)]
    [InlineData(0.0, true)]
    public void JoinBoundaryIsInclusive(double gap, bool joins)
    {
        var o = OrbSnap.Decide(new Point(0, 0), 18, "main", new[] { Disc("g-1", 36 + gap, 0) });
        Assert.Equal(joins, o is SnapOutcome.Join);
    }

    [Theory]
    [InlineData(72.0, false)]
    [InlineData(72.01, true)]
    [InlineData(40.0, false)]   // inside the hysteresis band: neither fires
    public void LeaveBoundaryIsExclusive(double gap, bool leaves)
    {
        var own = Disc("main", 36 + gap, 0);
        var o = OrbSnap.Decide(new Point(0, 0), 18, "main", new[] { own });
        Assert.Equal(leaves, o is SnapOutcome.Leave);
    }

    [Fact]
    public void JoinBeatsLeaveWhenBothHold()
    {
        var o = OrbSnap.Decide(new Point(0, 0), 18, "main",
            new[] { Disc("main", 500, 0), Disc("g-2", 50, 0) });
        Assert.Equal(new SnapOutcome.Join("g-2"), o);
    }

    [Fact]
    public void ATeamMemberFannedTowardsANeighbourSnapsTheWholeTeam()
    {
        var moving = new[] { (0.0, 0.0, 18.0), (200.0, 0.0, 13.0) };    // lead far from g-2, member next to it
        var o = OrbSnap.Decide(moving, "main", new[] { Disc("g-2", 200 + 13 + 10 + 18, 0), Disc("main", 0, -40) });
        Assert.Equal(new SnapOutcome.Join("g-2"), o);
    }

    [Fact]
    public void ATeamDoesNotLeaveWhileAnyDiscIsNearItsOwnGroup()
    {
        var moving = new[] { (0.0, 0.0, 18.0), (200.0, 0.0, 13.0) };
        var own = new SnapCandidate("main", System.Array.Empty<Point>(), new[] { (0.0, 60.0, 18.0), (400.0, 0.0, 18.0) });
        Assert.IsType<SnapOutcome.NoChange>(OrbSnap.Decide(moving, "main", new[] { own }));
        // control: with the near disc removed, the same team does leave
        var far = new SnapCandidate("main", System.Array.Empty<Point>(), new[] { (400.0, 0.0, 18.0) });
        Assert.IsType<SnapOutcome.Leave>(OrbSnap.Decide(moving, "main", new[] { far }));
    }

    [Fact]
    public void EqualGapsGoToTheOrdinalSmallestIdWhateverTheOrder()
    {
        var a = Disc("g-aaaaaaaa", 50, 0);
        var b = Disc("g-bbbbbbbb", -50, 0);
        Assert.Equal(new SnapOutcome.Join("g-aaaaaaaa"), OrbSnap.Decide(new Point(0, 0), 18, "main", new[] { a, b }));
        Assert.Equal(new SnapOutcome.Join("g-aaaaaaaa"), OrbSnap.Decide(new Point(0, 0), 18, "main", new[] { b, a }));
    }

    [Fact]
    public void OutlineOnlyCandidateJoinsInsideAndNearButNotFar()
    {
        var square = new[] { new Point(100, -50), new Point(200, -50), new Point(200, 50), new Point(100, 50) };
        var cand = new SnapCandidate("g-1", square, System.Array.Empty<(double, double, double)>());
        Assert.IsType<SnapOutcome.Join>(OrbSnap.Decide(new Point(150, 0), 18, "main", new[] { cand }));      // inside
        Assert.IsType<SnapOutcome.Join>(OrbSnap.Decide(new Point(100 - 18 - 20, 0), 18, "main", new[] { cand })); // 20 off
        Assert.IsType<SnapOutcome.NoChange>(OrbSnap.Decide(new Point(100 - 18 - 30, 0), 18, "main", new[] { cand })); // 30 off
    }

    [Fact]
    public void NoOtherOrbsInTheOwnGroupIsNoChange()
    {
        Assert.IsType<SnapOutcome.NoChange>(OrbSnap.Decide(new Point(0, 0), 18, "g-1",
            new[] { new SnapCandidate("g-1", System.Array.Empty<Point>(), System.Array.Empty<(double, double, double)>()) }));
    }
}

// Free-mode contract probes (CB-268 F3).
public class OrbSnapFreeModeQaTests
{
    private static readonly (double X, double Y, double R)[] Dragged = { (0, 0, 18) };

    private static SnapCandidate Loose(string root, double x) =>
        new(root, System.Array.Empty<Point>(), new[] { (x, 0.0, 18.0) }, Loose: true);

    [Fact]
    public void ALooseTeamPairsWithANearbyLooseTeam()
    {
        Assert.Equal(new SnapOutcome.Pair("s-2"),
            OrbSnap.Decide(Dragged, "main", new[] { Loose("s-2", 50) }, free: true));
    }

    [Fact]
    public void ALooseCandidateIsIgnoredWhenNotFree()
    {
        Assert.IsType<SnapOutcome.NoChange>(
            OrbSnap.Decide(Dragged, "main", new[] { Loose("s-2", 50) }, free: false));
    }

    // Contract (docs/cb268-contracts.md, S4 free mode): Pair only when the dragged team is itself loose.
    [Fact]
    public void ATeamAlreadyInAUserGroupDoesNotPairWithALooseTeam()
    {
        var own = new SnapCandidate("g-0000000a", System.Array.Empty<Point>(), new[] { (30.0, 100.0, 18.0) });
        var outcome = OrbSnap.Decide(Dragged, "g-0000000a", new[] { own, Loose("s-2", 50) }, free: true);
        Assert.IsNotType<SnapOutcome.Pair>(outcome);
    }

    [Fact]
    public void ALeaveInFreeModeIsLeaveToFreeAndNeverALeave()
    {
        var own = new SnapCandidate("g-0000000a", System.Array.Empty<Point>(), new[] { (500.0, 0.0, 18.0) });
        Assert.IsType<SnapOutcome.LeaveToFree>(OrbSnap.Decide(Dragged, "g-0000000a", new[] { own }, free: true));
        Assert.IsType<SnapOutcome.Leave>(OrbSnap.Decide(Dragged, "g-0000000a", new[] { own }, free: false));
    }

    [Fact]
    public void ALooseTeamWithNobodyNearStaysPut()
    {
        Assert.IsType<SnapOutcome.NoChange>(OrbSnap.Decide(Dragged, "main", new[] { Loose("s-2", 500) }, free: true));
    }
}
