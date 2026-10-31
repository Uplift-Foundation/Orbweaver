using Avalonia;
using Xunit;

namespace Orbweaver.Tests;

// QA (Nadia): the settings side of the free-mode gestures — what Pair, LeaveToFree and a
// member drag actually leave in the group table and the membership map.
[Collection("Settings")]
public class FreeModeSettingsQaTests : IDisposable
{
    private readonly HashSet<string> _before = OrbweaverSettings.OrbGroupList().Select(g => g.Id).ToHashSet();
    private readonly List<string> _keys = new();

    public void Dispose()
    {
        foreach (var key in _keys) OrbweaverSettings.UnassignOrbGroup(key);
        foreach (var g in OrbweaverSettings.OrbGroupList().Where(g => !_before.Contains(g.Id)))
            OrbweaverSettings.DeleteOrbGroup(g.Id);
    }

    private sealed class Host : IOrbDragHost
    {
        public bool IsArranged { get; set; }
        public Dictionary<string, string[]> Teams { get; } = new();
        public Dictionary<string, string> Groups { get; } = new();
        public Dictionary<string, string> Keys { get; } = new();
        public Dictionary<string, (double X, double Y, double R)> Discs { get; } = new();
        public List<SnapCandidate> Candidates { get; } = new();
        public List<string> Offsets { get; } = new();
        public bool LaidOut { get; set; }

        public bool IsLaidOut(string id) => LaidOut;
        public void SaveMemberOffset(string id) => Offsets.Add(id);
        public IReadOnlyList<string> TeamIdsOf(string id) => Teams.GetValueOrDefault(id) ?? new[] { id };
        public IReadOnlyList<string> OrbIdsInGroup(string groupId) => Array.Empty<string>();
        public OrbWindow? WindowOf(string id) => null;
        public string GroupIdOf(string id) => Groups.GetValueOrDefault(id) ?? OrbGroups.Main;
        public string PositionKeyOf(string id) => Keys.GetValueOrDefault(id, "");
        public (double X, double Y, double R) DiscOf(string id) => Discs[id];
        public IReadOnlyList<SnapCandidate> CandidatesFor(IReadOnlyCollection<string> excluded) => Candidates;
        public void HighlightGroup(string? g) { }
        public void ShiftGroupAnchor(string g, int dx, int dy) { }
        public void ReapplyArrangement() { }
    }

    private string Key(string name)
    {
        var key = "/qa/" + Guid.NewGuid().ToString("N")[..6] + "\n" + name;
        _keys.Add(key);
        return key;
    }

    private static string Id() => Guid.NewGuid().ToString("N")[..8];

    // Two loose teams side by side: A = lead + nested member, B = a solo orb 10 px away.
    private (Host host, string a, string aMember, string b) TwoLooseTeams()
    {
        string a = "a-" + Id(), am = "am-" + Id(), b = "b-" + Id();
        var host = new Host();
        host.Teams[a] = new[] { a, am };
        host.Keys[a] = Key("a");
        host.Keys[am] = Key("am");
        host.Keys[b] = Key("b");
        host.Discs[a] = (0, 0, 18);
        host.Discs[am] = (30, 0, 12);
        host.Discs[b] = (80, 0, 18);
        host.Candidates.Add(new SnapCandidate("loose-b", Array.Empty<Point>(), new[] { (80.0, 0.0, 18.0) }, Loose: true));
        host.Teams[b] = new[] { b };
        return (host, a, am, b);
    }

    [Fact]
    public void APairWritesBothTeamsKeysIntoOneNewGroupAnchoredBetweenThem()
    {
        var (host, a, am, b) = TwoLooseTeams();
        // The candidate names the other team's root by its GroupId.
        host.Candidates.Clear();
        host.Candidates.Add(new SnapCandidate(b, Array.Empty<Point>(), new[] { (80.0, 0.0, 18.0) }, Loose: true));

        var outcome = OrbDragCommit.Release(host, a, false, 5, 0, 1);

        Assert.IsType<SnapOutcome.Pair>(outcome);
        var group = OrbweaverSettings.OrbGroupMembershipFor(host.Keys[a]);
        Assert.NotNull(group);
        Assert.Equal(group, OrbweaverSettings.OrbGroupMembershipFor(host.Keys[am]));
        Assert.Equal(group, OrbweaverSettings.OrbGroupMembershipFor(host.Keys[b]));
        var made = OrbweaverSettings.OrbGroupFor(group)!;
        Assert.False(OrbGroups.IsPredefined(made.Id));
        Assert.NotNull(made.Anchor);
        Assert.Single(OrbweaverSettings.OrbGroupList().Where(g => !_before.Contains(g.Id)));
    }

