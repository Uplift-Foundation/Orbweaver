using Xunit;

namespace Orbweaver.UnitTests;

// CB-402: the roster log says which sessions moved, in a form short enough to read.
public class RosterDeltaLogTests
{
    [Theory]
    [InlineData("sid:369c40f8e1aa22bb", "369c40f8")]
    [InlineData("rc:.claude:sid:20b839b3-aaaa", "20b839b3")]
    [InlineData("short", "short")]
    [InlineData("name with no sid", "name wit")]
    public void AKeyIsShortenedToItsFirstEightOfTheSid(string key, string expected) =>
        Assert.Equal(expected, MirrorLog.ShortKey(key));

    [Fact]
    public void ASteadyRosterAddsNothing() =>
        Assert.Equal("", MirrorLog.RosterDelta(new[] { "sid:aaaaaaaa11", "sid:bbbbbbbb22" },
                                               new[] { "SID:BBBBBBBB22", "sid:aaaaaaaa11" }));

    [Fact]
    public void ALeadAndMemberLeavingAreNamedAsDropped() =>
        Assert.Equal(" dropped=20b839b3,369c40f8",
            MirrorLog.RosterDelta(new[] { "sid:369c40f8x", "sid:20b839b3x", "sid:other000" },
                                  new[] { "sid:other000" }));

    [Fact]
    public void AReturnIsNamedAsAddedAndAnArrivalToo() =>
        Assert.Equal(" added=369c40f8,fresh000",
            MirrorLog.RosterDelta(new[] { "sid:other000" },
                                  new[] { "sid:other000", "sid:369c40f8x", "sid:fresh0000" }));

    [Fact]
    public void BothDirectionsAtOnce() =>
        Assert.Equal(" added=bbbbbbbb dropped=aaaaaaaa",
            MirrorLog.RosterDelta(new[] { "sid:aaaaaaaa" }, new[] { "sid:bbbbbbbb" }));

    [Fact]
    public void TheRepublishSignatureChangesWithTheKeysAndNotTheirOrder()
    {
        RemoteControlSessions.Remote R(string route) => new("t", "peer", "idle", DateTime.UtcNow, ".claude", Route: route);
        var a = RemoteControlSessions.KeySignature(new[] { R("sid:1"), R("sid:2") });
        Assert.Equal(a, RemoteControlSessions.KeySignature(new[] { R("sid:2"), R("sid:1") }));
        Assert.NotEqual(a, RemoteControlSessions.KeySignature(new[] { R("sid:1") }));
    }
}
