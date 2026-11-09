using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>
/// CB-314: RelayClient over real loopback — a real HttpClient registering
/// against RelayStub, a real ClientWebSocket speaking hello, presence, roster
/// and delta, and every way a connection ends: the relay drops it, closes it,
/// goes silent, sends a frame too big, refuses the token or the version.
/// Every wait has a deadline that bounds a failure, never a pass.
/// </summary>
public class RelayClientTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb-relay-client-" + Guid.NewGuid());
    private RelayStub _stub = null!;
    private RelayClient? _client;

    private sealed class MemoryStore : IRelaySecretStore
    {
        private readonly Dictionary<string, byte[]> _items = new();
        public string? Protect(string name, byte[] secret) { lock (_items) _items[name] = secret; return "mem:" + name; }
        public byte[]? Unprotect(string reference) { lock (_items) return _items.TryGetValue(reference[4..], out var v) ? v : null; }
        public void Forget(string reference) { }
    }

    private readonly MemoryStore _store = new();

    public Task InitializeAsync()
    {
        _stub = new RelayStub();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.StopAsync();
        _client?.Dispose();
        await _stub.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private RelayIdentity Identity() => new(_dir, _store);

    private RelayClient Client(RelayIdentity identity, TimeSpan? ping = null, TimeSpan? silence = null, PromptPipeline.Seams? prompts = null,
        RelayRemote.Seams? remote = null)
    {
        var options = new RelayClient.Options(_stub.Base, "mac", "9.9.9", "Test Mac")
        {
            PingInterval = ping ?? TimeSpan.FromMinutes(5),
            Silence = silence ?? TimeSpan.FromMinutes(5),
            MinBackoff = TimeSpan.FromMilliseconds(20),
            MaxBackoff = TimeSpan.FromMilliseconds(100),
        };
        _client = new RelayClient(identity, options, new WebSocketRelayTransport(), promptSeams: prompts, remoteSeams: remote);
        return _client;
    }

    private static async Task Until(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met in time");
            await Task.Delay(20);
        }
    }

    // A phone already paired with this desktop, with just enough of its half
    // of the crypto to open what the desktop sends it.
    private sealed record Phone(string Id, KeyPair IdKey, KeyPair KaKey);

    private static Phone PairInto(RelayIdentity identity)
    {
        var phone = new Phone("ph_00000000000000c1", GenerateKeyPair(), GenerateKeyPair());
        identity.AddPhone(phone.Id, "Test iPhone", phone.IdKey.Public, phone.KaKey.Public);
        return phone;
    }

    private static JsonElement OpenAsPhone(JsonElement envelope, Phone phone, RelayIdentity identity)
    {
        var ct = envelope.GetProperty("ct").GetString()!;
        var nonce = PeekNonce(ct)!.Value;
        var desktopId = envelope.GetProperty("from").GetString()!;
        var basis = SessionBase(SharedSecret(phone.KaKey, identity.Keys().Agreement.Public), desktopId, phone.Id);
        var plain = Open(EpochKey(basis, nonce.Epoch), ct, Aad(desktopId, phone.Id, envelope.GetProperty("class").GetString()!), DesktopToPhone);
        Assert.NotNull(plain);
        return JsonDocument.Parse(Encoding.UTF8.GetString(plain!)).RootElement;
    }

    private static bool IsEnvelope(JsonElement e) => e.TryGetProperty("v", out _);

    [Fact]
    public async Task Registers_with_a_signed_challenge_connects_and_is_welcomed()
    {
        var identity = Identity();
        var client = Client(identity);
        var states = new List<RelayClient.Status>();
        client.StateChanged += () => { lock (states) states.Add(client.State); };
        client.Start();
        client.Start();                                            // idempotent

        var hello = await _stub.NextT("hello");
        Assert.Equal(("token-1", "dsk_stub000000000001", "mac", "9.9.9", 2),
            (hello.GetProperty("token").GetString(), hello.GetProperty("device_id").GetString(),
             hello.GetProperty("platform").GetString(), hello.GetProperty("app_version").GetString(), hello.GetProperty("proto").GetInt32()));
        await Until(() => client.State == RelayClient.Status.Connected);
        Assert.Equal(Hex(identity.Keys().Identity.Public), _stub.LastIdPub);
        Assert.Equal("dsk_stub000000000001", identity.DeviceId);
        lock (states) Assert.Equal([RelayClient.Status.Registering, RelayClient.Status.Connecting, RelayClient.Status.Connected], states);

        await client.StopAsync();
        Assert.Equal(RelayClient.Status.Off, client.State);
    }

    [Fact]
    public async Task An_offer_comes_over_https_and_its_qr_names_this_relay()
    {
        var identity = Identity();
        var client = Client(identity);
        var offer = await client.BeginPairingAsync();             // registers first: no token yet
        Assert.Equal("off_stub", offer.OfferId);
        Assert.True(offer.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(4));
        Assert.StartsWith($"orbweaver://pair?r=127.0.0.1:{_stub.Base.Port}&d=dsk_stub000000000001&o=off_stub&k=", offer.QrText);
        client.CancelPairing();
        Assert.Contains("POST /v1/offers", _stub.Requests);
    }

    // Found on the installed app: the connection loop and the first "Pair
    // phone…" both found no token and both registered; the relay killed the
    // first token when the second registration landed, so the first offer
    // failed 401 and the socket was refused. Registration is serialised now.
    [Fact]
    public async Task Starting_and_pairing_at_once_registers_once_and_both_work()
    {
        _stub.RegisterDelay = TimeSpan.FromMilliseconds(300);
        var identity = Identity();
        var client = Client(identity);
        client.Start();
        var offer = await client.BeginPairingAsync();
        Assert.Equal("off_stub", offer.OfferId);
        await Until(() => client.State == RelayClient.Status.Connected);
        Assert.Equal(1, _stub.Registrations);
    }

    [Fact]
    public async Task A_connect_that_hangs_times_out_and_is_retried()
    {
        var identity = Identity();
        identity.Keys();
        identity.SetRegistration("dsk_stub000000000001", "tok");
        var attempts = 0;
        var options = new RelayClient.Options(_stub.Base, "mac", "9.9.9", "Test Mac")
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(150),
            MinBackoff = TimeSpan.FromMilliseconds(10),
            MaxBackoff = TimeSpan.FromMilliseconds(20),
        };
        _client = new RelayClient(identity, options, new Hangs(() => Interlocked.Increment(ref attempts)));
        _client.Start();
        await Until(() => attempts >= 3);
        Assert.NotNull(_client.LastError);
        Assert.Equal(RelayClient.Status.Connecting, _client.State);
    }

    // The connection loop must outlive any exception, not only the ones it
    // expects: one it did not list used to escape RunAsync and end the loop
    // silently — no reconnect, nothing logged, answers queued at the relay for
    // a desktop that never came back (seen as "logged nothing after the
    // socket closed" in QA's run). First a connect that throws something
    // unexpected, then a socket that does mid-session; the client must come
    // back both times.
    [Fact]
    public async Task An_unexpected_exception_does_not_end_the_connection_loop()
    {
        var identity = Identity();
        identity.Keys();
        identity.SetRegistration("dsk_stub000000000001", "tok");
        var options = new RelayClient.Options(_stub.Base, "mac", "9.9.9", "Test Mac")
        {
            MinBackoff = TimeSpan.FromMilliseconds(10),
            MaxBackoff = TimeSpan.FromMilliseconds(20),
        };
        var flaky = new Flaky(new WebSocketRelayTransport());
        _client = new RelayClient(identity, options, flaky);
        _client.Start();
        await _stub.NextT("hello");                              // came back after the throwing connect
        await Until(() => _client.State == RelayClient.Status.Connected);
        flaky.ThrowOnNextReceive = true;
        await _stub.Send("""{"t":"ping"}""");                    // the receive that throws
        await _stub.NextT("hello");                              // and came back again
        Assert.True(flaky.Connects >= 2);
        Assert.Equal(RelayClient.Status.Connected, await WaitState(_client, RelayClient.Status.Connected));
    }

    private static async Task<RelayClient.Status> WaitState(RelayClient c, RelayClient.Status want)
    {
        await Until(() => c.State == want);
        return c.State;
    }

    private sealed class Flaky(IRelayTransport inner) : IRelayTransport
    {
        public int Connects;
        public volatile bool ThrowOnNextReceive;

        public async Task<IRelaySocket> ConnectAsync(Uri uri, CancellationToken token)
        {
            if (Interlocked.Increment(ref Connects) == 1) throw new InvalidOperationException("not a connection error the loop lists");
            return new Socket(this, await inner.ConnectAsync(uri, token));
        }

        private sealed class Socket(Flaky owner, IRelaySocket inner) : IRelaySocket
        {
            public Task SendAsync(string frame, CancellationToken token) => inner.SendAsync(frame, token);
            public async Task<string?> ReceiveAsync(CancellationToken token)
            {
                var text = await inner.ReceiveAsync(token);
                if (owner.ThrowOnNextReceive) { owner.ThrowOnNextReceive = false; throw new ObjectDisposedException("socket"); }
                return text;
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private sealed class Hangs(Action counted) : IRelayTransport
    {
        public async Task<IRelaySocket> ConnectAsync(Uri uri, CancellationToken token)
        {
            counted();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class RefusingStore : IRelaySecretStore
    {
        public string? Protect(string name, byte[] secret) => "mem:" + name;
        public byte[]? Unprotect(string reference) => throw new RelaySecretUnavailableException("the Keychain refused the relay keys (test)");
        public void Forget(string reference) => throw new InvalidOperationException("nothing may be forgotten");
    }

    // An unsigned build against the signed app's Keychain items: the client
    // says why it cannot connect and keeps trying, and destroys nothing.
    [Fact]
    public async Task Refused_keys_are_reported_and_nothing_is_regenerated()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, RelayIdentity.FileName);
        File.WriteAllText(file, """{"device_id":"dsk_signed","id_pub":"04aa","id_priv":"mem:identity","ka_pub":"04bb","ka_priv":"mem:agreement","phones":[]}""");
        var before = File.ReadAllText(file);
        var client = Client(new RelayIdentity(_dir, new RefusingStore()));
        client.Start();
        await Until(() => client.LastError?.Contains("Keychain refused") == true);
        Assert.NotEqual(RelayClient.Status.Connected, client.State);
        Assert.Equal(before, File.ReadAllText(file));
        Assert.Equal(0, _stub.Registrations);
    }

    [Fact]
    public async Task An_offer_refused_for_a_stale_token_forgets_the_token()
    {
        var identity = Identity();
        identity.Keys();
        identity.SetRegistration("dsk_stub000000000001", "stale");
        var client = Client(identity);
        await Assert.ThrowsAsync<RelayHttpException>(() => client.BeginPairingAsync());
        Assert.Null(identity.Token);
    }

    // CB-315: the amber loop over a real socket. A waiting session's dialog
    // goes out as push amber with an e the phone can open; the phone's signed
    // answer comes back sealed, the fake pane gets exactly one key, and resolved
    // goes out as push resolve under the same id.
    [Fact]
    public async Task A_prompt_goes_out_amber_and_a_signed_answer_presses_once_and_resolves()
    {
        var identity = Identity();
        identity.Keys();
        var phone = PairInto(identity);
        var screen = "────────────────────────────────────────\n Bash command\n   dotnet test\n Do you want to proceed?\n ❯ 1. Yes\n   2. No\n";
        var keys = new List<string>();
        var seams = new PromptPipeline.Seams(
            _ => Task.FromResult<string?>(screen),
            (_, k) => { lock (keys) keys.Add(k); screen = "> \n"; return Task.FromResult(true); },
            (_, _) => Task.FromResult(true),
            () => DateTimeOffset.UtcNow, _ => Task.CompletedTask, _ => { });
        var client = Client(identity, prompts: seams);
        client.Start();
        await _stub.NextT("hello");
        await Until(() => client.State == RelayClient.Status.Connected);

        await client.OnStatuses([("s1", new SessionStatus { State = "waiting", Source = SessionSource.ClaudeCode, Title = "proj" })]);
        var amber = await _stub.Next(IsEnvelope);
        Assert.Equal(("action", "amber"), (amber.GetProperty("class").GetString(), amber.GetProperty("push").GetString()));
        var prompt = OpenAsPhone(amber, phone, identity);
        var rid = prompt.GetProperty("request_id").GetString()!;
        Assert.Equal(rid, amber.GetProperty("id").GetString());
        var push = JsonDocument.Parse(PushSeal.Open(phone.KaKey, amber.GetProperty("e").GetString())!).RootElement;
        Assert.Equal(rid, push.GetProperty("request_id").GetString());

        await _stub.Send($$"""{"t":"ok","ref":"{{rid}}","code":"pushed","apns_ms":9}""");
        await _stub.Send($$"""{"t":"ok","ref":"{{rid}}","code":"push_throttled"}""");

        var exp = prompt.GetProperty("expires_at").GetInt64();
        var desktopId = amber.GetProperty("from").GetString()!;
        var basis = SessionBase(SharedSecret(phone.KaKey, identity.Keys().Agreement.Public), desktopId, phone.Id);
        ulong counter = 0;
        Task SendAnswer(KeyPair signer)
        {
            var nonce = Base64Url(RandomNumberGenerator.GetBytes(16));
            var sig = Base64Url(Sign(signer, AnswerSigning.Message(AnswerSigning.Body(rid, "s1", "approve", "", nonce, exp))));
            var answer = JsonSerializer.Serialize(new
            {
                t = "answer", request_id = rid, session_id = "s1", action = "approve", scope = "", note = "", nonce,
                expires_at = exp, screen_hash = prompt.GetProperty("screen_hash").GetString(), sig,
            });
            var ct = Seal(EpochKey(basis, 0), Nonce(PhoneToDesktop, 0, counter++), Encoding.UTF8.GetBytes(answer), Aad(phone.Id, desktopId, "action"));
            return _stub.Send(JsonSerializer.Serialize(new { v = 2, id = "a" + counter, from = phone.Id, to = desktopId, ts = 0, @class = "action", push = "none", ck = "", ct }));
        }

        // Signed by some other key: refused, sealed, to this phone, nothing pressed.
        await SendAnswer(GenerateKeyPair());
        var refusal = await _stub.Next(IsEnvelope);
        Assert.Equal("none", refusal.GetProperty("push").GetString());
        Assert.Equal(("err", "bad_signature"), (OpenAsPhone(refusal, phone, identity).GetProperty("t").GetString(), OpenAsPhone(refusal, phone, identity).GetProperty("code").GetString()));
        lock (keys) Assert.Empty(keys);

        await SendAnswer(phone.IdKey);

        var resolve = await _stub.Next(IsEnvelope);
        Assert.Equal(("resolve", rid), (resolve.GetProperty("push").GetString(), resolve.GetProperty("id").GetString()));
        // The resolve's ck is the amber's, so the passive alert replaces the
        // amber in its thread; its e is the sealed resolve, to this phone.
        Assert.Equal(amber.GetProperty("ck").GetString(), resolve.GetProperty("ck").GetString());
        var sealedResolve = JsonDocument.Parse(PushSeal.Open(phone.KaKey, resolve.GetProperty("e").GetString())!).RootElement;
        Assert.Equal(("resolved", "phone:" + phone.Id, "proj on Test Mac"),
            (sealedResolve.GetProperty("t").GetString(), sealedResolve.GetProperty("by").GetString(), sealedResolve.GetProperty("label").GetString()));
        var resolved = OpenAsPhone(resolve, phone, identity);
        Assert.Equal(("resolved", "phone:" + phone.Id), (resolved.GetProperty("t").GetString(), resolved.GetProperty("by").GetString()));
        lock (keys) Assert.Equal(["1"], keys);
    }

    // CB-316: the v4 round trip through a real socket — a sealed watch from the
    // phone becomes ok watching and a chat window, an input is typed and
    // answered, a request whose handler throws costs only itself, usage goes
    // out as state with the usage ck, and a dropped socket ends every watch.
    [Fact]
    public async Task A_watch_and_an_input_round_trip_and_usage_follows_the_readings()
    {
        var identity = Identity();
        identity.Keys();
        var phone = PairInto(identity);
        var typed = new List<string>();
        var screen = PaneFixtures.MacPromptAfterEsc;
        var lines = new List<TranscriptWatch.WireLine> { TranscriptWatch.Line("user", "hello", 1) };
        var throwOnce = true;
        var remote = new RelayRemote.Seams(
            () => DateTimeOffset.UtcNow, d => Task.Delay(d),
            s => s.Title == "boom" && throwOnce ? throw new InvalidOperationException("one bad request")
                : new TranscriptWatcher.Seams(() => "/t", p => new TranscriptWatcher.Stamp(p, lines.Count, DateTime.UnixEpoch), _ => lines),
            new RelayInput(new RelayInput.Seams(_ => TerminalTyping.Route.Tmux, _ => Task.FromResult<string?>(screen),
                (_, t) => { lock (typed) typed.Add(t); return Task.FromResult(true); }, _ => Task.CompletedTask)),
            new RelayRemote.LaunchSeams(false, _ => null, _ => false, () => "/bin/sh", (_, _) => (null, null), _ => null),
            _ => { });
        var client = Client(identity, prompts: new PromptPipeline.Seams(_ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult(false), (_, _) => Task.FromResult(false), () => DateTimeOffset.UtcNow, _ => Task.CompletedTask, _ => { }),
            remote: remote);
        client.Start();
        await _stub.NextT("hello");
        await Until(() => client.State == RelayClient.Status.Connected);
        await client.OnStatuses([("s1", new SessionStatus { Source = SessionSource.ClaudeCode, Title = "proj" }),
                                 ("s2", new SessionStatus { Source = SessionSource.ClaudeCode, Title = "boom" })]);
        await _stub.Send(JsonSerializer.Serialize(new { t = "presence", device_id = phone.Id, online = true, last_seen = 1 }));
        var roster = await _stub.Next(IsEnvelope);
        Assert.Equal("roster", OpenAsPhone(roster, phone, identity).GetProperty("t").GetString());

        var desktopId = roster.GetProperty("from").GetString()!;
        var basis = SessionBase(SharedSecret(phone.KaKey, identity.Keys().Agreement.Public), desktopId, phone.Id);
        ulong counter = 0;
        Task Send(string id, string body)
        {
            var ct = Seal(EpochKey(basis, 0), Nonce(PhoneToDesktop, 0, counter++), Encoding.UTF8.GetBytes(body), Aad(phone.Id, desktopId, "action"));
            return _stub.Send(JsonSerializer.Serialize(new { v = 2, id, from = phone.Id, to = desktopId, ts = 0, @class = "action", push = "none", ck = "", ct }));
        }

        // A handler that throws is logged and answered with nothing; the next
        // request on the same connection is still served.
        await Send("w0", """{"t":"watch","session_id":"s2"}""");
        await Send("w1", """{"t":"watch","session_id":"s1"}""");
        var ok = await _stub.Next(IsEnvelope);
        var okBody = OpenAsPhone(ok, phone, identity);
        Assert.Equal(("ok", "w1", "watching", 1), (okBody.GetProperty("t").GetString(), okBody.GetProperty("ref").GetString(),
            okBody.GetProperty("code").GetString(), okBody.GetProperty("total").GetInt32()));
        var window = await _stub.Next(IsEnvelope);
        Assert.Equal(("chat", ok.GetProperty("ck").GetString()), (window.GetProperty("class").GetString(), window.GetProperty("ck").GetString()));
        Assert.Equal("hello", OpenAsPhone(window, phone, identity).GetProperty("lines")[0].GetProperty("text").GetString());

        await Send("i1", """{"t":"input","session_id":"s1","text":"go on"}""");
        var typedReply = OpenAsPhone(await _stub.Next(IsEnvelope), phone, identity);
        Assert.Equal(("typed", "i1"), (typedReply.GetProperty("code").GetString(), typedReply.GetProperty("ref").GetString()));
        lock (typed) Assert.Equal(["go on"], typed);

        // A tail, through the client's own poll loop.
        lines.Add(TranscriptWatch.Line("assistant", "done", 2));
        var tail = OpenAsPhone(await _stub.Next(IsEnvelope), phone, identity);
        Assert.Equal((true, 1), (tail.GetProperty("live").GetBoolean(), tail.GetProperty("from_line").GetInt32()));

        // Usage: one frame for a change, none for the same reading again.
        var accounts = new List<RelayUsage.Account> { new("me", "claude", "max", 10, 100, null, null, 1, true) };
        RelayService.UseForTests(null);
        RelayService.OnUsage(accounts);                                     // no client: nothing, and no throw
        RelayService.UseForTests(client);
        try { RelayService.OnUsage(accounts); }
        finally { RelayService.UseForTests(null); }
        var usage = await _stub.Next(IsEnvelope);
        Assert.Equal(("state", "none"), (usage.GetProperty("class").GetString(), usage.GetProperty("push").GetString()));
        Assert.Equal("usage", OpenAsPhone(usage, phone, identity).GetProperty("t").GetString());
        client.OnUsage(accounts);
        client.OnUsage([accounts[0] with { WindowPct = 85 }]);
        var limit = await _stub.Next(IsEnvelope);
        Assert.Equal("limit", limit.GetProperty("push").GetString());

        Assert.Equal(1, client.Remote.WatchCount);
        await _stub.CloseConnection();
        await Until(() => client.Remote.WatchCount == 0);
    }

    // CB-316: a large backfill once never reached the phone, and the desk's
    // link flapped about once a second. The cause, measured on a real 6.7 MB
    // transcript: a full 48,000-byte window sealed to a 64,000-character ct and
    // the default JSON encoder wrote every base64 `+` as \u002B, so the envelope
    // was 69,176 bytes and the relay closed the desk for an oversize frame.
    // Here a backfill built to be as escape-heavy as real tool output goes
    // through the stub relay, which now enforces the relay's own 64 KB cap
    // exactly as Router.ReadTextAsync does: every window must arrive, every
    // frame must fit, and the link must stay up on its first connection.
    [Fact]
    public async Task A_large_escape_heavy_backfill_arrives_whole_and_the_link_stays_up()
    {
        var identity = Identity();
        identity.Keys();
        var phone = PairInto(identity);
        var chunk = string.Concat(Enumerable.Repeat("say \"hi\" \\ C:\\x\n\ttab ", 300));
        var lines = Enumerable.Range(0, 199)
            .Select(i => TranscriptWatch.Line(i % 2 == 0 ? "user" : "system", (i + " " + chunk)[..Math.Min(chunk.Length, 7990)], i))
            .Append(TranscriptWatch.Line("system", new string('\u0001', 8000), 999))   // 6 escaped bytes per character
            .ToList();
        var remote = new RelayRemote.Seams(
            () => DateTimeOffset.UtcNow, d => Task.Delay(d),
            _ => new TranscriptWatcher.Seams(() => "/big", p => new TranscriptWatcher.Stamp(p, lines.Count, DateTime.UnixEpoch), _ => lines),
            new RelayInput(new RelayInput.Seams(_ => TerminalTyping.Route.Tmux, _ => Task.FromResult<string?>(null),
                (_, _) => Task.FromResult(false), _ => Task.CompletedTask)),
            new RelayRemote.LaunchSeams(false, _ => null, _ => false, () => "/bin/sh", (_, _) => (null, null), _ => null),
            _ => { });
        var client = Client(identity, prompts: new PromptPipeline.Seams(_ => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult(false), (_, _) => Task.FromResult(false), () => DateTimeOffset.UtcNow, _ => Task.CompletedTask, _ => { }),
            remote: remote);
        client.Start();
        await _stub.NextT("hello");
        await Until(() => client.State == RelayClient.Status.Connected);
        await client.OnStatuses([("big", new SessionStatus { Source = SessionSource.ClaudeCode, Title = "big" })]);
        await _stub.Send(JsonSerializer.Serialize(new { t = "presence", device_id = phone.Id, online = true, last_seen = 1 }));
        var roster = await _stub.Next(IsEnvelope);
        var desktopId = roster.GetProperty("from").GetString()!;
        var basis = SessionBase(SharedSecret(phone.KaKey, identity.Keys().Agreement.Public), desktopId, phone.Id);
        var watch = Seal(EpochKey(basis, 0), Nonce(PhoneToDesktop, 0, 0), Encoding.UTF8.GetBytes("""{"t":"watch","session_id":"big"}"""), Aad(phone.Id, desktopId, "action"));
        await _stub.Send(JsonSerializer.Serialize(new { v = 2, id = "w-big", from = phone.Id, to = desktopId, ts = 0, @class = "action", push = "none", ck = "", ct = watch }));

        var ok = OpenAsPhone(await _stub.Next(IsEnvelope), phone, identity);
        Assert.Equal(("watching", 200), (ok.GetProperty("code").GetString(), ok.GetProperty("total").GetInt32()));
        var received = 0;
        var windows = 0;
        while (received < 200)
        {
            var env = await _stub.Next(IsEnvelope);
            Assert.True(Encoding.UTF8.GetByteCount(env.GetRawText()) <= RelayStub.MaxFrameBytes);
            var w = OpenAsPhone(env, phone, identity);
            Assert.Equal(received, w.GetProperty("from_line").GetInt32());   // contiguous: nothing lost
            received += w.GetProperty("lines").GetArrayLength();
            windows++;
        }
        Assert.True(windows > 20, $"meant to need many full windows, got {windows}");
        Assert.Equal(0, _stub.TooBigCloses);
        Assert.Equal(1, _stub.Connections);
        Assert.Equal(RelayClient.Status.Connected, client.State);
    }

    // Lead's decision: every pending prompt goes out again on every welcome.
    // The relay's 30-second alert throttle (frames-v3 rate_limits.amber) is
    // what keeps that from being a second alert: this stub plays the relay's
    // half — pushed for the first amber of an id, push_throttled for a repeat
    // inside 30 s — and the client must see exactly one pushed.
    [Fact]
    public async Task A_repeat_on_reconnect_goes_out_with_the_same_id_and_comes_back_throttled()
    {
        var identity = Identity();
        identity.Keys();
        var phone = PairInto(identity);
        var screen = "────────────────────────────────────────\n Bash command\n   dotnet test\n Do you want to proceed?\n ❯ 1. Yes\n   2. No\n";
        var client = Client(identity, prompts: new PromptPipeline.Seams(
            _ => Task.FromResult<string?>(screen), (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(true),
            () => DateTimeOffset.UtcNow, _ => Task.CompletedTask, _ => { }));
        var replies = new List<(string Id, string Code)>();
        client.PushReplied += (id, code, _) => { lock (replies) replies.Add((id, code)); };
        client.Start();
        await _stub.NextT("hello");
        await Until(() => client.State == RelayClient.Status.Connected);
        await client.OnStatuses([("s1", new SessionStatus { State = "waiting", Source = SessionSource.ClaudeCode, Title = "proj" })]);

        var pushedAt = new Dictionary<string, DateTime>();
        async Task<string> RelayAnswers()
        {
            var amber = await _stub.Next(e => IsEnvelope(e) && e.GetProperty("push").GetString() == "amber");
            var id = amber.GetProperty("id").GetString()!;
            var code = pushedAt.TryGetValue(id, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(30) ? "push_throttled" : "pushed";
            if (code == "pushed") pushedAt[id] = DateTime.UtcNow;
            await _stub.Send(JsonSerializer.Serialize(new { t = "ok", @ref = id, code }));
            return id;
        }

        var first = await RelayAnswers();
        await _stub.DropConnection();
        await _stub.NextT("hello");
        var second = await RelayAnswers();
        Assert.Equal(first, second);
        await Until(() => { lock (replies) return replies.Count == 2; });
        lock (replies) Assert.Equal([(first, "pushed"), (first, "push_throttled")], replies);
    }

    // A prompt minted before the socket is up produces no frame; the welcome
    // sends it.
    [Fact]
    public async Task A_prompt_minted_before_the_relay_welcomes_goes_out_on_the_welcome()
    {
        var identity = Identity();
        identity.Keys();
        var phone = PairInto(identity);
        var screen = "────────────────────────────────────────\n Bash command\n   dotnet test\n Do you want to proceed?\n ❯ 1. Yes\n   2. No\n";
        var client = Client(identity, prompts: new PromptPipeline.Seams(
            _ => Task.FromResult<string?>(screen), (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(true),
            () => DateTimeOffset.UtcNow, _ => Task.CompletedTask, _ => { }));
        await client.OnStatuses([("s1", new SessionStatus { State = "waiting", Source = SessionSource.ClaudeCode, Title = "proj" })]);
        Assert.Equal(1, client.Prompts.PendingCount);

        client.Start();
        await _stub.NextT("hello");
        var amber = await _stub.Next(IsEnvelope);
        Assert.Equal("amber", amber.GetProperty("push").GetString());
        Assert.Equal("prompt", OpenAsPhone(amber, phone, identity).GetProperty("t").GetString());
    }

    // Gate (a): the notification extension's /v1/ack reaches the desktop as a
    // control envelope with a plaintext ct; the client raises it with the
    // request id and the phone's at_ms.
    [Fact]
    public async Task A_push_ack_from_the_phone_is_raised_with_its_request_id()
    {
        var identity = Identity();
        identity.Keys();
        var phone = PairInto(identity);
        var client = Client(identity);
        var acks = new List<(string, string, long)>();
        client.PushAcked += (p, r, t) => { lock (acks) acks.Add((p, r, t)); };
        client.Start();
        await _stub.NextT("hello");
        await Until(() => client.State == RelayClient.Status.Connected);
        var ct = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"t":"push-ack","request_id":"01JC9R0A5N2Q7W8X3E4T6Y9ZBD","at_ms":1794111792500}"""));
        await _stub.Send(JsonSerializer.Serialize(new { v = 2, id = "k1", from = phone.Id, to = _stub.DeviceId, ts = 0, @class = "control", push = "none", ck = "", ct }));
        await Until(() => { lock (acks) return acks.Count == 1; });
        lock (acks) Assert.Equal((phone.Id, "01JC9R0A5N2Q7W8X3E4T6Y9ZBD", 1794111792500L), acks[0]);
    }

    [Fact]
    public async Task With_no_phone_paired_no_pane_is_read()
    {
        var identity = Identity();
        identity.Keys();
        var captures = 0;
        var client = Client(identity, prompts: new PromptPipeline.Seams(
            _ => { captures++; return Task.FromResult<string?>(null); }, (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(true),
            () => DateTimeOffset.UtcNow, _ => Task.CompletedTask, _ => { }));
        await client.OnStatuses([("s1", new SessionStatus { State = "waiting", Source = SessionSource.ClaudeCode })]);
        Assert.Equal(0, captures);
    }

    [Fact]
    public async Task A_paired_phone_coming_online_gets_a_roster_then_scans_become_deltas()
    {
        var identity = Identity();
        identity.Keys();
        var phone = PairInto(identity);
        var client = Client(identity);
        client.OnScanned([new RelaySession("s1", "claude", "proj", "/c", "idle", null, null)]);   // before connecting: kept in the book
        client.Start();
        await _stub.NextT("hello");
        await Until(() => client.State == RelayClient.Status.Connected);

        var changed = 0;
        client.PhonesChanged += () => Interlocked.Increment(ref changed);
        await _stub.Send(JsonSerializer.Serialize(new { t = "presence", device_id = phone.Id, online = true, last_seen = 1794111792 }));
        var roster = OpenAsPhone(await _stub.Next(IsEnvelope), phone, identity);
        Assert.Equal(("roster", "s1"), (roster.GetProperty("t").GetString(), roster.GetProperty("sessions")[0].GetProperty("session_id").GetString()));
        await Until(() => client.IsOnline(phone.Id));
        Assert.True(changed > 0);

        client.OnScanned([new RelaySession("s1", "claude", "proj", "/c", "generating", null, null)]);
        var delta = OpenAsPhone(await _stub.Next(IsEnvelope), phone, identity);
        Assert.Equal(("delta", 2L, "generating"), (delta.GetProperty("t").GetString(), delta.GetProperty("seq").GetInt64(), delta.GetProperty("state").GetString()));

        await _stub.Send(JsonSerializer.Serialize(new { t = "presence", device_id = phone.Id, online = false, last_seen = 1794111800 }));
        await Until(() => !client.IsOnline(phone.Id));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794111800), client.LastSeen(phone.Id));
        Assert.Single(client.Phones);
    }

    // Found against the live relay: a hundred back-to-back flips drew
    // err rate_limited and a dropped frame. Thirty deltas queued at once must
    // all arrive, in order, and spread out under the pacer rather than in one
    // burst the relay would drop.
    [Fact]
    public async Task A_burst_of_frames_is_paced_and_none_is_dropped()
    {
        var identity = Identity();
        identity.Keys();
        var phone = PairInto(identity);
        var client = Client(identity);
        client.OnScanned([new RelaySession("s1", "claude", "proj", "/c", "idle", null, null)]);
        client.Start();
        await _stub.NextT("hello");
        await Until(() => client.State == RelayClient.Status.Connected);
        await _stub.Send(JsonSerializer.Serialize(new { t = "presence", device_id = phone.Id, online = true }));
        await _stub.Next(IsEnvelope);                              // the roster

        for (var i = 0; i < 30; i++)
            client.OnScanned([new RelaySession("s1", "claude", "proj", "/c", i % 2 == 0 ? "generating" : "idle", null, null)]);

        var started = DateTime.UtcNow;
        var seqs = new List<long>();
        for (var i = 0; i < 30; i++)
            seqs.Add(OpenAsPhone(await _stub.Next(IsEnvelope), phone, identity).GetProperty("seq").GetInt64());
        var took = DateTime.UtcNow - started;

        Assert.Equal(Enumerable.Range(2, 30).Select(i => (long)i), seqs);
        // 10 go at once, the other 20 at 35/s: at least ~0.5 s, never one burst.
        Assert.True(took >= TimeSpan.FromMilliseconds(400), $"30 frames arrived in {took.TotalMilliseconds} ms");
    }

    // Acceptance 4 across a quit, through the real client: Remove while the
    // relay is unreachable, stop the client, start a new one over the same
    // files, and the relay receives the unlink on the first connection.
    [Fact]
    public async Task An_unlink_owed_across_a_restart_reaches_the_relay()
    {
        var identity = Identity();
        identity.Keys();
        var phone = PairInto(identity);
        var first = Client(identity);
        first.Start();
        await _stub.NextT("hello");
        await Until(() => first.State == RelayClient.Status.Connected);

        _stub.AcceptSockets = false;
        await _stub.DropConnection();
        await Until(() => first.State != RelayClient.Status.Connected);
        first.Remove(phone.Id);
        await first.StopAsync();
        first.Dispose();
        _client = null;

        _stub.AcceptSockets = true;
        var reopened = new RelayIdentity(_dir, _store);
        Assert.Equal([phone.Id], reopened.PendingUnlinks);
        var second = Client(reopened);
        second.Start();
        Assert.Equal(phone.Id, (await _stub.NextT("unlink")).GetProperty("peer_id").GetString());
        await Until(() => new RelayIdentity(_dir, _store).PendingUnlinks.Count == 0);   // the stub's ok settled it
    }

    // Hana's PR #9: an unlink refused for rate was not processed, so the
    // client must send it again — after a backoff — until it lands.
    [Fact]
    public async Task An_unlink_refused_for_rate_is_sent_again_until_it_lands()
    {
        var identity = Identity();
        identity.Keys();
        var phone = PairInto(identity);
        var client = Client(identity);
        client.Start();
        await _stub.NextT("hello");
        await Until(() => client.State == RelayClient.Status.Connected);
        _stub.RefuseUnlinks = 2;

        client.Remove(phone.Id);
        for (var i = 0; i < 3; i++)
            Assert.Equal(phone.Id, (await _stub.NextT("unlink")).GetProperty("peer_id").GetString());
        await Until(() => identity.PendingUnlinks.Count == 0);                          // the third was acked
    }

    [Fact]
    public async Task Stopping_while_a_retry_waits_cancels_it()
    {
        var identity = Identity();
        identity.Keys();
        var phone = PairInto(identity);
        var waiting = new TaskCompletionSource();
        var options = new RelayClient.Options(_stub.Base, "mac", "9.9.9", "Test Mac") { PingInterval = TimeSpan.FromMinutes(5), Silence = TimeSpan.FromMinutes(5) };
        _client = new RelayClient(identity, options, new WebSocketRelayTransport(), delay: async (t, ct) =>
        {
            if (t < TimeSpan.FromMinutes(1)) waiting.TrySetResult();           // the retry's short wait, not the ping's
            await Task.Delay(Timeout.Infinite, ct);
        });
        _stub.RefuseUnlinks = 1;
        _client.Start();
        await _stub.NextT("hello");
        await Until(() => _client.State == RelayClient.Status.Connected);
        _client.Remove(phone.Id);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await _client.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([phone.Id], identity.PendingUnlinks);                               // still owed, for the next run
    }

    // The probe's --no-pacer switch: the same burst arrives whole and in
    // order, only faster — what the relay's own limiter is then tested with.
    [Fact]
    public async Task With_pacing_off_a_burst_goes_out_at_once_and_in_order()
    {
        var identity = Identity();
        identity.Keys();
        var phone = PairInto(identity);
        var options = new RelayClient.Options(_stub.Base, "mac", "9.9.9", "Test Mac") { PaceFrames = false, PingInterval = TimeSpan.FromMinutes(5), Silence = TimeSpan.FromMinutes(5) };
        // Asserted on the cause, not the clock: a wall-clock bound fails on a
        // loaded runner. Every wait the client asks for is recorded; with
        // pacing off, none may be a pacing wait (the ping's five minutes is the
        // only one expected).
        var waits = new System.Collections.Concurrent.ConcurrentBag<TimeSpan>();
        _client = new RelayClient(identity, options, new WebSocketRelayTransport(), delay: (t, ct) => { waits.Add(t); return Task.Delay(t, ct); });
        _client.OnScanned([new RelaySession("s1", "claude", "proj", "/c", "idle", null, null)]);
        _client.Start();
        await _stub.NextT("hello");
        await Until(() => _client.State == RelayClient.Status.Connected);
        await _stub.Send(JsonSerializer.Serialize(new { t = "presence", device_id = phone.Id, online = true }));
        await _stub.Next(IsEnvelope);

        for (var i = 0; i < 30; i++)
            _client.OnScanned([new RelaySession("s1", "claude", "proj", "/c", i % 2 == 0 ? "generating" : "idle", null, null)]);
        var seqs = new List<long>();
        for (var i = 0; i < 30; i++)
            seqs.Add(OpenAsPhone(await _stub.Next(IsEnvelope), phone, identity).GetProperty("seq").GetInt64());
        Assert.Equal(Enumerable.Range(2, 30).Select(i => (long)i), seqs);
        Assert.DoesNotContain(waits, w => w < TimeSpan.FromMinutes(1));
    }

    // CB-314 #11244: in the two-process end to end, the probe's PairRequested
    // continuation ran inline on the client's read loop and blocked there,
    // so a forwarded resync was never read while deltas kept flowing. Events
    // now come off a serial queue; a handler that never returns must not
    // stop the client reading.
    [Fact]
    public async Task A_handler_that_never_returns_does_not_stop_the_client_reading()
    {
        var identity = Identity();
        var client = Client(identity);
        using var release = new ManualResetEventSlim();
        var stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PairRequested += _ => { stuck.TrySetResult(); release.Wait(); };  // never returns until the end of the test
        client.Start();
        await _stub.NextT("hello");
        await Until(() => client.State == RelayClient.Status.Connected);
        var offer = await client.BeginPairingAsync();

        var phone = new Phone("ph_00000000000000c3", GenerateKeyPair(), GenerateKeyPair());
        var q = offer.QrText[(offer.QrText.IndexOf('?') + 1)..].Split('&').Select(kv => kv.Split('=', 2)).ToDictionary(kv => kv[0], kv => kv[1]);
        var body = $"{{\"t\":\"pair.request\",\"phone_id\":\"{phone.Id}\",\"name\":\"P\",\"id_pub\":\"{Hex(phone.IdKey.Public)}\",\"ka_pub\":\"{Hex(phone.KaKey.Public)}\"}}";
        var ct = Seal(PairKey(FromBase64Url(q["s"])!, q["o"], q["d"], phone.Id), Nonce(PhoneToDesktop, 0, 0), Encoding.UTF8.GetBytes(body), Aad(phone.Id, q["d"], "pair"));
        await _stub.Send(JsonSerializer.Serialize(new { v = 2, id = "x", from = phone.Id, to = q["d"], ts = 0, @class = "pair", push = "none", ck = "", ct, offer = q["o"] }));
        await stuck.Task.WaitAsync(TimeSpan.FromSeconds(10));                 // the handler is now stuck

        await _stub.Send("""{"t":"ping"}""");
        await _stub.NextT("pong");                                            // still reading
        release.Set();
    }

    [Fact]
    public async Task Notes_reach_the_logged_event()
    {
        var client = Client(Identity());
        var notes = new System.Collections.Concurrent.ConcurrentQueue<string>();
        client.Logged += notes.Enqueue;
        client.Start();
        await _stub.NextT("hello");
        await Until(() => client.State == RelayClient.Status.Connected);
        await _stub.Send("""{"t":"resync","peer_id":"ph_nobody"}""");
        await Until(() => notes.Contains("resync for ph_nobody ignored: not an online paired phone (not paired)"));
    }

    [Fact]
    public async Task A_handler_that_throws_does_not_end_later_events()
    {
        var identity = Identity();
        var client = Client(identity);
        // One handler that throws on its first call and counts after that:
        // the pump has to survive the throw for the later calls to happen.
        var calls = 0;
        client.StateChanged += () =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("a handler bug");
        };
        client.Start();
        await _stub.NextT("hello");
        await Until(() => client.State == RelayClient.Status.Connected);
        await Until(() => Volatile.Read(ref calls) >= 3);                     // Registering (threw), Connecting, Connected
        await client.StopAsync();
        await Until(() => client.State == RelayClient.Status.Off);
    }

    [Fact]
    public async Task Remove_sends_revoke_and_unlink_to_the_relay()
    {
        var identity = Identity();
        identity.Keys();
        var phone = PairInto(identity);
        var client = Client(identity);
        client.Start();
        await _stub.NextT("hello");
        await Until(() => client.State == RelayClient.Status.Connected);
        await _stub.Send(JsonSerializer.Serialize(new { t = "presence", device_id = phone.Id, online = true }));
        await _stub.Next(IsEnvelope);                              // the roster

        client.Remove(phone.Id);
        var revoke = OpenAsPhone(await _stub.Next(IsEnvelope), phone, identity);
        Assert.Equal("pair.revoke", revoke.GetProperty("t").GetString());
        Assert.Equal(phone.Id, (await _stub.NextT("unlink")).GetProperty("peer_id").GetString());
        Assert.Empty(client.Phones);
    }

    [Fact]
    public async Task A_refused_token_re_registers_with_the_same_identity_and_reconnects()
    {
        var identity = Identity();
        var keys = identity.Keys();
        identity.SetRegistration("dsk_stub000000000001", "stale");
        var client = Client(identity);
        client.Start();

        Assert.Equal("stale", (await _stub.NextT("hello")).GetProperty("token").GetString());
        Assert.Equal("token-1", (await _stub.NextT("hello")).GetProperty("token").GetString());
        await Until(() => client.State == RelayClient.Status.Connected);
        Assert.Equal(1, _stub.Registrations);
        Assert.Equal(Hex(keys.Identity.Public), _stub.LastIdPub);   // same identity key
    }

    [Fact]
    public async Task A_refused_version_stops_trying_and_says_why()
    {
        _stub.HelloCode = "version";
        var client = Client(Identity());
        client.Start();
        await Until(() => client.State == RelayClient.Status.Refused);
        Assert.Contains("version", client.LastError);
        var connections = _stub.Connections;
        await Task.Delay(300);
        Assert.Equal(connections, _stub.Connections);               // no retry loop
    }

    [Fact]
    public async Task A_dropped_or_closed_connection_reconnects()
    {
        var client = Client(Identity());
        client.Start();
        await _stub.NextT("hello");
        await Until(() => client.State == RelayClient.Status.Connected);

        await _stub.DropConnection();
        await _stub.NextT("hello");
        await Until(() => client.State == RelayClient.Status.Connected);

        await _stub.CloseConnection();
        await _stub.NextT("hello");
        Assert.True(_stub.Connections >= 3);
    }

    [Fact]
    public async Task A_relay_that_is_down_is_retried_with_backoff()
    {
        _stub.AcceptSockets = false;
        var client = Client(Identity());
        client.Start();
        await Until(() => client.LastError is not null && _stub.Requests.Count(r => r == "GET /v1/socket") >= 2);
        Assert.Equal(RelayClient.Status.Connecting, client.State);
        _stub.AcceptSockets = true;
        await Until(() => client.State == RelayClient.Status.Connected);
    }

    // Two tests, not one: they used to share a 600 ms silence window, and on a
    // loaded CI runner the window closed before the stub's ping went out, so
    // the client had already (correctly) dropped the socket. Each now checks
    // one thing with nothing racing it.
    [Fact]
    public async Task We_ping_on_our_interval_and_answer_the_relays_ping()
    {
        var client = Client(Identity(), ping: TimeSpan.FromMilliseconds(150), silence: TimeSpan.FromMinutes(5));
        client.Start();
        await _stub.NextT("hello");
        await _stub.NextT("ping");                                 // our heartbeat
        await _stub.Send("""{"t":"ping"}""");
        await _stub.NextT("pong");                                 // and an answer to theirs
        Assert.Equal(1, _stub.Connections);
    }

    [Fact]
    public async Task Silence_longer_than_the_limit_ends_the_socket_and_reconnects()
    {
        // The stub never speaks unprompted and we send no pings in the window,
        // so the silence limit is the only thing that can end the socket.
        var client = Client(Identity(), ping: TimeSpan.FromMinutes(5), silence: TimeSpan.FromMilliseconds(300));
        client.Start();
        await _stub.NextT("hello");
        await _stub.NextT("hello", seconds: 10);
        Assert.True(_stub.Connections >= 2);
    }

    [Fact]
    public async Task An_oversized_frame_ends_the_connection_and_a_binary_one_is_ignored()
    {
        var client = Client(Identity());
        client.Start();
        await _stub.NextT("hello");
        await Until(() => client.State == RelayClient.Status.Connected);

        await _stub.SendRaw([1, 2, 3], WebSocketMessageType.Binary);
        await _stub.Send("""{"t":"ping"}""");
        await _stub.NextT("pong");                                 // still the same, healthy connection
        Assert.Equal(1, _stub.Connections);

        await _stub.SendRaw(Encoding.UTF8.GetBytes(new string('x', WebSocketRelayTransport.MaxFrame + 10)), WebSocketMessageType.Text);
        await _stub.NextT("hello");
        await Until(() => client.LastError?.Contains("64 KB") == true);
    }

    [Fact]
    public async Task A_pair_request_reaches_the_ui_event_and_allow_links()
    {
        var identity = Identity();
        var client = Client(identity);
        client.Start();
        await _stub.NextT("hello");
        await Until(() => client.State == RelayClient.Status.Connected);
        var offer = await client.BeginPairingAsync();

        RelayLink.PairRequest? asked = null;
        client.PairRequested += r => asked = r;
        var phone = new Phone("ph_00000000000000c2", GenerateKeyPair(), GenerateKeyPair());
        var q = offer.QrText[(offer.QrText.IndexOf('?') + 1)..].Split('&').Select(kv => kv.Split('=', 2)).ToDictionary(kv => kv[0], kv => kv[1]);
        var pairKey = PairKey(FromBase64Url(q["s"])!, q["o"], q["d"], phone.Id);
        var body = $"{{\"t\":\"pair.request\",\"phone_id\":\"{phone.Id}\",\"name\":\"QA iPhone\",\"id_pub\":\"{Hex(phone.IdKey.Public)}\",\"ka_pub\":\"{Hex(phone.KaKey.Public)}\"}}";
        var ct = Seal(pairKey, Nonce(PhoneToDesktop, 0, 0), Encoding.UTF8.GetBytes(body), Aad(phone.Id, q["d"], "pair"));
        await _stub.Send(JsonSerializer.Serialize(new { v = 2, id = "x", from = phone.Id, to = q["d"], ts = 0, @class = "pair", push = "none", ck = "", ct, offer = q["o"] }));
        await Until(() => asked is not null);
        Assert.Equal("QA iPhone", asked!.Name);

        client.Allow(asked);
        await _stub.Next(e => IsEnvelope(e) && e.GetProperty("class").GetString() == "pair");
        Assert.Equal(phone.Id, (await _stub.NextT("link")).GetProperty("phone_id").GetString());
        var roster = OpenAsPhone(await _stub.Next(IsEnvelope), phone, identity);
        Assert.Equal("roster", roster.GetProperty("t").GetString());

        client.Deny(asked);                                        // stale: harmless
        Assert.Single(client.Phones);
    }
}

/// <summary>
/// CB-314: RelayClient against a relay that answers HTTP wrongly, through a
/// fake handler — every malformed answer must surface as a RelayHttpException
/// the UI can show, never as a crash or a half-registered identity.
/// </summary>
public class RelayClientHttpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb-relay-http-" + Guid.NewGuid());

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class MemoryStore : IRelaySecretStore
    {
        private readonly Dictionary<string, byte[]> _items = new();
        public string? Protect(string name, byte[] secret) { _items[name] = secret; return "mem:" + name; }
        public byte[]? Unprotect(string reference) => _items.TryGetValue(reference[4..], out var v) ? v : null;
        public void Forget(string reference) { }
    }

    private sealed class Canned(Func<HttpRequestMessage, int, (int Status, string Body)> answer) : HttpMessageHandler
    {
        private int _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = answer(request, _calls++);
            return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent(body) });
        }
    }

    private sealed class NeverConnects : IRelayTransport
    {
        public Task<IRelaySocket> ConnectAsync(Uri uri, CancellationToken token) => throw new IOException("unreachable in this test");
    }

    private (RelayClient Client, RelayIdentity Identity) New(Func<HttpRequestMessage, int, (int, string)> answer, bool registered = false)
    {
        var identity = new RelayIdentity(_dir, new MemoryStore());
        identity.Keys();
        if (registered) identity.SetRegistration("dsk_x", "tok");
        var client = new RelayClient(identity, new RelayClient.Options(new Uri("https://relay.example"), "win", "1", "M"),
            new NeverConnects(), new Canned(answer), () => DateTimeOffset.UtcNow, (t, ct) => Task.Delay(t, ct));
        return (client, identity);
    }

    [Theory]
    [InlineData(500, "{}", "answered 500")]
    [InlineData(200, "[1]", "not an object")]
    [InlineData(200, "{}", "no challenge")]
    [InlineData(200, """{"challenge":7}""", "no challenge")]
    [InlineData(200, """{"challenge":"A"}""", "not base64url")]
    public async Task A_bad_first_registration_answer_is_a_relay_error(int status, string body, string message)
    {
        var (client, identity) = New((_, _) => (status, body));
        var ex = await Assert.ThrowsAsync<RelayHttpException>(() => client.BeginPairingAsync());
        Assert.Contains(message, ex.Message);
        Assert.Null(identity.DeviceId);
        client.Dispose();
    }

    [Theory]
    [InlineData("""{"token":"t"}""", "no device id")]
    [InlineData("""{"device_id":"dsk_1"}""", "no token")]
    public async Task A_bad_second_registration_answer_registers_nothing(string second, string message)
    {
        var (client, identity) = New((_, call) => call == 0 ? (200, """{"challenge":"AAAA"}""") : (200, second));
        var ex = await Assert.ThrowsAsync<RelayHttpException>(() => client.BeginPairingAsync());
        Assert.Contains(message, ex.Message);
        Assert.Null(identity.Token);
        client.Dispose();
    }

    [Theory]
    [InlineData("""{"expires_at":1794111792}""", "no id")]
    [InlineData("""{"offer_id":"off_1"}""", "no expiry")]
    public async Task A_bad_offer_answer_is_a_relay_error(string body, string message)
    {
        var (client, _) = New((_, _) => (200, body), registered: true);
        var ex = await Assert.ThrowsAsync<RelayHttpException>(() => client.BeginPairingAsync());
        Assert.Contains(message, ex.Message);
        client.Dispose();
    }

    [Fact]
    public async Task Stopping_during_backoff_returns_promptly()
    {
        var identity = new RelayIdentity(_dir, new MemoryStore());
        identity.Keys();
        identity.SetRegistration("dsk_x", "tok");
        var backingOff = new TaskCompletionSource();
        var client = new RelayClient(identity, new RelayClient.Options(new Uri("https://relay.example"), "win", "1", "M"),
            new NeverConnects(), new Canned((_, _) => (500, "")), null,
            async (t, ct) => { backingOff.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); });
        client.Start();
        await backingOff.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("unreachable in this test", client.LastError);
        await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RelayClient.Status.Off, client.State);
        client.Dispose();
    }
}

/// <summary>CB-314: RelayClient's pure helpers, no socket needed.</summary>
public class RelayClientHelperTests
{
    [Theory]
    [InlineData(null, "https://orbweaver-relay.uplifttech.org/")]
    [InlineData("", "https://orbweaver-relay.uplifttech.org/")]
    [InlineData("not a url", "https://orbweaver-relay.uplifttech.org/")]
    [InlineData("ftp://x.example/", "https://orbweaver-relay.uplifttech.org/")]
    [InlineData("http://127.0.0.1:5000", "http://127.0.0.1:5000/")]
    [InlineData("https://relay.example", "https://relay.example/")]
    public void Base_url_override_accepts_only_http_or_https(string? value, string expected) =>
        Assert.Equal(expected, RelayClient.BaseFrom(value).ToString());

    [Fact]
    public void Socket_uri_and_host_follow_the_base()
    {
        var https = new RelayClient.Options(new Uri("https://relay.example"), "mac", "1", "M");
        Assert.Equal("wss://relay.example/v1/socket", https.SocketUri.ToString());
        Assert.Equal("relay.example", https.RelayHost);
        var http = new RelayClient.Options(new Uri("http://127.0.0.1:5000"), "win", "1", "M");
        Assert.Equal("ws://127.0.0.1:5000/v1/socket", http.SocketUri.ToString());
        Assert.Equal("127.0.0.1:5000", http.RelayHost);
        Assert.Equal((TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(60)), (https.PingInterval, https.Silence));
    }

    [Theory]
    [InlineData("""{"t":"ping"}""", null)]
    [InlineData("""{"t":"pong"}""", null)]
    [InlineData("""{"t":"resync","peer_id":"ph_1"}""", "t=resync peer=ph_1")]
    [InlineData("""{"t":"presence","device_id":"ph_1","online":true}""", "t=presence peer=ph_1")]
    [InlineData("""{"t":"err","ref":"unlink","code":"rate_limited"}""", "t=err code=rate_limited")]
    [InlineData("""{"t":"ok","ref":"hello","code":"welcome"}""", "t=ok code=welcome")]
    [InlineData("""{"v":2,"from":"ph_1","to":"dsk_1","class":"pair","ct":"SECRET"}""", "envelope class=pair from=ph_1")]
    [InlineData("""{"hello":1}""", null)]
    [InlineData("""[1]""", null)]
    [InlineData("not json", null)]
    public void The_inbound_log_names_a_frame_without_its_payload(string frame, string? expected) =>
        Assert.Equal(expected, RelayClient.InboundKind(frame));

    // nadia's hostile-relay cases (PR #225): nothing a relay sends can forge
    // a log line, flood the log, or put a payload in it.
    [Fact]
    public void A_hostile_relay_cannot_forge_flood_or_smuggle_through_the_inbound_log()
    {
        var forged = RelayClient.InboundKind("""{"t":"x\nFAKE relay-in line: t=ok code=welcome"}""")!;
        Assert.Equal("t=other", forged);

        var flood = RelayClient.InboundKind(JsonSerializer.Serialize(new { t = "resync", peer_id = new string('a', 20_000) }))!;
        Assert.True(flood.Length < 100, $"{flood.Length} characters");
        Assert.EndsWith("…", flood);

        var smuggled = RelayClient.InboundKind("""{"v":2,"class":"SECRET-PAYLOAD-IN-CLASS","from":"c2VjcmV0/+==\n","ct":"x"}""")!;
        Assert.Equal("envelope class=other from=c2VjcmV0…", smuggled);
        Assert.DoesNotContain("\n", smuggled);
    }

    [Fact]
    public void Rate_limit_retries_back_off_from_a_quarter_second_with_jitter()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), RelayClient.RetryDelay(1, 0));
        Assert.Equal(TimeSpan.FromMilliseconds(500 + 125), RelayClient.RetryDelay(2, 0.5));
        Assert.Equal(TimeSpan.FromMilliseconds(4000 + 250), RelayClient.RetryDelay(9, 1));
    }

    [Fact]
    public void Backoff_doubles_to_its_ceiling()
    {
        var min = TimeSpan.FromSeconds(1);
        var max = TimeSpan.FromSeconds(60);
        Assert.Equal([1, 2, 4, 8, 16, 32, 60, 60], Enumerable.Range(0, 8).Select(a => (int)RelayClient.Backoff(a, min, max).TotalSeconds));
        Assert.Equal(max, RelayClient.Backoff(1000, min, max));
    }

    [Theory]
    [InlineData("""{"expires_at":1794111792}""", 1794111792L)]
    [InlineData("""{"expires_at":"2026-11-08T08:00:00Z"}""", 1794124800L)]
    [InlineData("""{"expires_at":"soon"}""", null)]
    [InlineData("""{"expires_at":true}""", null)]
    [InlineData("""{}""", null)]
    public void Expiry_reads_unix_seconds_or_iso(string json, long? expected) =>
        Assert.Equal(expected, RelayClient.ExpiresAt(JsonDocument.Parse(json).RootElement)?.ToUnixTimeSeconds());
}
