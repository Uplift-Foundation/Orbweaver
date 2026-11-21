using System.Text.RegularExpressions;

namespace Orbweaver
{
    // CB-400: a pane Claude Code draws over the conversation that Esc leaves,
    // `/mobile`'s QR screen being the first measured and `/mcp`, `/config`, `/help`
    // and the rest the same shape. Title and Caption are the words on it; Body is
    // every row of it, QR included, kept as drawn so the panel can show the code in
    // a monospace block. Label is what Esc does here, from the footer's own verb.
    internal sealed record Overlay(string Title, IReadOnlyList<string> Caption, IReadOnlyList<string> Body, string Label);

    // Reads such a pane off a captured screen. Pure, like ParseDialogCore: no
    // window, no settings, no process. It is about the *shape* of the pane and not
    // about any one command: a footer segment naming Esc and a leaving verb, a run
    // of `▔` above it, and no input box below it. Nothing looks for a QR, a command
    // name or a title's wording, so another pane of the same shape is covered by the
    // same rules and nobody has to know it exists.
    //
    // Why this cannot catch a permission prompt or a question form, where Esc
    // denies or abandons: none of the real captures of those has a `▔` line (their
    // rules are `─` and `╌`), and a numbered dialog is read first by
    // ParseDialogCore. The `▔` edge is what separates a pane you leave from a
    // dialog you answer, so it is required and it is not generalised to other
    // glyphs.
    internal static class OverlayScreen
    {
        internal const string CloseKey = "Escape", CloseLabel = "Close", GoBackLabel = "Go back", CancelLabel = "Cancel";

        // The footer segments that leave a pane, and the button each one labels. Any
        // other verb (`Esc to approve 1 blocked`, `Esc to clear`, `esc to interrupt`)
        // is not a footer: it either answers something or only edits a search box.
        private static readonly (string Segment, string Label)[] Footers =
        [
            ("Esc to close", CloseLabel),
            ("Esc to go back", GoBackLabel),
            ("Esc to cancel", CancelLabel),
        ];

        private const int MaxBodyLines = 60, MaxLineLength = 200;

        // The last row of a body that was cut, at either bound, so nobody takes the
        // rows shown for the whole screen. A cut body is the first 59 rows, each
        // clipped to 200 columns, plus this as row 60: never more than 60 rows, and
        // the same shape MirrorProtocol puts on the wire, so a body cut here passes
        // through its bound unchanged (a test pins the two texts equal).
        internal const string CutMarker = "… (more in the terminal)";

        private static readonly Regex TwoSpaces = new(@"\s{2,}", RegexOptions.CultureInvariant);

        internal static Overlay? Read(string? screen)
        {
            if (string.IsNullOrEmpty(screen)) return null;

            // A real dialog wins: a numbered dialog that happens to mention a footer
            // is that dialog. Callers also try a question form before this.
            if (ChatTranscript.ParseDialogCore(screen, out _, out _) is not null) return null;

            var lines = ChatTranscript.ScreenLines(screen);

            var footer = -1;
            string? label = null;
            for (var i = lines.Length - 1; i >= 0 && footer < 0; i--)
            {
                if (FooterLabel(lines[i]) is { } found) { footer = i; label = found; }
            }
            if (footer < 0) return null;

            // The input box below is what separates a live pane from prose that
            // quotes the footer: while the pane is up there is no box.
            for (var i = footer + 1; i < lines.Length; i++)
            {
                if (ChatTranscript.IsHorizontalRule(lines[i].Trim())) return null;
            }

            // The nearest edge above the footer, however far: tall panes put it up to
            // 47 rows above, and the conversation's own rules are `─`.
            var edge = -1;
            for (var i = footer - 1; i >= 0 && edge < 0; i--)
            {
                if (IsEdge(lines[i])) edge = i;
            }
            if (edge < 0) return null;

            // A footer that wrapped onto the line above it: lines directly above the
            // footer (no blank between) that still hold a separator.
            var bodyEnd = footer;
            while (bodyEnd - 1 > edge && lines[bodyEnd - 1].Contains('·')) bodyEnd--;

            var rows = new List<string>();
            for (var i = edge + 1; i < bodyEnd; i++) rows.Add(lines[i].TrimEnd());
            for (var i = footer + 1; i < lines.Length; i++) rows.Add(lines[i].TrimEnd());
            while (rows.Count > 0 && rows[^1].Length == 0) rows.RemoveAt(rows.Count - 1);
            while (rows.Count > 0 && rows[0].Length == 0) rows.RemoveAt(0);
            if (rows.Count == 0) return null;

            var indent = rows.Where(r => r.Length > 0).Min(r => r.Length - r.TrimStart().Length);
            var stripped = rows.Select(r => r.Length == 0 ? r : r[indent..]).ToList();
            var cut = stripped.Count > MaxBodyLines || stripped.Any(r => r.Length > MaxLineLength);
            var body = (cut ? stripped.Take(MaxBodyLines - 1) : stripped)
                .Select(r => r.Length > MaxLineLength ? r[..MaxLineLength] : r).ToList();
            if (cut) body.Add(CutMarker);

            var caption = body.Where(r => r.Any(char.IsLetterOrDigit)).Select(r => Collapse(r.Trim())).ToList();
            var first = body.FirstOrDefault(r => r.Trim().Length > 0);
            var title = first is null ? "" : TwoSpaces.Split(first.Trim())[0];
            return new Overlay(title, caption, body, label!);
        }

        // The overlay as the one-button prompt every surface already knows how to
        // draw: a title, a single button that presses Escape, and the body.
        internal static ChatPrompt ToPrompt(Overlay o) =>
            new(o.Title, new[] { new ChatPromptOption(CloseKey, o.Label) }, Body: o.Body);

        private static string? FooterLabel(string line)
        {
            foreach (var seg in line.Trim().Split(" · "))
            {
                var t = seg.Trim();
                foreach (var (segment, label) in Footers)
                    if (t == segment) return label;
            }
            return null;
        }

        private static string Collapse(string s) => TwoSpaces.Replace(s, " ");

        // The pane's top edge: a run of `▔` (U+2594), measured on the Mac and on the
        // Windows console alike.
        private static bool IsEdge(string line)
        {
            var t = line.Trim();
            return t.Length >= 10 && t.All(c => c == '▔');
        }
    }
}
