using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-306's product-readiness round: "Run slash commands in background jobs
// and Desktop sessions", through a real settings.json. It is on by default,
// so the case that matters most is the one a fresh install and an upgrade
// both hit: no key in the file at all, which must read as on — reading it as
// off would put every existing install back behind a Settings trip.
[Collection("Settings")]
public class BridgeCommandsSettingsTests : IDisposable
{
    private readonly string? _dirBefore = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", _dirBefore);
        OrbweaverSettings.ReloadForTests();
    }

    private static string NewSettingsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb306-bridge-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void PointSettingsAt(string dir)
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    [Fact]
    public void WithNoFileItIsOnAndRoundTripsBothWays()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        Assert.True(OrbweaverSettings.BridgeCommandsEnabled);

        OrbweaverSettings.BridgeCommandsEnabled = false;
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;
        Assert.False(root["bridgeCommandsEnabled"]!.GetValue<bool>());

        OrbweaverSettings.ReloadForTests();
        Assert.False(OrbweaverSettings.BridgeCommandsEnabled);

        OrbweaverSettings.BridgeCommandsEnabled = true;
        OrbweaverSettings.ReloadForTests();
        Assert.True(OrbweaverSettings.BridgeCommandsEnabled);
    }

    // An upgrade: a file written before the key existed, with "Show cloud
    // sessions" off. The new setting reads on and does not follow the cloud
    // one, and a key this build does not know survives a save.
    [Fact]
    public void AnOlderFileWithoutTheKeyReadsOnAndKeepsKeysItDoesNotKnow()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            """{"version":1,"claudeCloudEnabled":false,"someFutureKey":42}""");
        PointSettingsAt(dir);

        Assert.True(OrbweaverSettings.BridgeCommandsEnabled);
        Assert.False(OrbweaverSettings.ClaudeCloudEnabled);

        OrbweaverSettings.BridgeCommandsEnabled = false;

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;
        Assert.False(root["bridgeCommandsEnabled"]!.GetValue<bool>());
        Assert.Equal(42, root["someFutureKey"]!.GetValue<int>());
        Assert.False(root["claudeCloudEnabled"]!.GetValue<bool>());
    }
}
