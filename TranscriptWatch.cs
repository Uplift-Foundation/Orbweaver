using System.Text;

namespace Orbweaver
{
    // What a watched session's transcript becomes on the wire (CB-316,
    // frames-v4 `window`). Pure: parsed rows in, canonical window plaintexts
    // out. The parsers (ChatTranscript, CodexTranscript, GrokTranscript) stay
    // the authority on what a line is; this only numbers, cuts and chunks
    // their output, so the phone shows exactly what the chat panel shows.
    //
    // TranscriptWatcher owns the file and the timing; this owns every rule
    // the vectors pin, and the unit tests replay frames-v4-vectors.json's
    // window chunks through it byte for byte.
    internal static class TranscriptWatch
    {
        internal const int MaxWindowBytes = 48000;
        internal const int MaxLineBytes = 8000;
        internal const int BackfillLines = 200;
        private const string Ellipsis = "…";

        internal readonly record struct WireLine(string Role, string Text, long At, bool Error, bool Cut);

        internal static string RoleOf(ChatRole role) => role switch
        {
            ChatRole.User => "user",
            ChatRole.Assistant => "assistant",
            _ => "system",
        };

        internal static WireLine From(ChatTurn turn) => Line(RoleOf(turn.Role), turn.Text, turn.At.ToUnixTimeSeconds(), turn.IsError);

        internal static WireLine Line(string role, string text, long at, bool error = false)
        {
            var (kept, cut) = CutText(text);
            return new WireLine(role, kept, at, error, cut);
        }

        // A text over 8,000 UTF-8 bytes becomes its longest prefix ending on
        // a scalar boundary of at most 7,997 bytes, plus U+2026 (3 bytes).
        // Bytes, not chars: C# counts UTF-16 units and Swift grapheme
        // clusters, and the two disagree on exactly the text that matters.
        internal static (string Text, bool Cut) CutText(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length <= MaxLineBytes) return (text, false);
            var keep = MaxLineBytes - Encoding.UTF8.GetByteCount(Ellipsis);
            while (keep > 0 && (bytes[keep] & 0xC0) == 0x80) keep--;   // back to a scalar boundary
            return (Encoding.UTF8.GetString(bytes, 0, keep) + Ellipsis, true);
        }

        internal static string LineJson(WireLine l) =>
            new CanonicalJson().Str("role", l.Role).Str("text", l.Text).Num("at", l.At)
                .Flag("error", l.Error).Flag("cut", l.Cut).ToString();

        private static string Header(string sessionId, long fromLine, long total, bool live, bool reset) =>
            "{\"t\":\"window\",\"session_id\":" + CanonicalJson.Quote(sessionId)
            + ",\"from_line\":" + fromLine + ",\"total\":" + total + ",\"live\":" + (live ? "true" : "false")
            + (reset ? ",\"reset\":true" : "") + ",\"lines\":[";

        internal static string Window(string sessionId, long fromLine, long total, bool live, bool reset, IEnumerable<string> lineJsons) =>
            Header(sessionId, fromLine, total, live, reset) + string.Join(",", lineJsons) + "]}";

        // Greedy, in order: a line joins the open window while the canonical
        // window stays within 48,000 bytes, else it opens the next one, so
        // every window but the last is maximal. A single line always fits an
        // empty window (8,000-byte cap). reset rides only on the first.
        internal static List<string> Chunk(string sessionId, long first, long total, bool live, IReadOnlyList<WireLine> lines, bool reset = false)
        {
            var windows = new List<string>();
            var open = new List<string>();
            var start = first;
            var size = 0;
            foreach (var line in lines)
            {
                var json = LineJson(line);
                var bytes = Encoding.UTF8.GetByteCount(json);
                var firstReset = reset && windows.Count == 0;
                var header = Encoding.UTF8.GetByteCount(Header(sessionId, start, total, live, firstReset)) + 2;   // + "]}"
                var trial = header + size + (open.Count > 0 ? 1 : 0) + bytes;
                if (open.Count > 0 && trial > MaxWindowBytes)
                {
                    windows.Add(Window(sessionId, start, total, live, firstReset, open));
                    start += open.Count;
                    open = [json];
                    size = bytes;
                }
                else
                {
                    size += (open.Count > 0 ? 1 : 0) + bytes;
                    open.Add(json);
                }
            }
            windows.Add(Window(sessionId, start, total, live, reset && windows.Count == 0, open));
            return windows;
        }

