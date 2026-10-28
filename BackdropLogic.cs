using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;

namespace Orbweaver
{
    // CB-268 S5: every decision the group backdrop windows make, with no window
    // behind it. The native calls that act on these answers (setLevel:,
    // setIgnoresMouseEvents:, SetWindowRgn, SetWindowPos) cannot run on a headless
    // runner, so they are kept as thin as possible and everything they are told is
    // computed here, where it can be asserted on. docs/cb268-backdrop-spike.md has
    // the measurements these rules come from.

    // Whether a backdrop should catch the pointer. Used on macOS, where a window
    // is either wholly click-through or wholly not and the silhouette is enforced
    // by flipping that flag as the cursor moves.
    internal static class BackdropHitTest
    {
        // `cursor`, `bounds` and `polygon` share one coordinate space (the
        // backdrop's own). Returns the new "catch" state, or null to leave the
        // window alone: while a button is down (flipping mid-press can lose the
        // window half way through a click or drag), or when the answer has not
        // changed since `previous` (so the native call happens only on a change).
        // `previous` null means "never set", which always yields an answer — the
        // spike found the default, never-set state unreliable.
        //
        // `orbDiscs` (same space) are the orbs sitting on this backdrop: inside one
        // the answer is always false, so an orb keeps its own click even if the
        // window order is wrong for a moment (a first show can land in front).
        internal static bool? ShouldCatch(
            Point cursor, Rect bounds, Point[] polygon,
            IReadOnlyList<(double X, double Y, double R)> orbDiscs, bool buttonsDown, bool? previous)
        {
            if (buttonsDown) return null;

            var want = InsideSilhouette(cursor, bounds, polygon) && !InsideAnyDisc(cursor, orbDiscs);
            return previous == want ? null : want;
        }

        // Bounds first, because the polygon walk is 360 edges and most ticks are a
        // cursor nowhere near any backdrop.
        internal static bool InsideSilhouette(Point p, Rect bounds, Point[] polygon) =>
            bounds.Contains(p) && GroupOutline.SignedDistanceInside(p, polygon) >= 0;

        internal static bool InsideAnyDisc(Point p, IReadOnlyList<(double X, double Y, double R)> discs)
        {
            foreach (var d in discs)
            {
                var dx = p.X - d.X;
                var dy = p.Y - d.Y;
                if (dx * dx + dy * dy <= d.R * d.R) return true;
            }
            return false;
        }

        // The poll skips a tick when the cursor has not moved: nothing it would
        // decide can have changed.
        internal static bool CursorMoved(Point? last, Point now) => last is not { } l || l != now;
    }

    internal static class BackdropOrder
    {
        // `zOrder` is every top-level window front to back. Returns the rearmost
        // of ours that is not itself a backdrop: the window a backdrop must be
        // placed directly behind to sit under every orb, arrow and flyout. Null
        // when none of ours is on screen, in which case there is nothing to be
        // behind and the backdrop is left where it is.
        internal static IntPtr? LowestOf(
            IReadOnlyList<IntPtr> zOrder, ISet<IntPtr> ours, ISet<IntPtr> backdrops)
        {
            for (var i = zOrder.Count - 1; i >= 0; i--)
            {
                var h = zOrder[i];
                if (ours.Contains(h) && !backdrops.Contains(h)) return h;
            }
            return null;
        }
    }

    internal static class BackdropGeometry
    {
        // Cocoa reports the cursor from the bottom-left of the main display;
        // window positions are from the top-left.
        internal static Point CocoaToTopLeft(double x, double y, double mainDisplayHeight) =>
            new(x, mainDisplayHeight - y);

        // The silhouette as window-relative device pixels, for SetWindowRgn.
        // `origin` is the window's top-left in the polygon's space and `toDevice`
        // converts one polygon unit to a device pixel. Rounded, since a region is
        // integer; a half-pixel error on a hit area is not visible.
        internal static (int X, int Y)[] RegionPoints(Point[] polygon, Point origin, double toDevice)
        {
            var pts = new (int, int)[polygon.Length];
            for (var i = 0; i < polygon.Length; i++)
                pts[i] = (
                    (int)Math.Round((polygon[i].X - origin.X) * toDevice),
                    (int)Math.Round((polygon[i].Y - origin.Y) * toDevice));
            return pts;
        }

