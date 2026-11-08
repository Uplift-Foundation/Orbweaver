using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace Orbweaver
{
    // iTerm2: the session's `contents`, "the currently visible contents of the
    // session" (its sdef), addressed by the session GUID the hook records.
    //
    // CB-313: windows, tabs and sessions are addressed by INDEX, not by
    // iterating `repeat with s in sessions`, because a loop variable is a
    // reference and `contents of s` on a reference returns the session object
    // instead of its text. The typer's script iterates by reference and gets
    // away with it because `write text` is a command; a property read is not.
    // The answer is padded with trailing spaces to the window width, which the
    // parser trims.
    internal sealed class ITerm2ScreenReader : IScreenReader
    {
        internal static readonly ITerm2ScreenReader Instance = new();

        internal static string[] Args(SessionStatus status) => ScreenReaderCommands.ITerm2(status.TermId);

        [ExcludeFromCodeCoverage]
        public Task<string?> ReadAsync(SessionStatus? status)
        {
            if (status is null || string.IsNullOrEmpty(status.TermId) || !OperatingSystem.IsMacOS())
                return Task.FromResult<string?>(null);

            return Task.Run<string?>(() =>
            {
                try
                {
                    // A longer timeout than most, for the typer's reason: the first Apple Event to an
                    // application this process has not spoken to can raise a consent prompt.
                    return TerminalFocuser.TryRun("/usr/bin/osascript", 15000, out var screen, Args(status))
                        ? ScreenReaders.Normalise(screen)
                        : null;
                }
                catch { return null; }
            });
        }
    }
}
