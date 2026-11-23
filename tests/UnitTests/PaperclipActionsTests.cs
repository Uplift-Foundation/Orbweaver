using System.Text.Json;
using Xunit;
using static Orbweaver.PaperclipSessions;

namespace Orbweaver.Tests;

// CB-427: answering a waiting agent, and waking, pausing and resuming it.
// Paths, bodies and verdicts are pure; the transport is FakePaperclip.
[Collection("Settings")]
public class PaperclipActionsTests
{
    private static PaperclipNeed Approval(string id = "ap1") => new("approval", id, "Hire QA", "http://x/CB/approvals/" + id);
    private static PaperclipNeed Action(string id = "ac1") => new("action", id, "Run tool", "http://x/CB/inbox/requests");
    private static PaperclipNeed Blocked() => new("blocked", "i1", "Blocked: CB-1 Fix", "http://x/CB/issues/i1");

    private static PaperclipResponse Resp(int code, string body = "", PaperclipHttpKind? kind = null) =>
        new(kind ?? PaperclipHttp.Classify(code), code, body);

    // ---- paths and bodies ----------------------------------------------------

    [Theory]
    [InlineData("approval", "ap1", true, "/api/approvals/ap1/approve")]
    [InlineData("approval", "ap1", false, "/api/approvals/ap1/reject")]
    [InlineData("action", "ac1", true, "/api/tool-gateway/action-requests/ac1/approve")]
    [InlineData("action", "ac1", false, "/api/tool-gateway/action-requests/ac1/decline")]
    [InlineData("approval", "a b", true, "/api/approvals/a%20b/approve")]
    public void Each_need_kind_has_its_own_route(string kind, string id, bool approve, string path) =>
        Assert.Equal(path, PaperclipActions.PathFor(new PaperclipNeed(kind, id, "t", "u"), approve));

    [Theory]
    [InlineData("blocked", "i1")]
    [InlineData("approval", "pending")]   // the placeholder built from an agent status alone
    [InlineData("approval", "")]
    [InlineData("other", "x")]
    public void A_need_with_nothing_to_answer_has_no_route(string kind, string id)
    {
        var n = new PaperclipNeed(kind, id, "t", "u");
        Assert.Null(PaperclipActions.PathFor(n, true));
        Assert.False(PaperclipActions.Answerable(n));
    }

    [Fact]
    public void The_approval_body_is_the_note_and_the_action_body_names_the_company()
    {
        Assert.Equal("{\"decisionNote\":\"From Orbweaver\"}", PaperclipActions.BodyFor(Approval(), "co"));
        Assert.Equal("co", JsonDocument.Parse(PaperclipActions.BodyFor(Action(), "co")).RootElement.GetProperty("companyId").GetString());
    }

    [Fact]
    public void Agent_routes_escape_the_id() =>
        Assert.Equal("/api/agents/a%2Fb/pause", PaperclipActions.AgentPath("a/b", "pause"));

    // ---- verdicts ------------------------------------------------------------

    [Theory]
    [InlineData(200, "Ok", "Done", null)]
    [InlineData(202, "Ok", "Done", null)]
    [InlineData(404, "NotFound", "AlreadyDecided", "Already answered in Paperclip")]
    [InlineData(401, "Unauthorized", "NeedsReconnect", "Reconnect to Paperclip to answer")]
    [InlineData(500, "ServerError", "Unreachable", "Can't reach Paperclip")]
    [InlineData(0, "Timeout", "Unreachable", "Can't reach Paperclip")]
    [InlineData(0, "Unreachable", "Unreachable", "Can't reach Paperclip")]
    [InlineData(422, "Other", "Refused", "Paperclip refused that")]
    [InlineData(403, "Unauthorized", "Refused", "Paperclip refused that")]
    public void Each_answer_maps_to_one_outcome(int code, string kind, string result, string? why)
    {
        // a 403 is classified Unauthorized by PaperclipHttp, but only 401 is a reconnect
        var o = PaperclipActions.Classify(new PaperclipResponse(Enum.Parse<PaperclipHttpKind>(kind), code, ""));
        Assert.Equal((Enum.Parse<PaperclipActionResult>(result), why), (o.Result, o.Why));
    }

