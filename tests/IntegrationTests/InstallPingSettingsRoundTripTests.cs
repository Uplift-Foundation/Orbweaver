using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-466: the four settings keys through a real settings.json, with someone else's
// keys beside them (the downgrade protection in OrbweaverSettings._unknownKeys).
[Collection("Settings")]
public class InstallPingSettingsRoundTripTests : IDisposable
{
    public void Dispose()
    {
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.InstallPingEnabled = true;
        OrbweaverSettings.InternalInstall = false;
    }

    [Fact]
    public void TheKeysPersistSurviveAReloadAndLeaveUnknownKeysAlone()
    {
        Directory.CreateDirectory(OrbweaverSettings.Directory);
        File.WriteAllText(OrbweaverSettings.Path_, "{ \"keyFromANewerBuild\": { \"a\": 1 } }");
        OrbweaverSettings.ReloadForTests();

        OrbweaverSettings.InstallId = "0123456789abcdef0123456789abcdef";
        OrbweaverSettings.LastInstallPingUtc = new DateTimeOffset(2026, 11, 24, 3, 0, 0, TimeSpan.Zero);
        OrbweaverSettings.InternalInstall = true;
        OrbweaverSettings.InstallPingEnabled = true;

        OrbweaverSettings.ReloadForTests();
        var onDisk = JsonNode.Parse(File.ReadAllText(OrbweaverSettings.Path_))!.AsObject();
        Assert.Equal("0123456789abcdef0123456789abcdef", onDisk["installId"]!.GetValue<string>());
        Assert.True(onDisk["internalInstall"]!.GetValue<bool>());
        Assert.True(onDisk["installPingEnabled"]!.GetValue<bool>());
        Assert.Equal(1, onDisk["keyFromANewerBuild"]!["a"]!.GetValue<int>());
        Assert.Equal("0123456789abcdef0123456789abcdef", OrbweaverSettings.InstallId);

        OrbweaverSettings.InstallPingEnabled = false;
        var after = JsonNode.Parse(File.ReadAllText(OrbweaverSettings.Path_))!.AsObject();
        Assert.False(after["installPingEnabled"]!.GetValue<bool>());
        Assert.Null(after["installId"]);
        Assert.Null(after["lastInstallPingUtc"]);
    }
}
