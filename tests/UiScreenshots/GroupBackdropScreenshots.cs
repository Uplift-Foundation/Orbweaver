using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;

namespace Orbweaver.Tests;

// CB-268: what the group backdrops look like. A backdrop is a window of its own,
// so each is captured as a window — on a dark page, with a circle standing in for
// every orb it encloses at the orb's real radius (the orbs are separate windows
// and cannot be photographed inside another one) — and the pair is composed
// side by side. The circles are drawn from the same discs the outline was fitted
// to, so a ring that does not clear them would show here.
[Collection("Settings")]
public class GroupBackdropScreenshots : IDisposable
{
    private readonly List<OrbWindow> _orbs = new();
    private readonly Func<OrbWindow, (Point Centre, double Scale)?> _measure = GroupBackdrops.Measure;

    public GroupBackdropScreenshots()
    {
        GroupBackdrops.Measure = o => (new Point(o.Position.X + o.CentreDip, o.Position.Y + o.CentreDip), 1.0);
        GroupBackdrops.Update(Array.Empty<BackdropSpec>());
        GroupBackdrops.SetOutlinesVisible(true);
        GroupBackdrops.Highlight(null);
    }

    public void Dispose()
    {
        GroupBackdrops.Update(Array.Empty<BackdropSpec>());
        GroupBackdrops.ParkAll();
        GroupBackdrops.Measure = _measure;
        foreach (var o in _orbs) o.Close();
    }

    private OrbWindow Orb(string id, int x, int y, bool member = false)
    {
        var orb = new OrbWindow(id);
        if (member) orb.SetTeamRole(true);
        orb.Position = new PixelPoint(x, y);
        orb.Show();
        _orbs.Add(orb);
        return orb;
    }

    // Puts the page behind the transparent window and a circle where each orb is.
    private static Window Dress(string groupId, IEnumerable<OrbWindow> orbs)
    {
        var w = GroupBackdrops.WindowOf(groupId)!;
        var s = GroupBackdrops.StateOf(groupId)!.Value;

        // A pooled window comes back with the previous test's stage still in it:
        // take the outline back out of that rather than stacking stages.
        var shape = w.Content is Panel old ? old.Children[0] : (Control)w.Content!;
        if (w.Content is Panel prior) prior.Children.Clear();
        w.Content = null;

        var stage = new Panel { Background = new SolidColorBrush(Color.Parse("#1E1E24")) };
        stage.Children.Add(shape);

        foreach (var o in orbs)
        {
            var cx = o.Position.X + o.CentreDip - s.Position.X;
            var cy = o.Position.Y + o.CentreDip - s.Position.Y;
            stage.Children.Add(new Avalonia.Controls.Shapes.Ellipse
            {
                Width = o.OrbRadius * 2,
                Height = o.OrbRadius * 2,
                Fill = new SolidColorBrush(Color.Parse(o.OrbRadius < 18 ? "#C97B4A" : "#5B8DEF")),
                Margin = new Thickness(cx - o.OrbRadius, cy - o.OrbRadius, 0, 0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
            });
        }

        w.Content = stage;
        ScreenshotHelper.Flush();
        return w;
    }

    // Orbs placed by the real arrangement (not hand-placed), so the picture shows
    // what a group looks like when the app laid it out. leadOf marks team members.
    private List<OrbWindow> Arranged(string prefix, string shape, int[] leadOf, PixelRect work)
    {
        var at = OrbArrangement.Compute(
            leadOf.Length, leadOf, new OrbArrangement.Layout(work, 1.0, shape, 1.0));

        return at.Select((p, i) => Orb($"{prefix}{i}", p.X, p.Y, member: leadOf[i] >= 0)).ToList();
    }

    private static readonly PixelRect Stage = new(0, 0, 900, 720);

    [AvaloniaFact]
    public void TwoUserGroupsEachInItsOwnShape()
    {
        var heart = Arranged("h", "heart", Enumerable.Repeat(-1, 10).ToArray(), Stage);
        var ring = Arranged("c", "circle", Enumerable.Repeat(-1, 8).ToArray(), Stage);

        GroupBackdrops.Update(new[]
        {
            new BackdropSpec("g-heart", "heart", heart, null),
            new BackdropSpec("g-circle", "circle", ring, null)
        });

        ScreenshotHelper.SideBySide(new[] { Dress("g-heart", heart), Dress("g-circle", ring) },
            "group-backdrops-heart-and-circle.png");
    }

    [AvaloniaFact]
    public void AGroupContainingATeam()
    {
        // A lead with five fanned members, and four solo orbs, in one heart.
        var leadOf = new[] { -1, 0, 0, 0, 0, 0, -1, -1, -1, -1, -1, -1 };
        var all = Arranged("t", "heart", leadOf, Stage);

        GroupBackdrops.Update(new[] { new BackdropSpec("g-team", "heart", all, null) });

        ScreenshotHelper.Capture(Dress("g-team", all), "group-backdrops-with-team.png");
    }

    [AvaloniaFact]
    public void AStyleOverrideBesideTheGlobalStyle()
    {
        var left = Arranged("l", "circle", Enumerable.Repeat(-1, 7).ToArray(), Stage);
        var right = Arranged("r", "circle", Enumerable.Repeat(-1, 7).ToArray(), Stage);

        GroupBackdrops.Update(new[]
        {
            new BackdropSpec("g-default", "circle", left, null),
            new BackdropSpec("g-custom", "circle", right, new GroupStyle("#FF5A5F", 0.9, "#FF9F1C", 0.25))
        });

        ScreenshotHelper.SideBySide(new[] { Dress("g-default", left), Dress("g-custom", right) },
            "group-backdrops-style-override.png");
    }
}
