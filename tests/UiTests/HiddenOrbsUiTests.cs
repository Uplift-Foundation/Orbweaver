using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Xunit;

namespace Orbweaver.Tests;

// CB-276, the half of "Hide this orb" a user touches: the right-click item, the
// Dismiss relabel, and Settings → Hidden orbs with its Restore buttons.
//
// The rules (what is hidden, what a lead takes with it, when an entry is pruned)
// are HiddenOrbsTests; the file round trip is HiddenOrbsSettingsTests; the scan
// removing the window is HiddenOrbScanTests. What is left, and only reachable
// here, is the menu and the list doing what those say when clicked.
//
// [Collection("Settings")]: the list is a process-wide setting, and
// constructing an OrbWindow reads a colour setting in a field initializer.
[Collection("Settings")]
public class HiddenOrbsUiTests : IDisposable
{
    private static readonly PropertyInfo InstanceProperty =
        typeof(SessionManager).GetProperty("Instance")!;

    private readonly string _dir = Directory.CreateTempSubdirectory("hidden-ui-").FullName;

    public HiddenOrbsUiTests() => OrbweaverSettings.RestoreAllHiddenOrbs();

    public void Dispose()
    {
        InstanceProperty.SetValue(null, null);
        OrbweaverSettings.RestoreAllHiddenOrbs();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static HiddenOrb Entry(string id, string title = "fix the tray", string source = "Claude Code",
        string detail = "~/Source/Orbweaver", DateTime? at = null) =>
        new(id, at ?? DateTime.UtcNow.AddHours(-2), title, source, detail);

    private static SettingsWindow NewSettings() =>
        (SettingsWindow)typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)!.Invoke(null);

    private static IEnumerable<string> Texts(IEnumerable<Control> rows) =>
        rows.SelectMany(r => r.GetLogicalDescendants().OfType<TextBlock>()).Select(t => t.Text ?? "");

    private static T Named<T>(IEnumerable<Control> rows, string name) where T : Control =>
        rows.SelectMany(r => r.GetLogicalDescendants().OfType<T>()).Single(c => c.Name == name);

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static OrbWindow Orb(string key = "s1")
    {
        var orb = new OrbWindow(key);
        orb.UpdateFrom(new SessionStatus { State = "idle", Cwd = "/x" });
        return orb;
    }

    // --- the orb's menu ---

    [AvaloniaFact]
    public void EveryOrbOffersHideAboveDismissWithTheNewWording()
    {
        var orb = Orb();
        var hide = orb.FindControl<MenuItem>("HideItem")!;
        var dismiss = orb.FindControl<MenuItem>("DismissItem")!;

        Assert.True(hide.IsVisible);
        Assert.Equal("Hide this orb", hide.Header);
        Assert.Equal("Dismiss until next activity", dismiss.Header);

        var items = ((ContextMenu)orb.FindControl<Grid>("Root")!.ContextMenu!).Items.OfType<MenuItem>().ToList();
        Assert.True(items.IndexOf(hide) < items.IndexOf(dismiss));
    }

