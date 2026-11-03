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

// What a reply typed into a WhatsApp conversation puts on the wire, over an
// in-memory socket (CB-270 S5).
//
// Nothing about delivery changes in S5, and these are what says so: they pin
// today's payloads for the four shapes a WhatsApp conversation takes on the
// 2026.10.5 gateway, under S2's send policy. They are written against the
// payloads the code builds, not against a live send: **nothing was ever sent
// into a real WhatsApp conversation to learn any of this**, and whether
// WhatsApp renders the mirror prefix's ** as bold or as literal asterisks is
// not measured. The prefix is not changed because Classify reads it back.
//
// The four shapes (every number and group id invented):
//   1. a WhatsApp DM, keyed by the peer's number, with a full address;
//   2. a WhatsApp group with one agent in it, whose address carries an account;
//   3. a WhatsApp group two agents are in, sent from its room orb;
//   4. an agent's main session a WhatsApp conversation is routed into, whose
//      deliveryContext has a channel and no `to` and no lastTo, so there is no
//      address to mirror to.
//
// The harness is cloned from OpenClawSendTests and OpenClawRoomSendTests.
[Collection("Settings")]
public class OpenClawWhatsAppSendTests : IDisposable
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

    // Params as an ordered list with the idempotency key checked for shape (a
    // bare "D"-format guid, never carrying the ":user" suffix the gateway stamps
    // itself) and then replaced by a token.
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

    // 1. A WhatsApp DM: the typed message is mirrored to the peer's number, as
    // the agent, with no account because a DM has none, and then the agent is
    // asked to answer with delivery on.
    [Fact]
    public async Task AWhatsAppDmMirrorsToTheNumberWithNoAccountThenSendsWithDelivery()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var chat = Chat("agent:alpha:whatsapp:direct:+15550000001",
                new OpenClawSessions.Delivery("whatsapp", "+15550000001", null));

            await OpenClawSessions.SendAsync(chat, "hi", CancellationToken.None);

            var sent = Sent(socket);
            Assert.Equal(new[] { "send", "chat.send" }, sent.Select(r => r.Method).ToArray());
            AssertParams(sent[0],
                ("to", "+15550000001"),
                ("message", "**(via Orbweaver)** hi"),
                ("channel", "whatsapp"),
                ("idempotencyKey", "<guid>"));
            AssertParams(sent[1],
                ("sessionKey", "agent:alpha:whatsapp:direct:+15550000001"),
                ("message", "hi"),
                ("deliver", "true"),
                ("idempotencyKey", "<guid>"));
        }
    }

    // 2. A WhatsApp group with one agent: the same two requests, and the
    // mirror carries the account the group row names.
    [Fact]
    public async Task AWhatsAppGroupMirrorsUnderItsAccountThenSendsWithDelivery()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var chat = Chat("agent:gamma:whatsapp:group:120000000000000002@g.us",
                new OpenClawSessions.Delivery("whatsapp", "120000000000000002@g.us", "default"));

            await OpenClawSessions.SendAsync(chat, "hi", CancellationToken.None);

            var sent = Sent(socket);
            Assert.Equal(new[] { "send", "chat.send" }, sent.Select(r => r.Method).ToArray());
            AssertParams(sent[0],
                ("to", "120000000000000002@g.us"),
                ("message", "**(via Orbweaver)** hi"),
                ("channel", "whatsapp"),
                ("idempotencyKey", "<guid>"),
                ("accountId", "default"));
            AssertParams(sent[1],
                ("sessionKey", "agent:gamma:whatsapp:group:120000000000000002@g.us"),
                ("message", "hi"),
                ("deliver", "true"),
                ("idempotencyKey", "<guid>"));
        }
    }

    // 3. A WhatsApp group two agents share, sent from the room orb: the post goes
    // to the group under the *carrier's own* account, and then the carrier is
    // handed the message with delivery on.
    private static OpenClawChatSession GroupCarrier()
    {
        var chat = new OpenClawChatSession(
            "openclaw:agent:alpha:whatsapp:group:120000000000000001@g.us",
            "agent:alpha:whatsapp:group:120000000000000001@g.us",
            "Alpha");
        chat.Delivery = new OpenClawSessions.Delivery("whatsapp", "120000000000000001@g.us", "default");
        return chat;
    }

    [Fact]
    public async Task ARoomSendToAWhatsAppGroupPostsUnderTheCarriersAccountThenHandsItOver()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var failure = await OpenClawSessions.SendToRoomAsync(
                GroupCarrier(), "#Studio Crew", "Alpha", "anyone about?", CancellationToken.None);

            Assert.Null(failure);

            var sent = Sent(socket);
            Assert.Equal(new[] { "send", "chat.send" }, sent.Select(r => r.Method).ToArray());
            AssertParams(sent[0],
                ("to", "120000000000000001@g.us"),
                ("message", "**(via Orbweaver)** anyone about?"),
                ("channel", "whatsapp"),
                ("idempotencyKey", "<guid>"),
                ("accountId", "default"));
            AssertParams(sent[1],
                ("sessionKey", "agent:alpha:whatsapp:group:120000000000000001@g.us"),
                ("message", "anyone about?"),
                ("deliver", "true"),
                ("idempotencyKey", "<guid>"));
        }
    }

    // A refused post stops the room send: handing the agent a message that never
    // reached the group would be a silent private delivery.
    [Fact]
    public async Task ARefusedPostToAWhatsAppGroupSendsNothingToTheAgent()
    {
        var (socket, gateway) = await ConnectedAsync(refuse: "send");
        using (gateway)
        {
            var failure = await OpenClawSessions.SendToRoomAsync(
                GroupCarrier(), "#Studio Crew", "Alpha", "anyone about?", CancellationToken.None);

            Assert.NotNull(failure);
            Assert.Contains("Nothing was sent", failure!);
            Assert.DoesNotContain(Sent(socket), r => r.Method == "chat.send");
        }
    }

    // 4. The main session a WhatsApp conversation is routed into, whose
    // deliveryContext names a channel and no address. Read off a row of the real
    // shape rather than hand-built, so the claim "no Delivery" is the parser's.
    // A provider is present, so S2 calls it a surface conversation and the send
    // is chat.send with delivery on and no mirror: what the gateway does with a
    // channel-only context cannot be measured without sending into a real
    // conversation, so it is recorded as unmeasured and not changed.
    [Fact]
    public async Task AWhatsAppRoutedMainWithAChannelOnlyContextSendsChatSendAloneWithDelivery()
    {
        const string key = "agent:alpha:main";
        var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        var row = $$"""
            {"sessions":[{"key":"{{key}}","classification":"main","kind":"direct","chatType":"direct","isMain":true,
              "origin":{"provider":"whatsapp"},"deliveryContext":{"channel":"whatsapp"},"lastChannel":"whatsapp",
              "lastActivityAt":{{new DateTimeOffset(now.AddSeconds(-5)).ToUnixTimeMilliseconds()}}}]}
            """;

        OrbweaverSettings.OpenClawNativeMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawActiveWithinMinutes = OrbweaverSettings.OpenClawActiveWithinAll;
        OpenClawSessions.Parse(JsonDocument.Parse(row).RootElement, now);

        // The row has a channel and no address, so the address is none, and the
        // provider keeps it off the native route.
        var rowElement = JsonDocument.Parse(row).RootElement.GetProperty("sessions")[0];
        Assert.Null(OpenClawSessions.DeliveryFor(rowElement));
        Assert.Equal(OpenClawNativeKind.None, OpenClawSessions.NativeOf(key));

        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var chat = Chat(key, OpenClawSessions.DeliveryFor(rowElement));

            await OpenClawSessions.SendAsync(chat, "hi", CancellationToken.None);

            var sent = Assert.Single(Sent(socket));
            Assert.Equal("chat.send", sent.Method);
            AssertParams(sent,
                ("sessionKey", key),
                ("message", "hi"),
                ("deliver", "true"),
                ("idempotencyKey", "<guid>"));
        }
    }
}
