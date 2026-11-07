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
                .Select(o => o with { FreeText = o.Key == form.FreeTextKey })
                .ToList();

            return form.Prompt with { Options = options, Progress = ProgressOf(form) };
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
