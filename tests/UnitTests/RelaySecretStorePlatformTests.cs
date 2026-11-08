using Xunit;

namespace Orbweaver.Tests;

// CB-314: the two real secret stores, each on the one platform it exists for.
// Both classes are [ExcludeFromCodeCoverage] because a coverage run sees only
// one OS; these are what actually runs them.
//
// DPAPI runs on every Windows run (CI's windows leg included): CurrentUser
// DPAPI needs no setup and leaves nothing behind.
//
// The Keychain case writes a real item into the login Keychain of whoever runs
// it, so it is opt-in — BrandEnv.KeychainTests set to 1 — the same way
// OpenArgumentDeliveryTests keeps its Dock-touching launches off a
// developer's Mac. It deletes its item afterwards.
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "DPAPI exists only on Windows";
    }
}

public sealed class KeychainFactAttribute : FactAttribute
{
    public KeychainFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "the login Keychain exists only on macOS";
        else if (BrandEnv.Get(BrandEnv.KeychainTests) != "1")
            Skip = $"writes to the real login Keychain; set {BrandEnv.Name(BrandEnv.KeychainTests)}=1 to run";
    }
}

public class RelaySecretStorePlatformTests
{
    [WindowsOnlyFact]
    public void Dpapi_round_trips_and_refuses_tampering()
    {
        if (!OperatingSystem.IsWindows()) return;   // for the analyzer; the attribute already skips
        var store = new DpapiSecretStore();
        var secret = RelayCrypto.GenerateKeyPair().Private;
        var reference = store.Protect("identity", secret)!;
        Assert.StartsWith(RelaySecrets.DpapiPrefix, reference);
        Assert.DoesNotContain(Convert.ToBase64String(secret), reference);
        Assert.Equal(secret, store.Unprotect(reference));

        var blob = Convert.FromBase64String(reference[RelaySecrets.DpapiPrefix.Length..]);
        blob[^1] ^= 1;
        Assert.Null(store.Unprotect(RelaySecrets.DpapiPrefix + Convert.ToBase64String(blob)));
        Assert.Null(store.Unprotect(RelaySecrets.DpapiPrefix + "***"));
        Assert.Null(store.Unprotect("keychain:identity"));
        store.Forget(reference);
    }

    [KeychainFact]
    public void Keychain_round_trips_replaces_and_forgets()
    {
        var store = new KeychainSecretStore();
        var name = "test-" + Guid.NewGuid().ToString("N");
        try
        {
            var reference = store.Protect(name, [1, 2, 3])!;
            Assert.Equal(RelaySecrets.KeychainPrefix + name, reference);
            Assert.Equal(new byte[] { 1, 2, 3 }, store.Unprotect(reference));
            Assert.Equal(reference, store.Protect(name, [4, 5]));        // duplicate → update in place
            Assert.Equal(new byte[] { 4, 5 }, store.Unprotect(reference));
            store.Forget(reference);
            Assert.Null(store.Unprotect(reference));
            Assert.Null(store.Unprotect("plain:AQID"));
        }
        finally
        {
            MacOSKeychainItems.Delete(KeychainSecretStore.Service, name);
        }
    }

    [Fact]
    public void This_platform_gets_its_own_store()
    {
        var store = RelaySecrets.ForThisPlatform();
        if (OperatingSystem.IsWindows()) Assert.IsType<DpapiSecretStore>(store);
        else if (OperatingSystem.IsMacOS()) Assert.IsType<KeychainSecretStore>(store);
        else Assert.Same(PlainSecretStore.Instance, store);
    }
}
