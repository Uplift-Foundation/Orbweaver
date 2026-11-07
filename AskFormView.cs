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
        internal static ChatPrompt ToPrompt(AskForm form)
        {
            var options = form.Prompt.Options
                .Select(o => o with { FreeText = o.Key == form.FreeTextKey, Label = CheckboxLabel(o.Label) })
                .ToList();

            return form.Prompt with
            {
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

        // The terminal draws a multi-select's checkboxes as "[ ]" and "[✔]", and
        // a digit toggles that row without leaving the question. Shown as real
        // checkbox glyphs, one label per row, so a panel reads as a checklist; the
        // option's key is still the digit that toggles it.
        internal static string CheckboxLabel(string label) =>
            label.StartsWith("[ ] ", System.StringComparison.Ordinal) ? "\u2610 " + label[4..]
            : label.StartsWith("[\u2714] ", System.StringComparison.Ordinal) ? "\u2611 " + label[4..]
            : label;

        // The form is showing its Submit tab ("Review your answers").
        internal static bool OnSubmitTab(AskForm form) =>
            form.CurrentTab >= 0 && form.CurrentTab < form.Tabs.Count
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

            return ToPrompt(new AskForm(prompt, tabs, wire.Current, wire.FreeTextKey, wire.ChatAboutKey));
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
