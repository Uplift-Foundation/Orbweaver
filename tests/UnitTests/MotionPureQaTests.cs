using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Xunit;
using Xunit.Abstractions;

namespace Orbweaver.UnitTests;

// CB-268 motion QA (M1 magnetic pull, M4 drag squash, M5 spring glide) under a fake clock.
[Collection("Settings")]
public class MotionPureQaTests
{
    private readonly ITestOutputHelper _out;
    public MotionPureQaTests(ITestOutputHelper o) => _out = o;

    private static SnapCandidate Disc(double x, double y, double r = 18) =>
        new("g-1", Array.Empty<Point>(), new[] { (x, y, r) });

    // ---- M1 ----

    [Fact]
    public void ThePullIsAtMostThirtyPercentOfTheGapAndNeverCrossesTheTargetForAnyTeamShapeAndGap()
    {
        var rng = new Random(11);
        for (var i = 0; i < 3000; i++)
        {
            var target = Disc(rng.NextDouble() * 400, rng.NextDouble() * 400, 10 + rng.Next(30));
            var moving = Enumerable.Range(0, 1 + rng.Next(4))
                .Select(_ => (X: rng.NextDouble() * 400, Y: rng.NextDouble() * 400, R: 8 + rng.NextDouble() * 12)).ToList();
            var (dx, dy) = MagneticPull.Offset(moving, target, 24);
            var mag = Math.Sqrt(dx * dx + dy * dy);

            var gap = OrbSnap.DiscGap(moving, target.Discs);
            if (gap > 24 || gap <= 0) { Assert.Equal(0, mag, 9); continue; }

            Assert.True(mag <= 0.30 * gap + 1e-9, $"pull {mag} > 30% of gap {gap}");
            // After the pull, no moving disc has crossed into the target's disc.
            var after = moving.Select(m => (m.X + dx, m.Y + dy, m.R)).ToList();
            Assert.True(OrbSnap.DiscGap(after, target.Discs) >= 0.7 * gap - 1e-6, "pulled team crossed the target");
        }
    }

    [Fact]
    public void ThePullIsZeroAtTheEdgeOfJoinRangeAndContinuousThroughIt()
    {
        var m = new[] { (0.0, 0.0, 18.0) };
        double Pull(double gap) { var o = MagneticPull.Offset(m, Disc(36 + gap, 0), 24); return Math.Sqrt(o.X * o.X + o.Y * o.Y); }
        Assert.Equal(0, Pull(24.0), 9);
        Assert.Equal(0, Pull(24.001), 9);
        Assert.True(Pull(23.9) < 0.01, $"pull just inside the range jumped to {Pull(23.9)}");
        Assert.True(Pull(10) > 0);                                              // control: it does pull inside the range
        double prev = Pull(24);
        for (var gap = 23.9; gap >= 0.1; gap -= 0.1)
        {
            var p = Pull(gap);
            Assert.True(Math.Abs(p - prev) < 0.6, $"pull jumped {prev:F3} -> {p:F3} at gap {gap:F1}");
            prev = p;
        }
    }

    [Fact]
    public void NonFiniteGeometryMustNotProduceANaNPull()
    {
        var o = MagneticPull.Offset(new[] { (double.NaN, 0.0, 18.0) }, Disc(40, 0), 24);
        Assert.True(double.IsFinite(o.X) && double.IsFinite(o.Y), $"pull {o}");
    }

    // ---- M4 ----

