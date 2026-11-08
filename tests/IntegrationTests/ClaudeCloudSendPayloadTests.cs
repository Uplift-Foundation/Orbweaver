using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// A write into a cloud session, end to end through the seam: the request the
// app builds, handed to an ICloudApi, and the answer read back through the real
// OutcomeFor and ParseSendResult.
//
// **These fixtures are hand-written, not captured.** CB-199's gate probe prints
// response *shapes* plus an allow-list of enum-valued fields and never a whole
// body, deliberately, so there is no captured body to paste. Each fixture below
// is written from that shape output — the fields, their JSON types and the
// allow-listed values the probe printed — with every id and number invented.
// Where a status was not measured at all (413) the fixture says so; 409 was
// measured later, by CB-225, and its fixture is a scrubbed capture.
//
// Here as well as in tests/UnitTests for the reason CLAUDE.md gives: the unit
// suite checks each rule; this checks that the rules compose into the exchange
// the endpoint actually had with the probe.
public class ClaudeCloudSendPayloadTests
{
    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";
    private const string SessionId = "session_01FixtureOnly";

    // A 200 to a fresh user turn, as the gate's `send` printed it.
    private const string SentBody =
        """{"attestation_feedback":null,"results":[{"duplicate":false,"event_id":"11111111-2222-4333-8444-555555555555","sequence_num":"20"}]}""";

    // The same uuid sent again: 200, `duplicate: true`, the same sequence_num.
    private const string DuplicateBody =
        """{"attestation_feedback":null,"results":[{"duplicate":true,"event_id":"11111111-2222-4333-8444-555555555555","sequence_num":"20"}]}""";

    // `{"events":[]}` → 400 "Request validation failed".
    private const string EmptyRefusedBody =
        """{"error":{"message":"Request validation failed","reason":"a reason","type":"invalid_request_error"},"request_id":"req_fixture_1","type":"error"}""";

    // No Authorization → 401 "Authentication failed"; bogus Bearer → 401
    // "OAuth access token is invalid." Both measured on this route.
    private const string NoAuthBody =
        """{"type":"error","error":{"type":"authentication_error","message":"Authentication failed"},"request_id":"req_fixture_2"}""";

    private const string BogusTokenBody =
        """{"type":"error","error":{"type":"authentication_error","message":"OAuth access token is invalid."},"request_id":null}""";

    // Missing anthropic-version → 400.
    private const string NoVersionBody =
        """{"error":{"message":"anthropic-version: header is required","reason":"a reason","type":"invalid_request_error"},"request_id":"req_fixture_3","type":"error"}""";

    // **Measured by CB-225**: a send to a session archived moments earlier.
    // Captured and scrubbed — the id is the fixture's, the request id invented.
    private const string InactiveBody =
        """{"error":{"message":"Session session_01FixtureOnly is not active","type":"session_not_active"},"request_id":"req_fixture_4","type":"error"}""";

    // **Measured**, on `GET /v1/code/sessions/{id}` for a deleted session. The
    // write to one is not measured and is assumed to answer the same, which
    // is why this fixture is run through the send exchange as well.
    private const string GoneBody =
        """{"type":"error","error":{"type":"not_found_error","message":"Session session_01FixtureOnly not found"},"request_id":"req_fixture_5"}""";

    // One status and body per call, recording what was asked, so the whole
    // exchange is checkable: where it went, with what, and what came back.
    private sealed class FixtureApi : ICloudApi
    {
        private readonly int _status;
        private readonly string? _body;

        internal FixtureApi(int status, string? body)
        {
            _status = status;
            _body = body;
        }

        internal CloudRequestContext? Last { get; private set; }

        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            Last = context;

            // Built exactly as HttpCloudApi builds it, so what this records is
            // what would have gone on the wire.
            using var request = CloudRequest.Build(context.AccessToken, context.Path,
                context.Method, context.Body);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);

