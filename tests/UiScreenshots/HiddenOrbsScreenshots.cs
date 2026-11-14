using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace Orbweaver.Tests;

// CB-276: what "Hide this orb" looks like — the Settings → Hidden orbs rows
// populated (one per kind of source) and empty, and the orb's right-click menu
// with Hide above "Dismiss until next activity", for an ordinary orb and for a
// team lead, whose item says what else it takes.
//
// The rows are framed on their own, the way SettingsApproveRowScreenshots does
// it: the whole Settings window is far taller than any capture, and the group
// would sit below the fold of it. SizeToContent because the detail text wraps
// differently on each runner.
//
// [Collection("Settings")]: the list is a process-wide setting.
[Collection("Settings")]
public class HiddenOrbsScreenshots
{
    private static readonly PropertyInfo InstanceProperty =
        typeof(SessionManager).GetProperty("Instance")!;

    private static SettingsWindow NewSettings() =>
        (SettingsWindow)typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)!.Invoke(null);

    private static void CaptureRows(Control[] rows, string file)
    {
        var panel = new StackPanel { Width = 640, Margin = new Thickness(16) };
        foreach (var row in rows) panel.Children.Add(row);

        var window = new Window { Content = panel, SizeToContent = SizeToContent.WidthAndHeight };
        window.Show();
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(window, file);
    }

    [AvaloniaFact]
    public void HiddenOrbsPopulatedOneRowPerKindOfSource()
    {
        OrbweaverSettings.RestoreAllHiddenOrbs();
        try
        {
            var now = DateTime.UtcNow;
            OrbweaverSettings.HideOrb(new("a", now.AddMinutes(-5), "fix the tray", "Claude Code", "~/Source/Orbweaver"));
            OrbweaverSettings.HideOrb(new("b", now.AddHours(-3), "ops room", "OpenClaw room", "main"));
            OrbweaverSettings.HideOrb(new("c", now.AddDays(-2), "refactor the parser", "Claude cloud", "work account"));
            OrbweaverSettings.HideOrb(new("d", now.AddHours(-30), "nightly build", "Other machine", "mini", "mini"));

            var rows = NewSettings().HiddenOrbsRows();
            var texts = rows.SelectMany(r => r.GetLogicalDescendants().OfType<TextBlock>())
                .Select(t => t.Text ?? "").ToList();
            Assert.Contains("fix the tray", texts);
            Assert.Contains("OpenClaw room", texts);
            Assert.Contains(texts, t => t.Contains("hidden 2 days ago"));
            Assert.Equal(4, rows.SelectMany(r => r.GetLogicalDescendants().OfType<Button>())
                .Count(b => b.Name!.StartsWith("RestoreHiddenOrb:")));

            CaptureRows(rows, "settings-hidden-orbs-populated.png");
        }
        finally { OrbweaverSettings.RestoreAllHiddenOrbs(); }
    }

    [AvaloniaFact]
    public void HiddenOrbsEmpty()
    {
        OrbweaverSettings.RestoreAllHiddenOrbs();

        var rows = NewSettings().HiddenOrbsRows();
        Assert.Contains("No hidden orbs", rows.SelectMany(r => r.GetLogicalDescendants().OfType<TextBlock>())
            .Select(t => t.Text));

        CaptureRows(rows, "settings-hidden-orbs-empty.png");
    }

    private static void CaptureMenu(OrbWindow orb, string fileName)
    {
        orb.Show();
        orb.Width = 360;
        orb.Height = 560;
        ScreenshotHelper.Flush();

        var root = orb.FindControl<Grid>("Root")!;
        var menu = root.ContextMenu!;
        menu.Open(root);
        ScreenshotHelper.Flush();
        try { ScreenshotHelper.CaptureControl(menu, fileName); }
        finally { menu.Close(); }
    }

    private static OrbWindow Orb(string key)
    {
        var orb = new OrbWindow(key);
        orb.UpdateFrom(new SessionStatus { State = "idle", Title = "fix the tray", Cwd = "/Users/user/Source/Orbweaver" });
        return orb;
    }

    [AvaloniaFact]
    public void TheOrbMenuOffersHideAboveDismiss()
    {
        var orb = Orb("s1");
        Assert.Equal("Dismiss until next activity", orb.FindControl<MenuItem>("DismissItem")!.Header);

        CaptureMenu(orb, "orb-menu-hide.png");
    }

    [AvaloniaFact]
    public void ALeadsMenuSaysItHidesItsTeamToo()
    {
        var dir = Directory.CreateTempSubdirectory("hidden-shot-").FullName;
        try
        {
            var manager = new SessionManager(dir);
            InstanceProperty.SetValue(null, manager);
            var statuses = (Dictionary<string, SessionStatus>)typeof(SessionManager)
                .GetField("_statuses", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
            statuses["lead"] = new SessionStatus { Source = SessionSource.RemoteControl, State = "idle" };
            statuses["member"] = new SessionStatus { Source = SessionSource.RemoteControl, State = "idle", Lead = "lead" };

            var orb = Orb("lead");
            orb.SessionMenu_Opening(null, new System.ComponentModel.CancelEventArgs());
            Assert.Equal("Hide this orb and its team", orb.FindControl<MenuItem>("HideItem")!.Header);

            CaptureMenu(orb, "orb-menu-hide-team.png");
        }
        finally
        {
            InstanceProperty.SetValue(null, null);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
