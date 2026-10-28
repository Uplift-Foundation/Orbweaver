namespace Orbweaver
{
    // The letters an orb wears, and the letters the chat panel's header wears
    // beside it. Pure: no window, no settings, no colours — which is the whole
    // point of it being here rather than in OrbWindow, where it lived until the
    // hyphen bug and could only be exercised by looking at the screen.
    //
    // Same rule as OrbArrangement and the two transcript parsers: the thing
    // with a right answer is separated from the thing that draws it, so the
    // right answer can be asserted. See tests/GlyphTests.
    //
    // TwoLetterGlyphs is a user setting and is therefore a parameter, not a
    // lookup. A function that reads settings can only be tested on the machine
    // whose settings happen to suit — and on macOS the settings file does not
    // follow HOME, so a test could not have set it anyway.
    internal static class OrbGlyph
    {
        // What counts as the end of a word. A space is the obvious one; the
        // rest are here because the names this draws from are mostly not
        // written by hand. A session is usually named for its directory, and
        // directories are kebab or snake case — "claude-buddy" was one word to
        // a split on spaces and drew "Cl", two letters off the front of the
        // first half, which is exactly the "reads as a typo of it" case the
        // two-word branch below exists to avoid. It is "Cb".
        //
        // All three dashes, because an em dash separates words whether or not
        // someone put spaces around it. Spaced, it already worked — "Aurora —
        // wtvamp" split into three tokens and Initial() dropped the lone dash —
        // but "Aurora—wtvamp" was one word and gave "Au". Both give "Aw".
        //
        // Not '.' or '/', deliberately. Those show up in paths and version
        // numbers, where what follows the separator is rarely a word anyone
        // would take an initial from.
        internal static readonly char[] WordSeparators = { ' ', '-', '_', '–', '—' };

        public static string For(string label, bool twoLetter)
        {
            label = label.TrimStart();
            if (label.Length == 0) return "•";

            if (!twoLetter) return FirstGrapheme(label).ToUpperInvariant();

            // Two words get one letter each — the initials a person would
            // write by hand ("Menu UX" -> "Mu") — rather than two letters
            // from the first word alone, which reads as a typo of it
            // ("Menu UX" -> "Me"). A single word falls back to its own
            // first two letters, since there's nothing else to draw from.
            //
            // Upper then lower, not both upper: two capitals side by side
            // reads as an acronym ("MU"), where the point here is a little
            // word-shaped mark ("Mu") — same reason a monogram is "Mu", not
            // "MU". Only the letter case changes; which letters are picked
            // is exactly the same either way.
            //
            // Only words with something readable in them count, and the initial
            // is the first such character rather than the first character.
            // "Aurora — wtvamp" split into three tokens, the middle one a
            // lone em dash, and taking the first two of those produced "L—" on
            // every orb — which is how that was found. Skipping *within* a word
            // as well is what makes "#kubernetes" contribute "k" rather than
            // being thrown away for starting with a hash.
            var words = label
                .Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(Initial)
                .Where(initial => initial.Length > 0)
                .ToArray();

            if (words.Length >= 2)
                return words[0].ToUpperInvariant() + words[1].ToLowerInvariant();

            // One word, or none worth reading: take two letters from the label
            // itself, which is the old behaviour and still right for "Menu".
            //
            // From where it starts *reading*, though. The two-word branch above
            // skips leading punctuation per word, and this one did not — so
            // "#kubernetes" gave "Ku" only because it is two words after the
            // channel name is prefixed, while a single-word "#arch" gave "#a".
            // A room orb is named for its channel, so that is the common case
            // rather than an oddity.
            var readable = ReadableStart(label);
            var first = FirstGrapheme(readable);
            var rest = readable[first.Length..];
            var second = rest.Length > 0 ? FirstGrapheme(rest) : "";
            return first.ToUpperInvariant() + second.ToLowerInvariant();
        }

        // CB-287: a user-chosen override, used verbatim — trimmed and cut to
        // the first one or two *text elements*, case untouched. Not For(): that
        // derives letters from a word, and an override is the letters. Text
        // elements rather than chars so a family emoji (seven code units), a
        // flag (two) or a letter plus combining accent counts as one.
        public static string Clip(string? text, bool twoLetter)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            var e = System.Globalization.StringInfo.GetTextElementEnumerator(text.Trim());
            var sb = new System.Text.StringBuilder();
            for (var n = 0; n < (twoLetter ? 2 : 1) && e.MoveNext(); n++) sb.Append(e.GetTextElement());
            return sb.ToString();
        }

        // First letters of the first two words: "Ada Lovelace" gives AL, and a
        // single-word name gives its first two characters rather than one, which
        // fills a 68pt circle better and still reads as a monogram.
        //
        // Both letters capitalised here, unlike For() above, and the difference
        // is deliberate rather than an oversight: this draws at 26pt in a large
        // circle where an acronym reads fine, where the orb's glyph is a 12pt
        // mark on a 36pt disc and wants to look like a little word.
        public static string Initials(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";

            var words = name.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return "";

            if (words.Length == 1)
            {
                var w = words[0];
                return (w.Length >= 2 ? w[..2] : w).ToUpperInvariant();
            }

            return string.Concat(
                char.ToUpperInvariant(words[0][0]),
                char.ToUpperInvariant(words[1][0]));
        }

        // The first thing in a word a reader would call its initial, skipping
        // punctuation. Empty when there is nothing readable in it at all.
        private static string Initial(string word)
        {
            for (var i = 0; i < word.Length; i++)
            {
                if (char.IsHighSurrogate(word[i])) return word.Substring(i, Math.Min(2, word.Length - i));
                if (char.IsLetterOrDigit(word[i])) return word.Substring(i, 1);
            }

            return "";
        }

        private static string ReadableStart(string label)
        {
            for (var i = 0; i < label.Length; i++)
            {
                var c = label[i];
                if (c > 127 || char.IsLetterOrDigit(c)) return label[i..];
            }

            return label;
        }

        // An emoji is two chars, and slicing one in half draws a tofu box.
        private static string FirstGrapheme(string s) =>
            s.Length > 1 && char.IsHighSurrogate(s[0]) ? s[..2] : s[..1];
    }
}
