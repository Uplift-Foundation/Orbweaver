using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;

namespace Orbweaver.Tests;

// CB-281: what auto-grouping looks like. Group by CLI gives one outlined, named
// group per CLI (ids and names from the real classifier, laid out by the real
// ComputeGroups), drawn beside one hand-made group so the two read as the same
// kind of thing. Same technique as GroupBackdropScreenshots: each backdrop is a
// window, a circle stands in for each orb at its real radius.
[Collection("Settings")]
public class AutoGroupOrbScreenshots : IDisposable
{
    private readonly List<OrbWindow> _orbs = new();
    private readonly List<string> _created = new();
    private readonly Func<OrbWindow, (Point Centre, double Scale)?> _measure = GroupBackdrops.Measure;
    private readonly Func<double> _clock = BackdropMotion.Clock;
    private readonly bool _names = OrbweaverSettings.GroupNamesVisible;
    private readonly Dictionary<IReadOnlyList<OrbWindow>, GroupOutline.PlacedCurve?> _curves = new();

    public AutoGroupOrbScreenshots()
    {
        GroupBackdrops.Measure = o => (new Point(o.Position.X + o.CentreDip, o.Position.Y + o.CentreDip), 1.0);
        BackdropMotion.Enabled = false;
        GroupBackdrops.Update(Array.Empty<BackdropSpec>());
        GroupBackdrops.SetOutlinesVisible(true);
        GroupBackdrops.SetArranged(true);
        GroupBackdrops.SetLooseOrbs(Array.Empty<OrbWindow>());
        GroupBackdrops.PreviewPair(null);
        GroupBackdrops.Highlight(null);
    }

    public void Dispose()
    {
        GroupBackdrops.Update(Array.Empty<BackdropSpec>());
        GroupBackdrops.ParkAll();
        GroupBackdrops.SetNamesVisible(false);
        GroupBackdrops.Measure = _measure;
        BackdropMotion.Enabled = true;
        BackdropMotion.Clock = _clock;
        foreach (var o in _orbs) o.Close();
        foreach (var id in _created) OrbweaverSettings.DeleteOrbGroup(id);
        OrbweaverSettings.GroupNamesVisible = _names;
    }

    private static readonly PixelRect Stage = new(0, 0, 560, 480);

    private List<OrbWindow> Arranged(string prefix, string shape, int count)
    {
        var leadOf = Enumerable.Repeat(-1, count).ToArray();
        var (at, groups) = OrbArrangement.ComputeGroups(
            count, leadOf, new int[count],
            new[] { new OrbArrangement.GroupLayout(shape, null) },
            new OrbArrangement.Layout(Stage, 1.0, shape, 1.0));
        var orbs = at.Select((p, i) =>
        {
            var orb = new OrbWindow($"{prefix}{i}") { Position = new PixelPoint(p.X, p.Y) };
            orb.Show();
            _orbs.Add(orb);
            return orb;
        }).ToList();
        var g = groups[0];
        _curves[orbs] = g.CurveScale > 0 ? new GroupOutline.PlacedCurve(g.CurveCentre, g.CurveScale) : null;
        return orbs;
    }

    private static Window Dress(string groupId, IEnumerable<OrbWindow> orbs, string colour)
    {
        var w = GroupBackdrops.WindowOf(groupId)!;
        var s = GroupBackdrops.StateOf(groupId)!.Value;
        var shape = w.Content is Panel old ? old.Children[0] : (Control)w.Content!;
        if (w.Content is Panel prior) prior.Children.Clear();
        w.Content = null;

        var caption = GroupBackdrops.CaptionOf(groupId);
        var lift = caption is { Visible: true } c ? Math.Max(0, s.Position.Y - c.Rect.Y) : 0;

        var stage = new Panel { Background = new SolidColorBrush(Color.Parse("#1E1E24")) };
        shape.Margin = new Thickness(0, lift, 0, 0);
        stage.Children.Add(shape);
        if (caption is { Visible: true } cap)
        {
            stage.Children.Add(new TextBlock
            {
                Text = cap.Text,
                FontSize = GroupCaption.FontSizeDip,
                Foreground = new SolidColorBrush(cap.Colour),
                Margin = new Thickness(Math.Max(0, cap.Rect.X - s.Position.X), 0, 0, 0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
            });
            w.Height += lift;
        }
        foreach (var o in orbs)
        {
            stage.Children.Add(new Avalonia.Controls.Shapes.Ellipse
            {
                Width = o.OrbRadius * 2,
                Height = o.OrbRadius * 2,
                Fill = new SolidColorBrush(Color.Parse(colour)),
                Margin = new Thickness(
                    o.Position.X + o.CentreDip - s.Position.X - o.OrbRadius,
                    o.Position.Y + o.CentreDip - s.Position.Y - o.OrbRadius + lift, 0, 0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
            });
        }
        w.Content = stage;
        ScreenshotHelper.Flush();
        return w;
    }

    [AvaloniaFact]
    public void GroupedByCliBesideAHandMadeGroup()
    {
        string Auto(string key, string name)
        {
            var id = OrbAutoGroups.IdFor(AutoGroupBy.Cli, key);
            OrbweaverSettings.EnsureAutoOrbGroups(new[] { (id, name) });
            _created.Add(id);
            return id;
        }
        var claudeId = Auto("claude", "Claude");
        var codexId = Auto("codex", "Codex");
        var grokId = Auto("grok", "Grok");
        var hand = OrbweaverSettings.CreateOrbGroup("heart");
        _created.Add(hand.Id);
        OrbweaverSettings.RenameOrbGroup(hand.Id, "Reviewers (by hand)");

        var claude = Arranged("cl", "circle", 6);
        var codex = Arranged("cx", "circle", 4);
        var grok = Arranged("gk", "circle", 3);
        var heart = Arranged("hd", "heart", 7);

        GroupBackdrops.SetNamesVisible(true);
        GroupBackdrops.Update(new[]
        {
            new BackdropSpec(claudeId, "circle", claude, new GroupStyle("#58A6FF", 0.7, null, null), _curves[claude]),
            new BackdropSpec(codexId, "circle", codex, new GroupStyle("#3FB950", 0.7, null, null), _curves[codex]),
            new BackdropSpec(grokId, "circle", grok, new GroupStyle("#D29922", 0.7, null, null), _curves[grok]),
            new BackdropSpec(hand.Id, "heart", heart, new GroupStyle("#FF7B72", 0.7, null, null), _curves[heart])
        });

        ScreenshotHelper.SideBySide(new[]
        {
            Dress(claudeId, claude, "#58A6FF"), Dress(codexId, codex, "#3FB950"),
            Dress(grokId, grok, "#D29922"), Dress(hand.Id, heart, "#FF7B72")
        }, "auto-group-by-cli.png");
    }
}
