using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-423: every file that holds a secret is written through OwnerOnlyFile, so on
// Windows it has an explicit owner-only ACL with inheritance off, and a wider
// file already on disk does not hand its ACL to the rewrite. Each test writes one
// file through the real code path that owns it and reads the permissions back:
// the ACL on Windows, mode 0600 elsewhere.
[Collection("Settings")]
public sealed class OwnerOnlySecretFileTests : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb423-" + Guid.NewGuid().ToString("N"));

    public OwnerOnlySecretFileTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", _dir);
        OpenClawIdentity.ResetForTests();
        PeerIdentity.ForgetForTests();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", _previous);
        OpenClawIdentity.ResetForTests();
        PeerIdentity.ForgetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void OpenClawIdentityFile() =>
        WriteOverWiderFile(Path.Combine(_dir, "openclaw-identity.json"), () => OpenClawIdentity.Current());

    [Fact]
    public void OpenClawDeviceTokensFile() =>
        WriteOverWiderFile(Path.Combine(_dir, "openclaw-devices.json"), () => OpenClawIdentity.SetTokenFor("host-a", "tok"));

    [Fact]
    public void PeerIdentityFile() =>
        WriteOverWiderFile(Path.Combine(_dir, "peer-identity.json"), () => PeerIdentity.Remember(new PeerIdentity.Peer("ab", "m")));

    [Fact]
    public void RelayIdentityFile() =>
        WriteOverWiderFile(Path.Combine(_dir, RelayIdentity.FileName),
            () => new RelayIdentity(_dir, PlainSecretStore.Instance).Keys());

    [Fact]
    public void ClaudeCredentialCacheFile()
    {
        var source = new KeychainCredentialSource("cb423-service");
        var path = Path.Combine(_dir, "credential-cache", "cb423-service.json");
        WriteOverWiderFile(path, () => source.WriteDiskCache("stamp", "{}"));
    }

    [Fact]
    public void TheWriterKeepsTheContentAndLeavesNoTempFile()
    {
        var path = Path.Combine(_dir, "sub", "f.json");
        OwnerOnlyFile.WriteAllText(path, "{\"a\":1}");
        OwnerOnlyFile.WriteAllText(path, "{\"a\":2}");
        Assert.Equal("{\"a\":2}", File.ReadAllText(path));
        Assert.Equal(new[] { "f.json" }, Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName).ToArray());
        AssertOwnerOnly(path);
    }

    private static void WriteOverWiderFile(string path, Action write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
        Widen(path);

        write();

        Assert.True(File.Exists(path));
        AssertOwnerOnly(path);
    }

    private static void Widen(string path)
    {
        if (OperatingSystem.IsWindows()) WidenWindows(path);
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void WidenWindows(string path)
    {
        var wide = new FileInfo(path).GetAccessControl();
        wide.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(wide);
    }

    private static void AssertOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows()) AssertOwnerOnlyWindows(path);
        else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertOwnerOnlyWindows(string path)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected, "inheritance must be off");
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        var only = Assert.Single(rules);
        Assert.Equal(me, only.IdentityReference);
        Assert.Equal(AccessControlType.Allow, only.AccessControlType);
        Assert.Equal(FileSystemRights.FullControl, only.FileSystemRights);
        Assert.False(only.IsInherited);
    }
}
