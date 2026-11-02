using System.Reflection;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;

namespace Orbweaver.UiTests;

// CB-303: a dropped orb stays where it is released. A real SessionManager in
// free mode, the real pointer handlers and a fake motion clock; each case runs
// the release's settle to its end and asserts where the orb finished. The
// settle used to reflow an unpinned orb back to its stack slot before the drop
// pinned it, so these fail on 1.0.2.
[Collection("Settings")]
public class OrbDropSticksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb-303-" + Guid.NewGuid());
    private readonly List<string> _groups = new();
    private readonly List<string> _keys = new();
    private readonly OrbweaverSettings.OrbPlacement? _mainAnchor = OrbweaverSettings.ArrangeAnchor;
    private readonly Func<long> _motionClock = OrbWindow.MotionClock;
    private long _clock = 1_000_000;

    public OrbDropSticksTests()
    {
        Directory.CreateDirectory(_dir);
        MotionPolicy.ReduceMotionProbe = () => false;
        OrbweaverSettings.ArrangeAnchor = null;
        OrbWindow.MotionClock = () => _clock;
    }

    public void Dispose()
    {
        OrbWindow.MotionClock = _motionClock;
        OrbDragHost.Current = null;
        MotionPolicy.ReduceMotionProbe = null;
        foreach (var key in _keys) { OrbweaverSettings.UnassignOrbGroup(key); OrbweaverSettings.ClearOrbPosition(key); }
        foreach (var id in _groups) OrbweaverSettings.DeleteOrbGroup(id);
        OrbweaverSettings.ArrangeAnchor = _mainAnchor;
        GroupBackdrops.Update(Array.Empty<BackdropSpec>());
        GroupBackdrops.ParkAll();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SessionManager Scan(params (string Id, string Cli)[] sessions)
    {
        foreach (var (id, cli) in sessions)
            File.WriteAllText(Path.Combine(_dir, id + ".txt"),
                System.Text.Json.JsonSerializer.Serialize(new SessionStatus
                {
                    Cli = cli, Cwd = "/Users/user/cb303-" + id, Title = "",
                    SessionPid = Environment.ProcessId, TermProgram = "iTerm.app", Tty = "/dev/ttys004",
                }));
        OrbweaverSettings.ClaudeCodeEnabled = true;
        OrbweaverSettings.CodexEnabled = true;
        OrbweaverSettings.GrokEnabled = true;
        var m = new SessionManager(_dir) { Now = () => _clock };
        m.ScanAndUpdate();
        foreach (var w in Windows(m).Values) _keys.Add(w.PositionKey);
        OrbDragHost.Current = m;
        return m;
    }

    private static Dictionary<string, OrbWindow> Windows(SessionManager m) =>
        (Dictionary<string, OrbWindow>)typeof(SessionManager)
            .GetField("_windows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;

    private static object? Targets(SessionManager m) =>
        typeof(SessionManager).GetField("_arrangeAnimTargets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m);

    private void RunToEnd(SessionManager m)
    {
        for (var i = 0; i < 600 && Targets(m) is not null; i++)
        {
            _clock += 16;
            typeof(SessionManager).GetMethod("OnArrangeAnimTick", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(m, new object?[] { null, EventArgs.Empty });
        }
    }

    // Press, move in steps of 16 ms, then wait `holdMs` before releasing.
    private void Drag(OrbWindow w, int dx, int dy, long holdMs)
    {
        var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        w.RaiseEvent(new PointerPressedEventArgs(w, pointer, w, new Point(10, 10), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
        for (var i = 1; i <= 10; i++)
        {
            _clock += 16;
            w.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, w, pointer, w,
                new Point(10 + dx * i / 10.0, 10 + dy * i / 10.0), 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
                KeyModifiers.None));
        }
        _clock += holdMs;
        w.RaiseEvent(new PointerReleasedEventArgs(w, pointer, w, new Point(10 + dx, 10 + dy), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
    }

    private static PixelPoint Moved(PixelPoint p, int dx, int dy) => new(p.X + dx, p.Y + dy);

    private static void Press(OrbWindow w, Avalonia.Input.Pointer p) =>
        w.RaiseEvent(new PointerPressedEventArgs(w, p, w, new Point(10, 10), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));

    private void MoveTo(OrbWindow w, Avalonia.Input.Pointer p, int dx, int dy)
    {
        _clock += 16;
        w.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, w, p, w,
            new Point(10 + dx, 10 + dy), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
            KeyModifiers.None));
    }

    private static void Release(OrbWindow w, Avalonia.Input.Pointer p, int dx, int dy) =>
        w.RaiseEvent(new PointerReleasedEventArgs(w, p, w, new Point(10 + dx, 10 + dy), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));

    private static Avalonia.Input.Pointer NewPointer() =>
        new(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(4000)]
    public void AnUnpinnedLooseOrbStaysWhereItIsDropped(long hold)
    {
        var m = Scan(("solo", "claude"));
        var w = Windows(m)["solo"];
        Assert.False(w.IsPinned);
        var dropped = Moved(w.Position, -300, 200);

        Drag(w, -300, 200, hold);
        RunToEnd(m);

        Assert.Equal(dropped, w.Position);
        Assert.True(w.IsPinned);
    }

    [AvaloniaFact]
    public void ADroppedOrbStillStaysOnItsSecondDrag()
    {
        var m = Scan(("solo", "claude"));
        var w = Windows(m)["solo"];
        Drag(w, -300, 200, 0);
        RunToEnd(m);
        var dropped = Moved(w.Position, -50, 40);

        Drag(w, -50, 40, 0);
        RunToEnd(m);

        Assert.Equal(dropped, w.Position);
    }

    [AvaloniaFact]
    public void AnAlreadyPinnedLooseOrbStaysWhereItIsDropped()
    {
        var m = Scan(("solo", "claude"));
        var w = Windows(m)["solo"];
        w.PinAt(new PixelPoint(400, 300));
        var dropped = Moved(w.Position, -150, 120);

        Drag(w, -150, 120, 0);
        RunToEnd(m);

        Assert.Equal(dropped, w.Position);
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(4000)]
    public void AUserGroupOrbDraggedOutStaysWhereItIsDroppedAndIsLoose(long hold)
    {
        var m = Scan(("ga", "claude"), ("gb", "codex"));
        var g = OrbweaverSettings.CreateOrbGroup("line");
        _groups.Add(g.Id);
        var ws = Windows(m);
        OrbweaverSettings.AssignTeamToGroup(ws["ga"].PositionKey, Array.Empty<string>(), g.Id);
        OrbweaverSettings.AssignTeamToGroup(ws["gb"].PositionKey, Array.Empty<string>(), g.Id);
        m.ReapplyArrangement();
        RunToEnd(m);
        var w = ws["ga"];
        var dropped = Moved(w.Position, -400, 300);

        Drag(w, -400, 300, hold);
        RunToEnd(m);

        Assert.Equal(dropped, w.Position);
        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor(w.PositionKey));
    }

    [AvaloniaFact]
    public void ALooseTeamLeadDragCarriesItsFollowersAndTheyAllStay()
    {
        var m = Scan(("tl", ""), ("tm", "codex"));
        m.StatusFor("tm")!.Lead = "tl";
        m.ReapplyArrangement();
        RunToEnd(m);
        var ws = Windows(m);
        var leadDropped = Moved(ws["tl"].Position, -400, 300);
        var memberDropped = Moved(ws["tm"].Position, -400, 300);

        Drag(ws["tl"], -400, 300, 0);
        RunToEnd(m);

        Assert.Equal(leadDropped, ws["tl"].Position);
        Assert.Equal(memberDropped, ws["tm"].Position);
    }

    [AvaloniaFact]
    public void ALooseTeamMemberStaysWhereItIsDropped()
    {
        var m = Scan(("tl", ""), ("tm", "codex"));
        m.StatusFor("tm")!.Lead = "tl";
        m.ReapplyArrangement();
        RunToEnd(m);
        var w = Windows(m)["tm"];
        var dropped = Moved(w.Position, -100, 100);

        Drag(w, -100, 100, 0);
        RunToEnd(m);

        Assert.Equal(dropped, w.Position);
    }

    [AvaloniaFact]
    public void AFreeModeUserGroupMemberKeepsItsDroppedOffsetFromItsLead()
    {
        var m = Scan(("ul", ""), ("um", "codex"));
        m.StatusFor("um")!.Lead = "ul";
        var g = OrbweaverSettings.CreateOrbGroup("circle");
        _groups.Add(g.Id);
        var ws = Windows(m);
        OrbweaverSettings.AssignTeamToGroup(ws["ul"].PositionKey, new[] { ws["um"].PositionKey }, g.Id);
        m.ReapplyArrangement();
        RunToEnd(m);
        var w = ws["um"];
        var dropped = Moved(w.Position, 80, 60);

        Drag(w, 80, 60, 0);
        RunToEnd(m);

        Assert.Equal(dropped, w.Position);
        OrbweaverSettings.SetOrbMemberOffset(w.SoundKey, null);
    }

    [AvaloniaFact]
    public void AScanWhileTheOrbIsHeldStillDoesNotMoveItAndTheReleaseLandsUnderThePointer()
    {
        var m = Scan(("solo", "claude"));
        var w = Windows(m)["solo"];
        var pointer = NewPointer();
        Press(w, pointer);
        MoveTo(w, pointer, -300, 200);
        var held = w.Position;
        Assert.True(w.HeldByDrag);

        File.WriteAllText(Path.Combine(_dir, "newcomer.txt"), System.Text.Json.JsonSerializer.Serialize(new SessionStatus
        {
            Cli = "codex", Cwd = "/Users/user/cb303-new", SessionPid = Environment.ProcessId,
            TermProgram = "iTerm.app", Tty = "/dev/ttys004",
        }));
        m.ScanAndUpdate();
        RunToEnd(m);
        Assert.Equal(held, w.Position);

        _clock += 4000;
        Release(w, pointer, -300, 200);
        RunToEnd(m);

        Assert.Equal(held, w.Position);
        Assert.False(w.HeldByDrag);
    }

    [AvaloniaFact]
    public void AScanWhileHeldMovesNothingEvenIfTheReflowWouldHaveMovedTheOrbAndTheDropIsCommittedUnderThePointer()
    {
        var m = Scan(("solo", "claude"));
        var w = Windows(m)["solo"];
        var pointer = NewPointer();
        Press(w, pointer);
        MoveTo(w, pointer, -300, 200);
        var underPointer = w.Position;

        // Something else displaces the held window; the release still commits
        // where the pointer drew it.
        w.Position = new PixelPoint(5, 5);
        Release(w, pointer, -300, 200);
        RunToEnd(m);

        Assert.Equal(underPointer, w.Position);
    }

    [AvaloniaFact]
    public void AReleaseWithAThrowStillGlidesOutAndBackToTheDropPoint()
    {
        var m = Scan(("solo", "claude"));
        var w = Windows(m)["solo"];
        var dropped = Moved(w.Position, -300, 200);

        Drag(w, -300, 200, 0);
        var seenAway = false;
        for (var i = 0; i < 600 && Targets(m) is not null; i++)
        {
            _clock += 16;
            typeof(SessionManager).GetMethod("OnArrangeAnimTick", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(m, new object?[] { null, EventArgs.Empty });
            if (w.Position != dropped) seenAway = true;
        }

        Assert.True(seenAway);
        Assert.Equal(dropped, w.Position);
    }

    [AvaloniaFact]
    public void ADropThatJoinsAUserGroupLandsInItsSlotAndIsNotPinned()
    {
        var m = Scan(("ga", "claude"), ("gb", "codex"), ("solo", "grok"));
        var g = OrbweaverSettings.CreateOrbGroup("line");
        _groups.Add(g.Id);
        var ws = Windows(m);
        OrbweaverSettings.AssignTeamToGroup(ws["ga"].PositionKey, Array.Empty<string>(), g.Id);
        OrbweaverSettings.AssignTeamToGroup(ws["gb"].PositionKey, Array.Empty<string>(), g.Id);
        m.ReapplyArrangement();
        RunToEnd(m);
        var solo = ws["solo"];
        var target = ws["ga"].Position;

        Drag(solo, target.X - solo.Position.X, target.Y - solo.Position.Y, 0);
        RunToEnd(m);

        Assert.Equal(g.Id, OrbweaverSettings.OrbGroupMembershipFor(solo.PositionKey));
        Assert.False(solo.IsPinned);
    }

    [AvaloniaFact]
    public void InArrangedModeADropThatChangesNothingSpringsBackToItsSlotAndIsNotPinned()
    {
        var m = Scan(("a", "claude"), ("b", "codex"), ("c", "grok"));
        m.ArrangeOrbsInPattern();
        RunToEnd(m);
        var w = Windows(m)["a"];
        var slot = w.Position;

        Drag(w, 5, 5, 0);
        RunToEnd(m);

        Assert.Equal(slot, w.Position);
        Assert.False(w.IsPinned);
    }

    [AvaloniaFact]
    public void AnEndedSessionClearsTheHeldFlagAndCommitsWhereTheOrbIs()
    {
        var m = Scan(("solo", "claude"));
        var w = Windows(m)["solo"];
        var session = new OrbDragSession(m, "solo", DragSource.Orb, false, 1.0);

        session.Move(-120, 80);
        Assert.True(w.HeldByDrag);
        var at = w.Position;
        session.End();
        RunToEnd(m);

        Assert.False(w.HeldByDrag);
        Assert.Equal(at, w.Position);
        Assert.True(w.IsPinned);
    }

    [AvaloniaFact]
    public void AWholeGroupDragHoldsItsOrbsAndReleasesThem()
    {
        var m = Scan(("ga", "claude"), ("gb", "codex"));
        var g = OrbweaverSettings.CreateOrbGroup("line");
        _groups.Add(g.Id);
        var ws = Windows(m);
        OrbweaverSettings.AssignTeamToGroup(ws["ga"].PositionKey, Array.Empty<string>(), g.Id);
        OrbweaverSettings.AssignTeamToGroup(ws["gb"].PositionKey, Array.Empty<string>(), g.Id);
        m.ReapplyArrangement();
        RunToEnd(m);
        var session = new OrbDragSession(m, "ga", DragSource.Backdrop, false, 1.0);

        session.Move(60, 40);
        Assert.True(ws["ga"].HeldByDrag && ws["gb"].HeldByDrag);
        session.End();

        Assert.False(ws["ga"].HeldByDrag || ws["gb"].HeldByDrag);
    }
}
