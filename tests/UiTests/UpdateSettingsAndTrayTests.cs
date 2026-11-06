using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Xunit;

namespace Orbweaver.Tests;

// CB-308's two always-there surfaces: the Settings "Updates" group and the tray
// item. In the Settings collection because both read and write the process-wide
// settings static (SettingsCollection.cs).
[Collection("Settings")]
public class UpdateSettingsAndTrayTests : IDisposable
{
    public void Dispose()
    {
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.AutoCheckForUpdates = true;
        OrbweaverSettings.IncludePrereleaseUpdates = false;
        OrbweaverSettings.SkippedUpdateVersion = null;
        UpdateMenu.PendingForTests = null;
        UpdateMenu.OfferForTests = null;
        UpdateMenu.CheckForTests = null;
        SettingsWindow.CheckNowForTests = null;
    }

    private static SettingsWindow NewWindow()
    {
        OrbweaverSettings.ReloadForTests();
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!;
        return (SettingsWindow)ctor.Invoke(null);
    }

    private static IEnumerable<T> Descendants<T>(Control root) where T : Control
    {
        if (root is T self) yield return self;

        var children = root switch
        {
            Panel panel => panel.Children.OfType<Control>(),
            Decorator decorator when decorator.Child is Control only => new[] { only },
            ContentControl holder when holder.Content is Control only => new[] { only },
            _ => Enumerable.Empty<Control>()
        };

        foreach (var child in children)
        foreach (var found in Descendants<T>(child))
            yield return found;
    }

    private static string AllText(IEnumerable<Control> rows) =>
        string.Join(" | ", rows.SelectMany(Descendants<TextBlock>).Select(t => t.Text));

    private static Button ButtonNamed(IEnumerable<Control> rows, string content) =>
        rows.SelectMany(Descendants<Button>).First(b => (b.Content as string) == content);

    // ---- the Settings group --------------------------------------------------

    [AvaloniaFact]
    public void TheGroupHasBothSwitchesTheCheckAndTheLastCheckedLine()
    {
        var window = NewWindow();
        var rows = window.UpdateRows();
        var text = AllText(rows);

        Assert.Contains("Check for updates automatically", text);
        Assert.Contains("Include prerelease versions", text);
        Assert.Contains("Check now", text);
        Assert.Equal("Not checked yet.", window.UpdateStatusText);
        Assert.Contains("nothing is contacted at all", text);
    }

    [AvaloniaFact]
    public void TheGroupIsRegisteredAsASettingsSection()
    {
        Assert.Contains("updates", NewWindow().Sections.Keys);
    }

    [AvaloniaFact]
    public void ASkippedVersionGetsAForgetRowAndNoSkipMeansNoRow()
    {
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.SkippedUpdateVersion = null;
        Assert.DoesNotContain("Skipped:", AllText(NewWindow().UpdateRows()));

        OrbweaverSettings.SkippedUpdateVersion = "0.6.0";
        var rows = NewWindow().UpdateRows();
        Assert.Contains("Skipped: 0.6.0", AllText(rows));
        Assert.NotNull(ButtonNamed(rows, "Forget"));
    }

    [AvaloniaFact]
    public void ForgetClearsTheSkippedVersion()
    {
        OrbweaverSettings.ReloadForTests();
        OrbweaverSettings.SkippedUpdateVersion = "0.6.0";
        var window = NewWindow();

        window.OnForgetSkippedUpdate();

        Assert.Null(OrbweaverSettings.SkippedUpdateVersion);
    }

    [AvaloniaFact]
    public void TheAutoCheckSwitchWritesTheSettingBothWays()
    {
        var window = NewWindow();

        window.OnAutoCheckToggled(false);
        Assert.False(OrbweaverSettings.AutoCheckForUpdates);

        window.OnAutoCheckToggled(true);
        Assert.True(OrbweaverSettings.AutoCheckForUpdates);
    }

    [AvaloniaFact]
    public void CheckNowWithNoSeamAndNoCheckerReportsFailureWithoutTouchingTheNetwork()
    {
        var window = NewWindow();
        // The real (unseamed) path: with no checker there is no network to reach,
        // so it answers Failed at once.
        var button = ButtonNamed(window.UpdateRows(), "Check now");
        UpdateServices.Checker = null;
        window.OnCheckNowAsync(button).GetAwaiter().GetResult();

        Assert.Contains("Could not check", window.UpdateStatusText);
    }

    [AvaloniaFact]
    public void ClickingCheckForUpdatesWithNoSeamsAsksTheServiceAndReachesNothing()
    {
        UpdateServices.Checker = null;
        UpdateMenu.PendingForTests = () => null;

        UpdateMenu.Activate();
    }

    [AvaloniaFact]
    public void ThePrereleaseSwitchWritesTheSettingBothWays()
    {
        var window = NewWindow();

        window.OnPrereleaseToggled(true);
        Assert.True(OrbweaverSettings.IncludePrereleaseUpdates);

        window.OnPrereleaseToggled(false);
        Assert.False(OrbweaverSettings.IncludePrereleaseUpdates);
    }

