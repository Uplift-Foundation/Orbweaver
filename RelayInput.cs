namespace Orbweaver
{
    // A phone's `input`, typed into a session (CB-316, frames-v4
    // sealed_plaintext.input), and the first message of a phone's launch.
    //
    // The one route every typed path takes: CB-307's TypingGate (the record
    // must not say waiting, and the screen must read as a prompt, not a dialog
    // or a navigated list), then PR #227's settle rule generalised to typing —
    // a second read 150 ms later must be identical and still a prompt — and
    // only on a route that writes without focus. Terminal.app is never typed
    // into from a phone, and neither is a session with no route or one Claude
    // Desktop hosts (both have no route at all).
    internal sealed class RelayInput
    {
        internal sealed record Seams(
            Func<SessionStatus, TerminalTyping.Route> Route,
            Func<SessionStatus, Task<string?>> Read,
            Func<SessionStatus, string, Task<bool>> Type,
            Func<TimeSpan, Task> Delay);

        // Typed, or refused with a contract code and (for not_at_prompt and
        // typing_failed) the desktop's own sentence.
        internal readonly record struct Outcome(string? Code, string? Why)
        {
            internal bool Typed => Code is null;
            internal static readonly Outcome Done = new(null, null);
        }

        internal const string ChangingWhy = "its screen was still changing";
        internal const string RefusedWhy = "the terminal refused the text";

        private readonly Seams _seams;

        // One typing at a time across every session: two phones (or a launch's
        // first message and an input) must not interleave keystrokes, and the
        // settle reads are only meaningful with nothing of ours typing between.
        private readonly SemaphoreSlim _one = new(1, 1);

        internal RelayInput(Seams seams) => _seams = seams;

        internal static Outcome Refuse(string code, string? why = null) => new(code, why);

        internal async Task<Outcome> TypeAsync(SessionStatus status, string text, bool allowEmpty = false)
        {
            if (!RelayText.Typeable(text, allowEmpty)) return Refuse(RelayReplies.BadInput);
            if (text.Length == 0) return Outcome.Done;   // a launch with no first message types nothing
            if (!TerminalPress.Silent(_seams.Route(status))) return Refuse(RelayReplies.Untypeable);

            await _one.WaitAsync().ConfigureAwait(false);
            try
            {
                if (await JudgeAsync(status).ConfigureAwait(false) is { } refused) return refused;
                return await _seams.Type(status, text).ConfigureAwait(false)
                    ? Outcome.Done
                    : Refuse(RelayReplies.TypingFailed, RefusedWhy);
            }
            finally
            {
                _one.Release();
            }
        }

        // Null when the session is at its prompt and settled; else the
        // not_at_prompt refusal with the gate's own sentence.
        internal async Task<Outcome?> JudgeAsync(SessionStatus status)
        {
            string? first = null;
            var verdict = await TypingGate.JudgeAsync(
                status,
                s => s.State,
                async s => first = await _seams.Read(s).ConfigureAwait(false),
                paneReadable: true).ConfigureAwait(false);
            if (!verdict.Allowed) return Refuse(RelayReplies.NotAtPrompt, verdict.Why);

            await _seams.Delay(PromptPipeline.SettleCheck).ConfigureAwait(false);
            var second = await _seams.Read(status).ConfigureAwait(false);
            if (second is null || second != first) return Refuse(RelayReplies.NotAtPrompt, ChangingWhy);
            return null;
        }
    }
}
