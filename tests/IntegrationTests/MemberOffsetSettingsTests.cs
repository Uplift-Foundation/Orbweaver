using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-268 T2: a team member dragged inside a laid-out group keeps its offset from
// its lead, stored under its SoundKey. Through a real settings file, because the
// failures are the file's: a key never written, read back under another name,
// written twice, or lost by an older build that does not know it.
[Collection("Settings")]
public class MemberOffsetSettingsTests
{
    private static string Stage(string? existing = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb268-offsets-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        if (existing is not null) File.WriteAllText(Path.Combine(dir, "settings.json"), existing);

        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
        return dir;
    }

    private static void Reload(string dir)
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    private static JsonObject OnDisk(string dir) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;

    [Fact]
    public void AMemberWithNoSavedOffsetHasNone()
    {
        Stage();

        Assert.Null(OrbweaverSettings.MemberOffsetFor("member"));
        Assert.Null(OrbweaverSettings.MemberOffsetFor(""));
        Assert.Null(OrbweaverSettings.MemberOffsetFor(null));
    }

    [Fact]
    public void AnOffsetIsWrittenUnderItsSoundKeyAndSurvivesAReload()
    {
        var dir = Stage();

        OrbweaverSettings.SetMemberOffset("proj\nfix-login\nreviewer", -40, 25);

        var entry = OnDisk(dir)["orbMemberOffsets"]!["proj\nfix-login\nreviewer"]!;
        Assert.Equal(-40, entry["dx"]!.GetValue<int>());
        Assert.Equal(25, entry["dy"]!.GetValue<int>());

        Reload(dir);
        Assert.Equal(new OrbweaverSettings.MemberOffset(-40, 25), OrbweaverSettings.MemberOffsetFor("PROJ\nfix-login\nREVIEWER"));
    }

    [Fact]
    public void TwoTeammatesSharingACwdKeepTheirOwnOffsets()
    {
        Stage();

        OrbweaverSettings.SetMemberOffset("dir\ntitle\nalice", 10, 10);
        OrbweaverSettings.SetMemberOffset("dir\ntitle\nbob", -10, 30);

        Assert.Equal(new OrbweaverSettings.MemberOffset(10, 10), OrbweaverSettings.MemberOffsetFor("dir\ntitle\nalice"));
        Assert.Equal(new OrbweaverSettings.MemberOffset(-10, 30), OrbweaverSettings.MemberOffsetFor("dir\ntitle\nbob"));
    }

    [Fact]
    public void AZeroOffsetIsKeptDistinctFromNone()
    {
        var dir = Stage();

        OrbweaverSettings.SetMemberOffset("member", 0, 0);

        Assert.Equal(new OrbweaverSettings.MemberOffset(0, 0), OrbweaverSettings.MemberOffsetFor("member"));
        Assert.NotNull(OnDisk(dir)["orbMemberOffsets"]!["member"]);
    }

    [Fact]
    public void ClearingAnOffsetRemovesItFromTheModelAndTheFile()
    {
        var dir = Stage();
        OrbweaverSettings.SetMemberOffset("member", 5, 6);
        OrbweaverSettings.SetMemberOffset("other", 7, 8);

        OrbweaverSettings.ClearMemberOffset("member");

        Assert.Null(OrbweaverSettings.MemberOffsetFor("member"));
        Assert.Null(OnDisk(dir)["orbMemberOffsets"]!["member"]);
        Assert.NotNull(OnDisk(dir)["orbMemberOffsets"]!["other"]);
    }

