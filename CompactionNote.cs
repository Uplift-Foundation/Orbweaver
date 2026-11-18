using System;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orbweaver
{
    // CB-356: a Claude Code compaction, drawn as one compact, collapsible
    // "Conversation compacted" row instead of the summary arriving as a giant
    // user bubble. Claude Code writes two rows for one compaction: a `system`
    // row with subtype `compact_boundary` carrying the numbers, and a `user`
    // row with `isCompactSummary: true` carrying the summary the model resumes
    // from. This record is the pair, either half of which may be missing.
    //
    // Pure: no window, no settings, no I/O. docs/cb-356-plan.md section 2 is
    // the contract, and the measurements behind every rule here are in its
    // section 1.
    //
    // A compaction turn is Role System with Text = Line, the same choice CB-330
    // made for relays: anything that does not know about Compaction (speech,
    // Reconcile, the scroll rule, an older mirror peer, an older phone) sees a
    // dim one-line system note and degrades correctly.
    public sealed record CompactionNote(
        string? Trigger,     // as written: "auto", "manual", or anything else; null when unknown
        long? PreTokens,
        long? PostTokens,
        long? DurationMs,    // carried, never shown
        string Summary,      // Markdown for the expanded view, scaffolding stripped; "" until a summary row is seen
        bool HasBoundary)    // true when built from, or merged with, a boundary row
    {
        public bool HasSummary => Summary.Length > 0;

        // An unknown trigger is kept verbatim in Trigger and never shown: it
        // reads as the plain label. "idle" has never been observed on a real
        // row (0 of 192 on two Macs) and is mapped in case it is ever written.
        public string Label =>
            string.Equals(Trigger, "manual", StringComparison.OrdinalIgnoreCase) ? "Conversation compacted manually"
            : string.Equals(Trigger, "auto", StringComparison.OrdinalIgnoreCase) ? "Conversation compacted automatically"
            : string.Equals(Trigger, "idle", StringComparison.OrdinalIgnoreCase) ? "Conversation compacted while idle"
            : "Conversation compacted";

        // "246k → 21k tokens", "246k tokens" with only the count before, or "".
        // A count after without one before says nothing a reader can use.
        public string Detail =>
            PreTokens is not { } pre ? ""
            : PostTokens is { } post ? $"{Tokens(pre)} → {Tokens(post)} tokens"
            : $"{Tokens(pre)} tokens";

        // What ChatTurn.Text holds.
        public string Line => Detail.Length == 0 ? Label : $"{Label} · {Detail}";

        // Below a thousand, the number itself; then whole thousands rounded half
        // away from zero; then millions to one decimal once the thousands would
        // read 1000k. Invariant, so a German locale does not print "1,0M".
        public static string Tokens(long n)
        {
            if (n < 1000) return n.ToString(CultureInfo.InvariantCulture);

            var thousands = Math.Round(n / 1000m, MidpointRounding.AwayFromZero);
            if (thousands < 1000) return thousands.ToString("0", CultureInfo.InvariantCulture) + "k";

            var millions = Math.Round(n / 1_000_000m, 1, MidpointRounding.AwayFromZero);
            return millions.ToString("0.0", CultureInfo.InvariantCulture) + "M";
        }

        // The boundary row. Metadata missing or not an object still makes a row
        // (plain label, no detail). The snake_case form is the SDK's spelling,
        // read off the 2.1.327 binary's schema and never seen on a real row; it
        // is the fallback, camelCase wins when both are present.
        public static CompactionNote FromBoundary(JsonElement row)
        {
            if (row.ValueKind == JsonValueKind.Object
                && row.TryGetProperty("compactMetadata", out var camel)
                && camel.ValueKind == JsonValueKind.Object)
                return FromMetadata(camel, "trigger", "preTokens", "postTokens", "durationMs");

            if (row.ValueKind == JsonValueKind.Object
                && row.TryGetProperty("compact_metadata", out var snake)
                && snake.ValueKind == JsonValueKind.Object)
                return FromMetadata(snake, "trigger", "pre_tokens", "post_tokens", "duration_ms");

            return new CompactionNote(null, null, null, null, "", HasBoundary: true);
        }

        private static CompactionNote FromMetadata(
            JsonElement meta, string trigger, string pre, string post, string duration) =>
            new(
                meta.TryGetProperty(trigger, out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
                Count(meta, pre),
                Count(meta, post),
                Count(meta, duration),
                "",
                HasBoundary: true);

        // A non-number or a negative value counts as missing.
        private static long? Count(JsonElement meta, string name) =>
            meta.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var n)
            && n >= 0
                ? n
                : null;

        // The summary row's text, on its own: an orphan until a boundary claims it.
        public static CompactionNote FromSummary(string content) =>
            new(null, null, null, null, Strip(content), HasBoundary: false);

        public CompactionNote WithSummaryFrom(CompactionNote summary) =>
            this with { Summary = summary.Summary };

        private static readonly Regex Reminder = new(
            @"<system-reminder>.*?</system-reminder>",
            RegexOptions.Compiled | RegexOptions.Singleline);

        internal const string ArtifactPrelude = "<artifact-content-authored-by-others/>";

        internal const string Opening =
            "This session is being continued from a previous conversation that ran out of context. "
            + "The summary below covers the earlier portion of the conversation.";

        internal const string Tail = "If you need specific details from before compaction";

        private const string SummaryLabel = "Summary:";

        // Text written to the model rather than to a reader. Each step is
        // anchored and exact, and a step whose text is not there does nothing,
        // so an unfamiliar summary is shown whole rather than mangled. If the
        // result would be empty, the original trimmed is shown instead, so an
        // expandable row never opens onto nothing.
        public static string Strip(string content)
        {
            // 1. Every reminder block, tags included. An unclosed one stays.
            var text = Reminder.Replace(content, "");

            // 2. The artifact prelude and the rest of its sentence, up to the
            //    usual opening. Only when that opening follows, so the step
            //    never eats a line it cannot account for.
            var start = text.TrimStart();
            if (start.StartsWith(ArtifactPrelude, StringComparison.Ordinal))
            {
                var opening = start.IndexOf("\n" + Opening, StringComparison.Ordinal);
                if (opening >= 0) text = start[(opening + 1)..];
            }

            // 3. The opening sentence pair.
            start = text.TrimStart();
            if (start.StartsWith(Opening, StringComparison.Ordinal)) text = start[Opening.Length..];

            // 4. From the last paragraph that starts with the tail phrase, to the
            //    end. It names the transcript's path and tells the model how to
            //    resume.
            var tail = LastParagraphStart(text, Tail);
            if (tail >= 0) text = text[..tail];

            // 5. A leading "Summary:" line.
            start = text.TrimStart();
            if (start.StartsWith(SummaryLabel, StringComparison.Ordinal))
            {
                var eol = start.IndexOf('\n');
                if (eol < 0 ? start.Length == SummaryLabel.Length : start[..eol].TrimEnd() == SummaryLabel)
                    text = eol < 0 ? "" : start[(eol + 1)..];
            }

            text = text.Trim();
            return text.Length > 0 ? text : content.Trim();
        }

        // A paragraph start: the text's own start, or right after a newline.
        private static int LastParagraphStart(string text, string phrase)
        {
            var at = text.Length;
            while (at > 0)
            {
                var found = text.LastIndexOf(phrase, at - 1, StringComparison.Ordinal);
                if (found < 0) return -1;
                if (found == 0 || text[found - 1] == '\n') return found;
                at = found;
            }
            return -1;
        }

        // Across two reads: a live tail can deliver the boundary in one batch
        // and the summary in the next, the cloud stream maps one event per call,
        // and a page seam can fall between the two. When `open` is a boundary
        // still waiting for its summary and `incoming` is a summary with no
        // boundary, the summary is folded into `open` and the caller raises
        // TurnUpdated(open) instead of adding `incoming`. Two summaries, or two
        // boundaries, never merge.
        public static bool TryAbsorb(ChatTurn open, ChatTurn incoming)
        {
            if (open.Compaction is not { HasBoundary: true, HasSummary: false } boundary) return false;
            if (incoming.Compaction is not { HasBoundary: false, HasSummary: true } summary) return false;

            var merged = boundary.WithSummaryFrom(summary);
            open.Compaction = merged;
            open.Text = merged.Line;
            return true;
        }
    }
}
