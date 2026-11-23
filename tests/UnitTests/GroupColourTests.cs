using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-409: GroupColour.For — the stable, seeded colour a group wears in
// "Random per group" mode.
public class GroupColourTests
{
    [Fact]
    public void TheSameIdSeedAndSaltAlwaysGiveTheSameColour()
    {
        Assert.Equal(GroupColour.For("g-0a1b2c3d", 4, 2), GroupColour.For("g-0a1b2c3d", 4, 2));
    }

    // Hard-coded, so a hash that is randomised per process (string.GetHashCode)
    // fails here on the second run rather than quietly breaking "stable across
    // restarts". 9 is FNV-1a's answer for this input, worked out independently.
    [Fact]
    public void TheHashIsFnv1aNotARandomisedOne()
    {
        Assert.Equal(GroupColour.Palette[9], GroupColour.For("g-0a1b2c3d", 0, 0));
        Assert.Equal(GroupColour.Palette[10], GroupColour.For("g-0a1b2c3d", 7, 0));
    }

    [Fact]
    public void TwoHundredIdsReachMostOfThePalette()
    {
        var used = Enumerable.Range(0, 200)
            .Select(i => GroupColour.For($"g-{i * 2654435761u:x8}", 0, 0))
            .Distinct().Count();
        Assert.True(used >= 10, $"only {used} of {GroupColour.Palette.Length} slots used");
    }

    [Fact]
    public void ANewSeedChangesMostColours()
    {
        var ids = Enumerable.Range(0, 100).Select(i => $"g-{i:x8}").ToList();
        var changed = ids.Count(id => GroupColour.For(id, 0, 0) != GroupColour.For(id, 12345, 0));
        Assert.True(changed >= 70, $"only {changed} of 100 changed");
    }

    [Fact]
    public void ANewSaltChangesThatGroupsColour()
    {
        var ids = Enumerable.Range(0, 100).Select(i => $"g-{i:x8}").ToList();
        var changed = ids.Count(id => GroupColour.For(id, 3, 0) != GroupColour.For(id, 3, 1));
        Assert.True(changed >= 70, $"only {changed} of 100 changed");
    }

    [Fact]
    public void EveryPaletteEntryParsesAndIsUnique()
    {
        foreach (var hex in GroupColour.Palette) Assert.True(Color.TryParse(hex, out _), hex);
        Assert.Equal(GroupColour.Palette.Length, new HashSet<string>(GroupColour.Palette).Count);
    }

    [Fact]
    public void ANegativeSeedAndANullIdStillPickAPaletteEntry()
    {
        Assert.Contains(GroupColour.For("g-x", -5, -1), GroupColour.Palette);
        Assert.Contains(GroupColour.For(null!, 0, 0), GroupColour.Palette);
    }
}
