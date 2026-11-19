using System.Text;
using System.Text.Json;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

// CB-391 across its seams: a real settings.json on disk, read by the real
// settings loader, decides whether a Mac parked at the lock screen polls usage
// at all; when it does, a real Codex rollout under a temp $CODEX_HOME is read
// by the real CodexUsagePoller.ReadFrom, and the reading goes through the real
// RelayClient, a real ClientWebSocket and RelayStub, sealed, and opens on the
// phone's side as a usage frame. The live app-server ask is the only thing
// switched off (no `codex` is started), and the read runs inline. The unit and
// UI suites own the rules; this owns the exchange — that the settings file,
// the rollout on disk and the frame the phone decrypts agree with them.
[Collection("Settings")]
public class HeadlessUsageSettingsTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb391-int-" + Guid.NewGuid());
    private readonly string? _settingsDir = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");
    private RelayStub _stub = null!;
    private RelayClient? _client;
    private RelayIdentity? _identity;
    private Source _source = null!;

    // The real poller's per-home path over one temp $CODEX_HOME, counted.
    private sealed class Source(string codexHome) : IUsageSource
    {
        private int _reads;
        internal int Reads => Volatile.Read(ref _reads);
        public IReadOnlyList<AccountUsage> Read()
        {
            Interlocked.Increment(ref _reads);
            return CodexUsagePoller.ReadFrom([codexHome], DateTimeOffset.UtcNow, _ => null);
        }
    }

    // A token_count in the shape Codex writes (CodexUsageScanTests' capture),
    // with reset times in the future so neither window reads as expired.
    private string CodexHome()
    {
        var home = Path.Combine(_dir, "codex-home");
        var day = Path.Combine(home, "sessions", "2026", "11", "18");
        Directory.CreateDirectory(day);
        File.WriteAllText(Path.Combine(day, "rollout-locked.jsonl"),
            """
            {"timestamp":"2026-11-18T04:00:00.000Z","ordinal":183,"type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":37,"window_minutes":300,"resets_at":4102444800},"secondary":{"used_percent":12,"window_minutes":10080,"resets_at":4102444800},"plan_type":"team"}}}
            """ + "\n");
        return home;
    }

    private sealed class MemoryStore : IRelaySecretStore
    {
        private readonly Dictionary<string, byte[]> _items = new();
        public string? Protect(string name, byte[] secret) { lock (_items) _items[name] = secret; return "mem:" + name; }
        public byte[]? Unprotect(string reference) { lock (_items) return _items.TryGetValue(reference[4..], out var v) ? v : null; }
        public void Forget(string reference) { }
    }

    private sealed record Phone(string Id, KeyPair IdKey, KeyPair KaKey);

    public Task InitializeAsync()
    {
        _stub = new RelayStub();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        RelayService.ResetHeadlessForTests();
        RelayService.UseForTests(null);
        RelayService.Factory = RelayService.RealClient;
        RelayService.UserConfigDirs = RelayService.DefaultUserConfigDirs;
        RelayService.DefaultPersona = RelayService.CachedDefaultPersona;
        try { if (_client is not null) await RelayClientTests.StopBounded(_client); }
        finally
        {
            _client?.Dispose();
            await _stub.DisposeAsync();
            Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", _settingsDir);
            OrbweaverSettings.ReloadForTests();
        }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // The settings file as it would be on the mini, written as text and read
    // back by the app's own loader rather than set through a setter.
    private void SettingsFile(bool relay, bool codexUsage)
    {
        var dir = Path.Combine(_dir, "settings");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            $$"""{ "relayEnabled": {{(relay ? "true" : "false")}}, "accountUsageEnabled": false, "grokAccountUsageEnabled": false, "codexAccountUsageEnabled": {{(codexUsage ? "true" : "false")}} }""");
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    private Phone Wire()
    {
        _identity = new RelayIdentity(Path.Combine(_dir, "relay"), new MemoryStore());
        _identity.Keys();
        var phone = new Phone("ph_00000000000000c9", GenerateKeyPair(), GenerateKeyPair());
        _identity.AddPhone(phone.Id, "Test iPhone", phone.IdKey.Public, phone.KaKey.Public);

        var options = new RelayClient.Options(_stub.Base, "mac", "9.9.9", "Locked Mac")
        {
            MinBackoff = TimeSpan.FromMilliseconds(20),
            MaxBackoff = TimeSpan.FromMilliseconds(100),
        };
        RelayService.Factory = () => _client = new RelayClient(_identity, options, new WebSocketRelayTransport());
        RelayService.HeadlessScan = () => [];
        RelayService.HeadlessEvery = Timeout.InfiniteTimeSpan;
        RelayService.UserConfigDirs = () => [];
        RelayService.DefaultPersona = _ => null;

        _source = new Source(CodexHome());
        RelayService.UsageFactory = () => new HeadlessUsage(HeadlessUsage.Production(_source) with
        {
            OffThread = work => work(),
            RefreshGrok = () => throw new InvalidOperationException("no test may start Grok"),
        });
        RelayService.UsageFirstTick = Timeout.InfiniteTimeSpan;
        RelayService.UsageEvery = Timeout.InfiniteTimeSpan;
        return phone;
    }

    private JsonElement OpenAsPhone(JsonElement envelope, Phone phone)
    {
        var ct = envelope.GetProperty("ct").GetString()!;
        var nonce = PeekNonce(ct)!.Value;
        var desktopId = envelope.GetProperty("from").GetString()!;
        var basis = SessionBase(SharedSecret(phone.KaKey, _identity!.Keys().Agreement.Public), desktopId, phone.Id);
        var plain = Open(EpochKey(basis, nonce.Epoch), ct, Aad(desktopId, phone.Id, envelope.GetProperty("class").GetString()!), DesktopToPhone);
        Assert.NotNull(plain);
        return JsonDocument.Parse(Encoding.UTF8.GetString(plain!)).RootElement;
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

    [Fact]
    public async Task A_parked_Mac_with_usage_on_in_its_settings_file_sends_the_phone_a_sealed_usage_frame()
    {
        SettingsFile(relay: true, codexUsage: true);
        var phone = Wire();

        RelayService.ServeEarly();
        await _stub.NextT("hello");
        await Until(() => _client!.State == RelayClient.Status.Connected);
        await _stub.Send(JsonSerializer.Serialize(new { t = "presence", device_id = phone.Id, online = true, last_seen = 1 }));

        RelayService.UsageFeed!.Tick();

        // Whatever the roster sends first, the next usage envelope opens as
        // the rollout's reading.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        JsonElement usage = default;
        while (DateTime.UtcNow < deadline)
        {
            var envelope = await _stub.Next(e => e.TryGetProperty("v", out _));
            var body = OpenAsPhone(envelope, phone);
            if (body.GetProperty("t").GetString() != "usage") continue;
            Assert.Equal("state", envelope.GetProperty("class").GetString());
            usage = body;
            break;
        }

        Assert.Equal(JsonValueKind.Object, usage.ValueKind);
        var account = Assert.Single(usage.GetProperty("accounts").EnumerateArray());
        Assert.Equal("codex", account.GetProperty("cli").GetString());
        Assert.Equal(37, account.GetProperty("window_5h_pct").GetInt32());
        Assert.Equal(12, account.GetProperty("weekly_pct").GetInt32());
        Assert.Equal(1, _source.Reads);
    }

    // Control: the same file with Codex usage off. The relay still connects
    // and the feed starts, but nothing is read or sent.
    [Fact]
    public async Task The_same_Mac_with_usage_off_in_its_settings_file_reads_and_sends_nothing()
    {
        SettingsFile(relay: true, codexUsage: false);
        var phone = Wire();

        RelayService.ServeEarly();
        await _stub.NextT("hello");
        await Until(() => _client!.State == RelayClient.Status.Connected);
        await _stub.Send(JsonSerializer.Serialize(new { t = "presence", device_id = phone.Id, online = true, last_seen = 1 }));

        RelayService.UsageFeed!.Tick();

        Assert.Equal(0, _source.Reads);
        Assert.Equal(0, _client!.UsageFramesSent);
    }

    // Control: relayEnabled false in the file. No relay, no feed, no read.
    [Fact]
    public void The_same_Mac_with_the_relay_off_in_its_settings_file_starts_nothing()
    {
        SettingsFile(relay: false, codexUsage: true);
        Wire();

        RelayService.ServeEarly();

        Assert.Null(RelayService.UsageFeed);
        Assert.Null(RelayService.Client);
        Assert.Equal(0, _source.Reads);
        Assert.Equal(0, _stub.Connections);
    }
}
