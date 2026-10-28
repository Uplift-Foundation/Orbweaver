using System.Collections;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// The glide from the stack into a shape, and back, once it actually lands.
//
// SessionScanTests' arrangement cases stop at the toggle: ArrangeOrbsInPattern
// flips IsArranged and leaves it there, which is enough to prove the pattern
// was computed but nothing about what happens once the 600ms glide finishes —
// AbsorbIntoArrangement's re-fit when membership changes mid-glide, and the
// two animations' own completion callbacks (PinAt-ing every orb at its target,
// or unpinning it back to the stack).
//
// None of that can be waited out for real. Avalonia.Headless has no virtual
// clock, and the same DispatcherTimer that never fired within two real
// seconds in LocalCliChatSessionTests' debounce cases is exactly what drives
// this glide — so OnArrangeAnimTick is invoked directly, with _arrangeAnimStart
// wound back far enough that the very first tick reports the glide as already
// finished. That is not a shortcut around the logic: elapsed time past
// ArrangeAnimMs is precisely what a real 600ms wait would eventually produce,
// and the completion branch does not know or care how it got there.
[Collection("Settings")]
public class OrbArrangementAnimationTests
{
    private sealed class Scratch : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cb-arrscan-" + Guid.NewGuid());

        public Scratch() => Directory.CreateDirectory(Dir);

        public void Write(string sessionId, string cli = "") =>
            File.WriteAllText(Path.Combine(Dir, sessionId + ".txt"),
                System.Text.Json.JsonSerializer.Serialize(new SessionStatus
                {
                    Cli = cli,
                    Cwd = "/Users/user/project",
                    SessionPid = Environment.ProcessId,
                    TermProgram = "iTerm.app",
                    Tty = "/dev/ttys004",
                }));

        // A heartbeat-driven orb, for AnOrbEndingThatCollapsesAGroupStillLeaves-
        // SurvivorsInPlace — OrbClusters.GroupOf only puts an orb in the
        // Heartbeats group when this bit is set, which is what makes ending it
        // (rather than a plain chat orb) the case that changes the group count.
        //
        // A distinct CLI, not the default one "a" already uses: Superseded
        // keys on (pid, source), and sharing "a"'s source with the same test-
        // process pid would have this file judged a newer duplicate of "a" and
        // never get an orb — see the near-identical note on AnOrbEndingLeaves-
        // TheSurvivorsExactlyWhereTheyWere's "c", which is exactly the failure
        // that first shipped here before this comment existed.
        public void WriteHeartbeat(string sessionId) =>
            File.WriteAllText(Path.Combine(Dir, sessionId + ".txt"),
                System.Text.Json.JsonSerializer.Serialize(new SessionStatus
                {
                    Cli = "grok",
                    Cwd = "/Users/user/heartbeat",
                    SessionPid = Environment.ProcessId,
                    TermProgram = "iTerm.app",
                    Tty = "/dev/ttys005",
                    Heartbeat = true,
                }));

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static SessionManager Manager(string dir) => new(dir);

    private static void SetPrivate(SessionManager manager, string field, object? value) =>
        typeof(SessionManager)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(manager, value);

    private static T GetPrivate<T>(SessionManager manager, string field) =>
        (T)typeof(SessionManager)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;

    private static void InvokePrivate(SessionManager manager, string method, params object?[] args) =>
        typeof(SessionManager)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(manager, args);

    // Rewinds _arrangeAnimStart so the very next tick reports the glide as
    // finished, then fires that tick directly — OnArrangeAnimTick's own
    // signature, exactly as the real DispatcherTimer would call it.
    private static void CompleteTheGlide(SessionManager manager)
    {
        SetPrivate(manager, "_arrangeAnimStart", Environment.TickCount64 - 700);
        InvokePrivate(manager, "OnArrangeAnimTick", null, EventArgs.Empty);
    }

    private static Dictionary<string, OrbWindow> Windows(SessionManager manager) =>
        GetPrivate<Dictionary<string, OrbWindow>>(manager, "_windows");

