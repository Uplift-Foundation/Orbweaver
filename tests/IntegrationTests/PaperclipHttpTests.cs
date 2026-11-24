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
    public async Task ARefusedPort_SaysTheReasonOnce_AndTheConnectSentenceNamesTheAddressOnce()
    {
        using var refusing = LoopbackHttp.RefusingPort();
        var url = $"http://127.0.0.1:{refusing.Port}";
        var res = await new PaperclipHttp(url, "k").GetAsync("/x", default);
        Assert.Equal(PaperclipHttpKind.Unreachable, res.Kind);
        // The .NET chain nests one sentence three deep; the reason is printed once and
        // carries no address (the caller's sentence adds it).
        Assert.Equal(0, CountOf(res.Detail!, $"127.0.0.1:{refusing.Port}"));
        Assert.Equal(1, CountOf(res.Detail!, res.Detail!.Split(" (")[0]));

        var connect = await PaperclipConnect.RunAsync(url, k => new PaperclipHttp(url, k), u => true,
            () => DateTime.UtcNow, (d, c) => Task.CompletedTask, default);
        Assert.Equal(PaperclipConnectOutcome.Failed, connect.Outcome);
        Assert.StartsWith($"Can't reach Paperclip at 127.0.0.1:{refusing.Port}: ", connect.Message);
        Assert.Equal(1, CountOf(connect.Message!, $"127.0.0.1:{refusing.Port}"));
    }

    private static int CountOf(string text, string needle)
    {
        var n = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
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

    // ---- CB-445: a picture is bytes, fetched with the key ----

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAAEklEQVR4nGP438DwHx9mGBkKAFPWn4ETg8N3AAAAAElFTkSuQmCC");

    private static Task ReplyBytes(HttpListenerResponse r, int code, byte[] body, string type)
    {
        r.StatusCode = code;
        r.ContentType = type;
        r.OutputStream.Write(body);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task APicture_ComesBackAsTheExactBytes_WithItsType_AndTheKey()
    {
        _handler = (_, r) => ReplyBytes(r, 200, Png, "image/png");
        var res = await new PaperclipHttp(_root, "KEY-1").GetBytesAsync("/api/assets/a1/content", default);
        Assert.Equal((PaperclipHttpKind.Ok, 200, "image/png"), (res.Kind, res.StatusCode, res.ContentType));
        Assert.Equal(Png, res.Body);
        var s = Assert.Single(_seen);
        Assert.Equal(("GET", "/api/assets/a1/content", "Bearer KEY-1"), (s.Method, s.PathAndQuery, s.Auth));
    }

    [Fact]
    public async Task AMissingPicture_IsNotFound_WithNoBody()
    {
        _handler = (_, r) => Reply(r, 404, "{\"error\":\"x\"}");
        var res = await new PaperclipHttp(_root, "KEY").GetBytesAsync("/api/assets/gone/content", default);
        Assert.Equal((PaperclipHttpKind.NotFound, 404), (res.Kind, res.StatusCode));
        Assert.Empty(res.Body);
        Assert.False(res.IsOk);
    }

    [Fact]
    public async Task APictureOverTheBound_IsRefused_NotKept()
    {
        _handler = (_, r) => ReplyBytes(r, 200, new byte[(int)PaperclipHttp.MaxPictureBytes + 10], "image/png");
        var res = await new PaperclipHttp(_root, "KEY").GetBytesAsync("/api/assets/big/content", default);
        Assert.Equal(PaperclipHttpKind.Other, res.Kind);
        Assert.Empty(res.Body);
    }

    [Fact]
    public async Task APictureFetchThatTimesOut_IsATimeout_AndADroppedConnectionIsUnreachable()
    {
        _handler = async (_, _) => await Task.Delay(2000);
        var slow = await new PaperclipHttp(_root, "K", timeout: TimeSpan.FromMilliseconds(150)).GetBytesAsync("/api/assets/a/content", default);
        Assert.Equal(PaperclipHttpKind.Timeout, slow.Kind);

        var dead = await new PaperclipHttp("http://127.0.0.1:1", "K").GetBytesAsync("/api/assets/a/content", default);
        Assert.Equal(PaperclipHttpKind.Unreachable, dead.Kind);
    }

    [Fact]
    public async Task ACancelledPictureFetch_Throws()
    {
        _handler = async (_, _) => await Task.Delay(2000);
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PaperclipHttp(_root, "K").GetBytesAsync("/api/assets/a/content", cts.Token));
    }

    [Fact]
    public async Task ARedirectToAnotherHost_DoesNotCarryTheKeyThere()
    {
        var (other, otherRoot) = LoopbackHttp.Start();
        string? leaked = "unset";
        var hit = false;
        _ = Task.Run(async () =>
        {
            var ctx = await other.GetContextAsync();
            hit = true;
            leaked = ctx.Request.Headers["Authorization"];
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "image/png";
            ctx.Response.OutputStream.Write(Png);
            ctx.Response.Close();
        });
        try
        {
            _handler = (_, r) => { r.StatusCode = 302; r.Headers["Location"] = otherRoot + "/stolen.png"; return Task.CompletedTask; };
            await new PaperclipHttp(_root, "SECRET-KEY").GetBytesAsync("/api/assets/a/content", default);
            await Task.Delay(200);
            // Whatever the redirect did, the key must not have reached the other host.
            Assert.True(hit, "the redirect was not followed, so this proves nothing");
            Assert.Null(leaked);
        }
        finally { other.Stop(); }
    }

    [Fact]
    public async Task ThePictureFlow_FromTheAgentsListToAFileOnDisk_ThroughTheRealClient()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb445-int-" + Guid.NewGuid().ToString("N"));
        try
        {
            _handler = (req, r) => req.Url!.AbsolutePath == "/api/assets/a1/content"
                ? ReplyBytes(r, 200, Png, "image/png")
                : Reply(r, 404, "{}");
            var cache = new PaperclipPictureCache(dir);
            await cache.SyncAsync(["/api/assets/a1/content", "/api/assets/gone/content"], new PaperclipHttp(_root, "KEY"), default);
            Assert.Equal(Png, File.ReadAllBytes(cache.FileFor("/api/assets/a1/content")!));
            Assert.Null(cache.FileFor("/api/assets/gone/content"));
            lock (_seen) Assert.All(_seen, x => Assert.Equal("Bearer KEY", x.Auth));
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }
}