    [Fact]
    public void A_4xx_uses_paperclips_own_short_sentence_and_never_a_long_one_or_the_body()
    {
        var o = PaperclipActions.Classify(Resp(422, "{\"error\":\"Only pending approvals can be rejected\",\"secret\":\"k\"}"));
        Assert.Equal("Only pending approvals can be rejected", o.Why);
        var cut = PaperclipActions.Classify(Resp(400, $"{{\"error\":\"{new string('x', 400)}\"}}")).Why!;
        Assert.Equal(PaperclipActions.WhyLimit, cut.Length);
        Assert.Equal("Paperclip refused that", PaperclipActions.Classify(Resp(400, "<html>")).Why);
        Assert.Equal("Paperclip refused that", PaperclipActions.Classify(Resp(400, "{\"error\":\"  \"}")).Why);
        Assert.Equal("Paperclip refused that", PaperclipActions.Classify(Resp(400, "[1]")).Why);
        Assert.Equal("Paperclip refused that", PaperclipActions.Classify(Resp(400, "")).Why);
    }

    [Fact]
    public void Settled_means_done_or_decided_elsewhere()
    {
        Assert.True(new PaperclipOutcome(PaperclipActionResult.Done).Settled);
        Assert.True(new PaperclipOutcome(PaperclipActionResult.AlreadyDecided).Settled);
        Assert.False(new PaperclipOutcome(PaperclipActionResult.Refused).Settled);
        Assert.False(new PaperclipOutcome(PaperclipActionResult.Unreachable).IsDone);
    }

    // ---- the calls ------------------------------------------------------------

    private static (FakePaperclip Api, PaperclipActions A, Func<int> Polls) Rig()
    {
        var api = new FakePaperclip(); var polls = 0;
        return (api, new PaperclipActions(api, "co1", () => polls++), () => polls);
    }

    [Fact]
    public async Task Approve_and_decline_post_to_the_routes_with_the_bodies_and_nudge_the_poll()
    {
        var (api, a, polls) = Rig();
        api.Posts["/api/approvals/ap1/approve"] = FakePaperclip.Ok("{\"status\":\"approved\"}");
        api.Posts["/api/approvals/ap1/reject"] = FakePaperclip.Ok("{\"status\":\"rejected\"}");
        api.Posts["/api/tool-gateway/action-requests/ac1/approve"] = FakePaperclip.Ok("{}");
        api.Posts["/api/tool-gateway/action-requests/ac1/decline"] = FakePaperclip.Ok("{}");

        Assert.True((await a.DecideAsync(Approval(), true, default)).IsDone);
        Assert.Equal("{\"decisionNote\":\"From Orbweaver\"}", api.LastPostBody);
        Assert.True((await a.DecideAsync(Approval(), false, default)).IsDone);
        Assert.True((await a.DecideAsync(Action(), true, default)).IsDone);
        Assert.Contains("co1", api.LastPostBody);
        Assert.True((await a.DecideAsync(Action(), false, default)).IsDone);
        Assert.Equal(4, api.Calls.Count);
        Assert.Equal(4, polls());
    }

    [Fact]
    public async Task A_422_on_an_approval_that_is_no_longer_pending_is_already_decided()
    {
        var (api, a, polls) = Rig();
        api.Posts["/api/approvals/ap1/reject"] = new(PaperclipHttpKind.Other, 422, "{\"error\":\"Only pending or revision requested approvals can be rejected\"}");
        api.Gets["/api/approvals/ap1"] = FakePaperclip.Ok("{\"status\":\"approved\"}");
        var o = await a.DecideAsync(Approval(), false, default);
        Assert.Equal((PaperclipActionResult.AlreadyDecided, "Already answered in Paperclip"), (o.Result, o.Why));
        Assert.Equal(1, polls());
    }

    [Fact]
    public async Task A_422_on_an_approval_that_is_still_pending_is_a_refusal_with_its_sentence()
    {
        var (api, a, polls) = Rig();
        api.Posts["/api/approvals/ap1/approve"] = new(PaperclipHttpKind.Other, 422, "{\"error\":\"Needs a note\"}");
        api.Gets["/api/approvals/ap1"] = FakePaperclip.Ok("{\"status\":\"pending\"}");
        var o = await a.DecideAsync(Approval(), true, default);
        Assert.Equal((PaperclipActionResult.Refused, "Needs a note"), (o.Result, o.Why));
        Assert.Equal(0, polls());
    }

