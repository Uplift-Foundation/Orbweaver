using System;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-283 M2 QA: the drop pop, probed independently of DropPopTests.
public class DropPopQaTests
{
    [Fact]
    public void ItHitsTheThreeKeyframesAndStaysInsideTheDipAndThePeak()
    {
        Assert.Equal(1.15, DropPop.ScaleAt(70), 12);
        Assert.Equal(0.92, DropPop.ScaleAt(170), 12);
        Assert.Equal(1.0, DropPop.ScaleAt(280));
        for (var ms = -50.0; ms <= 400; ms += 0.1)
        {
            var s = DropPop.ScaleAt(ms);
            Assert.InRange(s, 0.92 - 1e-12, 1.15 + 1e-12);
        }
    }

    [Fact]
    public void ItIsContinuousWithNoOneMillisecondStepOverTwoPercentAndRisesFromRest()
    {
        var prev = DropPop.ScaleAt(-1);
        Assert.Equal(1.0, prev);
        var worst = 0.0;
        for (var ms = 0.0; ms <= 300; ms += 1)
        {
            var s = DropPop.ScaleAt(ms);
            worst = Math.Max(worst, Math.Abs(s - prev));
            prev = s;
        }
        Assert.True(worst < 0.02, $"biggest 1 ms step {worst}");
        Assert.True(DropPop.ScaleAt(1) < 1.001, "must rise from 1.0, not start on the peak");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1e9)]
    [InlineData(1e9)]
    [InlineData(280.0)]
    [InlineData(280.0000001)]
    public void ARestOrNonFiniteTimeIsExactlyOne(double ms) => Assert.Equal(1.0, DropPop.ScaleAt(ms));
}
