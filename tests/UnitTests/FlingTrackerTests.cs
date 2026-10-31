using Xunit;

namespace Orbweaver.Tests;

// CB-283 M5: the release velocity a drag throws its orbs with, on a fake clock.
public class FlingTrackerTests
{
    // A drag moving `perFrame` DIP every 16 ms for `frames` frames from t=0.
    private static FlingTracker Steady(double perFrameX, double perFrameY, int frames)
    {
        var t = new FlingTracker();
        for (var i = 0; i <= frames; i++) t.Observe(i * perFrameX, i * perFrameY, i * 16);
        return t;
    }

    [Fact]
    public void ASteadyDragReadsItsSpeedPerSecond()
    {
        var t = Steady(20, -10, 10);   // 1250, -625 DIP/s

        var (x, y) = t.VelocityAt(160);

        Assert.Equal(1250, x, 6);
        Assert.Equal(-625, y, 6);
    }

    [Fact]
    public void OnlyTheLastWindowOfTravelCounts()
    {
        // Slow for a long while, then fast for the last 96 ms: the 80 ms window
        // sees only the fast part, so the slow start does not dilute it.
        var t = new FlingTracker();
        long now = 0;
        double x = 0;
        for (var i = 0; i < 20; i++) { now += 16; x += 1; t.Observe(x, 0, now); }
        for (var i = 0; i < 6; i++) { now += 16; x += 40; t.Observe(x, 0, now); }

        var (vx, _) = t.VelocityAt(now);

        Assert.Equal(2500, vx, 6);   // 40 DIP per 16 ms
    }

    [Fact]
    public void APointerThatStoppedBeforeTheReleaseThrewNothing()
    {
        var t = Steady(20, 0, 10);

        Assert.NotEqual((0.0, 0.0), t.VelocityAt(160 + FlingTracker.StaleMs));
        Assert.Equal((0.0, 0.0), t.VelocityAt(160 + FlingTracker.StaleMs + 1));
    }

    [Fact]
    public void TooLittleTravelThrowsNothing()
    {
        var none = new FlingTracker();
        Assert.Equal((0.0, 0.0), none.VelocityAt(0));

        var one = new FlingTracker();
        one.Observe(5, 5, 10);
        Assert.Equal((0.0, 0.0), one.VelocityAt(10));

        // Two samples at the same instant: no time, no speed (and no division by it).
        var same = new FlingTracker();
        same.Observe(0, 0, 10);
        same.Observe(50, 0, 10);
        Assert.Equal((0.0, 0.0), same.VelocityAt(10));
    }

    [Fact]
    public void ANonFiniteOrOutOfOrderSampleIsIgnored()
    {
        var t = Steady(20, 0, 10);
        t.Observe(double.NaN, 0, 170);
        t.Observe(double.PositiveInfinity, 1, 170);
        t.Observe(-9999, 0, 100);   // earlier than the last sample

        var (x, y) = t.VelocityAt(160);
        Assert.Equal(1250, x, 6);
        Assert.Equal(0, y, 6);
    }

    [Fact]
    public void ADragSampledLessOftenThanTheWindowStillHasASpeed()
    {
        var t = new FlingTracker();
        t.Observe(0, 0, 0);
        t.Observe(100, 0, 200);
        t.Observe(150, 0, 250);

        // The sample at 0 is the last one at or before the window's start (170),
        // so it stays as the base: 150 DIP over 250 ms. Dropping it would leave
        // a single fast step and read the throw from 50 ms of travel.
        var (x, _) = t.VelocityAt(250);
        Assert.Equal(600, x, 6);
    }

    [Fact]
    public void AFlingIsNoneWithNoOrbsOrNoVelocity()
    {
        Assert.True(default(Fling).IsNone);
        Assert.True(new Fling(Array.Empty<string>(), 500, 0).IsNone);
        Assert.True(new Fling(new[] { "a" }, 0, 0).IsNone);
        Assert.False(new Fling(new[] { "a" }, 0, -1).IsNone);
        Assert.False(new Fling(new[] { "a" }, 1, 0).IsNone);
    }

    // SessionManager turns a fling from DIP/s into screen units with the orb's
    // measured units per DIP; an unusable measurement is 1, never 0.
    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 2.0)]
    [InlineData(1.25, 1.25)]
    [InlineData(0.0, 1.0)]
    [InlineData(-2.0, 1.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    public void AnUnusableUnitsMeasurementIsOneNotZero(double measured, double used) =>
        Assert.Equal(used, SessionManager.UsableUnits(measured));
}
