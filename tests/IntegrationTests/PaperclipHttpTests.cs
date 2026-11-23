using System.Net;
using System.Text;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.IntegrationTests;

// The real PaperclipHttp (HttpClient) against a loopback HttpListener standing
// in for Paperclip: the seam the unit tests fake. What fails here and not
// there is the wire: the exact path and query, whether Authorization is sent,
// the body of the challenge POST byte for byte, and how a real 401, a hung
// response, a dropped connection and a refused port come back.
public sealed class PaperclipHttpTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _root;
    private readonly List<(string Method, string PathAndQuery, string? Auth, string Body, string? ContentType)> _seen = [];
    private Func<HttpListenerRequest, HttpListenerResponse, Task> _handler = (_, r) => { r.StatusCode = 404; return Task.CompletedTask; };

    public PaperclipHttpTests()
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
                lock (_seen)
                    _seen.Add((ctx.Request.HttpMethod, ctx.Request.Url!.PathAndQuery,
                        ctx.Request.Headers["Authorization"], body, ctx.Request.ContentType));
                try { await _handler(ctx.Request, ctx.Response); } catch { }
                try { ctx.Response.Close(); } catch { }
            }
        });
    }

    public void Dispose() => _listener.Stop();

    private static Task Reply(HttpListenerResponse r, int code, string body)
    {
        r.StatusCode = code;
        var b = Encoding.UTF8.GetBytes(body);
        r.OutputStream.Write(b);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Get_SendsTheExactPathAndQuery_AndTheBearerKey()
    {
        _handler = (_, r) => Reply(r, 200, "[]");
        var api = new PaperclipHttp(_root, "KEY-123");
        var res = await api.GetAsync("/api/companies/c1/approvals?status=pending", default);
        Assert.Equal((PaperclipHttpKind.Ok, 200, "[]"), (res.Kind, res.StatusCode, res.Body));
        var s = Assert.Single(_seen);
        Assert.Equal(("GET", "/api/companies/c1/approvals?status=pending", "Bearer KEY-123"), (s.Method, s.PathAndQuery, s.Auth));
    }

    [Fact]
    public async Task WithoutAKey_NoAuthorizationHeaderIsSent()
    {
        _handler = (_, r) => Reply(r, 200, "{\"status\":\"ok\"}");
        foreach (var k in new string?[] { null, "" })
            await new PaperclipHttp(_root, k).GetAsync("/api/health", default);
        Assert.All(_seen, s => Assert.Null(s.Auth));
        Assert.Equal(2, _seen.Count);
    }

    [Fact]
    public async Task TheChallengePost_IsByteExact_Json_AndCarriesNoAuth()
    {
        _handler = (_, r) => Reply(r, 200, "{}");
        await new PaperclipHttp(_root, null).PostAsync("/api/cli-auth/challenges", PaperclipConnect.ChallengeBody, default);
        var s = Assert.Single(_seen);
        Assert.Equal(("POST", "/api/cli-auth/challenges"), (s.Method, s.PathAndQuery));
        Assert.Equal("{\"command\":\"Orbweaver\",\"clientName\":\"Orbweaver\",\"requestedAccess\":\"board\"}", s.Body);
        Assert.StartsWith("application/json", s.ContentType);
        Assert.Null(s.Auth);
    }

    [Theory]
    [InlineData(401, "Unauthorized")]
    [InlineData(403, "Unauthorized")]
    [InlineData(404, "NotFound")]
    [InlineData(400, "Other")]
    [InlineData(500, "ServerError")]
    [InlineData(503, "ServerError")]
    public async Task StatusCodes_ComeBackClassified(int code, string kind)
    {
        _handler = (_, r) => Reply(r, code, "x");
        var res = await new PaperclipHttp(_root, "k").GetAsync("/x", default);
        Assert.Equal(Enum.Parse<PaperclipHttpKind>(kind), res.Kind);
        Assert.Equal(code, res.StatusCode);
        Assert.False(res.IsOk);
    }

    [Fact]
    public async Task AHungResponse_IsATimeout_NotAnException()
    {
        _handler = async (_, r) => { await Task.Delay(3000); r.StatusCode = 200; };
        var res = await new PaperclipHttp(_root, "k", timeout: TimeSpan.FromMilliseconds(300)).GetAsync("/x", default);
        Assert.Equal(PaperclipHttpKind.Timeout, res.Kind);
    }

    [Fact]
    public async Task ADroppedConnection_IsUnreachable()
    {
        // HttpListener.Abort() does not drop the socket on this runtime (it
        // answered an empty 200 when tried), so a raw listener that accepts the
        // connection and closes it with no response is the honest fixture.
        var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        try
        {
            // Every connection, not one: HttpClient retries an idempotent GET once
            // when a connection closes before any response, and a second connect
            // nobody accepts would look like a timeout.
            _ = Task.Run(async () =>
            {
                try { while (true) { using var c = await tcp.AcceptTcpClientAsync(); } }
                catch { }
            });
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            var res = await new PaperclipHttp($"http://127.0.0.1:{port}", "k").GetAsync("/x", default);
            Assert.Equal(PaperclipHttpKind.Unreachable, res.Kind);
            Assert.False(string.IsNullOrEmpty(res.Detail));
        }
        finally { tcp.Stop(); }
    }

    [Fact]
    public async Task ARefusedPort_IsUnreachable()
    {
        using var refusing = LoopbackHttp.RefusingPort();
        var res = await new PaperclipHttp($"http://127.0.0.1:{refusing.Port}", "k").GetAsync("/x", default);
        Assert.Equal(PaperclipHttpKind.Unreachable, res.Kind);
    }

    [Fact]
    public async Task NonJson200_IsOkAtTheWire_AndParsesToNothing()
    {
        _handler = (_, r) => Reply(r, 200, "<html>proxy login</html>");
        var res = await new PaperclipHttp(_root, "k").GetAsync("/api/companies/c/agents", default);
        Assert.True(res.IsOk);
        Assert.Empty(PaperclipApi.ParseAgents(res.Body));
    }

    // The poll makes about 1.75 calls a second for as long as the app runs, and
    // under launchd stderr is an unrotated file, so the app logs failures only.
    [Fact]
    public async Task WithSuccessLoggingOff_OnlyFailuresAreLogged_AndNeverTheQuery()
    {
        var lines = new List<string>();
        var api = new PaperclipHttp(_root, "KEY-SECRET", log: lines.Add, logSuccess: false);

        _handler = (_, r) => Reply(r, 200, "[]");
        for (var i = 0; i < 20; i++) await api.GetAsync("/api/companies/c/agents", default);
        Assert.Empty(lines);

        _handler = (_, r) => Reply(r, 500, "boom");
        await api.GetAsync("/api/cli-auth/challenges/c1?token=CHALLENGE-TOKEN", default);
        var line = Assert.Single(lines);
        Assert.Contains("500", line);
        Assert.DoesNotContain("CHALLENGE-TOKEN", line);
        Assert.DoesNotContain("KEY-SECRET", line);
    }

    [Fact]
    public async Task ATimeoutAndARefusedPort_AreLoggedWithoutABody()
    {
        var lines = new List<string>();
        _handler = async (_, r) => { await Task.Delay(3000); r.StatusCode = 200; };
        await new PaperclipHttp(_root, "k", timeout: TimeSpan.FromMilliseconds(200), log: lines.Add, logSuccess: false)
            .GetAsync("/slow?token=T", default);
        using var refusing = LoopbackHttp.RefusingPort();
        await new PaperclipHttp($"http://127.0.0.1:{refusing.Port}", "k", log: lines.Add, logSuccess: false).GetAsync("/x", default);
        Assert.Equal(2, lines.Count);
        Assert.Contains("timed out", lines[0]);
        Assert.Contains("unreachable", lines[1]);
        Assert.All(lines, l => { Assert.DoesNotContain("token", l); Assert.DoesNotContain("?", l); });
    }

    [Fact]
    public async Task ADisposedClient_CanNoLongerSend()
    {
        _handler = (_, r) => Reply(r, 200, "[]");
        var api = new PaperclipHttp(_root, "k");
        Assert.True((await api.GetAsync("/x", default)).IsOk);
        api.Dispose();
        var after = await api.GetAsync("/x", default);
        Assert.False(after.IsOk);   // reported as a failure, never thrown
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        _handler = async (_, r) => { await Task.Delay(3000); r.StatusCode = 200; };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PaperclipHttp(_root, "k").GetAsync("/x", cts.Token));
    }

    [Fact]
    public async Task TheLog_HasStatusSizeAndTime_NeverTheBodyOrTheKey()
    {
        _handler = (_, r) => Reply(r, 200, "SECRET-BODY adapterConfig /Users/u/path");
        var lines = new List<string>();
        await new PaperclipHttp(_root, "KEY-SECRET", log: lines.Add).GetAsync("/api/companies/c/agents", default);
        var line = Assert.Single(lines);
        Assert.Contains("200", line);
        Assert.Contains("/api/companies/c/agents", line);
        Assert.DoesNotContain("SECRET", line);
        Assert.DoesNotContain("adapterConfig", line);
    }

    [Fact]
    public async Task TheLog_NeverCarriesAQuery_NotTheChallengeToken()
    {
        _handler = (_, r) => Reply(r, 200, "{}");
        var lines = new List<string>();
        await new PaperclipHttp(_root, null, log: lines.Add).GetAsync("/api/cli-auth/challenges/c1?token=CHALLENGE-TOKEN-XYZ", default);
        var line = Assert.Single(lines);
        Assert.Contains("/api/cli-auth/challenges/c1", line);
        Assert.DoesNotContain("CHALLENGE-TOKEN-XYZ", line);
        Assert.DoesNotContain("?", line);
        Assert.Equal("/x", PaperclipHttp.PathOnly("/x"));
        Assert.Equal("/x", PaperclipHttp.PathOnly("/x?a=b"));
    }

    [Fact]
    public async Task TheFullConnectFlow_OverTheRealTransport_ReachesConnected()
    {
        var polls = 0;
        _handler = (req, r) =>
        {
            var path = req.Url!.AbsolutePath;
            if (path == "/api/health") return Reply(r, 200, "{\"status\":\"ok\",\"deploymentMode\":\"authenticated\"}");
            if (path == "/api/cli-auth/challenges" && req.HttpMethod == "POST")
                return Reply(r, 200, "{\"id\":\"c1\",\"token\":\"tok\",\"boardApiToken\":\"BOARD\"," +
                    "\"approvalUrl\":\"" + _root + "/cli-auth/c1\",\"pollPath\":\"/cli-auth/challenges/c1\"," +
                    "\"suggestedPollIntervalMs\":250}");
            if (path == "/api/cli-auth/challenges/c1")
                return Reply(r, 200, "{\"status\":\"" + (++polls < 2 ? "pending" : "approved") + "\"}");
            if (path == "/api/cli-auth/me")
                return req.Headers["Authorization"] == "Bearer BOARD" ? Reply(r, 200, "{\"source\":\"board_key\"}") : Reply(r, 401, "{}");
            return Reply(r, 404, "");
        };
        var opened = new List<string>();
        var result = await PaperclipConnect.RunAsync(_root, k => new PaperclipHttp(_root, k), u => { opened.Add(u); return true; },
            () => DateTime.UtcNow, (d, c) => Task.Delay(d, c), default);
        Assert.Equal(PaperclipConnectOutcome.Connected, result.Outcome);
        Assert.Equal("BOARD", result.Key);
        Assert.Equal(new[] { _root + "/cli-auth/c1" }, opened.ToArray());
        lock (_seen) Assert.Contains(_seen, s => s.PathAndQuery == "/api/cli-auth/challenges/c1?token=tok");
    }

    [Fact]
    public async Task AnAbandonedAttempt_PostsTheCancelOverTheWire_WithTheTokenInTheBodyAndNotInTheLog()
    {
        _handler = (req, r) => req.Url!.AbsolutePath switch
        {
            "/api/health" => Reply(r, 200, "{\"status\":\"ok\",\"deploymentMode\":\"authenticated\"}"),
            "/api/cli-auth/challenges" => Reply(r, 200, "{\"id\":\"c1\",\"token\":\"CHALLENGE-TOKEN-0123456789\",\"boardApiToken\":\"B\"," +
                "\"approvalUrl\":\"" + _root + "/cli-auth/c1\",\"pollPath\":\"/cli-auth/challenges/c1\",\"suggestedPollIntervalMs\":250}"),
            "/api/cli-auth/challenges/c1/cancel" => Reply(r, 200, "{}"),
            _ => Reply(r, 404, ""),   // every poll 404s: the attempt fails fast
        };
        var lines = new List<string>();
        var result = await PaperclipConnect.RunAsync(_root, k => new PaperclipHttp(_root, k, log: lines.Add), u => true,
            () => DateTime.UtcNow, (d, c) => Task.Delay(1, c), default);
        Assert.Equal(PaperclipConnectOutcome.Failed, result.Outcome);
        string cancelBody;
        lock (_seen)
        {
            var cancel = Assert.Single(_seen, s => s.PathAndQuery == "/api/cli-auth/challenges/c1/cancel");
            Assert.Equal("POST", cancel.Method);
            Assert.Null(cancel.Auth);
            cancelBody = cancel.Body;
        }
        Assert.Equal("{\"token\":\"CHALLENGE-TOKEN-0123456789\"}", cancelBody);
        Assert.All(lines, l => Assert.DoesNotContain("CHALLENGE-TOKEN", l));
    }

    [Fact]
    public async Task DisconnectOverTheRealTransport_PostsRevokeCurrentWithTheKey()
    {
        _handler = (_, r) => Reply(r, 200, "{}");
        var revoked = await PaperclipConnect.DisconnectAsync(new PaperclipHttp(_root, "BOARD"), () => { }, default);
        Assert.True(revoked);
        var s = Assert.Single(_seen);
        Assert.Equal(("POST", "/api/cli-auth/revoke-current", "Bearer BOARD"), (s.Method, s.PathAndQuery, s.Auth));
    }

    [Fact]
    public async Task OnePollStep_OverTheRealTransport_Publishes()
    {
        _handler = (req, r) => req.Url!.AbsolutePath switch
        {
            "/api/companies" => Reply(r, 200, "[{\"id\":\"co\",\"name\":\"C\",\"issuePrefix\":\"EX\"}]"),
            "/api/companies/co/agents" => Reply(r, 200, "[{\"id\":\"a\",\"name\":\"A\",\"status\":\"idle\"}]"),
            "/api/companies/co/tools/action-requests" => Reply(r, 200, "{\"actionRequests\":[]}"),
            _ => Reply(r, 200, "[]"),
        };
        var cfg = new PaperclipSessions.PaperclipConfig(true, _root, "co", "KEY", null, "fold");
        var step = await PaperclipSessions.StepAsync(new PaperclipHttp(_root, "KEY"), cfg, PaperclipSessions.PollState.Initial, DateTime.UtcNow, default);
        Assert.Equal(PaperclipLink.Connected, step.Next.Link);
        Assert.Equal("a", Assert.Single(step.Next.Published!).Id);
        lock (_seen) Assert.All(_seen, s => Assert.Equal("Bearer KEY", s.Auth));
    }
}
