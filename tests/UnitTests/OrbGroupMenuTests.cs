using Xunit;

namespace Orbweaver.Tests;

// CB-268 S6: which group an orb's Group submenu says it is in, and which keys a
// "Move to" has to write. A team is the unit of grouping, so every case that
// matters is about a member resolving to its lead's group and a move reaching
// the whole team — the failure is a team split across two groups.
[Collection("Settings")]
public class OrbGroupMenuTests : IDisposable
{
    private readonly List<string> _groups = new();
    private readonly List<string> _keys = new();
    private readonly string _dir = "/Users/user/menu-" + Guid.NewGuid();

    public void Dispose()
    {
        foreach (var key in _keys) OrbweaverSettings.UnassignOrbGroup(key);
        foreach (var id in _groups) OrbweaverSettings.DeleteOrbGroup(id);
    }

    private SessionStatus Status(string title, string lead = "")
    {
        var status = new SessionStatus { Cwd = _dir, Title = title, Lead = lead };
        _keys.Add(SessionManager.PositionKeyFor(status, title));
        return status;
    }

    private static IReadOnlyDictionary<string, SessionStatus> Of(params (string Id, SessionStatus Status)[] rows) =>
        rows.ToDictionary(r => r.Id, r => r.Status, StringComparer.Ordinal);

    private static GroupMenuContext Resolve(IReadOnlyDictionary<string, SessionStatus> statuses, string id) =>
        OrbGroupMenu.Resolve(id, statuses, ClusterMode.WithChats, ClusterMode.WithChats);

    [Fact]
    public void ASoloOrbIsInMainWithItsOwnKeyAndNoMembers()
    {
        var statuses = Of(("solo", Status("solo")));

        var context = Resolve(statuses, "solo");

        Assert.Equal(OrbGroups.Main, context.GroupId);
        Assert.Equal(SessionManager.PositionKeyFor(statuses["solo"], "solo"), context.LeadKey);
        Assert.Empty(context.MemberKeys);
    }

    [Fact]
    public void AMemberResolvesToItsLeadsTeamAndKeysTheWholeTeam()
    {
        var statuses = Of(
            ("lead", Status("lead")),
            ("m1", Status("m1", "lead")),
            ("m2", Status("m2", "m1")),
            ("other", Status("other")));

        var fromMember = Resolve(statuses, "m2");

        Assert.Equal(SessionManager.PositionKeyFor(statuses["lead"], "lead"), fromMember.LeadKey);
        Assert.Equal(
            new[] { SessionManager.PositionKeyFor(statuses["m1"], "m1"), SessionManager.PositionKeyFor(statuses["m2"], "m2") },
            fromMember.MemberKeys);
        Assert.DoesNotContain(SessionManager.PositionKeyFor(statuses["other"], "other"), fromMember.MemberKeys);

        // From the lead, the same team.
        Assert.Equal(fromMember, Resolve(statuses, "lead") with { }, new ContextComparer());
    }

    [Fact]
    public void AMemberTakesItsLeadsGroupNotItsOwnMembership()
    {
        var statuses = Of(("lead", Status("lead")), ("m1", Status("m1", "lead")));
        var group = OrbweaverSettings.CreateOrbGroup("circle");
        _groups.Add(group.Id);
        OrbweaverSettings.MoveOrbToGroup(SessionManager.PositionKeyFor(statuses["lead"], "lead"), group.Id);
        // A stale entry under the member's own key disagrees with the lead's.
        OrbweaverSettings.MoveOrbToGroup(SessionManager.PositionKeyFor(statuses["m1"], "m1"), OrbGroups.Main);

        Assert.Equal(group.Id, Resolve(statuses, "m1").GroupId);
    }

    [Fact]
    public void ALeadThatIsNotOnScreenMakesTheMemberItsOwnRoot()
    {
        var statuses = Of(("m1", Status("m1", "gone")));

        var context = Resolve(statuses, "m1");

        Assert.Equal(SessionManager.PositionKeyFor(statuses["m1"], "m1"), context.LeadKey);
        Assert.Empty(context.MemberKeys);
    }

    [Fact]
    public void EveryOrbInALeadCycleIsItsOwnRoot()
    {
        // OrbArrangement.Classify's rule, through OrbGroups.RootOf: a cycle has
        // no lead to hang from, so each member is its own team.
        var statuses = Of(("a", Status("a", "b")), ("b", Status("b", "a")));

        Assert.Equal("a", OrbGroupMenu.RootOf("a", statuses));
        Assert.Equal("b", OrbGroupMenu.RootOf("b", statuses));
        Assert.Empty(Resolve(statuses, "a").MemberKeys);
        Assert.Empty(Resolve(statuses, "b").MemberKeys);
    }

