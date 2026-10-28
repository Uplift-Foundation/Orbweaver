using Avalonia;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.UiTests;

// CB-268 M3/M1: the outline morph and the highlight's breathing, driven by a clock
// the test holds still and steps by hand (BackdropMotion.Clock + GroupBackdrops.Tick),
// so every frame asserted here is exact.
[Collection("Settings")]
public class GroupBackdropsMotionTests : IDisposable
{
    private readonly List<OrbWindow> _orbs = new();
    private readonly Func<OrbWindow, (Point Centre, double Scale)?> _measure = GroupBackdrops.Measure;
    private readonly Func<double> _clock = BackdropMotion.Clock;
    private readonly Func<bool> _reduced = BackdropMotion.ReducedMotion;
    private double _now = 100;

    public GroupBackdropsMotionTests()
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

    // Three orbs in a group, then a fourth far away joins it: the outline swells.
    private (List<OrbWindow> Orbs, OrbWindow Joiner) Joining()
    {
        var orbs = new List<OrbWindow> { Orb("m1", 300, 300), Orb("m2", 400, 300), Orb("m3", 350, 380) };
        GroupBackdrops.Update(new[] { Spec("g-m", orbs) });
        var joiner = Orb("m4", 620, 340);
        return (orbs, joiner);
    }

    [AvaloniaFact]
    public void AnOutlineThatChangesSpringsInsteadOfJumpingAndEndsExactlyOnTheTarget()
    {
        var (orbs, joiner) = Joining();
        var before = GroupBackdrops.StateOf("g-m")!.Value;
        Assert.False(before.Morphing);

        orbs.Add(joiner);
        GroupBackdrops.Update(new[] { Spec("g-m", orbs) });

        var start = GroupBackdrops.StateOf("g-m")!.Value;
        Assert.True(start.Morphing);
        Assert.Equal(before.Vertices, start.Vertices);                  // still drawing the old polygon

        // The window covers the union of old and new while it morphs.
        var target = GroupOutline.Silhouette("circle", Discs(orbs), GroupBackdrops.PaddingDip);
        var targetBox = GroupOutline.Bounds(target);
        Assert.True(start.Position.X + start.Width >= targetBox.Right);
        Assert.True(start.Position.X <= before.Position.X);

        At(0.1);
        var mid = GroupBackdrops.StateOf("g-m")!.Value;
        Assert.True(mid.Morphing);
        Assert.Equal(OutlineMorph.Samples, mid.Vertices);               // an interpolated, resampled frame

        At(BackdropMotion.MorphSeconds + 0.01);
        var end = GroupBackdrops.StateOf("g-m")!.Value;
        Assert.False(end.Morphing);

        // Exactly the fitted silhouette — not a resampled copy of it — and the same
        // window a never-animated group would have.
        var outline = GroupBackdrops.OutlineOf("g-m");
        Assert.Equal(target.Length, outline.Length);
        for (var i = 0; i < target.Length; i++)
        {
            Assert.Equal(target[i].X, outline[i].X, 6);
            Assert.Equal(target[i].Y, outline[i].Y, 6);
        }
        Assert.True(end.Width < start.Width);                           // the window shrank back
        Assert.Equal(target.Length, end.Vertices);
    }

    [AvaloniaFact]
    public void EveryFrameIsTheInterpolationOfTheTwoResampledPolygonsNotAFreshFit()
    {
        var (orbs, joiner) = Joining();
        var from = OutlineMorph.Resample(GroupBackdrops.DisplayedOutlineOf("g-m"));

        orbs.Add(joiner);
        GroupBackdrops.Update(new[] { Spec("g-m", orbs) });
        var to = OutlineMorph.Resample(GroupBackdrops.OutlineOf("g-m"));

        foreach (var t in new[] { 0.03, 0.08, 0.15, 0.22 })
        {
            At(t);
            var s = OutlineMorph.SpringProgress(t, BackdropMotion.JoinOmega, BackdropMotion.JoinDamping);
            var expected = OutlineMorph.Frame(from, to, s);
            var drawn = GroupBackdrops.DisplayedOutlineOf("g-m");

            Assert.Equal(expected.Length, drawn.Length);
            for (var k = 0; k < expected.Length; k += 7)
            {
                Assert.Equal(expected[k].X, drawn[k].X, 6);
                Assert.Equal(expected[k].Y, drawn[k].Y, 6);
            }
        }
    }

