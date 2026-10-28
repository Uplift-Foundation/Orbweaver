using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Xunit;

namespace Orbweaver.Tests;

// CB-268 S6's right-click Group submenu, driven the way OrbSizeTests drives the
// Size one: SessionMenu_Opening and the submenu's own items, never a real
// right-click. A team is the unit of grouping, so the cases that matter are the
// ones where a member's menu moves its lead and its siblings.
[Collection("Settings")]
public class OrbGroupSubmenuTests : IDisposable
{
    private readonly List<string> _groups = new();
    private readonly List<string> _keys = new();

    public void Dispose()
    {
        foreach (var key in _keys) OrbweaverSettings.UnassignOrbGroup(key);
        foreach (var id in _groups) OrbweaverSettings.DeleteOrbGroup(id);
        OrbweaverSettings.SetOrbGroupShape(OrbGroups.Main, "heart");
        OrbweaverSettings.RenameOrbGroup(OrbGroups.Main, "Main");
        GroupStyleWindow.OpenForTests = null;
        GroupStyleWindow.PresentForTests = null;
        GroupStyleWindow.BringForwardForTests = null;
    }

    private string Key() { var k = "group-menu-" + Guid.NewGuid(); _keys.Add(k); return k; }

    private OrbGroup NewGroup(string shape = "circle")
    {
        var group = OrbweaverSettings.CreateOrbGroup(shape);
        _groups.Add(group.Id);
        return group;
    }

    private OrbWindow NewOrb(string? key = null)
    {
        key ??= Key();
        return new OrbWindow(Guid.NewGuid().ToString()) { PositionKey = key, SoundKey = key };
    }

    private static MenuItem Submenu(OrbWindow orb) => orb.GroupMenuItem;
    private static List<MenuItem> Items(MenuItem parent) => parent.Items.OfType<MenuItem>().ToList();
    private static MenuItem Child(MenuItem parent, string header) => Items(parent).Single(i => Equals(i.Header, header));
    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    [AvaloniaFact]
    public void OpeningTheContextMenuBuildsTheGroupSubmenu()
    {
        var orb = NewOrb();

        orb.SessionMenu_Opening(null, new System.ComponentModel.CancelEventArgs());

        Assert.NotEmpty(Items(Submenu(orb)));
    }

    [AvaloniaFact]
    public void TheMenuNamesTheGroupTheOrbIsInAndIsRebuiltNotAppended()
    {
        var orb = NewOrb();
        orb.RebuildGroupSubmenu();
        var first = Submenu(orb).Items.Count;

        orb.RebuildGroupSubmenu();

        Assert.Equal(first, Submenu(orb).Items.Count);
        var header = Items(Submenu(orb))[0];
        Assert.Equal("In: Main", header.Header);
        Assert.False(header.IsEnabled);
        Assert.IsType<Separator>(Submenu(orb).Items[1]);
        Assert.Equal(
            new[] { "Shape", "Move to", "Rename…", "Style…", "Leave group" },
            Items(Submenu(orb)).Skip(1).Select(i => (string)i.Header!));
    }

    [AvaloniaFact]
    public void LeaveGroupIsHiddenInMainAndShownElsewhere()
    {
        var orb = NewOrb();
        orb.RebuildGroupSubmenu();
        Assert.False(Child(Submenu(orb), "Leave group").IsVisible);

        var group = NewGroup();
        OrbweaverSettings.MoveOrbToGroup(orb.PositionKey, group.Id);
        orb.RebuildGroupSubmenu();

        Assert.Equal($"In: {group.Name}", Items(Submenu(orb))[0].Header);
        Assert.True(Child(Submenu(orb), "Leave group").IsVisible);
    }

    [AvaloniaFact]
    public void TheShapeSubmenuListsTheSettingsShapesAndChecksTheGroupsOwn()
    {
        var group = NewGroup("star");
        var orb = NewOrb();
        OrbweaverSettings.MoveOrbToGroup(orb.PositionKey, group.Id);
        orb.RebuildGroupSubmenu();

        var shapes = Items(Child(Submenu(orb), "Shape"));

        Assert.Equal(SettingsWindow.ShapeChoices.Select(c => c.Label), shapes.Select(s => (string)s.Header!));
        Assert.Equal("Star", Assert.Single(shapes, s => s.IsChecked).Header);
        Assert.All(shapes, s => Assert.Equal(MenuItemToggleType.CheckBox, s.ToggleType));
    }

    [AvaloniaFact]
    public void ChoosingAShapeWritesItToTheGroupAndNotToOtherGroups()
    {
        var group = NewGroup("star");
        var other = NewGroup("grid");
        var orb = NewOrb();
        OrbweaverSettings.MoveOrbToGroup(orb.PositionKey, group.Id);
        orb.RebuildGroupSubmenu();

        Click(Child(Child(Submenu(orb), "Shape"), "Circle"));

        Assert.Equal("circle", OrbweaverSettings.OrbGroupFor(group.Id)!.Shape);
        Assert.Equal("grid", OrbweaverSettings.OrbGroupFor(other.Id)!.Shape);
    }

