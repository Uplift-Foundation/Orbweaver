using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace Orbweaver
{
    // One group with orbs on screen, as SessionManager hands it over on each scan
    // or arrange.
    internal readonly record struct BackdropSpec(
        string GroupId, string DrawnShape, IReadOnlyList<OrbWindow> Orbs, GroupStyle? Style,
        GroupOutline.PlacedCurve? Curve = null);

    // What a backdrop currently is, for tests and diagnostics.
    internal readonly record struct BackdropState(
        string GroupId, bool Shown, PixelPoint Position, double Width, double Height,
        Color Stroke, Color Fill, double StrokeWidth, int Vertices, bool Highlighted, bool? Catching);

    // CB-268 S5: the shape drawn behind each group of orbs — the group's own
    // silhouette (GroupOutline) in the group's style, in a transparent window of
    // its own that sits BEHIND every orb and arrow and catches the pointer only
    // inside the silhouette, so the empty space in a group can be grabbed to drag
    // it. Mirrors TeamLinks: a pool of windows that are parked and reused, never
    // closed (tearing an Avalonia window down mid-render crashes), and a
    // Update/Refresh pair where Update takes a new set and Refresh re-reads where
    // the orbs are.
    //
    // Backdrops exist whenever a group has orbs on screen, including when outlines
    // are switched off: they are then invisible but still grabbable.
    //
    // Platform behaviour follows docs/cb268-backdrop-spike.md:
    //  - macOS: Topmost stays false and the level is set one below the orbs'
    //    floating level; hit-testing is one shared 30 Hz poll that flips
    //    ignoresMouseEvents (alpha-based pass-through cannot be relied on because
    //    fill opacity may be 0).
    //  - Windows: a polygon SetWindowRgn per silhouette and WindowsWindowOrder.
    //    PlaceBehindAll on show, on every Update and at drag end (Restack).
    //  - One window per group even across monitors; it may straddle mid-drag.
    internal static class GroupBackdrops
    {
        // Room between an orb's edge and the outline, in DIP. Scaled to the
        // arrangement's own units before it reaches GroupOutline.
        internal const double PaddingDip = 10;

        // Slack around the silhouette's bounding box, so a stroke (twice as wide
        // when highlighted) is never clipped by the window edge.
        internal const double EdgeDip = 4;

        private const int MaxParked = 8;
        private const int PollMs = 33; // ~30 Hz

        private static readonly List<BackdropSpec> Specs = new();
        private static readonly Dictionary<string, BackdropWindow> Active = new();
        private static readonly Stack<BackdropWindow> Parked = new();

        private static bool _outlines = true;
        private static bool _arranged = true;
        private static readonly List<OrbWindow> Loose = new();
        private static string? _highlight;
        private static Point? _lastCursor;
        private static DispatcherTimer? _timer;

        // Raised on a left-button press inside a silhouette (never in the corners
        // of its bounding box). Arguments: the group id, the press args and the
        // backdrop window that received it. S4 wires it to a group drag.
        internal static event Action<string, PointerPressedEventArgs, Window>? BackdropPressed;

        internal static int ShownCount => Active.Count;
        internal static int ParkedCount => Parked.Count;

        internal static Window? WindowOf(string groupId) =>
            Active.TryGetValue(groupId, out var w) ? w : null;

        internal static BackdropState? StateOf(string groupId) =>
            Active.TryGetValue(groupId, out var w) ? w.State : null;

        // Arrange mode, as SessionManager knows it. User groups have a backdrop in
        // both modes; main, heartbeats and crons only while arranged (see
        // BackdropVisibility). Call before Update. Defaults to arranged.
        internal static void SetArranged(bool arranged)
        {
            if (_arranged == arranged) return;
            _arranged = arranged;
            Refresh();
        }

        // Orbs that are not in any backdropped group — the loose stack, in free mode.
        // A backdrop never takes a click over one of them: their circles are cut out
        // of its hit area exactly as its own orbs' are, even if one sits inside the
        // silhouette or the corners of its bounding box.
        internal static void SetLooseOrbs(IEnumerable<OrbWindow> orbs)
        {
            Loose.Clear();
            Loose.AddRange(orbs);
        }

        // Replaces the set. Callers rebuild it on every scan rather than diffing.
        internal static void Update(IEnumerable<BackdropSpec> groups)
        {
            Specs.Clear();
            Specs.AddRange(groups);
            Refresh();
            Restack();
        }

        // Re-reads the orbs without touching the set. A drag calls this on every
        // pointer move, so the silhouette is only refitted when the discs really
        // changed shape: a group drag moves every orb by the same amount, and that
        // moves the window instead.
        internal static void Refresh()
        {
            var wanted = new HashSet<string>();
            var added = false;

            MeasureDiscs(Loose, out var looseDiscs, out _);

            foreach (var spec in Specs)
            {
                if (!BackdropVisibility.Shows(spec.GroupId, _arranged)) continue;
                if (!MeasureDiscs(spec.Orbs, out var discs, out var scale)) continue;

                wanted.Add(spec.GroupId);

                if (!Active.TryGetValue(spec.GroupId, out var window))
                {
                    window = Parked.Count > 0 ? Parked.Pop() : new BackdropWindow();
                    Active[spec.GroupId] = window;
                    _lastCursor = null; // a new window must be classified even under a still cursor
                    added = true;
                }

                window.Fit(spec.GroupId, spec.DrawnShape, discs, scale, spec.Curve);
                window.SyncHoles(looseDiscs);
                window.Restyle(StyleFor(spec), spec.GroupId == _highlight, _outlines);
                window.EnsureShown();
            }

            foreach (var id in Active.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                Park(id);
            }

            // A first show lands IN FRONT of orbs and arrows on Windows, so a window
            // that has just appeared is put behind them straight away rather than
            // waiting for the next Update.
            if (added) Restack();

            EnsureTimer();
        }

        // The silhouette last drawn for a group, in screen units (the space of
        // Window.Position), or empty if it has no backdrop. OrbSnap measures
        // against this so the padding lives only here.
        internal static Point[] OutlineOf(string groupId) =>
            Active.TryGetValue(groupId, out var w) ? w.Outline() : Array.Empty<Point>();

        // Highlight draws the target at full stroke opacity and twice the width —
        // also when outlines are off, since it is the "you will join this" cue.
        internal static void Highlight(string? groupId)
        {
            _highlight = groupId;
            foreach (var spec in Specs)
            {
                if (Active.TryGetValue(spec.GroupId, out var w))
                    w.Restyle(StyleFor(spec), spec.GroupId == _highlight, _outlines);
            }
        }

        internal static void SetOutlinesVisible(bool visible)
        {
            if (_outlines == visible) return;
            _outlines = visible;
            foreach (var spec in Specs)
            {
                if (Active.TryGetValue(spec.GroupId, out var w))
                    w.Restyle(StyleFor(spec), spec.GroupId == _highlight, _outlines);
            }
        }

        // Hide and pool every backdrop (arrange off, app hidden). The set is kept,
        // so the next Refresh brings them back.
        internal static void ParkAll()
        {
            foreach (var id in Active.Keys.ToList()) Park(id);
            EnsureTimer();
        }

        // Re-establishes "behind every orb" on Windows, where a newly shown topmost
        // window lands in front. Update does it; a drag end should too. A no-op
        // elsewhere (macOS orders by window level).
        [ExcludeFromCodeCoverage]
        internal static void Restack()
        {
            if (Active.Count > 0) WindowsWindowOrder.PlaceBehindAll(Active.Values.ToList());
        }

        private static void Park(string id)
        {
            var w = Active[id];
            Active.Remove(id);
            w.Park();
            if (Parked.Count < MaxParked) Parked.Push(w);
        }

        private static (Color Stroke, Color Fill) StyleFor(BackdropSpec spec) =>
            GroupStyle.Resolve(spec.Style, new GroupStyle(
                OrbweaverSettings.GroupOutlineStroke,
                OrbweaverSettings.GroupOutlineStrokeOpacity,
                OrbweaverSettings.GroupOutlineFill,
                OrbweaverSettings.GroupOutlineFillOpacity));

        // Each visible orb's circle — centre and radius in the same units as
        // Window.Position — and how many of those units make a DIP. A team member's
        // radius already carries MemberScale (OrbWindow.OrbRadius). An orb with no
        // platform window yet, or hidden, is skipped; a group with none has no
        // backdrop.
        // Where an orb's centre is on screen and how many screen units make a DIP,
        // or null for an orb with no platform window yet (a startup race; the next
        // Refresh has it). A seam because the headless platform's PointToScreen
        // ignores Window.Position, so a test has to say where its orbs are.
        internal static Func<OrbWindow, (Point Centre, double Scale)?> Measure =
            orb => MeasureFromDisc(orb, SessionManager.DiscOf, OrbWindow.UnitsPerDip);

        // S7: the same circle the drag measures (SessionManager.DiscOf) — the
        // centre from Window.Position, so it is right under the headless
        // platform too, and the scale from OrbWindow.UnitsPerDip, the factor
        // the drag divides back out. One measurement, not two that could drift.
        // The two are parameters so the guard below can be tested on purpose:
        // whether the headless platform makes PointToScreen throw for a
        // never-shown orb depends on what ran before it.
        internal static (Point Centre, double Scale)? MeasureFromDisc(
            OrbWindow orb,
            Func<OrbWindow, (double X, double Y, double R)> disc,
            Func<OrbWindow, double> unitsPerDip)
        {
            try
            {
                var d = disc(orb);
                return (new Point(d.X, d.Y), unitsPerDip(orb));
            }
            catch (ArgumentException)
            {
                // An orb that is not in a visual tree: PointToScreen throws
                // ArgumentException ("Visual does not belong to a visual tree"),
                // observed on a never-shown orb run on its own. This used to
                // catch InvalidOperationException, which PointToScreen was never
                // seen to throw, so the guard caught nothing. The next Refresh
                // has the orb.
                return null;
            }
        }

        private static bool MeasureDiscs(
            IReadOnlyList<OrbWindow> orbs, out List<(double X, double Y, double R)> discs, out double scale)
        {
            discs = new List<(double, double, double)>();
            scale = 1;
            var measured = false;

            foreach (var orb in orbs)
            {
                if (!orb.IsVisible) continue;

                if (Measure(orb) is not { } m) continue;

                // An orb read before it has been positioned can come back as NaN or
                // infinity. GroupOutline rejects those loudly, so they are dropped
                // here: a group of only such orbs has no backdrop yet and the next
                // Refresh has it.
                var r = orb.OrbRadius * m.Scale;
                if (!double.IsFinite(m.Centre.X) || !double.IsFinite(m.Centre.Y) ||
                    !double.IsFinite(r) || r < 0) continue;

                if (!measured) { scale = m.Scale; measured = true; }
                discs.Add((m.Centre.X, m.Centre.Y, orb.OrbRadius * m.Scale));
            }

            return discs.Count > 0;
        }

        // ---- macOS hit-test poll ---------------------------------------------

        private static void EnsureTimer()
        {
            var want = OperatingSystem.IsMacOS() && Active.Count > 0;

            if (want && _timer is null)
            {
                _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(PollMs), DispatcherPriority.Background, (_, _) => PollOnce());
                _timer.Start();
            }
            else if (!want && _timer is not null)
            {
                _timer.Stop();
                _timer = null;
            }
        }

        [ExcludeFromCodeCoverage]
        private static void PollOnce()
        {
            if (BackdropNative.CursorTopLeft() is { } cursor)
                HitTestTick(cursor, BackdropNative.AnyButtonDown(), (w, catches) => BackdropNative.SetIgnoresMouse(w, !catches));
        }

        // One poll tick. `apply` is told to start or stop catching the pointer, and
        // only on a change, in both directions. Skipped while a button is down and
        // while the cursor has not moved (unless a window has never been classified).
        internal static void HitTestTick(Point cursor, bool buttonsDown, Action<Window, bool> apply)
        {
            if (buttonsDown) return;

            var unclassified = Active.Values.Any(w => w.Catching is null);
            if (!unclassified && !BackdropHitTest.CursorMoved(_lastCursor, cursor)) return;
            _lastCursor = cursor;

            foreach (var w in Active.Values)
            {
                if (w.Classify(cursor) is { } now) apply(w, now);
            }
        }

        // ---- the window ---------------------------------------------------------

        // The pure parts (fit, style, press handling) are covered by
        // tests/UiTests/GroupBackdropsTests; what is excluded is only the code that
        // talks to the window server, which a headless runner has none of.
        private sealed class BackdropWindow : Window
        {
            private readonly ShapePath _shape;
            private readonly SolidColorBrush _stroke = new(Colors.Transparent);
            private readonly SolidColorBrush _fill = new(Colors.Transparent);

            private string _groupId = "";
            private List<(double X, double Y, double R)>? _discs;
            private Point[] _local = Array.Empty<Point>();
            private (double X, double Y, double R)[] _localDiscs = Array.Empty<(double, double, double)>();   // the group's own
            private (double X, double Y, double R)[] _holes = Array.Empty<(double, double, double)>();       // plus other orbs reaching in
            private double _scale = 1;
            private bool _shown;
            private string _drawnShape = "";
            private GroupOutline.PlacedCurve? _curve;
            private bool _highlighted;
            private Point _origin; // window top-left in screen units

            internal bool? Catching { get; private set; }

            public BackdropWindow()
            {
                WindowDecorations = WindowDecorations.None;
                Background = Brushes.Transparent; // so the whole window takes pointer events in Avalonia
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
                // NEVER assigned again: on macOS Avalonia rewrites the window level
                // whenever Topmost is set, which would undo SetLevelBelowOrbs.
                Topmost = false;
                ShowInTaskbar = false;
                ShowActivated = false;
                CanResize = false;
                SizeToContent = SizeToContent.Manual;

                _shape = new ShapePath
                {
                    Fill = _fill,
                    Stroke = _stroke,
                    StrokeThickness = 0,
                    Stretch = Stretch.None,
                    StrokeJoin = PenLineJoin.Round,
                    IsHitTestVisible = false
                };
                Content = _shape;

                Opened += (_, _) => OnOpenedNative();
                PointerPressed += OnPressed;
            }

            internal BackdropState State => new(
                _groupId, _shown, Position, Width, Height,
                _stroke.Color, _fill.Color, _shape.StrokeThickness, _local.Length, _highlighted, Catching);

            // Refits the silhouette — or just moves the window when every disc moved
            // by the same whole amount.
            internal void Fit(
                string groupId, string shape, List<(double X, double Y, double R)> discs, double scale,
                GroupOutline.PlacedCurve? curve)
            {
                _groupId = groupId;

                if (_discs is not null && scale == _scale && shape == _drawnShape &&
                    BackdropGeometry.TryTranslation(_discs, discs) is { } d &&
                    IsWhole(d.Dx) && IsWhole(d.Dy) && BackdropGeometry.CurveFollows(_curve, curve, d.Dx, d.Dy))
                {
                    _curve = curve;
                    _origin = new Point(_origin.X + d.Dx, _origin.Y + d.Dy);
                    _discs = discs;
                    Position = new PixelPoint((int)_origin.X, (int)_origin.Y);
                    return;
                }

                _curve = curve;
                _drawnShape = shape;
                var outline = GroupOutline.Silhouette(shape, discs, PaddingDip * scale, curve);
                var box = GroupOutline.Bounds(outline).Inflate(EdgeDip * scale);

                _discs = discs;
                _scale = scale;
                _origin = new Point(Math.Floor(box.X), Math.Floor(box.Y));

                _local = new Point[outline.Length];
                for (var i = 0; i < outline.Length; i++)
                    _local[i] = new Point(outline[i].X - _origin.X, outline[i].Y - _origin.Y);
                _localDiscs = BackdropGeometry.LocalDiscs(discs, _origin);
                _holes = _localDiscs;

                Position = new PixelPoint((int)_origin.X, (int)_origin.Y);
                Width = Math.Ceiling(box.Right - _origin.X) / scale;
                Height = Math.Ceiling(box.Bottom - _origin.Y) / scale;

                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(new Point(_local[0].X / scale, _local[0].Y / scale), true);
                    for (var i = 1; i < _local.Length; i++)
                        ctx.LineTo(new Point(_local[i].X / scale, _local[i].Y / scale));
                    ctx.EndFigure(true);
                }
                _shape.Data = geometry;

                ApplyRegion();
            }

            private static bool IsWhole(double v) => Math.Abs(v - Math.Round(v)) < 1e-6;

            internal void Restyle((Color Stroke, Color Fill) style, bool highlighted, bool outlines)
            {
                _highlighted = highlighted;
                var (stroke, fill, width) = BackdropStyle.Compute(style.Stroke, style.Fill, highlighted, outlines);
                if (_stroke.Color != stroke) _stroke.Color = stroke;
                if (_fill.Color != fill) _fill.Color = fill;
                _shape.StrokeThickness = width;
            }

            internal void EnsureShown()
            {
                if (_shown) return;
                _shown = true;
                Catching = false; // pass-through until the poll says the cursor is inside
                Show();
            }

            internal void Park()
            {
                if (!_shown) return;
                _shown = false;
                Catching = null;
                Hide();
            }

            // Where the cursor (screen units) is relative to this backdrop, decided
            // by BackdropHitTest. Returns the new catching state, or null for no
            // change.
            // Re-reads which other orbs reach into this window. Done on every Refresh,
            // because a loose orb can move without the group moving; the Windows
            // region is only rebuilt when the set of holes actually changed.
            internal void SyncHoles(IReadOnlyList<(double X, double Y, double R)> otherOrbs)
            {
                var holes = BackdropGeometry.HolesFor(_localDiscs, otherOrbs, _origin, Width * _scale, Height * _scale);
                if (holes.SequenceEqual(_holes)) return;

                _holes = holes;
                ApplyRegion();
            }

            internal Point[] Outline()
            {
                var abs = new Point[_local.Length];
                for (var i = 0; i < abs.Length; i++) abs[i] = new Point(_local[i].X + _origin.X, _local[i].Y + _origin.Y);
                return abs;
            }

            internal bool? Classify(Point cursor)
            {
                var local = new Point(cursor.X - _origin.X, cursor.Y - _origin.Y);
                var bounds = new Rect(0, 0, Width * _scale, Height * _scale);

                if (BackdropHitTest.ShouldCatch(local, bounds, _local, _holes, false, Catching) is not { } now) return null;
                Catching = now;
                return now;
            }

            private void OnPressed(object? sender, PointerPressedEventArgs e)
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

                // Belt and braces: the native layer already keeps the corners from
                // reaching us, but the poll can be a tick behind the cursor.
                var p = e.GetPosition(this);
                var px = new Point(p.X * _scale, p.Y * _scale);
                if (!BackdropHitTest.InsideSilhouette(px, new Rect(0, 0, Width * _scale, Height * _scale), _local)) return;

                BackdropPressed?.Invoke(_groupId, e, this);
            }

            [ExcludeFromCodeCoverage]
            private void OnOpenedNative()
            {
                this.ShowOnAllSpaces();
                BackdropNative.SetLevelBelowOrbs(this);
                BackdropNative.SetIgnoresMouse(this, Catching != true);
                ApplyRegion();
            }

            [ExcludeFromCodeCoverage]
            private void ApplyRegion() =>
                BackdropNative.SetPolygonRegion(
                    this, BackdropGeometry.RegionPoints(_local, default, 1), BackdropGeometry.RegionHoles(_holes, 1));
        }
    }
}