    [AvaloniaFact]
    public void ALeaveContractsWithAWobbleAndAJoinSwells()
    {
        var (orbs, joiner) = Joining();
        orbs.Add(joiner);
        GroupBackdrops.Update(new[] { Spec("g-m", orbs) });
        At(0.5);                                                         // the join has finished

        var big = GroupBackdrops.DisplayedOutlineOf("g-m");
        orbs.RemoveAt(3);                                                 // it leaves
        GroupBackdrops.Update(new[] { Spec("g-m", orbs) });

        var widths = new List<double>();
        for (var t = 0.01; t < 0.3; t += 0.01)
        {
            At(0.5 + t);
            var b = GroupOutline.Bounds(GroupBackdrops.DisplayedOutlineOf("g-m"));
            widths.Add(b.Width);
        }

        var finalWidth = GroupOutline.Bounds(GroupBackdrops.OutlineOf("g-m")).Width;
        Assert.True(widths[0] > finalWidth);                              // starts bigger and contracts
        Assert.True(widths.Min() < finalWidth - 0.5);                     // and wobbles below where it ends
        Assert.True(GroupOutline.Bounds(big).Width > finalWidth);
    }

    [AvaloniaFact]
    public void MidMorphTheHitAreaIsTheInterpolatedPolygonAndTheOrbDiscsStayCutOut()
    {
        var (orbs, joiner) = Joining();
        orbs.Add(joiner);
        GroupBackdrops.Update(new[] { Spec("g-m", orbs) });
        At(0.06);

        var drawn = GroupBackdrops.DisplayedOutlineOf("g-m");
        var final = GroupBackdrops.OutlineOf("g-m");
        var c = OutlineMorph.Centroid(drawn);

        // Probe both sides of the drawn edge directly. Which vertex is a good probe
        // depends on the orbs' size, so pick one rather than hard-code an index: its
        // inward point must be inside the drawn polygon and outside every orb disc
        // (a disc is never caught, so a probe inside one would tell nothing), and its
        // outward point must be outside the polygon.
        var bounds = GroupOutline.Bounds(drawn).Inflate(50);
        bool InAnyDisc(Point p) => orbs.Any(o =>
        {
            var dx = p.X - (o.Position.X + o.CentreDip);
            var dy = p.Y - (o.Position.Y + o.CentreDip);
            return Math.Sqrt(dx * dx + dy * dy) <= o.OrbRadius;
        });

        Point Toward(Point edge, double k) => new(c.X + (edge.X - c.X) * k, c.Y + (edge.Y - c.Y) * k);

        var probe = Enumerable.Range(0, drawn.Length).First(i =>
            BackdropHitTest.InsideSilhouette(Toward(drawn[i], 0.97), bounds, drawn) && !InAnyDisc(Toward(drawn[i], 0.97)) &&
            !BackdropHitTest.InsideSilhouette(Toward(drawn[i], 1.03), bounds, drawn) &&
            final[i] != drawn[i]);
        var edge = drawn[probe];
        var inward = Toward(edge, 0.97);
        var outward = Toward(edge, 1.03);
        Assert.NotEqual(final[probe], edge);                              // genuinely mid-morph

        var told = new List<bool>();
        GroupBackdrops.HitTestTick(inward, false, (_, catches) => told.Add(catches));
        Assert.Equal(new[] { true }, told);                               // catches inside the drawn outline

        GroupBackdrops.HitTestTick(outward, false, (_, catches) => told.Add(catches));
        Assert.Equal(new[] { true, false }, told);                        // and not outside it

        // An orb's own circle is still never caught, even though it is inside.
        var onOrb = new Point(orbs[0].Position.X + orbs[0].CentreDip, orbs[0].Position.Y + orbs[0].CentreDip);
        GroupBackdrops.HitTestTick(inward, false, (_, catches) => told.Add(catches));   // back to catching
        GroupBackdrops.HitTestTick(onOrb, false, (_, catches) => told.Add(catches));
        Assert.Equal(new[] { true, false, true, false }, told);
    }

