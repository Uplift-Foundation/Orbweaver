namespace Orbweaver
{
    // The decisions behind the settings window's filter box, pulled out of
    // SettingsWindow.cs so they can be reached with a plain [Fact] — no
    // AvaloniaFact, no app lifetime, no [Collection]. Nothing in here knows
    // about a Grid, a StackPanel or a Border; SettingsWindow's SettingsRow and
    // SettingsCard call these functions and apply the answers to real
    // controls, but the answers themselves don't need one to exist.
    //
    // A query is "active" once it holds anything but whitespace. An inactive
    // query means "show everything" throughout this file, which is what keeps
    // a freshly opened window and a cleared search box behaving identically
    // without either of them being a special case in the caller.
    internal static class SettingsFilter
    {
        // CB-281's "Auto-group orbs by" row: its choices in display order, and
        // its help. The help doubles as the row's search text (Row builds
        // SearchText from label + help), so the words a person would type to
        // find it -- group, directory, repo, CLI, profile, account -- live here.
        internal static readonly (string Label, AutoGroupBy Value)[] AutoGroupChoices =
        {
            ("Off", AutoGroupBy.Off),
            ("Directory", AutoGroupBy.Directory),
            ("CLI", AutoGroupBy.Cli),
            ("Claude profile", AutoGroupBy.Profile),
            ("Account", AutoGroupBy.Account)
        };

        internal const string AutoGroupHelp =
            "Gather orbs into one group per directory (the git repo they are in), CLI, "
            + "Claude profile or account, live as sessions come and go. A group you make by "
            + "hand is never changed, and orbs with no value for the choice stay ungrouped. "
            + "Turn on Outline groups and Show group names below to see the groups.";

        internal static bool IsActive(string? query) => !string.IsNullOrWhiteSpace(query);

        // Whitespace-split, empty entries dropped. Order isn't meaningful —
        // Matches requires all of them, in any order — so this doesn't need to
        // preserve anything beyond "which words were typed".
        internal static string[] Terms(string? query) =>
            IsActive(query)
                ? query!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();

        // An inactive query matches everything, including a row with no text
        // at all — that's what lets a cleared filter show rows this file never
        // learned a SearchText for. An active query against null text is the
        // one case that can never match: there's nothing there to find a word
        // in.
        internal static bool Matches(string? query, string? text)
        {
            if (!IsActive(query)) return true;
            if (text is null) return false;

            return Terms(query).All(term =>
                text.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        // What a row searches against. Nulls are dropped rather than turned
        // into an empty word, so a row with a label but no help text searches
        // as just the label instead of "label " with a trailing space nobody
        // typed.
        internal static string TextOf(params string?[] parts) =>
            string.Join(" ", parts.Where(p => p is not null));

        // One card's worth of rows. texts/chrome are parallel to each other and
        // to the returned array — index i in every one of them is the same
        // row. A chrome row (a column heading, a status line, the profiles
        // card's small print) carries no text of its own and instead follows
        // whether any of its non-chrome siblings matched: hiding a profile row
        // without its column headings, or a status line without the switch it
        // explains, would orphan one half of a pair that only makes sense
        // together.
        //
        // A title match means the user asked for the section by name rather
        // than for a row inside it, so every row shows regardless of its own
        // text — chrome included, since there's no longer a "which sibling
        // matched" question to answer.
        internal static bool[] VisibleRows(
            string? query, IReadOnlyList<string?> texts, IReadOnlyList<bool> chrome, bool titleMatches)
        {
            var n = texts.Count;
            var direct = new bool[n];
            for (var i = 0; i < n; i++)
            {
                direct[i] = !chrome[i] && Matches(query, texts[i]);
            }

            var anyDirect = direct.Any(d => d);
            var visible = new bool[n];
            for (var i = 0; i < n; i++)
            {
                visible[i] = titleMatches || (chrome[i] ? anyDirect : direct[i]);
            }

            return visible;
        }

        // Which of a card's hairlines — one before every row after the first —
        // should stay up once some rows are hidden. Named apart from VisibleRows
        // so a stacked-hairline bug (two lines between two rows that used to
        // have one each between them) or a leading rule (a line above the first
        // row still showing) is a unit-test failure instead of something only
        // visible on screen.
        //
        // Indices are into the same row order VisibleRows was given — "the
        // first visible row never gets a line above it, every visible row
        // after that does".
        internal static int[] SeparatorsBefore(IReadOnlyList<bool> visible)
        {
            var visibleIndexes = new List<int>();
            for (var i = 0; i < visible.Count; i++)
            {
                if (visible[i]) visibleIndexes.Add(i);
            }

            return visibleIndexes.Skip(1).ToArray();
        }

        // Whether a section should show at all: it asked for by name, or it
        // holds a card that matched. A card whose rows can't be searched at
        // all (a free-form control this file never learned a SearchText for)
        // contributes neither true nor false here — see SettingsCard.ApplyFilter
        // for why an unsearchable card is left out of this decision rather than
        // treated as an automatic match.
        internal static bool SectionVisible(bool anyRowVisible, bool titleMatches) =>
            anyRowVisible || titleMatches;

        internal static string EmptyStateText(string? query) =>
            $"No settings match \"{query?.Trim()}\".";
    }

    // What a key press means for the settings window, independent of the
    // Window that would otherwise be needed to ask. ShouldCloseOnKeyDown is a
    // projection of this with the query fixed at null — see its own comment.
    internal enum SettingsKeyVerdict
    {
        Ignore,
        FocusFilter,
        ClearFilter,
        Close
    }
}
