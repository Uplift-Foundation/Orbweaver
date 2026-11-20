using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// Covers the cloud event stream's pure half: SSE framing, reading each event
// for what it means, the two requests, and when to reconnect.
//
// **The fixtures follow CB-199's structural stream captures** (2026-09-29,
// /tmp/cb199-stream2.txt and /tmp/cb199-stream3.txt on the Mac they were taken
// on): event names, which events carry an id, the wrapper and payload key
// names and their order are the captured ones. Every value — ids, sequence
// numbers, text — is invented, because the probe printed structure only.
public class ClaudeCloudStreamTests
{
    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";
    private const string SessionId = "session_01StreamFixture";

    private static List<SseFrame> Frames(params string[] lines)
    {
        var parser = new SseParser();
        var frames = new List<SseFrame>();
        foreach (var line in lines)
        {
            if (parser.Feed(line) is { } frame) frames.Add(frame);
        }
        return frames;
    }

    // --- framing -------------------------------------------------------------

    [Fact]
    public void AnEventIsItsFieldsUpToTheBlankLine()
    {
        var frame = Assert.Single(Frames("event: client_event", "id: 42", "data: {\"a\":1}", ""));

        Assert.Equal("client_event", frame.EventName);
        Assert.Equal("42", frame.Id);
        Assert.Equal("{\"a\":1}", frame.Data);
        Assert.False(frame.IsComment);
    }

    [Fact]
    public void NothingIsDispatchedBeforeTheBlankLine()
    {
        Assert.Empty(Frames("event: client_event", "id: 42", "data: {}"));
    }

    [Fact]
    public void DataLinesJoinWithANewline()
    {
        var frame = Assert.Single(Frames("data: one", "data: two", "data:", "data: four", ""));

        Assert.Equal("one\ntwo\n\nfour", frame.Data);
    }

    // Measured: an ephemeral event has no id line.
    [Fact]
    public void AMissingIdIsNull()
    {
        var frame = Assert.Single(Frames("event: ephemeral_event", "data: {}", ""));

        Assert.Null(frame.Id);
    }

    // The deliberate departure from the spec: the id does not carry over, so
    // an ephemeral event after a durable one does not wear its sequence number.
    [Fact]
    public void AnIdDoesNotCarryIntoTheNextEvent()
    {
        var frames = Frames("event: client_event", "id: 7", "data: {}", "",
            "event: ephemeral_event", "data: {}", "");

        Assert.Equal("7", frames[0].Id);
        Assert.Null(frames[1].Id);
        Assert.Equal("ephemeral_event", frames[1].EventName);
    }

    [Fact]
    public void AnEventNameDoesNotCarryIntoTheNextEventEither()
    {
        var frames = Frames("event: client_event", "data: {}", "", "data: {}", "");

        Assert.Null(frames[1].EventName);
    }

    // Measured as the keepalive, about every 12–15 s.
    [Theory]
    [InlineData(":", "")]
    [InlineData(": keepalive", "keepalive")]
    [InlineData(":keepalive", "keepalive")]
    public void ACommentIsItsOwnFrameAtOnce(string line, string text)
    {
        var frame = Assert.Single(Frames(line));

        Assert.True(frame.IsComment);
        Assert.Equal(text, frame.Data);
        Assert.Null(frame.EventName);
    }

    [Fact]
    public void ACommentInsideAnEventDoesNotDisturbIt()
    {
        var frames = Frames("event: client_event", ": ping", "data: {}", "");

        Assert.True(frames[0].IsComment);
        Assert.Equal("client_event", frames[1].EventName);
        Assert.Equal("{}", frames[1].Data);
    }

    [Fact]
    public void ACrlfLineEndingIsStripped()
    {
        var frame = Assert.Single(Frames("event: client_event\r", "id: 3\r", "data: {}\r", "\r"));

        Assert.Equal("client_event", frame.EventName);
        Assert.Equal("3", frame.Id);
        Assert.Equal("{}", frame.Data);
    }

    // One space after the colon is removed, and only one.
    [Fact]
    public void OnlyOneLeadingSpaceIsRemoved()
    {
        Assert.Equal("x", Assert.Single(Frames("data:x", "")).Data);
        Assert.Equal(" x", Assert.Single(Frames("data:  x", "")).Data);
    }

    [Fact]
    public void AColonInTheValueIsKept()
    {
        Assert.Equal("{\"a\":\"b:c\"}", Assert.Single(Frames("data: {\"a\":\"b:c\"}", "")).Data);
    }

    // A line with no colon is a field with an empty value: `data` alone is an
    // empty data line.
    [Fact]
    public void AFieldWithNoColonHasAnEmptyValue()
    {
        Assert.Equal("", Assert.Single(Frames("data", "")).Data);
    }

    [Fact]
    public void ABlankLineWithNoDataDispatchesNothingAndClearsTheEvent()
    {
        var frames = Frames("event: client_event", "id: 9", "", "data: {}", "");

        var frame = Assert.Single(frames);
        Assert.Null(frame.EventName);
        Assert.Null(frame.Id);
    }

    [Fact]
    public void RetryAndUnknownFieldsAreIgnored()
    {
        var frame = Assert.Single(Frames("retry: 10", "whatever: x", "data: {}", ""));

        Assert.Equal("{}", frame.Data);
        Assert.Null(frame.EventName);
    }

    [Fact]
    public void AnIdWithANulInItIsIgnored()
    {
        Assert.Null(Assert.Single(Frames("id: 4\05", "data: {}", "")).Id);
    }

    // The spec's rule: an event whose blank line never came is discarded.
    [Fact]
    public void ResetDiscardsAHalfAssembledEvent()
    {
        var parser = new SseParser();
        parser.Feed("event: client_event");
        parser.Feed("id: 5");
        parser.Feed("data: {}");
        parser.Reset();

        Assert.Null(parser.Feed(""));
    }