    [AvaloniaTheory]
    [InlineData((int)UpdateCheckOutcome.UpToDate, "up to date")]
    [InlineData((int)UpdateCheckOutcome.Failed, "Could not check: offline")]
    [InlineData((int)UpdateCheckOutcome.Available, "0.6.0 is available")]
    public async Task CheckNowWritesTheAnswerBesideTheButtonAndReEnablesIt(int outcome, string expected)
    {
        var window = NewWindow();
        var rows = window.UpdateRows();
        var button = ButtonNamed(rows, "Check now");
        var release = new UpdateRelease("v0.6.0", "0.6.0", false, "", Array.Empty<UpdateAsset>());
        SettingsWindow.CheckNowForTests = () => Task.FromResult(
            new UpdateCheckResult((UpdateCheckOutcome)outcome, release, "offline", true));

        await window.OnCheckNowAsync(button);

        Assert.Contains(expected, window.UpdateStatusText);
        Assert.True(button.IsEnabled);
    }

    [AvaloniaFact]
    public async Task CheckNowSaysCheckingWhileItWaitsAndDisablesTheButton()
    {
        var window = NewWindow();
        var button = ButtonNamed(window.UpdateRows(), "Check now");
        var gate = new TaskCompletionSource<UpdateCheckResult>();
        SettingsWindow.CheckNowForTests = () => gate.Task;

        var pending = window.OnCheckNowAsync(button);

        Assert.Equal("Checking…", window.UpdateStatusText);
        Assert.False(button.IsEnabled);

        gate.SetResult(new UpdateCheckResult(UpdateCheckOutcome.UpToDate, null, null, true));
        await pending;
    }

    [AvaloniaFact]
    public async Task ACheckThatThrowsIsReportedNotSwallowedOrCrashed()
    {
        var window = NewWindow();
        var button = ButtonNamed(window.UpdateRows(), "Check now");
        SettingsWindow.CheckNowForTests = () => throw new InvalidOperationException("kaput");

        await window.OnCheckNowAsync(button);

        Assert.Contains("kaput", window.UpdateStatusText);
        Assert.True(button.IsEnabled);
    }

    // ---- the tray item --------------------------------------------------------

    private static TrayController? NewTray()
    {
        try { return new TrayController(); }
        catch { return null; }
    }

    private static List<string> Labels(TrayController tray)
    {
        var menu = (NativeMenu)typeof(TrayController)
            .GetField("_menu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tray)!;
        return menu.Items.OfType<NativeMenuItem>().Select(i => i.Header ?? "").ToList();
    }

    private static NativeMenuItem Item(TrayController tray, string header)
    {
        var menu = (NativeMenu)typeof(TrayController)
            .GetField("_menu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tray)!;
        return menu.Items.OfType<NativeMenuItem>().First(i => i.Header == header);
    }

    // Raises the item's Click event through its backing field: the platform's own
    // dispatch is not reachable headless. The handlers are the two UpdateMenu
    // seams, so nothing real runs.
    private static void Click(NativeMenuItem item)
    {
        var field = typeof(NativeMenuItem).GetField("Click", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((EventHandler?)field.GetValue(item))!.Invoke(item, EventArgs.Empty);
    }

    private static UpdateRelease Release() =>
        new("v0.6.0", "0.6.0", false, "", Array.Empty<UpdateAsset>());

    [AvaloniaFact]
    public void WithNothingPendingTheItemChecksAndSitsAboveSettings()
    {
        var tray = NewTray();
        if (tray is null) return;
        UpdateMenu.PendingForTests = () => null;
        tray.Refresh();

        var labels = Labels(tray);

        Assert.Contains("Check for updates…", labels);
        Assert.True(labels.IndexOf("Check for updates…") < labels.IndexOf("Settings…"));
    }

    [AvaloniaFact]
    public void ANewlyPendingUpdateChangesTheLabelWithoutAnythingElseChanging()
    {
        // The signature gate: Update() is called every two seconds and returns
        // early on an unchanged signature, so unless the pending version is in
        // the signature the label would never leave "Check for updates…".
        var tray = NewTray();
        if (tray is null) return;
        UpdateMenu.PendingForTests = () => null;
        tray.Update(Array.Empty<TrayController.SessionEntry>());
        Assert.Contains("Check for updates…", Labels(tray));

        UpdateMenu.PendingForTests = () => Release();
        tray.Update(Array.Empty<TrayController.SessionEntry>());

        var labels = Labels(tray);
        Assert.Contains("Update to 0.6.0 available…", labels);
        Assert.DoesNotContain("Check for updates…", labels);

        // And back when it is cleared (the user skipped it).
        UpdateMenu.PendingForTests = () => null;
        tray.Update(Array.Empty<TrayController.SessionEntry>());
        Assert.Contains("Check for updates…", Labels(tray));
    }

    [AvaloniaFact]
    public void ClickingTheItemChecksWhenNothingIsPendingAndOffersWhenSomethingIs()
    {
        var tray = NewTray();
        if (tray is null) return;
        var calls = new List<string>();
        UpdateMenu.CheckForTests = () => calls.Add("check");
        UpdateMenu.OfferForTests = r => calls.Add("offer:" + r.Version);

        UpdateMenu.PendingForTests = () => null;
        tray.Refresh();
        Click(Item(tray, "Check for updates…"));

        UpdateMenu.PendingForTests = () => Release();
        tray.Update(Array.Empty<TrayController.SessionEntry>());
        Click(Item(tray, "Update to 0.6.0 available…"));

        Assert.Equal(new[] { "check", "offer:0.6.0" }, calls);
    }
}
