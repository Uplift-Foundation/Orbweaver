using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-268 S1: the pure half of the group model. No settings file, no window —
// everything here is a decision about which group an orb is in, and the mistake
// it can make is an orb in the wrong shape or a team split across two.
public class OrbGroupsTests
{
    private static Func<string> Ids(params string[] ids)
    {
        var queue = new Queue<string>(ids);
        return queue.Dequeue;
    }

    // ---- precedence -----------------------------------------------------------

    [Fact]
    public void AnExplicitMembershipWinsOverTheClusterClassification()
    {
        // A heartbeat orb the user dragged into main is in main, even though
        // OrbClusters would put it in the heartbeats slot.
        var id = OrbGroups.GroupIdFor(
            OrbGroups.Main, OrbCluster.Heartbeats, ClusterMode.OwnShape, ClusterMode.OwnShape,
            ClusterMode.WithChats);

        Assert.Equal(OrbGroups.Main, id);
    }

    [Fact]
    public void AUserGroupMembershipIsReturnedAsIs()
    {
        Assert.Equal(
            "g-0a1b2c3d",
            OrbGroups.GroupIdFor("g-0a1b2c3d", OrbCluster.Chats, ClusterMode.WithChats, ClusterMode.WithChats, ClusterMode.WithChats));
    }

    [Theory]
    [InlineData(OrbCluster.Heartbeats, ClusterMode.OwnShape, ClusterMode.OwnShape, OrbGroups.Heartbeats)]
    [InlineData(OrbCluster.Crons, ClusterMode.OwnShape, ClusterMode.OwnShape, OrbGroups.Crons)]
    [InlineData(OrbCluster.Heartbeats, ClusterMode.WithChats, ClusterMode.OwnShape, OrbGroups.Main)]
    [InlineData(OrbCluster.Crons, ClusterMode.OwnShape, ClusterMode.WithChats, OrbGroups.Main)]
    [InlineData(OrbCluster.Chats, ClusterMode.OwnShape, ClusterMode.OwnShape, OrbGroups.Main)]
    public void WithoutAMembershipTheClusterClassificationDecides(
        OrbCluster cluster, ClusterMode heartbeats, ClusterMode crons, string expected)
    {
        Assert.Equal(expected, OrbGroups.GroupIdFor(null, cluster, heartbeats, crons, ClusterMode.WithChats));
    }

    // CB-270: native is the fourth predefined group, slot 3, and only Own shape
    // moves a native orb there. With WithChats it is main's; with an explicit
    // membership the membership wins as it does for every other cluster.
    [Fact]
    public void ANativeOrbIsInTheNativeGroupOnlyUnderOwnShape()
    {
        Assert.Equal(OrbGroups.Native, OrbGroups.GroupIdFor(
            null, OrbCluster.Native, ClusterMode.WithChats, ClusterMode.WithChats, ClusterMode.OwnShape));
        Assert.Equal(OrbGroups.Main, OrbGroups.GroupIdFor(
            null, OrbCluster.Native, ClusterMode.OwnShape, ClusterMode.OwnShape, ClusterMode.WithChats));
        Assert.Equal(OrbGroups.Main, OrbGroups.GroupIdFor(
            OrbGroups.Main, OrbCluster.Native, ClusterMode.WithChats, ClusterMode.WithChats, ClusterMode.OwnShape));
    }

    [Fact]
    public void NativeIsPredefinedUndeletableAndDefaultsToADiamond()
    {
        Assert.True(OrbGroups.IsPredefined("native"));
        Assert.Contains("native", OrbGroups.PredefinedIds);

        var table = new OrbGroupTable();
        Assert.False(table.Delete(OrbGroups.Native));

        var native = OrbGroups.DefaultFor(OrbGroups.Native);
        Assert.Equal("diamond", native.Shape);
        Assert.Equal(OpenClawNativeText.GroupName, native.Name);
        Assert.Equal(OrbweaverSettings.DefaultOpenClawNativeShape, native.Shape);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ABlankMembershipIsNoMembership(string blank)
    {
        Assert.Equal(
            OrbGroups.Crons,
            OrbGroups.GroupIdFor(blank, OrbCluster.Crons, ClusterMode.OwnShape, ClusterMode.OwnShape, ClusterMode.WithChats));
    }

    // ---- slot order and ids ---------------------------------------------------

    [Fact]
    public void SlotOrderIsMainHeartbeatsCronsThenUserGroupsByOrdinalId()
    {
        var table = new OrbGroupTable();
        table.Create(idSource: Ids("g-ffffffff"));
        table.Create(idSource: Ids("g-00000001"));
        table.Create(idSource: Ids("g-0000000a"));

        var ids = table.Ordered().Select(group => group.Id).ToArray();

        Assert.Equal(
            new[] { "main", "heartbeats", "crons", "native", "g-00000001", "g-0000000a", "g-ffffffff" }, ids);
    }

    [Fact]
    public void APredefinedGroupMissingFromTheDictionaryIsSynthesisedInItsSlot()
    {
        var onlyUser = new Dictionary<string, OrbGroup>
        {
            ["g-12345678"] = new("g-12345678", "Group 1", "circle", null, null)
        };

        var ordered = OrbGroups.Ordered(onlyUser);

        Assert.Equal(new[] { "main", "heartbeats", "crons", "native", "g-12345678" }, ordered.Select(g => g.Id));
        Assert.Equal(OrbweaverSettings.DefaultArrangeShape, ordered[0].Shape);
        Assert.Equal(OrbweaverSettings.DefaultOpenClawHeartbeatShape, ordered[1].Shape);
        Assert.Equal(OrbweaverSettings.DefaultOpenClawCronShape, ordered[2].Shape);
        Assert.Equal(OrbweaverSettings.DefaultOpenClawNativeShape, ordered[3].Shape);
        Assert.Equal(
            new[] { "Main", "Heartbeats", "Crons", OpenClawNativeText.GroupName },
            ordered.Take(4).Select(g => g.Name));
    }

    [Fact]
    public void NewUserGroupIdsAreGDashEightLowercaseHexAndDistinct()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 50; i++)
        {
            var id = OrbGroups.NewUserGroupId();
            Assert.Matches("^g-[0-9a-f]{8}$", id);
            seen.Add(id);
        }

