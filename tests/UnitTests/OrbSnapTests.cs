using Avalonia;
using Xunit;

namespace Orbweaver.Tests;

// CB-268 S4: the join/leave decision. Pure, in DIP. Every outcome, both
// thresholds, the hysteresis band between them, and a dragged team measured by
// all of its discs rather than only the one the pointer is on.
public class OrbSnapTests
{
    private static readonly Point Origin = new(0, 0);

    private static SnapCandidate Group(string id, params (double X, double Y, double R)[] discs) =>
        new(id, System.Array.Empty<Point>(), discs);

    // A 200 x 200 square outline around (0..200), for the from-outside cases.
    private static readonly Point[] Square =
        { new(0, 0), new(200, 0), new(200, 200), new(0, 200) };

    // --- Join ---

    [Fact]
    public void AnOrbWithinJoinDistanceOfAnotherGroupJoinsIt()
    {
        // Edge to edge: 100 - 18 - 18 = 64 for a; 60 - 36 = 24 for b — exactly the threshold.
        var outcome = OrbSnap.Decide(Origin, 18, "main", new[]
        {
            Group("main", (500, 0, 18)),
            Group("g-b", (60, 0, 18)),
        });

        Assert.Equal(new SnapOutcome.Join("g-b"), outcome);
    }

    [Fact]
    public void JustOutsideJoinDistanceDoesNotJoin()
    {
        var outcome = OrbSnap.Decide(Origin, 18, "main", new[]
        {
            Group("main", (50, 0, 18)),
            Group("g-b", (60.5, 0, 18)),
        });

        Assert.IsType<SnapOutcome.NoChange>(outcome);
    }

    [Fact]
    public void TheNearestOfSeveralGroupsWins()
    {
        var outcome = OrbSnap.Decide(Origin, 18, "main", new[]
        {
            Group("g-far", (60, 0, 18)),
            Group("g-near", (45, 0, 18)),
        });

        Assert.Equal(new SnapOutcome.Join("g-near"), outcome);
    }

    [Fact]
    public void TiesGoToTheOrdinalSmallestId()
    {
        var outcome = OrbSnap.Decide(Origin, 18, "main", new[]
        {
            Group("g-b", (50, 0, 18)),
            Group("g-a", (0, 50, 18)),
            Group("g-c", (-50, 0, 18)),
        });

        Assert.Equal(new SnapOutcome.Join("g-a"), outcome);
    }

    [Fact]
    public void AFartherGroupListedAfterTheNearestDoesNotReplaceIt()
    {
        var outcome = OrbSnap.Decide(Origin, 18, "main", new[]
        {
            Group("g-near", (45, 0, 18)),
            Group("g-far", (60, 0, 18)),
        });

        Assert.Equal(new SnapOutcome.Join("g-near"), outcome);
    }

    [Fact]
    public void TheCurrentGroupIsNeverJoined()
    {
        var outcome = OrbSnap.Decide(Origin, 18, "main", new[] { Group("main", (40, 0, 18)) });

        Assert.IsType<SnapOutcome.NoChange>(outcome);
    }

    [Fact]
    public void OverlappingADiscCountsAsAGapOfZero()
    {
        var outcome = OrbSnap.Decide(Origin, 18, "main", new[] { Group("g-b", (5, 0, 18)) });

        Assert.Equal(new SnapOutcome.Join("g-b"), outcome);
    }

    [Fact]
    public void JoinBeatsLeave()
    {
        // Far from everything in its own group, but right next to another group.
        var outcome = OrbSnap.Decide(Origin, 18, "main", new[]
        {
            Group("main", (1000, 0, 18)),
            Group("g-b", (50, 0, 18)),
        });

        Assert.Equal(new SnapOutcome.Join("g-b"), outcome);
    }

    // --- outlines ---

    [Fact]
    public void AnOrbInsideAnOutlineJoinsItEvenAwayFromEveryDisc()
    {
        var group = new SnapCandidate("g-b", Square, new[] { (190.0, 190.0, 10.0) });

        var outcome = OrbSnap.Decide(new Point(20, 20), 18, "main", new[] { group });

        Assert.Equal(new SnapOutcome.Join("g-b"), outcome);
    }

    [Fact]
    public void AnOrbJustOutsideAnOutlineJoinsWithinTheThreshold()
    {
        // Centre 40 left of the wall, radius 18: gap 22.
        var group = new SnapCandidate("g-b", Square, new[] { (190.0, 190.0, 10.0) });

        Assert.Equal(
            new SnapOutcome.Join("g-b"),
            OrbSnap.Decide(new Point(-40, 100), 18, "main", new[] { group }));
        // 50 left: gap 32.
        Assert.IsType<SnapOutcome.NoChange>(
            OrbSnap.Decide(new Point(-50, 100), 18, "main", new[] { group }));
    }

    [Fact]
    public void AnEmptyOutlineFallsBackToTheDiscs()
    {
        var group = new SnapCandidate("g-b", System.Array.Empty<Point>(), new[] { (50.0, 0.0, 18.0) });

        Assert.Equal(new SnapOutcome.Join("g-b"), OrbSnap.Decide(Origin, 18, "main", new[] { group }));
    }

