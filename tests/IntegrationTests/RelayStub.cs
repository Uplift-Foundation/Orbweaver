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
    // CB-337: one accepted socket and the request it came in on. Every reply
    // goes out on the socket whose frame it answers; nothing is addressed to
    // "the newest socket", which is what let a socket accepted mid-handshake
    // take another desktop's welcome.
    private sealed record Session(WebSocket Ws, HttpListenerContext Ctx);

    // "The desktop": whichever session was last welcomed, set when its hello is
    // answered and never on accept, so a socket that has not said hello cannot
    // be the target of a frame a test sends. Read through Volatile, cleared
    // only by compare-exchange against the session that set it.
    private Session? _current;
    private static readonly TimeSpan IoDeadline = TimeSpan.FromSeconds(10);

    public Uri Base { get; }
    public string DeviceId { get; set; } = "dsk_stub000000000001";
    public HashSet<string> ValidTokens { get; } = new();
    public int Registrations;
    public int Connections;
    public const int MaxFrameBytes = 64 * 1024;   // relay Limits.MaxFrameBytes
    public int TooBigCloses;
    public string? LastIdPub;
    public string HelloCode { get; set; } = "welcome";   // or "unauthorized" / "version"
    public bool AcceptSockets { get; set; } = true;
    public bool AckUnlinks { get; set; } = true;
    public int RefuseUnlinks;
    public TimeSpan RegisterDelay { get; set; }   // makes concurrent registrations overlap
    private readonly Dictionary<string, string> _tokenByIdPub = new();   // this many unlinks are answered err rate_limited ref unlink, unprocessed
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
                Interlocked.Increment(ref Connections);
                await SocketSession(new Session(ws, ctx));
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
        await Task.Delay(RegisterDelay);
        var token = "token-" + Interlocked.Increment(ref Registrations);
        lock (ValidTokens)
        {
            // As the relay does (addendum 1): the same identity key
            // registering again kills its previous token at that moment.
            var key = json.GetProperty("id_pub").GetString()!;
            if (_tokenByIdPub.TryGetValue(key, out var old)) ValidTokens.Remove(old);
            _tokenByIdPub[key] = token;
            ValidTokens.Add(token);
        }
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

    private static async Task SendOn(Session me, string frame)
    {
        // A deadline, because a send with none that never completes hangs the
        // whole suite with no stack (CB-337); it bounds a failure, never a pass.
        using var cts = new CancellationTokenSource(IoDeadline);
        await me.Ws.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, cts.Token);
    }

    private async Task SocketSession(Session me)
    {
        var ws = me.Ws;
        var buffer = new byte[8192];
        while (ws.State == WebSocketState.Open)
        {
            // The real relay's read (Router.ReadTextAsync): fragments assembled
            // to the end of the message, and anything over its 64 KB frame cap
            // closes the socket with MessageTooBig (CB-316 — the cap a large
            // backfill once ran into, which this stub used not to have).
            using var message = new MemoryStream();
            WebSocketReceiveResult r;
            var tooBig = false;
            try
            {
                do
                {
                    r = await ws.ReceiveAsync(buffer, _stop.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    message.Write(buffer, 0, r.Count);
                    if (message.Length > MaxFrameBytes) { tooBig = true; break; }
                } while (!r.EndOfMessage);
            }
            catch (Exception) { break; }
            if (r.MessageType == WebSocketMessageType.Close) break;
            if (tooBig)
            {
                Interlocked.Increment(ref TooBigCloses);
                try { using var cts = new CancellationTokenSource(IoDeadline); await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame too large", cts.Token); } catch { }
                break;
            }
            var text = Encoding.UTF8.GetString(message.ToArray());
            await Received.Writer.WriteAsync(text);

            var tName = JsonDocument.Parse(text).RootElement.TryGetProperty("t", out var tt) ? tt.GetString() : null;
            if (tName == "unlink" && RefuseUnlinks > 0)
            {
                RefuseUnlinks--;
                await SendOn(me, JsonSerializer.Serialize(new { t = "err", @ref = "unlink", code = "rate_limited" }));
                continue;
            }
            if (tName == "unlink" && AckUnlinks)
                await SendOn(me, JsonSerializer.Serialize(new { t = "ok", @ref = "unlink", code = "unlinked" }));
            if (tName == "link")
                await SendOn(me, JsonSerializer.Serialize(new { t = "ok", @ref = "link", code = "linked" }));
            if (JsonDocument.Parse(text).RootElement.TryGetProperty("t", out var t) && t.GetString() == "hello")
            {
                var token = JsonDocument.Parse(text).RootElement.GetProperty("token").GetString() ?? "";
                bool valid;
                lock (ValidTokens) valid = ValidTokens.Contains(token);
                if (HelloCode == "welcome" && valid)
                {
                    // Before the welcome goes out, so a test that sees the
                    // desktop connected can already address it.
                    Volatile.Write(ref _current, me);
                    await SendOn(me, JsonSerializer.Serialize(new { t = "ok", @ref = "hello", code = "welcome", links = Array.Empty<string>() }));
                }
                else
                {
                    var code = HelloCode == "welcome" ? "unauthorized" : HelloCode;
                    await SendOn(me, JsonSerializer.Serialize(new { t = "err", @ref = "hello", code }));
                    using var cts = new CancellationTokenSource(IoDeadline);
                    await ws.CloseAsync(code == "version" ? (WebSocketCloseStatus)4002 : (WebSocketCloseStatus)4001, code, cts.Token);
                    break;
                }
            }
        }
        Interlocked.CompareExchange(ref _current, null, me);
    }

    private Session Desktop() => Volatile.Read(ref _current) ?? throw new InvalidOperationException("no desktop connected");

    public async Task Send(string frame)
    {
        using var cts = new CancellationTokenSource(IoDeadline);
        await Desktop().Ws.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, cts.Token);
    }

    public async Task SendRaw(byte[] payload, WebSocketMessageType type)
    {
        using var cts = new CancellationTokenSource(IoDeadline);
        await Desktop().Ws.SendAsync(payload, type, true, cts.Token);
    }

    public async Task DropConnection()
    {
        // WebSocket.Abort alone leaves HttpListener's TCP connection open (the
        // client then only notices through its silence limit, which is the
        // half-open case and a different test); aborting the response under
        // it is what a dropped network looks like from the desktop.
        // One snapshot, and only that session is cleared, by compare-exchange: a
        // reconnect that lands between the abort and the clear keeps its own.
        if (Volatile.Read(ref _current) is { } me)
        {
            me.Ws.Abort();
            me.Ctx.Response.Abort();
            Interlocked.CompareExchange(ref _current, null, me);
        }
        await Task.Yield();
    }

    public async Task CloseConnection()
    {
        if (Volatile.Read(ref _current) is { } me && me.Ws.State == WebSocketState.Open)
        {
            using var cts = new CancellationTokenSource(IoDeadline);
            await me.Ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
        }
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
        Volatile.Read(ref _current)?.Ws.Abort();
        // Close alone: QA's stack traces were on the Close() after a Stop(),
        // and Close already stops and releases the prefix.
        _listener.Close();
        // Bounded, and a timeout is not swallowed: it names this await (CB-337).
        try { await _accept.WaitAsync(IoDeadline); } catch (TimeoutException) { throw; } catch { }
    }
}
