using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Orbweaver
{
    // CB-404: the effort dial's geometry, pure so it is unit-tested once and
    // ported to Swift and Kotlin as the same functions (plan 4.7).
    //
    // Angles are degrees clockwise from straight up, in (-180, 180]. The levels
    // sit at equal steps on a 270-degree arc from -135 (lowest, lower left) to
    // +135 (highest, lower right), leaving a gap at the bottom. A drag into the
    // gap clamps to the nearer end rather than wrapping, so sweeping past the
    // highest level never lands on the lowest.
    public static class EffortDialMath
    {
        public const double ArcStart = -135;
        public const double ArcEnd = 135;
        public const double ArcSweep = ArcEnd - ArcStart;

        // The angle of level `index` of `count`. One level sits at the top; no
        // levels has no angle worth drawing and answers the top as well.
        public static double AngleFor(int index, int count)
        {
            if (count <= 1) return 0;
            index = Math.Clamp(index, 0, count - 1);
            return ArcStart + index * ArcSweep / (count - 1);
        }

        // The nearest level to an angle. -1 for no levels, 0 for one.
        public static int IndexFor(double angle, int count)
        {
            if (count <= 0) return -1;
            if (count == 1) return 0;

            var a = Normalise(angle);
            // In the gap: whichever end is nearer. The gap is centred on 180.
            if (a > ArcEnd) return count - 1;
            if (a < ArcStart) return 0;

            var step = ArcSweep / (count - 1);
            return (int)Math.Clamp(Math.Round((a - ArcStart) / step, MidpointRounding.AwayFromZero), 0, count - 1);
        }

        // The angle of a point about a centre, in screen coordinates (y grows
        // downwards). A point on the centre itself is straight up.
        public static double AngleOf(Point point, Point centre)
        {
            var dx = point.X - centre.X;
            var dy = point.Y - centre.Y;
            if (dx == 0 && dy == 0) return 0;
            return Normalise(Math.Atan2(dx, -dy) * 180 / Math.PI);
        }

        public static int IndexFor(Point point, Point centre, int count) => IndexFor(AngleOf(point, centre), count);

        // A keyboard step, clamped at both ends. -1 stays -1 when there are no
        // levels; from "nothing selected" a step lands on the nearer end.
        public static int Step(int index, int delta, int count)
        {
            if (count <= 0) return -1;
            if (index < 0) return delta < 0 ? 0 : delta > 0 ? count - 1 : -1;
            return Math.Clamp(index + delta, 0, count - 1);
        }

        // A point on the dial's circle at an angle.
        public static Point PointAt(Point centre, double radius, double angle)
        {
            var r = angle * Math.PI / 180;
            return new Point(centre.X + radius * Math.Sin(r), centre.Y - radius * Math.Cos(r));
        }

        internal static double Normalise(double angle)
        {
            var a = angle % 360;
            if (a <= -180) a += 360;
            else if (a > 180) a -= 360;
            return a;
        }
    }

    // The round effort dial (CB-404, F2: a literal dial, not a slider).
    //
    // Drawn rather than templated: an arc with a tick and a short label per
    // level, a pointer, and the chosen level's name in the middle. Drag or click
    // a tick to choose; the choice is committed on release, not during the drag,
    // so one gesture is one request. Left/Down and Right/Up step, Home and End
    // jump, Enter or Space commits what the keys moved to, and the whole dial is
    // one tab stop. A pending level draws the pointer at the chosen level and a
    // hollow mark at the confirmed one; disabled draws greyed, with the reason
    // drawn by whoever hosts it (a tooltip alone is invisible on touch and in a
    // screenshot).
    public sealed class EffortDial : Control
    {
        public static readonly StyledProperty<IReadOnlyList<string>> LevelsProperty =
            AvaloniaProperty.Register<EffortDial, IReadOnlyList<string>>(nameof(Levels), Array.Empty<string>());

        public static readonly StyledProperty<int> SelectedIndexProperty =
            AvaloniaProperty.Register<EffortDial, int>(nameof(SelectedIndex), -1);

        public static readonly StyledProperty<int> ConfirmedIndexProperty =
            AvaloniaProperty.Register<EffortDial, int>(nameof(ConfirmedIndex), -1);

        public static readonly StyledProperty<bool> IsPendingProperty =
            AvaloniaProperty.Register<EffortDial, bool>(nameof(IsPending));

        public static readonly StyledProperty<IBrush?> AccentProperty =
            AvaloniaProperty.Register<EffortDial, IBrush?>(nameof(Accent), new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57)));

        static EffortDial()
        {
            FocusableProperty.OverrideDefaultValue<EffortDial>(true);
            AffectsRender<EffortDial>(LevelsProperty, SelectedIndexProperty, ConfirmedIndexProperty, IsPendingProperty, AccentProperty, IsEnabledProperty, IsFocusedProperty);
        }

        public EffortDial()
        {
            Width = 120;
            Height = 120;
        }

        public IReadOnlyList<string> Levels
        {
            get => GetValue(LevelsProperty);
            set => SetValue(LevelsProperty, value);
        }

        // The level drawn under the pointer: the chosen one, which may be pending.
        public int SelectedIndex
        {
            get => GetValue(SelectedIndexProperty);
            set => SetValue(SelectedIndexProperty, value);
        }

        // The level a reading last confirmed; -1 when none is known.
        public int ConfirmedIndex
        {
            get => GetValue(ConfirmedIndexProperty);
            set => SetValue(ConfirmedIndexProperty, value);
        }

        public bool IsPending
        {
            get => GetValue(IsPendingProperty);
            set => SetValue(IsPendingProperty, value);
        }

        public IBrush? Accent
        {
            get => GetValue(AccentProperty);
            set => SetValue(AccentProperty, value);
        }

        // Raised once per gesture with the level chosen, only when it differs
        // from SelectedIndex at the start of the gesture.
        public event Action<EffortDial, int>? Committed;

        // The level a gesture or the keys are pointing at before it is committed;
        // -1 when nothing is in progress. Drawn in place of SelectedIndex.
        internal int PreviewIndex { get; private set; } = -1;

        private bool _dragging;

        internal int ShownIndex => PreviewIndex >= 0 ? PreviewIndex : SelectedIndex;

        internal string? ShownLevel => ShownIndex >= 0 && ShownIndex < Levels.Count ? Levels[ShownIndex] : null;

        internal Point Centre => new(Bounds.Width / 2, Bounds.Height / 2);

        internal double Radius => Math.Max(0, Math.Min(Bounds.Width, Bounds.Height) / 2 - 22);

        // --- input ---

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!IsEnabled || Levels.Count == 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _dragging = true;
            e.Pointer.Capture(this);
            Focus();
            Preview(EffortDialMath.IndexFor(e.GetPosition(this), Centre, Levels.Count));
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_dragging) return;
            Preview(EffortDialMath.IndexFor(e.GetPosition(this), Centre, Levels.Count));
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_dragging) return;
            _dragging = false;
            e.Pointer.Capture(null);
            Commit();
            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            // A gesture taken away (a window change, a second pointer) commits
            // nothing.
            if (!_dragging) return;
            _dragging = false;
            Preview(-1);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!IsEnabled || Levels.Count == 0) return;

            var count = Levels.Count;
            var from = ShownIndex;
            int? to = e.Key switch
            {
                Key.Left or Key.Down => EffortDialMath.Step(from, -1, count),
                Key.Right or Key.Up => EffortDialMath.Step(from, +1, count),
                Key.Home => 0,
                Key.End => count - 1,
                _ => null,
            };

            if (to is { } index)
            {
                Preview(index);
                e.Handled = true;
                return;
            }

            if (e.Key is Key.Enter or Key.Space)
            {
                Commit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && PreviewIndex >= 0)
            {
                Preview(-1);
                e.Handled = true;
            }
        }

        // Moving off the dial with the keys still having moved it commits, so
        // a keyboard user is not left with a pointer that moved and a level
        // that did not.
        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            base.OnLostFocus(e);
            if (!_dragging) Commit();
        }

        // Sets the level as a person would, for automation and for tests that
        // cannot synthesize a drag: one commit, only if it changes anything.
        public void Choose(int index)
        {
            if (!IsEnabled || index < 0 || index >= Levels.Count) return;
            Preview(index);
            Commit();
        }

        private void Preview(int index)
        {
            if (PreviewIndex == index) return;
            PreviewIndex = index;
            InvalidateVisual();
        }

        private void Commit()
        {
            var chosen = PreviewIndex;
            Preview(-1);
            if (chosen < 0 || chosen == SelectedIndex) return;
            var old = SelectedIndex;
            SelectedIndex = chosen;
            (_peer as EffortDialAutomationPeer)?.RaiseValueChanged(old, chosen);
            Committed?.Invoke(this, chosen);
        }

        // --- drawing ---

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            var centre = Centre;
            var radius = Radius;
            if (radius <= 0) return;

            var enabled = IsEnabled;
            var fg = new SolidColorBrush(Color.FromArgb((byte)(enabled ? 0xDD : 0x66), 0x80, 0x80, 0x80));
            var accent = enabled ? Accent ?? Brushes.Orange : fg;
            var count = Levels.Count;

            // The track.
            var track = new StreamGeometry();
            using (var g = track.Open())
            {
                g.BeginFigure(EffortDialMath.PointAt(centre, radius, EffortDialMath.ArcStart), false);
                g.ArcTo(EffortDialMath.PointAt(centre, radius, EffortDialMath.ArcEnd), new Size(radius, radius), 0, true, SweepDirection.Clockwise);
            }
            context.DrawGeometry(null, new Pen(fg, 3, lineCap: PenLineCap.Round), track);

            // Ticks and labels.
            for (var i = 0; i < count; i++)
            {
                var a = EffortDialMath.AngleFor(i, count);
                context.DrawLine(new Pen(fg, 2), EffortDialMath.PointAt(centre, radius - 5, a), EffortDialMath.PointAt(centre, radius + 3, a));
                var text = Formatted(ShortLabel(Levels[i]), 9, fg);
                var at = EffortDialMath.PointAt(centre, radius + 13, a);
                context.DrawText(text, new Point(at.X - text.Width / 2, at.Y - text.Height / 2));
            }

            // The confirmed level, hollow, when a pending one differs from it.
            if (IsPending && ConfirmedIndex >= 0 && ConfirmedIndex < count && ConfirmedIndex != ShownIndex)
            {
                var c = EffortDialMath.PointAt(centre, radius, EffortDialMath.AngleFor(ConfirmedIndex, count));
                context.DrawEllipse(null, new Pen(accent, 2), c, 5, 5);
            }

            // The pointer and the knob.
            var shown = ShownIndex;
            context.DrawEllipse(null, new Pen(fg, 1.5), centre, radius * 0.55, radius * 0.55);
            if (shown >= 0 && shown < count)
            {
                var a = EffortDialMath.AngleFor(shown, count);
                context.DrawLine(new Pen(accent, 3, lineCap: PenLineCap.Round),
                    EffortDialMath.PointAt(centre, radius * 0.2, a), EffortDialMath.PointAt(centre, radius - 4, a));
                context.DrawEllipse(accent, null, EffortDialMath.PointAt(centre, radius, a), 5, 5);
            }

            // The name in the middle, with "…" while pending.
            var name = ShownLevel ?? "—";
            if (IsPending && PreviewIndex < 0) name += "…";
            var label = Formatted(name, 11, enabled ? Foreground() : fg);
            context.DrawText(label, new Point(centre.X - label.Width / 2, centre.Y + radius * 0.2));

            if (IsFocused && enabled)
                context.DrawEllipse(null, new Pen(accent, 1, new DashStyle(new double[] { 2, 2 }, 0)), centre, radius + 20, radius + 20);
        }

        private IBrush Foreground() =>
            this.FindResource("SystemControlForegroundBaseHighBrush") as IBrush ?? Brushes.Gray;

        // Labels at the ticks are short; the name in the middle is whole.
        internal static string ShortLabel(string level) => level.ToLowerInvariant() switch
        {
            "minimal" => "min",
            "medium" => "med",
            "xhigh" => "xhi",
            "ultra" => "ult",
            _ => level.Length > 4 ? level[..4] : level,
        };

        private static FormattedText Formatted(string text, double size, IBrush brush) =>
            new(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);

        // --- automation ---

        private AutomationPeer? _peer;

        protected override AutomationPeer OnCreateAutomationPeer() => _peer = new EffortDialAutomationPeer(this);
    }

    // A range-value peer whose value text is the level's name, so a screen
    // reader says "high", not "3".
    internal sealed class EffortDialAutomationPeer : ControlAutomationPeer, IRangeValueProvider, IValueProvider
    {
        public EffortDialAutomationPeer(EffortDial owner) : base(owner) { }

        private EffortDial Dial => (EffortDial)Owner;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

        protected override string GetClassNameCore() => nameof(EffortDial);

        public bool IsReadOnly => !Dial.IsEnabled;

        public double Minimum => 0;
        public double Maximum => Math.Max(0, Dial.Levels.Count - 1);
        double IRangeValueProvider.Value => Dial.SelectedIndex;
        public double SmallChange => 1;
        public double LargeChange => 1;

        public void SetValue(double value) => Dial.Choose((int)Math.Round(value));

        string? IValueProvider.Value => Dial.SelectedIndex >= 0 && Dial.SelectedIndex < Dial.Levels.Count ? Dial.Levels[Dial.SelectedIndex] : null;

        public void SetValue(string? value)
        {
            for (var i = 0; i < Dial.Levels.Count; i++)
                if (string.Equals(Dial.Levels[i], value, StringComparison.OrdinalIgnoreCase)) { Dial.Choose(i); return; }
        }

        internal void RaiseValueChanged(int old, int now)
        {
            RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, (double)old, (double)now);
        }
    }
}