    [AvaloniaFact]
    public void OutlineOfAlwaysReportsTheTrueSilhouetteEvenMidMorph()
    {
        var (orbs, joiner) = Joining();
        orbs.Add(joiner);
        GroupBackdrops.Update(new[] { Spec("g-m", orbs) });

        var target = GroupOutline.Silhouette("circle", Discs(orbs), GroupBackdrops.PaddingDip);

        At(0.05);
        var seen = GroupBackdrops.OutlineOf("g-m");

        Assert.Equal(target.Length, seen.Length);
        Assert.Equal(target[10].X, seen[10].X, 6);
        Assert.NotEqual(GroupBackdrops.DisplayedOutlineOf("g-m")[10], seen[10]);
    }

    [AvaloniaFact]
    public void ARefitMidMorphSpringsOnFromWhatIsDrawnSoNothingJumps()
    {
        var (orbs, joiner) = Joining();
        orbs.Add(joiner);
        GroupBackdrops.Update(new[] { Spec("g-m", orbs) });
        At(0.07);
        var drawnBefore = GroupBackdrops.DisplayedOutlineOf("g-m");

        orbs.RemoveAt(3);                                                 // changes its mind
        GroupBackdrops.Update(new[] { Spec("g-m", orbs) });
        var drawnAfter = GroupBackdrops.DisplayedOutlineOf("g-m");

        Assert.True(GroupBackdrops.StateOf("g-m")!.Value.Morphing);
        for (var k = 0; k < drawnBefore.Length; k += 11)
        {
            Assert.Equal(drawnBefore[k].X, drawnAfter[k].X, 6);
            Assert.Equal(drawnBefore[k].Y, drawnAfter[k].Y, 6);
        }
    }

    [AvaloniaFact]
    public void AFirstAppearanceAndAnOutlineThatIsOffDoNotMorph()
    {
        var orbs = new List<OrbWindow> { Orb("f1", 300, 300), Orb("f2", 400, 300), Orb("f3", 350, 380) };
        GroupBackdrops.Update(new[] { Spec("g-f", orbs) });
        Assert.False(GroupBackdrops.StateOf("g-f")!.Value.Morphing);       // nothing to spring from

        GroupBackdrops.SetOutlinesVisible(false);
        orbs.Add(Orb("f4", 620, 340));
        GroupBackdrops.Update(new[] { Spec("g-f", orbs) });
        Assert.False(GroupBackdrops.StateOf("g-f")!.Value.Morphing);       // invisible: it just moves
    }

    [AvaloniaFact]
    public void AGroupDragThatOnlyTranslatesNeverMorphs()
    {
        var (orbs, _) = Joining();
        foreach (var o in orbs) o.Position = new PixelPoint(o.Position.X + 30, o.Position.Y + 12);
        GroupBackdrops.Refresh();

        Assert.False(GroupBackdrops.StateOf("g-m")!.Value.Morphing);
    }

    [AvaloniaFact]
    public void ReducedMotionIsAPlainShortGlideWithNoOvershoot()
    {
        BackdropMotion.ReducedMotion = () => true;
        var (orbs, joiner) = Joining();
        var from = OutlineMorph.Resample(GroupBackdrops.DisplayedOutlineOf("g-m"));

        orbs.Add(joiner);
        GroupBackdrops.Update(new[] { Spec("g-m", orbs) });
        var to = OutlineMorph.Resample(GroupBackdrops.OutlineOf("g-m"));

        At(BackdropMotion.ReducedSeconds / 2);
        var expected = OutlineMorph.Frame(from, to, 0.5);                  // linear, half way
        var drawn = GroupBackdrops.DisplayedOutlineOf("g-m");
        Assert.Equal(expected[30].X, drawn[30].X, 6);
        Assert.Equal(expected[30].Y, drawn[30].Y, 6);

        At(BackdropMotion.ReducedSeconds + 0.001);
        Assert.False(GroupBackdrops.StateOf("g-m")!.Value.Morphing);       // done in 150 ms, not 300
    }

