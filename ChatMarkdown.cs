namespace Orbweaver
{
    // Enough Markdown to read a reply by, and no more.
    //
    // Both things the chat panel talks to write Markdown, and until this existed
    // the panel drew it literally — asterisks, backticks and all. That is worst
    // exactly where it matters most: a reply full of `**` reads as noise, and a
    // fenced code block wrapped as prose is unreadable.
    //
    // Pure, like ChatTranscript, and tested beside it. Nothing here knows what a
    // TextBlock is; ChatPanel turns these blocks and spans into controls.
    //
    // This is deliberately a *subset*, chosen from what the two transports
    // actually produce rather than from the CommonMark spec:
    //
    //  * Underscores are never emphasis. `file_path` and `snake_case_name` are
    //    far more common in this app's conversations than _emphasis_, and
    //    treating `_` as a delimiter mangles them. Only `*` emphasises.
    //  * Tables become Table blocks: verbatim, which ChatPanel draws in
    //    monospace, unwrapped, inside a horizontal scroller. Reflowing one
    //    into a paragraph destroys it, a real table renderer inside a 244pt
    //    bubble would be illegible anyway, and wrapping a row that is wider
    //    than the bubble moves every later column onto the next line — so a
    //    table scrolls where ordinary code wraps. Four shapes are tables:
    //    a pipe table with a leading `|`, a pipe table without one (only when
    //    a GFM delimiter row of the same width follows the header, which is
    //    what keeps `a | b` in prose), a box-drawn table (┌─┬─┐ │ ├─┼─┤)
    //    and an ASCII one (+---+). A fence whose every line is one of those
    //    is a table too, since a model pasting terminal output fences it.
    //    Tree output (├── src) is box-drawn as far as this is concerned and
    //    becomes a Table block: harmless, because it is monospace and
    //    unwrapped, which is what a tree wants as well.
    //  * No nested lists, no reference links, no HTML, no images. None of it
    //    turns up, and every rule here is a rule that can mis-fire.
    //  * Links are http and https only. An inline link, an <autolink> and a
    //    bare URL in prose all become Link spans carrying their destination,
    //    but only when CloudSessionLink.IsOpenable would hand that destination
    //    to a browser. Anything else — a relative path, file:, a scheme some
    //    installed program registered — keeps its label as plain text, so
    //    nothing on screen looks clickable that a click would not open.
    //  * Spans do not nest, so a URL inside **bold** or *italic* stays part
    //    of the emphasis and is not a link.
    public static class ChatMarkdown
    {
        public enum MdKind { Paragraph, Heading, Bullet, Ordered, Code, Quote, Table }

        // Marker is the bullet glyph or "3." for an ordered item — kept as
        // written so a list that starts at 4 still says 4.
        public sealed record MdBlock(MdKind Kind, string Text, string Marker = "", int Depth = 0);

        public enum MdStyle { Normal, Bold, Italic, BoldItalic, Code, Link }

        // Url is where a Link span goes, and it is null on every other style.
        //
        // The contract is that a Link span always carries one: a destination
        // the launcher would refuse (CloudSessionLink.IsOpenable) is not made
        // into a Link span in the first place, so a span that looks clickable
        // and a span that is clickable are the same set. Defaulted so the
        // styles that have no destination do not have to say so.
        public sealed record MdSpan(string Text, MdStyle Style, string? Url = null);

        // --- blocks ---

        public static List<MdBlock> Parse(string source)
        {
            var blocks = new List<MdBlock>();
            if (string.IsNullOrWhiteSpace(source)) return blocks;

            var lines = source.Replace("\r", "").Split('\n');
            var paragraph = new List<string>();

            void FlushParagraph()
            {
                if (paragraph.Count == 0) return;

                // Joined with a space, which is what Markdown means by a line
                // break inside a paragraph — the bubble does its own wrapping
                // and honouring the source's line endings would wrap twice.
                blocks.Add(new MdBlock(MdKind.Paragraph, string.Join(" ", paragraph)));
                paragraph.Clear();
            }

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                // Fenced code. Everything to the closing fence is literal —
                // including anything that looks like a heading or a list, which
                // is the whole point of a fence.
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    FlushParagraph();

                    var language = trimmed[3..].Trim();
                    var body = new List<string>();
                    i++;

                    while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                        body.Add(lines[i++]);

                    // An unterminated fence still becomes a code block: a reply
                    // read while it is still being written is the normal case,
                    // not a malformed document.
                    var code = Dedent(body);
                    blocks.Add(new MdBlock(IsAllTable(code) ? MdKind.Table : MdKind.Code, code, language));
                    continue;
                }

                // Tables, kept verbatim. See the note at the top.
                // Two consecutive lines at least: a lone "│ x" or "├── a" in
                // prose is a character somebody typed, not a table, and falls
                // through to be paragraph text. (A fence is another matter —
                // see IsAllTable — since there the author said it was code.)
                if (IsBoxLine(trimmed) && i + 1 < lines.Length && IsBoxLine(lines[i + 1].TrimStart()))
                {
                    FlushParagraph();

                    var rows = new List<string>();
                    while (i < lines.Length && IsBoxLine(lines[i].TrimStart()))
                        rows.Add(lines[i++].Trim());

                    i--;
                    blocks.Add(new MdBlock(MdKind.Table, string.Join("\n", rows)));
                    continue;
                }

                if (trimmed.StartsWith('|') || IsAsciiBorder(trimmed))
                {
                    FlushParagraph();

                    var rows = new List<string>();
                    while (i < lines.Length)
                    {
                        var t = lines[i].Trim();
                        if (!t.StartsWith('|') && !IsAsciiBorder(t)) break;

                        rows.Add(t);
                        i++;
                    }

                    i--;
                    blocks.Add(new MdBlock(MdKind.Table, string.Join("\n", rows)));
                    continue;
                }

                // Not on a line that starts something else: "# a | b" over a
                // delimiter row is a heading, and a bullet or a quote likewise.
                if (HasUnescapedPipe(trimmed)
                    && Heading(trimmed) is null
                    && ListItem(line) is null
                    && !trimmed.StartsWith("> ", StringComparison.Ordinal)
                    && i + 1 < lines.Length
                    && IsDelimiterRow(lines[i + 1].Trim())
                    && CellCount(trimmed) == CellCount(lines[i + 1].Trim()))
                {
                    FlushParagraph();

                    var rows = new List<string> { trimmed.TrimEnd(), lines[i + 1].Trim() };
                    i += 2;

                    while (i < lines.Length && HasUnescapedPipe(lines[i]))
                        rows.Add(lines[i++].Trim());

                    i--;
                    blocks.Add(new MdBlock(MdKind.Table, string.Join("\n", rows)));
                    continue;
                }

                if (trimmed.Length == 0)
                {
                    FlushParagraph();
                    continue;
                }

                // A rule is a paragraph separator that draws nothing. Drawing a
                // line inside a 244pt bubble reads as a border that has gone
                // wrong.
                if (IsRule(trimmed))
                {
                    FlushParagraph();
                    continue;
                }

                var heading = Heading(trimmed);
                if (heading is not null)
                {
                    FlushParagraph();
                    blocks.Add(heading);
                    continue;
                }

                var item = ListItem(line);
                if (item is not null)
                {
                    FlushParagraph();
                    blocks.Add(item);
                    continue;
                }

                if (trimmed.StartsWith("> ", StringComparison.Ordinal))
                {
                    FlushParagraph();
                    blocks.Add(new MdBlock(MdKind.Quote, trimmed[2..].Trim()));
                    continue;
                }

                paragraph.Add(trimmed);
            }

            FlushParagraph();
            return blocks;
        }

        // A line a box-drawn table is made of: it starts with a glyph from the
        // Box Drawing block. A line that starts with a horizontal-only glyph is
        // not one — a lone "────────" is a separator somebody typed, not a
        // table, and stays prose as it always was. Corners, verticals and tees
        // all qualify, so a table with no closing border (streamed, or cut off)
        // is still recognised from its first line.
        internal static bool IsBoxLine(string trimmed) =>
            trimmed.Length > 0
            && trimmed[0] is >= '\u2500' and <= '\u257F'
            && !HorizontalOnly.Contains(trimmed[0]);

        private const string HorizontalOnly = "─━┄┅┈┉╌╍═╴╶╸╺╼╾";

        // "+----+------+": the rule line of a sqlite or MySQL table.
        internal static bool IsAsciiBorder(string trimmed)
        {
            if (trimmed.Length < 3 || trimmed[0] != '+' || trimmed[^1] != '+') return false;

            var dashes = false;
            foreach (var c in trimmed)
            {
                if (c is not ('+' or '-' or '=' or ':')) return false;
                if (c is '-' or '=') dashes = true;
            }

            return dashes;
        }

        // GFM's delimiter row, "---|:---:|---". It needs an internal pipe, so
        // a bare "---" — a rule — never matches.
        internal static bool IsDelimiterRow(string trimmed) =>
            DelimiterRow.IsMatch(trimmed);

        private static readonly System.Text.RegularExpressions.Regex DelimiterRow = new(
            @"^\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)+\|?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        // Cells in a pipe row: one leading and one trailing pipe are framing,
        // and a pipe preceded by a backslash is text.
        internal static int CellCount(string row)
        {
            var t = row.Trim();
            if (t.StartsWith('|')) t = t[1..];
            if (t.EndsWith('|') && !t.EndsWith("\\|", StringComparison.Ordinal)) t = t[..^1];

            var cells = 1;
            for (var k = 0; k < t.Length; k++)
                if (t[k] == '|' && (k == 0 || t[k - 1] != '\\')) cells++;

            return cells;
        }

        private static bool HasUnescapedPipe(string line)
        {
            for (var k = 0; k < line.Length; k++)
                if (line[k] == '|' && (k == 0 || line[k - 1] != '\\')) return true;

            return false;
        }

        // A fence whose every non-blank line is table material. One other
        // line and it is code, which keeps wrapping.
        private static bool IsAllTable(string code)
        {
            var any = false;
            foreach (var raw in code.Split('\n'))
            {
                var t = raw.Trim();
                if (t.Length == 0) continue;
                if (!IsBoxLine(t) && !IsAsciiBorder(t) && !t.StartsWith('|')) return false;

                any = true;
            }

            return any;
        }

        private static MdBlock? Heading(string trimmed)
        {
            var level = 0;
            while (level < trimmed.Length && trimmed[level] == '#') level++;

            if (level is 0 or > 6) return null;
            if (level >= trimmed.Length || trimmed[level] != ' ') return null;

            return new MdBlock(MdKind.Heading, trimmed[(level + 1)..].Trim(), "", level);
        }

        private static MdBlock? ListItem(string line)
        {
            var indent = 0;
            while (indent < line.Length && line[indent] == ' ') indent++;

            var rest = line[indent..];
            var depth = Math.Min(indent / 2, 3);

            if (rest.Length > 2 && rest[0] is '-' or '*' or '+' && rest[1] == ' ')
                return new MdBlock(MdKind.Bullet, rest[2..].Trim(), "•", depth);

            var digits = 0;
            while (digits < rest.Length && char.IsAsciiDigit(rest[digits])) digits++;

            if (digits is > 0 and <= 3
                && digits + 1 < rest.Length
                && rest[digits] is '.' or ')'
                && rest[digits + 1] == ' ')
            {
                return new MdBlock(MdKind.Ordered, rest[(digits + 2)..].Trim(), rest[..(digits + 1)], depth);
            }

            return null;
        }

        private static bool IsRule(string trimmed) =>
            trimmed.Length >= 3
            && (trimmed.All(c => c == '-') || trimmed.All(c => c == '*') || trimmed.All(c => c == '_'));

        // Code inside a bubble that is already indented reads as doubly
        // indented, so the common leading whitespace comes off.
        private static string Dedent(List<string> body)
        {
            while (body.Count > 0 && body[0].Trim().Length == 0) body.RemoveAt(0);
            while (body.Count > 0 && body[^1].Trim().Length == 0) body.RemoveAt(body.Count - 1);
            if (body.Count == 0) return "";

            var common = int.MaxValue;
            foreach (var line in body)
            {
                if (line.Trim().Length == 0) continue;

                var n = 0;
                while (n < line.Length && line[n] == ' ') n++;
                common = Math.Min(common, n);
            }

            if (common is 0 or int.MaxValue) return string.Join("\n", body);

            return string.Join("\n", body.Select(l => l.Length >= common ? l[common..] : l.TrimStart()));
        }

        // --- inline spans ---

        // Left to right, one pass, no backtracking. A delimiter with no partner
        // is text: half-written emphasis is normal in a reply being streamed,
        // and swallowing the rest of the paragraph looking for a closer that
        // never comes is worse than showing an asterisk.
        public static List<MdSpan> Inline(string text)
        {
            var spans = new List<MdSpan>();
            if (string.IsNullOrEmpty(text)) return spans;

            var plain = new System.Text.StringBuilder();

            void Flush()
            {
                if (plain.Length == 0) return;

                spans.Add(new MdSpan(plain.ToString(), MdStyle.Normal));
                plain.Clear();
            }

            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];

                // Code first, so a `**` inside it stays literal.
                if (c == '`')
                {
                    var close = text.IndexOf('`', i + 1);
                    if (close > i + 1)
                    {
                        Flush();
                        spans.Add(new MdSpan(text[(i + 1)..close], MdStyle.Code));
                        i = close + 1;
                        continue;
                    }
                }
                else if (c == '*')
                {
                    var run = 1;
                    while (i + run < text.Length && text[i + run] == '*' && run < 3) run++;

                    var delim = new string('*', run);
                    var close = text.IndexOf(delim, i + run, StringComparison.Ordinal);

                    if (close > i + run)
                    {
                        Flush();

                        var style = run switch
                        {
                            1 => MdStyle.Italic,
                            2 => MdStyle.Bold,
                            _ => MdStyle.BoldItalic
                        };

                        spans.Add(new MdSpan(text[(i + run)..close], style));
                        i = close + run;
                        continue;
                    }
                }
                else if (c == '[')
                {
                    var link = Link(text, i);
                    if (link is not null)
                    {
                        // A destination the launcher would refuse keeps its
                        // label and loses the markup: the words still read,
                        // and nothing is drawn as a link that opens nothing.
                        if (link.Value.Url is null)
                        {
                            plain.Append(link.Value.Label);
                        }
                        else
                        {
                            Flush();
                            spans.Add(new MdSpan(link.Value.Label, MdStyle.Link, link.Value.Url));
                        }

                        i = link.Value.Next;
                        continue;
                    }
                }
                else if (c == '<')
                {
                    // <https://…>, CommonMark's autolink. Anything else in
                    // angle brackets is text, as it always was.
                    var close = text.IndexOf('>', i + 1);
                    if (close > i + 1)
                    {
                        var inner = text[(i + 1)..close];
                        if (CloudSessionLink.IsOpenable(inner))
                        {
                            Flush();
                            spans.Add(new MdSpan(inner, MdStyle.Link, inner));
                            i = close + 1;
                            continue;
                        }
                    }
                }
                else if (StartsBareUrl(text, i))
                {
                    var url = BareUrl(text, i);
                    if (CloudSessionLink.IsOpenable(url))
                    {
                        Flush();
                        spans.Add(new MdSpan(url, MdStyle.Link, url));
                        i += url.Length;
                        continue;
                    }
                }

                plain.Append(c);
                i++;
            }

            Flush();
            return spans;
        }

        // "[label](dest)": the label, and the destination when a browser
        // should be handed it, or null when it should not.
        //
        // The closing ')' is found by balancing, not by taking the first one,
        // because the URLs people actually paste carry parentheses —
        // Wikipedia's disambiguated titles most of all — and stopping at the
        // first ')' cut those short and left a stray ")" in the prose.
        // Unbalanced to the end of the text is not a link, which is the normal
        // state of a reply that is still being streamed.
        private static (string Label, string? Url, int Next)? Link(string text, int start)
        {
            var close = text.IndexOf(']', start + 1);
            if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(') return null;

            var end = -1;
            var depth = 1;
            for (var j = close + 2; j < text.Length; j++)
            {
                if (text[j] == '(') depth++;
                else if (text[j] == ')' && --depth == 0)
                {
                    end = j;
                    break;
                }
            }

            if (end < 0) return null;

            var label = text[(start + 1)..close];
            if (label.Length == 0) return null;

            return (label, Destination(text[(close + 2)..end]), end + 1);
        }

        // The URL inside a link's parentheses: a "title" after it dropped,
        // CommonMark's <…> wrapping taken off, and then the launcher's rule.
        private static string? Destination(string inner)
        {
            var dest = inner.Trim();

            var space = dest.IndexOfAny([' ', '\t']);
            if (space >= 0) dest = dest[..space];

            if (dest.Length >= 2 && dest[0] == '<' && dest[^1] == '>') dest = dest[1..^1];

            return CloudSessionLink.IsOpenable(dest) ? dest : null;
        }

        // A bare URL starts at "http://" or "https://", and not in the middle
        // of a word — "xhttps://" is somebody's identifier, not an address.
        private static bool StartsBareUrl(string text, int i) =>
            (i == 0 || !char.IsLetterOrDigit(text[i - 1]))
            && (text.AsSpan(i).StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || text.AsSpan(i).StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        // From the scheme to the first character no URL in prose contains,
        // then trimmed of what belongs to the sentence rather than the
        // address. A full stop or a comma after a link is the sentence's; a
        // closing bracket is the sentence's only when the URL has no opener
        // for it, which is what keeps ".../A_(b)" whole and "(see .../x)" not.
        // The trimmed characters are not lost — the caller only consumes the
        // URL, so they come back round as plain text.
        private static string BareUrl(string text, int start)
        {
            var end = start;
            while (end < text.Length
                   && !char.IsWhiteSpace(text[end])
                   && !char.IsControl(text[end])
                   && text[end] is not ('<' or '>' or '`'))
            {
                end++;
            }

            var url = text[start..end];

            // Never runs dry: the caller only gets here at "http://" or
            // "https://", and a '/' is never trimmed, so url[^1] always exists.
            while (BelongsToTheSentence(url)) url = url[..^1];

            return url;
        }

        private static bool BelongsToTheSentence(string url) => url[^1] switch
        {
            '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"' or '\u2019' or '\u201D' => true,
            ')' => Unmatched(url, '(', ')'),
            ']' => Unmatched(url, '[', ']'),
            '}' => Unmatched(url, '{', '}'),
            _ => false
        };

        private static bool Unmatched(string url, char open, char close) =>
            url.Count(c => c == close) > url.Count(c => c == open);
    }
}
