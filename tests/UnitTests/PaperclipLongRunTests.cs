using System.Text;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-446: a run with more than 500 events. The events route returns the OLDEST rows for <c>limit</c> and pages
/// forward with <c>afterSeq</c> (measured on Paperclip 2026.1117.0), so a poll that reads only
/// <c>?limit=500</c> never sees anything after seq 500. These drive <c>StepAsync</c> against a fake that honours
/// both parameters exactly that way.
/// </summary>
public class PaperclipLongRunTests
{
    private static readonly PaperclipSessions.PaperclipConfig Cfg = new(true, "http://pc", "co", "K", null, "fold");

    // The route as measured: rows with seq > afterSeq (default 0), ascending, at most limit of them.
    private sealed class EventLogApi : IPaperclipApi
    {
        internal readonly FakePaperclip Inner = new();
        internal readonly Dictionary<string, List<string>> Logs = new() { ["run1"] = [] };
        internal List<string> Log => Logs["run1"];   // one JSON object per event, ascending by seq
        internal readonly List<string> EventCalls = [];

        internal EventLogApi()
        {
            Inner.Gets["/api/companies"] = FakePaperclip.Ok("[{\"id\":\"co\",\"name\":\"C\",\"issuePrefix\":\"EX\"}]");
            Inner.Gets["/api/companies/co/agents"] = FakePaperclip.Ok("[{\"id\":\"ag1\",\"name\":\"Ledger\",\"status\":\"running\"}]");
            Inner.Gets["/api/companies/co/live-runs"] = FakePaperclip.Ok("[{\"id\":\"run1\",\"agentId\":\"ag1\",\"status\":\"running\"}]");
        }

        public Task<PaperclipResponse> GetAsync(string p, CancellationToken ct)
        {
            if (!p.StartsWith("/api/heartbeat-runs/")) return Inner.GetAsync(p, ct);
            EventCalls.Add(p);
            var runId = p["/api/heartbeat-runs/".Length..p.IndexOf("/events")];
            var q = System.Web.HttpUtility.ParseQueryString(p[(p.IndexOf('?') + 1)..]);
            var limit = int.Parse(q["limit"] ?? "200");
            var after = long.Parse(q["afterSeq"] ?? "0");
            var rows = Logs[runId].Where(j => JsonDocument.Parse(j).RootElement.GetProperty("seq").GetInt64() > after).Take(limit);
            return Task.FromResult(FakePaperclip.Ok("[" + string.Join(",", rows) + "]"));
        }

        public Task<PaperclipResponse> PostAsync(string p, string b, CancellationToken ct) => Inner.PostAsync(p, b, ct);

        internal void TwoRuns()
        {
            Logs["run2"] = [];
            Inner.Gets["/api/companies/co/agents"] = FakePaperclip.Ok("[{\"id\":\"ag1\",\"name\":\"Ledger\",\"status\":\"running\"},{\"id\":\"ag2\",\"name\":\"M\",\"status\":\"running\"}]");
            Inner.Gets["/api/companies/co/live-runs"] = FakePaperclip.Ok("[{\"id\":\"run1\",\"agentId\":\"ag1\",\"status\":\"running\"},{\"id\":\"run2\",\"agentId\":\"ag2\",\"status\":\"running\"}]");
        }

        internal long NextSeq => Log.Count + 1;
        internal void Filler(int count) { for (var i = 0; i < count; i++) Log.Add($"{{\"seq\":{NextSeq},\"eventType\":\"log\",\"payload\":{{\"prpEvent\":{{\"payload\":{{}}}}}}}}"); }
        internal void Tool(string name, string exec, string? subject = null)
        {
            Log.Add($"{{\"seq\":{NextSeq},\"eventType\":\"tool.execution.started\",\"payload\":{{\"prpEvent\":{{\"turnId\":\"t1\",\"payload\":{{\"name\":\"{name}\",\"executionId\":\"{exec}\"}}}}}}}}");
            if (subject != null)
                Log.Add($"{{\"seq\":{NextSeq},\"eventType\":\"tool.execution.progressed\",\"payload\":{{\"prpEvent\":{{\"payload\":{{\"executionId\":\"{exec}\",\"subject\":\"{subject}\"}}}}}}}}");
        }
        internal void Created(string id, string exec) =>
            Log.Add($"{{\"seq\":{NextSeq},\"eventType\":\"runtime_request.created\",\"createdAt\":\"2026-11-24T03:00:00Z\",\"payload\":{{\"prpEvent\":{{\"turnId\":\"t1\",\"payload\":{{\"request\":{{\"requestId\":\"{id}\",\"requestKind\":\"permission_approval\",\"prompt\":\"Run {id}\",\"details\":{{\"toolCallId\":\"{exec}\"}},\"choices\":[{{\"key\":\"accept\"}},{{\"key\":\"decline\"}}]}}}}}}}}}}");
        internal void Resolved(string id) =>
            Log.Add($"{{\"seq\":{NextSeq},\"eventType\":\"runtime_request.resolved\",\"payload\":{{\"prpEvent\":{{\"payload\":{{\"requestId\":\"{id}\"}}}}}}}}");
    }

