using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Orbweaver
{
    // CB-353: cuts a docked side panel off a captured pane.
    //
    // A mod pane (the jira-board mod is the real case) is docked to the right of
    // the chat, so `tmux capture-pane` returns each row as chat text, a long
    // gap, a `│` divider and then the panel's own text. Every reader of a screen
    // then parsed the board as part of the dialog: a form came back with no
    // tabs, no Submit row and option labels 343 characters long with backlog
    // rows in them, and the prompt fingerprint changed whenever the board did.
    //
    // The rule is a divider, found by what it looks like rather than where it is:
    // the leftmost display-cell column of at least MinChatCells that holds a `│`
    // on a run of rows that starts at the top of the screen (row 0 or 1), is at
    // least MinRows long, covers at least 90% of the rows between its first and
    // last, and has text to its right on at least one row. Each row in the run
    // is cut at that bar and trimmed; rows outside the run are untouched, so with
    // the board open and no dialog the input box below stays full width.
    //
    // Columns are display cells, not string indexes: a row holding CJK or emoji
    // has its divider at string index 110 and cell 119 (measured). The width
    // table is deliberately small — a row it gets wrong is absorbed by the 90%
    // allowance and by cutting at the nearest bar within Near cells.
    //
    // Not handled, on purpose: a panel docked left or below. No capture of one.
    internal static class PaneColumns
    {
        internal const char Bar = '│';
        internal const int MinChatCells = 20;
        internal const int MinRows = 8;
        internal const int Near = 3;

        // Display cells for one code point: 0 for combining marks and format
        // characters, 2 for East Asian wide and emoji ranges, otherwise 1.
        internal static int Cells(Rune r)
        {
            var category = Rune.GetUnicodeCategory(r);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
            var v = r.Value;
            var wide = (v >= 0x1100 && v <= 0x115F) || (v >= 0x2E80 && v <= 0xA4CF) || (v >= 0xAC00 && v <= 0xD7A3)
                || (v >= 0xF900 && v <= 0xFAFF) || (v >= 0xFE30 && v <= 0xFE4F) || (v >= 0xFF00 && v <= 0xFF60)
                || (v >= 0xFFE0 && v <= 0xFFE6) || (v >= 0x1F300 && v <= 0x1F64F) || (v >= 0x1F680 && v <= 0x1F6FF)
                || (v >= 0x1F900 && v <= 0x1FAFF) || (v >= 0x20000 && v <= 0x3FFFD)
                || v is 0x2705 or 0x274C or 0x2728 or 0x231A or 0x231B or 0x23F0 or 0x23F3 or 0x2B50 or 0x2B55;
            return wide ? 2 : 1;
        }

        // (string index, display cell) of every bar on a line.
        internal static List<(int Index, int Cell)> Bars(string line)
        {
            var bars = new List<(int Index, int Cell)>();
            int cell = 0, index = 0;
            foreach (var rune in line.EnumerateRunes())
            {
                if (rune.Value == Bar) bars.Add((index, cell));
                cell += Cells(rune);
                index += rune.Utf16SequenceLength;
            }
            return bars;
        }

        // The lines with any docked side panel cut off, or the same array when
        // there is none. Never throws and never returns null.
        internal static string[] StripSidePanel(string[] lines)
        {
            var bars = lines.Select(Bars).ToArray();
            var candidates = bars.SelectMany(b => b.Select(x => x.Cell)).Where(c => c >= MinChatCells).Distinct().OrderBy(c => c);

            foreach (var column in candidates)
            {
                var rows = Enumerable.Range(0, lines.Length).Where(n => bars[n].Any(b => b.Cell == column)).ToList();
                int top = rows[0], bottom = rows[^1];

                if (top > 1 || rows.Count < MinRows || rows.Count * 10 < (bottom - top + 1) * 9) continue;

                // A box's right edge has nothing to its right; a panel does.
                if (!rows.Any(n => lines[n][(bars[n].First(b => b.Cell == column).Index + 1)..].Any(ch => !char.IsWhiteSpace(ch)))) continue;

                var cut = (string[])lines.Clone();
                for (var n = top; n <= bottom; n++)
                {
                    var near = bars[n].Where(b => Math.Abs(b.Cell - column) <= Near)
                        .OrderBy(b => Math.Abs(b.Cell - column)).Cast<(int Index, int Cell)?>().FirstOrDefault();
                    if (near is { } hit) cut[n] = lines[n][..hit.Index].TrimEnd();
                }
                return cut;
            }
            return lines;
        }
    }
}
