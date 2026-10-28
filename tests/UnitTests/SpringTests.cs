using Xunit;

namespace Orbweaver.Tests;

// CB-268 motion: the spring maths, driven with a fake clock (a plain list of
// timesteps), never a real one. What every animation depends on: it settles,
// it lands exactly, its overshoot is bounded, and it is the same however the
// frames are cut.
public class SpringTests
{
    private const double Frame = 1.0 / 60;

    // Runs a spring from `from` to `target` and returns (peak, seconds to rest,
    // final state). Gives up after ten seconds, which would be a failure.
    private static (double Peak, double Seconds, SpringState Final) Run(
        double from, double target, SpringParams p, double dt = Frame, double v0 = 0)
    {
        var state = new SpringState(from, v0);
        var peak = from;
        var t = 0.0;
        while (!Spring.AtRest(state, target) && t < 10)
        {
            state = Spring.Step(state, target, dt, p);
            t += dt;
            peak = target >= from ? Math.Max(peak, state.Value) : Math.Min(peak, state.Value);
        }

        return (peak, t, state);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3)]
    [InlineData(0.63)]
    [InlineData(1.0)]
    [InlineData(1.8)]
    public void EveryDampingSettlesAndLandsExactlyOnTheTarget(double zeta)
    {
        var (_, seconds, final) = Run(0, 100, new SpringParams(26, zeta == 0 ? 0.05 : zeta));

        Assert.True(seconds < 10, $"zeta {zeta} did not settle");
        Assert.Equal(100.0, final.Value);   // exactly, not approximately
        Assert.Equal(0.0, final.Velocity);
    }

    [Fact]
    public void ThePopPresetOvershootsAboutEightPercentAndNoMore()
    {
        var (peak, _, _) = Run(0, 1, Spring.Pop, dt: 0.001);

        Assert.InRange(peak - 1, 0.07, 0.09);
    }

    [Fact]
    public void OvershootStaysBoundedAtFrameRates()
    {
        // The bound has to hold at the frame cut a user actually has.
        foreach (var dt in new[] { 1.0 / 30, 1.0 / 60, 1.0 / 144 })
        {
            var (peak, _, _) = Run(0, 100, Spring.Pop, dt);
            Assert.InRange(peak, 100, 109);
        }
    }

    [Fact]
    public void ACriticallyDampedSpringNeverCrossesTheTarget()
    {
        var (peak, _, _) = Run(0, 100, new SpringParams(26, 1.0), dt: 0.001);

        Assert.True(peak <= 100);

        var (peakDown, _, _) = Run(100, 0, new SpringParams(26, 1.0), dt: 0.001);
        Assert.True(peakDown >= 0);
    }

    [Fact]
    public void AnOverdampedSpringNeverCrossesTheTargetEither()
    {
        Assert.True(Run(0, 100, new SpringParams(26, 2.5), dt: 0.001).Peak <= 100);
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.08)]
    [InlineData(0.3)]
    public void DampingRatioForOvershootRoundTripsThroughOvershoot(double overshoot)
    {
        Assert.Equal(overshoot, Spring.Overshoot(Spring.DampingRatioForOvershoot(overshoot)), 9);
    }

    [Fact]
    public void NoOvershootWantedIsCriticalDamping()
    {
        Assert.Equal(1.0, Spring.DampingRatioForOvershoot(0));
        Assert.Equal(1.0, Spring.DampingRatioForOvershoot(-3));
        Assert.Equal(0.0, Spring.Overshoot(1.0));
        Assert.Equal(0.0, Spring.Overshoot(2.0));
        // Nonsense fractions are clamped rather than throwing.
        Assert.True(Spring.DampingRatioForOvershoot(5) < 0.01);
        Assert.Equal(Spring.Overshoot(0), Spring.Overshoot(-1));
    }

    [Fact]
    public void ItIsDeterministic()
    {
        var a = Run(10, -40, Spring.Pop, v0: 5);
        var b = Run(10, -40, Spring.Pop, v0: 5);

        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(0.4)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void OneBigStepEqualsManySmallOnesBecauseItIsClosedForm(double zeta)
    {
        var p = new SpringParams(20, zeta, RestDelta: 0, RestVelocity: 0);   // no rest snapping, so only the maths is compared
        var start = new SpringState(0, 3);

        var big = Spring.Step(start, 100, 0.2, p);
        var small = start;
        for (var i = 0; i < 200; i++) small = Spring.Step(small, 100, 0.001, p);

        Assert.Equal(big.Value, small.Value, 6);
        Assert.Equal(big.Velocity, small.Velocity, 6);
    }

    [Fact]
    public void ADroppedFrameDoesNotChangeWhereItEndsUp()
    {
        var coarse = Run(0, 100, Spring.Pop, dt: 0.2).Final;
        var fine = Run(0, 100, Spring.Pop, dt: 0.004).Final;

        Assert.Equal(fine, coarse);
    }

    [Fact]
    public void AHugeTimestepDoesNotBlowUp()
    {
        var state = Spring.Step(new SpringState(0, 0), 100, 1000, Spring.Pop);

        Assert.Equal(100.0, state.Value);
    }

    [Fact]
    public void ItMovesTowardsATargetBelowTheStartToo()
    {
        var (peak, _, final) = Run(100, 0, Spring.Pop, dt: 0.001);

        Assert.Equal(0.0, final.Value);
        Assert.InRange(-peak, 0.07 * 100, 0.09 * 100);
    }

    [Fact]
    public void AnInitialVelocityIsCarriedNotDiscarded()
    {
        // Thrown away from the target it goes further out before turning back.
        var from = Spring.Step(new SpringState(0, -500), 0, Frame, Spring.Pop);
        var still = Spring.Step(new SpringState(0, 0), 0, Frame, Spring.Pop);

        Assert.True(from.Value < 0);
        Assert.Equal(0.0, still.Value);
    }

    [Fact]
    public void ANonPositiveStepChangesNothing()
    {
        var s = new SpringState(3, 4);

        Assert.Equal(s, Spring.Step(s, 10, 0, Spring.Pop));
        Assert.Equal(s, Spring.Step(s, 10, -1, Spring.Pop));
    }

    [Fact]
    public void ANonPositiveFrequencyJumpsStraightToTheTarget()
    {
        Assert.Equal(new SpringState(10, 0), Spring.Step(new SpringState(3, 4), 10, Frame, new SpringParams(0, 1)));
    }

    [Fact]
    public void ANegativeDampingIsTreatedAsNone()
    {
        // No damping at all would never rest; a negative one must not explode.
        var state = new SpringState(0, 0);
        for (var i = 0; i < 600; i++) state = Spring.Step(state, 1, Frame, new SpringParams(10, -5));

        Assert.InRange(state.Value, -1.1, 2.1);
    }

    [Fact]
    public void RestIsExactOnlyWithinTheThresholds()
    {
        var p = new SpringParams(26, 1.0, RestDelta: 0.5, RestVelocity: 0.5);

        // Run long enough that both the distance and the speed are inside their
        // thresholds: it snaps to the target.
        Assert.True(Spring.AtRest(Spring.Step(new SpringState(99.7, 0.1), 100, 1.0, p), 100));
        // Inside on distance but still moving fast: keeps going.
        var moving = Spring.Step(new SpringState(99.9, 50), 100, 0.001, p);
        Assert.False(Spring.AtRest(moving, 100));
        Assert.NotEqual(100.0, moving.Value);
    }

    [Fact]
    public void AtRestMeansExactlyOnTheTargetWithNoVelocity()
    {
        Assert.True(Spring.AtRest(new SpringState(5, 0), 5));
        Assert.False(Spring.AtRest(new SpringState(5, 0.0001), 5));
        Assert.False(Spring.AtRest(new SpringState(5.0001, 0), 5));
    }

    [Fact]
    public void TheRelaxPresetBarelyOvershoots()
    {
        Assert.InRange(Run(0, 1, Spring.Relax, dt: 0.001).Peak - 1, 0.0, 0.015);
    }
}