    private static (double MaxStretch, bool Identity, double Det) DragFor(double speed, bool reduce, double seconds = 1.0)
    {
        MotionPolicy.ReduceMotionProbe = () => reduce;
        MotionPolicy.Clock = () => 0;
        try
        {
            var sq = new DragSquash();
            long t = 0; double x = 0; double max = 0; var det = 1.0; var identityWhenRest = true;
            sq.Observe(x, 0, t);
            for (var step = 0; step < (int)(seconds * 1000 / 8); step++)
            {
                t += 8; x += speed * 0.008;
                sq.Observe(x, 0, t);
                var m = sq.Step(t);
                max = Math.Max(max, Math.Abs(sq.Stretch));
                det = m.M11 * m.M22 - m.M12 * m.M21;
                if (sq.Stretch == 0) identityWhenRest &= m.Equals(Matrix.Identity);
            }
            return (max, identityWhenRest, det);
        }
        finally { MotionPolicy.ReduceMotionProbe = null; MotionPolicy.Clock = null; }
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1200)]
    [InlineData(5000)]
    [InlineData(1e6)]
    public void TheStretchStaysWithinTheSpringsOvershootOfTenPercentAndKeepsTheAreaConstant(double speed)
    {
        var (max, _, det) = DragFor(speed, false);
        var ceiling = DragSquash.MaxStretch * (1 + Spring.Overshoot(0.55)) + 1e-9;
        _out.WriteLine($"speed {speed}: max stretch {max:P2}, ceiling {ceiling:P2}");
        Assert.True(max <= ceiling, $"speed {speed}: stretch {max:P2} > {ceiling:P2}");
        Assert.Equal(1.0, det, 6);
    }

    [Fact]
    public void ControlAFastDragReallyStretchesAndReduceMotionNeverDoes()
    {
        Assert.True(DragFor(1200, false).MaxStretch > 0.05);
        Assert.Equal(0, DragFor(5000, true).MaxStretch);
    }

    [Fact]
    public void StoppingRelaxesToExactlyTheIdentityAndAStalePointerCountsAsStopped()
    {
        var sq = new DragSquash();
        long t = 0; double x = 0;
        sq.Observe(x, 0, t);
        for (var i = 0; i < 40; i++) { t += 8; x += 12; sq.Observe(x, 0, t); sq.Step(t); }
        Assert.True(sq.Stretch > 0.05);
        // No more pointer events at all (a drag that stops dead): within the stale window plus the spring's settle it is exactly at rest.
        for (var i = 0; i < 200; i++) { t += 8; sq.Step(t); }
        Assert.True(sq.AtRest);
        Assert.Equal(Matrix.Identity, sq.Current());
        // Release mid-stretch also relaxes exactly.
        var s2 = new DragSquash(); long t2 = 0; double x2 = 0;
        s2.Observe(0, 0, 0);
        for (var i = 0; i < 40; i++) { t2 += 8; x2 += 12; s2.Observe(x2, 0, t2); s2.Step(t2); }
        s2.Release();
        for (var i = 0; i < 200; i++) { t2 += 8; s2.Step(t2); }
        Assert.True(s2.AtRest); Assert.Equal(Matrix.Identity, s2.Current());
    }

    [Fact]
    public void ANonFinitePointerMustNotPutNaNInTheMatrix()
    {
        var sq = new DragSquash();
        sq.Observe(0, 0, 0);
        sq.Observe(double.NaN, 0, 8);
        var m = sq.Step(16);
        Assert.True(double.IsFinite(m.M11) && double.IsFinite(m.M12) && double.IsFinite(m.M22), $"{m}");
    }

    // ---- M5 ----

    private static (PixelPoint End, int MaxExcursion, long Ms) Glide(
        PixelPoint from, PixelPoint to, (double, double) v, bool reduce, long frame)
    {
        var g = new SpringGlide(from, to, v, 0, reduce);
        long t = 0; var max = 0; PixelPoint p = from;
        while (!g.Done && t < 20000)
        {
            t += frame;
            p = g.Step(t);
            max = Math.Max(max, Math.Max(Math.Abs(p.X - to.X), Math.Abs(p.Y - to.Y)));
        }
        return (p, max, t);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(100)]
    public void EveryGlideEndsExactlyOnTheSlotAtAnyFrameRateWithMotionOnOrReduced(long frame)
    {
        foreach (var reduce in new[] { false, true })
            foreach (var (from, to) in new[] { (new PixelPoint(0, 0), new PixelPoint(400, 300)), (new PixelPoint(900, 50), new PixelPoint(-30, 700)), (new PixelPoint(5, 5), new PixelPoint(6, 5)) })
            {
                var r = Glide(from, to, (300, -200), reduce, frame);
                Assert.Equal(to, r.End);
                if (reduce) Assert.True(r.Ms <= 150 + frame, $"reduced glide took {r.Ms} ms");
            }
    }

    [Fact]
    public void FromRestTheOvershootIsAboutEightPercentAndReducedMotionHasNone()
    {
        double PeakPastSlot(bool reduce)
        {
            var g = new SpringGlide(new PixelPoint(0, 0), new PixelPoint(500, 0), (0, 0), 0, reduce);
            var peak = 0.0;
            for (long t = 4; t < 3000 && !g.Done; t += 4) peak = Math.Max(peak, g.Step(t).X - 500);
            return peak;
        }
        Assert.InRange(PeakPastSlot(false), 25, 55);       // ~8% of 500 = 40
        Assert.Equal(0, PeakPastSlot(true));               // reduce motion: never past the slot
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(8000)]
    [InlineData(30000)]
    public void AFlungReleaseDoesNotCarryTheOrbFarPastItsSlot(double speed)
    {
        var r = Glide(new PixelPoint(500, 500), new PixelPoint(520, 500), (speed, 0), false, 8);
        _out.WriteLine($"release velocity {speed}: max excursion {r.MaxExcursion}px");
        // A slot that is 20px away must not become a trip across the screen. 400 px is generous (a quarter of a 1600-wide screen).
        Assert.True(r.MaxExcursion <= 400, $"release velocity {speed} px/s: orb flew {r.MaxExcursion}px past its slot");
        Assert.Equal(new PixelPoint(520, 500), r.End);
    }

    [Theory]
    [InlineData(90, 1500.0, 1.0)]
    [InlineData(90, 40000.0, 1.0)]
    [InlineData(0, 1500.0, 1.0)]
    [InlineData(5, 900.0, 2.0)]
    [InlineData(400, 8000.0, 2.0)]
    [InlineData(90, -1500.0, 1.0)]
    public void AFlungOvershootIsInTheThrowDirectionWithinTheCapAndEndsExactlyOnTheSlot(int distance, double vDip, double units)
    {
        // The slot is `distance` px ahead along +x; a negative velocity is a throw away from it.
        var from = new PixelPoint(1000 - distance, 400); var to = new PixelPoint(1000, 400);
        var vUnits = vDip * units;                       // SessionManager seeds the glide in screen units
        var g = new SpringGlide(from, to, (vUnits, 0), 0, false);
        double maxPast = 0, minBehind = 0; var lateral = 0;
        PixelPoint last = from;
        for (long t = 4; t < 5000 && !g.Done; t += 4)
        {
            last = g.Step(t);
            maxPast = Math.Max(maxPast, last.X - to.X);
            minBehind = Math.Min(minBehind, last.X - to.X);
            lateral = Math.Max(lateral, Math.Abs(last.Y - to.Y));
        }
        var cap = Math.Max(0.08 * distance, 12) * units;   // 8% of the distance or 12 DIP, in screen units
        _out.WriteLine($"d={distance} v={vDip} u={units}: past {maxPast}, behind {minBehind}, cap {cap:F1}");
        Assert.True(g.Done);
        Assert.Equal(to, last);                            // exactly on the slot
        Assert.Equal(0, lateral);                          // a throw along x never leaves the axis
        Assert.True(maxPast <= cap + 1.5, $"overshot {maxPast}px past the slot, cap {cap:F1}");
        // A throw away from the slot carries the orb back by at most the same cap before the spring pulls it home.
        Assert.True(minBehind >= -(distance * units + cap + 1.5), $"carried {minBehind}px behind where it started");
    }

    [Theory]
    [InlineData(1500.0)]
    [InlineData(-1500.0)]
    [InlineData(40000.0)]
    public void UnderReduceMotionAFlingChangesNothing(double v)
    {
        var from = new PixelPoint(910, 400); var to = new PixelPoint(1000, 400);
        var flung = new SpringGlide(from, to, (v, 0), 0, true);
        var still = new SpringGlide(from, to, (0, 0), 0, true);
        for (long t = 8; t < 400; t += 8)
        {
            var a = flung.Step(t); var b = still.Step(t);
            Assert.Equal(b, a);
            Assert.True(a.X <= to.X, $"reduce motion went past the slot at {t} ms");
        }
        Assert.True(flung.Done && still.Done);
    }
}
