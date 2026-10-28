using System.Reflection;
using Avalonia;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.UiTests;

// CB-283 M2/M5/M6 through SessionManager's arrangement animation, on a fake
// clock: a user-initiated move springs each orb into its slot (S4's
// SpringGlide), staggered StaggerMs apart, overshooting a little and landing
// exactly; reduce motion is a plain glide with no stagger; a drop settles the
// re-laid-out orbs from wherever they were let go. A scan's own re-fit keeps the
// eased glide.
[Collection("Settings")]
public class ArrangementMotionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb-motion-" + Guid.NewGuid());
    private readonly List<string> _groups = new();
    private readonly List<string> _keys = new();
    private readonly OrbweaverSettings.OrbPlacement? _mainAnchor = OrbweaverSettings.ArrangeAnchor;
    private long _clock = 1_000_000;

    public ArrangementMotionTests()
    {
        Directory.CreateDirectory(_dir);
        MotionPolicy.ReduceMotionProbe = () => false;
        OrbweaverSettings.ArrangeAnchor = null;
    }

    public void Dispose()
    {
        MotionPolicy.ReduceMotionProbe = null;
        foreach (var key in _keys) OrbweaverSettings.UnassignOrbGroup(key);
        foreach (var id in _groups) OrbweaverSettings.DeleteOrbGroup(id);
        OrbweaverSettings.ArrangeAnchor = _mainAnchor;
        GroupBackdrops.Update(Array.Empty<BackdropSpec>());
        GroupBackdrops.ParkAll();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string id, string cli) =>
        File.WriteAllText(Path.Combine(_dir, id + ".txt"),
            System.Text.Json.JsonSerializer.Serialize(new SessionStatus
            {
                Cli = cli,
                Cwd = "/Users/user/motion",
                SessionPid = Environment.ProcessId,
                TermProgram = "iTerm.app",
                Tty = "/dev/ttys004",
            }));

    private SessionManager Scan(params (string Id, string Cli)[] sessions)
    {
        foreach (var (id, cli) in sessions) Write(id, cli);
        OrbweaverSettings.ClaudeCodeEnabled = true;
        OrbweaverSettings.CodexEnabled = true;
        OrbweaverSettings.GrokEnabled = true;
        var manager = new SessionManager(_dir) { Now = () => _clock };
        manager.ScanAndUpdate();
        return manager;
    }

    private static T Field<T>(SessionManager m, string name) =>
        (T)typeof(SessionManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;

    private static Dictionary<string, OrbWindow> Windows(SessionManager m) =>
        Field<Dictionary<string, OrbWindow>>(m, "_windows");

    private static Dictionary<string, (PixelPoint From, PixelPoint To)>? Targets(SessionManager m) =>
        (Dictionary<string, (PixelPoint From, PixelPoint To)>?)typeof(SessionManager)
            .GetField("_arrangeAnimTargets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m);

    private static bool Springing(SessionManager m) =>
        typeof(SessionManager).GetField("_arrangeSprings", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(m) is not null;

    private void Tick(SessionManager m, long ms)
    {
        _clock += ms;
        typeof(SessionManager).GetMethod("OnArrangeAnimTick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(m, new object?[] { null, EventArgs.Empty });
    }

    // Runs the animation to its end at 60 Hz, recording every frame.
    private List<Dictionary<string, PixelPoint>> RunToEnd(SessionManager m, int maxFrames = 600)
    {
        var frames = new List<Dictionary<string, PixelPoint>>();
        for (var i = 0; i < maxFrames && Targets(m) is not null; i++)
        {
            Tick(m, 16);
            frames.Add(Windows(m).ToDictionary(kv => kv.Key, kv => kv.Value.Position));
        }

        Assert.Null(Targets(m));
        return frames;
    }

    // The disc's uniform scale. With no drag squash on it, the matrix is just
    // the pop.
    private static double Pop(OrbWindow w) => w.SquashMatrix.M11;

    // Runs the animation to its end, recording each orb's pop scale per frame.
    private List<Dictionary<string, double>> PopsToEnd(SessionManager m)
    {
        var frames = new List<Dictionary<string, double>>();
        for (var i = 0; i < 600 && Targets(m) is not null; i++)
        {
            Tick(m, 16);
            frames.Add(Windows(m).ToDictionary(kv => kv.Key, kv => Pop(kv.Value)));
        }

        Assert.Null(Targets(m));
        return frames;
    }

    private static bool PassedTarget(int from, int to, int at) =>
        to > from ? at > to : to < from && at < to;

    [AvaloniaFact]
    public void ArrangingSpringsEachOrbInStaggeredOvershootingAndLandingExactly()
    {
        var manager = Scan(("a", ""), ("b", "codex"), ("c", "grok"));
        manager.ArrangeOrbsInPattern();
        Assert.True(Springing(manager));

        var targets = new Dictionary<string, (PixelPoint From, PixelPoint To)>(Targets(manager)!);
        var moving = targets.Where(t => t.Value.From != t.Value.To).Select(t => t.Key).ToList();
        Assert.True(moving.Count >= 2);

        // The stagger: 10 ms in, only the first orb may have started.
        Tick(manager, 10);
        var started = moving.Count(id => Windows(manager)[id].Position != targets[id].From);
        Assert.True(started <= 1, $"{started} orbs started inside the first stagger step");

        var frames = RunToEnd(manager);

        // Lands exactly on every slot, and at least one orb sprang past its slot
        // on the way (Spring.Pop, about 8% overshoot).
        foreach (var (id, (_, to)) in targets) Assert.Equal(to, Windows(manager)[id].Position);
        Assert.Contains(frames, f => moving.Any(id =>
            PassedTarget(targets[id].From.X, targets[id].To.X, f[id].X) ||
            PassedTarget(targets[id].From.Y, targets[id].To.Y, f[id].Y)));
    }

    [AvaloniaFact]
    public void WithReduceMotionItIsAPlainGlideNoStaggerNoOvershootAboutAHundredFiftyMs()
    {
        MotionPolicy.ReduceMotionProbe = () => true;
        var manager = Scan(("a", ""), ("b", "codex"), ("c", "grok"));
        manager.ArrangeOrbsInPattern();

        var targets = new Dictionary<string, (PixelPoint From, PixelPoint To)>(Targets(manager)!);
        var moving = targets.Where(t => t.Value.From != t.Value.To).Select(t => t.Key).ToList();

        Tick(manager, 10);
        Assert.All(moving, id => Assert.NotEqual(targets[id].From, Windows(manager)[id].Position));   // no stagger

        var frames = RunToEnd(manager);
        Assert.True(frames.Count <= 10, $"{frames.Count} frames after the first: longer than ~150 ms");
        Assert.DoesNotContain(frames, f => moving.Any(id =>
            PassedTarget(targets[id].From.X, targets[id].To.X, f[id].X) ||
            PassedTarget(targets[id].From.Y, targets[id].To.Y, f[id].Y)));
        foreach (var (id, (_, to)) in targets) Assert.Equal(to, Windows(manager)[id].Position);
    }

    private (SessionManager Manager, Dictionary<string, PixelPoint> Settled) FreeGroup()
    {
        var manager = Scan(("loose", ""), ("b", "codex"), ("c", "grok"));
        var group = OrbweaverSettings.CreateOrbGroup("circle");
        _groups.Add(group.Id);
        foreach (var id in new[] { "b", "c" })
        {
            var key = OrbGroupMenu.KeyOf(id, manager.AllStatuses);
            _keys.Add(key);
            OrbweaverSettings.MoveOrbToGroup(key, group.Id);
        }

        manager.ReapplyArrangement();
        return (manager, Windows(manager).ToDictionary(kv => kv.Key, kv => kv.Value.Position));
    }

    [AvaloniaFact]
    public void ANoChangeDropSpringsTheTeamBackFromWhereItWasLetGo()
    {
        var (manager, settled) = FreeGroup();
        var b = Windows(manager)["b"];
        var dropped = new PixelPoint(settled["b"].X + 90, settled["b"].Y + 40);
        b.Position = dropped;

        ((IOrbDragHost)manager).SettleAfterDrop(Array.Empty<string>());

        // Not snapped: it starts where it was let go, and springs home.
        Assert.Equal(dropped, b.Position);
        Assert.True(Springing(manager));
        RunToEnd(manager);
        Assert.Equal(settled["b"], b.Position);
        Assert.Equal(settled["c"], Windows(manager)["c"].Position);
    }

    // --- M2: the arriving orbs pop --------------------------------------------

    [AvaloniaFact]
    public void AJoinPopsTheArrivingOrbsOnlyAndEndsEveryDiscAtRest()
    {
        var (manager, settled) = FreeGroup();
        var b = Windows(manager)["b"];
        b.Position = new PixelPoint(settled["b"].X + 90, settled["b"].Y + 40);

        // "ghost" has no orb: it is skipped, not popped and not a crash.
        ((IOrbDragHost)manager).SettleAfterDrop(new[] { "b", "ghost" });
        var frames = PopsToEnd(manager);

        // b swells past 1.14 and dips under 0.93 (DropPop's 1.15 and 0.92, read
        // off 16 ms frames); nobody else's disc changes; everything ends at 1.
        Assert.Contains(frames, f => f["b"] > 1.14);
        Assert.Contains(frames, f => f["b"] < 0.93);
        Assert.All(frames, f => Assert.Equal(1.0, f["c"]));
        Assert.All(frames, f => Assert.Equal(1.0, f["loose"]));
        Assert.All(Windows(manager).Values, w => Assert.Equal(Matrix.Identity, w.SquashMatrix));
        Assert.Equal(settled["b"], b.Position);
    }

    [AvaloniaFact]
    public void AnArrivingOrbAlreadyOnItsSlotStillPops()
    {
        var (manager, _) = FreeGroup();

        ((IOrbDragHost)manager).SettleAfterDrop(new[] { "b" });

        // Nothing moves, but there is an animation, and it is the pop.
        Assert.Empty(Targets(manager)!);
        var frames = PopsToEnd(manager);
        Assert.Contains(frames, f => f["b"] > 1.14);
        Assert.True(frames.Count * 16 >= DropPop.DurationMs, $"ended after {frames.Count} frames, before the pop did");
        Assert.Equal(1.0, Pop(Windows(manager)["b"]));
    }

    [AvaloniaFact]
    public void WithReduceMotionNothingPopsAndAnArrivalInPlaceStartsNoAnimation()
    {
        MotionPolicy.ReduceMotionProbe = () => true;
        var (manager, settled) = FreeGroup();
        IOrbDragHost host = manager;

        host.SettleAfterDrop(new[] { "b" });
        Assert.Null(Targets(manager));

        var b = Windows(manager)["b"];
        b.Position = new PixelPoint(settled["b"].X + 90, settled["b"].Y);
        host.SettleAfterDrop(new[] { "b" });
        Assert.All(PopsToEnd(manager), f => Assert.Equal(1.0, f["b"]));
        Assert.Equal(settled["b"], b.Position);
    }

    [AvaloniaFact]
    public void AnArrivalWithNoOrbOnScreenPopsNothingAndMovesNothing()
    {
        var (manager, settled) = FreeGroup();
        IOrbDragHost host = manager;

        // Nobody moved and the only arrival has no orb: no animation.
        host.SettleAfterDrop(new[] { "ghost" });
        Assert.Null(Targets(manager));

        // Somebody moved: the glide runs, with nothing to pop.
        var b = Windows(manager)["b"];
        b.Position = new PixelPoint(settled["b"].X + 90, settled["b"].Y);
        host.SettleAfterDrop(new[] { "ghost" });
        Assert.Null(Field<Dictionary<string, long>?>(manager, "_arrangePops"));
        Assert.All(PopsToEnd(manager), f => Assert.Equal(1.0, f["b"]));
    }

    [AvaloniaFact]
    public void AnOrbThatLeavesMidPopIsSkippedAndTheAnimationStillEnds()
    {
        var (manager, settled) = FreeGroup();
        var b = Windows(manager)["b"];
        b.Position = new PixelPoint(settled["b"].X + 90, settled["b"].Y);
        ((IOrbDragHost)manager).SettleAfterDrop(new[] { "b" });
        Tick(manager, 16);

        // Its window goes while it is popping. The frames and the landing both
        // skip it, and the animation still ends. (Only the window is taken
        // out, so this is the animation's own guard, not a scan's removal.)
        Windows(manager).Remove("b");
        PopsToEnd(manager);
        Assert.Null(Field<Dictionary<string, long>?>(manager, "_arrangePops"));
    }

    // Found while testing M2: a glide whose orb's window went mid-flight was
    // never stepped, so never Done, and the animation never ended, which left
    // every later re-layout deferred behind it for good. A scan removes the
    // window of a session that ends (CB-161), so this is the app's own path.
    [AvaloniaFact]
    public void ASpringWhoseOrbLeavesMidFlightStillLetsTheAnimationEnd()
    {
        var (manager, settled) = FreeGroup();
        Windows(manager)["b"].Position = new PixelPoint(settled["b"].X + 90, settled["b"].Y);
        Windows(manager)["c"].Position = new PixelPoint(settled["c"].X - 60, settled["c"].Y);
        ((IOrbDragHost)manager).SettleAfterDrop(Array.Empty<string>());
        Assert.Equal(2, Targets(manager)!.Count);
        Tick(manager, 16);

        Windows(manager).Remove("b");
        RunToEnd(manager);
        Assert.Equal(settled["c"], Windows(manager)["c"].Position);
    }

    // --- M5: the release's momentum carries into the settle ------------------

    // Drops b `offset` px right of its slot, settles it with `fling`, and
    // returns b's x and y on every frame plus its slot.
    private (List<PixelPoint> Path, PixelPoint Slot) Settle(
        SessionManager manager, Dictionary<string, PixelPoint> settled, int offset, Fling? fling)
    {
        var b = Windows(manager)["b"];
        b.Position = new PixelPoint(settled["b"].X + offset, settled["b"].Y);
        IOrbDragHost host = manager;
        if (fling is { } f) host.SettleAfterDrop(Array.Empty<string>(), f);
        else host.SettleAfterDrop(Array.Empty<string>());

        var path = new List<PixelPoint>();
        for (var i = 0; i < 600 && Targets(manager) is not null; i++)
        {
            Tick(manager, 16);
            path.Add(b.Position);
        }

        Assert.Null(Targets(manager));
        Assert.Equal(settled["b"], b.Position);   // ends exactly on its slot
        return (path, settled["b"]);
    }

    [AvaloniaFact]
    public void AFlungDropOvershootsFurtherInTheThrowDirectionButWithinTheCap()
    {
        var (manager, settled) = FreeGroup();
        var units = SessionManager.UsableUnits(OrbWindow.UnitsPerDip(Windows(manager)["b"]));
        var cap = Math.Max(SpringGlide.MaxOvershootFraction * 90, SpringGlide.MaxOvershootDip * units);

        // Let go 90 px right of the slot, once still and once thrown hard
        // left, at it: the throw carries it further past the slot, never past
        // the cap (+1 px for rounding to whole pixels).
        var (still, slot) = Settle(manager, settled, 90, null);
        var (thrown, _) = Settle(manager, settled, 90, new Fling(new[] { "b" }, -6000, 0));

        var stillPast = slot.X - still.Min(p => p.X);
        var thrownPast = slot.X - thrown.Min(p => p.X);
        Assert.True(thrownPast > stillPast + 2, $"thrown {thrownPast} px past, still {stillPast} px");
        Assert.True(thrownPast <= cap + 1, $"thrown {thrownPast} px past, cap {cap}");
        Assert.All(thrown, p => Assert.Equal(slot.Y, p.Y));   // the throw was purely horizontal
    }

    [AvaloniaFact]
    public void AnOrbThrownOnItsOwnSlotCarriesOnInTheThrowsDirectionAndSpringsBack()
    {
        var (manager, settled) = FreeGroup();
        var units = SessionManager.UsableUnits(OrbWindow.UnitsPerDip(Windows(manager)["b"]));

        // Dropped exactly on its slot, flung upwards: it still moves, up only,
        // by at most the 12 DIP cap, and lands back on the slot.
        var (path, slot) = Settle(manager, settled, 0, new Fling(new[] { "b" }, 0, -3000));

        Assert.Contains(path, p => p.Y < slot.Y);
        Assert.All(path, p => Assert.InRange(slot.Y - p.Y, -1, SpringGlide.MaxOvershootDip * units + 1));
        Assert.All(path, p => Assert.Equal(slot.X, p.X));
    }

    [AvaloniaFact]
    public void AZeroVelocityDropIsExactlyTheSettleWithNoFling()
    {
        var (manager, settled) = FreeGroup();

        var (plain, _) = Settle(manager, settled, 90, null);
        var (zero, _) = Settle(manager, settled, 90, new Fling(new[] { "b" }, 0, 0));
        var (none, _) = Settle(manager, settled, 90, default(Fling));

        Assert.Equal(plain, zero);
        Assert.Equal(plain, none);

        // And at zero velocity an orb already on its slot is not moved at all.
        ((IOrbDragHost)manager).SettleAfterDrop(Array.Empty<string>(), new Fling(new[] { "b" }, 0, 0));
        Assert.Null(Targets(manager));
    }

    [AvaloniaFact]
    public void WithReduceMotionAFlingIsIgnoredPlainGlideNoOvershoot()
    {
        MotionPolicy.ReduceMotionProbe = () => true;
        var (manager, settled) = FreeGroup();

        var (path, slot) = Settle(manager, settled, 90, new Fling(new[] { "b" }, -6000, -6000));

        Assert.True(path.Count <= 10, $"{path.Count} frames: longer than the ~150 ms plain glide");
        Assert.All(path, p => Assert.True(p.X >= slot.X && p.Y == slot.Y, $"{p} overshot {slot}"));

        // Thrown on its own slot under reduce motion: nothing to glide, no animation.
        ((IOrbDragHost)manager).SettleAfterDrop(Array.Empty<string>(), new Fling(new[] { "b" }, 0, -3000));
        Assert.Null(Targets(manager));
    }

    [AvaloniaFact]
    public void AFlingOfOrbsNotOnScreenThrowsNothing()
    {
        var (manager, _) = FreeGroup();

        ((IOrbDragHost)manager).SettleAfterDrop(Array.Empty<string>(), new Fling(new[] { "ghost" }, 4000, 0));

        Assert.Null(Targets(manager));
    }

    [AvaloniaFact]
    public void ADropThatMovesNobodyStartsNoGlide()
    {
        var (manager, _) = FreeGroup();

        ((IOrbDragHost)manager).SettleAfterDrop(Array.Empty<string>());

        Assert.Null(Targets(manager));
    }

    [AvaloniaFact]
    public void ASecondDropLandsTheFirstGlideBeforeStartingItsOwn()
    {
        var (manager, settled) = FreeGroup();
        var b = Windows(manager)["b"];
        b.Position = new PixelPoint(settled["b"].X + 120, settled["b"].Y);
        IOrbDragHost host = manager;
        host.SettleAfterDrop(new[] { "b" });
        Tick(manager, 16);

        Assert.NotEqual(1.0, Pop(b));   // mid-pop

        var c = Windows(manager)["c"];
        c.Position = new PixelPoint(settled["c"].X - 70, settled["c"].Y);
        host.SettleAfterDrop(new[] { "c" });

        // b was landed on its slot outright, its pop at rest; only c is gliding now.
        Assert.Equal(settled["b"], b.Position);
        Assert.Equal(1.0, Pop(b));
        Assert.Equal(new[] { "c" }, Targets(manager)!.Keys);
        RunToEnd(manager);
        Assert.Equal(settled["c"], c.Position);
    }

    [AvaloniaFact]
    public void AFreeModeReflowWaitsForTheGlideInsteadOfYankingIt()
    {
        var (manager, settled) = FreeGroup();
        var b = Windows(manager)["b"];
        var dropped = new PixelPoint(settled["b"].X + 90, settled["b"].Y);
        b.Position = dropped;
        ((IOrbDragHost)manager).SettleAfterDrop(Array.Empty<string>());

        manager.ReapplyArrangement();     // a reflow arriving mid-glide
        Assert.Equal(dropped, b.Position);
        Assert.True(Field<bool>(manager, "_refitPending"));

        RunToEnd(manager);
        Assert.False(Field<bool>(manager, "_refitPending"));
        Assert.Equal(settled["b"], b.Position);
    }

    [AvaloniaFact]
    public void AnOrbArrivingOnItsOwnStillGetsTheEasedGlideNotTheSpring()
    {
        var manager = Scan(("a", ""), ("b", "codex"));
        manager.ArrangeOrbsInPattern();
        RunToEnd(manager);

        Write("c", "grok");
        manager.ScanAndUpdate();

        Assert.NotNull(Targets(manager));
        Assert.False(Springing(manager));
        RunToEnd(manager);
    }
}
