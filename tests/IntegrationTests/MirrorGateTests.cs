using Xunit;

namespace Orbweaver.Tests;

// CB-407: the gate that keeps MirrorEdgeCaseTests and MirrorRoundTripTests from running over each
// other. Each case uses its own instance, never the shared one, so it cannot block or be blocked by them.
public class MirrorGateTests
{
    [Fact]
    public void A_setup_that_succeeds_holds_the_gate_until_Leave()
    {
        var gate = new MirrorGate();
        gate.Hold(() => { });
        Assert.False(gate.IsFree);
        gate.Leave();
        Assert.True(gate.IsFree);
    }

    // xUnit does not call Dispose on an instance whose constructor threw, so a gate left held would
    // block every test after it for the rest of the run.
    [Fact]
    public void A_setup_that_throws_gives_the_gate_back_and_rethrows()
    {
        var gate = new MirrorGate();

        var thrown = Assert.Throws<InvalidOperationException>(() => gate.Hold(() => throw new InvalidOperationException("setup failed")));

        Assert.Equal("setup failed", thrown.Message);
        Assert.True(gate.IsFree);
    }
}
