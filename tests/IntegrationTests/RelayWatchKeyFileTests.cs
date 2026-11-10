using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-323 watch-key addendum: a phone's watch_pub and watch_at through the real
/// relay-identity.json. Written, read back by a fresh instance, cleared by a
/// key.remove (watch_at kept, so an old add replayed later is stale), gone
/// with the phone on unpairing, and absent from the file for a phone that
/// never sent a key.add, so a v7 file reads the same before and after.
/// </summary>
public class RelayWatchKeyFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb323-wk-" + Guid.NewGuid().ToString("N")[..8]);

    private sealed class MemoryStore : IRelaySecretStore
    {
        private readonly Dictionary<string, byte[]> _items = new();
        public string? Protect(string name, byte[] secret) { _items[name] = secret; return "mem:" + name; }
        public byte[]? Unprotect(string reference) => _items.TryGetValue(reference[4..], out var v) ? v : null;
        public void Forget(string reference) { }
    }

    private readonly MemoryStore _store = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private RelayIdentity Open() => new(_dir, _store);

    private JsonElement StoredPhone(string id) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, RelayIdentity.FileName))).RootElement
            .GetProperty("phones").EnumerateArray().Single(p => p.GetProperty("phone_id").GetString() == id);

    [Fact]
    public void Watch_pub_and_watch_at_round_trip_through_the_real_file_and_go_with_the_phone()
    {
        var identity = Open();
        identity.Keys();
        var phone = RelayCrypto.GenerateKeyPair();
        identity.AddPhone("ph_00000000000000c1", "iPhone", phone.Public, RelayCrypto.GenerateKeyPair().Public);
        identity.AddPhone("ph_00000000000000c2", "Other", RelayCrypto.GenerateKeyPair().Public, RelayCrypto.GenerateKeyPair().Public);

        // A phone that never sent a key.add has neither key in the file.
        Assert.False(StoredPhone("ph_00000000000000c1").TryGetProperty("watch_pub", out _));
        Assert.False(StoredPhone("ph_00000000000000c1").TryGetProperty("watch_at", out _));

        var watch = RelayCrypto.GenerateKeyPair();
        Assert.True(identity.SaveWatchKey("ph_00000000000000c1", watch.Public, 1794111700));
        Assert.Equal((RelayCrypto.Hex(watch.Public), 1794111700L),
            (StoredPhone("ph_00000000000000c1").GetProperty("watch_pub").GetString(), StoredPhone("ph_00000000000000c1").GetProperty("watch_at").GetInt64()));

        var reread = Open().PhoneById("ph_00000000000000c1")!;
        Assert.Equal((RelayCrypto.Hex(watch.Public), 1794111700L), (RelayCrypto.Hex(reread.WatchPublic!), reread.WatchAt!.Value));
        Assert.Null(Open().PhoneById("ph_00000000000000c2")!.WatchAt);

        Assert.True(identity.SaveWatchKey("ph_00000000000000c1", null, 1794111900));
        Assert.False(StoredPhone("ph_00000000000000c1").TryGetProperty("watch_pub", out _));
        var removed = Open().PhoneById("ph_00000000000000c1")!;
        Assert.Equal((null, 1794111900L), (removed.WatchPublic, removed.WatchAt));

        Assert.False(identity.SaveWatchKey("ph_unknown", watch.Public, 1));
        identity.RemovePhone("ph_00000000000000c1");
        Assert.Null(Open().PhoneById("ph_00000000000000c1"));
        Assert.Null(Open().WatchPublicOf("ph_00000000000000c1"));
    }

    [Fact]
    public void A_watch_key_in_the_file_that_is_not_a_point_reads_as_none()
    {
        var identity = Open();
        identity.Keys();
        identity.AddPhone("ph_00000000000000c1", "iPhone", RelayCrypto.GenerateKeyPair().Public, RelayCrypto.GenerateKeyPair().Public);
        identity.SaveWatchKey("ph_00000000000000c1", RelayCrypto.GenerateKeyPair().Public, 5);
        var path = Path.Combine(_dir, RelayIdentity.FileName);
        var text = File.ReadAllText(path);
        var stored = StoredPhone("ph_00000000000000c1").GetProperty("watch_pub").GetString()!;
        File.WriteAllText(path, text.Replace(stored, "04" + new string('0', 128)));
        var reread = Open().PhoneById("ph_00000000000000c1")!;
        Assert.Equal((null, 5L), (reread.WatchPublic, reread.WatchAt));
    }
}