    [Fact]
    public void ADegenerateTwoPointOutlineIsIgnored()
    {
        var group = new SnapCandidate("g-b", new[] { new Point(0, 0), new Point(1, 1) }, new[] { (500.0, 0.0, 18.0) });

        Assert.IsType<SnapOutcome.NoChange>(OrbSnap.Decide(Origin, 18, "main", new[] { group }));
    }

    // --- Leave and hysteresis ---

    [Fact]
    public void FartherThanLeaveDistanceFromEveryOtherDiscLeaves()
    {
        // 100 - 36 = 64 < 72 ...
        Assert.IsType<SnapOutcome.NoChange>(
            OrbSnap.Decide(Origin, 18, "main", new[] { Group("main", (100, 0, 18)) }));
        // ... 120 - 36 = 84 > 72.
        Assert.IsType<SnapOutcome.Leave>(
            OrbSnap.Decide(Origin, 18, "main", new[] { Group("main", (120, 0, 18)) }));
    }

    [Fact]
    public void ExactlyLeaveDistanceStays()
    {
        Assert.IsType<SnapOutcome.NoChange>(
            OrbSnap.Decide(Origin, 18, "main", new[] { Group("main", (108, 0, 18)) }));
    }

    [Fact]
    public void LeavingNeedsEveryOtherDiscToBeFar()
    {
        var outcome = OrbSnap.Decide(Origin, 18, "main", new[]
        {
            Group("main", (500, 0, 18), (0, 90, 18)),
        });

        Assert.IsType<SnapOutcome.NoChange>(outcome);
    }

    [Fact]
    public void TheBandBetweenTheThresholdsChangesNothing()
    {
        // 40..70 edge to edge from its own group, 100+ from the other: neither fires.
        foreach (var x in new[] { 76.0, 90.0, 105.0 })
        {
            var outcome = OrbSnap.Decide(Origin, 18, "main", new[]
            {
                Group("main", (x, 0, 18)),
                Group("g-b", (-300, 0, 18)),
            });
            Assert.IsType<SnapOutcome.NoChange>(outcome);
        }
    }

    [Fact]
    public void TheOnlyOrbInItsGroupHasNothingToLeave()
    {
        Assert.IsType<SnapOutcome.NoChange>(OrbSnap.Decide(Origin, 18, "main", new[] { Group("main") }));
    }

    [Fact]
    public void AGroupMissingFromTheCandidatesHasNothingToLeave()
    {
        Assert.IsType<SnapOutcome.NoChange>(OrbSnap.Decide(Origin, 18, "main", new[] { Group("g-b", (500, 0, 18)) }));
        Assert.IsType<SnapOutcome.NoChange>(OrbSnap.Decide(Origin, 18, "main", System.Array.Empty<SnapCandidate>()));
    }

    [Fact]
    public void CustomThresholdsApply()
    {
        var other = new[] { Group("g-b", (100, 0, 18)) };

        Assert.IsType<SnapOutcome.NoChange>(OrbSnap.Decide(Origin, 18, "main", other));
        Assert.Equal(new SnapOutcome.Join("g-b"), OrbSnap.Decide(Origin, 18, "main", other, joinDip: 70));
        Assert.IsType<SnapOutcome.Leave>(
            OrbSnap.Decide(Origin, 18, "main", new[] { Group("main", (100, 0, 18)) }, leaveDip: 10));
    }

    // --- teams (T2) ---

    [Fact]
    public void AMemberFannedTowardsAnotherGroupSnapsTheWholeTeam()
    {
        // The lead is far from g-b, but a member sits right next to it.
        var team = new[] { (0.0, 0.0, 18.0), (230.0, 0.0, 10.0) };

        var outcome = OrbSnap.Decide(team, "main", new[]
        {
            Group("main", (-400, 0, 18)),
            Group("g-b", (270, 0, 18)),
        });

        Assert.Equal(new SnapOutcome.Join("g-b"), outcome);
    }

    [Fact]
    public void ATeamLeavesOnlyWhenEveryDiscOfItIsFar()
    {
        var own = Group("main", (200, 0, 18));

        // Lead 164 away, but a member is 40 away: stays.
        Assert.IsType<SnapOutcome.NoChange>(
            OrbSnap.Decide(new[] { (0.0, 0.0, 18.0), (150.0, 0.0, 10.0) }, "main", new[] { own }));
        // Everyone 164+ away: leaves.
        Assert.IsType<SnapOutcome.Leave>(
            OrbSnap.Decide(new[] { (0.0, 0.0, 18.0), (-50.0, 0.0, 10.0) }, "main", new[] { own }));
    }

    [Fact]
    public void ADraggedTeamWithNoDiscsDecidesNothing()
    {
        Assert.IsType<SnapOutcome.NoChange>(
            OrbSnap.Decide(System.Array.Empty<(double, double, double)>(), "main", new[] { Group("g-b", (0, 0, 18)) }));
    }

