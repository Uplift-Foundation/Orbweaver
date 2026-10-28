using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
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
    private readonly bool _outlines = OrbweaverSettings.GroupOutlinesEnabled;
    private readonly bool _showOrbs = OrbweaverSettings.ShowOrbs;

    public SessionManagerGroupsTests()
    {
        Directory.CreateDirectory(_dir);
        ResetBackdrops();
    }

    // GroupBackdrops is process-wide, so every test starts and ends with none.
    private static void ResetBackdrops()
    {
        GroupBackdrops.PreviewPair(null);
        GroupBackdrops.Update(Array.Empty<BackdropSpec>());
        GroupBackdrops.ParkAll();
        GroupBackdrops.Highlight(null);
        GroupBackdrops.SetOutlinesVisible(true);
    }

    private readonly List<string> _offsetKeys = new();

    public void Dispose()
    {
        foreach (var key in _offsetKeys) OrbweaverSettings.SetOrbMemberOffset(key, null);
        ResetBackdrops();
        OrbweaverSettings.GroupOutlinesEnabled = _outlines;
        OrbweaverSettings.ShowOrbs = _showOrbs;
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

    // The membership key is tracked per session for as long as the session is
    // known, not per orb on screen. Hiding the orbs alone never dropped one
    // (the windows stay, only invisible) — this test passes with or without
    // that rule, and pins the behaviour. The next one is what the rule fixes.
    [AvaloniaFact]
    public void AKeyThatChangesWhileTheOrbIsHiddenStillCarriesItsGroup()
    {
        var manager = Scan(("a", ""));
        var group = NewGroup();
        var before = KeyOf(manager, "a");
        OrbweaverSettings.MoveOrbToGroup(before, group.Id);
        Invoke(manager, "SyncOrbGroups");

        // Hidden for a scan, and the title arrives meanwhile.
        manager.SetOrbsVisible(false);
        manager.StatusFor("a")!.Title = "named while hidden";
        var after = KeyOf(manager, "a");
        Assert.NotEqual(before, after);
        Invoke(manager, "SyncOrbGroups");

        manager.SetOrbsVisible(true);
        Invoke(manager, "SyncOrbGroups");

        Assert.Equal(group.Id, OrbweaverSettings.OrbGroupMembershipFor(after));
    }

    // A session the manager still knows but has no orb for, for a scan: the
    // key was forgotten when tracking followed orbs on screen, and the carry
    // never fired. Constructed by taking the window away and putting it back;
    // this fails on the version that observed only the team tree.
    [AvaloniaFact]
    public void AKeyThatChangesWhileTheSessionHasNoOrbStillCarriesItsGroup()
    {
        var manager = Scan(("a", ""));
        var group = NewGroup();
        var before = KeyOf(manager, "a");
        OrbweaverSettings.MoveOrbToGroup(before, group.Id);
        Invoke(manager, "SyncOrbGroups");

        var windows = Windows(manager);
        var orb = windows["a"];
        windows.Remove("a");
        manager.StatusFor("a")!.Title = "named with no orb";
        var after = KeyOf(manager, "a");
        Invoke(manager, "SyncOrbGroups");

        windows["a"] = orb;
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
        // Arranged: every group is a target. (Free mode's targets, where main
        // is not one, are FreeModeSnapTargetsAreUserGroupsAndLooseTeamsNeverMain.)
        OrbweaverSettings.ArrangeAnchor = null;
        var manager = Scan(("a", ""), ("b", "codex"), ("c", "grok"));
        var group = NewGroup();
        OrbweaverSettings.MoveOrbToGroup(KeyOf(manager, "b"), group.Id);
        manager.ArrangeOrbsInPattern();
        CompleteTheGlide(manager);
        ResetBackdrops();                      // only the discs matter here, not the drawn outlines
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

    // --- the backdrops (S5) -----------------------------------------------

    private static void CompleteTheGlide(SessionManager manager)
    {
        typeof(SessionManager).GetField("_arrangeAnimStart", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(manager, Environment.TickCount64 - 700);
        Invoke(manager, "OnArrangeAnimTick", null, EventArgs.Empty);
    }

    private (SessionManager Manager, OrbGroup Group) ArrangedInTwoGroups()
    {
        OrbweaverSettings.ArrangeAnchor = null;
        var manager = Scan(("a", ""), ("b", "codex"));
        var group = NewGroup("heart");
        OrbweaverSettings.MoveOrbToGroup(KeyOf(manager, "b"), group.Id);

        manager.ArrangeOrbsInPattern();
        CompleteTheGlide(manager);
        Assert.True(manager.IsArranged);
        return (manager, group);
    }

    [AvaloniaFact]
    public void ArrangingGivesEveryGroupABackdropAndUnarrangingLeavesOnlyTheUserGroups()
    {
        var (manager, group) = ArrangedInTwoGroups();

        Assert.Equal(2, GroupBackdrops.ShownCount);
        Assert.True(GroupBackdrops.StateOf(OrbGroups.Main)!.Value.Shown);
        Assert.True(GroupBackdrops.StateOf(group.Id)!.Value.Shown);

        // The snap candidates carry the outline the backdrop drew — one place
        // decides the padding.
        IOrbDragHost host = manager;
        foreach (var candidate in host.CandidatesFor(Array.Empty<string>()))
        {
            Assert.NotEmpty(candidate.Outline);
            Assert.Equal(GroupBackdrops.OutlineOf(candidate.GroupId), candidate.Outline);
        }

        // The drag's "you will join this" cue reaches the backdrop.
        host.HighlightGroup(group.Id);
        Assert.True(GroupBackdrops.StateOf(group.Id)!.Value.Highlighted);
        host.HighlightGroup(null);
        Assert.False(GroupBackdrops.StateOf(group.Id)!.Value.Highlighted);

        // Hidden with the orbs, back with them.
        manager.SetOrbsVisible(false);
        Assert.Equal(0, GroupBackdrops.ShownCount);
        manager.SetOrbsVisible(true);
        Assert.Equal(2, GroupBackdrops.ShownCount);

        // Un-arranged, main has no backdrop any more; the user group keeps its
        // own (CB-268 F1: user groups are always live).
        manager.ArrangeOrbsInPattern();
        Assert.False(manager.IsArranged);
        Assert.Null(GroupBackdrops.StateOf(OrbGroups.Main));
        Assert.True(GroupBackdrops.StateOf(group.Id)!.Value.Shown);
    }

    [AvaloniaFact]
    public void AStyleOrTheOutlineSwitchReachesTheBackdropsThroughReapplyGroupStyles()
    {
        var (manager, group) = ArrangedInTwoGroups();
        OrbweaverSettings.GroupOutlinesEnabled = true;
        manager.ReapplyGroupStyles();

        OrbweaverSettings.SetOrbGroupStyle(group.Id, new GroupStyle("#FF0000", 1.0, null, null));
        Assert.NotEqual(Color.FromArgb(255, 255, 0, 0), GroupBackdrops.StateOf(group.Id)!.Value.Stroke);

        manager.ReapplyGroupStyles();
        Assert.Equal(Color.FromArgb(255, 255, 0, 0), GroupBackdrops.StateOf(group.Id)!.Value.Stroke);

        // Outlines off: invisible but still there, still grabbable.
        OrbweaverSettings.GroupOutlinesEnabled = false;
        manager.ReapplyGroupStyles();
        var off = GroupBackdrops.StateOf(group.Id)!.Value;
        Assert.True(off.Shown);
        Assert.Equal(Colors.Transparent, off.Stroke);
    }

    // --- free mode (F1–F4) --------------------------------------------------

    private static PixelRect Bounds(SessionManager manager, string groupId)
    {
        var placements = (System.Collections.IEnumerable)typeof(SessionManager)
            .GetField("_groupPlacements", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
        foreach (var g in placements)
        {
            var t = g.GetType();
            if ((string)t.GetProperty("GroupId")!.GetValue(g)! == groupId)
                return (PixelRect)t.GetProperty("Bounds")!.GetValue(g)!;
        }
        throw new InvalidOperationException("no placement for " + groupId);
    }

    private static PixelRect Rect(OrbWindow w)
    {
        var size = SessionManager.OrbPixels(w, w.Screens.Primary?.Scaling ?? 1);
        return new PixelRect(w.Position, new PixelSize(size, size));
    }

    private (SessionManager Manager, OrbGroup Group) FreeWithAUserGroup()
    {
        OrbweaverSettings.ArrangeAnchor = null;
        var manager = Scan(("loose", ""), ("b", "codex"), ("c", "grok"));
        var group = NewGroup("heart");
        OrbweaverSettings.MoveOrbToGroup(KeyOf(manager, "b"), group.Id);
        OrbweaverSettings.MoveOrbToGroup(KeyOf(manager, "c"), group.Id);
        manager.ReapplyArrangement();      // free mode: the reflow lays the user group out
        Assert.False(manager.IsArranged);
        return (manager, group);
    }

    [AvaloniaFact]
    public void InFreeModeAUserGroupIsLaidOutClearOfTheLooseStackWithItsOwnBackdrop()
    {
        var (manager, group) = FreeWithAUserGroup();
        var w = Windows(manager);

        // F1: the user group has a backdrop; main, not arranged, has none.
        Assert.True(GroupBackdrops.StateOf(group.Id)!.Value.Shown);
        Assert.Null(GroupBackdrops.StateOf(OrbGroups.Main));

        // F2: the loose orb is an obstacle — the group's box keeps clear of it.
        Assert.False(Bounds(manager, group.Id).Intersects(Rect(w["loose"])));

        // And a grouped orb is not the stack's: the loose orb alone holds the
        // stack's first slot, as it would with no group at all.
        Assert.NotEqual(w["loose"].Position, w["b"].Position);
        Assert.NotEqual(w["loose"].Position, w["c"].Position);
    }

    [AvaloniaFact]
    public void ArrangingAndUnarrangingNeverMoveAUserGroup()
    {
        var (manager, group) = FreeWithAUserGroup();
        var w = Windows(manager);
        var before = (w["b"].Position, w["c"].Position, OrbweaverSettings.OrbGroupFor(group.Id)!.Anchor);

        manager.ArrangeOrbsInPattern();
        CompleteTheGlide(manager);
        Assert.True(manager.IsArranged);
        Assert.Equal(before, (w["b"].Position, w["c"].Position, OrbweaverSettings.OrbGroupFor(group.Id)!.Anchor));
        Assert.True(GroupBackdrops.StateOf(OrbGroups.Main)!.Value.Shown);

        manager.ArrangeOrbsInPattern();
        CompleteTheGlide(manager);
        Assert.False(manager.IsArranged);
        Assert.Equal(before, (w["b"].Position, w["c"].Position, OrbweaverSettings.OrbGroupFor(group.Id)!.Anchor));
        Assert.True(GroupBackdrops.StateOf(group.Id)!.Value.Shown);
        Assert.Null(GroupBackdrops.StateOf(OrbGroups.Main));
    }

    [AvaloniaFact]
    public void FreeModeSnapTargetsAreUserGroupsAndLooseTeamsNeverMain()
    {
        var manager = Scan(("a", ""), ("lead", "codex"), ("member", "grok"));
        manager.StatusFor("member")!.Lead = "lead";
        var group = NewGroup();
        OrbweaverSettings.MoveOrbToGroup(KeyOf(manager, "a"), group.Id);
        manager.ReapplyArrangement();
        IOrbDragHost host = manager;

        var free = host.CandidatesFor(Array.Empty<string>());
        Assert.Equal(new[] { group.Id, "lead" }, free.Select(c => c.GroupId));
        Assert.False(free[0].Loose);
        Assert.True(free[1].Loose);
        Assert.Equal(2, free[1].Discs.Count);                  // the whole loose team
        Assert.Empty(free[1].Outline);

        // Dragging that team: it is not its own target.
        Assert.Equal(new[] { group.Id }, host.CandidatesFor(new[] { "lead", "member" }).Select(c => c.GroupId));

        // Arranged: today's candidates, main included, nothing loose.
        manager.ArrangeOrbsInPattern();
        CompleteTheGlide(manager);
        var arranged = host.CandidatesFor(Array.Empty<string>());
        Assert.Equal(new[] { group.Id, OrbGroups.Main }, arranged.Select(c => c.GroupId));
        Assert.All(arranged, c => Assert.False(c.Loose));
    }

    [AvaloniaFact]
    public void APairPreviewOutlinesBothTeamsAndClears()
    {
        var manager = Scan(("a", ""), ("lead", "codex"), ("member", "grok"));
        manager.StatusFor("member")!.Lead = "lead";
        manager.ReapplyArrangement();
        IOrbDragHost host = manager;

        // "a" dragged onto the loose team led by "lead": the preview takes in
        // both teams, member included, as the group that would be made.
        host.PreviewPair(new[] { "a" }, "member");          // any orb of the target team names it
        Assert.NotNull(GroupBackdrops.PreviewState);

        host.PreviewPair(null, null);
        Assert.Null(GroupBackdrops.PreviewState);

        host.PreviewPair(new[] { "a" }, "lead");
        host.PreviewPair(new[] { "a" }, null);              // either side null clears
        Assert.Null(GroupBackdrops.PreviewState);
    }

    // --- T2 revised: a member's own placement -------------------------------

    [AvaloniaFact]
    public void AMembersOffsetFromItsLeadIsSavedAndResetPositionForgetsIt()
    {
        var manager = Scan(("lead", ""), ("member", "codex"));
        manager.StatusFor("member")!.Lead = "lead";
        IOrbDragHost host = manager;
        var w = Windows(manager);
        var member = w["member"];
        Assert.NotEqual("", member.SoundKey);
        _offsetKeys.Add(member.SoundKey);

        member.Position = new PixelPoint(w["lead"].Position.X + 30, w["lead"].Position.Y + 40);
        host.SaveMemberOffset("member");

        var m = host.DiscOf("member");
        var l = host.DiscOf("lead");
        var units = OrbWindow.UnitsPerDip(member);
        Assert.Equal(((m.X - l.X) / units, (m.Y - l.Y) / units), OrbweaverSettings.OrbMemberOffsetFor(member.SoundKey));

        // The menu offers to undo it even though nothing is pinned.
        member.SessionMenu_Opening(null, new System.ComponentModel.CancelEventArgs());
        Assert.True(member.FindControl<MenuItem>("ResetPositionItem")!.IsVisible);

        manager.ReturnOrbToStack("member");
        Assert.Null(OrbweaverSettings.OrbMemberOffsetFor(member.SoundKey));
        member.SessionMenu_Opening(null, new System.ComponentModel.CancelEventArgs());
        Assert.False(member.FindControl<MenuItem>("ResetPositionItem")!.IsVisible);

        // A lead has no lead to be offset from: nothing is written.
        _offsetKeys.Add(w["lead"].SoundKey);
        host.SaveMemberOffset("lead");
        Assert.Null(OrbweaverSettings.OrbMemberOffsetFor(w["lead"].SoundKey));
        host.SaveMemberOffset("nobody");
    }

    [AvaloniaFact]
    public void ResettingAMemberWhileArrangedReLaysTheArrangement()
    {
        OrbweaverSettings.ArrangeAnchor = null;
        var manager = Scan(("lead", ""), ("member", "codex"));
        manager.StatusFor("member")!.Lead = "lead";
        manager.ArrangeOrbsInPattern();
        CompleteTheGlide(manager);
        var member = Windows(manager)["member"];
        _offsetKeys.Add(member.SoundKey);
        var settled = member.Position;

        OrbweaverSettings.SetOrbMemberOffset(member.SoundKey, (200, 0));
        member.Position = new PixelPoint(settled.X + 5, settled.Y + 5);
        manager.ReturnOrbToStack("member");

        Assert.Null(OrbweaverSettings.OrbMemberOffsetFor(member.SoundKey));
        Assert.Equal(settled, member.Position);    // re-laid, not left where it was nudged
    }

    [AvaloniaFact]
    public void TheOutlineGetsTheCurveOnlyWhereComputeGroupsReportsOne()
    {
        OrbArrangement.GroupPlacement Placed(string shape, double scale) =>
            new(0, new PixelPoint(0, 0), new PixelRect(0, 0, 10, 10), shape, new[] { 0 }, new Point(5, 6), scale);

        // ComputeGroups' own "no curve" is a scale of 0, whatever the shape —
        // including a heart squeezed into a grid, or a lone orb.
        foreach (var shape in new[] { "grid", "line", "vline", "heart" })
            Assert.Null(SessionManager.CurveOf(Placed(shape, 0)));

        Assert.Equal(new GroupOutline.PlacedCurve(new Point(5, 6), 7.5), SessionManager.CurveOf(Placed("heart", 7.5)));
    }

    [AvaloniaFact]
    public void AnOrbOutsideAVisualTreeMeasuresAsNothingRatherThanThrowing()
    {
        // What PointToScreen throws for a visual outside a visual tree —
        // forced, because whether a never-shown orb is one under the headless
        // platform depends on what ran before it.
        var orb = new OrbWindow("measure");
        (double, double, double) Outside(OrbWindow _) =>
            throw new ArgumentException("Visual does not belong to a visual tree.", "visual");

        Assert.Null(GroupBackdrops.MeasureFromDisc(orb, Outside, _ => 1));
        Assert.Equal((new Point(3, 4), 2.0), GroupBackdrops.MeasureFromDisc(orb, _ => (3, 4, 5), _ => 2));
        orb.Close();
    }

    [AvaloniaFact]
    public void TheBackdropsMeasureAnOrbAsTheDragDoes()
    {
        var manager = Scan(("a", ""));
        var a = Windows(manager)["a"];

        var measured = GroupBackdrops.Measure(a);
        var disc = ((IOrbDragHost)manager).DiscOf("a");

        Assert.NotNull(measured);
        Assert.Equal(new Point(disc.X, disc.Y), measured!.Value.Centre);
        Assert.Equal(OrbWindow.UnitsPerDip(a), measured.Value.Scale);
    }
}