            var outcome = CloudOutcomes.OutcomeFor(_status, _body);
            return Task.FromResult(new CloudApiResult(outcome,
                outcome.Kind == CloudOutcomeKind.Ok ? _body : null));
        }
    }

    private static async Task<(CloudApiResult Result, FixtureApi Api)> Send(int status, string? body,
        string text = "hello")
    {
        var api = new FixtureApi(status, body);
        var context = new CloudRequestContext(Token, CloudRequest.CodeEventsPath(SessionId)!,
            HttpMethod.Post, ClaudeCloudSend.UserMessageBody(SessionId, text, "uuid-fixture"));
        return (await api.SendAsync(context, CancellationToken.None), api);
    }

    [Fact]
    public async Task AnAcceptedTurnIsSentWithItsSequenceNumber()
    {
        var (result, api) = await Send(200, SentBody);

        Assert.Equal(CloudOutcomeKind.Ok, result.Outcome.Kind);
        var sent = ClaudeCloudSend.ParseSendResult(result.Body);
        Assert.Equal(ClaudeCloudSend.SendResultKind.Sent, sent.Kind);
        Assert.Equal("20", sent.SequenceNum);
        Assert.Equal("/v1/code/sessions/session_01FixtureOnly/events", api.Last!.Value.Path);
    }

    [Fact]
    public async Task AResentTurnIsADuplicate()
    {
        var (result, _) = await Send(200, DuplicateBody);

        var sent = ClaudeCloudSend.ParseSendResult(result.Body);
        Assert.Equal(ClaudeCloudSend.SendResultKind.Duplicate, sent.Kind);
        Assert.Equal("20", sent.SequenceNum);
    }

    [Theory]
    [InlineData(400, EmptyRefusedBody, "ShapeChanged")]
    [InlineData(400, NoVersionBody, "ShapeChanged")]
    [InlineData(401, NoAuthBody, "AuthFailed")]
    [InlineData(401, BogusTokenBody, "TokenRefused")]
    [InlineData(404, GoneBody, "SessionGone")]
    [InlineData(409, InactiveBody, "SessionInactive")]
    [InlineData(413, "", "TooLarge")]
    public async Task EachRefusalIsItsOwnVerdictAndCarriesNoBody(int status, string body,
        string kind)
    {
        var (result, _) = await Send(status, body);

        // By name: the enum is internal and a public theory cannot take it.
        Assert.Equal(kind, result.Outcome.Kind.ToString());
        Assert.Null(result.Body);
        Assert.False(string.IsNullOrWhiteSpace(result.Outcome.Detail));
    }

    // The canary, across the whole exchange: the token is in the context's
    // AccessToken and nowhere else — not in the body that went out, not in the
    // path, not in any verdict's detail.
    [Fact]
    public async Task TheTokenReachesNothingButTheAuthorizationHeader()
    {
        foreach (var (status, body) in new[]
                 {
                     (200, SentBody), (400, EmptyRefusedBody), (401, BogusTokenBody),
                     (403, "<html></html>"), (404, GoneBody), (409, InactiveBody), (413, ""), (429, ""), (500, ""),
                 })
        {
            var (result, api) = await Send(status, body);

            Assert.DoesNotContain(Token, api.Last!.Value.Body!, StringComparison.Ordinal);
            Assert.DoesNotContain(Token, api.Last.Value.Path, StringComparison.Ordinal);
            Assert.DoesNotContain(Token, result.Outcome.Detail ?? "", StringComparison.Ordinal);
        }
    }

    // CB-306: the control requests a cloud panel's slash commands post, captured
    // as they would go on the wire (FixtureApi builds the HttpRequestMessage
    // exactly as HttpCloudApi does) and run through the shared poster.
    [Theory]
    [InlineData("/rename my session", "rename_session", "title", "my session")]
    [InlineData("/model opus", "set_model", "model", "opus")]
    [InlineData("/permissions plan", "set_permission_mode", "mode", "plan")]
    public async Task ASlashCommandGoesOutAsAControlRequestOnTheEventsPath(
        string line, string subtype, string field, string value)
    {
        var api = new FixtureApi(200, SentBody);
        var (name, args) = SlashRouting.Split(line)!.Value;
        var route = SlashRouting.Resolve(SlashChannel.Cloud, name, args);
        var request = SlashRouting.ControlRequest(route.ControlSubtype!, args, out _)!;

        var failure = await CloudTurnSender.PostControlAsync(api, new Credentials(),
            new CredentialBudget(TimeSpan.FromSeconds(5), (_, ct) => Task.Delay(Timeout.Infinite, ct)),
            SessionId, request, CancellationToken.None);

        Assert.Null(failure);
        Assert.Equal("/v1/code/sessions/session_01FixtureOnly/events", api.Last!.Value.Path);
        Assert.DoesNotContain(Token, api.Last!.Value.Body, StringComparison.Ordinal);
        using var doc = System.Text.Json.JsonDocument.Parse(api.Last!.Value.Body!);
        var payload = doc.RootElement.GetProperty("events")[0].GetProperty("payload");
        Assert.Equal("control_request", payload.GetProperty("type").GetString());
        Assert.Equal(subtype, payload.GetProperty("request").GetProperty("subtype").GetString());
        Assert.Equal(value, payload.GetProperty("request").GetProperty(field).GetString());
    }

    private sealed class Credentials : ICloudCredentialSource
    {
        public string? Stamp() => "stamp-1";
        public CredentialRead Read() => new(CredentialOutcome.Found, Token, null, "a credential is present");
    }
}
