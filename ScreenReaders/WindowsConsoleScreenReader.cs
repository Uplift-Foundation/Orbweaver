using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace Orbweaver
{
    // Windows: the CLI's own console, read with AttachConsole and
    // ReadConsoleOutputCharacterW (WindowsConsoleRead has the how). Windows
    // Terminal and conhost are one case. The pid is the session's own, recorded
    // by the hook; for Grok on Windows it is the PARENT grok.exe, because the
    // child it spawns has its own hidden console and AttachConsole on it fails
    // with error 87 (CB-313). Shares TerminalFocuser.ConsoleGate with the typer.
    internal sealed class WindowsConsoleScreenReader : IScreenReader
    {
        internal static readonly WindowsConsoleScreenReader Instance = new();

        [ExcludeFromCodeCoverage]
        public Task<string?> ReadAsync(SessionStatus? status)
        {
            if (status is null || status.SessionPid <= 0 || !OperatingSystem.IsWindows())
                return Task.FromResult<string?>(null);

            return Task.Run<string?>(() =>
            {
                try { return WindowsConsoleRead.Read((uint)status.SessionPid, TerminalFocuser.ConsoleGate, out _); }
                catch { return null; }
            });
        }
    }
}
