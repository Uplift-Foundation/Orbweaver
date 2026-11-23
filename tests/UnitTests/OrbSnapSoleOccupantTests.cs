using System.Collections.Generic;
using Avalonia;
using Xunit;

namespace Orbweaver.Tests;

// CB-332: a user group with one orb in it. The host leaves the moving team's own
// group out of the candidates when nothing else is in it, so there is no other
// disc to be far from; the drag is measured against where the team started.
public class OrbSnapSoleOccupantTests
{
    private static readonly (double X, double Y, double R)[] Start = { (0, 0, 18) };

    private static SnapCandidate Group(string id, params (double X, double Y, double R)[] discs) =>
        new(id, System.Array.Empty<Point>(), discs);

    private static SnapOutcome Decide(
        double movedX, string group, bool free, IReadOnlyList<SnapCandidate>? candidates = null,
        IReadOnlyList<(double X, double Y, double R)>? start = null) =>
        OrbSnap.Decide(new[] { (movedX, 0.0, 18.0) }, group, candidates ?? new List<SnapCandidate>(), free,
            OrbSnap.JoinDip, OrbSnap.LeaveDip, start ?? Start);

    [Fact]
    public void ASoleOccupantDraggedBeyondLeaveDistanceLeavesWhenArranged()
    {
        // 108 - 36 = 72 is not > 72; 109 is.
        Assert.IsType<SnapOutcome.LeaveSole>(Decide(109, "g-1", free: false));
    }

    [Fact]
    public void ASoleOccupantDraggedBeyondLeaveDistanceIsLetGoWhenFree()
    {
        Assert.IsType<SnapOutcome.LeaveToFree>(Decide(109, "g-1", free: true));
    }

    [Fact]
    public void ASoleOccupantExactlyAtLeaveDistanceStays()
    {
        Assert.IsType<SnapOutcome.NoChange>(Decide(108, "g-1", free: false));
        Assert.IsType<SnapOutcome.NoChange>(Decide(108, "g-1", free: true));
    }

    [Fact]
    public void ASoleOccupantInsideLeaveDistanceStays()
    {
        Assert.IsType<SnapOutcome.NoChange>(Decide(40, "g-1", free: false));
    }

    [Fact]
    public void AnEmptyOwnCandidateIsTheSameAsNone()
    {
        var own = new[] { Group("g-1") };
        Assert.IsType<SnapOutcome.LeaveSole>(Decide(200, "g-1", free: false, own));
    }

    [Fact]
    public void WithNoStartDiscsNothingChangesForASoleOccupant()
    {
        Assert.IsType<SnapOutcome.NoChange>(
            OrbSnap.Decide(new[] { (500.0, 0.0, 18.0) }, "g-1", new List<SnapCandidate>(), false));
        Assert.IsType<SnapOutcome.NoChange>(Decide(500, "g-1", free: false, start: new (double, double, double)[0]));
    }

    [Fact]
    public void ASoleOccupantOfAPredefinedGroupHasNothingToLeave()
    {
        Assert.IsType<SnapOutcome.NoChange>(Decide(500, OrbGroups.Main, free: false));
        Assert.IsType<SnapOutcome.NoChange>(Decide(500, OrbGroups.Main, free: true));
    }

    [Fact]
    public void JoinStillBeatsLeaveForASoleOccupant()
    {
        // 200 from the start, but 10 edge to edge from g-b.
        var outcome = Decide(200, "g-1", free: false, new[] { Group("g-b", (228, 0, 18)) });
        Assert.Equal(new SnapOutcome.Join("g-b"), outcome);
    }

    [Fact]
    public void AGroupWithOtherOrbsKeepsItsHysteresis()
    {
        var own = new[] { Group("g-1", (0, 0, 18)) };
        // Inside 72 of the other disc: stays, even far from where it started.
        Assert.IsType<SnapOutcome.NoChange>(Decide(100, "g-1", free: false, own, start: new (double, double, double)[] { (500, 0, 18) }));
        // Beyond 72 of it: the multi-orb Leave, not LeaveSole.
        Assert.IsType<SnapOutcome.Leave>(Decide(109, "g-1", free: false, own, start: Start));
    }
}
