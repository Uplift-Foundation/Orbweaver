using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-268 motion QA: properties of Spring.Step under a fake clock.
public class SpringQaTests
{
    private static readonly SpringParams[] All =
    {
        Spring.Pop, Spring.Relax, new(30, 1.0), new(30, 2.5), new(12, 0.3), new(60, 0.05),
    };

    public static IEnumerable<object[]> Params() => Enumerable.Range(0, All.Length).Select(i => new object[] { i });

    private static (double Peak, double Settled, bool Exact, double Seconds) Run(
        SpringParams p, double from, double to, double v0, double dt)
    {
        var s = new SpringState(from, v0);
        var peak = from; var t = 0.0;
        var signed = Math.Sign(to - from);
        for (; t < 30; t += dt)
        {
            s = Spring.Step(s, to, dt, p);
            peak = signed >= 0 ? Math.Max(peak, s.Value) : Math.Min(peak, s.Value);
            if (Spring.AtRest(s, to)) return (peak, s.Value, true, t + dt);
        }
        return (peak, s.Value, false, t);
    }

    [Theory]
    [MemberData(nameof(Params))]
    public void EveryAnimationEndsExactlyOnTheTargetAtAnyFrameRate(int index)
    {
        var p = All[index];
        foreach (var dt in new[] { 0.001, 0.008, 0.016, 0.033, 0.1, 0.5 })
            foreach (var (from, to) in new[] { (0.0, 300.0), (500.0, -120.0), (-40.0, -41.0) })
            {
                var r = Run(p, from, to, 0, dt);
                Assert.True(r.Exact, $"{p} dt={dt} {from}->{to}: did not come to rest in 30 s");
                Assert.Equal(to, r.Settled);               // exactly, not "close"
            }
    }

    [Theory]
    [MemberData(nameof(Params))]
    public void FromRestOvershootIsAtMostTheDesignedFractionOfTheDistance(int index)
    {
        var p = All[index];
        var distance = 400.0;
        var r = Run(p, 0, distance, 0, 0.001);
        var overshoot = (r.Peak - distance) / distance;
        var designed = Spring.Overshoot(p.Zeta);
        Assert.True(overshoot <= designed + 0.002, $"{p}: overshoot {overshoot:P2} > designed {designed:P2}");
        Assert.True(overshoot >= 0 - 1e-9);
        if (p.Zeta >= 1) Assert.True(r.Peak <= distance + 1e-9, $"{p}: critically/over-damped spring overshot");
    }

    [Fact]
    public void ControlAnUnderdampedSpringReallyOvershootsSoTheMeasureCanFail()
    {
        var r = Run(new SpringParams(30, 0.3), 0, 400, 0, 0.001);
        Assert.True(r.Peak > 400 * 1.1);
    }

    [Theory]
    [MemberData(nameof(Params))]
    public void OneBigStepAndManySmallStepsAgreeBeforeRest(int index)
    {
        var p = All[index];
        var a = Spring.Step(new SpringState(0, 0), 200, 0.1, p);
        var b = new SpringState(0, 0);
        for (var i = 0; i < 100; i++) b = Spring.Step(b, 200, 0.001, p);
        Assert.Equal(a.Value, b.Value, 4);
        Assert.Equal(a.Velocity, b.Velocity, 3);
    }

    [Theory]
    [MemberData(nameof(Params))]
    public void ARestingSpringStaysAtRestAndNeverDrifts(int index)
    {
        var p = All[index];
        var s = new SpringState(250, 0);
        for (var i = 0; i < 1000; i++) s = Spring.Step(s, 250, 0.016, p);
        Assert.True(Spring.AtRest(s, 250));
    }

    [Fact]
    public void AFlickIntoTheTargetWithLargeVelocityStaysBoundedForPop()
    {
        // A drag release carries the pointer's velocity into the spring-back; a very large one must not fling the orb off-screen.
        foreach (var v in new[] { 0.0, 500, 5000, 50000 })
        {
            var r = Run(Spring.Pop, 0, 100, v, 0.016);
            Assert.True(r.Peak < 100 + 100 + v / 26 * 1.2 + 1, $"v0={v}: peak {r.Peak}");
            Assert.True(r.Exact);
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ANonFiniteTimeStepMustNotPoisonTheStateWithNaN(double dt)
    {
        var s = Spring.Step(new SpringState(10, 3), 50, dt, Spring.Pop);
        Assert.True(double.IsFinite(s.Value) && double.IsFinite(s.Velocity), $"dt={dt}: {s}");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ANonFiniteTargetMustNotPoisonTheStateWithNaN(double target)
    {
        var s = Spring.Step(new SpringState(10, 0), target, 0.016, Spring.Pop);
        Assert.True(double.IsFinite(s.Value) && double.IsFinite(s.Velocity), $"target={target}: {s}");
    }
}
