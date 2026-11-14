using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orbweaver
{
    // CB-327 items 4 and 5: an AskUserQuestion form read with the transcript's
    // help. The screen alone cannot carry a form on a narrow pane — a wrapped tab
    // bar reads as garbled tabs, a long question comes back as its last line, and
    // a label whose last line happens to fill the row swallows the first line of
    // its description — and on a pane shorter than the form it cannot carry one at
    // all, because the tab bar, the question and option 1 were never drawn. The
    // transcript holds the whole form: Claude Code writes the AskUserQuestion
    // tool_use row, every question, header, label and multiSelect,
    // before the form appears, and answers it with a tool_result naming the same
    // tool_use id only once it is submitted (measured on 2.1.325 in a scratch
    // session: the tool_use was the transcript's last row while the form was up).
    //
    // So the screen says THAT a form is waiting and which number keys it has, and
    // the transcript says what the form says. Neither is trusted alone: a form is
    // only rewritten from the transcript when the latest unanswered call has
    // exactly one question whose option count is the screen's, and whose labels
    // agree with whatever the screen did draw of them. Anything short of that
    // leaves the screen's own reading exactly as it was.
    //
    // Pure: a screen and a parsed call in, a form out. Reading the file is the
    // one impure step, and it is a thin wrapper (CallFor) over a parser of lines.
    internal static class AskFormTranscript
    {
        // Descriptions are not taken: nothing shows them (CB-327 leaves them out), and
        // a label is all the matching needs.
        internal sealed record CallQuestion(string Question, string Header, bool MultiSelect, IReadOnlyList<string> Options);

        // The latest AskUserQuestion the transcript has asked and not had answered.
        internal sealed record Call(string ToolUseId, IReadOnlyList<CallQuestion> Questions);

        internal const string ToolName = "AskUserQuestion";

        // The footer every AskUserQuestion form draws ("Enter to select · Tab/Arrow
        // keys to navigate · Esc to cancel"), wrapped or not. A screen without it
        // is not a form, so the transcript is not read at all — the scan runs this
        // for every waiting session, and a permission dialog never needs it.
        internal const string FormFooter = "Enter to select";

        // The pending call for a screen, or null without reading anything when
        // the screen has no form footer on it.
        internal static Call? CallFor(string? screen, string? transcriptPath)
        {
            if (string.IsNullOrEmpty(screen) || string.IsNullOrEmpty(transcriptPath)) return null;
            if (!HasFormFooter(ChatTranscript.ScreenLines(screen).Select(Clean).ToArray())) return null;
            if (!File.Exists(transcriptPath)) return null;
            return PendingCall(TranscriptReader.TailLines(transcriptPath));
        }

        // Walks the rows in order, remembering the last AskUserQuestion tool_use
        // and forgetting it when a tool_result names it. A torn or foreign row is
        // skipped, as everywhere else the transcript is read.
        internal static Call? PendingCall(IEnumerable<string> lines)
        {
            Call? pending = null;
            foreach (var line in lines)
            {
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("message", out var message)
                        || message.ValueKind != JsonValueKind.Object
                        || !message.TryGetProperty("content", out var content)
                        || content.ValueKind != JsonValueKind.Array) continue;

                    foreach (var block in content.EnumerateArray())
                    {
                        if (block.ValueKind != JsonValueKind.Object) continue;
                        var type = Str(block, "type");
                        if (type == "tool_use" && Str(block, "name") == ToolName && Str(block, "id") is { } id
                            && block.TryGetProperty("input", out var input) && QuestionsOf(input) is { Count: > 0 } questions)
                            pending = new Call(id, questions);
                        else if (type == "tool_result" && pending is not null && Str(block, "tool_use_id") == pending.ToolUseId)
                            pending = null;
                    }
                }
            }
            return pending;
        }

        private static List<CallQuestion>? QuestionsOf(JsonElement input)
        {
            if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("questions", out var qs)
                || qs.ValueKind != JsonValueKind.Array) return null;

            var questions = new List<CallQuestion>();
            foreach (var q in qs.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.Object || Str(q, "question") is not { } text
                    || !q.TryGetProperty("options", out var os) || os.ValueKind != JsonValueKind.Array) return null;

                var options = new List<string>();
                foreach (var o in os.EnumerateArray())
                {
                    if (o.ValueKind != JsonValueKind.Object || Str(o, "label") is not { } label) return null;
                    options.Add(label);
                }

                var multi = q.TryGetProperty("multiSelect", out var m) && m.ValueKind == JsonValueKind.True;
                questions.Add(new CallQuestion(text, Str(q, "header") ?? "", multi, options));
            }
            return questions;
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // ---- the form ----------------------------------------------------------

        // What every caller that used to call ChatTranscript.ParseAskForm calls
        // now: the screen's own form, rewritten from the call when they agree; or,
        // when the screen has no readable form because the pane is shorter than
        // it, the form minted from the call (item 5). Null as before otherwise.
        internal static AskForm? ReadForm(string screen, Call? call)
        {
            var form = ChatTranscript.ParseAskForm(screen);
            if (call is null) return form;
            return form is not null ? Merge(form, call) : Tall(screen, call);
        }

        // The screen's form with the call's words. The review tab has no question
        // of its own to match, and is left alone.
        internal static AskForm Merge(AskForm form, Call call)
        {
            if (form.IsReview) return form;

            var choices = form.Prompt.Options.Where(o => o.Key != form.FreeTextKey && o.Key != form.ChatAboutKey).ToList();
            var shown = choices.Select(o => (o.Key, o.Label)).ToList();
            if (MatchQuestion(call, shown, choices.Count) is not { } index) return form;
            var question = call.Questions[index];

            // Choices are numbered from 1 in the call's order; anything else is a
            // screen this does not understand, and it is left as it was read.
            for (var n = 0; n < choices.Count; n++)
                if (choices[n].Key != (n + 1).ToString()) return form;

            var options = form.Prompt.Options.Select(o =>
                int.TryParse(o.Key, out var k) && k >= 1 && k <= question.Options.Count
                    ? o with { Label = question.Options[k - 1] }
                    : o).ToList();

            return form with
            {
                Prompt = form.Prompt with { Title = question.Question, Options = options },
                Tabs = TabsFor(call, form.Tabs),
                CurrentTab = index,
                MultiSelect = question.MultiSelect,
            };
        }

        // Item 5: a form taller than the pane. Claude Code keeps its footer and the
        // bottom of the option list on screen and lets the top scroll away, so what
        // is left is a numbered run that does not start at 1 — "2." or "3." up to
        // "Chat about this" — above the footer, with nothing at the margin above it.
        // Measured in a 23x30 pane on 2.1.325: a digit selects its option whether
        // that option is on screen or not (2 and 1 pressed with both scrolled away
        // landed on options 2 and 1, by the review screen and the tool_result), so
        // the keys are as good as on a full screen and the call supplies the rest.
        //
        // A multi-select question is not minted: its ticks are drawn in the rows
        // that scrolled away, so a box ticked there would read as clear and a
        // press meant to tick it would clear it. Null leaves the session waiting
        // with nothing to answer, which sends the person to the terminal.
        internal static AskForm? Tall(string screen, Call call)
        {
            var raw = ChatTranscript.ScreenLines(screen);
            var cleaned = raw.Select(Clean).ToArray();

            var footer = Array.FindLastIndex(cleaned, l => l.StartsWith(FormFooter, StringComparison.Ordinal));
            if (footer < 0) return null;

            var run = new List<(int Line, int Key, string Label)>();
            for (var i = 0; i < footer; i++)
            {
                var m = ChatTranscript.OptionLine.Match(cleaned[i]);
                if (m.Success) run.Add((i, int.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim()));
            }

            if (run.Count < 2 || run[0].Key <= 1) return null;
            for (var n = 1; n < run.Count; n++)
                if (run[n].Key != run[n - 1].Key + 1) return null;

            // The top of the screen is the middle of the form: every line above the
            // first visible option is an option's indented continuation.
            for (var i = 0; i < run[0].Line; i++)
                if (raw[i].Length > 0 && !char.IsWhiteSpace(raw[i][0])) return null;

            var last = run[^1];
            if (!last.Label.StartsWith(ChatTranscript.ChatAboutLabel, StringComparison.Ordinal)) return null;
            // The row above "Chat about this" is "Type something." — or, once the
            // cursor is on it and text has been typed, that text (ask-freetext-
            // typed): FormAnswer reads the row back before pressing Enter, so it
            // must still read as the form's free-text row.
            var typeRow = run[^2];
            if (!StripBox(typeRow.Label).StartsWith(ChatTranscript.FreeTextLabel, StringComparison.Ordinal)
                && !cleaned[typeRow.Line].StartsWith('❯')) return null;

            // The option count is anchored on "Type something", not on the last key:
            // every real tall capture (single-select ask-venue/ask-catering/
            // ask-freetext, multi-select ask-mixed-suites) draws both it and "Chat
            // about this", but the row it sits on is what says where the choices end.
            var count = typeRow.Key - 1;

            // Each visible choice's label as drawn: its option line and the indented
            // rows under it up to the next option, joined. A narrow pane wraps the
            // label, so its first row alone ("Lakeside cabin") would agree with any
            // label that starts that way; the joined text runs on into the
            // description, which Agrees allows, and must hold the whole label.
            var shown = new List<(string, string)>();
            for (var n = 0; n < run.Count && run[n].Key <= count; n++)
            {
                var end = n + 1 < run.Count ? run[n + 1].Line : footer;
                var text = string.Join(" ", new[] { StripBox(run[n].Label) }
                    .Concat(cleaned[(run[n].Line + 1)..end].TakeWhile(l => l.Length > 0 && !ChatTranscript.IsHorizontalRule(l))));
                shown.Add((run[n].Key.ToString(), text));
            }
            if (MatchQuestion(call, shown, count) is not { } index) return null;
            var question = call.Questions[index];
            if (question.MultiSelect) return null;

            var options = question.Options
                .Select((o, i) => new ChatPromptOption((i + 1).ToString(), o))
                .Append(new ChatPromptOption(typeRow.Key.ToString(), StripBox(typeRow.Label)))
                .Append(new ChatPromptOption(last.Key.ToString(), last.Label))
                .ToList();

            string? cursor = null;
            foreach (var r in run)
                if (cleaned[r.Line].StartsWith('❯')) cursor = r.Key.ToString();

            return new AskForm(new ChatPrompt(question.Question, options), TabsFor(call, Array.Empty<AskTab>()), index,
                typeRow.Key.ToString(), last.Key.ToString(), CursorKey: cursor);
        }

        // The one question in the call the screen can be showing: as many choices
        // as the screen has, and every label the screen drew a beginning of. Two
        // candidates is no answer — a form whose questions cannot be told apart
        // on screen is left to the screen's own reading.
        internal static int? MatchQuestion(Call call, IReadOnlyList<(string Key, string Label)> shown, int count)
        {
            int? found = null;
            for (var q = 0; q < call.Questions.Count; q++)
            {
                var options = call.Questions[q].Options;
                if (options.Count != count) continue;
                if (!shown.All(s => int.TryParse(s.Key, out var k) && k >= 1 && k <= count && Agrees(s.Label, options[k - 1]))) continue;
                if (found is not null) return null;
                found = q;
            }
            return found;
        }

        // Whether what the screen drew of a label is that label: one runs into the
        // other once spacing is evened out — the screen's is shorter when the label
        // wrapped or was cut with an ellipsis, longer when it swallowed the first
        // line of its description. A screen that drew nothing usable agrees with
        // nothing.
        internal static bool Agrees(string screen, string label)
        {
            var a = Squash(screen.TrimEnd('…', ' '));
            var b = Squash(label);
            if (a.Length == 0 || b.Length == 0) return false;
            return a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal);
        }

        // The call's headers as tabs. A multi-question form ends with its Submit
        // tab, which the call does not list. A state the screen read off its bar is
        // kept when the bar had a tab for every header; otherwise every question
        // is Open, which only ever withholds a claim.
        internal static IReadOnlyList<AskTab> TabsFor(Call call, IReadOnlyList<AskTab> screen)
        {
            var questions = screen.Where(t => t.State != AskTabState.Submit).ToList();
            var tabs = call.Questions.Select((q, i) =>
                new AskTab(q.Header, questions.Count == call.Questions.Count ? questions[i].State : AskTabState.Open)).ToList();
            if (call.Questions.Count > 1) tabs.Add(new AskTab("Submit", AskTabState.Submit));
            return tabs;
        }

        internal static bool HasFormFooter(IEnumerable<string> cleaned) =>
            cleaned.Any(l => l.StartsWith(FormFooter, StringComparison.Ordinal));

        private static string Clean(string line) => line.Trim().Trim('│', '┃', '|', '║').Trim();

        private static string StripBox(string label) =>
            label.StartsWith("[ ] ", StringComparison.Ordinal) || label.StartsWith("[✔] ", StringComparison.Ordinal) ? label[4..] : label;

        // Spacing evened out, and a terminal's break after a hyphen ("team-" /
        // "building") closed up, on both sides alike.
        private static string Squash(string text) => Regex.Replace(Regex.Replace(text, @"\s+", " "), @"(?<=\S-) ", "").Trim();
    }
}
