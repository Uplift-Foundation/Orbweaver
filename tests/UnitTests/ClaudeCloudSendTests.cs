using System;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// Covers the two bodies a write into a cloud session carries, and reading the
// answer to one.
//
// The body shapes are the ones CB-199's gate sent to
// `POST /v1/code/sessions/{id}/events` and saw accepted with a 200, so these
// tests pin them field by field: a body that drifted from what was measured is
// a request nobody has seen succeed.
public class ClaudeCloudSendTests
{
    private const string SessionId = "session_01ABCdef";

    private static JsonElement Payload(string body)
    {
        var doc = JsonDocument.Parse(body);
        var events = doc.RootElement.GetProperty("events");
        Assert.Equal(1, events.GetArrayLength());
        return events[0].GetProperty("payload").Clone();
    }

    // --- the bodies ----------------------------------------------------------

    [Fact]
    public void AUserTurnIsTheMeasuredShape()
    {
        var payload = Payload(ClaudeCloudSend.UserMessageBody(SessionId, "hello there", "uuid-1"));

        Assert.Equal("uuid-1", payload.GetProperty("uuid").GetString());
        Assert.Equal(SessionId, payload.GetProperty("session_id").GetString());
        Assert.Equal("user", payload.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("parent_tool_use_id").ValueKind);

        var message = payload.GetProperty("message");
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("hello there", message.GetProperty("content").GetString());
    }

    // Built by the serialiser rather than by concatenation, so text that would
    // break a hand-assembled body round-trips exactly.
    [Theory]
    [InlineData("a \"quoted\" word")]
    [InlineData("two\nlines")]
    [InlineData("a } brace and a { brace")]
    [InlineData("back\\slash")]
    [InlineData("emoji 🎉 and ünïcödé")]
    public void AnyTextRoundTripsThroughTheBody(string text)
    {
        var payload = Payload(ClaudeCloudSend.UserMessageBody(SessionId, text, "u"));

        Assert.Equal(text, payload.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public void AnInterruptIsTheMeasuredShape()
    {
        var payload = Payload(ClaudeCloudSend.InterruptBody("req-1", "uuid-2"));

        Assert.Equal("control_request", payload.GetProperty("type").GetString());
        Assert.Equal("req-1", payload.GetProperty("request_id").GetString());
        Assert.Equal("uuid-2", payload.GetProperty("uuid").GetString());

        var request = payload.GetProperty("request");
        Assert.Equal("interrupt", request.GetProperty("subtype").GetString());
        Assert.True(request.GetProperty("cancel_queued").GetBoolean());
    }

    // CB-306: any control request rides the interrupt's envelope with the
    // request object supplied. Measured on a bridge and a cloud session on
    // 2026-11-03: `set_color` with a colour field answered success on the
    // bridge and "Unsupported control request subtype" on the cloud worker,
    // which is the kind of difference SlashRoute's table owns, not this envelope.
    [Fact]
    public void AControlRequestCarriesTheGivenRequestObject()
    {
        var body = ClaudeCloudSend.ControlRequestBody("req-7", "uuid-8",
            new System.Text.Json.Nodes.JsonObject { ["subtype"] = "set_color", ["color"] = "pink" });
        var payload = Payload(body);

        Assert.Equal("control_request", payload.GetProperty("type").GetString());
        Assert.Equal("req-7", payload.GetProperty("request_id").GetString());
        Assert.Equal("uuid-8", payload.GetProperty("uuid").GetString());
        Assert.Equal("set_color", payload.GetProperty("request").GetProperty("subtype").GetString());
        Assert.Equal("pink", payload.GetProperty("request").GetProperty("color").GetString());
    }

    // The interrupt is the same bytes it was before it was built through the
    // general envelope, so the one body CB-199 measured is unchanged.
    [Fact]
    public void TheInterruptIsTheGeneralEnvelopeWithItsOwnRequest()
    {
        var viaGeneral = ClaudeCloudSend.ControlRequestBody("r", "u",
            new System.Text.Json.Nodes.JsonObject { ["subtype"] = "interrupt", ["cancel_queued"] = true });

        Assert.Equal(viaGeneral, ClaudeCloudSend.InterruptBody("r", "u"));
    }

    // --- the answer ----------------------------------------------------------

    // Measured: `sequence_num` is a JSON *string*.
    [Fact]
    public void AFreshEventIsSentWithItsSequenceNumber()
    {
        var result = ClaudeCloudSend.ParseSendResult(
            """{"results":[{"duplicate":false,"sequence_num":"20","event_id":"uuid-1"}]}""");

        Assert.Equal(ClaudeCloudSend.SendResultKind.Sent, result.Kind);
        Assert.Equal("20", result.SequenceNum);
    }

    // Measured: the same uuid twice is 200 both times, the second duplicate.
    [Fact]
    public void AResentEventIsADuplicateWithTheSameSequenceNumber()
    {
        var result = ClaudeCloudSend.ParseSendResult(
            """{"results":[{"duplicate":true,"sequence_num":"20","event_id":"uuid-1"}]}""");

        Assert.Equal(ClaudeCloudSend.SendResultKind.Duplicate, result.Kind);
        Assert.Equal("20", result.SequenceNum);
    }

    // Not measured, but the most plausible drift, so it is read rather than
    // refused: an ordering token is an ordering token in either type.
    [Fact]
    public void ANumericSequenceNumberIsReadToo()
    {
        var result = ClaudeCloudSend.ParseSendResult(
            """{"results":[{"duplicate":false,"sequence_num":21}]}""");

        Assert.Equal(ClaudeCloudSend.SendResultKind.Sent, result.Kind);
        Assert.Equal("21", result.SequenceNum);
    }

    [Fact]
    public void AMissingDuplicateFlagWithASequenceNumberIsSent()
    {
        var result = ClaudeCloudSend.ParseSendResult("""{"results":[{"sequence_num":"3"}]}""");

        Assert.Equal(ClaudeCloudSend.SendResultKind.Sent, result.Kind);
    }

    // Every body that is not a receipt. None of these is a failure — the status
    // was already 2xx — they are an answer this version cannot read.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"results":null}""")]
    [InlineData("""{"results":{}}""")]
    [InlineData("""{"results":[]}""")]
    [InlineData("""{"results":["x"]}""")]
    [InlineData("""{"results":[{"duplicate":false}]}""")]
    [InlineData("""{"results":[{"duplicate":false,"sequence_num":""}]}""")]
    [InlineData("""{"results":[{"duplicate":false,"sequence_num":null}]}""")]
    [InlineData("""{"results":[{"duplicate":false,"sequence_num":true}]}""")]
    public void AnythingElseIsUnparseable(string? body)
    {
        var result = ClaudeCloudSend.ParseSendResult(body);

        Assert.Equal(ClaudeCloudSend.SendResultKind.Unparseable, result.Kind);
        Assert.Null(result.SequenceNum);
    }

    // A duplicate is a duplicate even without a sequence number: the flag is
    // the endpoint saying it already had the event, which is the whole answer.
    [Fact]
    public void ADuplicateWithoutASequenceNumberIsStillADuplicate()
    {
        var result = ClaudeCloudSend.ParseSendResult("""{"results":[{"duplicate":true}]}""");

        Assert.Equal(ClaudeCloudSend.SendResultKind.Duplicate, result.Kind);
        Assert.Null(result.SequenceNum);
    }
}
