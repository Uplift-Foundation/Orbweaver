using System.Net;
using System.Text;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.IntegrationTests;

// CB-443: the two message calls and the chat feed over the real PaperclipHttp, against a loopback listener
// standing in for Paperclip. What fails here and not in the unit tests is the wire: the exact route, the
// bearer key, the JSON body byte for byte, the afterSeq query, and how a real 401, 404 and dropped
// connection come back. The comment POST and the create-task POST are measured routes (plan 10); a 403 and
// the shape of a long thread are not.
public sealed class PaperclipChatWireTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _root;
    private readonly List<(string Method, string Target, string? Auth, string Body)> _seen = [];
    private Func<string, (int Code, string Body)> _reply = _ => (200, "[]");

    public PaperclipChatWireTests()
    {
        (_listener, _root) = LoopbackHttp.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                string body;
                using (var rd = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = await rd.ReadToEndAsync();
                var target = ctx.Request.Url!.PathAndQuery;
                lock (_seen) _seen.Add((ctx.Request.HttpMethod, target, ctx.Request.Headers["Authorization"], body));
                var (code, text) = _reply(target);
                try
                {
                    ctx.Response.StatusCode = code;
                    ctx.Response.OutputStream.Write(Encoding.UTF8.GetBytes(text));
                    ctx.Response.Close();
                }
                catch { }
            }
        });
    }

    public void Dispose() => _listener.Stop();

    private int Polls;
    private PaperclipActions Actions(string key = "KEY-1") => new(new PaperclipHttp(_root, key), "co-9", () => Polls++);
    private int Count() { lock (_seen) return _seen.Count; }

    // ---- CommentAsync ---------------------------------------------------------

    [Fact]
    public async Task A_comment_posts_the_exact_body_with_a_quote_a_newline_and_a_non_ascii_letter_and_reads_the_id()
    {
        _reply = _ => (201, "{\"id\":\"cm-7\",\"body\":\"x\"}");
        var sent = await Actions().CommentAsync("is-1", "say \"hi\"\nca\u00f1on \u2603", default);
        Assert.True(sent.Outcome.IsDone);
        Assert.Equal("cm-7", sent.CommentId);
        Assert.Null(sent.IssueId);
        var s = Assert.Single(_seen);
        Assert.Equal(("POST", "/api/issues/is-1/comments", "Bearer KEY-1"), (s.Method, s.Target, s.Auth));
        // byte for byte what System.Text.Json writes: the quote and newline escaped, the rest as \u escapes
        Assert.Equal("{\"body\":\"say \\u0022hi\\u0022\\nca\\u00F1on \\u2603\"}", s.Body);
        Assert.Equal(1, Polls);
    }

    [Fact]
    public async Task A_comment_whose_answer_cannot_be_read_still_counts_as_sent_with_no_id()
    {
        _reply = _ => (201, "<<not json>>");
        var sent = await Actions().CommentAsync("is-1", "hi", default);
        Assert.True(sent.Outcome.IsDone);
        Assert.Null(sent.CommentId);
    }

    [Fact]
    public async Task A_comment_refused_paired_controls_401_403_404_400_and_a_dropped_connection()
    {
        _reply = _ => (401, "{\"error\":\"Invalid key\"}");
        var r401 = await Actions("REVOKED").CommentAsync("is-1", "hi", default);
        Assert.Equal(PaperclipActionResult.NeedsReconnect, r401.Outcome.Result);
        Assert.Null(r401.CommentId);

        _reply = _ => (403, "{\"error\":\"Board access required\"}");
        Assert.Equal(PaperclipActionResult.NeedsReconnect, (await Actions().CommentAsync("is-1", "hi", default)).Outcome.Result);

        _reply = _ => (404, "{\"error\":\"Issue not found\"}");
        var r404 = await Actions().CommentAsync("is-gone", "hi", default);
        Assert.Equal((PaperclipActionResult.Refused, PaperclipActions.TaskGoneWhy), (r404.Outcome.Result, r404.Outcome.Why));

        _reply = _ => (400, "{\"error\":\"Validation error: body is required\"}");
        var r400 = await Actions().CommentAsync("is-1", "hi", default);
        Assert.Equal((PaperclipActionResult.Refused, "Validation error: body is required"), (r400.Outcome.Result, r400.Outcome.Why));

        _reply = _ => (503, "{}");
        Assert.Equal(PaperclipActionResult.Unreachable, (await Actions().CommentAsync("is-1", "hi", default)).Outcome.Result);

        Assert.Equal(0, Polls);               // nothing refused nudged the poll
        _listener.Stop();                     // nothing listens on the port any more
        var dropped = await Actions().CommentAsync("is-1", "hi", default);
        Assert.Equal((PaperclipActionResult.Unreachable, "Can't reach Paperclip"), (dropped.Outcome.Result, dropped.Outcome.Why));
    }

    [Fact]
    public async Task An_empty_message_sends_nothing_at_all()
    {
        var sent = await Actions().CommentAsync("is-1", "  \n ", default);
        Assert.Equal((PaperclipActionResult.Refused, PaperclipActions.NothingToSendWhy), (sent.Outcome.Result, sent.Outcome.Why));
        Assert.Equal(0, Count());
    }

    // ---- OpenTaskAsync --------------------------------------------------------

    [Fact]
    public async Task Opening_a_task_posts_title_description_assignee_and_todo_to_the_company_and_reads_the_issue_id()
    {
        _reply = _ => (201, "{\"id\":\"is-new\",\"title\":\"x\"}");
        var text = "Plan the release\nand tell me the date \u2603";
        var sent = await Actions().OpenTaskAsync("ag-1", text, default);
        Assert.True(sent.Outcome.IsDone);
        Assert.Equal("is-new", sent.IssueId);
        Assert.Null(sent.CommentId);
        var s = Assert.Single(_seen);
        Assert.Equal(("POST", "/api/companies/co-9/issues", "Bearer KEY-1"), (s.Method, s.Target, s.Auth));
        Assert.Equal("{\"title\":\"Plan the release\",\"description\":\"Plan the release\\nand tell me the date \\u2603\","
            + "\"assigneeAgentId\":\"ag-1\",\"status\":\"todo\"}", s.Body);
    }

    [Fact]
    public async Task Opening_a_task_refused_paired_controls_401_404_and_an_empty_message()
    {
        _reply = _ => (401, "{}");
        Assert.Equal(PaperclipActionResult.NeedsReconnect, (await Actions("REVOKED").OpenTaskAsync("ag-1", "hi", default)).Outcome.Result);
        _reply = _ => (404, "{}");
        var gone = await Actions().OpenTaskAsync("ag-gone", "hi", default);
        Assert.Equal((PaperclipActionResult.Refused, PaperclipActions.AgentGoneWhy), (gone.Outcome.Result, gone.Outcome.Why));
        var before = Count();
        Assert.Equal(PaperclipActionResult.Refused, (await Actions().OpenTaskAsync("ag-1", "", default)).Outcome.Result);
        Assert.Equal(before, Count());
        Assert.Equal(0, Polls);
    }

    // ---- PaperclipChatFeed ----------------------------------------------------

    private static readonly PaperclipChat.TaskRef Task1 = new("is-1", "CB-1", "Title", "in_progress");
    private PaperclipChatFeed Feed(Func<ChatTarget> target, out List<ChatSnapshot> got)
    {
        var list = new List<ChatSnapshot>(); got = list;
        var f = new PaperclipChatFeed(new PaperclipHttp(_root, "KEY-1"), "co-9", target,
            eventsEvery: TimeSpan.FromMilliseconds(40), commentsEvery: TimeSpan.FromMilliseconds(80));
        f.Updated += s => { lock (list) list.Add(s); };
        return f;
    }

    private static async Task Until(Func<bool> cond, int ms = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond() && DateTime.UtcNow < end) await Task.Delay(10);
        Assert.True(cond(), "the condition never held");
    }

    [Fact]
    public async Task The_feed_makes_no_request_before_it_is_opened_and_none_after_it_is_closed()
    {
        _reply = t => t.Contains("/comments") ? (200, "[]") : (200, "[]");
        using var feed = Feed(() => new ChatTarget("ag-1", Task1, "run-1", true), out _);
        await Task.Delay(200);
        Assert.Equal(0, Count());                      // never opened: a closed panel costs nothing

        feed.Opened();
        Assert.True(feed.IsOpen);
        await Until(() => Count() >= 4);
        feed.Closed();
        Assert.False(feed.IsOpen);
        await Task.Delay(150);                         // let any in-flight read finish
        var settled = Count();
        await Task.Delay(300);
        Assert.Equal(settled, Count());                // and nothing after it was closed
    }

    [Fact]
    public async Task The_feed_reads_comments_and_events_with_the_key_and_an_afterSeq_from_the_last_seq_seen()
    {
        // The shapes of the lead's real bodies (fixtures in tests/UnitTests), cut down: three comments newest
        // first, and a run that spoke its final answer at seq 5.
        var comments = "[{\"id\":\"c3\",\"body\":\"51.\",\"authorAgentId\":\"ag-1\",\"createdAt\":\"2026-11-24T03:00:03Z\"},"
            + "{\"id\":\"c2\",\"body\":\"q\",\"authorUserId\":\"u\",\"createdAt\":\"2026-11-24T03:00:02Z\"},"
            + "{\"id\":\"c1\",\"body\":\"hello\",\"authorAgentId\":\"ag-1\",\"createdAt\":\"2026-11-24T03:00:01Z\"}]";
        var events = "[{\"seq\":2,\"eventType\":\"lifecycle\",\"payload\":null},{\"seq\":5,\"eventType\":\"item.completed\","
            + "\"createdAt\":\"2026-11-24T03:00:00Z\",\"payload\":{\"prpEvent\":{\"itemId\":\"i\",\"payload\":"
            + "{\"kind\":\"agentMessage\",\"text\":\"51.\",\"channel\":\"final\"}}}}]";
        _reply = t => t.StartsWith("/api/issues/is-1/comments") ? (200, comments)
            : t.Contains("afterSeq=0&") ? (200, events) : (200, "[]");
        using var feed = Feed(() => new ChatTarget("ag-1", Task1, "run-1", true), out var got);
        feed.Opened();
        await Until(() => { lock (got) return got.Any(s => s.Comments.Count == 3 && s.Activity?.LastSeq == 5); });
        // wait for the next tick's read, which must ask only for what came after seq 5
        await Until(() => { lock (_seen) return _seen.Any(x => x.Target.Contains("afterSeq=5")); });
        feed.Closed();

        List<(string Method, string Target, string? Auth, string Body)> seen;
        lock (_seen) seen = [.. _seen];
        Assert.All(seen, s => Assert.Equal(("GET", "Bearer KEY-1"), (s.Method, s.Auth)));
        Assert.Contains(seen, s => s.Target == "/api/issues/is-1/comments");
        Assert.Contains(seen, s => s.Target == "/api/heartbeat-runs/run-1/events?afterSeq=0&limit=500");
        // the next read asks only for what came after
        Assert.Contains(seen, s => s.Target == "/api/heartbeat-runs/run-1/events?afterSeq=5&limit=500");
    }

    [Fact]
    public async Task A_full_page_is_followed_at_once_by_the_next_and_a_short_one_stops()
    {
        string Page(int from, int count) => "[" + string.Join(",", Enumerable.Range(from, count).Select(i =>
            $"{{\"seq\":{i},\"eventType\":\"lifecycle\",\"createdAt\":\"2026-11-24T03:00:00Z\",\"payload\":null}}")) + "]";
        _reply = t => t.Contains("afterSeq=0&") ? (200, Page(1, 500))
            : t.Contains("afterSeq=500&") ? (200, Page(501, 120)) : (200, "[]");
        using var feed = Feed(() => new ChatTarget("ag-1", Task1, "run-1", false), out var got);
        feed.Opened();
        await Until(() => { lock (got) return got.Any(s => s.Activity?.LastSeq == 620); });
        feed.Closed();
        lock (_seen)
        {
            var ev = _seen.Where(s => s.Target.Contains("/events")).Select(s => s.Target).ToList();
            Assert.Equal(["/api/heartbeat-runs/run-1/events?afterSeq=0&limit=500", "/api/heartbeat-runs/run-1/events?afterSeq=500&limit=500"], ev.Take(2));
        }
    }

    [Fact]
    public async Task A_401_stops_the_feed_with_needs_reconnect_and_a_failed_read_keeps_what_is_shown()
    {
        var calls = 0;
        _reply = t => t.Contains("/comments") ? (++calls == 1 ? (200, "[{\"id\":\"a\",\"body\":\"hi\",\"authorUserId\":\"u\"}]") : (500, "{}")) : (200, "[]");
        using var feed = Feed(() => new ChatTarget("ag-1", Task1, null, false), out var got);
        feed.Opened();
        await Until(() => Count() >= 3);               // the first read landed, later ones failed
        lock (got) Assert.Single(got);                 // a failed read raised nothing and kept the list
        _reply = _ => (401, "{}");
        await Until(() => { lock (got) return got.Any(s => s.NeedsReconnect); });
        var settled = Count();
        await Task.Delay(300);
        Assert.Equal(settled, Count());                // the loop stopped on its own
        feed.Closed();
    }

    [Fact]
    public async Task ReadAssigned_reads_the_agents_issues_once_and_a_failed_read_is_null_not_empty()
    {
        _reply = _ => (200, "[{\"id\":\"a\",\"identifier\":\"CB-3\",\"title\":\"T\",\"status\":\"done\",\"updatedAt\":\"2026-11-24T03:00:00Z\"}]");
        var api = new PaperclipHttp(_root, "KEY-1");
        var ok = await PaperclipChatFeed.ReadAssignedAsync(api, "co-9", "ag 1", default);
        Assert.Equal("a", Assert.Single(ok!).Task.Id);
        Assert.Equal("/api/companies/co-9/issues?assigneeAgentId=ag%201", Assert.Single(_seen).Target);
        _reply = _ => (500, "{}");
        Assert.Null(await PaperclipChatFeed.ReadAssignedAsync(api, "co-9", "ag-1", default));
    }
}
