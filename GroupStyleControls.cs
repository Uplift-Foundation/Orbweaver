using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Orbweaver
{
    // The two controls the Settings "Group outlines" card and GroupStyleWindow
    // both build: a 0-100% opacity slider with its readout, and the stored
    // #RRGGBB turned back into a colour for a picker. One place so the card and
    // the per-group dialog cannot disagree about the range or the step.
    internal static class GroupStyleControls
    {
        // Whole percents. Finer than that is not something an eye can tell on a
        // translucent fill, and a tick per percent keeps the readout honest.
        internal const double Step = 0.01;

        internal static string Percent(double opacity) =>
            $"{Math.Round(opacity * 100).ToString(System.Globalization.CultureInfo.InvariantCulture)}%";

        // A stored colour that does not parse reads as white rather than
        // throwing — the setter normalises on the way in, so this only fires for
        // a hand-edited file that got past Load, and it costs that one swatch.
        internal static Color ColorOf(string? hex) =>
            Color.TryParse(OrbGroups.NormalizeColor(hex) ?? "#FFFFFF", out var color) ? color : Colors.White;

        // The slider and its percentage, right-aligned in the settings row's
        // control column. `write` receives 0..1.
        internal static Control OpacityControl(double value, Action<double> write)
        {
            var readout = new TextBlock
            {
                FontSize = 12,
                Width = 36,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Text = Percent(value)
            };

            var slider = OpacitySlider(value, v =>
            {
                readout.Text = Percent(v);
                write(v);
            });

            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { slider, readout }
            };
        }

        internal static Slider OpacitySlider(double value, Action<double> write)
        {
            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 1,
                Value = value,
                MinWidth = 160,
                SmallChange = Step,
                LargeChange = 0.1,
                TickFrequency = Step,
                IsSnapToTickEnabled = true
            };
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property != Slider.ValueProperty) return;
                write(slider.Value);
            };
            ToolTip.SetTip(slider, Percent(value));
            return slider;
        }
    }
}
