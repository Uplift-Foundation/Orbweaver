using Avalonia;
using Xunit;

namespace Orbweaver.Tests;

// CB-286: the sequencing of the chat panel's reveal gate, with every effect a
// delegate so no window is involved.
public class ChatPanelRevealTests
{
    private sealed class Harness
    {
        public bool Settled;
        public int Hides, Reveals, DeadlinesDisposed;
        public readonly List<Action> Frames = new();
        public Action? Deadline;
        public readonly ChatPanelReveal Gate;

        public Harness()
        {
            Gate = new ChatPanelReveal(
                () => Settled, () => Hides++, () => Reveals++,
                cb => Frames.Add(cb),
                cb => { Deadline = cb; return new Disposer(() => DeadlinesDisposed++); });
        }

        public void Frame()
        {
            var next = Frames.ToList();
            Frames.Clear();
            foreach (var f in next) f();
        }

        private sealed class Disposer(Action a) : IDisposable { public void Dispose() => a(); }
    }

    [Fact]
    public void Begin_HidesAndWaitsForAFrame()
    {
        var h = new Harness();
        h.Gate.Begin();
        Assert.Equal(1, h.Hides);
        Assert.True(h.Gate.Pending);
        Assert.Empty(h.Frames);
        h.Gate.Start();
        Assert.Single(h.Frames);
        Assert.Equal(0, h.Reveals);
    }

    [Fact]
    public void Start_WithNothingPendingRequestsNoFrame()
    {
        var h = new Harness();
        h.Gate.Start();
        Assert.Empty(h.Frames);

        h.Gate.Begin();
        h.Gate.Cancel();
        h.Gate.Start();
        Assert.Empty(h.Frames);
    }

    [Fact]
    public void SettledStillNeedsMinimumFrames()
    {
        var h = new Harness { Settled = true };
        h.Gate.Begin();
        h.Gate.Start();
        h.Frame();
        Assert.Equal(0, h.Reveals);
        h.Frame();
        Assert.Equal(1, h.Reveals);
        Assert.False(h.Gate.Pending);
        Assert.Equal(1, h.DeadlinesDisposed);
    }

    [Fact]
    public void UnsettledKeepsAskingForFramesUntilItSettles()
    {
        var h = new Harness();
        h.Gate.Begin();
        h.Gate.Start();
        for (var i = 0; i < 5; i++) h.Frame();
        Assert.Equal(0, h.Reveals);
        h.Settled = true;
        h.Frame();
        Assert.Equal(1, h.Reveals);
        Assert.Empty(h.Frames);
    }

    [Fact]
    public void NeverSettling_FailsOpenAtMaxFrames()
    {
        var h = new Harness();
        h.Gate.Begin();
        h.Gate.Start();
        for (var i = 0; i < ChatPanelReveal.MaxFrames - 1; i++) h.Frame();
        Assert.Equal(0, h.Reveals);
        h.Frame();
        Assert.Equal(1, h.Reveals);
        Assert.Empty(h.Frames);
    }

    [Fact]
    public void Deadline_RevealsWhenStillPending()
    {
        var h = new Harness();
        h.Gate.Begin();
        h.Gate.Start();
        h.Deadline!();
        Assert.Equal(1, h.Reveals);
        h.Frame();
        Assert.Equal(1, h.Reveals);
    }

    [Fact]
    public void Deadline_AfterRevealOrCancelDoesNothing()
    {
        var h = new Harness { Settled = true };
        h.Gate.Begin();
        h.Gate.Start();
        var late = h.Deadline!;
        h.Frame(); h.Frame();
        late();
        Assert.Equal(1, h.Reveals);

        h.Gate.Begin();
        var cancelled = h.Deadline!;
        h.Gate.Cancel();
        cancelled();
        Assert.Equal(1, h.Reveals);
    }

    [Fact]
    public void Cancel_StopsPendingFramesWithoutRevealing()
    {
        var h = new Harness { Settled = true };
        h.Gate.Begin();
        h.Gate.Start();
        h.Gate.Cancel();
        Assert.False(h.Gate.Pending);
        h.Frame(); h.Frame(); h.Frame();
        Assert.Equal(0, h.Reveals);
    }

    [Fact]
    public void Begin_AgainSupersedesTheEarlierRound()
    {
        var h = new Harness { Settled = true };
        h.Gate.Begin();
        h.Gate.Start();
        var old = h.Deadline!;
        h.Frame();                // one frame into round 1
        h.Gate.Begin(); h.Gate.Start(); // round 2; round 1's callbacks are stale
        Assert.Equal(2, h.Hides);
        old();
        Assert.Equal(0, h.Reveals);
        h.Frame();                // round 2, frame 1
        Assert.Equal(0, h.Reveals);
        h.Frame();                // frame 2 -> reveal
        Assert.Equal(1, h.Reveals);
    }

    [Fact]
    public void Cancel_WithNothingPendingIsHarmless()
    {
        var h = new Harness();
        h.Gate.Cancel();
        Assert.False(h.Gate.Pending);
    }

    [Theory]
    [InlineData(300, 260, 300, 260, 1, 1, true)]
    [InlineData(300, 260, 300.4, 259.6, 1, 1, true)]
    [InlineData(300, 260, 600, 640, 1, 1, false)]
    [InlineData(300, 260, 300, 640, 1, 1, false)]
    [InlineData(300, 260, 300, 260, 1, 2, false)]
    [InlineData(300, 260, 300, 260, 2, 2, true)]
    [InlineData(300, 260, 300, 260, 1, double.NaN, true)]
    [InlineData(300, 260, 300, 260, 1, 0, true)]
    [InlineData(300, 260, 300, 260, 1, double.PositiveInfinity, true)]
    public void IsSettled(double tw, double th, double lw, double lh, double ws, double ss, bool expected) =>
        Assert.Equal(expected, ChatPanelReveal.IsSettled(new Size(tw, th), new Size(lw, lh), ws, ss));
}
