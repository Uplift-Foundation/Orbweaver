using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-268 QA: hand-edited and downgraded settings.json shapes for the group table.
public class OrbGroupTableQaTests
{
    private static OrbGroupTable Read(string json)
    {
        var table = new OrbGroupTable();
        table.ReadJson(JsonNode.Parse(json));
        return table;
    }

    [Fact]
    public void AUserGroupWithNoNameIsNotCalledMain()
    {
        var table = Read("""{"groups":{"g-0000abcd":{"shape":"star"}},"members":{}}""");

        var group = table.Get("g-0000abcd");
        Assert.NotNull(group);
        Assert.NotEqual("Main", group!.Name);
        Assert.Equal("star", group.Shape);
    }

    [Fact]
    public void AMembershipNamingAMissingGroupIsDroppedAndNeverResurrectsIt()
    {
        var table = Read("""{"groups":{},"members":{"/a":"g-deadbeef","/b":"main"}}""");

        Assert.Null(table.MembershipFor("/a"));
        Assert.Equal("main", table.MembershipFor("/b"));
        Assert.Null(table.Get("g-deadbeef"));
    }

    [Fact]
    public void MembershipKeysAreCaseInsensitiveAndTheFallbackKeyIsTried()
    {
        var table = Read("""{"groups":{"g-00000001":{"name":"G","shape":"circle"}},"members":{"C:\\Repo":"g-00000001"}}""");

        Assert.Equal("g-00000001", table.MembershipFor("c:\\repo"));
        Assert.Equal("g-00000001", table.MembershipFor("c:\\repo|Title", "C:\\REPO"));
        Assert.Null(table.MembershipFor("c:\\repo|Title", null));
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"groups":[], "members":[]}""")]
    [InlineData("""{"groups":{"main":5,"heartbeats":null,"g-1":"x"},"members":{"/a":7,"":"main"}}""")]
    public void WrongTypesCostOnlyTheEntryNeverTheTable(string json)
    {
        var table = Read(json);

        Assert.Equal(OrbGroups.PredefinedIds.Length, table.Ordered().Count);
        Assert.Equal("main", table.Ordered()[0].Id);
    }

    [Fact]
    public void ControlAWellFormedBlockDoesChangeTheTable()
    {
        var table = Read("""{"groups":{"heartbeats":{"name":"HB","shape":"star","anchor":{"x":5,"y":6}}},"members":{}}""");

        Assert.Equal("HB", table.Get("heartbeats")!.Name);
        Assert.Equal(5, table.Get("heartbeats")!.Anchor!.X);
    }

    [Fact]
    public void LeavingOneGroupForAnotherPrunesOnlyTheEmptiedUserGroup()
    {
        var table = new OrbGroupTable();
        var a = table.Create();
        var b = table.Create();
        table.AssignTeam("/lead", new[] { "/m1", "/m2" }, a.Id);
        table.AssignTeam("/other", new string?[0], a.Id);

        table.AssignTeam("/lead", new[] { "/m1", "/m2" }, b.Id);

        Assert.NotNull(table.Get(a.Id));      // /other is still in a
        Assert.Equal(b.Id, table.MembershipFor("/m2"));

        table.Move("/other", b.Id);
        Assert.Null(table.Get(a.Id));         // now empty
        Assert.NotNull(table.Get("main"));
    }
}

// T1/T4/T3 adversarial cases for the team-root rules.
public class OrbTeamRootQaTests
{
    private static OrbGroupTable TableWith(params (string key, string group)[] members)
    {
        var table = new OrbGroupTable();
        foreach (var g in members.Select(m => m.group).Distinct().Where(g => !OrbGroups.IsPredefined(g)))
            table.Create(null, () => g);
        foreach (var (key, group) in members) table.Move(key, group);
        return table;
    }

    [Fact]
    public void AMemberWhoseOwnEntryDisagreesFollowsItsLeadAcrossGroups()
    {
        var table = TableWith(("lead", "g-0000000a"), ("m1", "g-0000000b"), ("m2", "g-0000000b"));
        var keys = new[] { "lead", "m1", "m2" };
        var leadOf = new[] { -1, 0, 1 };

        for (var i = 0; i < 3; i++)
            Assert.Equal("g-0000000a", OrbGroups.TeamGroupOf(i, leadOf, keys, k => table.MembershipFor(k)));

        // Control: with no team the members answer from their own keys.
        var flat = new[] { -1, -1, -1 };
        Assert.Equal("g-0000000b", OrbGroups.TeamGroupOf(1, flat, keys, k => table.MembershipFor(k)));
    }

    [Fact]
    public void EveryOrbInALeadCycleIsItsOwnRootAndANonCycleOrbIsUnaffected()
    {
        // 0->2->1->0 cycle; 3 alone. Rule (87c7c5bc): a cycle member is its own root.
        var leadOf = new[] { 2, 0, 1, -1 };
        for (var i = 0; i < 3; i++) Assert.Equal(i, OrbGroups.RootOf(i, leadOf, 4));
        Assert.Equal(3, OrbGroups.RootOf(3, leadOf, 4));
        // a tail hanging off the cycle resolves to the tail's own nearest non-cycle ancestor or itself, never loops
        var withTail = new[] { 2, 0, 1, 0 };
        Assert.InRange(OrbGroups.RootOf(3, withTail, 4), 0, 3);
    }

    [Fact]
    public void ALeadThatEndedLeavesTheNearestSurvivingAncestorAsRoot()
    {
        // 0 <- 1 <- 2, and 1's lead index is out of range (ended): 1 is the root of 1 and 2.
        var leadOf = new[] { -1, 99, 1 };
        Assert.Equal(1, OrbGroups.RootOf(2, leadOf, 3));
        Assert.Equal(1, OrbGroups.RootOf(1, leadOf, 3));
    }

    [Fact]
    public void AMemberOutlivingItsLeadKeepsTheTeamsGroupOnceEnsured()
    {
        var table = TableWith(("lead", "g-0000000a"));
        Assert.True(table.EnsureTeamMembership("lead", new string?[] { "m1", "", null }));
        Assert.False(table.EnsureTeamMembership("lead", new string?[] { "m1" }));   // idempotent
        Assert.Equal("g-0000000a", table.MembershipFor("m1"));

        table.Move("m2", "main");
        table.EnsureTeamMembership("lead", new string?[] { "m2" });
        Assert.Equal("main", table.MembershipFor("m2"));                            // never overwrites
    }

    [Fact]
    public void ARootWithNoGroupGivesNothing()
    {
        var table = new OrbGroupTable();
        Assert.False(table.EnsureTeamMembership("nobody", new string?[] { "m1" }));
        Assert.Null(table.MembershipFor("m1"));
    }
}
