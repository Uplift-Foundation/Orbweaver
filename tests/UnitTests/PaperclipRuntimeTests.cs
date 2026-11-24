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
        Assert.Equal(["Admin thing", "Permission"], needs.Select(n => n.Title).ToArray());
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
    [InlineData(409, "AlreadyDecided", "Already answered in Paperclip")]
    [InlineData(404, "Refused", "This request no longer exists in Paperclip")]
    [InlineData(403, "NeedsReconnect", "Reconnect to Paperclip to answer")]
    [InlineData(500, "Unreachable", "Can't reach Paperclip")]
    public async Task Each_measured_status_of_the_resolve_call_maps_to_its_outcome(int code, string result, string? why)
    {
        var api = new FakePaperclip();
        api.Posts["/api/heartbeat-runs/run1/runtime-requests/r-open/resolve"] = new(PaperclipHttp.Classify(code), code, "{\"accepted\":true}");
        var polled = 0;
        var o = await new PaperclipActions(api, "co", () => polled++).DecideAsync(Need(), true, default);
        Assert.Equal((result, why), (o.Result.ToString(), o.Why));
        Assert.Equal(code is 200 or 409 ? 1 : 0, polled);
        Assert.DoesNotContain(api.Calls, c => c.StartsWith("GET"));
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
        api.Gets[EventsPath] = FakePaperclip.Ok(Events.Replace("]\n", ",{\"seq\":20,\"eventType\":\"runtime_request.resolved\",\"payload\":{\"prpEvent\":{\"payload\":{\"request\":{\"requestId\":\"r-open\"}}}}}]\n"));
        var s2 = await PaperclipSessions.StepAsync(api, Cfg, s1.Next, t0.AddSeconds(5), default);
        Assert.DoesNotContain(s2.Next.Published!.Single().Needs, n => n.Id == "r-open");
        api.Gets["/api/companies/co/live-runs"] = FakePaperclip.Ok("[]");
        var s3 = await PaperclipSessions.StepAsync(api, Cfg, s2.Next, t0.AddSeconds(10), default);
        Assert.Empty(s3.Next.RuntimeByRun!);
    }
}