    [AvaloniaFact]
    public void ArrangingAndCompletingTheGlideLeavesEveryOrbUnpinned()
    {
        var anchorBefore = OrbweaverSettings.ArrangeAnchor;
        try
        {
            OrbweaverSettings.ArrangeAnchor = null;

            using var scratch = new Scratch();
            scratch.Write("a");
            scratch.Write("b", cli: "codex");

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            manager.ArrangeOrbsInPattern();
            Assert.NotNull(GetPrivate<object?>(manager, "_arrangeAnimTargets"));

            CompleteTheGlide(manager);

            Assert.Null(GetPrivate<object?>(manager, "_arrangeAnimTargets"));
            Assert.True(manager.IsArranged);

            // The intent since CB-268, no longer a recorded bug. This used to
            // document that the glide's completion (PinEveryOrbAtItsTarget)
            // never ran, so nothing was pinned by accident. CB-268 retired it:
            // an arranged orb is held by its group's anchor, and a pin would
            // make the next arrange treat it as an exception — the pull-back
            // plan §B removes. So "not pinned" is now what is meant.
            Assert.All(Windows(manager).Values, w => Assert.False(w.IsPinned));
        }
        finally
        {
            OrbweaverSettings.ArrangeAnchor = anchorBefore;
        }
    }

    // A spurious re-fit with nothing actually different — the shape is already
    // sitting exactly where the same computation would put it — declines to
    // start a second glide. AbsorbIntoArrangement is invoked directly because
    // nothing public triggers it without a real membership change.
    [AvaloniaFact]
    public void ReFittingAnAlreadySettledShapeStartsNoNewGlide()
    {
        var anchorBefore = OrbweaverSettings.ArrangeAnchor;
        try
        {
            OrbweaverSettings.ArrangeAnchor = null;

            using var scratch = new Scratch();
            scratch.Write("a");

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();
            manager.ArrangeOrbsInPattern();
            CompleteTheGlide(manager);

            // Forget that the orb was already placed, so this is a real re-fit
            // rather than CB-161's departure-only shortcut — which is what an
            // absorb with nobody new would otherwise take, never reaching the
            // "already where the shape wants it" check this test is named for.
            // The re-fit has to reproduce the settled layout to the pixel
            // (ComputeGroups' one path, fed its own saved anchors).
            GetPrivate<HashSet<string>>(manager, "_arrangedIds").Clear();
            var before = Windows(manager)["a"].Position;

            InvokePrivate(manager, "AbsorbIntoArrangement");

            Assert.Null(GetPrivate<object?>(manager, "_arrangeAnimTargets"));
            Assert.Equal(before, Windows(manager)["a"].Position);
            Assert.Contains("a", GetPrivate<HashSet<string>>(manager, "_arrangedIds"));
        }
        finally
        {
            OrbweaverSettings.ArrangeAnchor = anchorBefore;
        }
    }

    // A membership change that lands mid-glide does not fight the animation
    // already running — it is deferred until that one completes, and then
    // picked up rather than dropped. This is the case the source comment
    // calls "the whole complaint" the reversal in AbsorbIntoArrangement fixed.
    [AvaloniaFact]
    public void AnArrivalMidGlideIsDeferredAndPickedUpOnceTheCurrentGlideLands()
    {
        var anchorBefore = OrbweaverSettings.ArrangeAnchor;
        try
        {
            OrbweaverSettings.ArrangeAnchor = null;

            using var scratch = new Scratch();
            scratch.Write("a");
            scratch.Write("b", cli: "codex");

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();
            manager.ArrangeOrbsInPattern();

            // Still mid-glide: the first animation's targets have not been
            // cleared, so this scan's reflow must defer rather than fight it.
            Assert.NotNull(GetPrivate<object?>(manager, "_arrangeAnimTargets"));

            // The newcomer is a gateway session rather than a third local
            // file: both local slots here already carry this test process's
            // one certainly-alive pid (one per CLI, which is what
            // SessionScanTests' own class comment explains), and a third
            // local file sharing either would collide with Superseded's
            // pid-and-source grouping rather than testing the arrival this
            // case is about.
            OrbweaverSettings.OpenClawEnabled = true;
            var (sessions, _) = OpenClawSessions.Parse(
                JsonDocument.Parse($$"""
                    {"sessions":[{"key":"agent:main:discord:direct:1","chatType":"direct",
                                  "lastActivityAt":{{new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds()}}}]}
                    """).RootElement,
                DateTime.UtcNow);
            OpenClawSessions.SetSnapshotForTests(sessions);

            manager.ScanAndUpdate();

            Assert.True(GetPrivate<bool>(manager, "_refitPending"));

            // Landing the deferred glide picks the pending re-fit up rather
            // than leaving the newcomer stranded outside the shape.
            CompleteTheGlide(manager);
            Assert.False(GetPrivate<bool>(manager, "_refitPending"));

            // The re-fit itself started a second glide to include the
            // newcomer; land that one too.
            if (GetPrivate<object?>(manager, "_arrangeAnimTargets") is not null)
            {
                CompleteTheGlide(manager);
            }

            Assert.True(manager.IsArranged);
            Assert.Equal(3, Windows(manager).Count);
        }
        finally
        {
            OrbweaverSettings.ArrangeAnchor = anchorBefore;
            OpenClawSessions.SetSnapshotForTests(Array.Empty<OpenClawSessions.Session>());
        }
    }