    // --- classifying ---------------------------------------------------------

    private static CloudStreamEvent Classify(string name, string data, string? id = null) =>
        ClaudeCloudStreamEvents.Classify(new SseFrame(name, id, data, false));

    private const string UserEcho =
        """{"event_id":"e1","sequence_num":"101","event_type":"user","source":"client","payload":{"type":"user","uuid":"uuid-ours","session_id":"session_01StreamFixture","message":{"role":"user","content":"hi"},"parent_tool_use_id":null},"created_at":"2026-09-29T10:00:00Z"}""";

    [Fact]
    public void AClientEventIsDurableWithItsSequenceNumberAndUuid()
    {
        var ev = Classify("client_event", UserEcho, "101");

        Assert.Equal(CloudStreamEventKind.Durable, ev.Kind);
        Assert.Equal("client_event", ev.Name);
        Assert.Equal(101, ev.SequenceNum);
        Assert.Equal("user", ev.PayloadType);
        Assert.Equal("uuid-ours", ev.Uuid);
        Assert.Equal(UserEcho, ev.PayloadJson);
        Assert.Null(ev.TextDelta);
        Assert.Null(ev.Outcome);
    }

    // The SSE id is the measured carrier; the wrapper's own sequence_num is
    // the fallback, as a string or a number.
    [Theory]
    [InlineData("""{"sequence_num":"55","payload":{"type":"assistant"}}""", 55L)]
    [InlineData("""{"sequence_num":56,"payload":{"type":"assistant"}}""", 56L)]
    [InlineData("""{"sequence_num":-1,"payload":{"type":"assistant"}}""", null)]
    [InlineData("""{"sequence_num":1.5,"payload":{"type":"assistant"}}""", null)]
    [InlineData("""{"sequence_num":true,"payload":{"type":"assistant"}}""", null)]
    [InlineData("""{"sequence_num":"x","payload":{"type":"assistant"}}""", null)]
    [InlineData("""{"payload":{"type":"assistant"}}""", null)]
    public void ADurableEventWithNoIdFallsBackToTheWrapper(string data, long? expected)
    {
        Assert.Equal(expected, Classify("client_event", data).SequenceNum);
    }

    [Fact]
    public void TheSseIdWinsOverTheWrapper()
    {
        Assert.Equal(9, Classify("client_event", """{"sequence_num":"8","payload":{}}""", "9").SequenceNum);
    }

    [Theory]
    [InlineData("""{"payload":{"type":"system","subtype":"init"}}""", "system", "init", null)]
    [InlineData("""{"payload":{"type":"system","subtype":"status","status":"requesting"}}""", "system", "status", "requesting")]
    [InlineData("""{"payload":{"type":"result","subtype":"success"}}""", "result", "success", null)]
    [InlineData("""{"payload":{"type":"result","subtype":"error_during_execution"}}""", "result", "error_during_execution", null)]
    [InlineData("""{"payload":{"type":"control_request"}}""", "control_request", null, null)]
    [InlineData("""{"payload":{"type":"control_response"}}""", "control_response", null, null)]
    [InlineData("""{"payload":{"type":"assistant","aborted":true}}""", "assistant", null, null)]
    public void DurableRowsCarryTheirTypeSubtypeAndStatus(string data, string type, string? subtype,
        string? status)
    {
        var ev = Classify("client_event", data, "1");

        Assert.Equal(type, ev.PayloadType);
        Assert.Equal(subtype, ev.Subtype);
        Assert.Equal(status, ev.StatusValue);
    }

    // A `status` field on anything but a system status row is not the turn's
    // status, and is not reported as one.
    [Fact]
    public void AStatusOnSomeOtherRowIsNotTheTurnsStatus()
    {
        Assert.Null(Classify("client_event", """{"payload":{"type":"system","subtype":"init","status":"x"}}""").StatusValue);
        Assert.Null(Classify("client_event", """{"payload":{"type":"user","subtype":"status","status":"x"}}""").StatusValue);
    }

    private static string StreamEvent(string inner) =>
        """{"event_type":"stream_event","payload":{"type":"stream_event","event":""" + inner
        + ""","parent_tool_use_id":null,"session_id":"session_01StreamFixture","uuid":"u-e"},"timestamp":"t","source":"s"}""";

    [Fact]
    public void ATextDeltaCarriesItsText()
    {
        var ev = Classify("ephemeral_event",
            StreamEvent("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello, wor"}}"""));

        Assert.Equal(CloudStreamEventKind.Ephemeral, ev.Kind);
        Assert.Equal("stream_event", ev.PayloadType);
        Assert.Equal("content_block_delta", ev.InnerType);
        Assert.Equal("Hello, wor", ev.TextDelta);
        Assert.Null(ev.SequenceNum);
    }

    // Only text: a thinking or tool-input delta is not text the panel shows.
    [Theory]
    [InlineData("""{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"hmm"}}""")]
    [InlineData("""{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{"}}""")]
    [InlineData("""{"type":"content_block_delta","index":0,"delta":"not an object"}""")]
    [InlineData("""{"type":"content_block_delta","index":0}""")]
    [InlineData("""{"type":"message_delta","delta":{"type":"text_delta","text":"no"}}""")]
    public void AnythingButATextDeltaHasNoText(string inner)
    {
        var ev = Classify("ephemeral_event", StreamEvent(inner));

        Assert.Null(ev.TextDelta);
        Assert.NotNull(ev.InnerType);
    }

