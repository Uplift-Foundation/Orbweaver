using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// The far Buddy's slash resolver (CB-306), driven with the real routing and
// fakes only at the machine's edges: pane, registry, cloud login.
public class MirrorSlashHostTests
{
    private sealed class Edges
    {
        public bool CanType;
        public bool CanMessage;
        public string? Bridge;
        public bool TypeOk = true;
        public string? PostFailure;
        public List<string> Typed { get; } = new();
        public List<(string Subtype, JsonObject Request)> Posted { get; } = new();
        public string[] Catalogue = { "/compact", "/color", "/review" };

        public MirrorSlashHost Host() => new(new MirrorSlashHost.Seams(
            _ => CanType, _ => CanMessage, _ => Bridge,
            _ => Catalogue.Select(n => new SlashCommand(n, "")).ToList(),
            (_, text) => { Typed.Add(text); return Task.FromResult(TypeOk); },
            (_, subtype, request) => { Posted.Add((subtype, request)); return Task.FromResult(PostFailure); }));
    }

    private const string BridgeId = "session_01ABCDEFGHJKMNPQRSTVWXYZ12";

    private static SessionStatus Status(bool desktop = false) =>
        new() { Source = SessionSource.ClaudeCode, Entrypoint = desktop ? SessionStatus.DesktopEntrypoint : "cli" };

    [Fact]
    public void APaneMeansTheTerminalChannelAndEverythingIsTyped()
    {
        var host = new Edges { CanType = true }.Host();

        Assert.Equal(SlashChannel.Terminal, host.ChannelOf(Status()));
        Assert.All(host.CommandsFor(Status()), c => Assert.Equal(SlashRouteKind.Typed, c.Route));
    }

    [Fact]
    public void ABridgeWithNoPaneOffersTheControlCommandsAndHidesTheRestByRoute()
    {
        var host = new Edges { Bridge = BridgeId }.Host();

        var offered = host.CommandsFor(Status());

        Assert.Equal(SlashChannel.Bridge, host.ChannelOf(Status()));
        // The five control commands lead, then the rest of the catalogue —
        // listed as Unavailable rather than dropped, so a near panel still
        // knows /compact is a command and refuses it instead of sending prose.
        Assert.Equal(SlashRouteKind.Control, offered.Single(c => c.Name == "/color").Route);
        Assert.Equal(SlashRouteKind.Control, offered.Single(c => c.Name == "/model").Route);
        Assert.Equal(SlashRouteKind.Unavailable, offered.Single(c => c.Name == "/compact").Route);
        Assert.Equal(SlashRouteKind.Unavailable, offered.Single(c => c.Name == "/review").Route);
        Assert.Equal(offered.Count, offered.Select(c => c.Name).Distinct().Count());
    }

    [Fact]
    public void ASocketWithNoBridgeOffersEverythingAsUnavailable()
    {
        var host = new Edges { CanMessage = true }.Host();

        Assert.Equal(SlashChannel.PeerSocket, host.ChannelOf(Status()));
        Assert.All(host.CommandsFor(Status()), c => Assert.Equal(SlashRouteKind.Unavailable, c.Route));
    }

    [Fact]
    public void ADesktopChatNeverTypesWhateverItsStandInTerminalSays()
    {
        var host = new Edges { CanType = true, Bridge = BridgeId }.Host();

        Assert.Equal(SlashChannel.Bridge, host.ChannelOf(Status(desktop: true)));
    }

    [Fact]
    public async Task ATypedCommandIsTypedAndReportedAsSuch()
    {
        var edges = new Edges { CanType = true };

        var outcome = await edges.Host().RunAsync(Status(), "/compact now");

        Assert.Equal(MirrorProtocol.ViaTyped, outcome.Via);
        Assert.Equal("/compact now", Assert.Single(edges.Typed));
    }

    [Fact]
    public async Task ATerminalThatRefusesTheTextIsAnAnsweredRefusal()
    {
        var outcome = await new Edges { CanType = true, TypeOk = false }.Host().RunAsync(Status(), "/compact");

        Assert.Equal(MirrorProtocol.ViaRefused, outcome.Via);
        Assert.Equal("the terminal refused the text", outcome.Reason);
    }

    [Fact]
    public async Task AControlCommandIsPostedOverTheBridgeWithItsArgument()
    {
        var edges = new Edges { Bridge = BridgeId };

        var outcome = await edges.Host().RunAsync(Status(), "/color pink");

        Assert.Equal(MirrorProtocol.ViaControl, outcome.Via);
        var (subtype, request) = Assert.Single(edges.Posted);
        Assert.Equal("set_color", subtype);
        Assert.Equal("pink", (string?)request["color"]);
        Assert.Empty(edges.Typed);
    }

    [Fact]
    public async Task ACommandThatCannotRunOnTheBridgeIsRefusedWithTheReasonAndSendsNothing()
    {
        var edges = new Edges { Bridge = BridgeId };

        var outcome = await edges.Host().RunAsync(Status(), "/compact");

        Assert.Equal(MirrorProtocol.ViaRefused, outcome.Via);
        Assert.Equal(SlashRouting.PeerMessageReason, outcome.Reason);
        Assert.Empty(edges.Posted);
        Assert.Empty(edges.Typed);
    }

    [Fact]
    public async Task AControlCommandWithABadArgumentIsRefusedWithoutPosting()
    {
        var edges = new Edges { Bridge = BridgeId };

        var outcome = await edges.Host().RunAsync(Status(), "/color");

        Assert.Equal(MirrorProtocol.ViaRefused, outcome.Via);
        Assert.Contains("which colour", outcome.Reason);
        Assert.Empty(edges.Posted);
    }

