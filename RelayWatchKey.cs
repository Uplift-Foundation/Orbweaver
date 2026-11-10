using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orbweaver
{
    // CB-323 watch-key addendum (Orbweaver-Mobile contracts/frames-v7-watch-key.json):
    // the phone's second key, which signs only answers that came over
    // WatchConnectivity from the paired Watch. It is published to this desktop
    // by a sealed `key.add` signed under the pair's approval key (id_pub) and
    // withdrawn by a sealed `key.remove` signed under the watch key itself.
    //
    // Pure, like AnswerGate: the frame, the envelope's sender, the pair's
    // id_pub and what is stored now go in; the decision and what to store come
    // out. RelayLink does the opening, the persisting and the reply. The rules
    // run in the contract's order and the first failure decides, so a vector
    // that breaks one thing hits one code.
    internal sealed record WatchKeyState(byte[]? Public, long? At)
    {
        internal static readonly WatchKeyState None = new(null, null);

        internal bool Same(WatchKeyState other) =>
            At == other.At && (Public is null ? other.Public is null : other.Public is not null && Public.AsSpan().SequenceEqual(other.Public));
    }

    // Op and At echo the frame ("" and 0 where a bad_frame could not read
    // them); Code is null when accepted; After is the state to store, equal to
    // the state before for anything but an accepted change.
    internal sealed record WatchKeyDecision(string Op, long At, string? Code, WatchKeyState After)
    {
        internal bool Accepted => Code is null;

        // key.ok / key.err (frames-v7-watch-key frames), canonical.
        internal string Reply()
        {
            var j = new CanonicalJson().Str("t", Accepted ? "key.ok" : "key.err").Str("op", Op).Str("kind", RelayWatchKey.Kind).Num("at", At);
            if (Code is not null) j.Str("code", Code);
            return j.ToString();
        }
    }

    internal static class RelayWatchKey
    {
        internal const string Domain = "orbweaver-key-v1";
        internal const string Kind = "watch";
        internal const string Add = "add", Remove = "remove";
        internal const string AddType = "key.add", RemoveType = "key.remove";
        internal const string BadFrame = "bad_frame", BadKey = "bad_key", BadSignature = "bad_signature", Stale = "stale";

        private static readonly string[] FrameKeys = ["t", "kind", "pub", "at", "sig"];
        private static readonly Regex PubShape = new("^04[0-9a-f]{128}$", RegexOptions.CultureInvariant);

        internal static bool IsKeyFrame(string? t) => t is AddType or RemoveType;

        // The signed body: exactly these keys in this order, strings through
        // the shared escaper, at a bare integer.
        internal static string Body(string op, string kind, string pub, string phoneId, long at) =>
            $"{{\"op\":\"{AnswerSigning.Escape(op)}\",\"kind\":\"{AnswerSigning.Escape(kind)}\",\"pub\":\"{AnswerSigning.Escape(pub)}\"," +
            $"\"phone_id\":\"{AnswerSigning.Escape(phoneId)}\",\"at\":{at.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";

        internal static byte[] Message(string body) => [.. Encoding.UTF8.GetBytes(Domain), .. Encoding.UTF8.GetBytes(body)];

        // phoneId is the envelope's `from`: the body is rebuilt with it, so a
        // frame signed for another phone cannot verify here.
        internal static WatchKeyDecision Decide(JsonElement frame, string phoneId, byte[] idPublic, WatchKeyState state)
        {
            var op = Str(frame, "t") == RemoveType ? Remove : Add;

            // bad_frame: exactly the five keys, and at an integer.
            var keys = frame.EnumerateObject().Select(p => p.Name).ToList();
            var hasAt = frame.TryGetProperty("at", out var atEl) && atEl.ValueKind == JsonValueKind.Number && atEl.TryGetInt64(out _);
            var at = hasAt ? atEl.GetInt64() : 0;
            if (keys.Count != FrameKeys.Length || !FrameKeys.All(keys.Contains) || !hasAt)
                return new(op, at, BadFrame, state);

            WatchKeyDecision Refuse(string code) => new(op, at, code, state);

            // bad_key: the kind, and the point.
            if (Str(frame, "kind") != Kind) return Refuse(BadKey);
            if (Str(frame, "pub") is not { } pubHex || !PubShape.IsMatch(pubHex)) return Refuse(BadKey);
            var pub = RelayCrypto.FromHex(pubHex)!;
            var sig = RelayCrypto.FromBase64Url(Str(frame, "sig"));
            var message = Message(Body(op, Kind, pubHex, phoneId, at));

            if (op == Add)
            {
                if (!RelayCrypto.IsValidPublicKey(pub) || pub.AsSpan().SequenceEqual(idPublic)) return Refuse(BadKey);
                if (!RelayCrypto.Verify(idPublic, message, sig)) return Refuse(BadSignature);
                var added = new WatchKeyState(pub, at);
                // The add already applied, sent again by a phone that missed
                // the reply: confirmed, nothing changes.
                if (added.Same(state)) return new(op, at, null, state);
                if (state.At is { } stored && at <= stored) return Refuse(Stale);
                return new(op, at, null, added);
            }

            // remove
            if (state.Public is null)
                return state.At == at ? new(op, at, null, state) : Refuse(BadKey);
            if (!pub.AsSpan().SequenceEqual(state.Public)) return Refuse(BadKey);
            if (!RelayCrypto.Verify(state.Public, message, sig)) return Refuse(BadSignature);
            if (state.At is { } before && at <= before) return Refuse(Stale);
            return new(op, at, null, new WatchKeyState(null, at));
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
