using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Xunit;

namespace Orbweaver.Tests;

// CB-268 S6's Settings card: the global outline style a group inherits. Every
// control writes through the S1 properties, and a colour is only written when a
// person made the change (the arming guard ColorRow always had).
[Collection("Settings")]
public class SettingsGroupOutlineRowTests : IDisposable
{
    private readonly bool _enabled = OrbweaverSettings.GroupOutlinesEnabled;
    private readonly bool _names = OrbweaverSettings.GroupNamesVisible;
    private readonly string _stroke = OrbweaverSettings.GroupOutlineStroke;
    private readonly double _strokeOpacity = OrbweaverSettings.GroupOutlineStrokeOpacity;
    private readonly string _fill = OrbweaverSettings.GroupOutlineFill;
    private readonly double _fillOpacity = OrbweaverSettings.GroupOutlineFillOpacity;
    private readonly bool _random = OrbweaverSettings.GroupColourRandom;

    public void Dispose()
    {
        OrbweaverSettings.GroupOutlinesEnabled = _enabled;
        OrbweaverSettings.GroupNamesVisible = _names;
        OrbweaverSettings.GroupOutlineStroke = _stroke;
        OrbweaverSettings.GroupOutlineStrokeOpacity = _strokeOpacity;
        OrbweaverSettings.GroupOutlineFill = _fill;
        OrbweaverSettings.GroupOutlineFillOpacity = _fillOpacity;
        OrbweaverSettings.GroupColourRandom = _random;
    }

