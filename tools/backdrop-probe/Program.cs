using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

// CB-268 S0. Shows an orb stand-in (topmost, like OrbWindow) and a group
// backdrop overlapping it, then asks the window manager — not our own
// bookkeeping — where the backdrop sits in the stack and which window a
// mouse-down at a given point would hit. It never synthesizes input, so it is
// safe on a machine somebody is using; it does draw two small windows for a
// few seconds.
//
//   dotnet run --project tools/backdrop-probe            # z-order + hit-test
//   dotnet run --project tools/backdrop-probe -- --cpu   # ...plus the macOS poll cost
//   dotnet run --project tools/backdrop-probe -- --at 40,500
//   dotnet run --project tools/backdrop-probe -- --hold 6   # pause at level 2 for an outside reading
//
// Every line is "stage: key=value ...", so two runs diff cleanly.
namespace BackdropProbe
{
    internal static class Program
    {
        internal static string[] Args = Array.Empty<string>();

        [STAThread]
        private static int Main(string[] args)
        {
            Args = args;
            return AppBuilder.Configure<ProbeApp>()
                .UsePlatformDetect()
                .With(new MacOSPlatformOptions { ShowInDock = false })
                .StartWithClassicDesktopLifetime(args);
        }
    }

    internal sealed class ProbeApp : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Dispatcher.UIThread.Post(async () =>
                {
                    var code = 0;
                    try { await Probe.Run(Program.Args); }
                    catch (Exception e) { Console.WriteLine("EXCEPTION " + e); code = 2; }
                    desktop.Shutdown(code);
                });
            }
            base.OnFrameworkInitializationCompleted();
        }
    }

    internal static class Probe
    {
        // The backdrop is 300x300 DIP with a circular silhouette of radius 120
        // at its middle, so its four corners are outside the silhouette — the
        // part that must let clicks through. The orb sits at the middle.
        internal const double Size = 300, Radius = 120, OrbSize = 80;
        internal static readonly Point Inside = new(150, 60);   // in the silhouette, clear of the orb
        internal static readonly Point Corner = new(12, 12);    // in the window, outside the silhouette
        internal static readonly Point OrbMid = new(150, 150);

        internal static Point[] Silhouette()
        {
            var pts = new Point[360];
            for (var i = 0; i < 360; i++)
            {
                var t = i * Math.PI / 180;
                pts[i] = new Point(Size / 2 + Radius * Math.Cos(t), Size / 2 + Radius * Math.Sin(t));
            }
            return pts;
        }

        internal static bool InPolygon(Point p, Point[] poly)
        {
            var inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) &&
                    p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                    inside = !inside;
            }
            return inside;
        }

        internal static int Arg(string[] args, string name, int fallback)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
        }

        internal static async Task Pause(int ms = 400) => await Task.Delay(ms);

        public static async Task Run(string[] args)
        {
            var at = new PixelPoint(40, 480);
            var i = Array.IndexOf(args, "--at");
            if (i >= 0 && i + 1 < args.Length)
            {
                var xy = args[i + 1].Split(',');
                at = new PixelPoint(int.Parse(xy[0]), int.Parse(xy[1]));
            }

            Console.WriteLine($"env: os={RuntimeInformation.OSDescription} arch={RuntimeInformation.ProcessArchitecture} pid={Environment.ProcessId} at={at}");

            if (OperatingSystem.IsMacOS()) await MacProbe.Run(at, args.Contains("--cpu"), Arg(args, "--hold", 0));
            else if (OperatingSystem.IsWindows()) await WinProbe.Run(at);
            else Console.WriteLine("unsupported OS");
        }
    }

    internal sealed class OrbStandIn : Window
    {
        public OrbStandIn()
        {
            WindowDecorations = WindowDecorations.None;
            Background = Brushes.Transparent;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            CanResize = false;
            Width = Probe.OrbSize;
            Height = Probe.OrbSize;
            Content = new Ellipse { Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0x5D, 0x3A)) };
        }
    }

    // A team arrow, as TeamLinks.LinkWindow builds one: topmost, and made
    // click-through by MakeClickThrough once it exists. Sits over Probe.Inside,
    // between the orb and the silhouette's edge, where a real arrow would.
    internal sealed class ArrowStandIn : Window
    {
        public ArrowStandIn()
        {
            WindowDecorations = WindowDecorations.None;
            Background = Brushes.Transparent;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            CanResize = false;
            Width = 40;
            Height = 20;
            Content = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)) };

            // LinkWindow applies click-through in Opened. Whether that fires
            // again on a pooled re-Show decides whether the app re-applies it.
            Opened += (_, _) => OpenedCount++;
        }

        public int OpenedCount { get; private set; }

        internal static readonly Point Offset = new(Probe.Inside.X - 20, Probe.Inside.Y - 10);
    }

    internal sealed class Backdrop : Window
    {
        private readonly Path _path;

        public Backdrop(bool topmost)
        {
            WindowDecorations = WindowDecorations.None;
            Background = Brushes.Transparent;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Topmost = topmost;
            ShowInTaskbar = false;
            ShowActivated = false;
            CanResize = false;
            Width = Probe.Size;
            Height = Probe.Size;

            var pts = Probe.Silhouette();
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                c.BeginFigure(pts[0], true);
                for (var k = 1; k < pts.Length; k++) c.LineTo(pts[k]);
                c.EndFigure(true);
            }

            _path = new Path
            {
                Data = geo,
                Stroke = new SolidColorBrush(Color.FromArgb(220, 0x4A, 0x90, 0xE2)),
                StrokeThickness = 3
            };
            SetFillAlpha(0);
            Content = _path;
        }

        public void SetFillAlpha(byte alpha) =>
            _path.Fill = new SolidColorBrush(Color.FromArgb(alpha, 0x4A, 0x90, 0xE2));
    }

    // ---------------------------------------------------------------- macOS

    [StructLayout(LayoutKind.Sequential)]
    internal struct CGPoint { public double X, Y; public CGPoint(double x, double y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CGRect { public double X, Y, W, H; }

    internal static class MacProbe
    {
        private const string Objc = "/usr/lib/libobjc.A.dylib";
        private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(Objc)] private static extern IntPtr sel_registerName(string n);
        [DllImport(Objc)] private static extern IntPtr objc_getClass(string n);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern long MsgLong(IntPtr r, IntPtr s);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgPtr(IntPtr r, IntPtr s);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgPtrLong(IntPtr r, IntPtr s, long a);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern void MsgSetLong(IntPtr r, IntPtr s, long v);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern void MsgSetBool(IntPtr r, IntPtr s, [MarshalAs(UnmanagedType.U1)] bool v);
        [DllImport(Objc, EntryPoint = "objc_msgSend")][return: MarshalAs(UnmanagedType.U1)] private static extern bool MsgBool(IntPtr r, IntPtr s);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern CGPoint MsgPoint(IntPtr r, IntPtr s);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern long MsgWindowAt(IntPtr r, IntPtr s, CGPoint p, long below);

        [DllImport(CG)] private static extern uint CGMainDisplayID();
        [DllImport(CG)] private static extern CGRect CGDisplayBounds(uint display);
        [DllImport(CG)] private static extern IntPtr CGWindowListCreate(uint option, uint relativeTo);
        [DllImport(CF)] private static extern long CFArrayGetCount(IntPtr a);
        [DllImport(CF)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr a, long i);
        [DllImport(CF)] private static extern void CFRelease(IntPtr o);

        private static IntPtr NS(Window w) =>
            w.TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle m ? m.NSWindow : IntPtr.Zero;

        private static long Level(Window w) => MsgLong(NS(w), sel_registerName("level"));
        private static long Number(Window w) => MsgLong(NS(w), sel_registerName("windowNumber"));

        private static void SetLevel(Window w, long level) => MsgSetLong(NS(w), sel_registerName("setLevel:"), level);

        // Front-to-back index in the window server's global on-screen list —
        // the same order CGWindowListCopyWindowInfo reports, from the same call.
        private static int GlobalIndex(long windowNumber)
        {
            var arr = CGWindowListCreate(1 /* OnScreenOnly */, 0);
            try
            {
                var n = CFArrayGetCount(arr);
                for (long k = 0; k < n; k++)
                    if ((long)CFArrayGetValueAtIndex(arr, k) == windowNumber) return (int)k;
                return -1;
            }
            finally { CFRelease(arr); }
        }

        private static void Report(string stage, Window orb, Window back)
        {
            Console.WriteLine(
                $"{stage}: orb.level={Level(orb)} back.level={Level(back)} " +
                $"orb.z={GlobalIndex(Number(orb))} back.z={GlobalIndex(Number(back))} " +
                $"backBehindOrb={GlobalIndex(Number(back)) > GlobalIndex(Number(orb))} " +
                $"back.visible={MsgBool(NS(back), sel_registerName("isVisible"))} " +
                $"back.ignoresMouse={MsgBool(NS(back), sel_registerName("ignoresMouseEvents"))}");
        }

        // Which window a mouse-down at this DIP point of the backdrop would hit,
        // by AppKit's own answer: +[NSWindow windowNumberAtPoint:belowWindowWithWindowNumber:]
        // takes Cocoa coordinates (origin bottom-left of the main display).
        private static Window? _arrow;

        private static string HitAt(Window back, Point local, Window orb)
        {
            var mainH = CGDisplayBounds(CGMainDisplayID()).H;
            var p = back.Position;   // points on macOS
            var cocoa = new CGPoint(p.X + local.X, mainH - (p.Y + local.Y));
            var n = MsgWindowAt(objc_getClass("NSWindow"), sel_registerName("windowNumberAtPoint:belowWindowWithWindowNumber:"), cocoa, 0);
            return n == Number(back) ? "backdrop" : n == Number(orb) ? "orb"
                : _arrow is not null && n == Number(_arrow) ? "arrow" : $"other({n})";
        }

        private static void HitReport(string stage, Window back, Window orb) =>
            Console.WriteLine(
                $"{stage}: inside={HitAt(back, Probe.Inside, orb)} corner={HitAt(back, Probe.Corner, orb)} " +
                $"orbMid={HitAt(back, Probe.OrbMid, orb)} ignoresMouse={MsgBool(NS(back), sel_registerName("ignoresMouseEvents"))}");

        public static async Task Run(PixelPoint at, bool cpu, int hold)
        {
            var back = new Backdrop(topmost: false) { Position = at };
            var orb = new OrbStandIn
            {
                Position = new PixelPoint(at.X + (int)((Probe.Size - Probe.OrbSize) / 2), at.Y + (int)((Probe.Size - Probe.OrbSize) / 2))
            };

            orb.Show();
            back.Show();
            await Probe.Pause();
            Report("m0-shown-no-level-change", orb, back);

            SetLevel(back, Level(orb) - 1);
            await Probe.Pause();
            Report("m1-setLevel(orb-1)", orb, back);
            if (hold > 0)
            {
                Console.WriteLine($"hold: {hold}s at level {Level(back)}, backdrop window number {Number(back)}, orb {Number(orb)}");
                await Task.Delay(hold * 1000);
            }

            // Negative control: one level *above* the orb must read as in
            // front, or backBehindOrb=True above proves nothing.
            SetLevel(back, Level(orb) + 1);
            await Probe.Pause();
            Report("mc-CONTROL-setLevel(orb+1)", orb, back);
            SetLevel(back, Level(orb) - 1);
            await Probe.Pause();
            Report("m1b-setLevel(orb-1)-restored", orb, back);

            back.Hide();
            await Probe.Pause(200);
            back.Show();
            await Probe.Pause();
            Report("m2-after-hide-show", orb, back);

            orb.Hide();
            await Probe.Pause(200);
            orb.Show();
            await Probe.Pause();
            Report("m3-after-orb-hide-show", orb, back);

            back.Position = new PixelPoint(at.X + 5, at.Y + 5);
            await Probe.Pause();
            back.Position = at;
            await Probe.Pause();
            Report("m4-after-move", orb, back);

            back.Topmost = true;
            await Probe.Pause();
            Report("m5-after-Topmost=true", orb, back);
            back.Topmost = false;
            await Probe.Pause();
            Report("m6-after-Topmost=false", orb, back);
            SetLevel(back, Level(orb) - 1);
            await Probe.Pause();
            Report("m7-setLevel-again", orb, back);

            // Hit-testing. Fill alpha 0 inside the silhouette first, then the
            // smallest non-zero alpha, then a visible one; ignoresMouseEvents
            // never touched in these three, which is AppKit's "decide from the
            // pixels" default for a non-opaque window.
            back.SetFillAlpha(0);
            await Probe.Pause(600);
            HitReport("h0-fill0-default", back, orb);
            back.SetFillAlpha(1);
            await Probe.Pause(600);
            HitReport("h1-fill1of255-default", back, orb);
            back.SetFillAlpha(64);
            await Probe.Pause(600);
            HitReport("h2-fill64-default", back, orb);

            MsgSetBool(NS(back), sel_registerName("setIgnoresMouseEvents:"), true);
            await Probe.Pause();
            HitReport("h3-ignoresMouse=YES", back, orb);
            MsgSetBool(NS(back), sel_registerName("setIgnoresMouseEvents:"), false);
            await Probe.Pause();
            HitReport("h4-ignoresMouse=NO", back, orb);

            await TeamArrow(at, back, orb);

            if (cpu) await CpuCost(back);

            back.Close();
            orb.Close();
        }

        // T6: a team arrow stays above the backdrop and click-through, through
        // the pool's Hide()/Show() as well as the backdrop's.
        private static async Task TeamArrow(PixelPoint at, Window back, Window orb)
        {
            MsgSetBool(NS(back), sel_registerName("setIgnoresMouseEvents:"), false);
            var arrow = new ArrowStandIn
            {
                Position = new PixelPoint(at.X + (int)ArrowStandIn.Offset.X, at.Y + (int)ArrowStandIn.Offset.Y)
            };
            _arrow = arrow;
            arrow.Show();
            await Probe.Pause();

            void Arrow(string stage) => Console.WriteLine(
                $"{stage}: arrow.level={Level(arrow)} back.level={Level(back)} " +
                $"arrowAboveBack={GlobalIndex(Number(arrow)) < GlobalIndex(Number(back))} " +
                $"arrow.ignoresMouse={MsgBool(NS(arrow), sel_registerName("ignoresMouseEvents"))} " +
                $"hitUnderArrow={HitAt(back, Probe.Inside, orb)} arrow.opened={arrow.OpenedCount}");

            // Control: before MakeClickThrough, the point under the arrow must
            // resolve to the arrow, or "backdrop" below proves nothing.
            Arrow("t0-CONTROL-arrow-not-click-through");
            MsgSetBool(NS(arrow), sel_registerName("setIgnoresMouseEvents:"), true);
            await Probe.Pause();
            Arrow("t1-arrow-click-through");

            arrow.Hide(); await Probe.Pause(200); arrow.Show(); await Probe.Pause();
            Arrow("t2-after-arrow-pool-hide-show");

            back.Hide(); await Probe.Pause(200); back.Show(); await Probe.Pause();
            Arrow("t3-after-backdrop-hide-show");

            arrow.Close();
            _arrow = null;
        }

        // What the "toggle ignoresMouseEvents from a cursor poll" design costs.
        // Process CPU time over a fixed window, idle first as the baseline.
        private static async Task CpuCost(Window back)
        {
            var poly = Probe.Silhouette();
            var mainH = CGDisplayBounds(CGMainDisplayID()).H;
            var nsEvent = objc_getClass("NSEvent");
            var mouseLocation = sel_registerName("mouseLocation");
            var setIgnores = sel_registerName("setIgnoresMouseEvents:");
            var handle = NS(back);
            bool? last = null;
            var toggles = 0;

            void Tick()
            {
                var m = MsgPoint(nsEvent, mouseLocation);
                var p = back.Position;
                var local = new Point(m.X - p.X, mainH - m.Y - p.Y);
                var catchIt = Probe.InPolygon(local, poly);
                if (last == catchIt) return;
                last = catchIt;
                toggles++;
                MsgSetBool(handle, setIgnores, !catchIt);
            }

            async Task<double> Measure(int hz, int seconds)
            {
                DispatcherTimer? timer = null;
                if (hz > 0)
                {
                    timer = new DispatcherTimer(TimeSpan.FromMilliseconds(1000.0 / hz), DispatcherPriority.Background, (_, _) => Tick());
                    timer.Start();
                }
                var proc = Process.GetCurrentProcess();
                proc.Refresh();
                var before = proc.TotalProcessorTime;
                var sw = Stopwatch.StartNew();
                await Task.Delay(seconds * 1000);
                proc.Refresh();
                var used = (proc.TotalProcessorTime - before).TotalMilliseconds / sw.Elapsed.TotalSeconds;
                timer?.Stop();
                return used;
            }

            var idle = await Measure(0, 8);
            var hz30 = await Measure(30, 8);
            var hz60 = await Measure(60, 8);
            Console.WriteLine($"cpu: idle={idle:F2}ms/s poll30={hz30:F2}ms/s poll60={hz60:F2}ms/s toggles={toggles} (one backdrop, 360-point polygon)");
        }
    }

    // -------------------------------------------------------------- Windows

    internal static class WinProbe
    {
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] private static extern long GetWindowLongPtrW(IntPtr h, int index);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
        [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr h, IntPtr rgn);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("gdi32.dll")] private static extern IntPtr CreatePolygonRgn(POINT[] pts, int count, int mode);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
        [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] private static extern long SetWindowLongPtrW(IntPtr h, int index, long value);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateEllipticRgn(int l, int t, int r, int b);
        [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dest, IntPtr a, IntPtr b, int mode);
        private static IntPtr _arrowH;

        private const uint SwpNoSize = 1, SwpNoMove = 2, SwpNoActivate = 0x10;
        private const int GwlExStyle = -20;
        private const long WsExTopmost = 0x8, WsExLayered = 0x80000, WsExTransparent = 0x20, WsExNoRedirect = 0x200000, WsExNoActivate = 0x8000000;

        private static IntPtr H(Window w) => w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

        private static int ZIndex(IntPtr target)
        {
            var i = 0;
            for (var h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = GetWindow(h, 2 /* GW_HWNDNEXT */), i++)
                if (h == target) return i;
            return -1;
        }

        private static string Ex(IntPtr h)
        {
            var ex = GetWindowLongPtrW(h, GwlExStyle);
            var parts = new List<string>();
            if ((ex & WsExTopmost) != 0) parts.Add("TOPMOST");
            if ((ex & WsExLayered) != 0) parts.Add("LAYERED");
            if ((ex & WsExTransparent) != 0) parts.Add("TRANSPARENT");
            if ((ex & WsExNoRedirect) != 0) parts.Add("NOREDIRECTIONBITMAP");
            if ((ex & WsExNoActivate) != 0) parts.Add("NOACTIVATE");
            return $"0x{ex:X}[{string.Join('|', parts)}]";
        }

        private static string RegionKind(IntPtr h)
        {
            var rgn = CreateRectRgn(0, 0, 0, 0);
            try
            {
                return GetWindowRgn(h, rgn) switch
                {
                    0 => "ERROR(none)",
                    1 => "NULLREGION",
                    2 => "SIMPLEREGION",
                    3 => "COMPLEXREGION",
                    var k => $"?{k}"
                };
            }
            finally { DeleteObject(rgn); }
        }

        private static void Report(string stage, Window orb, Window back)
        {
            var o = H(orb); var b = H(back);
            Console.WriteLine(
                $"{stage}: orb.z={ZIndex(o)} back.z={ZIndex(b)} backBehindOrb={ZIndex(b) > ZIndex(o)} " +
                $"orb.ex={Ex(o)} back.ex={Ex(b)} back.rgn={RegionKind(b)} back.visible={IsWindowVisible(b)}");
        }

        private static string HitAt(Window back, Point local, Window orb)
        {
            var px = back.PointToScreen(local);
            var h = GetAncestor(WindowFromPoint(new POINT { X = px.X, Y = px.Y }), 2 /* GA_ROOT */);
            return h == H(back) ? "backdrop" : h == H(orb) ? "orb" : h != IntPtr.Zero && h == _arrowH ? "arrow"
                : h == GetDesktopWindow() ? "desktop" : $"other(0x{h:X})";
        }

        private static void HitReport(string stage, Window back, Window orb) =>
            Console.WriteLine($"{stage}: inside={HitAt(back, Probe.Inside, orb)} corner={HitAt(back, Probe.Corner, orb)} orbMid={HitAt(back, Probe.OrbMid, orb)}");

        private static void PlaceBehind(Window back, Window orb) =>
            SetWindowPos(H(back), H(orb), 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

        private static void ApplyRegion(Window back, Window? holeFor = null)
        {
            var scale = back.RenderScaling;
            var pts = Probe.Silhouette()
                .Select(p => new POINT { X = (int)Math.Round(p.X * scale), Y = (int)Math.Round(p.Y * scale) })
                .ToArray();
            var rgn = CreatePolygonRgn(pts, pts.Length, 2 /* WINDING */);
            if (holeFor is not null)
            {
                // T6: the orb's disc cut out of the region, so the backdrop can
                // never take an orb's click even if the z-order were wrong.
                var o = holeFor.PointToScreen(new Point(0, 0));
                var b = back.PointToScreen(new Point(0, 0));
                var d = (int)Math.Round(Probe.OrbSize * scale);
                var hole = CreateEllipticRgn(o.X - b.X, o.Y - b.Y, o.X - b.X + d, o.Y - b.Y + d);
                Console.WriteLine($"  CombineRgn(DIFF) -> {CombineRgn(rgn, rgn, hole, 4 /* RGN_DIFF */)}");
                DeleteObject(hole);
            }
            // The system owns the region after a successful SetWindowRgn; do not delete it.
            Console.WriteLine($"  SetWindowRgn -> {SetWindowRgn(H(back), rgn, true)} (scale={scale})");
        }

        public static async Task Run(PixelPoint at)
        {
            // Topmost, as the plan's PlaceBehindAll assumes: inserting after a
            // topmost window keeps a window in the topmost band.
            var back = new Backdrop(topmost: true) { Position = at };
            var orb = new OrbStandIn();

            back.Show();
            await Probe.Pause();
            var scale = back.RenderScaling;
            orb.Position = new PixelPoint(
                at.X + (int)((Probe.Size - Probe.OrbSize) / 2 * scale),
                at.Y + (int)((Probe.Size - Probe.OrbSize) / 2 * scale));
            orb.Show();
            await Probe.Pause();
            Report("w0-shown(back first, orb second)", orb, back);

            back.Hide(); await Probe.Pause(200); back.Show(); await Probe.Pause();
            Report("w1-back-reshown(no placement)", orb, back);

            // Negative control: in front of the topmost band must read as in
            // front, or the backBehindOrb=True readings prove nothing.
            SetWindowPos(H(back), new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            await Probe.Pause();
            Report("wc-CONTROL-HWND_TOPMOST", orb, back);
            HitReport("hc-CONTROL-front-no-region", back, orb);

            PlaceBehind(back, orb);
            await Probe.Pause();
            Report("w2-PlaceBehind(orb)", orb, back);

            HitReport("h0-no-region", back, orb);

            ApplyRegion(back);
            await Probe.Pause();
            Report("w3-region-set", orb, back);
            HitReport("h1-region", back, orb);

            back.Hide(); await Probe.Pause(200); back.Show(); await Probe.Pause();
            Report("w4-after-hide-show", orb, back);
            HitReport("h2-after-hide-show", back, orb);

            PlaceBehind(back, orb);
            await Probe.Pause();
            Report("w5-PlaceBehind-again", orb, back);

            back.Position = new PixelPoint(at.X + 7, at.Y + 7);
            await Probe.Pause();
            back.Position = at;
            await Probe.Pause();
            Report("w6-after-move", orb, back);
            HitReport("h3-after-move", back, orb);

            back.Width = Probe.Size + 10;
            await Probe.Pause();
            Report("w7-after-resize", orb, back);
            HitReport("h4-after-resize", back, orb);

            orb.Hide(); await Probe.Pause(200); orb.Show(); await Probe.Pause();
            Report("w8-after-orb-reshown", orb, back);

            // A second orb shown after the backdrop was placed: does it land
            // in front (Windows puts a newly shown topmost window at the front
            // of the band, per WindowsWindowOrder's comment)?
            var orb2 = new OrbStandIn { Position = new PixelPoint(at.X + 10, at.Y + 10) };
            orb2.Show();
            await Probe.Pause();
            Console.WriteLine($"w9-new-orb-after: orb2.z={ZIndex(H(orb2))} back.z={ZIndex(H(back))} backBehindOrb2={ZIndex(H(back)) > ZIndex(H(orb2))}");
            orb2.Close();

            // T6: a team arrow. Shown after the backdrop was placed, made
            // click-through the way LinkWindow.MakeClickThrough does it.
            var arrow = new ArrowStandIn();
            arrow.Position = new PixelPoint(
                at.X + (int)(ArrowStandIn.Offset.X * scale), at.Y + (int)(ArrowStandIn.Offset.Y * scale));
            arrow.Show();
            await Probe.Pause();
            _arrowH = H(arrow);

            void Arrow(string stage) => Console.WriteLine(
                $"{stage}: arrow.z={ZIndex(_arrowH)} back.z={ZIndex(H(back))} arrowAboveBack={ZIndex(_arrowH) < ZIndex(H(back))} " +
                $"arrow.ex={Ex(_arrowH)} hitUnderArrow={HitAt(back, Probe.Inside, orb)} arrow.opened={arrow.OpenedCount}");

            Arrow("t0-CONTROL-arrow-not-click-through");
            SetWindowLongPtrW(_arrowH, GwlExStyle, GetWindowLongPtrW(_arrowH, GwlExStyle) | WsExTransparent | WsExLayered);
            await Probe.Pause();
            Arrow("t1-arrow-click-through");

            arrow.Hide(); await Probe.Pause(200); arrow.Show(); await Probe.Pause();
            Arrow("t2-after-arrow-pool-hide-show");

            back.Hide(); await Probe.Pause(200); back.Show(); await Probe.Pause();
            Arrow("t3-after-backdrop-hide-show");

            // What LinkWindow actually does: MakeClickThrough from Opened, which
            // fires on every Show. Does it run after Avalonia rewrites the
            // ex-style, so the pooled arrow comes back click-through?
            arrow.Opened += (_, _) =>
                SetWindowLongPtrW(_arrowH, GwlExStyle, GetWindowLongPtrW(_arrowH, GwlExStyle) | WsExTransparent | WsExLayered);
            arrow.Hide(); await Probe.Pause(200); arrow.Show(); await Probe.Pause();
            Arrow("t3b-pool-hide-show-with-Opened-handler");
            arrow.Hide(); await Probe.Pause(200); arrow.Show(); await Probe.Pause();
            Arrow("t3c-again");

            // PlaceBehindAll's rule: behind the lowest of our own topmost,
            // non-backdrop windows (orb and arrow here).
            var lowest = ZIndex(H(orb)) > ZIndex(_arrowH) ? H(orb) : _arrowH;
            SetWindowPos(H(back), lowest, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            await Probe.Pause();
            Arrow("t4-PlaceBehindAll(orb,arrow)");
            Console.WriteLine($"t4b: orb.z={ZIndex(H(orb))} backBehindOrb={ZIndex(H(back)) > ZIndex(H(orb))}");

            // A backdrop shown for the first time after the orbs exist — the
            // case w0 did not isolate.
            var back2 = new Backdrop(topmost: true) { Position = new PixelPoint(at.X + 20, at.Y + 20) };
            back2.Show();
            await Probe.Pause();
            Console.WriteLine($"t5-new-backdrop-first-show: back2.z={ZIndex(H(back2))} orb.z={ZIndex(H(orb))} arrow.z={ZIndex(_arrowH)} back2InFrontOfOrb={ZIndex(H(back2)) < ZIndex(H(orb))}");
            back2.Close();

            // T6: the orb's disc cut out of the region. Forced in front (the
            // wrong z-order), a plain region takes the orb's centre (hc above);
            // with the hole, the orb keeps it.
            ApplyRegion(back, holeFor: orb);
            SetWindowPos(H(back), new IntPtr(-1), 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            await Probe.Pause();
            Report("t6-hole-and-forced-front", orb, back);
            HitReport("h5-hole-forced-front", back, orb);

            arrow.Close();
            _arrowH = IntPtr.Zero;

            back.Close();
            orb.Close();
        }
    }
}
