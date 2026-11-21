using Avalonia;
using Orbweaver;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-404: the dial's value↔angle mapping. Angles are degrees clockwise from
// straight up; levels sit on a 270-degree arc from -135 to +135 with the gap
// at the bottom. These are the vectors the phone ports are checked against.
public class EffortDialMathTests
{
    [Theory]
    [InlineData(0, 5, -135)]
    [InlineData(1, 5, -67.5)]
    [InlineData(2, 5, 0)]
    [InlineData(4, 5, 135)]
    [InlineData(0, 2, -135)]
    [InlineData(1, 2, 135)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(-3, 5, -135)]
    [InlineData(9, 5, 135)]
    public void AngleFor(int index, int count, double angle)
    {
        Assert.Equal(angle, EffortDialMath.AngleFor(index, count), 6);
    }

    [Theory]
    [InlineData(-135, 5, 0)]
    [InlineData(-102, 5, 0)]   // just short of halfway to the second tick
    [InlineData(-101.25, 5, 1)] // exactly halfway rounds up
    [InlineData(0, 5, 2)]
    [InlineData(135, 5, 4)]
    [InlineData(170, 5, 4)]    // in the gap, nearer the top end
    [InlineData(-170, 5, 0)]   // in the gap, nearer the bottom end
    [InlineData(180, 5, 4)]    // dead bottom counts as the high side
    [InlineData(225, 5, 0)]    // 225 is -135
    [InlineData(-315, 5, 3)]   // -315 is 45, nearer 67.5 (index 3) than 0
    [InlineData(10, 1, 0)]
    [InlineData(10, 0, -1)]
    public void IndexForAngle(double angle, int count, int index)
    {
        Assert.Equal(index, EffortDialMath.IndexFor(angle, count));
    }

    [Theory]
    [InlineData(50, 0, 0)]     // straight up
    [InlineData(100, 50, 90)]  // right
    [InlineData(0, 50, -90)]   // left
    [InlineData(50, 100, 180)] // straight down
    [InlineData(50, 50, 0)]    // the centre itself
    public void AngleOfAPoint(double x, double y, double angle)
    {
        Assert.Equal(angle, EffortDialMath.AngleOf(new Point(x, y), new Point(50, 50)), 6);
    }

    [Fact]
    public void IndexForAPoint()
    {
        Assert.Equal(4, EffortDialMath.IndexFor(new Point(100, 100), new Point(50, 50), 5)); // lower right
        Assert.Equal(0, EffortDialMath.IndexFor(new Point(0, 100), new Point(50, 50), 5));   // lower left
    }

    [Theory]
    [InlineData(2, 1, 5, 3)]
    [InlineData(4, 1, 5, 4)]
    [InlineData(0, -1, 5, 0)]
    [InlineData(-1, -1, 5, 0)]
    [InlineData(-1, 1, 5, 4)]
    [InlineData(-1, 0, 5, -1)]
    [InlineData(2, 9, 5, 4)]
    [InlineData(0, 1, 0, -1)]
    public void Step(int index, int delta, int count, int expected)
    {
        Assert.Equal(expected, EffortDialMath.Step(index, delta, count));
    }

    [Theory]
    [InlineData(0, 50, 0)]
    [InlineData(90, 100, 50)]
    [InlineData(180, 50, 100)]
    public void PointAtIsTheInverseOfAngleOf(double angle, double x, double y)
    {
        var p = EffortDialMath.PointAt(new Point(50, 50), 50, angle);
        Assert.Equal(x, p.X, 6);
        Assert.Equal(y, p.Y, 6);
    }

    [Theory]
    [InlineData(540, 180)]
    [InlineData(-180, 180)]
    [InlineData(-190, 170)]
    [InlineData(190, -170)]
    public void Normalise(double angle, double expected)
    {
        Assert.Equal(expected, EffortDialMath.Normalise(angle), 6);
    }

    [Theory]
    [InlineData("low", "low")]
    [InlineData("medium", "med")]
    [InlineData("xhigh", "xhi")]
    [InlineData("high", "high")]
    [InlineData("minimal", "min")]
    [InlineData("ultra", "ult")]
    [InlineData("Extended", "Exte")]
    public void ShortLabels(string level, string label)
    {
        Assert.Equal(label, EffortDial.ShortLabel(level));
    }
}
