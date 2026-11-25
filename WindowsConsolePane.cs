using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orbweaver
{
    // CB-476: the console a Windows launch started, as a CB-397 "pane", so the
    // trust question is read, answered and closed by the same LaunchRunner
    // steps as a tmux pane on the Mac.
    //
    // The pane is "con:<pid>" where the pid is the conhost.exe LaunchRunner
    // started (CB-316: `conhost.exe cmd.exe /k claude ...`). conhost itself
    // owns the console but is not a client of it, and AttachConsole on its pid
    // fails with error 6 — measured on the Windows PC, where the cmd.exe under
    // it attached first time. So every look goes through the console's client:
    // conhost's child that is not itself a conhost. The client is looked up on
    // each call, not remembered, so a console that has closed reads as null
    // rather than as whatever later reused a remembered pid.
    //
    // The read and the press are the CB-315 ones the app already uses for a
    // session's console (WindowsConsoleScreenReader, TerminalFocuser's console
    // press), under the same gate, so this adds no second way to touch a
    // console. Close kills the conhost and everything under it.
    internal static class WindowsConsolePane
    {
        internal const string Prefix = "con:";

        internal static string For(int conhostPid) => Prefix + conhostPid;

        // The conhost pid out of a pane id, or null for anything else (a tmux
        // "%N" pane, a malformed id).
        internal static int? Host(string? pane) =>
            pane is not null && pane.StartsWith(Prefix, StringComparison.Ordinal)
                && int.TryParse(pane.AsSpan(Prefix.Length), System.Globalization.NumberStyles.None, null, out var pid) && pid > 0
                ? pid
                : null;

        internal readonly record struct Row(int Pid, int Parent, string Exe);

        // The console's client: the first child of the host that is not a
        // conhost, in the order the process table lists them. Null when the
        // host has no such child (gone, or not started yet).
        internal static int? Client(int host, IEnumerable<Row> table) =>
            table.Where(r => r.Parent == host && r.Pid != host && r.Pid > 0
                    && !string.Equals(r.Exe, "conhost.exe", StringComparison.OrdinalIgnoreCase))
                .Select(r => (int?)r.Pid)
                .FirstOrDefault();

        // What a console look is addressed by: the CB-315 route picks the
        // Windows console off a SessionPid on Windows.
        internal static SessionStatus StatusFor(int client) =>
            new() { Source = SessionSource.ClaudeCode, SessionPid = client };

        // The three trust seams over a console, with the process table, the
        // read, the press, the kill and the log passed in so every arm is a
        // unit case. `read` takes the client pid and returns the screen, or
        // null with the Win32 error.
        //
        // A console that cannot be read stays no_session, as before, but now
        // says why in the log, once per failed look (at most one a second for
        // LaunchCheckIn): no client under the host, or the read's error (87:
        // no console of its own, 5: access denied). The pid only, never a folder.
        internal static LaunchRunner.TrustSeams Seams(
            Func<IEnumerable<Row>> table,
            Func<int, Task<(string? Screen, int Error)>> read,
            Func<SessionStatus, string, Func<string?, bool>, Task<(bool Pressed, string? Before)>> press,
            Action<int> kill,
            Action<string> log)
        {
            int? ClientOf(string pane) => Host(pane) is { } host ? Client(host, table()) : null;

            async Task<string?> ReadPane(string pane)
            {
                if (Host(pane) is not { } host) return null;
                if (Client(host, table()) is not { } client)
                {
                    log($"console-read-failed host={host} reason=no_client");
                    return null;
                }
                var (screen, error) = await read(client).ConfigureAwait(false);
                if (screen is null) log($"console-read-failed host={host} client={client} error={error}");
                return screen;
            }

            return new(
                ReadPane,
                (pane, key, approved) => ClientOf(pane) is { } c ? press(StatusFor(c), key, approved) : Task.FromResult((false, (string?)null)),
                pane =>
                {
                    if (Host(pane) is { } host) kill(host);
                    return Task.CompletedTask;
                });
        }

        // The real seams. Excluded: each is one line into a real console or a
        // real process; the decisions they feed are covered above.
        [ExcludeFromCodeCoverage]
        internal static LaunchRunner.TrustSeams Default() => Seams(
            () => OperatingSystem.IsWindows() ? Snapshot() : [],
            ReadConsole,
            TerminalFocuser.CaptureAndPress,
            Kill,
            line => MirrorLog.Say("peer-launch", line));

        // WindowsConsoleScreenReader's read, keeping the error it drops.
        [ExcludeFromCodeCoverage]
        private static Task<(string? Screen, int Error)> ReadConsole(int client) => Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows()) return ((string?)null, 0);
            try
            {
                var screen = ScreenReaders.Normalise(WindowsConsoleRead.Read((uint)client, TerminalFocuser.ConsoleGate, out var error));
                return (screen, error);
            }
            catch (Exception) { return ((string?)null, -1); }
        });

        [ExcludeFromCodeCoverage]
        private static void Kill(int host)
        {
            try
            {
                using var process = Process.GetProcessById(host);
                ProcessTreeKill.Kill(process, useLibproc: false);
            }
            catch (Exception) { }
        }

        // --- the process table (Toolhelp) -------------------------------------

        private const uint Th32csSnapProcess = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32W entry);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32W entry);

        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        // Every process's pid, parent and image name, or empty on failure.
        // Integration-tested on the Windows leg against a child this process
        // starts itself (WindowsConsolePaneTests).
        [SupportedOSPlatform("windows")]
        internal static IReadOnlyList<Row> Snapshot()
        {
            var rows = new List<Row>();
            var snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return rows;
            try
            {
                var entry = new ProcessEntry32W { dwSize = (uint)Marshal.SizeOf<ProcessEntry32W>() };
                for (var ok = Process32FirstW(snap, ref entry); ok; ok = Process32NextW(snap, ref entry))
                    rows.Add(new Row((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile ?? ""));
            }
            finally
            {
                CloseHandle(snap);
            }
            return rows;
        }
    }
}
