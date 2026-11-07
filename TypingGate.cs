using System;
using System.Threading.Tasks;

namespace Orbweaver
{
    // Whether text may be typed into this session's terminal right now
    // (CB-307's addendum).
    //
    // Typing is keystrokes into a pane, and keystrokes mean whatever the pane
    // is showing. At the prompt they are a message; on a dialog or a
    // navigated list like `/permissions` they move a cursor, toggle an item or
    // answer a question nobody meant to answer. That happened for real: a line
    // sent from the MacBook's panel landed in a mini session sitting on
    // `/permissions`. So every typed path — the mirror's INPUT, both slash
    // paths, and the local panel — asks this first.
    //
    // Two reads, in order:
    //
    //  1. **Claude Code's own session record says `waiting`.** It writes that
    //     when its TUI goes modal and clears it within seconds of the dialog
    //     closing (measured on the mini, 2026-11-06). Only `waiting` is
    //     trusted: the same record said `busy` for two days about a session
    //     sitting idle at its prompt, so `busy` and `idle` decide nothing here.
    //     Asked first because it is one cached stat, and because for the
    //     routes below with no screen to read it is the only guard there is.
    //  2. **The screen**, where it can be read (tmux only — CapturePane has no
    //     other route). A prompt allows; a dialog refuses, naming it; anything
    //     else refuses, including a capture that failed or threw. Unknown
    //     refuses on purpose: this is the arm that has to fail safe.
    //
    // **The known gap:** Kitty, WezTerm, iTerm2, Terminal.app and the Windows
    // console have no capture, so for them only the record protects. Refusing
    // them outright would switch remote typing off for every such session,
    // which is a bigger regression than the one fixed here. Closing the gap
    // needs a screen read per route and is its own change.
    internal readonly record struct TypingVerdict(bool Allowed, string? Why)
    {
        internal static readonly TypingVerdict Allow = new(true, null);

        // How the screen read, for a log line and nothing else: "capture=null",
        // "capture=threw:<exception type>", or "lines=<n> check=<PaneScreen.Read's word>". Never
        // any of the screen's text. Null when the screen was not read.
        internal string? Diagnostic { get; init; }
    }

    internal static class TypingGate
    {
        internal const string WaitingWhy = "Claude Code reports it is waiting on a dialog";
        internal const string UnreadableWhy = "its screen could not be read as a prompt";

        internal static string DialogWhy(string title) => "it is showing a dialog: " + title;

        // The sentence a slash refusal carries back, wrapping the gate's own
        // reason; the near panel puts it after "can't run from here —".
        internal static string NotAtPromptReason(string why) =>
            "the terminal there isn't at its prompt (" + why + ")";

        // Type, but only past the gate. The one thing every typed path calls.
        //
        // **Ungated when neither read is wired**, the same contract CanDeliver
        // documents on the mirror's seams: a harness or build that predates
        // this behaves exactly as it always has. Wired, the screen is read only
        // when there is a capture and the route says the pane is readable
        // (canReadPane null means "yes, if there is a capture" — a test seam
        // that supplies a screen means it). Refusal returns the gate's reason
        // and types nothing.
        internal static async Task<(bool Typed, string? Refusal, string? Diagnostic)> GuardedTypeAsync(
            SessionStatus status,
            string text,
            Func<SessionStatus, string, Task<bool>> typeInto,
            Func<SessionStatus, string?>? recordStatus,
            Func<SessionStatus, Task<string?>>? capturePane,
            Func<SessionStatus, bool>? canReadPane)
        {
            if (recordStatus is not null || capturePane is not null)
            {
                var verdict = await JudgeAsync(
                    status,
                    recordStatus ?? (_ => null),
                    capturePane ?? (_ => Task.FromResult<string?>(null)),
                    capturePane is not null && (canReadPane?.Invoke(status) ?? true)).ConfigureAwait(false);

                if (!verdict.Allowed) return (false, verdict.Why, verdict.Diagnostic);
            }

            return (await typeInto(status, text).ConfigureAwait(false), null, null);
        }

        internal static async Task<TypingVerdict> JudgeAsync(
            SessionStatus status,
            Func<SessionStatus, string?> recordStatus,
            Func<SessionStatus, Task<string?>> capturePane,
            bool paneReadable)
        {
            if (string.Equals(recordStatus(status), "waiting", StringComparison.Ordinal))
                return new TypingVerdict(false, WaitingWhy);

            if (!paneReadable) return TypingVerdict.Allow;

            string? screen;
            try { screen = await capturePane(status).ConfigureAwait(false); }
            catch (Exception e)
            {
                // The type only: a message can carry a path or a pane's text.
                return new TypingVerdict(false, UnreadableWhy) { Diagnostic = "capture=threw:" + e.GetType().Name };
            }

            if (screen is null) return new TypingVerdict(false, UnreadableWhy) { Diagnostic = "capture=null" };

            var (reading, check) = PaneScreen.Read(screen);
            var diagnostic = $"lines={ChatTranscript.ScreenLines(screen).Length} check={check}";

            return reading switch
            {
                PaneReading.Prompt => TypingVerdict.Allow with { Diagnostic = diagnostic },
                PaneReading.Dialog => new TypingVerdict(false, DialogWhy(ChatTranscript.ParseDialog(screen)!.Title))
                    { Diagnostic = diagnostic },
                _ => new TypingVerdict(false, UnreadableWhy) { Diagnostic = diagnostic },
            };
        }
    }
}
