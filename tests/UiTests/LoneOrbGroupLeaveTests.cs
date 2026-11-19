using System.Reflection;
using Avalonia;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.UiTests;

// CB-332: a user group of one orb. Dragged far enough from where it started the
// orb leaves (the menu's Leave group), and the group is easy to grab. A real
// SessionManager and the real drag session; only the pointer is skipped.
[Collection("Settings")]
public class LoneOrbGroupLeaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb-332-" + Guid.NewGuid());
    private readonly List<string> _groups = new();
    private readonly List<string> _keys = new();
    private readonly OrbweaverSettings.OrbPlacement? _mainAnchor = OrbweaverSettings.ArrangeAnchor;
    private readonly AutoGroupBy _by = OrbweaverSettings.AutoGroupBy;
    private readonly bool _outlines = OrbweaverSettings.GroupOutlinesEnabled;

    public LoneOrbGroupLeaveTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        OrbDragHost.Current = null;
        foreach (var key in _keys) { OrbweaverSettings.UnassignOrbGroup(key); OrbweaverSettings.ClearOrbPosition(key); }
        foreach (var id in _groups) OrbweaverSettings.DeleteOrbGroup(id);
        OrbweaverSettings.ArrangeAnchor = _mainAnchor;
        OrbweaverSettings.AutoGroupBy = _by;
        OrbweaverSettings.GroupOutlinesEnabled = _outlines;
        foreach (var g in OrbweaverSettings.OrbGroupList().Where(g => OrbAutoGroups.IsAuto(g.Id)).ToList())
            OrbweaverSettings.DeleteOrbGroup(g.Id);
        GroupBackdrops.Update(Array.Empty<BackdropSpec>());
        GroupBackdrops.ParkAll();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static Dictionary<string, OrbWindow> Windows(SessionManager m) =>
        (Dictionary<string, OrbWindow>)typeof(SessionManager)
            .GetField("_windows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;

    private static void Glide(SessionManager m)
    {
        typeof(SessionManager).GetField("_arrangeAnimStart", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(m, Environment.TickCount64 - 700);
        typeof(SessionManager).GetMethod("OnArrangeAnimTick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(m, new object?[] { null, EventArgs.Empty });
    }

    // "lone" is alone in a user group; "other" stays in main. Free mode unless arranged.
    private (SessionManager M, string Group, string Key) Setup(bool arranged)
    {
        OrbweaverSettings.ArrangeAnchor = null;
        foreach (var (id, cli) in new[] { ("lone", "claude"), ("other", "codex") })
            File.WriteAllText(Path.Combine(_dir, id + ".txt"),
                System.Text.Json.JsonSerializer.Serialize(new SessionStatus
                {
                    Cli = cli, Cwd = "/Users/user/cb332-" + id, Title = "",
                    SessionPid = Environment.ProcessId, TermProgram = "iTerm.app", Tty = "/dev/ttys004",
                }));
        OrbweaverSettings.ClaudeCodeEnabled = true;
        OrbweaverSettings.CodexEnabled = true;
        OrbweaverSettings.GrokEnabled = true;
        var m = new SessionManager(_dir);
        m.ScanAndUpdate();

        var group = OrbweaverSettings.CreateOrbGroup("circle");
        _groups.Add(group.Id);
        var key = OrbGroupMenu.KeyOf("lone", m.AllStatuses);
        _keys.Add(key);
        _keys.Add(OrbGroupMenu.KeyOf("other", m.AllStatuses));
        OrbweaverSettings.AssignTeamToGroup(key, Array.Empty<string>(), group.Id);
        m.ReapplyArrangement();
        if (arranged) m.ArrangeOrbsInPattern();
        Glide(m);
        Assert.Equal(arranged, m.IsArranged);
        OrbDragHost.Current = m;
        return (m, group.Id, key);
    }

    private static SnapOutcome DragLead(SessionManager m, string id, int dx, int dy)
    {
        var session = new OrbDragSession(m, id, DragSource.Orb, false, OrbWindow.UnitsPerDip(Windows(m)[id]));
        session.Move(dx, dy);
        return session.End();
    }

    // The distance comes from the start discs, never the drawn outline.
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALoneOrbDraggedFarEnoughLeavesItsGroupWhenArranged(bool outlines)
    {
        OrbweaverSettings.GroupOutlinesEnabled = outlines;
        var (m, group, key) = Setup(arranged: true);
        Assert.Equal(group, ((IOrbDragHost)m).GroupIdOf("lone"));

        var outcome = DragLead(m, "lone", 600, 0);

        Assert.IsType<SnapOutcome.LeaveSole>(outcome);
        Assert.Equal(OrbGroups.Main, OrbweaverSettings.OrbGroupMembershipFor(key));
        Assert.Equal(OrbGroups.Main, ((IOrbDragHost)m).GroupIdOf("lone"));
        // The group had no one left, so it went with it.
        Assert.DoesNotContain(OrbweaverSettings.OrbGroupList(), g => g.Id == group);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALoneOrbDraggedFarEnoughGoesLooseWhenFree(bool outlines)
    {
        OrbweaverSettings.GroupOutlinesEnabled = outlines;
        var (m, group, key) = Setup(arranged: false);

        var outcome = DragLead(m, "lone", 600, 0);

        Assert.IsType<SnapOutcome.LeaveToFree>(outcome);
        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor(key));
        Assert.True(OrbGroups.IsPredefined(((IOrbDragHost)m).GroupIdOf("lone")));
        Assert.DoesNotContain(OrbweaverSettings.OrbGroupList(), g => g.Id == group);
    }

    [AvaloniaFact]
    public void ALoneOrbNudgedWithinTheGroupStaysWhenFree()
    {
        var (m, group, key) = Setup(arranged: false);

        var outcome = DragLead(m, "lone", 30, 10);

        Assert.IsType<SnapOutcome.NoChange>(outcome);
        Assert.Equal(group, OrbweaverSettings.OrbGroupMembershipFor(key));
    }

    // Arranged, the groups sit side by side, so a short drag may join a
    // neighbour; what it must not do is leave on a nudge.
    [AvaloniaFact]
    public void ALoneOrbNudgedDoesNotLeaveWhenArranged()
    {
        var (m, _, _) = Setup(arranged: true);

        Assert.IsNotType<SnapOutcome.LeaveSole>(DragLead(m, "lone", 30, 10));
    }

    [AvaloniaFact]
    public void ThePreviewOfALeaveReadsAsLeavingBeforeTheRelease()
    {
        var (m, _, _) = Setup(arranged: true);
        var w = Windows(m)["lone"];
        var session = new OrbDragSession(m, "lone", DragSource.Orb, false, OrbWindow.UnitsPerDip(w));

        session.Move(600, 0);

        // Mid-drag, so the group's backdrop already fades as it does for any leave.
        Assert.IsType<SnapOutcome.LeaveSole>(
            OrbDragCommit.Preview(m, "lone", OrbWindow.UnitsPerDip(w), 600, 0));
        session.End();
    }

    [AvaloniaFact]
    public void ADragOnTheWidenedMarginMovesTheGroupAnchor()
    {
        var (m, group, _) = Setup(arranged: false);
        var before = OrbweaverSettings.OrbGroupList().Single(g => g.Id == group).Anchor;
        var w = Windows(m)["lone"];
        var start = w.Position;

        var session = new OrbDragSession(m, "lone", DragSource.Backdrop, false, OrbWindow.UnitsPerDip(w));
        session.Move(500, 0);
        var outcome = session.End();

        Assert.IsType<SnapOutcome.NoChange>(outcome);   // a whole-group drag never changes membership
        Assert.True(session.WholeGroup);
        Assert.Equal(group, ((IOrbDragHost)m).GroupIdOf("lone"));
        Assert.NotEqual(before, OrbweaverSettings.OrbGroupList().Single(g => g.Id == group).Anchor);
        Assert.NotEqual(start, w.Position);
    }

    [AvaloniaFact]
    public void DroppedWithinJoinDistanceOfAnotherGroupTheLoneOrbJoinsAndItsGroupIsPruned()
    {
        // Arranged: main is a snap target (free mode pairs loose teams instead).
        var (m, group, key) = Setup(arranged: true);
        IOrbDragHost host = m;
        var lone = host.DiscOf("lone");
        var other = host.DiscOf("other");

        // Dropped on the other orb: far from where it started, yet it joins.
        var outcome = DragLead(m, "lone", (int)Math.Round(other.X - lone.X), (int)Math.Round(other.Y - lone.Y));

        Assert.Equal(new SnapOutcome.Join(OrbGroups.Main), outcome);
        Assert.Equal(OrbGroups.Main, host.GroupIdOf("lone"));
        Assert.DoesNotContain(OrbweaverSettings.OrbGroupList(), g => g.Id == group);
    }

    // CB-281: an auto group with one orb in it. Grouping by CLI makes every
    // orb here the sole occupant of its own auto group.
    private (SessionManager M, string Auto, string Key) AutoSetup(bool arranged)
    {
        OrbweaverSettings.ArrangeAnchor = null;
        OrbweaverSettings.AutoGroupBy = AutoGroupBy.Cli;
        foreach (var (id, cli) in new[] { ("lone", "claude"), ("other", "codex") })
            File.WriteAllText(Path.Combine(_dir, id + ".txt"),
                System.Text.Json.JsonSerializer.Serialize(new SessionStatus
                {
                    Cli = cli, Cwd = "/Users/user/cb332-auto-" + id, Title = "",
                    SessionPid = Environment.ProcessId, TermProgram = "iTerm.app", Tty = "/dev/ttys004",
                }));
        OrbweaverSettings.ClaudeCodeEnabled = true;
        OrbweaverSettings.CodexEnabled = true;
        OrbweaverSettings.GrokEnabled = true;
        var m = new SessionManager(_dir) { AutoGroupRepoRootOf = _ => null };
        m.ScanAndUpdate();
        var key = OrbGroupMenu.KeyOf("lone", m.AllStatuses);
        _keys.Add(key);
        _keys.Add(OrbGroupMenu.KeyOf("other", m.AllStatuses));
        var auto = OrbAutoGroups.IdFor(AutoGroupBy.Cli, "claude");
        Assert.Equal(auto, ((IOrbDragHost)m).GroupIdOf("lone"));
        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor(key));
        if (arranged) m.ArrangeOrbsInPattern();
        Glide(m);
        Assert.Equal(arranged, m.IsArranged);
        OrbDragHost.Current = m;
        return (m, auto, key);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALoneOrbDraggedOutOfItsAutoGroupIsWrittenIntoMainAndStaysThere(bool arranged)
    {
        var (m, auto, key) = AutoSetup(arranged);

        var outcome = DragLead(m, "lone", -5000, 0);

        Assert.True(arranged ? outcome is SnapOutcome.LeaveSole : outcome is SnapOutcome.LeaveToFree);
        // Explicit, not forgotten: forgetting it would let the classifier put it back.
        Assert.Equal(OrbGroups.Main, OrbweaverSettings.OrbGroupMembershipFor(key));
        m.ScanAndUpdate();
        Assert.Equal(OrbGroups.Main, ((IOrbDragHost)m).GroupIdOf("lone"));
        Assert.NotEqual(auto, ((IOrbDragHost)m).GroupIdOf("lone"));
    }

    [AvaloniaFact]
    public void ALoneOrbNudgedInItsAutoGroupStaysAutomaticWithNothingWritten()
    {
        var (m, auto, key) = AutoSetup(arranged: false);

        Assert.IsType<SnapOutcome.NoChange>(DragLead(m, "lone", 20, 0));

        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor(key));
        Assert.Equal(auto, ((IOrbDragHost)m).GroupIdOf("lone"));
    }

    // The lead's call on hidden members: the visible orb is a sole occupant, so
    // dragging it out takes it out, but the group is not pruned — its hidden
    // members are still assigned to it and it comes back when they do.
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void AGroupWhoseOtherMembersAreHiddenKeepsThemWhenItsVisibleOrbLeaves(bool arranged)
    {
        var (m, group, key) = Setup(arranged);
        const string hidden = "key:cb332-hidden-member";
        _keys.Add(hidden);
        OrbweaverSettings.AssignTeamToGroup(hidden, Array.Empty<string>(), group);

        var outcome = DragLead(m, "lone", 600, 0);

        Assert.True(arranged ? outcome is SnapOutcome.LeaveSole : outcome is SnapOutcome.LeaveToFree);
        Assert.NotEqual(group, ((IOrbDragHost)m).GroupIdOf("lone"));
        Assert.Contains(OrbweaverSettings.OrbGroupList(), g => g.Id == group);
        Assert.Equal(group, OrbweaverSettings.OrbGroupMembershipFor(hidden));
    }
}
