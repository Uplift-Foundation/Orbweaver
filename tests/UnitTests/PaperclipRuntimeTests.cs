using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-442: a Paperclip agent's runtime permission prompt as a "needs you". The parser reads a run's events,
/// Compose turns what is pending into a need on the run's agent, PaperclipActions resolves it on its run, and
/// the prompt frame carries "Allow once" / "Deny" under the same option keys the phone already answers.
/// </summary>
public class PaperclipRuntimeTests
{
    private static string Events => PaperclipFx.Read("native/runtime/events-mixed.json");

    // ---- the parser ------------------------------------------------------------

    [Fact]
    public void Only_a_request_whose_latest_event_is_created_is_pending_and_it_carries_its_tool_and_turn()
    {
        var rows = PaperclipApi.ParseRuntimeRequests("run1", Events);
        Assert.Equal(["r-open", "r-admin", "r-cancel-only"], rows.Select(r => r.RequestId).ToArray());
        var open = rows[0];
        Assert.Equal(("run1", "t1", "Create probe directory", "Bash", "permission_approval"),
            (open.RunId, open.TurnId, open.Prompt, open.ToolName, open.RequestKind));
        Assert.True(open.Answerable);
    }

    // The real run (scrubbed): created at seq 49, then resolved by a decline at seq 51, whose requestId is at prpEvent.payload.requestId.
    [Fact]
    public void The_real_run_is_pending_until_its_resolved_event_and_the_tool_joins_through_the_item_id()
    {
        var pending = Assert.Single(PaperclipApi.ParseRuntimeRequests("run-1", PaperclipFx.Read("native/runtime/events-real-pending.json")));
        Assert.Equal(("request-1", "turn-1", "Create probe directory", "Bash", "permission_approval", true),
            (pending.RequestId, pending.TurnId, pending.Prompt, pending.ToolName, pending.RequestKind, pending.Answerable));
        Assert.Empty(PaperclipApi.ParseRuntimeRequests("run-1", PaperclipFx.Read("native/runtime/events-real-resolved.json")));
    }

    [Fact]
    public void A_request_of_another_kind_or_without_a_deny_choice_shows_but_is_not_answerable()
    {
        var rows = PaperclipApi.ParseRuntimeRequests("run1", Events);
        Assert.False(rows.Single(r => r.RequestId == "r-admin").Answerable);
        Assert.False(rows.Single(r => r.RequestId == "r-cancel-only").Answerable);
    }

    [Fact]
    public void A_later_event_wins_by_seq_even_when_it_comes_earlier_in_the_file()
    {
        var json = "[{\"seq\":9,\"eventType\":\"runtime_request.resolved\",\"payload\":{\"prpEvent\":{\"payload\":{\"requestId\":\"r\"}}}}," +
                   "{\"seq\":3,\"eventType\":\"runtime_request.created\",\"payload\":{\"prpEvent\":{\"turnId\":\"t\",\"payload\":{\"request\":{\"requestId\":\"r\"}}}}}]";
        Assert.Empty(PaperclipApi.ParseRuntimeRequests("run", json));
    }

    [Fact]
    public void An_events_object_is_read_like_a_bare_array_and_junk_is_nothing()
    {
        var wrapped = "{\"events\":" + Events + "}";
        Assert.Equal(3, PaperclipApi.ParseRuntimeRequests("r", wrapped).Count);
        Assert.Empty(PaperclipApi.ParseRuntimeRequests("r", "{\"events\":5}"));
        Assert.Empty(PaperclipApi.ParseRuntimeRequests("r", "not json"));
        Assert.Empty(PaperclipApi.ParseRuntimeRequests("r", null));
        Assert.Empty(PaperclipApi.ParseRuntimeRequests("r", "[{\"eventType\":\"runtime_request.created\",\"payload\":{}}]"));
    }

    // ---- Compose ----------------------------------------------------------------

    private static PaperclipApi.RawAgent Raw(string id) =>
        new(id, "Ledger bot", "r", "", "running", null, "claude_local", 0, 0, null, null, null);

