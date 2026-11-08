using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace Orbweaver
{
    // tmux: `capture-pane -p -t <pane>`, no -e, so no escapes. The route the app
    // had before CB-315, moved behind the interface (TerminalFocuser.CapturePane
    // calls here, so there is one implementation of it).
    internal sealed class TmuxScreenReader : IScreenReader
    {
        internal static readonly TmuxScreenReader Instance = new();

        // The arguments, pure, so the socket pinning and the pane are a test.
        internal static string[] Args(SessionStatus status) =>
            TerminalFocuser.TmuxArgs(status, "capture-pane", "-p", "-t", status.TmuxPane);

        [ExcludeFromCodeCoverage]
        public Task<string?> ReadAsync(SessionStatus? status)
        {
            if (status is null || string.IsNullOrEmpty(status.TmuxPane)) return Task.FromResult<string?>(null);

            return Task.Run<string?>(() =>
            {
                try
                {
                    var tmux = TerminalFocuser.ResolveTmuxBinary(status.TmuxBin);
                    if (tmux is null) return null;

                    return TerminalFocuser.TryRun(tmux, out var screen, Args(status))
                        ? ScreenReaders.Normalise(screen)
                        : null;
                }
                catch { return null; }
            });
        }
    }
}