    private static Task<PaperclipSessions.StepResult> Step(IPaperclipApi api, PaperclipSessions.PollState st, DateTime at, Action<string>? log = null) =>
        PaperclipSessions.StepAsync(api, Cfg, st, at, default, log);

    private static string[] RuntimeNeeds(PaperclipSessions.StepResult s) =>
        s.Next.Published!.Single().Needs.Where(n => n.Kind == "runtime").Select(n => n.Id).ToArray();

    [Fact]
    public async Task A_request_created_after_seq_500_in_a_first_read_is_found_by_paging_on()
    {
        var api = new EventLogApi();
        api.Filler(520);
        api.Tool("Bash", "x1", "ls /tmp");
        api.Created("late", "x1");
        api.Filler(30);
        Assert.True(api.Log.Count > 550);
        var s = await Step(api, PaperclipSessions.PollState.Initial, DateTime.UtcNow);
        Assert.Equal(["late"], RuntimeNeeds(s));
        var need = s.Next.Published!.Single().Needs.Single(n => n.Kind == "runtime");
        Assert.Equal("Bash: ls /tmp", need.Title);
        // Page one is the plain limit read; page two continues from the last seq of page one.
        Assert.Equal(["/api/heartbeat-runs/run1/events?limit=500", "/api/heartbeat-runs/run1/events?afterSeq=500&limit=500"], api.EventCalls);
    }

    [Fact]
    public async Task A_first_read_of_exactly_one_full_page_asks_once_more_and_stops_on_the_empty_page()
    {
        var api = new EventLogApi();
        api.Filler(500);
        var s = await Step(api, PaperclipSessions.PollState.Initial, DateTime.UtcNow);
        Assert.Empty(RuntimeNeeds(s));
        Assert.Equal(2, api.EventCalls.Count);
    }

    [Fact]
    public async Task A_request_created_before_500_and_resolved_after_it_is_not_pending()
    {
        var api = new EventLogApi();
        api.Filler(100);
        api.Tool("Bash", "x1");
        api.Created("early", "x1");
        api.Filler(500);
        api.Resolved("early");
        var s = await Step(api, PaperclipSessions.PollState.Initial, DateTime.UtcNow);
        Assert.Empty(RuntimeNeeds(s));
    }

    [Fact]
    public async Task A_run_followed_across_ticks_asks_only_for_what_came_after_the_last_seq_seen()
    {
        var api = new EventLogApi();
        var t0 = DateTime.UtcNow;
        api.Filler(480);
        api.Tool("Bash", "x1", "make build");
        api.Created("a", "x1");                      // seq 483, inside the first page
        var s1 = await Step(api, PaperclipSessions.PollState.Initial, t0);
        Assert.Equal(["a"], RuntimeNeeds(s1));
        Assert.Equal(["/api/heartbeat-runs/run1/events?limit=500"], api.EventCalls);

        api.EventCalls.Clear();
        api.Filler(40);                              // crosses seq 500
        api.Tool("Edit", "x2");
        api.Created("b", "x2");                      // past 500, on the next tick
        var s2 = await Step(api, s1.Next, t0.AddSeconds(5));
        Assert.Equal(["a", "b"], RuntimeNeeds(s2).OrderBy(x => x).ToArray());
        Assert.Equal(["/api/heartbeat-runs/run1/events?afterSeq=483&limit=500"], api.EventCalls);   // not the whole run again
        // The tool and subject joined on tick one still apply to "a" after the fold.
        Assert.Equal("Bash: make build", s2.Next.Published!.Single().Needs.Single(n => n.Id == "a").Title);

        api.EventCalls.Clear();
        api.Resolved("a");
        var s3 = await Step(api, s2.Next, t0.AddSeconds(10));
        Assert.Equal(["b"], RuntimeNeeds(s3));
        Assert.Equal(["/api/heartbeat-runs/run1/events?afterSeq=" + (api.Log.Count - 1) + "&limit=500"], api.EventCalls);

        api.EventCalls.Clear();
        var s4 = await Step(api, s3.Next, t0.AddSeconds(15));        // nothing new
        Assert.Equal(["b"], RuntimeNeeds(s4));
        Assert.Equal(["/api/heartbeat-runs/run1/events?afterSeq=" + api.Log.Count + "&limit=500"], api.EventCalls);
    }

