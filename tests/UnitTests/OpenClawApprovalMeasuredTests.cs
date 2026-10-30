using System.IO;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// The approval shapes MEASURED on a live gateway (openclaw 2026.10.5, CB-270 Stage 6
// P1 to P7), scrubbed: ids, session keys, the device id and the clock are
// placeholders that keep the shape. Everything here is what the gateway actually
// sent, field for field, nulls included; the tests around it pin what this app
// does with each. Where an older fixture elsewhere says "assembled" or "assumed",
// this file is the measurement that replaces it.
//
// What the run showed, in short:
//   * the legacy exec.approval.requested broadcast and the exec.approval.list row
//     are the same record, with many null fields and allowedDecisions that
//     includes "allow-always";
//   * the legacy exec.approval.resolved broadcast is {id, decision, resolvedBy, ts,
//     request}: NO status, NO reason, `resolvedBy` a string and `ts` a number, which
//     were the aliases we had marked assumed. An expiry arrives as decision "deny"
//     with resolvedBy null, stamped at the expiry instant;
//   * session.approval carries both phases, and the terminal one is the only place
//     an expiry says "expired" and "timeout";
//   * resolving an id again after it was decided answers applied:false with the
//     terminal snapshot, and an id the gateway does not know is INVALID_REQUEST
//     "approval not found";
//   * a connection that asked for no cap got no approval event of any kind.
[Collection("Settings")]
public class OpenClawApprovalMeasuredTests : IDisposable
{
    private const string Key = "agent:main:dashboard:00000000-0000-4000-8000-000000000001";
    private const string Device = "device-0001";

    private static readonly DateTime Now = new(
        DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private readonly bool _approve = OrbweaverSettings.OpenClawApproveEnabled;

    public OpenClawApprovalMeasuredTests()
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
    }

    // --- the measured records ---

    private static string Request(string key = Key) => $$"""
        {"command":"true","systemRunBinding":null,"systemRunPlan":null,"cwd":"/tmp","nodeId":null,
         "host":null,"security":null,"ask":null,"warningText":null,"scope":null,
         "commandAnalysis":{"commandCount":1,"nestedCommandCount":0,"riskKinds":[],"warningLines":[]},
         "allowedDecisions":["allow-once","allow-always","deny"],"agentId":"main","resolvedPath":null,
         "sessionKey":"{{key}}","sessionId":null,"runId":null,"toolCallId":null,"turnSourceChannel":null,
         "turnSourceTo":null,"turnSourceAccountId":null,"turnSourceThreadId":null,
         "cronExecutionSource":null,"cronOperationBinding":null}
        """;

    // exec.approval.requested, and the same record as a row of exec.approval.list.
    private static string Requested(string id, DateTime expires) =>
        "{\"approvalKind\":\"exec\",\"id\":\"" + id + "\",\"request\":" + Request() + ",\"createdAtMs\":" +
        Ms(Now) + ",\"expiresAtMs\":" + Ms(expires) + "}";

    // exec.approval.resolved after a person's answer: no status, no reason.
    private static string ResolvedDeny(string id, DateTime at) =>
        "{\"id\":\"" + id + "\",\"decision\":\"deny\",\"resolvedBy\":\"gateway-client\",\"ts\":" + Ms(at) +
        ",\"request\":" + Request() + "}";

    // exec.approval.resolved after the request ran out of time: a "deny" nobody gave.
    private static string ResolvedByTimeout(string id, DateTime at) =>
        "{\"id\":\"" + id + "\",\"decision\":\"deny\",\"resolvedBy\":null,\"ts\":" + Ms(at) +
        ",\"request\":" + Request() + "}";

    private static string Presentation() =>
        "{\"kind\":\"exec\",\"commandText\":\"true\",\"commandPreview\":null,\"warningText\":null,\"host\":null," +
        "\"nodeId\":null,\"agentId\":\"main\",\"allowedDecisions\":[\"allow-once\",\"allow-always\",\"deny\"]}";

    private static string Snapshot(string id, string status, DateTime expires, string tail = "") =>
        "{\"id\":\"" + id + "\",\"status\":\"" + status + "\",\"presentation\":" + Presentation() +
        ",\"urlPath\":\"/approve/" + id + "\",\"createdAtMs\":" + Ms(Now) + ",\"expiresAtMs\":" + Ms(expires) + tail + "}";

