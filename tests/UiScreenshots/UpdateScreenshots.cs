using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Xunit;

namespace Orbweaver.Tests;

// CB-308's surfaces, captured through real Skia (UiTests' null renderer cannot
// say whether release notes read as notes or as markup). Never presented through
// the real Present(): the constructors are internal and called directly, and the
// installer is a fake, so nothing downloads, stages or exits.
//
// In the Settings collection because the Settings group and the tray read the
// process-wide settings static.
[Collection("Settings")]
public class UpdateScreenshots : IDisposable
{
    public UpdateScreenshots()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cb308-screens-" + Guid.NewGuid());
        System.IO.Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
        UpdateNoticeWindow.ClearFailureMarkerForTests = (_, _) => { };
    }

    public void Dispose()
    {
        UpdateNoticeWindow.ClearFailureMarkerForTests = null;
        UpdateMenu.PendingForTests = null;
    }

    // The real v1.0.3 release body, the same text UpdateNoticeWindowTests pins.
    private const string Notes = UpdateReleaseNotesFixture.V103;

    private static UpdateRelease Release() =>
        new("v1.0.3", "1.0.3", false, Notes, Array.Empty<UpdateAsset>());

    private sealed class FakeInstaller : IUpdateInstaller
    {
        public Func<IProgress<UpdateProgress>, Task<InstallOutcome>> Run =
            _ => Task.FromResult(new InstallOutcome(InstallStatus.HandedOff, "ok"));

        public Task<InstallOutcome> InstallAsync(UpdateRelease release, IProgress<UpdateProgress> progress, CancellationToken ct) =>
            Run(progress);
    }

    private static UpdateNoticeWindow Notice(FakeInstaller installer) =>
        new(Release(), installer, _ => { }, _ => { });

    [AvaloniaFact]
    public void NoticeOffer()
    {
        ScreenshotHelper.Capture(Notice(new FakeInstaller()), "update-notice-offer.png");
    }

    [AvaloniaFact]
    public async Task NoticeDownloading()
    {
        var gate = new TaskCompletionSource<InstallOutcome>();
        var installer = new FakeInstaller
        {
            Run = progress =>
            {
                progress.Report(new UpdateProgress(UpdateStage.Downloading, 61_000_000, 142_000_000));
                return gate.Task;
            }
        };
        var window = Notice(installer);
        window.Show();

        var install = window.InstallAsync();
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(window, "update-notice-downloading.png");

        gate.SetResult(new InstallOutcome(InstallStatus.HandedOff, "ok"));
        await install;
    }

    [AvaloniaFact]
    public async Task NoticeFailure()
    {
        // A platform-neutral failure whose text comes from the real source: the
        // downloader's own exception for a server that cannot be reached (a
        // loopback port nothing listens on), not a hand-typed sentence.
        string message;
        try
        {
            using var http = new HttpUpdateHttp();
            await http.GetStringAsync("http://127.0.0.1:1/SHA256SUMS.txt", CancellationToken.None);
            throw new InvalidOperationException("the unreachable server answered");
        }
        catch (UpdateException ex)
        {
            Assert.Equal(UpdateFailure.Network, ex.Kind);
            message = ex.Message;
        }

        var installer = new FakeInstaller
        {
            Run = _ => Task.FromResult(new InstallOutcome(InstallStatus.Failed, message))
        };
        var window = Notice(installer);
        window.Show();

        await window.InstallAsync();
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(window, "update-notice-failure.png");
    }

    [AvaloniaFact]
    public void PostInstallNoticeWithTheLocalNetworkButton()
    {
        ScreenshotHelper.Capture(
            new UpdateMessageWindow($"{Brand.DisplayName} was updated", UpdatePostInstallNotice.Sentence, true, () => { }),
            "update-post-install-notice.png");
    }

    [AvaloniaFact]
    public void PostInstallNoticeWithoutTheButton()
    {
        ScreenshotHelper.Capture(
            new UpdateMessageWindow($"{Brand.DisplayName} was updated", UpdatePostInstallNotice.Sentence, false, () => { }),
            "update-post-install-notice-no-button.png");
    }

    [AvaloniaFact]
    public void SettingsUpdatesGroup()
    {
        OrbweaverSettings.SkippedUpdateVersion = "0.5.12-beta";
        OrbweaverSettings.LastUpdateCheckUtc = DateTimeOffset.UtcNow.AddHours(-3);

        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");
        var window = (SettingsWindow)ctor.Invoke(null);

        window.Show();
        ScreenshotHelper.Flush();

        // The group by content, not by measuring an ancestor: anchored on a row,
        // climbed to the nearest control that also carries the "Updates" heading,
        // and asserted to still contain it, the same discipline as
        // SettingsWindowScreenshots.CaptureGroup (private there).
        var anchor = window.GetLogicalDescendants().OfType<TextBlock>()
            .First(t => t.Text == "Check for updates automatically");
        var group = anchor.GetLogicalAncestors().OfType<Control>()
            .First(c => c.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "Updates"));
        Assert.True(group.Bounds.Height > 150 && group.Bounds.Width > 200,
            $"capture target measured {group.Bounds.Width}x{group.Bounds.Height}");

        ScreenshotHelper.CaptureControl(group, "settings-updates-group.png");
    }

    // A NativeMenu cannot be rendered, so this is a mirror: the real labels, read
    // off a real TrayController's menu, drawn in a card. It shows what the menu
    // says in each state, not how macOS or Windows chrome it.
    [AvaloniaFact]
    public void TrayMenuMirror()
    {
        TrayController tray;
        try { tray = new TrayController(); }
        catch { return; }

        var menu = (NativeMenu)typeof(TrayController)
            .GetField("_menu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tray)!;

        StackPanel Column(string heading)
        {
            var column = new StackPanel { Margin = new Thickness(12), Spacing = 3, MinWidth = 230 };
            column.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
            foreach (var item in menu.Items)
            {
                column.Children.Add(item is NativeMenuItem native && item is not NativeMenuItemSeparator
                    ? new TextBlock { Text = native.Header, FontSize = 13 }
                    : new Border { Height = 1, Background = Brushes.Gray, Margin = new Thickness(0, 3) });
            }
            return column;
        }

        UpdateMenu.PendingForTests = () => null;
        tray.Refresh();
        var nothing = Column("Nothing pending");

        UpdateMenu.PendingForTests = () => Release();
        tray.Update(Array.Empty<TrayController.SessionEntry>());
        var pending = Column("Update waiting");

        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { nothing, pending } };
        var window = new Window { Width = 520, SizeToContent = SizeToContent.Height, Content = row };
        window.Show();
        ScreenshotHelper.Flush();

        ScreenshotHelper.CaptureControl(row, "update-tray-menu-mirror.png");
    }
}
