using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Orbweaver
{
    // Where the phone relay's secrets live at rest (CB-314): the identity and
    // agreement private keys and the relay token. The spec says macOS Keychain
    // and Windows DPAPI, and those are two different shapes — the Keychain
    // holds the secret itself and the file only names it, while DPAPI hands
    // back a wrapped blob the file has to keep. So a store turns a secret into
    // a *reference* that goes in relay-identity.json, and back again:
    //
    //   keychain:<account>   the bytes are in the login Keychain
    //   dpapi:<base64>       the bytes are in the file, wrapped to this user
    //   plain:<base64>       the bytes are in the file, owner-only, unwrapped
    //
    // The prefix is what makes a file self-describing: it is read back by the
    // method that wrote it whichever platform store is current, so a Keychain
    // write that failed once and fell back to plain still loads after the
    // Keychain starts working again.
    internal interface IRelaySecretStore
    {
        // A reference to store, or null if this store could not keep it.
        string? Protect(string name, byte[] secret);

        // The bytes back, or null if this store cannot open the reference.
        byte[]? Unprotect(string reference);

        // Forgets a reference. Best effort.
        void Forget(string reference);
    }

    internal static class RelaySecrets
    {
        internal const string KeychainPrefix = "keychain:";
        internal const string DpapiPrefix = "dpapi:";
        internal const string PlainPrefix = "plain:";

        // The store this machine should use: DPAPI on Windows, the Keychain on
        // macOS, and the plain file anywhere else (nothing ships elsewhere,
        // but `dotnet test` on Linux should not throw).
        [ExcludeFromCodeCoverage] // one arm per OS; each test run sees one
        internal static IRelaySecretStore ForThisPlatform() =>
            OperatingSystem.IsWindows() ? new DpapiSecretStore()
            : OperatingSystem.IsMacOS() ? new KeychainSecretStore()
            : PlainSecretStore.Instance;
    }

    // Unwrapped bytes in the file. The degraded path, named as such wherever
    // it is chosen — RelayIdentity logs every secret that lands here. Owner-
    // only file permissions are the only protection, which is what
    // peer-identity.json has today.
    internal sealed class PlainSecretStore : IRelaySecretStore
    {
        internal static readonly PlainSecretStore Instance = new();

        public string? Protect(string name, byte[] secret) => RelaySecrets.PlainPrefix + Convert.ToBase64String(secret);

        public byte[]? Unprotect(string reference)
        {
            if (!reference.StartsWith(RelaySecrets.PlainPrefix, StringComparison.Ordinal)) return null;
            try { return Convert.FromBase64String(reference[RelaySecrets.PlainPrefix.Length..]); }
            catch (FormatException) { return null; }
        }

        public void Forget(string reference) { }
    }

    // DPAPI, CurrentUser scope: only this Windows account on this machine can
    // unwrap. The entropy is fixed per purpose so another program running as
    // the same user cannot unwrap the blob by passing it to DPAPI without
    // knowing what it is for — a small bar, but free.
    [ExcludeFromCodeCoverage] // Windows-only; RelaySecretStoreWindowsTests runs it on the Windows leg
    [SupportedOSPlatform("windows")]
    internal sealed class DpapiSecretStore : IRelaySecretStore
    {
        private static readonly byte[] Entropy = "orbweaver-relay-secret-v1"u8.ToArray();

        public string? Protect(string name, byte[] secret)
        {
            try { return RelaySecrets.DpapiPrefix + Convert.ToBase64String(ProtectedData.Protect(secret, Entropy, DataProtectionScope.CurrentUser)); }
            catch (CryptographicException) { return null; }
        }

        public byte[]? Unprotect(string reference)
        {
            if (!reference.StartsWith(RelaySecrets.DpapiPrefix, StringComparison.Ordinal)) return null;
            try { return ProtectedData.Unprotect(Convert.FromBase64String(reference[RelaySecrets.DpapiPrefix.Length..]), Entropy, DataProtectionScope.CurrentUser); }
            catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
        }

        public void Forget(string reference) { }
    }

    // The login Keychain, one generic-password item per secret, written by
    // MacOSKeychainItems. See that file for why a read-back of our own item is
    // expected not to prompt on a signed build, and why that is an
    // expectation rather than a measurement.
    [ExcludeFromCodeCoverage] // real Keychain; RelayKeychainManualTests behind BrandEnv.KeychainTests set to 1
    internal sealed class KeychainSecretStore : IRelaySecretStore
    {
        internal static readonly string Service = Brand.MacBundleId + ".relay";

        public string? Protect(string name, byte[] secret)
        {
            var status = MacOSKeychainItems.Write(Service, name, secret);
            if (status == 0) return RelaySecrets.KeychainPrefix + name;
            MirrorLog.Say("relay-keychain", $"write {name} failed: {MacOSKeychainItems.Describe(status)}");
            return null;
        }

        public byte[]? Unprotect(string reference)
        {
            if (!reference.StartsWith(RelaySecrets.KeychainPrefix, StringComparison.Ordinal)) return null;
            var (secret, status) = MacOSKeychainItems.Read(Service, reference[RelaySecrets.KeychainPrefix.Length..]);
            if (secret is null) MirrorLog.Say("relay-keychain", $"read failed: {MacOSKeychainItems.Describe(status)}");
            return secret;
        }

        public void Forget(string reference)
        {
            if (reference.StartsWith(RelaySecrets.KeychainPrefix, StringComparison.Ordinal))
                MacOSKeychainItems.Delete(Service, reference[RelaySecrets.KeychainPrefix.Length..]);
        }
    }
}
