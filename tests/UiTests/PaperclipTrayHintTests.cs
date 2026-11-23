using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-421: the one-time "Paperclip found" row in the tray menu. As TrayMenuTests,
// the item's Click is never invoked (it opens Settings and marks the hint
// used); what is asserted is whether the row is there, and the two rules that
// decide it, which are pure and covered in PaperclipConnectorTests.
[Collection("Settings")]
public class PaperclipTrayHintTests : IDisposable
{
    private readonly bool _enabled = OrbweaverSettings.PaperclipEnabled;
    private readonly bool _shown = OrbweaverSettings.PaperclipHintShown;

    public void Dispose()
    {
        PaperclipHint.SetFoundForTests([]);
        OrbweaverSettings.PaperclipEnabled = _enabled;
        OrbweaverSettings.PaperclipHintShown = _shown;
    }

    private static TrayController? NewController()
    {
        try { return new TrayController(); }
        catch { return null; }   // a TrayIcon may not construct headless, as TrayMenuTests
    }

    private static IEnumerable<string?> Headers(TrayController tray) =>
        ((NativeMenu)typeof(TrayController).GetField("_menu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tray)!)
            .Items.OfType<NativeMenuItem>().Select(i => i.Header);

    private static readonly PaperclipDiscovery.Found Found = new("http://127.0.0.1:3100", "default", "authenticated");

    [AvaloniaFact]
    public void TheRowIsInTheMenu_WhenAPaperclipWasFoundAndNotYetUsed()
    {
        var tray = NewController();
        if (tray is null) return;
        OrbweaverSettings.PaperclipEnabled = false;
        OrbweaverSettings.PaperclipHintShown = false;
        PaperclipHint.SetFoundForTests([Found]);

        tray.Update([]);

        Assert.Contains(PaperclipHint.Label, Headers(tray));
    }

    [AvaloniaFact]
    public void TheRowIsGone_OnceUsed_OnceConnected_OrWhenNothingWasFound()
    {
        var tray = NewController();
        if (tray is null) return;
        OrbweaverSettings.PaperclipEnabled = false;
        OrbweaverSettings.PaperclipHintShown = false;

        PaperclipHint.SetFoundForTests([]);
        tray.Update([]);
        Assert.DoesNotContain(PaperclipHint.Label, Headers(tray));          // nothing found

        PaperclipHint.SetFoundForTests([Found]);
        tray.Update([]);
        Assert.Contains(PaperclipHint.Label, Headers(tray));

        PaperclipHint.MarkUsed();
        tray.Update([]);
        Assert.DoesNotContain(PaperclipHint.Label, Headers(tray));          // used

        OrbweaverSettings.PaperclipHintShown = false;
        OrbweaverSettings.PaperclipEnabled = true;
        tray.Update([]);
        Assert.DoesNotContain(PaperclipHint.Label, Headers(tray));          // already connected
    }
}
