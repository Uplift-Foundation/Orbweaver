using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Xunit;

namespace Orbweaver.Tests;

// What CB-14 actually looks like, on both runners.
//
// The visible surface of this feature is *where the orbs end up*, and that is
// the one surface the rest of this project cannot photograph: an arrangement is
// a list of window positions across the whole desktop, and a headless runner has
// no desktop to place windows on. So these two capture the arrangement as a
// plot — one circle per orb at the position OrbArrangement.Compute returned,
// scaled down to fit the page — which is the same information a person gets by
// glancing at their screen and is the only form of it a screenshot can hold.
//
// Worth having as a picture rather than only as the assertions in
// tests/UnitTests/OrbArrangementBandTests.cs, because "three shapes, side by
// side, none on top of another" is a judgement a reviewer makes in a second from
// an image and makes badly from a list of coordinates. The pair is the point:
// the same orbs, the same screen, with the setting off and on.
[Collection("Settings")]
public class OrbClusterScreenshots
{
    // A 1080p screen at 1x, so the plot's own arithmetic is the identity and
    // what is drawn is what Compute returned.
    private static readonly PixelRect Work = new(0, 0, 1920, 1080);

    // Enough orbs that each shape is recognisable, and an uneven split so the
    // bands are visibly unequal — which is the behaviour that keeps the chats'
    // shape close to the size it would have had on a screen of its own.
    private static readonly int[] Groups =
        { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2 };

    [AvaloniaFact]
    public void OneShapeWhenBothTimerGroupsRideWithTheChats()
        => Plot(
            new int[Groups.Length],
            new[] { "heart", "circle", "line" },
            "arrangement-one-shape.png");

    [AvaloniaFact]
    public void ThreeShapesWhenBothTimerGroupsAreGivenTheirOwn()
        => Plot(
            Groups,
            new[] { "heart", "circle", "line" },
            "arrangement-three-shapes.png");

    // CB-270: the fourth predefined group. Chats a heart, heartbeats a circle,
    // crons a line and an agent's own sessions a diamond, each in its own colour,
    // none on top of another — the picture the setting exists to make possible.
    [AvaloniaFact]
    public void FourShapesWhenAnAgentsOwnSessionsAreGivenTheirOwnToo()
        => Plot(
            new[] { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 3, 3, 3, 3, 3 },
            new[] { "heart", "circle", "line", "diamond" },
            "arrangement-four-shapes.png");

    // Heartbeats separated and crons left with the chats — the mixed case, and
    // the one that shows the bands are cut from the groups actually in use
    // rather than from all three slots.
    [AvaloniaFact]
    public void TwoShapesWhenOnlyOneTimerGroupIsSeparated()
        => Plot(
            Groups.Select(g => g == 2 ? 0 : g).ToArray(),
            new[] { "heart", "circle", "line" },
            "arrangement-two-shapes.png");

    // CB-198: one heart of orbs at every size the slider offers, from its
    // bottom to its top — this runner's bottom, so the Windows capture shows
    // its 70% floor and the macOS one its 60%, with a team of three hanging off the biggest. What a
    // reviewer is looking for is the rule OrbArrangement states — the lattice
    // is laid out for the largest orb and every orb is centred in its slot —
    // so the small ones should sit as far apart as the big one would, none
    // touching, the team's arrows' worth from its lead.
    [AvaloniaFact]
    public void MixedSizesShareOneHeartSpacedForTheLargest()
    {
        const int count = 13;

        var sizes = Enumerable.Range(0, count)
            .Select(i => OrbSizing.Min + (OrbSizing.Max - OrbSizing.Min) * (i % 5) / 4.0)
            .ToArray();

        // Orb 4 is the biggest in the ramp; make it the lead of 5, 6 and 7.
        var leads = Enumerable.Repeat(-1, count).ToArray();
        for (var i = 5; i <= 7; i++) leads[i] = 4;

        Plot(new int[count], new[] { "heart" }, "arrangement-mixed-sizes.png", leads, sizes);
    }

    private static void Plot(
        int[] groups, string[] shapes, string fileName, int[]? leads = null, double[]? sizes = null)
    {
        leads ??= Enumerable.Repeat(-1, groups.Length).ToArray();
        var layout = new OrbArrangement.Layout(Work, 1.0, shapes[0], 0.85, null);

        var placed = OrbArrangement.Compute(groups.Length, leads, groups, shapes, layout, sizes);

        // A third of life size, so a 1920x1080 desktop fits a page and the orbs
        // stay big enough to tell apart.
        const double scale = 1.0 / 3;

        var canvas = new Canvas
        {
            Width = Work.Width * scale,
            Height = Work.Height * scale,
            Background = new SolidColorBrush(Color.FromRgb(0x1b, 0x1b, 0x1f))
        };

        // One colour per group, so which shape an orb belongs to is readable
        // from the picture rather than inferred from where it happens to be.
        var colours = new[]
        {
            Color.FromRgb(0xd9, 0x77, 0x57),   // chats — the app's own orange
            Color.FromRgb(0x6f, 0xa8, 0xdc),   // heartbeats
            Color.FromRgb(0x93, 0xc4, 0x7d),   // crons
            Color.FromRgb(0xe0, 0xb0, 0x4a)    // agents' own sessions (CB-270)
        };

        for (var i = 0; i < placed.Length; i++)
        {
            var g = groups[i];

            // Each orb at its own user size, and a team member at MemberScale of
            // that again, as OrbWindow draws them.
            var size = sizes?[i] ?? 1.0;
            var member = leads[i] >= 0 && leads[i] < placed.Length && leads[i] != i;
            var orb = OrbArrangement.CircleDip * size * (member ? OrbArrangement.MemberScale : 1.0) * scale;

            var dot = new Ellipse
            {
                Width = orb,
                Height = orb,
                Fill = new SolidColorBrush(colours[g % colours.Length])
            };

            // Compute returns window top-left corners, each for that orb's own
            // window; the circle a person sees is centred in it, so the drawn
            // dot is offset from the corner by half the difference. Same
            // arithmetic OrbWindow's own layout does, and getting it wrong here
            // would draw a picture that is subtly not what the app does.
            var inset = (OrbArrangement.WindowDip * size * scale - orb) / 2;

            Canvas.SetLeft(dot, (placed[i].X - Work.X) * scale + inset);
            Canvas.SetTop(dot, (placed[i].Y - Work.Y) * scale + inset);

            canvas.Children.Add(dot);
        }

        var window = new Window { Content = canvas, Width = canvas.Width, Height = canvas.Height };

        // Never closed, for the reason SettingsWindowScreenshots states: a
        // Close() in this suite once corrupted the process-wide Avalonia
        // FontManager cache.
        window.Show();
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureControl(canvas, fileName);
    }

