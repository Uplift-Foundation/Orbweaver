using Orbweaver;

namespace Orbweaver.Tests
{
    // The orb-initials cases, as a class rather than as a script.
    //
    // The cases themselves are not new and nothing here changes one. They moved
    // out of Program.cs for the same reason the arrangement sweep did: this
    // project is a plain console exe with no test SDK reference, so
    // tools/coverage.sh never saw it, and OrbGlyph reported 40% coverage from
    // incidental use elsewhere while actually being checked here in detail.
    // tests/UnitTests compiles this file in and runs the identical tables, so
    // the lines land in the denominator and `dotnet run --project
    // tests/GlyphTests` keeps working.
    //
    // Why this suite exists at all is worth repeating where the cases live: the
    // initials were wrong for a year without anyone noticing. Every kebab-case
    // directory drew two letters off the front of its first word, so
    // "claude-buddy" was "Cl" when it should have been "Cb". It was invisible
    // partly because it only looks wrong when the two halves start with
    // different letters, and partly because the only way to see the answer was
    // to look at the screen.
    //
    // Cases stay grouped by the reason each one is here rather than by input, so
    // a failure says which rule broke and not merely which string moved.
    internal static class GlyphSuite
    {
        internal readonly record struct GlyphCase(string Group, string Input, bool TwoLetter, string Want);

        internal readonly record struct InitialsCase(string Input, string Want);

        internal readonly record struct SpeakerCase(
            string Why, string? Identity, string? Title, string? Previous, string? Want);

        // CB-36: whether an unattributed assistant turn's chip may borrow the
        // panel's sole-speaker name. IsRoom is the only input — the caller
        // (ChatPanel.TurnView.SpeakerName) has already checked the role and
        // that the turn carries no Speaker of its own before asking this.
        internal readonly record struct FallbackCase(string Why, bool IsRoom, bool Want);

        internal static readonly GlyphCase[] Glyphs =
        {
            // Kebab and snake case — a session is named for its directory, and
            // this is what directories look like. The bug that prompted the
            // suite.
            new("word breaks",    "claude-buddy",       true,  "Cb"),
            new("word breaks",    "Claude-Buddy",       true,  "Cb"),
            new("word breaks",    "my_cool_project",    true,  "Mc"),
            new("word breaks",    "e-commerce",         true,  "Ec"),

            // All three dashes, spaced or not. Spaced already worked; unspaced
            // did not.
            new("dashes",         "Aurora — wtvamp",  true,  "Aw"),
            new("dashes",         "Aurora—wtvamp",    true,  "Aw"),
            new("dashes",         "Aurora–wtvamp",    true,  "Aw"),

            // A separator that leads or repeats contributes no word of its own.
            new("empty words",    "-leading",           true,  "Le"),
            new("empty words",    "a--b",               true,  "Ab"),
            new("empty words",    "  spaced  out  ",    true,  "So"),

            // Not every punctuation mark is a word break. '.' and '/' appear in
            // versions and paths, where what follows is not a word to take from.
            new("not separators", "v1.2.3",             true,  "V1"),
            new("not separators", "a/b",                true,  "A/"),

            // Two real words: one letter each, the initials a person would
            // write.
            new("two words",      "Menu UX",            true,  "Mu"),
            new("two words",      "Ada Lovelace",       true,  "Al"),

            // One word: two letters from itself, which is still right for
            // "Menu".
            new("one word",       "Menu",               true,  "Me"),
            new("one word",       "x",                  true,  "X"),

            // Leading punctuation is skipped, in both branches. A room orb is
            // named for its channel, so "#" leads far more often than not.
            new("punctuation",    "#kubernetes",        true,  "Ku"),
            new("punctuation",    "#arch",              true,  "Ar"),
            new("punctuation",    "#dev ops",           true,  "Do"),
            new("punctuation",    "!!!",                true,  "!!"),

            // Nothing to draw. The orb shows a dot rather than an empty circle.
            new("empty",          "",                   true,  "•"),
            new("empty",          "   ",                true,  "•"),

            // An emoji is a surrogate pair; slicing one in half draws a tofu
            // box.
            new("emoji",          "\U0001F680 rocket",  true,  "\U0001F680r"),
            new("emoji",          "\U0001F680",         false, "\U0001F680"),

            // With the setting off, one letter, and the same skipping does NOT
            // apply — this branch has always taken the very first character.
            // Asserted so the difference is a decision on the record rather than
            // a surprise later.
            new("single letter",  "claude-buddy",       false, "C"),
            new("single letter",  "Menu UX",            false, "M"),
            new("single letter",  "#arch",              false, "#"),
        };

