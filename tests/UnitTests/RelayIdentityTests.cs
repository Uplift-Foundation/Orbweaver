using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-314: the desktop's relay identity at rest — keys, token and paired
/// phones in relay-identity.json, with the secrets behind an
/// IRelaySecretStore. Every case uses its own scratch directory and a fake
/// store, so nothing touches a real Keychain, DPAPI or settings directory.
/// </summary>
public class RelayIdentityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb-relay-id-" + Guid.NewGuid());
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // A store that keeps secrets in memory under "fake:<name>", and can be
    // told to refuse writes or to have lost everything.
    private sealed class FakeStore : IRelaySecretStore
    {
        public readonly Dictionary<string, byte[]> Items = new();
        public readonly List<string> Forgotten = new();
        public bool RefuseWrites;

        public string? Protect(string name, byte[] secret)
        {
            if (RefuseWrites) return null;
            Items[name] = secret;
            return "fake:" + name;
        }

        public byte[]? Unprotect(string reference) =>
            reference.StartsWith("fake:") && Items.TryGetValue(reference[5..], out var v) ? v : null;

        public void Forget(string reference) => Forgotten.Add(reference);
    }

    private RelayIdentity New(FakeStore store) => new(_dir, store, () => T0);

    private JsonElement File_() => JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, RelayIdentity.FileName))).RootElement;

    [Fact]
    public void First_use_makes_keys_stores_secrets_by_reference_and_reloads_them()
    {
        var store = new FakeStore();
        var (id, ka) = New(store).Keys();

        var json = File_();
        Assert.Equal("fake:identity", json.GetProperty("id_priv").GetString());
        Assert.Equal("fake:agreement", json.GetProperty("ka_priv").GetString());
        Assert.Equal(RelayCrypto.Hex(id.Public), json.GetProperty("id_pub").GetString());
        Assert.DoesNotContain(RelayCrypto.Hex(id.Private), File.ReadAllText(Path.Combine(_dir, RelayIdentity.FileName)));

        var again = New(store).Keys();
        Assert.Equal(id.Private, again.Identity.Private);
        Assert.Equal(ka.Public, again.Agreement.Public);
    }

    [Fact]
    public void A_refusing_store_falls_back_to_the_owner_only_file()
    {
        var store = new FakeStore { RefuseWrites = true };
        var (id, _) = New(store).Keys();
        Assert.StartsWith(RelaySecrets.PlainPrefix, File_().GetProperty("id_priv").GetString());
        Assert.Equal(id.Private, New(store).Keys().Identity.Private);   // plain is read back whatever the store
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dir, RelayIdentity.FileName)));
    }

    [Fact]
    public void Lost_secrets_mean_new_keys_and_no_pairings()
    {
        var store = new FakeStore();
        var first = New(store);
        var (id, _) = first.Keys();
        first.SetRegistration("dsk_1", "tok");
        first.AddPhone("ph_1", "Phone", RelayCrypto.GenerateKeyPair().Public, RelayCrypto.GenerateKeyPair().Public);

        store.Items.Clear();                                  // the Keychain items were deleted
        var after = New(store);
        var (id2, _) = after.Keys();
        Assert.NotEqual(id.Public, id2.Public);
        Assert.Null(after.DeviceId);
        Assert.Null(after.Token);
        Assert.Empty(after.Phones);
        Assert.Contains("fake:identity", store.Forgotten);
        Assert.Contains("fake:token", store.Forgotten);
    }

    [Fact]
    public void A_private_key_that_does_not_match_its_public_key_is_not_trusted()
    {
        var store = new FakeStore();
        var (id, _) = New(store).Keys();
        store.Items["identity"] = RelayCrypto.GenerateKeyPair().Private;   // half-written / copied
        Assert.NotEqual(id.Public, New(store).Keys().Identity.Public);

        store.Items["identity"] = new byte[32];                             // not a valid scalar at all
        Assert.Equal(32, New(store).Keys().Identity.Private.Length);
    }

    [Fact]
    public void Registration_round_trips_and_clear_token_keeps_the_device()
    {
        var store = new FakeStore();
        var a = New(store);
        a.Keys();
        a.SetRegistration("dsk_aaaa", "secret-token");
        a.SetRegistration("dsk_aaaa", "rotated-token");
        Assert.Contains("fake:token", store.Forgotten);
        Assert.DoesNotContain("rotated-token", File.ReadAllText(Path.Combine(_dir, RelayIdentity.FileName)));

        var b = New(store);
        Assert.Equal("dsk_aaaa", b.DeviceId);
        Assert.Equal("rotated-token", b.Token);
        b.ClearToken();
        Assert.Null(b.Token);
        Assert.Equal("dsk_aaaa", New(store).DeviceId);
        Assert.Null(New(store).Token);
    }

    [Fact]
    public void Phones_add_replace_remove_and_keep_their_counters()
    {
        var store = new FakeStore();
        var a = New(store);
        var idPub = RelayCrypto.GenerateKeyPair().Public;
        var kaPub = RelayCrypto.GenerateKeyPair().Public;
        var p = a.AddPhone("ph_1", "Warren's iPhone", idPub, kaPub);
        Assert.Equal(("ph_1", "Warren's iPhone", T0, (ushort)0, false), (p.PhoneId, p.Name, p.PairedAt, p.SendEpoch, p.SendExhausted));
        Assert.Null(p.Received);
        Assert.Equal(idPub, p.IdPublic);
        Assert.Equal(kaPub, p.KaPublic);

        Assert.True(a.SaveSendEpoch("ph_1", 7, false));
        a.SaveReceive("ph_1", 3, 42);
        var reloaded = New(store).PhoneById("ph_1")!;
        Assert.Equal((7, false), (reloaded.SendEpoch, reloaded.SendExhausted));
        Assert.Equal(((ushort)3, 42UL), reloaded.Received);

        a.AddPhone("ph_1", "Renamed", idPub, kaPub);                      // re-pair restarts the epoch
        Assert.Equal(0, New(store).PhoneById("ph_1")!.SendEpoch);
        Assert.Single(New(store).Phones);

        Assert.False(a.SaveSendEpoch("ph_missing", 1, false));
        a.SaveReceive("ph_missing", 1, 1);                                // a no-op, not a throw
        Assert.True(a.RemovePhone("ph_1"));
        Assert.False(a.RemovePhone("ph_1"));
        Assert.Null(New(store).PhoneById("ph_1"));
    }

    [Fact]
    public void A_phone_with_unusable_keys_is_dropped_on_load()
    {
        var store = new FakeStore();
        var a = New(store);
        a.AddPhone("ph_good", "Good", RelayCrypto.GenerateKeyPair().Public, RelayCrypto.GenerateKeyPair().Public);
        a.AddPhone("ph_bad", "Bad", new byte[65], RelayCrypto.GenerateKeyPair().Public);
        a.AddPhone("ph_bad2", "Bad", RelayCrypto.GenerateKeyPair().Public, [1, 2, 3]);
        Assert.Equal(["ph_good"], New(store).Phones.Select(p => p.PhoneId));
    }

    [Fact]
    public void A_corrupt_file_is_replaced_not_fatal()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, RelayIdentity.FileName), "{ not json");
        var id = New(new FakeStore());
        Assert.Empty(id.Phones);
        Assert.Equal(RelayCrypto.PublicKeyLength, id.Keys().Identity.Public.Length);
        Assert.Equal(JsonValueKind.Object, File_().ValueKind);
    }

    [Fact]
    public void A_null_phone_list_in_the_file_loads_as_empty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, RelayIdentity.FileName), """{"device_id":"dsk_x","phones":null}""");
        var id = New(new FakeStore());
        Assert.Empty(id.Phones);
        Assert.Equal("dsk_x", id.DeviceId);
    }

    [Fact]
    public void A_receive_position_with_only_one_half_reads_as_none()
    {
        var a = New(new FakeStore());
        a.AddPhone("ph_1", "P", RelayCrypto.GenerateKeyPair().Public, RelayCrypto.GenerateKeyPair().Public);
        var path = Path.Combine(_dir, RelayIdentity.FileName);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"send_exhausted\": false", "\"send_exhausted\": false, \"recv_epoch\": 4"));
        Assert.Contains("recv_epoch", File.ReadAllText(path));
        Assert.Null(New(new FakeStore()).PhoneById("ph_1")!.Received);
    }

    [Fact]
    public void An_unwritable_directory_reports_failure_instead_of_throwing()
    {
        // A file where the directory should be: every write fails.
        File.WriteAllText(_dir, "in the way");
        try
        {
            var id = New(new FakeStore());
            Assert.Equal(RelayCrypto.PublicKeyLength, id.Keys().Identity.Public.Length);   // still works this run
            id.AddPhone("ph_1", "P", RelayCrypto.GenerateKeyPair().Public, RelayCrypto.GenerateKeyPair().Public);
            Assert.False(id.SaveSendEpoch("ph_1", 1, false));                             // caller must not send
        }
        finally
        {
            File.Delete(_dir);
        }
    }

    [Fact]
    public void An_unreadable_file_is_treated_as_empty()
    {
        Directory.CreateDirectory(Path.Combine(_dir, RelayIdentity.FileName));   // a directory where the file should be
        var id = New(new FakeStore());
        Assert.Null(id.DeviceId);
    }

    [Fact]
    public void Shared_lives_in_the_settings_directory()
    {
        Assert.Equal(Path.Combine(OrbweaverSettings.Directory, RelayIdentity.FileName), RelayIdentity.Shared.FilePath);
    }
}

public class PlainSecretStoreTests
{
    [Fact]
    public void Round_trips_and_refuses_what_is_not_its_own()
    {
        var s = PlainSecretStore.Instance;
        var reference = s.Protect("x", [1, 2, 3])!;
        Assert.Equal("plain:AQID", reference);
        Assert.Equal(new byte[] { 1, 2, 3 }, s.Unprotect(reference));
        Assert.Null(s.Unprotect("keychain:x"));
        Assert.Null(s.Unprotect("plain:***"));
        s.Forget(reference);   // nothing to forget; must not throw
    }
}