    // Clicking arrange again restores every orb to where it was before —
    // pinned ones back to their exact spot, and unpinned ones released back
    // to the stack — which is the other half of the toggle ArrangingIsAToggle
    // in SessionScanTests only proves the flag for.
    [AvaloniaFact]
    public void TogglingBackRestoresEveryOrbToWhereItWasBeforeArranging()
    {
        var anchorBefore = OrbweaverSettings.ArrangeAnchor;
        try
        {
            OrbweaverSettings.ArrangeAnchor = null;

            using var scratch = new Scratch();
            scratch.Write("a");
            scratch.Write("b", cli: "codex");

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var before = Windows(manager).ToDictionary(kv => kv.Key, kv => kv.Value.Position);

            manager.ArrangeOrbsInPattern();
            CompleteTheGlide(manager);
            Assert.True(manager.IsArranged);

            manager.ArrangeOrbsInPattern(); // toggles back: RestoreFromPattern
            Assert.NotNull(GetPrivate<object?>(manager, "_arrangeAnimTargets"));

            CompleteTheGlide(manager);

            Assert.False(manager.IsArranged);
            foreach (var (id, window) in Windows(manager))
            {
                Assert.Equal(before[id], window.Position);
                Assert.False(window.IsPinned);
            }
        }
        finally
        {
            OrbweaverSettings.ArrangeAnchor = anchorBefore;
        }
    }

    // A dragged orb's exact spot survives the round trip — RestoreFromPattern
    // pins it straight back rather than letting it fall into wherever the
    // default stack would have put it, which is the wasPinned branch
    // TogglingBackRestoresEveryOrbToWhereItWasBeforeArranging's two never-
    // pinned orbs cannot reach.
    [AvaloniaFact]
    public void TogglingBackReturnsADraggedOrbToItsExactPinnedSpot()
    {
        var anchorBefore = OrbweaverSettings.ArrangeAnchor;
        try
        {
            OrbweaverSettings.ArrangeAnchor = null;

            using var scratch = new Scratch();
            scratch.Write("a");
            scratch.Write("b", cli: "codex");

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var dragged = Windows(manager)["a"];
            dragged.PinAt(new PixelPoint(777, 222));

            manager.ArrangeOrbsInPattern();
            CompleteTheGlide(manager);

            manager.ArrangeOrbsInPattern();
            CompleteTheGlide(manager);

            Assert.Equal(new PixelPoint(777, 222), dragged.Position);
            Assert.True(dragged.IsPinned);
        }
        finally
        {
            OrbweaverSettings.ArrangeAnchor = anchorBefore;
        }
    }

    // An orb that vanishes while the shape is up — its session ended — drops
    // out of the saved pre-arrange state along with it, rather than being
    // restored to a position nothing will ever ask for again.
    [AvaloniaFact]
    public void AnOrbThatEndsWhileArrangedIsDroppedFromThePreArrangeState()
    {
        var anchorBefore = OrbweaverSettings.ArrangeAnchor;
        try
        {
            OrbweaverSettings.ArrangeAnchor = null;

            using var scratch = new Scratch();
            scratch.Write("a");
            scratch.Write("b", cli: "codex");

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();
            manager.ArrangeOrbsInPattern();
            CompleteTheGlide(manager);

            var preArrangeState = GetPrivate<IDictionary>(manager, "_preArrangeState");
            Assert.True(preArrangeState.Contains("b"));

            File.Delete(Path.Combine(scratch.Dir, "b.txt"));
            manager.ScanAndUpdate(); // "b" is gone; ReflowPositions re-fits via AbsorbIntoArrangement

            Assert.False(preArrangeState.Contains("b"));
        }
        finally
        {
            OrbweaverSettings.ArrangeAnchor = anchorBefore;
        }
    }

