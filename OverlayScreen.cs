namespace Orbweaver
{
    // CB-400: a screen Claude Code draws over the conversation that only Esc
    // closes, `/mobile`'s QR screen being the one measured. Title and Caption are
    // the words on it; Body is every row of it, QR included, kept as drawn so the
    // panel can show the code in a monospace block.
    internal sealed record Overlay(string Title, IReadOnlyList<string> Caption, IReadOnlyList<string> Body);

    // Reads such a screen off a captured pane. Pure, like ParseDialogCore: no
    // window, no settings, no process. It is deliberately about the *shape* of
    // the screen and not about /mobile: a footer segment `Esc to close`, a rule
    // above it, and no input box below it. Nothing looks for a QR, the command
    // name or the title's wording, so another full-screen overlay that draws the
    // same footer is covered by the same rules and nobody has to know it exists.
    internal static class OverlayScreen
    {
        internal const string Footer = "Esc to close", CloseKey = "Escape", CloseLabel = "Close";

        private const int MaxBodyLines = 60, MaxLineLength = 200;

        internal static Overlay? Read(string? screen)
        {
            if (string.IsNullOrEmpty(screen)) return null;

            // A real dialog wins: a numbered dialog that happens to mention the
            // footer is that dialog. A navigated list (`/permissions`, `/memory`) is
            // not excluded: the footer is the authority, and the Permissions
            // "Recently blocked" tab's footer says `Esc to approve 1 blocked`, which
            // is not `Esc to close`, so it never matches here.
            if (ChatTranscript.ParseDialogCore(screen, out _, out _) is not null) return null;

            var lines = ChatTranscript.ScreenLines(screen);

            var footer = -1;
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (IsFooter(lines[i])) { footer = i; break; }
            }
            if (footer < 0) return null;

            // The input box below is what separates a live overlay from prose
            // that quotes the footer: while the overlay is up there is no box.
            // ParseDialogCore's rule 3 relies on the same fact.
            for (var i = footer + 1; i < lines.Length; i++)
            {
                if (ChatTranscript.IsHorizontalRule(lines[i].Trim())) return null;
            }

            var edge = -1;
            // The nearest rule above the footer, however far: tall screens (/help,
            // /mcp) put it up to 47 rows above, and the conversation's own rules are
            // `─`, never this one.
            for (var i = footer - 1; i >= 0; i--)
            {
                if (IsRule(lines[i])) { edge = i; break; }
            }
            if (edge < 0) return null;

            var rows = new List<string>();
            for (var i = edge + 1; i < lines.Length; i++)
            {
                if (i != footer) rows.Add(lines[i].TrimEnd());
            }
            while (rows.Count > 0 && rows[^1].Length == 0) rows.RemoveAt(rows.Count - 1);
            while (rows.Count > 0 && rows[0].Length == 0) rows.RemoveAt(0);
            if (rows.Count == 0) return null;

            var indent = rows.Where(r => r.Length > 0).Min(r => r.Length - r.TrimStart().Length);
            var body = rows
                .Take(MaxBodyLines)
                .Select(r => r.Length == 0 ? r : r[indent..])
                .Select(r => r.Length > MaxLineLength ? r[..MaxLineLength] : r)
                .ToList();

            var caption = body.Where(r => r.Any(char.IsLetterOrDigit)).Select(r => r.Trim()).ToList();
            var title = caption.Count > 0 ? caption[0] : "";
            return new Overlay(title, caption, body);
        }

        // The overlay as the one-button prompt every surface already knows how to
        // draw: a title, a single `Close` that presses Escape, and the body.
        internal static ChatPrompt ToPrompt(Overlay o) =>
            new(o.Title, new[] { new ChatPromptOption(CloseKey, CloseLabel) }, Body: o.Body);

        private static bool IsFooter(string line) =>
            line.Trim().Split(" · ").Any(seg => seg.Trim() == Footer);

        // The overlay's top edge: a run of `▔` (U+2594), measured on the Mac and on
        // the Windows console alike.
        private static bool IsRule(string line)
        {
            var t = line.Trim();
            return t.Length >= 10 && t.All(c => c == '▔');
        }
    }
}
