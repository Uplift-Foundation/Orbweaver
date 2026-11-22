using Xunit;

namespace Orbweaver.Tests;

// CB-402: the drawn rows are out of step only when their keys differ from what
// the client knows; a fresher Seen alone is not a reason to rebuild them.
public class RemoteOutOfStepTests
{
    private static RemoteControlSessions.Remote Row(string route, DateTime seen) =>
        new("t", "mini", "idle", seen, ".claude", Route: route);

    [Fact]
    public void AReturnedSessionPutsTheRowsOutOfStep()
    {
        var now = DateTime.UtcNow;
        Assert.True(RemoteControlSessions.OutOfStep(
            new[] { Row("sid:other", now) },
            new[] { Row("sid:lead", now), Row("sid:other", now) }));
    }

    [Fact]
    public void AGoneSessionPutsTheRowsOutOfStep()
    {
        var now = DateTime.UtcNow;
        Assert.True(RemoteControlSessions.OutOfStep(
            new[] { Row("sid:lead", now), Row("sid:other", now) },
            new[] { Row("sid:other", now) }));
    }

    [Fact]
    public void TheSameKeysInAnotherOrderWithANewerSeenAreInStep()
    {
        var then = DateTime.UtcNow.AddMinutes(-9);
        Assert.False(RemoteControlSessions.OutOfStep(
            new[] { Row("sid:a", then), Row("sid:b", then) },
            new[] { Row("sid:b", DateTime.UtcNow), Row("sid:a", DateTime.UtcNow) }));
    }
}
