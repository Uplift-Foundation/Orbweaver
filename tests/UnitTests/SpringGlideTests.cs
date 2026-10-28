using Avalonia;
using Xunit;

namespace Orbweaver.Tests;

// CB-268 M5/M6: an orb gliding to a slot, against a fake clock.
public class SpringGlideTests
{
    private static (int PeakX, long Ms, PixelPoint Final) Run(SpringGlide glide, long start)
    {
        var peak = int.MinValue;
        var t = start;
        PixelPoint last = default;
        while (!glide.Done && t < start + 5000)
        {
            t += 16;
            last = glide.Step(t);
            peak = System.Math.Max(peak, last.X);
        }

        return (peak, t - start, last);
    }

    [Fact]
    public void ItEndsExactlyOnTheSlotWithOvershootInBetween()
    {
        var glide = new SpringGlide(new PixelPoint(0, 0), new PixelPoint(100, 40), (0, 0), 0, reduceMotion: false);

        var (peak, ms, final) = Run(glide, 0);

        Assert.Equal(new PixelPoint(100, 40), final);
        Assert.True(glide.Done);
        Assert.InRange(peak, 101, 109);    // overshoots, by less than ~9%
        Assert.InRange(ms, 100, 2000);
    }

    [Fact]
    public void ReleaseVelocityIsCarriedIntoTheSettle()
    {
        // Flung away from the slot it travels further the wrong way first.
        var still = new SpringGlide(new PixelPoint(0, 0), new PixelPoint(0, 0), (0, 0), 0, false);
        var flung = new SpringGlide(new PixelPoint(0, 0), new PixelPoint(0, 0), (-2000, 0), 0, false);

        Assert.True(still.Done);
        Assert.False(flung.Done);
        Assert.True(flung.Step(16).X < 0);
    }

    [Fact]
    public void AGlideFromTheSlotItselfAtRestIsAlreadyDone()
    {
        var glide = new SpringGlide(new PixelPoint(5, 5), new PixelPoint(5, 5), (0, 0), 0, false);

        Assert.True(glide.Done);
        Assert.Equal(new PixelPoint(5, 5), glide.Step(16));
    }

    [Fact]
    public void ReduceMotionIsAPlainEasedGlideWithNoOvershoot()
    {
        var glide = new SpringGlide(new PixelPoint(0, 0), new PixelPoint(100, 0), (900, 0), 0, reduceMotion: true);

        var xs = new List<int>();
        for (long t = 16; t < 150; t += 16) xs.Add(glide.Step(t).X);

        Assert.All(xs, x => Assert.InRange(x, 0, 100));
        Assert.Equal(xs.OrderBy(x => x), xs);             // only ever moves towards it
        Assert.False(glide.Done);
        Assert.Equal(new PixelPoint(100, 0), glide.Step(150));   // exactly on the slot at 150 ms
        Assert.True(glide.Done);
    }

    [Fact]
    public void ReduceMotionTakesAboutOneHundredAndFiftyMilliseconds()
    {
        var glide = new SpringGlide(new PixelPoint(0, 0), new PixelPoint(50, 50), (0, 0), 1000, true);

        Assert.False(glide.Done);
        glide.Step(1100);
        Assert.False(glide.Done);
        Assert.Equal(new PixelPoint(50, 50), glide.Step(1150));
        Assert.True(glide.Done);
    }

    [Fact]
    public void AFinishedGlideStaysOnTheTarget()
    {
        var glide = new SpringGlide(new PixelPoint(0, 0), new PixelPoint(10, 10), (0, 0), 0, false);
        Run(glide, 0);

        Assert.Equal(new PixelPoint(10, 10), glide.Step(99999));
        Assert.Equal(new PixelPoint(10, 10), glide.Target);
    }

    [Fact]
    public void ItIsDeterministic()
    {
        var a = Run(new SpringGlide(new PixelPoint(3, 9), new PixelPoint(80, -20), (100, 50), 0, false), 0);
        var b = Run(new SpringGlide(new PixelPoint(3, 9), new PixelPoint(80, -20), (100, 50), 0, false), 0);

        Assert.Equal(a, b);
    }

    [Fact]
    public void AClockThatDoesNotAdvanceChangesNothing()
    {
        var glide = new SpringGlide(new PixelPoint(0, 0), new PixelPoint(100, 0), (0, 0), 500, false);

        var first = glide.Step(500);

        Assert.Equal(new PixelPoint(0, 0), first);
        Assert.False(glide.Done);
    }
}
