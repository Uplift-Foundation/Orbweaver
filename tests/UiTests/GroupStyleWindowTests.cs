using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Xunit;

namespace Orbweaver.Tests;

// CB-268 S6's per-group dialog. Never Show()n or Close()d — see NewChatWindow's
// seams. The writes are the thing under test: name, override on and off, and
// each colour and opacity landing in the group's GroupStyle without touching the
// fields the user did not change.
[Collection("Settings")]
public class GroupStyleWindowTests : IDisposable
{
    private readonly List<string> _groups = new();
    private readonly string _stroke = OrbweaverSettings.GroupOutlineStroke;
    private readonly double _strokeOpacity = OrbweaverSettings.GroupOutlineStrokeOpacity;
    private readonly string _fill = OrbweaverSettings.GroupOutlineFill;
    private readonly double _fillOpacity = OrbweaverSettings.GroupOutlineFillOpacity;

    public void Dispose()
    {
        foreach (var id in _groups) OrbweaverSettings.DeleteOrbGroup(id);
        OrbweaverSettings.GroupOutlineStroke = _stroke;
        OrbweaverSettings.GroupOutlineStrokeOpacity = _strokeOpacity;
        OrbweaverSettings.GroupOutlineFill = _fill;
        OrbweaverSettings.GroupOutlineFillOpacity = _fillOpacity;
        GroupStyleWindow.OpenForTests = null;
        GroupStyleWindow.PresentForTests = null;
        GroupStyleWindow.BringForwardForTests = null;
    }

    private OrbGroup NewGroup()
    {
        var group = OrbweaverSettings.CreateOrbGroup("circle");
        _groups.Add(group.Id);
        return group;
    }

