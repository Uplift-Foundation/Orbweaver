using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-268 M1 (magnetic pull), M4 (drag squash) and M6 (reduce motion), driven
// through a real OrbDragSession and real OrbWindows with a FAKE CLOCK: every
// frame is a millisecond timestamp the test chooses, so nothing races wall time.
//
// [Collection("Settings")]: OrbWindow reads settings when constructed, and the
// motion seams (MotionClock, MotionPolicy) are process-wide.
[Collection("Settings")]
public class OrbWindowMotionTests : IDisposable
{
    private long _now = 100_000;

    public OrbWindowMotionTests()
    {
        OrbWindow.MotionClock = () => _now;
        MotionPolicy.ReduceMotionProbe = () => false;
    }

    public void Dispose()
    {
        OrbWindow.MotionClock = () => Environment.TickCount64;
        MotionPolicy.ReduceMotionProbe = null;
        OrbDragHost.Current = null;
    }

    private sealed class Roster : IOrbDragHost
    {
        public Dictionary<string, OrbWindow> Windows { get; } = new();
        public bool Arranged { get; set; }
        public bool IsArranged => Arranged;
        // Where ReapplyArrangement puts an orb: its slot, instantly, as the real
        // arrangement does.
        public Dictionary<string, PixelPoint> Slots { get; } = new();
        public List<IReadOnlyCollection<string>> Settles { get; } = new();
        public void SettleAfterDrop(IReadOnlyCollection<string> arrivingIds)
        {
            Settles.Add(arrivingIds.ToList());
            ReapplyArrangement();
        }

        // CB-283 M5: what the release threw, as the host is handed it.
        public List<Fling> Flings { get; } = new();
        public void SettleAfterDrop(IReadOnlyCollection<string> arrivingIds, Fling fling)
        {
            Flings.Add(fling);
            SettleAfterDrop(arrivingIds);
        }

        public IReadOnlyList<string> TeamIdsOf(string id) => new[] { id };
        public string GroupIdOf(string id) => OrbweaverSettings.OrbGroupMembershipFor("key:" + id) ?? OrbGroups.Main;
        public IReadOnlyList<string> OrbIdsInGroup(string g) => Windows.Keys.Where(id => GroupIdOf(id) == g).ToList();
        public OrbWindow? WindowOf(string id) => Windows.GetValueOrDefault(id);
        public string PositionKeyOf(string id) => "key:" + id;

        public (double X, double Y, double R) DiscOf(string id)
        {
            var p = Windows[id].Position;
            return (p.X + 28, p.Y + 28, 18);
        }

        public IReadOnlyList<SnapCandidate> CandidatesFor(IReadOnlyCollection<string> excluded) =>
            Windows.Keys.Where(id => !excluded.Contains(id) && !OrbGroups.IsPredefined(GroupIdOf(id)))
                .GroupBy(GroupIdOf)
                .Select(g => new SnapCandidate(g.Key, Array.Empty<Point>(), g.Select(DiscOf).ToList()))
                .ToList();

        public void HighlightGroup(string? groupId) { }
        public void ShiftGroupAnchor(string groupId, int dx, int dy) { }
        public void ReapplyArrangement()
        {
            foreach (var (id, slot) in Slots) Windows[id].Position = slot;
        }

        public OrbWindow Add(string id, int x, int y)
        {
            var w = new OrbWindow(id) { PositionKey = "key:" + id, Position = new PixelPoint(x, y) };
            Windows[id] = w;
            return w;
        }
    }

    private static string Uid() => Guid.NewGuid().ToString("N")[..8];

    // A (loose, at 100,100) and a user group holding B at (400,100).
    private static (Roster Roster, string A, string B) Scene()
    {
        var roster = new Roster();
        var u = Uid();
        var (a, b) = ("a-" + u, "b-" + u);
        roster.Add(a, 100, 100);
        roster.Add(b, 400, 100);
        var group = OrbweaverSettings.CreateOrbGroup().Id;
        OrbweaverSettings.AssignTeamToGroup("key:" + b, Array.Empty<string>(), group);
        OrbDragHost.Current = roster;
        return (roster, a, b);
    }

