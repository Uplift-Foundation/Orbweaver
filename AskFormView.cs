using System.Linq;

namespace Orbweaver
{
    // What a panel shows for a question form (CB-307 addendum item 3), from the
    // parser's AskForm. Pure and shared by the local session and the peer one, so
    // a form reads the same wherever the pane is.
    //
    // Two things beyond the parser's own prompt: the option labelled
    // "Type something" is marked FreeText, so the panel opens a text box for it
    // instead of pressing its key; and a multi-question form says "Question n of
    // m" — but only when the screen says which tab is current. The terminal draws
    // the current tab in reverse video, which a captured screen does not carry,
    // so the parser infers it, and an inference it cannot make is -1 or the
    // Submit tab, where nothing is claimed rather than a guess.
    internal static class AskFormView
    {
        // The key of the explicit "Submit" button a multi-select question gets.
        // Not a digit: the terminal's Submit row is unnumbered, so there is no
        // key to press, and pressing it is a different act (Right, which moves
        // on with the ticks kept). A sentinel no screen can produce, so no
        // option read off a screen can ever be mistaken for it.
        internal const string CommitKey = "commit";

        internal static ChatPrompt ToPrompt(AskForm form)
        {
            var ticked = form.Checked ?? System.Array.Empty<string>();

            var options = form.Prompt.Options
                // Typing into a multi-select's "Type something" row was never
                // captured, and a digit on it would toggle a box rather than
                // open a field, so that row is not offered at all.
                .Where(o => !(form.MultiSelect && o.Key == form.FreeTextKey))
                .Select(o => o with
                {
                    FreeText = !form.MultiSelect && o.Key == form.FreeTextKey,

                    // A multi-select is a checklist: its boxes are a separate
                    // fact the parser reads off the screen, and a digit toggles
                    // the row without leaving the question.
                    Label = form.MultiSelect && o.Key != form.ChatAboutKey
                        ? (ticked.Contains(o.Key) ? "\u2611 " : "\u2610 ") + o.Label
                        : o.Label,
                })
                .ToList();

            // A multi-select question is finished with its unnumbered Submit
            // row, which is a button here: ticking is a digit, committing is its
            // own explicit act, matched by the row being there at all.
            if (form.MultiSelect && form.HasSubmitRow)
                options.Add(new ChatPromptOption(CommitKey, "Submit"));

            return form.Prompt with
            {
                Title = TitleOf(form),
                Options = options,
                Progress = ProgressOf(form),

                // The Submit tab's two buttons are answers that cannot be taken
                // back: "Submit answers" sends everything, and "Cancel" declines
                // the whole form. Armed, so neither fires on a press that began
                // before the buttons had settled or is dragged off them, and
                // neither is ever pressed by anything but a deliberate click.
                ArmedActivation = OnSubmitTab(form),
            };
        }

        // The review screen lists what is about to be sent; showing it under the
        // question is what makes "Submit answers" an informed click.
        internal static string TitleOf(AskForm form)
        {
            if (form.Review is not { Count: > 0 } review) return form.Prompt.Title;

            return form.Prompt.Title + "\n"
                + string.Join("\n", review.Select(r => $"\u25cf {r.Question} \u2192 {r.Answer}"));
        }

        // The form is showing its Submit tab ("Review your answers").
        internal static bool OnSubmitTab(AskForm form) =>
            form.IsReview
            || form.CurrentTab >= 0 && form.CurrentTab < form.Tabs.Count
            && form.Tabs[form.CurrentTab].State == AskTabState.Submit;

        // A prompt the far Buddy read off its own pane and sent over the link, as
        // the panel shows it. Tab states travel as lowercase words; anything
        // unrecognised reads as Open, the cautious end (it can only withhold a
        // question number, never invent one).
        internal static ChatPrompt FromWire(MirrorProtocol.WirePrompt wire)
        {
            var tabs = (wire.Tabs ?? System.Array.Empty<MirrorProtocol.WireTab>())
                .Select(t => new AskTab(t.Name, t.State switch
                {
                    "answered" => AskTabState.Answered,
                    "submit" => AskTabState.Submit,
                    _ => AskTabState.Open,
                }))
                .ToList();

            var prompt = new ChatPrompt(
                wire.Title,
                wire.Options.Select(o => new ChatPromptOption(o.Key, o.Label)).ToList());

            return ToPrompt(new AskForm(
                prompt, tabs, wire.Current, wire.FreeTextKey, wire.ChatAboutKey,
                MultiSelect: wire.MultiSelect, Checked: wire.Checked, HasSubmitRow: wire.HasSubmitRow,
                CursorKey: wire.CursorKey, IsReview: wire.Review is not null,
                Review: wire.Review?.Select(r => new AskReviewItem(r.Question, r.Answer)).ToList(),
                SubmitAnswersKey: wire.SubmitAnswersKey, CancelKey: wire.CancelKey));
        }

        // Questions, not tabs: the Submit tab is where the form ends, not one
        // of its questions. A single-question form claims nothing, since "1 of
        // 1" is noise.
        internal static string? ProgressOf(AskForm form)
        {
            var questions = form.Tabs.Count(t => t.State != AskTabState.Submit);
            if (questions < 2) return null;

            if (form.CurrentTab < 0 || form.CurrentTab >= form.Tabs.Count) return null;
            if (form.Tabs[form.CurrentTab].State == AskTabState.Submit) return null;

            return $"Question {form.CurrentTab + 1} of {questions}";
        }
    }
}