        // The chat panel's header. Same word breaks, both letters capitalised,
        // and no bullet for the empty case — an empty circle is correct there.
        internal static readonly InitialsCase[] Initials =
        {
            new("claude-buddy",    "CB"),
            new("Ada Lovelace",    "AL"),
            new("Annabel Lee",     "AL"),
            new("Aurora",        "AU"),
            new("my_cool_project", "MC"),
            new("x",               "X"),
            new("",                ""),
            new("   ",             ""),
        };

        // Who a chip belongs to. Same suite because it is the same question one
        // step earlier — these letters are drawn from whatever this resolves to,
        // and both were wrong in the same way: read once, too early, from the
        // wrong place.
        internal static readonly SpeakerCase[] Speakers =
        {
            // The identity wins wherever there is one. A room's title is the
            // room.
            new("agent in a room",       "Aurora", "#openclaw-management", null, "Aurora"),
            new("agent, no title",       "Aurora", "",                     null, "Aurora"),

            // A terminal session has no identity; its title is genuinely its
            // agent.
            new("terminal session",      null,       "claude-buddy",         null, "claude-buddy"),
            new("terminal, blank id",    "",         "claude-buddy",         null, "claude-buddy"),
            new("whitespace identity",   "   ",      "claude-buddy",         null, "claude-buddy"),

            // The whole point: not knowing is never an answer. A gateway
            // reconnect empties the agent list, and a status update in that
            // window used to wipe the chips off a transcript that had been
            // showing them.
            new("identity list dropped", null,       "",                     "Aurora", "Aurora"),
            new("everything dropped",    null,       null,                   "Aurora", "Aurora"),
            new("blank over known",      "",         "   ",                  "Aurora", "Aurora"),

            // But a real change is a real change — a rebind to another session.
            new("rebound elsewhere",     "Alexis",   "#general",             "Aurora", "Alexis"),
            new("title changed",         null,       "codex-work",           "claude-buddy", "codex-work"),

            // Nothing known yet, and nothing known before. Bare bubbles are
            // correct here: inventing a name would be worse than showing none.
            new("nothing at all",        null,       null,                   null, null),
            new("all blank",             "",         "",                     "",   ""),
        };

        // Both outcomes CB-36's exclusion has to produce, named for what a
        // wrong answer in either direction would look like on screen. Two
        // rows rather than one because the whole ticket is that these used to
        // be conflated: a terminal session's fallback (true) and a room's
        // (false) look identical from inside SpeakerName until this is asked.
        internal static readonly FallbackCase[] Fallbacks =
        {
            // A one-to-one session — a terminal, or a single gateway agent —
            // has exactly one speaker, so an assistant turn with none of its
            // own genuinely is that one speaker.
            new("terminal or single agent", false, true),

            // A room's unattributed turn was built by Rebuild's own "left,
            // neutral, no name" branch specifically because several agents
            // talk in it and none could be named — falling back here would
            // answer with the room's own title, asserting the channel itself
            // spoke.
            new("room",                     true,  false),
        };

        // CB-287: OrbGlyph.Clip, the verbatim-override clipper. Rows are
        // (why, input, twoLetter, want).
        internal readonly record struct ClipCase(string Why, string? Input, bool TwoLetter, string Want);