    // Moves the session's pointer to `dx` and runs `frames` 16 ms frames there.
    private void Hold(OrbDragSession session, int dx, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            _now += 16;
            session.Move(dx, 0);
        }
    }

    // --- M5: the release velocity reaches the settle ---

    [AvaloniaFact]
    public void AReleaseThrowsTheDraggedOrbsAtThePointersVelocityInDip()
    {
        var (roster, a, _) = Scene();

        // Two screen units per DIP: 40 units every 16 ms, away from B, is
        // 20 DIP per 16 ms, 1250 DIP/s.
        var session = new OrbDragSession(roster, a, DragSource.Orb, false, 2.0);
        for (var i = 1; i <= 6; i++)
        {
            _now += 16;
            session.Move(-40 * i, 0);
        }

        session.End();

        var fling = Assert.Single(roster.Flings);
        Assert.Equal(new[] { a }, fling.Ids);
        Assert.Equal(-1250, fling.X, 6);
        Assert.Equal(0, fling.Y, 6);
    }

    [AvaloniaFact]
    public void AnOrbHeldStillBeforeItIsLetGoIsThrownAtNoSpeed()
    {
        var (roster, a, _) = Scene();
        var session = new OrbDragSession(roster, a, DragSource.Orb, false, 1.0);
        for (var i = 1; i <= 6; i++)
        {
            _now += 16;
            session.Move(-40 * i, 0);
        }

        _now += FlingTracker.StaleMs + 1;   // the pointer stops, then lets go
        session.End();

        Assert.True(Assert.Single(roster.Flings).IsNone);
    }

    // --- M1: magnetic pull ---

    [AvaloniaFact]
    public void WhileTheDropWouldJoinTheTeamIsPulledAboutThirtyPercentOfTheGapAndNeverPastIt()
    {
        var (roster, a, _) = Scene();
        var session = new OrbDragSession(roster, a, DragSource.Orb, false, 1.0);

        // Pointer at +250: A's disc edge is 14 from B's. Hold, then let it settle.
        Hold(session, 250, 3);
        for (var i = 0; i < 60; i++) { _now += 16; session.Step(_now); }

        var pulled = roster.Windows[a].Position.X - (100 + 250);
        Assert.InRange(pulled, 3, 5);                // 30% of 14 = 4.2 px, rounded
        var gapAfter = 400 - roster.Windows[a].Position.X - 36;
        Assert.True(gapAfter > 0, "never crosses the target");
    }

    [AvaloniaFact]
    public void ThePullEasesInRatherThanJumping()
    {
        var (roster, a, _) = Scene();
        var session = new OrbDragSession(roster, a, DragSource.Orb, false, 1.0);

        _now += 16;
        session.Move(250, 0);
        var first = roster.Windows[a].Position.X - 350;

        for (var i = 0; i < 40; i++) { _now += 16; session.Step(_now); }
        var settled = roster.Windows[a].Position.X - 350;

        Assert.True(first < settled, $"first frame {first}, settled {settled}");
    }

    [AvaloniaFact]
    public void LeavingJoinRangeReleasesThePullBackToExactlyThePointer()
    {
        var (roster, a, _) = Scene();
        var session = new OrbDragSession(roster, a, DragSource.Orb, false, 1.0);
        Hold(session, 250, 3);
        for (var i = 0; i < 40; i++) { _now += 16; session.Step(_now); }
        Assert.NotEqual(350, roster.Windows[a].Position.X);

        // Move well out of range: the pull relaxes to nothing and the orb is
        // exactly where the pointer has it.
        Hold(session, 120, 1);
        for (var i = 0; i < 80; i++) { _now += 16; session.Step(_now); }

        Assert.Equal(220, roster.Windows[a].Position.X);
    }

    [AvaloniaFact]
    public void ReduceMotionHasNoPullAtAll()
    {
        MotionPolicy.ReduceMotionProbe = () => true;
        var (roster, a, _) = Scene();
        var session = new OrbDragSession(roster, a, DragSource.Orb, false, 1.0);

        Hold(session, 250, 3);
        for (var i = 0; i < 40; i++) { _now += 16; session.Step(_now); }

        Assert.Equal(350, roster.Windows[a].Position.X);
    }

    [AvaloniaFact]
    public void ADragThatIsNotALeadDragOrWouldNotSnapIsNeverPulled()
    {
        var (roster, a, _) = Scene();
        var session = new OrbDragSession(roster, a, DragSource.Orb, false, 1.0);

        Hold(session, 150, 5);   // nowhere near anything
        for (var i = 0; i < 40; i++) { _now += 16; session.Step(_now); }

        Assert.Equal(250, roster.Windows[a].Position.X);
    }

    // --- M4: drag squash ---

    [AvaloniaFact]
    public void ADraggedOrbStretchesAlongItsTravelAndOnlyThatOrb()
    {
        var (roster, a, b) = Scene();
        var session = new OrbDragSession(roster, a, DragSource.Orb, false, 1.0);

        // 20 DIP per 16 ms is 1250 DIP/s: full stretch.
        for (var i = 1; i <= 30; i++)
        {
            _now += 16;
            session.Move(20 * i, 0);
        }

        var m = roster.Windows[a].SquashMatrix;
        Assert.NotEqual(Matrix.Identity, m);
        Assert.InRange(m.M11, 1.07, 1.1001);   // longer along the drag (x), never past 10%
        Assert.True(m.M22 < 1);               // narrower across it
        Assert.Equal(Matrix.Identity, roster.Windows[b].SquashMatrix);
    }

    [AvaloniaFact]
    public void TheSquashNeverChangesTheWindowOrTheRootOrTheCirclesCentre()
    {
        var (roster, a, _) = Scene();
        var w = roster.Windows[a];
        var (width, height, rootWidth, rootHeight) = (w.Width, w.Height, w.FindControl<Avalonia.Controls.Grid>("Root")!.Width, w.FindControl<Avalonia.Controls.Grid>("Root")!.Height);
        var session = new OrbDragSession(roster, a, DragSource.Orb, false, 1.0);

        for (var i = 1; i <= 30; i++)
        {
            _now += 16;
            session.Move(20 * i, 0);
        }

        Assert.NotEqual(Matrix.Identity, w.SquashMatrix);
        Assert.Equal(width, w.Width);
        Assert.Equal(height, w.Height);
        Assert.Equal(rootWidth, w.FindControl<Avalonia.Controls.Grid>("Root")!.Width);
        Assert.Equal(rootHeight, w.FindControl<Avalonia.Controls.Grid>("Root")!.Height);
        // The transform is on the Disc cell, about its own centre (the circle's).
        var disc = w.FindControl<Avalonia.Controls.Grid>("Disc")!;
        Assert.Equal(56, disc.Width);
        Assert.Equal(new Avalonia.RelativePoint(0.5, 0.5, Avalonia.RelativeUnit.Relative), disc.RenderTransformOrigin);
    }

    [AvaloniaFact]
    public void ReleasingRelaxesItToExactlyTheIdentity()
    {
        var (roster, a, _) = Scene();
        var w = roster.Windows[a];
        var session = new OrbDragSession(roster, a, DragSource.Orb, false, 1.0);
        for (var i = 1; i <= 30; i++)
        {
            _now += 16;
            session.Move(20 * i, 0);
        }

        session.End();
        for (var i = 0; i < 120; i++)
        {
            _now += 16;
            w.StepSquash(_now);
        }

        Assert.Equal(Matrix.Identity, w.SquashMatrix);
    }

    [AvaloniaFact]
    public void ReduceMotionKeepsTheOrbFlat()
    {
        MotionPolicy.ReduceMotionProbe = () => true;
        var (roster, a, _) = Scene();
        var session = new OrbDragSession(roster, a, DragSource.Orb, false, 1.0);

        for (var i = 1; i <= 30; i++)
        {
            _now += 16;
            session.Move(20 * i, 0);
        }

        Assert.Equal(Matrix.Identity, roster.Windows[a].SquashMatrix);
    }

    [AvaloniaFact]
    public void ABackdropDragSquashesNothing()
    {
        var (roster, a, b) = Scene();
        OrbweaverSettings.AssignTeamToGroup("key:" + a, Array.Empty<string>(), OrbweaverSettings.OrbGroupMembershipFor("key:" + b)!);
        var session = new OrbDragSession(roster, a, DragSource.Backdrop, false, 1.0);

        for (var i = 1; i <= 30; i++)
        {
            _now += 16;
            session.Move(20 * i, 0);
        }

        Assert.Equal(Matrix.Identity, roster.Windows[a].SquashMatrix);
        Assert.Equal(Matrix.Identity, roster.Windows[b].SquashMatrix);
    }

    // --- M5: the spring back is the host's (SettleAfterDrop) ---

    // An arranged team whose slot is (100,100), dragged to (160,100) and dropped
    // where nothing snaps. The drag no longer animates the return itself: it
    // hands the host one SettleAfterDrop with no arriving ids, and the host's
    // glide (the one engine) moves every orb from where it was dropped.
    [AvaloniaFact]
    public void ANoChangeDropAsksTheHostToSettleWithNoArrivingOrbs()
    {
        var (roster, a, _) = Scene();
        roster.Arranged = true;
        roster.Slots[a] = new PixelPoint(100, 100);
        var session = new OrbDragSession(roster, a, DragSource.Orb, false, 1.0);
        _now += 16;
        session.Move(60, 0);

        session.End();

        Assert.Single(roster.Settles);
        Assert.Empty(roster.Settles[0]);
        // The default settle is the instant re-layout: the orb is on its slot.
        Assert.Equal(new PixelPoint(100, 100), roster.Windows[a].Position);
    }

    [AvaloniaFact]
    public void ADropThatJoinsAsksTheHostToSettleWithTheDroppedTeamArriving()
    {
        var (roster, a, _) = Scene();
        var session = new OrbDragSession(roster, a, DragSource.Orb, false, 1.0);
        _now += 16;
        session.Move(250, 0);   // 14 from B: Join

        session.End();

        Assert.Single(roster.Settles);
        Assert.Equal(new[] { a }, roster.Settles[0]);
    }

    [AvaloniaFact]
    public void ADraggedOrbIsNeverMovedByTheDragItselfAfterTheRelease()
    {
        // No glide of the drag's own: after End the position is whatever the
        // host's settle left, and nothing steps it afterwards.
        var (roster, a, _) = Scene();
        var session = new OrbDragSession(roster, a, DragSource.Orb, false, 1.0);
        _now += 16;
        session.Move(100, 0);
        session.End();
        var at = roster.Windows[a].Position;

        _now += 1000;

        Assert.Equal(at, roster.Windows[a].Position);
    }

    // --- arrival pop composed with the squash (Disc transform has one owner) ---

    [AvaloniaFact]
    public void ThePopScaleScalesTheDiscUniformly()
    {
        var w = new OrbWindow("pop-" + Uid());

        w.SetPopScale(1.15);

        Assert.Equal(1.15, w.SquashMatrix.M11, 9);
        Assert.Equal(1.15, w.SquashMatrix.M22, 9);
        Assert.Equal(0.0, w.SquashMatrix.M12, 9);

        w.SetPopScale(1.0);
        Assert.Equal(Matrix.Identity, w.SquashMatrix);
    }

    [AvaloniaFact]
    public void PopAndSquashComposeInsteadOfFighting()
    {
        var (roster, a, _) = Scene();
        var w = roster.Windows[a];
        var session = new OrbDragSession(roster, a, DragSource.Orb, false, 1.0);
        for (var i = 1; i <= 30; i++)
        {
            _now += 16;
            session.Move(20 * i, 0);
        }

        var squashed = w.SquashMatrix;
        w.SetPopScale(1.15);

        // Along the drag: stretch times pop; across it: squeeze times pop.
        Assert.Equal(squashed.M11 * 1.15, w.SquashMatrix.M11, 9);
        Assert.Equal(squashed.M22 * 1.15, w.SquashMatrix.M22, 9);

        // The squash keeps updating under the pop rather than overwriting it.
        _now += 16;
        w.StepSquash(_now);
        Assert.True(w.SquashMatrix.M11 > 1.1);
    }

    [AvaloniaTheory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    public void ANonsensePopScaleReadsAsRest(double bad)
    {
        var w = new OrbWindow("badpop-" + Uid());
        w.SetPopScale(1.3);

        w.SetPopScale(bad);

        Assert.Equal(Matrix.Identity, w.SquashMatrix);
    }
}
