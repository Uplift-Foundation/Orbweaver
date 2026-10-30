using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace Orbweaver.Tests;

// CB-270 Stage 6: what the new "Allow approving agent actions" row looks like.
//
// The existing settings-openclaw-cluster-rows capture is the whole OpenClaw section
// and is 1080 px tall, so it ends at "Allow replying to agents" and the approve row
// below it never appears in it on either runner. These two are framed on just the
// reply row and the approve row (the pair the person decides between), once with
// approving off and once with it on, with the row's description text in frame,
// because that text is what says what the switch grants and what it never does.
[Collection("Settings")]
public class SettingsApproveRowScreenshots
{
    private const string ReplyLabel = "Allow replying to agents";
    private const string ApproveLabel = "Allow approving agent actions";

    // A phrase from the description that carries the promise, asserted so a reframe
    // that clips the text cannot quietly keep a picture of a bare switch.
    private const string Promise = "Nothing is ever allowed automatically";

    [AvaloniaTheory]
    [InlineData(false, "settings-openclaw-approve-row-off.png")]
    [InlineData(true, "settings-openclaw-approve-row-on.png")]
    public void TheReplyAndApproveRowsAreShownWithTheirDescriptions(bool approving, string file)
    {
        var enabled = OrbweaverSettings.OpenClawEnabled;
        var approve = OrbweaverSettings.OpenClawApproveEnabled;

        try
        {
            OrbweaverSettings.OpenClawEnabled = true;
            OrbweaverSettings.OpenClawApproveEnabled = approving;

            var ctor = typeof(SettingsWindow).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
                ?? throw new MissingMethodException("SettingsWindow", ".ctor()");
            var settings = (SettingsWindow)ctor.Invoke(null);

            static bool Has(Control row, string label) =>
                row.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == label);

            var rows = settings.OpenClawRows();
            var reply = rows.Single(r => Has(r, ReplyLabel));
            var approveRow = rows.Single(r => Has(r, ApproveLabel));

            var panel = new StackPanel { Width = 520, Margin = new Thickness(16) };
            panel.Children.Add(reply);
            panel.Children.Add(approveRow);

            // SizeToContent for the reason OrbClusterScreenshots gives: the
            // descriptions wrap to a different number of lines on each runner.
            var window = new Window { Content = panel, SizeToContent = SizeToContent.WidthAndHeight };
            window.Show();
            ScreenshotHelper.Flush();

            var texts = panel.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Assert.Contains(ReplyLabel, texts);
            Assert.Contains(ApproveLabel, texts);
            Assert.Contains(texts, t => t.Contains(Promise));

            // The switch in the approve row shows the state the setting holds.
            var toggle = approveRow.GetLogicalDescendants().OfType<ToggleButton>().Single();
            Assert.Equal(approving, toggle.IsChecked);

            ScreenshotHelper.CaptureAlreadyShown(window, file);

            Assert.True(
                window.Bounds.Height >= panel.Bounds.Height,
                $"the window is {window.Bounds.Height:0}px for {panel.Bounds.Height:0}px of rows");
        }
        finally
        {
            OrbweaverSettings.OpenClawApproveEnabled = approve;
            OrbweaverSettings.OpenClawEnabled = enabled;
        }
    }
}
