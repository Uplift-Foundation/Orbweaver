using System.Text.RegularExpressions;

namespace Orbweaver
{
    // Grok Build's permission dialog, read off the screen (CB-313 captured it on
    // every terminal; CB-315 teaches the parser).
    //
    // It is not a numbered list in Claude Code's sense. Each option is
    //
    //     1 (○) Yes, and don't ask again for anything (always-approve mode)
    //     2 (●) Yes, proceed
    //     3 (○) No, reject (type to add feedback)
    //
    // — the digit, a space, a radio glyph in parentheses (● or • for the one
    // selected, ○ for the rest) and the label, inside a vertical bar the
    // terminal draws as ┃ (Windows Terminal, the Mac emulators) or │ (conhost).
    // Pressing the digit chooses that option at once, which is why the parser
    // keys the options by their digit like every other CLI's.
    //
    // Two variants share the shape and differ in what is offered:
    //
    //   Mac, 3 options:    always-approve mode / Yes, proceed / No, reject
    //   Windows, 5 options, headed "Auto-review blocked this action.":
    //                      always-approve mode / Always allow: <pattern> /
    //                      Yes, proceed / No, reject / Never allow: <pattern>
    //
    // Above the options is the session's own description of the call on a line
    // beginning with a › (a > under conhost), and under that the command itself
    // on a line beginning with │. The title is the command, as it is for Codex,
    // because that is what a person is being asked about.
    //
    // **The first option is not an answer.** "Yes, and don't ask again for
    // anything (always-approve mode)" switches the whole session to approving
    // everything. It is parsed, and carried for completeness, but flagged
    // Mode so that nothing offers it as a button and nothing presses it: see
    // OptionModes.
    internal static class GrokDialog
    {
        // The digit, then a radio glyph in parentheses. The glyph is one of
        // the three seen (● ○ and conhost's •), or blank, since a terminal that
        // cannot draw them shows a space.
        private static readonly Regex RadioOption =
            new(@"^(\d{1,2})\s+\(\s*[○●•◦◯◉]?\s*\)\s+(\S.*)$", RegexOptions.Compiled);

        // "1/3:select  │  Tab:next option …": the footer every Grok dialog draws
        // under its options and the input box never has.
        private static readonly Regex Footer = new(@"^\d+/\d+:select\b", RegexOptions.Compiled);

        // How far above the first option to look for the description and the
        // command: the Windows variant puts a hint line and the heading between
        // them and the options.
        private const int LookUp = 10;

        internal static bool LooksLike(IReadOnlyList<string> cleaned, int from)
        {
            for (var i = cleaned.Count - 1; i >= from; i--)
                if (RadioOption.IsMatch(Strip(cleaned[i]))) return true;
            return false;
        }

        // The dialog, or null. `cleaned` is ChatTranscript's cleaned lines, so
        // the same window and the same notion of a blank line apply.
        internal static ChatPrompt? Parse(string[] cleaned, int from, out int firstOption)
        {
            firstOption = -1;

            var runEnd = -1;
            for (var i = cleaned.Length - 1; i >= from && runEnd < 0; i--)
                if (RadioOption.IsMatch(Strip(cleaned[i]))) runEnd = i;

            if (runEnd < 0) return null;

            // Walk up while the digits count down by one with nothing between:
            // unlike Claude Code's, Grok's options are drawn on consecutive lines.
            var picked = new List<int> { runEnd };
            while (true)
            {
                var at = picked[^1];
                var want = int.Parse(RadioOption.Match(Strip(cleaned[at])).Groups[1].Value) - 1;
                if (want < 1 || at - 1 < from) break;

                var m = RadioOption.Match(Strip(cleaned[at - 1]));
                if (!m.Success || m.Groups[1].Value != want.ToString()) break;
                picked.Add(at - 1);
            }

            picked.Reverse();
            if (picked.Count < 2) return null;

            var options = new List<ChatPromptOption>();
            foreach (var i in picked)
            {
                var m = RadioOption.Match(Strip(cleaned[i]));
                var label = m.Groups[2].Value.Trim();
                options.Add(new ChatPromptOption(m.Groups[1].Value, label, Mode: OptionModes.IsMode(label)));
            }

            for (var n = 0; n < options.Count; n++)
                if (options[n].Key != (n + 1).ToString()) return null;

            // The input box drawn below means these digits are prose in a
            // transcript, not a dialog the session is blocked on (as for the
            // numbered form).
            for (var i = runEnd + 1; i < cleaned.Length; i++)
                if (ChatTranscript.IsHorizontalRule(Strip(cleaned[i]))) return null;

            firstOption = picked[0];

            // The description (› …) and under it the command (│ …).
            string? description = null, command = null;
            for (var k = firstOption - 1; k >= Math.Max(from, firstOption - LookUp); k--)
            {
                var line = cleaned[k].Trim();
                if (line.StartsWith('›') || line.StartsWith('>'))
                {
                    description = Strip(line.TrimStart('›', '>')).Trim();
                    for (var c = k + 1; c < firstOption; c++)
                    {
                        var below = cleaned[c].Trim();
                        if (below.StartsWith('│')) { command = Strip(below).Trim(); break; }
                    }
                    break;
                }
            }

            var title = !string.IsNullOrWhiteSpace(command) ? command
                : !string.IsNullOrWhiteSpace(description) ? description
                : "Waiting for input";

            for (var n = 0; n < options.Count; n++)
                if (OptionModes.IsTruncatedScope(options[n].Label, command))
                    options[n] = options[n] with { ScopeTruncated = true };

            return new ChatPrompt(title!, options);
        }

