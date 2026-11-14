using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-276's "hiddenOrbs" key, through a real settings file.
//
// A hide is a promise that outlives a restart, so the file is where it has to
// be proved: written, read back by a fresh load, and left alone by everything
// else that saves. Two failures this file exists for, both silent. The
// KnownKeys trap — a key Save writes but KnownKeys does not list is carried in
// _unknownKeys as well — and a malformed entry that un-hides an orb because its
// timestamp did not parse. The second is the one a user would notice, and
// would have no way to explain.
[Collection("Settings")]
public class HiddenOrbsSettingsTests : IDisposable
{
    // CB-276: every test here points settings at a fresh directory and hides
    // things in it. Without putting the variable and the model back, the
    // hides outlive the test in memory and take orbs away from the next
    // class in this collection that scans one (LocalPersonaScanTests does).
    private readonly string? _previousSettingsDir =
        Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");

    public void Dispose()
    {
        OrbweaverSettings.RestoreAllHiddenOrbs();
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", _previousSettingsDir);
        OrbweaverSettings.ReloadForTests();
    }

    private static readonly DateTime At = new(2026, 11, 14, 20, 7, 13, DateTimeKind.Utc);

    private static string PointSettingsAtNewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-integrationtests-hidden-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
        return dir;
    }

    private static string SettingsFile(string dir) => Path.Combine(dir, "settings.json");

    private static void Reload() => OrbweaverSettings.ReloadForTests();

    private static string WriteRaw(string json)
    {
        var dir = PointSettingsAtNewDir();
        File.WriteAllText(SettingsFile(dir), json);
        Reload();
        return dir;
    }

    [Fact]
    public void AHideSurvivesARestartWithEveryField()
    {
        PointSettingsAtNewDir();
        OrbweaverSettings.HideOrb(new HiddenOrb("rc:work:abc", At, "fix the tray", "Other machine", "mini", "mini"));
        OrbweaverSettings.HideOrb(new HiddenOrb("local", At.AddMinutes(1), "newer", "Claude Code", "/x"));

        Reload();

        var list = OrbweaverSettings.HiddenOrbList();
        Assert.Equal(
            new[]
            {
                new HiddenOrb("local", At.AddMinutes(1), "newer", "Claude Code", "/x"),
                new HiddenOrb("rc:work:abc", At, "fix the tray", "Other machine", "mini", "mini")
            },
            list);
        Assert.Equal(DateTimeKind.Utc, list[0].HiddenAtUtc.Kind);
    }

    [Fact]
    public void APeerIsWrittenOnlyWhenThereIsOne()
    {
        var dir = PointSettingsAtNewDir();
        OrbweaverSettings.HideOrb(new HiddenOrb("local", At, "t", "Claude Code", "/x"));

        var entry = JsonNode.Parse(File.ReadAllText(SettingsFile(dir)))!["hiddenOrbs"]!["local"]!.AsObject();

        Assert.False(entry.ContainsKey("peer"));
        Assert.Equal("2026-11-14T20:07:13Z", entry["hiddenAt"]!.GetValue<string>());
    }

    [Fact]
    public void AnUnknownTimeIsWrittenAsNullAndReadBackAsUnknown()
    {
        var dir = PointSettingsAtNewDir();
        OrbweaverSettings.HideOrb(new HiddenOrb("local", DateTime.MinValue, "t", "Claude Code", ""));

        Assert.Null(JsonNode.Parse(File.ReadAllText(SettingsFile(dir)))!["hiddenOrbs"]!["local"]!["hiddenAt"]);

        Reload();
        Assert.Equal(DateTime.MinValue, OrbweaverSettings.HiddenOrbList().Single().HiddenAtUtc);
    }

    [Fact]
    public void ANewerBuildsKeysSurviveAHide()
    {
        var dir = WriteRaw("""{ "fromTheFuture": { "keep": true }, "showOrbs": true }""");

        OrbweaverSettings.HideOrb(new HiddenOrb("local", At, "t", "Claude Code", ""));

        var root = JsonNode.Parse(File.ReadAllText(SettingsFile(dir)))!.AsObject();
        Assert.True(root["fromTheFuture"]!["keep"]!.GetValue<bool>());
        Assert.NotNull(root["hiddenOrbs"]!["local"]);
    }

    [Fact]
    public void AFileThatAlreadyHasHiddenOrbsSavesItOnceAndDoesNotThrow()
    {
        // The KnownKeys trap. If "hiddenOrbs" were missing from KnownKeys the
        // loaded value would be carried in _unknownKeys too, and a later save
        // would either throw on the duplicate or write the stale copy.
        var dir = WriteRaw("""{ "hiddenOrbs": { "old": { "hiddenAt": "2026-11-01T00:00:00Z", "title": "old" } } }""");

        OrbweaverSettings.RestoreHiddenOrb("old");
        OrbweaverSettings.HideOrb(new HiddenOrb("new", At, "new", "Claude Code", ""));

        var text = File.ReadAllText(SettingsFile(dir));
        Assert.Equal(1, CountOf(text, "\"hiddenOrbs\""));

        var hidden = JsonNode.Parse(text)!["hiddenOrbs"]!.AsObject();
        Assert.Equal(new[] { "new" }, hidden.Select(p => p.Key).ToArray());
    }

    [Fact]
    public void IdsThatDifferOnlyInCaseAreOneEntry()
    {
        PointSettingsAtNewDir();
        OrbweaverSettings.HideOrb(new HiddenOrb("ABC", At, "first", "Claude Code", ""));
        OrbweaverSettings.HideOrb(new HiddenOrb("abc", At, "second", "Claude Code", ""));

        Assert.Single(OrbweaverSettings.HiddenOrbList());
        Assert.Contains("aBc", OrbweaverSettings.HiddenOrbIds());
        Assert.True(OrbweaverSettings.RestoreHiddenOrb("Abc"));
        Assert.Empty(OrbweaverSettings.HiddenOrbIds());
    }

    [Fact]
    public void HiddenOrbsThatIsNotAnObjectIsIgnored()
    {
        WriteRaw("""{ "hiddenOrbs": [ "a", "b" ] }""");

        Assert.Empty(OrbweaverSettings.HiddenOrbList());
    }

    [Fact]
    public void AnEntryThatIsNotAnObjectIsSkippedAndTheRestKept()
    {
        WriteRaw("""{ "hiddenOrbs": { "bad": 3, " ": { "title": "blank id" }, "good": { "title": "g" } } }""");

        Assert.Equal(new[] { "good" }, OrbweaverSettings.HiddenOrbList().Select(o => o.SessionId).ToArray());
    }

    [Theory]
    [InlineData("""{ "title": "t" }""")]
    [InlineData("""{ "title": "t", "hiddenAt": "yesterday-ish" }""")]
    [InlineData("""{ "title": "t", "hiddenAt": 12 }""")]
    public void ABadTimestampKeepsTheHide(string entry)
    {
        WriteRaw($$"""{ "hiddenOrbs": { "a": {{entry}} } }""");

        var orb = OrbweaverSettings.HiddenOrbList().Single();
        Assert.Equal(new HiddenOrb("a", DateTime.MinValue, "t", "", ""), orb);
    }

    [Fact]
    public void RestoringOneLeavesTheOthers()
    {
        PointSettingsAtNewDir();
        OrbweaverSettings.HideOrb(new HiddenOrb("a", At, "a", "Claude Code", ""));
        OrbweaverSettings.HideOrb(new HiddenOrb("b", At, "b", "Claude Code", ""));

        Assert.True(OrbweaverSettings.RestoreHiddenOrb("a"));
        Reload();

        Assert.Equal(new[] { "b" }, OrbweaverSettings.HiddenOrbIds().ToArray());
    }

    [Fact]
    public void RestoreAllEmptiesTheList()
    {
        PointSettingsAtNewDir();
        OrbweaverSettings.HideOrb(new HiddenOrb("a", At, "a", "Claude Code", ""));
        OrbweaverSettings.HideOrb(new HiddenOrb("b", At, "b", "Claude Code", ""));

        OrbweaverSettings.RestoreAllHiddenOrbs();
        Reload();

        Assert.Empty(OrbweaverSettings.HiddenOrbList());
    }

    [Fact]
    public void PruningABatchForgetsOnlyThoseIds()
    {
        PointSettingsAtNewDir();
        foreach (var id in new[] { "a", "b", "c" })
            OrbweaverSettings.HideOrb(new HiddenOrb(id, At, id, "Claude Code", ""));

        OrbweaverSettings.PruneHiddenOrbs(new[] { "a", "C", "not-hidden" });
        Reload();

        Assert.Equal(new[] { "b" }, OrbweaverSettings.HiddenOrbIds().ToArray());
    }

    [Fact]
    public void NothingIsWrittenWhenNothingChanged()
    {
        // No file exists in a fresh dir, so a write of any kind would create
        // one. Each call below has nothing to do.
        var dir = PointSettingsAtNewDir();

        Assert.False(OrbweaverSettings.RestoreHiddenOrb("absent"));
        OrbweaverSettings.RestoreAllHiddenOrbs();
        OrbweaverSettings.PruneHiddenOrbs(new[] { "absent" });
        Assert.False(File.Exists(SettingsFile(dir)));

        // And re-hiding an identical entry does not write again.
        var orb = new HiddenOrb("a", At, "a", "Claude Code", "");
        OrbweaverSettings.HideOrb(orb);
        File.Delete(SettingsFile(dir));
        OrbweaverSettings.HideOrb(orb);
        Assert.False(File.Exists(SettingsFile(dir)));
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
