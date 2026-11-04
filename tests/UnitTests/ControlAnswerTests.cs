using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// CB-306's product-readiness round: a control request's 2xx says only that the
// event was stored. The CLI's own answer — success, or an error with its
// sentence — is read back off the session's events, and a rejection is worded
// with the session's Claude Code version beside the one the requests were
// measured on. Fakes only: no network, no Keychain.
public class ControlAnswerTests
{
    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";
    private const string SessionId = "session_01FixtureOnly";

    // Real output, captured 2026-11-03 from a throwaway `claude --bg
    // --remote-control` job on Claude Code 2.1.318 with
    // `GET /v1/code/sessions/<bridge>/events?limit=20&sort_order=desc`, after
    // three control requests: set_color "not-a-colour", set_color "default",
    // and a subtype that does not exist. Ids, uuids and the session id are
    // replaced; every other byte of these rows is as it came back. Newest
    // first, as the endpoint returned it.
    private const string CapturedPage = """
        {"data":[
        {"event_id":"evt-30","event_type":"control_response","sequence_num":"30","source":"worker","created_at":"2026-11-04T03:55:29.119341Z","payload":{"response":{"error":"REPL bridge does not handle control_request subtype: cb306_no_such_subtype","request_id":"00000000-0000-4000-8000-000000000002","subtype":"error"},"session_id":"session_01FixtureOnly","type":"control_response","uuid":"00000000-0000-4000-8000-000000000001"}},
        {"event_id":"evt-29","event_type":"control_request","sequence_num":"29","source":"client","created_at":"2026-11-04T03:55:28.899642Z","payload":{"request":{"subtype":"cb306_no_such_subtype"},"request_id":"00000000-0000-4000-8000-000000000002","type":"control_request","uuid":"00000000-0000-4000-8000-000000000003"}},
        {"event_id":"evt-28","event_type":"control_response","sequence_num":"28","source":"worker","created_at":"2026-11-04T03:55:27.415297Z","payload":{"response":{"request_id":"00000000-0000-4000-8000-000000000005","subtype":"success"},"session_id":"session_01FixtureOnly","type":"control_response","uuid":"00000000-0000-4000-8000-000000000004"}},
        {"event_id":"evt-27","event_type":"control_request","sequence_num":"27","source":"client","created_at":"2026-11-04T03:55:27.255859Z","payload":{"request":{"color":"default","subtype":"set_color"},"request_id":"00000000-0000-4000-8000-000000000005","type":"control_request","uuid":"00000000-0000-4000-8000-000000000006"}},
        {"event_id":"evt-26","event_type":"control_response","sequence_num":"26","source":"worker","created_at":"2026-11-04T03:55:26.210275Z","payload":{"response":{"error":"Unknown color \"not-a-colour\". Available: red, blue, green, yellow, purple, orange, pink, cyan, default","request_id":"00000000-0000-4000-8000-000000000008","subtype":"error"},"session_id":"session_01FixtureOnly","type":"control_response","uuid":"00000000-0000-4000-8000-000000000007"}},
        {"event_id":"evt-25","event_type":"control_request","sequence_num":"25","source":"client","created_at":"2026-11-04T03:55:26.036037Z","payload":{"request":{"color":"not-a-colour","subtype":"set_color"},"request_id":"00000000-0000-4000-8000-000000000008","type":"control_request","uuid":"00000000-0000-4000-8000-000000000009"}}
        ],"next_cursor":"c","resume_cursor":"r"}
        """;

    // --- ControlAnswerIn, against the capture ---------------------------------

    [Fact]
    public void TheCapturedSuccessReadsAsAnAnswerWithNoError()
    {
        var answer = CloudTurnSender.ControlAnswerIn(CapturedPage, "00000000-0000-4000-8000-000000000005");

        Assert.NotNull(answer);
        Assert.Null(answer!.Value.Error);
    }

    [Theory]
    [InlineData("00000000-0000-4000-8000-000000000008", "Unknown color \"not-a-colour\". Available: red, blue, green, yellow, purple, orange, pink, cyan, default")]
    [InlineData("00000000-0000-4000-8000-000000000002", "REPL bridge does not handle control_request subtype: cb306_no_such_subtype")]
    public void TheCapturedErrorsReadAsTheClisOwnSentence(string requestId, string error)
    {
        Assert.Equal(error, CloudTurnSender.ControlAnswerIn(CapturedPage, requestId)!.Value.Error);
    }

