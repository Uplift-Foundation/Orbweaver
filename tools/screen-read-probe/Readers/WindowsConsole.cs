using System.Runtime.InteropServices;
using System.Text;

namespace Orbweaver.ScreenReadProbe;

// Windows: attach to the CLI's own console and read the visible window of the
// screen buffer. Models TerminalFocuser.SendViaConsole: a lock, FreeConsole
// first, FreeConsole in a finally. The same route reaches conhost and a
// ConPTY-hosted console (Windows Terminal), which is the question under test.
internal static class WindowsConsole
{
    private static readonly object Gate = new();

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

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfoEx(IntPtr h, ref ScreenBufferInfoEx info);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ReadConsoleOutputCharacterW(IntPtr h, [Out] char[] buf, uint len, Coord at, out uint read);

    // KEY_EVENT_RECORD inside INPUT_RECORD (20 bytes).
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public int KeyDown;
        [FieldOffset(8)] public ushort RepeatCount;
        [FieldOffset(10)] public ushort VirtualKeyCode;
        [FieldOffset(12)] public ushort VirtualScanCode;
        [FieldOffset(14)] public char Char;
        [FieldOffset(16)] public uint ControlKeyState;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteConsoleInputW(IntPtr h, InputRecord[] recs, uint len, out uint written);

    // Types into the console of `pid` the way TerminalFocuser.SendViaConsole does:
    // key-down/key-up per character, optionally bracketed as a paste, optionally
    // ending in VK_RETURN carrying '\r'. Used only to drive the CLI under test.
    internal static int Type(int pid, string text, bool paste, bool enter)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("wintype writes a Windows console");
        var body = paste ? "\u001b[200~" + text + "\u001b[201~" : text;
        var recs = new List<InputRecord>();
        void Add(char c, ushort vk)
        {
            for (var down = 1; down >= 0; down--)
                recs.Add(new InputRecord { EventType = 1, KeyDown = down, RepeatCount = 1, VirtualKeyCode = vk, Char = c });
        }
        foreach (var c in body) Add(c, 0);
        if (enter) Add('\r', 0x0D);

        lock (Gate)
        {
            FreeConsole();
            if (!AttachConsole((uint)pid))
                throw new InvalidOperationException($"AttachConsole({pid}) failed, error {Marshal.GetLastWin32Error()}");
            var h = IntPtr.Zero;
            try
            {
                h = CreateFileW("CONIN$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h == new IntPtr(-1))
                    throw new InvalidOperationException($"CONIN$ open failed, error {Marshal.GetLastWin32Error()}");
                var arr = recs.ToArray();
                if (!WriteConsoleInputW(h, arr, (uint)arr.Length, out var n) || n != arr.Length)
                    throw new InvalidOperationException($"WriteConsoleInputW wrote {n}/{arr.Length}, error {Marshal.GetLastWin32Error()}");
                return (int)n;
            }
            finally
            {
                if (h != IntPtr.Zero && h != new IntPtr(-1)) CloseHandle(h);
                FreeConsole();
            }
        }
    }

    // The read is the app's own (ScreenReaders/WindowsConsoleRead.cs), not a copy.
    internal static string Read(int pid)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("wincon reads a Windows console");

        return WindowsConsoleRead.Read((uint)pid, Gate, out var error)
            ?? throw new InvalidOperationException($"console read of pid {pid} failed, Win32 error {error}");
    }
}
