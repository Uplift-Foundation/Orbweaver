using Xunit;

namespace Orbweaver.Tests;

// Which slash channel a local Claude Code session sits on, and what that channel
// offers (CB-306). Pure, so every arm gets a case.
public class LocalSlashChannelTests
{
    private const string Bridge = "session_01FixtureOnly";
    private const string Other = "session_01OtherFixture";

    [Fact]
    public void TheDesktopRecordsBridgeWinsOverTheRegistrys()
        => Assert.Equal(Bridge, LocalSlashChannel.BridgeIdFor(Bridge, Other));

    [Fact]
    public void TheRegistrysBridgeIsTheFallback()
        => Assert.Equal(Other, LocalSlashChannel.BridgeIdFor(null, Other));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("local_abc", "not-a-session")]
    [InlineData("session_", "session_has space")]
    public void NoWellFormedIdIsNoBridge(string? desktop, string? registry)
        => Assert.Null(LocalSlashChannel.BridgeIdFor(desktop, registry));

    [Fact]
    public void AMalformedDesktopIdFallsThroughToAWellFormedRegistryId()
        => Assert.Equal(Other, LocalSlashChannel.BridgeIdFor("junk", Other));

    [Theory]
    // A pane to type into, bridge or not: the keyboard wins.
    [InlineData(false, true, false, null, SlashChannel.Terminal)]
    [InlineData(false, true, true, Bridge, SlashChannel.Terminal)]
    // Neither pane nor socket nor bridge: today's answer, unchanged.
    [InlineData(false, false, false, null, SlashChannel.Terminal)]
    // A socket alone: messages only.
    [InlineData(false, false, true, null, SlashChannel.PeerSocket)]
    // A socket plus a bridge: the bridge carries commands.
    [InlineData(false, false, true, Bridge, SlashChannel.Bridge)]
    // A bridge with no socket is still a bridge.
    [InlineData(false, false, false, Bridge, SlashChannel.Bridge)]
    // Desktop never types, whatever canType says.
    [InlineData(true, true, false, Bridge, SlashChannel.Bridge)]
    [InlineData(true, true, false, null, SlashChannel.PeerSocket)]
    [InlineData(true, false, true, null, SlashChannel.PeerSocket)]
    public void TheChannelFollowsWhatTheSessionCanReach(
        bool desktop, bool canType, bool canMessage, string? bridge, SlashChannel expected)
        => Assert.Equal(expected, LocalSlashChannel.ChannelFor(desktop, canType, canMessage, bridge));

    [Fact]
    public void TerminalOffersTheLocalCatalogue()
    {
        var local = new[] { new SlashCommand("/compact", "Compact") };
        Assert.Same(local, LocalSlashChannel.CommandsFor(SlashChannel.Terminal, () => local));
    }

    [Fact]
    public void TheBridgeOffersTheFiveControlCommandsAndNeverReadsTheCatalogue()
    {
        var commands = LocalSlashChannel.CommandsFor(SlashChannel.Bridge,
            () => throw new Xunit.Sdk.XunitException("catalogue read for a bridge"));

        Assert.Equal(new[] { "/color", "/rename", "/model", "/permissions", "/think" },
            commands.Select(c => c.Name));
        Assert.All(commands, c =>
        {
            Assert.Equal(SlashRouteKind.Control, c.Route);
            // Each one must actually route to a control request on this channel.
            Assert.Equal(SlashRouteKind.Control, SlashRouting.Resolve(SlashChannel.Bridge, c.Name, "x").Kind);
        });
    }

    [Theory]
    [InlineData(SlashChannel.PeerSocket)]
    [InlineData(SlashChannel.Cloud)]
    [InlineData(SlashChannel.OpenClaw)]
    public void EveryOtherChannelOffersNothingHere(SlashChannel channel)
        => Assert.Empty(LocalSlashChannel.CommandsFor(channel,
            () => throw new Xunit.Sdk.XunitException("catalogue read")));

    [Fact]
    public void ARegistryRowsBridgeIsReadAndAMalformedOneIsNot()
    {
        string Row(string extra) =>
            "{\"pid\":4242,\"sessionId\":\"s1\",\"messagingSocketPath\":\"/tmp/a.sock\",\"peerProtocol\":1" + extra + "}";

        Assert.Equal(Bridge,
            SessionRegistry.Parse(Row($",\"bridgeSessionId\":\"{Bridge}\""), null)!.Value.BridgeSessionId);
        Assert.Null(SessionRegistry.Parse(Row(",\"bridgeSessionId\":\"nope\""), null)!.Value.BridgeSessionId);
        Assert.Null(SessionRegistry.Parse(Row(""), null)!.Value.BridgeSessionId);
    }
}
