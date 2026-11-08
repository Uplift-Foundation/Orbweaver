using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace Orbweaver
{
    // WezTerm: `wezterm cli get-text --pane-id <n>`, the pane the session exports
    // as WEZTERM_PANE. The visible viewport by default, no --escapes. CB-313
    // note: each WezTerm process has its own mux socket, so a pane id alone
    // addresses the right one only through the WEZTERM_UNIX_SOCKET the session
    // inherited; the app's own typer works the same way and this matches it.
    internal sealed class WezTermScreenReader : IScreenReader
    {
        internal static readonly WezTermScreenReader Instance = new();

        internal static string[] Args(SessionStatus status) => ScreenReaderCommands.WezTerm(status.TermId);

        [ExcludeFromCodeCoverage]
        public Task<string?> ReadAsync(SessionStatus? status)
        {
            if (status is null || string.IsNullOrEmpty(status.TermId)) return Task.FromResult<string?>(null);

            return Task.Run<string?>(() =>
            {
                try
                {
                    var wezterm = TerminalFocuser.OnPath("wezterm");
                    return wezterm is not null && TerminalFocuser.TryRun(wezterm, 5000, out var screen, Args(status))
                        ? ScreenReaders.Normalise(screen)
                        : null;
                }
                catch { return null; }
            });
        }
    }
}
