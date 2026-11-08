using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Orbweaver.Tests;

// CB-314: a stand-in for the phone relay on loopback, speaking plan §2.1's
// HTTP and socket grammar over a real HttpListener, so RelayClient is tested
// through a real ClientWebSocket and a real HttpClient rather than fakes of
// either. It does exactly what the desktop needs and nothing the relay's own
// suite owns: it checks the registration signature, hands out tokens, issues
// offers, welcomes or refuses a hello, records every frame the desktop sends,
// and lets a test push frames back.
internal sealed class RelayStub : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    private WebSocket? _socket;
    private HttpListenerContext? _socketContext;

    public Uri Base { get; }
    public string DeviceId { get; set; } = "dsk_stub000000000001";
    public HashSet<string> ValidTokens { get; } = new();
    public int Registrations;
    public int Connections;
    public string? LastIdPub;
    public string HelloCode { get; set; } = "welcome";   // or "unauthorized" / "version"
    public bool AcceptSockets { get; set; } = true;
    public bool AckUnlinks { get; set; } = true;
    public int RefuseUnlinks;   // this many unlinks are answered err rate_limited ref unlink, unprocessed
    public readonly Channel<string> Received = Channel.CreateUnbounded<string>();
    public readonly ConcurrentQueue<string> Requests = new();

    public RelayStub()
    {
        // Bound once, never probed and released: see LoopbackHttp.
        (_listener, var root) = LoopbackHttp.Start();
        Base = new Uri(root);
        _accept = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        Requests.Enqueue($"{ctx.Request.HttpMethod} {path}");
        try
        {
            if (path == "/v1/socket" && ctx.Request.IsWebSocketRequest)
            {
                if (!AcceptSockets) { ctx.Response.StatusCode = 503; ctx.Response.Close(); return; }
                var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                _socketContext = ctx;
                Interlocked.Increment(ref Connections);
                await SocketSession(ws);
                return;
            }

            var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
            if (path == "/v1/register") await Register(ctx, body);
            else if (path == "/v1/offers") await Offer(ctx);
            else { ctx.Response.StatusCode = 404; ctx.Response.Close(); }
        }
        catch (Exception) { /* a torn-down test: nothing to report to */ }
    }

    private async Task Register(HttpListenerContext ctx, string body)
    {
        var json = JsonDocument.Parse(body).RootElement;
        var idPub = Convert.FromHexString(json.GetProperty("id_pub").GetString()!);
        LastIdPub = json.GetProperty("id_pub").GetString();
        if (!json.TryGetProperty("sig", out var sig))
        {
            // As the relay does (addendum 1): 32 random bytes, base64url.
            await Json(ctx, 200, new { challenge = RelayCrypto.Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) });
            return;
        }
        var challenge = json.GetProperty("challenge").GetString()!;
        if (!RelayCrypto.Verify(idPub, RelayCrypto.RegisterMessage(RelayCrypto.FromBase64Url(challenge)!), RelayCrypto.FromBase64Url(sig.GetString())))
        {
            await Json(ctx, 401, new { code = "bad_signature" });
            return;
        }
        var token = "token-" + Interlocked.Increment(ref Registrations);
        lock (ValidTokens) ValidTokens.Add(token);
        await Json(ctx, 200, new { device_id = DeviceId, token });
    }

    private async Task Offer(HttpListenerContext ctx)
    {
        var auth = ctx.Request.Headers["Authorization"] ?? "";
        bool ok;
        lock (ValidTokens) ok = auth.StartsWith("Bearer ") && ValidTokens.Contains(auth[7..]);
        if (!ok) { await Json(ctx, 401, new { code = "unauthorized" }); return; }
        await Json(ctx, 200, new { offer_id = "off_stub", expires_at = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() });
    }

    private static async Task Json(HttpListenerContext ctx, int status, object body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private async Task SocketSession(WebSocket ws)
    {
        _socket = ws;
        var buffer = new byte[256 * 1024];
        while (ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buffer, _stop.Token); }
            catch (Exception) { break; }
            if (r.MessageType == WebSocketMessageType.Close) break;
            var text = Encoding.UTF8.GetString(buffer, 0, r.Count);
            await Received.Writer.WriteAsync(text);

            var tName = JsonDocument.Parse(text).RootElement.TryGetProperty("t", out var tt) ? tt.GetString() : null;
            if (tName == "unlink" && RefuseUnlinks > 0)
            {
                RefuseUnlinks--;
                await Send(JsonSerializer.Serialize(new { t = "err", @ref = "unlink", code = "rate_limited" }));
                continue;
            }
            if (tName == "unlink" && AckUnlinks)
                await Send(JsonSerializer.Serialize(new { t = "ok", @ref = "unlink", code = "unlinked" }));
            if (tName == "link")
                await Send(JsonSerializer.Serialize(new { t = "ok", @ref = "link", code = "linked" }));
            if (JsonDocument.Parse(text).RootElement.TryGetProperty("t", out var t) && t.GetString() == "hello")
            {
                var token = JsonDocument.Parse(text).RootElement.GetProperty("token").GetString() ?? "";
                bool valid;
                lock (ValidTokens) valid = ValidTokens.Contains(token);
                if (HelloCode == "welcome" && valid)
                    await Send(JsonSerializer.Serialize(new { t = "ok", @ref = "hello", code = "welcome", links = Array.Empty<string>() }));
                else
                {
                    var code = HelloCode == "welcome" ? "unauthorized" : HelloCode;
                    await Send(JsonSerializer.Serialize(new { t = "err", @ref = "hello", code }));
                    await ws.CloseAsync(code == "version" ? (WebSocketCloseStatus)4002 : (WebSocketCloseStatus)4001, code, CancellationToken.None);
                    break;
                }
            }
        }
        if (ReferenceEquals(_socket, ws)) _socket = null;
    }

    public async Task Send(string frame)
    {
        var ws = _socket ?? throw new InvalidOperationException("no desktop connected");
        await ws.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public async Task SendRaw(byte[] payload, WebSocketMessageType type)
    {
        var ws = _socket ?? throw new InvalidOperationException("no desktop connected");
        await ws.SendAsync(payload, type, true, CancellationToken.None);
    }

    public async Task DropConnection()
    {
        // WebSocket.Abort alone leaves HttpListener's TCP connection open (the
        // client then only notices through its silence limit, which is the
        // half-open case and a different test); aborting the response under
        // it is what a dropped network looks like from the desktop.
        if (_socket is { } ws) ws.Abort();
        _socketContext?.Response.Abort();
        _socket = null;
        await Task.Yield();
    }

    public async Task CloseConnection()
    {
        if (_socket is { } ws && ws.State == WebSocketState.Open)
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
    }

    // The next frame the desktop sent whose `t` (or, for an envelope, class)
    // matches, within a deadline that bounds a failure and never a pass.
    public async Task<JsonElement> Next(Func<JsonElement, bool> match, int seconds = 10)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true)
        {
            var text = await Received.Reader.ReadAsync(cts.Token);
            var e = JsonDocument.Parse(text).RootElement;
            if (match(e)) return e;
        }
    }

    public Task<JsonElement> NextT(string t, int seconds = 10) =>
        Next(e => e.TryGetProperty("t", out var v) && v.GetString() == t, seconds);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _socket?.Abort();
        // Close alone: QA's stack traces were on the Close() after a Stop(),
        // and Close already stops and releases the prefix.
        _listener.Close();
        try { await _accept; } catch { }
    }
}
