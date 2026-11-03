using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-170: the pure half of an OpenClaw orb's Interrupt and End rows — what the
// gateway's answers mean, what the rows say about them, and when each row is
// offered at all. The requests themselves are OpenClawOrbActionRequestTests,
// over an in-memory socket.
//
// The answer shapes are the ones measured against a real gateway (OpenClaw
// 2026.9.2) and recorded on CB-170: chat.abort answers {ok, aborted, runIds},
// sessions.patch answers {ok, key, entry}, and a refusal is an error whose
// message is the gateway's own sentence.
public class OpenClawOrbActionsTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // --- chat.abort's answer ---

    [Fact]
    public void AnAbortThatStoppedSomethingIsDone()
    {
        var (outcome, detail) = OpenClawOrbActions.ParseAbortResult(
            Json("""{"ok":true,"aborted":true,"runIds":["r1"]}"""));

        Assert.Equal(OpenClawActionOutcome.Done, outcome);
        Assert.Null(detail);
    }

    // Measured: an idle session answers ok with aborted:false. That is the
    // gateway saying there was nothing to stop, not a failure.
    [Fact]
    public void AnAbortWithNothingToStopIsNothingRunning()
    {
        var (outcome, detail) = OpenClawOrbActions.ParseAbortResult(
            Json("""{"ok":true,"aborted":false,"runIds":[]}"""));

        Assert.Equal(OpenClawActionOutcome.NothingRunning, outcome);
        Assert.Null(detail);
    }

    // Neither "interrupted" nor "nothing running" is claimed from a reply that
    // does not say which.
    [Theory]
    [InlineData("""{"ok":true}""")]
    [InlineData("""{"ok":true,"aborted":"yes"}""")]
    [InlineData("[]")]
    public void AnAbortReplyThatDoesNotSayIsNotAResult(string json)
    {
        var (outcome, detail) = OpenClawOrbActions.ParseAbortResult(Json(json));

        Assert.Equal(OpenClawActionOutcome.Refused, outcome);
        Assert.Equal("the gateway didn't say whether anything stopped", detail);
    }

    // --- sessions.patch's answer ---

    [Fact]
    public void AConfirmedArchiveIsDone()
    {
        var (outcome, detail) = OpenClawOrbActions.ParsePatchResult(
            Json("""{"ok":true,"key":"agent:main:dashboard:abc","entry":{"archivedAt":1}}"""));

        Assert.Equal(OpenClawActionOutcome.Done, outcome);
        Assert.Null(detail);
    }

    [Theory]
    [InlineData("""{"ok":false}""")]
    [InlineData("""{"key":"agent:main:dashboard:abc"}""")]
    [InlineData("null")]
    public void AnArchiveReplyWithoutOkIsNotAnArchive(string json)
    {
        var (outcome, detail) = OpenClawOrbActions.ParsePatchResult(Json(json));

        Assert.Equal(OpenClawActionOutcome.Refused, outcome);
        Assert.Equal("the gateway didn't confirm the archive", detail);
    }

    // --- a request that threw ---

    // The gateway's whole message for a run this device did not start.
    [Fact]
    public void UnauthorizedIsARunFromAnotherDevice()
    {
        var (outcome, detail) = OpenClawOrbActions.ClassifyFailure(
            new OpenClawRequestException("unauthorized", null) { Code = "INVALID_REQUEST" });

        Assert.Equal(OpenClawActionOutcome.OtherDevice, outcome);
        Assert.Null(detail);
    }

    // Everything else is the gateway's sentence, verbatim — including one that
    // merely contains the word.
    [Theory]
    [InlineData("missing scope: operator.write")]
    [InlineData("Cannot archive an agent's main session.")]
    [InlineData("unauthorized: device revoked")]
    public void AnyOtherRefusalIsShownAsTheGatewayWordedIt(string message)
    {
        var (outcome, detail) = OpenClawOrbActions.ClassifyFailure(
            new OpenClawRequestException(message, null));

        Assert.Equal(OpenClawActionOutcome.Refused, outcome);
        Assert.Equal(message, detail);
    }

    // Transport failures are not gateway refusals, but they are still shown
    // rather than swallowed.
    [Fact]
    public void ATransportFailureIsShownByItsMessage()
    {
        var (outcome, detail) = OpenClawOrbActions.ClassifyFailure(
            new TimeoutException("the gateway didn't answer"));

        Assert.Equal(OpenClawActionOutcome.Refused, outcome);
        Assert.Equal("the gateway didn't answer", detail);
    }

    [Fact]
    public void OnlyAnUnavailableRefusalIsWorthAskingAgain()
    {
        Assert.True(OpenClawOrbActions.IsRetryable(
            new OpenClawRequestException("Session x is still active; retry the archive.", null)
            {
                Code = "UNAVAILABLE"
            }));

        Assert.False(OpenClawOrbActions.IsRetryable(
            new OpenClawRequestException("missing scope: operator.write", null) { Code = "INVALID_REQUEST" }));
        Assert.False(OpenClawOrbActions.IsRetryable(new OpenClawRequestException("no code", null)));
        Assert.False(OpenClawOrbActions.IsRetryable(new InvalidOperationException("UNAVAILABLE")));
    }

    // --- the wording ---

    [Fact]
    public void TheRowsSayWhatTheyDoToTheConversation()
    {
        Assert.Equal("Interrupt the current run", OpenClawActionText.Header(OpenClawAction.Interrupt));
        Assert.Equal("End the conversation", OpenClawActionText.Header(OpenClawAction.End));
        Assert.Equal("End it? Click again (can be restored on the gateway)", OpenClawActionText.Armed);
        Assert.Equal("Interrupting…", OpenClawActionText.Working(OpenClawAction.Interrupt));
        Assert.Equal("Ending…", OpenClawActionText.Working(OpenClawAction.End));
        Assert.Equal("Stops the agent generating. The conversation stays.",
            OpenClawActionText.Tip(OpenClawAction.Interrupt));
        Assert.Contains("It can be restored from OpenClaw.", OpenClawActionText.Tip(OpenClawAction.End));
    }

    [Theory]
    [InlineData(OpenClawActionOutcome.Done, OpenClawAction.Interrupt, "Interrupted")]
    [InlineData(OpenClawActionOutcome.Done, OpenClawAction.End, "Ended")]
    [InlineData(OpenClawActionOutcome.NothingRunning, OpenClawAction.Interrupt, "Nothing was running")]
    [InlineData(OpenClawActionOutcome.OtherDevice, OpenClawAction.Interrupt, "Started from another device")]
    [InlineData(OpenClawActionOutcome.OtherDevice, OpenClawAction.End,
        "Couldn't end: a run started from another device is still going")]
    [InlineData(OpenClawActionOutcome.NotConnected, OpenClawAction.Interrupt,
        "Couldn't interrupt: not connected to the gateway")]
    [InlineData(OpenClawActionOutcome.NotConnected, OpenClawAction.End,
        "Couldn't end: not connected to the gateway")]
    [InlineData(OpenClawActionOutcome.Refused, OpenClawAction.Interrupt,
        "Couldn't interrupt: missing scope: operator.write")]
    [InlineData(OpenClawActionOutcome.Refused, OpenClawAction.End,
        "Couldn't end: missing scope: operator.write")]
    public void EachOutcomeHasItsSentence(OpenClawActionOutcome outcome, OpenClawAction action, string expected)
    {
        Assert.Equal(expected, OpenClawActionText.For(outcome, "missing scope: operator.write", action));
    }

    // --- hello-ok's method list ---

    [Fact]
    public void TheMethodListIsReadOffHelloOk()
    {
        var methods = OpenClawGateway.ParseFeatures(Json(
            """{"features":{"methods":["chat.abort","sessions.patch",7,"",null,"chat.abort"],"events":[]}}"""));

        Assert.Equal(new[] { "chat.abort", "sessions.patch" }, methods.OrderBy(m => m).ToArray());
    }

    // Anything missing or malformed offers nothing.
    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"features":[]}""")]
    [InlineData("""{"features":{}}""")]
    [InlineData("""{"features":{"methods":"chat.abort"}}""")]
    [InlineData("[]")]
    public void NoMethodListIsNoMethods(string json)
    {
        Assert.Empty(OpenClawGateway.ParseFeatures(Json(json)));
    }

    // --- when each row is offered ---

    private static readonly string[] BothMethods = { "chat.abort", "sessions.patch" };

    private static OpenClawActionContext Context(
        bool connected = true,
        string[]? methods = null,
        string[]? scopes = null,
        bool isMain = false,
        string? sessionId = "sid-1",
        string? key = "agent:main:dashboard:abc") =>
        new(connected,
            new HashSet<string>(methods ?? BothMethods),
            scopes ?? new[] { "operator.read", "operator.write" },
            isMain, sessionId, key);

    private static SessionStatus Status(SessionSource source = SessionSource.OpenClaw, bool room = false) =>
        new() { Source = source, IsRoom = room };

    [Fact]
    public void AConnectedWriteScopedOpenClawOrbOffersBoth()
    {
        Assert.True(SessionPresence.CanInterruptOpenClaw(Status(), Context()));
        Assert.True(SessionPresence.CanEndOpenClawConversation(Status(), Context()));
    }

    // One clause at a time, each flipped against the case above.
    [Theory]
    [InlineData(SessionSource.ClaudeCode)]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.RemoteControl)]
    public void NoOtherSourceIsOfferedEither(SessionSource source)
    {
        Assert.False(SessionPresence.CanInterruptOpenClaw(Status(source), Context()));
        Assert.False(SessionPresence.CanEndOpenClawConversation(Status(source), Context()));
    }

    [Fact]
    public void ARoomIsOfferedNeither()
    {
        Assert.False(SessionPresence.CanInterruptOpenClaw(Status(room: true), Context()));
        Assert.False(SessionPresence.CanEndOpenClawConversation(Status(room: true), Context()));
    }

    [Fact]
    public void DisconnectedIsOfferedNeither()
    {
        Assert.False(SessionPresence.CanInterruptOpenClaw(Status(), Context(connected: false)));
        Assert.False(SessionPresence.CanEndOpenClawConversation(Status(), Context(connected: false)));
    }

    [Fact]
    public void NoKeyIsOfferedNeither()
    {
        Assert.False(SessionPresence.CanInterruptOpenClaw(Status(), Context(key: null)));
        Assert.False(SessionPresence.CanEndOpenClawConversation(Status(), Context(key: null)));
    }

    // "Allow replying to agents" off: the device has read and nothing else.
    [Fact]
    public void WithoutWriteScopeNeitherIsOffered()
    {
        var readOnly = Context(scopes: new[] { "operator.read" });

        Assert.False(SessionPresence.CanInterruptOpenClaw(Status(), readOnly));
        Assert.False(SessionPresence.CanEndOpenClawConversation(Status(), readOnly));
    }

    // A gateway that does not name chat.abort gets neither — End sends it first.
    [Fact]
    public void WithoutChatAbortNeitherIsOffered()
    {
        var noAbort = Context(methods: new[] { "sessions.patch" });

        Assert.False(SessionPresence.CanInterruptOpenClaw(Status(), noAbort));
        Assert.False(SessionPresence.CanEndOpenClawConversation(Status(), noAbort));
    }

    [Fact]
    public void WithoutSessionsPatchOnlyInterruptIsOffered()
    {
        var noPatch = Context(methods: new[] { "chat.abort" });

        Assert.True(SessionPresence.CanInterruptOpenClaw(Status(), noPatch));
        Assert.False(SessionPresence.CanEndOpenClawConversation(Status(), noPatch));
    }

    // The gateway refuses to archive an agent's main session, so End is hidden
    // there rather than offered and refused.
    [Fact]
    public void AMainSessionCanBeInterruptedButNotEnded()
    {
        var main = Context(isMain: true);

        Assert.True(SessionPresence.CanInterruptOpenClaw(Status(), main));
        Assert.False(SessionPresence.CanEndOpenClawConversation(Status(), main));
    }

    // No id to send as expectedSessionId, which the archive is refused without.
    [Fact]
    public void WithoutASessionIdOnlyInterruptIsOffered()
    {
        var noId = Context(sessionId: null);

        Assert.True(SessionPresence.CanInterruptOpenClaw(Status(), noId));
        Assert.False(SessionPresence.CanEndOpenClawConversation(Status(), noId));
    }

    [Fact]
    public void TheEmptyContextOffersNothing()
    {
        Assert.False(SessionPresence.CanInterruptOpenClaw(Status(), OpenClawActionContext.None));
        Assert.False(SessionPresence.CanEndOpenClawConversation(Status(), OpenClawActionContext.None));
    }

    // CanEndSession is not widened by any of this: an OpenClaw orb still has
    // no pid and still does not offer "End this session".
    [Fact]
    public void TheLocalEndRowStillIgnoresOpenClaw()
    {
        Assert.False(SessionPresence.CanEndSession(new SessionStatus
        {
            Source = SessionSource.OpenClaw,
            SessionPid = 0
        }));
    }

    // --- CB-270: Keep on screen, Rename conversation ---

    [Theory]
    [InlineData(OpenClawAction.KeepOnScreen, "Keep on screen")]
    [InlineData(OpenClawAction.StopKeeping, "Keep on screen")]
    [InlineData(OpenClawAction.Rename, "Rename conversation…")]
    public void TheNewHeaders(OpenClawAction action, string expected) =>
        Assert.Equal(expected, OpenClawActionText.Header(action));

    [Fact]
    public void TheNewTipsAndWorkingTexts()
    {
        Assert.Equal(
            "Pins this conversation on the gateway, so it stays on screen however long it has been quiet — in every client.",
            OpenClawActionText.Tip(OpenClawAction.KeepOnScreen));
        Assert.Equal(OpenClawActionText.Tip(OpenClawAction.KeepOnScreen),
            OpenClawActionText.Tip(OpenClawAction.StopKeeping));
        Assert.Contains("name", OpenClawActionText.Tip(OpenClawAction.Rename));

        Assert.Equal("Keeping…", OpenClawActionText.Working(OpenClawAction.KeepOnScreen));
        Assert.Equal("Releasing…", OpenClawActionText.Working(OpenClawAction.StopKeeping));
        Assert.Equal("Renaming…", OpenClawActionText.Working(OpenClawAction.Rename));
    }

    [Theory]
    [InlineData(OpenClawActionOutcome.Done, OpenClawAction.KeepOnScreen, null, "Kept on screen")]
    [InlineData(OpenClawActionOutcome.Done, OpenClawAction.StopKeeping, null, "No longer kept on screen")]
    [InlineData(OpenClawActionOutcome.Done, OpenClawAction.Rename, null, "Renamed")]
    [InlineData(OpenClawActionOutcome.Refused, OpenClawAction.KeepOnScreen, "nope", "Couldn't keep it on screen: nope")]
    [InlineData(OpenClawActionOutcome.Refused, OpenClawAction.StopKeeping, "nope", "Couldn't release it: nope")]
    [InlineData(OpenClawActionOutcome.Refused, OpenClawAction.Rename, "nope", "Couldn't rename: nope")]
    [InlineData(OpenClawActionOutcome.NotConnected, OpenClawAction.Rename, null,
        "Couldn't rename: not connected to the gateway")]
    [InlineData(OpenClawActionOutcome.OtherDevice, OpenClawAction.KeepOnScreen, null,
        "Couldn't keep it on screen: the gateway said unauthorized")]
    public void TheNewActionsSayWhatHappened(
        OpenClawActionOutcome outcome, OpenClawAction action, string? detail, string expected) =>
        Assert.Equal(expected, OpenClawActionText.For(outcome, detail, action));

    // The two existing actions' words are asserted unchanged by the cases above;
    // this pins the ones the switch rewrite could most easily have disturbed.
    [Fact]
    public void TheExistingActionsKeepTheirExactWords()
    {
        Assert.Equal("Couldn't end: a run started from another device is still going",
            OpenClawActionText.For(OpenClawActionOutcome.OtherDevice, null, OpenClawAction.End));
        Assert.Equal("Started from another device",
            OpenClawActionText.For(OpenClawActionOutcome.OtherDevice, null, OpenClawAction.Interrupt));
        Assert.Equal("Ended", OpenClawActionText.For(OpenClawActionOutcome.Done, null, OpenClawAction.End));
        Assert.Equal("Interrupted",
            OpenClawActionText.For(OpenClawActionOutcome.Done, null, OpenClawAction.Interrupt));
    }

    [Fact]
    public void AnUnconfirmedPatchSaysWhatTheCallerAskedItTo()
    {
        var (outcome, detail) = OpenClawOrbActions.ParsePatchResult(
            Json("""{"ok":false}"""), "the gateway didn't confirm the change");

        Assert.Equal(OpenClawActionOutcome.Refused, outcome);
        Assert.Equal("the gateway didn't confirm the change", detail);
    }

    // CanPatchOpenClaw: true with everything present, false for each missing
    // conjunct. Unlike End it needs neither the abort method nor a non-main
    // session.
    private static readonly string[] PatchOnly = { "sessions.patch" };

    [Fact]
    public void KeepAndRenameAreOfferedWhereverAPatchWillWork()
    {
        Assert.True(SessionPresence.CanPatchOpenClaw(Status(), Context()));
        Assert.True(SessionPresence.CanPatchOpenClaw(Status(), Context(methods: PatchOnly)));
        Assert.True(SessionPresence.CanPatchOpenClaw(Status(), Context(isMain: true)));
    }

    [Fact]
    public void KeepAndRenameAreHiddenForEachMissingPiece()
    {
        Assert.False(SessionPresence.CanPatchOpenClaw(Status(SessionSource.ClaudeCode), Context()));
        Assert.False(SessionPresence.CanPatchOpenClaw(Status(room: true), Context()));
        Assert.False(SessionPresence.CanPatchOpenClaw(Status(), Context(connected: false)));
        Assert.False(SessionPresence.CanPatchOpenClaw(Status(), Context(key: null)));
        Assert.False(SessionPresence.CanPatchOpenClaw(Status(), Context(sessionId: null)));
        Assert.False(SessionPresence.CanPatchOpenClaw(Status(), Context(methods: new[] { "chat.abort" })));
        Assert.False(SessionPresence.CanPatchOpenClaw(Status(), Context(scopes: new[] { "operator.read" })));
    }
}
