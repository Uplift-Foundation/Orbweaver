using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-282 / N1: groupNamesVisible through a real settings file — default off, written
// once under its own key, round-tripped, tolerant of a bad value, and a newer build's
// unknown keys still preserved beside it.
[Collection("Settings")]
public class GroupNamesSettingsRoundTripTests
{
    private static string Stage(string? existing = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb282-names-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        if (existing is not null) File.WriteAllText(Path.Combine(dir, "settings.json"), existing);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
        return dir;
    }

    private static JsonObject OnDisk(string dir) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;

    [Fact]
    public void ItDefaultsToOffAndIsWrittenOnceUnderItsOwnKeyAfterAFlush()
    {
        var dir = Stage();
        Assert.False(OrbweaverSettings.GroupNamesVisible);

        OrbweaverSettings.GroupNamesVisible = true;
        OrbweaverSettings.FlushPendingSave();

        Assert.True(OnDisk(dir)["groupNamesVisible"]!.GetValue<bool>());
        Assert.Equal(1, File.ReadAllText(Path.Combine(dir, "settings.json")).Split("\"groupNamesVisible\":").Length - 1);
    }

    [Fact]
    public void ItSurvivesAReloadAndTurningItOffSurvivesToo()
    {
        var dir = Stage();
        OrbweaverSettings.GroupNamesVisible = true;
        OrbweaverSettings.FlushPendingSave();
        OrbweaverSettings.ReloadForTests();
        Assert.True(OrbweaverSettings.GroupNamesVisible);

        OrbweaverSettings.GroupNamesVisible = false;
        OrbweaverSettings.FlushPendingSave();
        OrbweaverSettings.ReloadForTests();
        Assert.False(OrbweaverSettings.GroupNamesVisible);
        Assert.False(OnDisk(dir)["groupNamesVisible"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void AHandEditedNonBoolFallsBackToOffWithoutResettingTheFile(string value)
    {
        Stage($$"""{ "groupNamesVisible": {{value}}, "orbSize": 1.5 }""");

        Assert.False(OrbweaverSettings.GroupNamesVisible);
        Assert.Equal(1.5, OrbweaverSettings.OrbSize, 3);
    }

    [Fact]
    public void UnknownKeysFromANewerVersionSurviveASaveBesideIt()
    {
        var dir = Stage("""{ "someFutureSetting": { "a": [1, 2, 3] } }""");

        OrbweaverSettings.GroupNamesVisible = true;
        OrbweaverSettings.FlushPendingSave();

        Assert.Equal(3, OnDisk(dir)["someFutureSetting"]!["a"]!.AsArray().Count);
    }

    [Fact]
    public void AnOlderBuildsRewriteThatDropsNothingKeepsTheSettingReadable()
    {
        // An older build keeps unknown keys verbatim, so the key it does not know is
        // put back untouched. Simulate that rewrite and read the file back.
        var dir = Stage();
        OrbweaverSettings.GroupNamesVisible = true;
        OrbweaverSettings.FlushPendingSave();

        var root = OnDisk(dir);
        root["arrangeShape"] = "star";
        File.WriteAllText(Path.Combine(dir, "settings.json"), root.ToJsonString());
        OrbweaverSettings.ReloadForTests();

        Assert.True(OrbweaverSettings.GroupNamesVisible);
        Assert.Equal("star", OrbweaverSettings.ArrangeShape);
    }
}