    [Fact]
    public async Task A_run_that_leaves_live_runs_is_forgotten_and_a_run_that_returns_reads_from_the_start()
    {
        var api = new EventLogApi();
        var t0 = DateTime.UtcNow;
        api.Filler(10);
        var s1 = await Step(api, PaperclipSessions.PollState.Initial, t0);
        Assert.Contains("run1", s1.Next.RuntimeFolds!.Keys);
        var live = api.Inner.Gets["/api/companies/co/live-runs"];
        api.Inner.Gets["/api/companies/co/live-runs"] = FakePaperclip.Ok("[]");
        var s2 = await Step(api, s1.Next, t0.AddSeconds(5));
        Assert.Empty(s2.Next.RuntimeFolds!);
        api.Inner.Gets["/api/companies/co/live-runs"] = live;
        api.EventCalls.Clear();
        await Step(api, s2.Next, t0.AddSeconds(10));
        Assert.Equal(["/api/heartbeat-runs/run1/events?limit=500"], api.EventCalls);
    }

    // The failing read of a later page costs the run the tick (kept once), and the state is not half-advanced.
    private sealed class FailsPage(EventLogApi inner, string needle) : IPaperclipApi
    {
        internal bool Fail = true;
        public Task<PaperclipResponse> GetAsync(string p, CancellationToken ct) =>
            Fail && p.Contains(needle) ? Task.FromResult(FakePaperclip.Status(PaperclipHttpKind.ServerError, 500)) : inner.GetAsync(p, ct);
        public Task<PaperclipResponse> PostAsync(string p, string b, CancellationToken ct) => inner.PostAsync(p, b, ct);
    }

    [Fact]
    public async Task A_failed_later_page_keeps_the_run_for_one_tick_then_the_next_good_read_starts_clean()
    {
        var api = new EventLogApi();
        var t0 = DateTime.UtcNow;
        api.Filler(10); api.Tool("Bash", "x1"); api.Created("a", "x1");
        var s1 = await Step(api, PaperclipSessions.PollState.Initial, t0);
        Assert.Equal(["a"], RuntimeNeeds(s1));
        var failing = new FailsPage(api, "afterSeq");
        var s2 = await Step(failing, s1.Next, t0.AddSeconds(5));
        Assert.Equal(["a"], RuntimeNeeds(s2));               // kept one tick
        var s3 = await Step(failing, s2.Next, t0.AddSeconds(10));
        Assert.Empty(RuntimeNeeds(s3));                      // dropped on the second miss
        failing.Fail = false;
        api.Resolved("a");
        api.EventCalls.Clear();
        var s4 = await Step(failing, s3.Next, t0.AddSeconds(15));
        Assert.Empty(RuntimeNeeds(s4));                      // fresh state: whole run read again, resolved honoured
        Assert.Equal(["/api/heartbeat-runs/run1/events?limit=500"], api.EventCalls);
    }

    [Fact]
    public async Task A_body_with_no_seq_cannot_be_paged_and_is_read_whole_each_tick()
    {
        var api = new EventLogApi();
        var body = "[{\"eventType\":\"runtime_request.created\",\"payload\":{\"prpEvent\":{\"turnId\":\"t\",\"payload\":{\"request\":{\"requestId\":\"r\",\"requestKind\":\"permission_approval\",\"choices\":[{\"key\":\"accept\"},{\"key\":\"decline\"}]}}}}}]";
        api.Inner.Gets["/api/heartbeat-runs/run1/events?limit=500"] = FakePaperclip.Ok(body);
        var raw = new RawEvents(api, body);
        var t0 = DateTime.UtcNow;
        var s1 = await Step(raw, PaperclipSessions.PollState.Initial, t0);
        var s2 = await Step(raw, s1.Next, t0.AddSeconds(5));
        Assert.Equal(["r"], RuntimeNeeds(s2));
        Assert.Equal(["/api/heartbeat-runs/run1/events?limit=500", "/api/heartbeat-runs/run1/events?limit=500"], raw.Paths);
    }