    private static PaperclipAgent Compose(params PaperclipApi.RawRuntimeRequest[] rr) =>
        PaperclipApi.Compose(new PaperclipApi.ComposeInput([Raw("ag1")], [new("run1", "ag1", "running", null, null, null, null)],
            [], [], [], [], new Dictionary<string, string>(), "http://pc", "EX", DateTime.UtcNow, rr)).Single();

    [Fact]
    public void A_pending_runtime_request_makes_its_agent_need_you_titled_by_tool_and_prompt()
    {
        var r = PaperclipApi.ParseRuntimeRequests("run1", Events)[0] with { AgentId = "ag1" };
        var agent = Compose(r);
        var need = Assert.Single(agent.Needs);
        Assert.Equal("waiting", agent.State);
        Assert.Equal(("runtime", "r-open", "Bash: Create probe directory", "run1", "t1", "Bash"),
            (need.Kind, need.Id, need.Title, need.RunId, need.TurnId, need.Tool));
        Assert.True(PaperclipActions.Answerable(need));
    }

    [Fact]
    public void An_unanswerable_request_and_one_with_no_tool_or_prompt_still_show_with_a_link_only()
    {
        var admin = PaperclipApi.ParseRuntimeRequests("run1", Events)[1] with { AgentId = "ag1" };
        var bare = new PaperclipApi.RawRuntimeRequest("run1", "rb", "t", null, null, null, true, true, null, "ag1");
        var needs = Compose(admin, bare).Needs;
        Assert.Equal(["Bash: Admin thing", "Permission"], needs.Select(n => n.Title).ToArray());   // the admin one falls back to the newest tool started before it
        Assert.All(needs, n => Assert.False(PaperclipActions.Answerable(n)));
        Assert.Equal("Permission", needs[1].Tool);
    }

    // ---- Actions ----------------------------------------------------------------

    private static PaperclipNeed Need(string? run = "run1", string? turn = "t1") =>
        new("runtime", "r-open", "Bash: x", "http://pc", RunId: run, TurnId: turn);

    [Fact]
    public void A_runtime_need_resolves_on_its_run_and_the_body_names_the_action_and_turn()
    {
        Assert.Equal("/api/heartbeat-runs/run1/runtime-requests/r-open/resolve", PaperclipActions.PathFor(Need(), true));
        Assert.Equal(PaperclipActions.PathFor(Need(), true), PaperclipActions.PathFor(Need(), false));
        Assert.Null(PaperclipActions.PathFor(Need(null, "t1"), true));
        Assert.Null(PaperclipActions.PathFor(Need("run1", null), true));
        using var a = JsonDocument.Parse(PaperclipActions.BodyFor(Need(), "co", true));
        Assert.Equal("accept", a.RootElement.GetProperty("resolution").GetProperty("action").GetString());
        Assert.Equal("permission_approval", a.RootElement.GetProperty("requestKind").GetString());
        Assert.Equal("t1", a.RootElement.GetProperty("turnId").GetString());
        using var d = JsonDocument.Parse(PaperclipActions.BodyFor(Need(), "co", false));
        Assert.Equal("decline", d.RootElement.GetProperty("resolution").GetProperty("action").GetString());
    }

    [Theory]
    [InlineData(200, "Done", null)]
    [InlineData(202, "Done", null)]   // measured twice on the mini: the resolve route answers 202
    [InlineData(409, "AlreadyDecided", "Already answered in Paperclip")]
    [InlineData(404, "Refused", "This request no longer exists in Paperclip")]
    [InlineData(403, "NeedsReconnect", "Reconnect to Paperclip to answer")]
    [InlineData(500, "Unreachable", "Can't reach Paperclip")]
    [InlineData(401, "NeedsReconnect", "Reconnect to Paperclip to answer")]
    public async Task Each_measured_status_of_the_resolve_call_maps_to_its_outcome(int code, string result, string? why)
    {
        var api = new FakePaperclip();
        api.Posts["/api/heartbeat-runs/run1/runtime-requests/r-open/resolve"] = new(PaperclipHttp.Classify(code), code, "{\"accepted\":true}");
        var polled = 0;
        var o = await new PaperclipActions(api, "co", () => polled++).DecideAsync(Need(), true, default);
        Assert.Equal((result, why), (o.Result.ToString(), o.Why));
        Assert.Equal(code is 200 or 202 or 409 ? 1 : 0, polled);
        // CB-450: only an outcome of unknown effect is confirmed with a read; a 5xx is one, every other status here is not.
        Assert.Equal(code == 500 ? 1 : 0, api.Calls.Count(c => c.StartsWith("GET")));
    }

