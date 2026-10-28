using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-287: accountOrbOverrides through a real settings.json.
[Collection("Settings")]
public class AccountOrbOverrideSettingsTests
{
    private const string Icon = "0123456789ab-0123456789abcdef.png";

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "orb287-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void PointAt(string dir)
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    private static JsonObject Read(string dir) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;

    [Fact]
    public void RoundTrips_AllFields()
    {
        var dir = NewDir(); PointAt(dir);
        OrbweaverSettings.SetAccountOrbOverride("account:~", new("AC", "Acme", Icon));

        var entry = Read(dir)["accountOrbOverrides"]!["account:~"]!.AsObject();
        Assert.Equal("AC", entry["initials"]!.GetValue<string>());
        Assert.Equal("Acme", entry["name"]!.GetValue<string>());
        Assert.Equal(Icon, entry["iconFile"]!.GetValue<string>());

        PointAt(dir);
        Assert.Equal(new AccountOrbOverride("AC", "Acme", Icon), OrbweaverSettings.AccountOrbOverrideFor("account:~"));
    }

    [Fact]
    public void UnknownTopLevelKeys_Preserved()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"version\": 1, \"fromNewerBuild\": \"keep me\" }");
        PointAt(dir);
        OrbweaverSettings.SetAccountOrbOverride("account:~", new("AC", null, null));

        var root = Read(dir);
        Assert.Equal("keep me", root["fromNewerBuild"]!.GetValue<string>());
        Assert.NotNull(root["accountOrbOverrides"]);
    }

    [Fact]
    public void Key_IsCaseInsensitive()
    {
        PointAt(NewDir());
        OrbweaverSettings.SetAccountOrbOverride(@"account:C:\Users\A\.claude", new("AC", null, null));
        Assert.NotNull(OrbweaverSettings.AccountOrbOverrideFor(@"account:c:\users\a\.CLAUDE"));
    }

    [Fact]
    public void EmptyEntryAndNonObjectInFile_Dropped()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"accountOrbOverrides\": { \"a\": {}, \"b\": 5, \"c\": { \"initials\": \" \" }, \"d\": { \"name\": \"Dee\" } } }");
        PointAt(dir);
        Assert.Null(OrbweaverSettings.AccountOrbOverrideFor("a"));
        Assert.Null(OrbweaverSettings.AccountOrbOverrideFor("b"));
        Assert.Null(OrbweaverSettings.AccountOrbOverrideFor("c"));
        Assert.Equal("Dee", OrbweaverSettings.AccountOrbOverrideFor("d")!.Name);
    }

    [Fact]
    public void FileWithoutKey_LoadsEmpty()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"version\": 1 }");
        PointAt(dir);
        Assert.Null(OrbweaverSettings.AccountOrbOverrideFor("account:~"));
    }

    [Fact]
    public void Clearing_RemovesEntry_AndEmptyKeyIsIgnored()
    {
        var dir = NewDir(); PointAt(dir);
        OrbweaverSettings.SetAccountOrbOverride("k", new("A", null, null));
        OrbweaverSettings.SetAccountOrbOverride("k", new(" ", null, null));
        Assert.Null(OrbweaverSettings.AccountOrbOverrideFor("k"));
        Assert.False(Read(dir)["accountOrbOverrides"]!.AsObject().ContainsKey("k"));

        OrbweaverSettings.SetAccountOrbOverride("", new("A", null, null));
        Assert.Null(OrbweaverSettings.AccountOrbOverrideFor(""));
        OrbweaverSettings.SetAccountOrbOverride("never", null); // absent: no-op
    }

    [Fact]
    public void SettingSameValueTwice_IsNoOp()
    {
        PointAt(NewDir());
        OrbweaverSettings.SetAccountOrbOverride("k", new("A", null, null));
        OrbweaverSettings.SetAccountOrbOverride("k", new(" A ", null, null));
        Assert.Equal("A", OrbweaverSettings.AccountOrbOverrideFor("k")!.Initials);
    }

    [Fact]
    public void Customization_WritesClipsAndRaisesChanged()
    {
        PointAt(NewDir());
        var raised = new List<string>();
        void On(string k) => raised.Add(k);
        AccountOrbCustomization.Changed += On;
        try
        {
            AccountOrbCustomization.SetInitials("k", "abc");
            AccountOrbCustomization.SetName("k", "  Acme ");
            Assert.Equal(new AccountOrbOverride("ab", "Acme", null), AccountOrbCustomization.Current("k"));

            var look = AccountOrbCustomization.Preview(new("k", "k", AccountUsageSource.Codex, "derived"));
            Assert.Equal("Acme", look.DisplayName);

            AccountOrbCustomization.SetInitials("k", "  ");
            Assert.Null(AccountOrbCustomization.Current("k")!.Initials);

            AccountOrbCustomization.RemoveIcon("k");
            AccountOrbCustomization.Reset("k");
            Assert.Null(AccountOrbCustomization.Current("k"));
            Assert.Equal(5, raised.Count(x => x == "k"));

            AccountOrbCustomization.Reset("");
            AccountOrbCustomization.SetName("", "x");
            Assert.Null(AccountOrbCustomization.Current(""));
            Assert.Equal(5, raised.Count);

            Assert.Equal(IconImportError.UnsupportedFormat, AccountOrbCustomization.SetIcon("k", "x").Error);
        }
        finally { AccountOrbCustomization.Changed -= On; }
    }
}