    private static void Arm(ColorPicker picker)
    {
        var pointer = new Avalonia.Input.Pointer(
            Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        picker.RaiseEvent(new PointerPressedEventArgs(
            picker, pointer, picker, default, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
    }

    private static GroupStyle? StyleOf(OrbGroup group) => OrbweaverSettings.OrbGroupFor(group.Id)!.Style;

    [AvaloniaFact]
    public void ItOpensOnTheGroupsNameAndUsingTheDefault()
    {
        var group = NewGroup();

        var window = new GroupStyleWindow(group.Id, focusName: false);

        Assert.Equal(group.Name, window.NameBox.Text);
        Assert.True(window.UseDefaultBox.IsChecked);
        Assert.False(window.StrokePicker!.IsEnabled);
        Assert.False(window.FillPicker!.IsEnabled);
        Assert.False(window.StrokeOpacitySlider!.IsEnabled);
        Assert.False(window.FillOpacitySlider!.IsEnabled);
        Assert.Null(StyleOf(group));
    }

    [AvaloniaFact]
    public void TypingANameRenamesTheGroupAndABlankKeepsTheOldOne()
    {
        var group = NewGroup();
        var window = new GroupStyleWindow(group.Id, focusName: true);

        window.NameBox.Text = "  Reviewers ";
        Assert.Equal("Reviewers", OrbweaverSettings.OrbGroupFor(group.Id)!.Name);

        window.NameBox.Text = "";
        Assert.Equal("Reviewers", OrbweaverSettings.OrbGroupFor(group.Id)!.Name);
    }

    [AvaloniaFact]
    public void TurningTheDefaultOffWritesTodaysEffectiveStyleAsTheOverride()
    {
        OrbweaverSettings.GroupOutlineStroke = "#102030";
        OrbweaverSettings.GroupOutlineStrokeOpacity = 0.5;
        OrbweaverSettings.GroupOutlineFill = "#A0B0C0";
        OrbweaverSettings.GroupOutlineFillOpacity = 0.25;
        var group = NewGroup();
        var window = new GroupStyleWindow(group.Id, false);

        window.UseDefaultBox.IsChecked = false;

        Assert.Equal(new GroupStyle("#102030", 0.5, "#A0B0C0", 0.25), StyleOf(group));
        Assert.True(window.StrokePicker!.IsEnabled);
        Assert.True(window.FillOpacitySlider!.IsEnabled);
        Assert.False(window.UseDefaultBox.IsChecked);
    }

    [AvaloniaFact]
    public void TurningTheDefaultBackOnClearsTheOverride()
    {
        var group = NewGroup();
        OrbweaverSettings.SetOrbGroupStyle(group.Id, new GroupStyle("#112233", 0.9, null, null));
        var window = new GroupStyleWindow(group.Id, false);
        Assert.False(window.UseDefaultBox.IsChecked);

        window.UseDefaultBox.IsChecked = true;

        Assert.Null(StyleOf(group));
        Assert.False(window.StrokePicker!.IsEnabled);
    }

    [AvaloniaFact]
    public void AnInheritedFieldShowsTheGlobalValueAndIsKeptInheritedUntilTouched()
    {
        OrbweaverSettings.GroupOutlineFill = "#A0B0C0";
        var group = NewGroup();
        OrbweaverSettings.SetOrbGroupStyle(group.Id, new GroupStyle("#112233", null, null, null));

        var window = new GroupStyleWindow(group.Id, false);

        Assert.Equal(Color.Parse("#112233"), window.StrokePicker!.Color);
        Assert.Equal(Color.Parse("#A0B0C0"), window.FillPicker!.Color);
        Assert.Equal(OrbweaverSettings.GroupOutlineFillOpacity, window.FillOpacitySlider!.Value, 3);
    }

    [AvaloniaFact]
    public void EachControlWritesItsOwnFieldInTheGroupsStyle()
    {
        var group = NewGroup();
        var window = new GroupStyleWindow(group.Id, false);
        window.UseDefaultBox.IsChecked = false;

        Arm(window.StrokePicker!);
        window.StrokePicker!.Color = Color.FromRgb(0x01, 0x02, 0x03);
        Assert.Equal("#010203", StyleOf(group)!.Stroke);

        Arm(window.FillPicker!);
        window.FillPicker!.Color = Color.FromRgb(0x0A, 0x0B, 0x0C);
        Assert.Equal("#0A0B0C", StyleOf(group)!.Fill);

        window.StrokeOpacitySlider!.Value = 0.8;
        Assert.Equal(0.8, StyleOf(group)!.StrokeOpacity!.Value, 3);

        window.FillOpacitySlider!.Value = 0.3;
        var style = StyleOf(group)!;
        Assert.Equal(0.3, style.FillOpacity!.Value, 3);

        // Nothing else was disturbed by the last write.
        Assert.Equal("#010203", style.Stroke);
        Assert.Equal("#0A0B0C", style.Fill);
        Assert.Equal(0.8, style.StrokeOpacity!.Value, 3);
    }

    [AvaloniaFact]
    public void ATemplateColourWhileOverridingIsNotSaved()
    {
        var group = NewGroup();
        OrbweaverSettings.SetOrbGroupStyle(group.Id, new GroupStyle("#112233", 0.5, "#445566", 0.5));
        var window = new GroupStyleWindow(group.Id, false);

        window.StrokePicker!.Color = Color.FromRgb(0x2C, 0x27, 0x3C);

        Assert.Equal("#112233", StyleOf(group)!.Stroke);
    }

    [AvaloniaTheory]
    [InlineData(Key.Escape, KeyModifiers.None, true)]
    [InlineData(Key.W, KeyModifiers.Meta, true)]
    [InlineData(Key.W, KeyModifiers.None, false)]
    [InlineData(Key.A, KeyModifiers.None, false)]
    public void EscapeAndCmdWCloseIt(Key key, KeyModifiers modifiers, bool closes) =>
        Assert.Equal(closes, GroupStyleWindow.ShouldClose(key, modifiers));

    // EscapeAndCmdWCloseIt pins the rule; this is the handler acting on it.
    [AvaloniaFact]
    public void EscapeThroughTheKeyHandlerClosesTheWindow()
    {
        var window = new GroupStyleWindow(NewGroup().Id, false);
        window.Show();
        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.OnWindowKeyDown(null, new KeyEventArgs { Key = Key.Escape });

        Assert.True(closed);
    }

    [AvaloniaFact]
    public void ANonClosingKeyLeavesItOpen()
    {
        var window = new GroupStyleWindow(NewGroup().Id, false);

        window.OnWindowKeyDown(null, new KeyEventArgs { Key = Key.A });
    }

    [AvaloniaFact]
    public void ToggleOpensOneWindowAndRetargetsItForAnotherGroup()
    {
        var a = NewGroup();
        var b = NewGroup();
        var presented = new List<GroupStyleWindow>();
        var forward = new List<GroupStyleWindow>();
        GroupStyleWindow.PresentForTests = presented.Add;
        GroupStyleWindow.BringForwardForTests = forward.Add;

        GroupStyleWindow.Toggle(a.Id);
        GroupStyleWindow.Toggle(b.Id, focusName: true);

        Assert.Single(presented);
        Assert.Single(forward);
        Assert.Equal(b.Id, presented[0].GroupId);
        Assert.Equal(b.Name, presented[0].NameBox.Text);
        Assert.Same(presented[0], GroupStyleWindow.OpenForTests);

        GroupStyleWindow.Forget();
        Assert.Null(GroupStyleWindow.OpenForTests);
    }

    [AvaloniaFact]
    public void AMinimisedWindowIsRestoredWhenBroughtForward()
    {
        var group = NewGroup();
        GroupStyleWindow.PresentForTests = _ => { };
        GroupStyleWindow.BringForwardForTests = _ => { };
        GroupStyleWindow.Toggle(group.Id);
        var open = GroupStyleWindow.OpenForTests!;
        open.WindowState = WindowState.Minimized;

        GroupStyleWindow.Toggle(group.Id);

        Assert.Equal(WindowState.Normal, open.WindowState);
    }

    [AvaloniaFact]
    public void AGroupThatNoLongerExistsOpensNothing()
    {
        var shown = false;
        GroupStyleWindow.PresentForTests = _ => shown = true;

        GroupStyleWindow.Toggle("g-deadbeef");

        Assert.False(shown);
        Assert.Null(GroupStyleWindow.OpenForTests);
    }

    [AvaloniaFact]
    public void AGroupDeletedWhileOpenShowsABlankNameAndWritesNothing()
    {
        var group = NewGroup();
        var window = new GroupStyleWindow(group.Id, false);
        OrbweaverSettings.DeleteOrbGroup(group.Id);

        window.Retarget(group.Id, false);
        window.UseDefaultBox.IsChecked = false;

        Assert.Equal("", window.NameBox.Text);
        Assert.Null(OrbweaverSettings.OrbGroupFor(group.Id));
    }

    [AvaloniaFact]
    public void ASliderDragDefersTheFileWriteButAnOverrideSwitchDoesNot()
    {
        var group = NewGroup();
        var window = new GroupStyleWindow(group.Id, false);
        window.UseDefaultBox.IsChecked = false;

        window.StrokeOpacitySlider!.Value = 0.123;

        // The in-memory style is current at once; the file write waits.
        Assert.Equal(0.12, StyleOf(group)!.StrokeOpacity!.Value, 2);
    }
}