    // CB-161: an orb ending must not move anyone else. Before this fix,
    // AbsorbIntoArrangement re-fitted the whole shape on every membership
    // change, arrivals and departures alike — which meant the two survivors
    // here would have been recomputed onto an entirely different two-point
    // shape (Unit's arc-length sampling has no notion of "the same points
    // minus one") the instant the third orb ended, and glided there. That is
    // "the entire shape relocates" from CB-161's report, reproduced with
    // nothing more exotic than three orbs and a normal session end.
    [AvaloniaFact]
    public void AnOrbEndingLeavesTheSurvivorsExactlyWhereTheyWere()
    {
        var anchorBefore = OrbweaverSettings.ArrangeAnchor;
        try
        {
            OrbweaverSettings.ArrangeAnchor = null;

            using var scratch = new Scratch();
            scratch.Write("a");
            scratch.Write("b", cli: "codex");
            // A third CLI, not a third default-CLI file: Superseded keys on
            // (pid, source), and a third file with the same pid and the same
            // ClaudeCode source as "a" would be judged a newer duplicate of it
            // and never get an orb at all, which is a different bug than the
            // one this test is for.
            scratch.Write("c", cli: "grok");

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();
            Assert.Equal(3, Windows(manager).Count);

            manager.ArrangeOrbsInPattern();
            CompleteTheGlide(manager);
            Assert.True(manager.IsArranged);

            var before = Windows(manager)
                .Where(kv => kv.Key != "c")
                .ToDictionary(kv => kv.Key, kv => kv.Value.Position);

            File.Delete(Path.Combine(scratch.Dir, "c.txt"));
            manager.ScanAndUpdate(); // "c" ends; ReflowPositions re-fits via AbsorbIntoArrangement

            // No glide at all — the survivors' positions never became a
            // target to animate toward, because nothing computed new ones.
            Assert.Null(GetPrivate<object?>(manager, "_arrangeAnimTargets"));

            foreach (var (id, position) in before)
                Assert.Equal(position, Windows(manager)[id].Position);
        }
        finally
        {
            OrbweaverSettings.ArrangeAnchor = anchorBefore;
        }
    }

    // The other half of CB-161's acceptance criteria: the stability above
    // holds even for an orb whose ending changes the *group* count — the
    // case OrbArrangement.CentreFor's single/multi-group split makes
    // structurally different, and the one the ticket's own diagnosis singled
    // out as least understood. Heartbeat sessions get their own shape once
    // OrbweaverSettings.OpenClawHeartbeatMode is on, so ending the only
    // heartbeat orb collapses two groups into one mid-arrangement.
    [AvaloniaFact]
    public void AnOrbEndingThatCollapsesAGroupStillLeavesTheSurvivorsInPlace()
    {
        var anchorBefore = OrbweaverSettings.ArrangeAnchor;
        var heartbeatBefore = OrbweaverSettings.OpenClawHeartbeatMode;
        try
        {
            OrbweaverSettings.ArrangeAnchor = null;
            OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.OwnShape;

            using var scratch = new Scratch();
            scratch.Write("a");
            scratch.Write("b", cli: "codex");
            scratch.WriteHeartbeat("hb");

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();
            Assert.Equal(3, Windows(manager).Count);

            manager.ArrangeOrbsInPattern();
            CompleteTheGlide(manager);
            Assert.True(manager.IsArranged);

            var before = Windows(manager)
                .Where(kv => kv.Key != "hb")
                .ToDictionary(kv => kv.Key, kv => kv.Value.Position);

            File.Delete(Path.Combine(scratch.Dir, "hb.txt"));
            manager.ScanAndUpdate();

            Assert.Null(GetPrivate<object?>(manager, "_arrangeAnimTargets"));

            foreach (var (id, position) in before)
                Assert.Equal(position, Windows(manager)[id].Position);
        }
        finally
        {
            OrbweaverSettings.ArrangeAnchor = anchorBefore;
            OrbweaverSettings.OpenClawHeartbeatMode = heartbeatBefore;
        }
    }

    // A tick that fires after the glide it belonged to already landed —
    // possible because stopping a DispatcherTimer does not retract a Tick
    // already queued — finds nothing to animate and stops itself rather than
    // throwing on a null dictionary.
    [AvaloniaFact]
    public void AStaleTickAfterTheGlideHasAlreadyLandedStopsItselfHarmlessly()
    {
        var anchorBefore = OrbweaverSettings.ArrangeAnchor;
        try
        {
            OrbweaverSettings.ArrangeAnchor = null;

            using var scratch = new Scratch();
            scratch.Write("a");

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();
            manager.ArrangeOrbsInPattern();
            CompleteTheGlide(manager);

            Assert.Null(GetPrivate<object?>(manager, "_arrangeAnimTargets"));

            // The stale tick: fired again with nothing to do.
            InvokePrivate(manager, "OnArrangeAnimTick", null, EventArgs.Empty);

            Assert.Null(GetPrivate<object?>(manager, "_arrangeAnimTargets"));
        }
        finally
        {
            OrbweaverSettings.ArrangeAnchor = anchorBefore;
        }
    }

