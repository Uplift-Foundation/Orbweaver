using System.Text;
using System.Text.Json;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

// CB-391 across its seams: a real settings.json on disk, read by the real
// settings loader, decides whether a Mac parked at the lock screen polls usage
// at all; when it does, the reading goes through the real RelayClient, a real
// ClientWebSocket and RelayStub, sealed, and opens on the phone's side as a
// usage frame. Only the usage source and the read's runner are fakes, so no
// `claude -p` is started. The unit and UI suites own the rules; this owns the
// exchange — that the settings file someone else's editor wrote and the frame
// the phone decrypts agree with what those rules decided.
[Collection("Settings")]
public class HeadlessUsageSettingsTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb391-int-" + Guid.NewGuid());
    private readonly string? _settingsDir = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");
    private RelayStub _stub = null!;
    private RelayClient? _client;
    private RelayIdentity? _identity;
    private readonly Source _source = new();

    private sealed class Source : IUsageSource
    {
        private int _reads;
        internal int Reads => Volatile.Read(ref _reads);
        public IReadOnlyList<AccountUsage> Read()
        {
            Interlocked.Increment(ref _reads);
            var now = DateTimeOffset.UtcNow;
            return [new AccountUsage("/acct/locked", "locked-board", true, "max",
                new UsageWindow(12, now.AddHours(3)), new UsageWindow(34, now.AddDays(3)), null, now)];
        }
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
    private void SettingsFile(bool accountUsage)
    {
        var dir = Path.Combine(_dir, "settings");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            $$"""{ "relayEnabled": true, "accountUsageEnabled": {{(accountUsage ? "true" : "false")}}, "grokAccountUsageEnabled": false, "codexAccountUsageEnabled": false }""");
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

        HeadlessUsage.Source = () => _source;
        HeadlessUsage.OffThread = work => work();
        HeadlessUsage.Every = Timeout.InfiniteTimeSpan;
        HeadlessUsage.GrokRefresh = () => throw new InvalidOperationException("no test may start Grok");
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
        SettingsFile(accountUsage: true);
        var phone = Wire();

        RelayService.ServeEarly();
        await _stub.NextT("hello");
        await Until(() => _client!.State == RelayClient.Status.Connected);
        await _stub.Send(JsonSerializer.Serialize(new { t = "presence", device_id = phone.Id, online = true, last_seen = 1 }));

        HeadlessUsage.Tick();

        // Whatever the roster sends first, the next usage envelope opens as
        // the reading, named as the orbs would name it.
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
        Assert.Equal("locked-board", account.GetProperty("name").GetString());
        Assert.Equal(1, _source.Reads);
    }

    // The control: the same file with Claude usage off. The relay still
    // connects, and nothing is read or sent.
    [Fact]
    public async Task The_same_Mac_with_usage_off_in_its_settings_file_reads_and_sends_nothing()
    {
        SettingsFile(accountUsage: false);
        var phone = Wire();

        RelayService.ServeEarly();
        await _stub.NextT("hello");
        await Until(() => _client!.State == RelayClient.Status.Connected);
        await _stub.Send(JsonSerializer.Serialize(new { t = "presence", device_id = phone.Id, online = true, last_seen = 1 }));

        HeadlessUsage.Tick();

        Assert.True(HeadlessUsage.Started);
        Assert.Equal(0, _source.Reads);
        Assert.Equal(0, _client!.UsageFramesSent);
    }
}
