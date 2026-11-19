using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Orbweaver;
using Xunit;

namespace Orbweaver.Tests;

// CB-355: the session rows moved out of the tray's top level into one
// "Agents (N)" submenu, because 17+ sessions pushed Show orbs, Settings and
// Quit off the screen. AgentsItem is the pure builder; these pin its shape
// without a TrayController, so no click ever reaches TerminalFocuser.
public class TrayAgentsItemTests
{
    private static TrayController.SessionEntry Entry(string id, string title, string state = "idle") =>
        new(id, new SessionStatus { State = state, Title = title });

    private static List<NativeMenuItem> Children(NativeMenuItem item) =>
        item.Menu!.Items.OfType<NativeMenuItem>().ToList();

    // NativeMenuItem exposes no public way to fire Click; the platform exporter
    // calls this internal bridge method when the user picks the item.
    private static void Click(NativeMenuItem item)
    {
        var m = typeof(NativeMenuItem).GetMethods(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            .First(x => x.Name.EndsWith("RaiseClick") || x.Name.EndsWith("RaiseClicked"));
        m.Invoke(item, null);
    }

    [AvaloniaFact]
    public void NoSessionsGivesADisabledAgentsItemWithNoSubmenu()
    {
        var item = TrayController.AgentsItem(Array.Empty<TrayController.SessionEntry>(), _ => { });

        Assert.Equal("Agents", item.Header);
        Assert.False(item.IsEnabled);
        Assert.Null(item.Menu);
    }

    [AvaloniaFact]
    public void SessionsGoInASubmenuLabelledWithTheCountInOrder()
    {
        var item = TrayController.AgentsItem(new[]
        {
            Entry("id-1", "first"),
            Entry("id-2", "second", "waiting"),
            Entry("id-3", "third", "generating"),
        }, _ => { });

        Assert.Equal("Agents (3)", item.Header);
        Assert.True(item.IsEnabled);
        Assert.Equal(
            new[] { "first — idle", "second — needs you", "third — working" },
            Children(item).Select(c => c.Header).ToArray());
    }

    [AvaloniaFact]
    public void CollidingNamesKeepTheirIdSuffixInsideTheSubmenu()
    {
        var item = TrayController.AgentsItem(new[]
        {
            Entry("aaaa1111", "evidence"),
            Entry("bbbb2222", "evidence"),
            Entry("cccc3333", "unique"),
        }, _ => { });

        Assert.Equal(
            new[] { "evidence (aaaa) — idle", "evidence (bbbb) — idle", "unique — idle" },
            Children(item).Select(c => c.Header).ToArray());
    }

    [AvaloniaFact]
    public void ClickingAChildPassesThatSessionToTheCallback()
    {
        var a = Entry("id-1", "first");
        var b = Entry("id-2", "second");
        var clicked = new List<TrayController.SessionEntry>();
        var item = TrayController.AgentsItem(new[] { a, b }, clicked.Add);

        var children = Children(item);
        Click(children[1]);

        Assert.Equal(new[] { b }, clicked);
        Click(children[0]);
        Assert.Equal(new[] { b, a }, clicked);
    }
}
