using Xunit;

namespace Orbweaver.Tests;

// CB-283 M2: DropPop's curve, 1.0 -> 1.15 -> 0.92 -> 1.0 over 280 ms, exact at
// every keyframe and at rest outside the pop.
public class DropPopTests
{
    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData((double)DropPop.PeakMs, DropPop.Peak)]
    [InlineData((double)DropPop.DipMs, DropPop.Dip)]
    [InlineData((double)DropPop.DurationMs, 1.0)]
    [InlineData(-5.0, 1.0)]
    [InlineData(10_000.0, 1.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(double.NegativeInfinity, 1.0)]
    public void ItIsExactAtEveryKeyframeAndAtRestOutsideThePop(double ms, double scale) =>
        Assert.Equal(scale, DropPop.ScaleAt(ms), 12);

    [Fact]
    public void ItRisesToThePeakDipsToTheDipAndSettlesWithNoOtherExtremes()
    {
        var samples = Enumerable.Range(0, DropPop.DurationMs * 10 + 1).Select(i => DropPop.ScaleAt(i / 10.0)).ToList();

        Assert.Equal(DropPop.Peak, samples.Max(), 12);
        Assert.Equal(DropPop.Dip, samples.Min(), 12);

        // Up, down, up, one leg each: the sign of the slope changes exactly twice.
        var signs = samples.Zip(samples.Skip(1), (a, b) => Math.Sign(b - a)).Where(s => s != 0).ToList();
        var turns = signs.Zip(signs.Skip(1), (a, b) => a != b).Count(t => t);
        Assert.Equal(2, turns);
        Assert.Equal(1, signs[0]);
    }

    [Fact]
    public void ItIsStillAtEveryKeyframeSoThereIsNoVisibleKink()
    {
        // A half-cosine leg has zero slope at both ends: a 0.1 ms step either
        // side of a keyframe moves the scale by well under a thousandth.
        foreach (var at in new double[] { 0, DropPop.PeakMs, DropPop.DipMs, DropPop.DurationMs })
        {
            Assert.True(Math.Abs(DropPop.ScaleAt(at + 0.1) - DropPop.ScaleAt(at)) < 1e-4, $"after {at}");
            Assert.True(Math.Abs(DropPop.ScaleAt(at - 0.1) - DropPop.ScaleAt(at)) < 1e-4, $"before {at}");
        }
    }
}
