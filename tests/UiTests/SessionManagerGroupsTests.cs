using System.Reflection;
using Avalonia;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.UiTests;

// CB-268 S7: SessionManager's side of groups, against real scans of a scratch
// status directory and real (headless) orb windows.
//
// Leads are set on the statuses by hand after a scan. In the app they come from
// each process's --parent-session-id (ScanProbes.TeamOf, keyed by pid), and every
// file here shares the test process's pid, so a scan cannot be made to report
// one. A lead that ends is simulated by dropping its status, which is what takes
// it out of DisplayOrder, and therefore out of the team tree, in the real
// removal pass too.
[Collection("Settings")]
public class SessionManagerGroupsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb-groups-" + Guid.NewGuid());
    private readonly List<string> _groups = new();
    private readonly List<string> _keys = new();
    private readonly OrbweaverSettings.OrbPlacement? _mainAnchor = OrbweaverSettings.ArrangeAnchor;

    public SessionManagerGroupsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var key in _keys) OrbweaverSettings.UnassignOrbGroup(key);
        foreach (var id in _groups) OrbweaverSettings.DeleteOrbGroup(id);
        OrbweaverSettings.ArrangeAnchor = _mainAnchor;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // A distinct CLI per orb: Superseded keys on (pid, source), and every file
    // here has this process's pid, so two files with one source would be judged
    // one session.
    private void Write(string sessionId, string cli, string title = "") =>
        File.WriteAllText(Path.Combine(_dir, sessionId + ".txt"),
            System.Text.Json.JsonSerializer.Serialize(new SessionStatus
            {
                Cli = cli,
                Cwd = "/Users/user/groups",
                Title = title,
                SessionPid = Environment.ProcessId,
                TermProgram = "iTerm.app",
                Tty = "/dev/ttys004",
            }));

    private SessionManager Scan(params (string Id, string Cli)[] sessions)
    {
        foreach (var (id, cli) in sessions) Write(id, cli);

        // Said out loud, not inherited: a scan filters each session through its
        // CLI's switch, and another test in this collection turns Codex off on
        // purpose (see SessionScanTests.Scan for the CI failure that taught it).
        OrbweaverSettings.ClaudeCodeEnabled = true;
        OrbweaverSettings.CodexEnabled = true;
        OrbweaverSettings.GrokEnabled = true;

        var manager = new SessionManager(_dir);
        manager.ScanAndUpdate();
        foreach (var (id, _) in sessions) Assert.NotNull(manager.StatusFor(id));
        return manager;
    }

    private string KeyOf(SessionManager manager, string id)
    {
        var key = OrbGroupMenu.KeyOf(id, manager.AllStatuses);
        Assert.NotEqual("", key);
        _keys.Add(key);
        return key;
    }

    private OrbGroup NewGroup(string shape = "circle")
    {
        var group = OrbweaverSettings.CreateOrbGroup(shape);
        _groups.Add(group.Id);
        return group;
    }

    private static void Invoke(SessionManager manager, string method, params object?[] args) =>
        typeof(SessionManager).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(manager, args);

    private static T Field<T>(SessionManager manager, string name) =>
        (T)typeof(SessionManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;

    private static Dictionary<string, OrbWindow> Windows(SessionManager manager) =>
        Field<Dictionary<string, OrbWindow>>(manager, "_windows");

    private static Dictionary<string, PixelPoint> Targets(SessionManager manager, params string[] ids)
    {
        var orbs = ids.Select(id => Windows(manager)[id]).ToList();
        var placed = (List<(OrbWindow Orb, PixelPoint Target)>)typeof(SessionManager)
            .GetMethod("ComputeClusteredPositions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(manager, new object[] { orbs })!;
        return placed.ToDictionary(p => p.Orb.SessionId, p => p.Target);
    }

    // --- T1 / T2: the team resolves from its root --------------------------

    [AvaloniaFact]
    public void AMemberIsInTheGroupItsRootWasSnappedIntoAndMovesWithTheWholeGroup()
    {
        var manager = Scan(("root", ""), ("member", "codex"), ("solo", "grok"));
        manager.StatusFor("member")!.Lead = "root";

        var group = NewGroup();
        OrbweaverSettings.MoveOrbToGroup(KeyOf(manager, "root"), group.Id);
        // A stale entry under the member's own key loses to its root's (T1).
        OrbweaverSettings.MoveOrbToGroup(KeyOf(manager, "member"), OrbGroups.Main);

        IOrbDragHost host = manager;
        Assert.Equal(group.Id, host.GroupIdOf("member"));
        Assert.Equal(new[] { "root", "member" }, host.TeamIdsOf("member"));
        Assert.Equal(new[] { "root", "member" }, host.OrbIdsInGroup(group.Id));
        Assert.Equal(new[] { "solo" }, host.OrbIdsInGroup(OrbGroups.Main));
    }

    [AvaloniaFact]
    public void AUserGroupIsArrangedAsItsOwnSlotNotAmongTheMainOrbs()
    {
        OrbweaverSettings.ArrangeAnchor = null;
        var manager = Scan(("a", ""), ("b", "codex"));

        var together = Targets(manager, "a", "b");

        OrbweaverSettings.MoveOrbToGroup(KeyOf(manager, "b"), NewGroup().Id);
        var apart = Targets(manager, "a", "b");

        // Same orbs, same anchor, same shapes; only b's group changed, and the
        // layout with it — the membership reaches the arrangement.
        Assert.NotEqual(together["b"], apart["b"]);
    }

    // --- T3: late members catch up ----------------------------------------

    [AvaloniaFact]
    public void AScanWritesTheRootsGroupUnderALateMembersKey()
    {
        var manager = Scan(("root", ""), ("late", "codex"));
        var group = NewGroup();
        OrbweaverSettings.MoveOrbToGroup(KeyOf(manager, "root"), group.Id);
        var lateKey = KeyOf(manager, "late");
        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor(lateKey));

        manager.StatusFor("late")!.Lead = "root";
        Invoke(manager, "SyncOrbGroups");

        Assert.Equal(group.Id, OrbweaverSettings.OrbGroupMembershipFor(lateKey));
    }

    // --- T4: an ended lead hands its members on ---------------------------

    [AvaloniaFact]
    public void ALeadThatEndsHandsItsMemberToTheNearestSurvivingAncestor()
    {
        var manager = Scan(("root", ""), ("lead", "codex"), ("member", "grok"));
        manager.StatusFor("lead")!.Lead = "root";
        manager.StatusFor("member")!.Lead = "lead";
        Invoke(manager, "SyncOrbGroups");

        // "lead" ends. Its member still names it — the process's parent is what
        // it is — and nothing on screen can say who led "lead" any more.
        Field<Dictionary<string, SessionStatus>>(manager, "_statuses").Remove("lead");
        Invoke(manager, "SyncOrbGroups");

        IOrbDragHost host = manager;
        Assert.Equal(new[] { "root", "member" }, host.TeamIdsOf("member"));
        Assert.Equal(KeyOf(manager, "root"), manager.GroupMenuContextFor("member").LeadKey);

        // The arrow follows the same tree; drawing it must not throw.
        Invoke(manager, "UpdateTeamLinks");

        // Once nothing descends through "lead", its remembered lead is dropped.
        manager.StatusFor("member")!.Lead = "";
        Invoke(manager, "SyncOrbGroups");
        Assert.False(Field<Dictionary<string, string>>(manager, "_knownLead").ContainsKey("lead"));
        Assert.Equal(new[] { "member" }, host.TeamIdsOf("member"));
    }

    // --- CB-10: a key that changes carries its membership ------------------

    [AvaloniaFact]
    public void AGroupChosenBeforeTheTitleArrivedFollowsTheOrbToItsNewKey()
    {
        var manager = Scan(("a", ""));
        var group = NewGroup();
        var before = KeyOf(manager, "a");
        OrbweaverSettings.MoveOrbToGroup(before, group.Id);
        Invoke(manager, "SyncOrbGroups");

        manager.StatusFor("a")!.Title = "now named";
        var after = KeyOf(manager, "a");
        Assert.NotEqual(before, after);

        Invoke(manager, "SyncOrbGroups");

        Assert.Equal(group.Id, OrbweaverSettings.OrbGroupMembershipFor(after));
    }

    // --- per-group anchors -------------------------------------------------

    // This found a real S2 defect (a heart's first-draw anchor, fed back,
    // redrew the group ~160 px away); ComputeGroups is now one path, so the
    // first draw and every later one are the same computation.
    [AvaloniaFact]
    public void TheFirstArrangeSavesEveryGroupsAnchorAndTheNextReproducesIt()
    {
        // With no anchor anywhere, ComputeGroups draws today's bands; each
        // non-empty group's landed centre is saved from that (CB-211 per group).
        // The saved anchors then have to reproduce the same picture, or the
        // second arrange — any arrival — would visibly jump.
        OrbweaverSettings.ArrangeAnchor = null;
        var manager = Scan(("a", ""), ("b", "codex"), ("c", "grok"));
        var group = NewGroup("heart");
        Assert.Null(group.Anchor);
        OrbweaverSettings.MoveOrbToGroup(KeyOf(manager, "c"), group.Id);

        var first = Targets(manager, "a", "b", "c");

        Assert.NotNull(OrbweaverSettings.ArrangeAnchor);
        Assert.NotNull(OrbweaverSettings.OrbGroupFor(group.Id)!.Anchor);

        var anchors = (OrbweaverSettings.ArrangeAnchor, OrbweaverSettings.OrbGroupFor(group.Id)!.Anchor);
        var second = Targets(manager, "a", "b", "c");

        Assert.Equal(first, second);
        Assert.Equal(anchors, (OrbweaverSettings.ArrangeAnchor, OrbweaverSettings.OrbGroupFor(group.Id)!.Anchor));
    }

    [AvaloniaFact]
    public void ShiftingAGroupsAnchorMovesThatGroupOnlyAndOnlyIfItHasOne()
    {
        var anchored = NewGroup();
        var unanchored = NewGroup();
        OrbweaverSettings.SetOrbGroupAnchor(anchored.Id, new OrbweaverSettings.OrbPlacement(100, 100));
        OrbweaverSettings.ArrangeAnchor = new OrbweaverSettings.OrbPlacement(500, 500);

        var manager = new SessionManager(_dir);
        manager.ShiftArrangementAnchor(5, -5, anchored.Id);
        manager.ShiftArrangementAnchor(5, -5, unanchored.Id);
        manager.ShiftArrangementAnchor(5, -5, "g-deadbeef");   // no such group: nothing to move
        ((IOrbDragHost)manager).ShiftGroupAnchor(anchored.Id, 1, 1);

        Assert.Equal(new OrbweaverSettings.OrbPlacement(106, 96), OrbweaverSettings.OrbGroupFor(anchored.Id)!.Anchor);
        Assert.Null(OrbweaverSettings.OrbGroupFor(unanchored.Id)!.Anchor);
        Assert.Equal(new OrbweaverSettings.OrbPlacement(500, 500), OrbweaverSettings.ArrangeAnchor);
    }

    // --- the rest of IOrbDragHost -------------------------------------------

    [AvaloniaFact]
    public void TheDragHostDescribesOrbsAsTheirVisibleCirclesInPositionUnits()
    {
        var manager = Scan(("a", ""), ("b", "codex"));
        IOrbDragHost host = manager;
        var a = Windows(manager)["a"];

        Assert.Same(a, host.WindowOf("a"));
        Assert.Null(host.WindowOf("nobody"));
        Assert.Equal(KeyOf(manager, "a"), host.PositionKeyOf("a"));

        // The factor OrbDragCommit divides back out, so the two are one factor.
        var units = OrbWindow.UnitsPerDip(a);
        Assert.True(units > 0);
        var disc = host.DiscOf("a");
        Assert.Equal(a.Position.X + a.CentreDip * units, disc.X);
        Assert.Equal(a.Position.Y + a.CentreDip * units, disc.Y);
        Assert.Equal(a.OrbRadius * units, disc.R);
        Assert.Equal((0.0, 0.0, 0.0), host.DiscOf("nobody"));
    }

    [AvaloniaFact]
    public void SnapCandidatesAreEveryOtherGroupWithItsDiscsAndNothingOfTheDraggedTeam()
    {
        var manager = Scan(("a", ""), ("b", "codex"), ("c", "grok"));
        var group = NewGroup();
        OrbweaverSettings.MoveOrbToGroup(KeyOf(manager, "b"), group.Id);
        IOrbDragHost host = manager;

        var all = host.CandidatesFor(Array.Empty<string>());
        Assert.Equal(new[] { group.Id, OrbGroups.Main }, all.Select(c => c.GroupId));   // ordinal
        Assert.Equal(2, all.Single(c => c.GroupId == OrbGroups.Main).Discs.Count);
        Assert.All(all, c => Assert.Empty(c.Outline));

        var withoutA = host.CandidatesFor(new[] { "a", "c" });
        var only = Assert.Single(withoutA);
        Assert.Equal(group.Id, only.GroupId);
        Assert.Equal(host.DiscOf("b"), Assert.Single(only.Discs));
    }

    [AvaloniaFact]
    public void TheHighlightedGroupIsRecordedForTheBackdrops()
    {
        var manager = new SessionManager(_dir);
        IOrbDragHost host = manager;

        host.HighlightGroup("g-target");
        Assert.Equal("g-target", manager.HighlightedGroup);
        host.HighlightGroup(null);
        Assert.Null(manager.HighlightedGroup);
    }
}
