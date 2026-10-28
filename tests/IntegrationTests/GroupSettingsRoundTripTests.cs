using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-268 S1 through a real settings file.
//
// Covered here as well as in OrbGroupsTests because the two fail differently:
// the table can be right about what a group is while a key never reaches disk,
// is read under another name, is written twice (KnownKeys and _unknownKeys), or
// an upgrade quietly changes where the shapes people already chose live.
[Collection("Settings")]
public class GroupSettingsRoundTripTests
{
    private static string Stage(string? existing = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb268-groups-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        if (existing is not null) File.WriteAllText(Path.Combine(dir, "settings.json"), existing);

        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
        return dir;
    }

    private static void Reload(string dir)
    {
        OrbweaverSettings.FlushPendingSave();
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    private static JsonObject OnDisk(string dir) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;

    // ---- defaults and migration ----------------------------------------------

    [Fact]
    public void WithNoFileThereAreThreePredefinedGroupsAndOutlinesAreOff()
    {
        Stage();

        var groups = OrbweaverSettings.OrbGroupList();

        Assert.Equal(new[] { "main", "heartbeats", "crons" }, groups.Select(g => g.Id));
        Assert.Equal(OrbweaverSettings.DefaultArrangeShape, groups[0].Shape);
        Assert.Equal(OrbweaverSettings.DefaultOpenClawHeartbeatShape, groups[1].Shape);
        Assert.Equal(OrbweaverSettings.DefaultOpenClawCronShape, groups[2].Shape);
        Assert.False(OrbweaverSettings.GroupOutlinesEnabled);
        Assert.Equal("#FFFFFF", OrbweaverSettings.GroupOutlineStroke);
        Assert.Equal(0.35, OrbweaverSettings.GroupOutlineStrokeOpacity, 3);
        Assert.Equal("#FFFFFF", OrbweaverSettings.GroupOutlineFill);
        Assert.Equal(0.06, OrbweaverSettings.GroupOutlineFillOpacity, 3);
    }

    [Fact]
    public void AFileFromBeforeGroupsMigratesItsFourLegacyKeysIntoThePredefinedGroups()
    {
        Stage("""
            { "arrangeShape": "star", "arrangeAnchor": { "x": 640, "y": 360 },
              "openclawHeartbeatShape": "grid", "openclawCronShape": "vline",
              "orbSize": 1.5 }
            """);

        Assert.Equal("star", OrbweaverSettings.ArrangeShape);
        Assert.Equal(new OrbweaverSettings.OrbPlacement(640, 360), OrbweaverSettings.ArrangeAnchor);
        Assert.Equal("grid", OrbweaverSettings.OpenClawHeartbeatShape);
        Assert.Equal("vline", OrbweaverSettings.OpenClawCronShape);

        Assert.Equal("star", OrbweaverSettings.OrbGroupFor("main")!.Shape);
        Assert.Equal(new OrbweaverSettings.OrbPlacement(640, 360), OrbweaverSettings.OrbGroupFor("main")!.Anchor);
        Assert.Equal("grid", OrbweaverSettings.OrbGroupFor("heartbeats")!.Shape);
        Assert.Equal("vline", OrbweaverSettings.OrbGroupFor("crons")!.Shape);
        Assert.Equal(1.5, OrbweaverSettings.OrbSize, 3);
    }

    [Fact]
    public void TheFirstSaveAfterMigrationWritesOrbGroupsAndKeepsTheLegacyKeys()
    {
        var dir = Stage("""
            { "arrangeShape": "star", "arrangeAnchor": { "x": 640, "y": 360 },
              "openclawHeartbeatShape": "grid", "openclawCronShape": "vline" }
            """);

        OrbweaverSettings.GroupOutlinesEnabled = true;
        var root = OnDisk(dir);

        // The old keys an older build reads, still there and still true.
        Assert.Equal("star", root["arrangeShape"]!.GetValue<string>());
        Assert.Equal(640, root["arrangeAnchor"]!["x"]!.GetValue<int>());
        Assert.Equal("grid", root["openclawHeartbeatShape"]!.GetValue<string>());
        Assert.Equal("vline", root["openclawCronShape"]!.GetValue<string>());

        // And the new block carrying the same facts.
        var groups = root["orbGroups"]!["groups"]!;
        Assert.Equal("star", groups["main"]!["shape"]!.GetValue<string>());
        Assert.Equal(360, groups["main"]!["anchor"]!["y"]!.GetValue<int>());
        Assert.Equal("grid", groups["heartbeats"]!["shape"]!.GetValue<string>());
        Assert.Equal("vline", groups["crons"]!["shape"]!.GetValue<string>());
        Assert.True(root["groupOutlinesEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public void TheLegacyPropertiesAreViewsOverTheGroupsInBothDirections()
    {
        var dir = Stage();

        OrbweaverSettings.ArrangeShape = "diamond";
        Assert.Equal("diamond", OrbweaverSettings.OrbGroupFor("main")!.Shape);

        OrbweaverSettings.SetOrbGroupShape("main", "circle");
        Assert.Equal("circle", OrbweaverSettings.ArrangeShape);
        Assert.Equal("circle", OnDisk(dir)["arrangeShape"]!.GetValue<string>());

        OrbweaverSettings.OpenClawHeartbeatShape = "star";
        OrbweaverSettings.OpenClawCronShape = "grid";
        Assert.Equal("star", OrbweaverSettings.OrbGroupFor("heartbeats")!.Shape);
        Assert.Equal("grid", OrbweaverSettings.OrbGroupFor("crons")!.Shape);

        OrbweaverSettings.ArrangeAnchor = new OrbweaverSettings.OrbPlacement(10, 20);
        Assert.Equal(new OrbweaverSettings.OrbPlacement(10, 20), OrbweaverSettings.OrbGroupFor("main")!.Anchor);

        OrbweaverSettings.SetOrbGroupAnchor("main", new OrbweaverSettings.OrbPlacement(30, 40));
        Assert.Equal(new OrbweaverSettings.OrbPlacement(30, 40), OrbweaverSettings.ArrangeAnchor);
        Assert.Equal(30, OnDisk(dir)["arrangeAnchor"]!["x"]!.GetValue<int>());
    }

    [Fact]
    public void AnOlderBuildsEditToALegacyKeyWinsOverTheStaleCopyInOrbGroups()
    {
        var dir = Stage();
        OrbweaverSettings.ArrangeShape = "star";
        OrbweaverSettings.ArrangeAnchor = new OrbweaverSettings.OrbPlacement(100, 200);

        // An older build knows only the legacy keys, so it changes those and
        // writes everything it does not understand — orbGroups — back untouched.
        var root = OnDisk(dir);
        root["arrangeShape"] = "circle";
        root["arrangeAnchor"] = new JsonObject { ["x"] = 7, ["y"] = 8 };
        File.WriteAllText(Path.Combine(dir, "settings.json"), root.ToJsonString());

        Reload(dir);

        Assert.Equal("circle", OrbweaverSettings.ArrangeShape);
        Assert.Equal(new OrbweaverSettings.OrbPlacement(7, 8), OrbweaverSettings.ArrangeAnchor);
        Assert.Equal("circle", OrbweaverSettings.OrbGroupFor("main")!.Shape);
    }

    // ---- round trip -----------------------------------------------------------

    [Fact]
    public void GroupsStylesAnchorsAndMembershipSurviveBeingReloadedFromDisk()
    {
        var dir = Stage();

        var group = OrbweaverSettings.CreateOrbGroup("circle");
        OrbweaverSettings.RenameOrbGroup(group.Id, "Evidence");
        OrbweaverSettings.SetOrbGroupAnchor(group.Id, new OrbweaverSettings.OrbPlacement(800, 450));
        OrbweaverSettings.SetOrbGroupStyle(group.Id, new GroupStyle("#112233", 0.7, null, 0.2));
        OrbweaverSettings.SetOrbGroupAnchor("crons", new OrbweaverSettings.OrbPlacement(5, 6));
        OrbweaverSettings.AssignTeamToGroup("lead-key", new string?[] { "member-key" }, group.Id);
        var before = OrbweaverSettings.OrbGroupList();

        Reload(dir);

        Assert.Equal(before, OrbweaverSettings.OrbGroupList());
        Assert.Equal(group.Id, OrbweaverSettings.OrbGroupMembershipFor("lead-key"));
        Assert.Equal(group.Id, OrbweaverSettings.OrbGroupMembershipFor("MEMBER-KEY"));
        Assert.Equal("Evidence", OrbweaverSettings.OrbGroupFor(group.Id)!.Name);
        Assert.Equal(new GroupStyle("#112233", 0.7, null, 0.2), OrbweaverSettings.OrbGroupFor(group.Id)!.Style);
    }

    [Fact]
    public void MembershipChangesAreWrittenImmediately()
    {
        var dir = Stage();
        var group = OrbweaverSettings.CreateOrbGroup();

        OrbweaverSettings.MoveOrbToGroup("a", group.Id);

        Assert.Equal(group.Id, OnDisk(dir)["orbGroups"]!["members"]!["a"]!.GetValue<string>());
    }

    [Fact]
    public void TheDirectoryFallbackFindsAMembershipSavedUnderTheBareDirectory()
    {
        Stage();
        var group = OrbweaverSettings.CreateOrbGroup();
        OrbweaverSettings.MoveOrbToGroup("/work/repo", group.Id);

        Assert.Equal(group.Id, OrbweaverSettings.OrbGroupMembershipFor("/work/repo\nsome title", "/work/repo"));
        Assert.Null(OrbweaverSettings.OrbGroupMembershipFor("/work/repo\nsome title", null));
    }

    [Fact]
    public void ALastMemberLeavingRemovesTheGroupFromTheFileToo()
    {
        var dir = Stage();
        var group = OrbweaverSettings.CreateOrbGroup();
        OrbweaverSettings.MoveOrbToGroup("a", group.Id);

        OrbweaverSettings.MoveOrbToGroup("a", OrbGroups.Main);

        Assert.Null(OrbweaverSettings.OrbGroupFor(group.Id));
        Assert.Null(OnDisk(dir)["orbGroups"]!["groups"]![group.Id]);
    }

    [Fact]
    public void UnassignDeleteAndCarryGoThroughTheFile()
    {
        var dir = Stage();
        var one = OrbweaverSettings.CreateOrbGroup();
        var two = OrbweaverSettings.CreateOrbGroup();
        OrbweaverSettings.MoveOrbToGroup("old", one.Id);
        OrbweaverSettings.MoveOrbToGroup("x", two.Id);

        OrbweaverSettings.CarryOrbGroupMembership("old", "new");
        OrbweaverSettings.UnassignOrbGroup("x");
        OrbweaverSettings.DeleteOrbGroup(OrbGroups.Main);

        var root = OnDisk(dir)["orbGroups"]!;
        Assert.Equal(one.Id, root["members"]!["new"]!.GetValue<string>());
        Assert.Equal(one.Id, root["members"]!["old"]!.GetValue<string>());
        Assert.Null(root["members"]!["x"]);
        Assert.Null(root["groups"]![two.Id]);
        Assert.NotNull(root["groups"]!["main"]);

        OrbweaverSettings.DeleteOrbGroup(one.Id);
        root = OnDisk(dir)["orbGroups"]!;
        Assert.Null(root["groups"]![one.Id]);
        Assert.Empty(root["members"]!.AsObject());
    }

    [Fact]
    public void ANoOpGroupChangeDoesNotRewriteTheFile()
    {
        var dir = Stage();
        OrbweaverSettings.ArrangeShape = "star";
        var path = Path.Combine(dir, "settings.json");
        File.Delete(path);

        OrbweaverSettings.SetOrbGroupShape("main", "star");
        OrbweaverSettings.RenameOrbGroup("g-nope", "x");
        OrbweaverSettings.MoveOrbToGroup("", OrbGroups.Main);

        Assert.False(File.Exists(path));
    }

    // ---- the file survives what it should -------------------------------------

    [Fact]
    public void UnknownKeysFromANewerVersionSurviveASaveAndTheNewKeysAreNotDuplicated()
    {
        var dir = Stage("""{ "someFutureSetting": { "a": [1, 2, 3] } }""");

        OrbweaverSettings.GroupOutlinesEnabled = true;
        OrbweaverSettings.SetOrbGroupShape("main", "star");
        var text = File.ReadAllText(Path.Combine(dir, "settings.json"));
        var root = OnDisk(dir);

        Assert.Equal(3, root["someFutureSetting"]!["a"]!.AsArray().Count);
        foreach (var key in new[]
                 {
                     "orbGroups", "groupOutlinesEnabled", "groupOutlineStroke",
                     "groupOutlineStrokeOpacity", "groupOutlineFill", "groupOutlineFillOpacity"
                 })
        {
            Assert.NotNull(root[key]);
            Assert.Equal(1, text.Split($"\"{key}\":").Length - 1);
        }
    }

    [Theory]
    [InlineData("\"oops\"")]
    [InlineData("[1, 2]")]
    [InlineData("{ \"groups\": \"nope\", \"members\": [1] }")]
    [InlineData("{ \"groups\": { \"g-aaaaaaaa\": 5 }, \"members\": { \"k\": 9 } }")]
    public void AMalformedOrbGroupsBlockCostsOnlyTheBlockNotTheRestOfTheFile(string block)
    {
        Stage($$"""
            { "orbGroups": {{block}}, "arrangeShape": "star", "orbSize": 1.5,
              "orbPositions": { "/some/where": { "x": 11, "y": 22 } } }
            """);

        Assert.Equal(3, OrbweaverSettings.OrbGroupList().Count);
        Assert.Equal("star", OrbweaverSettings.ArrangeShape);
        Assert.Equal(1.5, OrbweaverSettings.OrbSize, 3);
        Assert.Equal(new OrbweaverSettings.OrbPlacement(11, 22), OrbweaverSettings.OrbPositionFor("/some/where"));
    }

    [Fact]
    public void AMalformedBlockIsReplacedByAValidOneOnTheNextSave()
    {
        var dir = Stage("""{ "orbGroups": "oops" }""");

        OrbweaverSettings.GroupOutlinesEnabled = true;

        Assert.IsType<JsonObject>(OnDisk(dir)["orbGroups"]);
    }

    // ---- outline style --------------------------------------------------------

    [Fact]
    public void OutlineStyleRoundTripsThroughTheFile()
    {
        var dir = Stage();

        OrbweaverSettings.GroupOutlinesEnabled = true;
        OrbweaverSettings.GroupOutlineStroke = "#ff8800";
        OrbweaverSettings.GroupOutlineStrokeOpacity = 0.8;
        OrbweaverSettings.GroupOutlineFill = "#00FF00";
        OrbweaverSettings.GroupOutlineFillOpacity = 0.5;
        Reload(dir);

        Assert.True(OrbweaverSettings.GroupOutlinesEnabled);
        Assert.Equal("#FF8800", OrbweaverSettings.GroupOutlineStroke);
        Assert.Equal(0.8, OrbweaverSettings.GroupOutlineStrokeOpacity, 3);
        Assert.Equal("#00FF00", OrbweaverSettings.GroupOutlineFill);
        Assert.Equal(0.5, OrbweaverSettings.GroupOutlineFillOpacity, 3);
        Assert.Equal(
            new GroupStyle("#FF8800", 0.8, "#00FF00", 0.5), OrbweaverSettings.GroupOutlineGlobals());
    }

    [Fact]
    public void TheColourAndOpacitySettersDeferTheirWriteLikeTheOrbColours()
    {
        var dir = Stage();
        var path = Path.Combine(dir, "settings.json");

        OrbweaverSettings.GroupOutlineStroke = "#123456";
        OrbweaverSettings.GroupOutlineStrokeOpacity = 0.4;
        OrbweaverSettings.GroupOutlineFill = "#654321";
        OrbweaverSettings.GroupOutlineFillOpacity = 0.3;

        Assert.False(File.Exists(path));

        OrbweaverSettings.FlushPendingSave();

        var root = OnDisk(dir);
        Assert.Equal("#123456", root["groupOutlineStroke"]!.GetValue<string>());
        Assert.Equal(0.4, root["groupOutlineStrokeOpacity"]!.GetValue<double>(), 3);
        Assert.Equal("#654321", root["groupOutlineFill"]!.GetValue<string>());
        Assert.Equal(0.3, root["groupOutlineFillOpacity"]!.GetValue<double>(), 3);
    }

    [Theory]
    [InlineData(5.0, 1.0)]
    [InlineData(-5.0, 0.0)]
    public void OpacityIsClampedOnTheWayIn(double given, double stored)
    {
        Stage();

        OrbweaverSettings.GroupOutlineStrokeOpacity = given;
        OrbweaverSettings.GroupOutlineFillOpacity = given;

        Assert.Equal(stored, OrbweaverSettings.GroupOutlineStrokeOpacity, 3);
        Assert.Equal(stored, OrbweaverSettings.GroupOutlineFillOpacity, 3);
    }

    [Fact]
    public void ANanOpacityOrAnUnparseableColourIsIgnored()
    {
        Stage();
        OrbweaverSettings.GroupOutlineStroke = "#AABBCC";
        OrbweaverSettings.GroupOutlineFill = "#DDEEFF";
        OrbweaverSettings.GroupOutlineStrokeOpacity = 0.9;
        OrbweaverSettings.GroupOutlineFillOpacity = 0.8;

        OrbweaverSettings.GroupOutlineStroke = "red";
        OrbweaverSettings.GroupOutlineFill = "";
        OrbweaverSettings.GroupOutlineStrokeOpacity = double.NaN;
        OrbweaverSettings.GroupOutlineFillOpacity = double.NaN;

        Assert.Equal("#AABBCC", OrbweaverSettings.GroupOutlineStroke);
        Assert.Equal("#DDEEFF", OrbweaverSettings.GroupOutlineFill);
        Assert.Equal(0.9, OrbweaverSettings.GroupOutlineStrokeOpacity, 3);
        Assert.Equal(0.8, OrbweaverSettings.GroupOutlineFillOpacity, 3);
    }

    [Fact]
    public void HandEditedOutlineValuesFallBackOneByOneWithoutResettingTheFile()
    {
        Stage("""
            { "groupOutlinesEnabled": "yes", "groupOutlineStroke": "blue",
              "groupOutlineStrokeOpacity": 9, "groupOutlineFill": "#abc",
              "groupOutlineFillOpacity": "lots", "orbSize": 1.5 }
            """);

        Assert.False(OrbweaverSettings.GroupOutlinesEnabled);
        Assert.Equal("#FFFFFF", OrbweaverSettings.GroupOutlineStroke);
        Assert.Equal(1.0, OrbweaverSettings.GroupOutlineStrokeOpacity, 3);
        Assert.Equal("#AABBCC", OrbweaverSettings.GroupOutlineFill);
        Assert.Equal(0.06, OrbweaverSettings.GroupOutlineFillOpacity, 3);
        Assert.Equal(1.5, OrbweaverSettings.OrbSize, 3);
    }
}
