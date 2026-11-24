using Xunit;

namespace Orbweaver.Tests;

// The slow-phase diagnostic: one line when a scan phase takes about a second or
// more, nothing when the pass is healthy. The decision is pure, so each rule is a
// case with no clock and no sleeping.
public class SlowPhaseTests
{
    private static readonly TimeSpan OneSecond = SlowPhase.DefaultThreshold;

    [Fact]
    public void TheThresholdIsAboutASecond() => Assert.Equal(TimeSpan.FromSeconds(1), OneSecond);

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(999)]
    public void AFastPhase_LogsNothing(int ms) =>
        Assert.Null(SlowPhase.Line("agent teams (pids)", TimeSpan.FromMilliseconds(ms), 8, OneSecond));

    [Theory]
    [InlineData(1000)]
    [InlineData(1500)]
    [InlineData(9600)]
    public void ASlowPhase_LogsOneLine_WithThePhase_TheMilliseconds_AndTheCount(int ms)
    {
        var line = SlowPhase.Line("agent teams (files)", TimeSpan.FromMilliseconds(ms), 3, OneSecond)!;
        Assert.Equal($"slow scan phase: agent teams (files) took {ms} ms (3 sessions)", line);
    }

    [Fact]
    public void OneSessionIsSingular_AndNoOtherDetailIsEverCarried()
    {
        var line = SlowPhase.Line("apply", TimeSpan.FromSeconds(2), 1, OneSecond)!;
        Assert.EndsWith("(1 session)", line);
        // Only the phase, the time and the count: nothing to carry a path or a title.
        Assert.Equal("slow scan phase: apply took 2000 ms (1 session)", line);
    }

    // ---- Measure, with a fake clock that the "work" itself advances ----

    private sealed class Clock
    {
        internal TimeSpan Now;
        internal Func<TimeSpan> Read => () => Now;
    }

    [Fact]
    public void AFastPhase_WritesNothing_ToTheSink()
    {
        var clock = new Clock(); var lines = new List<string>();
        var r = SlowPhase.Measure("p", 4, () => { clock.Now += TimeSpan.FromMilliseconds(20); return 7; }, clock.Read, OneSecond, lines.Add);
        Assert.Equal(7, r);
        Assert.Empty(lines);
    }

    [Fact]
    public void ASlowPhase_WritesExactlyOneLine_AndReturnsItsResult()
    {
        var clock = new Clock(); var lines = new List<string>();
        var r = SlowPhase.Measure("tmux pane owners", 2, () => { clock.Now += TimeSpan.FromSeconds(2.5); return "x"; }, clock.Read, OneSecond, lines.Add);
        Assert.Equal("x", r);
        Assert.Equal("slow scan phase: tmux pane owners took 2500 ms (2 sessions)", Assert.Single(lines));
    }

    [Fact]
    public void ASlowPhaseThatThrows_StillReportsItsTime_AndTheExceptionPropagates()
    {
        var clock = new Clock(); var lines = new List<string>();
        Assert.Throws<InvalidOperationException>(() => SlowPhase.Measure<int>("attach clients", 5,
            () => { clock.Now += TimeSpan.FromSeconds(3); throw new InvalidOperationException(); }, clock.Read, OneSecond, lines.Add));
        Assert.Single(lines);
        Assert.Contains("attach clients took 3000 ms", lines[0]);
    }

    [Fact]
    public void TheCountCanComeFromTheResult()
    {
        var clock = new Clock(); var lines = new List<string>();
        var r = SlowPhase.Measure("read status files", (List<int> l) => l.Count,
            () => { clock.Now += TimeSpan.FromSeconds(1); return new List<int> { 1, 2, 3, 4 }; }, clock.Read, OneSecond, lines.Add);
        Assert.Equal(4, r.Count);
        Assert.Equal("slow scan phase: read status files took 1000 ms (4 sessions)", Assert.Single(lines));
    }

    [Fact]
    public void TheDefaultsExist_AndTheStopwatchClockMovesForward()
    {
        var read = SlowPhase.StopwatchClock();
        var a = read(); var b = read();
        Assert.True(b >= a);
        // The default sink writes to stderr; only invoked for a slow phase, so a
        // line here would be the one a real slow pass writes.
        SlowPhase.DefaultSink("slow scan phase: test took 1 ms (0 sessions)");
    }
}
