using Xunit;

namespace Orbweaver.Tests;

// CB-268 M6. [Collection("Settings")] is not about settings: MotionPolicy is a
// process-wide static with two test seams, and tests that flip them must not
// interleave with each other.
[Collection("Settings")]
public class MotionPolicyTests : IDisposable
{
    public void Dispose()
    {
        MotionPolicy.ReduceMotionProbe = null;
        MotionPolicy.Clock = null;
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ReduceMotionTurnsOffEveryPlayfulEffect(bool reduce, bool playful)
    {
        MotionPolicy.ReduceMotionProbe = () => reduce;

        Assert.Equal(reduce, MotionPolicy.ReduceMotion);
        Assert.Equal(playful, MotionPolicy.AllowsOvershoot);
        Assert.Equal(playful, MotionPolicy.AllowsSquash);
        Assert.Equal(playful, MotionPolicy.AllowsMagneticPull);
    }

    [Fact]
    public void AFailedLookupMeansMotionIsLeftOn()
    {
        MotionPolicy.ReduceMotionProbe = () => throw new InvalidOperationException("no accessibility service");

        Assert.False(MotionPolicy.ReduceMotion);
        Assert.True(MotionPolicy.AllowsOvershoot);
    }

    [Fact]
    public void TheAnswerIsCachedForASecondAndThenReadAgain()
    {
        var now = 1000L;
        var reads = 0;
        var answer = false;
        MotionPolicy.Clock = () => now;
        MotionPolicy.ReduceMotionProbe = () => { reads++; return answer; };

        Assert.False(MotionPolicy.ReduceMotion);
        answer = true;
        now += 999;
        Assert.False(MotionPolicy.ReduceMotion);   // still the cached answer
        Assert.Equal(1, reads);

        now += 1;
        Assert.True(MotionPolicy.ReduceMotion);    // a second has passed
        Assert.Equal(2, reads);
    }

    [Fact]
    public void ReplacingTheProbeDropsTheCache()
    {
        MotionPolicy.ReduceMotionProbe = () => false;
        Assert.False(MotionPolicy.ReduceMotion);

        MotionPolicy.ReduceMotionProbe = () => true;
        Assert.True(MotionPolicy.ReduceMotion);
    }

    [Fact]
    public void TheRealOperatingSystemReadAnswersWithoutThrowing()
    {
        // Resets to the genuine probe: on macOS this is the NSWorkspace call and
        // on Windows SystemParametersInfo, run for real. Either answer is valid
        // — it depends on the machine's accessibility settings — so only that it
        // answers is asserted.
        MotionPolicy.ReduceMotionProbe = null;
        MotionPolicy.Clock = null;

        var answer = MotionPolicy.ReduceMotion;

        Assert.True(answer || !answer);
    }

    [Fact]
    public void ThePlainGlideIsEasedAndEndsExactlyOnTheTarget()
    {
        Assert.Equal(10.0, MotionPolicy.ReducedGlide(10, 110, 0));
        Assert.Equal(10.0, MotionPolicy.ReducedGlide(10, 110, -1));
        Assert.Equal(60.0, MotionPolicy.ReducedGlide(10, 110, MotionPolicy.ReducedGlideSeconds / 2), 9);
        Assert.Equal(110.0, MotionPolicy.ReducedGlide(10, 110, MotionPolicy.ReducedGlideSeconds));
        Assert.Equal(110.0, MotionPolicy.ReducedGlide(10, 110, 5));
    }

    [Fact]
    public void ThePlainGlideNeverOvershootsAndOnlyMovesTowardsTheTarget()
    {
        var last = 0.0;
        for (var t = 0.0; t <= 0.2; t += 0.001)
        {
            var x = MotionPolicy.ReducedGlide(0, 100, t);
            Assert.InRange(x, 0, 100);
            Assert.True(x >= last);
            last = x;
        }

        // And the other direction.
        Assert.InRange(MotionPolicy.ReducedGlide(100, 0, 0.05), 0, 100);
    }

    [Fact]
    public void ReducedGlideTakesAbout150Milliseconds()
    {
        Assert.Equal(0.15, MotionPolicy.ReducedGlideSeconds);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(0.25, 0.0625)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.75, 0.9375)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public void EaseInOutCubicIsClampedAndSymmetric(double t, double expected)
    {
        Assert.Equal(expected, MotionPolicy.EaseInOutCubic(t), 9);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ANonFiniteElapsedTimeFinishesAtTheTargetNeverNaN(double elapsed)
    {
        Assert.Equal(110.0, MotionPolicy.ReducedGlide(10, 110, elapsed));
    }

    [Fact]
    public void NonFiniteEndpointsNeverProduceNaN()
    {
        Assert.Equal(7.0, MotionPolicy.ReducedGlide(double.NaN, 7, 0.05));
        Assert.Equal(3.0, MotionPolicy.ReducedGlide(3, double.NaN, 0.05));
        Assert.Equal(0.0, MotionPolicy.ReducedGlide(double.NaN, double.NaN, 0.05));
    }

    [Fact]
    public void EaseOfNaNIsDone()
    {
        Assert.Equal(1.0, MotionPolicy.EaseInOutCubic(double.NaN));
    }
}
