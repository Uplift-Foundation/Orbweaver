using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // A cloud session's live event stream:
    // `GET /v1/code/sessions/{id}/events/stream`, server-sent events.
    //
    // **Measured on a real Mac on 2026-09-29** against a throwaway
    // `anthropic_cloud` session with the CLI's own login; the contract is in
    // CB-199's stream notes. What it gives the panel that polling cannot is the
    // text as it is written — `text_delta`s about every 0.2 s — plus a turn's
    // start and end as events rather than as a status somebody has to go and
    // read.
    //
    // Four SSE event names were seen, and each maps to one Kind:
    //
    //  * `client_event` — **durable**. Carries an SSE `id` equal to the event's
    //    `sequence_num`, and a payload shaped like a Claude Code transcript row
    //    (the same shape the /v2 feed and ChatTranscript already read).
    //  * `ephemeral_event` — no id, never replayed. The live deltas, and the
    //    post-turn summary.
    //  * `delivery_update` — no id. RECEIVED / PROCESSING / PROCESSED for a
    //    message we sent.
    //  * `session_update` — no id. Seen at connect, carrying connection_status.
    //
    // A `:` comment arrives every 12–15 s as a keepalive.
    internal enum CloudStreamEventKind
    {
        Durable,
        Ephemeral,
        Delivery,
        Session,
        Keepalive,

        // An SSE event name this version has not seen. Delivered rather than
        // dropped, so a new kind shows up in a trace instead of vanishing; the
        // panel ignores it.
        Other,

        // **The stream is over**, and Outcome says why. Always the last item an
        // OpenAsync enumeration yields, and yielded exactly once:
        //
        //  * a clean end of stream is `Ok` with the status that opened it;
        //  * a non-2xx answer is whatever CloudOutcomes.OutcomeFor makes of it
        //    — so a deleted session is SessionGone, a refused login
        //    TokenRefused, a 429 RateLimited with its Retry-After;
        //  * a transport failure mid-stream or before the headers is
        //    `Unavailable` with status 0.
        //
        // An enumeration that stopped because the caller cancelled yields
        // nothing further: cancelling is the caller's own decision and needs no
        // report.
        Ended,
    }

    // One event off the stream.
    //
    // PayloadJson is the raw `data:` JSON exactly as it arrived, so the panel
    // can hand a durable row to the transcript mapping without anything here
    // pretending to understand it. The fields after it are **hints** read out
    // of that JSON by ClaudeCloudStreamEvents.Classify — each is null when the
    // event does not carry it, and none is a promise that it will:
    //
    //  * PayloadType — the payload's `type`: user, assistant, system, result,
    //    control_request, control_response, stream_event, …
    //  * Subtype — the payload's `subtype`: init, status, success,
    //    error_during_execution, post_turn_summary, …
    //  * StatusValue — a `system subtype=status` event's `status`
    //    ("requesting"), or a session_update's `connection_status`.
    //  * TextDelta — the text of a `stream_event` whose inner event is a
    //    `content_block_delta` with a `text_delta`. Only text: a thinking or
    //    tool-input delta is not text the panel shows.
    //  * InnerType — a `stream_event`'s inner `event.type`: message_start,
    //    content_block_start, content_block_delta, content_block_stop,
    //    message_delta, message_stop.
    //  * DeliveryStatus — a delivery_update's `DELIVERY_STATUS_*`.
    //  * Uuid — the payload's `uuid`, which is how the echo of our own user
    //    turn reconciles with the bubble added after the send's 2xx. On a
    //    delivery_update it is that event's `event_id`, presumed (not
    //    confirmed) to be the uuid we sent.
    //  * RowJson — for a durable event only, the raw JSON of its `payload`
    //    object: the transcript row itself, ready for ChatTranscript without
    //    the consumer unwrapping and re-serialising it. **Not every durable
    //    row is a turn.** Captured alongside user/assistant/result:
    //    env_manager_log, turn_handoff_available, active_goal,
    //    autocompact_state, rate_limit_event, prompt_suggestion, and system
    //    hook_started/hook_response — all durable, all with sequence numbers,
    //    none of them chat. The mapping decides which to show.
    //
    // **No field ever holds the access token.** The token goes into the
    // request's Authorization header and nowhere else; the canary tests hold
    // every field here to that.
    internal sealed record CloudStreamEvent(
        CloudStreamEventKind Kind,
        string? Name,
        long? SequenceNum,
        string? PayloadJson,
        string? PayloadType = null,
        string? Subtype = null,
        string? StatusValue = null,
        string? TextDelta = null,
        string? InnerType = null,
        string? DeliveryStatus = null,
        string? Uuid = null,
        CloudOutcome? Outcome = null,
        string? RowJson = null);

    // The stream, as an interface, so the chat session can be driven by a fake
    // that yields a scripted turn. Same argument as ICloudApi.
    //
    // **One call opens one connection.** The enumeration ends with exactly one
    // `Ended` event (see the Kind) and the connection is closed when the
    // enumeration is disposed or `ct` is cancelled. Reconnecting is the
    // caller's job, decided by ClaudeCloudStreamPolicy — the stream itself
    // never retries, for the reason CB-164 gave about retry loops against an
    // endpoint we are a guest on.
    //
    // `fromSequenceNum` is the last durable sequence number the caller has.
    // **Always pass one when you have one.** Measured: with it, the stream
    // starts live with no replay; without it, the server replays the whole
    // session history first (129 events in 0.4 s on a short session).
    internal interface ICloudEventStream
    {
        IAsyncEnumerable<CloudStreamEvent> OpenAsync(string accessToken, string sessionId,
            long? fromSequenceNum, CancellationToken ct);
    }

    // --- SSE framing -------------------------------------------------------------

    // One dispatched SSE event, or one comment line, before anything has looked
    // at what the data means.
    internal sealed record SseFrame(string? EventName, string? Id, string? Data, bool IsComment);

    // Lines in, frames out: the text/event-stream framing, per the WHATWG rules
    // the endpoint follows, with one deliberate departure (the id, below).
    //
    // Pure and stateful in the smallest way — a buffer for the event being
    // assembled — so every framing rule is a unit test over a list of lines.
    //
    // The rules, and the argument for each where there is one:
    //
    //  * `field: value`, with **one** optional space after the colon removed.
    //    A line with no colon is a field name with an empty value.
    //  * `data` lines accumulate, joined by `\n`, so a multi-line payload
    //    survives intact.
    //  * A blank line dispatches — but only if some `data` arrived. An event
    //    with no data is not dispatched, per the spec, and nothing measured
    //    sends one.
    //  * A line starting `:` is a comment. **Measured as the keepalive**, every
    //    12–15 s, so it is surfaced as its own frame rather than swallowed: a
    //    consumer can tell a quiet stream from a dead one by it.
    //  * `retry` and unknown fields are ignored. The server's retry hint is not
    //    obeyed: reconnect timing is ClaudeCloudStreamPolicy's decision, and a
    //    server-chosen interval is the kind of number that becomes a retry storm.
    //  * A trailing `\r` is stripped. A reader that splits on `\n` alone would
    //    otherwise hand CRLF lines over with it attached, and `data: {..}\r` is
    //    not JSON.
    //
    // **The departure: the id does not persist from one event to the next.** The
    // spec keeps a "last event id" that later events inherit. Here only
    // `client_event`s carry an id — it is the durable sequence number — and an
    // ephemeral event after one must not appear to carry that event's sequence
    // number, which is exactly what inheriting it would do. The *resume* point,
    // which is what the spec's persistence is for, is tracked by the caller from
    // durable events alone (ClaudeCloudStreamPolicy.ResumeFrom).
    internal sealed class SseParser
    {
        private readonly StringBuilder _data = new();
        private bool _hasData;
        private string? _event;
        private string? _id;

        // One line, without its terminator. Returns a frame when this line
        // completes one, and null otherwise.
        internal SseFrame? Feed(string line)
        {
            if (line.EndsWith('\r')) line = line[..^1];

            if (line.Length == 0) return Dispatch();

            if (line[0] == ':')
            {
                var text = line.Length > 1 && line[1] == ' ' ? line[2..] : line[1..];
                return new SseFrame(null, null, text, true);
            }

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];

            switch (field)
            {
                case "event":
                    _event = value;
                    break;
                case "data":
                    if (_hasData) _data.Append('\n');
                    _data.Append(value);
                    _hasData = true;
                    break;
                case "id":
                    // The spec ignores an id containing NUL; so does this.
                    if (!value.Contains('\0')) _id = value;
                    break;
            }

            return null;
        }

        // The stream ended. **A half-assembled event is discarded, not
        // dispatched** — the spec's rule, and the right one here: an event whose
        // blank line never arrived may be missing data lines, and a durable
        // event lost this way is replayed by the reconnect, which resumes from
        // the last durable event actually *delivered*.
        internal void Reset()
        {
            _data.Clear();
            _hasData = false;
            _event = null;
            _id = null;
        }

        private SseFrame? Dispatch()
        {
            if (!_hasData)
            {
                Reset();
                return null;
            }

            var frame = new SseFrame(_event, _id, _data.ToString(), false);
            Reset();
            return frame;
        }
    }

    // --- classifying -------------------------------------------------------------

    // A frame, read for what it means. Pure; the fixtures in the tests are
    // hand-written from the measured contract's key names, with invented values.
    //
    // **The `data:` wrappers, measured 2026-09-29 (key names only):**
    //
    //  * `client_event`: `{event_id, sequence_num, event_type, source, payload,
    //    created_at}`, the payload a transcript row.
    //  * `ephemeral_event`: `{event_type, payload, timestamp, source}` — no
    //    sequence_num, no event_id. A `stream_event` payload carries `event`,
    //    the Anthropic Messages streaming event.
    //  * `delivery_update`: flat, `{event_id, status, timestamp}`.
    //  * `session_update`: flat, `{connection_status}`.
    //
    // The structural captures behind that are /tmp/cb199-stream2.txt (an
    // interrupted turn) and /tmp/cb199-stream3.txt (a cold-start turn) on the
    // Mac they were taken on: event names, ids, key names and order, no text.
    //
    // **Where the hints come from is decided by the SSE event name and nothing
    // else.** The two nested kinds are read from `payload` and only from it;
    // the two flat ones from the root; an unknown name gets no hints at all. A
    // nested event whose `payload` is missing or not an object therefore has
    // no hints — it is not read from its wrapper instead, because the wrapper
    // carries `event_type` and friends that would then be read as if they were
    // the row's own fields.
    internal static class ClaudeCloudStreamEvents
    {
        internal const string ClientEvent = "client_event";
        internal const string EphemeralEvent = "ephemeral_event";
        internal const string DeliveryUpdate = "delivery_update";
        internal const string SessionUpdate = "session_update";

        internal static CloudStreamEventKind KindFor(string? name) => name switch
        {
            ClientEvent => CloudStreamEventKind.Durable,
            EphemeralEvent => CloudStreamEventKind.Ephemeral,
            DeliveryUpdate => CloudStreamEventKind.Delivery,
            SessionUpdate => CloudStreamEventKind.Session,
            _ => CloudStreamEventKind.Other,
        };

        internal static CloudStreamEvent Classify(SseFrame frame)
        {
            if (frame.IsComment)
            {
                return new CloudStreamEvent(CloudStreamEventKind.Keepalive, null, null, null);
            }

            var kind = KindFor(frame.EventName);

            JsonDocument? doc = null;
            try
            {
                doc = string.IsNullOrWhiteSpace(frame.Data) ? null : JsonDocument.Parse(frame.Data);
            }
            catch (JsonException)
            {
                doc = null;
            }

            using (doc)
            {
                if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return new CloudStreamEvent(kind, frame.EventName,
                        kind == CloudStreamEventKind.Durable ? SequenceFrom(frame.Id) : null,
                        frame.Data);
                }

                var root = doc.RootElement;

                // Only a durable event has a sequence number. The SSE id is the
                // measured carrier; the envelope's own `sequence_num` is the
                // fallback, in either of the two types it has been seen in.
                long? sequence = kind == CloudStreamEventKind.Durable
                    ? SequenceFrom(frame.Id) ?? SequenceFrom(root, "sequence_num")
                    : null;

                JsonElement payload;
                switch (kind)
                {
                    case CloudStreamEventKind.Durable:
                    case CloudStreamEventKind.Ephemeral:
                        if (!root.TryGetProperty("payload", out payload)
                            || payload.ValueKind != JsonValueKind.Object)
                        {
                            return new CloudStreamEvent(kind, frame.EventName, sequence, frame.Data);
                        }
                        break;

                    case CloudStreamEventKind.Delivery:
                    case CloudStreamEventKind.Session:
                        payload = root;
                        break;

                    default:
                        return new CloudStreamEvent(kind, frame.EventName, null, frame.Data);
                }

                var type = Str(payload, "type");
                var subtype = Str(payload, "subtype");

                string? status = null;
                if (kind == CloudStreamEventKind.Session)
                {
                    status = Str(payload, "connection_status");
                }
                else if (type == "system" && subtype == "status")
                {
                    status = Str(payload, "status");
                }

                string? inner = null;
                string? text = null;
                if (type == "stream_event"
                    && payload.TryGetProperty("event", out var ev)
                    && ev.ValueKind == JsonValueKind.Object)
                {
                    inner = Str(ev, "type");
                    if (inner == "content_block_delta"
                        && ev.TryGetProperty("delta", out var delta)
                        && delta.ValueKind == JsonValueKind.Object
                        && Str(delta, "type") == "text_delta")
                    {
                        text = Str(delta, "text");
                    }
                }

                var isDelivery = kind == CloudStreamEventKind.Delivery;
                var delivery = isDelivery ? Str(payload, "status") : null;

                // A delivery_update names its event by `event_id`, which is
                // presumably the uuid we sent — the send's own receipt echoes
                // our uuid as `event_id`, measured — but on this event that is
                // unconfirmed. Carried as Uuid so a consumer can try the match;
                // not a promise it will hold.
                var uuid = isDelivery ? Str(payload, "event_id") : Str(payload, "uuid");

                return new CloudStreamEvent(kind, frame.EventName, sequence, frame.Data,
                    type, subtype, status, text, inner, delivery, uuid,
                    RowJson: kind == CloudStreamEventKind.Durable ? payload.GetRawText() : null);
            }
        }

        internal static long? SequenceFrom(string? text) =>
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

        // `sequence_num` is a JSON string where it was measured; a number is
        // read too, as the most plausible drift.
        internal static long? SequenceFrom(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value)) return null;

            return value.ValueKind switch
            {
                JsonValueKind.String => SequenceFrom(value.GetString()),
                JsonValueKind.Number when value.TryGetInt64(out var n) && n >= 0 => n,
                _ => null,
            };
        }

        private static string? Str(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        // A whole stream, line by line, as events: framing then classifying,
        // ending with exactly one Ended event. Pure over a TextReader so the
        // loop HttpCloudEventStream runs is the loop the tests run.
        //
        // `opened` is the status the stream was opened with, carried into the
        // clean-EOF Ended so a consumer sees what answered. A read that throws
        // an IOException — a connection dropped mid-stream — ends Unavailable
        // with status 0, the same verdict HttpCloudApi gives a transport
        // failure.
        //
        // `mediaType` is the response's own. **A 2xx that is not an event
        // stream is not read at all**: it ends ShapeChanged at once, which the
        // policy backs off and counts as a failure. The failure it guards
        // against is a proxy or a moved route answering 200 with a page — whose
        // lines would otherwise be framed as SSE, find no events, and end in a
        // clean Ok that reads exactly like a healthy stream closing.
        //
        // **Cancellation yields nothing further, ever.** The token is checked
        // after every read as well as inside it, because a reader is entitled
        // to return a line it already had even though the token was cancelled
        // while it was being asked — and delivering that line, or an Ended for
        // an EOF that raced the cancel, would hand a consumer that has already
        // closed its panel an event it has to remember to ignore.
        internal static async IAsyncEnumerable<CloudStreamEvent> ReadAsync(TextReader reader,
            int opened, string? mediaType, [EnumeratorCancellation] CancellationToken ct)
        {
            if (!IsEventStream(mediaType))
            {
                yield return Ended(new CloudOutcome(CloudOutcomeKind.ShapeChanged, opened, null,
                    NotAnEventStreamDetail(mediaType)));
                yield break;
            }

            var parser = new SseParser();

            while (true)
            {
                string? line;
                CloudOutcome? failure = null;
                try
                {
                    line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    yield break;
                }
                catch (IOException ex)
                {
                    line = null;
                    failure = new CloudOutcome(CloudOutcomeKind.Unavailable, 0, null, ex.Message);
                }

                if (ct.IsCancellationRequested) yield break;

                if (line is null)
                {
                    parser.Reset();
                    yield return Ended(failure ?? new CloudOutcome(CloudOutcomeKind.Ok, opened, null,
                        EndOfStreamDetail));
                    yield break;
                }

                if (parser.Feed(line) is { } frame) yield return Classify(frame);
            }
        }

        internal const string EndOfStreamDetail = "the stream ended";

        // The measured media type, compared without its parameters (a charset
        // would be legal) and without regard to case, as media types are.
        internal static bool IsEventStream(string? mediaType) =>
            mediaType is not null
            && string.Equals(mediaType.Split(';')[0].Trim(), ClaudeCloudStreamRequest.EventStreamMediaType,
                StringComparison.OrdinalIgnoreCase);

        internal static string NotAnEventStreamDetail(string? mediaType) =>
            "the stream answered with "
            + (string.IsNullOrWhiteSpace(mediaType) ? "no media type" : mediaType.Trim())
            + ", not an event stream";

        internal static CloudStreamEvent Ended(CloudOutcome outcome) =>
            new(CloudStreamEventKind.Ended, null, null, null, Outcome: outcome);
    }

    // --- the requests ------------------------------------------------------------

    // The stream request and the newest-sequence read, built through the same
    // CloudRequest.Build every other request to this API goes through, so the
    // header set stays one copy.
    internal static class ClaudeCloudStreamRequest
    {
        internal const string EventStreamMediaType = "text/event-stream";
        internal const string LastEventIdHeader = "Last-Event-ID";

        // `.../events/stream`, resuming from a sequence number when given one.
        // Null for an id that is not well formed, for CodeEventsPath's reason: a
        // malformed id is refused before it becomes a request.
        internal static string? StreamPath(string? sessionId, long? fromSequenceNum)
        {
            var events = CloudRequest.CodeEventsPath(sessionId);
            if (events is null) return null;

            var path = events + "/stream";
            return fromSequenceNum is { } from && from >= 0
                ? path + "?from_sequence_num=" + from.ToString(CultureInfo.InvariantCulture)
                : path;
        }

        // Measured headers: Bearer, anthropic-version, and Accept
        // text/event-stream; on a resume, Last-Event-ID carrying the same number
        // as the query. The token reaches the Authorization header only.
        internal static HttpRequestMessage? Build(string accessToken, string? sessionId,
            long? fromSequenceNum)
        {
            var path = StreamPath(sessionId, fromSequenceNum);
            if (path is null) return null;

            var request = CloudRequest.Build(accessToken, path);
            request.Headers.Accept.ParseAdd(EventStreamMediaType);

            if (fromSequenceNum is { } from && from >= 0)
            {
                request.Headers.TryAddWithoutValidation(LastEventIdHeader,
                    from.ToString(CultureInfo.InvariantCulture));
            }

            return request;
        }

        // What an OpenAsync for a malformed id ends with, having sent nothing.
        // SessionGone rather than a retryable kind, because no amount of waiting
        // makes a malformed id name a session — and ClaudeCloudStreamPolicy stops
        // on SessionGone.
        internal static readonly CloudOutcome RefusedId = new(CloudOutcomeKind.SessionGone, 0, null,
            "not a well-formed session id, so no request was made");

        // Where the stream should start: the newest durable event there is.
        internal static string? NewestSequencePath(string? sessionId)
        {
            var events = CloudRequest.CodeEventsPath(sessionId);
            return events is null ? null : events + "?limit=1&sort_order=desc";
        }

        // `data[0].sequence_num` of that read — a JSON string where measured, a
        // number read too. Null when there is nothing to read, which for a new
        // session with no events is a true answer rather than a failure.
        internal static long? ParseNewestSequence(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array
                    || data.GetArrayLength() == 0
                    || data[0].ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                return ClaudeCloudStreamEvents.SequenceFrom(data[0], "sequence_num");
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    // --- reconnecting --------------------------------------------------------------

    // When to open the stream again after it ended, and when to give up on it
    // and fall back to polling.
    //
    // **Every number here is a placeholder**, named Unmeasured* for CB-122's
    // reason. What was measured: a stream held 40+ s idle with keepalives, and
    // nothing about how long the server holds one, whether it closes them on
    // purpose, or what it does on a token expiry mid-stream. So a clean end of
    // stream is treated as ordinary — reconnect soon — and everything else
    // follows Backoff, which this arm already trusts.
    internal static class ClaudeCloudStreamPolicy
    {
        // After a clean end of a connection that was delivering events.
        internal static readonly TimeSpan UnmeasuredReconnectAfterEnd = TimeSpan.FromSeconds(1);

        // How long a connection has to have stayed open to count as healthy
        // when it delivered no real event — a quiet session sends nothing but
        // keepalives, and one that held for this long was not being refused.
        internal static readonly TimeSpan UnmeasuredHealthyConnectionAge = TimeSpan.FromSeconds(60);

        // Consecutive unhealthy connections before the panel should stop
        // relying on the stream and poll instead. It keeps retrying the stream
        // in the background at the backed-off wait; this only says when the
        // panel should not wait for it.
        internal const int UnmeasuredFailuresBeforeFallback = 3;

        // Where a run of reconnects stands. A healthy connection resets it; see
        // WasHealthy for what counts.
        internal readonly record struct State(TimeSpan? LastWait, int ConsecutiveFailures)
        {
            internal static State Initial => new(null, 0);
        }

        // Wait null means **stop**: the session has gone or ended, or the
        // credential or the account was refused. Reopening would be a retry
        // loop against an answer that is not going to change.
        internal readonly record struct Decision(TimeSpan? Wait, bool FallBackToPolling, State Next);

        // Did an event count towards the connection having been healthy?
        //
        // **Only a client_event or an ephemeral_event.** A keepalive, a
        // session_update and a delivery_update all arrive on a connection that
        // is doing nothing for us — the first two are the very first things a
        // fresh connection sends — so a server that answered each connection
        // with them and closed would otherwise read as healthy every time, and
        // earn the one-second reconnect forever. That was QA's reconnect storm.
        internal static bool CountsTowardHealth(CloudStreamEvent ev) =>
            ev.Kind is CloudStreamEventKind.Durable or CloudStreamEventKind.Ephemeral;

        // A connection was healthy if it delivered a real event, or if it stayed
        // open long enough that it was plainly not being turned away.
        internal static bool WasHealthy(bool deliveredEvent, TimeSpan connectedFor) =>
            deliveredEvent || connectedFor >= UnmeasuredHealthyConnectionAge;

        // `healthy` is WasHealthy's answer for the connection that just ended.
        internal static Decision Next(State state, CloudOutcome ended, bool healthy)
        {
            var failures = healthy ? 0 : state.ConsecutiveFailures;
            var previous = healthy ? null : state.LastWait;

            TimeSpan? wait;
            switch (ended.Kind)
            {
                // A clean end. After a healthy connection it is ordinary and
                // gets the short reconnect; an unhealthy one is counted and
                // backed off like a failure, so a server closing every stream
                // at once cannot turn this into a tight loop.
                case CloudOutcomeKind.Ok:
                    if (healthy)
                    {
                        return new Decision(UnmeasuredReconnectAfterEnd, false,
                            new State(UnmeasuredReconnectAfterEnd, 0));
                    }

                    wait = Backoff.Next(new CloudOutcome(CloudOutcomeKind.Unavailable, ended.Status),
                        previous);
                    break;

                // An ended session will not take a stream either. Backoff keeps
                // 409 retryable because the roster reads it; the stream does not.
                case CloudOutcomeKind.SessionInactive:
                    wait = null;
                    break;

                default:
                    wait = Backoff.Next(ended, previous);
                    break;
            }

            if (wait is null)
            {
                return new Decision(null, true, new State(null, failures + 1));
            }

            var count = failures + 1;
            return new Decision(wait, count >= UnmeasuredFailuresBeforeFallback, new State(wait, count));
        }

        // Where a run starts: the newest durable event the server has, unless
        // this session has already handled an older one, in which case from
        // there — everything after it is what the panel missed while it was
        // shut. The replay is bounded by the server, not by this: measured
        // (2026-11-17) a stream opened from 0 on a 2,547-event session began at
        // 1,531, so a panel shut for weeks costs at most that window, and every
        // replayed row reconciles by uuid against what the panel already holds.
        // Nothing seen, or a sequence the server no longer has (newer than its
        // newest, or no newest at all), starts from the newest as before.
        internal static long? StartFrom(long? newest, long? lastSeen) =>
            lastSeen is { } seen && newest is { } now && seen < now ? seen : newest;

        // The sequence number to resume from after this event: a durable
        // event's, if it is newer. Only durable events move it — an ephemeral
        // event is never replayed, so resuming from one would skip nothing and
        // mean nothing.
        internal static long? ResumeFrom(long? current, CloudStreamEvent ev) =>
            ev.Kind == CloudStreamEventKind.Durable
            && ev.SequenceNum is { } seq
            && (current is not { } now || seq > now)
                ? seq
                : current;
    }

    // --- the socket ----------------------------------------------------------------

    // The real one.
    //
    // Excluded from coverage for HttpCloudApi's reason: it is an HttpClient
    // talking to api.anthropic.com, and tests never touch the real network.
    // Everything it decides is in the pure classes above — the request, the
    // framing, the classifying, the verdict on a non-2xx — and the line loop it
    // runs is ClaudeCloudStreamEvents.ReadAsync, which the tests run over a
    // StringReader. What is left is the connection.
    [ExcludeFromCodeCoverage]
    internal sealed class HttpCloudEventStream : ICloudEventStream, IDisposable
    {
        // **No timeout.** A stream is supposed to stay open for as long as the
        // panel is; HttpClient's default hundred seconds would cut every one
        // short. What ends a stream is the caller's cancellation, the server, or
        // the network. ResponseHeadersRead below is what lets the body be read
        // as it arrives rather than buffered to its end, which never comes.
        private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

        public async IAsyncEnumerable<CloudStreamEvent> OpenAsync(string accessToken,
            string sessionId, long? fromSequenceNum, [EnumeratorCancellation] CancellationToken ct)
        {
            using var request = ClaudeCloudStreamRequest.Build(accessToken, sessionId, fromSequenceNum);
            if (request is null)
            {
                yield return ClaudeCloudStreamEvents.Ended(ClaudeCloudStreamRequest.RefusedId);
                yield break;
            }

            HttpResponseMessage? response = null;
            CloudOutcome? failure = null;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                yield break;
            }
            catch (HttpRequestException ex)
            {
                failure = new CloudOutcome(CloudOutcomeKind.Unavailable, 0, null, ex.Message);
            }

            if (response is null)
            {
                yield return ClaudeCloudStreamEvents.Ended(failure!);
                yield break;
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    string body;
                    try
                    {
                        body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException
                                                   or OperationCanceledException)
                    {
                        body = "";
                    }

                    yield return ClaudeCloudStreamEvents.Ended(CloudOutcomes.OutcomeFor(
                        (int)response.StatusCode, body, response.Headers.RetryAfter?.Delta,
                        response.Headers.Contains("cf-mitigated")));
                    yield break;
                }

                Stream? stream = null;
                try
                {
                    stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    yield break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    failure = new CloudOutcome(CloudOutcomeKind.Unavailable, 0, null, ex.Message);
                }

                if (stream is null)
                {
                    yield return ClaudeCloudStreamEvents.Ended(failure!);
                    yield break;
                }

                // Disposing the reader disposes the stream, which is what closes
                // the connection when the caller stops enumerating.
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await foreach (var ev in ClaudeCloudStreamEvents.ReadAsync(reader,
                                   (int)response.StatusCode, response.Content.Headers.ContentType?.MediaType,
                                   ct).ConfigureAwait(false))
                {
                    yield return ev;
                }
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