    // The negative control: the request rows carry the same request_id, and
    // an id nobody answered finds nothing, so a match is the response row.
    [Fact]
    public void ARequestNobodyAnsweredFindsNothingEvenBesideItsOwnRequestRow()
    {
        Assert.Null(CloudTurnSender.ControlAnswerIn(CapturedPage, "00000000-0000-4000-8000-000000000003"));
        Assert.Null(CloudTurnSender.ControlAnswerIn(CapturedPage, "not-an-id-anybody-sent"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{\"data\":[null,1,{\"payload\":7},{\"payload\":{\"type\":\"control_response\",\"response\":\"x\"}}]}")]
    public void AnythingThatIsNotAPageWithAnAnswerFindsNothing(string? body)
    {
        Assert.Null(CloudTurnSender.ControlAnswerIn(body, "r1"));
    }

    [Fact]
    public void AnErrorWithNoSentenceGetsAStandIn()
    {
        var body = Page(("r1", "error", null));

        Assert.Equal("it gave no reason", CloudTurnSender.ControlAnswerIn(body, "r1")!.Value.Error);
    }

    [Fact]
    public void AVeryLongErrorIsCapped()
    {
        var body = Page(("r1", "error", new string('x', 1000)));

        var error = CloudTurnSender.ControlAnswerIn(body, "r1")!.Value.Error!;

        Assert.Equal(CloudTurnSender.MaxErrorLength + 1, error.Length);
        Assert.EndsWith("…", error);
    }

    // --- RejectionClause -----------------------------------------------------

    [Fact]
    public void TheRejectionNamesBothVersionsWhenTheRowHasOne()
    {
        Assert.Equal(
            "Claude Code refused it: no. This session runs Claude Code 2.1.318; Orbweaver's commands were measured on 2.1.317",
            CloudTurnSender.RejectionClause("no.", " 2.1.318 "));
        Assert.Equal("2.1.317", SlashRouting.MeasuredOnClaudeCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void TheVersionSentenceIsLeftOutWhenTheVersionIsUnknown(string? version)
    {
        Assert.Equal("Claude Code refused it: no", CloudTurnSender.RejectionClause("no", version));
    }

    // --- PostControlAndReadAnswerAsync ---------------------------------------

    // Answers each request with a function of the POST's request_id, so a
    // page can carry the answer to exactly the request that was sent.
    private sealed class Api : ICloudApi
    {
        internal List<CloudRequestContext> Requests { get; } = new();
        internal CloudOutcome PostOutcome { get; set; } = new(CloudOutcomeKind.Ok, 200);
        internal Func<int, string, CloudApiResult> Page { get; set; } =
            (_, _) => new CloudApiResult(new CloudOutcome(CloudOutcomeKind.Ok, 200), "{\"data\":[]}");

        private string _requestId = "";
        private int _gets;

        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            Requests.Add(context);
            if (context.Method == HttpMethod.Post)
            {
                using var doc = JsonDocument.Parse(context.Body!);
                _requestId = doc.RootElement.GetProperty("events")[0].GetProperty("payload")
                    .GetProperty("request_id").GetString()!;
                return Task.FromResult(new CloudApiResult(PostOutcome, null));
            }
            return Task.FromResult(Page(_gets++, _requestId));
        }
    }

    private sealed class Credentials : ICloudCredentialSource
    {
        internal CredentialRead Reading { get; set; } = new(CredentialOutcome.Found, Token, null, "present");
        public string? Stamp() => "stamp-1";
        public CredentialRead Read() => Reading;
    }

    private static string Page(params (string RequestId, string Subtype, string? Error)[] answers) =>
        new JsonObject
        {
            ["data"] = new JsonArray(answers.Select(a => (JsonNode)new JsonObject
            {
                ["payload"] = new JsonObject
                {
                    ["type"] = "control_response",
                    ["response"] = a.Error is null
                        ? new JsonObject { ["request_id"] = a.RequestId, ["subtype"] = a.Subtype }
                        : new JsonObject { ["request_id"] = a.RequestId, ["subtype"] = a.Subtype, ["error"] = a.Error },
                },
            }).ToArray()),
        }.ToJsonString();

    private static CloudApiResult OkPage(string body) => new(new CloudOutcome(CloudOutcomeKind.Ok, 200), body);

    private static async Task<(CloudTurnSender.ControlAttempt Attempt, List<TimeSpan> Waits)> Run(
        Api api, Credentials? credentials = null, string id = SessionId)
    {
        var waits = new List<TimeSpan>();
        var attempt = await CloudTurnSender.PostControlAndReadAnswerAsync(api, credentials ?? new Credentials(),
            TestBudgets.AlwaysAnswers, id, new JsonObject { ["subtype"] = "set_color", ["color"] = "teal" },
            (span, _) => { waits.Add(span); return Task.CompletedTask; }, CancellationToken.None);
        return (attempt, waits);
    }

    [Fact]
    public async Task ASuccessAnswerIsReadBackAndTheCommandRan()
    {
        var api = new Api { Page = (_, rid) => OkPage(Page(("someone-else", "error", "nope"), (rid, "success", null))) };

        var (attempt, waits) = await Run(api);

        Assert.Equal(new CloudTurnSender.ControlAttempt(null, null, Confirmed: true), attempt);
        Assert.Equal(2, api.Requests.Count);
        var get = api.Requests[1];
        Assert.Null(get.Method);
        Assert.Equal(CloudRequest.CodeEventsPath(SessionId) + "?limit=20&sort_order=desc", get.Path);
        Assert.Equal(Token, get.AccessToken);
        Assert.Equal(new[] { CloudTurnSender.AnswerPollInterval }, waits);
    }

    [Fact]
    public async Task AnErrorAnswerOnALaterPageIsTheClisRejection()
    {
        var api = new Api
        {
            Page = (n, rid) => OkPage(n == 0 ? Page() : Page((rid, "error", "Unknown color \"teal\". Available: red"))),
        };

        var (attempt, waits) = await Run(api);

        Assert.Null(attempt.Failure);
        Assert.Equal("Unknown color \"teal\". Available: red", attempt.CliError);
        Assert.False(attempt.Confirmed);
        Assert.Equal(2, waits.Count);
    }

    // No answer within the polls is not a failure — the command was
    // delivered — but it is not confirmed either. Ten reads, 500 ms apart:
    // about five seconds, so a session mid-turn still gets to answer.
    [Fact]
    public async Task NoAnswerWithinThePollsIsDeliveredAndUnconfirmed()
    {
        var api = new Api();

        var (attempt, waits) = await Run(api);

        Assert.Equal(new CloudTurnSender.ControlAttempt(null, null), attempt);
        Assert.Equal(1 + CloudTurnSender.AnswerPolls, api.Requests.Count);
        Assert.Equal(CloudTurnSender.AnswerPolls, waits.Count);
        Assert.Equal(10, CloudTurnSender.AnswerPolls);
        Assert.Equal(TimeSpan.FromSeconds(5), CloudTurnSender.AnswerPollInterval * CloudTurnSender.AnswerPolls);
    }

    [Fact]
    public async Task AReadThatFailsStopsPollingAndIsNotAFailure()
    {
        var api = new Api { Page = (_, _) => new CloudApiResult(new CloudOutcome(CloudOutcomeKind.Unavailable, 503, null, "down"), null) };

        var (attempt, _) = await Run(api);

        Assert.Equal(new CloudTurnSender.ControlAttempt(null, null), attempt);
        Assert.Equal(2, api.Requests.Count);
    }

    [Fact]
    public async Task APostThatFailsReadsNothingBack()
    {
        var api = new Api { PostOutcome = new CloudOutcome(CloudOutcomeKind.Unavailable, 503, null, "the endpoint was unavailable") };

        var (attempt, waits) = await Run(api);

        Assert.Equal("the endpoint was unavailable", attempt.Failure);
        Assert.Null(attempt.CliError);
        Assert.Single(api.Requests);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task NoLoginPostsNothingAndReadsNothing()
    {
        var api = new Api();
        var credentials = new Credentials { Reading = new CredentialRead(CredentialOutcome.Denied, null, null, "denied") };

        var (attempt, _) = await Run(api, credentials);

        Assert.Equal(ClaudeCliCredentials.Describe(CredentialOutcome.Denied), attempt.Failure);
        Assert.Empty(api.Requests);
    }
}
