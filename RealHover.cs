using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace Orbweaver
{
    // CB-302: whether the pointer is *really* over an orb, as opposed to whether
    // Avalonia still believes it is.
    //
    // A TopLevel's IsPointerOver is cleared only by a raw LeaveWindow, and on
    // macOS an orb window can miss that leave (window motion, the backdrops'
    // ignoresMouseEvents toggling, leaving onto an overlapping window — which
    // one is not established). The flag then stays true indefinitely, and
    // everything that trusts it answers about a pointer that left long ago:
    // the thought bubble (see ThoughtBubbles) and the flyout's hover bridge
    // (OrbWindow.OnFlyoutShowTick / OnFlyoutHideTick / OnFlyoutWatchTick). The
    // real cursor is the cross-check. When it is unavailable the rules fall
    // back to the flag, which is exactly the behaviour before this ticket.
    //
    // Pure: no window, no clock, no cursor. The caller reads those and hands
    // them in, so every arm here is a unit test.
    internal static class RealHover
    {
        // Opening needs both: Avalonia saying the pointer arrived (a real
        // PointerEntered started the delay that led here) and, where the real
        // cursor can be read, the cursor actually being inside the window. A
        // stale window satisfies the first and fails the second.
        internal static bool ShouldOpen(bool isPointerOver, PixelPoint? cursor, PixelRect frame) =>
            isPointerOver && (cursor is not { } c || Inside(c, frame));

        // Staying open asks only the cursor when there is one: the flag is the
        // very thing that goes stale, so a known cursor outside every frame
        // closes, and a known cursor inside one keeps it open even if a leave
        // was reported early. With no cursor, the flag is all there is.
        internal static bool ShouldStayOpen(bool isPointerOver, PixelPoint? cursor, params PixelRect[] frames)
        {
            if (cursor is not { } c) return isPointerOver;
            foreach (var frame in frames)
            {
                if (Inside(c, frame)) return true;
            }
            return false;
        }

        // Left and top edges in, right and bottom out, so two windows sharing an
        // edge never both claim the pixel on it. Spelled out rather than left to
        // PixelRect.Contains so the rule is visible where the tests pin it.
        internal static bool Inside(PixelPoint p, PixelRect frame) =>
            p.X >= frame.X && p.X < frame.X + frame.Width &&
            p.Y >= frame.Y && p.Y < frame.Y + frame.Height;

        // A window's frame in Window.Position's own units: Position is physical
        // pixels on Windows and points on macOS, and UnitsPerDip is the measured
        // ratio between those and the DIPs Bounds is in (1 on macOS).
        internal static PixelRect FrameOf(Window window)
        {
            var scale = OrbWindow.UnitsPerDip(window);
            return new PixelRect(
                window.Position,
                new PixelSize(
                    (int)Math.Round(window.Bounds.Width * scale),
                    (int)Math.Round(window.Bounds.Height * scale)));
        }
    }

    // The one cursor read shared by the bubbles and the flyouts. A seam, so a
    // test hands it positions rather than reading wherever the developer's
    // mouse happens to be; the UI suites' bootstraps set it to "unavailable"
    // and individual tests point it where they need it.
    internal static class CursorProbe
    {
        internal static Func<PixelPoint?> Read = Native;

        // Excluded from coverage: the real cursor, read from the OS. NSEvent's
        // mouseLocation on macOS (through BackdropNative.CursorTopLeft, which
        // GroupBackdrops already compares against Window.Position, so the units
        // agree) and user32's GetCursorPos on Windows, which is physical pixels
        // like Position there. A headless runner has no cursor worth reading,
        // and reading the developer's would make every hover test depend on
        // where their mouse is.
        [ExcludeFromCodeCoverage]
        private static PixelPoint? Native()
        {
            if (OperatingSystem.IsMacOS())
            {
                return BackdropNative.CursorTopLeft() is { } p
                    ? new PixelPoint((int)Math.Floor(p.X), (int)Math.Floor(p.Y))
                    : null;
            }

            if (OperatingSystem.IsWindows())
            {
                return GetCursorPos(out var pt) ? new PixelPoint(pt.X, pt.Y) : null;
            }

            return null;
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        [ExcludeFromCodeCoverage]
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT point);
    }
}