    [Fact]
    public async Task AFailedPostIsRefusedWithTheFailureSentence()
    {
        var outcome = await new Edges { Bridge = BridgeId, PostFailure = "the login was refused" }
            .Host().RunAsync(Status(), "/model opus");

        Assert.Equal(MirrorProtocol.ViaRefused, outcome.Via);
        Assert.Equal("the login was refused", outcome.Reason);
    }

    [Fact]
    public async Task APeerSocketRunsNothing()
    {
        var edges = new Edges { CanMessage = true };

        var outcome = await edges.Host().RunAsync(Status(), "/color pink");

        Assert.Equal(MirrorProtocol.ViaRefused, outcome.Via);
        Assert.Equal(SlashRouting.PeerSocketReason, outcome.Reason);
        Assert.Empty(edges.Posted);
    }

    [Fact]
    public async Task ALineThatIsNotACommandIsRefusedRatherThanTyped()
    {
        var edges = new Edges { CanType = true };

        var outcome = await edges.Host().RunAsync(Status(), "hello");

        Assert.Equal(MirrorProtocol.ViaRefused, outcome.Via);
        Assert.Empty(edges.Typed);
    }

    [Fact]
    public void ABridgeListsEveryControlCommandEvenWhenTheCatalogueDoesNotHaveIt()
    {
        var edges = new Edges { Bridge = BridgeId, Catalogue = new[] { "/compact" } };

        var names = edges.Host().CommandsFor(Status()).ToDictionary(c => c.Name, c => c.Route);

        foreach (var control in new[] { "/color", "/rename", "/model", "/permissions", "/think" })
            Assert.Equal(SlashRouteKind.Control, names[control]);
        Assert.Equal(SlashRouteKind.Unavailable, names["/compact"]);
    }

    [Fact]
    public async Task AKnownCommandPastAHundredAndTwentyNamesIsStillKnownAndRefused()
    {
        var edges = new Edges
        {
            Bridge = BridgeId,
            Catalogue = Enumerable.Range(0, 300).Select(i => $"/cmd{i}").ToArray(),
        };
        var host = edges.Host();

        var all = host.CommandsFor(Status());
        var late = all.Single(c => c.Name == "/cmd250");
        Assert.Equal(SlashRouteKind.Unavailable, late.Route);
        Assert.Equal(SlashRouteKind.Control, all.Single(c => c.Name == "/think").Route);

        var outcome = await host.RunAsync(Status(), "/cmd250");
        Assert.Equal(MirrorProtocol.ViaRefused, outcome.Via);
        Assert.Empty(edges.Posted);
        Assert.Empty(edges.Typed);
    }

    // The far side trusts the near side's known-name gate, so what it does with
    // an unknown word is pinned per channel: typed on a pane (a Typed line is
    // the keyboard's to judge), refused with the channel's reason elsewhere.
    [Fact]
    public async Task AnUnknownWordIsTypedOnATerminalAndRefusedOnABridgeAndASocket()
    {
        var pane = new Edges { CanType = true };
        Assert.Equal(MirrorProtocol.ViaTyped, (await pane.Host().RunAsync(Status(), "/nonesuch x")).Via);
        Assert.Equal("/nonesuch x", Assert.Single(pane.Typed));

        var bridge = new Edges { Bridge = BridgeId };
        var onBridge = await bridge.Host().RunAsync(Status(), "/nonesuch x");
        Assert.Equal(MirrorProtocol.ViaRefused, onBridge.Via);
        Assert.Equal(SlashRouting.PeerMessageReason, onBridge.Reason);

        var socket = new Edges { CanMessage = true };
        var onSocket = await socket.Host().RunAsync(Status(), "/nonesuch x");
        Assert.Equal(MirrorProtocol.ViaRefused, onSocket.Via);
        Assert.Equal(SlashRouting.PeerSocketReason, onSocket.Reason);
    }

    [Fact]
    public async Task ThinkOnAJobWithNeitherPaneNorBridgeIsListedUnavailableAndRefused()
    {
        var edges = new Edges { CanMessage = true };
        var host = edges.Host();

        Assert.Equal(SlashRouteKind.Unavailable, host.CommandsFor(Status()).Single(c => c.Name == "/think").Route);

        var outcome = await host.RunAsync(Status(), "/think 8000");
        Assert.Equal(MirrorProtocol.ViaRefused, outcome.Via);
        Assert.Empty(edges.Posted);
    }

    // --- GatedPost (CB-306's product-readiness round) ----------------------

    [Fact]
    public async Task GatedPostRefusesWithTheToggleSentenceWhenCommandsAreOff()
    {
        var calls = 0;
        var post = MirrorSlashHost.GatedPost(() => false, _ => BridgeId,
            (_, _, _) => { calls++; return Task.FromResult<string?>(null); });

        Assert.Equal(LocalCliChatSession.BridgeCommandsOffHint,
            await post(Status(), "set_color", new JsonObject()));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task GatedPostRefusesASessionWithNoBridge()
    {
        var calls = 0;
        var post = MirrorSlashHost.GatedPost(() => true, _ => null,
            (_, _, _) => { calls++; return Task.FromResult<string?>(null); });

        Assert.Equal(MirrorSlashHost.NoBridgeReason, await post(Status(), "set_color", new JsonObject()));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task GatedPostPostsToTheBridgeAndPassesTheAnswerBack()
    {
        string? sentTo = null;
        var post = MirrorSlashHost.GatedPost(() => true, _ => BridgeId,
            (_, id, _) => { sentTo = id; return Task.FromResult<string?>("the far side said no"); });

        Assert.Equal("the far side said no", await post(Status(), "set_color", new JsonObject()));
        Assert.Equal(BridgeId, sentTo);
    }
}
