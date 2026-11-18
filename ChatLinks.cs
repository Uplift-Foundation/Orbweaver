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
        //
        // files says whether the session behind the bubble can open a file at
        // all (IRemoteChatFiles). A cloud, OpenClaw or room session cannot, so
        // there a file link is flattened to its label exactly as before CB-363
        // rather than drawn as a link that does nothing.
        internal static bool IsLive(ChatMarkdown.MdSpan span, bool files = true) =>
            span.Style == ChatMarkdown.MdStyle.Link
            && (CloudSessionLink.IsOpenable(span.Url) || (files && IsFileUrl(span.Url)));

        // Whether a destination is one of the file forms (CB-363) rather than
        // a web address. The click handler asks it to choose between Open and
        // OpenFile, and IsLive to decide whether a span is a link at all, so
        // the two cannot disagree about which destinations are files.
        internal static bool IsFileUrl(string? url) => FileLink.TryParse(url) is not null;

        // The style a span is drawn in: its own, except that a Link span with
        // nothing openable behind it is drawn as plain text — the same rule
        // the click handler uses, so what looks like a link is exactly what
        // opens one. The parser never emits such a span today; this is the arm
        // that keeps a dead link off the screen if it ever does, and it lives
        // here rather than inline in the renderer so it is a unit case.
        internal static ChatMarkdown.MdStyle StyleOf(ChatMarkdown.MdSpan span, bool files = true) =>
            span.Style == ChatMarkdown.MdStyle.Link && !IsLive(span, files) ? ChatMarkdown.MdStyle.Normal : span.Style;

        // The link ranges of a line, in the same character offsets the block's
        // text layout reports: each span's text is concatenated into Runs in
        // order, so a running sum over span lengths is the layout's offset.
        internal static IReadOnlyList<LinkRange> RangesOf(IReadOnlyList<ChatMarkdown.MdSpan> spans, bool files = true)
        {
            var ranges = new List<LinkRange>();
            var offset = 0;

            foreach (var span in spans)
            {
                if (IsLive(span, files)) ranges.Add(new LinkRange(offset, span.Text.Length, span.Url!));
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

        // Opens a local file a chat message named, or the cached copy of one
        // fetched from another machine (CB-363). Given a decoded local path —
        // never a raw destination — and returns whether something started.
        // A seam for the same reason as Open: both UI suites set it to a no-op
        // before any test runs, so no click in either can start a real
        // application or a Finder window. FileLinkLauncher decides through
        // FileLinkPolicy on its own, whatever it is handed here.
        internal static Func<string, bool> OpenFile { get; set; } = FileLinkLauncher.OpenLocal;

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