    private static SettingsWindow NewWindow() =>
        (SettingsWindow)typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)!.Invoke(null);

    private static T In<T>(Control row, int index = 0) where T : class =>
        row.GetLogicalDescendants().OfType<T>().ElementAt(index);

    private static void Arm(ColorPicker picker)
    {
        var pointer = new Avalonia.Input.Pointer(
            Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        picker.RaiseEvent(new PointerPressedEventArgs(
            picker, pointer, picker, default, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
    }

    [AvaloniaFact]
    public void TheCardIsInTheWindowAfterAutoOrganize()
    {
        var window = NewWindow();

        var titles = window.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        var auto = titles.IndexOf("Auto-organize");
        var outlines = titles.IndexOf("Group outlines");

        Assert.True(auto >= 0 && outlines > auto, "Group outlines card follows Auto-organize");
        Assert.True(titles.IndexOf("Orb colours") > outlines);
    }

    [AvaloniaFact]
    public void TheCardHasNineRowsEachWithHelp()
    {
        var rows = NewWindow().GroupOutlineRows();

        Assert.Equal(9, rows.Length);
        Assert.All(rows, row =>
            Assert.True(row.GetLogicalDescendants().OfType<TextBlock>().Count() >= 2, "label plus help"));
    }

    [AvaloniaFact]
    public void TheSwitchWritesTheEnabledSetting()
    {
        OrbweaverSettings.GroupOutlinesEnabled = false;
        var row = NewWindow().GroupOutlineRows()[1];
        var toggle = In<ToggleButton>(row);

        toggle.IsChecked = true;
        Assert.True(OrbweaverSettings.GroupOutlinesEnabled);

        toggle.IsChecked = false;
        Assert.False(OrbweaverSettings.GroupOutlinesEnabled);
    }

    [AvaloniaFact]
    public void TheControlsOpenOnTheStoredStyle()
    {
        OrbweaverSettings.GroupOutlinesEnabled = true;
        OrbweaverSettings.GroupOutlineStroke = "#102030";
        OrbweaverSettings.GroupOutlineStrokeOpacity = 0.4;
        OrbweaverSettings.GroupOutlineFill = "#A0B0C0";
        OrbweaverSettings.GroupOutlineFillOpacity = 0.2;

        var rows = NewWindow().GroupOutlineRows();

        Assert.True(In<ToggleButton>(rows[1]).IsChecked);
        Assert.Equal(Color.Parse("#102030"), In<ColorPicker>(rows[4]).Color);
        Assert.Equal(0.4, In<Slider>(rows[5]).Value, 3);
        Assert.Equal(Color.Parse("#A0B0C0"), In<ColorPicker>(rows[6]).Color);
        Assert.Equal(0.2, In<Slider>(rows[7]).Value, 3);
    }

    [AvaloniaFact]
    public void TheOpacitySlidersWriteZeroToOneAndShowAPercentage()
    {
        var rows = NewWindow().GroupOutlineRows();

        var stroke = In<Slider>(rows[5]);
        stroke.Value = 0.75;
        Assert.Equal(0.75, OrbweaverSettings.GroupOutlineStrokeOpacity, 3);
        Assert.Contains(rows[5].GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "75%");

        var fill = In<Slider>(rows[7]);
        fill.Value = 0.1;
        Assert.Equal(0.1, OrbweaverSettings.GroupOutlineFillOpacity, 3);
        Assert.Contains(rows[7].GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "10%");

        Assert.Equal(0, stroke.Minimum);
        Assert.Equal(1, stroke.Maximum);
    }

    [AvaloniaFact]
    public void AColourAPersonPicksIsWrittenAsHex()
    {
        var rows = NewWindow().GroupOutlineRows();

        var stroke = In<ColorPicker>(rows[4]);
        Arm(stroke);
        stroke.Color = Color.FromRgb(0x12, 0x34, 0x56);
        Assert.Equal("#123456", OrbweaverSettings.GroupOutlineStroke);

        var fill = In<ColorPicker>(rows[6]);
        Arm(fill);
        fill.Color = Color.FromRgb(0xAB, 0xCD, 0xEF);
        Assert.Equal("#ABCDEF", OrbweaverSettings.GroupOutlineFill);
    }

    [AvaloniaFact]
    public void AColourTheTemplateChoosesIsNotWritten()
    {
        OrbweaverSettings.GroupOutlineStroke = "#102030";
        var picker = In<ColorPicker>(NewWindow().GroupOutlineRows()[4]);

        picker.Color = Color.FromRgb(0x2C, 0x27, 0x3C);

        Assert.Equal("#102030", OrbweaverSettings.GroupOutlineStroke);
        Assert.Equal(Color.Parse("#102030"), picker.Color);
    }

    [AvaloniaFact]
    public void TheShippedColourRowsStillTakeAStateName()
    {
        // The overload the three state rows use is unchanged by the
        // generalisation; SettingsColourRowTests pins the behaviour, this pins
        // that it still resolves and builds a picker on the stored colour.
        var picker = In<ColorPicker>((Control)NewWindow().ColorRow("Idle", "idle"));

        Assert.Equal(OrbColors.For("idle").R, picker.Color.R);
    }

    [AvaloniaFact]
    public void ControlHelpers()
    {
        Assert.Equal("35%", GroupStyleControls.Percent(0.35));
        Assert.Equal(Colors.White, GroupStyleControls.ColorOf("not a colour"));
        Assert.Equal(Color.Parse("#112233"), GroupStyleControls.ColorOf("#112233"));
        Assert.Equal(Colors.White, GroupStyleControls.ColorOf(null));
    }

    [AvaloniaFact]
    public void TheShowGroupNamesSwitchIsOffByDefaultAndWritesItsSetting()
    {
        OrbweaverSettings.GroupNamesVisible = false;
        var row = NewWindow().GroupOutlineRows()[8];
        Assert.Contains(row.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Show group names");
        var toggle = In<ToggleButton>(row);
        Assert.False(toggle.IsChecked);

        toggle.IsChecked = true;
        Assert.True(OrbweaverSettings.GroupNamesVisible);

        toggle.IsChecked = false;
        Assert.False(OrbweaverSettings.GroupNamesVisible);
    }

    // ---- CB-409: group colours ----------------------------------------------

    [AvaloniaFact]
    public void TheModePickerWritesTheSetting()
    {
        OrbweaverSettings.GroupColourRandom = false;
        var picker = In<ComboBox>(NewWindow().GroupOutlineRows()[2]);
        Assert.Equal(0, picker.SelectedIndex);

        picker.SelectedIndex = 1;
        Assert.True(OrbweaverSettings.GroupColourRandom);

        picker.SelectedIndex = 0;
        Assert.False(OrbweaverSettings.GroupColourRandom);
    }

    [AvaloniaFact]
    public void ThePickerOpensOnTheStoredMode()
    {
        OrbweaverSettings.GroupColourRandom = true;
        Assert.Equal(1, In<ComboBox>(NewWindow().GroupOutlineRows()[2]).SelectedIndex);
    }

    [AvaloniaFact]
    public void ShuffleChangesTheSeedAndIsDisabledInSameForAllMode()
    {
        OrbweaverSettings.GroupColourRandom = false;
        var rows = NewWindow().GroupOutlineRows();
        var shuffle = In<Button>(rows[3]);
        Assert.False(shuffle.IsEnabled);

        In<ComboBox>(rows[2]).SelectedIndex = 1;
        Assert.True(shuffle.IsEnabled);

        var group = OrbweaverSettings.OrbGroupFor("main");
        var before = OrbweaverSettings.RandomColourFor(group);
        var changed = false;
        for (var i = 0; i < 6 && !changed; i++)
        {
            shuffle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            changed = OrbweaverSettings.RandomColourFor(group) != before;
        }
        Assert.True(changed, "six shuffles never moved Main's colour");
    }

    [AvaloniaFact]
    public void TheGlobalColourRowsAreDisabledInRandomModeAndTheOpacityRowsAreNot()
    {
        OrbweaverSettings.GroupColourRandom = false;
        var rows = NewWindow().GroupOutlineRows();
        Assert.True(rows[4].IsEnabled && rows[6].IsEnabled);

        In<ComboBox>(rows[2]).SelectedIndex = 1;

        Assert.False(rows[4].IsEnabled);
        Assert.False(rows[6].IsEnabled);
        Assert.True(rows[5].IsEnabled);
        Assert.True(rows[7].IsEnabled);

        In<ComboBox>(rows[2]).SelectedIndex = 0;
        Assert.True(rows[4].IsEnabled && rows[6].IsEnabled);
    }

    [AvaloniaFact]
    public void TheCardOpensWithTheColourRowsDisabledWhenAlreadyRandom()
    {
        OrbweaverSettings.GroupColourRandom = true;
        var rows = NewWindow().GroupOutlineRows();

        Assert.False(rows[4].IsEnabled);
        Assert.True(In<Button>(rows[3]).IsEnabled);
    }

    [AvaloniaFact]
    public void ReselectingTheSameModeOrClearingTheSelectionWritesNothing()
    {
        OrbweaverSettings.GroupColourRandom = false;
        var picker = In<ComboBox>(NewWindow().GroupOutlineRows()[2]);

        picker.SelectedIndex = -1;                 // cleared: ignored
        Assert.False(OrbweaverSettings.GroupColourRandom);

        picker.SelectedIndex = 0;                  // same mode as stored: no write, no resync
        Assert.False(OrbweaverSettings.GroupColourRandom);

        picker.SelectedIndex = 1;
        picker.SelectedIndex = -1;
        Assert.True(OrbweaverSettings.GroupColourRandom);
    }
}
