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
        internal readonly List<string> Log = [];   // one JSON object per event, ascending by seq
        internal readonly List<string> EventCalls = [];

        internal EventLogApi()
        {
            Inner.Gets["/api/companies"] = FakePaperclip.Ok("[{\"id\":\"co\",\"name\":\"C\",\"issuePrefix\":\"EX\"}]");
            Inner.Gets["/api/companies/co/agents"] = FakePaperclip.Ok("[{\"id\":\"ag1\",\"name\":\"Ledger\",\"status\":\"running\"}]");
            Inner.Gets["/api/companies/co/live-runs"] = FakePaperclip.Ok("[{\"id\":\"run1\",\"agentId\":\"ag1\",\"status\":\"running\"}]");
        }

        public Task<PaperclipResponse> GetAsync(string p, CancellationToken ct)
        {
            if (!p.StartsWith("/api/heartbeat-runs/run1/events")) return Inner.GetAsync(p, ct);
            EventCalls.Add(p);
            var q = System.Web.HttpUtility.ParseQueryString(p[(p.IndexOf('?') + 1)..]);
            var limit = int.Parse(q["limit"] ?? "200");
            var after = long.Parse(q["afterSeq"] ?? "0");
            var rows = Log.Where(j => JsonDocument.Parse(j).RootElement.GetProperty("seq").GetInt64() > after).Take(limit);
            return Task.FromResult(FakePaperclip.Ok("[" + string.Join(",", rows) + "]"));
        }

        public Task<PaperclipResponse> PostAsync(string p, string b, CancellationToken ct) => Inner.PostAsync(p, b, ct);

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
}