    [Theory]
    [InlineData("message_start")]
    [InlineData("content_block_start")]
    [InlineData("content_block_stop")]
    [InlineData("message_delta")]
    [InlineData("message_stop")]
    public void AStreamEventNamesItsInnerType(string inner)
    {
        Assert.Equal(inner, Classify("ephemeral_event", StreamEvent("""{"type":""" + "\"" + inner + "\"}")).InnerType);
    }

    [Fact]
    public void AStreamEventWithNoEventObjectHasNoInnerType()
    {
        var ev = Classify("ephemeral_event", """{"payload":{"type":"stream_event","event":"x"}}""");

        Assert.Equal("stream_event", ev.PayloadType);
        Assert.Null(ev.InnerType);
        Assert.Null(ev.TextDelta);
    }

    // An `event` object on something that is not a stream_event is not read.
    [Fact]
    public void AnEventObjectOnAnotherTypeIsNotAStreamEvent()
    {
        Assert.Null(Classify("ephemeral_event", """{"payload":{"type":"system","event":{"type":"message_start"}}}""").InnerType);
    }

    [Fact]
    public void ThePostTurnSummaryIsAnEphemeralSystemRow()
    {
        var ev = Classify("ephemeral_event",
            """{"event_type":"system","payload":{"type":"system","subtype":"post_turn_summary","needs_action":false,"status_category":"c","status_detail":"d","summarizes_uuid":"u"},"timestamp":"t","source":"s"}""");

        Assert.Equal("system", ev.PayloadType);
        Assert.Equal("post_turn_summary", ev.Subtype);
        Assert.Null(ev.StatusValue);
    }

    // An ephemeral event is never replayed, so it never carries a sequence
    // number — even if an id line or a sequence_num turned up on one.
    [Fact]
    public void AnEphemeralEventNeverHasASequenceNumber()
    {
        Assert.Null(Classify("ephemeral_event", """{"sequence_num":"3","payload":{}}""", "3").SequenceNum);
    }

    [Theory]
    [InlineData("DELIVERY_STATUS_RECEIVED")]
    [InlineData("DELIVERY_STATUS_PROCESSING")]
    [InlineData("DELIVERY_STATUS_PROCESSED")]
    public void ADeliveryUpdateIsFlatWithItsStatusAndEventId(string status)
    {
        var ev = Classify("delivery_update",
            """{"event_id":"uuid-ours","status":""" + "\"" + status + "\"" + ""","timestamp":"t"}""");

        Assert.Equal(CloudStreamEventKind.Delivery, ev.Kind);
        Assert.Equal(status, ev.DeliveryStatus);
        Assert.Equal("uuid-ours", ev.Uuid);
        Assert.Null(ev.SequenceNum);
    }

    // Flat means flat: a delivery status is read off the root, and a nested
    // payload does not stand in for it.
    [Fact]
    public void ADeliveryUpdateReadsTheRootNotAPayload()
    {
        var ev = Classify("delivery_update", """{"payload":{"status":"DELIVERY_STATUS_RECEIVED","event_id":"x"}}""");

        Assert.Null(ev.DeliveryStatus);
        Assert.Null(ev.Uuid);
    }

    [Fact]
    public void ASessionUpdateCarriesItsConnectionStatus()
    {
        var ev = Classify("session_update", """{"connection_status":"connected"}""");

        Assert.Equal(CloudStreamEventKind.Session, ev.Kind);
        Assert.Equal("connected", ev.StatusValue);
        Assert.Null(ev.DeliveryStatus);
    }

    // Where the hints come from is keyed off the event name alone. A nested
    // kind whose payload is missing or not an object has no hints — its
    // wrapper is not read as if it were the row — but a durable one keeps its
    // sequence number, so the resume point survives.
    [Theory]
    [InlineData("""{"type":"assistant","uuid":"u","payload":"not an object"}""")]
    [InlineData("""{"type":"assistant","uuid":"u"}""")]
    [InlineData("""{"event_type":"assistant","sequence_num":"4","payload":null}""")]
    public void ANestedKindWithNoPayloadObjectHasNoHints(string data)
    {
        var durable = Classify("client_event", data, "4");
        Assert.Equal(4, durable.SequenceNum);
        Assert.Null(durable.PayloadType);
        Assert.Null(durable.Uuid);
        Assert.Null(durable.RowJson);
        Assert.Equal(data, durable.PayloadJson);

        var ephemeral = Classify("ephemeral_event", data);
        Assert.Null(ephemeral.PayloadType);
        Assert.Null(ephemeral.Uuid);
    }

    // An event name nobody has seen gets no hints, even if its data looks like
    // a row: guessing what an unknown event means is how a new kind of event
    // would end up drawn as a turn.
    [Fact]
    public void AnUnknownEventNameGetsNoHints()
    {
        var ev = Classify("prompt_suggestion", """{"payload":{"type":"assistant","uuid":"u"},"type":"x"}""", "9");

        Assert.Equal(CloudStreamEventKind.Other, ev.Kind);
        Assert.Null(ev.PayloadType);
        Assert.Null(ev.Uuid);
        Assert.Null(ev.SequenceNum);
        Assert.Null(ev.RowJson);
        Assert.NotNull(ev.PayloadJson);
    }

    // RowJson is the durable row itself, ready for the transcript mapping.
    [Fact]
    public void ADurableEventCarriesItsRowAsJson()
    {
        var ev = Classify("client_event", UserEcho, "101");

        using var row = System.Text.Json.JsonDocument.Parse(ev.RowJson!);
        Assert.Equal("user", row.RootElement.GetProperty("type").GetString());
        Assert.Equal("uuid-ours", row.RootElement.GetProperty("uuid").GetString());
        Assert.False(row.RootElement.TryGetProperty("event_type", out _));
        Assert.False(row.RootElement.TryGetProperty("sequence_num", out _));
    }