    [Fact]
    public void The_task_the_agent_is_on_rides_in_the_summary_when_its_title_is_known()
    {
        var r = new PaperclipApi.RawRuntimeRequest("run1", "rq", "t", "p", "Bash", "permission_approval", true, true, null, "ag1", "iss1");
        var with = PaperclipApi.Compose(new PaperclipApi.ComposeInput([Raw("ag1")], [], [], [], [], [],
            new Dictionary<string, string> { ["iss1"] = "Probe the workspace" }, "http://pc", "EX", DateTime.UtcNow, [r])).Single();
        Assert.Equal("Task: Probe the workspace", with.Needs.Single().Summary);
        Assert.Null(Compose(r).Needs.Single().Summary);   // no title known for that issue
    }

    [Fact]
    public void A_new_request_after_one_was_resolved_is_pending_on_its_own()
    {
        string Ev(int seq, string type, string id, bool request) =>
            "{\"seq\":" + seq + ",\"eventType\":\"" + type + "\",\"payload\":{\"prpEvent\":{\"turnId\":\"t\",\"payload\":" +
            (request ? "{\"request\":{\"requestId\":\"" + id + "\"}}" : "{\"requestId\":\"" + id + "\"}") + "}}}";
        var json = "[" + string.Join(",", Ev(1, "runtime_request.created", "a", true), Ev(2, "runtime_request.resolved", "a", false),
            Ev(3, "runtime_request.created", "b", true)) + "]";
        Assert.Equal(["b"], PaperclipApi.ParseRuntimeRequests("run", json).Select(r => r.RequestId).ToArray());
    }

    [Fact]
    public async Task Two_requests_on_one_run_both_show_and_a_first_failed_read_with_nothing_before_it_shows_none()
    {
        var api = Api();
        api.Gets[EventsPath] = FakePaperclip.Ok(Events.Replace("r-admin", "r-open-2"));
        var step = await PaperclipSessions.StepAsync(api, Cfg, PaperclipSessions.PollState.Initial, DateTime.UtcNow, default);
        Assert.Equal(2, step.Next.Published!.Single().Needs.Count(n => n.Kind == "runtime" && n.Id.StartsWith("r-open")));
        // Pinned: with no earlier reading there is nothing to keep, so a failed read shows no runtime need and is not a link failure.
        var bare = Api();
        bare.Gets[EventsPath] = FakePaperclip.Status(PaperclipHttpKind.ServerError, 500);
        var s = await PaperclipSessions.StepAsync(bare, Cfg, PaperclipSessions.PollState.Initial, DateTime.UtcNow, default);
        Assert.Equal(PaperclipLink.Connected, s.Next.Link);
        Assert.DoesNotContain(s.Next.Published!.Single().Needs, n => n.Kind == "runtime");
    }

    [Fact]
    public async Task A_dropped_connection_on_a_runtime_resolve_is_unreachable_and_the_need_stays_answerable()
    {
        var api = new FakePaperclip();
        api.Posts["/api/heartbeat-runs/run1/runtime-requests/r-open/resolve"] = new(PaperclipHttpKind.Unreachable, 0, "");
        var polled = 0;
        var o = await new PaperclipActions(api, "co", () => polled++).DecideAsync(Need(), false, default);
        Assert.Equal((PaperclipActionResult.Unreachable, 0), (o.Result, polled));
    }

    // ---- the prompt frame --------------------------------------------------------