    [Fact]
    public async Task When_the_item_cannot_be_re_read_a_422_stays_a_refusal_and_a_401_never_re_reads()
    {
        var (api, a, _) = Rig();
        api.Posts["/api/approvals/ap1/approve"] = new(PaperclipHttpKind.Other, 422, "{}");
        // no GET scripted: the fake answers 404, which is not a status to act on
        Assert.Equal(PaperclipActionResult.Refused, (await a.DecideAsync(Approval(), true, default)).Result);
        api.Gets["/api/approvals/ap1"] = FakePaperclip.Ok("not json");
        Assert.Equal(PaperclipActionResult.Refused, (await a.DecideAsync(Approval(), true, default)).Result);

        var (api2, b, _) = Rig();
        api2.Posts["/api/approvals/ap1/approve"] = new(PaperclipHttpKind.Unauthorized, 401, "");
        Assert.Equal(PaperclipActionResult.NeedsReconnect, (await b.DecideAsync(Approval(), true, default)).Result);
        Assert.DoesNotContain(api2.Calls, c => c.StartsWith("GET"));

        // a tool action is never re-read: a 409 is already decided, any other 4xx a refusal
        var (api3, c3, _) = Rig();
        api3.Posts["/api/tool-gateway/action-requests/ac1/approve"] = new(PaperclipHttpKind.Other, 400, "{\"error\":\"x\"}");
        Assert.Equal(PaperclipActionResult.Refused, (await c3.DecideAsync(Action(), true, default)).Result);
        api3.Posts["/api/tool-gateway/action-requests/ac1/approve"] = new(PaperclipHttpKind.Other, 409, "{}");
        Assert.Equal(PaperclipActionResult.AlreadyDecided, (await c3.DecideAsync(Action(), true, default)).Result);
        Assert.DoesNotContain(api3.Calls, c => c.StartsWith("GET"));
    }

    [Fact]
    public async Task A_need_that_cannot_be_answered_here_makes_no_call()
    {
        var (api, a, polls) = Rig();
        var o = await a.DecideAsync(Blocked(), true, default);
        Assert.Equal(PaperclipActionResult.Refused, o.Result);
        Assert.Empty(api.Calls);
        Assert.Equal(0, polls());
    }

    [Fact]
    public async Task Wake_pause_and_resume_post_to_the_agent()
    {
        var (api, a, polls) = Rig();
        api.Posts["/api/agents/a1/wakeup"] = FakePaperclip.Ok("{}");
        api.Posts["/api/agents/a1/pause"] = FakePaperclip.Ok("{}");
        Assert.True((await a.WakeAsync("a1", default)).IsDone);
        Assert.Equal("{\"source\":\"on_demand\"}", api.LastPostBody);
        Assert.True((await a.PauseAsync("a1", default)).IsDone);
        // no route scripted: the fake answers 404, and an agent that is gone is a refusal
        Assert.Equal(PaperclipActionResult.Refused, (await a.ResumeAsync("a1", default)).Result);
        Assert.Equal(["POST /api/agents/a1/wakeup", "POST /api/agents/a1/pause", "POST /api/agents/a1/resume"], api.Calls);
        Assert.Equal(2, polls());
    }

    [Fact]
    public async Task The_default_nudge_is_the_polls()
    {
        while (TakeNudge()) { }
        var api = new FakePaperclip();
        api.Posts["/api/agents/a1/pause"] = FakePaperclip.Ok("{}");
        await new PaperclipActions(api, "co1").PauseAsync("a1", default);
        Assert.True(TakeNudge());
    }

    [Fact]
    public void Actions_can_be_replaced_and_the_live_ones_exist_only_with_a_company()
    {
        var fake = new Fake();
        PaperclipSessions.Actions = fake;
        try { Assert.Same(fake, PaperclipSessions.Actions); }
        finally { PaperclipSessions.Actions = null; }
        // no config: switched off, so nothing to act with
        Assert.Null(PaperclipSessions.Actions);
    }

    private sealed class Fake : IPaperclipActions
    {
        public Task<PaperclipOutcome> DecideAsync(PaperclipNeed need, bool approve, CancellationToken ct) => Task.FromResult(default(PaperclipOutcome));
        public Task<PaperclipOutcome> WakeAsync(string agentId, CancellationToken ct) => Task.FromResult(default(PaperclipOutcome));
        public Task<PaperclipOutcome> PauseAsync(string agentId, CancellationToken ct) => Task.FromResult(default(PaperclipOutcome));
        public Task<PaperclipOutcome> ResumeAsync(string agentId, CancellationToken ct) => Task.FromResult(default(PaperclipOutcome));
    }

