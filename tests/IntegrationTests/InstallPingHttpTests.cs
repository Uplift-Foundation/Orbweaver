using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.IntegrationTests;

// CB-466: the real HttpInstallPingHttp against a loopback HttpListener standing in
// for Umami, and the sender over it. What fails here and not in the unit tests:
// the method, path, content type, User-Agent and body as they cross a real socket,
// a hung server, and a refused connection. Nothing leaves the machine.
public sealed class InstallPingHttpTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _root;
    private readonly List<(string Method, string Path, string ContentType, string UserAgent, string Body)> _seen = [];
    private Func<HttpListenerResponse, Task> _answer = r => { r.StatusCode = 200; return Task.CompletedTask; };

    public InstallPingHttpTests()
    {
        (_listener, _root) = LoopbackHttp.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                lock (_seen)
                    _seen.Add((ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, ctx.Request.ContentType ?? "",
                        ctx.Request.UserAgent ?? "", body));
                try { await _answer(ctx.Response); } catch { }
                try { ctx.Response.Close(); } catch { }
            }
        });
    }

    public void Dispose() => _listener.Close();

    private static async Task Reply(HttpListenerResponse r, int status, string body)
    {
        r.StatusCode = status;
        var bytes = Encoding.UTF8.GetBytes(body);
        r.ContentLength64 = bytes.Length;
        await r.OutputStream.WriteAsync(bytes);
    }

    [Fact]
    public async Task TheRequestCrossesTheSocketAsDocumented()
    {
        _answer = r => Reply(r, 200, "{\"cache\":\"x\",\"sessionId\":\"s\",\"visitId\":\"v\"}");
        using var http = new HttpInstallPingHttp();
        var json = InstallPing.BuildBody(InstallPing.WebsiteId, "abc", "1.0.7", "macos", "arm64", false);

        var (status, body) = await http.PostJsonAsync(_root + "/api/send", json, "Orbweaver/1.0.7 (macos; arm64)", default);

        Assert.Equal(200, status);
        Assert.Contains("sessionId", body);
        var seen = Assert.Single(_seen);
        Assert.Equal("POST", seen.Method);
        Assert.Equal("/api/send", seen.Path);
        Assert.StartsWith("application/json", seen.ContentType);
        Assert.Equal("Orbweaver/1.0.7 (macos; arm64)", seen.UserAgent);
        Assert.Equal(json, seen.Body);
    }

    [Fact]
    public async Task ABotAnswerIsReturnedVerbatimSoTheSenderCanRefuseIt()
    {
        _answer = r => Reply(r, 200, "{\"beep\":\"boop\"}");
        using var http = new HttpInstallPingHttp();
        var (status, body) = await http.PostJsonAsync(_root + "/api/send", "{}", "ua", default);
        Assert.False(InstallPing.Accepted(status, body));
    }

    [Fact]
    public async Task AHungServerIsGivenUpOnWithinTheBound()
    {
        _answer = async r => { await Task.Delay(TimeSpan.FromSeconds(30)); r.StatusCode = 200; };
        using var http = new HttpInstallPingHttp(TimeSpan.FromMilliseconds(250));
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => http.PostJsonAsync(_root + "/api/send", "{}", "ua", default));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AnUnsetTimeoutUsesTheDocumentedTenSeconds()
    {
        _answer = r => Reply(r, 200, "{}");
        using var http = new HttpInstallPingHttp();
        Assert.Equal(TimeSpan.FromSeconds(10), InstallPing.HttpTimeout);
        await http.PostJsonAsync(_root + "/api/send", "{}", "ua", default);
    }

    private sealed class PingSettings : IInstallPingSettings
    {
        public bool Enabled => true;
        public string? InstallId { get; set; }
        public DateTimeOffset? LastPing { get; set; }
        public bool Internal => false;
    }

    private static InstallPingSender Sender(PingSettings s, IInstallPingHttp http, string root, List<string> log) =>
        new(s, http, () => DateTimeOffset.UtcNow, log.Add, _ => null, "1.0.7", "macos", "arm64", root);

    [Fact]
    public async Task TheSenderStampsOnlyAfterARealAcceptedAnswer()
    {
        _answer = r => Reply(r, 200, "{\"cache\":\"x\"}");
        var s = new PingSettings();
        var log = new List<string>();
        using var http = new HttpInstallPingHttp();
        await Sender(s, http, _root, log).TrySendAsync();

        Assert.NotNull(s.LastPing);
        var sent = JsonNode.Parse(Assert.Single(_seen).Body)!["payload"]!;
        Assert.Equal(s.InstallId, sent["id"]!.GetValue<string>());
        Assert.Empty(log);
    }

    [Fact]
    public async Task ARefusedConnectionIsLoggedNotThrownAndNotStamped()
    {
        var (dead, deadRoot) = LoopbackHttp.Start();
        dead.Close(); // nothing listens on this port any more
        var s = new PingSettings();
        var log = new List<string>();
        using var http = new HttpInstallPingHttp(TimeSpan.FromSeconds(5));

        await Sender(s, http, deadRoot, log).TrySendAsync();

        Assert.Null(s.LastPing);
        Assert.Contains(log, l => l.Contains("install ping failed"));
    }

    [Fact]
    public async Task AServerErrorIsLoggedAndNotStamped()
    {
        _answer = r => Reply(r, 500, "oops");
        var s = new PingSettings();
        var log = new List<string>();
        using var http = new HttpInstallPingHttp();
        await Sender(s, http, _root, log).TrySendAsync();
        Assert.Null(s.LastPing);
        Assert.Contains(log, l => l.Contains("not accepted") && l.Contains("500"));
    }
}
