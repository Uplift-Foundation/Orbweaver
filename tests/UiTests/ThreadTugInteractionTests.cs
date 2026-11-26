using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// Attending to an agent stops its thread tugging at you.
//
// Driven through OnClicked and OpenChat directly, as OrbWindowClickResolutionTests
// does: the pointer handling above them reaches the terminal, and these two do
// not. SessionManager.Instance is null, so OpenChat finds no chat and returns —
// after acknowledging, which is the part under test.
[Collection("Settings")]
public class ThreadTugInteractionTests : IDisposable
{
    private long _now;

    public ThreadTugInteractionTests()
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

    private static OrbWindow NewOrb(string state)
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(new SessionStatus { Source = SessionSource.OpenClaw, State = state, Title = "Nova" });
        return orb;
    }

    [AvaloniaFact]
    public void AnOrbReportsTheStateItsSessionIsIn()
    {
        Assert.Equal("generating", NewOrb("generating").State);
        Assert.Equal("idle", new OrbWindow(Guid.NewGuid().ToString()).State);
    }

    [AvaloniaFact]
    public void AClickOnAnOrbSilencesItsTug()
    {
        var click = OrbweaverSettings.ClickAction;
        try
        {
            // Nothing to run: this is about the click being noticed, not acted on.
            OrbweaverSettings.ClickAction = "none";

            var orb = NewOrb("generating");
            ThreadTug.Observe(orb, orb.State);
            _now = 100;
            Assert.True(ThreadTug.Now(orb).Glow > 0);

            orb.OnClicked(1);

            Assert.Equal((0, 0), ThreadTug.Now(orb));

            // Still generating, and still quiet, a whole period on.
            _now = (long)(ThreadTug.Period * 1000) * 2;
            ThreadTug.Observe(orb, orb.State);
            Assert.Equal((0, 0), ThreadTug.Now(orb));
            Assert.False(ThreadTug.NeedsFrames());
        }
        finally
        {
            OrbweaverSettings.ClickAction = click;
        }
    }

    [AvaloniaFact]
    public void OpeningAnOrbsChatSilencesItsTug()
    {
        var orb = NewOrb("generating");
        ThreadTug.Observe(orb, orb.State);
        _now = 100;
        Assert.True(ThreadTug.Now(orb).Glow > 0);

        orb.OpenChat();

        Assert.Equal((0, 0), ThreadTug.Now(orb));
    }

    [AvaloniaFact]
    public void ClickingOneOrbLeavesAnotherTuggingAway()
    {
        var click = OrbweaverSettings.ClickAction;
        try
        {
            OrbweaverSettings.ClickAction = "none";

            var clicked = NewOrb("generating");
            var other = NewOrb("generating");
            ThreadTug.Observe(clicked, clicked.State);
            ThreadTug.Observe(other, other.State);
            _now = 100;

            clicked.OnClicked(1);

            Assert.Equal((0, 0), ThreadTug.Now(clicked));
            Assert.True(ThreadTug.Now(other).Glow > 0);
        }
        finally
        {
            OrbweaverSettings.ClickAction = click;
        }
    }
}