    private sealed class RawEvents(EventLogApi inner, string body) : IPaperclipApi
    {
        internal readonly List<string> Paths = [];
        public Task<PaperclipResponse> GetAsync(string p, CancellationToken ct)
        {
            if (!p.StartsWith("/api/heartbeat-runs/run1/events")) return inner.GetAsync(p, ct);
            Paths.Add(p);
            return Task.FromResult(FakePaperclip.Ok(body));
        }
        public Task<PaperclipResponse> PostAsync(string p, string b, CancellationToken ct) => inner.PostAsync(p, b, ct);
    }

    [Fact]
    public void The_one_shot_parser_and_a_fold_in_two_halves_agree()
    {
        var api = new EventLogApi();
        api.Filler(5); api.Tool("Bash", "x1", "ls"); api.Created("a", "x1"); api.Filler(5); api.Created("b", "x1"); api.Resolved("a");
        string Body(int from, int to) => "[" + string.Join(",", api.Log.Skip(from).Take(to - from)) + "]";
        var whole = PaperclipApi.ParseRuntimeRequests("run1", Body(0, api.Log.Count));
        var fold = new PaperclipApi.RuntimeFold();
        Assert.Equal(8, fold.Apply("run1", Body(0, 8)));
        Assert.Equal(api.Log.Count - 8, fold.Apply("run1", Body(8, api.Log.Count)));
        Assert.Equal(whole, fold.Pending("run1"));
        Assert.Equal(api.Log.Count, fold.LastSeq);
        Assert.Equal(["b"], whole.Select(r => r.RequestId).ToArray());
        Assert.Equal("ls", whole.Single().Subject);
    }

    [Fact]
    public async Task A_request_created_and_resolved_inside_one_page_is_never_shown()
    {
        var api = new EventLogApi();
        api.Tool("Bash", "x1"); api.Created("blink", "x1"); api.Resolved("blink");
        Assert.Empty(RuntimeNeeds(await Step(api, PaperclipSessions.PollState.Initial, DateTime.UtcNow)));
    }

    [Fact]
    public void Duplicate_and_out_of_order_seq_rows_count_once_and_never_move_the_cursor_back()
    {
        string Row(long seq, string type, string id) => type == "runtime_request.created"
            ? $"{{\"seq\":{seq},\"eventType\":\"{type}\",\"payload\":{{\"prpEvent\":{{\"turnId\":\"t\",\"payload\":{{\"request\":{{\"requestId\":\"{id}\",\"choices\":[{{\"key\":\"accept\"}},{{\"key\":\"decline\"}}]}}}}}}}}}}"
            : $"{{\"seq\":{seq},\"eventType\":\"{type}\",\"payload\":{{\"prpEvent\":{{\"payload\":{{\"requestId\":\"{id}\"}}}}}}}}";
        var fold = new PaperclipApi.RuntimeFold();
        // resolved (9) before created (3) in the file, created twice, and a second request created twice.
        fold.Apply("r", "[" + string.Join(",", Row(9, "runtime_request.resolved", "gone"), Row(3, "runtime_request.created", "gone"),
            Row(5, "runtime_request.created", "kept"), Row(5, "runtime_request.created", "kept"), Row(4, "runtime_request.created", "kept")) + "]");
        Assert.Equal(9, fold.LastSeq);
        Assert.Equal(["kept"], fold.Pending("r").Select(x => x.RequestId).ToArray());
        // A later page whose rows are older than the cursor (a replay) adds nothing and does not rewind it.
        var delta = new PaperclipApi.RuntimeFold();
        delta.Apply("r", "[" + Row(2, "runtime_request.created", "gone") + "," + Row(1, "runtime_request.resolved", "kept") + "]");
        fold.Merge(delta);
        Assert.Equal(9, fold.LastSeq);
        Assert.Equal(["kept"], fold.Pending("r").Select(x => x.RequestId).ToArray());
    }

