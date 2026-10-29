using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-270 Stage 4, the wiring: run state read off sessions.list, the model
// catalogue, the three patch requests, the gate on which conversations get run
// controls at all, and the time limit and pictures on a native send. All over an
// in-memory socket, through the real OpenClawGateway.
[Collection("Settings")]
public class OpenClawRunControlsTests : IDisposable
{
    private const string NativeKey = "agent:main:dashboard:abc";
    private const string DiscordKey = "agent:quill:discord:direct:1900000000000000001";

    public void Dispose()
    {
        OpenClawSessions.SetGatewayForTests(null);
        OpenClawSessions.SetSnapshotForTests(Array.Empty<OpenClawSessions.Session>());
        OpenClawSessions.SetModelsForTests(Array.Empty<OpenClawModel>());
        OrbweaverSettings.OpenClawReplyEnabled = true;
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    // One native row and one Discord DM, both carrying run fields: the gate is the
    // route and never whether the data exists.
    private static string Rows() => """
        {"sessions":[
         {"key":"agent:main:dashboard:abc","sessionId":"sid-n","classification":"dashboard","lastActivityAt":NOW,
          "model":"claude-opus-4-6","modelProvider":"anthropic","thinkingDefault":"adaptive",
          "thinkingLevels":[{"id":"off","label":"off"},{"id":"adaptive","label":"adaptive"},{"id":"high","label":"high"}],
          "effectiveFastMode":false},
         {"key":"agent:quill:discord:direct:1900000000000000001","sessionId":"sid-d","classification":"direct","chatType":"direct",
          "origin":{"provider":"discord"},"deliveryContext":{"channel":"discord","to":"user:1900000000000000001"},
          "lastChannel":"discord","lastActivityAt":NOW,
          "model":"claude-opus-4-6","modelProvider":"anthropic","thinkingDefault":"adaptive",
          "thinkingLevels":[{"id":"off","label":"off"},{"id":"adaptive","label":"adaptive"}],"effectiveFastMode":false},
         {"key":"agent:main:dashboard:nomodel","sessionId":"sid-x","classification":"dashboard","lastActivityAt":NOW}
        ]}
        """.Replace("NOW", new DateTimeOffset(Now.AddSeconds(-5)).ToUnixTimeMilliseconds().ToString());

    private static IReadOnlyList<OpenClawSessions.Session> ParseRows()
    {
        OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawActiveWithinMinutes = OrbweaverSettings.OpenClawActiveWithinAll;

        var (sessions, _) = OpenClawSessions.Parse(Json(Rows()), Now);
        OpenClawSessions.SetSnapshotForTests(sessions);
        return sessions;
    }

    private static IReadOnlyList<OpenClawModel> Catalogue() => OpenClawModelCatalog.Offered(
        OpenClawModelCatalog.Parse(Json("""
            [{"id":"claude-opus-4-6","name":"Claude Opus 4.6","provider":"anthropic","available":true,"thinkingDefault":"adaptive",
              "thinkingLevels":[{"id":"off","label":"off"},{"id":"adaptive","label":"adaptive"},{"id":"high","label":"high"}]},
             {"id":"claude-haiku-4-5","name":"Claude Haiku 4.5","provider":"anthropic","available":true,"thinkingDefault":"medium",
              "thinkingLevels":[{"id":"off","label":"off"},{"id":"medium","label":"medium"}]},
             {"id":"gpt-5.6-sol","name":"GPT","available":false}]
            """)));

    private static async Task<(FakeGatewaySocket Socket, OpenClawGateway Gateway)> ConnectedAsync(
        Func<FakeGatewaySocket.Request, object?>? answer = null,
        string[]? methods = null, string[]? scopes = null, long maxPayload = 1_048_576)
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce = "nonce-1" });

        socket.OnRequest = request =>
            request.Method == "connect"
                ? FakeGatewaySocket.Ok(request.Id, new
                {
                    protocol = 4,
                    server = new { version = "2026.10.5" },
                    auth = new { scopes = scopes ?? new[] { "operator.read", "operator.write" } },
                    features = new { methods = methods ?? new[] { "sessions.patch", "chat.send", "models.list" } },
                    policy = new { tickIntervalMs = 15_000, maxPayload }
                })
                : answer?.Invoke(request)
                  ?? (request.Method == "sessions.patch"
                      ? FakeGatewaySocket.Ok(request.Id, new { ok = true, key = NativeKey, entry = new { } })
                      : FakeGatewaySocket.Ok(request.Id, new { }));

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

    private static OpenClawChatSession Chat(string key) => new("openclaw:" + key, key, "Agent");

    // --- run state off the list ---

    [Fact]
    public void EveryListedRowWithAModelHasARunStateWhateverItsRoute()
    {
        ParseRows();

        Assert.Equal("claude-opus-4-6", OpenClawSessions.RunStateOf(NativeKey)!.Model);
        Assert.Equal("claude-opus-4-6", OpenClawSessions.RunStateOf(DiscordKey)!.Model);
        Assert.Null(OpenClawSessions.RunStateOf("agent:main:dashboard:nomodel"));
        Assert.Null(OpenClawSessions.RunStateOf("agent:unlisted"));
    }

    [Fact]
    public void AChatIsToldWhenItsRunStateChangedAndNotWhenItDidNot()
    {
        var a = new OpenClawRunState("m1", null, null, null, Array.Empty<OpenClawRunOption>(), null, false, false);
        var same = a with { };
        var moved = a with { Model = "m2" };

        var changed = Chat("agent:a:dashboard:one");
        var steady = Chat("agent:b:dashboard:two");
        var appeared = Chat("agent:c:dashboard:three");
        var vanished = Chat("agent:d:dashboard:four");
        var never = Chat("agent:e:dashboard:five");

        var before = new Dictionary<string, OpenClawRunState>
        {
            [changed.GatewayKey] = a, [steady.GatewayKey] = a, [vanished.GatewayKey] = a
        };
        var after = new Dictionary<string, OpenClawRunState>
        {
            [changed.GatewayKey] = moved, [steady.GatewayKey] = same, [appeared.GatewayKey] = a
        };

        var told = OpenClawSessions.ChatsWithChangedRunState(
            before, after, new[] { changed, steady, appeared, vanished, never });

        Assert.Equal(new[] { changed, appeared, vanished }, told);
    }

    // --- the model catalogue ---

    [Fact]
    public async Task ModelsAreLoadedAndOnlyTheAvailableOnesKept()
    {
        var (socket, gateway) = await ConnectedAsync(r => r.Method == "models.list"
            ? FakeGatewaySocket.Ok(r.Id, new
            {
                models = new object[]
                {
                    new { id = "claude-haiku-4-5", name = "Haiku", available = true },
                    new { id = "gpt-5.6-sol", name = "GPT", available = false }
                }
            })
            : null);
        using (gateway)
        {
            await OpenClawSessions.LoadModelsAsync(gateway, CancellationToken.None);

            Assert.Equal(new[] { "claude-haiku-4-5" }, OpenClawSessions.OfferedModels.Select(m => m.Id));
            Assert.Single(Sent(socket));
        }
    }

    // A failure leaves the catalogue as it was and says nothing: the controls are
    // an extra and a conversation is no worse off without them.
    [Fact]
    public async Task AFailedModelListLeavesTheCatalogueAlone()
    {
        OpenClawSessions.SetModelsForTests(Catalogue());
        var (_, gateway) = await ConnectedAsync(r => r.Method == "models.list"
            ? FakeGatewaySocket.Error(r.Id, "nope", "no", null)
            : null);
        using (gateway)
        {
            await OpenClawSessions.LoadModelsAsync(gateway, CancellationToken.None);

            Assert.Equal(2, OpenClawSessions.OfferedModels.Count);
        }
    }

    // --- the three patch requests ---

    private static async Task<(FakeGatewaySocket Socket, OpenClawGateway Gateway)> ConnectedWithRowsAsync(
        Func<FakeGatewaySocket.Request, object?>? answer = null)
    {
        var connected = await ConnectedAsync(answer);
        ParseRows();
        OpenClawSessions.SetModelsForTests(Catalogue());
        return connected;
    }

    private static List<KeyValuePair<string, JsonElement>> Fields(FakeGatewaySocket.Request r) =>
        r.Params.EnumerateObject().Select(p => new KeyValuePair<string, JsonElement>(p.Name, p.Value)).ToList();

    [Fact]
    public async Task AModelChangeSendsKeyModelAndExpectedSessionIdAndUpdatesTheState()
    {
        var (socket, gateway) = await ConnectedWithRowsAsync();
        using (gateway)
        {
            var result = await OpenClawSessions.SetModelAsync("openclaw:" + NativeKey, "claude-haiku-4-5", CancellationToken.None);

            Assert.Equal(OpenClawActionOutcome.Done, result.Outcome);
            var frame = Assert.Single(Sent(socket));
            Assert.Equal("sessions.patch", frame.Method);
            Assert.Equal(new[] { "key", "model", "expectedSessionId" }, Fields(frame).Select(f => f.Key));
            Assert.Equal("claude-haiku-4-5", frame.Params.GetProperty("model").GetString());
            Assert.Equal("sid-n", frame.Params.GetProperty("expectedSessionId").GetString());

            // The choices belong to the model, so they moved with it.
            var state = OpenClawSessions.RunStateOf(NativeKey)!;
            Assert.Equal("claude-haiku-4-5", state.Model);
            Assert.Equal("medium", state.ThinkingDefault);
            Assert.Equal(new[] { "off", "medium" }, state.ThinkingLevels.Select(o => o.Id));
        }
    }

    // A level the new model lists survives the change; one it does not is dropped,
    // and the poll corrects whichever way the gateway went (unmeasured).
    [Fact]
    public async Task AModelChangeKeepsAThinkingLevelTheNewModelListsAndDropsOneItDoesNot()
    {
        var (_, gateway) = await ConnectedWithRowsAsync();
        using (gateway)
        {
            await OpenClawSessions.SetThinkingAsync("openclaw:" + NativeKey, "off", CancellationToken.None);
            await OpenClawSessions.SetModelAsync("openclaw:" + NativeKey, "claude-haiku-4-5", CancellationToken.None);
            Assert.Equal("off", OpenClawSessions.RunStateOf(NativeKey)!.ThinkingLevel);

            await OpenClawSessions.SetThinkingAsync("openclaw:" + NativeKey, "medium", CancellationToken.None);
            await OpenClawSessions.SetModelAsync("openclaw:" + NativeKey, "claude-opus-4-6", CancellationToken.None);
            Assert.Null(OpenClawSessions.RunStateOf(NativeKey)!.ThinkingLevel);
        }
    }

    // A model the catalogue does not carry changes only the model.
    [Fact]
    public async Task AModelNotInTheCatalogueChangesOnlyTheModel()
    {
        var (_, gateway) = await ConnectedWithRowsAsync();
        using (gateway)
        {
            await OpenClawSessions.SetModelAsync("openclaw:" + NativeKey, "something-else", CancellationToken.None);

            var state = OpenClawSessions.RunStateOf(NativeKey)!;
            Assert.Equal("something-else", state.Model);
            Assert.Equal("adaptive", state.ThinkingDefault);
        }
    }

    [Fact]
    public async Task ClearingTheModelSendsANullAndLeavesTheStateForThePollToCorrect()
    {
        var (socket, gateway) = await ConnectedWithRowsAsync();
        using (gateway)
        {
            var result = await OpenClawSessions.SetModelAsync("openclaw:" + NativeKey, null, CancellationToken.None);

            Assert.Equal(OpenClawActionOutcome.Done, result.Outcome);
            var frame = Assert.Single(Sent(socket));
            Assert.Equal(JsonValueKind.Null, frame.Params.GetProperty("model").ValueKind);
            Assert.Equal("claude-opus-4-6", OpenClawSessions.RunStateOf(NativeKey)!.Model);
        }
    }

    [Fact]
    public async Task AThinkingChangeSendsTheIdAndClearingSendsNull()
    {
        var (socket, gateway) = await ConnectedWithRowsAsync();
        using (gateway)
        {
            await OpenClawSessions.SetThinkingAsync("openclaw:" + NativeKey, "high", CancellationToken.None);
            Assert.Equal("high", OpenClawSessions.RunStateOf(NativeKey)!.ThinkingLevel);

            await OpenClawSessions.SetThinkingAsync("openclaw:" + NativeKey, null, CancellationToken.None);
            Assert.Null(OpenClawSessions.RunStateOf(NativeKey)!.ThinkingLevel);

            var frames = Sent(socket);
            Assert.Equal("high", frames[0].Params.GetProperty("thinkingLevel").GetString());
            Assert.Equal(JsonValueKind.Null, frames[1].Params.GetProperty("thinkingLevel").ValueKind);
        }
    }

    [Fact]
    public async Task FastModeSendsABooleanAndClearingSendsNull()
    {
        var (socket, gateway) = await ConnectedWithRowsAsync();
        using (gateway)
        {
            await OpenClawSessions.SetFastModeAsync("openclaw:" + NativeKey, true, CancellationToken.None);
            var on = OpenClawSessions.RunStateOf(NativeKey)!;
            Assert.True(on.FastMode);
            Assert.True(on.EffectiveFastMode);

            await OpenClawSessions.SetFastModeAsync("openclaw:" + NativeKey, null, CancellationToken.None);
            var cleared = OpenClawSessions.RunStateOf(NativeKey)!;
            Assert.Null(cleared.FastMode);
            Assert.False(cleared.EffectiveFastMode);

            var frames = Sent(socket);
            Assert.Equal(JsonValueKind.True, frames[0].Params.GetProperty("fastMode").ValueKind);
            Assert.Equal(JsonValueKind.Null, frames[1].Params.GetProperty("fastMode").ValueKind);
        }
    }

    // The gateway's own words, and the state is left as it was.
    [Fact]
    public async Task ARefusalComesBackVerbatimAndChangesNothing()
    {
        var (_, gateway) = await ConnectedWithRowsAsync(r => r.Method == "sessions.patch"
            ? FakeGatewaySocket.Error(r.Id, "invalid", "model not allowed: anthropic/nope", null)
            : null);
        using (gateway)
        {
            var result = await OpenClawSessions.SetModelAsync("openclaw:" + NativeKey, "nope", CancellationToken.None);

            Assert.NotEqual(OpenClawActionOutcome.Done, result.Outcome);
            Assert.Contains("model not allowed: anthropic/nope", result.Detail);
            Assert.Equal("claude-opus-4-6", OpenClawSessions.RunStateOf(NativeKey)!.Model);
        }
    }

    [Fact]
    public async Task NothingIsSentWithoutAConnectionOrAListedSession()
    {
        OpenClawSessions.SetGatewayForTests(null);
        var offline = await OpenClawSessions.SetFastModeAsync("openclaw:" + NativeKey, true, CancellationToken.None);
        Assert.Equal(OpenClawActionOutcome.NotConnected, offline.Outcome);

        var notAnOrb = await ConnectedWithRowsAsync();
        using (notAnOrb.Gateway)
        {
            var wrongId = await OpenClawSessions.SetFastModeAsync("not-an-orb-id", true, CancellationToken.None);
            var unlisted = await OpenClawSessions.SetFastModeAsync("openclaw:agent:z:dashboard:none", true, CancellationToken.None);

            Assert.Equal(OpenClawActionOutcome.NotConnected, wrongId.Outcome);
            Assert.Equal(OpenClawActionOutcome.Refused, unlisted.Outcome);
            Assert.Empty(Sent(notAnOrb.Socket));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ABlankModelOrLevelIsRefusedWithNothingSent(string blank)
    {
        var (socket, gateway) = await ConnectedWithRowsAsync();
        using (gateway)
        {
            Assert.Equal(OpenClawActionOutcome.Refused,
                (await OpenClawSessions.SetModelAsync("openclaw:" + NativeKey, blank, CancellationToken.None)).Outcome);
            Assert.Equal(OpenClawActionOutcome.Refused,
                (await OpenClawSessions.SetThinkingAsync("openclaw:" + NativeKey, blank, CancellationToken.None)).Outcome);
            Assert.Empty(Sent(socket));
        }
    }

    // --- the gate: which conversations get run controls ---

    private static async Task<OpenClawChatSession> GatedNativeAsync(
        string[]? methods = null, string[]? scopes = null)
    {
        await ConnectedAsync(methods: methods, scopes: scopes);
        ParseRows();
        OpenClawSessions.SetModelsForTests(Catalogue());
        return Chat(NativeKey);
    }

    [Fact]
    public async Task ANativeConversationWithEverythingPresentGetsRunOptions()
    {
        var chat = await GatedNativeAsync();

        var options = chat.RunOptions!;

        Assert.Equal("claude-opus-4-6", options.ModelId);
        Assert.Equal(new[] { "claude-opus-4-6", "claude-haiku-4-5" }, options.Models.Select(m => m.Id));
        Assert.Equal("Claude Opus 4.6 · adaptive", options.ChipText);
        Assert.Null(options.ThinkingId);
        Assert.Equal("adaptive", options.ThinkingDefaultLabel);
        Assert.Equal(new[] { "off", "adaptive", "high" }, options.ThinkingChoices.Select(c => c.Id));
        Assert.False(options.Fast);
        Assert.False(options.FastIsSet);
        Assert.False(options.Locked);
        Assert.True(options.CanResetToDefault);
    }

    // The negative control: the same data on a Discord conversation, which is
    // exactly as complete, and the gate is still the route.
    [Fact]
    public async Task ASurfaceConversationGetsNoRunOptionsEvenThoughItsRunStateExists()
    {
        await GatedNativeAsync();

        var discord = Chat(DiscordKey);

        Assert.NotNull(OpenClawSessions.RunStateOf(DiscordKey));
        Assert.Null(discord.RunOptions);
        Assert.False(discord.CanSendImages);
    }

    [Fact]
    public async Task ReplyingOffMeansNoRunOptions()
    {
        var chat = await GatedNativeAsync();
        OrbweaverSettings.OpenClawReplyEnabled = false;

        Assert.Null(chat.RunOptions);
    }

    [Fact]
    public async Task WithoutTheWriteScopeOrThePatchMethodThereAreNoRunOptions()
    {
        var noWrite = await GatedNativeAsync(scopes: new[] { "operator.read" });
        Assert.Null(noWrite.RunOptions);
        OpenClawSessions.SetGatewayForTests(null);

        var noPatch = await GatedNativeAsync(methods: new[] { "chat.send" });
        Assert.Null(noPatch.RunOptions);
    }

    [Fact]
    public void NotConnectedMeansNoRunOptions()
    {
        OpenClawSessions.SetGatewayForTests(null);
        ParseRows();
        OpenClawSessions.SetModelsForTests(Catalogue());

        Assert.Null(Chat(NativeKey).RunOptions);
    }

    [Fact]
    public async Task NoRunStateOrNoCatalogueMeansNoRunOptions()
    {
        await ConnectedAsync();
        ParseRows();

        OpenClawSessions.SetModelsForTests(Array.Empty<OpenClawModel>());
        Assert.Null(Chat(NativeKey).RunOptions);

        OpenClawSessions.SetModelsForTests(Catalogue());
        Assert.Null(Chat("agent:main:dashboard:nomodel").RunOptions);
    }

    // The model in force may be one the gateway would not let a patch choose; it
    // is still what the chip names, so it is listed rather than left out.
    [Fact]
    public async Task AModelThatIsNotOfferedIsStillListedAndNamed()
    {
        await ConnectedAsync();
        ParseRows();
        OpenClawSessions.SetModelsForTests(OpenClawModelCatalog.Offered(OpenClawModelCatalog.Parse(Json(
            """[{"id":"claude-haiku-4-5","name":"Claude Haiku 4.5","available":true}]"""))));

        var options = Chat(NativeKey).RunOptions!;

        Assert.Equal("claude-opus-4-6", options.Models[0].Id);
        Assert.Equal("claude-opus-4-6 · adaptive", options.ChipText);
    }

    // No levels on the row: the catalogue's entry for the model stands in, and a
    // model with neither has no thinking choices at all.
    [Fact]
    public async Task ThinkingChoicesFallBackToTheCatalogueEntry()
    {
        await ConnectedAsync();
        var now = new DateTimeOffset(Now.AddSeconds(-5)).ToUnixTimeMilliseconds();
        OpenClawSessions.SetSnapshotForTests(OpenClawSessions.Parse(Json(
            "{\"sessions\":[{\"key\":\"" + NativeKey + "\",\"sessionId\":\"sid-n\",\"classification\":\"dashboard\",\"lastActivityAt\":" + now
            + ",\"model\":\"claude-haiku-4-5\",\"thinkingDefault\":\"medium\"}]}"), Now).Sessions);
        OpenClawSessions.SetModelsForTests(Catalogue());

        var withCatalogue = Chat(NativeKey).RunOptions!;
        Assert.Equal(new[] { "off", "medium" }, withCatalogue.ThinkingChoices.Select(c => c.Id));

        OpenClawSessions.SetSnapshotForTests(OpenClawSessions.Parse(Json(
            "{\"sessions\":[{\"key\":\"" + NativeKey + "\",\"sessionId\":\"sid-n\",\"classification\":\"dashboard\",\"lastActivityAt\":" + now
            + ",\"model\":\"unknown-model\"}]}"), Now).Sessions);

        var without = Chat(NativeKey).RunOptions!;
        Assert.Empty(without.ThinkingChoices);
        Assert.Null(without.ThinkingDefaultLabel);
    }

    [Fact]
    public async Task ALockedSelectionAndAChosenFastModeAreReported()
    {
        await ConnectedAsync();
        var now = new DateTimeOffset(Now.AddSeconds(-5)).ToUnixTimeMilliseconds();
        OpenClawSessions.SetSnapshotForTests(OpenClawSessions.Parse(Json(
            "{\"sessions\":[{\"key\":\"" + NativeKey + "\",\"sessionId\":\"sid-n\",\"classification\":\"dashboard\",\"lastActivityAt\":" + now
            + ",\"model\":\"claude-opus-4-6\",\"thinkingLevel\":\"high\",\"fastMode\":true,\"effectiveFastMode\":true,\"runtimeSelectionLocked\":true}]}"),
            Now).Sessions);
        OpenClawSessions.SetModelsForTests(Catalogue());

        var options = Chat(NativeKey).RunOptions!;

        Assert.True(options.Locked);
        Assert.True(options.FastIsSet);
        Assert.True(options.Fast);
        Assert.Equal("high", options.ThinkingId);
        Assert.Equal("Claude Opus 4.6 · high · fast", options.ChipText);
    }

    // The chat's three setters map the outcome onto the sentence the flyout shows:
    // nothing on success, the gateway's words otherwise.
    [Fact]
    public async Task TheChatsSettersReturnNullOnSuccessAndTheSentenceOtherwise()
    {
        var chat = await GatedNativeAsync();

        Assert.Null(await chat.SetModelAsync("claude-haiku-4-5"));
        Assert.Null(await chat.SetThinkingAsync("medium"));
        Assert.Null(await chat.SetFastModeAsync(true));

        OpenClawSessions.SetGatewayForTests(null);
        Assert.Equal("Couldn't change fast mode: not connected to the gateway", await chat.SetFastModeAsync(false));
        Assert.Equal("Couldn't change the model: not connected to the gateway", await chat.SetModelAsync("x"));
        Assert.Equal("Couldn't change thinking: not connected to the gateway", await chat.SetThinkingAsync("x"));
    }

    [Fact]
    public void AChatReportsItsOwnRunOptionsChanged()
    {
        var chat = Chat(NativeKey);
        var raised = 0;
        chat.RunOptionsChanged += () => raised++;

        chat.OnRunOptionsChanged();

        Assert.Equal(1, raised);
    }

    // --- time limit and pictures on a send ---

    private static async Task<(FakeGatewaySocket Socket, OpenClawChatSession Chat)> NativeSendAsync(
        long maxPayload = 1_048_576)
    {
        var (socket, _) = await ConnectedAsync(maxPayload: maxPayload);
        ParseRows();
        return (socket, Chat(NativeKey));
    }

    [Fact]
    public async Task ANativeSendCarriesTheTimeLimitAppendedLast()
    {
        var (socket, chat) = await NativeSendAsync();
        chat.TimeoutMinutes = 10;

        await OpenClawSessions.SendAsync(chat, "hi", CancellationToken.None);

        var frame = Assert.Single(Sent(socket));
        Assert.Equal(new[] { "sessionKey", "message", "deliver", "idempotencyKey", "timeoutMs" },
            Fields(frame).Select(f => f.Key));
        Assert.Equal(600_000, frame.Params.GetProperty("timeoutMs").GetInt32());
    }

    // The negative control: the same limit on a Discord conversation is never sent.
    [Fact]
    public async Task ASurfaceSendNeverCarriesATimeLimit()
    {
        var (socket, _) = await NativeSendAsync();
        var discord = Chat(DiscordKey);
        discord.Delivery = new OpenClawSessions.Delivery("discord", "user:1900000000000000001", null);
        discord.TimeoutMinutes = 10;

        await OpenClawSessions.SendAsync(discord, "hi", CancellationToken.None);

        var chatSend = Sent(socket).Single(r => r.Method == "chat.send");
        Assert.DoesNotContain("timeoutMs", Fields(chatSend).Select(f => f.Key));
    }

    [Fact]
    public async Task ANativeSendWithNoLimitCarriesNone()
    {
        var (socket, chat) = await NativeSendAsync();

        await OpenClawSessions.SendAsync(chat, "hi", CancellationToken.None);

        Assert.DoesNotContain("timeoutMs", Fields(Sent(socket).Single()).Select(f => f.Key));
    }

    private static OpenClawOutgoingImage Picture() => OpenClawOutgoingImage.From("p.png", new byte[] { 1, 2, 3 })!;

    [Fact]
    public async Task ANativeSendCarriesItsPicturesLastAfterTheLimit()
    {
        var (socket, chat) = await NativeSendAsync();
        chat.TimeoutMinutes = 2;

        await OpenClawSessions.SendAsync(chat, "look", CancellationToken.None, new[] { Picture() });

        var frame = Assert.Single(Sent(socket));
        Assert.Equal(
            new[] { "sessionKey", "message", "deliver", "idempotencyKey", "timeoutMs", "attachments" },
            Fields(frame).Select(f => f.Key));
        var wire = frame.Params.GetProperty("attachments")[0];
        Assert.Equal("image", wire.GetProperty("type").GetString());
        Assert.Equal("AQID", wire.GetProperty("content").GetString());
    }

    // Refused before anything goes: a picture that cannot go leaves no mirrored
    // message behind it.
    [Fact]
    public async Task APictureToASurfaceConversationIsRefusedWithNothingSent()
    {
        var (socket, _) = await NativeSendAsync();
        var discord = Chat(DiscordKey);
        discord.Delivery = new OpenClawSessions.Delivery("discord", "user:1900000000000000001", null);

        var ex = await Assert.ThrowsAsync<IOException>(() =>
            OpenClawSessions.SendAsync(discord, "look", CancellationToken.None, new[] { Picture() }));

        Assert.Contains("answered in place", ex.Message);
        Assert.Empty(Sent(socket));
    }

    [Fact]
    public async Task APictureTooLargeForTheConnectionIsRefusedWithNothingSent()
    {
        var (socket, chat) = await NativeSendAsync(maxPayload: 4200);
        var big = OpenClawOutgoingImage.From("big.png", new byte[3000])!;

        var ex = await Assert.ThrowsAsync<IOException>(() =>
            OpenClawSessions.SendAsync(chat, "look", CancellationToken.None, new[] { big }));

        Assert.Contains("too large", ex.Message);
        Assert.Empty(Sent(socket));
    }

    // --- sending pictures from files the panel saved ---

    [Fact]
    public async Task SendingFilesSendsTheImagesAndDrawsTheFirstOnTheMessage()
    {
        var (socket, chat) = await NativeSendAsync();
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var png = Path.Combine(dir, "shot.png");
            var txt = Path.Combine(dir, "notes.txt");
            await File.WriteAllBytesAsync(png, new byte[] { 1, 2, 3 });
            await File.WriteAllTextAsync(txt, "not a picture");

            var outcome = await chat.SendWithImagesAsync("look", new[] { png, txt, Path.Combine(dir, "missing.png") });

            Assert.Equal(ChatSendOutcome.Sent, outcome);
            var attachments = Assert.Single(Sent(socket)).Params.GetProperty("attachments");
            Assert.Equal(1, attachments.GetArrayLength());
            Assert.Equal("shot.png", attachments[0].GetProperty("fileName").GetString());

            var mine = chat.History[0];
            Assert.Equal("look", mine.Text);
            Assert.Equal(new byte[] { 1, 2, 3 }, mine.ImageBytes);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task OnlyANativeConversationWithReplyingOnCanSendPictures()
    {
        var (_, chat) = await NativeSendAsync();
        Assert.True(chat.CanSendImages);

        OrbweaverSettings.OpenClawReplyEnabled = false;
        Assert.False(chat.CanSendImages);
    }

    // --- history: a picture on a user turn is under __openclaw.media ---

    // The route the assistant-media fetch takes: the media:// string as the escaped
    // source, and this conversation's key. A bare media:// url as a request path
    // answers nothing (measured), which is why it is never handed over as one.
    private static string Route(string source) =>
        OpenClawSessions.AssistantMediaRoute + Uri.EscapeDataString(source)
        + "&sessionKey=" + Uri.EscapeDataString("agent:main:dashboard:abc");

    [Fact]
    public void AUserTurnsPictureIsReadFromItsMediaListBeforeItsText()
    {
        var messages = Json("""
            [{"role":"user","content":"what is this","timestamp":1790000000000,"idempotencyKey":"k1:user",
              "__openclaw":{"senderIsOwner":true,"media":[
                {"url":"media://inbound/abc.png","contentType":"image/png","kind":"image","fileName":"probe.png","sizeBytes":70},
                {"url":"media://inbound/def.ogg","contentType":"audio/ogg","kind":"audio","fileName":"v.ogg"},
                {"url":"","kind":"image","fileName":"empty.png"},
                {"url":"media://inbound/ghi.png","kind":"image"}]}}]
            """);

        var turns = OpenClawSessions.TurnsFromHistory(messages, "agent:main:dashboard:abc");

        Assert.Equal(3, turns.Count);
        Assert.Equal(Route("media://inbound/abc.png"), turns[0].ImageUrl);
        Assert.Equal("probe.png", turns[0].ImageAlt);
        Assert.Equal("", turns[0].Text);
        Assert.True(turns[0].Mine);
        Assert.Equal(Route("media://inbound/ghi.png"), turns[1].ImageUrl);
        Assert.Equal("", turns[1].ImageAlt);
        Assert.Equal("what is this", turns[2].Text);
    }

    // A message with a picture and no words still draws the picture.
    [Fact]
    public void APictureOnlyUserTurnStillDrawsThePicture()
    {
        var messages = Json("""
            [{"role":"user","content":"","__openclaw":{"media":[{"url":"media://inbound/abc.png","kind":"image","fileName":"p.png"}]}}]
            """);

        var turns = OpenClawSessions.TurnsFromHistory(messages, "agent:main:dashboard:abc");

        var picture = Assert.Single(turns);
        Assert.Equal(Route("media://inbound/abc.png"), picture.ImageUrl);
        Assert.False(picture.Mine);
    }

    // A turn the gateway labels as its own machinery is not a person's picture,
    // the same rule the text of such a turn is held to.
    [Fact]
    public void AGatewayInjectedTurnsPictureIsNotDrawnAsAPersons()
    {
        var messages = Json("""
            [{"role":"user","content":"Exec completed (id 1)","provenance":{"kind":"internal_system"},
              "__openclaw":{"media":[{"url":"media://inbound/x.png","kind":"image","fileName":"x.png"}]}}]
            """);

        var turns = OpenClawSessions.TurnsFromHistory(messages, "agent:main:dashboard:abc");

        Assert.DoesNotContain(turns, t => t.ImageUrl is not null);
    }

    // Not an owner's own message and not an array: nothing to read.
    [Theory]
    [InlineData("""{"role":"user","content":"hi","__openclaw":{"media":"nope"}}""")]
    [InlineData("""{"role":"user","content":"hi","__openclaw":{}}""")]
    [InlineData("""{"role":"assistant","content":"hi","__openclaw":{"media":[{"url":"/x","kind":"image"}]}}""")]
    public void NoMediaListMeansNoPictureTurn(string message)
    {
        var turns = OpenClawSessions.TurnsFromHistory(Json("[" + message + "]"), "agent:main:dashboard:abc");

        Assert.Single(turns);
        Assert.Null(turns[0].ImageUrl);
    }

    // --- the action wording for the three new controls ---

    [Fact]
    public void TheRunControlActionsHaveWording()
    {
        foreach (var action in new[] { OpenClawAction.Model, OpenClawAction.Thinking, OpenClawAction.Fast })
        {
            Assert.False(string.IsNullOrEmpty(OpenClawActionText.Header(action)));
            Assert.False(string.IsNullOrEmpty(OpenClawActionText.Tip(action)));
            Assert.Equal("Changing…", OpenClawActionText.Working(action));
            Assert.Equal("Changed", OpenClawActionText.For(OpenClawActionOutcome.Done, null, action));
        }

        Assert.Equal("Couldn't change the model: nope",
            OpenClawActionText.For(OpenClawActionOutcome.Refused, "nope", OpenClawAction.Model));
        Assert.Equal("Couldn't change thinking: nope",
            OpenClawActionText.For(OpenClawActionOutcome.Refused, "nope", OpenClawAction.Thinking));
        Assert.Equal("Couldn't change fast mode: nope",
            OpenClawActionText.For(OpenClawActionOutcome.Refused, "nope", OpenClawAction.Fast));
    }

    // --- a run that stopped at its time limit says so (P7) ---

    [Fact]
    public void TheRowsRunStatusIsKeptForEveryListedRow()
    {
        var now = new DateTimeOffset(Now.AddSeconds(-5)).ToUnixTimeMilliseconds();
        OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawActiveWithinMinutes = OrbweaverSettings.OpenClawActiveWithinAll;
        OpenClawSessions.Parse(Json(
            "{\"sessions\":[{\"key\":\"" + NativeKey + "\",\"classification\":\"dashboard\",\"lastActivityAt\":" + now + ",\"status\":\"timeout\"},"
            + "{\"key\":\"agent:main:dashboard:fine\",\"classification\":\"dashboard\",\"lastActivityAt\":" + now + ",\"status\":\"done\"},"
            + "{\"key\":\"agent:main:dashboard:blank\",\"classification\":\"dashboard\",\"lastActivityAt\":" + now + ",\"status\":\"\"}]}"), Now);

        Assert.Equal("timeout", OpenClawSessions.RunStatusOf(NativeKey));
        Assert.Equal("done", OpenClawSessions.RunStatusOf("agent:main:dashboard:fine"));
        Assert.Null(OpenClawSessions.RunStatusOf("agent:main:dashboard:blank"));
        Assert.Null(OpenClawSessions.RunStatusOf("agent:unlisted"));
    }

    private static void DrawARun(OpenClawChatSession chat) =>
        chat.OnAgentEvent("agent", Json("""{"runId":"r","stream":"assistant","data":{"text":"working on it"}}"""));

    [Fact]
    public async Task ARunThatTimedOutSaysSoAndNamesTheLimitOnce()
    {
        ParseRows();
        OrbweaverSettings.OpenClawReplyEnabled = false;
        var chat = Chat(NativeKey);
        chat.TimeoutMinutes = 10;
        await chat.SendAsync("go");
        DrawARun(chat);

        chat.OnRunStatus("timeout");
        chat.OnRunStatus("timeout");

        var notes = chat.History.Where(t => t.Text.StartsWith("The time limit")).ToList();
        var note = Assert.Single(notes);
        Assert.Equal("The time limit (10 minutes) was reached, so the run was stopped.", note.Text);
        Assert.Equal(ChatRole.System, note.Role);
    }

    // With no limit chosen the gateway's own was the one that applied.
    [Fact]
    public void ATimeoutWithNoLimitChosenSaysSoWithoutANumber()
    {
        var chat = Chat(NativeKey);
        DrawARun(chat);

        chat.OnRunStatus("timeout");

        Assert.Contains(chat.History, t => t.Text == "The run's time limit was reached, so the run was stopped.");
    }

    // A conversation opened on an old timeout does not announce it, and a run
    // that ended any other way says nothing.
    [Fact]
    public void NoNoteWithoutARunThisPanelSawOrForAnyOtherEnd()
    {
        var unseen = Chat(NativeKey);
        unseen.OnRunStatus("timeout");
        Assert.Empty(unseen.History);

        var done = Chat(NativeKey);
        DrawARun(done);
        done.OnRunStatus("done");
        done.OnRunStatus(null);
        Assert.DoesNotContain(done.History, t => t.Text.Contains("time limit"));
    }

    // QA's repro: a refused send leaves the typed message at the tail with its note,
    // and a delivery from another client then did not appear until reopen, because
    // the typed message, which no page has, became the anchor. It stays visible
    // once; only the search for where the page joins looks past it.
    [Fact]
    public async Task ADeliveryAppearsAfterARefusedSendLeftATypedMessageAtTheTail()
    {
        OrbweaverSettings.OpenClawReplyEnabled = false;
        var chat = Chat(NativeKey);
        chat.SetHistory(new[]
        {
            new HistoryTurn(ChatRole.User, "hello", null, "", DateTimeOffset.UtcNow, null, null),
            new HistoryTurn(ChatRole.Assistant, "hi there", null, "", DateTimeOffset.UtcNow, null, null),
        });
        await chat.SendAsync("typed while disconnected");

        chat.AppendNewTail(new[]
        {
            new HistoryTurn(ChatRole.User, "hello", null, "", DateTimeOffset.UtcNow, null, null),
            new HistoryTurn(ChatRole.Assistant, "hi there", null, "", DateTimeOffset.UtcNow, null, null),
            new HistoryTurn(ChatRole.Assistant, "a delivery from elsewhere", null, "", DateTimeOffset.UtcNow, null, null),
        });

        var texts = chat.History.Select(t => t.Text).ToList();
        Assert.Equal(1, texts.Count(t => t == "a delivery from elsewhere"));
        Assert.Equal(1, texts.Count(t => t == "typed while disconnected"));
    }

    // Once a page shows the typed message it is ordinary again: it is the anchor,
    // and what follows it on the page is appended.
    [Fact]
    public async Task ASentMessageThePageNowShowsAnchorsTheAppendAgain()
    {
        OrbweaverSettings.OpenClawReplyEnabled = false;
        var chat = Chat(NativeKey);
        chat.SetHistory(new[]
        {
            new HistoryTurn(ChatRole.User, "hello", null, "", DateTimeOffset.UtcNow, null, null),
        });
        await chat.SendAsync("typed");

        chat.AppendNewTail(new[]
        {
            new HistoryTurn(ChatRole.User, "hello", null, "", DateTimeOffset.UtcNow, null, null),
            new HistoryTurn(ChatRole.User, "typed", null, "", DateTimeOffset.UtcNow, null, null),
            new HistoryTurn(ChatRole.Assistant, "the reply", null, "", DateTimeOffset.UtcNow, null, null),
        });

        var texts = chat.History.Select(t => t.Text).ToList();
        Assert.Equal(1, texts.Count(t => t == "typed"));
        Assert.Contains("the reply", texts);
    }

    // A status that says the run is still going, or one nobody recognises, leaves
    // the run waiting; one that says it ended settles it, so a later timeout from
    // somewhere else is not announced with this send's limit.
    [Fact]
    public void OnlyAnEndedStatusSettlesTheRunAndAStillRunningOneDoesNot()
    {
        var waiting = Chat(NativeKey);
        DrawARun(waiting);
        waiting.OnRunStatus("running");
        waiting.OnRunStatus("something-new");
        waiting.OnRunStatus("timeout");
        Assert.Contains(waiting.History, t => t.Text.Contains("time limit"));

        var settled = Chat(NativeKey);
        DrawARun(settled);
        settled.OnRunStatus("done");
        settled.OnRunStatus("timeout");
        Assert.DoesNotContain(settled.History, t => t.Text.Contains("time limit"));
    }

    // A note at the tail is no anchor: it matches nothing on a page, and used to
    // stop every later delivery appearing. Here the note is the one a send leaves
    // when replying is off, straight after the message that was typed.
    [Fact]
    public async Task ADeliveryStillAppendsPastANoteAtTheTail()
    {
        OrbweaverSettings.OpenClawReplyEnabled = false;
        var chat = Chat(NativeKey);
        chat.SetHistory(new[]
        {
            new HistoryTurn(ChatRole.User, "go", null, "", DateTimeOffset.UtcNow, null, null),
            new HistoryTurn(ChatRole.Assistant, "done", null, "", DateTimeOffset.UtcNow, null, null),
        });
        await chat.SendAsync("one more");
        Assert.StartsWith("Replying is off", chat.History[^1].Text);

        chat.AppendNewTail(new[]
        {
            new HistoryTurn(ChatRole.User, "go", null, "", DateTimeOffset.UtcNow, null, null),
            new HistoryTurn(ChatRole.Assistant, "done", null, "", DateTimeOffset.UtcNow, null, null),
            new HistoryTurn(ChatRole.User, "one more", null, "", DateTimeOffset.UtcNow, null, null),
            new HistoryTurn(ChatRole.Assistant, "later delivery", null, "", DateTimeOffset.UtcNow, null, null),
        });

        Assert.Contains(chat.History, t => t.Text == "later delivery");
    }

    // The snapshot the poll takes before it parses: what the run states were.
    [Fact]
    public void TheRunStatesSnapshotIsACopyOfWhatParseKept()
    {
        ParseRows();

        var snapshot = OpenClawSessions.RunStatesSnapshot();

        Assert.Equal("claude-opus-4-6", snapshot[NativeKey].Model);
        Assert.Equal(2, snapshot.Count);
    }

    // Nobody listening is fine.
    [Fact]
    public void ARunOptionsChangeWithNoSubscriberIsHarmless() => Chat(NativeKey).OnRunOptionsChanged();

    // A default the model's own list does not carry is shown as its id.
    [Fact]
    public async Task ADefaultThinkingIdMissingFromTheLevelsIsShownAsItself()
    {
        await ConnectedAsync();
        var now = new DateTimeOffset(Now.AddSeconds(-5)).ToUnixTimeMilliseconds();
        OpenClawSessions.SetSnapshotForTests(OpenClawSessions.Parse(Json(
            "{\"sessions\":[{\"key\":\"" + NativeKey + "\",\"sessionId\":\"sid-n\",\"classification\":\"dashboard\",\"lastActivityAt\":" + now
            + ",\"model\":\"claude-opus-4-6\",\"thinkingDefault\":\"xhigh\",\"thinkingLevels\":[{\"id\":\"off\",\"label\":\"off\"}]}]}"),
            Now).Sessions);
        OpenClawSessions.SetModelsForTests(Catalogue());

        Assert.Equal("xhigh", Chat(NativeKey).RunOptions!.ThinkingDefaultLabel);
    }
}

