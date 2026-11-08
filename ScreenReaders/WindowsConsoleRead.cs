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
                FreeConsole();
                if (!AttachConsole(pid)) { error = Marshal.GetLastWin32Error(); return null; }

                try
                {
                    // CONOUT$ rather than GetStdHandle: after AttachConsole this process's standard
                    // handles are the ones it started with, not the attached console's.
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