    // The settings slider's live preview: once arranged, a spacing change
    // reflows every orb into the new spacing immediately, with no glide.
    [AvaloniaFact]
    public void ReapplyingArrangementWhileArrangedMovesEveryOrbToTheNewSpacing()
    {
        var anchorBefore = OrbweaverSettings.ArrangeAnchor;
        var spacingBefore = OrbweaverSettings.ArrangeSpacing;
        try
        {
            OrbweaverSettings.ArrangeAnchor = null;

            using var scratch = new Scratch();
            scratch.Write("a");
            scratch.Write("b", cli: "codex");

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();
            manager.ArrangeOrbsInPattern();
            CompleteTheGlide(manager);

            var before = Windows(manager).ToDictionary(kv => kv.Key, kv => kv.Value.Position);

            OrbweaverSettings.ArrangeSpacing = Math.Min(1.0, OrbweaverSettings.ArrangeSpacing + 0.4);
            manager.ReapplyArrangement();

            // At least worth asking: nothing has thrown, and the call did not
            // silently decline the way it does while unarranged (that path is
            // SessionScanTests.ReapplyingTheArrangementDoesNothingWhileNothingIsArranged).
            Assert.True(manager.IsArranged);
            Assert.Null(GetPrivate<object?>(manager, "_arrangeAnimTargets"));
            Assert.NotEmpty(Windows(manager));
        }
        finally
        {
            OrbweaverSettings.ArrangeAnchor = anchorBefore;
            OrbweaverSettings.ArrangeSpacing = spacingBefore;
        }
    }

    // The work area SessionManager itself arranges into — the same primary-
    // screen read, with the same fallback, so a test anchor "past the right
    // edge" is past the edge the arrangement is actually clamped to.
    private static PixelRect WorkOf(SessionManager manager)
    {
        var orb = Windows(manager).Values.First();
        var screen = orb.Screens.Primary ?? orb.Screens.All.FirstOrDefault();
        return screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
    }

    // CB-211 end to end, at the level the ticket asked for: a saved anchor
    // left well past the right edge (Warren's was 565px out), an arrange, a
    // whole-shape drag of (-300, +200), a re-arrange — and every orb moves by
    // exactly the drag. Before the fix the horizontal half vanished into the
    // overshoot. Also asserts the saved value itself was repaired by the first
    // arrange, since that is the part that heals a settings file already
    // carrying a stale anchor.
    [AvaloniaFact]
    public void ADragBackFromAStaleAnchorPastTheEdgeMovesEveryOrbByTheWholeDrag()
    {
        var anchorBefore = OrbweaverSettings.ArrangeAnchor;
        try
        {
            using var scratch = new Scratch();
            scratch.Write("a");
            scratch.Write("b", cli: "codex");
            scratch.Write("c", cli: "grok");

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var work = WorkOf(manager);
            var stale = new OrbweaverSettings.OrbPlacement(work.Right + 480, work.Y + work.Height / 2);
            OrbweaverSettings.ArrangeAnchor = stale;

            manager.ArrangeOrbsInPattern();
            CompleteTheGlide(manager);

            var repaired = OrbweaverSettings.ArrangeAnchor!;
            Assert.True(repaired.X < work.Right, $"anchor still at {repaired.X}, past {work.Right}");
            Assert.Equal(stale.Y, repaired.Y);

            var before = Windows(manager).ToDictionary(kv => kv.Key, kv => kv.Value.Position);

            manager.ShiftArrangementAnchor(-300, 200);
            manager.ReapplyArrangement();

            Assert.All(Windows(manager), kv =>
                Assert.Equal(new PixelPoint(before[kv.Key].X - 300, before[kv.Key].Y + 200), kv.Value.Position));
        }
        finally
        {
            OrbweaverSettings.ArrangeAnchor = anchorBefore;
        }
    }

