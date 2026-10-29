using System.Net.WebSockets;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-270 Stage 4, the seam with a format somebody else defines: a models.list
// result in the gateway's own envelope, and a sessions.list page carrying run
// fields on a native row and on a Discord row, through the real parsers and a
// real connection. The unit suite checks each rule; this checks that the whole
// exchange lands where the gate expects it.
//
// Every value is invented on the measured structure (2026.10.5).
[Collection("Settings")]
public class OpenClawRunControlsPayloadTests : IDisposable
{
    public void Dispose()
    {
        OpenClawSessions.SetGatewayForTests(null);
        OpenClawSessions.SetSnapshotForTests(Array.Empty<OpenClawSessions.Session>());
        OpenClawSessions.SetModelsForTests(Array.Empty<OpenClawModel>());
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // models.list answers more than the list: the envelope carries outcomes and an
    // account selection the catalogue must ignore.
    private const string ModelsList = """
        {"models":[
          {"id":"claude-sonnet-4-6","name":"Claude Sonnet 4.6","provider":"anthropic","contextWindow":1000000,"reasoning":true,
           "tags":["default","configured"],"agentRuntime":{"id":"claude-cli","source":"model"},
           "thinkingLevels":[{"id":"off","label":"off"},{"id":"adaptive","label":"adaptive"},{"id":"ultra","label":"ultra"}],
           "thinkingDefault":"adaptive","available":true},
          {"id":"qwen/qwen3.6-plus","name":"Qwen: Qwen3.6 Plus","provider":"openrouter","reasoning":true,
           "thinkingLevels":[{"id":"off","label":"off"},{"id":"low","label":"on"},{"id":"ultra","label":"ultra"}],
           "thinkingDefault":"low","available":true},
          {"id":"gpt-5.6-sol","name":"GPT-5.6-Sol","provider":"openai","agentRuntime":{"id":"codex"},"available":false}
        ],
         "providerOutcomes":[{"provider":"anthropic","ok":true}],
         "accountSelection":{"mode":"auto"}}
        """;

    [Fact]
    public void AModelsListEnvelopeGivesTheCatalogueAndTheOfferedSubset()
    {
        var all = OpenClawModelCatalog.Parse(Json(ModelsList));
        var offered = OpenClawModelCatalog.Offered(all);

        Assert.Equal(3, all.Count);
        Assert.Equal(new[] { "claude-sonnet-4-6", "qwen/qwen3.6-plus" }, offered.Select(m => m.Id));
        Assert.Equal("on", offered[1].ThinkingLevels.Single(l => l.Id == "low").Label);
    }

    private static string Page() => """
        {"sessions":[
         {"key":"agent:main:dashboard:11111111-2222-3333-4444-555555555555","sessionId":"sid-native","classification":"dashboard",
          "lastActivityAt":NOW,"model":"claude-sonnet-4-6","modelProvider":"anthropic",
          "thinkingLevel":null,"thinkingDefault":"adaptive",
          "thinkingLevels":[{"id":"off","label":"off"},{"id":"adaptive","label":"adaptive"},{"id":"ultra","label":"ultra"}],
          "fastMode":null,"effectiveFastMode":false,"effectiveFastModeSource":"default"},
         {"key":"agent:quill:discord:direct:1900000000000000001","sessionId":"sid-discord","classification":"direct","chatType":"direct",
          "origin":{"provider":"discord"},"deliveryContext":{"channel":"discord","to":"user:1900000000000000001"},
          "lastChannel":"discord","lastActivityAt":NOW,"model":"claude-sonnet-4-6","modelProvider":"anthropic",
          "thinkingLevel":null,"thinkingDefault":"adaptive",
          "thinkingLevels":[{"id":"off","label":"off"},{"id":"adaptive","label":"adaptive"}],
          "fastMode":null,"effectiveFastMode":false}
        ]}
        """.Replace("NOW", new DateTimeOffset(new DateTime(2026, 10, 29, 11, 59, 55, DateTimeKind.Utc)).ToUnixTimeMilliseconds().ToString());

    // The negative control that proves the gate is the route and not the data: both
    // rows carry a complete run state, the connection is real and grants write,
    // and only the native conversation is offered controls.
    [Fact]
    public async Task OnlyTheNativeConversationIsOfferedControlsThoughBothHaveARunState()
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce = "nonce-1" });
        socket.OnRequest = r => r.Method == "connect"
            ? FakeGatewaySocket.Ok(r.Id, new
            {
                protocol = 4,
                server = new { version = "2026.10.5" },
                auth = new { scopes = new[] { "operator.read", "operator.write" } },
                features = new { methods = new[] { "sessions.patch", "chat.send", "models.list" } },
                policy = new { tickIntervalMs = 15_000, maxPayload = 1_048_576 }
            })
            : FakeGatewaySocket.Ok(r.Id, new { });

        using var gateway = new OpenClawGateway("gw.local", 4443, "gw-token",
            (_, _, _, _) => Task.FromResult(new OpenClawSocket.Connection((WebSocket)socket, Stream.Null, "fp-abc")),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        await gateway.ConnectAsync(null, CancellationToken.None);
        OpenClawSessions.SetGatewayForTests(gateway);

        OrbweaverSettings.OpenClawReplyEnabled = true;
        OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawActiveWithinMinutes = OrbweaverSettings.OpenClawActiveWithinAll;
        var (sessions, _) = OpenClawSessions.Parse(Json(Page()), new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc));
        OpenClawSessions.SetSnapshotForTests(sessions);
        OpenClawSessions.SetModelsForTests(OpenClawModelCatalog.Offered(OpenClawModelCatalog.Parse(Json(ModelsList))));

        var nativeKey = "agent:main:dashboard:11111111-2222-3333-4444-555555555555";
        var discordKey = "agent:quill:discord:direct:1900000000000000001";
        var native = new OpenClawChatSession("openclaw:" + nativeKey, nativeKey, "main");
        var discord = new OpenClawChatSession("openclaw:" + discordKey, discordKey, "quill");
        discord.Delivery = new OpenClawSessions.Delivery("discord", "user:1900000000000000001", null);

        Assert.NotNull(OpenClawSessions.RunStateOf(nativeKey));
        Assert.NotNull(OpenClawSessions.RunStateOf(discordKey));
        Assert.NotNull(native.RunOptions);
        Assert.Null(discord.RunOptions);
        Assert.Equal("Claude Sonnet 4.6 · adaptive", native.RunOptions!.ChipText);
    }
}