    private static string SessionEvent(string phase, string snapshot, DateTime at) =>
        "{\"sessionKey\":\"" + Key + "\",\"sourceSessionKey\":\"" + Key + "\",\"updatedAtMs\":" + Ms(at) +
        ",\"phase\":\"" + phase + "\",\"approval\":" + snapshot + "}";

    private static void Event(string name, string json) => OpenClawSessions.OnEvent(name, Json(json), Now);

    private static string Ids() =>
        string.Join(",", OpenClawSessions.PendingFor(Key, new DateTimeOffset(Now)).Select(a => a.Id));

    // --- requested, list row and replay ---

    [Fact]
    public void TheMeasuredRequestedRecordIsHeldWithItsThreeDecisionsAndOnlyTwoAreEverSent()
    {
        var expires = Now.AddMinutes(5);
        Event("exec.approval.requested", Requested("approval-0001", expires));

        var held = Assert.Single(OpenClawSessions.PendingFor(Key, new DateTimeOffset(Now)));
        Assert.Equal("true", held.Command);
        Assert.Equal("main", held.AgentId);
        Assert.Equal(new[] { "allow-once", "allow-always", "deny" }, held.AllowedDecisions);
        Assert.True(held.Allows("deny"));
        Assert.True(held.Allows("allow-once"));

        // The gateway offers a standing grant; this app never sends one.
        Assert.False(OpenClawApprovals.IsAnswerableDecision("allow-always"));
    }

    [Fact]
    public void TheMeasuredListRowIsTheSameRecord()
    {
        var parsed = OpenClawApprovals.ParseList(Json("[" + Requested("approval-0001", Now.AddMinutes(5)) + "]"), "exec", new DateTimeOffset(Now));

        var row = Assert.Single(parsed);
        Assert.Equal("approval-0001", row.Id);
        Assert.Equal(Key, row.SessionKey);
        Assert.Equal("true", row.Command);
    }

    [Fact]
    public void TheMeasuredReplayHoldsAPendingSnapshotWithItsSourceSession()
    {
        var response = "{\"subscribed\":true,\"key\":\"" + Key + "\",\"agentId\":\"main\",\"approvalReplay\":{\"sessionKey\":\"" +
                       Key + "\",\"updatedAtMs\":" + Ms(Now) + ",\"approvals\":[" +
                       Snapshot("approval-0001", "pending", Now.AddMinutes(5), ",\"sourceSessionKey\":\"" + Key + "\"") +
                       "],\"truncated\":false}}";

        var replay = OpenClawApprovals.ParseReplay(Json(response), Key, new DateTimeOffset(Now))!;

        Assert.False(replay.Truncated);
        var held = Assert.Single(replay.Approvals);
        Assert.Equal("true", held.Command);
        Assert.Equal(new[] { "allow-once", "allow-always", "deny" }, held.AllowedDecisions);
    }

    // --- the legacy resolved broadcast ---

    [Fact]
    public void ThePersonsAnswerOnTheLegacyEventEndsTheRecordAsADenialWithNoStatus()
    {
        Event("exec.approval.requested", Requested("approval-0001", Now.AddMinutes(5)));

        Event("exec.approval.resolved", ResolvedDeny("approval-0001", Now.AddSeconds(10)));

        var ended = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal("resolved", ended.Cause);
        Assert.Equal("deny", ended.Decision);
        Assert.Equal("gateway-client", ended.ResolvedBy);
        Assert.Equal("unknown", ended.Status);
    }

    // The measured expiry: decision "deny", resolvedBy null, stamped after the
    // record's own expiry. Read alone it is an expiry, not a refusal.
    [Fact]
    public void AnExpiryOnTheLegacyEventAloneEndsTheRecordAsExpiredNotDenied()
    {
        var expires = Now.AddMinutes(5);
        Event("exec.approval.requested", Requested("approval-0001", expires));

        Event("exec.approval.resolved", ResolvedByTimeout("approval-0001", expires.AddMilliseconds(7)));

        var ended = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal("expired", ended.Cause);
        Assert.Equal("expired", ended.Status);
        Assert.Equal("timeout", ended.Reason);
        Assert.Null(ended.Decision);
        Assert.Equal("", Ids());
    }

