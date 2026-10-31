using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// The per-conversation message subscription (CB-270 Stage 3): held while a
// panel is open on the conversation, released when the last one closes, asked
// for again after a reconnect, and never held across the roster. Over an
// in-memory socket, on the harness OpenClawSendTests uses.
[Collection("Settings")]
public class OpenClawMessageSubscriptionTests : IDisposable
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

    private static OpenClawChatSession Chat(string key = "agent:alpha:dashboard:one") =>
        new("openclaw:" + key, key, "Alpha");

    // MessagesWanted does its work on a pool thread and nothing awaits it, so a
    // positive case polls for the frame it expects. Negative cases never poll:
    // they assert on code that never starts the task at all.
    private static async Task<List<FakeGatewaySocket.Request>> WaitForAsync(
        FakeGatewaySocket socket, int count)
    {
        for (var i = 0; i < 200 && Sent(socket).Count < count; i++) await Task.Delay(10);
        return Sent(socket);
    }

    private static string Key(FakeGatewaySocket.Request r) => r.Params.GetProperty("key").GetString()!;

    [Fact]
    public async Task TheFirstPanelOpenedSubscribesWithTheKeyAndNothingElse()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            Chat().PanelOpened();

            var sent = await WaitForAsync(socket, 1);
            var frame = Assert.Single(sent);
            Assert.Equal("sessions.messages.subscribe", frame.Method);
            Assert.Equal("agent:alpha:dashboard:one", Key(frame));
            Assert.Single(frame.Params.EnumerateObject());
        }
    }

    // The second panel on the same chat holds the same subscription.
    [Fact]
    public async Task ASecondPanelOnTheSameChatSendsNothing()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var chat = Chat();
            chat.PanelOpened();
            await WaitForAsync(socket, 1);

            chat.PanelOpened();
            await Task.Delay(100);

            Assert.Single(Sent(socket));
        }
    }

    [Fact]
    public async Task OnlyTheLastPanelClosedUnsubscribes()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var chat = Chat();
            chat.PanelOpened();
            chat.PanelOpened();
            await WaitForAsync(socket, 1);

            chat.PanelClosed();
            await Task.Delay(100);
            Assert.Single(Sent(socket));

            chat.PanelClosed();
            var sent = await WaitForAsync(socket, 2);
            Assert.Equal("sessions.messages.unsubscribe", sent[1].Method);
            Assert.Equal("agent:alpha:dashboard:one", Key(sent[1]));
        }
    }

    // A close with nothing open has nothing to release.
    [Fact]
    public async Task ACloseWithNoPanelOpenSendsNothing()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            Chat().PanelClosed();
            await Task.Delay(100);

            Assert.Empty(Sent(socket));
        }
    }

    [Fact]
    public void NoGatewayMeansNoRequestAndNoFailure()
    {
        OpenClawSessions.SetGatewayForTests(null);
        var chat = Chat();

        chat.PanelOpened();
        chat.PanelClosed();

        Assert.Empty(chat.History);
    }

    [Fact]
    public async Task ARefusedSubscribeReturnsTheGatewaysWords()
    {
        var (_, gateway) = await ConnectedAsync(refuse: "sessions.messages.subscribe");
        using (gateway)
        {
            var failure = await OpenClawSessions.MessagesRequestAsync(
                gateway, "agent:a:b", true, CancellationToken.None);

            Assert.Equal("the gateway said no", failure);
        }
    }

    [Fact]
    public async Task ARefusedUnsubscribeIsSilent()
    {
        var (_, gateway) = await ConnectedAsync(refuse: "sessions.messages.unsubscribe");
        using (gateway)
        {
            Assert.Null(await OpenClawSessions.MessagesRequestAsync(
                gateway, "agent:a:b", false, CancellationToken.None));
        }
    }

    [Fact]
    public void AFailedSubscribeIsNotedInTheTranscriptAndNowhereElse()
    {
        var chat = Chat();

        chat.NoteLiveUnavailable("the gateway said no");

        var turn = Assert.Single(chat.History);
        Assert.Equal(ChatRole.System, turn.Role);
        Assert.Equal("Live updates aren't available for this conversation: the gateway said no", turn.Text);
    }

    [Fact]
    public void OnlyChatsWithAPanelOpenAreWorthASubscription()
    {
        var open = Chat("agent:a:one");
        var closed = Chat("agent:a:two");
        var reopened = Chat("agent:a:three");
        OpenClawSessions.SetGatewayForTests(null);

        open.PanelOpened();
        reopened.PanelOpened();
        reopened.PanelClosed();

        Assert.Equal(new[] { "agent:a:one" },
            OpenClawSessions.MessageSubscriptionsFor(new[] { open, closed, reopened }).ToArray());
    }

    // The negative control: a reconnect with chats and no open panel subscribes
    // to nothing at all. A subscription across the roster is the failure this
    // exists to rule out.
    [Fact]
    public async Task AReconnectWithNoOpenPanelSendsNoSubscribe()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            await OpenClawSessions.ResubscribeOpenPanelsAsync(
                gateway, new[] { Chat("agent:a:one"), Chat("agent:a:two"), Chat("agent:a:three") },
                CancellationToken.None);

            Assert.Empty(Sent(socket));
        }
    }

    [Fact]
    public async Task AReconnectSubscribesEachOpenPanelOnce()
    {
        var (socket, gateway) = await ConnectedAsync();
        using (gateway)
        {
            var open = Chat("agent:a:one");
            OpenClawSessions.SetGatewayForTests(null);
            open.PanelOpened();
            OpenClawSessions.SetGatewayForTests(gateway);

            await OpenClawSessions.ResubscribeOpenPanelsAsync(
                gateway, new[] { open, Chat("agent:a:two") }, CancellationToken.None);

            var frame = Assert.Single(Sent(socket));
            Assert.Equal("sessions.messages.subscribe", frame.Method);
            Assert.Equal("agent:a:one", Key(frame));
        }
    }
}
