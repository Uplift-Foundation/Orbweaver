using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Orbweaver
{
    // CB-268 S5: the native calls behind a group backdrop, and nothing else.
    // Every decision they act on is made in BackdropLogic.cs; see
    // docs/cb268-backdrop-spike.md for what each call was measured to do.
    // Excluded from coverage: objc_msgSend against a real NSWindow and user32
    // against a real HWND. A headless runner has neither, and window level and
    // hit-testing are state the window server owns.
    [ExcludeFromCodeCoverage]
    internal static class BackdropNative
    {
        // NSFloatingWindowLevel (3) is what Topmost = true gives an orb; one below
        // it sits behind every orb and arrow and in front of ordinary windows
        // (spike §1, read on the MacBook and the mini). Avalonia rewrites the level
        // whenever Topmost is assigned, so a backdrop never assigns it.
        internal const long MacBackdropLevel = 3 - 1;

        private const string Objc = "/usr/lib/libobjc.A.dylib";
        private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        [StructLayout(LayoutKind.Sequential)] private struct CGPoint { public double X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct CGRect { public double X, Y, W, H; }

        [DllImport(Objc)] private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string n);
        [DllImport(Objc)] private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string n);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern void MsgSetLong(IntPtr r, IntPtr s, long v);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern void MsgSetBool(IntPtr r, IntPtr s, [MarshalAs(UnmanagedType.U1)] bool v);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern CGPoint MsgPoint(IntPtr r, IntPtr s);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern ulong MsgULong(IntPtr r, IntPtr s);
        [DllImport(CoreGraphics)] private static extern uint CGMainDisplayID();
        [DllImport(CoreGraphics)] private static extern CGRect CGDisplayBounds(uint display);

        private static IntPtr NSWindowOf(Window w) =>
            w.TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle mac ? mac.NSWindow : IntPtr.Zero;

        internal static void SetLevelBelowOrbs(Window w)
        {
            if (!OperatingSystem.IsMacOS()) return;
            var ns = NSWindowOf(w);
            if (ns != IntPtr.Zero) MsgSetLong(ns, sel_registerName("setLevel:"), MacBackdropLevel);
        }

        internal static void SetIgnoresMouse(Window w, bool ignore)
        {
            if (!OperatingSystem.IsMacOS()) return;
            var ns = NSWindowOf(w);
            if (ns != IntPtr.Zero) MsgSetBool(ns, sel_registerName("setIgnoresMouseEvents:"), ignore);
        }

        // The cursor in the same top-left screen space as Window.Position, or null
        // off macOS.
        internal static Point? CursorTopLeft()
        {
            if (!OperatingSystem.IsMacOS()) return null;
            var m = MsgPoint(objc_getClass("NSEvent"), sel_registerName("mouseLocation"));
            return BackdropGeometry.CocoaToTopLeft(m.X, m.Y, CGDisplayBounds(CGMainDisplayID()).H);
        }

        internal static bool AnyButtonDown() =>
            OperatingSystem.IsMacOS() &&
            MsgULong(objc_getClass("NSEvent"), sel_registerName("pressedMouseButtons")) != 0;

        // ---- Windows -------------------------------------------------------

        [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
        [DllImport("gdi32.dll")] private static extern IntPtr CreatePolygonRgn(POINT[] pts, int count, int mode);

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        // The system owns the region after a successful call, so it is not deleted.
        // Survives Hide/Show, moves and resizes (spike §3); set again whenever the
        // silhouette itself changes.
        internal static void SetPolygonRegion(Window w, (int X, int Y)[] points)
        {
            if (!OperatingSystem.IsWindows() || points.Length < 3) return;
            var hwnd = w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return;

            var pts = new POINT[points.Length];
            for (var i = 0; i < pts.Length; i++) pts[i] = new POINT { X = points[i].X, Y = points[i].Y };
            const int Winding = 2;
            SetWindowRgn(hwnd, CreatePolygonRgn(pts, pts.Length, Winding), true);
        }
    }
}
