using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;

namespace Orbweaver.UiTests;

// CB-282: the group name caption on a backdrop, through real windows on the
// headless platform. Placement for every shape is GroupCaptionTests in the unit
// suite; this is the wiring — off by default, the setting, the colour, a live
// rename, and the caption staying out of the hit region.
[Collection("Settings")]
public class GroupCaptionUiTests : IDisposable
{
    private readonly List<OrbWindow> _orbs = new();
    private readonly List<string> _groups = new();
    private readonly Func<OrbWindow, (Point Centre, double Scale)?> _measure = GroupBackdrops.Measure;
    private readonly bool _names = OrbweaverSettings.GroupNamesVisible;
    private readonly string _stroke = OrbweaverSettings.GroupOutlineStroke;
    private readonly double _strokeOpacity = OrbweaverSettings.GroupOutlineStrokeOpacity;

    public GroupCaptionUiTests()
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
        OrbweaverSettings.GroupOutlineStroke = _stroke;
        OrbweaverSettings.GroupOutlineStrokeOpacity = _strokeOpacity;
        GroupBackdrops.Measure = _measure;
    }

    private OrbWindow Orb(string id, int x, int y)
    {
        var orb = new OrbWindow(id) { Position = new PixelPoint(x, y) };
        orb.Show();
        _orbs.Add(orb);
        return orb;
    }

    private OrbGroup NewGroup(string name)
    {
        var group = OrbweaverSettings.CreateOrbGroup("circle");
        _groups.Add(group.Id);
        OrbweaverSettings.RenameOrbGroup(group.Id, name);
        return group;
    }

    private BackdropSpec Spec(OrbGroup group, GroupStyle? style = null) =>
        new(group.Id, "circle", new List<OrbWindow> { Orb(group.Id + "1", 300, 300), Orb(group.Id + "2", 400, 300) }, style);

    [AvaloniaFact]
    public void NamesAreOffByDefaultAndTheSettingIsPersistedUnderItsOwnKey()
    {
        using var scope = new Orbweaver.Tests.ScopedSettingsDir("group-names-key");
        Assert.False(OrbweaverSettings.GroupNamesVisible);

        OrbweaverSettings.GroupNamesVisible = true;
        OrbweaverSettings.FlushPendingSave();

        var text = File.ReadAllText(Path.Combine(scope.Dir, "settings.json"));
        Assert.Contains("\"groupNamesVisible\": true", text);
    }

    [AvaloniaFact]
    public void WithNamesOffThereIsNoCaptionAndTheWindowIsTheOldSize()
    {
        var group = NewGroup("Reviewers");
        GroupBackdrops.Update(new[] { Spec(group) });

        var caption = GroupBackdrops.CaptionOf(group.Id)!.Value;
        Assert.False(caption.Visible);
        Assert.Null(caption.Text);
    }

    [AvaloniaFact]
    public void WithNamesOnTheCaptionShowsTheNameAboveTheOutlineInsideAGrownWindow()
    {
        var group = NewGroup("Reviewers");
        var spec = Spec(group);
        GroupBackdrops.Update(new[] { spec });
        var before = GroupBackdrops.StateOf(group.Id)!.Value;

        GroupBackdrops.SetNamesVisible(true);

        var caption = GroupBackdrops.CaptionOf(group.Id)!.Value;
        var after = GroupBackdrops.StateOf(group.Id)!.Value;
        Assert.True(caption.Visible);
        Assert.Equal("Reviewers", caption.Text);
        // The window grew upward to hold it, and the caption is wholly inside.
        Assert.True(after.Height > before.Height, $"h {before.Height}->{after.Height} rect {caption.Rect} pos {before.Position}->{after.Position}");
        Assert.True(after.Position.Y < before.Position.Y);
        Assert.True(caption.Rect.Top >= 0 && caption.Rect.Bottom <= after.Height);
        // And the outline did not move: its top is below the caption.
        var outlineTop = GroupBackdrops.OutlineOf(group.Id).Min(p => p.Y) - after.Position.Y;
        Assert.True(caption.Rect.Bottom <= outlineTop + 1e-6);
    }

    [AvaloniaFact]
    public void TheCaptionIsTheResolvedStrokeColourAtFullOpacity()
    {
        OrbweaverSettings.GroupOutlineStroke = "#58A6FF";
        OrbweaverSettings.GroupOutlineStrokeOpacity = 0.2;
        var group = NewGroup("Blue");
        GroupBackdrops.SetNamesVisible(true);
        GroupBackdrops.Update(new[] { Spec(group) });

        Assert.Equal(Color.Parse("#58A6FF"), GroupBackdrops.CaptionOf(group.Id)!.Value.Colour);

        // A per-group override wins, and the alpha is still full.
        GroupBackdrops.Update(new[] { Spec(group, new GroupStyle("#FF7B72", 0.1, null, null)) });
        Assert.Equal(Color.Parse("#FF7B72"), GroupBackdrops.CaptionOf(group.Id)!.Value.Colour);
    }

    [AvaloniaFact]
    public void ARenameShowsOnTheNextRefreshWithNothingToldAboutIt()
    {
        var group = NewGroup("Before");
        GroupBackdrops.SetNamesVisible(true);
        GroupBackdrops.Update(new[] { Spec(group) });
        Assert.Equal("Before", GroupBackdrops.CaptionOf(group.Id)!.Value.Text);

        OrbweaverSettings.RenameOrbGroup(group.Id, "After");
        GroupBackdrops.Refresh();

        Assert.Equal("After", GroupBackdrops.CaptionOf(group.Id)!.Value.Text);
    }

    [AvaloniaFact]
    public void ARenameFromTheStyleDialogReachesTheCaptionThroughTheManager()
    {
        // The dialog tells SessionManager.Instance?.ReapplyGroupStyles(), which
        // refreshes the backdrops; without a manager that is a no-op, so this
        // asserts the dialog's own write and that a Refresh then shows it.
        var group = NewGroup("Typed");
        GroupBackdrops.SetNamesVisible(true);
        GroupBackdrops.Update(new[] { Spec(group) });
        var window = new GroupStyleWindow(group.Id, focusName: true);

        window.NameBox.Text = "Typed again";
        GroupBackdrops.Refresh();

        Assert.Equal("Typed again", GroupBackdrops.CaptionOf(group.Id)!.Value.Text);
    }

    [AvaloniaFact]
    public void SwitchingNamesBackOffHidesTheCaptionAndGivesTheWindowBack()
    {
        var group = NewGroup("Gone soon");
        GroupBackdrops.Update(new[] { Spec(group) });
        var plain = GroupBackdrops.StateOf(group.Id)!.Value;
        GroupBackdrops.SetNamesVisible(true);

        GroupBackdrops.SetNamesVisible(false);

        Assert.False(GroupBackdrops.CaptionOf(group.Id)!.Value.Visible);
        var again = GroupBackdrops.StateOf(group.Id)!.Value;
        Assert.Equal(plain.Height, again.Height, 3);
        Assert.Equal(plain.Position, again.Position);
    }

    [AvaloniaFact]
    public void AGroupMissingFromTheTableIsCaptionedGroupN()
    {
        var spec = new BackdropSpec(
            "g-deadbeef", "circle",
            new List<OrbWindow> { Orb("ghost1", 300, 300), Orb("ghost2", 400, 300) }, null);
        GroupBackdrops.SetNamesVisible(true);

        GroupBackdrops.Update(new[] { spec });

        Assert.StartsWith("Group ", GroupBackdrops.CaptionOf("g-deadbeef")!.Value.Text);
    }

    [AvaloniaFact]
    public void TheCaptionNeverCatchesThePointer()
    {
        var group = NewGroup("Click through");
        GroupBackdrops.SetNamesVisible(true);
        GroupBackdrops.Update(new[] { Spec(group) });
        var state = GroupBackdrops.StateOf(group.Id)!.Value;
        var caption = GroupBackdrops.CaptionOf(group.Id)!.Value;

        // Never hit-testable in Avalonia...
        var canvas = (Canvas)((Window)GroupBackdrops.WindowOf(group.Id)!).Content!;
        Assert.All(canvas.Children.OfType<TextBlock>(), t => Assert.False(t.IsHitTestVisible));

        // ...and outside the silhouette, so the shared poll leaves it passing
        // clicks through. The cursor is on the caption's centre.
        var over = new Point(
            state.Position.X + caption.Rect.Center.X, state.Position.Y + caption.Rect.Center.Y);
        var changes = new List<bool>();
        GroupBackdrops.HitTestTick(over, false, (_, catches) => changes.Add(catches));
        Assert.DoesNotContain(true, changes);

        // The control: the middle of the group does catch it.
        var middle = new Point(state.Position.X + state.Width / 2, state.Position.Y + state.Height * 0.6);
        GroupBackdrops.HitTestTick(middle, false, (_, catches) => changes.Add(catches));
        Assert.Contains(true, changes);
    }
}
