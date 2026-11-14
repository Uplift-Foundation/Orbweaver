using System.Text.Json.Nodes;
using Orbweaver;
using Xunit;

namespace Orbweaver.Tests;

// CB-276 QA: the hiddenOrbs key through a real settings file, the cases most
// likely to be missed. Complements HiddenOrbsSettingsTests.
[Collection("Settings")]
public class HiddenOrbsSettingsAdversarialTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb276-qa-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Point(string dir)
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    private static JsonObject Raw(string dir) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;

    private static int CountKey(string dir, string key) =>
        System.Text.RegularExpressions.Regex.Matches(
            File.ReadAllText(Path.Combine(dir, "settings.json")), "\"" + key + "\"\\s*:").Count;

    private static readonly DateTime T = new(2026, 11, 14, 20, 7, 13, DateTimeKind.Utc);

    private static HiddenOrb Orb(string id, string? peer = null) =>
        new(id, T, "title " + id, "Claude Code", "detail", peer);

    [Fact]
    public void TheKeyAppearsExactlyOnceOnDiskAfterLoadingAFileThatHasIt()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"version\": 1, \"newerBuildKey\": 7, \"hiddenOrbs\": { \"a\": { \"hiddenAt\": \"2026-11-14T20:07:13Z\", \"title\": \"t\", \"source\": \"s\", \"detail\": \"d\" } } }");
        Point(dir);

        OrbweaverSettings.HideOrb(Orb("b"));

        Assert.Equal(1, CountKey(dir, "hiddenOrbs"));
        var root = Raw(dir);
        Assert.Equal(7, root["newerBuildKey"]!.GetValue<int>());
        var hidden = (JsonObject)root["hiddenOrbs"]!;
        Assert.Equal(2, hidden.Count);
        Assert.NotNull(hidden["a"]);
        Assert.NotNull(hidden["b"]);
    }

    [Fact]
    public void EveryFieldRoundTripsIncludingPeerAndTheAbsenceOfPeer()
    {
        var dir = NewDir();
        Point(dir);
        OrbweaverSettings.HideOrb(Orb("local"));
        OrbweaverSettings.HideOrb(Orb("rc:acct:r1", "mini"));

        Point(dir);
        var list = OrbweaverSettings.HiddenOrbList();
        var local = list.Single(o => o.SessionId == "local");
        var rc = list.Single(o => o.SessionId == "rc:acct:r1");
        Assert.Equal(Orb("local"), local);
        Assert.Equal(Orb("rc:acct:r1", "mini"), rc);
        Assert.False(((JsonObject)Raw(dir)["hiddenOrbs"]!["local"]!).ContainsKey("peer"));
    }

    [Fact]
    public void ASecondOfPrecisionIsAllThatSurvivesSoSubSecondTimesAreTruncatedNotCorrupted()
    {
        var dir = NewDir();
        Point(dir);
        OrbweaverSettings.HideOrb(Orb("a") with { HiddenAtUtc = T.AddMilliseconds(900) });
        Point(dir);
        Assert.Equal(T, OrbweaverSettings.HiddenOrbList().Single().HiddenAtUtc);
    }

    [Fact]
    public void IdsDifferingOnlyInCaseAreOneEntryAndSurviveAReload()
    {
        var dir = NewDir();
        Point(dir);
        OrbweaverSettings.HideOrb(Orb("AbC"));
        OrbweaverSettings.HideOrb(Orb("abc"));
        Assert.Single(OrbweaverSettings.HiddenOrbList());
        Assert.Single((JsonObject)Raw(dir)["hiddenOrbs"]!);
        Point(dir);
        Assert.Single(OrbweaverSettings.HiddenOrbList());
        Assert.Contains("ABC", OrbweaverSettings.HiddenOrbIds());
    }

    [Fact]
    public void AHandEditedFileWithTwoCaseVariantsOfOneIdLoadsAsOneEntry()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"hiddenOrbs\": { \"Abc\": { \"title\": \"one\" }, \"abc\": { \"title\": \"two\" } } }");
        Point(dir);
        Assert.Single(OrbweaverSettings.HiddenOrbList());
        // And it must still save without throwing on a duplicate key.
        OrbweaverSettings.HideOrb(Orb("other"));
        Assert.Equal(2, ((JsonObject)Raw(dir)["hiddenOrbs"]!).Count);
    }

    [Theory]
    [InlineData("\"hiddenOrbs\": []")]
    [InlineData("\"hiddenOrbs\": \"nope\"")]
    [InlineData("\"hiddenOrbs\": 5")]
    [InlineData("\"hiddenOrbs\": null")]
    public void AHiddenOrbsKeyThatIsNotAnObjectLoadsAsEmptyAndTheFileStillSaves(string fragment)
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ " + fragment + ", \"chatTextScale\": 1.3 }");
        Point(dir);
        Assert.Empty(OrbweaverSettings.HiddenOrbList());

        OrbweaverSettings.HideOrb(Orb("a"));
        Assert.Equal(1, CountKey(dir, "hiddenOrbs"));
        Assert.Single(OrbweaverSettings.HiddenOrbList());
    }

    [Fact]
    public void AnEntryThatIsNotAnObjectIsSkippedWithoutDroppingItsNeighbours()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"hiddenOrbs\": { \"bad1\": 5, \"bad2\": \"x\", \"bad3\": null, \"bad4\": [], \"good\": { \"title\": \"g\" }, \"\": { \"title\": \"emptyid\" }, \"  \": { \"title\": \"ws\" } } }");
        Point(dir);
        var list = OrbweaverSettings.HiddenOrbList();
        Assert.Single(list);
        Assert.Equal("good", list[0].SessionId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"hiddenAt\": null,")]
    [InlineData("\"hiddenAt\": \"not a date\",")]
    [InlineData("\"hiddenAt\": 12345,")]
    [InlineData("\"hiddenAt\": {},")]
    [InlineData("\"hiddenAt\": \"\",")]
    public void ABadOrMissingTimestampKeepsTheHideWithAnUnknownTime(string hiddenAt)
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"hiddenOrbs\": { \"a\": { " + hiddenAt + " \"title\": \"t\" } } }");
        Point(dir);
        var orb = Assert.Single(OrbweaverSettings.HiddenOrbList());
        Assert.Equal(DateTime.MinValue, orb.HiddenAtUtc);
        Assert.Contains("a", OrbweaverSettings.HiddenOrbIds());
        Assert.Equal("", HiddenOrbs.WhenText(orb.HiddenAtUtc, DateTime.UtcNow));
    }

    [Fact]
    public void AnUnknownTimeIsWrittenBackAsNullAndStillReadsAsUnknown()
    {
        var dir = NewDir();
        Point(dir);
        OrbweaverSettings.HideOrb(Orb("a") with { HiddenAtUtc = DateTime.MinValue });
        Point(dir);
        Assert.Equal(DateTime.MinValue, OrbweaverSettings.HiddenOrbList().Single().HiddenAtUtc);
    }

    [Fact]
    public void ATimestampWithAnOffsetIsNormalisedToUtc()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"hiddenOrbs\": { \"a\": { \"hiddenAt\": \"2026-11-14T12:07:13-08:00\" } } }");
        Point(dir);
        Assert.Equal(T, OrbweaverSettings.HiddenOrbList().Single().HiddenAtUtc);
    }

    [Fact]
    public void ListIsNewestFirstWithUnknownTimesLastAndStableOnTies()
    {
        var dir = NewDir();
        Point(dir);
        OrbweaverSettings.HideOrb(Orb("old") with { HiddenAtUtc = T.AddHours(-2) });
        OrbweaverSettings.HideOrb(Orb("unknown") with { HiddenAtUtc = DateTime.MinValue });
        OrbweaverSettings.HideOrb(Orb("new") with { HiddenAtUtc = T });
        OrbweaverSettings.HideOrb(new HiddenOrb("tieB", T, "Bravo", "s", "d"));
        OrbweaverSettings.HideOrb(new HiddenOrb("tieA", T, "alpha", "s", "d"));
        Assert.Equal(new[] { "tieA", "tieB", "new", "old", "unknown" },
            OrbweaverSettings.HiddenOrbList().Select(o => o.SessionId).ToArray());
    }

    [Fact]
    public void ReadersGetCopiesNotTheLiveCollection()
    {
        var dir = NewDir();
        Point(dir);
        OrbweaverSettings.HideOrb(Orb("a"));
        var ids = OrbweaverSettings.HiddenOrbIds();
        var list = OrbweaverSettings.HiddenOrbList();
        OrbweaverSettings.RestoreAllHiddenOrbs();
        Assert.Contains("a", ids);
        Assert.Single(list);
        Assert.Empty(OrbweaverSettings.HiddenOrbIds());
    }

    [Fact]
    public void RestoreAndPruneWriteNothingWhenNothingChanged()
    {
        var dir = NewDir();
        Point(dir);
        OrbweaverSettings.HideOrb(Orb("a"));
        var path = Path.Combine(dir, "settings.json");
        var stamp = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        File.SetLastWriteTimeUtc(path, stamp.AddHours(-1));
        var backdated = File.GetLastWriteTimeUtc(path);

        Assert.False(OrbweaverSettings.RestoreHiddenOrb("never-hidden"));
        OrbweaverSettings.PruneHiddenOrbs(new[] { "never-hidden" });
        OrbweaverSettings.PruneHiddenOrbs(Array.Empty<string>());
        OrbweaverSettings.HideOrb(Orb("a")); // identical record: no write

        Assert.Equal(backdated, File.GetLastWriteTimeUtc(path));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void RestoreAllOnAnEmptyListWritesNothing()
    {
        var dir = NewDir();
        Point(dir);
        OrbweaverSettings.RestoreAllHiddenOrbs();
        Assert.False(File.Exists(Path.Combine(dir, "settings.json")));
    }

    [Fact]
    public void PruneRemovesABatchInOneWriteAndLeavesTheRest()
    {
        var dir = NewDir();
        Point(dir);
        foreach (var id in new[] { "a", "b", "c" }) OrbweaverSettings.HideOrb(Orb(id));
        OrbweaverSettings.PruneHiddenOrbs(new[] { "A", "c", "zzz" });
        Point(dir);
        Assert.Equal(new[] { "b" }, OrbweaverSettings.HiddenOrbIds().ToArray());
    }

    [Fact]
    public void RestoreIsCaseInsensitive()
    {
        var dir = NewDir();
        Point(dir);
        OrbweaverSettings.HideOrb(Orb("AbC"));
        Assert.True(OrbweaverSettings.RestoreHiddenOrb("abc"));
        Assert.Empty(OrbweaverSettings.HiddenOrbList());
        Point(dir);
        Assert.Empty(OrbweaverSettings.HiddenOrbList());
    }

    [Fact]
    public void UnknownFieldsInsideAnEntryAreDroppedButUnknownTopLevelKeysSurvive()
    {
        // Documented behaviour in the plan (section 4.2): pinned so a change is deliberate.
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"topLevelFromNewer\": { \"x\": 1 }, \"hiddenOrbs\": { \"a\": { \"title\": \"t\", \"fieldFromNewer\": true } } }");
        Point(dir);
        OrbweaverSettings.HideOrb(Orb("b"));
        var root = Raw(dir);
        Assert.NotNull(root["topLevelFromNewer"]);
        Assert.Null(root["hiddenOrbs"]!["a"]!["fieldFromNewer"]);
    }

    [Fact]
    public void HidingDoesNotDisturbOrbPositionsOrOtherSettings()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"chatTextScale\": 1.3, \"orbPositions\": { \"k\": { \"x\": 10, \"y\": 20 } } }");
        Point(dir);
        OrbweaverSettings.HideOrb(Orb("a"));
        var root = Raw(dir);
        Assert.Equal(1.3, root["chatTextScale"]!.GetValue<double>(), 3);
        Assert.NotNull(root["orbPositions"]!["k"]);
    }
}
