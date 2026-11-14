using System;
using System.Collections.Generic;

namespace Orbweaver
{
    // Which link, if any, a pointer is over in one line of a chat bubble — and
    // whether a press and a release together amount to opening it (CB-284).
    //
    // A line of a reply is one SelectableTextBlock built from Runs, and a Run
    // is not an input element in Avalonia: it has no pointer events of its
    // own. So the panel hit-tests the block's text layout to a character
    // offset and asks this file what lives there. Everything here is that
    // second half — offsets and strings, no window and no layout — so each
    // rule is a unit case rather than something only a synthesized click can
    // reach.
    internal static class ChatLinks
    {
        // One clickable stretch of a line: where it starts in the block's text,
        // how long it is, and where it goes.
        internal readonly record struct LinkRange(int Start, int Length, string Url);

        // Whether a span is drawn and treated as a link at all.
        //
        // The parser only hands out a Link span whose destination passed
        // CloudSessionLink.IsOpenable, so on today's ChatMarkdown the second
        // half is always true. It is checked again here because this is the
        // one rule the renderer and the click handler must agree on — a span
        // drawn as a link that opens nothing is a dead link, and a span drawn
        // as text that opens something on click is worse — and stating it once,
        // beside the launcher's own allowlist, keeps the two from drifting if
        // the parser's contract ever loosens.
        internal static bool IsLive(ChatMarkdown.MdSpan span) =>
            span.Style == ChatMarkdown.MdStyle.Link && CloudSessionLink.IsOpenable(span.Url);

        // The link ranges of a line, in the same character offsets the block's
        // text layout reports: each span's text is concatenated into Runs in
        // order, so a running sum over span lengths is the layout's offset.
        internal static IReadOnlyList<LinkRange> RangesOf(IReadOnlyList<ChatMarkdown.MdSpan> spans)
        {
            var ranges = new List<LinkRange>();
            var offset = 0;

            foreach (var span in spans)
            {
                if (IsLive(span)) ranges.Add(new LinkRange(offset, span.Text.Length, span.Url!));
                offset += span.Text.Length;
            }

            return ranges;
        }

        // The destination of the link covering this character, or null.
        internal static string? UrlAt(IReadOnlyList<LinkRange>? ranges, int textPosition)
        {
            if (ranges is null) return null;

            foreach (var range in ranges)
            {
                if (textPosition >= range.Start && textPosition < range.Start + range.Length) return range.Url;
            }

            return null;
        }

        // Opens a link someone clicked in a message. A seam rather than a
        // direct call, the same shape as OpenClawVideo.Open: both UI suites set
        // it to a no-op before any test runs, so no test in either can launch a
        // real browser, and the link tests swap in a recorder to see what would
        // have been opened. CloudSessionLink.Open refuses anything but http and
        // https on its own, whatever it is handed here.
        //
        // Here rather than on ChatPanel because the suites set it from a module
        // initializer, before Avalonia is up — and touching ChatPanel that
        // early runs its static constructor, which draws the resize cursors
        // and throws without a render interface. This class has no Avalonia
        // statics to trip over.
        internal static Func<string, bool> Open { get; set; } = CloudSessionLink.Open;

        // A click opens a link only when it began and ended on the same one and
        // left nothing selected.
        //
        // Same link, because a press on one and a release on another is a
        // drag, not a choice of either. Nothing selected, because a drag that
        // happens to start and end inside one long URL is somebody selecting
        // it to copy — and a double-click selects a word, which is how a second
        // click on a link avoids opening the page twice.
        internal static bool ShouldOpen(string? pressedUrl, string? releasedUrl, bool hasSelection) =>
            pressedUrl is not null
            && string.Equals(pressedUrl, releasedUrl, StringComparison.Ordinal)
            && !hasSelection;
    }
}
