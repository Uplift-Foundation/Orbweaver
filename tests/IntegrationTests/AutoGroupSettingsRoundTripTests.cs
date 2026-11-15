using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-281: autoGroupBy and the a- groups through a real settings file — Off when absent or
// junk, written once under its own key, round-tripped, auto groups and their anchors
// surviving a save and load, and a newer build's unknown keys kept beside them.
[Collection("Settings")]
public class AutoGroupSettingsRoundTripTests
{
    private static string Stage(string? existing = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb281-auto-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        if (existing is not null) File.WriteAllText(Path.Combine(dir, "settings.json"), existing);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
        return dir;
    }

    private static JsonObject OnDisk(string dir) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;

    [Fact]
    public void ItDefaultsToOffWhenTheKeyIsAbsent()
    {
        Stage();
        Assert.Equal(AutoGroupBy.Off, OrbweaverSettings.AutoGroupBy);
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("cli")]
    [InlineData("profile")]
    [InlineData("account")]
    [InlineData("off")]
    public void EachChoiceIsWrittenOnceAndSurvivesAReload(string name)
    {
        var dir = Stage();
        var by = OrbAutoGroups.Parse(name);

        OrbweaverSettings.AutoGroupBy = by;
        OrbweaverSettings.FlushPendingSave();

        Assert.Equal(name, OnDisk(dir)["autoGroupBy"]!.GetValue<string>());
        Assert.Equal(1, File.ReadAllText(Path.Combine(dir, "settings.json")).Split("\"autoGroupBy\":").Length - 1);

        OrbweaverSettings.ReloadForTests();
        Assert.Equal(by, OrbweaverSettings.AutoGroupBy);
    }

    [Theory]
    [InlineData("\"galaxy\"")]
    [InlineData("7")]
    [InlineData("null")]
    [InlineData("true")]
    public void AHandEditedJunkValueReadsAsOffWithoutResettingTheFile(string value)
    {
        Stage($$"""{ "autoGroupBy": {{value}}, "orbSize": 1.5 }""");

        Assert.Equal(AutoGroupBy.Off, OrbweaverSettings.AutoGroupBy);
        Assert.Equal(1.5, OrbweaverSettings.OrbSize, 3);
    }

    [Fact]
    public void AutoGroupsAndTheirAnchorsAndRenamesSurviveASaveAndLoad()
    {
        var dir = Stage();
        var id = OrbAutoGroups.IdFor(AutoGroupBy.Cli, "claude");

        OrbweaverSettings.EnsureAutoOrbGroups(new[] { (id, "Claude") });
        OrbweaverSettings.SetOrbGroupAnchor(id, new OrbweaverSettings.OrbPlacement(120, 340));
        OrbweaverSettings.RenameOrbGroup(id, "My Claudes");
        OrbweaverSettings.FlushPendingSave();
        Assert.Equal("My Claudes", OnDisk(dir)["orbGroups"]!["groups"]![id]!["name"]!.GetValue<string>());

        OrbweaverSettings.ReloadForTests();
        var group = OrbweaverSettings.OrbGroupFor(id)!;
        Assert.Equal("My Claudes", group.Name);
        Assert.Equal(120, group.Anchor!.X);
        Assert.Equal(340, group.Anchor.Y);
    }

    [Fact]
    public void EnsuringTheSameGroupAgainChangesNothingAndNothingWantedIsANoOp()
    {
        var dir = Stage();
        var id = OrbAutoGroups.IdFor(AutoGroupBy.Cli, "codex");
        OrbweaverSettings.EnsureAutoOrbGroups(new[] { (id, "Codex") });
        OrbweaverSettings.FlushPendingSave();
        var written = File.GetLastWriteTimeUtc(Path.Combine(dir, "settings.json"));
        var text = File.ReadAllText(Path.Combine(dir, "settings.json"));

        Thread.Sleep(20);
        OrbweaverSettings.EnsureAutoOrbGroups(new[] { (id, "Codex") });
        OrbweaverSettings.EnsureAutoOrbGroups(Array.Empty<(string, string)>());
        OrbweaverSettings.FlushPendingSave();

        Assert.Equal(text, File.ReadAllText(Path.Combine(dir, "settings.json")));
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(dir, "settings.json")));
    }

    [Fact]
    public void TheListIncludesAutoGroupsAfterThePredefinedFour()
    {
        Stage();
        var id = OrbAutoGroups.IdFor(AutoGroupBy.Directory, "/x/y");
        OrbweaverSettings.EnsureAutoOrbGroups(new[] { (id, "y") });

        var ids = OrbweaverSettings.OrbGroupList().Select(g => g.Id).ToList();
        Assert.Equal(OrbGroups.PredefinedIds, ids.Take(4));
        Assert.Equal(id, ids[4]);
    }

    [Fact]
    public void UnknownKeysFromANewerVersionSurviveASaveBesideIt()
    {
        var dir = Stage("""{ "someFutureSetting": { "a": [1, 2, 3] } }""");

        OrbweaverSettings.AutoGroupBy = AutoGroupBy.Profile;
        OrbweaverSettings.FlushPendingSave();

        var root = OnDisk(dir);
        Assert.Equal("profile", root["autoGroupBy"]!.GetValue<string>());
        Assert.Equal(3, root["someFutureSetting"]!["a"]!.AsArray().Count);
    }
}
