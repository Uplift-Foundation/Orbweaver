using Avalonia;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.UiTests;

// CB-268, found hands-on on a real Mac: dragging an orb out of a group made the
// outline bounce, and a few seconds of it aborted the app. Every Refresh during
// the drag restarted the morph, and each restart inflated the window from the
// window it already had (the old box plus 15% a side), so it grew 30% a refresh
// until Skia refused to draw it — "Failed to create Skia render target surface",
// an unhandled exception on the next paint. Same fake clock and Measure seam as
// GroupBackdropsMotionTests.
[Collection("Settings")]
public class GroupBackdropsMorphStabilityTests : IDisposable
{
    private readonly List<OrbWindow> _orbs = new();
    private readonly Func<OrbWindow, (Point Centre, double Scale)?> _measure = GroupBackdrops.Measure;
    private readonly Func<double> _clock = BackdropMotion.Clock;
    private readonly Func<bool> _reduced = BackdropMotion.ReducedMotion;
    private double _now = 100;

    public GroupBackdropsMorphStabilityTests()
    {
        GroupBackdrops.Update(Array.Empty<BackdropSpec>());
        GroupBackdrops.ParkAll();
        GroupBackdrops.ResetHitTestState();
        GroupBackdrops.SetNamesVisible(false);
        GroupBackdrops.SetOutlinesVisible(true);
        GroupBackdrops.SetArranged(true);
        GroupBackdrops.SetLooseOrbs(Array.Empty<OrbWindow>());
        GroupBackdrops.PreviewPair(null);
        GroupBackdrops.Highlight(null);

        GroupBackdrops.Measure = o => (new Point(o.Position.X + o.CentreDip, o.Position.Y + o.CentreDip), 1.0);
        BackdropMotion.Clock = () => _now;
        BackdropMotion.Enabled = true;
        BackdropMotion.ReducedMotion = () => false;
    }

    public void Dispose()
    {
        GroupBackdrops.Update(Array.Empty<BackdropSpec>());
        GroupBackdrops.ParkAll();
        GroupBackdrops.ResetHitTestState();
        GroupBackdrops.PreviewPair(null);
        GroupBackdrops.Highlight(null);
        foreach (var o in _orbs) o.Close();
        GroupBackdrops.Measure = _measure;
        BackdropMotion.Clock = _clock;
        BackdropMotion.ReducedMotion = _reduced;
        BackdropMotion.Enabled = true;
    }

    private OrbWindow Orb(string id, int x, int y)
    {
        var orb = new OrbWindow(id) { Position = new PixelPoint(x, y) };
        orb.Show();
        _orbs.Add(orb);
        return orb;
    }

    private static BackdropSpec Spec(IReadOnlyList<OrbWindow> orbs, string shape = "line") => new("g-m", shape, orbs, null);

    private BackdropState State() => GroupBackdrops.StateOf("g-m")!.Value;

    private void At(double seconds)
    {
        _now = 100 + seconds;
        GroupBackdrops.Tick();
    }

    private static List<(double X, double Y, double R)> Discs(IEnumerable<OrbWindow> orbs) =>
        orbs.Select(o => ((double)o.Position.X + o.CentreDip, (double)o.Position.Y + o.CentreDip, o.OrbRadius)).ToList();

    // Seven orbs in a ring, the shape of the group the crash was read off.
    private List<OrbWindow> Ring() => new()
    {
        Orb("r1", 400, 300), Orb("r2", 376, 365), Orb("r3", 348, 347), Orb("r4", 333, 317),
        Orb("r5", 333, 283), Orb("r6", 348, 253), Orb("r7", 376, 235),
    };

    [AvaloniaFact]
    public void DraggingAMemberAroundNeverGrowsTheWindowPastItsOutline()
    {
        var orbs = Ring();
        var dragged = Orb("r8", 300, 420);
        orbs.Add(dragged);
        GroupBackdrops.Update(new[] { Spec(orbs) });

        // The member leaves (a morph starts), then drag frames keep refreshing
        // while it is in flight — the sequence that compounded on a real Mac.
        var without = orbs.Take(7).ToList();
        GroupBackdrops.Update(new[] { Spec(without) });
        Assert.True(State().Morphing);
        var morphWidth = State().Width;

        var widest = 0.0;
        for (var frame = 0; frame < 60; frame++)
        {
            dragged.Position = new PixelPoint(300 - frame * 3, 420 + frame * 5);
            GroupBackdrops.Update(new[] { Spec(frame % 2 == 0 ? without : orbs) });
            GroupBackdrops.Refresh();
            widest = Math.Max(widest, Math.Max(State().Width, State().Height));
        }

        // Bounded by the morph window of the widest real outline (the member at the
        // far end of its drag), never compounding.
        var reach = GroupOutline.Bounds(GroupOutline.Silhouette("line", Discs(orbs), GroupBackdrops.PaddingDip));
        // The box is the outline plus 10% breathing room and the stroke's edge; a
        // morph window is that plus 30%. So 1.5x is the most one morph can need.
        Assert.True(widest <= 1.5 * Math.Max(reach.Width, reach.Height) + 4 * GroupBackdrops.EdgeDip,
            $"window reached {widest:F0} dip around an outline {Math.Max(reach.Width, reach.Height):F0} wide");
        Assert.True(widest < BackdropGeometry.MaxSide);
        Assert.True(morphWidth < BackdropGeometry.MaxSide);
    }

