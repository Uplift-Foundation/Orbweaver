using System;
using Xunit;

namespace Orbweaver.UnitTests;

// ThreadTug: when a team member's thread is tugged, and what a tug looks like.
//
// The decisions are pure and take the clock as an argument, so a tug is asserted
// on at a chosen instant and nothing here sleeps.
[Collection("Settings")]
public class ThreadTugTests : IDisposable
{
    private long _now;

    public ThreadTugTests()
    {
        ThreadTug.Reset();
        ThreadTug.Clock = () => _now;
        MotionPolicy.ReduceMotionProbe = () => false;
    }

    public void Dispose()
    {
        ThreadTug.Reset();
        ThreadTug.Clock = null;
        MotionPolicy.ReduceMotionProbe = null;
    }

    // ---- when to tug ---------------------------------------------------------

    [Fact]
    public void AGeneratingMemberTugsAtOnceThenOnABeat()
    {
        Assert.Equal(1, ThreadTug.Strength("idle", "generating", double.PositiveInfinity));
        Assert.Equal(0, ThreadTug.Strength("generating", "generating", ThreadTug.Period - 0.1));
        Assert.Equal(1, ThreadTug.Strength("generating", "generating", ThreadTug.Period));
    }

    [Fact]
    public void ArrivingAtWaitingTugsOnceAndHarder()
    {
        Assert.Equal(ThreadTug.WaitingStrength, ThreadTug.Strength("generating", "waiting", 0.5));
        Assert.Equal(0, ThreadTug.Strength("waiting", "waiting", 10));
        Assert.True(ThreadTug.WaitingStrength > 1);
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("something-new")]
    public void AnythingElseDoesNotTug(string? state)
    {
        Assert.Equal(0, ThreadTug.Strength("generating", state, double.PositiveInfinity));
    }

    // ---- what a tug looks like -----------------------------------------------

    [Fact]
    public void ATugStartsAtRestAndHasStoppedByItsDuration()
    {
        Assert.Equal(0, ThreadTug.Offset(0, 1), 9);
        Assert.Equal(0, ThreadTug.Offset(ThreadTug.Duration, 1));
        Assert.Equal(0, ThreadTug.Offset(ThreadTug.Duration + 5, 1));
        Assert.Equal(0, ThreadTug.Offset(-0.1, 1));
    }

    [Fact]
    public void ATugSwingsBothWaysAndDiesAway()
    {
        var early = 0.0;
        var late = 0.0;
        var sawPositive = false;
        var sawNegative = false;

        for (var t = 0.0; t < ThreadTug.Duration; t += 0.005)
        {
            var o = ThreadTug.Offset(t, 1);
            if (o > 0) sawPositive = true;
            if (o < 0) sawNegative = true;
            if (t < ThreadTug.Duration / 3) early = Math.Max(early, Math.Abs(o));
            if (t > ThreadTug.Duration * 2 / 3) late = Math.Max(late, Math.Abs(o));
        }

        Assert.True(sawPositive && sawNegative);
        Assert.True(early > late * 2, $"early {early:0.00} should dwarf late {late:0.00}");
        Assert.True(early <= ThreadTug.Amplitude);
    }

    [Fact]
    public void AHarderTugSwingsFurther()
    {
        Assert.Equal(
            ThreadTug.Offset(0.1, 1) * ThreadTug.WaitingStrength,
            ThreadTug.Offset(0.1, ThreadTug.WaitingStrength), 9);
        Assert.Equal(0, ThreadTug.Offset(0.1, 0));
    }

    [Fact]
    public void GlowIsBrightestAtThePullAndFades()
    {
        Assert.Equal(1, ThreadTug.Glow(0, 1), 9);
        Assert.True(ThreadTug.Glow(0.4, 1) < ThreadTug.Glow(0.1, 1));
        Assert.Equal(0, ThreadTug.Glow(ThreadTug.Duration, 1));
        Assert.Equal(0, ThreadTug.Glow(-1, 1));
        Assert.Equal(0, ThreadTug.Glow(0.1, 0));

        // Never above 1, however hard the tug.
        Assert.Equal(1, ThreadTug.Glow(0, 5), 9);
    }

    // ---- tracking members ----------------------------------------------------

    [Fact]
    public void AMemberThatStartsGeneratingTugsAndThenSettles()
    {
        var member = new object();

        Assert.Equal((0, 0), ThreadTug.Now(member));

        ThreadTug.Observe(member, "generating");
        _now = 100;
        var (offset, glow) = ThreadTug.Now(member);
        Assert.NotEqual(0, offset);
        Assert.True(glow > 0);

        _now = (long)(ThreadTug.Duration * 1000) + 100;
        Assert.Equal((0.0, 0.0), ThreadTug.Now(member));
    }

    [Fact]
    public void AGeneratingMemberTugsAgainAfterThePeriod()
    {
        var member = new object();
        ThreadTug.Observe(member, "generating");

        _now = (long)(ThreadTug.Period * 1000) - 1;
        ThreadTug.Observe(member, "generating");
        var before = ThreadTug.Now(member);

        _now = (long)(ThreadTug.Period * 1000);
        ThreadTug.Observe(member, "generating");
        var tugAgain = ThreadTug.Now(member);

        Assert.Equal(0, tugAgain.Offset, 9);          // a fresh tug starts at rest
        Assert.Equal(1, tugAgain.Glow, 9);            // ...and at full glow
        Assert.True(before.Glow < 0.05, "the first tug had faded by the time the second began");
    }

