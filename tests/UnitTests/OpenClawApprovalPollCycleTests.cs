using System.IO;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// Three behaviours of the approvals feed that the code gets right and nothing
// pinned (QA's M13, M17 and M21): the id bound as literal numbers, the purge of
// approvals for sessions the poll no longer lists, and the poll's isolation, driven
// through one real cycle of the private PollAsync loop.
[Collection("Settings")]
public class OpenClawApprovalPollCycleTests : IDisposable
{
    private const string Key = "agent:alpha:dashboard:11111111-2222-3333-4444-555555555555";
    private const string OtherKey = "agent:beta:dashboard:99999999-8888-7777-6666-555555555555";

    private static readonly DateTime Now = new(
        DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private readonly bool _approve = OrbweaverSettings.OpenClawApproveEnabled;
    private readonly bool _enabled = OrbweaverSettings.OpenClawEnabled;
    private readonly int _window = OrbweaverSettings.OpenClawActiveWithinMinutes;

    public OpenClawApprovalPollCycleTests()
    {
        OrbweaverSettings.OpenClawApproveEnabled = true;
        OrbweaverSettings.OpenClawActiveWithinMinutes = OrbweaverSettings.OpenClawActiveWithinAll;
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
        OrbweaverSettings.OpenClawActiveWithinMinutes = _window;
    }

    // --- M13: the id bound, as numbers rather than as the constant ---
    //
    // The other id tests build their ids from MaxIdLength, so lowering the constant
    // moves the test with it. 512 is the number the gateway's own schema allows.

    [Fact]
    public void AnIdOf512CharactersIsAcceptedAnd513IsRefused()
    {
        Assert.True(OpenClawApprovals.IsWellFormedId(new string('x', 512)));
        Assert.False(OpenClawApprovals.IsWellFormedId(new string('x', 513)));
        Assert.True(OpenClawApprovals.IsWellFormedId(new string('x', 200)));
        Assert.True(OpenClawApprovals.IsWellFormedId(new string('x', 300)));
    }

    [Fact]
    public void ARowWithA512CharacterIdIsKeptAndOneWith513IsDroppedWholeNotTruncated()
    {
        string RowWith(string id) =>
            "{\"id\":\"" + id + "\",\"approvalKind\":\"exec\",\"request\":{\"command\":\"ls\",\"sessionKey\":\"" + Key + "\"}," +
            "\"createdAtMs\":" + Ms(Now) + ",\"expiresAtMs\":" + Ms(Now.AddMinutes(30)) + "}";

        var kept = OpenClawApprovals.ParseRecord(Json(RowWith(new string('x', 512))), "exec", new DateTimeOffset(Now));
        var dropped = OpenClawApprovals.ParseRecord(Json(RowWith(new string('x', 513))), "exec", new DateTimeOffset(Now));

        Assert.Equal(512, kept!.Id.Length);
        Assert.Null(dropped);
    }

    // --- one real cycle of the poll ---

    private static async Task<(FakeGatewaySocket Socket, OpenClawGateway Gateway)> ConnectedAsync(
        Func<FakeGatewaySocket.Request, object?> answer)
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce = "nonce-1" });

        socket.OnRequest = request =>
            request.Method == "connect"
                ? FakeGatewaySocket.Ok(request.Id, new
                {
                    protocol = 4,
                    server = new { version = "1.2.3" },
                    auth = new { scopes = new[] { "operator.read", "operator.approvals" } },
                    policy = new { tickIntervalMs = 15_000, maxPayload = 1_048_576 }
                })
                : answer(request) ?? FakeGatewaySocket.Ok(request.Id, new { });

        var gateway = new OpenClawGateway("gw.local", 4443, "gw-token",
            (_, _, _, _) => Task.FromResult(new OpenClawSocket.Connection(socket, Stream.Null, "fp-abc")),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        Assert.Equal(OpenClawGateway.Outcome.Connected, (await gateway.ConnectAsync(null, CancellationToken.None)).Outcome);
        return (socket, gateway);
    }

    private static string ListRow(string key) =>
        "{\"key\":\"" + key + "\",\"chatType\":\"direct\",\"lastActivityAt\":" + Ms(Now) + "}";

    // Runs PollAsync until the snapshot it publishes AFTER the purge lists `expectKey`,
    // then stops it. The snapshot is assigned last in the cycle, so seeing it means the
    // approvals read, the parse and the purge have all run for this cycle.
    private static async Task RunOneCycleAsync(OpenClawGateway gateway, string expectKey)
    {
        OrbweaverSettings.OpenClawEnabled = true;
        var poll = typeof(OpenClawSessions).GetMethod("PollAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var cts = new CancellationTokenSource();
        var task = (Task)poll.Invoke(null, new object[] { gateway, cts.Token })!;

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!OpenClawSessions.Snapshot().Any(s => s.Key == expectKey))
        {
            Assert.True(DateTime.UtcNow < deadline, "the poll never published a snapshot");
            Assert.False(task.IsFaulted, "the poll faulted: " + task.Exception);
            await Task.Delay(10);
        }

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private static string SessionApproval(string id, string key) =>
        "{\"sessionKey\":\"" + key + "\",\"updatedAtMs\":" + Ms(Now) + ",\"phase\":\"pending\",\"approval\":" +
        "{\"id\":\"" + id + "\",\"status\":\"pending\",\"createdAtMs\":" + Ms(Now) + ",\"expiresAtMs\":" + Ms(Now.AddMinutes(30)) +
        ",\"presentation\":{\"kind\":\"exec\",\"commandText\":\"ls\",\"allowedDecisions\":[\"allow-once\",\"deny\"]}}}";

    // --- M17: the purge ---

    // A session-sourced record survives the lists (they cannot show it), so only the
    // purge can remove it when the poll stops listing its session. A listed session's
    // record, and the endings of an unlisted one, are the controls on each side.
    [Fact]
    public async Task ApprovalsAndEndingsForASessionThePollNoLongerListsAreDropped()
    {
        OpenClawSessions.OnEvent("session.approval", Json(SessionApproval("gone-1", Key)), Now);
        OpenClawSessions.OnEvent("session.approval", Json(SessionApproval("kept-1", OtherKey)), Now);
        OpenClawSessions.OnEvent("session.approval", Json(SessionApproval("ended-1", Key)), Now);
        OpenClawSessions.OnEvent("session.approval", Json(
            "{\"sessionKey\":\"" + Key + "\",\"updatedAtMs\":" + Ms(Now) + ",\"phase\":\"terminal\",\"approval\":" +
            "{\"id\":\"ended-1\",\"status\":\"denied\"}}"), Now);

        Assert.Equal("gone-1", string.Join(",", OpenClawSessions.PendingFor(Key).Select(a => a.Id)));

        var (_, gateway) = await ConnectedAsync(r => r.Method == "sessions.list"
            ? FakeGatewaySocket.Ok(r.Id, Json("{\"sessions\":[" + ListRow(OtherKey) + "]}"))
            : FakeGatewaySocket.Ok(r.Id, Json("[]")));
        using (gateway)
        {
            await RunOneCycleAsync(gateway, OtherKey);

            Assert.Empty(OpenClawSessions.PendingFor(Key));
            Assert.Empty(OpenClawSessions.TakeEnded(Key));
            Assert.Equal("kept-1", string.Join(",", OpenClawSessions.PendingFor(OtherKey).Select(a => a.Id)));
        }
    }

    // --- M21: the poll's isolation ---

    // The approvals lists fail; the sessions.list in the same cycle still parses and
    // publishes, and the other kinds' lists are still asked. A cycle that died with
    // the approvals read would publish nothing, which is every OpenClaw orb stalled.
    [Fact]
    public async Task AFailingApprovalsListStillLetsTheSessionsParseInTheSameCycle()
    {
        var (socket, gateway) = await ConnectedAsync(r => r.Method switch
        {
            "sessions.list" => FakeGatewaySocket.Ok(r.Id, Json("{\"sessions\":[" + ListRow(Key) + "]}")),
            "exec.approval.list" => FakeGatewaySocket.Error(r.Id, "UNAVAILABLE", "approvals are down", null),
            _ => FakeGatewaySocket.Ok(r.Id, Json("[]")),
        });
        using (gateway)
        {
            await RunOneCycleAsync(gateway, Key);

            Assert.Contains(OpenClawSessions.Snapshot(), s => s.Key == Key);
            var asked = socket.Requests.Select(r => r.Method).ToList();
            Assert.Contains("exec.approval.list", asked);
            Assert.Contains("plugin.approval.list", asked);
            Assert.Contains("openclaw.approval.list", asked);
        }
    }
}