        // The last 200 lines, or all of them when there are fewer.
        internal static List<string> Backfill(string sessionId, IReadOnlyList<WireLine> all, bool reset = false)
        {
            var from = Math.Max(0, all.Count - BackfillLines);
            return Chunk(sessionId, from, all.Count, live: false, Slice(all, from, all.Count), reset);
        }

        // Up to 200 lines ending at line before-1; before beyond total is
        // clamped to it. The caller has already refused before < 1.
        internal static List<string> Page(string sessionId, IReadOnlyList<WireLine> all, long before)
        {
            var end = (int)Math.Min(before, all.Count);
            var from = Math.Max(0, end - BackfillLines);
            return Chunk(sessionId, from, all.Count, live: false, Slice(all, from, end));
        }

        internal static List<string> Tail(string sessionId, IReadOnlyList<WireLine> all, int fromIndex) =>
            Chunk(sessionId, fromIndex, all.Count, live: true, Slice(all, fromIndex, all.Count));

        private static List<WireLine> Slice(IReadOnlyList<WireLine> all, int from, int to)
        {
            var list = new List<WireLine>(Math.Max(0, to - from));
            for (var i = from; i < to; i++) list.Add(all[i]);
            return list;
        }

        // Restate: the last row sent changed in place (with or without rows
        // after it), so the tail starts at that row's own index.
        internal enum Change { None, Tail, Restate, Reset }

        // Rows are append-only (frames-v4 window.rules): a count that did not
        // grow sends nothing, one that grew sends the new rows, and one that
        // shrank — or a first row that is no longer the one sent — means the
        // file was truncated or replaced, which is a reset backfill.
        //
        // Two refinements from CB-316's QA round. Rows are compared by what the
        // phone draws, never by `at`: every parser falls back to "now" for a
        // row with no timestamp, so the same row parsed twice differs in time
        // and an Equals compare reset on every change. And the last row sent
        // is compared too: Grok's parser coalesces streamed chunks into one
        // turn, so that row grows in place without the count moving. That is
        // a Restate — a live tail from the row's own index, which the phone
        // replaces by index — not a reset: a reset is a 200-line backfill
        // every 250 ms while a turn streams, and drops everything the phone
        // holds (PM decision, final). Reset stays for a shrink or a changed
        // first row.
        internal static Change Diff(int heldCount, WireLine? heldFirst, IReadOnlyList<WireLine> now, WireLine? heldLast = null)
        {
            if (now.Count < heldCount) return Change.Reset;
            if (heldCount > 0 && heldFirst is { } first && !Same(first, now[0])) return Change.Reset;
            if (heldCount > 0 && heldLast is { } last && !Same(last, now[heldCount - 1])) return Change.Restate;
            return now.Count > heldCount ? Change.Tail : Change.None;
        }

        // The same row as far as the diff is concerned: role and text, exactly
        // frames-v4's window.diff_rule (Mobile #31). Not `at` (see Diff); and
        // cut follows from the text, so it needs no compare of its own.
        internal static bool Same(WireLine a, WireLine b) => a.Role == b.Role && a.Text == b.Text;

        // Where the window a change produces starts: the previous count, or
        // one before it for a restated last row. Null for none and reset.
        internal static int? Start(Change change, int heldCount) => change switch
        {
            Change.Tail => heldCount,
            Change.Restate => heldCount - 1,
            _ => null,
        };
    }
}