    [Fact]
    public async Task A_failure_on_page_two_of_a_first_read_commits_nothing_and_the_next_tick_reads_from_the_start()
    {
        var api = new EventLogApi();
        var t0 = DateTime.UtcNow;
        api.Filler(520); api.Tool("Bash", "x1"); api.Created("late", "x1");
        var failing = new FailsPage(api, "afterSeq");
        var s1 = await Step(failing, PaperclipSessions.PollState.Initial, t0);
        Assert.Equal(PaperclipLink.Connected, s1.Next.Link);
        Assert.Empty(RuntimeNeeds(s1));
        Assert.Empty(s1.Next.RuntimeFolds!);                 // no half-read state was kept
        failing.Fail = false;
        api.EventCalls.Clear();
        var s2 = await Step(failing, s1.Next, t0.AddSeconds(5));
        Assert.Equal(["late"], RuntimeNeeds(s2));
        Assert.Equal("/api/heartbeat-runs/run1/events?limit=500", api.EventCalls[0]);
    }

    [Fact]
    public async Task A_failure_on_a_later_page_of_a_followed_run_leaves_its_cursor_where_it_was()
    {
        var api = new EventLogApi();
        var t0 = DateTime.UtcNow;
        api.Filler(10);
        var s1 = await Step(api, PaperclipSessions.PollState.Initial, t0);
        api.Filler(520);                                      // next read is a full page then a second one
        api.Tool("Bash", "x1"); api.Created("late", "x1");
        var failing = new FailsPage(api, "afterSeq=510");     // the second page of the catch-up
        var s2 = await Step(failing, s1.Next, t0.AddSeconds(5));
        Assert.Equal(10, s2.Next.RuntimeFolds!["run1"].LastSeq);
        failing.Fail = false;
        api.EventCalls.Clear();
        var s3 = await Step(failing, s2.Next, t0.AddSeconds(10));
        Assert.Equal(["late"], RuntimeNeeds(s3));
        Assert.Equal("/api/heartbeat-runs/run1/events?afterSeq=10&limit=500", api.EventCalls[0]);
    }

    [Fact]
    public async Task Two_running_runs_each_ask_for_their_own_afterSeq_once_per_tick()
    {
        var api = new EventLogApi();
        api.TwoRuns();
        var t0 = DateTime.UtcNow;
        api.Filler(5);
        api.Logs["run2"].AddRange(Enumerable.Range(1, 7).Select(i => $"{{\"seq\":{i},\"eventType\":\"log\",\"payload\":{{}}}}"));
        var s1 = await Step(api, PaperclipSessions.PollState.Initial, t0);
        Assert.Equal(["/api/heartbeat-runs/run1/events?limit=500", "/api/heartbeat-runs/run2/events?limit=500"], api.EventCalls);
        api.EventCalls.Clear();
        await Step(api, s1.Next, t0.AddSeconds(5));
        Assert.Equal(["/api/heartbeat-runs/run1/events?afterSeq=5&limit=500", "/api/heartbeat-runs/run2/events?afterSeq=7&limit=500"], api.EventCalls);
    }

    [Fact]
    public async Task A_run_longer_than_the_page_ceiling_is_read_in_slices_and_a_late_request_still_shows()
    {
        var api = new EventLogApi();
        var t0 = DateTime.UtcNow;
        api.Filler(1100); api.Tool("Bash", "x1"); api.Created("late", "x1");      // 1102 rows: three pages
        var log = new List<string>();
        var s1 = await Step2(api, PaperclipSessions.PollState.Initial, t0, log);
        Assert.Empty(RuntimeNeeds(s1));
        Assert.Equal(2, api.EventCalls.Count);                                     // the ceiling: two pages
        Assert.Equal("runtime events for run run1 paused after 2 pages at seq 1000; resuming next tick", Assert.Single(log));
        api.EventCalls.Clear();
        var s2 = await Step2(api, s1.Next, t0.AddSeconds(5), log);
        Assert.Equal(["late"], RuntimeNeeds(s2));
        Assert.Equal(["/api/heartbeat-runs/run1/events?afterSeq=1000&limit=500"], api.EventCalls);
        Assert.Single(log);                                                        // no second pause line
    }

    private static Task<PaperclipSessions.StepResult> Step2(IPaperclipApi api, PaperclipSessions.PollState st, DateTime at, List<string> log) =>
        PaperclipSessions.StepAsync(api, Cfg, st, at, default, log.Add, maxEventPages: 2);
}
