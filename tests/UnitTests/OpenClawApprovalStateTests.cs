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
        OpenClawSessions.ForgetApprovalsForTests();
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static string Row(string id, string key = Key, string kind = "exec", DateTime? expires = null) =>
        "{\"id\":\"" + id + "\",\"approvalKind\":\"" + kind + "\",\"request\":{\"command\":\"ls -la /tmp\"," +
        "\"cwd\":\"/tmp\",\"host\":\"gateway\",\"agentId\":\"alpha\",\"sessionKey\":\"" + key + "\"}," +
        "\"createdAtMs\":" + Ms(Now) + ",\"expiresAtMs\":" + Ms(expires ?? Now.AddMinutes(30)) + "}";

    private static void Requested(string id, string key = Key, string kind = "exec", DateTime? expires = null) =>
        OpenClawSessions.OnEvent(EventPrefix(kind) + ".approval.requested", Json(Row(id, key, kind, expires)), Now);

    // The gateway names the system agent's events openclaw.approval.*.
    private static string EventPrefix(string kind) => kind == "system-agent" ? "openclaw" : kind;

    private static void Resolved(string id, string kind = "exec", string decision = "deny") =>
        OpenClawSessions.OnEvent(
            EventPrefix(kind) + ".approval.resolved",
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
    public void ARequestDatedInThePastIsShownForASecondNotDroppedUnseen()
    {
        Requested("a1", expires: Now.AddSeconds(-1));

        Assert.Equal("a1", Ids(Key));
        Assert.Equal("waiting", OpenClawSessions.StateFor(Key, Now));

        Assert.Equal("idle", OpenClawSessions.StateFor(Key, Now.AddSeconds(2)));
        Assert.Empty(OpenClawSessions.PendingFor(Key, new DateTimeOffset(Now.AddSeconds(2))));
    }

    // A date years ahead cannot pin a prompt: it expires within a day.
    [Fact]
    public void ARequestDatedAYearAheadExpiresWithinADay()
    {
        Requested("a1", expires: Now.AddDays(365));

        Assert.Equal("waiting", OpenClawSessions.StateFor(Key, Now.AddHours(23)));
        Assert.Equal("idle", OpenClawSessions.StateFor(Key, Now.AddHours(24)));
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

    // A flood is capped at EIGHT per session, asserted as the number and not the
    // constant so that raising or removing the cap fails this test. The newest are
    // kept: the oldest arrival is evicted, and the evictions are recorded so the
    // open panel can say so.
    [Fact]
    public void ARunawayFloodIsCappedAtEightPerSessionKeepingTheNewest()
    {
        for (var i = 0; i < 30; i++) Requested("a" + i.ToString("D3"));

        var held = OpenClawSessions.PendingFor(Key, new DateTimeOffset(Now));

        Assert.Equal(8, held.Count);
        Assert.Equal(
            Enumerable.Range(22, 8).Select(i => "a" + i.ToString("D3")).ToArray(),
            held.Select(a => a.Id).ToArray());

        var evicted = OpenClawSessions.TakeEnded(Key);
        Assert.Equal(20, evicted.Count);
        Assert.All(evicted, e => Assert.Equal("evicted", e.Cause));

        // The cap is per session: another session still holds its own.
        Requested("b1", key: OtherKey);
        Assert.Equal("b1", Ids(OtherKey));
        Assert.Equal(8, OpenClawSessions.PendingFor(Key, new DateTimeOffset(Now)).Count);
    }

    // And SIXTY-FOUR overall: 65 requests on 65 distinct sessions hold 64, and the
    // first, the oldest arrival, is gone and told about.
    [Fact]
    public void SixtyFiveRequestsOnSixtyFiveSessionsHoldSixtyFourAndTheOldestIsGone()
    {
        for (var i = 0; i < 65; i++) Requested("g" + i.ToString("D2"), key: "agent:a" + i + ":dashboard:one");

        var held = Enumerable.Range(0, 65)
            .Count(i => OpenClawSessions.PendingFor("agent:a" + i + ":dashboard:one", new DateTimeOffset(Now)).Count == 1);

        Assert.Equal(64, held);
        Assert.Empty(OpenClawSessions.PendingFor("agent:a0:dashboard:one", new DateTimeOffset(Now)));
        Assert.Equal("evicted", Assert.Single(OpenClawSessions.TakeEnded("agent:a0:dashboard:one")).Cause);
        Assert.Equal("waiting", OpenClawSessions.StateFor("agent:a64:dashboard:one", Now));
    }

    // Three thousand requests on three thousand invented keys leave sixty-four, and
    // keyed lookup keeps that fast: this would take minutes with a scan under the lock.
    [Fact]
    public void ThreeThousandInventedKeysLeaveSixtyFourAndFinishQuickly()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 3000; i++) Requested("f" + i, key: "agent:x" + i + ":dashboard:one");
        clock.Stop();

        var held = Enumerable.Range(0, 3000)
            .Count(i => OpenClawSessions.PendingFor("agent:x" + i + ":dashboard:one", new DateTimeOffset(Now)).Count == 1);

        Assert.Equal(64, held);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    // Unlisted keys are purged on every poll: Parse records every session the
    // gateway listed, and a pending approval on one it does not list, or on an
    // invented key, goes with its endings.
    [Fact]
    public void AnUnlistedKeyIsPurgedAfterThePollAndAListedOneIsKept()
    {
        var window = OrbweaverSettings.OpenClawActiveWithinMinutes;
        try
        {
            OrbweaverSettings.OpenClawActiveWithinMinutes = OrbweaverSettings.OpenClawActiveWithinAll;
            Requested("listed", key: Key);
            Requested("invented", key: OtherKey);
            Resolved("invented");

            OpenClawSessions.Parse(Json("{\"sessions\":[{\"key\":\"" + Key + "\",\"chatType\":\"direct\",\"lastActivityAt\":" + Ms(Now) + "}]}"), Now);
            OpenClawSessions.PurgeUnlistedApprovals();

            Assert.Equal("listed", Ids(Key));
            Assert.Equal("", Ids(OtherKey));
            Assert.Empty(OpenClawSessions.TakeEnded(OtherKey));

            // A quiet session the gateway lists is kept even outside a short window.
            OrbweaverSettings.OpenClawActiveWithinMinutes = 1;
            OpenClawSessions.Parse(Json("{\"sessions\":[{\"key\":\"" + Key + "\",\"chatType\":\"direct\",\"lastActivityAt\":" + Ms(Now.AddHours(-9)) + "}]}"), Now);
            OpenClawSessions.PurgeUnlistedApprovals();
            Assert.Equal("listed", Ids(Key));
        }
        finally
        {
            OrbweaverSettings.OpenClawActiveWithinMinutes = window;
        }
    }

    // Arrival order, never the gateway's own dates: a record carrying an old date
    // cannot pin itself first, so a hostile gateway cannot choose what is shown.
    [Fact]
    public void PendingIsInArrivalOrderWhateverTheGatewaysDatesSay()
    {
        var older = Row("z-older").Replace("\"createdAtMs\":" + Ms(Now), "\"createdAtMs\":" + Ms(Now.AddYears(-3)));
        OpenClawSessions.OnEvent("exec.approval.requested", Json(Row("b-first")), Now);
        OpenClawSessions.OnEvent("exec.approval.requested", Json(Row("a-second")), Now);
        OpenClawSessions.OnEvent("exec.approval.requested", Json(older), Now);

        Assert.Equal("b-first,a-second,z-older", Ids(Key));
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
    public void TheListAddsWhatBroadcastsNeverSaidAndCollapsesDuplicateRows()
    {
        var now = new DateTimeOffset(Now);
        var rows = OpenClawApprovals.ParseList(
            Json("[" + Row("a1") + "," + Row("a1") + "," + Row("stale", expires: Now.AddSeconds(-1)) + "]"), "exec", now);

        OpenClawSessions.ReplacePending("exec", rows, now);

        // The stale row is clamped to a second, so it is held and then gone.
        Assert.Equal("a1,stale", string.Join(",", Ids(Key).Split(',').OrderBy(i => i)));
        Assert.Equal("waiting", OpenClawSessions.StateFor(Key, Now));
        Assert.Equal("a1", string.Join(",", OpenClawSessions.PendingFor(Key, new DateTimeOffset(Now.AddSeconds(2))).Select(a => a.Id)));
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
        var held = approval ?? Approval(kind: kind);
        if (OpenClawSessions.PendingFor(held.SessionKey).All(a => a.Id != held.Id))
            Requested(held.Id, held.SessionKey, held.Kind);

        var (socket, gateway) = await ConnectedAsync(Approving, r =>
            r.Method == "approval.resolve" ? reply(r.Id) : null);
        using (gateway)
        {
            var outcome = await OpenClawSessions.ResolveApprovalAsync(chat ?? Chat(), held.Id, decision);
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
            var first = await OpenClawSessions.ResolveApprovalAsync(Chat(), Approval().Id, "deny");

            Assert.Equal(OpenClawApprovalResult.Failed, first.Result);
            Assert.Equal("missing scope: operator.approvals", first.Detail);
            Assert.Equal("a1", Ids(Key));

            failing = false;
            var second = await OpenClawSessions.ResolveApprovalAsync(Chat(), Approval().Id, "deny");

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
    public async Task ASecondAnswerForAnApprovalThatSettledSendsNothing()
    {
        Requested("a1");
        var (socket, gateway) = await ConnectedAsync(Approving, r =>
            r.Method == "approval.resolve" ? FakeGatewaySocket.Ok(r.Id, Json("{\"applied\":true}")) : null);
        using (gateway)
        {
            var first = await OpenClawSessions.ResolveApprovalAsync(Chat(), "a1", "deny");
            var second = await OpenClawSessions.ResolveApprovalAsync(Chat(), "a1", "allow-once");

            Assert.Equal(OpenClawApprovalResult.Answered, first.Result);

            // It settled: the second click is for an approval that is no longer
            // pending, and nothing is sent for it.
            Assert.Equal(OpenClawApprovalResult.NoLongerPending, second.Result);
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
            var outcome = await OpenClawSessions.ResolveApprovalAsync(Chat(), Approval().Id, decision);

            Assert.Equal(OpenClawApprovalResult.Failed, outcome.Result);
            Assert.Equal("that answer is not offered", outcome.Detail);
            Assert.Empty(Sent(socket));
        }
    }

    [Fact]
    public async Task AnApprovalAnsweredFromAnotherConversationsPanelNeverReachesTheWire()
    {
        Requested("a1");
        var (socket, gateway) = await ConnectedAsync(Approving);
        using (gateway)
        {
            var outcome = await OpenClawSessions.ResolveApprovalAsync(Chat(OtherKey), "a1", "deny");

            Assert.Equal(OpenClawApprovalResult.Failed, outcome.Result);
            Assert.Equal("this request belongs to another conversation", outcome.Detail);
            Assert.Empty(Sent(socket));
        }
    }

    [Fact]
    public async Task WithNoConnectionNothingIsSentAndTheIdIsNotSpent()
    {
        Requested("a1");
        OpenClawSessions.SetGatewayForTests(null);
        var outcome = await OpenClawSessions.ResolveApprovalAsync(Chat(), "a1", "deny");

        Assert.Equal(OpenClawApprovalResult.Failed, outcome.Result);
        Assert.Equal("not connected to the gateway", outcome.Detail);

        // Connected afterwards, the same id can still be answered.
        var (_, gateway) = await ConnectedAsync(Approving, r =>
            r.Method == "approval.resolve" ? FakeGatewaySocket.Ok(r.Id, Json("{\"applied\":true}")) : null);
        using (gateway)
        {
            Assert.Equal(
                OpenClawApprovalResult.Answered,
                (await OpenClawSessions.ResolveApprovalAsync(Chat(), "a1", "deny")).Result);
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

            // The session-scoped path too: its events, its replay, and the subscribe
            // that asks for it.
            SessionPending("s1");
            SessionTerminal("s1");
            OpenClawSessions.ApplyReplay(Key, Replay(false, "r1"), new DateTimeOffset(Now));
            await OpenClawSessions.MessagesRequestAsync(gateway, Key, true, CancellationToken.None);
            OpenClawSessions.ReleaseSessionSourced(Key);
            await OpenClawSessions.MessagesRequestAsync(gateway, Key, false, CancellationToken.None);

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

    // --- the subscribe's includeApprovals flag ---

    private static async Task<FakeGatewaySocket.Request> SubscribeFrameAsync(
        string[] scopes, bool wanted)
    {
        var (socket, gateway) = await ConnectedAsync(scopes);
        using (gateway)
        {
            Assert.Null(await OpenClawSessions.MessagesRequestAsync(
                gateway, "agent:a:dashboard:one", wanted, CancellationToken.None));

            return Assert.Single(Sent(socket));
        }
    }

    // On only for a device that chose to approve AND was granted the scope, and
    // only on a subscribe.
    [Fact]
    public async Task ASubscribeAsksForApprovalsOnlyWhenApprovingIsOnAndGranted()
    {
        var frame = await SubscribeFrameAsync(Approving, wanted: true);

        Assert.Equal("sessions.messages.subscribe", frame.Method);
        Assert.Equal(new[] { "key", "includeApprovals" }, frame.Params.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.True(frame.Params.GetProperty("includeApprovals").GetBoolean());
    }

    // The paired controls: each of the three missing pieces alone sends the frame
    // it always did.
    [Fact]
    public async Task WithoutTheSwitchTheScopeOrOnAnUnsubscribeTheFrameIsTheOldOne()
    {
        OrbweaverSettings.OpenClawApproveEnabled = false;
        var off = await SubscribeFrameAsync(Approving, wanted: true);
        Assert.Equal(new[] { "key" }, off.Params.EnumerateObject().Select(p => p.Name).ToArray());

        OrbweaverSettings.OpenClawApproveEnabled = true;
        var notGranted = await SubscribeFrameAsync(ReadOnly, wanted: true);
        Assert.Equal(new[] { "key" }, notGranted.Params.EnumerateObject().Select(p => p.Name).ToArray());

        var unsubscribe = await SubscribeFrameAsync(Approving, wanted: false);
        Assert.Equal("sessions.messages.unsubscribe", unsubscribe.Method);
        Assert.Equal(new[] { "key" }, unsubscribe.Params.EnumerateObject().Select(p => p.Name).ToArray());
    }

    // --- the session-scoped path: session.approval and the subscribe's replay ---
    //
    // Declared shapes, from the gateway's own schemas; see OpenClawApprovalsTests.

    private static string Snapshot(string id, string key = Key, string decisions = "[\"allow-once\",\"deny\"]") =>
        "{\"id\":\"" + id + "\",\"status\":\"pending\",\"urlPath\":\"/approve/" + id + "\"," +
        "\"createdAtMs\":" + Ms(Now) + ",\"expiresAtMs\":" + Ms(Now.AddMinutes(30)) + "," +
        "\"presentation\":{\"kind\":\"exec\",\"commandText\":\"ls -la /tmp\",\"agentId\":\"alpha\"," +
        "\"allowedDecisions\":" + decisions + "}}";

    private static void SessionPending(string id, string key = Key, string decisions = "[\"allow-once\",\"deny\"]") =>
        OpenClawSessions.OnEvent("session.approval", Json(
            "{\"sessionKey\":\"" + key + "\",\"updatedAtMs\":" + Ms(Now) + ",\"phase\":\"pending\",\"approval\":" +
            Snapshot(id, key, decisions) + "}"), Now);

    private static void SessionTerminal(string id, string key = Key, string status = "denied") =>
        OpenClawSessions.OnEvent("session.approval", Json(
            "{\"sessionKey\":\"" + key + "\",\"updatedAtMs\":" + Ms(Now) + ",\"phase\":\"terminal\",\"approval\":" +
            "{\"id\":\"" + id + "\",\"status\":\"" + status + "\",\"decision\":\"deny\",\"reason\":\"user\"}}"), Now);

    [Fact]
    public void ASessionApprovalEventMakesTheSessionWaitingAndATerminalOneEndsIt()
    {
        SessionPending("s1");

        Assert.Equal("s1", Ids(Key));
        Assert.Equal("waiting", OpenClawSessions.StateFor(Key, Now));

        SessionTerminal("s1");

        Assert.Equal("", Ids(Key));
        var ended = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal("resolved", ended.Cause);
        Assert.Equal("deny", ended.Decision);
        Assert.Equal("user", ended.Reason);
    }

    [Fact]
    public void ASessionApprovalEventIsIgnoredWithApprovingOffOrMalformedAndAPastExpiryIsShownBriefly()
    {
        OrbweaverSettings.OpenClawApproveEnabled = false;
        SessionPending("s1");
        Assert.Equal("", Ids(Key));

        OrbweaverSettings.OpenClawApproveEnabled = true;
        OpenClawSessions.OnEvent("session.approval", Json("{\"phase\":\"pending\"}"), Now);
        Assert.Equal("", Ids(Key));

        // A past expiry is clamped to a second from now, so it is shown for that
        // second rather than dropped unseen.
        OpenClawSessions.OnEvent("session.approval", Json(
            "{\"sessionKey\":\"" + Key + "\",\"phase\":\"pending\",\"approval\":" +
            Snapshot("late").Replace("\"expiresAtMs\":" + Ms(Now.AddMinutes(30)), "\"expiresAtMs\":" + Ms(Now.AddSeconds(-1))) + "}"), Now);
        Assert.Equal("late", Ids(Key));
        Assert.Empty(OpenClawSessions.PendingFor(Key, new DateTimeOffset(Now.AddSeconds(2))));

        // The control: the same event with the switch on and a live expiry is held.
        SessionPending("s1");
        Assert.Contains("s1", Ids(Key));
    }

    // The same approval met by the session path and by a broadcast is one record,
    // and a terminal event for an id this app never held is ignored.
    [Fact]
    public void TheSessionPathAndABroadcastAreOneRecordAndAnUnknownEndingIsIgnored()
    {
        Requested("a1");
        SessionPending("a1");

        Assert.Equal("a1", Ids(Key));

        SessionTerminal("never-held");
        Assert.Equal("a1", Ids(Key));
        Assert.Empty(OpenClawSessions.TakeEnded(Key));
    }

    // The point of the session path: a record the lists cannot see is not dropped
    // by the list poll, because the poll cannot see it. A record only the list
    // taught is, as before.
    [Fact]
    public void AListThatDoesNotShowASessionSourcedRecordDoesNotDropIt()
    {
        SessionPending("only-via-session");
        Requested("only-via-broadcast");

        var now = new DateTimeOffset(Now);
        OpenClawSessions.ReplacePending("exec", Array.Empty<OpenClawApproval>(), now);

        Assert.Equal("only-via-session", Ids(Key));
        Assert.Equal("gone", Assert.Single(OpenClawSessions.TakeEnded(Key)).Cause);
    }

    // A session path that meets a record the lists already held claims it.
    [Fact]
    public void ASessionEventForARecordTheListsAlreadyHeldClaimsItFromThePoll()
    {
        Requested("a1");
        SessionPending("a1");

        OpenClawSessions.ReplacePending("exec", Array.Empty<OpenClawApproval>(), new DateTimeOffset(Now));

        Assert.Equal("a1", Ids(Key));
    }

    // When the panel that taught it closes, what it taught goes back to the list to
    // judge, so an approval resolved while nobody listened does not stay amber.
    [Fact]
    public void ReleasingASessionHandsItsRecordsBackToTheList()
    {
        SessionPending("a1");
        OpenClawSessions.ReleaseSessionSourced(Key);
        OpenClawSessions.ReleaseSessionSourced("agent:nobody:main");

        OpenClawSessions.ReplacePending("exec", Array.Empty<OpenClawApproval>(), new DateTimeOffset(Now));

        Assert.Equal("", Ids(Key));
    }

    [Fact]
    public void ARestartForgetsWhichRecordsCameFromTheSessionPath()
    {
        SessionPending("a1");
        OpenClawSessions.Restart();

        // Re-learned from the list, the record is the list's to judge again.
        OpenClawSessions.ReplacePending(
            "exec", OpenClawApprovals.ParseList(Json("[" + Row("a1") + "]"), "exec", new DateTimeOffset(Now)),
            new DateTimeOffset(Now));
        OpenClawSessions.ReplacePending("exec", Array.Empty<OpenClawApproval>(), new DateTimeOffset(Now));

        Assert.Equal("", Ids(Key));
    }

    private static OpenClawApprovalReplay Replay(bool truncated, params string[] ids) =>
        new(ids.Select(i => OpenClawApprovals.ParseSnapshot(Json(Snapshot(i)), Key, new DateTimeOffset(Now))!).ToList(), truncated);

    // The replay is the authoritative set: what it lists is added, and what it
    // omits is gone, whatever else taught this app about it.
    [Fact]
    public void AReplayAddsWhatItListsAndRemovesWhatItOmits()
    {
        Requested("old");
        Requested("kept");

        OpenClawSessions.ApplyReplay(Key, Replay(false, "kept", "fresh"), new DateTimeOffset(Now));

        Assert.Equal("fresh,kept", string.Join(",", Ids(Key).Split(',').OrderBy(i => i)));
        Assert.Equal("old", Assert.Single(OpenClawSessions.TakeEnded(Key)).Approval.Id);
    }

    // A replay the gateway says it cut short cannot be trusted to omit: nothing is
    // removed on its say-so, though what it lists is still added.
    [Fact]
    public void ATruncatedReplayAddsButNeverRemoves()
    {
        Requested("held");

        OpenClawSessions.ApplyReplay(Key, Replay(true, "fresh"), new DateTimeOffset(Now));

        Assert.Equal("fresh,held", string.Join(",", Ids(Key).Split(',').OrderBy(i => i)));
        Assert.Empty(OpenClawSessions.TakeEnded(Key));
    }

    // An omitted record found past its expiry ended by expiring, and an expired or
    // duplicate row in the replay is not added.
    [Fact]
    public void AReplayOmittingAnExpiredRecordEndsItByExpiryAndSkipsExpiredAndDuplicateRows()
    {
        Requested("will-expire", expires: Now.AddMinutes(5));
        var later = new DateTimeOffset(Now.AddMinutes(6));
        var stale = OpenClawApprovals.ParseSnapshot(Json(Snapshot("stale")), Key, later)! with { ExpiresAt = later.AddSeconds(-1) };
        var live = OpenClawApprovals.ParseSnapshot(Json(Snapshot("live")), Key, later)! with { ExpiresAt = later.AddMinutes(5) };

        OpenClawSessions.ApplyReplay(Key, new OpenClawApprovalReplay(new[] { stale, live, live }, false), later);

        Assert.Equal("expired", Assert.Single(OpenClawSessions.TakeEnded(Key)).Cause);
        Assert.Equal("live", string.Join(",", OpenClawSessions.PendingFor(Key, later).Select(a => a.Id)));
    }

    // A replay for a session that held nothing, listing nothing, changes nothing.
    [Fact]
    public void AnEmptyReplayForAnEmptySessionChangesNothing()
    {
        OpenClawSessions.ApplyReplay(Key, Replay(false), new DateTimeOffset(Now));

        Assert.Equal("", Ids(Key));
        Assert.Empty(OpenClawSessions.TakeEnded(Key));
    }

    // --- the subscribe over a socket: replay in, session-sourced released ---

    [Fact]
    public async Task ASubscribeWithApprovalsIngestsTheReplayInItsAnswer()
    {
        var (_, gateway) = await ConnectedAsync(Approving, r =>
            r.Method == "sessions.messages.subscribe"
                ? FakeGatewaySocket.Ok(r.Id, Json(
                    "{\"subscribed\":true,\"key\":\"" + Key + "\",\"approvalReplay\":{\"sessionKey\":\"" + Key +
                    "\",\"updatedAtMs\":" + Ms(Now) + ",\"approvals\":[" + Snapshot("r1") + "],\"truncated\":false}}"))
                : null);
        using (gateway)
        {
            Assert.Null(await OpenClawSessions.MessagesRequestAsync(gateway, Key, true, CancellationToken.None));

            Assert.Equal("r1", Ids(Key));
            Assert.Equal("waiting", OpenClawSessions.StateFor(Key));
        }
    }

    // Without approvals asked for the answer carries no replay, and a subscribe
    // that was not for approvals ingests nothing.
    [Fact]
    public async Task ASubscribeWithoutApprovalsIngestsNothing()
    {
        OrbweaverSettings.OpenClawApproveEnabled = false;
        var (_, gateway) = await ConnectedAsync(Approving, r =>
            r.Method == "sessions.messages.subscribe"
                ? FakeGatewaySocket.Ok(r.Id, Json(
                    "{\"subscribed\":true,\"approvalReplay\":{\"approvals\":[" + Snapshot("r1") + "],\"truncated\":false}}"))
                : null);
        using (gateway)
        {
            await OpenClawSessions.MessagesRequestAsync(gateway, Key, true, CancellationToken.None);

            Assert.Equal("", Ids(Key));
        }
    }

    // An approvals subscribe whose answer has no replay (an older gateway) changes
    // nothing and is not a failure.
    [Fact]
    public async Task AnApprovalsSubscribeWhoseAnswerHasNoReplayChangesNothing()
    {
        Requested("held");
        var (_, gateway) = await ConnectedAsync(Approving);
        using (gateway)
        {
            Assert.Null(await OpenClawSessions.MessagesRequestAsync(gateway, Key, true, CancellationToken.None));

            Assert.Equal("held", Ids(Key));
        }
    }

    // Closing the panel hands what the session path taught back to the list.
    [Fact]
    public async Task AnUnsubscribeReleasesWhatTheSessionPathTaught()
    {
        SessionPending("a1");
        var (_, gateway) = await ConnectedAsync(Approving);
        using (gateway)
        {
            await OpenClawSessions.MessagesRequestAsync(gateway, Key, false, CancellationToken.None);

            OpenClawSessions.ReplacePending("exec", Array.Empty<OpenClawApproval>(), new DateTimeOffset(Now));
            Assert.Equal("", Ids(Key));
        }
    }

    // --- an answer the gateway lists as unavailable is never sent ---

    [Fact]
    public async Task AnAnswerTheGatewayListsAsUnavailableForThisRequestNeverReachesTheWire()
    {
        SessionPending("a1", decisions: "[\"deny\"]");
        var (socket, gateway) = await ConnectedAsync(Approving, r =>
            r.Method == "approval.resolve" ? FakeGatewaySocket.Ok(r.Id, Json("{\"applied\":true}")) : null);
        using (gateway)
        {
            var refused = await OpenClawSessions.ResolveApprovalAsync(Chat(), "a1", "allow-once");

            Assert.Equal(OpenClawApprovalResult.Failed, refused.Result);
            Assert.Equal("the gateway doesn't allow that answer for this request", refused.Detail);
            Assert.Empty(Sent(socket));

            // The control: deny is listed, and goes.
            var allowed = await OpenClawSessions.ResolveApprovalAsync(Chat(), "a1", "deny");
            Assert.Equal(OpenClawApprovalResult.Answered, allowed.Result);
            Assert.Single(Sent(socket));
        }
    }

    // A session waiting on an approval is exempt from the recency window like one
    // mid-run, however quiet the gateway says it is: the orb that is asking for a
    // person is the one that must not vanish. Nothing is stamped into a static to
    // achieve it; the state itself is what exempts it, and it ends with the approval.
    [Fact]
    public void AWaitingSessionIsExemptFromTheRecencyWindowUntilItsApprovalEnds()
    {
        var window = OrbweaverSettings.OpenClawActiveWithinMinutes;
        try
        {
            OrbweaverSettings.OpenClawActiveWithinMinutes = 60;
            var quiet = "{\"sessions\":[{\"key\":\"" + Key + "\",\"chatType\":\"direct\",\"lastActivityAt\":" +
                        Ms(Now.AddHours(-5)) + "}]}";

            Assert.Empty(OpenClawSessions.Parse(Json(quiet), Now).Sessions);

            Requested("a1");
            var kept = Assert.Single(OpenClawSessions.Parse(Json(quiet), Now).Sessions);
            Assert.Equal("waiting", kept.State);

            Resolved("a1");
            Assert.Empty(OpenClawSessions.Parse(Json(quiet), Now).Sessions);
        }
        finally
        {
            OrbweaverSettings.OpenClawActiveWithinMinutes = window;
        }
    }

    // --- a refusal is not-found only when it is exactly the gateway's not-found ---

    private static object Refusal(string id, string code, string message) =>
        FakeGatewaySocket.Error(id, code, message, null);

    // Ines's case: a transient refusal that merely contains "unknown" must not take a
    // real pending approval out of sight. It is a failure, the record stays, the id
    // is free, and the retry reaches the wire.
    [Fact]
    public async Task AnUnknownErrorThatIsNotTheGatewaysNotFoundKeepsTheRecordAndTheRetryReachesTheWire()
    {
        Requested("a1");
        var busy = true;
        var (socket, gateway) = await ConnectedAsync(Approving, r =>
            r.Method != "approval.resolve" ? null
            : busy ? Refusal(r.Id, "INVALID_REQUEST", "unknown error: gateway busy")
                   : FakeGatewaySocket.Ok(r.Id, Json("{\"applied\":true}")));
        using (gateway)
        {
            var first = await OpenClawSessions.ResolveApprovalAsync(Chat(), "a1", "deny");

            Assert.Equal(OpenClawApprovalResult.Failed, first.Result);
            Assert.Equal("unknown error: gateway busy", first.Detail);
            Assert.Equal("a1", Ids(Key));

            busy = false;
            var retry = await OpenClawSessions.ResolveApprovalAsync(Chat(), "a1", "deny");

            Assert.Equal(OpenClawApprovalResult.Answered, retry.Result);
            Assert.Equal(2, Sent(socket).Count);
        }
    }

    // The gateway's own not-found takes it off, releases the id, and a poll that
    // lists it again (the gateway disagreeing with itself) makes it answerable
    // again rather than stranding it behind a spent id.
    [Fact]
    public async Task AGenuineNotFoundTakesTheRecordOffAndAReListedOneIsAnswerableAgain()
    {
        Requested("a1");
        var gone = true;
        var (socket, gateway) = await ConnectedAsync(Approving, r =>
            r.Method != "approval.resolve" ? null
            : gone ? Refusal(r.Id, "INVALID_REQUEST", "unknown or expired approval id")
                   : FakeGatewaySocket.Ok(r.Id, Json("{\"applied\":true}")));
        using (gateway)
        {
            var first = await OpenClawSessions.ResolveApprovalAsync(Chat(), "a1", "deny");

            Assert.Equal(OpenClawApprovalResult.NoLongerPending, first.Result);
            Assert.Equal("", Ids(Key));

            gone = false;
            Requested("a1");
            Assert.Equal("a1", Ids(Key));

            var again = await OpenClawSessions.ResolveApprovalAsync(Chat(), "a1", "deny");
            Assert.Equal(OpenClawApprovalResult.Answered, again.Result);
            Assert.Equal(2, Sent(socket).Count);
        }
    }

    // --- an applied id is never re-added ---

    // A replayed request for an id whose resolve applied is ignored, whichever feed
    // brings it back: the prompt would show again and could not be answered.
    [Fact]
    public async Task AnIdWhoseAnswerAppliedIsNotReAddedByAnyFeed()
    {
        Requested("a1");
        var (socket, gateway) = await ConnectedAsync(Approving, r =>
            r.Method == "approval.resolve" ? FakeGatewaySocket.Ok(r.Id, Json("{\"applied\":true}")) : null);
        using (gateway)
        {
            await OpenClawSessions.ResolveApprovalAsync(Chat(), "a1", "deny");
            Assert.Equal("", Ids(Key));

            Requested("a1");
            SessionPending("a1");
            OpenClawSessions.ApplyReplay(Key, Replay(false, "a1"), new DateTimeOffset(Now));
            OpenClawSessions.ReplacePending(
                "exec", OpenClawApprovals.ParseList(Json("[" + Row("a1") + "]"), "exec", new DateTimeOffset(Now)),
                new DateTimeOffset(Now));

            Assert.Equal("", Ids(Key));
            Assert.Equal("idle", OpenClawSessions.StateFor(Key, Now));
            Assert.Single(Sent(socket));
        }
    }

    // Another reviewer having decided first (applied:false) spends the id too.
    [Fact]
    public async Task AnIdAnotherReviewerDecidedFirstIsNotReAddedEither()
    {
        Requested("a1");
        var (_, gateway) = await ConnectedAsync(Approving, r =>
            r.Method == "approval.resolve"
                ? FakeGatewaySocket.Ok(r.Id, Json("{\"applied\":false,\"approval\":{\"status\":\"denied\"}}"))
                : null);
        using (gateway)
        {
            await OpenClawSessions.ResolveApprovalAsync(Chat(), "a1", "allow-once");

            Requested("a1");

            Assert.Equal("", Ids(Key));
        }
    }

    // --- a click is bound to the approval it was drawn for ---

    // resolved(A) then requested(B): a click drawn for A sends nothing; a click for
    // B is the one that goes, with B's id on the frame.
    [Fact]
    public async Task AStaleClickForAReplacedApprovalSendsNothingAndTheNewOneIsAnswerable()
    {
        Requested("A");
        Resolved("A");
        Requested("B");
        var (socket, gateway) = await ConnectedAsync(Approving, r =>
            r.Method == "approval.resolve" ? FakeGatewaySocket.Ok(r.Id, Json("{\"applied\":true}")) : null);
        using (gateway)
        {
            var stale = await OpenClawSessions.ResolveApprovalAsync(Chat(), "A", "deny");
            Assert.Equal(OpenClawApprovalResult.NoLongerPending, stale.Result);
            Assert.Empty(Sent(socket));

            var current = await OpenClawSessions.ResolveApprovalAsync(Chat(), "B", "deny");
            Assert.Equal(OpenClawApprovalResult.Answered, current.Result);
            Assert.Equal("B", Sent(socket).Single().Params.GetProperty("id").GetString());
        }
    }

    [Fact]
    public async Task AClickForAnIdNeverHeldAnEvictedOneOrAnExpiredOneSendsNothing()
    {
        // Evicted: the ninth request on a session pushes the first out.
        for (var i = 0; i < 9; i++) Requested("e" + i);
        // Expired: arrived an hour ago, a past expiry clamped to a second after.
        OpenClawSessions.OnEvent("exec.approval.requested", Json(Row("old", OtherKey, expires: Now.AddHours(-2))), DateTime.UtcNow.AddHours(-1));

        var (socket, gateway) = await ConnectedAsync(Approving);
        using (gateway)
        {
            foreach (var id in new[] { "never-held", "e0", "old" })
            {
                var chat = Chat(id == "old" ? OtherKey : Key);
                var outcome = await OpenClawSessions.ResolveApprovalAsync(chat, id, "deny");

                Assert.Equal(OpenClawApprovalResult.NoLongerPending, outcome.Result);
            }

            Assert.Empty(Sent(socket));
        }
    }

    // The panel's own call, holding the record it drew, is the same answer.
    [Fact]
    public async Task TheRecordOverloadAnswersByTheRecordsId()
    {
        Requested("a1");
        var (socket, gateway) = await ConnectedAsync(Approving, r =>
            r.Method == "approval.resolve" ? FakeGatewaySocket.Ok(r.Id, Json("{\"applied\":true}")) : null);
        using (gateway)
        {
            var outcome = await OpenClawSessions.ResolveApprovalAsync(Chat(), Approval().Id, "deny");

            Assert.Equal(OpenClawApprovalResult.Answered, outcome.Result);
            Assert.Equal("a1", Sent(socket).Single().Params.GetProperty("id").GetString());
        }
    }

    // Two clicks while the first is still in flight: the id is spent the moment the
    // first lands, so the second is a duplicate and sends nothing. Driven through the
    // set directly because the in-memory socket answers too fast to race reliably.
    [Fact]
    public async Task ASecondClickWhileTheFirstIsInFlightIsADuplicate()
    {
        Requested("a1");
        var answered = (HashSet<string>)typeof(OpenClawSessions)
            .GetField("Answered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;
        var (socket, gateway) = await ConnectedAsync(Approving);
        using (gateway)
        {
            lock (typeof(OpenClawSessions).GetField("Gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!)
                answered.Add("a1");

            var outcome = await OpenClawSessions.ResolveApprovalAsync(Chat(), "a1", "deny");

            Assert.Equal(OpenClawApprovalResult.Duplicate, outcome.Result);
            Assert.Empty(Sent(socket));
        }
    }

    // --- an allow is never sent for what nobody saw whole or safely ---

    private static string HostileSnapshot(string id) =>
        Snapshot(id).Replace("\"commandText\":\"ls -la /tmp\"", "\"commandText\":\"ls \\u202E /tmp\"");

    [Fact]
    public async Task AnUnsafeRequestCanOnlyBeDeniedAndItsOrbIsStillAmber()
    {
        OpenClawSessions.OnEvent("session.approval", Json(
            "{\"sessionKey\":\"" + Key + "\",\"phase\":\"pending\",\"approval\":" + HostileSnapshot("u1") + "}"), Now);

        var held = Assert.Single(OpenClawSessions.PendingFor(Key, new DateTimeOffset(Now)));
        Assert.True(held.Unsafe);
        Assert.Equal("waiting", OpenClawSessions.StateFor(Key, Now));

        var (socket, gateway) = await ConnectedAsync(Approving, r =>
            r.Method == "approval.resolve" ? FakeGatewaySocket.Ok(r.Id, Json("{\"applied\":true}")) : null);
        using (gateway)
        {
            var allow = await OpenClawSessions.ResolveApprovalAsync(Chat(), "u1", "allow-once");
            Assert.Equal(OpenClawApprovalResult.Failed, allow.Result);
            Assert.Equal("this request can only be denied here", allow.Detail);
            Assert.Empty(Sent(socket));

            var deny = await OpenClawSessions.ResolveApprovalAsync(Chat(), "u1", "deny");
            Assert.Equal(OpenClawApprovalResult.Answered, deny.Result);
            Assert.Single(Sent(socket));
        }
    }

    [Fact]
    public async Task ACutCommandCanOnlyBeDeniedToo()
    {
        var long5000 = Snapshot("t1").Replace("\"commandText\":\"ls -la /tmp\"", "\"commandText\":\"" + new string('a', 5000) + "\"");
        OpenClawSessions.OnEvent("session.approval", Json(
            "{\"sessionKey\":\"" + Key + "\",\"phase\":\"pending\",\"approval\":" + long5000 + "}"), Now);

        var held = Assert.Single(OpenClawSessions.PendingFor(Key, new DateTimeOffset(Now)));
        Assert.True(held.CommandTruncated);
        Assert.Equal(5000, held.CommandLength);

        var (socket, gateway) = await ConnectedAsync(Approving);
        using (gateway)
        {
            var allow = await OpenClawSessions.ResolveApprovalAsync(Chat(), "t1", "allow-once");

            Assert.Equal(OpenClawApprovalResult.Failed, allow.Result);
            Assert.Empty(Sent(socket));
        }
    }

    // --- one bad row must never stall the feed ---

    // The poll over a socket, with one list holding a poisoned row, a hostile id and
    // a lone-surrogate command beside a good row. It returns, the good row is kept,
    // the hostile id is absent, and the poisoned row is held with its time clamped.
    [Fact]
    public async Task APoisonedListNeverStallsThePollAndTheGoodRowIsKept()
    {
        var poison = Row("poison").Replace("\"expiresAtMs\":" + Ms(Now.AddMinutes(30)), "\"expiresAtMs\":9223372036854775807");
        // A valid but hostile character: the in-memory socket cannot carry a lone
        // surrogate escape through a JsonElement, which the parser tests cover directly.
        var lone = Row("lone").Replace("\"command\":\"ls -la /tmp\"", "\"command\":\"ls \\u202E /tmp\"");
        var hostileId = Row("bad\\u202Eid");

        var (socket, gateway) = await ConnectedAsync(Approving, r => r.Method switch
        {
            "exec.approval.list" => FakeGatewaySocket.Ok(r.Id, Json("[" + Row("good") + "," + poison + "," + lone + "," + hostileId + "]")),
            _ => FakeGatewaySocket.Ok(r.Id, Json("[]")),
        });
        using (gateway)
        {
            await OpenClawSessions.PollApprovalsOnceAsync(gateway, CancellationToken.None);

            var held = OpenClawSessions.PendingFor(Key);
            Assert.Equal("good,lone,poison", string.Join(",", held.Select(a => a.Id).OrderBy(i => i)));
            Assert.True(held.Single(a => a.Id == "lone").Unsafe);
            Assert.False(held.Single(a => a.Id == "good").Unsafe);
            Assert.True(held.Single(a => a.Id == "poison").ExpiresAt <= DateTimeOffset.UtcNow.AddHours(24).AddMinutes(1));
            Assert.Equal(3, Sent(socket).Count);
        }
    }

    // The same poisoned feeds never keep sessions from updating: Parse runs on its own
    // input, and a poisoned approvals row cannot reach it.
    [Fact]
    public void ASessionListParsesWhateverTheApprovalsHeld()
    {
        Requested("poison", expires: Now.AddYears(1));
        OpenClawSessions.OnEvent("exec.approval.requested", Json(Row("lone").Replace("\"command\":\"ls -la /tmp\"", "\"command\":\"x\\ud800\"")), Now);
        var window = OrbweaverSettings.OpenClawActiveWithinMinutes;
        try
        {
            OrbweaverSettings.OpenClawActiveWithinMinutes = OrbweaverSettings.OpenClawActiveWithinAll;

            var parsed = OpenClawSessions.Parse(Json(
                "{\"sessions\":[{\"key\":\"" + Key + "\",\"chatType\":\"direct\",\"lastActivityAt\":" + Ms(Now) + "}," +
                "{\"key\":\"" + OtherKey + "\",\"chatType\":\"direct\",\"lastActivityAt\":" + Ms(Now) + "}]}"), Now);

            Assert.Equal(2, parsed.Sessions.Count);
            Assert.Equal("waiting", parsed.Sessions.Single(x => x.Key == Key).State);
            Assert.Equal("idle", parsed.Sessions.Single(x => x.Key == OtherKey).State);
        }
        finally
        {
            OrbweaverSettings.OpenClawActiveWithinMinutes = window;
        }
    }

    // --- no auto-approve, however the method name is built ---
    //
    // The single-site test above greps for the literal, so a sender whose method name
    // is assembled at run time ("approval." + "resolve") would pass it, and the
    // zero-frames replay only exercises the paths that exist. This one reads the
    // method-name argument of EVERY request the app sends to the gateway and refuses
    // anything that is not either a plain string literal other than a resolve, or one
    // of a short list of known constants. Adding a dynamically built request, or any
    // request that could be a resolve, makes it fail until someone looks.

    private static readonly string[] AllowedDynamicMethodNames =
    {
        "OpenClawApprovals.ListMethodFor(kind)",
        "OpenClawOrbActions.AbortMethod",
        "OpenClawOrbActions.PatchMethod",
        "wanted ? \"sessions.messages.subscribe\" : \"sessions.messages.unsubscribe\"",
    };

    // A line without its trailing // comment (a // inside a string literal is kept),
    // so a request mentioned in prose is not mistaken for one that is sent.
    private static string StripLineComment(string line)
    {
        var quoted = false;

        for (var i = 0; i < line.Length - 1; i++)
        {
            if (quoted)
            {
                if (line[i] == '\\') i++;
                else if (line[i] == '"') quoted = false;
            }
            else if (line[i] == '"') quoted = true;
            else if (line[i] == '/' && line[i + 1] == '/') return line[..i];
        }

        return line;
    }

    private static List<(string File, string Argument)> GatewayRequestMethodNames()
    {
        var found = new List<(string, string)>();
        var root = RepositoryRoot();
        var call = new System.Text.RegularExpressions.Regex(@"(?<![A-Za-z0-9_])RequestAsync\(");

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(
                         part => part is "tests" or "tools" or "obj" or "bin" or "TestResults")
                                 && Path.GetFileName(f).StartsWith("OpenClaw", StringComparison.Ordinal)))
        {
            var text = string.Join("\n", File.ReadAllLines(file).Select(StripLineComment));

            foreach (System.Text.RegularExpressions.Match m in call.Matches(text))
            {
                int depth = 0, i = m.Index + m.Length, j = i;
                var quoted = false;

                for (; j < text.Length; j++)
                {
                    var c = text[j];
                    if (quoted)
                    {
                        if (c == '\\') j++;
                        else if (c == '"') quoted = false;
                    }
                    else if (c == '"') quoted = true;
                    else if (c is '(' or '[' or '{') depth++;
                    else if (c is ')' or ']' or '}')
                    {
                        if (depth == 0) break;
                        depth--;
                    }
                    else if (c == ',' && depth == 0) break;
                }

                var argument = System.Text.RegularExpressions.Regex.Replace(text[i..j], @"\s+", " ").Trim();

                // A method declaration, not a call: "string method", "Type name".
                if (System.Text.RegularExpressions.Regex.IsMatch(argument, @"^[\w<>?\[\]]+ \w+$")) continue;

                found.Add((Path.GetFileName(file), argument));
            }
        }

        return found;
    }

    [Fact]
    public void EveryGatewayRequestsMethodNameIsALiteralOrAKnownConstantAndNeverAResolve()
    {
        var calls = GatewayRequestMethodNames();

        // The scan actually found the app's requests; an empty scan would pass by
        // looking at nothing.
        Assert.True(calls.Count > 15, $"only {calls.Count} requests found");
        Assert.Contains(calls, c => c.Argument == "\"approval.resolve\"");

        foreach (var (file, argument) in calls)
        {
            var literal = System.Text.RegularExpressions.Regex.IsMatch(argument, "^\"[^\"]*\"$");

            if (literal)
            {
                if (argument == "\"approval.resolve\"")
                    Assert.Equal("OpenClawSessions.Approvals.cs", file);
                else
                    Assert.DoesNotContain("resolve", argument, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Assert.True(
                    AllowedDynamicMethodNames.Contains(argument),
                    $"{file} builds a gateway method name at run time: {argument}");
            }
        }

        // And the allowed dynamic names can only ever be what they say.
        Assert.All(OpenClawApprovals.Kinds, k => Assert.EndsWith(".approval.list", OpenClawApprovals.ListMethodFor(k)));
        Assert.DoesNotContain("resolve", OpenClawOrbActions.AbortMethod, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resolve", OpenClawOrbActions.PatchMethod, StringComparison.OrdinalIgnoreCase);
    }

    // The method that sends a resolve is called from exactly one place outside its own
    // file: the chat session's answer to a click. Its own file adds only the
    // overload that forwards to it.
    [Fact]
    public void ResolveApprovalAsyncHasExactlyOneCallerOutsideItsOwnFile()
    {
        var root = RepositoryRoot();

        var callers = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(
                part => part is "tests" or "tools" or "obj" or "bin" or "TestResults"))
            .Where(f => Path.GetFileName(f) != "OpenClawSessions.Approvals.cs")
            .SelectMany(f => File.ReadAllLines(f).Select(line => (File: Path.GetFileName(f), Text: line)))
            .Where(x => x.Text.Contains("ResolveApprovalAsync(", StringComparison.Ordinal)
                        && !x.Text.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .ToList();

        var caller = Assert.Single(callers);
        Assert.Equal("OpenClawChatSession.cs", caller.File);
    }

    // --- the assembly-wide reset: what it does and what it must not ---

    // Around a test in the Settings collection it clears every piece of approvals
    // state, so one class's approvals cannot reach the next whatever either does.
    [Fact]
    public void TheResetHookClearsApprovalsAroundASettingsCollectionTest()
    {
        var hook = new ResetOpenClawApprovalsAttribute();
        var inSettings = typeof(OpenClawApprovalStateTests).GetMethod(nameof(TheResetHookClearsApprovalsAroundASettingsCollectionTest))!;

        Requested("a1");
        Resolved("a1");
        Requested("a2", key: OtherKey);
        hook.Before(inSettings);
        Assert.Equal("", Ids(Key));
        Assert.Equal("", Ids(OtherKey));
        Assert.Empty(OpenClawSessions.TakeEnded(Key));

        Requested("a3");
        hook.After(inSettings);
        Assert.Equal("", Ids(Key));
    }

    // And it leaves a test in any other collection alone. xunit runs separate
    // collections in parallel, and a reset fired from one in the middle of another
    // class's test would wipe that test's state: this is the property that was
    // missing when the first version reset around every test and made the suite
    // flaky.
    [Fact]
    public void TheResetHookLeavesATestInAnotherCollectionAlone()
    {
        var hook = new ResetOpenClawApprovalsAttribute();
        var elsewhere = typeof(OpenClawApprovalsTests).GetMethods().First(m => m.GetCustomAttributes(typeof(FactAttribute), false).Length > 0);

        Requested("a1");
        hook.Before(elsewhere);
        hook.After(elsewhere);

        Assert.Equal("a1", Ids(Key));
    }
}
