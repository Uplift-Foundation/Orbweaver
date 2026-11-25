using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Orbweaver
{
    // Reads the visible window of a Windows console's screen buffer: attach to
    // the CLI's own console, open CONOUT$, read each row with
    // ReadConsoleOutputCharacterW. Windows Terminal (ConPTY, OpenConsole.exe)
    // and conhost are the same case, and CB-313 measured that on both.
    //
    // A file of its own with no dependency on the rest of the app, so
    // tools/screen-read-probe compiles it and the probe reads a console with
    // exactly this code. The lock is a parameter: the app passes the gate the
    // typer holds while it attaches (TerminalFocuser.ConsoleGate), because a
    // console is process-global state and two attachers would interleave.
    internal static class WindowsConsoleRead
    {
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareReadWrite = 0x00000003;
        private const uint OpenExisting = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct Coord { public short X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SmallRect { public short Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ScreenBufferInfoEx
        {
            public uint cbSize;
            public Coord dwSize;
            public Coord dwCursorPosition;
            public ushort wAttributes;
            public SmallRect srWindow;
            public Coord dwMaximumWindowSize;
            public ushort wPopupAttributes;
            public int bFullscreenSupported;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public uint[] ColorTable;
        }

        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachConsole(uint pid);

        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int which);

        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetStdHandle(int which, IntPtr handle);

        private static readonly int[] StdIds = { -10, -11, -12 };

        // CB-476: this process's standard handles, to put back after a console
        // has been attached and freed. AttachConsole points a GUI process's
        // standard handles at the console it attaches to, and FreeConsole does
        // NOT put them back, so afterwards they name handles of a console this
        // process no longer has. A child started with UseShellExecute = false
        // inherits them, and conhost started that way (the named-account launch,
        // CB-474) exits at once with code 0. Measured on the Windows PC with a
        // GUI-subsystem repro: std handles 0,0,0 before a read and 44c,458,454
        // after it, with the next conhost dead in under 3 s; restoring them, it
        // lived. Every attach in the app (this read, the console typer, the
        // console press) saves before and restores after.
        internal readonly struct StdHandles
        {
            private readonly IntPtr[] _saved;

            private StdHandles(IntPtr[] saved) => _saved = saved;

            [SupportedOSPlatform("windows")]
            internal static StdHandles Save() => new(StdIds.Select(GetStdHandle).ToArray());

            [SupportedOSPlatform("windows")]
            internal void Restore()
            {
                for (var i = 0; i < StdIds.Length; i++) SetStdHandle(StdIds[i], _saved[i]);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetConsoleScreenBufferInfoEx(SafeFileHandle output, ref ScreenBufferInfoEx info);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadConsoleOutputCharacterW(SafeFileHandle output, [Out] char[] buffer, uint length, Coord at, out uint read);

        // The rows as text: trailing blanks trimmed (the buffer is padded to its width), LF between
        // rows. Pure, so the shape is a test; the reader only fetches the rows.
        internal static string Assemble(IEnumerable<string> rows) =>
            string.Join('\n', rows.Select(r => r.TrimEnd()));

        // Null on any failure, with the Win32 error in `error` (87 is AttachConsole on a process with
        // no console of its own; 5 is access denied). Throws nothing.
        [SupportedOSPlatform("windows")]
        internal static string? Read(uint pid, object gate, out int error)
        {
            error = 0;
            lock (gate)
            {
                var std = StdHandles.Save();
                try { return ReadAttached(pid, out error); }
                finally { std.Restore(); }
            }
        }

        [SupportedOSPlatform("windows")]
        private static string? ReadAttached(uint pid, out int error)
        {
            error = 0;
            // The block is the body of the lock this used to sit in; Read holds the gate now.
            {
                FreeConsole();
                if (!AttachConsole(pid)) { error = Marshal.GetLastWin32Error(); return null; }

                try
                {
                    // CONOUT$ rather than GetStdHandle: a process started with standard handles of its
                    // own keeps them after AttachConsole. (One with none, a GUI app, is pointed at the
                    // attached console and left pointing there after FreeConsole: Read restores them.)
                    using var output = CreateFileW("CONOUT$", GenericRead | GenericWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                    if (output.IsInvalid) { error = Marshal.GetLastWin32Error(); return null; }

                    var info = new ScreenBufferInfoEx { cbSize = (uint)Marshal.SizeOf<ScreenBufferInfoEx>(), ColorTable = new uint[16] };
                    if (!GetConsoleScreenBufferInfoEx(output, ref info)) { error = Marshal.GetLastWin32Error(); return null; }

                    var width = info.srWindow.Right - info.srWindow.Left + 1;
                    var rows = new List<string>();
                    for (var row = info.srWindow.Top; row <= info.srWindow.Bottom; row++)
                    {
                        var buffer = new char[width];
                        if (!ReadConsoleOutputCharacterW(output, buffer, (uint)width, new Coord { X = info.srWindow.Left, Y = row }, out var read))
                        {
                            error = Marshal.GetLastWin32Error();
                            return null;
                        }

                        rows.Add(new string(buffer, 0, (int)read));
                    }

                    return Assemble(rows);
                }
                finally
                {
                    FreeConsole();
                }
            }
        }
    }
}
