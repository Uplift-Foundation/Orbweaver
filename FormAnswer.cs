using System;
using System.Linq;
using System.Threading.Tasks;

namespace Orbweaver
{
    // Answering a dialog or AskUserQuestion form on a pane: the one procedure
    // the mirror server (for a peer's panel) and LocalCliChatSession (for this
    // machine's own) both run, so the rules below cannot drift between them
    // (CB-307 addendum item 3). Each rule is pinned to QA's real captures of
    // every form state (item3/ fixtures).
    //
    // The screen is read first and again after every step, and nothing is
    // pressed unless the dialog there is still the one the answer was chosen
    // from (same fingerprint):
    //
    //  - an option's digit. On a single-select question it selects and moves
    //    on; on a multi-select one it toggles a box and stays.
    //  - "commit" (MirrorProtocol.ActionCommit), a multi-select question only:
    //    Right, which moves on with the ticks kept — the way QA saw work.
    //    Enter on the unnumbered Submit row was never tried, so it is not used.
    //  - free text, the "Type something" option with text. That row is the
    //    field itself: the cursor is moved onto it and seen there, the text is
    //    typed and seen in the row, and only then is Enter pressed.
    //
    // **Enter on an empty "Type something" declines the whole form** (capture
    // 03), and a digit on it on a single-select question selects it empty,
    // which is the same thing. So that option is never pressed by its digit,
    // never with empty, whitespace-only or multi-line text, and Enter goes
    // only after the typed text has been read back off the row. Free text on a
    // multi-select question is refused: typing into its "[ ] Type something"
    // row was not captured.
    //
    // Returns null when the answer was given, otherwise a MirrorProtocol error
    // code; on every refusal nothing more was pressed than the code says.
    internal static class FormAnswer
    {
        internal static async Task<string?> AnswerAsync(
            SessionStatus status,
            Func<SessionStatus, Task<string?>> capture,
            Func<SessionStatus, string, Task<bool>> press,
            Func<SessionStatus, string, Task<bool>>? typeLiteral,
            string fingerprint, string? key, string? action, string? text)
        {
            var prompt = await ReadAsync(capture, status).ConfigureAwait(false);

            if (prompt is null) return MirrorProtocol.ErrNoPrompt;
            if (prompt.Fingerprint != fingerprint) return MirrorProtocol.ErrPromptChanged;

            if (action == MirrorProtocol.ActionCommit)
                return prompt.MultiSelect && prompt.HasSubmitRow
                    ? await PressAsync(press, status, "Right").ConfigureAwait(false)
                    : MirrorProtocol.ErrBadKey;

            if (action is not null) return MirrorProtocol.ErrBadKey;

            if (key is not null && key == prompt.FreeTextKey)
                return await FreeTextAsync(capture, press, typeLiteral, status, prompt, text).ConfigureAwait(false);

            if (text is not null || prompt.Options.All(o => o.Key != key)) return MirrorProtocol.ErrBadKey;

            return await PressAsync(press, status, key!).ConfigureAwait(false);
        }

        // Text the free-text row can take: something to say, on one line. An
        // empty or blank answer would be Enter on an empty field, which
        // declines the form; a newline would be Enter in the middle of it.
        internal static bool IsUsableFreeText(string? text) =>
            !string.IsNullOrWhiteSpace(text) && text.IndexOfAny(new[] { '\n', '\r' }) < 0;

        // Whether the row reads back as what was typed — whole, or cut short
        // with an ellipsis on a narrow pane.
        internal static bool ShowsTyped(string label, string text)
        {
            var shown = label.Trim();
            var wanted = text.Trim();
            if (shown == wanted) return true;
            return shown.EndsWith('…') && shown.Length > 1 && wanted.StartsWith(shown[..^1].TrimEnd(), StringComparison.Ordinal);
        }

        private static async Task<MirrorProtocol.WirePrompt?> ReadAsync(
            Func<SessionStatus, Task<string?>> capture, SessionStatus status)
        {
            string? screen;
            try { screen = await capture(status).ConfigureAwait(false); }
            catch { screen = null; }

            return MirrorProtocol.PromptFromScreen(screen);
        }

        private static async Task<string?> PressAsync(Func<SessionStatus, string, Task<bool>> press, SessionStatus status, string key)
        {
            try { return await press(status, key).ConfigureAwait(false) ? null : MirrorProtocol.ErrTypeFailed; }
            catch { return MirrorProtocol.ErrTypeFailed; }
        }

        private static async Task<string?> FreeTextAsync(
            Func<SessionStatus, Task<string?>> capture,
            Func<SessionStatus, string, Task<bool>> press,
            Func<SessionStatus, string, Task<bool>>? typeLiteral,
            SessionStatus status, MirrorProtocol.WirePrompt prompt, string? text)
        {
            if (!IsUsableFreeText(text)) return MirrorProtocol.ErrBadKey;
            if (prompt.MultiSelect || typeLiteral is null) return MirrorProtocol.ErrUnsupported;

            var keys = prompt.Options.Select(o => o.Key).ToList();
            var from = prompt.CursorKey is { } c ? keys.IndexOf(c) : -1;
            var to = keys.IndexOf(prompt.FreeTextKey!);
            if (from < 0) return MirrorProtocol.ErrTypeFailed;

            for (var i = 0; i < Math.Abs(to - from); i++)
                if (await PressAsync(press, status, to > from ? "Down" : "Up").ConfigureAwait(false) is { } failed)
                    return failed;

            // On the row, and still the same form.
            var there = await ReadAsync(capture, status).ConfigureAwait(false);
            if (there is null || there.Fingerprint != prompt.Fingerprint || there.CursorKey != prompt.FreeTextKey)
                return MirrorProtocol.ErrTypeFailed;

            bool typed;
            try { typed = await typeLiteral(status, text!).ConfigureAwait(false); }
            catch { typed = false; }
            if (!typed) return MirrorProtocol.ErrTypeFailed;

            // The row now shows the text (capture 05), and nothing else moved.
            var filled = await ReadAsync(capture, status).ConfigureAwait(false);
            var row = filled?.Options.FirstOrDefault(o => o.Key == prompt.FreeTextKey);
            if (filled is null || row is null || filled.CursorKey != prompt.FreeTextKey || !ShowsTyped(row.Label, text!))
                return MirrorProtocol.ErrTypeFailed;

            return await PressAsync(press, status, "Enter").ConfigureAwait(false);
        }
    }
}
