using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbweaver
{
    // What two copies of this app say to each other over a direct connection.
    //
    // This replaces MirrorProtocol's wire format, and the difference is worth
    // stating plainly because it is the whole reason for the change. That format
    // carried a transcript as 6KB chunks of base64, each with its own SHA-256
    // and a whole-payload digest on top, sized so that a *language model* could
    // retype it into a peer-messaging tool. Measured on two real machines: 222,
    // 231, 234, 247 and 192 seconds for a single chunk, and at least once the
    // model got a character wrong and the digest correctly refused the result.
    //
    // None of that is needed here. A TLS stream is ordered, framed and integrity
    // checked by construction, so what goes on the wire is the JSON itself:
    //
    //   * **No chunking.** A window is one message. A 500KB transcript is one
    //     read rather than a four-minute hand-copy.
    //   * **No base64.** JSON strings carry text; there is no model in the path
    //     to be confused by punctuation.
    //   * **No digests.** TLS already detects a mangled byte, and a mangled byte
    //     is a broken connection rather than a frame to re-request.
    //
    // What is kept is the *vocabulary* — hello, roster, fetch, window, watch,
    // delta, input, ok, err — because those decisions are sound and are what
    // RemoteMirrorServer already knows how to answer.
    internal static class PeerProtocol
    {
        // Bumped when a change would make an older peer misread a message rather
        // than merely miss a field. Both ends state it in `hello` and a mismatch
        // is refused there, once, instead of failing later in a way that reads
        // as a network fault.
        public const int Version = 1;

        // --- what a message can be ---------------------------------------------

        public const string Hello = "hello";
        public const string Roster = "roster";
        public const string Fetch = "fetch";
        public const string Window = "window";
        public const string Watch = "watch";
        public const string Unwatch = "unwatch";
        public const string Delta = "delta";
        public const string Input = "input";
        public const string Ok = "ok";
        public const string Err = "err";
        // Optional capability negotiation: an older Buddy ignores these two
        // messages, and the requester simply keeps its local/global fallback.
        // CB-322: the link's own keepalive, consumed by the read loop and never
        // delivered. A build without it hands it to the mirror, which finds no
        // body and drops it, so sending one to an older peer is harmless.
        public const string Ping = "ping";

        public const string OpenClawIdentityGet = "openclaw-identity-get";
        public const string OpenClawIdentity = "openclaw-identity";

        // Error codes, carried in `err`. The same set MirrorProtocol used, since
        // they describe the far machine's state rather than the transport: the
        // panel's wording for each is already written and tested.
        public const string ErrNoSession = "no-session";
        public const string ErrNoTranscript = "no-transcript";
        public const string ErrNoPane = "no-pane";
        public const string ErrReplyOff = "reply-off";
        public const string ErrUnsupported = "unsupported";
        public const string ErrVersion = "version";

        // The greeting was refused: not paired, and no pairing window open with
        // a matching code. Distinct from the others because it is the only one
        // that means "and the connection is now closed" — see PeerLink.Judge.
        public const string ErrUntrusted = "untrusted";

        // --- the message --------------------------------------------------------

        // One message, with its payload left as raw JSON.
        //
        // `Body` is a JsonElement rather than a typed field because the four
        // things that carry one — a roster, a window, a delta, a line of input —
        // have nothing in common, and a base class with four nullable
        // properties would be a worse description of that than an element the
        // reader interprets by Type.
        //
        // Short property names because this is the only place they are read and
        // a transcript window repeats them once per turn.
        internal sealed record PeerMessage(
            [property: JsonPropertyName("v")] int Version,
            [property: JsonPropertyName("t")] string Type,
            [property: JsonPropertyName("id")] string Id,
            [property: JsonPropertyName("n")] string? Name = null,
            [property: JsonPropertyName("code")] string? Code = null,
            [property: JsonPropertyName("body")] JsonElement? Body = null);

        // Reflection-based on purpose, matching the rest of this app: there is no
        // source-generated context anywhere here, and introducing one for a
        // single record would be a convention nothing else follows.
        internal static readonly JsonSerializerOptions Json = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // --- framing -------------------------------------------------------------

        // A four-byte big-endian length, then that many bytes of UTF-8 JSON.
        //
        // Length-prefixed rather than newline-delimited, which is what the old
        // format used because a relay pane could only carry lines. A transcript
        // is full of newlines, so a line-delimited format has to escape them and
        // a reader has to scan for a terminator it might be halfway through. A
        // length says exactly how much to read and needs no escaping at all.
        public const int HeaderBytes = 4;

        // A ceiling, so a peer that is confused or hostile cannot ask this
        // process to allocate arbitrarily. Generous against the real case: the
        // largest window measured on the mini was 524KB of transcript, and the
        // whole file it came from was 6MB.
        public const int MaxMessageBytes = 32 * 1024 * 1024;

        internal static byte[] Encode(PeerMessage message)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(message, Json);

            if (json.Length > MaxMessageBytes)
                throw new InvalidOperationException(
                    $"peer message of {json.Length} bytes exceeds the {MaxMessageBytes} ceiling");

            var buffer = new byte[HeaderBytes + json.Length];
            BinaryPrimitives.WriteInt32BigEndian(buffer, json.Length);
            json.CopyTo(buffer, HeaderBytes);

            return buffer;
        }

        internal static async Task WriteAsync(
            Stream stream, PeerMessage message, CancellationToken ct = default)
        {
            var bytes = Encode(message);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        // Null at a clean end of stream — the peer hung up between messages,
        // which is an ordinary disconnect and not an error to report.
        //
        // Throws on a *dirty* end: a header that stops halfway, or a body
        // shorter than its own length. Those say the connection died mid-message
        // and the caller must not treat what it has as a message.
        internal static async Task<PeerMessage?> ReadAsync(
            Stream stream, CancellationToken ct = default)
        {
            var header = new byte[HeaderBytes];

            var got = await ReadExactlyOrEndAsync(stream, header, ct).ConfigureAwait(false);
            if (got == 0) return null;

            if (got < HeaderBytes)
                throw new InvalidDataException("peer connection ended inside a message header");

            var length = BinaryPrimitives.ReadInt32BigEndian(header);

            // Both arms matter. A negative length is a garbled or hostile peer;
            // an oversized one would otherwise be honoured as an allocation.
            if (length < 0 || length > MaxMessageBytes)
                throw new InvalidDataException($"peer announced a message of {length} bytes");

            var body = new byte[length];

            if (await ReadExactlyOrEndAsync(stream, body, ct).ConfigureAwait(false) < length)
                throw new InvalidDataException("peer connection ended inside a message body");

            return JsonSerializer.Deserialize<PeerMessage>(body, Json);
        }

        // How many bytes were actually read, stopping at the end of the stream
        // rather than throwing.
        //
        // Written out rather than using Stream.ReadExactlyAsync because the
        // difference between "nothing at all" and "some of it" is the difference
        // between a clean hangup and a broken one, and ReadExactly cannot say
        // which it saw.
        private static async Task<int> ReadExactlyOrEndAsync(
            Stream stream, byte[] into, CancellationToken ct)
        {
            var filled = 0;

            while (filled < into.Length)
            {
                var read = await stream
                    .ReadAsync(into.AsMemory(filled), ct)
                    .ConfigureAwait(false);

                if (read == 0) return filled;

                filled += read;
            }

            return filled;
        }

        // --- convenience ---------------------------------------------------------

        // Ids are correlation only: a reply carries the id of what it answers.
        // Short because they are never shown and never persisted, and unique
        // enough for the handful of requests one connection has open at once.
        internal static string NewId() => Guid.NewGuid().ToString("N")[..8];

        internal static PeerMessage Message(
            string type, string id, string? name = null, string? code = null, JsonElement? body = null) =>
            new(Version, type, id, name, code, body);

        internal static JsonElement BodyOf<T>(T value) =>
            JsonSerializer.SerializeToElement(value, Json);
    }
}
