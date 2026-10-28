using System;
using System.Linq;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-268 motion QA (M6): reduce-motion glide under a fake clock.
[Collection("Settings")]
public class MotionPolicyQaTests
{
    [Theory]
    [InlineData(0.0, 300.0)]
    [InlineData(500.0, -120.0)]
    [InlineData(-40.0, -41.0)]
    public void TheReducedGlideIsMonotoneNeverOvershootsAndEndsExactlyOnTheTarget(double from, double to)
    {
        var lo = Math.Min(from, to); var hi = Math.Max(from, to);
        var last = from;
        for (var t = 0.0; t <= 0.4; t += 0.001)
        {
            var v = MotionPolicy.ReducedGlide(from, to, t);
            Assert.InRange(v, lo - 1e-9, hi + 1e-9);                         // never outside [from, to]: no overshoot
            Assert.True(to >= from ? v >= last - 1e-9 : v <= last + 1e-9);   // monotone
            last = v;
        }
        Assert.Equal(from, MotionPolicy.ReducedGlide(from, to, 0));
        Assert.Equal(from, MotionPolicy.ReducedGlide(from, to, -5));
        Assert.Equal(to, MotionPolicy.ReducedGlide(from, to, MotionPolicy.ReducedGlideSeconds));   // exactly
        Assert.Equal(to, MotionPolicy.ReducedGlide(from, to, 1000));
    }

    [Fact]
    public void ControlAPlayfulSpringWouldHaveLeftTheRangeSoTheBoundCanFail()
    {
        var s = new SpringState(0, 0); var peak = 0.0;
        for (var i = 0; i < 400; i++) { s = Spring.Step(s, 100, 0.005, Spring.Pop); peak = Math.Max(peak, s.Value); }
        Assert.True(peak > 100);
    }

    [Fact]
    public void ReduceMotionTurnsOffEveryPlayfulEffectTogetherAndBackOn()
    {
        try
        {
            MotionPolicy.ReduceMotionProbe = () => true;
            Assert.False(MotionPolicy.AllowsOvershoot); Assert.False(MotionPolicy.AllowsSquash); Assert.False(MotionPolicy.AllowsMagneticPull);
            MotionPolicy.ReduceMotionProbe = () => false;
            Assert.True(MotionPolicy.AllowsOvershoot); Assert.True(MotionPolicy.AllowsSquash); Assert.True(MotionPolicy.AllowsMagneticPull);
        }
        finally { MotionPolicy.ReduceMotionProbe = null; MotionPolicy.Clock = null; }
    }

    [Fact]
    public void TheAnswerIsCachedForASecondThenReReadAndAThrowingProbeReadsAsNotAsked()
    {
        long now = 1_000_000; var reads = 0; var answer = true;
        try
        {
            MotionPolicy.Clock = () => now;
            MotionPolicy.ReduceMotionProbe = () => { reads++; return answer; };
            Assert.True(MotionPolicy.ReduceMotion);
            answer = false; now += 999;
            Assert.True(MotionPolicy.ReduceMotion);                  // cached
            Assert.Equal(1, reads);
            now += 2;
            Assert.False(MotionPolicy.ReduceMotion);                 // re-read after a second
            Assert.Equal(2, reads);

            now += 5000;
            MotionPolicy.ReduceMotionProbe = () => throw new InvalidOperationException("accessibility API failed");
            Assert.False(MotionPolicy.ReduceMotion);
        }
        finally { MotionPolicy.ReduceMotionProbe = null; MotionPolicy.Clock = null; }
    }

    [Fact]
    public void ANonFiniteElapsedTimeMustNotProduceNaN()
    {
        Assert.True(double.IsFinite(MotionPolicy.ReducedGlide(10, 50, double.NaN)));
    }
}