        Assert.Equal(50, seen.Count);
    }

    [Fact]
    public void ANewTableHoldsExactlyTheFourPredefinedGroups()
    {
        var table = new OrbGroupTable();

        Assert.Equal(
            new[] { "main", "heartbeats", "crons", "native" },
            table.Groups.Keys.OrderBy(k => Array.IndexOf(OrbGroups.PredefinedIds, k)));
        Assert.True(OrbGroups.IsPredefined("crons"));
        Assert.False(OrbGroups.IsPredefined("g-00000000"));
        Assert.False(OrbGroups.IsPredefined(null));
    }

    // ---- creating, naming, deleting -------------------------------------------

    [Fact]
    public void UserGroupsAreNamedGroupNWithTheLowestFreeN()
    {
        var table = new OrbGroupTable();

        var first = table.Create(idSource: Ids("g-00000001"));
        var second = table.Create(idSource: Ids("g-00000002"));
        Assert.Equal("Group 1", first.Name);
        Assert.Equal("Group 2", second.Name);

        table.Delete(first.Id);
        var third = table.Create(idSource: Ids("g-00000003"));

        Assert.Equal("Group 1", third.Name);
    }

    [Fact]
    public void ARenamedGroupDoesNotBlockItsOldNameBeingReused()
    {
        var table = new OrbGroupTable();
        var first = table.Create(idSource: Ids("g-00000001"));
        table.Rename(first.Id, "Evidence");

        Assert.Equal("Group 1", table.Create(idSource: Ids("g-00000002")).Name);
    }

    [Fact]
    public void ANewGroupTakesTheRequestedShapeOrMainsWhenNoneIsGiven()
    {
        var table = new OrbGroupTable();
        table.SetShape(OrbGroups.Main, "star");

        Assert.Equal("circle", table.Create("circle", Ids("g-00000001")).Shape);
        Assert.Equal("star", table.Create(null, Ids("g-00000002")).Shape);
        Assert.Equal("star", table.Create("  ", Ids("g-00000003")).Shape);
    }

    [Fact]
    public void ACollidingIdIsDrawnAgain()
    {
        var table = new OrbGroupTable();
        table.Create(idSource: Ids("g-00000001"));

        var second = table.Create(idSource: Ids("g-00000001", "main", "g-00000002"));

        Assert.Equal("g-00000002", second.Id);
    }

    [Fact]
    public void ACreatedGroupIsKeptEmptyUntilSomeoneLeavesIt()
    {
        var table = new OrbGroupTable();
        var group = table.Create(idSource: Ids("g-00000001"));

        Assert.NotNull(table.Get(group.Id));
        Assert.Equal(0, table.MemberCount(group.Id));
    }

    [Fact]
    public void OnlyUserGroupsCanBeDeletedAndTheirMembersFallBack()
    {
        var table = new OrbGroupTable();
        var group = table.Create(idSource: Ids("g-00000001"));
        table.Move("a", group.Id);
        table.Move("b", group.Id);

        Assert.False(table.Delete(OrbGroups.Main));
        Assert.False(table.Delete(OrbGroups.Heartbeats));
        Assert.False(table.Delete("g-nope"));
        Assert.True(table.Delete(group.Id));

        Assert.Null(table.Get(group.Id));
        Assert.Null(table.MembershipFor("a"));
        Assert.Null(table.MembershipFor("b"));
        Assert.Empty(table.Members);
    }

    [Fact]
    public void RenameTrimsAndRefusesBlanksAndUnknownIdsAndNoOps()
    {
        var table = new OrbGroupTable();
        var group = table.Create(idSource: Ids("g-00000001"));

        Assert.True(table.Rename(group.Id, "  Evidence  "));
        Assert.Equal("Evidence", table.Get(group.Id)!.Name);
        Assert.False(table.Rename(group.Id, "Evidence"));
        Assert.False(table.Rename(group.Id, " "));
        Assert.False(table.Rename(group.Id, null));
        Assert.False(table.Rename("g-nope", "x"));
    }

    [Fact]
    public void ShapeAnchorAndStyleChangesReportWhetherAnythingChanged()
    {
        var table = new OrbGroupTable();

        Assert.True(table.SetShape(OrbGroups.Main, "star"));
        Assert.False(table.SetShape(OrbGroups.Main, "star"));
        Assert.False(table.SetShape(OrbGroups.Main, ""));
        Assert.False(table.SetShape("g-nope", "star"));

        var anchor = new OrbweaverSettings.OrbPlacement(10, 20);
        Assert.True(table.SetAnchor(OrbGroups.Crons, anchor));
        Assert.False(table.SetAnchor(OrbGroups.Crons, new OrbweaverSettings.OrbPlacement(10, 20)));
        Assert.Equal(anchor, table.Get(OrbGroups.Crons)!.Anchor);
        Assert.True(table.SetAnchor(OrbGroups.Crons, null));

        Assert.True(table.SetStyle(OrbGroups.Main, new GroupStyle("#abc", 2.0, null, -1.0)));
        var style = table.Get(OrbGroups.Main)!.Style!;
        Assert.Equal("#AABBCC", style.Stroke);
        Assert.Equal(1.0, style.StrokeOpacity);
        Assert.Null(style.Fill);
        Assert.Equal(0.0, style.FillOpacity);
        Assert.True(table.SetStyle(OrbGroups.Main, new GroupStyle(null, null, "nonsense", double.NaN)));
        Assert.Null(table.Get(OrbGroups.Main)!.Style);
        Assert.False(table.SetStyle(OrbGroups.Main, null));
    }

    // ---- value hygiene --------------------------------------------------------

    [Theory]
    [InlineData("#ffffff", "#FFFFFF")]
    [InlineData("  #1a2b3c ", "#1A2B3C")]
    [InlineData("#abc", "#AABBCC")]
    [InlineData("#80FF0000", "#FF0000")]
    [InlineData("FFFFFF", null)]
    [InlineData("#12345", null)]
    [InlineData("#GGGGGG", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ColoursAreStoredAsUpperCaseRrggbbOrRejected(string? input, string? expected)
    {
        Assert.Equal(expected, OrbGroups.NormalizeColor(input));
    }

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(-3.0, 0.0)]
    [InlineData(7.0, 1.0)]
    [InlineData(double.NaN, null)]
    [InlineData(null, null)]
    public void OpacityIsClampedToZeroToOneAndNaNIsUnset(double? input, double? expected)
    {
        Assert.Equal(expected, OrbGroups.ClampOpacity(input));
    }

    // ---- membership -----------------------------------------------------------

    [Fact]
    public void MembershipLookupIsCaseInsensitiveOnTheKey()
    {
        var table = new OrbGroupTable();
        var group = table.Create(idSource: Ids("g-00000001"));
        table.Move(@"C:\Work\Repo", group.Id);

        Assert.Equal(group.Id, table.MembershipFor(@"c:\work\repo"));
    }

    [Fact]
    public void TheDirectoryFallbackIsTriedOnlyWhenTheFullKeyHasNoMembership()
    {
        var table = new OrbGroupTable();
        var one = table.Create(idSource: Ids("g-00000001"));
        var two = table.Create(idSource: Ids("g-00000002"));
        table.Move("/work/repo", one.Id);

        Assert.Equal(one.Id, table.MembershipFor("/work/repo\ntitle", "/work/repo"));

        table.Move("/work/repo\ntitle", two.Id);
        Assert.Equal(two.Id, table.MembershipFor("/work/repo\ntitle", "/work/repo"));

        Assert.Null(table.MembershipFor("/other\nt", null));
        Assert.Null(table.MembershipFor(null, "/nowhere"));
        Assert.Null(table.MembershipFor("", ""));
    }

    [Fact]
    public void ADanglingMembershipReadsAsNoneAndLetsTheFallbackAnswer()
    {
        var table = new OrbGroupTable();
        var one = table.Create(idSource: Ids("g-00000001"));
        table.Move("dir", one.Id);

        var json = table.ToJson();
        json["members"]!.AsObject()["full"] = "g-gone";
        var reread = new OrbGroupTable();
        reread.ReadJson(json);

        // "g-gone" was dropped at read time because the group does not exist.
        Assert.Null(reread.MembershipFor("full"));
        Assert.Equal(one.Id, reread.MembershipFor("full", "dir"));
    }

    [Fact]
    public void MovingToAnUnknownGroupOrWithAnEmptyKeyDoesNothing()
    {
        var table = new OrbGroupTable();

        Assert.False(table.Move("a", "g-nope"));
        Assert.False(table.Move("", OrbGroups.Main));
        Assert.False(table.Move(null, OrbGroups.Main));
        Assert.Empty(table.Members);
    }

    [Fact]
    public void MovingToTheGroupAnOrbIsAlreadyInReportsNoChange()
    {
        var table = new OrbGroupTable();
        Assert.True(table.Move("a", OrbGroups.Main));
        Assert.False(table.Move("a", OrbGroups.Main));
    }

    [Fact]
    public void LeavingToMainIsAnExplicitMembershipThatBeatsTheClassification()
    {
        var table = new OrbGroupTable();
        table.Move("hb", OrbGroups.Main);

        var id = OrbGroups.GroupIdFor(
            table.MembershipFor("hb"), OrbCluster.Heartbeats, ClusterMode.OwnShape, ClusterMode.OwnShape,
            ClusterMode.WithChats);

        Assert.Equal(OrbGroups.Main, id);
    }

    [Fact]
    public void UnassigningHandsTheOrbBackToTheClassification()
    {
        var table = new OrbGroupTable();
        table.Move("hb", OrbGroups.Main);

        Assert.True(table.Unassign("hb"));
        Assert.False(table.Unassign("hb"));
        Assert.False(table.Unassign(""));
        Assert.False(table.Unassign(null));

        Assert.Equal(
            OrbGroups.Heartbeats,
            OrbGroups.GroupIdFor(
                table.MembershipFor("hb"), OrbCluster.Heartbeats, ClusterMode.OwnShape, ClusterMode.OwnShape,
                ClusterMode.WithChats));
    }

    // ---- last-member removal --------------------------------------------------

    [Fact]
    public void MovingTheLastMemberOutOfAUserGroupRemovesTheGroup()
    {
        var table = new OrbGroupTable();
        var group = table.Create(idSource: Ids("g-00000001"));
        table.Move("a", group.Id);
        table.Move("b", group.Id);

        table.Move("a", OrbGroups.Main);
        Assert.NotNull(table.Get(group.Id));
        Assert.Equal(1, table.MemberCount(group.Id));

        table.Move("b", OrbGroups.Main);
        Assert.Null(table.Get(group.Id));
    }

    [Fact]
    public void UnassigningTheLastMemberOfAUserGroupRemovesIt()
    {
        var table = new OrbGroupTable();
        var group = table.Create(idSource: Ids("g-00000001"));
        table.Move("a", group.Id);

        table.Unassign("a");

        Assert.Null(table.Get(group.Id));
    }

    [Fact]
    public void ThePredefinedGroupsSurviveBeingEmptied()
    {
        var table = new OrbGroupTable();
        table.Move("a", OrbGroups.Crons);
        table.Move("a", OrbGroups.Main);
        table.Unassign("a");

        Assert.NotNull(table.Get(OrbGroups.Crons));
        Assert.NotNull(table.Get(OrbGroups.Main));
        Assert.NotNull(table.Get(OrbGroups.Heartbeats));
    }

    // ---- teams ----------------------------------------------------------------

    [Fact]
    public void SnappingATeamWritesTheLeadAndEveryMember()
    {
        var table = new OrbGroupTable();
        var group = table.Create(idSource: Ids("g-00000001"));

        var changed = table.AssignTeam("lead", new string?[] { "m1", "m2", null, "" }, group.Id);

        Assert.True(changed);
        Assert.Equal(group.Id, table.MembershipFor("lead"));
        Assert.Equal(group.Id, table.MembershipFor("m1"));
        Assert.Equal(group.Id, table.MembershipFor("m2"));
        Assert.Equal(3, table.MemberCount(group.Id));
    }

    [Fact]
    public void ATeamSharingTheLeadsKeyIsHarmless()
    {
        var table = new OrbGroupTable();
        var group = table.Create(idSource: Ids("g-00000001"));

        table.AssignTeam("repo", new string?[] { "repo", "REPO" }, group.Id);

        Assert.Equal(1, table.MemberCount(group.Id));
    }

    [Fact]
    public void SnappingATeamToAnUnknownGroupWritesNothing()
    {
        var table = new OrbGroupTable();

        Assert.False(table.AssignTeam("lead", new string?[] { "m1" }, "g-nope"));
        Assert.Empty(table.Members);
    }

    [Fact]
    public void SnappingATeamOutOfAGroupRemovesItOnlyOnceEveryMemberHasLeft()
    {
        var table = new OrbGroupTable();
        var from = table.Create(idSource: Ids("g-00000001"));
        var to = table.Create(idSource: Ids("g-00000002"));
        table.AssignTeam("lead", new string?[] { "m1" }, from.Id);
        table.Move("loner", from.Id);

        table.AssignTeam("lead", new string?[] { "m1" }, to.Id);
        Assert.NotNull(table.Get(from.Id));

        table.Move("loner", to.Id);
        Assert.Null(table.Get(from.Id));
        Assert.Equal(3, table.MemberCount(to.Id));
    }

    [Fact]
    public void SnappingATeamThatIsAlreadyThereChangesNothing()
    {
        var table = new OrbGroupTable();
        var group = table.Create(idSource: Ids("g-00000001"));
        table.AssignTeam("lead", new string?[] { "m1" }, group.Id);

        Assert.False(table.AssignTeam("lead", new string?[] { "m1" }, group.Id));
    }

    // ---- carrying a membership across a key change ----------------------------

    [Fact]
    public void ACarriedMembershipIsCopiedNotMoved()
    {
        var table = new OrbGroupTable();
        var group = table.Create(idSource: Ids("g-00000001"));
        table.Move("session-id", group.Id);

        Assert.True(table.CarryMembership("session-id", "/work/repo\ntitle"));

        Assert.Equal(group.Id, table.MembershipFor("/work/repo\ntitle"));
        Assert.Equal(group.Id, table.MembershipFor("session-id"));
    }

    [Fact]
    public void ACarryNeverOverwritesAMembershipTheNewKeyAlreadyHas()
    {
        var table = new OrbGroupTable();
        var one = table.Create(idSource: Ids("g-00000001"));
        var two = table.Create(idSource: Ids("g-00000002"));
        table.Move("old", one.Id);
        table.Move("new", two.Id);

        Assert.False(table.CarryMembership("old", "new"));
        Assert.Equal(two.Id, table.MembershipFor("new"));
    }

    [Fact]
    public void ACarryOverADanglingMembershipOnTheNewKeyReplacesIt()
    {
        var table = new OrbGroupTable();
        var one = table.Create(idSource: Ids("g-00000001"));
        table.Move("old", one.Id);

        var json = table.ToJson();
        json["members"]!.AsObject()["new"] = "g-gone";
        var reread = new OrbGroupTable();
        reread.ReadJson(json);

        // The dangling entry never survived the read, so "new" is simply empty.
        Assert.True(reread.CarryMembership("old", "new"));
        Assert.Equal(one.Id, reread.MembershipFor("new"));
    }

    [Theory]
    [InlineData(null, "new")]
    [InlineData("", "new")]
    [InlineData("nobody", "new")]
    [InlineData("old", null)]
    [InlineData("old", "")]
    [InlineData("old", "OLD")]
    public void ACarryWithNothingToCarryOrNowhereToPutItDoesNothing(string? oldKey, string? newKey)
    {
        var table = new OrbGroupTable();
        var group = table.Create(idSource: Ids("g-00000001"));
        table.Move("old", group.Id);
        var before = table.Members.Count;

        Assert.False(table.CarryMembership(oldKey, newKey));
        Assert.Equal(before, table.Members.Count);
    }

    // ---- the settings.json block ----------------------------------------------

    [Fact]
    public void TheBlockRoundTripsGroupsStylesAnchorsAndMembers()
    {
        var table = new OrbGroupTable();
        var group = table.Create("circle", Ids("g-00000001"));
        table.Rename(group.Id, "Evidence");
        table.SetAnchor(group.Id, new OrbweaverSettings.OrbPlacement(300, 400));
        table.SetStyle(group.Id, new GroupStyle("#112233", 0.5, "#445566", 0.25));
        table.SetAnchor(OrbGroups.Crons, new OrbweaverSettings.OrbPlacement(1, 2));
        table.Move("key one", group.Id);

        var reread = new OrbGroupTable();
        reread.ReadJson(table.ToJson());

        Assert.Equal(table.Ordered(), reread.Ordered());
        Assert.Equal(group.Id, reread.MembershipFor("key one"));
    }

    [Fact]
    public void ALegacyShapeAndTheMainAnchorWinOverTheBlock()
    {
        // The table is built from the legacy keys first, then the block is laid
        // over it. An older build edits only the legacy keys, so those are the
        // ones that can be newer.
        var table = new OrbGroupTable();
        table.SetShape(OrbGroups.Main, "star");
        table.SetShape(OrbGroups.Heartbeats, "diamond");
        table.SetAnchor(OrbGroups.Main, new OrbweaverSettings.OrbPlacement(5, 5));

        table.ReadJson(JsonNode.Parse("""
            { "groups": {
                "main":       { "name": "Home", "shape": "circle", "anchor": { "x": 99, "y": 99 } },
                "heartbeats": { "shape": "grid", "anchor": { "x": 7, "y": 8 } },
                "g-aaaaaaaa": { "name": "Mine", "shape": "line" } } }
            """));

        var main = table.Get(OrbGroups.Main)!;
        Assert.Equal("Home", main.Name);
        Assert.Equal("star", main.Shape);
        Assert.Equal(new OrbweaverSettings.OrbPlacement(5, 5), main.Anchor);

        var heartbeats = table.Get(OrbGroups.Heartbeats)!;
        Assert.Equal("Heartbeats", heartbeats.Name);
        Assert.Equal("diamond", heartbeats.Shape);
        Assert.Equal(new OrbweaverSettings.OrbPlacement(7, 8), heartbeats.Anchor);

        Assert.Equal("line", table.Get("g-aaaaaaaa")!.Shape);
    }

    [Fact]
    public void AUserGroupWithNoShapeOrNameGetsDefaults()
    {
        var table = new OrbGroupTable();
        table.ReadJson(JsonNode.Parse("""{ "groups": { "g-bbbbbbbb": { } } }"""));

        var group = table.Get("g-bbbbbbbb")!;
        Assert.Equal("g-bbbbbbbb", group.Id);
        Assert.Equal("Group 1", group.Name);
        Assert.Equal(OrbweaverSettings.DefaultArrangeShape, group.Shape);
        Assert.Null(group.Anchor);
        Assert.Null(group.Style);
    }

    [Fact]
    public void MalformedEntriesCostOnlyThemselves()
    {
        var table = new OrbGroupTable();

        table.ReadJson(JsonNode.Parse("""
            { "groups": {
                "g-11111111": "not an object",
                "g-22222222": { "name": 7, "shape": ["x"], "anchor": { "x": "left", "y": 2 },
                                "style": { "stroke": "red", "strokeOpacity": "high" } },
                "g-33333333": { "name": "Good", "shape": "circle", "anchor": "centre", "style": "loud" },
                "": { "name": "blank id" } },
              "members": {
                "a": "g-33333333",
                "b": 12,
                "c": "g-missing",
                "": "g-33333333" } }
            """));

        Assert.Null(table.Get("g-11111111"));
        var bad = table.Get("g-22222222")!;
        Assert.StartsWith("Group ", bad.Name);
        Assert.Null(bad.Anchor);
        Assert.Null(bad.Style);
        var good = table.Get("g-33333333")!;
        Assert.Equal("Good", good.Name);
        Assert.Null(good.Anchor);
        Assert.Null(good.Style);

        Assert.Equal("g-33333333", table.MembershipFor("a"));
        Assert.Null(table.MembershipFor("b"));
        Assert.Null(table.MembershipFor("c"));
        Assert.Equal(1, table.Members.Count);
    }

    [Theory]
    [InlineData("""[1, 2]""")]
    [InlineData("\"a string\"")]
    [InlineData("""{ "groups": [], "members": 5 }""")]
    public void ABlockOfTheWrongShapeIsIgnoredWhole(string json)
    {
        var table = new OrbGroupTable();
        table.ReadJson(JsonNode.Parse(json));

        Assert.Equal(OrbGroups.PredefinedIds.Length, table.Groups.Count);
        Assert.Empty(table.Members);
    }

    [Fact]
    public void AnAbsentBlockLeavesTheTableAsTheLegacyKeysBuiltIt()
    {
        var table = new OrbGroupTable();
        table.SetShape(OrbGroups.Main, "star");

        table.ReadJson(null);

        Assert.Equal("star", table.Get(OrbGroups.Main)!.Shape);
    }

    [Fact]
    public void AnchorsAreRoundedToWholePixelsOnTheWayIn()
    {
        var table = new OrbGroupTable();
        table.ReadJson(JsonNode.Parse("""
            { "groups": { "crons": { "anchor": { "x": 10.6, "y": -3.4 } } } }
            """));

        Assert.Equal(new OrbweaverSettings.OrbPlacement(11, -3), table.Get(OrbGroups.Crons)!.Anchor);
    }

    [Fact]
    public void ToJsonOmitsWhatIsNotSetAndWritesWhatIs()
    {
        var table = new OrbGroupTable();
        table.SetStyle(OrbGroups.Main, new GroupStyle(null, null, "#000000", null));

        var json = table.ToJson();

        var main = json["groups"]!["main"]!.AsObject();
        Assert.Null(main["anchor"]);
        Assert.Equal("#000000", main["style"]!["fill"]!.GetValue<string>());
        Assert.Null(main["style"]!["stroke"]);
        Assert.Null(main["style"]!["strokeOpacity"]);
        Assert.Null(main["style"]!["fillOpacity"]);
        Assert.Empty(json["members"]!.AsObject());
    }

    [Fact]
    public void NamelessUserGroupsGetDistinctGroupNNamesNeverMain()
    {
        var table = new OrbGroupTable();
        table.ReadJson(JsonNode.Parse("""
            { "groups": { "g-0000abcd": { "shape": "star" }, "g-0000abce": { "name": "  " } } }
            """));

        var names = new[] { table.Get("g-0000abcd")!.Name, table.Get("g-0000abce")!.Name };
        Assert.DoesNotContain("Main", names);
        Assert.Equal(2, names.Distinct().Count());
        Assert.Equal("star", table.Get("g-0000abcd")!.Shape);
    }

    // ---- teams: root resolution (T1, T4) ---------------------------------------

    private static string? TeamGroup(OrbGroupTable table, int index, int[] leadOf, params string?[] keys) =>
        OrbGroups.TeamGroupOf(index, leadOf, keys, key => table.MembershipFor(key));

    [Fact]
    public void AMemberResolvesFromItsRootLeadNotFromItsOwnEntry()
    {
        var table = new OrbGroupTable();
        var a = table.Create(idSource: Ids("g-00000001"));
        var b = table.Create(idSource: Ids("g-00000002"));
        table.Move("lead", a.Id);
        table.Move("member", b.Id);   // a stale entry that disagrees with the lead

        Assert.Equal(a.Id, TeamGroup(table, 1, new[] { -1, 0 }, "lead", "member"));
        Assert.Equal(a.Id, TeamGroup(table, 0, new[] { -1, 0 }, "lead", "member"));
    }

    [Fact]
    public void NestedMembersResolveFromTheTopOfTheChain()
    {
        var table = new OrbGroupTable();
        var a = table.Create(idSource: Ids("g-00000001"));
        table.Move("root", a.Id);

        // 0 root <- 1 <- 2 <- 3
        var leadOf = new[] { -1, 0, 1, 2 };
        for (var i = 0; i < 4; i++)
            Assert.Equal(a.Id, TeamGroup(table, i, leadOf, "root", "m1", "m2", "m3"));
    }

    [Fact]
    public void AMissingLeadMakesTheNearestSurvivingAncestorTheRoot()
    {
        var table = new OrbGroupTable();
        var a = table.Create(idSource: Ids("g-00000001"));
        var b = table.Create(idSource: Ids("g-00000002"));
        table.Move("mid", a.Id);
        table.Move("leaf", b.Id);

        // 0 mid <- 1 leaf, and mid's own lead (index 9) is not on screen.
        Assert.Equal(a.Id, TeamGroup(table, 1, new[] { 9, 0 }, "mid", "leaf"));
    }

    [Fact]
    public void AMemberThatOutlivesItsLeadResolvesFromItsOwnKey()
    {
        var table = new OrbGroupTable();
        var a = table.Create(idSource: Ids("g-00000001"));
        table.AssignTeam("lead", new string?[] { "member" }, a.Id);

        // The lead's orb is gone: the member is alone, with a lead index of -1.
        Assert.Equal(a.Id, TeamGroup(table, 0, new[] { -1 }, "member"));
    }

    [Fact]
    public void ARootWithNoMembershipLeavesTheClassificationToDecide()
    {
        var table = new OrbGroupTable();
        var b = table.Create(idSource: Ids("g-00000002"));
        table.Move("member", b.Id);

        Assert.Null(TeamGroup(table, 1, new[] { -1, 0 }, "lead", "member"));
        Assert.Null(TeamGroup(table, 0, new[] { -1 }, ""));
        Assert.Null(TeamGroup(table, 5, new[] { -1 }, "only"));
    }

    [Fact]
    public void EveryOrbInACycleIsItsOwnRootAndResolvesItsOwnMembership()
    {
        var table = new OrbGroupTable();
        var a = table.Create(idSource: Ids("g-00000001"));
        var b = table.Create(idSource: Ids("g-00000002"));
        table.Move("k0", a.Id);
        table.Move("k1", b.Id);
        table.Move("k2", b.Id);

        // 0 -> 1 -> 2 -> 0, and a tail 3 -> 2 hanging off it. Classify makes
        // all four anchors, so each answers from its own key.
        var leadOf = new[] { 1, 2, 0, 2 };
        var keys = new string?[] { "k0", "k1", "k2", "k3" };
        Assert.Equal(a.Id, TeamGroup(table, 0, leadOf, keys));
        Assert.Equal(b.Id, TeamGroup(table, 1, leadOf, keys));
        Assert.Equal(b.Id, TeamGroup(table, 2, leadOf, keys));
        Assert.Null(TeamGroup(table, 3, leadOf, keys));
    }

    [Fact]
    public void RootOfAgreesWithTheAnchorsTheArrangementActuallyUses()
    {
        // Run the rule, not a paraphrase: Classify is what the arrangement
        // draws with, over every lead shape the sweep builds.
        var checkedOrbs = 0;
        foreach (var team in ArrangementSweep.TeamShapes)
        {
            foreach (var count in ArrangementSweep.Counts)
            {
                var leadOf = team.Build(count);
                var anchors = new List<int>();
                var members = new Dictionary<int, List<int>>();
                OrbArrangement.Classify(count, leadOf, anchors, members);

                for (var i = 0; i < count; i++)
                {
                    var root = OrbGroups.RootOf(i, leadOf, count);
                    Assert.True(anchors.Contains(root), $"{team.Name} n={count} orb {i}: root {root} is not an anchor");

                    if (anchors.Contains(i)) Assert.Equal(i, root);
                    else
                    {
                        // A member is reached from its root through its leads.
                        var at = i;
                        while (at != root) at = leadOf[at];
                    }

                    checkedOrbs++;
                }
            }
        }

        Assert.True(checkedOrbs > 200);
    }

    [Fact]
    public void ASelfLeadIsItsOwnRoot()
    {
        Assert.Equal(0, OrbGroups.RootOf(0, new[] { 0 }, 1));
        Assert.Equal(1, OrbGroups.RootOf(1, new[] { -1 }, 2));
        Assert.Equal(1, OrbGroups.RootOf(1, Array.Empty<int>(), 2));
    }

    // A chain that walks off the end of a short leadOf: the orb past the end
    // has no lead, so it is the root. ASelfLeadIsItsOwnRoot's empty array
    // returns before the walk (Resolves says no first); this one walks.
    [Fact]
    public void AChainThatWalksPastAShortLeadArrayEndsThere()
    {
        Assert.Equal(1, OrbGroups.RootOf(0, new[] { 1 }, 2));
    }

    // ---- teams: a late member (T3) ---------------------------------------------

    [Fact]
    public void ALateMemberGetsTheRootsGroupAndNothingIsOverwritten()
    {
        var table = new OrbGroupTable();
        var a = table.Create(idSource: Ids("g-00000001"));
        var b = table.Create(idSource: Ids("g-00000002"));
        table.AssignTeam("lead", new string?[] { "m1" }, a.Id);
        table.Move("chose-b", b.Id);

        var changed = table.EnsureTeamMembership("lead", new string?[] { "m1", "late", "chose-b", null, "" });

        Assert.True(changed);
        Assert.Equal(a.Id, table.MembershipFor("late"));
        Assert.Equal(b.Id, table.MembershipFor("chose-b"));
        Assert.Equal(a.Id, table.MembershipFor("m1"));
    }

    [Fact]
    public void EnsuringATeamTwiceChangesNothingTheSecondTime()
    {
        var table = new OrbGroupTable();
        var a = table.Create(idSource: Ids("g-00000001"));
        table.Move("lead", a.Id);

        Assert.True(table.EnsureTeamMembership("lead", new string?[] { "late" }));
        Assert.False(table.EnsureTeamMembership("lead", new string?[] { "late" }));
    }

    [Fact]
    public void ARootWithNoGroupHasNothingToGive()
    {
        var table = new OrbGroupTable();

        Assert.False(table.EnsureTeamMembership("lead", new string?[] { "late" }));
        Assert.False(table.EnsureTeamMembership(null, new string?[] { "late" }));
        Assert.Empty(table.Members);
    }

    // Each field alone is enough for a style to be kept — the four arms of the
    // "nothing left to override" test in CleanStyle.
    [Theory]
    [InlineData("#112233", null, null, null)]
    [InlineData(null, 0.5, null, null)]
    [InlineData(null, null, "#112233", null)]
    [InlineData(null, null, null, 0.5)]
    public void AStyleWithAnySingleFieldSetIsKept(string? stroke, double? strokeOpacity, string? fill, double? fillOpacity)
    {
        Assert.NotNull(OrbGroups.CleanStyle(new GroupStyle(stroke, strokeOpacity, fill, fillOpacity)));
    }

    [Fact]
    public void GetAnswersNullForANullIdAndDeleteLeavesOtherGroupsMembersAlone()
    {
        var table = new OrbGroupTable();
        var one = table.Create(idSource: Ids("g-00000001"));
        var two = table.Create(idSource: Ids("g-00000002"));
        table.Move("a", one.Id);
        table.Move("b", two.Id);

        Assert.Null(table.Get(null));

        table.Delete(one.Id);

        Assert.Null(table.MembershipFor("a"));
        Assert.Equal(two.Id, table.MembershipFor("b"));
    }

    [Fact]
    public void ARootGroupedOnlyByItsDirectoryKeyGivesItsMembersThatGroup()
    {
        var table = new OrbGroupTable();
        var a = table.Create(idSource: Ids("g-00000001"));
        table.Move("/work/repo", a.Id);

        var changed = table.EnsureTeamMembership(
            "/work/repo\ntitle", new string?[] { "m1", "m2" }, "/work/repo");

        Assert.True(changed);
        Assert.Equal(a.Id, table.MembershipFor("m1"));
        Assert.Equal(a.Id, table.MembershipFor("m2"));
    }

    [Fact]
    public void ARootWithNoFallbackGivesItsMembersNothingFromAMatchingDirectoryEntry()
    {
        // Codex and Grok roots are passed no fallback, so a Claude Code orb's
        // directory entry is not theirs to lend.
        var table = new OrbGroupTable();
        var a = table.Create(idSource: Ids("g-00000001"));
        table.Move("/work/repo", a.Id);

        Assert.False(table.EnsureTeamMembership("codex\n/work/repo\ntitle", new string?[] { "m1" }, null));
        Assert.Null(table.MembershipFor("m1"));
    }

    [Fact]
    public void TheRootsOwnKeyBeatsTheFallbackWhenBothExist()
    {
        var table = new OrbGroupTable();
        var a = table.Create(idSource: Ids("g-00000001"));
        var b = table.Create(idSource: Ids("g-00000002"));
        table.Move("/work/repo", a.Id);
        table.Move("/work/repo\ntitle", b.Id);

        table.EnsureTeamMembership("/work/repo\ntitle", new string?[] { "m1" }, "/work/repo");

        Assert.Equal(b.Id, table.MembershipFor("m1"));
    }
}
