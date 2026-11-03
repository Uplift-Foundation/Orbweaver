using System.IO;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-306, OpenClaw arm: the catalogue off commands.list, the permission filter,
// the refusal in front of a typed command, and the session's two interface
// methods — parse and decide as pure functions, the wiring over an in-memory
// socket through the real OpenClawGateway. Fixtures are the real payload shape
// (OpenClawSlashFixtures).
[Collection("Settings")]
public class OpenClawSlashCommandsTests : IDisposable
{
    private const string NativeKey = "agent:main:dashboard:abc";
    private static readonly string[] Write = { "operator.read", "operator.write" };
    private static readonly string[] Admin = { "operator.read", "operator.write", "operator.admin" };

    public void Dispose()
    {
        OpenClawSessions.SetGatewayForTests(null);
        OpenClawSessions.SetCommandsForTests(OpenClawSessions.CommandCatalogue.None);
        OrbweaverSettings.OpenClawReplyEnabled = true;
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static IReadOnlyList<OpenClawCommand> Real() =>
        OpenClawCommandCatalog.Parse(Json(OpenClawSlashFixtures.CommandsList));

    // --- parse ---

    [Fact]
    public void TheRealRowsParseWithTheirAliasesAndArgumentFlag()
    {
        var commands = Real();

        Assert.Equal(
            new[] { "/status", "/openclaw", "/whoami", "/reset", "/new", "/compact", "/think", "/model", "/add_model_provider" },
            commands.Select(c => c.Name));
        Assert.Equal(new[] { "/think", "/thinking", "/t" }, commands.Single(c => c.Name == "/think").Aliases);
        Assert.Equal(new[] { "/whoami", "/id" }, commands.Single(c => c.Name == "/whoami").Aliases);
        Assert.True(commands.Single(c => c.Name == "/model").AcceptsArgs);
        Assert.False(commands.Single(c => c.Name == "/whoami").AcceptsArgs);
        Assert.Equal("Reset the current session.", commands.Single(c => c.Name == "/reset").Description);
    }

    [Fact]
    public void ABareArrayParsesToo()
    {
        var commands = OpenClawCommandCatalog.Parse(Json("""[{"textAliases":["/a"],"description":"d","acceptsArgs":true}]"""));

        var a = Assert.Single(commands);
        Assert.Equal("/a", a.Name);
        Assert.True(a.AcceptsArgs);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"commands":"no"}""")]
    [InlineData("5")]
    public void ANonListParsesToNothing(string json) =>
        Assert.Empty(OpenClawCommandCatalog.Parse(Json(json)));

    [Fact]
    public void RowsThatCannotBeTypedAreSkipped()
    {
        var commands = OpenClawCommandCatalog.Parse(Json("""
            {"commands":[
              5,
              {"name":"no-aliases"},
              {"name":"aliases-not-a-list","textAliases":"/x"},
              {"name":"empty","textAliases":[]},
              {"name":"junk","textAliases":[3,"x","/","/ok"]},
              {"name":"fine","textAliases":["/fine"]}]}
            """));

        Assert.Equal(new[] { "/ok", "/fine" }, commands.Select(c => c.Name));
        Assert.Equal("", commands[0].Description);
        Assert.False(commands[0].AcceptsArgs);
    }

    // --- scope filter and refusal ---

    [Fact]
    public void ResetNeedsAdminAndNothingElseInTheCutDoes()
    {
        var needing = Real().Where(c => OpenClawCommandCatalog.ScopeFor(c) is not null).ToList();

        var reset = Assert.Single(needing);
        Assert.Equal("/reset", reset.Name);
        Assert.Equal("operator.admin", OpenClawCommandCatalog.ScopeFor(reset));
    }

    [Fact]
    public void WithoutAdminResetIsNotOffered()
    {
        var offered = OpenClawCommandCatalog.Offered(Real(), Write);

        Assert.DoesNotContain(offered, c => c.Name == "/reset");
        Assert.Equal(8, offered.Count);
        Assert.All(offered, c => Assert.Equal(SlashRouteKind.PassThrough, c.Route));
        Assert.Equal("Show current status.", offered.Single(c => c.Name == "/status").Description);
    }

    [Fact]
    public void WithAdminResetIsOffered() =>
        Assert.Contains(OpenClawCommandCatalog.Offered(Real(), Admin), c => c.Name == "/reset");

    [Fact]
    public void AListedCommandIsNotRefusedUnderAnyOfItsAliases()
    {
        Assert.Null(OpenClawCommandCatalog.RefusalFor("/status", Real(), Write));
        Assert.Null(OpenClawCommandCatalog.RefusalFor("/T", Real(), Write));
    }

    [Fact]
    public void AnUnlistedCommandIsRefusedWhenACatalogueIsKnown() =>
        Assert.Equal(OpenClawCommandCatalog.UnlistedReason,
            OpenClawCommandCatalog.RefusalFor("/nonesuch", Real(), Write));

    [Fact]
    public void ResetIsRefusedWithoutAdminWhetherListedOrNot()
    {
        var reason = OpenClawCommandCatalog.ScopeReason("operator.admin");

        Assert.Equal(reason, OpenClawCommandCatalog.RefusalFor("/reset", Real(), Write));
        Assert.Equal(reason, OpenClawCommandCatalog.RefusalFor("/reset", Array.Empty<OpenClawCommand>(), Write));
        Assert.Null(OpenClawCommandCatalog.RefusalFor("/reset", Real(), Admin));
    }

    [Fact]
    public void WithNoCatalogueAnythingElseGoesAsItAlwaysDid() =>
        Assert.Null(OpenClawCommandCatalog.RefusalFor("/whatever", Array.Empty<OpenClawCommand>(), Write));

    // --- the catalogue load ---

    private static async Task<(FakeGatewaySocket Socket, OpenClawGateway Gateway)> ConnectedAsync(
        string[] methods, string[] scopes, Func<FakeGatewaySocket.Request, object?>? answer = null)
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce = "nonce-1" });

        socket.OnRequest = request =>
            request.Method == "connect"
                ? FakeGatewaySocket.Ok(request.Id, new
                {
                    protocol = 4,
                    server = new { version = "2026.10.10" },
                    auth = new { scopes },
                    features = new { methods },
                    policy = new { tickIntervalMs = 15_000, maxPayload = 1_048_576L }
                })
                : answer?.Invoke(request) ?? FakeGatewaySocket.Ok(request.Id, new { });

        var gateway = new OpenClawGateway("gw.local", 4443, "gw-token",
            (_, _, _, _) => Task.FromResult(new OpenClawSocket.Connection(socket, Stream.Null, "fp-abc")),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        var result = await gateway.ConnectAsync(null, CancellationToken.None);
        Assert.Equal(OpenClawGateway.Outcome.Connected, result.Outcome);
        OpenClawSessions.SetGatewayForTests(gateway);
        return (socket, gateway);
    }

    private static List<FakeGatewaySocket.Request> Sent(FakeGatewaySocket socket) =>
        socket.Requests.Where(r => r.Method != "connect").ToList();

    private static object CommandsResult() => JsonSerializer.Deserialize<JsonElement>(OpenClawSlashFixtures.CommandsList);

    [Fact]
    public async Task TheCatalogueIsLoadedWithTheScopesTheConnectionWasGranted()
    {
        var (socket, gateway) = await ConnectedAsync(
            new[] { "chat.send", "commands.list" }, Write,
            r => r.Method == "commands.list" ? FakeGatewaySocket.Ok(r.Id, CommandsResult()) : null);
        using (gateway)
        {
            await OpenClawSessions.LoadCommandsAsync(gateway, CancellationToken.None);

            var request = Assert.Single(Sent(socket));
            Assert.Equal("commands.list", request.Method);
            Assert.Equal(JsonValueKind.Object, request.Params.ValueKind);
            Assert.Empty(request.Params.EnumerateObject());
            Assert.Equal(9, OpenClawSessions.Commands.Commands.Count);
            Assert.Equal(Write, OpenClawSessions.Commands.Scopes);
        }
    }

    [Fact]
    public async Task AGatewayThatDoesNotAdvertiseTheMethodIsNotAsked()
    {
        var (socket, gateway) = await ConnectedAsync(new[] { "chat.send" }, Write);
        using (gateway)
        {
            await OpenClawSessions.LoadCommandsAsync(gateway, CancellationToken.None);

            Assert.Empty(Sent(socket));
            Assert.Empty(OpenClawSessions.Commands.Commands);
        }
    }

    [Fact]
    public async Task AFailedLoadLeavesTheCatalogueAsItWas()
    {
        OpenClawSessions.SetCommandsForTests(new OpenClawSessions.CommandCatalogue(Real(), Write));
        var (_, gateway) = await ConnectedAsync(
            new[] { "commands.list" }, Write,
            r => r.Method == "commands.list" ? FakeGatewaySocket.Error(r.Id, "nope", "no", null) : null);
        using (gateway)
        {
            await OpenClawSessions.LoadCommandsAsync(gateway, CancellationToken.None);

            Assert.Equal(9, OpenClawSessions.Commands.Commands.Count);
        }
    }

    // --- the session ---

    private static OpenClawChatSession Chat() => new("openclaw:" + NativeKey, NativeKey, "Agent");

    [Fact]
    public void ASessionOffersTheScopeFilteredCatalogue()
    {
        OpenClawSessions.SetCommandsForTests(new OpenClawSessions.CommandCatalogue(Real(), Write));

        var offered = Chat().SlashCommands;

        Assert.Contains(offered, c => c.Name == "/status");
        Assert.DoesNotContain(offered, c => c.Name == "/reset");
    }

    [Fact]
    public void ASessionBeforeTheCatalogueArrivesOffersNothing() =>
        Assert.Empty(Chat().SlashCommands);

    [Fact]
    public async Task AnAllowedCommandIsSentAsChatSendVerbatim()
    {
        OpenClawSessions.SetCommandsForTests(new OpenClawSessions.CommandCatalogue(Real(), Write));
        var (socket, gateway) = await ConnectedAsync(new[] { "chat.send", "commands.list" }, Write);
        using (gateway)
        {
            var outcome = await Chat().RunSlashAsync("/model  claude-haiku-4-5 -s");

            Assert.Equal(ChatSendOutcome.Sent, outcome);
            var send = Assert.Single(Sent(socket));
            Assert.Equal("chat.send", send.Method);
            Assert.Equal("/model  claude-haiku-4-5 -s", send.Params.GetProperty("message").GetString());
            Assert.Equal(NativeKey, send.Params.GetProperty("sessionKey").GetString());
        }
    }

    [Fact]
    public async Task ResetWithoutAdminIsRefusedWithANoteAndNothingIsSent()
    {
        OpenClawSessions.SetCommandsForTests(new OpenClawSessions.CommandCatalogue(Real(), Write));
        var (socket, gateway) = await ConnectedAsync(new[] { "chat.send", "commands.list" }, Write);
        using (gateway)
        {
            var chat = Chat();

            var outcome = await chat.RunSlashAsync("/reset");

            Assert.Equal(ChatSendOutcome.Failed, outcome);
            Assert.Empty(Sent(socket));
            var note = Assert.Single(chat.History);
            Assert.Equal(ChatRole.System, note.Role);
            Assert.Equal(
                SlashRouting.RefusalNote("/reset", OpenClawCommandCatalog.ScopeReason("operator.admin")),
                note.Text);
        }
    }

    [Fact]
    public async Task AnUnlistedCommandIsRefusedAndNothingIsSent()
    {
        OpenClawSessions.SetCommandsForTests(new OpenClawSessions.CommandCatalogue(Real(), Admin));
        var (socket, gateway) = await ConnectedAsync(new[] { "chat.send" }, Admin);
        using (gateway)
        {
            var chat = Chat();

            Assert.Equal(ChatSendOutcome.Failed, await chat.RunSlashAsync("/nonesuch now"));

            Assert.Empty(Sent(socket));
            Assert.Contains(OpenClawCommandCatalog.UnlistedReason, Assert.Single(chat.History).Text);
        }
    }

    // A line that is not a command at all (the panel never sends one, but the
    // method is public) is named as typed rather than throwing.
    [Fact]
    public async Task ALineWithNoCommandInItIsRefusedByItsOwnText()
    {
        OpenClawSessions.SetCommandsForTests(new OpenClawSessions.CommandCatalogue(Real(), Write));
        var chat = Chat();

        Assert.Equal(ChatSendOutcome.Failed, await chat.RunSlashAsync("hello"));

        Assert.Contains("hello", Assert.Single(chat.History).Text);
    }
}