    // --- the gap itself ---

    [Fact]
    public void GapToTakesTheSmallerOfOutlineAndDisc()
    {
        var group = new SnapCandidate("g", Square, new[] { (-100.0, 100.0, 10.0) });
        var moving = new[] { (-40.0, 100.0, 18.0) };

        // Outline: 40 - 18 = 22. Disc: 60 - 28 = 32.
        Assert.Equal(22, OrbSnap.GapTo(moving, group), 6);
    }

    // --- free mode (F3) ---

    private static SnapCandidate Loose(string id, params (double X, double Y, double R)[] discs) =>
        new(id, System.Array.Empty<Point>(), discs, Loose: true);

    private static (double X, double Y, double R)[] One(double x = 0, double y = 0, double r = 18) =>
        new[] { (x, y, r) };

    [Fact]
    public void ALooseCandidateWithinJoinDistanceIsPairedNotJoined()
    {
        var outcome = OrbSnap.Decide(One(), "main", new[] { Loose("s-1", (50, 0, 18)) }, free: true);

        Assert.Equal(new SnapOutcome.Pair("s-1"), outcome);
    }

    [Fact]
    public void ALooseCandidateJustOutsideJoinDistanceIsLeftAlone()
    {
        Assert.IsType<SnapOutcome.NoChange>(
            OrbSnap.Decide(One(), "main", new[] { Loose("s-1", (60.5, 0, 18)) }, free: true));
    }

    [Fact]
    public void ANearerUserGroupBeatsAFartherLooseOrbAndTheOtherWayRound()
    {
        var group = Group("g-a", (45, 0, 18));
        var far = Loose("s-1", (60, 0, 18));

        Assert.Equal(new SnapOutcome.Join("g-a"), OrbSnap.Decide(One(), "main", new[] { far, group }, free: true));

        var near = Loose("s-2", (45, 0, 18));
        var farGroup = Group("g-b", (60, 0, 18));
        Assert.Equal(new SnapOutcome.Pair("s-2"), OrbSnap.Decide(One(), "main", new[] { farGroup, near }, free: true));
    }

    [Fact]
    public void TiesBetweenALooseTeamAndAGroupGoToTheOrdinalSmallestId()
    {
        var outcome = OrbSnap.Decide(One(), "main", new[]
        {
            Loose("s-9", (50, 0, 18)),
            Group("g-1", (0, 50, 18)),
        }, free: true);

        Assert.Equal(new SnapOutcome.Join("g-1"), outcome);
    }

    [Fact]
    public void LeavingAUserGroupInFreeModeIsLeaveToFreeNotLeave()
    {
        var own = Group("g-own", (200, 0, 18));

        Assert.IsType<SnapOutcome.LeaveToFree>(OrbSnap.Decide(One(), "g-own", new[] { own }, free: true));
        Assert.IsType<SnapOutcome.Leave>(OrbSnap.Decide(One(), "g-own", new[] { own }, free: false));
    }

    [Fact]
    public void FreeModeHasHysteresisToo()
    {
        // Inside the band between the thresholds nothing fires, in either mode.
        var own = Group("g-own", (100, 0, 18));   // 64 dip edge to edge: under 72

        Assert.IsType<SnapOutcome.NoChange>(OrbSnap.Decide(One(), "g-own", new[] { own }, free: true));
    }

    [Fact]
    public void ALooseTeamHasNoGroupToLeaveSoWanderingAwayChangesNothing()
    {
        // Free mode leaves `main` out of the candidates, so a loose orb has no
        // "own" group: far from everything it simply stays loose.
        Assert.IsType<SnapOutcome.NoChange>(
            OrbSnap.Decide(One(), "main", new[] { Loose("s-1", (900, 0, 18)) }, free: true));
    }

    [Fact]
    public void ATeamMemberFannedTowardsALooseOrbPairsTheWholeTeam()
    {
        var team = new[] { (0.0, 0.0, 18.0), (230.0, 0.0, 10.0) };

        var outcome = OrbSnap.Decide(team, "main", new[] { Loose("s-1", (270, 0, 18)) }, free: true);

        Assert.Equal(new SnapOutcome.Pair("s-1"), outcome);
    }

    [Fact]
    public void ALooseCandidateIsIgnoredOutsideFreeMode()
    {
        Assert.IsType<SnapOutcome.NoChange>(
            OrbSnap.Decide(One(), "main", new[] { Loose("s-1", (50, 0, 18)) }, free: false));
        Assert.IsType<SnapOutcome.NoChange>(
            OrbSnap.Decide(new Point(0, 0), 18, "main", new[] { Loose("s-1", (50, 0, 18)) }));
    }

    [Fact]
    public void ALooseCandidateIsMeasuredByItsOutlineToo()
    {
        var team = new SnapCandidate("s-1", Square, new[] { (190.0, 190.0, 10.0) }, Loose: true);

        Assert.Equal(
            new SnapOutcome.Pair("s-1"),
            OrbSnap.Decide(One(-40, 100), "main", new[] { team }, free: true));
    }
}
