using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Orbweaver
{
    // CB-268's per-group dialog, opened from an orb's Group submenu: the
    // group's name, and whether it uses the global outline style or its own.
    //
    // The NewChatWindow pattern — one window at a time, Toggle() to open or
    // bring forward, MacOSActivation on open and close, the presentation halves
    // behind test seams — because the same two facts hold: a headless suite must
    // not Show() or Close() a window, and the decisions around those calls are
    // worth testing. Unlike NewChatWindow it can be pointed at a different group
    // while open (right-clicking another group's orb and choosing Style…), in
    // which case it retargets rather than opening a second window.
    //
    // Writes land as the user edits, with no Apply step, the way every row in
    // SettingsWindow does: a name through RenameOrbGroup, a style through
    // SetOrbGroupStyle, then ReapplyGroupStyles so a backdrop already on screen
    // repaints. A blank name is ignored by the table, so clearing the box
    // mid-edit keeps the old name rather than saving an empty one.
    //
    // "Use default style" is a CheckBox, not the ToggleSwitch the plan said:
    // the macOS theme's ToggleSwitch template is broken against the stock
    // control (see SettingsWindow.BorrowFluentToggleSwitch), and a dialog that
    // crashes on the Mac is worse than one with a tick box.
    internal sealed class GroupStyleWindow : Window
    {
        private static GroupStyleWindow? _open;

        internal string GroupId { get; private set; }

        // Rebuilt with the body every time (Body() makes fresh ones): a control
        // can only have one parent, and Retarget and the default switch both
        // rebuild the whole content.
        internal TextBox NameBox { get; private set; } = new();
        internal CheckBox UseDefaultBox { get; private set; } = new();
        internal ColorPicker? StrokePicker { get; private set; }
        internal ColorPicker? FillPicker { get; private set; }
        internal Slider? StrokeOpacitySlider { get; private set; }
        internal Slider? FillOpacitySlider { get; private set; }

        // Opens the dialog on a group, or brings the open one forward, pointed
        // at that group. A group that is not in the table (deleted between the
        // menu opening and the click) opens nothing.
        public static void Toggle(string groupId, bool focusName = false)
        {
            if (OrbweaverSettings.OrbGroupFor(groupId) is null) return;

            if (_open is not null)
            {
                _open.Retarget(groupId, focusName);
                BringForward(_open);
                return;
            }

            _open = new GroupStyleWindow(groupId, focusName);
            _open.Closed += OnClosed;
            (PresentForTests ?? Present)(_open);
        }

        private static void BringForward(GroupStyleWindow window)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            (BringForwardForTests ?? Activate)(window);
        }

        internal static Action<GroupStyleWindow>? PresentForTests;
        internal static Action<GroupStyleWindow>? BringForwardForTests;

        // The singleton, for a test to inspect and clear between cases — tests
        // never Close() a window, see NewChatWindow.OpenForTests.
        internal static GroupStyleWindow? OpenForTests
        {
            get => _open;
            set => _open = value;
        }

        [ExcludeFromCodeCoverage]
        private static void Present(GroupStyleWindow window)
        {
            MacOSActivation.SetRegular();
            window.Show();
            window.Activate();
            window.FocusRequestedControl();
        }

        [ExcludeFromCodeCoverage]
        private static void Activate(GroupStyleWindow window)
        {
            MacOSActivation.SetRegular();
            window.Activate();
            window.FocusRequestedControl();
        }

        [ExcludeFromCodeCoverage]
        private static void OnClosed(object? sender, EventArgs e)
        {
            Forget();
            MacOSActivation.SetAccessory();
        }

        internal static void Forget() => _open = null;

        private bool _focusName;

        // Built directly by tests too, through this constructor, never Show()n.
        internal GroupStyleWindow(string groupId, bool focusName)
        {
            GroupId = groupId;
            _focusName = focusName;

            Title = "Group style";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            KeyDown += OnWindowKeyDown;

            Content = Body();
        }

        internal static bool ShouldClose(Key key, KeyModifiers modifiers) =>
            key == Key.Escape || (key == Key.W && modifiers.HasFlag(KeyModifiers.Meta));

        internal void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (ShouldClose(e.Key, e.KeyModifiers)) CloseForReal();
        }

        [ExcludeFromCodeCoverage]
        private void CloseForReal() => Close();

        // Which control the menu item that opened this asked for: Rename… wants
        // the name box ready to type in, Style… leaves focus alone. Applied after
        // the window is shown, the only moment focus means anything.
        [ExcludeFromCodeCoverage]
        private void FocusRequestedControl()
        {
            if (!_focusName) return;
            NameBox.Focus();
            NameBox.SelectAll();
        }

        internal void Retarget(string groupId, bool focusName)
        {
            GroupId = groupId;
            _focusName = focusName;
            Content = Body();
        }

        private OrbGroup? Group => OrbweaverSettings.OrbGroupFor(GroupId);

        // Guards the handlers against the writes Body() itself causes by
        // seeding the controls from the store.
        private bool _building;

        // The style the controls show: the group's override where it has one,
        // the global default where it inherits, field by field.
        private GroupStyle Effective()
        {
            var over = Group?.Style;
            var globals = OrbweaverSettings.GroupOutlineGlobals();
            return new GroupStyle(
                over?.Stroke ?? globals.Stroke,
                over?.StrokeOpacity ?? globals.StrokeOpacity,
                over?.Fill ?? globals.Fill,
                over?.FillOpacity ?? globals.FillOpacity);
        }

        private Control Body()
        {
            _building = true;

            var group = Group;
            var usingDefault = group?.Style is null;
            var effective = Effective();

            NameBox = new TextBox { MinWidth = 240, Text = group?.Name ?? "" };
            NameBox.PropertyChanged += OnNameBoxChanged;
            UseDefaultBox = new CheckBox { Content = "Use default style", IsChecked = usingDefault };
            UseDefaultBox.IsCheckedChanged += OnUseDefaultChanged;

            var root = new StackPanel { Margin = new Thickness(20), Spacing = 12 };

            root.Children.Add(new TextBlock { Text = "Name", FontWeight = FontWeight.SemiBold });
            root.Children.Add(NameBox);

            root.Children.Add(new TextBlock { Text = "Outline", FontWeight = FontWeight.SemiBold });
            root.Children.Add(UseDefaultBox);

            StrokePicker = SettingsWindow.ArmedColorPicker(
                () => GroupStyleControls.ColorOf(Effective().Stroke),
                color => Write(style => style with { Stroke = OrbColors.ToHex(color) }));
            FillPicker = SettingsWindow.ArmedColorPicker(
                () => GroupStyleControls.ColorOf(Effective().Fill),
                color => Write(style => style with { Fill = OrbColors.ToHex(color) }));
            StrokePicker.IsEnabled = FillPicker.IsEnabled = !usingDefault;

            var strokeOpacity = GroupStyleControls.OpacityControl(
                effective.StrokeOpacity ?? OrbweaverSettings.DefaultGroupOutlineStrokeOpacity,
                v => Write(style => style with { StrokeOpacity = v }));
            var fillOpacity = GroupStyleControls.OpacityControl(
                effective.FillOpacity ?? OrbweaverSettings.DefaultGroupOutlineFillOpacity,
                v => Write(style => style with { FillOpacity = v }));
            StrokeOpacitySlider = FindSlider(strokeOpacity);
            FillOpacitySlider = FindSlider(fillOpacity);
            StrokeOpacitySlider.IsEnabled = FillOpacitySlider.IsEnabled = !usingDefault;

            root.Children.Add(Line("Stroke colour", StrokePicker));
            root.Children.Add(Line("Stroke opacity", strokeOpacity));
            root.Children.Add(Line("Fill colour", FillPicker));
            root.Children.Add(Line("Fill opacity", fillOpacity));

            _building = false;
            return root;
        }

        private static Slider FindSlider(Control control) =>
            ((StackPanel)control).Children.OfType<Slider>().Single();

        private static Control Line(string label, Control control)
        {
            control.HorizontalAlignment = HorizontalAlignment.Right;
            control.VerticalAlignment = VerticalAlignment.Center;

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
            return grid;
        }

        private void OnNameBoxChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (_building || e.Property != TextBox.TextProperty) return;

            OrbweaverSettings.RenameOrbGroup(GroupId, NameBox.Text);
            SessionManager.Instance?.ReapplyGroupStyles();
        }

        // Switching the override on writes today's effective style as an
        // explicit one — a style with every field null means "no override" and
        // would not stick — and switching it off clears the override so the
        // group follows the global card again.
        private void OnUseDefaultChanged(object? sender, EventArgs e)
        {
            if (_building) return;

            if (UseDefaultBox.IsChecked == true)
            {
                OrbweaverSettings.SetOrbGroupStyle(GroupId, null);
            }
            else
            {
                OrbweaverSettings.SetOrbGroupStyle(GroupId, Effective());
            }

            SessionManager.Instance?.ReapplyGroupStyles();
            Content = Body();
        }

        private void Write(Func<GroupStyle, GroupStyle> change)
        {
            if (_building) return;

            OrbweaverSettings.SetOrbGroupStyle(GroupId, change(Effective()));
            SessionManager.Instance?.ReapplyGroupStyles();
        }
    }
}
