using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace Orbweaver
{
    // Terminal.app: the tab's visible contents, addressed by tty (the only
    // handle that survives a tab moving between windows).
    //
    // CB-313, finding 5, and it is the whole reason this file has a comment:
    // the sdef's `contents` property, written by name, collides with
    // AppleScript's own dereference and returns the TAB ("tab 1 of window id
    // N"), not its text. The property goes by its four-character code,
    // «property pcnt» (from `sdef /System/Applications/Utilities/Terminal.app`),
    // and on the tab expression in place: a variable holding the tab still
    // returns the tab. `history` would return the whole scrollback and works
    // by name, but the visible screen is what a dialog is drawn on.
    internal sealed class TerminalAppScreenReader : IScreenReader
    {
        internal static readonly TerminalAppScreenReader Instance = new();

        internal static string[] Args(SessionStatus status) =>
            ScreenReaderCommands.TerminalApp(TerminalTyping.DevicePath(status.Tty));

        [ExcludeFromCodeCoverage]
        public Task<string?> ReadAsync(SessionStatus? status)
        {
            if (status is null || string.IsNullOrEmpty(status.Tty) || !OperatingSystem.IsMacOS())
                return Task.FromResult<string?>(null);

            return Task.Run<string?>(() =>
            {
                try
                {
                    return TerminalFocuser.TryRun("/usr/bin/osascript", 15000, out var screen, Args(status))
                        ? ScreenReaders.Normalise(screen)
                        : null;
                }
                catch { return null; }
            });
        }
    }
}
