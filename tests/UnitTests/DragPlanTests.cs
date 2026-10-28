using Xunit;

namespace Orbweaver.Tests;

// CB-268 S4: who moves with a drag. The gesture table from the plan: a plain
// orb drag moves the orb's team, Shift moves the group, the backdrop always
// moves the group — for a solo orb, a lead, a member and a nested member.
public class DragPlanTests
{
    private static readonly string[] Group = { "solo", "lead", "m1", "m2", "nested", "other" };

    public static TheoryData<string, string[]> Teams => new()
    {
        { "solo", new[] { "solo" } },
        { "lead", new[] { "lead", "m1", "m2", "nested" } },
        // Pressing a member moves the lead and every member too.
        { "m1", new[] { "lead", "m1", "m2", "nested" } },
        { "nested", new[] { "lead", "m1", "m2", "nested" } },
    };

    [Theory]
    [MemberData(nameof(Teams))]
    public void APlainOrbDragMovesTheOrbsWholeTeamNotTheGroup(string pressed, string[] team)
    {
        var (whole, ids) = DragPlan.Followers(DragSource.Orb, false, team, Group);

        Assert.False(whole);
        Assert.Equal(team, ids);
        Assert.Contains(pressed, ids);
    }

    [Theory]
    [MemberData(nameof(Teams))]
    public void ShiftOnAnOrbMovesTheWholeGroup(string pressed, string[] team)
    {
        var (whole, ids) = DragPlan.Followers(DragSource.Orb, true, team, Group);

        Assert.True(whole);
        Assert.Equal(Group, ids);
        Assert.Contains(pressed, ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABackdropDragMovesTheWholeGroupWhateverShiftSays(bool shift)
    {
        var (whole, ids) = DragPlan.Followers(DragSource.Backdrop, shift, new[] { "lead" }, Group);

        Assert.True(whole);
        Assert.Equal(Group, ids);
    }

    [Fact]
    public void TheThresholdIsSixDip()
    {
        Assert.Equal(6, DragPlan.ThresholdDip);
    }

    [Theory]
    [InlineData(5, 0, 1, false)]
    [InlineData(6, 0, 1, true)]
    [InlineData(0, -6, 1, true)]
    [InlineData(-3, 5, 1, false)]
    // 2x display: 11 physical px is 5.5 DIP, 12 is 6.
    [InlineData(11, 0, 2, false)]
    [InlineData(0, 12, 2, true)]
    // A nonsense scaling reads as 1 rather than dividing by zero.
    [InlineData(6, 0, 0, true)]
    [InlineData(5, 0, -2, false)]
    public void ExceededMeasuresInDipNotPhysicalPixels(double dx, double dy, double scaling, bool expected)
    {
        Assert.Equal(expected, DragPlan.Exceeded(dx, dy, scaling));
    }
}
