using Xunit;

namespace Orbweaver.Tests;

// QA (Nadia) over S6's Group menu and the S4 commit it shares keys with.
// Each case states the rule it checks; the first three are expected to FAIL on
// b481a53c if the finding is real.
[Collection("Settings")]
public class OrbGroupS6QaTests : IDisposable
{
    private readonly List<string> _groups = new();
    private readonly List<string> _keys = new();
    private readonly string _dir = "/Users/user/qa-" + Guid.NewGuid();

    public void Dispose()
    {
        foreach (var key in _keys) OrbweaverSettings.UnassignOrbGroup(key);
        foreach (var id in _groups) OrbweaverSettings.DeleteOrbGroup(id);
    }

    private SessionStatus Status(string title, SessionSource source = SessionSource.ClaudeCode, string lead = "")
    {
        var status = new SessionStatus { Cwd = _dir, Title = title, Lead = lead, Source = source };
        _keys.Add(SessionManager.PositionKeyFor(status, title));
        return status;
    }

    // SessionManager restricts the bare-directory fallback to Claude Code, because a
    // Codex orb must never be handed the place a Claude Code orb was put. The menu's
    // read of "which group am I in" must agree, or "In: <name>" names a group the
    // arrangement does not put the orb in.
    [Fact]
    public void ACodexOrbDoesNotInheritAClaudeCodeOrbsDirectoryMembership()
    {
        var group = OrbweaverSettings.CreateOrbGroup();
        _groups.Add(group.Id);
        var claude = Status("claude-orb");
        var codex = Status("codex-orb", SessionSource.Codex);
        // Membership saved under the bare directory, as an older build's Claude Code orb would have.
        OrbweaverSettings.MoveOrbToGroup(SessionManager.DirectoryKeyFor(claude), group.Id);
        _keys.Add(SessionManager.DirectoryKeyFor(claude));

        var statuses = new Dictionary<string, SessionStatus>(StringComparer.Ordinal)
        {
            ["claude-orb"] = claude,
            ["codex-orb"] = codex
        };

        var forClaude = OrbGroupMenu.Resolve("claude-orb", statuses, ClusterMode.WithChats, ClusterMode.WithChats, ClusterMode.WithChats);
        var forCodex = OrbGroupMenu.Resolve("codex-orb", statuses, ClusterMode.WithChats, ClusterMode.WithChats, ClusterMode.WithChats);

        Assert.Equal(group.Id, forClaude.GroupId);          // positive control: fallback works for Claude Code
        Assert.Equal(OrbGroups.Main, forCodex.GroupId);      // the rule under test
    }

    // One resolver, the arrangement's: every orb in a lead cycle is its own root.
    [Fact]
    public void EveryOrbInALeadCycleIsItsOwnRoot()
    {
        var statuses = new Dictionary<string, SessionStatus>(StringComparer.Ordinal)
        {
            ["a"] = Status("a", lead: "b"),
            ["b"] = Status("b", lead: "a"),
            ["solo"] = Status("solo")
        };

        Assert.Equal("a", OrbGroupMenu.RootOf("a", statuses));
        Assert.Equal("b", OrbGroupMenu.RootOf("b", statuses));
        Assert.Equal("solo", OrbGroupMenu.RootOf("solo", statuses));   // control: a plain orb passes
    }

    private sealed class KeylessHost : IOrbDragHost
    {
        public bool IsArranged => false;
        public IReadOnlyList<string> TeamIdsOf(string id) => new[] { id };
        public IReadOnlyList<string> OrbIdsInGroup(string groupId) => Array.Empty<string>();
        public OrbWindow? WindowOf(string id) => null;
        public string GroupIdOf(string id) => OrbGroups.Main;
        public string PositionKeyOf(string id) => "";   // a local CLI session with no cwd
        public (double X, double Y, double R) DiscOf(string id) => (0, 0, 18);
        public IReadOnlyList<SnapCandidate> CandidatesFor(IReadOnlyCollection<string> excludedIds) =>
            new[] { new SnapCandidate(OrbGroups.Main, Array.Empty<Avalonia.Point>(), new[] { (5000.0, 0.0, 18.0) }) };
        public void HighlightGroup(string? groupId) { }
        public void ShiftGroupAnchor(string groupId, int dx, int dy) { }
        public void ReapplyArrangement() { }
    }
}
