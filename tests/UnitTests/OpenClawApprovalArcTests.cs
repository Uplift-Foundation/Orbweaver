using System.IO;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// The branch arcs QA measured open on the Stage 6 lines (coverage at 281beb0e):
// each is a real input a gateway or peer can send, so each gets a case rather than
// an exclusion. They are grouped by file, with the arc each one closes named.
[Collection("Settings")]
public class OpenClawApprovalArcTests : IDisposable
{
    private const string Key = "agent:alpha:dashboard:11111111-2222-3333-4444-555555555555";

    private static readonly DateTime Now = new(
        DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private readonly bool _approve = OrbweaverSettings.OpenClawApproveEnabled;
    private readonly bool _enabled = OrbweaverSettings.OpenClawEnabled;

    public OpenClawApprovalArcTests()
    {
        OrbweaverSettings.OpenClawApproveEnabled = true;
        OpenClawSessions.Restart();
        OpenClawSessions.ForgetApprovalsForTests();
    }

    public void Dispose()
    {
        OpenClawSessions.SetGatewayForTests(null);
        OpenClawSessions.Restart();
        OpenClawSessions.ForgetApprovalsForTests();
        OrbweaverSettings.OpenClawApproveEnabled = _approve;
        OrbweaverSettings.OpenClawEnabled = _enabled;
    }

    // --- OpenClawApprovals.cs ---

    // 263: `request` is present but is not an object. The session comes from
    // sourceSessionKey instead, and nothing is read out of the non-object.
    [Theory]
    [InlineData("\"request\":\"ls\"")]
    [InlineData("\"request\":5")]
    [InlineData("\"request\":[\"ls\"]")]
    [InlineData("\"request\":null")]
    [InlineData("\"unrelated\":1")]
    public void ARowWhoseRequestIsNotAnObjectIsReadFromItsSourceSessionKeyAndNeverThrows(string request)
    {
        var row = "{\"id\":\"a1\",\"approvalKind\":\"exec\"," + request + ",\"sourceSessionKey\":\"" + Key + "\"," +
                  "\"createdAtMs\":" + Ms(Now) + ",\"expiresAtMs\":" + Ms(Now.AddMinutes(30)) + "}";

        var withKey = OpenClawApprovals.ParseRecord(Json(row), "exec", new DateTimeOffset(Now));
        var withoutKey = OpenClawApprovals.ParseRecord(
            Json(row.Replace("\"sourceSessionKey\":\"" + Key + "\",", "")), "exec", new DateTimeOffset(Now));

        // Whatever it makes of the missing command, the session came from the source
        // key; with no source key either, it is not a row anything can hang on a session.
        if (withKey is not null) Assert.Equal(Key, withKey.SessionKey);
        Assert.Null(withoutKey);
    }

    // 499: `truncated` is read only when it is exactly the JSON true.
    [Theory]
    [InlineData(",\"truncated\":true", true)]
    [InlineData(",\"truncated\":false", false)]
    [InlineData(",\"truncated\":\"true\"", false)]
    [InlineData(",\"truncated\":1", false)]
    [InlineData("", false)]
    public void AReplayIsTruncatedOnlyWhenTheGatewaySaysTrue(string truncated, bool expected)
    {
        var replay = OpenClawApprovals.ParseReplay(Json(
            "{\"subscribed\":true,\"approvalReplay\":{\"sessionKey\":\"" + Key + "\",\"updatedAtMs\":" + Ms(Now) +
            ",\"approvals\":[]" + truncated + "}}"),
            Key, new DateTimeOffset(Now));

        Assert.Equal(expected, replay!.Truncated);
    }

    // 516: the resolve result's `approval` is present but not an object (or absent):
    // the answer still reads, with nothing taken from the snapshot.
    [Theory]
    [InlineData(",\"approval\":\"x\"")]
    [InlineData(",\"approval\":5")]
    [InlineData(",\"approval\":null")]
    [InlineData("")]
    public void AResolveResultWhoseApprovalIsNotAnObjectStillSaysWhetherItApplied(string approval)
    {
        var answer = OpenClawApprovals.ParseResolveResult(Json("{\"applied\":true" + approval + "}"));

        Assert.True(answer!.Applied);
        Assert.Null(answer.Status);
        Assert.Null(answer.Reason);
        Assert.Null(answer.Decision);
    }

    // --- OpenClawPeerIdentity.cs 56 ---

    // An agent id that is not a string reads as null and is refused, not thrown on.
    [Fact]
    public void ARequestWithANonStringAgentIdIsRefused()
    {
        var pin = new string('a', 64);

        Assert.Null(OpenClawPeerIdentity.RequestFrom(Json("{\"gatewayPin\":\"" + pin + "\",\"agentIds\":[5]}")));
        Assert.Null(OpenClawPeerIdentity.RequestFrom(Json("{\"gatewayPin\":\"" + pin + "\",\"agentIds\":[\"alpha\",null]}")));
        Assert.NotNull(OpenClawPeerIdentity.RequestFrom(Json("{\"gatewayPin\":\"" + pin + "\",\"agentIds\":[\"alpha\"]}")));
    }

    // --- OpenClawHistoryShape.cs 213 ---

    // A rollup's text is read whatever its content is, and a string content that
    // cannot be read is empty text, not a throw (a text STEP with one is not a step
    // at all, because HasText is false for it, so only a rollup reaches this arm).
    [Fact]
    public void ARollupWhoseStringContentCannotBeReadIsEmptyTextAndTheSpanStillResolves()
    {
        var page = Json("""
            [{"role":"user","content":"go"},
             {"role":"assistant","content":[{"type":"text","text":"step one"}]},
             {"role":"assistant","api":"cli","content":"\ud800"}]
            """);

        var drop = OpenClawHistoryShape.TextsToDrop(page);

        // Nothing throws, and an unreadable rollup covers nothing, so the step stays.
        Assert.DoesNotContain(1, drop);
    }

    // --- OpenClawGateway.cs 465 and 909 ---

    private static async Task<(FakeGatewaySocket Socket, OpenClawGateway Gateway)> ConnectedAsync(
        object scopes, Func<FakeGatewaySocket.Request, object?>? answer = null)
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce = "nonce-1" });

        socket.OnRequest = request =>
            request.Method == "connect"
                ? FakeGatewaySocket.Ok(request.Id, new
                {
                    protocol = 4,
                    server = new { version = "1.2.3" },
                    auth = new { scopes },
                    policy = new { tickIntervalMs = 15_000, maxPayload = 1_048_576 }
                })
                : answer?.Invoke(request) ?? FakeGatewaySocket.Ok(request.Id, new { });

        var gateway = new OpenClawGateway("gw.local", 4443, "gw-token",
            (_, _, _, _) => Task.FromResult(new OpenClawSocket.Connection(socket, Stream.Null, "fp-abc")),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        Assert.Equal(OpenClawGateway.Outcome.Connected, (await gateway.ConnectAsync(null, CancellationToken.None)).Outcome);
        return (socket, gateway);
    }

    // 465: a scope entry that is not a readable string reads as "" and is dropped.
    [Fact]
    public async Task AGrantedScopeThatIsNotAStringIsDroppedAndTheOthersKept()
    {
        var (_, gateway) = await ConnectedAsync(new object?[] { "operator.read", 5, null, "", "operator.approvals" });
        using (gateway)
        {
            Assert.Equal(new[] { "operator.read", "operator.approvals" }, gateway.GrantedScopes);
        }
    }

    // 909: an event whose name is not a string is delivered under "" (or, with nobody
    // listening, dropped). Frames are handled in order, so a request answered after
    // them proves the first one was already handled.
    [Fact]
    public async Task AnEventWithANonStringNameIsDeliveredAsAnEmptyNameAndNeverStopsTheLoop()
    {
        var (socket, gateway) = await ConnectedAsync(new[] { "operator.read" });
        using (gateway)
        {
            // No listener yet: the frame is dropped without a throw.
            socket.PushJson("{\"event\":5,\"payload\":{}}");
            await gateway.RequestAsync("ping", new Dictionary<string, object>(), CancellationToken.None);

            var names = new List<string>();
            gateway.EventReceived += (name, _) => names.Add(name);

            socket.PushJson("{\"event\":5,\"payload\":{}}");
            socket.PushJson("{\"event\":null}");
            socket.PushJson("{\"event\":\"real\",\"payload\":{}}");
            await gateway.RequestAsync("ping", new Dictionary<string, object>(), CancellationToken.None);

            Assert.Equal(new[] { "", "", "real" }, names);
        }
    }

    // --- OpenClawSessions.Approvals.cs 592 ---

    // Past MaxAnswered (1024) answered ids the set is cleared rather than growing for
    // ever. Observable: an id answered long ago can be requested again afterwards,
    // where before the clear it would be refused as already answered.
    [Fact]
    public async Task TheAnsweredSetIsClearedPastItsCapSoItNeverGrowsWithoutBound()
    {
        var (_, gateway) = await ConnectedAsync(
            new[] { "operator.read", "operator.approvals" },
            r => r.Method == "approval.resolve" ? FakeGatewaySocket.Ok(r.Id, Json("{\"applied\":true}")) : null);
        using (gateway)
        {
            OpenClawSessions.SetGatewayForTests(gateway);
            var chat = new OpenClawChatSession("openclaw:" + Key, Key, "Alpha");

            string Row(string id) =>
                "{\"id\":\"" + id + "\",\"approvalKind\":\"exec\",\"request\":{\"command\":\"ls\",\"sessionKey\":\"" + Key + "\"}," +
                "\"createdAtMs\":" + Ms(Now) + ",\"expiresAtMs\":" + Ms(Now.AddMinutes(30)) + "}";

            void Request(string id) => OpenClawSessions.OnEvent("exec.approval.requested", Json(Row(id)), Now);

            Request("first");
            Assert.Equal(OpenClawApprovalResult.Answered, (await OpenClawSessions.ResolveApprovalAsync(chat, "first", "deny")).Result);

            // Still remembered as answered: a replay of it is refused.
            Request("first");
            Assert.Empty(OpenClawSessions.PendingFor(Key));

            for (var i = 0; i < 1030; i++)
            {
                var id = "flood-" + i;
                Request(id);
                Assert.Equal(OpenClawApprovalResult.Answered, (await OpenClawSessions.ResolveApprovalAsync(chat, id, "deny")).Result);
            }

            // The set was cleared on the way past the cap, so the first id is not remembered.
            Request("first");
            Assert.Equal("first", string.Join(",", OpenClawSessions.PendingFor(Key).Select(a => a.Id)));
        }
    }
}