    // The boundary is "at or after": the instant of expiry is an expiry, a millisecond
    // earlier is still a person's answer. And no timestamp means no inference.
    [Fact]
    public void TheExpiryInferenceIsAtOrAfterTheExpiryAndNeedsATimestamp()
    {
        var expires = Now.AddMinutes(5);

        Event("exec.approval.requested", Requested("at", expires));
        Event("exec.approval.resolved", ResolvedByTimeout("at", expires));
        Assert.Equal("expired", Assert.Single(OpenClawSessions.TakeEnded(Key)).Cause);

        Event("exec.approval.requested", Requested("before", expires));
        Event("exec.approval.resolved", ResolvedByTimeout("before", expires.AddMilliseconds(-1)));
        Assert.Equal("resolved", Assert.Single(OpenClawSessions.TakeEnded(Key)).Cause);

        Event("exec.approval.requested", Requested("no-ts", expires));
        Event("exec.approval.resolved", "{\"id\":\"no-ts\",\"decision\":\"deny\",\"resolvedBy\":null}");
        var untimed = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal("resolved", untimed.Cause);
        Assert.Equal("deny", untimed.Decision);
    }

    // An event that does carry a status is never second-guessed by the clock.
    [Fact]
    public void ADeclaredStatusOnTheEventWinsOverTheClock()
    {
        var expires = Now.AddMinutes(5);
        Event("exec.approval.requested", Requested("approval-0001", expires));

        Event("exec.approval.resolved",
            "{\"id\":\"approval-0001\",\"status\":\"denied\",\"decision\":\"deny\",\"ts\":" + Ms(expires.AddMinutes(1)) + "}");

        var ended = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal("resolved", ended.Cause);
        Assert.Equal("denied", ended.Status);
    }

    // --- both arrival orders with the session event ---

    [Fact]
    public void AnExpiryReadsAsExpiredWhicheverEventArrivesFirst()
    {
        var expires = Now.AddMinutes(5);
        var terminal = SessionEvent("terminal",
            Snapshot("approval-0001", "expired", expires, ",\"resolvedAtMs\":" + Ms(expires.AddMilliseconds(7)) + ",\"reason\":\"timeout\""),
            expires.AddMilliseconds(7));

        // The measured order: the session event first, the legacy one after.
        Event("session.approval", SessionEvent("pending", Snapshot("approval-0001", "pending", expires), Now));
        Event("session.approval", terminal);
        Event("exec.approval.resolved", ResolvedByTimeout("approval-0001", expires.AddMilliseconds(7)));
        var first = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal(("expired", "timeout"), (first.Status, first.Reason));

        // The other order: the legacy event first, the terminal one finds nothing to end.
        Event("session.approval", SessionEvent("pending", Snapshot("approval-0002", "pending", expires), Now));
        Event("exec.approval.resolved", ResolvedByTimeout("approval-0002", expires.AddMilliseconds(7)));
        Event("session.approval", terminal.Replace("approval-0001", "approval-0002"));
        var second = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal(("expired", "timeout"), (second.Status, second.Reason));
    }

    [Fact]
    public void ADenialReadsAsADenialWhicheverEventArrivesFirst()
    {
        var expires = Now.AddMinutes(5);
        var at = Now.AddSeconds(10);
        string Terminal(string id) => SessionEvent("terminal",
            Snapshot(id, "denied", expires, ",\"resolvedAtMs\":" + Ms(at) + ",\"reason\":\"user\",\"decision\":\"deny\""), at);

        Event("session.approval", SessionEvent("pending", Snapshot("approval-0001", "pending", expires), Now));
        Event("session.approval", Terminal("approval-0001"));
        Event("exec.approval.resolved", ResolvedDeny("approval-0001", at));
        var first = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal(("resolved", "denied", "deny"), (first.Cause, first.Status, first.Decision));

        Event("session.approval", SessionEvent("pending", Snapshot("approval-0002", "pending", expires), Now));
        Event("exec.approval.resolved", ResolvedDeny("approval-0002", at));
        Event("session.approval", Terminal("approval-0002"));
        var second = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal(("resolved", "deny"), (second.Cause, second.Decision));
    }

