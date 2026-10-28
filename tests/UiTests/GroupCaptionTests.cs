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
    public void WithNamesOnTheCaptionShowsTheNameAboveTheOutlineInAWindowOfItsOwn()
    {
        var group = NewGroup("Reviewers");
        GroupBackdrops.Update(new[] { Spec(group) });
        var before = GroupBackdrops.StateOf(group.Id)!.Value;

        GroupBackdrops.SetNamesVisible(true);

        var caption = GroupBackdrops.CaptionOf(group.Id)!.Value;
        var after = GroupBackdrops.StateOf(group.Id)!.Value;
        Assert.True(caption.Visible);
        Assert.Equal("Reviewers", caption.Text);
        Assert.True(caption.Rect.Width > 0 && caption.Rect.Height > 0);

        // The backdrop is exactly what it was: its native region is the
        // silhouette, and on Windows a region clips drawing, so the caption must
        // not live in it.
        Assert.Equal(before.Height, after.Height, 3);
        Assert.Equal(before.Width, after.Width, 3);
        Assert.Equal(before.Position, after.Position);

        // The caption lies wholly above the outline, centred on the backdrop.
        var outline = GroupBackdrops.OutlineOf(group.Id);
        Assert.True(caption.Rect.Bottom <= outline.Min(p => p.Y) + 1e-6);
        Assert.Equal(after.Position.X + after.Width / 2, caption.Rect.Center.X, 1);

        // And it is click-through from its first Show.
        Assert.True(caption.ClickThrough);
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

        // A click-through window of its own, whose only content is not hit-testable.
        Assert.True(caption.ClickThrough);

        // The cursor on the caption's centre is outside the backdrop's silhouette,
        // so the shared poll leaves the backdrop passing clicks through too.
        var changes = new List<bool>();
        GroupBackdrops.HitTestTick(caption.Rect.Center, false, (_, catches) => changes.Add(catches));
        Assert.DoesNotContain(true, changes);

        // The control: the middle of the group does catch it.
        var middle = new Point(state.Position.X + state.Width / 2, state.Position.Y + state.Height * 0.6);
        GroupBackdrops.HitTestTick(middle, false, (_, catches) => changes.Add(catches));
        Assert.Contains(true, changes);
    }

    [AvaloniaFact]
    public void ADragThatMovesTheWholeGroupMovesTheCaptionWithIt()
    {
        var group = NewGroup("Follows");
        GroupBackdrops.SetNamesVisible(true);
        var spec = Spec(group);
        GroupBackdrops.Update(new[] { spec });
        var before = GroupBackdrops.CaptionOf(group.Id)!.Value;

        foreach (var o in spec.Orbs) o.Position = new PixelPoint(o.Position.X + 50, o.Position.Y + 30);
        GroupBackdrops.Refresh();

        var after = GroupBackdrops.CaptionOf(group.Id)!.Value;
        Assert.Equal(before.Rect.X + 50, after.Rect.X, 3);
        Assert.Equal(before.Rect.Y + 30, after.Rect.Y, 3);
        Assert.True(after.Visible);
    }

    [AvaloniaFact]
    public void AParkedAndReturnedBackdropShowsItsCaptionAgain()
    {
        var group = NewGroup("Back again");
        GroupBackdrops.SetNamesVisible(true);
        var spec = Spec(group);
        GroupBackdrops.Update(new[] { spec });
        Assert.True(GroupBackdrops.CaptionOf(group.Id)!.Value.Visible);

        GroupBackdrops.ParkAll();
        Assert.Null(GroupBackdrops.CaptionOf(group.Id));

        GroupBackdrops.Refresh();
        Assert.True(GroupBackdrops.CaptionOf(group.Id)!.Value.Visible);
    }
}
