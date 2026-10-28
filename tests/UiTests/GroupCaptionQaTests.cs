using Avalonia;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.UiTests;

// QA (Nadia), N1 at 386cde38: the caption is never counted as a backdrop, and a long
// name widens the caption window only, never the backdrop (whose region is the silhouette).
[Collection("Settings")]
public class GroupCaptionQaTests : IDisposable
{
    private readonly List<OrbWindow> _orbs = new();
    private readonly List<string> _groups = new();
    private readonly Func<OrbWindow, (Point Centre, double Scale)?> _measure = GroupBackdrops.Measure;
    private readonly bool _names = OrbweaverSettings.GroupNamesVisible;

    public GroupCaptionQaTests()
    {
        GroupBackdrops.Measure = o => (new Point(o.Position.X + o.CentreDip, o.Position.Y + o.CentreDip), 1.0);
        Reset();
    }

    private static void Reset()
    {
        GroupBackdrops.Update(Array.Empty<BackdropSpec>());
        GroupBackdrops.ParkAll();
        GroupBackdrops.SetNamesVisible(false);
        GroupBackdrops.SetOutlinesVisible(true);
        GroupBackdrops.SetArranged(true);
        GroupBackdrops.SetLooseOrbs(Array.Empty<OrbWindow>());
    }

    public void Dispose()
    {
        Reset();
        foreach (var o in _orbs) o.Close();
        foreach (var id in _groups) OrbweaverSettings.DeleteOrbGroup(id);
        OrbweaverSettings.GroupNamesVisible = _names;
        GroupBackdrops.Measure = _measure;
    }

    private BackdropSpec Spec(string name, int x)
    {
        var group = OrbweaverSettings.CreateOrbGroup("circle");
        _groups.Add(group.Id);
        OrbweaverSettings.RenameOrbGroup(group.Id, name);
        var orbs = new List<OrbWindow>();
        foreach (var dx in new[] { 0, 100 })
        {
            var orb = new OrbWindow(group.Id + dx) { Position = new PixelPoint(x + dx, 300) };
            orb.Show();
            _orbs.Add(orb);
            orbs.Add(orb);
        }
        return new BackdropSpec(group.Id, "circle", orbs, null);
    }

    [AvaloniaFact]
    public void TurningNamesOnOrOffNeverChangesTheNumberOfBackdrops()
    {
        GroupBackdrops.Update(new[] { Spec("One", 100), Spec("Two", 600) });
        var shown = GroupBackdrops.ShownCount;
        var parked = GroupBackdrops.ParkedCount;
        Assert.Equal(2, shown);

        GroupBackdrops.SetNamesVisible(true);
        Assert.Equal(shown, GroupBackdrops.ShownCount);
        Assert.Equal(parked, GroupBackdrops.ParkedCount);

        GroupBackdrops.SetNamesVisible(false);
        Assert.Equal(shown, GroupBackdrops.ShownCount);
        Assert.Equal(parked, GroupBackdrops.ParkedCount);
    }

    [AvaloniaFact]
    public void ALongNameWidensTheCaptionButNeverTheBackdrop()
    {
        var spec = Spec(new string('W', 120), 300);
        GroupBackdrops.Update(new[] { spec });
        var before = GroupBackdrops.StateOf(spec.GroupId)!.Value;

        GroupBackdrops.SetNamesVisible(true);

        var after = GroupBackdrops.StateOf(spec.GroupId)!.Value;
        var caption = GroupBackdrops.CaptionOf(spec.GroupId)!.Value;
        Assert.True(caption.Rect.Width > after.Width, $"caption {caption.Rect.Width} vs backdrop {after.Width}");
        Assert.Equal(before.Width, after.Width, 3);
        Assert.Equal(before.Height, after.Height, 3);
        Assert.Equal(before.Position, after.Position);
    }

    [AvaloniaFact]
    public void ParkingEverythingHidesTheCaptionToo()
    {
        var spec = Spec("Gone", 300);
        GroupBackdrops.SetNamesVisible(true);
        GroupBackdrops.Update(new[] { spec });
        Assert.True(GroupBackdrops.CaptionOf(spec.GroupId)!.Value.Visible);

        GroupBackdrops.Update(Array.Empty<BackdropSpec>());

        Assert.Null(GroupBackdrops.CaptionOf(spec.GroupId));
        Assert.Equal(0, GroupBackdrops.ShownCount);
    }
}
