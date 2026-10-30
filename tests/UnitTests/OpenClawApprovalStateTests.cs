using System.IO;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// Stage 6's gateway side with a real OpenClawSessions and an in-memory socket:
// what turns an orb amber, how the broadcasts and the poll feed it, and the one
// method that answers an approval.
//
// The record shapes are DECLARED, not measured. They were read out of openclaw
// 2026.10.5's own dist and nothing has been called against a live approval; see
// OpenClawApprovalsTests for the same note. What *is* pinned here is this app's own
// behaviour around them, which does not depend on the gateway agreeing.
//
// Three of these are the Stage's no-auto-approve requirement as tests that fail,
// not prose: the literal "approval.resolve" has exactly one production site; a
// replay of every approval path sends zero resolve frames; and a decision outside
// {deny, allow-once} never reaches the wire.
[Collection("Settings")]
public class OpenClawApprovalStateTests : IDisposable
{
    private const string Key = "agent:alpha:dashboard:11111111-2222-3333-4444-555555555555";
    private const string OtherKey = "agent:beta:dashboard:99999999-8888-7777-6666-555555555555";

    // The real clock, to the second: the poll reads the real one, and a fixed date
    // in the past would make every record it ingests already expired.
    private static readonly DateTime Now = new(
        DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();

    private readonly bool _approve = OrbweaverSettings.OpenClawApproveEnabled;

    public OpenClawApprovalStateTests()
    {
        OrbweaverSettings.OpenClawApproveEnabled = true;
        Reset();
    }

    public void Dispose()
    {
        Reset();
        OrbweaverSettings.OpenClawApproveEnabled = _approve;
        OpenClawSessions.SetGatewayForTests(null);
    }

    // Nothing carried between cases: pending records, endings, answered ids, runs.
    private static void Reset()
    {
        OrbweaverSettings.OpenClawEnabled = false;
        OpenClawSessions.Restart();
        OpenClawSessions.ForgetRunningForTests();
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static string Row(string id, string key = Key, string kind = "exec", DateTime? expires = null) =>
        "{\"id\":\"" + id + "\",\"approvalKind\":\"" + kind + "\",\"request\":{\"command\":\"ls -la /tmp\"," +
        "\"cwd\":\"/tmp\",\"host\":\"gateway\",\"agentId\":\"alpha\",\"sessionKey\":\"" + key + "\"}," +
        "\"createdAtMs\":" + Ms(Now) + ",\"expiresAtMs\":" + Ms(expires ?? Now.AddMinutes(30)) + "}";

    private static void Requested(string id, string key = Key, string kind = "exec", DateTime? expires = null) =>
        OpenClawSessions.OnEvent(kind + ".approval.requested", Json(Row(id, key, kind, expires)), Now);

    private static void Resolved(string id, string kind = "exec", string decision = "deny") =>
        OpenClawSessions.OnEvent(
            kind + ".approval.resolved",
            Json("{\"id\":\"" + id + "\",\"decision\":\"" + decision + "\",\"resolvedBy\":\"device-0001\"}"), Now);

    private static string Ids(string key) =>
        string.Join(",", OpenClawSessions.PendingFor(key, new DateTimeOffset(Now)).Select(a => a.Id));

    // --- amber ---

    [Fact]
    public void ARequestedApprovalMakesTheSessionWaitingWithNoRunInFlight()
    {
        Assert.Equal("idle", OpenClawSessions.StateFor(Key, Now));

        Requested("a1");

        Assert.Equal("a1", Ids(Key));
        Assert.Equal("waiting", OpenClawSessions.StateFor(Key, Now));
    }

    // A session mid-run that is blocked on a person is waiting, not working.
    [Fact]
    public void WaitingOutranksGenerating()
    {
        OpenClawSessions.SetRunningForTests(Key, Now, open: true);
        Assert.Equal("generating", OpenClawSessions.StateFor(Key, Now));

        Requested("a1");
        Assert.Equal("waiting", OpenClawSessions.StateFor(Key, Now));

        Resolved("a1");
        Assert.Equal("generating", OpenClawSessions.StateFor(Key, Now));
    }

    // Only the session the approval names. A neighbour is unaffected.
    [Fact]
    public void OnlyTheNamedSessionGoesAmber()
    {
        Requested("a1");

        Assert.Equal("waiting", OpenClawSessions.StateFor(Key, Now));
        Assert.Equal("idle", OpenClawSessions.StateFor(OtherKey, Now));
    }

    [Fact]
    public void ResolvingTheOnlyApprovalLeavesAmberAndATwoDeepQueueNeedsBothAnswered()
    {
        Requested("a1");
        Requested("a2");

        Resolved("a1");
        Assert.Equal("waiting", OpenClawSessions.StateFor(Key, Now));
        Assert.Equal("a2", Ids(Key));

        Resolved("a2");
        Assert.Equal("idle", OpenClawSessions.StateFor(Key, Now));
    }

    // Expiry is enforced here as well as by the gateway, so a prompt cannot
    // outlive its record when every signal is missed.
    [Fact]
    public void AnApprovalPastItsExpiryIsNoLongerPendingOrWaiting()
    {
        Requested("a1", expires: Now.AddMinutes(5));
        Assert.Equal("waiting", OpenClawSessions.StateFor(Key, Now.AddMinutes(4)));

        Assert.Equal("idle", OpenClawSessions.StateFor(Key, Now.AddMinutes(5)));
        Assert.Empty(OpenClawSessions.PendingFor(Key, new DateTimeOffset(Now.AddMinutes(5))));
    }

    // --- the broadcasts ---

    [Fact]
    public void ARequestAlreadyPastItsExpiryIsNotHeld()
    {
        Requested("a1", expires: Now.AddSeconds(-1));

        Assert.Equal("", Ids(Key));
        Assert.Equal("idle", OpenClawSessions.StateFor(Key, Now));
    }

    [Fact]
    public void TheSameApprovalTwiceIsOneRecordEvenAcrossSessions()
    {
        Requested("a1");
        Requested("a1");
        Requested("a1", key: OtherKey);

        Assert.Equal("a1", Ids(Key));
        Assert.Equal("", Ids(OtherKey));
    }

    // A flood is capped: the oldest stay and the rest are not held.
    [Fact]
    public void ARunawayFloodIsCappedPerSession()
    {
        for (var i = 0; i < OpenClawApprovals.MaxPerSession + 5; i++) Requested("a" + i.ToString("D3"));

        var held = OpenClawSessions.PendingFor(Key, new DateTimeOffset(Now));

        Assert.Equal(OpenClawApprovals.MaxPerSession, held.Count);
        Assert.Contains(held, a => a.Id == "a000");
        Assert.DoesNotContain(held, a => a.Id == "a" + (OpenClawApprovals.MaxPerSession + 4).ToString("D3"));

        // The cap is per session: another session still holds its own.
        Requested("b1", key: OtherKey);
        Assert.Equal("b1", Ids(OtherKey));
    }

    // Oldest first, ties broken on id so the answer is the same twice running.
    [Fact]
    public void PendingIsOldestFirstWithTiesBrokenOnId()
    {
        var older = Row("z-older").Replace("\"createdAtMs\":" + Ms(Now), "\"createdAtMs\":" + Ms(Now.AddMinutes(-2)));
        OpenClawSessions.OnEvent("exec.approval.requested", Json(Row("b-tie")), Now);
        OpenClawSessions.OnEvent("exec.approval.requested", Json(Row("a-tie")), Now);
        OpenClawSessions.OnEvent("exec.approval.requested", Json(older), Now);

        Assert.Equal("z-older,a-tie,b-tie", Ids(Key));
    }

    // An ending for an id this app never held is ignored: a broadcast cannot
    // invent an ending for something it was not holding.
    [Fact]
    public void AResolvedEventForAnUnknownIdChangesNothing()
    {
        Requested("a1");

        Resolved("never-listed");

        Assert.Equal("a1", Ids(Key));
        Assert.Empty(OpenClawSessions.TakeEnded(Key));
    }

    [Fact]
    public void AResolvedEventIsRecordedOnceForThePanelToWriteUp()
    {
        Requested("a1");

        Resolved("a1", decision: "allow-once");

        var ended = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal("a1", ended.Approval.Id);
        Assert.Equal("resolved", ended.Cause);
        Assert.Equal("allow-once", ended.Decision);
        Assert.Equal("device-0001", ended.ResolvedBy);

        // Drained: the same ending is not written up twice.
        Assert.Empty(OpenClawSessions.TakeEnded(Key));
    }

    // The endings kept per session are bounded too.
    [Fact]
    public void TheEndingsKeptPerSessionAreBounded()
    {
        for (var i = 0; i < 25; i++)
        {
            Requested("e" + i.ToString("D2"));
            Resolved("e" + i.ToString("D2"));
        }

        var ended = OpenClawSessions.TakeEnded(Key);

        Assert.Equal(20, ended.Count);
        Assert.Equal("e05", ended[0].Approval.Id);
        Assert.Equal("e24", ended[^1].Approval.Id);
    }

    // Without the switch, an approval event that arrives anyway does nothing: it is
    // not allowed to colour an orb for someone who never asked to approve anything.
    [Fact]
    public void WithApprovingOffAnApprovalEventIsIgnored()
    {
        OrbweaverSettings.OpenClawApproveEnabled = false;

        Requested("a1");

        Assert.Equal("", Ids(Key));
        Assert.Equal("idle", OpenClawSessions.StateFor(Key, Now));

        // The control: the same event with the switch on is held.
        OrbweaverSettings.OpenClawApproveEnabled = true;
        Requested("a1");
        Assert.Equal("a1", Ids(Key));
    }

    // A payload that is not a record, or a resolved event with no id, is ignored.
    [Fact]
    public void MalformedApprovalEventsAreIgnored()
    {
        OpenClawSessions.OnEvent("exec.approval.requested", Json("{}"), Now);
        OpenClawSessions.OnEvent("exec.approval.resolved", Json("{}"), Now);
        OpenClawSessions.OnEvent("plugin.approval.requested", Json("[]"), Now);

        Assert.Equal("idle", OpenClawSessions.StateFor(Key, Now));
    }

    // An approval event never touches a run's state, and does touch last-seen.
    [Fact]
    public void AnApprovalEventDoesNotStartOrEndARun()
    {
        Requested("a1");
        Resolved("a1");

        Assert.Equal("idle", OpenClawSessions.StateFor(Key, Now));

        OpenClawSessions.SetRunningForTests(Key, Now, open: true);
        Requested("a2");
        Resolved("a2");
        Assert.Equal("generating", OpenClawSessions.StateFor(Key, Now));
    }

    // --- reconnect ---

    [Fact]
    public void ARestartForgetsEverythingPending()
    {
        Requested("a1");
        Requested("a2", key: OtherKey);
        Resolved("a2");

        OpenClawSessions.Restart();

        Assert.Equal("", Ids(Key));
        Assert.Equal("idle", OpenClawSessions.StateFor(Key, Now));
        Assert.Empty(OpenClawSessions.TakeEnded(OtherKey));
    }

    // --- the poll's reconciliation ---

    [Fact]
    public void ReplacingAKindDropsWhatTheListOmitsAndKeepsOtherKinds()
    {
        Requested("exec-1");
        Requested("exec-2");
        Requested("plug-1", kind: "plugin");

        var kept = OpenClawApprovals.ParseList(Json("[" + Row("exec-2") + "]"), "exec", new DateTimeOffset(Now));
        OpenClawSessions.ReplacePending("exec", kept, new DateTimeOffset(Now));

        Assert.Equal("exec-2,plug-1", Ids(Key));

        // What went missing without a broadcast is recorded as gone, before expiry.
        var ended = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal("exec-1", ended.Approval.Id);
        Assert.Equal("gone", ended.Cause);
        Assert.Null(ended.Decision);
    }

    // Found missing after its expiry, the ending is an expiry.
    [Fact]
    public void AnApprovalTheListOmitsAfterItsExpiryEndedByExpiring()
    {
        Requested("a1", expires: Now.AddMinutes(5));

        OpenClawSessions.ReplacePending("exec", Array.Empty<OpenClawApproval>(), new DateTimeOffset(Now.AddMinutes(6)));

        Assert.Equal("expired", Assert.Single(OpenClawSessions.TakeEnded(Key)).Cause);
        Assert.Equal("", Ids(Key));
    }

    // The list also teaches this app about approvals it was never told of, which
    // is how an approval raised before the app started becomes amber.
    [Fact]
    public void TheListAddsWhatBroadcastsNeverSaidAndSkipsExpiredAndDuplicateRows()
    {
        var now = new DateTimeOffset(Now);
        var rows = OpenClawApprovals.ParseList(
            Json("[" + Row("a1") + "," + Row("a1") + "," + Row("stale", expires: Now.AddSeconds(-1)) + "]"), "exec", now);

        OpenClawSessions.ReplacePending("exec", rows, now);

        Assert.Equal("a1", Ids(Key));
        Assert.Equal("waiting", OpenClawSessions.StateFor(Key, Now));
    }

    [Fact]
    public void AListIdentifiedByABroadcastEarlierIsNotDuplicated()
    {
        Requested("a1");

        OpenClawSessions.ReplacePending(
            "exec", OpenClawApprovals.ParseList(Json("[" + Row("a1") + "]"), "exec", new DateTimeOffset(Now)),
            new DateTimeOffset(Now));

        Assert.Equal("a1", Ids(Key));
        Assert.Empty(OpenClawSessions.TakeEnded(Key));
    }

    // --- the harness: a gateway granted the scopes a case asks for ---

    private static async Task<(FakeGatewaySocket Socket, OpenClawGateway Gateway)> ConnectedAsync(
        string[] scopes, Func<FakeGatewaySocket.Request, object?>? answer = null)
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

        var result = await gateway.ConnectAsync(null, CancellationToken.None);
        Assert.Equal(OpenClawGateway.Outcome.Connected, result.Outcome);

        OpenClawSessions.SetGatewayForTests(gateway);
        return (socket, gateway);
    }

    private static readonly string[] Approving = { "operator.read", "operator.approvals" };
    private static readonly string[] ReadOnly = { "operator.read" };

    private static List<FakeGatewaySocket.Request> Sent(FakeGatewaySocket socket) =>
        socket.Requests.Where(r => r.Method != "connect").ToList();

    private static OpenClawChatSession Chat(string key = Key) =>
        new("openclaw:" + key, key, "Alpha");

    private static OpenClawApproval Approval(string id = "a1", string key = Key, string kind = "exec") =>
        OpenClawApprovals.ParseRecord(Json(Row(id, key, kind)), kind, new DateTimeOffset(Now))!;

    // --- the poll over a socket ---

    [Fact]
    public async Task APollingDeviceWithTheScopeAsksEachListAndIngestsTheAnswers()
    {
        var (socket, gateway) = await ConnectedAsync(Approving, r => r.Method switch
        {
            "exec.approval.list" => FakeGatewaySocket.Ok(r.Id, Json("[" + Row("e1") + "]")),
            "plugin.approval.list" => FakeGatewaySocket.Ok(r.Id, Json("[" + Row("p1", kind: "plugin") + "]")),
            _ => FakeGatewaySocket.Ok(r.Id, Json("[]")),
        });
        using (gateway)
        {
            await OpenClawSessions.PollApprovalsOnceAsync(gateway, CancellationToken.None);

            Assert.Equal(
                new[] { "exec.approval.list", "plugin.approval.list", "openclaw.approval.list" },
                Sent(socket).Select(r => r.Method).ToArray());
            Assert.Equal("e1,p1", string.Join(",", OpenClawSessions.PendingFor(Key).Select(a => a.Id).OrderBy(i => i)));
        }
    }

    // The paired negative controls: without the switch, or with it but without the
    // scope having been granted, the poll asks nothing at all.
    [Fact]
    public async Task WithTheSwitchOffThePollAsksNothing()
    {
        OrbweaverSettings.OpenClawApproveEnabled = false;
        var (socket, gateway) = await ConnectedAsync(Approving);
        using (gateway)
        {
            await OpenClawSessions.PollApprovalsOnceAsync(gateway, CancellationToken.None);

            Assert.Empty(Sent(socket));
        }
    }

    [Fact]
    public async Task WithTheSwitchOnButTheScopeNotGrantedThePollAsksNothing()
    {
        var (socket, gateway) = await ConnectedAsync(ReadOnly);
        using (gateway)
        {
            await OpenClawSessions.PollApprovalsOnceAsync(gateway, CancellationToken.None);

            Assert.Empty(Sent(socket));
        }
    }

    // One refused list must not wipe the others or take the poll down: a kind that
    // could not be read keeps its records, and the others are still reconciled.
    [Fact]
    public async Task ARefusedListLeavesItsKindAloneAndTheOthersStillReconcile()
    {
        Requested("p-old", kind: "plugin");

        var (_, gateway) = await ConnectedAsync(Approving, r => r.Method switch
        {
            "plugin.approval.list" => FakeGatewaySocket.Error(r.Id, "INVALID_REQUEST", "missing scope: operator.approvals", null),
            "exec.approval.list" => FakeGatewaySocket.Ok(r.Id, Json("[" + Row("e1") + "]")),
            _ => FakeGatewaySocket.Ok(r.Id, Json("[]")),
        });
        using (gateway)
        {
            await OpenClawSessions.PollApprovalsOnceAsync(gateway, CancellationToken.None);

            Assert.Equal("e1,p-old", string.Join(",", OpenClawSessions.PendingFor(Key).Select(a => a.Id).OrderBy(i => i)));
        }
    }

    // Cancelled mid-poll is not swallowed as a refused list.
    [Fact]
    public async Task ACancelledPollIsNotSwallowed()
    {
        var (_, gateway) = await ConnectedAsync(Approving);
        using (gateway)
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => OpenClawSessions.PollApprovalsOnceAsync(gateway, cts.Token));
        }
    }

    // --- answering ---

    private static async Task<(OpenClawApprovalOutcome Outcome, FakeGatewaySocket Socket)> AnswerAsync(
        string decision, Func<string, object> reply, string kind = "exec", OpenClawChatSession? chat = null,
        OpenClawApproval? approval = null)
    {
        var (socket, gateway) = await ConnectedAsync(Approving, r =>
            r.Method == "approval.resolve" ? reply(r.Id) : null);
        using (gateway)
        {
            var outcome = await OpenClawSessions.ResolveApprovalAsync(
                chat ?? Chat(), approval ?? Approval(kind: kind), decision);
            return (outcome, socket);
        }
    }

    // The exact frame: id, kind and decision, in that order, no reviewer, never
    // allow-always.
    [Theory]
    [InlineData("deny")]
    [InlineData("allow-once")]
    public async Task AnAnswerSendsExactlyIdKindAndDecision(string decision)
    {
        Requested("a1");
        var (outcome, socket) = await AnswerAsync(decision,
            id => FakeGatewaySocket.Ok(id, Json("{\"applied\":true,\"approval\":{\"status\":\"denied\",\"reason\":\"user\"}}")));

        var frame = Assert.Single(Sent(socket));
        Assert.Equal("approval.resolve", frame.Method);
        Assert.Equal(new[] { "id", "kind", "decision" }, frame.Params.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("a1", frame.Params.GetProperty("id").GetString());
        Assert.Equal("exec", frame.Params.GetProperty("kind").GetString());
        Assert.Equal(decision, frame.Params.GetProperty("decision").GetString());
        Assert.False(frame.Params.TryGetProperty("reviewer", out _));
        Assert.False(frame.Params.TryGetProperty("grantExpiresInDays", out _));

        Assert.Equal(OpenClawApprovalResult.Answered, outcome.Result);
        Assert.Equal(decision, outcome.Decision);
    }

    [Theory]
    [InlineData("plugin")]
    [InlineData("system-agent")]
    public async Task TheKindOnTheFrameIsTheRecordsOwn(string kind)
    {
        var (_, socket) = await AnswerAsync("deny",
            id => FakeGatewaySocket.Ok(id, Json("{\"applied\":true}")), kind);

        Assert.Equal(kind, Assert.Single(Sent(socket)).Params.GetProperty("kind").GetString());
    }

    // Answering takes it off the pending set at once, without waiting for a poll.
    [Fact]
    public async Task AnAnsweredApprovalLeavesThePendingSetAndTheAmber()
    {
        Requested("a1");
        await AnswerAsync("deny", id => FakeGatewaySocket.Ok(id, Json("{\"applied\":true}")));

        Assert.Equal("", Ids(Key));
        Assert.Equal("idle", OpenClawSessions.StateFor(Key, Now));
    }

    // applied:false: another reviewer decided first, and the snapshot says what.
    [Fact]
    public async Task AnUnappliedAnswerIsAnotherReviewersDecision()
    {
        Requested("a1");
        var (outcome, _) = await AnswerAsync("deny", id => FakeGatewaySocket.Ok(id, Json(
            "{\"applied\":false,\"approval\":{\"status\":\"allowed\",\"reason\":\"user\",\"decision\":\"allow-once\"}}")));

        Assert.Equal(OpenClawApprovalResult.DecidedElsewhere, outcome.Result);
        Assert.Equal("allow-once", outcome.Decision);
        Assert.Equal("allowed", outcome.Status);
        Assert.Equal("", Ids(Key));
    }

    // The gateway does not say whether an id is expired, unknown or not visible.
    [Fact]
    public async Task ANotFoundRefusalIsNoLongerPending()
    {
        Requested("a1");
        var (outcome, _) = await AnswerAsync("deny",
            id => FakeGatewaySocket.Error(id, "INVALID_REQUEST", "approval expired or not found", null));

        Assert.Equal(OpenClawApprovalResult.NoLongerPending, outcome.Result);
        Assert.Equal("", Ids(Key));
    }

    // Any other failure keeps the record and the prompt, and the id may be tried
    // again: nothing was decided.
    [Fact]
    public async Task AnyOtherFailureKeepsTheRecordAndAllowsAnotherAttempt()
    {
        Requested("a1");
        var failing = true;
        var (socket, gateway) = await ConnectedAsync(Approving, r =>
            r.Method != "approval.resolve" ? null
            : failing
                ? FakeGatewaySocket.Error(r.Id, "INVALID_REQUEST", "missing scope: operator.approvals", null)
                : FakeGatewaySocket.Ok(r.Id, Json("{\"applied\":true}")));
        using (gateway)
        {
            var first = await OpenClawSessions.ResolveApprovalAsync(Chat(), Approval(), "deny");

            Assert.Equal(OpenClawApprovalResult.Failed, first.Result);
            Assert.Equal("missing scope: operator.approvals", first.Detail);
            Assert.Equal("a1", Ids(Key));

            failing = false;
            var second = await OpenClawSessions.ResolveApprovalAsync(Chat(), Approval(), "deny");

            Assert.Equal(OpenClawApprovalResult.Answered, second.Result);
            Assert.Equal(2, Sent(socket).Count);
        }
    }

    // A reply that says nothing of whether it applied is not an answer.
    [Fact]
    public async Task AReplyThatDoesNotSayWhetherItAppliedIsAFailureAndKeepsTheRecord()
    {
        Requested("a1");
        var (outcome, _) = await AnswerAsync("deny", id => FakeGatewaySocket.Ok(id, Json("{\"ok\":true}")));

        Assert.Equal(OpenClawApprovalResult.Failed, outcome.Result);
        Assert.Equal("the gateway didn't confirm the answer", outcome.Detail);
        Assert.Equal("a1", Ids(Key));
    }

    // Two clicks, or a click racing a broadcast: one frame. The id is marked the
    // moment the first click lands.
    [Fact]
    public async Task ASecondAnswerForTheSameIdSendsNothing()
    {
        Requested("a1");
        var (socket, gateway) = await ConnectedAsync(Approving, r =>
            r.Method == "approval.resolve" ? FakeGatewaySocket.Ok(r.Id, Json("{\"applied\":true}")) : null);
        using (gateway)
        {
            var first = await OpenClawSessions.ResolveApprovalAsync(Chat(), Approval(), "deny");
            var second = await OpenClawSessions.ResolveApprovalAsync(Chat(), Approval(), "allow-once");

            Assert.Equal(OpenClawApprovalResult.Answered, first.Result);
            Assert.Equal(OpenClawApprovalResult.Duplicate, second.Result);
            Assert.Single(Sent(socket));
        }
    }

    // Nothing outside the two offered answers reaches the wire, and nothing for an
    // approval that belongs to another conversation or with no connection.
    [Theory]
    [InlineData("allow-always")]
    [InlineData("Deny")]
    [InlineData("")]
    public async Task AnAnswerOutsideTheTwoOfferedNeverReachesTheWire(string decision)
    {
        var (socket, gateway) = await ConnectedAsync(Approving);
        using (gateway)
        {
            var outcome = await OpenClawSessions.ResolveApprovalAsync(Chat(), Approval(), decision);

            Assert.Equal(OpenClawApprovalResult.Failed, outcome.Result);
            Assert.Equal("that answer is not offered", outcome.Detail);
            Assert.Empty(Sent(socket));
        }
    }

    [Fact]
    public async Task AnApprovalAnsweredFromAnotherConversationsPanelNeverReachesTheWire()
    {
        var (socket, gateway) = await ConnectedAsync(Approving);
        using (gateway)
        {
            var outcome = await OpenClawSessions.ResolveApprovalAsync(Chat(OtherKey), Approval(), "deny");

            Assert.Equal(OpenClawApprovalResult.Failed, outcome.Result);
            Assert.Equal("this request belongs to another conversation", outcome.Detail);
            Assert.Empty(Sent(socket));
        }
    }

    [Fact]
    public async Task WithNoConnectionNothingIsSentAndTheIdIsNotSpent()
    {
        OpenClawSessions.SetGatewayForTests(null);
        var outcome = await OpenClawSessions.ResolveApprovalAsync(Chat(), Approval(), "deny");

        Assert.Equal(OpenClawApprovalResult.Failed, outcome.Result);
        Assert.Equal("not connected to the gateway", outcome.Detail);

        // Connected afterwards, the same id can still be answered.
        var (_, gateway) = await ConnectedAsync(Approving, r =>
            r.Method == "approval.resolve" ? FakeGatewaySocket.Ok(r.Id, Json("{\"applied\":true}")) : null);
        using (gateway)
        {
            Assert.Equal(
                OpenClawApprovalResult.Answered,
                (await OpenClawSessions.ResolveApprovalAsync(Chat(), Approval(), "deny")).Result);
        }
    }

    // --- no auto-approve, as properties that can fail ---

    // Nothing here answers an approval. Every path that touches an approval, the
    // broadcasts, the polls, expiry, endings, a restart, the setting flipping, runs
    // and no resolve frame is ever sent.
    [Fact]
    public async Task ReplayingEveryApprovalPathSendsZeroResolveFrames()
    {
        var (socket, gateway) = await ConnectedAsync(Approving, r => r.Method switch
        {
            "exec.approval.list" => FakeGatewaySocket.Ok(r.Id, Json("[" + Row("e1") + "," + Row("e2", OtherKey) + "]")),
            "plugin.approval.list" => FakeGatewaySocket.Ok(r.Id, Json("[" + Row("p1", kind: "plugin") + "]")),
            _ => FakeGatewaySocket.Ok(r.Id, Json("[]")),
        });
        using (gateway)
        {
            Requested("b1");
            Requested("b2", kind: "plugin");
            Requested("b3", kind: "system-agent", expires: Now.AddMinutes(1));
            Resolved("b1");
            Resolved("b2", kind: "plugin", decision: "allow-once");
            Resolved("never-listed");

            await OpenClawSessions.PollApprovalsOnceAsync(gateway, CancellationToken.None);
            await OpenClawSessions.PollApprovalsOnceAsync(gateway, CancellationToken.None);

            OpenClawSessions.ReplacePending("exec", Array.Empty<OpenClawApproval>(), new DateTimeOffset(Now.AddHours(2)));
            _ = OpenClawSessions.StateFor(Key, Now.AddHours(2));
            _ = OpenClawSessions.PendingFor(Key, new DateTimeOffset(Now.AddHours(2)));
            _ = OpenClawSessions.TakeEnded(Key);

            OrbweaverSettings.OpenClawApproveEnabled = false;
            Requested("ignored");
            OrbweaverSettings.OpenClawApproveEnabled = true;
            OpenClawSessions.Restart();

            Assert.DoesNotContain(socket.Requests, r => r.Method.EndsWith("approval.resolve", StringComparison.Ordinal));
            Assert.DoesNotContain(socket.Requests, r => r.Method.Contains("resolve", StringComparison.Ordinal));
            Assert.NotEmpty(Sent(socket));
        }
    }

    // The literal that sends a resolve is in exactly one production method. The
    // scan covers every .cs file of the app itself, which is everything outside the
    // tests and the tools: the tools build their own requests for probing and are
    // not part of what a user runs.
    [Fact]
    public void ApprovalResolveHasExactlyOneProductionSite()
    {
        var root = RepositoryRoot();
        var literal = "\"approval.resolve\"";

        var sites = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(
                part => part is "tests" or "tools" or "obj" or "bin" or "TestResults"))
            .SelectMany(f => File.ReadAllLines(f)
                .Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(x => x.Text.Contains(literal, StringComparison.Ordinal)
                        && !x.Text.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .ToList();

        var site = Assert.Single(sites);
        Assert.Equal("OpenClawSessions.Approvals.cs", site.File);

        // And that one is inside ResolveApprovalAsync, not some other method.
        var lines = File.ReadAllLines(Path.Combine(root, "OpenClawSessions.Approvals.cs"));
        var method = lines.Take(site.Line).Select((l, i) => (l, i))
            .Last(x => x.l.Contains("static async Task<OpenClawApprovalOutcome> ", StringComparison.Ordinal));
        Assert.Contains("ResolveApprovalAsync", method.l);
    }

    // The answers the offered options carry are exactly the two the app may send.
    [Fact]
    public void TheOnlyAnswersTheAppCanSendAreDenyAndAllowOnce()
    {
        Assert.Equal("deny", OpenClawApprovals.Deny);
        Assert.Equal("allow-once", OpenClawApprovals.AllowOnce);
        Assert.False(OpenClawApprovals.IsAnswerableDecision("allow-always"));
    }

    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        var directory = Path.GetDirectoryName(thisFile);
        while (directory is not null && !File.Exists(Path.Combine(directory, "Orbweaver.csproj")))
            directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException("no Orbweaver.csproj above " + thisFile);
    }
}
