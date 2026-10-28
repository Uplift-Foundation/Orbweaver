using Avalonia;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.UiTests;

// CB-268 M3/M1 QA, independent of the engineer's cases: a contracting (leave)
// morph's hit region, and reduce motion at the window level.
[Collection("Settings")]
public class GroupBackdropsMotionQaTests : IDisposable
{
    private readonly List<OrbWindow> _orbs = new();
    private readonly Func<OrbWindow, (Point Centre, double Scale)?> _measure = GroupBackdrops.Measure;
    private readonly Func<double> _clock = BackdropMotion.Clock;
    private readonly Func<bool> _reduced = BackdropMotion.ReducedMotion;
    private double _now = 100;

    public GroupBackdropsMotionQaTests()
    {
        GroupBackdrops.Update(Array.Empty<BackdropSpec>());
        GroupBackdrops.ParkAll();
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
        GroupBackdrops.SetNamesVisible(false);
        GroupBackdrops.SetOutlinesVisible(true);
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

    private static BackdropSpec Spec(string id, IReadOnlyList<OrbWindow> orbs, string shape = "circle") => new(id, shape, orbs, null);

    private void At(double seconds)
    {
        _now = 100 + seconds;
        GroupBackdrops.Tick();
    }

    private static bool Inside(Point[] poly, Point p)
    {
        var inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) &&
                p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }

    [AvaloniaFact]
    public void ALeavingOrbsMorphCatchesWhereItIsDrawnNotWhereItWasOrWillBe()
    {
        var orbs = new List<OrbWindow> { Orb("q1", 300, 300), Orb("q2", 400, 300), Orb("q3", 350, 380), Orb("q4", 620, 340) };
        GroupBackdrops.Update(new[] { Spec("g-q", orbs) });
        At(1.0);
        var old = GroupBackdrops.DisplayedOutlineOf("g-q");

        orbs.RemoveAt(3);
        GroupBackdrops.Update(new[] { Spec("g-q", orbs) });
        Assert.True(GroupBackdrops.StateOf("g-q")!.Value.Morphing);
        var final = GroupBackdrops.OutlineOf("g-q");

        At(1.03);   // early: still close to the old, large polygon
        var drawn = GroupBackdrops.DisplayedOutlineOf("g-q");
        var c = OutlineMorph.Centroid(drawn);
        // A point just inside the drawn edge on the side the leaver was: inside what is
        // drawn, but outside the FINAL polygon. It must catch (what is drawn catches).
        var k = 0; var far = double.MinValue;
        for (var i = 0; i < drawn.Length; i++)
        {
            var d = Math.Abs(drawn[i].X - c.X);
            if (drawn[i].X > c.X && d > far) { far = d; k = i; }
        }
        var inwardOfDrawn = new Point(c.X + (drawn[k].X - c.X) * 0.97, c.Y + (drawn[k].Y - c.Y) * 0.97);
        Assert.True(!Inside(final, inwardOfDrawn), "probe point must be outside the final polygon");

        var told = new List<bool>();
        GroupBackdrops.HitTestTick(inwardOfDrawn, false, (_, catches) => told.Add(catches));
        Assert.Equal(new[] { true }, told);

        var outward = new Point(c.X + (drawn[k].X - c.X) * 1.05, c.Y + (drawn[k].Y - c.Y) * 1.05);
        GroupBackdrops.HitTestTick(outward, false, (_, catches) => told.Add(catches));
        Assert.Equal(new[] { true, false }, told);

        GroupBackdrops.HitTestTick(inwardOfDrawn, false, (_, catches) => told.Add(catches));   // back inside the drawn edge
        Assert.Equal(new[] { true, false, true }, told);

        // After the morph ends the same point no longer catches: it is outside the final polygon.
        At(1.0 + BackdropMotion.MorphSeconds + 0.01);
        Assert.False(GroupBackdrops.StateOf("g-q")!.Value.Morphing);
        GroupBackdrops.HitTestTick(new Point(inwardOfDrawn.X + 0.5, inwardOfDrawn.Y), false, (_, catches) => told.Add(catches));
        Assert.Equal(new[] { true, false, true, false }, told);
    }

    [AvaloniaFact]
    public void ControlWithoutReduceMotionTheSameHighlightDoesBreathe()
    {
        var orbs = new List<OrbWindow> { Orb("c1", 300, 300), Orb("c2", 400, 300), Orb("c3", 350, 380) };
        GroupBackdrops.Update(new[] { Spec("g-c", orbs) });
        At(0);
        GroupBackdrops.Highlight("g-c");
        At(OutlineMorph.BreathPeriodSeconds / 2);
        Assert.Equal(1.04, GroupBackdrops.StateOf("g-c")!.Value.Breath, 9);
    }
}
