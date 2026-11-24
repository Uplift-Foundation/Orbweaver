using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-421: the five Paperclip keys, through a real settings.json. The case that
// matters most is the upgrade: a file written before the keys existed must load
// as "off, fold, nothing chosen", and a key a later build adds must survive a
// save here. The board key is deliberately not among them - it lives in
// paperclip-credentials.json - and one test proves it never reaches this file.
[Collection("Settings")]
public class PaperclipSettingsTests : IDisposable
{
    private readonly string? _dirBefore = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", _dirBefore);
        OrbweaverSettings.ReloadForTests();
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb421-paperclip-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void PointAt(string dir)
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    [Fact]
    public void AllFiveKeysSurviveSaveAndLoad()
    {
        var dir = NewDir();
        PointAt(dir);

        OrbweaverSettings.PaperclipEnabled = true;
        OrbweaverSettings.PaperclipUrl = "http://192.0.2.10:3100";
        OrbweaverSettings.PaperclipCompanyId = "company-1";
        OrbweaverSettings.PaperclipSleeping = "hide";
        OrbweaverSettings.PaperclipHintShown = true;

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;
        Assert.True(root["paperclipEnabled"]!.GetValue<bool>());
        Assert.Equal("http://192.0.2.10:3100", root["paperclipUrl"]!.GetValue<string>());
        Assert.Equal("company-1", root["paperclipCompanyId"]!.GetValue<string>());
        Assert.Equal("hide", root["paperclipSleeping"]!.GetValue<string>());
        Assert.True(root["paperclipHintShown"]!.GetValue<bool>());

        OrbweaverSettings.ReloadForTests();
        Assert.True(OrbweaverSettings.PaperclipEnabled);
        Assert.Equal("http://192.0.2.10:3100", OrbweaverSettings.PaperclipUrl);
        Assert.Equal("company-1", OrbweaverSettings.PaperclipCompanyId);
        Assert.Equal("hide", OrbweaverSettings.PaperclipSleeping);
        Assert.True(OrbweaverSettings.PaperclipHintShown);
    }

    [Fact]
    public void AnOlderFileLoadsTheDefaults_AndKeepsKeysItDoesNotKnow()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{"version":1,"claudeCloudEnabled":true,"someFutureKey":42}""");
        PointAt(dir);

        Assert.False(OrbweaverSettings.PaperclipEnabled);
        Assert.Equal("", OrbweaverSettings.PaperclipUrl);
        Assert.Equal("", OrbweaverSettings.PaperclipCompanyId);
        Assert.Equal("fold", OrbweaverSettings.PaperclipSleeping);
        Assert.False(OrbweaverSettings.PaperclipHintShown);

        OrbweaverSettings.PaperclipEnabled = true;
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;
        Assert.Equal(42, root["someFutureKey"]!.GetValue<int>());
        Assert.True(root["claudeCloudEnabled"]!.GetValue<bool>());
        Assert.True(root["paperclipEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public void AnUnknownSleepingValue_ReadsAsFold()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{"version":1,"paperclipSleeping":"banana"}""");
        PointAt(dir);
        Assert.Equal("fold", OrbweaverSettings.PaperclipSleeping);
    }

    [Fact]
    public void TheBoardKey_NeverReachesSettingsJson()
    {
        var dir = NewDir();
        PointAt(dir);

        var creds = new PaperclipCredentials(dir);
        creds.Set("http://192.0.2.10:3100", "SECRET-BOARD-KEY", DateTime.UtcNow.AddDays(30));
        OrbweaverSettings.PaperclipEnabled = true;
        OrbweaverSettings.PaperclipUrl = "http://192.0.2.10:3100";

        Assert.DoesNotContain("SECRET-BOARD-KEY", File.ReadAllText(Path.Combine(dir, "settings.json")));
        Assert.Contains("SECRET-BOARD-KEY", File.ReadAllText(Path.Combine(dir, "paperclip-credentials.json")));
    }
}
