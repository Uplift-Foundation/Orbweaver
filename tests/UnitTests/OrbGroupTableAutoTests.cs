using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-281: auto groups in the table. They are ordinary entries under a derived id, so what
// matters is that they are created once, never disturbed, never pruned as empty, and
// bounded.
public class OrbGroupTableAutoTests
{
    private static readonly IReadOnlySet<string> NoneLive = new HashSet<string>();

    private static string Id(int n) => $"a-dir-{n:x12}";

    [Fact]
    public void ItCreatesTheGroupWithMainsShapeAndNoAnchor()
    {
        var table = new OrbGroupTable();
        table.SetShape(OrbGroups.Main, "circle");

        Assert.True(table.EnsureAuto(Id(1), "board", NoneLive));

        var group = table.Get(Id(1))!;
        Assert.Equal(new OrbGroup(Id(1), "board", "circle", null, null), group);
    }

    [Fact]
    public void ItNeverRenamesReshapesOrMovesAnExistingGroup()
    {
        var table = new OrbGroupTable();
        table.EnsureAuto(Id(1), "board", NoneLive);
        table.Rename(Id(1), "My boards");
        table.SetShape(Id(1), "star");
        table.SetAnchor(Id(1), new OrbweaverSettings.OrbPlacement(10, 20));
        var before = table.Get(Id(1));

        Assert.False(table.EnsureAuto(Id(1), "board", NoneLive));

        Assert.Equal(before, table.Get(Id(1)));
    }

    [Fact]
    public void ItRefusesAnIdThatIsNotAnAutoId()
    {
        var table = new OrbGroupTable();
        Assert.False(table.EnsureAuto("g-0a1b2c3d", "x", NoneLive));
        Assert.False(table.EnsureAuto("main", "x", NoneLive));
        Assert.Null(table.Get("g-0a1b2c3d"));
    }

    [Fact]
    public void ATakenNameGetsANumberedSuffixCaseInsensitively()
    {
        var table = new OrbGroupTable();
        table.EnsureAuto(Id(1), "app", NoneLive);
        table.EnsureAuto(Id(2), "APP", NoneLive);
        table.EnsureAuto(Id(3), "app", NoneLive);

        Assert.Equal("app", table.Get(Id(1))!.Name);
        Assert.Equal("APP (2)", table.Get(Id(2))!.Name);
        Assert.Equal("app (3)", table.Get(Id(3))!.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ABlankNameBecomesGroup(string name)
    {
        var table = new OrbGroupTable();
        table.EnsureAuto(Id(1), name, NoneLive);
        Assert.Equal("Group", table.Get(Id(1))!.Name);
    }

    [Fact]
    public void AnAutoGroupSurvivesItsLastExplicitMemberLeaving()
    {
        var table = new OrbGroupTable();
        table.EnsureAuto(Id(1), "board", NoneLive);
        table.Move("k", Id(1));

        table.Unassign("k");

        Assert.NotNull(table.Get(Id(1)));
    }

    [Fact]
    public void AnAutoGroupSurvivesAMemberBeingMovedAway()
    {
        var table = new OrbGroupTable();
        table.EnsureAuto(Id(1), "board", NoneLive);
        table.Move("k", Id(1));

        table.Move("k", OrbGroups.Main);

        Assert.NotNull(table.Get(Id(1)));
    }

    [Fact]
    public void AManualGroupStillPrunesWhenItsLastMemberLeaves()
    {
        var table = new OrbGroupTable();
        var manual = table.Create(null, () => "g-00000001");
        table.Move("k", manual.Id);

        table.Unassign("k");

        Assert.Null(table.Get(manual.Id));
    }

    [Fact]
    public void ADeletedAutoGroupIsDeletedAndItsMembersFallBack()
    {
        var table = new OrbGroupTable();
        table.EnsureAuto(Id(1), "board", NoneLive);
        table.Move("k", Id(1));

        Assert.True(table.Delete(Id(1)));
        Assert.Null(table.MembershipFor("k"));
    }

    // ---- the cap --------------------------------------------------------------

    private static OrbGroupTable Full(out List<string> ids)
    {
        var table = new OrbGroupTable();
        ids = new List<string>();
        for (var i = 0; i < OrbGroupTable.MaxAutoGroups; i++)
        {
            ids.Add(Id(i));
            table.EnsureAuto(Id(i), "g" + i, NoneLive);
        }
        return table;
    }

    [Fact]
    public void PastTheCapTheFirstDormantGroupByIdIsEvicted()
    {
        var table = Full(out var ids);

        Assert.True(table.EnsureAuto(Id(999), "new", NoneLive));

        Assert.Null(table.Get(ids[0]));
        Assert.NotNull(table.Get(ids[1]));
        Assert.NotNull(table.Get(Id(999)));
        Assert.Equal(OrbGroupTable.MaxAutoGroups, table.Groups.Keys.Count(OrbAutoGroups.IsAuto));
    }

    [Fact]
    public void AGroupOnScreenOrWithAMemberIsNeverEvicted()
    {
        var table = Full(out var ids);
        table.Move("k", ids[0]);                                   // explicit member
        var live = new HashSet<string> { ids[1] };                 // on screen now

        table.EnsureAuto(Id(999), "new", live);

        Assert.NotNull(table.Get(ids[0]));
        Assert.NotNull(table.Get(ids[1]));
        Assert.Null(table.Get(ids[2]));
    }

    [Fact]
    public void WithNothingDormantTheGroupIsCreatedAnywayAndNothingIsLost()
    {
        var table = Full(out var ids);

        table.EnsureAuto(Id(999), "new", new HashSet<string>(ids));

        Assert.NotNull(table.Get(Id(999)));
        Assert.All(ids, id => Assert.NotNull(table.Get(id)));
    }

    // ---- file -----------------------------------------------------------------

    [Fact]
    public void AnAutoGroupRoundTripsThroughJsonWithItsAnchorAndRename()
    {
        var table = new OrbGroupTable();
        table.EnsureAuto(Id(1), "board", NoneLive);
        table.SetAnchor(Id(1), new OrbweaverSettings.OrbPlacement(300, 400));
        table.Rename(Id(1), "Boards");

        var back = new OrbGroupTable();
        back.ReadJson(JsonNode.Parse(table.ToJson().ToJsonString()));

        Assert.Equal(table.Get(Id(1)), back.Get(Id(1)));
        Assert.Equal(300, back.Get(Id(1))!.Anchor!.X);
    }
}
