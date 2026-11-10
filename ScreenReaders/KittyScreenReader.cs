using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace Orbweaver
{
    // kitty: `kitty @ get-text --match id:<n> --extent screen`, the window the
    // session exports as KITTY_WINDOW_ID. `--extent screen` is the visible
    // screen, not the scrollback. Remote control is OFF unless the user turned
    // it on, so a kitty without it is null here, as it is a route with no typer
    // (TerminalFocuser.KittyIsListening is what keeps the route from being
    // chosen at all).
    internal sealed class KittyScreenReader : IScreenReader
    {
        internal static readonly KittyScreenReader Instance = new();

        internal static string[] Args(SessionStatus status) => ScreenReaderCommands.Kitty(status.TermId);

        [ExcludeFromCodeCoverage]
        public Task<string?> ReadAsync(SessionStatus? status)
        {
            if (status is null || string.IsNullOrEmpty(status.TermId)) return Task.FromResult<string?>(null);

            return Task.Run<string?>(() =>
            {
                try
                {
                    var kitty = TerminalFocuser.OnPath("kitty");
                    return kitty is not null && TerminalFocuser.TryRun(kitty, 5000, out var screen, Args(status))
                        ? ScreenReaders.Normalise(screen)
                        : null;
                }
                catch { return null; }
            });
        }
    }
}