    [Fact]
    public void A_runtime_need_goes_to_the_phone_as_Allow_once_and_Deny_under_the_same_keys()
    {
        var labels = PaperclipPrompts.OptionsFor(Need()).Select(o => (o.Key, o.Label)).ToArray();
        Assert.Equal([("approve", "Allow once"), ("decline", "Deny")], labels);
        Assert.Equal(PaperclipPrompts.Options.Select(o => o.Key), PaperclipPrompts.RuntimeOptions.Select(o => o.Key));
        Assert.Equal([("approve", "Approve"), ("decline", "Decline")],
            PaperclipPrompts.OptionsFor(new PaperclipNeed("action", "a", "t", "u")).Select(o => (o.Key, o.Label)).ToArray());
    }

    // ---- the poll ----------------------------------------------------------------

    private static readonly PaperclipSessions.PaperclipConfig Cfg = new(true, "http://pc", "co", "K", null, "fold");
    private const string EventsPath = "/api/heartbeat-runs/run1/events?limit=500";

    private static FakePaperclip Api()
    {
        var api = new FakePaperclip();
        api.Gets["/api/companies"] = FakePaperclip.Ok("[{\"id\":\"co\",\"name\":\"C\",\"issuePrefix\":\"EX\"}]");
        api.Gets["/api/companies/co/agents"] = FakePaperclip.Ok("[{\"id\":\"ag1\",\"name\":\"Ledger\",\"status\":\"running\"}]");
        api.Gets["/api/companies/co/live-runs"] = FakePaperclip.Ok(
            "[{\"id\":\"run1\",\"agentId\":\"ag1\",\"status\":\"running\"},{\"id\":\"run2\",\"agentId\":\"ag1\",\"status\":\"queued\"}]");
        api.Gets[EventsPath] = FakePaperclip.Ok(Events);
        return api;
    }

    [Fact]
    public async Task The_poll_reads_events_for_each_running_run_only_and_the_agent_needs_you()
    {
        var api = Api();
        var step = await PaperclipSessions.StepAsync(api, Cfg, PaperclipSessions.PollState.Initial, DateTime.UtcNow, default);
        Assert.Equal(["GET " + EventsPath], api.Calls.Where(c => c.Contains("/events")).ToArray());   // run2 is queued
        var agent = Assert.Single(step.Next.Published!);
        Assert.Equal("waiting", agent.State);
        Assert.Contains(agent.Needs, n => n is { Kind: "runtime", Id: "r-open", RunId: "run1", Title: "Bash: Create probe directory" });
    }

    [Fact]
    public async Task A_failed_events_read_keeps_the_last_answer_for_one_tick_and_drops_it_on_the_second()
    {
        var api = Api();
        var t0 = DateTime.UtcNow;
        var s1 = await PaperclipSessions.StepAsync(api, Cfg, PaperclipSessions.PollState.Initial, t0, default);
        api.Gets[EventsPath] = FakePaperclip.Status(PaperclipHttpKind.ServerError, 500);
        var s2 = await PaperclipSessions.StepAsync(api, Cfg, s1.Next, t0.AddSeconds(5), default);
        Assert.Equal(PaperclipLink.Connected, s2.Next.Link);
        Assert.Contains(s2.Next.Published!.Single().Needs, n => n.Kind == "runtime");
        var s3 = await PaperclipSessions.StepAsync(api, Cfg, s2.Next, t0.AddSeconds(10), default);
        Assert.DoesNotContain(s3.Next.Published!.Single().Needs, n => n.Kind == "runtime");
    }

