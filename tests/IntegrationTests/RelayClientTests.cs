using System.Net.WebSockets;
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

    private RelayClient Client(RelayIdentity identity, TimeSpan? ping = null, TimeSpan? silence = null)
    {
        var options = new RelayClient.Options(_stub.Base, "mac", "9.9.9", "Test Mac")
        {
            PingInterval = ping ?? TimeSpan.FromMinutes(5),
            Silence = silence ?? TimeSpan.FromMinutes(5),
            MinBackoff = TimeSpan.FromMilliseconds(20),
            MaxBackoff = TimeSpan.FromMilliseconds(100),
        };
        _client = new RelayClient(identity, options, new WebSocketRelayTransport());
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

    private sealed class Hangs(Action counted) : IRelayTransport
    {
        public async Task<IRelaySocket> ConnectAsync(Uri uri, CancellationToken token)
        {
            counted();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        }
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
        _client = new RelayClient(identity, options, new WebSocketRelayTransport());
        _client.OnScanned([new RelaySession("s1", "claude", "proj", "/c", "idle", null, null)]);
        _client.Start();
        await _stub.NextT("hello");
        await Until(() => _client.State == RelayClient.Status.Connected);
        await _stub.Send(JsonSerializer.Serialize(new { t = "presence", device_id = phone.Id, online = true }));
        await _stub.Next(IsEnvelope);

        for (var i = 0; i < 30; i++)
            _client.OnScanned([new RelaySession("s1", "claude", "proj", "/c", i % 2 == 0 ? "generating" : "idle", null, null)]);
        var started = DateTime.UtcNow;
        var seqs = new List<long>();
        for (var i = 0; i < 30; i++)
            seqs.Add(OpenAsPhone(await _stub.Next(IsEnvelope), phone, identity).GetProperty("seq").GetInt64());
        Assert.Equal(Enumerable.Range(2, 30).Select(i => (long)i), seqs);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(400), "unpaced frames were held back");
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

    [Fact]
    public async Task Silence_longer_than_the_limit_reconnects_and_pings_keep_the_relay_hearing_us()
    {
        var client = Client(Identity(), ping: TimeSpan.FromMilliseconds(150), silence: TimeSpan.FromMilliseconds(600));
        client.Start();
        await _stub.NextT("hello");
        await _stub.NextT("ping");                                 // our heartbeat

        await _stub.Send("""{"t":"ping"}""");
        await _stub.NextT("pong");                                 // and an answer to theirs

        // The stub never speaks unprompted, so 600 ms of silence ends the socket.
        await _stub.NextT("hello", seconds: 5);
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