    [Fact]
    public void OnlyADurableEventCarriesARow()
    {
        Assert.Null(Classify("ephemeral_event", """{"payload":{"type":"stream_event"}}""").RowJson);
        Assert.Null(Classify("delivery_update", """{"event_id":"e","status":"DELIVERY_STATUS_RECEIVED"}""").RowJson);
        Assert.Null(Classify("session_update", """{"connection_status":"connected"}""").RowJson);
    }

    // A hint field that is present but not a string is no hint, not a
    // stringified number or a crash.
    [Fact]
    public void AHintThatIsNotAStringIsNull()
    {
        var ev = Classify("client_event", """{"payload":{"type":5,"subtype":true,"uuid":{"a":1}}}""", "1");
        Assert.Null(ev.PayloadType);
        Assert.Null(ev.Subtype);
        Assert.Null(ev.Uuid);

        Assert.Null(Classify("session_update", """{"connection_status":3}""").StatusValue);
        Assert.Null(Classify("delivery_update", """{"status":null,"event_id":7}""").DeliveryStatus);
    }

    [Theory]
    [InlineData("client_event", "Durable")]
    [InlineData("ephemeral_event", "Ephemeral")]
    [InlineData("delivery_update", "Delivery")]
    [InlineData("session_update", "Session")]
    [InlineData("prompt_suggestion", "Other")]
    [InlineData("CLIENT_EVENT", "Other")]
    [InlineData(null, "Other")]
    public void EachNameHasItsKind(string? name, string kind)
    {
        // By name: the enum is internal and a public theory cannot take it.
        Assert.Equal(kind, ClaudeCloudStreamEvents.KindFor(name).ToString());
    }

