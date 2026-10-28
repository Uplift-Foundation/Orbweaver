using System;
using System.Runtime.InteropServices;

namespace Orbweaver
{
    // CB-268 M6: whether the motion work may be playful. With the OS's
    // "reduce motion" on, none of it is: no overshoot, no squash, no magnetic
    // pull, only a plain eased glide of about 150 ms, which still tells the user
    // where things went without bouncing them there.
    //
    // The decision is pure: everything is a function of one bool, and where
    // that bool comes from is a seam (ReduceMotionProbe), so a test flips it
    // without touching the machine's accessibility settings. The real read
    // (NSWorkspace.accessibilityDisplayShouldReduceMotion on macOS,
    // SPI_GETCLIENTAREAANIMATION on Windows) is cached for a second, because
    // an animation tick asks every frame and the answer does not change at 60 Hz.
    internal static class MotionPolicy
    {
        /// <summary>How long the plain glide takes with reduce motion on.</summary>
        internal const double ReducedGlideSeconds = 0.15;

        private static Func<bool> _probe = ReadOperatingSystem;
        private static Func<long> _clock = () => Environment.TickCount64;
        private static long _cachedAt = long.MinValue;
        private static bool _cached;
        private const long CacheMilliseconds = 1000;

        /// <summary>Test seam: where "is reduce motion on" comes from. Null restores the OS read.</summary>
        internal static Func<bool>? ReduceMotionProbe
        {
            set
            {
                _probe = value ?? ReadOperatingSystem;
                _cachedAt = long.MinValue;
            }
        }

        /// <summary>Test seam: the clock the OS-read cache uses, in milliseconds.</summary>
        internal static Func<long>? Clock
        {
            set
            {
                _clock = value ?? (() => Environment.TickCount64);
                _cachedAt = long.MinValue;
            }
        }

        internal static bool ReduceMotion
        {
            get
            {
                var now = _clock();
                if (_cachedAt != long.MinValue && now - _cachedAt < CacheMilliseconds) return _cached;

                bool value;
                try { value = _probe(); }
                // Unreadable means "not asked for": a failed accessibility
                // lookup must not turn motion off for everyone.
                catch (Exception) { value = false; }

                _cached = value;
                _cachedAt = now;
                return value;
            }
        }

        // What each effect is allowed to do right now.
        internal static bool AllowsOvershoot => !ReduceMotion;
        internal static bool AllowsSquash => !ReduceMotion;
        internal static bool AllowsMagneticPull => !ReduceMotion;

        /// <summary>
        /// The plain eased glide: <paramref name="from"/> to <paramref name="to"/>
        /// over <see cref="ReducedGlideSeconds"/>, no overshoot, ending exactly on
        /// <paramref name="to"/> from that time on.
        /// </summary>
        internal static double ReducedGlide(double from, double to, double elapsedSeconds)
        {
            if (elapsedSeconds >= ReducedGlideSeconds) return to;
            if (elapsedSeconds <= 0) return from;

            return from + (to - from) * EaseInOutCubic(elapsedSeconds / ReducedGlideSeconds);
        }

        /// <summary>Slow in, fast through the middle, slow out. 0 at 0, 1 at 1, clamped outside.</summary>
        internal static double EaseInOutCubic(double t)
        {
            t = Math.Clamp(t, 0, 1);
            return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        }

        // The two native reads below are not excluded from coverage: the tests
        // call them for real on whichever OS runs them, and CI runs macOS and
        // Windows. Linux has no such setting here, so it never reduces motion.
        private static bool ReadOperatingSystem()
        {
            if (OperatingSystem.IsMacOS()) return MacReduceMotion();
            if (OperatingSystem.IsWindows()) return WindowsReduceMotion();
            return false;
        }

        private const string Libobjc = "/usr/lib/libobjc.A.dylib";

        [DllImport(Libobjc)]
        private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Libobjc)]
        private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend_ptr(IntPtr receiver, IntPtr selector);

        [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool objc_msgSend_bool(IntPtr receiver, IntPtr selector);

        // [[NSWorkspace sharedWorkspace] accessibilityDisplayShouldReduceMotion].
        // AppKit is loaded in any process that draws a window; asked before it
        // is (a test host), the class lookup is null and the answer is "no".
        private static bool MacReduceMotion()
        {
            var cls = objc_getClass("NSWorkspace");
            if (cls == IntPtr.Zero) return false;

            var shared = objc_msgSend_ptr(cls, sel_registerName("sharedWorkspace"));
            return shared != IntPtr.Zero
                && objc_msgSend_bool(shared, sel_registerName("accessibilityDisplayShouldReduceMotion"));
        }

        private const uint SpiGetClientAreaAnimation = 0x1042;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);

        // "Show animations in Windows" off is Windows' reduce-motion: the setting
        // reads TRUE when animations are ON, so reduce motion is its negation.
        private static bool WindowsReduceMotion()
        {
            var animations = 1;
            return SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref animations, 0) && animations == 0;
        }
    }
}
