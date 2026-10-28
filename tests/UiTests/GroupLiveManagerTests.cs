using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Xunit;

namespace Orbweaver.Tests;

// CB-268 S6's write paths with a manager running. Everything in the S6 surface
// tells the manager (`SessionManager.Instance?.ReapplyGroupStyles()`,
// `CurrentManager()?.ReapplyArrangement()`), and the other tests run with none,
// so the arm that calls it is only reached here. Instance has a private setter
// because only Start() assigns it, and Start() builds a tray icon and a gateway
// connection; this sets it by reflection for the length of one test and puts it
// back, in the Settings collection so nothing else that reads settings runs
// meanwhile.
[Collection("Settings")]
public class GroupLiveManagerTests : IDisposable
{
    private static readonly PropertyInfo InstanceProperty =
        typeof(SessionManager).GetProperty("Instance")!;

    private readonly string _dir = Directory.CreateTempSubdirectory("group-live-").FullName;
    private readonly SessionManager _manager;
    private readonly List<string> _groups = new();
    private readonly bool _enabled = OrbweaverSettings.GroupOutlinesEnabled;
    private readonly string _stroke = OrbweaverSettings.GroupOutlineStroke;
    private readonly string _fill = OrbweaverSettings.GroupOutlineFill;
    private readonly double _strokeOpacity = OrbweaverSettings.GroupOutlineStrokeOpacity;
    private readonly double _fillOpacity = OrbweaverSettings.GroupOutlineFillOpacity;

    public GroupLiveManagerTests()
    {
        _manager = new SessionManager(_dir);
        InstanceProperty.SetValue(null, _manager);
    }

    public void Dispose()
    {
        InstanceProperty.SetValue(null, null);
        foreach (var id in _groups) OrbweaverSettings.DeleteOrbGroup(id);
        OrbweaverSettings.SetOrbGroupShape(OrbGroups.Main, "heart");
        OrbweaverSettings.GroupOutlinesEnabled = _enabled;
        OrbweaverSettings.GroupOutlineStroke = _stroke;
        OrbweaverSettings.GroupOutlineFill = _fill;
        OrbweaverSettings.GroupOutlineStrokeOpacity = _strokeOpacity;
        OrbweaverSettings.GroupOutlineFillOpacity = _fillOpacity;
        GroupStyleWindow.OpenForTests = null;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private OrbGroup NewGroup()
    {
        var group = OrbweaverSettings.CreateOrbGroup("circle");
        _groups.Add(group.Id);
        return group;
    }

    private static SettingsWindow NewSettings() =>
        (SettingsWindow)typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)!.Invoke(null);

    private static T In<T>(Control row) where T : class => row.GetLogicalDescendants().OfType<T>().First();

    private static void Arm(ColorPicker picker)
    {
        var pointer = new Avalonia.Input.Pointer(
            Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, isPrimary: true);
        picker.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(
            picker, pointer, picker, default, 0,
            new Avalonia.Input.PointerPointProperties(
                Avalonia.Input.RawInputModifiers.LeftMouseButton,
                Avalonia.Input.PointerUpdateKind.LeftButtonPressed),
            Avalonia.Input.KeyModifiers.None, 1));
    }

    [AvaloniaFact]
    public void EverySettingsControlTellsTheManager()
    {
        var window = NewSettings();
        var rows = window.GroupOutlineRows();

        var toggle = In<Avalonia.Controls.Primitives.ToggleButton>(rows[0]);
        toggle.IsChecked = !(toggle.IsChecked ?? false);

        var stroke = In<ColorPicker>(rows[1]);
        Arm(stroke);
        stroke.Color = Color.FromRgb(1, 2, 3);
        In<Slider>(rows[2]).Value = 0.5;

        var fill = In<ColorPicker>(rows[3]);
        Arm(fill);
        fill.Color = Color.FromRgb(4, 5, 6);
        In<Slider>(rows[4]).Value = 0.5;

        var state = In<ColorPicker>((Control)window.ColorRow("Idle", "idle"));
        Arm(state);
        state.Color = Color.FromRgb(7, 8, 9);
        OrbColors.Set("idle", null);

        Assert.Equal("#010203", OrbweaverSettings.GroupOutlineStroke);
        Assert.Equal("#040506", OrbweaverSettings.GroupOutlineFill);
    }

    [AvaloniaFact]
    public void TheStyleDialogTellsTheManagerOnEveryWrite()
    {
        var group = NewGroup();
        var window = new GroupStyleWindow(group.Id, false);

        window.NameBox.Text = "Live";
        window.UseDefaultBox.IsChecked = false;
        window.StrokeOpacitySlider!.Value = 0.7;
        window.UseDefaultBox.IsChecked = true;

        Assert.Equal("Live", OrbweaverSettings.OrbGroupFor(group.Id)!.Name);
        Assert.Null(OrbweaverSettings.OrbGroupFor(group.Id)!.Style);
    }

    [AvaloniaFact]
    public void TheMenuTellsTheManagerAfterAShapeOrAMove()
    {
        var group = NewGroup();
        var key = "live-" + Guid.NewGuid();
        var orb = new OrbWindow(Guid.NewGuid().ToString()) { PositionKey = key, SoundKey = key };
        orb.CurrentManager = () => _manager;
        orb.GroupContextOverride = () => new GroupMenuContext(OrbGroups.Main, key, Array.Empty<string>());
        orb.RebuildGroupSubmenu();

        orb.GroupMenuItem.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Shape"))
            .Items.OfType<MenuItem>().First(i => Equals(i.Header, "Grid"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        orb.GroupMenuItem.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Move to"))
            .Items.OfType<MenuItem>().First(i => Equals(i.Header, group.Name))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal("grid", OrbweaverSettings.OrbGroupFor(OrbGroups.Main)!.Shape);
        Assert.Equal(group.Id, OrbweaverSettings.OrbGroupMembershipFor(key));
        OrbweaverSettings.UnassignOrbGroup(key);
    }

    [AvaloniaFact]
    public void TheManagerAnswersTheMenuFromItsLiveStatuses()
    {
        // GroupMenuContextFor is the thin wrapper OrbGroupMenu.Resolve sits
        // behind; with no statuses the session is its own keyless root.
        var context = _manager.GroupMenuContextFor("nobody");

        Assert.Equal(OrbGroups.Main, context.GroupId);
        Assert.Null(context.LeadKey);

        // ReapplyGroupStyles is a no-op until S7, but it must be callable.
        _manager.ReapplyGroupStyles();
    }
}
