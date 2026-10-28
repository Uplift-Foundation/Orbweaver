using Avalonia;
using Xunit;

namespace Orbweaver.Tests;

// CB-268 S4: what releasing a drag does — preview highlight, membership for the
// lead and every member, a new one-orb group on Leave, anchors for a whole-group
// move — against a fake roster. [Collection("Settings")] because the writes go
// through OrbweaverSettings' process-wide model.
[Collection("Settings")]
public class OrbDragCommitTests
{
    private sealed class FakeHost : IOrbDragHost
    {
        public bool IsArranged { get; set; } = true;
        public Dictionary<string, string[]> Teams { get; } = new();
        public Dictionary<string, string> Groups { get; } = new();
        public Dictionary<string, (double X, double Y, double R)> Discs { get; } = new();
        public List<SnapCandidate> Candidates { get; } = new();
        public List<string?> Highlights { get; } = new();
        public List<(string Group, int Dx, int Dy)> Shifts { get; } = new();
        public int Reapplied { get; private set; }
        public IReadOnlyCollection<string>? ExcludedAsked { get; private set; }

        public IReadOnlyList<string> TeamIdsOf(string id) => Teams.GetValueOrDefault(id) ?? new[] { id };
        public IReadOnlyList<string> OrbIdsInGroup(string groupId) =>
            Groups.Where(g => g.Value == groupId).Select(g => g.Key).ToList();
        public OrbWindow? WindowOf(string id) => null;
        public string GroupIdOf(string id) => Groups.GetValueOrDefault(id) ?? OrbGroups.Main;
        public string PositionKeyOf(string id) => "key:" + id;
        public (double X, double Y, double R) DiscOf(string id) => Discs[id];

        public IReadOnlyList<SnapCandidate> CandidatesFor(IReadOnlyCollection<string> excludedIds)
        {
            ExcludedAsked = excludedIds;
            return Candidates;
        }

        public void HighlightGroup(string? groupId) => Highlights.Add(groupId);
        public void ShiftGroupAnchor(string groupId, int dx, int dy) => Shifts.Add((groupId, dx, dy));
        public void ReapplyArrangement() => Reapplied++;
    }

    private static string UniqueId() => Guid.NewGuid().ToString("N")[..8];

    // A solo orb at the origin in main, one other main orb far away so there is
    // something to leave, and a user group right next to it to join.
    private static (FakeHost Host, string Orb, string Target) Scene(double targetX, double mainX = 120)
    {
        var orb = "orb-" + UniqueId();
        var target = OrbweaverSettings.CreateOrbGroup().Id;
        var host = new FakeHost();
        host.Discs[orb] = (0, 0, 18);
        host.Candidates.Add(new SnapCandidate(OrbGroups.Main, Array.Empty<Point>(), new[] { (mainX, 0.0, 18.0) }));
        host.Candidates.Add(new SnapCandidate(target, Array.Empty<Point>(), new[] { (targetX, 0.0, 18.0) }));
        return (host, orb, target);
    }

    [Fact]
    public void PreviewHighlightsTheGroupItWouldJoin()
    {
        var (host, orb, target) = Scene(50);

        var outcome = OrbDragCommit.Preview(host, orb, 1);

        Assert.Equal(new SnapOutcome.Join(target), outcome);
        Assert.Equal(new[] { target }, host.Highlights);
    }

    [Fact]
    public void PreviewClearsTheHighlightWhenNothingWouldJoin()
    {
        var (host, orb, _) = Scene(5000);

        Assert.IsType<SnapOutcome.Leave>(OrbDragCommit.Preview(host, orb, 1));
        Assert.Equal(new string?[] { null }, host.Highlights);
    }

    [Fact]
    public void ReleaseJoiningWritesMembershipForTheLeadAndEveryMemberThenReapplies()
    {
        var (host, orb, target) = Scene(50);
        var lead = "lead-" + UniqueId();
        var member = "member-" + UniqueId();
        host.Teams[orb] = new[] { lead, orb, member };
        host.Discs[lead] = (0, 0, 18);
        host.Discs[member] = (0, 0, 10);

        var outcome = OrbDragCommit.Release(host, orb, false, 30, 40, 1);

        Assert.Equal(new SnapOutcome.Join(target), outcome);
        Assert.Equal(target, OrbweaverSettings.OrbGroupMembershipFor("key:" + lead));
        Assert.Equal(target, OrbweaverSettings.OrbGroupMembershipFor("key:" + orb));
        Assert.Equal(target, OrbweaverSettings.OrbGroupMembershipFor("key:" + member));
        Assert.Equal(1, host.Reapplied);
        Assert.Equal(new string?[] { null }, host.Highlights);
        Assert.Empty(host.Shifts);
        // The team is excluded from the candidates it is measured against.
        Assert.Equal(new[] { lead, orb, member }.OrderBy(x => x), host.ExcludedAsked!.OrderBy(x => x));
    }

