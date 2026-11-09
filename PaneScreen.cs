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

            var mark = -1;
            for (var i = lines.Length - 1; i >= from && mark < 0; i--)
                if (IsPromptMark(lines[i]) && !ChatTranscript.OptionLine.IsMatch(lines[i])) mark = i;

            if (mark < 0) return (PaneReading.Unknown, "no-prompt-mark");

            var above = mark - 1;
            while (above >= Math.Max(0, mark - TopWithin) && lines[above].Length == 0) above--;
            if (above < Math.Max(0, mark - TopWithin) || !IsBoxTop(lines[above]))
                return (PaneReading.Unknown, "no-box-top");

            var bottom = -1;
            for (var j = mark + 1; j <= mark + RuleWithin && j < lines.Length && bottom < 0; j++)
                if (ChatTranscript.IsHorizontalRule(lines[j])) bottom = j;

            if (bottom < 0) return (PaneReading.Unknown, "no-box-bottom");

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

        private static bool IsGrokMark(string line) =>
            line.Length >= 3 && line[0] == '│' && line.TrimEnd().EndsWith('│')
            && line[1..].TrimStart() is { Length: > 0 } inner && (inner[0] == '❯' || inner[0] == '>')
            && (inner.Length == 1 || char.IsWhiteSpace(inner[1]));

        // A spinner glyph, a space, then an activity with an ellipsis: Grok's
        // "working" line (`⠋ Responding… 2.0s`, `/ Thinking… 1.3s`). The ellipsis
        // keeps an ordinary line that happens to start "- " from counting.
        private static bool IsSpinner(string line) =>
            line.Length > 2
            && (line[0] is >= '\u2801' and <= '\u28FF' or '|' or '/' or '-' or '\\')
            && line[1] == ' '
            && line.Contains('\u2026');

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

        // A box's top edge: a long run of rule characters at its start, with
        // anything (a session name) allowed after it.
        private static bool IsBoxTop(string line) =>
            line.Length >= 10 && line.Take(10).All(c => c is '─' or '━' or '═');
    }
}