    [AvaloniaFact]
    public void MoveToListsEveryOtherGroupInSlotOrder()
    {
        var group = NewGroup();
        var orb = NewOrb();
        orb.RebuildGroupSubmenu();

        var targets = Items(Child(Submenu(orb), "Move to")).Select(i => (string)i.Header!).ToList();

        Assert.DoesNotContain("Main", targets);
        Assert.Contains(group.Name, targets);
        Assert.Equal(
            OrbweaverSettings.OrbGroupList().Where(g => g.Id != OrbGroups.Main).Select(g => g.Name),
            targets);
    }

    [AvaloniaFact]
    public void MovingASoloOrbWritesItsMembership()
    {
        var group = NewGroup();
        var orb = NewOrb();
        orb.RebuildGroupSubmenu();

        Click(Child(Child(Submenu(orb), "Move to"), group.Name));

        Assert.Equal(group.Id, OrbweaverSettings.OrbGroupMembershipFor(orb.PositionKey));
    }

    [AvaloniaFact]
    public void MovingATeamMemberMovesTheLeadAndEverySibling()
    {
        var group = NewGroup();
        var leadKey = Key();
        var m1 = Key();
        var m2 = Key();
        var member = NewOrb(m1);
        member.GroupContextOverride = () => new GroupMenuContext(OrbGroups.Main, leadKey, new[] { m1, m2 });
        member.RebuildGroupSubmenu();

        Click(Child(Child(Submenu(member), "Move to"), group.Name));

        Assert.All(new[] { leadKey, m1, m2 },
            k => Assert.Equal(group.Id, OrbweaverSettings.OrbGroupMembershipFor(k)));
    }

    [AvaloniaFact]
    public void LeaveGroupSendsTheWholeTeamToMain()
    {
        var group = NewGroup();
        var leadKey = Key();
        var m1 = Key();
        OrbweaverSettings.AssignTeamToGroup(leadKey, new[] { m1 }, group.Id);
        var member = NewOrb(m1);
        member.GroupContextOverride = () => new GroupMenuContext(group.Id, leadKey, new[] { m1 });
        member.RebuildGroupSubmenu();

        Click(Child(Submenu(member), "Leave group"));

        // An explicit main, not a forgotten entry — see MoveOrbToGroup.
        Assert.Equal(OrbGroups.Main, OrbweaverSettings.OrbGroupMembershipFor(leadKey));
        Assert.Equal(OrbGroups.Main, OrbweaverSettings.OrbGroupMembershipFor(m1));
    }

    [AvaloniaFact]
    public void AnOrbWithNoKeyStillBuildsAMenu()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());

        orb.RebuildGroupSubmenu();

        Assert.Equal("In: Main", Items(Submenu(orb))[0].Header);
    }

    [AvaloniaFact]
    public void TheManagersAnswerIsUsedWhenThereIsOne()
    {
        // CurrentManager is the seam; a manager built here knows no sessions,
        // so the orb is its own root and the menu still builds.
        var orb = NewOrb();
        orb.CurrentManager = () => null;
        orb.RebuildGroupSubmenu();

        Assert.Equal("In: Main", Items(Submenu(orb))[0].Header);
    }

    [AvaloniaFact]
    public void RenameAndStyleOpenTheDialogOnTheGroup()
    {
        var group = NewGroup();
        var orb = NewOrb();
        OrbweaverSettings.MoveOrbToGroup(orb.PositionKey, group.Id);
        GroupStyleWindow? shown = null;
        GroupStyleWindow.PresentForTests = w => shown = w;
        orb.RebuildGroupSubmenu();

        Click(Child(Submenu(orb), "Style…"));

        Assert.NotNull(shown);
        Assert.Equal(group.Id, shown!.GroupId);

        GroupStyleWindow.OpenForTests = null;
        shown = null;
        Click(Child(Submenu(orb), "Rename…"));
        Assert.Equal(group.Id, shown!.GroupId);
    }

    [AvaloniaFact]
    public void ChoosingAShapeOnADeletedGroupDoesNotThrow()
    {
        var group = NewGroup();
        var orb = NewOrb();
        OrbweaverSettings.MoveOrbToGroup(orb.PositionKey, group.Id);
        orb.RebuildGroupSubmenu();
        OrbweaverSettings.DeleteOrbGroup(group.Id);

        Click(Child(Child(Submenu(orb), "Shape"), "Grid"));

        Assert.Null(OrbweaverSettings.OrbGroupFor(group.Id));
    }
}
