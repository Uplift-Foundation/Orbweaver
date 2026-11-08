using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Orbweaver
{
    // Generic-password items this app writes for itself, in the login
    // Keychain: the phone relay's private keys and token (CB-314). The
    // read-only half of the Keychain this app already had, MacOSKeychain,
    // reads an item another program (the Claude Code CLI) owns, and every
    // caveat in its header about consent prompts is about that. These items
    // are created by this app, so their ACL names this app and reading them
    // back is not supposed to prompt — but that rests on the build being
    // signed with a stable identity (CLAUDE.md: an ad-hoc build has a new
    // code identity every time), and it is a statement about Apple's ACL
    // rules, not a measurement. RelayIdentity therefore treats every call
    // here as fallible and never makes one on the UI thread.
    //
    // Items are keyed by (service, account): the service is fixed per brand,
    // the account names the secret ("identity", "agreement", "token").
    //
    // Same mechanics as MacOSKeychain: DllImport against the framework's
    // absolute path, CoreFoundation objects released by hand, and the whole
    // class excluded from coverage, because a CI runner has no login
    // Keychain and a developer's test run must not write to theirs.
    // tests/UnitTests/RelayKeychainManualTests exercises it for real behind
    // BrandEnv.KeychainTests set to 1.
    [ExcludeFromCodeCoverage]
    internal static class MacOSKeychainItems
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        private const int ErrSecSuccess = 0;
        private const int ErrSecItemNotFound = -25300;
        private const int ErrSecDuplicateItem = -25299;
        private const uint KCFStringEncodingUtf8 = 0x08000100;

        [DllImport(Security)] private static extern int SecItemAdd(IntPtr attributes, IntPtr result);
        [DllImport(Security)] private static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);
        [DllImport(Security)] private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
        [DllImport(Security)] private static extern int SecItemDelete(IntPtr query);
        [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, long count, IntPtr keyCallBacks, IntPtr valueCallBacks);
        [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);
        [DllImport(CoreFoundation)] private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, long length);
        [DllImport(CoreFoundation)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
        [DllImport(CoreFoundation)] private static extern long CFDataGetLength(IntPtr data);
        [DllImport(CoreFoundation)] private static extern long CFGetTypeID(IntPtr reference);
        [DllImport(CoreFoundation)] private static extern long CFDataGetTypeID();
        [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr reference);

        private static IntPtr Constant(string library, string symbol) =>
            Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(library), symbol));

        private static IntPtr Address(string library, string symbol) =>
            NativeLibrary.GetExport(NativeLibrary.Load(library), symbol);

        // Writes or replaces one item. Returns the OSStatus, 0 on success.
        internal static int Write(string service, string account, byte[] secret)
        {
            if (!OperatingSystem.IsMacOS()) return -1;
            var refs = new List<IntPtr>();
            try
            {
                var svc = Track(refs, CFStringCreateWithCString(IntPtr.Zero, service, KCFStringEncodingUtf8));
                var acct = Track(refs, CFStringCreateWithCString(IntPtr.Zero, account, KCFStringEncodingUtf8));
                var data = Track(refs, CFDataCreate(IntPtr.Zero, secret, secret.Length));

                var add = Track(refs, Dictionary(
                    (Constant(Security, "kSecClass"), Constant(Security, "kSecClassGenericPassword")),
                    (Constant(Security, "kSecAttrService"), svc),
                    (Constant(Security, "kSecAttrAccount"), acct),
                    (Constant(Security, "kSecAttrAccessible"), Constant(Security, "kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly")),
                    (Constant(Security, "kSecValueData"), data)));
                var status = SecItemAdd(add, IntPtr.Zero);
                if (status != ErrSecDuplicateItem) return status;

                var query = Track(refs, Dictionary(
                    (Constant(Security, "kSecClass"), Constant(Security, "kSecClassGenericPassword")),
                    (Constant(Security, "kSecAttrService"), svc),
                    (Constant(Security, "kSecAttrAccount"), acct)));
                var update = Track(refs, Dictionary((Constant(Security, "kSecValueData"), data)));
                return SecItemUpdate(query, update);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return -2;
            }
            finally
            {
                foreach (var r in refs) if (r != IntPtr.Zero) CFRelease(r);
            }
        }

        // The stored bytes, or null with the OSStatus that explains why.
        internal static (byte[]? Secret, int Status) Read(string service, string account)
        {
            if (!OperatingSystem.IsMacOS()) return (null, -1);
            var refs = new List<IntPtr>();
            var result = IntPtr.Zero;
            try
            {
                var svc = Track(refs, CFStringCreateWithCString(IntPtr.Zero, service, KCFStringEncodingUtf8));
                var acct = Track(refs, CFStringCreateWithCString(IntPtr.Zero, account, KCFStringEncodingUtf8));
                var query = Track(refs, Dictionary(
                    (Constant(Security, "kSecClass"), Constant(Security, "kSecClassGenericPassword")),
                    (Constant(Security, "kSecAttrService"), svc),
                    (Constant(Security, "kSecAttrAccount"), acct),
                    (Constant(Security, "kSecMatchLimit"), Constant(Security, "kSecMatchLimitOne")),
                    (Constant(Security, "kSecReturnData"), Constant(CoreFoundation, "kCFBooleanTrue"))));
                var status = SecItemCopyMatching(query, out result);
                if (status != ErrSecSuccess) return (null, status);
                if (result == IntPtr.Zero || CFGetTypeID(result) != CFDataGetTypeID()) return (null, -3);

                var length = CFDataGetLength(result);
                var buffer = new byte[length];
                if (length > 0) Marshal.Copy(CFDataGetBytePtr(result), buffer, 0, (int)length);
                return (buffer, ErrSecSuccess);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return (null, -2);
            }
            finally
            {
                if (result != IntPtr.Zero) CFRelease(result);
                foreach (var r in refs) if (r != IntPtr.Zero) CFRelease(r);
            }
        }

        // Removes one item. Not-found counts as done.
        internal static int Delete(string service, string account)
        {
            if (!OperatingSystem.IsMacOS()) return -1;
            var refs = new List<IntPtr>();
            try
            {
                var svc = Track(refs, CFStringCreateWithCString(IntPtr.Zero, service, KCFStringEncodingUtf8));
                var acct = Track(refs, CFStringCreateWithCString(IntPtr.Zero, account, KCFStringEncodingUtf8));
                var query = Track(refs, Dictionary(
                    (Constant(Security, "kSecClass"), Constant(Security, "kSecClassGenericPassword")),
                    (Constant(Security, "kSecAttrService"), svc),
                    (Constant(Security, "kSecAttrAccount"), acct)));
                var status = SecItemDelete(query);
                return status == ErrSecItemNotFound ? ErrSecSuccess : status;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return -2;
            }
            finally
            {
                foreach (var r in refs) if (r != IntPtr.Zero) CFRelease(r);
            }
        }

        private static IntPtr Track(List<IntPtr> refs, IntPtr reference)
        {
            if (reference == IntPtr.Zero) throw new DllNotFoundException("CoreFoundation returned null");
            refs.Add(reference);
            return reference;
        }

        private static IntPtr Dictionary(params (IntPtr Key, IntPtr Value)[] pairs) =>
            CFDictionaryCreate(IntPtr.Zero,
                pairs.Select(p => p.Key).ToArray(), pairs.Select(p => p.Value).ToArray(), pairs.Length,
                Address(CoreFoundation, "kCFTypeDictionaryKeyCallBacks"),
                Address(CoreFoundation, "kCFTypeDictionaryValueCallBacks"));

        internal static string Describe(int status) => status switch
        {
            ErrSecSuccess => "ok",
            ErrSecItemNotFound => "not found",
            -1 => "not macOS",
            -2 => "the Keychain API is unavailable",
            -3 => "the item is not data",
            _ => $"OSStatus {status}",
        };
    }
}
