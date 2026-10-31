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

        Assert.Null(OrbweaverSettings.OrbMemberOffsetFor("member"));
        Assert.Null(OrbweaverSettings.OrbMemberOffsetFor(""));
        Assert.Null(OrbweaverSettings.OrbMemberOffsetFor(null));
    }

    [Fact]
    public void AnOffsetIsWrittenUnderItsSoundKeyAndSurvivesAReload()
    {
        var dir = Stage();

        OrbweaverSettings.SetOrbMemberOffset("proj\nfix-login\nreviewer", (-40.0, 25.0));

        var entry = OnDisk(dir)["orbMemberOffsets"]!["proj\nfix-login\nreviewer"]!;
        Assert.Equal(-40, entry["dx"]!.GetValue<int>());
        Assert.Equal(25, entry["dy"]!.GetValue<int>());

        Reload(dir);
        Assert.Equal(((double, double)?)(-40.0, 25.0), OrbweaverSettings.OrbMemberOffsetFor("PROJ\nfix-login\nREVIEWER"));
    }

    [Fact]
    public void TwoTeammatesSharingACwdKeepTheirOwnOffsets()
    {
        Stage();

        OrbweaverSettings.SetOrbMemberOffset("dir\ntitle\nalice", (10.0, 10.0));
        OrbweaverSettings.SetOrbMemberOffset("dir\ntitle\nbob", (-10.0, 30.0));

        Assert.Equal(((double, double)?)(10.0, 10.0), OrbweaverSettings.OrbMemberOffsetFor("dir\ntitle\nalice"));
        Assert.Equal(((double, double)?)(-10.0, 30.0), OrbweaverSettings.OrbMemberOffsetFor("dir\ntitle\nbob"));
    }

    [Fact]
    public void AZeroOffsetIsKeptDistinctFromNone()
    {
        var dir = Stage();

        OrbweaverSettings.SetOrbMemberOffset("member", (0.0, 0.0));

        Assert.Equal(((double, double)?)(0.0, 0.0), OrbweaverSettings.OrbMemberOffsetFor("member"));
        Assert.NotNull(OnDisk(dir)["orbMemberOffsets"]!["member"]);
    }

    [Fact]
    public void ClearingAnOffsetRemovesItFromTheModelAndTheFile()
    {
        var dir = Stage();
        OrbweaverSettings.SetOrbMemberOffset("member", (5.0, 6.0));
        OrbweaverSettings.SetOrbMemberOffset("other", (7.0, 8.0));

        OrbweaverSettings.SetOrbMemberOffset("member", null);

        Assert.Null(OrbweaverSettings.OrbMemberOffsetFor("member"));
        Assert.Null(OnDisk(dir)["orbMemberOffsets"]!["member"]);
        Assert.NotNull(OnDisk(dir)["orbMemberOffsets"]!["other"]);
    }

    [Fact]
    public void ANoOpSetOrClearDoesNotRewriteTheFileAndEmptyKeysAreIgnored()
    {
        var dir = Stage();
        OrbweaverSettings.SetOrbMemberOffset("member", (5.0, 6.0));
        var path = Path.Combine(dir, "settings.json");
        File.Delete(path);

        OrbweaverSettings.SetOrbMemberOffset("member", (5.0, 6.0));
        OrbweaverSettings.SetOrbMemberOffset("never-set", null);
        OrbweaverSettings.SetOrbMemberOffset("", (1.0, 1.0));
        OrbweaverSettings.SetOrbMemberOffset(null, (1.0, 1.0));
        OrbweaverSettings.SetOrbMemberOffset("", null);
        OrbweaverSettings.SetOrbMemberOffset(null, null);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ACarryCopiesNeverMovesAndNeverOverwrites()
    {
        var dir = Stage();
        OrbweaverSettings.SetOrbMemberOffset("session-id", (11.0, 12.0));

        OrbweaverSettings.CarryOrbMemberOffset("session-id", "dir\ntitle\nagent");

        Assert.Equal(((double, double)?)(11.0, 12.0), OrbweaverSettings.OrbMemberOffsetFor("dir\ntitle\nagent"));
        Assert.Equal(((double, double)?)(11.0, 12.0), OrbweaverSettings.OrbMemberOffsetFor("session-id"));

        // An offset the new key already has is somebody's choice.
        OrbweaverSettings.SetOrbMemberOffset("other-old", (1.0, 2.0));
        OrbweaverSettings.SetOrbMemberOffset("other-new", (3.0, 4.0));
        OrbweaverSettings.CarryOrbMemberOffset("other-old", "other-new");
        Assert.Equal(((double, double)?)(3.0, 4.0), OrbweaverSettings.OrbMemberOffsetFor("other-new"));

        Reload(dir);
        Assert.Equal(((double, double)?)(11.0, 12.0), OrbweaverSettings.OrbMemberOffsetFor("dir\ntitle\nagent"));
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
        OrbweaverSettings.SetOrbMemberOffset("old", (1.0, 2.0));
        var path = Path.Combine(dir, "settings.json");
        File.Delete(path);

        OrbweaverSettings.CarryOrbMemberOffset(oldKey, newKey);

        Assert.False(File.Exists(path));
        Assert.Null(OrbweaverSettings.OrbMemberOffsetFor("new"));
    }

    [Fact]
    public void UnknownKeysSurviveASaveAndTheNewKeyIsWrittenOnce()
    {
        var dir = Stage("""{ "someFutureSetting": [1, 2] }""");

        OrbweaverSettings.SetOrbMemberOffset("member", (5.0, 6.0));
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
        OrbweaverSettings.SetOrbMemberOffset("member", (5.0, 6.0));

        var root = OnDisk(dir);
        root["arrangeShape"] = "star";
        File.WriteAllText(Path.Combine(dir, "settings.json"), root.ToJsonString());
        Reload(dir);

        Assert.Equal(((double, double)?)(5.0, 6.0), OrbweaverSettings.OrbMemberOffsetFor("member"));
    }

    [Theory]
    [InlineData("\"oops\"")]
    [InlineData("[1, 2]")]
    [InlineData("{ \"a\": 5, \"b\": { \"dx\": \"left\", \"dy\": 2 }, \"c\": { \"dx\": 1 } }")]
    public void AMalformedBlockCostsOnlyTheBadEntriesNotTheFile(string block)
    {
        Stage($$"""{ "orbMemberOffsets": {{block}}, "orbSize": 1.5 }""");

        Assert.Null(OrbweaverSettings.OrbMemberOffsetFor("a"));
        Assert.Null(OrbweaverSettings.OrbMemberOffsetFor("b"));
        Assert.Null(OrbweaverSettings.OrbMemberOffsetFor("c"));
        Assert.Equal(1.5, OrbweaverSettings.OrbSize, 3);
    }

    [Fact]
    public void FractionalOffsetsRoundTripExactly()
    {
        var dir = Stage();

        OrbweaverSettings.SetOrbMemberOffset("m", (10.625, -3.375));
        Reload(dir);

        Assert.Equal(((double, double)?)(10.625, -3.375), OrbweaverSettings.OrbMemberOffsetFor("m"));
    }

    [Theory]
    [InlineData(double.NaN, 1.0)]
    [InlineData(1.0, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, 0.0)]
    [InlineData(1e20, 0.0)]
    [InlineData(0.0, -10001.0)]
    [InlineData(-1e20, 0.0)]
    public void ANonFiniteOrAbsurdOffsetIsIgnoredAndNeverReachesTheFile(double dx, double dy)
    {
        var dir = Stage();
        OrbweaverSettings.SetOrbMemberOffset("m", (2.0, 3.0));

        OrbweaverSettings.SetOrbMemberOffset("m", (dx, dy));

        Assert.Equal(((double, double)?)(2.0, 3.0), OrbweaverSettings.OrbMemberOffsetFor("m"));
        Assert.Equal(2.0, OnDisk(dir)["orbMemberOffsets"]!["m"]!["dx"]!.GetValue<double>());
    }

    [Fact]
    public void NonFiniteAndAbsurdOffsetsInTheFileAreDroppedOnLoad()
    {
        // JSON has no NaN, but a huge exponent parses to infinity in a double.
        Stage("""{ "orbMemberOffsets": { "m": { "dx": 1e999, "dy": 1 }, "big": { "dx": 1e20, "dy": -1e20 }, "edge": { "dx": 10000, "dy": -10000 }, "ok": { "dx": 1.5, "dy": 2 } } }""");

        Assert.Null(OrbweaverSettings.OrbMemberOffsetFor("m"));
        Assert.Null(OrbweaverSettings.OrbMemberOffsetFor("big"));
        Assert.Equal(((double, double)?)(10000.0, -10000.0), OrbweaverSettings.OrbMemberOffsetFor("edge"));
        Assert.Equal(((double, double)?)(1.5, 2.0), OrbweaverSettings.OrbMemberOffsetFor("ok"));
    }

    [Fact]
    public void AnOffsetOfExactlyTheBoundIsKeptOnSetAndSurvivesAReload()
    {
        var dir = Stage();

        OrbweaverSettings.SetOrbMemberOffset("m", (10000.0, -10000.0));
        Reload(dir);

        Assert.Equal(((double, double)?)(10000.0, -10000.0), OrbweaverSettings.OrbMemberOffsetFor("m"));
    }
}
