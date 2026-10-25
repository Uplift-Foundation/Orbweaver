using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// The send shared by the cloud panel and the Desktop-bridge send (CB-266): the
// credential read, the POST, the one retry and the words. Every case here goes
// through a fake ICloudApi and a fake credential source, so nothing reaches a
// network or a Keychain.
public class CloudTurnSenderTests
{
    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";
    private const string SessionId = "session_01FixtureOnly";

    private sealed class ScriptedApi : ICloudApi
    {
        private readonly Queue<CloudOutcome> _answers;

        internal ScriptedApi(params CloudOutcome[] answers) => _answers = new Queue<CloudOutcome>(answers);

        internal List<CloudRequestContext> Requests { get; } = new();

        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            Requests.Add(context);
            return Task.FromResult(new CloudApiResult(_answers.Dequeue(), null));
        }
    }

    private sealed class FakeCredentials : ICloudCredentialSource
    {
        internal CredentialRead Reading { get; set; } =
            new(CredentialOutcome.Found, Token, null, "a credential is present");

        internal int Reads { get; private set; }

        public string? Stamp() => "stamp-1";

        public CredentialRead Read()
        {
            Reads++;
            return Reading;
        }
    }

    private static CloudOutcome Ok => new(CloudOutcomeKind.Ok, 200);

    private static CloudOutcome Unavailable => new(CloudOutcomeKind.Unavailable, 503, null, "the endpoint was unavailable");

    private static readonly Func<TimeSpan, CancellationToken, Task> NoWait = (_, _) => Task.CompletedTask;

    private static Task<CloudSendAttempt> Send(
        ICloudApi api, ICloudCredentialSource credentials, string id = SessionId,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        CloudTurnSender.SendUserTurnAsync(api, credentials, TestBudgets.AlwaysAnswers, id, "hello there",
            delay ?? NoWait, CancellationToken.None);

    // The sender races the read against the budget it was handed and no other.
    [Fact]
    public async Task TheSendReadsThroughTheBudgetItWasHanded()
    {
        var (budget, calls) = TestBudgets.Spy();
        var credentials = new FakeCredentials();

        var attempt = await CloudTurnSender.SendUserTurnAsync(new ScriptedApi(Ok), credentials, budget,
            SessionId, "hello there", NoWait, CancellationToken.None);

        Assert.Equal(CloudSendResult.Sent, attempt.Result);
        Assert.Equal(1, credentials.Reads);
        Assert.Equal(1, calls());
    }

    [Fact]
    public async Task ATwoHundredIsSentAndCarriesTheUuidTheEventWentOutWith()
    {
        var api = new ScriptedApi(Ok);

        var attempt = await Send(api, new FakeCredentials());

        Assert.Equal(CloudSendResult.Sent, attempt.Result);
        Assert.Null(attempt.Note);
        Assert.Equal(CloudOutcomeKind.Ok, attempt.Outcome!.Kind);

        var request = Assert.Single(api.Requests);
        Assert.Equal(CloudRequest.CodeEventsPath(SessionId), request.Path);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(Token, request.AccessToken);

        // The uuid in the result is the one in the body, which is what lets a
        // caller recognise the echo.
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Contains(attempt.Uuid, body.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(ClaudeCloudSend.UserMessageBody(SessionId, "hello there", attempt.Uuid), request.Body);
    }

    [Fact]
    public async Task AMalformedIdIsRefusedBeforeAnyCredentialIsRead()
    {
        var api = new ScriptedApi();
        var credentials = new FakeCredentials();

        var attempt = await Send(api, credentials, id: "not-a-session-id");

        Assert.Equal(CloudSendResult.NotSent, attempt.Result);
        Assert.Contains("is not in a shape", attempt.Note);
        Assert.Null(attempt.Outcome);
        Assert.Equal(0, credentials.Reads);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task AMissingCredentialIsNotSentAndNothingIsPosted()
    {
        var api = new ScriptedApi();
        var credentials = new FakeCredentials
        {
            Reading = new CredentialRead(CredentialOutcome.NotLoggedIn, null, null, "none"),
        };

        var attempt = await Send(api, credentials);

        Assert.Equal(CloudSendResult.NotSent, attempt.Result);
        Assert.StartsWith("Not sent: ", attempt.Note);
        Assert.EndsWith(".", attempt.Note);
        Assert.Null(attempt.Outcome);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task UnavailableOnceIsRetriedWithTheSameUuidAfterTheFloor()
    {
        var api = new ScriptedApi(Unavailable, Ok);
        var waits = new List<TimeSpan>();

        var attempt = await Send(api, new FakeCredentials(), delay: (span, _) =>
        {
            waits.Add(span);
            return Task.CompletedTask;
        });

        Assert.Equal(CloudSendResult.Sent, attempt.Result);
        Assert.Equal(new[] { Backoff.UnavailableFloor }, waits);
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(api.Requests[0].Body, api.Requests[1].Body);
    }

    [Fact]
    public async Task UnavailableTwiceIsMaybeNotDeliveredRatherThanNotSent()
    {
        var api = new ScriptedApi(Unavailable, Unavailable);

        var attempt = await Send(api, new FakeCredentials());

        Assert.Equal(CloudSendResult.MaybeNotDelivered, attempt.Result);
        Assert.Equal(CloudTurnSender.MaybeNotDeliveredNote(Unavailable), attempt.Note);
        Assert.Contains("may not have arrived", attempt.Note);
        Assert.Equal(2, api.Requests.Count);
        Assert.NotEqual("", attempt.Uuid);
    }

    [Fact]
    public async Task ARefusalIsNotRetriedAndCarriesItsNote()
    {
        var refused = new CloudOutcome(CloudOutcomeKind.RateLimited, 429, null, "rate limited");
        var api = new ScriptedApi(refused);

        var attempt = await Send(api, new FakeCredentials());

        Assert.Equal(CloudSendResult.NotSent, attempt.Result);
        Assert.Equal(CloudTurnSender.NotSentNote(refused), attempt.Note);
        Assert.Equal(refused, attempt.Outcome);
        Assert.Single(api.Requests);
    }

    [Theory]
    [InlineData("TokenRefused", " Run `claude` in a terminal and sign in, then try again.")]
    [InlineData("AuthFailed", " Run `claude` in a terminal and sign in, then try again.")]
    [InlineData("RateLimited", " Wait a minute before trying again.")]
    [InlineData("TooLarge", " Try a shorter message.")]
    [InlineData("Blocked", "")]
    [InlineData("SessionGone", "")]
    public void AdviceIsPerKindAndEmptyWhereThereIsNone(string kindName, string advice) =>
        Assert.Equal(advice, CloudTurnSender.AdviceFor(System.Enum.Parse<CloudOutcomeKind>(kindName)));

    [Fact]
    public void TheNotSentNoteIsTheDetailThenTheAdvice()
    {
        var outcome = new CloudOutcome(CloudOutcomeKind.TooLarge, 413, null, "the message is too large");

        Assert.Equal("Not sent: the message is too large. Try a shorter message.",
            CloudTurnSender.NotSentNote(outcome));
    }
}