    [Fact]
    public void ReleaseLeavingMakesANewOneOrbGroupAnchoredAtTheDropPoint()
    {
        var (host, orb, _) = Scene(5000);
        host.Discs[orb] = (310.4, 220.6, 18);
        var before = OrbweaverSettings.OrbGroupList().Select(g => g.Id).ToHashSet();

        var outcome = OrbDragCommit.Release(host, orb, false, 5, 5, 1);

        Assert.IsType<SnapOutcome.Leave>(outcome);
        var madeId = OrbweaverSettings.OrbGroupMembershipFor("key:" + orb);
        Assert.NotNull(madeId);
        Assert.DoesNotContain(madeId, before);
        Assert.Equal(new OrbweaverSettings.OrbPlacement(310, 221), OrbweaverSettings.OrbGroupFor(madeId)!.Anchor);
        Assert.Equal(1, host.Reapplied);
    }

    [Fact]
    public void ReleaseWithNoChangeWritesNothingButStillReapplies()
    {
        var (host, orb, _) = Scene(5000, mainX: 70);

        var outcome = OrbDragCommit.Release(host, orb, false, 5, 5, 1);

        Assert.IsType<SnapOutcome.NoChange>(outcome);
        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor("key:" + orb));
        Assert.Equal(1, host.Reapplied);
    }

    [Fact]
    public void ReleaseWhenNotArrangedDoesNotReapply()
    {
        var (host, orb, target) = Scene(50);
        host.IsArranged = false;

        OrbDragCommit.Release(host, orb, false, 5, 5, 1);

        Assert.Equal(target, OrbweaverSettings.OrbGroupMembershipFor("key:" + orb));
        Assert.Equal(0, host.Reapplied);
    }

    [Fact]
    public void ADragOnA2xScreenIsMeasuredInDip()
    {
        // Physical: radius 36 each, centres 110 apart. At 1x that is a 38 px gap,
        // too far to join. At 2x it is 55 DIP between centres and radius 18 each:
        // a 19 DIP gap, which joins.
        var (host, orb, target) = Scene(110);
        host.Discs[orb] = (0, 0, 36);
        host.Candidates[1] = new SnapCandidate(target, Array.Empty<Point>(), new[] { (110.0, 0.0, 36.0) });

        Assert.IsType<SnapOutcome.NoChange>(OrbDragCommit.Preview(host, orb, 1));
        Assert.Equal(new SnapOutcome.Join(target), OrbDragCommit.Preview(host, orb, 2));
        // A nonsense scaling reads as 1.
        Assert.IsType<SnapOutcome.NoChange>(OrbDragCommit.Preview(host, orb, 0));
    }

    [Fact]
    public void AnOutlineIsScaledToDipToo()
    {
        var (host, orb, target) = Scene(5000);
        host.Discs[orb] = (-100, 200, 36);
        // 2x: outline 0..400 physical = 0..200 DIP; orb centre (-50,100) DIP, r 18: gap 32 DIP.
        host.Candidates[1] = new SnapCandidate(
            target,
            new[] { new Point(0, 0), new Point(400, 0), new Point(400, 400), new Point(0, 400) },
            Array.Empty<(double, double, double)>());
        Assert.IsType<SnapOutcome.Leave>(OrbDragCommit.Preview(host, orb, 2));

        host.Discs[orb] = (-80, 200, 36);   // centre -40 DIP: gap 22
        Assert.Equal(new SnapOutcome.Join(target), OrbDragCommit.Preview(host, orb, 2));
    }

    [Fact]
    public void AWholeGroupDragShiftsTheGroupsAnchorAndChangesNoMembership()
    {
        var (host, orb, _) = Scene(50);
        host.Groups[orb] = "g-src";

        var outcome = OrbDragCommit.Release(host, orb, true, 12, -7, 1);

        Assert.IsType<SnapOutcome.NoChange>(outcome);
        Assert.Equal(new[] { ("g-src", 12, -7) }, host.Shifts);
        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor("key:" + orb));
        Assert.Equal(1, host.Reapplied);
    }

    [Fact]
    public void AWholeGroupDragWhenNotArrangedShiftsNothing()
    {
        var (host, orb, _) = Scene(50);
        host.IsArranged = false;

        OrbDragCommit.Release(host, orb, true, 12, -7, 1);

        Assert.Empty(host.Shifts);
        Assert.Equal(0, host.Reapplied);
    }
}
