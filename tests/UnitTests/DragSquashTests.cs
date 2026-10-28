using Avalonia;
using Xunit;

namespace Orbweaver.Tests;

// CB-268 M4, against a fake clock: millisecond timestamps the test advances.
// [Collection("Settings")] only because MotionPolicy is a process-wide static.
[Collection("Settings")]
public class DragSquashTests : IDisposable
{
    public DragSquashTests() => MotionPolicy.ReduceMotionProbe = () => false;
    public void Dispose() => MotionPolicy.ReduceMotionProbe = null;

    private static double Len(Point p) => System.Math.Sqrt(p.X * p.X + p.Y * p.Y);

    // Drags at `speed` DIP/s along (dx, dy) for `ms` milliseconds in 16 ms frames.
    private static long Drag(DragSquash squash, double speed, double dx, double dy, long fromMs, long ms)
    {
        var x = 0.0;
        var y = 0.0;
        var t = fromMs;
        squash.Observe(x, y, t);
        while (t < fromMs + ms)
        {
            t += 16;
            x += dx * speed * 0.016;
            y += dy * speed * 0.016;
            squash.Observe(x, y, t);
            squash.Step(t);
        }

        return t;
    }

    [Fact]
    public void ANewSquashIsAtRestAndTheIdentity()
    {
        var squash = new DragSquash();

        Assert.True(squash.AtRest);
        Assert.Equal(Matrix.Identity, squash.Current());
        Assert.Equal(Matrix.Identity, squash.Step(1000));
    }

    [Fact]
    public void AFastDragStretchesAboutTenPercentAndNoMore()
    {
        var squash = new DragSquash();

        Drag(squash, 6000, 1, 0, 0, 800);   // far faster than FullSpeed

        Assert.InRange(squash.Stretch, 0.09, 0.115);
    }

    [Fact]
    public void ASlowerDragStretchesProportionallyLess()
    {
        var slow = new DragSquash();
        var brisk = new DragSquash();

        Drag(slow, 300, 1, 0, 0, 800);
        Drag(brisk, 1200, 1, 0, 0, 800);

        Assert.InRange(slow.Stretch, 0.02, 0.03);   // 300/1200 of 10%
        Assert.InRange(brisk.Stretch, 0.09, 0.105);
        Assert.True(slow.Stretch < brisk.Stretch);
    }

    [Fact]
    public void TheStretchIsAlongTheDirectionOfTravelAndNarrowsAcrossIt()
    {
        var squash = new DragSquash();
        Drag(squash, 6000, 1, 0, 0, 800);
        var m = squash.Current();

        // Along x it is longer; along y it is narrower by the reciprocal.
        var along = m.Transform(new Point(1, 0));
        var across = m.Transform(new Point(0, 1));
        Assert.InRange(along.X, 1.09, 1.115);
        Assert.Equal(0.0, along.Y, 6);
        Assert.InRange(across.Y, 0.89, 0.92);
        Assert.Equal(1 / along.X, across.Y, 6);
    }

    [Fact]
    public void ADiagonalDragStretchesAlongTheDiagonal()
    {
        var squash = new DragSquash();
        var d = 1 / System.Math.Sqrt(2);
        Drag(squash, 6000, d, d, 0, 800);

        var alongDiagonal = squash.Current().Transform(new Point(d, d));

        Assert.InRange(Len(alongDiagonal), 1.09, 1.115);
        // Along the other diagonal it narrows.
        Assert.True(Len(squash.Current().Transform(new Point(d, -d))) < 1);
    }

    [Fact]
    public void ItRelaxesToExactlyTheIdentityWhenThePointerStops()
    {
        var squash = new DragSquash();
        var t = Drag(squash, 6000, 1, 0, 0, 500);

        // No more pointer events: the last velocity goes stale and it relaxes.
        for (var i = 0; i < 90 && !squash.AtRest; i++)
        {
            t += 16;
            squash.Step(t);
        }

        Assert.True(squash.AtRest);
        Assert.Equal(Matrix.Identity, squash.Current());
    }

    [Fact]
    public void ReleasingTheDragRelaxesItToo()
    {
        var squash = new DragSquash();
        var t = Drag(squash, 6000, 1, 0, 0, 500);
        squash.Release();

        for (var i = 0; i < 90 && !squash.AtRest; i++)
        {
            t += 16;
            squash.Step(t);
        }

        Assert.True(squash.AtRest);
    }

    [Fact]
    public void ResetGoesStraightBackWithNoWobble()
    {
        var squash = new DragSquash();
        Drag(squash, 6000, 1, 0, 0, 500);

        squash.Reset();

        Assert.True(squash.AtRest);
        Assert.Equal(Matrix.Identity, squash.Current());
    }

    [Fact]
    public void ReduceMotionKeepsItFlat()
    {
        MotionPolicy.ReduceMotionProbe = () => true;
        var squash = new DragSquash();

        Drag(squash, 6000, 1, 0, 0, 500);

        Assert.Equal(0.0, squash.Stretch);
        Assert.Equal(Matrix.Identity, squash.Current());
    }

    [Fact]
    public void TheFirstObservationOnlyRemembersWhereThePointerIs()
    {
        var squash = new DragSquash();

        squash.Observe(500, 500, 1000);

        Assert.Equal(Matrix.Identity, squash.Step(1016));
        Assert.True(squash.AtRest);
    }

    [Fact]
    public void TheSameTimestampTwiceDoesNotDivideByZero()
    {
        var squash = new DragSquash();
        squash.Observe(0, 0, 1000);
        squash.Observe(50, 0, 1000);   // same ms: no velocity from it

        var m = squash.Step(1016);

        Assert.Equal(Matrix.Identity, m);
    }

    [Fact]
    public void AStandingStartKeepsTheLastDirectionWhileItRelaxes()
    {
        var squash = new DragSquash();
        var t = Drag(squash, 6000, 0, 1, 0, 500);   // straight down

        t += 200;   // pointer stopped
        squash.Step(t);

        // Relaxing along y, not snapping to x.
        var m = squash.Current();
        Assert.True(Len(m.Transform(new Point(0, 1))) >= Len(m.Transform(new Point(1, 0))));
    }
}
