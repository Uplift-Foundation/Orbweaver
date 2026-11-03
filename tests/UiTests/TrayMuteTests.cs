using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-298 on the tray: the "Mute" checkbox, the icon and the hover text all
// follow the one flag, however it got changed. As in TrayMenuTests the item's
// Click is never invoked — ToggleMute, which it calls, is, directly.
[Collection("Settings")]
public class TrayMuteTests : IDisposable
{
    private readonly ScopedSettingsDir _dir = new("traymute");

    public void Dispose()
    {
        AudioMute.Set(false);
        _dir.Dispose();
    }

    private static TrayController? NewController()
    {
        try { return new TrayController(); }
        catch { return null; }   // as TrayMenuTests: a TrayIcon may not construct headless
    }

    private static NativeMenu MenuOf(TrayController tray) =>
        (NativeMenu)typeof(TrayController)
            .GetField("_menu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tray)!;

    private static TrayIcon IconOf(TrayController tray) =>
        (TrayIcon)typeof(TrayController)
            .GetField("_tray", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tray)!;

    private static IEnumerable<string> IconKeys(TrayController tray) =>
        ((Dictionary<string, Avalonia.Controls.WindowIcon>)typeof(TrayController)
            .GetField("_iconCache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tray)!).Keys;

    private static NativeMenuItem Mute(TrayController tray) =>
        MenuOf(tray).Items.OfType<NativeMenuItem>().Single(i => i.Header == "Mute");

    [AvaloniaFact]
    public void TheMenuHasAMuteCheckboxThatMirrorsTheFlag()
    {
        var tray = NewController();
        if (tray is null) return;

        var item = Mute(tray);
        Assert.Equal(MenuItemToggleType.CheckBox, item.ToggleType);
        Assert.False(item.IsChecked);

        TrayController.ToggleMute();

        Assert.True(AudioMute.IsMuted);
        Assert.True(Mute(tray).IsChecked);

        TrayController.ToggleMute();
        Assert.False(Mute(tray).IsChecked);
    }

    [AvaloniaFact]
    public void TheIconAndTheHoverTextFollowTheMute()
    {
        var tray = NewController();
        if (tray is null) return;
        Assert.DoesNotContain("tray-idle-muted", IconKeys(tray));

        AudioMute.Set(true);

        Assert.Contains("tray-idle-muted", IconKeys(tray));
        Assert.EndsWith(", muted", IconOf(tray).ToolTipText);

        AudioMute.Set(false);
        Assert.DoesNotContain(", muted", IconOf(tray).ToolTipText);
    }

    // The icon is the urgent half and is never held back, even with the menu
    // open; the menu is, and catches up on close.
    [AvaloniaFact]
    public void AMuteWhileTheMenuIsOpenFlipsTheIconNowAndTheCheckboxOnClose()
    {
        var tray = NewController();
        if (tray is null) return;
        var menu = MenuOf(tray);
        if (!Raise(menu, "RaiseOpening")) return;

        AudioMute.Set(true);

        Assert.Contains("tray-idle-muted", IconKeys(tray));
        Assert.False(Mute(tray).IsChecked);   // the menu somebody is looking at is left alone

        if (!Raise(menu, "RaiseClosed")) return;
        Assert.True(Mute(tray).IsChecked);
    }

    [AvaloniaFact]
    public void TheMutedIconKeepsWorkingWithACustomisedStateColour()
    {
        OrbColors.Set("idle", "#FF00FF");
        try
        {
            Assert.NotNull(TrayController.Tinted("idle", muted: true));
            Assert.NotNull(TrayController.Tinted("idle"));
        }
        finally { OrbColors.Set("idle", null); }
    }

    private static bool Raise(NativeMenu menu, string method)
    {
        var raise = typeof(NativeMenu)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name.EndsWith(method, StringComparison.Ordinal)
                                 && m.GetParameters().Length == 0);
        if (raise is null) return false;
        raise.Invoke(menu, null);
        return true;
    }
}
