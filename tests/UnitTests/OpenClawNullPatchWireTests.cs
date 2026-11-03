using System.Net.WebSockets;
using Xunit;

namespace Orbweaver.Tests;

// CB-270 Stage 4: clearing a field back to its default sends a JSON null, and the
// real gateway connection has to write it rather than drop the key. Measured
// accepted by the live gateway on 2026.10.5 when sent raw; this is the half that
// is ours.
public class OpenClawNullPatchWireTests
{
    [Fact]
    public async Task ANullInAPatchReachesTheWireAsANull()
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

        using var gateway = new OpenClawGateway("gw.local", 4443, "gw-token",
            (_, _, _, _) => Task.FromResult(
                new OpenClawSocket.Connection((WebSocket)socket, Stream.Null, "fp-abc")),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        await gateway.ConnectAsync(null, CancellationToken.None);

        await gateway.RequestAsync(
            "sessions.patch", new Dictionary<string, object?>(OpenClawSessionPatch.ClearThinking("k", "sid").Params),
            CancellationToken.None);

        var sent = socket.Requests.Single(r => r.Method == "sessions.patch");
        var field = sent.Params.GetProperty("thinkingLevel");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, field.ValueKind);
        Assert.Equal("sid", sent.Params.GetProperty("expectedSessionId").GetString());
    }
}