    [Fact]
    public void AMeasuredAllowOnceTerminalEventCarriesItsStatusReasonAndDecision()
    {
        var expires = Now.AddMinutes(5);
        Event("session.approval", SessionEvent("pending", Snapshot("approval-0001", "pending", expires), Now));

        Event("session.approval", SessionEvent("terminal",
            Snapshot("approval-0001", "allowed", expires, ",\"resolvedAtMs\":" + Ms(Now) + ",\"reason\":\"user\",\"decision\":\"allow-once\""), Now));

        var ended = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal(("allowed", "user", "allow-once"), (ended.Status, ended.Reason, ended.Decision));
    }

    // --- the resolve call's answers ---

    [Fact]
    public void TheMeasuredResolveAnswersReadAsAppliedAndAsDecidedElsewhere()
    {
        var terminal = Snapshot("approval-0001", "denied", Now.AddMinutes(5),
            ",\"resolvedAtMs\":" + Ms(Now) + ",\"reason\":\"user\",\"decision\":\"deny\"," +
            "\"source\":{\"agentId\":\"main\",\"sessionKey\":\"" + Key + "\"},\"resolver\":{\"kind\":\"device\",\"id\":\"" + Device + "\"}");

        var applied = OpenClawApprovals.ParseResolveResult(Json("{\"applied\":true,\"approval\":" + terminal + "}"))!;
        var again = OpenClawApprovals.ParseResolveResult(Json("{\"applied\":false,\"approval\":" + terminal + "}"))!;

        Assert.True(applied.Applied);
        Assert.Equal(("denied", "user", "deny"), (applied.Status, applied.Reason, applied.Decision));
        Assert.False(again.Applied);
        Assert.Equal("denied", again.Status);
    }

    // --- the measured not-found, and what is not ---

    private static OpenClawRequestException Refusal(string message, string code) =>
        new(message, null) { Code = code };

    [Fact]
    public void TheMeasuredNotFoundIsNotFoundAndNothingLooserIs()
    {
        Assert.True(OpenClawApprovals.IsNotFound(Refusal("approval not found", "INVALID_REQUEST")));

        // Controls: the same words under another code, a transient refusal, and a
        // near miss are all ordinary failures, which keep the record and allow a retry.
        Assert.False(OpenClawApprovals.IsNotFound(Refusal("approval not found", "UNAVAILABLE")));
        Assert.False(OpenClawApprovals.IsNotFound(Refusal("unknown error: gateway busy", "INVALID_REQUEST")));
        Assert.False(OpenClawApprovals.IsNotFound(Refusal("approval not found yet", "INVALID_REQUEST")));
    }

    private static async Task<OpenClawGateway> ConnectedAsync(Func<FakeGatewaySocket.Request, object?> answer)
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
        OpenClawSessions.SetGatewayForTests(gateway);
        return gateway;
    }

    // Through the one method that answers: the measured refusal for a gone id takes
    // the record off the screen; a transient refusal keeps it.
    [Fact]
    public async Task AClickOnAnIdTheGatewayNoLongerKnowsIsNoLongerPendingButABusyGatewayIsAFailure()
    {
        Event("exec.approval.requested", Requested("approval-0001", Now.AddMinutes(5)));
        var refusal = "approval not found";
        using var gateway = await ConnectedAsync(r => r.Method == "approval.resolve"
            ? FakeGatewaySocket.Error(r.Id, "INVALID_REQUEST", refusal, null)
            : null);
        var chat = new OpenClawChatSession("openclaw:" + Key, Key, "Main");

        refusal = "unknown error: gateway busy";
        var failed = await OpenClawSessions.ResolveApprovalAsync(chat, "approval-0001", "deny");
        Assert.Equal(OpenClawApprovalResult.Failed, failed.Result);
        Assert.Equal("approval-0001", Ids());

        refusal = "approval not found";
        var gone = await OpenClawSessions.ResolveApprovalAsync(chat, "approval-0001", "deny");
        Assert.Equal(OpenClawApprovalResult.NoLongerPending, gone.Result);
        Assert.Equal("", Ids());
    }
}
