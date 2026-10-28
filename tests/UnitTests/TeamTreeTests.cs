using Xunit;

namespace Orbweaver.Tests;

// CB-268 S7: the one answer to "who leads whom" that the arrangement, the drag,
// the Group menu and the arrows share. T1 (a team resolves from its root), T2
// (the team is the root plus every descendant), T4 (an ended lead hands its
// members to the nearest surviving ancestor) and the cycle rule that has to
// agree with OrbArrangement.Classify.
public class TeamTreeTests
{
    private static TeamTree Tree(string[] present, params (string Id, string Lead)[] leads) =>
        new(present, leads.ToDictionary(l => l.Id, l => l.Lead, StringComparer.Ordinal));

    [Fact]
    public void ASoloOrbIsItsOwnRootAndATeamOfOne()
    {
        var tree = Tree(new[] { "a" });

        Assert.Null(tree.LeadOf("a"));
        Assert.Equal("a", tree.RootOf("a"));
        Assert.Equal(new[] { "a" }, tree.TeamOf("a"));
    }

    [Fact]
    public void EveryOrbOfANestedTeamHasTheSameTeamRootFirst()
    {
        // root ← lead ← member, plus an unrelated orb listed between them.
        var tree = Tree(new[] { "member", "other", "lead", "root" },
            ("lead", "root"), ("member", "lead"));

        Assert.Equal("lead", tree.LeadOf("member"));
        Assert.Equal("root", tree.RootOf("member"));

        // T2: whichever orb was pressed, the same list — root, then `present` order.
        var team = new[] { "root", "member", "lead" };
        Assert.Equal(team, tree.TeamOf("member"));
        Assert.Equal(team, tree.TeamOf("lead"));
        Assert.Equal(team, tree.TeamOf("root"));
        Assert.Equal(new[] { "other" }, tree.TeamOf("other"));
    }

    [Fact]
    public void AMemberWhoseLeadEndedAttachesToItsNearestSurvivingAncestor()
    {
        // T4: "lead" is gone from the screen but its own lead is remembered.
        var tree = Tree(new[] { "root", "member" }, ("lead", "root"), ("member", "lead"));

        Assert.Equal("root", tree.LeadOf("member"));
        Assert.Equal(new[] { "root", "member" }, tree.TeamOf("member"));
    }

    [Fact]
    public void SeveralEndedLeadsInARowAreWalkedPast()
    {
        var tree = Tree(new[] { "root", "member" },
            ("gone1", "root"), ("gone2", "gone1"), ("member", "gone2"));

        Assert.Equal("root", tree.LeadOf("member"));
    }

    [Fact]
    public void AMemberWithNoSurvivingAncestorIsItsOwnRoot()
    {
        var tree = Tree(new[] { "member" }, ("lead", "root"), ("member", "lead"));

        Assert.Null(tree.LeadOf("member"));
        Assert.Equal("member", tree.RootOf("member"));
    }

    [Fact]
    public void AChainThatLoopsAmongEndedSessionsFindsNobody()
    {
        var tree = Tree(new[] { "member" }, ("member", "x"), ("x", "y"), ("y", "x"));

        Assert.Null(tree.LeadOf("member"));
    }

    [Fact]
    public void ASelfLeadOrAnEmptyLeadIsNoLead()
    {
        var tree = Tree(new[] { "a", "b" }, ("a", "a"), ("b", ""));

        Assert.Null(tree.LeadOf("a"));
        Assert.Null(tree.LeadOf("b"));
    }

    // OrbArrangement.Classify makes every orb in a cycle, and every orb pointing
    // into one, an anchor of its own. The tree has to say the same, or a drag
    // would move as one team orbs the arrangement drew apart.
    [Fact]
    public void OrbsInACycleAndOrbsPointingIntoOneAreEachTheirOwnRoot()
    {
        var tree = Tree(new[] { "a", "b", "c" }, ("a", "b"), ("b", "a"), ("c", "a"));

        Assert.Equal("b", tree.LeadOf("a"));
        Assert.Equal("a", tree.RootOf("a"));
        Assert.Equal("b", tree.RootOf("b"));
        Assert.Equal("c", tree.RootOf("c"));
        Assert.Equal(new[] { "a" }, tree.TeamOf("a"));

        var leadOf = tree.LeadIndices();
        for (var i = 0; i < 3; i++)
            Assert.False(OrbArrangement.Resolves(i, leadOf, 3), $"Classify would make orb {i} a member");
    }

    [Fact]
    public void LeadIndicesAreTheEffectiveLeadsAsPositionsInPresent()
    {
        var tree = Tree(new[] { "root", "member", "solo", "root" }, ("lead", "root"), ("member", "lead"));

        // Duplicates in `present` are dropped.
        Assert.Equal(new[] { "root", "member", "solo" }, tree.Present);
        Assert.Equal(new[] { -1, 0, -1 }, tree.LeadIndices());
    }

    [Fact]
    public void ReachableKeepsEveryLinkSomethingOnScreenWalksThrough()
    {
        var known = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["member"] = "lead",       // on screen
            ["lead"] = "root",         // ended, but member walks through it
            ["root"] = "",             // a stored empty lead stops the walk
            ["stranger"] = "nobody",   // ended, and nothing descends from it
            ["loop1"] = "loop2",
            ["loop2"] = "loop1",
        };

        var keep = TeamTree.Reachable(new[] { "member", "loop1", "absent" }, known);

        Assert.Equal(new[] { "lead", "loop1", "loop2", "member", "root" }, keep.OrderBy(k => k, StringComparer.Ordinal));
    }
}