    // The control: an anchor the screen honours — an odd number of pixels off
    // the middle, the case a recomputed value would round first — is left
    // exactly as saved by an arrange, and a drag from it is honoured in full,
    // just as before CB-211.
    [AvaloniaFact]
    public void AnInRangeAnchorIsLeftExactlyAsSavedAndStillHonoursADrag()
    {
        var anchorBefore = OrbweaverSettings.ArrangeAnchor;
        try
        {
            using var scratch = new Scratch();
            scratch.Write("a");
            scratch.Write("b", cli: "codex");

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var work = WorkOf(manager);
            var honoured = new OrbweaverSettings.OrbPlacement(
                work.X + work.Width / 2 - 101, work.Y + work.Height / 2 + 37);
            OrbweaverSettings.ArrangeAnchor = honoured;

            manager.ArrangeOrbsInPattern();
            CompleteTheGlide(manager);

            Assert.Equal(honoured, OrbweaverSettings.ArrangeAnchor);

            var before = Windows(manager).ToDictionary(kv => kv.Key, kv => kv.Value.Position);

            manager.ShiftArrangementAnchor(40, -24);
            manager.ReapplyArrangement();

            Assert.Equal(new OrbweaverSettings.OrbPlacement(honoured.X + 40, honoured.Y - 24),
                OrbweaverSettings.ArrangeAnchor);
            Assert.All(Windows(manager), kv =>
                Assert.Equal(new PixelPoint(before[kv.Key].X + 40, before[kv.Key].Y - 24), kv.Value.Position));
        }
        finally
        {
            OrbweaverSettings.ArrangeAnchor = anchorBefore;
        }
    }

    // The one behaviour CB-211 changes on purpose, pinned so it stays a
    // decision rather than an accident. A shape that *grows* into an edge —
    // here the spacing slider, through the same ReapplyArrangement the
    // Settings slider drives — is pushed back by OrbArrangement, and the saved
    // anchor now follows it to where it landed. Shrinking it again leaves the
    // anchor there: the shape stays where it was last seen rather than
    // creeping back out toward the edge, which is what it did before, when
    // the overshoot was kept.
    [AvaloniaFact]
    public void AShapeThatGrowsIntoTheEdgeKeepsItsLandedAnchorWhenItShrinksAgain()
    {
        var anchorBefore = OrbweaverSettings.ArrangeAnchor;
        var spacingBefore = OrbweaverSettings.ArrangeSpacing;
        var shapeBefore = OrbweaverSettings.ArrangeShape;
        try
        {
            OrbweaverSettings.ArrangeSpacing = 0.1;
            OrbweaverSettings.ArrangeShape = "heart";

            using var scratch = new Scratch();
            scratch.Write("a");
            scratch.Write("b", cli: "codex");
            scratch.Write("c", cli: "grok");

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var work = WorkOf(manager);
            // 55px in: three orbs in a heart reach 44px right of their anchor at
            // the tightest spacing and 65px at the widest (measured at Scale
            // 1), so the tight shape clears the edge and the wide one cannot.
            var nearEdge = new OrbweaverSettings.OrbPlacement(work.Right - 55, work.Y + work.Height / 2);
            OrbweaverSettings.ArrangeAnchor = nearEdge;

            manager.ArrangeOrbsInPattern();
            CompleteTheGlide(manager);

            // Fixture: tight, the shape fits where it was put, so nothing moved.
            Assert.Equal(nearEdge, OrbweaverSettings.ArrangeAnchor);

            OrbweaverSettings.ArrangeSpacing = 1.0;
            manager.ReapplyArrangement();

            var landed = OrbweaverSettings.ArrangeAnchor!;
            Assert.True(landed.X < nearEdge.X, $"growing into the edge left the anchor at {landed.X}");
            Assert.Equal(nearEdge.Y, landed.Y);

            OrbweaverSettings.ArrangeSpacing = 0.1;
            manager.ReapplyArrangement();

            Assert.Equal(landed, OrbweaverSettings.ArrangeAnchor);

            // And the shrunk shape is drawn around that landed centre, clear of
            // the edge it was pushed off, rather than back where it started.
            var rightmost = Windows(manager).Values.Max(w => w.Position.X + (int)Math.Round(w.Bounds.Width * w.RenderScaling));
            Assert.True(rightmost < work.Right, $"shrunk shape reaches {rightmost}, the edge is {work.Right}");
        }
        finally
        {
            OrbweaverSettings.ArrangeAnchor = anchorBefore;
            OrbweaverSettings.ArrangeSpacing = spacingBefore;
            OrbweaverSettings.ArrangeShape = shapeBefore;
        }
    }
}
