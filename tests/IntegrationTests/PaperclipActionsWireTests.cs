using System.Net;
using System.Text;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.IntegrationTests;

// CB-427: PaperclipActions over the real PaperclipHttp, against a loopback
// listener standing in for Paperclip. What fails here and not in the unit tests
// is the wire: the exact route, the bearer key, the JSON body byte for byte, and
// how a real 403, 409 and dropped connection come back as verdicts.
// The routes are Paperclip's declared ones; approve / reject was measured live
// (plan 1.3), the rest were not and are only as right as the source they came from.
public sealed class PaperclipActionsWireTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _root;
    private readonly List<(string Method, string Path, string? Auth, string Body, string? ContentType)> _seen = [];
    private Func<string, (int Code, string Body)> _reply = _ => (200, "{}");

    public PaperclipActionsWireTests()
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
                var path = ctx.Request.Url!.AbsolutePath;
                lock (_seen) _seen.Add((ctx.Request.HttpMethod, path, ctx.Request.Headers["Authorization"], body, ctx.Request.ContentType));
                var (code, text) = _reply(path);
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

    private PaperclipActions Actions(string key = "KEY-1") => new(new PaperclipHttp(_root, key), "co-9", () => Polls++);
    private int Polls;

    [Fact]
    public async Task Approving_an_approval_posts_the_note_with_the_key()
    {
        _reply = _ => (200, "{\"status\":\"approved\"}");
        var r = await Actions().DecideAsync(new PaperclipNeed("approval", "ap-1", "Hire", "u"), true, default);
        Assert.True(r.IsDone);
        var s = Assert.Single(_seen);
        Assert.Equal(("POST", "/api/approvals/ap-1/approve", "Bearer KEY-1", "{\"decisionNote\":\"From Orbweaver\"}"),
            (s.Method, s.Path, s.Auth, s.Body));
        Assert.StartsWith("application/json", s.ContentType);
    }

    [Fact]
    public async Task Declining_a_tool_action_posts_the_company()
    {
        var r = await Actions().DecideAsync(new PaperclipNeed("action", "ac-1", "Tool", "u"), false, default);
        Assert.True(r.IsDone);
        var s = Assert.Single(_seen);
        Assert.Equal(("/api/tool-gateway/action-requests/ac-1/decline", "{\"companyId\":\"co-9\"}"), (s.Path, s.Body));
    }

    [Theory]
    [InlineData("wake", "/api/agents/a-1/wakeup", "{\"source\":\"on_demand\"}")]
    [InlineData("pause", "/api/agents/a-1/pause", "{}")]
    [InlineData("resume", "/api/agents/a-1/resume", "{}")]
    public async Task Agent_verbs_reach_their_routes(string verb, string path, string body)
    {
        _reply = _ => (202, "{}");
        var a = Actions();
        var r = verb switch { "wake" => await a.WakeAsync("a-1", default), "pause" => await a.PauseAsync("a-1", default), _ => await a.ResumeAsync("a-1", default) };
        Assert.True(r.IsDone);
        var s = Assert.Single(_seen);
        Assert.Equal((path, body), (s.Path, s.Body));
    }

    [Fact]
    public async Task A_revoked_key_is_a_reconnect_not_a_success()
    {
        // The paired negative: the same call with the key refused must not read as done.
        _reply = _ => (401, "{\"error\":\"Invalid key\"}");
        var r = await Actions("REVOKED").DecideAsync(new PaperclipNeed("approval", "ap-1", "Hire", "u"), true, default);
        Assert.Equal((PaperclipActionResult.NeedsReconnect, "Reconnect to Paperclip to answer"), (r.Result, r.Why));
        Assert.Equal(0, Polls);
    }

    [Fact]
    public async Task Rejecting_an_approved_item_is_a_422_then_a_re_read_and_says_already_decided()
    {
        // The measured pair (plan 13.1 row 4): the decide is refused, and the item reads back decided.
        _reply = path => path.EndsWith("/reject") ? (422, "{\"error\":\"Only pending or revision requested approvals can be rejected\"}")
            : (200, "{\"status\":\"approved\"}");
        var r = await Actions().DecideAsync(new PaperclipNeed("approval", "ap-1", "Hire", "u"), false, default);
        Assert.Equal(PaperclipActionResult.AlreadyDecided, r.Result);
        Assert.Equal(["/api/approvals/ap-1/reject", "/api/approvals/ap-1"], _seen.Select(x => x.Path));
        Assert.Equal("GET", _seen[1].Method);
        Assert.Equal(1, Polls);
    }

    [Fact]
    public async Task Approving_twice_is_a_second_200_and_so_done()
    {
        _reply = _ => (200, "{\"status\":\"approved\"}");
        var a = Actions();
        var need = new PaperclipNeed("approval", "ap-1", "Hire", "u");
        Assert.True((await a.DecideAsync(need, true, default)).IsDone);
        Assert.True((await a.DecideAsync(need, true, default)).IsDone);
        Assert.Equal(2, Polls);
    }

    [Fact]
    public async Task A_refused_port_fails_with_a_sentence_and_does_not_throw()
    {
        _listener.Stop(); // nothing listens on the port any more
        var r = await Actions().PauseAsync("a-1", default);
        Assert.Equal((PaperclipActionResult.Unreachable, "Can't reach Paperclip"), (r.Result, r.Why));
    }
}
