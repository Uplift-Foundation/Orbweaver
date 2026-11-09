using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Orbweaver
{
    // One canonical JSON object, written the way frames-v4.json's `canonical`
    // rule says every v4 plaintext is written (CB-316): keys in the order the
    // caller adds them, no whitespace, integers bare, strings through the
    // hand-written escaper the answer gate already rebuilds signed bodies with
    // (AnswerSigning.Escape). Utf8JsonWriter is not enough even with relaxed
    // escaping — it escapes + < > & ' and characters outside the BMP — and a
    // chunk's size, a launch body's signature and the vectors' bytes all
    // depend on both apps producing exactly the same bytes.
    internal sealed class CanonicalJson
    {
        private readonly StringBuilder _sb = new("{");
        private bool _any;

        internal static string Quote(string value) => "\"" + AnswerSigning.Escape(value) + "\"";

        internal CanonicalJson Str(string key, string value) => Raw(key, Quote(value));

        internal CanonicalJson Num(string key, long value) => Raw(key, value.ToString(CultureInfo.InvariantCulture));

        // A key that is present only when true (window's reset, a line's
        // error and cut): absent rather than false, per the contract.
        internal CanonicalJson Flag(string key, bool value) => value ? Raw(key, "true") : this;

        // An array of values that are already canonical JSON.
        internal CanonicalJson Array(string key, IEnumerable<string> items) => Raw(key, "[" + string.Join(",", items) + "]");

        internal CanonicalJson Raw(string key, string json)
        {
            if (_any) _sb.Append(',');
            _sb.Append(Quote(key)).Append(':').Append(json);
            _any = true;
            return this;
        }

        public override string ToString() => _sb.ToString() + "}";

        internal byte[] Utf8() => Encoding.UTF8.GetBytes(ToString());
    }

    // The v4 replies (frames-v4 sealed_plaintext ok / err). A v4 refusal carries
    // `ref` and never `request_id`, which is how a phone tells it from v3's six
    // answer refusals (PromptFrames.Refusal) without a version field.
    internal static class RelayReplies
    {
        internal const string Watching = "watching", Unwatched = "unwatched", Typed = "typed", Launched = "launched";

        internal const string BadFrame = "bad_frame", UnknownSession = "unknown_session", NotAtPrompt = "not_at_prompt",
            Untypeable = "untypeable", TypingFailed = "typing_failed", BadInput = "bad_input", BadSignature = "bad_signature",
            Expired = "expired", Replayed = "replayed", UnknownCli = "unknown_cli", UnknownCwd = "unknown_cwd",
            BadModel = "bad_model", UnsupportedPersona = "unsupported_persona", UnknownParent = "unknown_parent",
            UnsupportedParent = "unsupported_parent", NoTerminal = "no_terminal", LaunchFailed = "launch_failed",
            NoSession = "no_session";

        internal static readonly string[] OkCodes = [Watching, Unwatched, Typed, Launched];

        internal static readonly string[] ErrCodes =
        [
            BadFrame, UnknownSession, NotAtPrompt, Untypeable, TypingFailed, BadInput, BadSignature, Expired, Replayed,
            UnknownCli, UnknownCwd, BadModel, UnsupportedPersona, UnknownParent, UnsupportedParent, NoTerminal,
            LaunchFailed, NoSession,
        ];

        // Only these four carry the desktop's own sentence; every other code's
        // copy is the phone's (frames-v4 err.rules).
        internal static readonly string[] CodesWithWhy = [NotAtPrompt, TypingFailed, NoTerminal, LaunchFailed];

        internal static string Ok(string reference, string code, string sessionId, long? total = null)
        {
            var j = new CanonicalJson().Str("t", "ok").Str("ref", reference).Str("code", code).Str("session_id", sessionId);
            if (total is { } n) j.Num("total", n);
            return j.ToString();
        }

        internal static string Err(string reference, string code, string? sessionId = null, string? why = null)
        {
            var j = new CanonicalJson().Str("t", "err").Str("ref", reference).Str("code", code);
            if (sessionId is not null) j.Str("session_id", sessionId);
            if (why is not null && Array.IndexOf(CodesWithWhy, code) >= 0) j.Str("why", why);
            return j.ToString();
        }
    }

    // A phone's v4 request, read out of an action envelope that opened under
    // the pair's key. `Ref` is the envelope id, echoed on every reply.
    internal abstract record RelayRequest(string Ref);

    internal sealed record WatchRequest(string Ref, string SessionId, long? Before) : RelayRequest(Ref);

    internal sealed record UnwatchRequest(string Ref, string SessionId) : RelayRequest(Ref);

    internal sealed record InputRequest(string Ref, string SessionId, string Text) : RelayRequest(Ref);

    internal sealed record LaunchRequest(string Ref, LaunchFields Fields, string Sig) : RelayRequest(Ref);

    // A v4 type whose shape is wrong: answered `err bad_frame`, and nothing
    // else happens. SessionId when the frame named one, so the reply can too.
    internal sealed record BadFrameRequest(string Ref, string? SessionId) : RelayRequest(Ref);

    internal sealed record LaunchFields(
        string Cwd, string Cli, string Persona, string Model, string ParentSessionId,
        string FirstMessage, string Nonce, long ExpiresAt);

    internal static class RelayRequests
    {
        internal static readonly string[] Types = ["watch", "unwatch", "input", "launch"];

        // Null when the plaintext is not a v4 request at all (an answer, or a
        // type this desktop does not know) — those keep their existing paths.
        internal static RelayRequest? Parse(JsonElement f, string reference)
        {
            if (Str(f, "t") is not { } t || Array.IndexOf(Types, t) < 0) return null;
            var sid = Str(f, "session_id");
            switch (t)
            {
                case "watch":
                    if (sid is null) return new BadFrameRequest(reference, null);
                    if (!f.TryGetProperty("before", out var b)) return new WatchRequest(reference, sid, null);
                    // `before` is a line number: an integer >= 1. 0 asks for
                    // the page ending at line -1, which is nothing — bad_frame.
                    return b.ValueKind == JsonValueKind.Number && b.TryGetInt64(out var before) && before >= 1
                        ? new WatchRequest(reference, sid, before)
                        : new BadFrameRequest(reference, sid);
                case "unwatch":
                    return sid is null ? new BadFrameRequest(reference, null) : new UnwatchRequest(reference, sid);
                case "input":
                    return sid is null || Str(f, "text") is not { } text
                        ? new BadFrameRequest(reference, sid)
                        : new InputRequest(reference, sid, text);
                default:
                    return ParseLaunch(f, reference);
            }
        }

        private static RelayRequest ParseLaunch(JsonElement f, string reference)
        {
            // parent_session_id may be absent; when present it must be a string.
            var parent = "";
            if (f.TryGetProperty("parent_session_id", out var p))
            {
                if (p.ValueKind != JsonValueKind.String) return new BadFrameRequest(reference, null);
                parent = p.GetString()!;
            }
            if (Str(f, "cwd") is not { } cwd || Str(f, "cli") is not { } cli || Str(f, "persona") is not { } persona
                || Str(f, "model") is not { } model || Str(f, "first_message") is not { } message
                || Str(f, "nonce") is not { } nonce || Str(f, "sig") is not { } sig
                || !f.TryGetProperty("expires_at", out var e) || e.ValueKind != JsonValueKind.Number || !e.TryGetInt64(out var expires))
                return new BadFrameRequest(reference, null);
            return new LaunchRequest(reference, new LaunchFields(cwd, cli, persona, model, parent, message, nonce, expires), sig);
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    // input.text and launch.first_message (frames-v4 input.rules): 1 to 8,000
    // UTF-8 bytes (launch allows 0), newline and tab allowed, every other C0
    // control and DEL refused — an escape byte would drive the TUI. The C1
    // range U+0080-U+009F is refused too (QA, CB-316): U+009B is a one-byte
    // CSI to a terminal that honours 8-bit controls, and nobody types one into
    // a chat. Stricter than frames-v4 as written; raised for a contract
    // amendment so both apps refuse the same set.
    internal static class RelayText
    {
        internal const int MaxBytes = 8000;

        internal static bool Typeable(string text, bool allowEmpty = false)
        {
            if (text.Length == 0) return allowEmpty;
            if (Encoding.UTF8.GetByteCount(text) > MaxBytes) return false;
            foreach (var c in text)
                if ((c < 0x20 && c != '\n' && c != '\t') || (c >= 0x7F && c <= 0x9F)) return false;
            return true;
        }
    }
}
