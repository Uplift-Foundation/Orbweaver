using System.Text;
using System.Text.Json;

namespace Orbweaver
{
    // The desktop's half of the phone's live screens (CB-316, frames-v4.json):
    // `watch` and `unwatch` (a session's parsed transcript, backfilled and then
    // tailed), and `input` (a line typed into the session through the guarded
    // route). Pure of sockets, clocks and real terminals: everything the world
    // supplies comes in through Seams, and everything it says goes out through
    // Send, so the suite can drive every refusal.
    //
    // Not thread-safe by itself; RelayClient calls it from one place at a time.
    internal sealed class PhoneService
    {
        // One local CLI session this desktop knows, as the roster sees it.
        internal sealed record Session(string Id, SessionStatus Status, string State);

        internal sealed record Seams(
            // The local CLI session with this id, or null (not in the book, a
            // cloud session, an OpenClaw conversation, a peer's orb).
            Func<string, Session?> Find,
            // The session's parsed transcript, oldest first, or null when no
            // transcript path is known yet.
            Func<Session, IReadOnlyList<PhoneFrames.Line>?> Load,
            // Whether text can be typed into this session's terminal from a
            // phone: a silent route, not Terminal.app, not desk-hosted.
            Func<SessionStatus, bool> CanType,
            // The guarded typer: TypingGate first, then the keys. Typed, or the
            // gate's own sentence, or neither when the typer returned false.
            Func<Session, string, Task<(bool Typed, string? Refusal)>> Type,
            // One sealed plaintext to one phone: class, the session its ck
            // names (null for none), the plaintext. False when not sent.
            Func<string, string, string?, string, bool> Send,
            Action<string> Log);

        private readonly Seams _s;
        private readonly Dictionary<(string Phone, string Session), List<PhoneFrames.Line>> _watches = new();

        internal PhoneService(Seams seams) { _s = seams; }

        internal int WatchCount => _watches.Count;

        // ---- what the phone asks ---------------------------------------------

        internal Task OnRequestAsync(string phoneId, string requestId, JsonElement frame)
        {
            switch (Str(frame, "t"))
            {
                case "watch": Watch(phoneId, requestId, frame); break;
                case "unwatch": Unwatch(phoneId, requestId, frame); break;
                case "input": return InputAsync(phoneId, requestId, frame);
            }
            return Task.CompletedTask;
        }

        private void Watch(string phone, string reference, JsonElement f)
        {
            if (Str(f, "session_id") is not { Length: > 0 } sid) { BadFrame(phone, reference); return; }
            int? before = null;
            if (f.TryGetProperty("before", out var b))
            {
                if (b.ValueKind != JsonValueKind.Number || !b.TryGetInt32(out var n) || n < 1) { BadFrame(phone, reference, sid); return; }
                before = n;
            }
            if (_s.Find(sid) is not { } session) { Err(phone, reference, "unknown_session", sid); return; }

            var rows = _s.Load(session) ?? [];
            if (before is { } until)
            {
                // A page: up to 200 lines ending at line before-1, no change to
                // the subscription. Clamped to the transcript's length.
                var end = Math.Min(until, rows.Count);
                var start = Math.Max(0, end - PhoneFrames.BackfillLines);
                SendWindows(phone, sid, start, rows.Count, live: false, reset: false, rows.Skip(start).Take(end - start).ToList());
                return;
            }

            _watches[(phone, sid)] = [.. rows];
            Ok(phone, reference, "watching", sid, rows.Count);
            Backfill(phone, sid, rows, reset: false);
        }

        private void Unwatch(string phone, string reference, JsonElement f)
        {
            if (Str(f, "session_id") is not { Length: > 0 } sid) { BadFrame(phone, reference); return; }
            if (_s.Find(sid) is null) { Err(phone, reference, "unknown_session", sid); return; }
            _watches.Remove((phone, sid));
            Ok(phone, reference, "unwatched", sid);
        }

        private async Task InputAsync(string phone, string reference, JsonElement f)
        {
            if (Str(f, "session_id") is not { Length: > 0 } sid || Str(f, "text") is not { } text) { BadFrame(phone, reference); return; }
            if (!ValidTypedText(text, allowEmpty: false)) { Err(phone, reference, "bad_input", sid); return; }
            if (_s.Find(sid) is not { } session) { Err(phone, reference, "unknown_session", sid); return; }

            // Answering a prompt is `answer`, never a typed line.
            if (session.State == RelayRoster.Waiting) { Err(phone, reference, "not_at_prompt", sid, TypingGate.WaitingWhy); return; }
            if (!_s.CanType(session.Status)) { Err(phone, reference, "untypeable", sid); return; }

            (bool Typed, string? Refusal) result;
            try { result = await _s.Type(session, text).ConfigureAwait(false); }
            catch (Exception e)
            {
                _s.Log($"input {sid}: typer threw {e.GetType().Name}");
                result = (false, null);
            }

            if (result.Refusal is { } why) { Err(phone, reference, "not_at_prompt", sid, why); return; }
            if (!result.Typed) { Err(phone, reference, "typing_failed", sid, "the terminal refused the text"); return; }
            Ok(phone, reference, "typed", sid);
        }