    [AvaloniaFact]
    public void ARefreshTowardTheSameOutlineLeavesTheMorphRunning()
    {
        var orbs = Ring();
        var joiner = Orb("j", 600, 300);
        GroupBackdrops.Update(new[] { Spec(orbs) });

        orbs.Add(joiner);
        GroupBackdrops.Update(new[] { Spec(orbs) });
        At(0.1);
        var frame = GroupBackdrops.DisplayedOf("g-m");
        var window = (State().Position, State().Width, State().Height);

        // A drag frame or a scan arrives with nothing changed.
        GroupBackdrops.Update(new[] { Spec(orbs) });
        GroupBackdrops.Refresh();

        Assert.True(State().Morphing);
        Assert.Equal(frame, GroupBackdrops.DisplayedOf("g-m"));
        Assert.Equal(window, (State().Position, State().Width, State().Height));

        // It ends when the ORIGINAL morph ends: a restart at 0.1 would still be going.
        At(BackdropMotion.MorphSeconds + 0.01);
        Assert.False(State().Morphing);
    }

    [AvaloniaFact]
    public void OrbsThatOnlyMovedAreFollowedExactlyWithoutASpring()
    {
        var orbs = Ring();
        GroupBackdrops.Update(new[] { Spec(orbs) });

        orbs[0].Position = new PixelPoint(430, 310);     // a member dragged within the group
        GroupBackdrops.Update(new[] { Spec(orbs) });

        Assert.False(State().Morphing);
        var target = GroupOutline.Silhouette("line", Discs(orbs), GroupBackdrops.PaddingDip);
        var outline = GroupBackdrops.OutlineOf("g-m");
        Assert.Equal(target.Length, outline.Length);
        for (var i = 0; i < target.Length; i++)
        {
            Assert.Equal(target[i].X, outline[i].X, 6);
            Assert.Equal(target[i].Y, outline[i].Y, 6);
        }
    }

    [AvaloniaFact]
    public void AShapeChangeStillSprings()
    {
        var orbs = Ring();
        GroupBackdrops.Update(new[] { Spec(orbs, "line") });
        GroupBackdrops.Update(new[] { Spec(orbs, "grid") });
        Assert.True(State().Morphing);
    }

    [AvaloniaFact]
    public void AGroupTooBigToDrawIsHiddenRatherThanDrawn()
    {
        var orbs = new List<OrbWindow> { Orb("a", 0, 0), Orb("b", 9000, 0) };
        GroupBackdrops.Update(new[] { Spec(orbs) });
        Assert.False(State().Shown);

        // And it comes back once the orbs are somewhere drawable again.
        orbs[1].Position = new PixelPoint(200, 0);
        GroupBackdrops.Update(new[] { Spec(orbs) });
        Assert.True(State().Shown);
        Assert.True(State().Width < BackdropGeometry.MaxSide);
    }

    [AvaloniaFact]
    public void AMorphWhoseWindowWouldBeTooBigJumpsInstead()
    {
        // Each outline is drawable on its own; the union of the two, plus the
        // overshoot margin, is not.
        var orbs = new List<OrbWindow> { Orb("a", 0, 0), Orb("b", 3000, 0) };
        GroupBackdrops.Update(new[] { Spec(orbs) });
        Assert.True(State().Shown);

        var far = new List<OrbWindow> { orbs[0], orbs[1], Orb("c", 6500, 0) };
        orbs[0].Position = new PixelPoint(3500, 0);
        GroupBackdrops.Update(new[] { Spec(far) });

        Assert.True(State().Shown);
        Assert.False(State().Morphing);
        Assert.True(State().Width < BackdropGeometry.MaxSide);
    }
}