    [Fact]
    public void ANoOpSetOrClearDoesNotRewriteTheFileAndEmptyKeysAreIgnored()
    {
        var dir = Stage();
        OrbweaverSettings.SetMemberOffset("member", 5, 6);
        var path = Path.Combine(dir, "settings.json");
        File.Delete(path);

        OrbweaverSettings.SetMemberOffset("member", 5, 6);
        OrbweaverSettings.ClearMemberOffset("never-set");
        OrbweaverSettings.SetMemberOffset("", 1, 1);
        OrbweaverSettings.SetMemberOffset(null, 1, 1);
        OrbweaverSettings.ClearMemberOffset("");
        OrbweaverSettings.ClearMemberOffset(null);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ACarryCopiesNeverMovesAndNeverOverwrites()
    {
        var dir = Stage();
        OrbweaverSettings.SetMemberOffset("session-id", 11, 12);

        OrbweaverSettings.CarryMemberOffset("session-id", "dir\ntitle\nagent");

        Assert.Equal(new OrbweaverSettings.MemberOffset(11, 12), OrbweaverSettings.MemberOffsetFor("dir\ntitle\nagent"));
        Assert.Equal(new OrbweaverSettings.MemberOffset(11, 12), OrbweaverSettings.MemberOffsetFor("session-id"));

        // An offset the new key already has is somebody's choice.
        OrbweaverSettings.SetMemberOffset("other-old", 1, 2);
        OrbweaverSettings.SetMemberOffset("other-new", 3, 4);
        OrbweaverSettings.CarryMemberOffset("other-old", "other-new");
        Assert.Equal(new OrbweaverSettings.MemberOffset(3, 4), OrbweaverSettings.MemberOffsetFor("other-new"));

        Reload(dir);
        Assert.Equal(new OrbweaverSettings.MemberOffset(11, 12), OrbweaverSettings.MemberOffsetFor("dir\ntitle\nagent"));
    }

    [Theory]
    [InlineData(null, "new")]
    [InlineData("", "new")]
    [InlineData("nobody", "new")]
    [InlineData("old", null)]
    [InlineData("old", "")]
    public void ACarryWithNothingToCarryOrNowhereToPutItDoesNothing(string? oldKey, string? newKey)
    {
        var dir = Stage();
        OrbweaverSettings.SetMemberOffset("old", 1, 2);
        var path = Path.Combine(dir, "settings.json");
        File.Delete(path);

        OrbweaverSettings.CarryMemberOffset(oldKey, newKey);

        Assert.False(File.Exists(path));
        Assert.Null(OrbweaverSettings.MemberOffsetFor("new"));
    }

    [Fact]
    public void UnknownKeysSurviveASaveAndTheNewKeyIsWrittenOnce()
    {
        var dir = Stage("""{ "someFutureSetting": [1, 2] }""");

        OrbweaverSettings.SetMemberOffset("member", 5, 6);
        var text = File.ReadAllText(Path.Combine(dir, "settings.json"));

        Assert.Equal(2, OnDisk(dir)["someFutureSetting"]!.AsArray().Count);
        Assert.Equal(1, text.Split("\"orbMemberOffsets\":").Length - 1);
    }

    [Fact]
    public void AnOlderBuildThatDoesNotKnowTheKeyPutsItBackUntouched()
    {
        // An older build keeps unknown keys verbatim (its _unknownKeys). Simulate its
        // save by rewriting the file without touching orbMemberOffsets, and check this
        // build still reads the offsets after.
        var dir = Stage();
        OrbweaverSettings.SetMemberOffset("member", 5, 6);

        var root = OnDisk(dir);
        root["arrangeShape"] = "star";
        File.WriteAllText(Path.Combine(dir, "settings.json"), root.ToJsonString());
        Reload(dir);

        Assert.Equal(new OrbweaverSettings.MemberOffset(5, 6), OrbweaverSettings.MemberOffsetFor("member"));
    }

    [Theory]
    [InlineData("\"oops\"")]
    [InlineData("[1, 2]")]
    [InlineData("{ \"a\": 5, \"b\": { \"dx\": \"left\", \"dy\": 2 }, \"c\": { \"dx\": 1 } }")]
    public void AMalformedBlockCostsOnlyTheBadEntriesNotTheFile(string block)
    {
        Stage($$"""{ "orbMemberOffsets": {{block}}, "orbSize": 1.5 }""");

        Assert.Null(OrbweaverSettings.MemberOffsetFor("a"));
        Assert.Null(OrbweaverSettings.MemberOffsetFor("b"));
        Assert.Null(OrbweaverSettings.MemberOffsetFor("c"));
        Assert.Equal(1.5, OrbweaverSettings.OrbSize, 3);
    }

    [Fact]
    public void HandEditedFractionalOffsetsAreRoundedToWholePixels()
    {
        Stage("""{ "orbMemberOffsets": { "m": { "dx": 10.6, "dy": -3.4 } } }""");

        Assert.Equal(new OrbweaverSettings.MemberOffset(11, -3), OrbweaverSettings.MemberOffsetFor("m"));
    }
}
