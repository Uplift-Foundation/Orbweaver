using System;
using System.Linq;
using Avalonia;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-268 M3/M1 QA: the morph's pure pieces, probed independently of the engineer's
// own cases. Time is a plain number here, so nothing depends on a clock.
public class OutlineMorphQaTests
{
    private static Point[] Square(double cx, double cy, double h) => new[]
    {
        new Point(cx - h, cy - h), new Point(cx + h, cy - h), new Point(cx + h, cy + h), new Point(cx - h, cy + h),
    };

    private static Point[] Triangle(double cx, double cy, double r) => new[]
    {
        new Point(cx, cy - r), new Point(cx + r, cy + r), new Point(cx - r, cy + r),
    };

    [Theory]
    [InlineData(28.0, 0.7)]
    [InlineData(34.0, 0.5)]
    public void TheSpringStartsAtZeroAndTheDriversSnapAtThreeHundredMillisecondsJumpsByUnderAHalfPercent(double omega, double damping)
    {
        Assert.Equal(0, OutlineMorph.SpringProgress(0, omega, damping), 12);
        var first = -1.0;
        for (var t = 0.0; t <= 3.0; t += 0.001)
        {
            Assert.True(double.IsFinite(OutlineMorph.SpringProgress(t, omega, damping)));
            if (first < 0 && OutlineMorph.AtRest(t, omega, damping)) { first = t; Assert.Equal(1.0, OutlineMorph.SpringProgress(t, omega, damping)); }
        }
        Assert.True(first > 0, "never reached rest");
        // Advance() ends a morph at the first rest OR at MorphSeconds, whichever comes
        // first, and snaps onto the exact target. The jump it makes is the unsnapped
        // distance from 1 at that moment: the leave spring (damping 0.5) is still
        // 0.31% over at 300 ms, which is the largest the snap ever moves anything.
        var end = Math.Min(first, BackdropMotion.MorphSeconds);
        var raw = Spring.Step(default, 1, end, new SpringParams(omega, damping, 0.002, 0.05)).Value;
        Assert.InRange(Math.Abs(raw - 1), 0, 0.005);
    }

    [Theory]
    [InlineData(28.0, 0.7)]
    [InlineData(34.0, 0.5)]
    public void TheSpringRestsWellInsideTheThreeHundredMillisecondSnapAndOvershootIsBounded(double omega, double damping)
    {
        var max = double.MinValue; var min = double.MaxValue;
        for (var t = 0.0; t <= 2.0; t += 0.0005)
        {
            var s = OutlineMorph.SpringProgress(t, omega, damping);
            max = Math.Max(max, s); min = Math.Min(min, s);
        }
        Assert.InRange(max, 1.0, 1.25);   // a swell, not a fling
        Assert.True(min >= -0.01, $"went backwards to {min}");
        Assert.True(OutlineMorph.AtRest(0.6, omega, damping), "not at rest by 600 ms");
    }

    [Fact]
    public void AFrameAtZeroIsTheSourceAndAtOneIsTheTargetAndRadiiNeverGoNegative()
    {
        var a = OutlineMorph.Resample(Square(100, 100, 40));
        var b = OutlineMorph.Resample(Triangle(300, 200, 90));
        var f0 = OutlineMorph.Frame(a, b, 0);
        var f1 = OutlineMorph.Frame(a, b, 1);
        var ref0 = OutlineMorph.Frame(a, a, 0.37);   // from == to is the identity at any s
        Assert.Equal(OutlineMorph.Samples, f0.Length);
        for (var k = 0; k < OutlineMorph.Samples; k++)
        {
            var ang = 2 * Math.PI * k / OutlineMorph.Samples;
            Assert.Equal(a.Centre.X + a.Radii[k] * Math.Sin(ang), f0[k].X, 9);
            Assert.Equal(b.Centre.Y - b.Radii[k] * Math.Cos(ang), f1[k].Y, 9);
            Assert.Equal(a.Centre.X + a.Radii[k] * Math.Sin(ang), ref0[k].X, 9);
        }
        foreach (var s in new[] { -5.0, -0.5, 1.5, 7.0 })
            Assert.All(OutlineMorph.Frame(a, b, s), p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y)));
    }

    [Fact]
    public void EveryFrameOfARealSpringStaysFiniteAndTheLastOneIsExactlyTheTarget()
    {
        var a = OutlineMorph.Resample(Square(0, 0, 50));
        var b = OutlineMorph.Resample(Square(80, 20, 120));
        double[] last = null;
        for (var t = 0.0; t <= 1.0; t += 0.004)
        {
            var s = OutlineMorph.SpringProgress(t, 28, 0.7);
            var f = OutlineMorph.Frame(a, b, s);
            Assert.All(f, p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y)));
            last = f.SelectMany(p => new[] { p.X, p.Y }).ToArray();
        }
        var target = OutlineMorph.Frame(a, b, 1.0).SelectMany(p => new[] { p.X, p.Y }).ToArray();
        Assert.Equal(target, last);
    }

    [Fact]
    public void BreathStartsAtTheTrueSizeStaysWithinOneAndOnePointZeroFourAndPeaksOnce()
    {
        Assert.Equal(1.00, OutlineMorph.Breath(0), 12);
        double min = 9, max = 0;
        for (var t = 0.0; t <= 10; t += 0.001)
        {
            var b = OutlineMorph.Breath(t);
            min = Math.Min(min, b); max = Math.Max(max, b);
        }
        Assert.InRange(min, 1.0 - 1e-9, 1.0 + 1e-6);
        Assert.InRange(max, 1.04 - 1e-4, 1.04 + 1e-9);
        Assert.Equal(1.04, OutlineMorph.Breath(OutlineMorph.BreathPeriodSeconds / 2), 9);
        Assert.Equal(1.00, OutlineMorph.Breath(OutlineMorph.BreathPeriodSeconds), 9);
    }

    [Fact]
    public void AGrowingJoinAndAContractingLeaveAreTold()
    {
        Assert.True(OutlineMorph.IsGrowing(Square(0, 0, 10), Square(0, 0, 40)));
        Assert.False(OutlineMorph.IsGrowing(Square(0, 0, 40), Square(0, 0, 10)));
    }
}