        internal static readonly ClipCase[] Clips =
        {
            new("two letters kept",          "AB",           true,  "AB"),
            new("longer cut to two",         "ABC",          true,  "AB"),
            new("one-letter setting",        "ABC",          false, "A"),
            new("case preserved",            "aBc",          true,  "aB"),
            new("surrounding space trimmed", "  Xy  ",       true,  "Xy"),
            new("blank is empty",            "   ",          true,  ""),
            new("null is empty",             null,           true,  ""),
            new("family emoji is one",       "\U0001F468\u200D\U0001F469\u200D\U0001F467A", true, "\U0001F468\u200D\U0001F469\u200D\U0001F467A"),
            new("flag is one",               "\U0001F1EF\U0001F1F5X", true, "\U0001F1EF\U0001F1F5X"),
            new("combining accent attached", "e\u0301xy",    true,  "e\u0301x"),
            new("emoji one-letter",          "\U0001F600A",  false, "\U0001F600"),
        };

        internal static string? CheckClip(ClipCase c)
        {
            var got = OrbGlyph.Clip(c.Input, c.TwoLetter);
            return got == c.Want ? null : $"clip {c.Why}: Clip({Show(c.Input)}) = {Show(got)}, wanted {Show(c.Want)}";
        }

        internal static string Show(string? s) => s is null ? "null" : $"\"{s}\"";

        // Null is counted as its own case rather than added to the Initials
        // table, because that table is not nullable and making it so to hold one
        // row would weaken every other row's type.
        internal static int Total =>
            Glyphs.Length + Initials.Length + Speakers.Length + Fallbacks.Length + Clips.Length + 1;

        internal static string? CheckGlyph(GlyphCase c)
        {
            var got = OrbGlyph.For(c.Input, c.TwoLetter);
            return got == c.Want
                ? null
                : $"{c.Group}: For(\"{c.Input}\", twoLetter: "
                    + $"{c.TwoLetter.ToString().ToLowerInvariant()}) = \"{got}\", wanted \"{c.Want}\"";
        }

        internal static string? CheckInitials(InitialsCase c)
        {
            var got = OrbGlyph.Initials(c.Input);
            return got == c.Want
                ? null
                : $"header initials: Initials(\"{c.Input}\") = \"{got}\", wanted \"{c.Want}\"";
        }

        internal static string? CheckSpeaker(SpeakerCase c)
        {
            var got = ChatSpeaker.Resolve(c.Identity, c.Title, c.Previous);
            return got == c.Want
                ? null
                : $"speaker ({c.Why}): Resolve({Show(c.Identity)}, {Show(c.Title)}, "
                    + $"{Show(c.Previous)}) = {Show(got)}, wanted {Show(c.Want)}";
        }

        internal static string? CheckFallback(FallbackCase c)
        {
            var got = ChatSpeaker.CanFallBackToSoleSpeaker(c.IsRoom);
            return got == c.Want
                ? null
                : $"fallback ({c.Why}): CanFallBackToSoleSpeaker(isRoom: "
                    + $"{c.IsRoom.ToString().ToLowerInvariant()}) = "
                    + $"{got.ToString().ToLowerInvariant()}, wanted "
                    + $"{c.Want.ToString().ToLowerInvariant()}";
        }

        internal static string? CheckNullInitials()
            => OrbGlyph.Initials(null) is var got && got == ""
                ? null
                : $"header initials: Initials(null) = \"{got}\", wanted \"\"";

        internal static List<string> RunAll()
        {
            var failures = new List<string>();

            foreach (var c in Speakers)
                if (CheckSpeaker(c) is { } failure) failures.Add(failure);

            foreach (var c in Fallbacks)
                if (CheckFallback(c) is { } failure) failures.Add(failure);

            foreach (var c in Glyphs)
                if (CheckGlyph(c) is { } failure) failures.Add(failure);

            foreach (var c in Initials)
                if (CheckInitials(c) is { } failure) failures.Add(failure);

            foreach (var c in Clips)
                if (CheckClip(c) is { } failure) failures.Add(failure);

            if (CheckNullInitials() is { } nullFailure) failures.Add(nullFailure);

            return failures;
        }
    }
}
