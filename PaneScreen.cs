using System;
using System.Linq;

namespace Orbweaver
{
    // What a Claude Code pane is showing, read off its text: its input prompt,
    // a dialog, or something this cannot vouch for (CB-307's addendum).
    //
    // It exists because remote typing landed in a dialog. A line typed over
    // the link into a session sitting on `/permissions` became keystrokes on a
    // list of rules, not a message, and nothing anywhere said so. The question
    // the typing gate needs answered is narrow — "is this pane at its prompt,
    // where text becomes a message?" — and this answers only that.
    //
    // **It answers yes on positive evidence only**, and every part of that
    // evidence was needed against a real screen. The prompt is the *last*
    // `❯` line in the window that is not a numbered option, and it counts
    // only when it sits in the input box:
    //
    //  - the nearest non-blank line above it, within two lines, is the box's
    //    top edge — a line that starts with a run of rule characters. Not a
    //    pure rule: the mini's carries the session name (`──── openclaw ─`);
    //  - the box's bottom edge, a pure rule, is within three lines below it,
    //    leaving room for a draft that wrapped;
    //  - and nothing after that edge is a numbered option or a dialog hint
    //    (`Enter to …`, `Esc to …`) — under the box there is only the footer.
    //
    // QA found why the first two matter. A real "Model switch" dialog has the
    // user's own echoed message (`❯ hi`) in scrollback a few lines above the
    // dialog's top border, and "a ❯ line with a rule shortly below" read that
    // as a prompt — so the gate would have typed into the dialog. The echo has
    // no box top above it, and the dialog's options follow its rule.
    //
    // CB-403 added two things an agent team's footer needs: a `❯` on one of
    // its rows is a selection, not the prompt, and is skipped; and a box that
    // belongs to a teammate (Enter on its row) is refused, since text typed
    // there never reaches the lead. Captures/cb403 has every screen.
    //
    // Anything not recognised as a prompt or a dialog is Unknown, and the
    // gate refuses Unknown: a screen this has never seen is exactly where
    // typing is least safe.
    //
    // Reuses ChatTranscript's own OptionLine, IsHorizontalRule and
    // DialogWindow rather than restating them, so the two readings of one
    // screen cannot drift apart.
    internal enum PaneReading { Prompt, Dialog, Unknown }

    internal static class PaneScreen
    {
        // How far below the `❯` line the box's bottom rule may be. One line in
        // every capture; three leaves room for a draft that wrapped.
        private const int RuleWithin = 3;

        // How far above it the box's top edge may be, skipping blanks.
        private const int TopWithin = 2;

        internal static PaneReading Classify(string? screen) => Read(screen).Reading;

        // The reading and which check decided it, as a short fixed word — never
        // any of the screen's own text. The word is for a log line: a refusal
        // reported from a machine nobody here can look at has to say which
        // check fired, or the next report is another guess (CB-307 addendum:
        // the first such report could not be reproduced from the captures).
        internal static (PaneReading Reading, string Check) Read(string? screen)
        {
            if (string.IsNullOrWhiteSpace(screen)) return (PaneReading.Unknown, "empty");

            if (ChatTranscript.ParseDialog(screen) is not null) return (PaneReading.Dialog, "dialog");

            var lines = ChatTranscript.ScreenLines(screen).Select(l => l.Trim()).ToArray();
            var from = ChatTranscript.WindowStart(lines);

            // CB-316: Grok's and Codex's prompts are not Claude's ruled box, so a
            // phone's input to either was always refused. Their own shapes, read
            // off real captures, are tried first; each answers only when its own
            // prompt is on screen (text above it can look like Claude's mark).
            if (GrokPrompt(lines, from) is { } grok) return grok;
            if (CodexPrompt(lines, from) is { } codex) return codex;

            return ClaudePrompt(lines, from);
        }