    [Fact]
    public async Task An_answered_request_leaves_with_the_next_events_read_and_a_run_that_ended_is_forgotten()
    {
        var api = Api();
        var t0 = DateTime.UtcNow;
        var s1 = await PaperclipSessions.StepAsync(api, Cfg, PaperclipSessions.PollState.Initial, t0, default);
        // Appended after the closing bracket, found by trimming: a Windows checkout turns the file's "\n" into "\r\n".
        // CB-446: the second read asks only for what came after the last seq of the first, so the answer is the new rows alone.
        var lastSeq = JsonDocument.Parse(Events).RootElement.EnumerateArray().Max(e => e.TryGetProperty("seq", out var q) ? q.GetInt64() : 0);
        api.Gets[$"/api/heartbeat-runs/run1/events?afterSeq={lastSeq}&limit=500"] = FakePaperclip.Ok(
            "[{\"seq\":" + (lastSeq + 1) + ",\"eventType\":\"runtime_request.resolved\",\"payload\":{\"prpEvent\":{\"payload\":{\"request\":{\"requestId\":\"r-open\"}}}}}]");
        var s2 = await PaperclipSessions.StepAsync(api, Cfg, s1.Next, t0.AddSeconds(5), default);
        Assert.DoesNotContain(s2.Next.Published!.Single().Needs, n => n.Id == "r-open");
        api.Gets["/api/companies/co/live-runs"] = FakePaperclip.Ok("[]");
        var s3 = await PaperclipSessions.StepAsync(api, Cfg, s2.Next, t0.AddSeconds(10), default);
        Assert.Empty(s3.Next.RuntimeByRun!);
    }

    // The mini, 2026-11-23: the first tick with a running run read its events before any request existed, and the
    // body held a lifecycle event whose payload is null. The parser threw, StepAsync with it, and the loop's catch
    // backed off for 30 s on every tick. These are the real bodies (reduced, scrubbed) through the whole step.
    [Fact]
    public async Task The_live_bodies_from_the_mini_go_through_a_whole_step_and_the_pending_request_shows()
    {
        var api = new FakePaperclip();
        api.Gets["/api/companies"] = FakePaperclip.Ok("[{\"id\":\"company-1\",\"name\":\"C\",\"issuePrefix\":\"ORB\"}]");
        api.Gets["/api/companies/co/agents"] = FakePaperclip.Ok("[{\"id\":\"agent-1\",\"name\":\"Test Agent\",\"status\":\"running\"}]");
        api.Gets["/api/companies/co/live-runs"] = FakePaperclip.Ok(PaperclipFx.Read("native/runtime/live-pending-live-runs.json"));
        api.Gets["/api/companies/co/issues?status=in_progress"] = FakePaperclip.Ok(PaperclipFx.Read("native/runtime/live-pending-issues.json"));
        api.Gets["/api/heartbeat-runs/run-1/events?limit=500"] = FakePaperclip.Ok(PaperclipFx.Read("native/runtime/live-pending-events.json"));
        var step = await PaperclipSessions.StepAsync(api, Cfg, PaperclipSessions.PollState.Initial, DateTime.UtcNow, default);
        var need = Assert.Single(Assert.Single(step.Next.Published!).Needs);
        Assert.Equal(("runtime", "Bash: mkdir cb442-proof-a && ls -ld cb442-proof-a", "Create empty directory and verify\nTask: CB-442 proof A"),
            (need.Kind, need.Title, need.Summary));
    }

    [Fact]
    public void An_event_whose_payload_is_null_or_not_an_object_is_skipped_not_thrown_on()
    {
        var json = "[{\"seq\":1,\"eventType\":\"lifecycle\",\"payload\":null},{\"seq\":2,\"eventType\":\"x\",\"payload\":\"text\"}," +
                   "{\"seq\":3,\"eventType\":\"y\"},{\"seq\":4,\"eventType\":\"z\",\"payload\":{\"prpEvent\":null}}]";
        Assert.Empty(PaperclipApi.ParseRuntimeRequests("run", json));
    }

    [Fact]
    public void A_throwing_step_is_logged_once_per_distinct_message_and_again_after_a_good_step()
    {
        string? last = null;
        var boom = new InvalidOperationException("wrong type");
        Assert.Equal("poll step failed InvalidOperationException: wrong type", PaperclipSessions.StepFailureLine(boom, ref last));
        Assert.Null(PaperclipSessions.StepFailureLine(new InvalidOperationException("wrong type"), ref last));
        Assert.StartsWith("poll step failed ArgumentException: other", PaperclipSessions.StepFailureLine(new ArgumentException("other"), ref last));
        var cut = PaperclipSessions.StepFailureLine(new Exception(new string('x', 500)), ref last)!;
        Assert.Equal("poll step failed Exception: ".Length + 200, cut.Length);
    }