    [AvaloniaTheory]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.ClaudeCloud)]
    [InlineData(SessionSource.RemoteControl)]
    public void HideIsOfferedForNonLocalSourcesToo(SessionSource source)
    {
        var orb = new OrbWindow("x:1");
        orb.UpdateFrom(new SessionStatus { Source = source, State = "idle", Title = "t" });

        Assert.True(orb.FindControl<MenuItem>("HideItem")!.IsVisible);
    }

    [AvaloniaFact]
    public void ANonLeadSaysHideThisOrbOnOpening()
    {
        var orb = Orb();
        orb.SessionMenu_Opening(null, new System.ComponentModel.CancelEventArgs());

        Assert.Equal("Hide this orb", orb.FindControl<MenuItem>("HideItem")!.Header);
    }

    [AvaloniaFact]
    public void ALeadSaysItTakesItsTeamOnOpening()
    {
        var manager = new SessionManager(_dir);
        InstanceProperty.SetValue(null, manager);
        var statuses = (Dictionary<string, SessionStatus>)typeof(SessionManager)
            .GetField("_statuses", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
        statuses["lead"] = new SessionStatus { Source = SessionSource.RemoteControl, State = "idle" };
        statuses["member"] = new SessionStatus { Source = SessionSource.RemoteControl, State = "idle", Lead = "lead" };

        var lead = Orb("lead");
        lead.SessionMenu_Opening(null, new System.ComponentModel.CancelEventArgs());
        var member = Orb("member");
        member.SessionMenu_Opening(null, new System.ComponentModel.CancelEventArgs());

        Assert.Equal("Hide this orb and its team", lead.FindControl<MenuItem>("HideItem")!.Header);
        Assert.Equal("Hide this orb", member.FindControl<MenuItem>("HideItem")!.Header);
    }

    [AvaloniaFact]
    public void ClickingHideAsksTheManagerToHideThatSession()
    {
        var manager = new SessionManager(_dir);
        InstanceProperty.SetValue(null, manager);
        var statuses = (Dictionary<string, SessionStatus>)typeof(SessionManager)
            .GetField("_statuses", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
        statuses["s1"] = new SessionStatus { State = "idle", Title = "fix the tray", Cwd = "/x" };

        var orb = Orb("s1");
        orb.FindControl<MenuItem>("HideItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        var entry = Assert.Single(OrbweaverSettings.HiddenOrbList());
        Assert.Equal("s1", entry.SessionId);
        Assert.Equal("fix the tray", entry.Title);
    }

    [AvaloniaFact]
    public void ClickingHideWithNoManagerHidesNothing()
    {
        Orb().FindControl<MenuItem>("HideItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Empty(OrbweaverSettings.HiddenOrbList());
    }

    // --- Settings → Hidden orbs ---

    [AvaloniaFact]
    public void AnEmptyListSaysSoAndOffersNoRestoreAll()
    {
        var rows = NewSettings().HiddenOrbsRows();

        Assert.Contains("No hidden orbs", Texts(rows));
        Assert.DoesNotContain(rows.SelectMany(r => r.GetLogicalDescendants().OfType<Button>()),
            b => b.Name == "RestoreAllHiddenOrbs");
    }

    [AvaloniaFact]
    public void APopulatedListShowsTitleBadgeDetailAndWhenPerRow()
    {
        OrbweaverSettings.HideOrb(Entry("a", "fix the tray", "Claude Code", "~/Source/Orbweaver"));
        OrbweaverSettings.HideOrb(Entry("b", "ops room", "OpenClaw room", "main", DateTime.MinValue));

        var rows = NewSettings().HiddenOrbsRows();
        var texts = Texts(rows).ToList();

        Assert.Contains("fix the tray", texts);
        Assert.Contains("Claude Code", texts);
        Assert.Contains("~/Source/Orbweaver · hidden 2 h ago", texts);
        Assert.Contains("OpenClaw room", texts);
        // An unknown timestamp drops the when text, not the detail.
        Assert.Contains("main", texts);
        Assert.NotNull(Named<Button>(rows, "RestoreHiddenOrb:a"));
        Assert.NotNull(Named<Button>(rows, "RestoreAllHiddenOrbs"));
    }

    [AvaloniaFact]
    public void ARowWithNoDetailShowsJustTheWhenText()
    {
        OrbweaverSettings.HideOrb(Entry("a", detail: ""));

        Assert.Contains("hidden 2 h ago", Texts(NewSettings().HiddenOrbsRows()));
    }

    [AvaloniaFact]
    public void RestoreRemovesThatRowAndItsEntryOnly()
    {
        OrbweaverSettings.HideOrb(Entry("a", "one"));
        OrbweaverSettings.HideOrb(Entry("b", "two"));
        var settings = NewSettings();

        Click(Named<Button>(settings.HiddenOrbsRows(), "RestoreHiddenOrb:a"));

        Assert.Equal(new[] { "b" }, OrbweaverSettings.HiddenOrbList().Select(o => o.SessionId));
    }

    [AvaloniaFact]
    public void RestoreAllClearsTheList()
    {
        OrbweaverSettings.HideOrb(Entry("a"));
        OrbweaverSettings.HideOrb(Entry("b"));

        Click(Named<Button>(NewSettings().HiddenOrbsRows(), "RestoreAllHiddenOrbs"));

        Assert.Empty(OrbweaverSettings.HiddenOrbList());
    }

    [AvaloniaFact]
    public void RestoreGoesThroughTheManagerWhenOneIsRunning()
    {
        InstanceProperty.SetValue(null, new SessionManager(_dir));
        OrbweaverSettings.HideOrb(Entry("a"));
        OrbweaverSettings.HideOrb(Entry("b"));
        var settings = NewSettings();

        Click(Named<Button>(settings.HiddenOrbsRows(), "RestoreHiddenOrb:a"));
        Assert.Equal(new[] { "b" }, OrbweaverSettings.HiddenOrbList().Select(o => o.SessionId));

        Click(Named<Button>(settings.HiddenOrbsRows(), "RestoreAllHiddenOrbs"));
        Assert.Empty(OrbweaverSettings.HiddenOrbList());
    }

    [AvaloniaFact]
    public void TheFilterFindsARowByTitleSourceOrDetail()
    {
        OrbweaverSettings.HideOrb(Entry("a", "fix the tray", "Claude Code", "~/Source/Orbweaver"));
        var row = (SettingsWindow.SettingsRow)NewSettings().HiddenOrbsRows()[0];

        Assert.Contains("hidden", row.SearchText);
        Assert.Contains("fix the tray", row.SearchText);
        Assert.Contains("Orbweaver", row.SearchText);
    }

    [AvaloniaFact]
    public void AnOpenWindowRebuildsWhenTheListChangesUnderIt()
    {
        var settings = NewSettings();
        settings.HiddenOrbsRows();

        // No change: the tick leaves the window alone.
        settings.OnStatusTick(null, EventArgs.Empty);

        OrbweaverSettings.HideOrb(Entry("late"));
        settings.OnStatusTick(null, EventArgs.Empty);

        Assert.Contains("fix the tray", Texts(settings.HiddenOrbsRows()));
    }
}