    [Fact]
    public void AnIdleMemberNeverTugs()
    {
        var member = new object();
        ThreadTug.Observe(member, "idle");
        _now = 50;

        Assert.Equal((0, 0), ThreadTug.Now(member));
        Assert.False(ThreadTug.NeedsFrames());
    }

    [Fact]
    public void FramesAreNeededWhileGeneratingAndWhileATugRingsAndNotAfter()
    {
        var member = new object();
        Assert.False(ThreadTug.NeedsFrames());

        ThreadTug.Observe(member, "generating");
        Assert.True(ThreadTug.NeedsFrames());

        // A waiting tug rings for its duration and then needs nothing.
        ThreadTug.Observe(member, "waiting");
        _now = 100;
        Assert.True(ThreadTug.NeedsFrames());

        _now = (long)(ThreadTug.Duration * 1000) + 200;
        Assert.False(ThreadTug.NeedsFrames());
    }

    // ---- interaction silences a tug ------------------------------------------

    [Fact]
    public void AcknowledgingStopsATugThatIsRinging()
    {
        var member = new object();
        ThreadTug.Observe(member, "generating");
        _now = 100;
        Assert.NotEqual(0, ThreadTug.Now(member).Glow);

        ThreadTug.Acknowledge(member);

        Assert.Equal((0, 0), ThreadTug.Now(member));
    }

    [Fact]
    public void AnAcknowledgedMemberStaysQuietWhileItStaysInTheSameState()
    {
        var member = new object();
        ThreadTug.Observe(member, "generating");
        ThreadTug.Acknowledge(member);

        // Well past the period: it would have tugged again.
        _now = (long)(ThreadTug.Period * 1000) * 3;
        ThreadTug.Observe(member, "generating");

        Assert.Equal((0, 0), ThreadTug.Now(member));
        Assert.False(ThreadTug.NeedsFrames());
    }

    [Fact]
    public void AnAcknowledgedMemberTugsAgainOnceItsStateChanges()
    {
        var member = new object();
        ThreadTug.Observe(member, "generating");
        ThreadTug.Acknowledge(member);

        _now = 1000;
        ThreadTug.Observe(member, "idle");
        Assert.False(ThreadTug.NeedsFrames());

        // It finishes and starts again: that is something new to tug about.
        _now = 2000;
        ThreadTug.Observe(member, "generating");

        Assert.True(ThreadTug.Now(member).Glow > 0);
        Assert.True(ThreadTug.NeedsFrames());
    }

    [Fact]
    public void AcknowledgingAMemberNothingHasObservedDoesNothing()
    {
        var member = new object();

        ThreadTug.Acknowledge(member);
        ThreadTug.Observe(member, "generating");
        _now = 50;

        // Not acknowledged retroactively: the first observation still tugs.
        Assert.True(ThreadTug.Now(member).Glow > 0);
    }

    [Fact]
    public void AcknowledgingOneMemberDoesNotQuietItsNeighbour()
    {
        var a = new object();
        var b = new object();
        ThreadTug.Observe(a, "generating");
        ThreadTug.Observe(b, "generating");

        ThreadTug.Acknowledge(a);
        _now = 50;

        Assert.Equal((0, 0), ThreadTug.Now(a));
        Assert.True(ThreadTug.Now(b).Glow > 0);
    }

    // ---- members that left ----------------------------------------------------

    [Fact]
    public void AMemberThatLeftNoLongerAsksForFrames()
    {
        var staying = new object();
        var gone = new object();
        ThreadTug.Observe(staying, "idle");
        ThreadTug.Observe(gone, "generating");
        Assert.True(ThreadTug.NeedsFrames());

        ThreadTug.Retain(new[] { staying });

        Assert.False(ThreadTug.NeedsFrames());
        Assert.Equal((0, 0), ThreadTug.Now(gone));
    }

    [Fact]
    public void RetainingEveryoneChangesNothing()
    {
        var member = new object();
        ThreadTug.Observe(member, "generating");

        ThreadTug.Retain(new[] { member });
        _now = 50;

        Assert.True(ThreadTug.Now(member).Glow > 0);
    }

    [Fact]
    public void ReduceMotionStopsEveryTug()
    {
        var member = new object();
        MotionPolicy.ReduceMotionProbe = () => true;

        ThreadTug.Observe(member, "generating");
        _now = 100;

        Assert.Equal((0, 0), ThreadTug.Now(member));
        Assert.False(ThreadTug.NeedsFrames());
    }
}

public class ThreadStyleNormalizeTests
{
    [Theory]
    [InlineData("silk", "silk")]
    [InlineData("arrows", "arrows")]
    [InlineData("ARROWS", "arrows")]
    [InlineData("Arrows", "arrows")]
    [InlineData("", "silk")]
    [InlineData(null, "silk")]
    [InlineData("a-style-from-a-later-build", "silk")]
    public void AnythingButArrowsIsSilk(string? saved, string expected)
    {
        Assert.Equal(expected, OrbweaverSettings.NormalizeThreadStyle(saved));
    }

    [Fact]
    public void TheDefaultsAreTheWebAndSilk()
    {
        Assert.Equal("web", OrbweaverSettings.DefaultArrangeShape);
        Assert.Equal("silk", OrbweaverSettings.DefaultThreadStyle);
    }
}