    // ---- tool actions, from the captured fixtures (plan 13.1 row 12) ------------------

    [Fact]
    public void The_captured_pending_list_parses_to_its_real_rows()
    {
        var rows = PaperclipApi.ParseActionRequests(PaperclipFx.Read("native/tool-actions/action-requests-pending.json"));
        Assert.NotEmpty(rows);
        var r = rows[0];
        Assert.Equal("00000000-0000-4000-8000-000000000001", r.Id);
        Assert.Null(r.AgentId);                       // a test-call request names no agent
        Assert.Equal("create-item", r.Summary);       // toolTitle is null: the part after the last colon
        Assert.Equal("create_item \u00b7 Title: Orbweaver measurement 3", r.Preview);   // ** removed
        Assert.Equal(new DateTime(2026, 11, 23, 18, 58, 43, 830, DateTimeKind.Utc), r.CreatedAt);
        Assert.Equal(r.CreatedAt!.Value.AddHours(1).AddMilliseconds(211), r.ExpiresAt);
    }

    [Fact]
    public void A_tool_title_beats_the_tool_name_and_a_decided_row_is_skipped_and_the_agent_order_holds()
    {
        var json = "{\"actionRequests\":[" +
            "{\"request\":{\"id\":\"1\",\"status\":\"pending\",\"requestedByAgentId\":\"A\"},\"toolTitle\":\"Create item\",\"toolName\":\"x:y\",\"requestedByAgentId\":\"B\"}," +
            "{\"request\":{\"id\":\"2\",\"status\":\"failed\"},\"toolName\":\"x:y\"}," +
            "{\"request\":{\"id\":\"3\"},\"toolName\":\"x:\",\"requestedByAgentId\":\"B\"}," +
            "{\"request\":{\"status\":\"pending\"}}," +
            "{\"request\":5,\"id\":\"4\",\"tool\":\"flat\"}]}";
        var r = PaperclipApi.ParseActionRequests(json);
        Assert.Equal(new[] { "1", "3", "4" }, r.Select(x => x.Id).ToArray());
        Assert.Equal(new string?[] { "A", "B", null }, r.Select(x => x.AgentId).ToArray());
        Assert.Equal(new string?[] { "Create item", null, "flat" }, r.Select(x => x.Summary).ToArray());
    }

    [Fact]
    public void A_long_preview_is_cut_and_a_need_carries_what_the_row_had()
    {
        var long_ = new string('a', 5000);
        var json = $"{{\"actionRequests\":[{{\"request\":{{\"id\":\"1\",\"previewMarkdown\":\"{long_}\"}},\"toolTitle\":\"T\"}}]}}";
        Assert.Equal(PaperclipApi.PreviewLimit, PaperclipApi.ParseActionRequests(json)[0].Preview!.Length);

        var raw = new PaperclipApi.RawActionRequest("a1", null, "Tool", "preview", DateTime.UnixEpoch, DateTime.UnixEpoch.AddHours(1));
        var agent = new PaperclipApi.RawAgent("lead", "Lead", "ceo", "", "idle", null, "x", 0, 0, null, null, null);
        var agents = PaperclipApi.Compose(new PaperclipApi.ComposeInput([agent], [], [], [], [raw], [], new Dictionary<string, string>(),
            "http://p", "CB", DateTime.UnixEpoch));
        var need = Assert.Single(agents[0].Needs);     // no agent named: the company's lead
        Assert.Equal(("action", "Tool", "preview", DateTime.UnixEpoch, DateTime.UnixEpoch.AddHours(1)),
            (need.Kind, need.Title, need.Summary, need.CreatedAt, need.ExpiresAt));

        var ap = PaperclipApi.ParseApprovals("[{\"id\":\"x\",\"type\":\"hire_agent\",\"payload\":{\"title\":\"Hire\",\"summary\":\"Why\"},\"createdAt\":\"2026-11-23T10:00:00Z\"}]")[0];
        Assert.Equal(("Why", new DateTime(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc)), (ap.Summary, ap.CreatedAt));
    }

