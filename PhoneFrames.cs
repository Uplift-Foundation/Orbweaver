using System.Globalization;
using System.Text;

namespace Orbweaver
{
    // The sealed plaintext frames of the phone's live protocol (frames-v4.json,
    // CB-316): window, ok and err, written in the one canonical form the spec
    // pins byte for byte — keys in the file's order, no whitespace, integers
    // bare, strings through AnswerSigning.Escape (Utf8JsonWriter's relaxed
    // escaping still escapes + < > & ', which the phone's own writer does not).
    //
    // Pure: no socket, no clock, no settings. RelayLink seals what this writes.
    internal static class PhoneFrames
    {
        // The limits the spec sets (frames-v4.json limits).
        internal const int MaxWindowBytes = 48000;
        internal const int MaxLineBytes = 8000;
        internal const int BackfillLines = 200;

        internal readonly record struct Line(string Role, string Text, long At, bool Error = false, bool Cut = false);

        // A parsed row as a wire line: role, the text cut at the line cap, the
        // time in Unix seconds, and error/cut only when true.
        internal static Line FromTurn(ChatTurn turn)
        {
            var (text, cut) = CutText(turn.Text ?? "");
            var role = turn.Role switch { ChatRole.User => "user", ChatRole.Assistant => "assistant", _ => "system" };
            return new Line(role, text, turn.At.ToUnixTimeSeconds(), turn.IsError, cut);
        }

        // A line over 8,000 UTF-8 bytes is the longest prefix ending on a scalar
        // boundary of at most 7,997 bytes plus U+2026 (3 bytes): measured in
        // bytes rather than UTF-16 units because Swift and C# disagree on
        // exactly the text that matters.
        internal static (string Text, bool Cut) CutText(string text)
        {
            if (Encoding.UTF8.GetByteCount(text) <= MaxLineBytes) return (text, false);
            var budget = MaxLineBytes - 3;
            var used = 0;
            var end = 0;
            while (end < text.Length)
            {
                var size = char.IsHighSurrogate(text[end]) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
                var bytes = Encoding.UTF8.GetByteCount(text.AsSpan(end, size));
                if (used + bytes > budget) break;
                used += bytes;
                end += size;
            }
            return (text[..end] + "…", true);
        }

        private static string Int(long n) => n.ToString(CultureInfo.InvariantCulture);
        private static string Str(string s) => "\"" + AnswerSigning.Escape(s) + "\"";

        internal static string LineJson(Line l) =>
            "{\"role\":" + Str(l.Role) + ",\"text\":" + Str(l.Text) + ",\"at\":" + Int(l.At)
            + (l.Error ? ",\"error\":true" : "") + (l.Cut ? ",\"cut\":true" : "") + "}";

        private static string WindowHead(string sessionId, int fromLine, int total, bool live, bool reset) =>
            "{\"t\":\"window\",\"session_id\":" + Str(sessionId) + ",\"from_line\":" + Int(fromLine)
            + ",\"total\":" + Int(total) + ",\"live\":" + (live ? "true" : "false") + (reset ? ",\"reset\":true" : "") + ",\"lines\":[";

        internal static string Window(string sessionId, int fromLine, int total, bool live, bool reset, IReadOnlyList<Line> lines) =>
            WindowHead(sessionId, fromLine, total, live, reset) + string.Join(",", lines.Select(LineJson)) + "]}";

        // The lines in order, chunked: a line is added while the canonical
        // window stays within 48,000 UTF-8 bytes; when the next would exceed
        // it the window is sent and the next begins with that line, so every
        // window of a run but the last is maximal. A line always fits an empty
        // window (its text is capped). Nothing at all for no lines: a tail
        // window is sent only when a row was appended, and a transcript with
        // nothing in it has no backfill to send.
        internal static List<string> Windows(string sessionId, int firstLine, int total, bool live, bool reset, IReadOnlyList<Line> lines)
        {
            var windows = new List<string>();
            var i = 0;
            while (i < lines.Count)
            {
                var from = firstLine + i;
                var head = WindowHead(sessionId, from, total, live, reset);
                var size = Encoding.UTF8.GetByteCount(head) + 2;   // "]}"
                var json = new StringBuilder(head);
                var first = true;
                while (i < lines.Count)
                {
                    var piece = LineJson(lines[i]);
                    var add = Encoding.UTF8.GetByteCount(piece) + (first ? 0 : 1);
                    if (!first && size + add > MaxWindowBytes) break;
                    if (!first) json.Append(',');
                    json.Append(piece);
                    size += add;
                    first = false;
                    i++;
                }
                windows.Add(json.Append("]}").ToString());
            }
            return windows;
        }

        // ok: t, ref, code, session_id, total (watching only).
        internal static string Ok(string reference, string code, string sessionId, int? total = null) =>
            "{\"t\":\"ok\",\"ref\":" + Str(reference) + ",\"code\":" + Str(code) + ",\"session_id\":" + Str(sessionId)
            + (total is { } n ? ",\"total\":" + Int(n) : "") + "}";

        // err: t, ref, code, session_id (when the request named one), why
        // (not_at_prompt, typing_failed, no_terminal, launch_failed only).
        internal static string Err(string reference, string code, string? sessionId = null, string? why = null) =>
            "{\"t\":\"err\",\"ref\":" + Str(reference) + ",\"code\":" + Str(code)
            + (sessionId is null ? "" : ",\"session_id\":" + Str(sessionId))
            + (why is null ? "" : ",\"why\":" + Str(why)) + "}";
    }
}
