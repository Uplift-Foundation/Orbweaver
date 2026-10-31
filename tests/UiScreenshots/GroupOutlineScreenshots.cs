using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace Orbweaver.Tests;

// CB-268 S6's two visible surfaces: the Settings "Group outlines" card and the
// per-group style dialog. Captured through real Skia like every other scenario
// here, with the settings they set restored in Dispose.
//
// The Group submenu is not captured: it is a popup menu, which the headless
// platform has no window to present, and the control tests in
// tests/UiTests/OrbGroupSubmenuTests assert its items instead.
[Collection("Settings")]
public class GroupOutlineScreenshots : IDisposable
{
    private readonly bool _enabled = OrbweaverSettings.GroupOutlinesEnabled;
    private readonly string _stroke = OrbweaverSettings.GroupOutlineStroke;
    private readonly double _strokeOpacity = OrbweaverSettings.GroupOutlineStrokeOpacity;
    private readonly string _fill = OrbweaverSettings.GroupOutlineFill;
    private readonly double _fillOpacity = OrbweaverSettings.GroupOutlineFillOpacity;
    private readonly List<string> _groups = new();

    public void Dispose()
    {
        foreach (var id in _groups) OrbweaverSettings.DeleteOrbGroup(id);
        OrbweaverSettings.GroupOutlinesEnabled = _enabled;
        OrbweaverSettings.GroupOutlineStroke = _stroke;
        OrbweaverSettings.GroupOutlineStrokeOpacity = _strokeOpacity;
        OrbweaverSettings.GroupOutlineFill = _fill;
        OrbweaverSettings.GroupOutlineFillOpacity = _fillOpacity;
    }

    private void GlobalStyle()
    {
        OrbweaverSettings.GroupOutlinesEnabled = true;
        OrbweaverSettings.GroupOutlineStroke = "#58A6FF";
        OrbweaverSettings.GroupOutlineStrokeOpacity = 0.6;
        OrbweaverSettings.GroupOutlineFill = "#58A6FF";
        OrbweaverSettings.GroupOutlineFillOpacity = 0.12;
    }

    [AvaloniaFact]
    public void TheGroupOutlinesSettingsCard()
    {
        GlobalStyle();
        var window = (Window)typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)!.Invoke(null);
        window.Show();
        ScreenshotHelper.Flush();

        var heading = window.GetLogicalDescendants().OfType<TextBlock>()
            .First(block => block.Text == "Group outlines");
        var section = heading.GetLogicalAncestors().OfType<SettingsWindow.SettingsSection>().First();

        ScreenshotHelper.CaptureControl(section, "settings-group-outlines-card.png");
    }

    [AvaloniaFact]
    public void TheGroupStyleWindowWithAnOverride()
    {
        GlobalStyle();
        var group = OrbweaverSettings.CreateOrbGroup("heart");
        _groups.Add(group.Id);
        OrbweaverSettings.RenameOrbGroup(group.Id, "Reviewers");
        OrbweaverSettings.SetOrbGroupStyle(group.Id, new GroupStyle("#FF7B72", 0.9, null, 0.25));

        var window = new GroupStyleWindow(group.Id, focusName: false);

        ScreenshotHelper.Capture(window, "group-style-window-override.png");
    }

    [AvaloniaFact]
    public void TheGroupStyleWindowUsingTheDefault()
    {
        GlobalStyle();
        var group = OrbweaverSettings.CreateOrbGroup("circle");
        _groups.Add(group.Id);

        var window = new GroupStyleWindow(group.Id, focusName: false);

        ScreenshotHelper.Capture(window, "group-style-window-default.png");
    }
}