    [Theory]
    [InlineData("action-approve-200-failed", 200, true, "Done", "Approved, but the tool failed")]
    [InlineData("action-approve-again-200", 200, true, "Done", "Approved, but the tool failed")]   // the same row, which reads failed
    [InlineData("action-decline-200", 200, false, "Done", null)]
    [InlineData("action-decline-again-200", 200, false, "Done", null)]
    [InlineData("action-approve-after-decline-409", 409, true, "AlreadyDecided", "Already answered in Paperclip")]
    [InlineData("action-decline-after-approve-409", 409, false, "AlreadyDecided", "Already answered in Paperclip")]
    [InlineData("action-approve-unknown-404", 404, true, "Refused", "This request no longer exists in Paperclip")]
    [InlineData("action-approve-no-company-400", 400, true, "Refused", "companyId is required")]
    public async Task Each_captured_tool_action_answer_maps_to_its_outcome(string fixture, int code, bool approve, string result, string? why)
    {
        var (api, a, _) = Rig();
        api.Posts["/api/tool-gateway/action-requests/ac1/" + (approve ? "approve" : "decline")] =
            new(PaperclipHttp.Classify(code), code, PaperclipFx.Read("native/tool-actions/" + fixture + ".json"));
        var o = await a.DecideAsync(Action(), approve, default);
        Assert.Equal((Enum.Parse<PaperclipActionResult>(result), why), (o.Result, o.Why));
        Assert.DoesNotContain(api.Calls, c => c.StartsWith("GET"));   // a tool action is never re-read
    }

    [Fact]
    public async Task A_404_is_answered_elsewhere_for_an_approval_but_not_for_a_tool_action()
    {
        // An approval's 404 means the item is gone. A tool action's 404 is also what a
        // Paperclip without that route, or a wrong company id, says, so claiming that
        // someone else decided would be false.
        var (api, a, polls) = Rig();
        api.Posts["/api/approvals/ap1/approve"] = new(PaperclipHttpKind.NotFound, 404, "");
        Assert.Equal(PaperclipActionResult.AlreadyDecided, (await a.DecideAsync(Approval(), true, default)).Result);
        api.Posts["/api/tool-gateway/action-requests/ac1/approve"] = new(PaperclipHttpKind.NotFound, 404, "{\"reasonCode\":\"action_request_not_found\"}");
        var o = await a.DecideAsync(Action(), true, default);
        Assert.Equal((PaperclipActionResult.Refused, "This request no longer exists in Paperclip"), (o.Result, o.Why));
        Assert.Equal(1, polls());      // only the approval's answer nudged the poll
    }

    [Fact]
    public async Task A_declined_request_that_reads_failed_is_not_reported_as_a_tool_failure()
    {
        var (api, a, _) = Rig();
        api.Posts["/api/tool-gateway/action-requests/ac1/decline"] = FakePaperclip.Ok("{\"status\":\"failed\"}");
        Assert.Null((await a.DecideAsync(Action(), false, default)).Why);
    }

    [Fact]
    public async Task A_wake_is_noted_before_the_call_and_the_other_verbs_are_not()
    {
        var (api, a, _) = Rig();
        var before = DateTime.UtcNow;
        await a.WakeAsync("wake-me", default);
        await a.PauseAsync("not-woken", default);
        var wakes = PaperclipSessions.Wakes();
        Assert.True(wakes["wake-me"] >= before);
        Assert.False(wakes.ContainsKey("not-woken"));
        // the returned map is a copy: changing it changes nothing
        ((Dictionary<string, DateTime>)wakes).Clear();
        Assert.True(PaperclipSessions.Wakes().ContainsKey("wake-me"));
    }

    // ---- the nudge ------------------------------------------------------------

    [Fact]
    public void A_nudge_makes_every_cadence_due_and_forgets_a_backoff()
    {
        var t = DateTime.UtcNow;
        var next = Nudged(PollState.Initial with { LastFast = t, LastSlow = t, NextAttempt = t.AddSeconds(30), CompanyId = "c" });
        Assert.Null(next.LastFast); Assert.Null(next.LastSlow); Assert.Null(next.NextAttempt);
        Assert.Equal("c", next.CompanyId);
    }