    [AvaloniaFact]
    public void ADisabledMotionJumpsAsBefore()
    {
        BackdropMotion.Enabled = false;
        var (orbs, joiner) = Joining();
        orbs.Add(joiner);
        GroupBackdrops.Update(new[] { Spec("g-m", orbs) });

        Assert.False(GroupBackdrops.StateOf("g-m")!.Value.Morphing);
    }

    [AvaloniaFact]
    public void ThePairPreviewFollowsADragWithoutLaggingBehindItself()
    {
        var a = Orb("pa", 200, 200);
        var b = Orb("pb", 400, 200);
        GroupBackdrops.PreviewPair(new[] { a, b });
        b.Position = new PixelPoint(600, 200);
        GroupBackdrops.Refresh();

        Assert.False(GroupBackdrops.PreviewState!.Value.Morphing);        // the preview jumps with the drag
    }

    // ---- the caption rides the morph (CB-282) -----------------------------------------

    [AvaloniaFact]
    public void TheGroupNameCaptionRidesTheInterpolatedOutlineAndLandsOnTheFinalOne()
    {
        GroupBackdrops.SetNamesVisible(true);
        var (orbs, joiner) = Joining();
        var gap = GroupCaption.GapDip;

        orbs.Add(joiner);
        GroupBackdrops.Update(new[] { Spec("g-m", orbs) });

        foreach (var t in new[] { 0.04, 0.1, 0.2 })
        {
            At(t);
            var drawn = GroupOutline.Bounds(GroupBackdrops.DisplayedOutlineOf("g-m"));
            var caption = GroupBackdrops.CaptionOf("g-m")!.Value;

            // Centred over the drawn outline's bounds and just above its top, in screen
            // units: the caption is a window of its own, not a child of the backdrop.
            Assert.Equal(drawn.Center.X, caption.Rect.Center.X, 1);
            Assert.Equal(drawn.Top - gap, caption.Rect.Bottom, 1);
        }

        At(BackdropMotion.MorphSeconds + 0.01);
        var final = GroupOutline.Bounds(GroupBackdrops.OutlineOf("g-m"));
        var end = GroupBackdrops.CaptionOf("g-m")!.Value;

        Assert.Equal(final.Center.X, end.Rect.Center.X, 1);
        Assert.Equal(final.Top - gap, end.Rect.Bottom, 1);
    }

    // ---- breathing ---------------------------------------------------------------------------------

    [AvaloniaFact]
    public void AHighlightedOutlineBreathesBetweenOneAndOnePointZeroFourAndStopsWhenCleared()
    {
        var (orbs, _) = Joining();
        At(0);
        GroupBackdrops.Highlight("g-m");
        At(0);
        Assert.Equal(1.00, GroupBackdrops.StateOf("g-m")!.Value.Breath, 9);            // begins at its true size

        At(OutlineMorph.BreathPeriodSeconds / 2);
        Assert.Equal(1.04, GroupBackdrops.StateOf("g-m")!.Value.Breath, 9);

        foreach (var t in new[] { 0.1, 0.4, 0.9, 1.3, 2.0, 3.7 })
        {
            At(t);
            Assert.InRange(GroupBackdrops.StateOf("g-m")!.Value.Breath, 1.00 - 1e-9, 1.04 + 1e-9);
        }

        GroupBackdrops.Highlight(null);
        Assert.Equal(1.0, GroupBackdrops.StateOf("g-m")!.Value.Breath, 9);
        At(0.3);
        Assert.Equal(1.0, GroupBackdrops.StateOf("g-m")!.Value.Breath, 9);             // no longer ticking
    }

