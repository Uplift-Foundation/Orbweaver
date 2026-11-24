using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-466's four settings keys: defaults, a real file round trip, unknown keys kept
// beside them, the opt-out forgetting the ID, and the adapter's auto-check gate.
// Each case rewrites settings.json by hand and reloads, as UpdateSettingsTests does.
[Collection("Settings")]
public class InstallPingSettingsTests : IDisposable
{
    public void Dispose()
    {
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.InstallPingEnabled = true;
        OrbweaverSettings.InternalInstall = false;
        OrbweaverSettings.AutoCheckForUpdates = true;
    }

    private static void Write(string json)
    {
        Directory.CreateDirectory(OrbweaverSettings.Directory);
        File.WriteAllText(OrbweaverSettings.Path_, json);
        OrbweaverSettings.ReloadForTests();
    }

    private static JsonObject Read() =>
        JsonNode.Parse(File.ReadAllText(OrbweaverSettings.Path_))!.AsObject();

    [Fact]
    public void AFileWithNoPingKeysGetsTheDefaults()
    {
        Write("{}");
        Assert.True(OrbweaverSettings.InstallPingEnabled);
        Assert.Null(OrbweaverSettings.InstallId);
        Assert.Null(OrbweaverSettings.LastInstallPingUtc);
        Assert.False(OrbweaverSettings.InternalInstall);
    }

    [Fact]
    public void LoadingNeverMintsAnId()
    {
        Write("{}");
        _ = OrbweaverSettings.InstallPingEnabled;
        OrbweaverSettings.InternalInstall = false; // forces a Save
        Assert.False(Read().ContainsKey("installId") && Read()["installId"] is not null);
    }

    [Fact]
    public void EveryKeyRoundTripsThroughARealFile()
    {
        Write("{ \"someoneElsesKey\": 7 }");
        var at = new DateTimeOffset(2026, 11, 24, 8, 30, 0, TimeSpan.Zero);
        OrbweaverSettings.InstallId = "abc";
        OrbweaverSettings.LastInstallPingUtc = at;
        OrbweaverSettings.InternalInstall = true;

        var onDisk = Read();
        Assert.True(onDisk["installPingEnabled"]!.GetValue<bool>());
        Assert.Equal("abc", onDisk["installId"]!.GetValue<string>());
        Assert.True(onDisk["internalInstall"]!.GetValue<bool>());
        Assert.Equal(7, onDisk["someoneElsesKey"]!.GetValue<int>());

        OrbweaverSettings.ReloadForTests();
        Assert.Equal("abc", OrbweaverSettings.InstallId);
        Assert.Equal(at, OrbweaverSettings.LastInstallPingUtc);
        Assert.True(OrbweaverSettings.InternalInstall);
    }

    [Fact]
    public void AGarbledStampDegradesToNeverPinged()
    {
        Write("{ \"lastInstallPingUtc\": \"not a date\", \"installId\": \"  \" }");
        Assert.Null(OrbweaverSettings.LastInstallPingUtc);
        OrbweaverSettings.InstallId = " ";
        Assert.Null(OrbweaverSettings.InstallId);
    }

    [Fact]
    public void ClearingTheStampWritesNullBack()
    {
        Write("{}");
        OrbweaverSettings.LastInstallPingUtc = DateTimeOffset.UtcNow;
        OrbweaverSettings.LastInstallPingUtc = null;
        Assert.Null(OrbweaverSettings.LastInstallPingUtc);
    }

    [Fact]
    public void TurningItOffForgetsTheIdAndTheStampAndOnStartsFresh()
    {
        Write("{}");
        OrbweaverSettings.InstallId = "abc";
        OrbweaverSettings.LastInstallPingUtc = DateTimeOffset.UtcNow;

        OrbweaverSettings.InstallPingEnabled = false;
        Assert.False(Read()["installPingEnabled"]!.GetValue<bool>());
        Assert.Null(OrbweaverSettings.InstallId);
        Assert.Null(OrbweaverSettings.LastInstallPingUtc);

        OrbweaverSettings.InstallPingEnabled = true;
        Assert.Null(OrbweaverSettings.InstallId);
        Assert.True(OrbweaverSettings.InstallPingEnabled);
    }

    [Fact]
    public void TurningItOnKeepsAnExistingId()
    {
        Write("{}");
        OrbweaverSettings.InstallId = "abc";
        OrbweaverSettings.InstallPingEnabled = true;
        Assert.Equal("abc", OrbweaverSettings.InstallId);
    }

    [Fact]
    public void TheAdapterIsOnlyEnabledWithTheSettingAndAutomaticChecks()
    {
        Write("{}");
        var s = new OrbweaverInstallPingSettings();
        Assert.True(s.Enabled);

        OrbweaverSettings.AutoCheckForUpdates = false;
        Assert.False(s.Enabled);

        OrbweaverSettings.AutoCheckForUpdates = true;
        OrbweaverSettings.InstallPingEnabled = false;
        Assert.False(s.Enabled);
    }

    [Fact]
    public void TheAdapterReadsAndWritesTheRealSettings()
    {
        Write("{}");
        var s = new OrbweaverInstallPingSettings();
        var at = new DateTimeOffset(2026, 11, 24, 1, 0, 0, TimeSpan.Zero);
        s.InstallId = "xyz";
        s.LastPing = at;
        Assert.Equal("xyz", OrbweaverSettings.InstallId);
        Assert.Equal(at, OrbweaverSettings.LastInstallPingUtc);
        Assert.Equal("xyz", s.InstallId);
        Assert.Equal(at, s.LastPing);

        Assert.False(s.Internal);
        OrbweaverSettings.InternalInstall = true;
        Assert.True(s.Internal);
    }
}