        // Discs relative to the window's top-left.
        internal static (double X, double Y, double R)[] LocalDiscs(
            IReadOnlyList<(double X, double Y, double R)> discs, Point origin)
        {
            var local = new (double, double, double)[discs.Count];
            for (var i = 0; i < local.Length; i++)
                local[i] = (discs[i].X - origin.X, discs[i].Y - origin.Y, discs[i].R);
            return local;
        }

        // The same discs as window-relative device pixels, for cutting out of the
        // Windows region (CombineRgn RGN_DIFF), so an orb keeps its click.
        internal static (int X, int Y, int R)[] RegionHoles(
            IReadOnlyList<(double X, double Y, double R)> localDiscs, double toDevice)
        {
            var holes = new (int, int, int)[localDiscs.Count];
            for (var i = 0; i < holes.Length; i++)
                holes[i] = (
                    (int)Math.Round(localDiscs[i].X * toDevice),
                    (int)Math.Round(localDiscs[i].Y * toDevice),
                    (int)Math.Ceiling(localDiscs[i].R * toDevice));
            return holes;
        }

        // A group drag moves every orb by the same amount, so the outline needs
        // moving, not refitting. Returns that shared offset if `next` is `prev`
        // translated (same count, same radii, one common delta), else null.
        internal static (double Dx, double Dy)? TryTranslation(
            IReadOnlyList<(double X, double Y, double R)> prev,
            IReadOnlyList<(double X, double Y, double R)> next,
            double tolerance = 1e-6)
        {
            if (prev.Count != next.Count || prev.Count == 0) return null;

            var dx = next[0].X - prev[0].X;
            var dy = next[0].Y - prev[0].Y;

            for (var i = 0; i < prev.Count; i++)
            {
                if (Math.Abs(next[i].R - prev[i].R) > tolerance) return null;
                if (Math.Abs(next[i].X - prev[i].X - dx) > tolerance) return null;
                if (Math.Abs(next[i].Y - prev[i].Y - dy) > tolerance) return null;
            }

            return (dx, dy);
        }
    }

    // What a backdrop draws with, given its resolved style and state.
    internal static class BackdropStyle
    {
        internal const double StrokeDip = 2;
        internal const double HighlightFactor = 2;

        // Outlines off: invisible, but the window still exists and still hit-tests.
        // Highlighted: the snap target, so it shows even with outlines off, at full
        // stroke opacity and twice the width. The fill keeps its own alpha — a
        // fully opaque slab behind the orbs would hide what is under the group.
        internal static (Color Stroke, Color Fill, double Width) Compute(
            Color stroke, Color fill, bool highlighted, bool outlinesVisible)
        {
            if (highlighted)
                return (Color.FromArgb(255, stroke.R, stroke.G, stroke.B), fill, StrokeDip * HighlightFactor);

            return outlinesVisible
                ? (stroke, fill, StrokeDip)
                : (Colors.Transparent, Colors.Transparent, 0);
        }
    }

    internal sealed partial record GroupStyle
    {
        private const string FallbackStroke = "#FFFFFF";
        private const string FallbackFill = "#000000";

        // Each null field of `over` (or all of it, if null) takes the matching
        // field of `globals`. A colour that does not parse falls back to the
        // global's, and if that fails too to white (stroke) / black (fill). Opacity
        // is clamped to 0..1 and becomes the alpha byte, replacing whatever alpha
        // the colour string carried. A non-finite opacity is treated as missing.
        internal static (Color Stroke, Color Fill) Resolve(GroupStyle? over, GroupStyle globals)
        {
            var stroke = Build(over?.Stroke, over?.StrokeOpacity, globals.Stroke, globals.StrokeOpacity, FallbackStroke,
                OrbweaverSettings.DefaultGroupOutlineStrokeOpacity);
            var fill = Build(over?.Fill, over?.FillOpacity, globals.Fill, globals.FillOpacity, FallbackFill,
                OrbweaverSettings.DefaultGroupOutlineFillOpacity);
            return (stroke, fill);
        }

        private static Color Build(
            string? color, double? opacity, string? globalColor, double? globalOpacity,
            string fallbackColor, double fallbackOpacity)
        {
            if (!(color is not null && Color.TryParse(color, out var c)) &&
                !(globalColor is not null && Color.TryParse(globalColor, out c)))
                c = Color.Parse(fallbackColor);

            var o = Finite(opacity) ?? Finite(globalOpacity) ?? fallbackOpacity;
            return Color.FromArgb((byte)Math.Round(Math.Clamp(o, 0, 1) * 255), c.R, c.G, c.B);
        }

        private static double? Finite(double? v) => v is { } d && double.IsFinite(d) ? d : null;
    }
}
