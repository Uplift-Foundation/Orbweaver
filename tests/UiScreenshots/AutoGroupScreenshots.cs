using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace Orbweaver.Tests;

// CB-281's visible surface in Settings: the "Auto-group orbs by" row at the top
// of the Group outlines card, captured through real Skia with the setting on
// CLI so the picker shows a chosen value rather than Off. The grouped orbs
// themselves are captured in AutoGroupOrbScreenshots.
[Collection("Settings")]
public class AutoGroupScreenshots : IDisposable
{
    private readonly AutoGroupBy _by = OrbweaverSettings.AutoGroupBy;
    private readonly bool _enabled = OrbweaverSettings.GroupOutlinesEnabled;
    private readonly bool _names = OrbweaverSettings.GroupNamesVisible;

    public void Dispose()
    {
        OrbweaverSettings.AutoGroupBy = _by;
        OrbweaverSettings.GroupOutlinesEnabled = _enabled;
        OrbweaverSettings.GroupNamesVisible = _names;
    }

    [AvaloniaFact]
    public void TheAutoGroupRowInTheGroupOutlinesCard()
    {
        OrbweaverSettings.AutoGroupBy = AutoGroupBy.Cli;
        OrbweaverSettings.GroupOutlinesEnabled = true;
        OrbweaverSettings.GroupNamesVisible = true;
        var window = (Window)typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)!.Invoke(null);
        window.Show();
        ScreenshotHelper.Flush();

        var heading = window.GetLogicalDescendants().OfType<TextBlock>()
            .First(block => block.Text == "Group outlines");
        var section = heading.GetLogicalAncestors().OfType<SettingsWindow.SettingsSection>().First();

        ScreenshotHelper.CaptureControl(section, "settings-auto-group-card.png");
    }
}
