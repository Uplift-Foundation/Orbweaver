using System.Net.WebSockets;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-270 Stage 3, the whole seam from bytes to bubbles: the captured native runs
// pushed as event frames through a real OpenClawGateway, out of its
// EventReceived, into a chat session, and the subscribe and unsubscribe frames
// a panel puts on the wire.
//
// Here as well as in the unit suite because the two fail differently: the unit
// cases hand the session a parsed payload, and only this one finds out that a
// frame the gateway actually delivers is not shaped the way the session reads
// it. The runs are the real capture with its identifiers scrubbed.
[Collection("Settings")]
public class OpenClawLiveStreamPayloadTests : IDisposable
{
    public void Dispose() => OpenClawSessions.SetGatewayForTests(null);

    private static OpenClawGateway Gateway(WebSocket socket) =>
        new("gw.local", 4443, "gw-token",
            (_, _, _, _) => Task.FromResult(
                new OpenClawSocket.Connection(socket, Stream.Null, "fp-abc")),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

    private static FakeGatewaySocket Socket()
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
                : FakeGatewaySocket.Ok(request.Id, new { });

        return socket;
    }

    // The captured rows, each wrapped as the frame the gateway sends.
    private static IEnumerable<string> Frames() =>
        OpenClawNativeRunFixture.Events
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                var row = JsonDocument.Parse(line).RootElement;
                return JsonSerializer.Serialize(new
                {
                    type = "event",
                    @event = row.GetProperty("name").GetString(),
                    payload = row.GetProperty("payload")
                });
            });

    // Every frame in order, through the real gateway's reader, into a chat.
    [Fact]
    public async Task TheCapturedRunsWalkFromFramesIntoTheBubblesAHistoryReadAgreesWith()
    {
        OpenClawSessions.SetGatewayForTests(null);
        var socket = Socket();
        using var gateway = Gateway(socket);
        await gateway.ConnectAsync(null, CancellationToken.None);

        var chat = new OpenClawChatSession(
            "openclaw:agent:main:dashboard:0a1b2c3d-0000-4000-8000-000000000001",
            "agent:main:dashboard:0a1b2c3d-0000-4000-8000-000000000001", "main");
        var seen = 0;
        gateway.EventReceived += (name, payload) =>
        {
            // The two kinds of row the session reads. The doorbell rows
            // (chat final, session.message) refetch history, which this seam does
            // not drive, so only the stream rows are delivered here.
            if (name is "agent" or "session.tool") chat.OnAgentEvent(name, payload);
            Interlocked.Increment(ref seen);
        };

        var frames = Frames().ToList();
        foreach (var frame in frames) socket.PushJson(frame);

        for (var i = 0; i < 400 && Volatile.Read(ref seen) < frames.Count; i++) await Task.Delay(10);
        Assert.Equal(frames.Count, Volatile.Read(ref seen));

        var toolLine = ChatTranscript.ToolSummary("mcp__openclaw__exec",
            JsonDocument.Parse("""{"command":"ls /home/op/workspace | wc -l","title":"Count workspace files"}""").RootElement);

        // Run one: a step, its tool line, a step. Run two: its own bubble.
        Assert.Equal(4, chat.History.Count);
        Assert.StartsWith("Hey Boss! I'm Nova", chat.History[0].Text);
        Assert.Equal(toolLine, chat.History[1].Text);
        Assert.Equal("probe ok, 18 files", chat.History[2].Text);
        Assert.Equal("probe ok, deliver true", chat.History[3].Text);
        Assert.All(chat.History, t => Assert.True(t.IsComplete));
    }

    // Exactly the frames a panel puts on the wire: the method and the key, and
    // nothing else. Anything extra is a field the gateway would be handed that
    // nobody reviewed.
    [Fact]
    public async Task SubscribeAndUnsubscribeAreExactlyTheMethodAndTheKey()
    {
        var socket = Socket();
        using var gateway = Gateway(socket);
        await gateway.ConnectAsync(null, CancellationToken.None);

        await OpenClawSessions.MessagesRequestAsync(gateway, "agent:main:dashboard:x", true, CancellationToken.None);
        await OpenClawSessions.MessagesRequestAsync(gateway, "agent:main:dashboard:x", false, CancellationToken.None);

        var sent = socket.Requests.Where(r => r.Method != "connect").ToList();
        Assert.Equal(new[] { "sessions.messages.subscribe", "sessions.messages.unsubscribe" },
            sent.Select(r => r.Method).ToArray());

        foreach (var frame in sent)
        {
            var property = Assert.Single(frame.Params.EnumerateObject());
            Assert.Equal("key", property.Name);
            Assert.Equal("agent:main:dashboard:x", property.Value.GetString());
        }
    }
}
