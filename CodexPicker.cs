using System;
using System.Linq;
using System.Threading.Tasks;

namespace Orbweaver
{
    // CB-404: setting a Codex older than 0.164 through its own /model picker
    // (plan 2.4), because those versions have no `/model <id>
    // --reasoning=<level>` argument form.
    //
    // Measured end to end on 0.154.0 in a private tmux pane:
    //   1. "/model" + Enter shows "Select Model and Effort" with numbered model rows;
    //   2. the row's digit shows "Select Reasoning Level for <id>";
    //   3. the level's digit closes the picker and prints "Model changed to <id> <level>";
    //   and Esc twice from step 2 changed nothing.
    //
    // Every key is sent only after the screen it is meant for has been read,
    // and Esc is the way out of any screen that is not the one expected. A
    // choice that is not on the first screen (more than nine rows, the "More
    // reasoning…" sub-list) is reported as a failure, never guessed at. The
    // picker also rewrites ~/.codex/config.toml, which the flyout says before
    // anything is chosen (RunText.CodexPickerDefault).
    internal static class CodexPicker
    {
        internal sealed record Seams(
            // Types "/model" and submits it, through the typing gate (never into
            // an open dialog, never mid-turn). False when it did not go.
            Func<string, Task<bool>> TypeLine,

            // Presses one key: a digit, "Escape" or "Enter".
            Func<string, Task<bool>> Press,

            // The pane as it is now, or null when it cannot be read.
            Func<Task<string?>> ReadScreen,

            Func<TimeSpan, Task> Delay);

        // How often and how long to wait for a screen. The picker draws within
        // a frame or two (measured well under a second on 0.154.0); the bound is
        // generous so a busy machine is not read as a refusal.
        internal static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);
        internal const int Attempts = 12;

        internal const string TypeRefused = "the terminal refused the text";
        internal const string NoPicker = "Codex's /model picker did not open";
        internal const string Unreadable = "the terminal can't be read, so the picker can't be driven safely";
        internal const string KeyRefused = "the terminal refused a key";
        internal const string NotConfirmed = "Codex didn't confirm the change";

        internal static string NotListed(string what) =>
            $"{what} isn't on the first screen of this Codex's /model picker, so it wasn't chosen";

        internal static string WrongScreen(string id) =>
            $"Codex's picker showed the levels for a different model than {id}, so nothing was chosen";

        // Chooses `model` and `effort`; a null model keeps the highlighted one. When `effortRequired` is false (a
        // model change), effort is only a preference — the level the session
        // was on — and a model without it takes the highlighted level instead.
        // Null on a confirmed change, or the sentence to show.
        internal static async Task<string?> SetAsync(Seams seams, string? model, string? effort, bool effortRequired)
        {
            if (!await seams.TypeLine("/model").ConfigureAwait(false)) return TypeRefused;

            // Screen one: the models.
            var first = await WaitFor(seams, s => s.Stage == RunReadings.CodexPickerStage.Models).ConfigureAwait(false);
            // An unreadable pane gets no key at all: Esc is a way out of a screen
            // that was read, not a blind keystroke.
            if (first.Failure is { } f1) return await Out(seams, f1 == Unreadable ? 0 : 1, f1).ConfigureAwait(false);
            var models = first.Screen!;

            // On an effort change the model is only a preference (the one the
            // session is on, by its reading), with the highlighted row — the
            // picker's own idea of the current model — as the fallback.
            var modelRow = (model is null ? null : models.Rows.FirstOrDefault(r => string.Equals(r.Id, model, StringComparison.OrdinalIgnoreCase)))
                ?? (effortRequired || model is null ? models.Rows.FirstOrDefault(r => r.Highlighted) : null);
            if (modelRow is null) return await Out(seams, 1, NotListed(model ?? "the current model")).ConfigureAwait(false);

            if (!await seams.Press(modelRow.Digit.ToString()).ConfigureAwait(false)) return await Out(seams, 1, KeyRefused).ConfigureAwait(false);

            // Screen two: the levels, for that model and no other.
            var second = await WaitFor(seams, s => s.Stage == RunReadings.CodexPickerStage.Levels).ConfigureAwait(false);
            if (second.Failure is { } f2) return await Out(seams, f2 == Unreadable ? 0 : 2, f2).ConfigureAwait(false);
            var levels = second.Screen!;
            if (!string.Equals(levels.ForModel, modelRow.Id, StringComparison.OrdinalIgnoreCase))
                return await Out(seams, 2, WrongScreen(modelRow.Id)).ConfigureAwait(false);

            var levelRow = (effort is null ? null : levels.Rows.FirstOrDefault(r => string.Equals(r.Id, effort, StringComparison.OrdinalIgnoreCase)))
                ?? (effortRequired && effort is not null ? null : levels.Rows.FirstOrDefault(r => r.Highlighted));
            if (levelRow is null) return await Out(seams, 2, NotListed(effort ?? "the current level")).ConfigureAwait(false);

            if (!await seams.Press(levelRow.Digit.ToString()).ConfigureAwait(false)) return await Out(seams, 2, KeyRefused).ConfigureAwait(false);

            // The confirmation line, naming the model chosen.
            for (var i = 0; i < Attempts; i++)
            {
                var screen = await seams.ReadScreen().ConfigureAwait(false);
                var changed = RunReadings.FromCodexChanged(screen);
                if (string.Equals(changed.Model, modelRow.Id, StringComparison.OrdinalIgnoreCase)
                    && (changed.Effort is null || string.Equals(changed.Effort, levelRow.Id, StringComparison.OrdinalIgnoreCase)))
                    return null;
                await seams.Delay(Poll).ConfigureAwait(false);
            }

            return NotConfirmed;
        }

        private sealed record Wait(RunReadings.CodexPickerScreen? Screen, string? Failure);

        // Reads until the expected screen is up. Any other picker screen, an
        // unreadable pane, or nothing appearing in time is a failure.
        private static async Task<Wait> WaitFor(Seams seams, Func<RunReadings.CodexPickerScreen, bool> expected)
        {
            for (var i = 0; i < Attempts; i++)
            {
                var text = await seams.ReadScreen().ConfigureAwait(false);
                if (text is null) return new(null, Unreadable);
                if (RunReadings.ParseCodexPicker(text) is { } screen)
                    return expected(screen) ? new(screen, null) : new(null, NoPicker);
                await seams.Delay(Poll).ConfigureAwait(false);
            }

            return new(null, NoPicker);
        }

        // Backs out with one Esc per screen deep, then answers the failure.
        private static async Task<string> Out(Seams seams, int depth, string failure)
        {
            for (var i = 0; i < depth; i++) await seams.Press("Escape").ConfigureAwait(false);
            return failure;
        }
    }
}
