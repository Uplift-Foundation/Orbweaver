using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-355 edge cases around the tray's "Agents (N)" submenu: the shape of the
// top level after Rebuild, and the submenu contents AgentsItem hands back.
// Click handlers are never invoked — they reach TerminalFocuser.
[Collection("Settings")]
public class TrayAgentsSubmenuQaTests
{
    private static TrayController? NewController()
    {
        try { return new TrayController(); }
        catch { return null; } // no TrayIcon under the headless platform
    }

    private static NativeMenu MenuOf(TrayController tray) =>
        (NativeMenu)typeof(TrayController)
            .GetField("_menu", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(tray)!;

    private static TrayController.SessionEntry Entry(
        string id, string state = "idle", string title = "") =>
        new(id, new SessionStatus { State = state, Title = title });

    private static List<string> TopLabels(NativeMenu menu) =>
        menu.Items.OfType<NativeMenuItem>().Select(i => i.Header ?? "").ToList();

    private static List<string> SubLabels(NativeMenuItem agents) =>
        agents.Menu!.Items.OfType<NativeMenuItem>().Select(i => i.Header ?? "").ToList();

    private static NativeMenuItem Agents(NativeMenu menu) =>
        menu.Items.OfType<NativeMenuItem>().Single(i => (i.Header ?? "").StartsWith("Agents"));

    [AvaloniaFact]
    public void OneSessionIsCountedAsOneAndListedInsideTheSubmenu()
    {
        var item = TrayController.AgentsItem(new[] { Entry("id-1", title: "solo") }, _ => { });

        Assert.Equal("Agents (1)", item.Header);
        Assert.True(item.IsEnabled);
        Assert.Equal(new[] { "solo — idle" }, SubLabels(item));
    }

    [AvaloniaFact]
    public void TheCountMatchesExactlyForManySessionsAndKeepsOrder()
    {
        var sessions = Enumerable.Range(1, 7)
            .Select(n => Entry($"id-{n}", title: $"s{n}")).ToArray();

        var item = TrayController.AgentsItem(sessions, _ => { });

        Assert.Equal("Agents (7)", item.Header);
        Assert.Equal(
            Enumerable.Range(1, 7).Select(n => $"s{n} — idle").ToList(),
            SubLabels(item));
    }

    [AvaloniaFact]
    public void RebuildHasExactlyOneAgentsItemAtTopLevelAndNoSessionRowsBesideIt()
    {
        var tray = NewController();
        if (tray is null) return;

        tray.Update(new[] { Entry("id-1", title: "first"), Entry("id-2", state: "waiting", title: "second") });
        var menu = MenuOf(tray);
        var top = TopLabels(menu);

        Assert.Single(top, h => h.StartsWith("Agents"));
        Assert.Equal("Agents (2)", top[0]);
        Assert.DoesNotContain("first — idle", top);
        Assert.DoesNotContain("second — needs you", top);
        Assert.DoesNotContain("No sessions", top);
        Assert.Equal(new[] { "first — idle", "second — needs you" }, SubLabels(Agents(menu)));
    }

    [AvaloniaFact]
    public void EmptyRebuildShowsDisabledAgentsAtTopAndNoNoSessionsRow()
    {
        var tray = NewController();
        if (tray is null) return;

        var menu = MenuOf(tray);
        var agents = Agents(menu);

        Assert.Equal("Agents", agents.Header);
        Assert.False(agents.IsEnabled);
        Assert.Null(agents.Menu);
        Assert.DoesNotContain("No sessions", TopLabels(menu));
    }

    [AvaloniaFact]
    public void ThePermanentTopLevelItemsStayTopLevelAfterAgentsWithAndWithoutSessions()
    {
        var tray = NewController();
        if (tray is null) return;

        string[] stayers = { "Show orbs", "Reset all sessions to idle", "Pair phone…", "New chat…", "Settings…", $"Quit {Brand.DisplayName}" };

        foreach (var sessions in new[]
        {
            Array.Empty<TrayController.SessionEntry>(),
            new[] { Entry("id-1", title: "first") },
        })
        {
            tray.Update(sessions);
            var top = TopLabels(MenuOf(tray));
            var agentsAt = top.FindIndex(h => h.StartsWith("Agents"));
            Assert.Equal(0, agentsAt);
            foreach (var label in stayers)
            {
                Assert.Contains(label, top);
                Assert.True(top.IndexOf(label) > agentsAt, label);
            }
            // Quit stays last.
            Assert.Equal($"Quit {Brand.DisplayName}", top[^1]);
        }
    }

    [AvaloniaFact]
    public void GoingFromSessionsBackToNoneDropsTheSubmenu()
    {
        var tray = NewController();
        if (tray is null) return;

        tray.Update(new[] { Entry("id-1", title: "first") });
        Assert.NotNull(Agents(MenuOf(tray)).Menu);

        tray.Update(Array.Empty<TrayController.SessionEntry>());
        var agents = Agents(MenuOf(tray));
        Assert.Equal("Agents", agents.Header);
        Assert.Null(agents.Menu);
        Assert.False(agents.IsEnabled);
    }
}