        // Cleaned lines still carry an inner │ (the command's own bar), and the
        // option lines may begin with one under conhost.
        private static string Strip(string line) => line.Trim().Trim('│', '┃', '|', '║').Trim();

        // The footer is the strongest single sign this is a Grok dialog and
        // not a numbered list in a transcript that happens to look like one;
        // exposed for the tests.
        internal static bool IsFooter(string line) => Footer.IsMatch(Strip(line));
    }

    // Which options change the session's permission mode rather than answer
    // the question in front of it. They are parsed, because the dialog draws
    // them, and then never offered or pressed: a tap that means "approve
    // everything from now on" does not belong on a lock screen or a button
    // row, however it is labelled. One place decides, so the panel, the
    // phone's prompt (kind "mode") and the answer gate cannot disagree.
    //
    // Evidenced, not guessed: Grok's always-approve option and Claude Code's
    // "switch to auto mode" are both in the CB-313 captures. Add a label here
    // when a capture shows another.
    internal static class OptionModes
    {
        internal static bool IsMode(string label) =>
            label.Contains("always-approve mode", StringComparison.OrdinalIgnoreCase)
            || label.Contains("switch to auto mode", StringComparison.OrdinalIgnoreCase);

        // An option that grants a standing permission with a scope: Claude's "Yes, and always
        // allow access to …", Codex's "Yes, and don't ask again for commands that start with …",
        // Grok's "Always allow: …". A mode switch is not one (it grants no scope to show).
        internal static bool IsAlwaysAllow(string label) =>
            !IsMode(label)
            && (label.Contains("always allow", StringComparison.OrdinalIgnoreCase)
                || label.Contains("don't ask again", StringComparison.OrdinalIgnoreCase));

        // Claude Code and Codex draw full-width rules, so the width is known; an always-allow label
        // that is not wrapped and whose line fills it was cut at the edge (the terminal wraps them
        // when it can, so a line that ends exactly at the edge with no continuation is suspect).
        internal static bool IsTruncatedAtEdge(string label, bool wrapped, int lineLength, int? ruleWidth) =>
            IsAlwaysAllow(label) && !wrapped && ruleWidth is int w && lineLength >= w;

        // Grok on Windows cuts the "Always allow: <command>" label at the end of its box without
        // wrapping (CB-313: `… -ErrorAction`, where the command above ends `-ErrorAction
        // SilentlyContinue`), and the screen is wider than the cut, so the edge test cannot see it.
        // The command is on screen in full, so a scope that is a strict prefix of it was cut. A
        // scope that ends in a wildcard is a pattern the person narrowed on purpose, not a cut.
        internal static bool IsTruncatedScope(string label, string? command)
        {
            const string Marker = "Always allow:";
            if (string.IsNullOrWhiteSpace(command) || !label.StartsWith(Marker, StringComparison.OrdinalIgnoreCase))
                return false;

            var scope = label[Marker.Length..].Trim();
            return scope.Length > 0
                && scope.Length < command.Length
                && !scope.EndsWith('*')
                && command.StartsWith(scope, StringComparison.Ordinal);
        }
    }
}