    [Fact]
    public void LeavingToFreeSendsAHeartbeatOrbBackToItsClassificationNotToMain()
    {
        var host = new Host();
        string orb = "hb-" + Id(), other = "o-" + Id();
        host.Keys[orb] = Key("hb");
        host.Keys[other] = Key("o");
        host.Discs[orb] = (0, 0, 18);
        var group = OrbweaverSettings.CreateOrbGroup();
        OrbweaverSettings.AssignTeamToGroup(host.Keys[orb], Array.Empty<string>(), group.Id);
        OrbweaverSettings.AssignTeamToGroup(host.Keys[other], Array.Empty<string>(), group.Id);
        host.Groups[orb] = group.Id;
        // The other member of its group is far away, so this is a LeaveToFree.
        host.Candidates.Add(new SnapCandidate(group.Id, Array.Empty<Point>(), new[] { (5000.0, 0.0, 18.0) }));

        var outcome = OrbDragCommit.Release(host, orb, false, 5, 0, 1);

        Assert.IsType<SnapOutcome.LeaveToFree>(outcome);
        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor(host.Keys[orb]));
        Assert.Equal(group.Id, OrbweaverSettings.OrbGroupMembershipFor(host.Keys[other]));
        Assert.Equal(
            OrbGroups.Heartbeats,
            OrbGroups.GroupIdFor(
                null, OrbCluster.Heartbeats, ClusterMode.OwnShape, ClusterMode.OwnShape, ClusterMode.WithChats));
    }

    [Fact]
    public void AGroupSurvivesOneTeamLeavingAndIsPrunedWhenTheLastMemberGoes()
    {
        var (host, a, am, b) = TwoLooseTeams();
        host.Candidates.Clear();
        host.Candidates.Add(new SnapCandidate(b, Array.Empty<Point>(), new[] { (80.0, 0.0, 18.0) }, Loose: true));
        OrbDragCommit.Release(host, a, false, 5, 0, 1);
        var group = OrbweaverSettings.OrbGroupMembershipFor(host.Keys[a])!;

        // Team A drags far from team B: LeaveToFree for A.
        host.Groups[a] = group; host.Groups[am] = group; host.Groups[b] = group;
        host.Candidates.Clear();
        host.Candidates.Add(new SnapCandidate(group, Array.Empty<Point>(), new[] { (5000.0, 0.0, 18.0) }));
        Assert.IsType<SnapOutcome.LeaveToFree>(OrbDragCommit.Release(host, a, false, 5, 0, 1));
        Assert.NotNull(OrbweaverSettings.OrbGroupFor(group));            // B still in it
        Assert.Equal(group, OrbweaverSettings.OrbGroupMembershipFor(host.Keys[b]));

        // Now B (a solo team) leaves too: nobody left, the group must be gone.
        host.Candidates.Clear();
        host.Candidates.Add(new SnapCandidate(group, Array.Empty<Point>(), new[] { (5000.0, 0.0, 18.0) }));
        host.Teams[b] = new[] { b };
        // No other disc in the group any more from B's view; call the settings directly as the drag would.
        OrbweaverSettings.UnassignOrbGroup(host.Keys[b]);

        Assert.Null(OrbweaverSettings.OrbGroupFor(group));
        Assert.Empty(OrbweaverSettings.OrbGroupList().Where(g => !_before.Contains(g.Id)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADraggedMemberNeverChangesMembershipOrCreatesAGroup(bool laidOut)
    {
        string lead = "l-" + Id(), member = "m-" + Id();
        var host = new Host { LaidOut = laidOut };
        host.Teams[lead] = host.Teams[member] = new[] { lead, member };
        host.Keys[lead] = Key("lead");
        host.Keys[member] = Key("member");
        host.Discs[lead] = (0, 0, 18);
        host.Discs[member] = (30, 0, 12);
        var group = OrbweaverSettings.CreateOrbGroup();
        OrbweaverSettings.AssignTeamToGroup(host.Keys[lead], new[] { host.Keys[member] }, group.Id);
        host.Candidates.Add(new SnapCandidate(OrbGroups.Main, Array.Empty<Point>(), new[] { (35.0, 0.0, 18.0) }));
        var groupsBefore = OrbweaverSettings.OrbGroupList().Select(g => g.Id).ToList();

        OrbDragCommit.Release(host, member, false, 5, 5, 1);

        Assert.Equal(groupsBefore, OrbweaverSettings.OrbGroupList().Select(g => g.Id).ToList());
        Assert.Equal(group.Id, OrbweaverSettings.OrbGroupMembershipFor(host.Keys[lead]));
        Assert.Equal(group.Id, OrbweaverSettings.OrbGroupMembershipFor(host.Keys[member]));
        Assert.Equal(laidOut ? 1 : 0, host.Offsets.Count);
    }
}
