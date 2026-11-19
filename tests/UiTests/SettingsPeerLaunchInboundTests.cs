using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-357: "Allow other desktops to start sessions here", in the peer link card.
//
// What matters is that the row exists only where it means something (under a
// link that is switched on), that it starts off, and that its control writes
// this setting and no other. The control is found as a ToggleButton because
// the row's switch is a ToggleSwitch or, when the Fluent template could not be
// borrowed, a CheckBox — both are ToggleButtons, and which one it is depends on
// the theme rather than on this setting.
[Collection("Settings")]
public class SettingsPeerLaunchInboundTests : IDisposable
{
    private readonly ScopedSettingsDir _settings = new("peer-launch-inbound");

    public void Dispose() => _settings.Dispose();

    private static SettingsWindow NewWindow(bool link)
    {
        OrbweaverSettings.PeerLinkEnabled = link;
        var ctor = typeof(SettingsWindow).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");
        return (SettingsWindow)ctor.Invoke(null);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        yield return root;
        var children = root switch
        {
            Panel panel => panel.Children.OfType<Control>(),
            Decorator { Child: Control only } => [only],
            ContentControl { Content: Control only } => [only],
            _ => Enumerable.Empty<Control>()
        };
        foreach (var child in children)
        foreach (var found in Descendants(child))
            yield return found;
    }

    private static Control? LaunchRow(SettingsWindow window) =>
        window.PeerLinkRows().FirstOrDefault(r => Descendants(r).OfType<TextBlock>()
            .Any(t => t.Text == SettingsWindow.PeerLaunchInboundLabel));

    [AvaloniaFact]
    public void TheRowIsAbsentWhileThePeerLinkIsOff() =>
        Assert.Null(LaunchRow(NewWindow(link: false)));

    [AvaloniaFact]
    public void UnderTheLinkTheRowStartsOffAndItsSwitchWritesTheSetting()
    {
        var row = LaunchRow(NewWindow(link: true));
        Assert.NotNull(row);

        var toggle = Descendants(row!).OfType<ToggleButton>().Single();
        Assert.False(toggle.IsChecked);
        Assert.False(OrbweaverSettings.PeerLaunchInboundEnabled);

        toggle.IsChecked = true;
        Assert.True(OrbweaverSettings.PeerLaunchInboundEnabled);
        Assert.True(OrbweaverSettings.PeerLinkEnabled);   // the link's own switch is untouched

        toggle.IsChecked = false;
        Assert.False(OrbweaverSettings.PeerLaunchInboundEnabled);
    }

    // A desk that already opted in shows it.
    [AvaloniaFact]
    public void TheRowReflectsASavedChoice()
    {
        OrbweaverSettings.PeerLaunchInboundEnabled = true;

        var toggle = Descendants(LaunchRow(NewWindow(link: true))!).OfType<ToggleButton>().Single();

        Assert.True(toggle.IsChecked);
    }

    // Says what it costs: the folders and accounts are not shared while off.
    [AvaloniaFact]
    public void TheRowSaysWhatItShares()
    {
        var text = string.Join(" ", Descendants(LaunchRow(NewWindow(link: true))!).OfType<TextBlock>().Select(t => t.Text));

        Assert.Contains("start a Claude Code, Codex or Grok session on this one", text);
        Assert.Contains("can't see this one's recent folders or accounts", text);
    }
}
