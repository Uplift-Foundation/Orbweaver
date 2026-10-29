using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// What a reply typed into a chat panel puts on the wire, over an in-memory
// socket (CB-270).
//
// The first five cases pin today's payloads for a conversation that lives on a
// chat surface, and were committed green *before* the send policy existed.
// They are the control for the whole change: a Discord or WhatsApp
// conversation must send byte-identical requests before and after, and these
// are what says so. Do not edit them to make a later change pass.
//
// The harness is cloned from OpenClawRoomSendTests, which is where the
// reasoning for driving a gateway over FakeGatewaySocket lives.
[Collection("Settings")]
public class OpenClawSendTests : IDisposable
{
    public void Dispose() => OpenClawSessions.SetGatewayForTests(null);

    private static OpenClawGateway Gateway(WebSocket socket) =>
        new("gw.local", 4443, "gw-token",
            (_, _, _, _) => Task.FromResult(
                new OpenClawSocket.Connection(socket, Stream.Null, "fp-abc")),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

    private static FakeGatewaySocket Socket(string? refuse = null)
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce = "nonce-1" });

        socket.OnRequest = request =>
            request.Method == "connect"
                ? FakeGatewaySocket.Ok(request.Id, new
                {
                    protocol = 4,
                    server = new { version = "1.2.3" },
                    auth = new { scopes = new[] { "operator.read", "operator.write" } },
                    policy = new { tickIntervalMs = 15_000, maxPayload = 1_048_576 }
                })
                : request.Method == refuse
                    ? FakeGatewaySocket.Error(request.Id, "refused", "the gateway said no", null)
                    : FakeGatewaySocket.Ok(request.Id, new { });

        return socket;
    }

    private static async Task<(FakeGatewaySocket Socket, OpenClawGateway Gateway)> ConnectedAsync(
        string? refuse = null)
    {
        var socket = Socket(refuse);
        var gateway = Gateway(socket);

        var result = await gateway.ConnectAsync(null, CancellationToken.None);
        Assert.Equal(OpenClawGateway.Outcome.Connected, result.Outcome);

        OpenClawSessions.SetGatewayForTests(gateway);
        return (socket, gateway);
    }

    private static List<FakeGatewaySocket.Request> Sent(FakeGatewaySocket socket) =>
        socket.Requests.Where(r => r.Method != "connect").ToList();

    private static OpenClawChatSession Chat(string key, OpenClawSessions.Delivery? delivery)
    {
        var chat = new OpenClawChatSession("openclaw:" + key, key, "Agent");
        chat.Delivery = delivery;
        return chat;
    }

    // The request's params as an ordered list, with the idempotency key checked
    // for shape and then replaced by a fixed token. The key is a bare "D"-format
    // guid and never carries a suffix: the gateway stamps ":user" onto a message
    // it accepted from this client, and the client must not write that itself.
    private static List<KeyValuePair<string, string>> Normalised(FakeGatewaySocket.Request request)
    {
        var list = new List<KeyValuePair<string, string>>();

        foreach (var p in request.Params.EnumerateObject())
        {
            var value = p.Value.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => p.Value.GetString()!
            };

            if (p.Name == "idempotencyKey")
            {
                Assert.True(Guid.TryParseExact(value, "D", out _), value);
                Assert.False(value.EndsWith(":user", StringComparison.Ordinal));
                value = "<guid>";
            }

            list.Add(new(p.Name, value));
        }

        return list;
    }

    private static void AssertParams(
        FakeGatewaySocket.Request request, params (string Key, string Value)[] expected) =>
        Assert.Equal(
            expected.Select(e => new KeyValuePair<string, string>(e.Key, e.Value)).ToList(),
            Normalised(request));

    // (a) A Discord DM: the typed message is mirrored into the channel under
    // the session's own account, then the agent is asked to answer it, with
    // delivery on.
    [Fact]
    public async Task ADiscordDmMirrorsTheMessageAndThenSendsItWithDelivery()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var chat = Chat("agent:quill:discord:direct:2000000000000000001",
                new OpenClawSessions.Delivery("discord", "user:2000000000000000001", "quillbot"));

            await OpenClawSessions.SendAsync(chat, "hi", CancellationToken.None);

            var sent = Sent(socket);
            Assert.Equal(2, sent.Count);

            Assert.Equal("send", sent[0].Method);
            AssertParams(sent[0],
                ("to", "user:2000000000000000001"),
                ("message", "**(via Orbweaver)** hi"),
                ("channel", "discord"),
                ("idempotencyKey", "<guid>"),
                ("accountId", "quillbot"));

            Assert.Equal("chat.send", sent[1].Method);
            AssertParams(sent[1],
                ("sessionKey", "agent:quill:discord:direct:2000000000000000001"),
                ("message", "hi"),
                ("deliver", "true"),
                ("idempotencyKey", "<guid>"));
        }
    }

    // (b) A WhatsApp DM routed into main: same two requests, no accountId.
    [Fact]
    public async Task AWhatsAppDmSendsTheSameTwoRequestsWithoutAnAccount()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var chat = Chat("agent:main:main",
                new OpenClawSessions.Delivery("whatsapp", "+15550000001", null));

            await OpenClawSessions.SendAsync(chat, "hi", CancellationToken.None);

            var sent = Sent(socket);
            Assert.Equal(2, sent.Count);

            Assert.Equal("send", sent[0].Method);
            AssertParams(sent[0],
                ("to", "+15550000001"),
                ("message", "**(via Orbweaver)** hi"),
                ("channel", "whatsapp"),
                ("idempotencyKey", "<guid>"));

            Assert.Equal("chat.send", sent[1].Method);
            AssertParams(sent[1],
                ("sessionKey", "agent:main:main"),
                ("message", "hi"),
                ("deliver", "true"),
                ("idempotencyKey", "<guid>"));
        }
    }

    // (c) No delivery context: chat.send only, still with delivery on.
    [Fact]
    public async Task ASessionWithNoDeliveryContextSendsChatSendOnly()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var chat = Chat("agent:quill:discord:direct:2000000000000000001", null);

            await OpenClawSessions.SendAsync(chat, "hi", CancellationToken.None);

            var sent = Sent(socket);
            Assert.Single(sent);
            Assert.Equal("chat.send", sent[0].Method);
            AssertParams(sent[0],
                ("sessionKey", "agent:quill:discord:direct:2000000000000000001"),
                ("message", "hi"),
                ("deliver", "true"),
                ("idempotencyKey", "<guid>"));
        }
    }

    // (d) A refused mirror is swallowed and the message still goes through.
    [Fact]
    public async Task ARefusedMirrorStillSendsTheMessage()
    {
        var (socket, gateway) = await ConnectedAsync(refuse: "send");
        using (gateway)
        {
            var chat = Chat("agent:quill:discord:direct:2000000000000000001",
                new OpenClawSessions.Delivery("discord", "user:2000000000000000001", "quillbot"));

            await OpenClawSessions.SendAsync(chat, "hi", CancellationToken.None);

            var sent = Sent(socket);
            Assert.Equal(new[] { "send", "chat.send" }, sent.Select(r => r.Method).ToArray());
        }
    }

    // (e) No gateway: a loud failure, not a silent drop.
    [Fact]
    public async Task NoGatewayThrowsNotConnected()
    {
        OpenClawSessions.SetGatewayForTests(null);

        var ex = await Assert.ThrowsAsync<IOException>(() =>
            OpenClawSessions.SendAsync(Chat("agent:main:main", null), "hi", CancellationToken.None));

        Assert.Equal("not connected to the gateway", ex.Message);
    }

    // --- native sessions (CB-270) ---
    //
    // Rows reach the policy the way they do in production: through Parse, which
    // records each listed key's classification. Shapes follow the scrubbed rows
    // in OpenClawSessionsParseTests, values invented.

    private const string DashboardKey = "agent:alpha:dashboard:11111111-2222-3333-4444-555555555555";
    private const string DiscordKey = "agent:alpha:discord:direct:1900000000000000001";
    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    private static void List(string rows)
    {
        OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawActiveWithinMinutes = OrbweaverSettings.OpenClawActiveWithinAll;

        var at = new DateTimeOffset(Now.AddSeconds(-5)).ToUnixTimeMilliseconds();
        var json = "{\"sessions\":[" + rows.Replace("{AT}", at.ToString()) + "]}";
        OpenClawSessions.Parse(JsonDocument.Parse(json).RootElement, Now);
    }

    private const string DashboardRow =
        "{\"key\":\"" + DashboardKey + "\",\"classification\":\"dashboard\",\"lastActivityAt\":{AT}}";

    private const string DiscordRow =
        "{\"key\":\"" + DiscordKey + "\",\"classification\":\"direct\",\"chatType\":\"direct\"," +
        "\"origin\":{\"provider\":\"discord\"},\"deliveryContext\":{\"channel\":\"discord\",\"to\":\"user:1900000000000000001\"}," +
        "\"lastChannel\":\"discord\",\"lastActivityAt\":{AT}}";

    // The WhatsApp-main shape: classification main, but a provider, a delivery
    // context and a last channel behind it, so it is a surface conversation.
    private const string WhatsAppMainRow =
        "{\"key\":\"agent:beta:main\",\"classification\":\"main\",\"isMain\":true,\"chatType\":\"direct\"," +
        "\"origin\":{\"provider\":\"whatsapp\",\"to\":\"+15550000001\"}," +
        "\"deliveryContext\":{\"channel\":\"whatsapp\",\"to\":\"+15550000001\"}," +
        "\"lastChannel\":\"whatsapp\",\"lastActivityAt\":{AT}}";

    // (f) A dashboard session: chat.send only, deliver:false, never a mirror.
    [Fact]
    public async Task ADashboardSessionSendsChatSendWithoutDeliveryAndNoMirror()
    {
        List(DashboardRow);
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            await OpenClawSessions.SendAsync(Chat(DashboardKey, null), "hi", CancellationToken.None);

            var sent = Sent(socket);
            Assert.Single(sent);
            Assert.Equal("chat.send", sent[0].Method);
            AssertParams(sent[0],
                ("sessionKey", DashboardKey),
                ("message", "hi"),
                ("deliver", "false"),
                ("idempotencyKey", "<guid>"));
        }
    }

    // (g) Sticky: once a key has been listed as a surface conversation, a later
    // listing as native does not move it.
    [Fact]
    public async Task AKeyOnceListedAsASurfaceStaysOnTheSurfaceRoute()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var chat = Chat(DashboardKey, null);

            List(DiscordRow.Replace(DiscordKey, DashboardKey));
            await OpenClawSessions.SendAsync(chat, "one", CancellationToken.None);

            List(DashboardRow);
            await OpenClawSessions.SendAsync(chat, "two", CancellationToken.None);

            var chatSends = Sent(socket).Where(r => r.Method == "chat.send").ToList();
            Assert.Equal(2, chatSends.Count);
            Assert.All(chatSends, r => Assert.Equal("true", Normalised(r).Single(p => p.Key == "deliver").Value));
        }
    }

    // (h) A native key that nonetheless carries a delivery address gets the
    // surface payload, mirror included.
    [Fact]
    public async Task ANativeKeyHoldingAStaleDeliveryGetsTheSurfacePayload()
    {
        List(DashboardRow);
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var chat = Chat(DashboardKey, new OpenClawSessions.Delivery("discord", "user:1900000000000000001", null));

            await OpenClawSessions.SendAsync(chat, "hi", CancellationToken.None);

            Assert.Equal(new[] { "send", "chat.send" }, Sent(socket).Select(r => r.Method).ToArray());
            Assert.Equal("true", Normalised(Sent(socket)[1]).Single(p => p.Key == "deliver").Value);
        }
    }

    // (i) Accepted behaviour, pinned: a first send before any poll has listed
    // the key goes out on the surface route, which is today's payload.
    [Fact]
    public async Task AKeyNoPollHasListedYetGetsTheSurfacePayload()
    {
        List("");
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            await OpenClawSessions.SendAsync(Chat(DashboardKey, null), "hi", CancellationToken.None);

            var sent = Sent(socket);
            Assert.Single(sent);
            Assert.Equal("true", Normalised(sent[0]).Single(p => p.Key == "deliver").Value);
        }
    }

    // (j) The WhatsApp-main shape, listed with its delivery context, is not
    // native and gets the surface payload with the mirror.
    [Fact]
    public async Task AWhatsAppMainListedWithItsDeliveryContextGetsTheSurfacePayload()
    {
        List(WhatsAppMainRow);
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var chat = Chat("agent:beta:main", new OpenClawSessions.Delivery("whatsapp", "+15550000001", null));

            await OpenClawSessions.SendAsync(chat, "hi", CancellationToken.None);

            Assert.Equal(new[] { "send", "chat.send" }, Sent(socket).Select(r => r.Method).ToArray());
            Assert.Equal("true", Normalised(Sent(socket)[1]).Single(p => p.Key == "deliver").Value);
        }
    }
}