    // An api whose events read for one run throws, standing in for a body that makes the parser throw.
    private sealed class ThrowingFor(FakePaperclip inner, string path) : IPaperclipApi
    {
        public Task<PaperclipResponse> GetAsync(string p, CancellationToken ct) =>
            p == path ? throw new InvalidOperationException("odd body") : inner.GetAsync(p, ct);
        public Task<PaperclipResponse> PostAsync(string p, string b, CancellationToken ct) => inner.PostAsync(p, b, ct);
    }

    [Fact]
    public async Task One_run_whose_events_throw_costs_that_run_only_and_is_logged_once()
    {
        var api = Api();
        api.Gets["/api/companies/co/agents"] = FakePaperclip.Ok("[{\"id\":\"ag1\",\"name\":\"L\",\"status\":\"running\"},{\"id\":\"ag2\",\"name\":\"M\",\"status\":\"running\"}]");
        api.Gets["/api/companies/co/live-runs"] = FakePaperclip.Ok(
            "[{\"id\":\"run1\",\"agentId\":\"ag1\",\"status\":\"running\"},{\"id\":\"run3\",\"agentId\":\"ag2\",\"status\":\"running\"}]");
        api.Gets["/api/companies/co/tools/action-requests"] = FakePaperclip.Ok(
            "{\"actionRequests\":[{\"request\":{\"id\":\"ac9\",\"status\":\"pending\",\"requestedByAgentId\":\"ag1\"},\"toolTitle\":\"Send\"}]}");
        api.Gets["/api/heartbeat-runs/run3/events?limit=500"] = FakePaperclip.Ok(Events.Replace("r-open", "r-three"));
        var wrapped = new ThrowingFor(api, EventsPath);
        var log = new List<string>();
        var t0 = DateTime.UtcNow;
        var s1 = await PaperclipSessions.StepAsync(wrapped, Cfg, PaperclipSessions.PollState.Initial, t0, default, log.Add);
        Assert.Equal(PaperclipLink.Connected, s1.Next.Link);
        var byAgent = s1.Next.Published!.ToDictionary(a => a.Id, a => a.Needs);
        Assert.Contains(byAgent["ag2"], n => n.Id == "r-three");              // the other run's request still shows
        Assert.Contains(byAgent["ag1"], n => n.Kind == "action");             // actions still publish
        Assert.DoesNotContain(byAgent["ag1"], n => n.Kind == "runtime");      // nothing prior to keep for the odd run
        var s2 = await PaperclipSessions.StepAsync(wrapped, Cfg, s1.Next, t0.AddSeconds(5), default, log.Add);
        Assert.Contains(s2.Next.Published!.Single(a => a.Id == "ag2").Needs, n => n.Id == "r-three");
        Assert.Equal("runtime events read failed InvalidOperationException: odd body", Assert.Single(log));
    }

