using System;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace Orbweaver.Tests;

// CB-270 Stage 6: the "Allow approving agent actions" row. A third power beside
// "Allow replying to agents": it writes its own setting and never the reply one.
//
// With no gateway address set, Restart is inert, which is how the handler runs
// here without a socket; the switch's own handler is the same Restart-and-rebuild
// the reply switch has.
[Collection("Settings")]
public class SettingsWindowApproveRowTests : IDisposable
{
    private readonly bool _enabled = OrbweaverSettings.OpenClawEnabled;
    private readonly string _host = OrbweaverSettings.OpenClawHost;
    private readonly bool _reply = OrbweaverSettings.OpenClawReplyEnabled;
    private readonly bool _approve = OrbweaverSettings.OpenClawApproveEnabled;

    public SettingsWindowApproveRowTests()
    {
        OrbweaverSettings.OpenClawHost = "";
        OrbweaverSettings.OpenClawEnabled = true;
        OrbweaverSettings.OpenClawReplyEnabled = false;
        OrbweaverSettings.OpenClawApproveEnabled = false;
    }

    public void Dispose()
    {
        OrbweaverSettings.OpenClawApproveEnabled = _approve;
        OrbweaverSettings.OpenClawReplyEnabled = _reply;
        OrbweaverSettings.OpenClawHost = _host;
        OrbweaverSettings.OpenClawEnabled = _enabled;
    }

    private static SettingsWindow NewWindow() =>
        (SettingsWindow)typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)!.Invoke(null);

    private static ToggleButton SwitchIn(Control row) =>
        row.GetLogicalDescendants().OfType<ToggleButton>().Single();

    private static string[] TextsIn(Control row) =>
        row.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();

    private static Control RowLabelled(Control[] rows, string label) =>
        rows.Single(r => TextsIn(r).Contains(label));

    [AvaloniaFact]
    public void TheApproveSwitchWritesItsOwnSettingAndNeverTheReplyOne()
    {
        var window = NewWindow();

        window.OnOpenClawApproveToggled(true);
        Assert.True(OrbweaverSettings.OpenClawApproveEnabled);
        Assert.False(OrbweaverSettings.OpenClawReplyEnabled);

        window.OnOpenClawApproveToggled(false);
        Assert.False(OrbweaverSettings.OpenClawApproveEnabled);
    }

    // The row sits under the reply row, and flipping its switch is what reaches
    // the handler.
    [AvaloniaFact]
    public void TheRowFollowsTheReplyRowAndItsSwitchReachesTheHandler()
    {
        var rows = NewWindow().OpenClawRows();

        var reply = Array.FindIndex(rows, r => TextsIn(r).Contains("Allow replying to agents"));
        var approve = Array.FindIndex(rows, r => TextsIn(r).Contains("Allow approving agent actions"));
        Assert.True(reply >= 0 && approve == reply + 1, $"reply at {reply}, approve at {approve}");

        var toggle = SwitchIn(rows[approve]);
        Assert.NotEqual(true, toggle.IsChecked);

        toggle.IsChecked = true;

        Assert.True(OrbweaverSettings.OpenClawApproveEnabled);
        Assert.False(OrbweaverSettings.OpenClawReplyEnabled);
    }

    // The text says what it grants, what it never does, and that the device has to
    // be approved again on the gateway.
    [AvaloniaFact]
    public void TheRowSaysWhatItGrantsAndThatNothingIsEverAllowedAutomatically()
    {
        var row = RowLabelled(NewWindow().OpenClawRows(), "Allow approving agent actions");
        var text = string.Join(" ", TextsIn(row));

        Assert.Contains("Allow once or Deny", text);
        Assert.Contains("Nothing is ever allowed automatically", text);
        Assert.Contains("no \"always\"", text);
        Assert.Contains("openclaw devices approve --latest", text);
        Assert.Contains("independent of replying", text);
    }
}