    [Fact]
    public void ACodexOrbDoesNotInheritAClaudeCodeOrbsDirectoryMembership()
    {
        var group = OrbweaverSettings.CreateOrbGroup("star");
        _groups.Add(group.Id);
        _keys.Add(_dir);
        OrbweaverSettings.MoveOrbToGroup(_dir, group.Id);
        var codex = new SessionStatus { Source = SessionSource.Codex, Cwd = _dir, Title = "t" };
        _keys.Add(SessionManager.PositionKeyFor(codex, "x"));

        Assert.Equal(OrbGroups.Main, Resolve(Of(("x", codex)), "x").GroupId);
        // The control: a Claude Code orb in the same directory does get it.
        Assert.Equal(group.Id, Resolve(Of(("y", Status("t"))), "y").GroupId);
    }

    [Fact]
    public void KeylessAndDuplicateKeyedMembersAreNotListedTwiceOrAtAll()
    {
        // m1 shares the lead's directory and title, so its key is the lead's;
        // m2 and m3 share one key between them; m4 has no directory and so no
        // key. Only m2's key is written, once.
        var lead = Status("same");
        var statuses = Of(
            ("lead", lead),
            ("m1", Status("same", "lead")),
            ("m2", Status("dup", "lead")),
            ("m3", Status("dup", "lead")),
            ("m4", new SessionStatus { Cwd = "", Title = "", Lead = "lead" }));

        var context = Resolve(statuses, "lead");

        Assert.Equal(new[] { SessionManager.PositionKeyFor(statuses["m2"], "m2") }, context.MemberKeys);
    }

    [Fact]
    public void AnOrbPointingAtItselfIsItsOwnRoot()
    {
        var statuses = Of(("a", Status("a", "a")));

        Assert.Equal("a", OrbGroupMenu.RootOf("a", statuses));
    }

    [Fact]
    public void ASessionTheManagerHasNeverHeardOfIsItsOwnRootWithNoKey()
    {
        var context = Resolve(Of(), "ghost");

        Assert.Equal(OrbGroups.Main, context.GroupId);
        Assert.Null(context.LeadKey);
        Assert.Empty(context.MemberKeys);
    }

    [Fact]
    public void AnOrbWithNoDirectoryHasNoKeyToWrite()
    {
        // PositionKeyFor is "" for a local session with no cwd: nothing to key
        // a membership on, so none is looked up.
        var statuses = Of(("a", new SessionStatus { Cwd = "", Title = "" }));

        var context = Resolve(statuses, "a");

        Assert.Null(context.LeadKey);
        Assert.Equal(OrbGroups.Main, context.GroupId);
    }

    [Fact]
    public void ABareDirectoryMembershipIsFoundForATitledClaudeSession()
    {
        // OrbPositions' second chance: a membership saved under the bare
        // directory still applies to the titled session that grew out of it.
        var statuses = Of(("a", Status("titled")));
        var group = OrbweaverSettings.CreateOrbGroup("star");
        _groups.Add(group.Id);
        _keys.Add(_dir);
        OrbweaverSettings.MoveOrbToGroup(_dir, group.Id);

        Assert.Equal(group.Id, Resolve(statuses, "a").GroupId);
    }

    [Fact]
    public void AHeartbeatTeamRootFallsBackToItsClusterGroup()
    {
        var statuses = Of(("hb", new SessionStatus
        {
            Source = SessionSource.OpenClaw,
            Heartbeat = true
        }));

        var context = OrbGroupMenu.Resolve("hb", statuses, ClusterMode.OwnShape, ClusterMode.OwnShape);

        Assert.Equal(OrbGroups.Heartbeats, context.GroupId);
    }

    // Value equality on the key list, which a record's default does not give.
    private sealed class ContextComparer : IEqualityComparer<GroupMenuContext>
    {
        public bool Equals(GroupMenuContext? a, GroupMenuContext? b) =>
            a is not null && b is not null
            && a.GroupId == b.GroupId && a.LeadKey == b.LeadKey && a.MemberKeys.SequenceEqual(b.MemberKeys);

        public int GetHashCode(GroupMenuContext c) => c.GroupId.GetHashCode();
    }
}