    [Fact]
    public async Task A_cancelled_events_read_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        var api = new ThrowingCancel(Api(), EventsPath, cts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PaperclipSessions.StepAsync(api, Cfg, PaperclipSessions.PollState.Initial, DateTime.UtcNow, cts.Token));
    }

    private sealed class ThrowingCancel(FakePaperclip inner, string path, CancellationTokenSource cts) : IPaperclipApi
    {
        public Task<PaperclipResponse> GetAsync(string p, CancellationToken ct)
        {
            if (p != path) return inner.GetAsync(p, ct);
            cts.Cancel();
            throw new OperationCanceledException(ct);
        }
        public Task<PaperclipResponse> PostAsync(string p, string b, CancellationToken ct) => inner.PostAsync(p, b, ct);
    }

    // ---- tool join, command and title (measured on the mini: every tool event in a run shares one itemId) ----

    private static string Tool(int seq, string type, string item, string exec, string name, string? subject = null) =>
        "{\"seq\":" + seq + ",\"eventType\":\"" + type + "\",\"payload\":{\"prpEvent\":{\"turnId\":\"t\",\"itemId\":\"" + item + "\",\"payload\":{\"name\":\"" + name +
        "\",\"executionId\":\"" + exec + "\"" + (subject != null ? ",\"subject\":\"" + subject + "\"" : "") + "}}}}";

    private static string Created(int seq, string id, string item, string? call, string prompt) =>
        "{\"seq\":" + seq + ",\"eventType\":\"runtime_request.created\",\"payload\":{\"prpEvent\":{\"turnId\":\"t\",\"itemId\":\"" + item + "\",\"payload\":{\"request\":{\"requestId\":\"" + id +
        "\",\"requestKind\":\"permission_approval\",\"prompt\":\"" + prompt + "\",\"choices\":[{\"key\":\"accept\"},{\"key\":\"decline\"}]" +
        (call != null ? ",\"details\":{\"toolCallId\":\"" + call + "\"}" : "") + "}}}}}";

    [Fact]
    public void The_tool_is_joined_by_tool_call_id_not_the_shared_item_id_and_the_newest_subject_is_the_command()
    {
        // Bash then paperclip_finish, one itemId; the request is for the FIRST call, so an itemId join would name the wrong tool.
        var json = "[" + string.Join(",", Tool(1, "tool.execution.started", "item-run", "toolu-1", "Bash"),
            Tool(2, "tool.execution.progressed", "item-run", "toolu-1", "Bash", "ls"),
            Tool(3, "tool.execution.progressed", "item-run", "toolu-1", "Bash", "mkdir x"),
            Tool(4, "tool.execution.started", "item-run", "toolu-2", "paperclip_finish"),
            Created(5, "rq", "item-run", "toolu-1", "Make x")) + "]";
        var r = Assert.Single(PaperclipApi.ParseRuntimeRequests("run", json));
        Assert.Equal(("Bash", "mkdir x"), (r.ToolName, r.Subject));
    }

    [Fact]
    public void With_no_matching_execution_id_the_newest_tool_started_before_the_request_stands_in()
    {
        var json = "[" + string.Join(",", Tool(1, "tool.execution.started", "i", "toolu-1", "Bash"),
            Tool(2, "tool.execution.started", "i", "toolu-2", "Edit"), Tool(2, "tool.execution.progressed", "i", "toolu-2", "Edit", "file.cs"), Created(3, "rq", "i", "toolu-gone", "Edit it"),
            Tool(4, "tool.execution.started", "i", "toolu-3", "Later")) + "]";
        var r = Assert.Single(PaperclipApi.ParseRuntimeRequests("run", json));
        Assert.Equal(("Edit", "file.cs"), (r.ToolName, r.Subject));
        var none = "[" + Created(1, "rq", "i", null, "Orphan") + "]";
        var o = Assert.Single(PaperclipApi.ParseRuntimeRequests("run", none));
        Assert.Equal((null, null), (o.ToolName, o.Subject));
    }

    [Theory]
    [InlineData("Bash", "mkdir x", "Make x", "Bash: mkdir x", "Make x")]          // the command leads, the description follows
    [InlineData("Bash", "mkdir x", "bash", "Bash: mkdir x", null)]                 // prompt is only the tool name, any case
    [InlineData("Bash", null, "Bash", "Bash", null)]                              // measured: prompt "Bash", never "Bash: Bash"
    [InlineData("Bash", null, "", "Bash", null)]
    [InlineData("Bash", null, "Make x", "Bash: Make x", null)]                    // no command known: the description
    [InlineData("Bash", "same", "same", "Bash: same", null)]
    [InlineData(null, null, null, "Permission", null)]
    [InlineData(null, "ls", null, "ls", null)]
    public void The_title_never_doubles_the_tool_name_and_shows_the_command_when_there_is_one(string? tool, string? subject, string? prompt, string title, string? summary)
    {
        var r = new PaperclipApi.RawRuntimeRequest("run1", "rq", "t", prompt, tool, "permission_approval", true, true, null, "ag1", null, subject);
        var need = Assert.Single(Compose(r).Needs);
        Assert.Equal((title, summary), (need.Title, need.Summary));
    }
}