    [Fact]
    public async Task PollNow_cuts_the_wait_short_once_however_many_times_it_is_asked()
    {
        while (TakeNudge()) { }
        PollNow(); PollNow(); PollNow();
        Assert.True(await WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
        // consumed: the next wait runs its course
        Assert.False(await WaitAsync(TimeSpan.FromMilliseconds(30), CancellationToken.None));
        Assert.False(TakeNudge());
    }

    [Fact]
    public async Task PollNow_during_a_wait_wakes_it()
    {
        while (TakeNudge()) { }
        var wait = WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        PollNow();
        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_stale_signal_with_no_nudge_does_not_cut_a_wait_short()
    {
        while (TakeNudge()) { }
        PollNow();
        Assert.True(TakeNudge());              // the nudge is consumed elsewhere; the signal count stays
        Assert.False(await WaitAsync(TimeSpan.FromMilliseconds(60), CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_wait_throws_so_the_loop_can_end()
    {
        while (TakeNudge()) { }
        using var cts = new CancellationTokenSource(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitAsync(TimeSpan.FromSeconds(30), cts.Token));
    }

    // ---- CB-425: the poll-seen mark ---------------------------------------------------

    [Fact]
    public void The_earliest_unrendered_change_is_kept_and_taken_once()
    {
        TakeChangeSeen();
        Assert.Null(TakeChangeSeen());
        var t = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        MarkChanged(t);
        MarkChanged(t.AddSeconds(1));
        Assert.Equal(t, TakeChangeSeen());
        Assert.Null(TakeChangeSeen());
    }

    [Fact]
    public void Publishing_a_changed_picture_marks_the_change_and_an_unchanged_one_does_not()
    {
        TakeChangeSeen();
        var agents = new List<PaperclipAgent>();
        Publish(agents, PaperclipLink.Connected, "CB-425 first " + Guid.NewGuid());
        Assert.NotNull(TakeChangeSeen());
        var status = "CB-425 same";
        Publish(agents, PaperclipLink.Connected, status);
        TakeChangeSeen();
        Publish(agents, PaperclipLink.Connected, status);          // identical: nothing drawn differently
        Assert.Null(TakeChangeSeen());
    }

    // ---- Nadia's batch -------------------------------------------------------------------

    [Fact]
    public async Task A_404_on_an_agent_verb_is_a_refusal_that_says_the_agent_is_gone_and_does_not_nudge()
    {
        var (api, a, polls) = Rig();      // nothing scripted: the fake answers 404
        foreach (var o in new[] { await a.WakeAsync("a1", default), await a.PauseAsync("a1", default), await a.ResumeAsync("a1", default) })
            Assert.Equal((PaperclipActionResult.Refused, "That agent is no longer in Paperclip"), (o.Result, o.Why));
        Assert.Equal(0, polls());
    }

    [Fact]
    public void An_answer_gets_far_more_time_than_the_poll_and_a_wake_inside_thirty_seconds_is_recent()
    {
        Assert.True(PaperclipSessions.ActionTimeout >= TimeSpan.FromSeconds(15));
        Assert.True(PaperclipSessions.ActionTimeout > PaperclipHttp.RequestTimeout);
        var t = DateTime.UtcNow;
        PaperclipSessions.NoteWake("recent-1", t);
        Assert.True(PaperclipSessions.WokeRecently("recent-1", new DateTimeOffset(t.AddSeconds(29))));
        Assert.True(PaperclipSessions.WokeRecently("recent-1", new DateTimeOffset(t.AddSeconds(30))));
        Assert.False(PaperclipSessions.WokeRecently("recent-1", new DateTimeOffset(t.AddSeconds(31))));
        Assert.False(PaperclipSessions.WokeRecently("recent-1", new DateTimeOffset(t.AddSeconds(-1))));
        Assert.False(PaperclipSessions.WokeRecently("never-woken", new DateTimeOffset(t)));
    }

    // ---- the orb's rules -------------------------------------------------------

    private static SessionStatus Agent(string status, string state = "idle", OrbPresence p = OrbPresence.Present, params PaperclipNeed[] needs) =>
        new()
        {
            Source = SessionSource.Paperclip, Url = "http://127.0.0.1:3100/CB/agents/a", PaperclipAgentId = "a",
            PaperclipAgentStatus = status, State = state, Presence = p, Needs = needs,
        };

    [Fact]
    public void An_agent_paused_for_budget_is_offered_nothing_and_one_paused_by_hand_only_resume()
    {
        var budget = Agent("paused"); budget.Mark = PaperclipMark.OverBudget;
        var o = PaperclipOrb.Offer(budget, PaperclipLink.Connected);
        Assert.False(o.Wake || o.Pause || o.Resume);
        var hand = Agent("paused"); hand.Mark = PaperclipMark.Paused;
        var h = PaperclipOrb.Offer(hand, PaperclipLink.Connected);
        Assert.Equal((false, false, true), (h.Wake, h.Pause, h.Resume));
        var gone = Agent("terminated");
        Assert.False(PaperclipOrb.Offer(gone, PaperclipLink.Connected).Wake);
        // awaiting its own approval: Paperclip has not approved the agent yet
        var pending = Agent("pending_approval", "waiting");
        var p = PaperclipOrb.Offer(pending, PaperclipLink.Connected);
        Assert.False(p.Wake);
        Assert.True(p.Pause);
    }

    [Theory]
    [InlineData("idle", "idle", true, true, false)]        // wake, pause
    [InlineData("running", "generating", false, true, false)]  // pause only
    [InlineData("paused", "idle", false, false, true)]     // resume only
    [InlineData("pending_approval", "waiting", false, true, false)]
    [InlineData("", "idle", false, false, false)]          // parked: no status
    public void Wake_pause_and_resume_show_only_where_they_apply(string status, string state, bool wake, bool pause, bool resume)
    {
        var o = PaperclipOrb.Offer(Agent(status, state), PaperclipLink.Connected);
        Assert.Equal((wake, pause, resume), (o.Wake, o.Pause, o.Resume));
    }

    [Fact]
    public void Pause_is_not_offered_to_an_agent_in_error()
    {
        // Paperclip clears an error when a pause is lifted: Orbweaver must not edit
        // an agent's state as a side effect. Wake stays (it is a run, not an edit).
        var o = PaperclipOrb.Offer(Agent("error"), PaperclipLink.Connected);
        Assert.Equal((true, false, false), (o.Wake, o.Pause, o.Resume));
    }

    [Theory]
    [InlineData("Unreachable")]
    [InlineData("NeedsReconnect")]
    [InlineData("Connecting")]
    public void No_action_is_offered_while_the_link_is_not_live(string link)
    {
        var o = PaperclipOrb.Offer(Agent("idle"), Enum.Parse<PaperclipLink>(link));
        Assert.False(o.Wake || o.Pause || o.Resume);
    }

    [Fact]
    public void An_agent_with_no_id_or_a_row_that_is_not_paperclip_offers_no_action()
    {
        var noId = Agent("idle"); noId.PaperclipAgentId = "";
        Assert.False(PaperclipOrb.Offer(noId, PaperclipLink.Connected).Pause);
        var other = Agent("idle"); other.Source = SessionSource.ClaudeCode;
        Assert.Equal(default, PaperclipOrb.Offer(other, PaperclipLink.Connected));
    }

    [Fact]
    public void A_click_opens_the_panel_only_for_an_amber_orb_with_something_to_answer()
    {
        var amber = OrbPresence.NeedsInput;
        Assert.True(PaperclipOrb.ShowsNeeds(Agent("idle", "waiting", amber, Approval()), PaperclipLink.Connected));
        Assert.True(PaperclipOrb.ShowsNeeds(Agent("idle", "waiting", amber, Blocked(), Action()), PaperclipLink.Connected));
        // only a blocked task: the link, as before
        Assert.False(PaperclipOrb.ShowsNeeds(Agent("idle", "waiting", amber, Blocked()), PaperclipLink.Connected));
        // not amber, link not live, not paperclip, no status at all
        Assert.False(PaperclipOrb.ShowsNeeds(Agent("idle", "idle", OrbPresence.Present, Approval()), PaperclipLink.Connected));
        Assert.False(PaperclipOrb.ShowsNeeds(Agent("idle", "waiting", amber, Approval()), PaperclipLink.Unreachable));
        var other = Agent("idle", "waiting", amber, Approval()); other.Source = SessionSource.ClaudeCode;
        Assert.False(PaperclipOrb.ShowsNeeds(other, PaperclipLink.Connected));
        Assert.False(PaperclipOrb.ShowsNeeds(null, PaperclipLink.Connected));
    }
}