        // Claude's ruled input box. Its own method, as GrokPrompt and CodexPrompt are,
        // so a test can put a screen straight to this rule without ParseDialog
        // answering first (CB-327).
        internal static (PaneReading Reading, string Check) ClaudePrompt(string[] lines, int from)
        {
            // CB-403: a selected row of the agent-team footer is not the prompt.
            // One Down arrow at an idle prompt puts a `❯` on the footer under the
            // box (`❯ ⏺ main`, `❯ ◯ <teammate>`), and as the last `❯` on screen it
            // was taken for the mark, found no box top above it, and an idle
            // session refused a phone's message. The box above is still there, so
            // such a row is skipped and the box is judged as it always was.
            var mark = -1;
            for (var i = lines.Length - 1; i >= from && mark < 0; i--)
                if (IsPromptMark(lines[i]) && !ChatTranscript.OptionLine.IsMatch(lines[i]) && !IsSelectedTeamRow(lines[i])) mark = i;

            if (mark < 0) return (PaneReading.Unknown, "no-prompt-mark");

            var above = mark - 1;
            while (above >= Math.Max(0, mark - TopWithin) && lines[above].Length == 0) above--;
            if (above < Math.Max(0, mark - TopWithin) || !IsBoxTop(lines[above]))
                return (PaneReading.Unknown, "no-box-top");

            var bottom = -1;
            for (var j = mark + 1; j <= mark + RuleWithin && j < lines.Length && bottom < 0; j++)
                if (ChatTranscript.IsHorizontalRule(lines[j])) bottom = j;

            if (bottom < 0) return (PaneReading.Unknown, "no-box-bottom");

            // CB-403, the other direction: Enter on a teammate's footer row turns
            // the pane over to that teammate. The box is drawn exactly as the
            // lead's is, so it read as the prompt, and a message typed there goes
            // to the teammate and not to the session the phone is talking to.
            // Two things on a real capture say so, and either is enough: the
            // box's title is `@<teammate>`, and the footer's filled mark has left
            // `main` (`◯ main` / `⏺ <teammate>`).
            if (TitleOf(lines[above]).StartsWith('@')) return (PaneReading.Unknown, "teammate-view");
            for (var k = bottom + 1; k < lines.Length; k++)
                if (IsUnviewedMain(lines[k])) return (PaneReading.Unknown, "teammate-view");

            for (var k = bottom + 1; k < lines.Length; k++)
                if (ChatTranscript.OptionLine.IsMatch(lines[k])
                    || lines[k].Contains("Enter to ", StringComparison.Ordinal)
                    || lines[k].Contains("Esc to ", StringComparison.Ordinal))
                    return (PaneReading.Unknown, "options-under-box");

            return (PaneReading.Prompt, "prompt");
        }

        // The idle input line's mark: ❯, or > where the console cannot draw it.
        // The character after the > is a NO-BREAK SPACE in the real capture, not
        // a space, so any whitespace counts.
        // Windows' classic console host (conhost) draws Claude Code's mark as a
        // plain > (CB-313), and that is the only difference between its idle
        // screen and every other terminal's. The box around it (a rule above
        // and one below, checked after this) is what keeps a > that is a quote
        // or a shell prompt from reading as the input box.
        // Grok Build: a rounded box, `╭─…╮` / `│ ❯ … │` / `╰─… Grok … ─╯`, with
        // `>` for `❯` in the Windows console (real captures, CB-313/CB-316). Its
        // home screen draws the same box under a menu (Resume session, New
        // worktree, …) where typing starts nothing a phone asked for, and while
        // it works there is a spinner line above the box — braille on the Mac
        // (`⠋ Responding… 2.0s`), ASCII `| / - \` in the Windows console
        // (`/ Thinking… 1.3s`), with a banner between it and the box there — and
        // on Windows the footer offers `Ctrl+c:cancel`: all read as not at the
        // prompt. Null when the box is not there at all.
        private static (PaneReading, string)? GrokPrompt(string[] lines, int from)
        {
            for (var i = lines.Length - 1; i >= from; i--)
            {
                if (!IsGrokMark(lines[i])) continue;
                var top = Previous(lines, i, from, TopWithin);
                if (top < 0 || !lines[top].StartsWith("╭─", StringComparison.Ordinal)) return null;
                var bottom = -1;
                for (var j = i + 1; j <= i + RuleWithin && j < lines.Length && bottom < 0; j++)
                    if (lines[j].StartsWith("╰─", StringComparison.Ordinal)) bottom = j;
                if (bottom < 0) return null;
                for (var k = from; k < lines.Length; k++)
                    if (GrokHome.Any(h => lines[k].Contains(h, StringComparison.Ordinal))) return (PaneReading.Unknown, "grok-home");
                for (var k = from; k < top; k++)
                    if (IsSpinner(lines[k])) return (PaneReading.Unknown, "working");
                for (var k = bottom + 1; k < lines.Length; k++)
                    if (lines[k].Contains("Ctrl+c:cancel", StringComparison.Ordinal)) return (PaneReading.Unknown, "working");
                return (PaneReading.Prompt, "grok-prompt");
            }
            return null;
        }

        // Not "Help improve Grok": that banner sits over ordinary chat screens
        // too (measured in the Windows console).
        private static readonly string[] GrokHome = ["Resume session", "New worktree"];