    [AvaloniaFact]
    public void ReduceMotionMeansAHighlightedOutlineDoesNotBreathe()
    {
        BackdropMotion.ReducedMotion = () => true;
        var (orbs, _) = Joining();
        At(0);
        GroupBackdrops.Highlight("g-m");

        foreach (var t in new[] { 0.0, 0.35, OutlineMorph.BreathPeriodSeconds / 2, 1.0, 2.1 })
        {
            At(t);
            Assert.Equal(1.0, GroupBackdrops.StateOf("g-m")!.Value.Breath);
        }

        Assert.False(GroupBackdrops.AnimationTimerRunning, "nothing is animating, so the timer is off");
    }

    [AvaloniaFact]
    public void ReduceMotionSwitchedOnMidBreathSettlesAtTrueSize()
    {
        var (orbs, _) = Joining();
        At(0);
        GroupBackdrops.Highlight("g-m");
        At(OutlineMorph.BreathPeriodSeconds / 2);
        Assert.Equal(1.04, GroupBackdrops.StateOf("g-m")!.Value.Breath, 9);

        BackdropMotion.ReducedMotion = () => true;
        At(OutlineMorph.BreathPeriodSeconds / 2 + 0.1);
        Assert.Equal(1.0, GroupBackdrops.StateOf("g-m")!.Value.Breath);

        BackdropMotion.ReducedMotion = () => false;   // and back on again
        At(OutlineMorph.BreathPeriodSeconds);
        Assert.InRange(GroupBackdrops.StateOf("g-m")!.Value.Breath, 1.0, 1.04 + 1e-9);
    }

    [AvaloniaFact]
    public void BreathingDoesNotMoveTheHitArea()
    {
        var (orbs, _) = Joining();
        GroupBackdrops.Highlight("g-m");
        var before = GroupBackdrops.DisplayedOutlineOf("g-m");

        At(OutlineMorph.BreathPeriodSeconds / 2);

        var after = GroupBackdrops.DisplayedOutlineOf("g-m");
        Assert.Equal(before[17].X, after[17].X, 9);                     // the scale is visual only
        Assert.Equal(before[17].Y, after[17].Y, 9);
    }

    [AvaloniaFact]
    public void ThePairPreviewBreathesToo()
    {
        var a = Orb("ba", 200, 200);
        var b = Orb("bb", 400, 200);
        At(0);
        GroupBackdrops.PreviewPair(new[] { a, b });

        At(OutlineMorph.BreathPeriodSeconds / 2);

        Assert.Equal(1.04, GroupBackdrops.PreviewState!.Value.Breath, 9);
    }

    // The preview window is kept and reused, so a second preview must begin at its
    // true size, not mid-breath from the last one.
    [AvaloniaFact]
    public void ARePreviewedPairStartsItsBreathAgainFromTheTrueSize()
    {
        var a = Orb("ra", 200, 200);
        var b = Orb("rb", 400, 200);
        At(0);
        GroupBackdrops.PreviewPair(new[] { a, b });
        At(0.45);
        Assert.True(GroupBackdrops.PreviewState!.Value.Breath > 1.0);

        GroupBackdrops.PreviewPair(null);
        At(2.0);                                                        // time passes while it is hidden
        GroupBackdrops.PreviewPair(new[] { a, b });
        At(2.0);

        Assert.Equal(1.00, GroupBackdrops.PreviewState!.Value.Breath, 9);
    }

    [AvaloniaFact]
    public void TheWindowKeepsRoomForTheBreath()
    {
        var (orbs, _) = Joining();
        var s = GroupBackdrops.StateOf("g-m")!.Value;
        var outline = GroupBackdrops.OutlineOf("g-m");
        var bounds = GroupOutline.Bounds(outline);
        var room = bounds.Width * GroupBackdrops.BreathRoom;

        Assert.True(s.Width >= bounds.Width + 2 * room - 2);
    }

    private static List<(double X, double Y, double R)> Discs(IEnumerable<OrbWindow> orbs) =>
        orbs.Select(o => ((double)o.Position.X + o.CentreDip, (double)o.Position.Y + o.CentreDip, o.OrbRadius)).ToList();
}