    // And the rows that turn it on.
    //
    // Both groups on Own shape, because that is the state with the most rows in
    // it: two mode pickers and the two shape pickers that only exist in this
    // state. A capture of the default would show the two pickers and prove
    // nothing about the ones that appear.
    //
    // The rows are re-asked of the window and rendered in a window of their own,
    // sized to them, and it is that *window* that is captured. Two earlier
    // versions of this were wrong on Windows only and both are worth recording,
    // because the macOS capture was clean every time and a green build says
    // nothing here.
    //
    // The first walked up from the row's label to the first ancestor over
    // 200x200, the way the Claude Desktop group's capture does. Ghosted on
    // Windows: every line of text doubled at a slight offset.
    //
    // The second rendered the rows in a panel of their own and captured the
    // panel. Still ghosted — so the ancestor was never the problem. What the two
    // had in common is that both captured a *nested control whose content was
    // taller than the window clipping it*: the panel's own capture came out
    // truncated mid-sentence, which is the same fact showing itself a second
    // way. In the same Windows run, the whole-window settings capture next door
    // was crisp.
    //
    // So: SizeToContent, and CaptureAlreadyShown. A window is measured to its
    // content rather than clipping it, and capturing the window is the code path
    // that has always come out crisp on both runners. Nothing to guess at, and
    // nothing rendered outside what holds it.
    [AvaloniaFact]
    public void TheOpenClawSectionShowsBothModeRowsAndBothShapeRows()
    {
        var enabled = OrbweaverSettings.OpenClawEnabled;
        var heartbeats = OrbweaverSettings.OpenClawHeartbeatMode;
        var crons = OrbweaverSettings.OpenClawCronMode;
        var native = OrbweaverSettings.OpenClawNativeMode;

        try
        {
            OrbweaverSettings.OpenClawEnabled = true;
            OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.OwnShape;
            OrbweaverSettings.OpenClawCronMode = ClusterMode.OwnShape;
            OrbweaverSettings.OpenClawNativeMode = ClusterMode.OwnShape;

            var ctor = typeof(SettingsWindow).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                types: Type.EmptyTypes)
                ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

            var settings = (SettingsWindow)ctor.Invoke(null);

            // Fresh controls — OpenClawRows() builds them rather than handing
            // back the ones already on the page, which is what lets them be
            // parented here. Same seam tests/UiTests drives these rows through.
            var panel = new StackPanel { Width = 520, Margin = new Thickness(16) };
            foreach (var row in settings.OpenClawRows()) panel.Children.Add(row);

            // SizeToContent rather than a height guessed here: these rows carry
            // long help text that wraps to a different number of lines on each
            // runner, so any fixed height is one that clips on one of them.
            var window = new Window
            {
                Content = panel,
                SizeToContent = SizeToContent.WidthAndHeight
            };

            // Never closed, for the reason SettingsWindowScreenshots states.
            window.Show();
            ScreenshotHelper.Flush();

            // The rows this feature adds are in there, which is what the
            // capture is of — asserted so a framing change cannot quietly turn
            // this into a photograph of something else.
            var labels = panel.GetLogicalDescendants().OfType<TextBlock>()
                .Select(block => block.Text)
                .ToList();

            Assert.Contains("Heartbeat sessions", labels);
            Assert.Contains("Heartbeat shape", labels);
            Assert.Contains("Cron sessions", labels);
            Assert.Contains("Cron shape", labels);
            Assert.Contains(OpenClawNativeText.SettingLabel, labels);
            Assert.Contains(OpenClawNativeText.ShapeLabel, labels);

            // The window, not the panel: see the note above this test.
            ScreenshotHelper.CaptureAlreadyShown(window, "settings-openclaw-cluster-rows.png");

            // Nothing clipped. The whole point of SizeToContent here, and the
            // thing that was quietly wrong before — a capture that stops
            // mid-sentence is one a reviewer cannot use.
            Assert.True(
                window.Bounds.Height >= panel.Bounds.Height,
                $"the window is {window.Bounds.Height:0}px for {panel.Bounds.Height:0}px of rows");
        }
        finally
        {
            OrbweaverSettings.OpenClawNativeMode = native;
            OrbweaverSettings.OpenClawCronMode = crons;
            OrbweaverSettings.OpenClawHeartbeatMode = heartbeats;
            OrbweaverSettings.OpenClawEnabled = enabled;
        }
    }
}
