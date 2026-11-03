using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Xunit;

namespace Orbweaver.Tests;

// CB-298: the muted tray icons next to the ones they replace. Rendered through
// real Skia, which is the point: whether a slash reads as a slash at menu-bar
// size is a question about pixels, and the real menu-bar rendering is still a
// look on the three machines. Each icon is drawn at 4x on a mid-grey card so
// the ring, the gap and the bar are all visible without a light or dark
// menu bar's own contrast deciding it.
public class TrayIconMutedScreenshots
{
    private static readonly string[] States = { "idle", "generating", "waiting" };

    private static Image Icon(string asset) => new()
    {
        Width = 64 * 4 / 2,
        Height = 64 * 4 / 2,
        Margin = new Thickness(6),
        Source = new Bitmap(AssetLoader.Open(
            new Uri($"avares://{Brand.AssemblyName}/Assets/{asset}.png")))
    };

    [AvaloniaFact]
    public void MutedAndUnmutedTrayIconsForEveryState()
    {
        var rows = new StackPanel { Background = new SolidColorBrush(Color.Parse("#808080")) };
        foreach (var muted in new[] { false, true })
        {
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            foreach (var state in States) row.Children.Add(Icon(TrayIconLook.AssetName(state, muted)));
            rows.Children.Add(row);
        }

        var window = new Window { Width = 3 * 140, Height = 2 * 140, Content = rows };
        window.Show();
        ScreenshotHelper.Flush();

        ScreenshotHelper.CaptureControl(rows, "tray-icons-muted-vs-unmuted.png");
    }

    // The runtime re-tint has to work on the muted mask, for a customised
    // colour, or a user who recoloured their idle icon loses it on mute.
    [AvaloniaFact]
    public void TheMutedIconStillReTintsToACustomColour()
    {
        OrbColors.Set("idle", "#FF00FF");
        try
        {
            Assert.NotNull(TrayController.Tinted("idle", muted: true));
        }
        finally { OrbColors.Set("idle", null); }
    }
}
