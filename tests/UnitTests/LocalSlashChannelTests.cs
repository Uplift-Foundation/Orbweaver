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

    private static readonly IReadOnlyList<SlashCommand> Local = new[]
    {
        new SlashCommand("/compact", "Compact"),
        new SlashCommand("/color", "Colour"),
        new SlashCommand("/deploy", "Custom"),
    };

    [Fact]
    public void TerminalOffersTheLocalCatalogueUntouched()
        => Assert.Same(Local, LocalSlashChannel.CommandsFor(SlashChannel.Terminal, () => Local));

    [Fact]
    public void TheBridgeCarriesTheFullCatalogueWithControlForTheFiveAndUnavailableForTheRest()
    {
        var commands = LocalSlashChannel.CommandsFor(SlashChannel.Bridge, () => Local);

        Assert.Equal(SlashRouteKind.Unavailable, commands.Single(c => c.Name == "/compact").Route);
        Assert.Equal(SlashRouteKind.Unavailable, commands.Single(c => c.Name == "/deploy").Route);
        // Listed in the catalogue: keeps its own description, gains the route.
        Assert.Equal("Colour", commands.Single(c => c.Name == "/color").Description);
        Assert.Equal(SlashRouteKind.Control, commands.Single(c => c.Name == "/color").Route);
        // Not listed in the catalogue: added, once each.
        Assert.Equal(new[] { "/color", "/model", "/permissions", "/rename", "/think" },
            commands.Where(c => c.Route == SlashRouteKind.Control).Select(c => c.Name).Order().ToArray());
        Assert.Equal(commands.Count, commands.Select(c => c.Name).Distinct().Count());
    }

    [Fact]
    public void EveryCommandOnAnEmptyCatalogueStillOffersTheFiveOnABridge()
        => Assert.Equal(5, LocalSlashChannel.CommandsFor(SlashChannel.Bridge,
            () => Array.Empty<SlashCommand>()).Count(c => c.Route == SlashRouteKind.Control));

    [Theory]
    [InlineData(SlashChannel.PeerSocket)]
    [InlineData(SlashChannel.Cloud)]
    [InlineData(SlashChannel.OpenClaw)]
    public void EveryOtherChannelKnowsTheCatalogueAndRunsNoneOfIt(SlashChannel channel)
    {
        var commands = LocalSlashChannel.CommandsFor(channel, () => Local);

        Assert.Equal(Local.Select(c => c.Name), commands.Select(c => c.Name));
        Assert.All(commands, c => Assert.Equal(SlashRouteKind.Unavailable, c.Route));
    }

    // The known-name gate is SlashRouting.NamesKnownCommand over this list: a
    // known-but-unavailable name is a command, a path or unknown word is not.
    [Theory]
    [InlineData("/compact now", true)]
    [InlineData("/DEPLOY", true)]
    [InlineData("/think 5", true)]
    [InlineData("/tmp/x.log boom", false)]
    [InlineData("/", false)]
    [InlineData("/nonsense", false)]
    public void TheBridgeListIsTheKnownSetForTheSharedGate(string line, bool expected)
        => Assert.Equal(expected, SlashRouting.NamesKnownCommand(line,
            LocalSlashChannel.CommandsFor(SlashChannel.Bridge, () => Local).Select(c => c.Name)));

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
