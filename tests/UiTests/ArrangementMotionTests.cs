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

        var c = Windows(manager)["c"];
        c.Position = new PixelPoint(settled["c"].X - 70, settled["c"].Y);
        host.SettleAfterDrop(new[] { "c" });

        // b was landed on its slot outright; only c is gliding now.
        Assert.Equal(settled["b"], b.Position);
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