    // Data that is not a JSON object is delivered with no hints rather than
    // dropped — a durable one still keeps the sequence number its id gave it,
    // so the resume point is not lost to a body this version cannot read.
    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"a string\"")]
    [InlineData("")]
    [InlineData("   ")]
    public void DataThatIsNotAnObjectKeepsItsKindAndId(string data)
    {
        var ev = Classify("client_event", data, "12");

        Assert.Equal(CloudStreamEventKind.Durable, ev.Kind);
        Assert.Equal(12, ev.SequenceNum);
        Assert.Equal(data, ev.PayloadJson);
        Assert.Null(ev.PayloadType);
    }

    [Fact]
    public void UnparseableDataOnANonDurableEventHasNoSequence()
    {
        Assert.Null(Classify("ephemeral_event", "not json", "12").SequenceNum);
    }

    [Fact]
    public void AKeepaliveIsJustAKeepalive()
    {
        var ev = ClaudeCloudStreamEvents.Classify(new SseFrame(null, null, "keepalive", true));

        Assert.Equal(CloudStreamEventKind.Keepalive, ev.Kind);
        Assert.Null(ev.PayloadJson);
        Assert.Null(ev.SequenceNum);
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("129", 129L)]
    [InlineData("-1", null)]
    [InlineData("+1", null)]
    [InlineData(" 1", null)]
    [InlineData("1.0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("99999999999999999999", null)]
    public void ASequenceNumberIsANonNegativeInteger(string? text, long? expected)
    {
        Assert.Equal(expected, ClaudeCloudStreamEvents.SequenceFrom(text));
    }

    // --- reading a whole stream ----------------------------------------------

    private static async Task<List<CloudStreamEvent>> ReadAll(TextReader reader,
        CancellationToken ct = default)
    {
        var events = new List<CloudStreamEvent>();
        await foreach (var ev in ClaudeCloudStreamEvents.ReadAsync(reader, 200, "text/event-stream", ct)) events.Add(ev);
        return events;
    }

    [Fact]
    public async Task AStreamEndsWithExactlyOneOkEnded()
    {
        var events = await ReadAll(new StringReader(
            ": keepalive\nevent: session_update\ndata: {\"connection_status\":\"connected\"}\n\n"));

        Assert.Equal(new[] { CloudStreamEventKind.Keepalive, CloudStreamEventKind.Session,
            CloudStreamEventKind.Ended }, events.Select(e => e.Kind));

        var ended = events[^1];
        Assert.Equal(CloudOutcomeKind.Ok, ended.Outcome!.Kind);
        Assert.Equal(200, ended.Outcome.Status);
        Assert.Equal(ClaudeCloudStreamEvents.EndOfStreamDetail, ended.Outcome.Detail);
    }

    // The partial final event at EOF is discarded, per the spec.
    [Fact]
    public async Task APartialFinalEventIsDiscarded()
    {
        var events = await ReadAll(new StringReader(
            "event: client_event\nid: 1\ndata: {}\n\nevent: client_event\nid: 2\ndata: {}"));

        Assert.Equal(new long?[] { 1, null }, events.Select(e => e.SequenceNum));
        Assert.Equal(CloudStreamEventKind.Ended, events[^1].Kind);
    }

    [Fact]
    public async Task CrlfStreamsReadTheSame()
    {
        var events = await ReadAll(new StringReader(
            "event: client_event\r\nid: 3\r\ndata: {\"payload\":{\"type\":\"user\"}}\r\n\r\n"));

        Assert.Equal(3, events[0].SequenceNum);
        Assert.Equal("user", events[0].PayloadType);
    }

    [Fact]
    public async Task AnEmptyStreamIsJustEnded()
    {
        var events = await ReadAll(new StringReader(""));

        Assert.Equal(CloudStreamEventKind.Ended, Assert.Single(events).Kind);
    }

    // A connection dropped mid-stream.
    private sealed class DroppingReader : TextReader
    {
        private readonly Queue<string> _lines;
        internal DroppingReader(params string[] lines) => _lines = new Queue<string>(lines);

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (_lines.Count > 0) return ValueTask.FromResult<string?>(_lines.Dequeue());
            throw new IOException("the connection was reset");
        }
    }

    [Fact]
    public async Task ADroppedConnectionEndsUnavailableAndDiscardsThePartialEvent()
    {
        var events = await ReadAll(new DroppingReader("event: client_event", "id: 1", "data: {}"));

        var ended = Assert.Single(events);
        Assert.Equal(CloudStreamEventKind.Ended, ended.Kind);
        Assert.Equal(CloudOutcomeKind.Unavailable, ended.Outcome!.Kind);
        Assert.Equal(0, ended.Outcome.Status);
        Assert.Equal("the connection was reset", ended.Outcome.Detail);
    }

    // Cancelled is the caller's decision: no Ended, nothing further.
    private sealed class CancellingReader : TextReader
    {
        private readonly CancellationTokenSource _cts;
        private bool _first = true;
        internal CancellingReader(CancellationTokenSource cts) => _cts = cts;

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (_first)
            {
                _first = false;
                return ValueTask.FromResult<string?>(": keepalive");
            }

            _cts.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    [Fact]
    public async Task CancellingEndsTheEnumerationWithNoEnded()
    {
        using var cts = new CancellationTokenSource();
        var events = await ReadAll(new CancellingReader(cts), cts.Token);

        Assert.Equal(CloudStreamEventKind.Keepalive, Assert.Single(events).Kind);
    }

    [Fact]
    public void EndedCarriesItsOutcomeAndNothingElse()
    {
        var outcome = CloudOutcomes.OutcomeFor(404, null);
        var ended = ClaudeCloudStreamEvents.Ended(outcome);

        Assert.Equal(CloudStreamEventKind.Ended, ended.Kind);
        Assert.Same(outcome, ended.Outcome);
        Assert.Null(ended.PayloadJson);
        Assert.Null(ended.SequenceNum);
    }

    // --- a 2xx that is not an event stream (QA) --------------------------------

    private static async Task<List<CloudStreamEvent>> ReadAs(string? mediaType, TextReader reader,
        CancellationToken ct = default)
    {
        var events = new List<CloudStreamEvent>();
        await foreach (var ev in ClaudeCloudStreamEvents.ReadAsync(reader, 200, mediaType, ct)) events.Add(ev);
        return events;
    }

    // A proxy or a moved route answering 200 with a page must not be framed
    // as SSE and end in an Ok that reads like a healthy stream closing.
    [Theory]
    [InlineData("text/html", "the stream answered with text/html, not an event stream")]
    [InlineData("application/json", "the stream answered with application/json, not an event stream")]
    [InlineData(null, "the stream answered with no media type, not an event stream")]
    [InlineData("  ", "the stream answered with no media type, not an event stream")]
    public async Task ATwoHundredThatIsNotAnEventStreamIsNotRead(string? mediaType, string detail)
    {
        var events = await ReadAs(mediaType, new StringReader("event: client_event\nid: 1\ndata: {}\n\n"));

        var ended = Assert.Single(events);
        Assert.Equal(CloudStreamEventKind.Ended, ended.Kind);
        Assert.Equal(CloudOutcomeKind.ShapeChanged, ended.Outcome!.Kind);
        Assert.Equal(200, ended.Outcome.Status);
        Assert.Equal(detail, ended.Outcome.Detail);
    }

    // And the policy treats it as a failure: backed off, counted.
    [Fact]
    public async Task ThePolicyBacksOffAWrongMediaType()
    {
        var ended = Assert.Single(await ReadAs("text/html", new StringReader("<html>")));

        var d = ClaudeCloudStreamPolicy.Next(ClaudeCloudStreamPolicy.State.Initial, ended.Outcome!,
            ClaudeCloudStreamPolicy.WasHealthy(false, TimeSpan.FromMilliseconds(50)));

        Assert.Equal(Backoff.UnavailableFloor, d.Wait);
        Assert.Equal(1, d.Next.ConsecutiveFailures);
    }

    [Theory]
    [InlineData("text/event-stream", true)]
    [InlineData("TEXT/Event-Stream", true)]
    [InlineData(" text/event-stream ", true)]
    [InlineData("text/event-stream; charset=utf-8", true)]
    [InlineData("text/event-streams", false)]
    [InlineData("text/plain", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheMediaTypeIsComparedWithoutParametersOrCase(string? mediaType, bool expected)
    {
        Assert.Equal(expected, ClaudeCloudStreamEvents.IsEventStream(mediaType));
    }

    // --- cancellation after a read (QA) ------------------------------------------

    // A reader that hands back what it already had while the token is being
    // cancelled under it: a line on the first call, EOF on the second.
    private sealed class RacingReader : TextReader
    {
        private readonly CancellationTokenSource _cts;
        private readonly string?[] _answers;
        private int _call;
        internal RacingReader(CancellationTokenSource cts, params string?[] answers)
        {
            _cts = cts;
            _answers = answers;
        }

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var answer = _answers[_call++];
            if (_call == _answers.Length) _cts.Cancel();
            return ValueTask.FromResult(answer);
        }
    }

    // The line that completes an event arrives as the token is cancelled:
    // no frame is delivered for it.
    [Fact]
    public async Task ALineReadAsTheTokenIsCancelledYieldsNoFrame()
    {
        using var cts = new CancellationTokenSource();
        var events = await ReadAs("text/event-stream",
            new RacingReader(cts, "event: client_event", "id: 1", "data: {}", ""), cts.Token);

        Assert.Empty(events);
    }

    [Fact]
    public async Task AKeepaliveReadAsTheTokenIsCancelledYieldsNothing()
    {
        using var cts = new CancellationTokenSource();
        var events = await ReadAs("text/event-stream", new RacingReader(cts, ": keepalive"), cts.Token);

        Assert.Empty(events);
    }

    // An EOF that races the cancel produces no Ended either.
    [Fact]
    public async Task AnEndOfStreamReadAsTheTokenIsCancelledYieldsNoEnded()
    {
        using var cts = new CancellationTokenSource();
        var events = await ReadAs("text/event-stream", new RacingReader(cts, ": keepalive", null), cts.Token);

        Assert.Equal(CloudStreamEventKind.Keepalive, Assert.Single(events).Kind);
    }

    // --- what counts as a healthy connection (QA) --------------------------------

    [Theory]
    [InlineData(CloudStreamEventKindName.Durable, true)]
    [InlineData(CloudStreamEventKindName.Ephemeral, true)]
    [InlineData(CloudStreamEventKindName.Keepalive, false)]
    [InlineData(CloudStreamEventKindName.Session, false)]
    [InlineData(CloudStreamEventKindName.Delivery, false)]
    [InlineData(CloudStreamEventKindName.Other, false)]
    [InlineData(CloudStreamEventKindName.Ended, false)]
    public void OnlyARealEventCountsTowardHealth(string kind, bool counts)
    {
        var ev = new CloudStreamEvent(Enum.Parse<CloudStreamEventKind>(kind), null, null, null);

        Assert.Equal(counts, ClaudeCloudStreamPolicy.CountsTowardHealth(ev));
    }

    // Names rather than the internal enum, which a public theory cannot take.
    public static class CloudStreamEventKindName
    {
        public const string Durable = "Durable", Ephemeral = "Ephemeral", Keepalive = "Keepalive",
            Session = "Session", Delivery = "Delivery", Other = "Other", Ended = "Ended";
    }

    // QA's storm: a server that answers each connection with a keepalive and a
    // session_update and then closes cleanly. Each one is backed off and
    // counted, and the run reaches the fallback.
    [Fact]
    public async Task KeepalivesThenACleanCloseBackOffAndCountTowardFallback()
    {
        const string body = ": keepalive\nevent: session_update\ndata: {\"connection_status\":\"connected\"}\n\n";
        var state = ClaudeCloudStreamPolicy.State.Initial;
        var waits = new List<TimeSpan?>();
        ClaudeCloudStreamPolicy.Decision d = default;

        for (var i = 0; i < ClaudeCloudStreamPolicy.UnmeasuredFailuresBeforeFallback; i++)
        {
            var events = await ReadAs("text/event-stream", new StringReader(body));
            var delivered = events.Any(ClaudeCloudStreamPolicy.CountsTowardHealth);
            Assert.False(delivered);

            d = ClaudeCloudStreamPolicy.Next(state, events[^1].Outcome!,
                ClaudeCloudStreamPolicy.WasHealthy(delivered, TimeSpan.FromMilliseconds(300)));
            waits.Add(d.Wait);
            state = d.Next;
        }

        Assert.DoesNotContain(ClaudeCloudStreamPolicy.UnmeasuredReconnectAfterEnd, waits);
        Assert.Equal(new TimeSpan?[] { Backoff.UnavailableFloor, Backoff.UnavailableFloor * 2,
            Backoff.UnavailableFloor * 4 }, waits);
        Assert.True(d.FallBackToPolling);
    }

    // A quiet session sends nothing but keepalives; a connection that held
    // for the healthy age was not being turned away.
    [Fact]
    public void ALongLivedKeepaliveOnlyConnectionIsHealthy()
    {
        var healthy = ClaudeCloudStreamPolicy.WasHealthy(false,
            ClaudeCloudStreamPolicy.UnmeasuredHealthyConnectionAge);
        var d = ClaudeCloudStreamPolicy.Next(new ClaudeCloudStreamPolicy.State(TimeSpan.FromSeconds(8), 2),
            new CloudOutcome(CloudOutcomeKind.Ok, 200), healthy);

        Assert.True(healthy);
        Assert.Equal(ClaudeCloudStreamPolicy.UnmeasuredReconnectAfterEnd, d.Wait);
        Assert.Equal(0, d.Next.ConsecutiveFailures);
        Assert.False(ClaudeCloudStreamPolicy.WasHealthy(false,
            ClaudeCloudStreamPolicy.UnmeasuredHealthyConnectionAge - TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public async Task ARealEventMakesAShortConnectionHealthy()
    {
        var events = await ReadAs("text/event-stream", new StringReader(
            ": keepalive\nevent: ephemeral_event\ndata: {\"payload\":{\"type\":\"stream_event\"}}\n\n"));
        var delivered = events.Any(ClaudeCloudStreamPolicy.CountsTowardHealth);

        Assert.True(delivered);
        Assert.True(ClaudeCloudStreamPolicy.WasHealthy(delivered, TimeSpan.FromMilliseconds(10)));
    }

    // --- the requests --------------------------------------------------------

    [Fact]
    public void TheStreamPathIsUnderTheEvents()
    {
        Assert.Equal("/v1/code/sessions/session_01StreamFixture/events/stream",
            ClaudeCloudStreamRequest.StreamPath(SessionId, null));
        Assert.Equal("/v1/code/sessions/session_01StreamFixture/events/stream?from_sequence_num=128",
            ClaudeCloudStreamRequest.StreamPath(SessionId, 128));
        Assert.Equal("/v1/code/sessions/session_01StreamFixture/events/stream?from_sequence_num=0",
            ClaudeCloudStreamRequest.StreamPath(SessionId, 0));
    }

    [Fact]
    public void ANegativeResumePointIsNoResumePoint()
    {
        Assert.Equal("/v1/code/sessions/session_01StreamFixture/events/stream",
            ClaudeCloudStreamRequest.StreamPath(SessionId, -1));

        using var request = ClaudeCloudStreamRequest.Build(Token, SessionId, -1)!;
        Assert.False(request.Headers.Contains("Last-Event-ID"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("session_a/../x")]
    [InlineData("session_a?x=1")]
    [InlineData("01ABC")]
    public void AMalformedIdGetsNoRequestAtAll(string? id)
    {
        Assert.Null(ClaudeCloudStreamRequest.StreamPath(id, 5));
        Assert.Null(ClaudeCloudStreamRequest.Build(Token, id, 5));
        Assert.Null(ClaudeCloudStreamRequest.NewestSequencePath(id));
    }

    // Measured headers: Bearer, anthropic-version, Accept text/event-stream,
    // and on a resume, Last-Event-ID with the same number as the query.
    [Fact]
    public void AResumingStreamRequestCarriesTheMeasuredHeaders()
    {
        using var request = ClaudeCloudStreamRequest.Build(Token, SessionId, 128)!;

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            "https://api.anthropic.com/v1/code/sessions/session_01StreamFixture/events/stream?from_sequence_num=128",
            request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, request.Headers.Authorization.Parameter);
        Assert.Equal("2023-06-01", string.Join(",", request.Headers.GetValues("anthropic-version")));
        Assert.Equal("text/event-stream", Assert.Single(request.Headers.Accept).MediaType);
        Assert.Equal("128", string.Join(",", request.Headers.GetValues("Last-Event-ID")));
        Assert.Null(request.Content);
        Assert.False(request.Headers.Contains("anthropic-beta"));
        Assert.False(request.Headers.Contains("x-organization-uuid"));
    }

    [Fact]
    public void AFreshStreamRequestHasNoLastEventId()
    {
        using var request = ClaudeCloudStreamRequest.Build(Token, SessionId, null)!;

        Assert.False(request.Headers.Contains("Last-Event-ID"));
        Assert.Equal("text/event-stream", Assert.Single(request.Headers.Accept).MediaType);
    }

    [Fact]
    public void AMalformedIdEndsAsGoneWithoutARequest()
    {
        var refused = ClaudeCloudStreamRequest.RefusedId;

        Assert.Equal(CloudOutcomeKind.SessionGone, refused.Kind);
        Assert.Equal(0, refused.Status);
        Assert.Contains("no request was made", refused.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNewestSequenceReadAsksForOneEventNewestFirst()
    {
        Assert.Equal("/v1/code/sessions/session_01StreamFixture/events?limit=1&sort_order=desc",
            ClaudeCloudStreamRequest.NewestSequencePath(SessionId));
    }

    [Theory]
    [InlineData("""{"data":[{"sequence_num":"128","type":"result"}],"has_more":true}""", 128L)]
    [InlineData("""{"data":[{"sequence_num":129}]}""", 129L)]
    [InlineData("""{"data":[]}""", null)]
    [InlineData("""{"data":[{"type":"result"}]}""", null)]
    [InlineData("""{"data":["x"]}""", null)]
    [InlineData("""{"data":{}}""", null)]
    [InlineData("""{}""", null)]
    [InlineData("""[]""", null)]
    [InlineData("not json", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheNewestSequenceIsReadOffTheFirstRow(string? body, long? expected)
    {
        Assert.Equal(expected, ClaudeCloudStreamRequest.ParseNewestSequence(body));
    }

    // --- reconnecting --------------------------------------------------------

    private static CloudOutcome Ok => new(CloudOutcomeKind.Ok, 200);

    [Fact]
    public void AHealthyStreamThatEndsReconnectsSoonAndResetsTheRun()
    {
        var d = ClaudeCloudStreamPolicy.Next(new ClaudeCloudStreamPolicy.State(TimeSpan.FromSeconds(30), 2),
            Ok, healthy: true);

        Assert.Equal(ClaudeCloudStreamPolicy.UnmeasuredReconnectAfterEnd, d.Wait);
        Assert.False(d.FallBackToPolling);
        Assert.Equal(0, d.Next.ConsecutiveFailures);
    }

    // A server closing every stream at once must not become a tight loop.
    [Fact]
    public void AStreamThatEndsWithNothingBacksOffLikeAFailure()
    {
        var state = ClaudeCloudStreamPolicy.State.Initial;
        var waits = new List<TimeSpan?>();
        var fallBack = new List<bool>();

        for (var i = 0; i < 4; i++)
        {
            var d = ClaudeCloudStreamPolicy.Next(state, Ok, healthy: false);
            waits.Add(d.Wait);
            fallBack.Add(d.FallBackToPolling);
            state = d.Next;
        }

        Assert.Equal(new TimeSpan?[]
        {
            Backoff.UnavailableFloor, Backoff.UnavailableFloor * 2,
            Backoff.UnavailableFloor * 4, Backoff.UnavailableFloor * 8,
        }, waits);
        Assert.Equal(new[] { false, false, true, true }, fallBack);
        Assert.Equal(4, state.ConsecutiveFailures);
    }

    [Fact]
    public void ATransportFailureBacksOffAndAHealthyOneResetsIt()
    {
        var unavailable = new CloudOutcome(CloudOutcomeKind.Unavailable, 0);

        var first = ClaudeCloudStreamPolicy.Next(ClaudeCloudStreamPolicy.State.Initial, unavailable, false);
        var second = ClaudeCloudStreamPolicy.Next(first.Next, unavailable, false);
        var afterHealthy = ClaudeCloudStreamPolicy.Next(second.Next, unavailable, healthy: true);

        Assert.Equal(Backoff.UnavailableFloor, first.Wait);
        Assert.Equal(Backoff.UnavailableFloor * 2, second.Wait);
        Assert.Equal(Backoff.UnavailableFloor, afterHealthy.Wait);
        Assert.Equal(1, afterHealthy.Next.ConsecutiveFailures);
    }

    [Fact]
    public void ARateLimitWaitsAtLeastTheFloor()
    {
        var d = ClaudeCloudStreamPolicy.Next(ClaudeCloudStreamPolicy.State.Initial,
            CloudOutcomes.OutcomeFor(429, null, TimeSpan.FromSeconds(5)), false);

        Assert.Equal(Backoff.RateLimitFloor, d.Wait);
    }

    // Stop, and fall back: nothing will make these answers change on a timer.
    [Theory]
    [InlineData(401, """{"error":{"message":"OAuth access token is invalid."}}""")]
    [InlineData(401, "")]
    [InlineData(403, "")]
    [InlineData(404, "")]
    [InlineData(409, "")]
    public void ADefiniteRefusalStops(int status, string body)
    {
        var d = ClaudeCloudStreamPolicy.Next(ClaudeCloudStreamPolicy.State.Initial,
            CloudOutcomes.OutcomeFor(status, body), false);

        Assert.Null(d.Wait);
        Assert.True(d.FallBackToPolling);
        Assert.Null(d.Next.LastWait);
        Assert.Equal(1, d.Next.ConsecutiveFailures);
    }

    [Fact]
    public void AMalformedIdStops()
    {
        Assert.Null(ClaudeCloudStreamPolicy.Next(ClaudeCloudStreamPolicy.State.Initial,
            ClaudeCloudStreamRequest.RefusedId, false).Wait);
    }

    [Fact]
    public void ABadRequestShapeBacksOffRatherThanStops()
    {
        Assert.Equal(Backoff.UnavailableFloor, ClaudeCloudStreamPolicy.Next(
            ClaudeCloudStreamPolicy.State.Initial, CloudOutcomes.OutcomeFor(400, null), false).Wait);
    }

    private static CloudStreamEvent Durable(long seq) =>
        new(CloudStreamEventKind.Durable, "client_event", seq, "{}");

    // Where a run starts. A panel that has handled something resumes from it,
    // so a turn that ran while the panel was hidden is replayed into it rather
    // than skipped; with nothing handled, or nothing older than the server's
    // newest to resume from, it starts from the newest as it always did.
    [Theory]
    [InlineData(2495L, 2530L, 2495L)]
    [InlineData(null, 2530L, 2530L)]
    [InlineData(2530L, 2530L, 2530L)]
    [InlineData(2600L, 2530L, 2530L)]
    [InlineData(2495L, null, null)]
    [InlineData(null, null, null)]
    public void ARunStartsFromWhatThePanelLastSawIfThereIsAnything(long? lastSeen, long? newest, long? expected)
    {
        Assert.Equal(expected, ClaudeCloudStreamPolicy.StartFrom(newest, lastSeen));
    }

    [Fact]
    public void OnlyANewerDurableEventMovesTheResumePoint()
    {
        Assert.Equal(5, ClaudeCloudStreamPolicy.ResumeFrom(null, Durable(5)));
        Assert.Equal(6, ClaudeCloudStreamPolicy.ResumeFrom(5, Durable(6)));
        Assert.Equal(6, ClaudeCloudStreamPolicy.ResumeFrom(6, Durable(4)));
        Assert.Equal(6, ClaudeCloudStreamPolicy.ResumeFrom(6, Durable(6)));
        Assert.Equal(6, ClaudeCloudStreamPolicy.ResumeFrom(6,
            new CloudStreamEvent(CloudStreamEventKind.Ephemeral, "ephemeral_event", 99, "{}")));
        Assert.Equal(6, ClaudeCloudStreamPolicy.ResumeFrom(6,
            new CloudStreamEvent(CloudStreamEventKind.Durable, "client_event", null, "{}")));
        Assert.Null(ClaudeCloudStreamPolicy.ResumeFrom(null,
            new CloudStreamEvent(CloudStreamEventKind.Keepalive, null, null, null)));
    }

    [Fact]
    public void ThePlaceholdersArePositive()
    {
        Assert.True(ClaudeCloudStreamPolicy.UnmeasuredReconnectAfterEnd > TimeSpan.Zero);
        Assert.True(ClaudeCloudStreamPolicy.UnmeasuredFailuresBeforeFallback > 0);
        Assert.Null(ClaudeCloudStreamPolicy.State.Initial.LastWait);
        Assert.Equal(0, ClaudeCloudStreamPolicy.State.Initial.ConsecutiveFailures);
    }

    // --- the canary ----------------------------------------------------------

    // The token reaches the Authorization header of the request and nothing
    // else: not the path, not any event, not any outcome's detail.
    [Fact]
    public async Task TheTokenReachesNothingButTheAuthorizationHeader()
    {
        using var request = ClaudeCloudStreamRequest.Build(Token, SessionId, 7)!;
        Assert.DoesNotContain(Token, request.RequestUri!.ToString(), StringComparison.Ordinal);
        foreach (var header in request.Headers.Where(h => h.Key != "Authorization"))
        {
            Assert.DoesNotContain(Token, string.Join(",", header.Value), StringComparison.Ordinal);
        }

        var events = await ReadAll(new StringReader(
            "event: client_event\nid: 1\ndata: " + UserEcho + "\n\n: ping\n"));
        foreach (var ev in events)
        {
            Assert.DoesNotContain(Token, ev.ToString(), StringComparison.Ordinal);
        }

        foreach (var status in new[] { 400, 401, 403, 404, 409, 429, 500 })
        {
            var detail = CloudOutcomes.OutcomeFor(status, "leaked " + Token).Detail ?? "";
            Assert.DoesNotContain(Token, detail, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(Token, ClaudeCloudStreamRequest.RefusedId.Detail!, StringComparison.Ordinal);
    }
}
