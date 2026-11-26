using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Orbweaver
{
    // Tells the window server that a decoration window — an arrow, a web — is not
    // to take clicks. IsHitTestVisible only settles it inside Avalonia; the window
    // itself would still eat the click, so it has to be said to the OS on both
    // platforms.
    //
    // Excluded from coverage, as the arrow window's own copy of this was: it sends
    // setIgnoresMouseEvents: to an NSWindow through objc_msgSend on macOS and
    // rewrites GWL_EXSTYLE through user32 on Windows, and the headless platform has
    // no window for either to act on.
    [ExcludeFromCodeCoverage]
    internal static class OverlayClickThrough
    {
        public static void Apply(Window window)
        {
            var handle = window.TryGetPlatformHandle();
            if (handle is null) return;

            if (OperatingSystem.IsMacOS())
            {
                if (handle is IMacOSTopLevelPlatformHandle mac && mac.NSWindow != IntPtr.Zero)
                {
                    objc_msgSend_bool(mac.NSWindow,
                        sel_registerName("setIgnoresMouseEvents:"), true);
                }
                return;
            }

            if (OperatingSystem.IsWindows() && handle.Handle != IntPtr.Zero)
            {
                var style = GetWindowLongPtrW(handle.Handle, GWL_EXSTYLE);
                SetWindowLongPtrW(handle.Handle, GWL_EXSTYLE,
                    style | WS_EX_TRANSPARENT | WS_EX_LAYERED);
            }
        }

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TRANSPARENT = 0x00000020;
        private const long WS_EX_LAYERED = 0x00080000;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern long GetWindowLongPtrW(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern long SetWindowLongPtrW(IntPtr hWnd, int index, long value);

        [DllImport("/usr/lib/libobjc.A.dylib")]
        private static extern IntPtr sel_registerName(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_bool(IntPtr receiver, IntPtr selector,
            [MarshalAs(UnmanagedType.U1)] bool value);
    }
}
