using System.Reflection;
using Avalonia;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.UiTests;

// CB-268 QA: free-mode (F1-F4, T1) stress through real SessionManager scans, beyond the
// one-group cases in SessionManagerGroupsTests.
[Collection("Settings")]
public class SessionManagerFreeModeQaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb-freeqa-" + Guid.NewGuid());
    private readonly List<string> _groups = new();
    private readonly List<string> _keys = new();
    private readonly OrbweaverSettings.OrbPlacement? _mainAnchor = OrbweaverSettings.ArrangeAnchor;
    private readonly bool _showOrbs = OrbweaverSettings.ShowOrbs;

    public SessionManagerFreeModeQaTests()
    {
        Directory.CreateDirectory(_dir);
        Reset();
    }

    private static void Reset()
    {
        GroupBackdrops.PreviewPair(null);
        GroupBackdrops.Update(Array.Empty<BackdropSpec>());
        GroupBackdrops.ParkAll();
        GroupBackdrops.Highlight(null);
    }

    public void Dispose()
    {
        Reset();
        OrbweaverSettings.ShowOrbs = _showOrbs;
        foreach (var key in _keys) OrbweaverSettings.UnassignOrbGroup(key);
        foreach (var id in _groups) OrbweaverSettings.DeleteOrbGroup(id);
        OrbweaverSettings.ArrangeAnchor = _mainAnchor;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly string[] Clis = { "", "codex", "grok" };

    // Superseded keeps one orb per (pid, CLI) and every fake file shares this process's pid, so a
    // scan can hold at most one session per CLI: three orbs. The long-stack case is covered by
    // Hana's obstacle sweep at the ComputeGroups level instead.
    private void Write(string id, string cli, string title) =>
        File.WriteAllText(Path.Combine(_dir, id + ".txt"),
            System.Text.Json.JsonSerializer.Serialize(new SessionStatus
            {
                Cli = cli,
                Cwd = "/Users/user/freeqa",
                Title = title,
                SessionPid = Environment.ProcessId,
                TermProgram = "iTerm.app",
                Tty = "/dev/ttys004",
            }));

    private SessionManager Scan(int count)
    {
        for (var i = 0; i < count; i++) Write("s" + i, Clis[i % 3], "t" + i);
        OrbweaverSettings.ClaudeCodeEnabled = true;
        OrbweaverSettings.CodexEnabled = true;
        OrbweaverSettings.GrokEnabled = true;
        var manager = new SessionManager(_dir);
        manager.ScanAndUpdate();
        Assert.True(manager.AllStatuses.Count == count && Windows(manager).Count == count,
            $"scan made {manager.AllStatuses.Count} statuses and {Windows(manager).Count} windows of {count}");
        return manager;
    }

    private string KeyOf(SessionManager m, string id)
    {
        var key = OrbGroupMenu.KeyOf(id, m.AllStatuses);
        _keys.Add(key);
        return key;
    }

    private OrbGroup Group(string shape = "circle")
    {
        var g = OrbweaverSettings.CreateOrbGroup(shape);
        _groups.Add(g.Id);
        return g;
    }

    private static Dictionary<string, OrbWindow> Windows(SessionManager m) =>
        (Dictionary<string, OrbWindow>)typeof(SessionManager)
            .GetField("_windows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;

    private static PixelRect Rect(OrbWindow w)
    {
        var size = SessionManager.OrbPixels(w, w.Screens.Primary?.Scaling ?? 1);
        return new PixelRect(w.Position, new PixelSize(size, size));
    }

    private static PixelRect Bounds(SessionManager m, string groupId)
    {
        var placements = (System.Collections.IEnumerable)typeof(SessionManager)
            .GetField("_groupPlacements", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;
        foreach (var g in placements)
        {
            var t = g.GetType();
            if ((string)t.GetProperty("GroupId")!.GetValue(g)! == groupId)
                return (PixelRect)t.GetProperty("Bounds")!.GetValue(g)!;
        }
        throw new InvalidOperationException("no placement for " + groupId);
    }

    private static void Glide(SessionManager m)
    {
        typeof(SessionManager).GetField("_arrangeAnimStart", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(m, Environment.TickCount64 - 700);
        typeof(SessionManager).GetMethod("OnArrangeAnimTick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(m, new object?[] { null, EventArgs.Empty });
    }

    [AvaloniaFact]
    public void ALooseOrbPinnedOnTheGroupsAnchorMakesTheGroupMoveClearNotTheOrb()
    {
        OrbweaverSettings.ArrangeAnchor = null;
        var m = Scan(3);                                   // s0 loose; s1, s2 grouped
        var g = Group("circle");
        foreach (var i in new[] { 1, 2 }) OrbweaverSettings.MoveOrbToGroup(KeyOf(m, "s" + i), g.Id);
        m.ReapplyArrangement();
        var w = Windows(m);
        var anchor = OrbweaverSettings.OrbGroupFor(g.Id)!.Anchor!;

        var pinAt = new PixelPoint(anchor.X - 20, anchor.Y - 20);
        // Control: the pin really does land on the group's box as it was laid out before the pin.
        Assert.True(Bounds(m, g.Id).Intersects(new PixelRect(pinAt, Rect(w["s0"]).Size)));
        w["s0"].PinAt(pinAt);
        m.ReapplyArrangement();

        Assert.Equal(pinAt, w["s0"].Position);                       // the loose orb stays where it was dropped
        Assert.False(Bounds(m, g.Id).Intersects(Rect(w["s0"])));      // the group yields
    }

    [AvaloniaFact]
    public void ArrangeThenUnarrangeTwiceWithAPinnedLooseOrbLeavesUserGroupAndLooseOrbPut()
    {
        OrbweaverSettings.ArrangeAnchor = null;
        var m = Scan(3);
        var g = Group("star");
        foreach (var i in new[] { 1, 2 }) OrbweaverSettings.MoveOrbToGroup(KeyOf(m, "s" + i), g.Id);
        m.ReapplyArrangement();
        var w = Windows(m);
        w["s0"].PinAt(new PixelPoint(w["s0"].Position.X - 300, w["s0"].Position.Y + 40));
        m.ReapplyArrangement();

        var before = w.ToDictionary(p => p.Key, p => p.Value.Position);
        var anchorBefore = OrbweaverSettings.OrbGroupFor(g.Id)!.Anchor;

        for (var round = 0; round < 2; round++)
        {
            m.ArrangeOrbsInPattern(); Glide(m); Assert.True(m.IsArranged);
            foreach (var i in new[] { 1, 2 }) Assert.Equal(before["s" + i], w["s" + i].Position);
            Assert.Equal(anchorBefore, OrbweaverSettings.OrbGroupFor(g.Id)!.Anchor);

            m.ArrangeOrbsInPattern(); Glide(m); Assert.False(m.IsArranged);
            foreach (var p in before) Assert.Equal(p.Value, w[p.Key].Position);
            Assert.Equal(anchorBefore, OrbweaverSettings.OrbGroupFor(g.Id)!.Anchor);
        }
    }

    [AvaloniaFact]
    public void ANestedTeamIsWholeInFreeModeArrangedAndAfterItsLeadEnds()
    {
        OrbweaverSettings.ArrangeAnchor = null;
        var m = Scan(3);
        m.StatusFor("s1")!.Lead = "s0";
        m.StatusFor("s2")!.Lead = "s1";                  // nested
        var g = Group("circle");
        foreach (var i in new[] { 0, 1, 2 }) OrbweaverSettings.MoveOrbToGroup(KeyOf(m, "s" + i), g.Id);
        m.ReapplyArrangement();
        IOrbDragHost host = m;

        void AssertWhole()
        {
            foreach (var id in new[] { "s0", "s1", "s2" }) Assert.Equal(g.Id, host.GroupIdOf(id));
        }

        AssertWhole();
        m.ArrangeOrbsInPattern(); Glide(m); AssertWhole();
        m.ArrangeOrbsInPattern(); Glide(m); AssertWhole();

        // The lead (root) ends: the members keep the team's group (T3/T4).
        typeof(SessionManager).GetMethod("SyncOrbGroups", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(m, null);
        var statuses = (Dictionary<string, SessionStatus>)typeof(SessionManager)
            .GetField("_statuses", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;
        statuses.Remove("s0");
        typeof(SessionManager).GetMethod("SyncOrbGroups", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(m, null);
        Assert.Equal(g.Id, host.GroupIdOf("s1"));
        Assert.Equal(g.Id, host.GroupIdOf("s2"));
    }

    // Warren's bug, end to end through the real host: drag a member of a laid-out group, re-lay-out, and the member
    // must be at lead + offset (not back in the fan); Reset puts it back in the fan.
    [AvaloniaFact]
    public void ADraggedMemberStaysAtItsLeadPlusOffsetThroughRelayoutAndArrangeAndResetFansItAgain()
    {
        OrbweaverSettings.ArrangeAnchor = null;
        var m = Scan(3);                                 // s0 lead, s1 member, s2 loose
        m.StatusFor("s1")!.Lead = "s0";
        var g = Group("circle");
        foreach (var i in new[] { 0, 1 }) OrbweaverSettings.MoveOrbToGroup(KeyOf(m, "s" + i), g.Id);
        m.ReapplyArrangement();
        var w = Windows(m);
        IOrbDragHost host = m;
        var key = w["s1"].SoundKey;
        try
        {
            var fanned = w["s1"].Position;
            var leadAt = w["s0"].Position;
            var anchorBefore = OrbweaverSettings.OrbGroupFor(g.Id)!.Anchor;
            var boxBefore = Bounds(m, g.Id);

            // "Drag" the member: put its window somewhere else and let the drag release save the offset.
            w["s1"].Position = new PixelPoint(leadAt.X + 140, leadAt.Y + 110);
            host.SaveMemberOffset("s1");
            var saved = OrbweaverSettings.OrbMemberOffsetFor(key);
            Assert.NotNull(saved);
            Assert.NotEqual(fanned, w["s1"].Position);                    // control: it really is somewhere else now

            PixelPoint Expected()
            {
                var l = host.DiscOf("s0"); var mem = host.DiscOf("s1");
                var u = OrbWindow.UnitsPerDip(w["s1"]);
                return new PixelPoint((int)Math.Round(l.X + saved!.Value.Dx * u), (int)Math.Round(l.Y + saved.Value.Dy * u));
            }

            void AssertAtOffset(string when)
            {
                var l = host.DiscOf("s0"); var mem = host.DiscOf("s1");
                var u = OrbWindow.UnitsPerDip(w["s1"]);
                Assert.True(Math.Abs((mem.X - l.X) / u - saved!.Value.Dx) < 1.5 && Math.Abs((mem.Y - l.Y) / u - saved.Value.Dy) < 1.5,
                    $"{when}: member centre is ({(mem.X - l.X) / u:F1},{(mem.Y - l.Y) / u:F1}) DIP from its lead, saved offset {saved}");
            }

            m.ReapplyArrangement();
            AssertAtOffset("after a free-mode relayout");
            Assert.Equal(leadAt, w["s0"].Position);                        // the lead did not move
            Assert.Equal(anchorBefore, OrbweaverSettings.OrbGroupFor(g.Id)!.Anchor);
            Assert.Equal(boxBefore, Bounds(m, g.Id));

            m.ArrangeOrbsInPattern(); Glide(m);
            AssertAtOffset("while arranged");
            m.ArrangeOrbsInPattern(); Glide(m);
            AssertAtOffset("after un-arranging");

            // Reset: the offset is forgotten and the member is fanned again.
            m.ReturnOrbToStack("s1");
            Assert.Null(OrbweaverSettings.OrbMemberOffsetFor(key));
            Assert.Equal(fanned, w["s1"].Position);
        }
        finally { OrbweaverSettings.SetOrbMemberOffset(key, null); }
    }
}