        // ---- the tail ---------------------------------------------------------

        // Called on a debounce (250 ms) while anything is watched: re-parse each
        // watched session and send what the diff says. A session that has left
        // the book ends its watch silently — the roster removal is the signal.
        internal void Tick()
        {
            foreach (var key in _watches.Keys.ToList())
            {
                if (_s.Find(key.Session) is not { } session) { _watches.Remove(key); continue; }
                var rows = _s.Load(session) ?? [];
                var prev = _watches[key];
                switch (Diff(prev, rows))
                {
                    case { Kind: DiffKind.None }: break;
                    case { Kind: DiffKind.Reset }:
                        _watches[key] = [.. rows];
                        Backfill(key.Phone, key.Session, rows, reset: true);
                        break;
                    case { Kind: DiffKind.Tail, Start: var start }:
                        _watches[key] = [.. rows];
                        SendWindows(key.Phone, key.Session, start, rows.Count, live: true, reset: false, rows.Skip(start).ToList());
                        break;
                }
            }
        }

        // A phone gone offline, or removed, has no use for windows: the relay's
        // 1 MB queue must never fill with frames nobody is reading.
        internal void PhoneOffline(string phoneId)
        {
            foreach (var key in _watches.Keys.Where(k => k.Phone == phoneId).ToList()) _watches.Remove(key);
        }

        internal enum DiffKind { None, Reset, Tail }

        internal readonly record struct DiffResult(DiffKind Kind, int Start);

        // The spec's diff rule, rows compared by role and text and never by at
        // (a parser stamps an undated row with now, so at differs between two
        // parses of one row). A shrunk count or a changed first row is a reset;
        // otherwise the tail starts at the previous count, or one earlier when
        // the previous last row changed in place (Grok streams into its last
        // row); no rows from there on means nothing is sent.
        internal static DiffResult Diff(IReadOnlyList<PhoneFrames.Line> prev, IReadOnlyList<PhoneFrames.Line> next)
        {
            if (next.Count < prev.Count || (prev.Count > 0 && !Same(prev[0], next[0])))
                return new(DiffKind.Reset, 0);
            var start = prev.Count;
            if (prev.Count > 0 && !Same(prev[^1], next[prev.Count - 1])) start = prev.Count - 1;
            return start < next.Count ? new(DiffKind.Tail, start) : new(DiffKind.None, 0);
        }

        private static bool Same(PhoneFrames.Line a, PhoneFrames.Line b) => a.Role == b.Role && a.Text == b.Text;

        // ---- sending ----------------------------------------------------------

        private void Backfill(string phone, string sid, IReadOnlyList<PhoneFrames.Line> rows, bool reset)
        {
            var start = Math.Max(0, rows.Count - PhoneFrames.BackfillLines);
            // A reset restates the transcript even when it is empty: the phone
            // must drop what it holds. A plain backfill of nothing sends nothing.
            if (reset && rows.Count == 0)
            {
                _s.Send(phone, "chat", sid, PhoneFrames.Window(sid, 0, 0, live: false, reset: true, []));
                return;
            }
            SendWindows(phone, sid, start, rows.Count, live: false, reset, rows.Skip(start).ToList());
        }

        private void SendWindows(string phone, string sid, int from, int total, bool live, bool reset, IReadOnlyList<PhoneFrames.Line> lines)
        {
            foreach (var window in PhoneFrames.Windows(sid, from, total, live, reset, lines))
                _s.Send(phone, "chat", sid, window);
        }

        private void Ok(string phone, string reference, string code, string sid, int? total = null) =>
            _s.Send(phone, "action", sid, PhoneFrames.Ok(reference, code, sid, total));

        private void Err(string phone, string reference, string code, string? sid = null, string? why = null) =>
            _s.Send(phone, "action", sid, PhoneFrames.Err(reference, code, sid, why));

        private void BadFrame(string phone, string reference, string? sid = null) => Err(phone, reference, "bad_frame", sid);

        // ---- the text rule shared by input and launch --------------------------

        internal const int MaxTextBytes = 8000;

        // 1 to 8,000 UTF-8 bytes (0 allowed for a launch's first message); LF
        // and TAB are typed, every other C0 control, DEL and every C1 control
        // is refused (U+009B is an 8-bit CSI, so a C1 byte drives the TUI
        // exactly as ESC would).
        internal static bool ValidTypedText(string text, bool allowEmpty)
        {
            if (text.Length == 0) return allowEmpty;
            if (Encoding.UTF8.GetByteCount(text) > MaxTextBytes) return false;
            foreach (var c in text)
            {
                if (c is '\n' or '\t') continue;
                if (c < 0x20 || (c >= 0x7F && c <= 0x9F)) return false;
            }
            return true;
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