        private static readonly System.Text.RegularExpressions.Regex GrokMark =
            new(@"^│\s*[❯>]\s.*│$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static bool IsGrokMark(string line) => GrokMark.IsMatch(line);

        // A spinner glyph, a space, then an activity with an ellipsis: Grok's
        // "working" line (`⠋ Responding… 2.0s`, `/ Thinking… 1.3s`). The ellipsis
        // keeps an ordinary line that happens to start "- " from counting.
        private static readonly System.Text.RegularExpressions.Regex Spinner =
            new(@"^[\u2801-\u28FF|/\\-] .*\u2026", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static bool IsSpinner(string line) => Spinner.IsMatch(line);

        // Codex: `› <draft or placeholder>` over its footer (`? for shortcuts`),
        // no box. A numbered `› 1.` is an approval's option, not the prompt, and
        // `esc to interrupt` anywhere in view means a turn is running; the
        // session record's `generating` (RelayInput) covers the stretches where
        // Codex streams text with no such line on screen.
        private static (PaneReading, string)? CodexPrompt(string[] lines, int from)
        {
            for (var i = lines.Length - 1; i >= from; i--)
            {
                if (!lines[i].StartsWith("› ", StringComparison.Ordinal) && lines[i] != "›") continue;
                if (ChatTranscript.OptionLine.IsMatch(lines[i])) return null;
                var footer = false;
                for (var j = i + 1; j <= i + 4 && j < lines.Length; j++)
                    if (lines[j].Contains("for shortcuts", StringComparison.Ordinal)) footer = true;
                if (!footer) return null;
                for (var k = from; k < lines.Length; k++)
                    if (lines[k].Contains("esc to interrupt", StringComparison.Ordinal)) return (PaneReading.Unknown, "working");
                return (PaneReading.Prompt, "codex-prompt");
            }
            return null;
        }

        // The nearest non-blank line above `index`, at most `within` lines up, or -1.
        private static int Previous(string[] lines, int index, int from, int within)
        {
            for (var j = index - 1; j >= Math.Max(from, index - within); j--)
                if (lines[j].Length > 0) return j;
            return -1;
        }

        private static bool IsPromptMark(string line) =>
            line.StartsWith('❯') || line == ">" || (line.Length > 1 && line[0] == '>' && char.IsWhiteSpace(line[1]));

        // The agent-team footer under the box (CB-403), one row per member:
        // `⏺ main` / `◯ <teammate>  <task>  <elapsed>`. The filled mark is on
        // whoever the pane is showing, and after a Down arrow one row carries a
        // leading `❯`. Both marks are needed after the `❯`: a line the user
        // typed that starts some other way is still a prompt mark.
        private static bool IsTeamMark(char c) => c is '⏺' or '◯';

        internal static bool IsSelectedTeamRow(string line)
        {
            if (!line.StartsWith('❯')) return false;
            var rest = line[1..].TrimStart();
            return rest.Length > 1 && IsTeamMark(rest[0]) && char.IsWhiteSpace(rest[1]);
        }

        // `◯ main`, selected or not: the lead's own row without the filled mark.
        internal static bool IsUnviewedMain(string line)
        {
            // Trimmed here as well as by the caller: a reader that pads rows to
            // the pane's width must not turn this refusal off.
            var rest = line.Trim();
            if (rest.StartsWith('❯')) rest = rest[1..].TrimStart();
            return rest == "◯ main";
        }

        // A box top's title: what is left once the rule characters and spaces at
        // both ends are gone. Empty for a pure rule.
        internal static string TitleOf(string edge) => edge.Trim('─', '━', '═', ' ');

        private static bool IsRuleChar(char c) => c is '─' or '━' or '═';

        // A box's top edge, in either of the two shapes Claude Code draws (CB-327):
        //
        //  (a) a leading run of 10 or more rule characters, anything after it
        //      (a session name) allowed — the shape this always accepted;
        //  (b) a titled edge, for a pane too narrow to keep ten rule characters
        //      beside the name. Measured at 23 columns it is `─── name ─`, at 20
        //      `name ─` with no leading rule at all, and from 18 down the name is
        //      cut with an ellipsis (`name… ─`). What every one of them keeps is
        //      its ending: a space and then rule characters. So (b) is: the line
        //      ends with a space and one or more rule characters, whatever is
        //      before that is a run of rule characters (possibly none) and then
        //      the title, and the title holds a letter or digit, is not a prompt
        //      mark and holds no rule character of its own. A line with text
        //      *before* the leading run (`foo ─── x ─`) is refused: the run must
        //      start the line.
        //
        // The edge is deliberately not required to be as long as the bottom rule:
        // a name with wide characters would draw a shorter line and re-create
        // this bug. What stops a stray line from passing is everything else the
        // caller checks (a pure rule below, no options or hint after it).
        internal static bool IsBoxTop(string line)
        {
            if (line.Length >= 10 && line.Take(10).All(IsRuleChar)) return true;

            var end = line.Length;
            while (end > 0 && IsRuleChar(line[end - 1])) end--;
            if (end == line.Length || end == 0 || line[end - 1] != ' ') return false;

            var title = line[..(end - 1)];
            var lead = 0;
            while (lead < title.Length && IsRuleChar(title[lead])) lead++;
            if (lead == title.Length) return false;
            if (lead > 0)
            {
                if (title[lead] != ' ') return false;
                title = title[lead..].TrimStart();
            }

            return title.Length > 0 && !IsPromptMark(title) && !title.Any(IsRuleChar) && title.Any(char.IsLetterOrDigit);
        }
    }
}
