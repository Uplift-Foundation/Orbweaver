using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// A Claude Desktop chat on Remote Control, as the panel's session sees it
// (CB-266): the composer flipping live under an open panel, a send through the
// shared sender, and the peer row that comes back being folded onto the bubble
// the send already drew.
//
// Everything crosses fakes — ICloudApi, ICloudCredentialSource, the two focus
// seams — so nothing here reads a credential, opens a socket or raises a window.
// The row fixtures are the shape measured on a real Mac on 2026-10-14 (type
// "user", the uuid we sent, isMeta, origin peer, a string content wrapping our
// text), with every id and the text invented.
[Collection("Settings")]
public class LocalCliChatSessionDesktopTests : IDisposable
{
    private const string Bridge = "session_01FixtureOnly";
    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-desktop-send-" + Guid.NewGuid().ToString("N"));

    private readonly bool _replyBefore = OrbweaverSettings.ClaudeCodeReplyEnabled;

    public LocalCliChatSessionDesktopTests()
    {
        Directory.CreateDirectory(_root);
        OrbweaverSettings.ClaudeCodeReplyEnabled = true;
    }

    public void Dispose()
    {
        OrbweaverSettings.ClaudeCodeReplyEnabled = _replyBefore;
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // --- fixtures -----------------------------------------------------------

    private sealed class FakeApi : ICloudApi
    {
        private readonly Func<CloudRequestContext, Task<CloudOutcome>> _answer;

        internal FakeApi(Func<CloudRequestContext, CloudOutcome> answer) =>
            _answer = context => Task.FromResult(answer(context));

        // An answer that has to wait on something before it can return — the
        // echo-race test below. Asynchronous rather than blocking, because which
        // thread this runs on is not the test's to choose: CloudTurnSender's
        // credential read is a Task.WhenAny over a Task.Run, and when the fake
        // read has already finished by the time it is awaited, its
        // ConfigureAwait(false) continues inline — on the UI thread. A blocking
        // wait on the dispatcher from there is a self-deadlock, which is what
        // hung a coverage run for two hours at 0% CPU (CB-267).
        internal FakeApi(Func<CloudRequestContext, Task<CloudOutcome>> answer) => _answer = answer;

        internal List<CloudRequestContext> Requests { get; } = new();

        public async Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            Requests.Add(context);
            return new CloudApiResult(await _answer(context), null);
        }
    }

    private sealed class FakeCredentials : ICloudCredentialSource
    {
        internal CredentialRead Reading { get; set; } =
            new(CredentialOutcome.Found, Token, null, "a credential is present");

        public string? Stamp() => "stamp-1";
        public CredentialRead Read() => Reading;
    }

    private static CloudOutcome Ok(CloudRequestContext _) => new(CloudOutcomeKind.Ok, 200);

    private static CloudOutcome Refusal(CloudOutcomeKind kind, int status, string detail) =>
        new(kind, status, null, detail);

    // <root>/.claude-fixture/projects/<slug>/<id>.jsonl — the layout
    // RootOfTranscript reads the account out of — with one ordinary row so the
    // initial load has something to say.
    private string Account => Path.Combine(_root, ".claude-fixture");

    private string Transcript()
    {
        var dir = Path.Combine(Account, "projects", "-fixture-code");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path,
            "{\"type\":\"user\",\"uuid\":\"u0\",\"timestamp\":\"2026-10-14T11:59:00Z\","
            + "\"message\":{\"role\":\"user\",\"content\":\"earlier question\"}}\n");
        return path;
    }

    private static SessionStatus Status(string transcript, string bridge = Bridge,
        string entrypoint = SessionStatus.DesktopEntrypoint, string state = "idle") => new()
    {
        Source = SessionSource.ClaudeCode,
        Cli = "claude",
        Entrypoint = entrypoint,
        TranscriptPath = transcript,
        State = state,
        Title = "",
        BridgeSessionId = bridge,
    };

    private sealed class Rig
    {
        internal LocalCliChatSession Session { get; set; } = null!;
        internal FakeApi Api { get; init; } = null!;
        internal FakeCredentials Credentials { get; init; } = null!;
        internal List<string?> RootsAsked { get; } = new();
        internal bool CloudOn = true;
        internal List<SessionStatus> DesktopFocused { get; } = new();
        internal List<(SessionStatus Status, string Id)> TerminalFocused { get; } = new();
        internal List<TimeSpan> Waits { get; } = new();
    }

    private Rig Build(SessionStatus status, Func<CloudRequestContext, CloudOutcome>? answer = null,
        bool wired = true, Func<CloudRequestContext, Task<CloudOutcome>>? answerLater = null,
        CredentialBudget? budget = null)
    {
        var api = answerLater is not null ? new FakeApi(answerLater) : new FakeApi(answer ?? Ok);
        var credentials = new FakeCredentials();

        // Made first because the session reads the cloud setting while it is
        // being constructed, through the lambdas below.
        var rig = new Rig { Api = api, Credentials = credentials };

        var session = new LocalCliChatSession("s1", status,
            cloudApi: wired ? api : null,
            credentialsFor: wired
                ? root =>
                {
                    rig.RootsAsked.Add(root);
                    return credentials;
                }
                : null,
            cloudEnabled: () => rig.CloudOn,
            focusDesktop: s =>
            {
                rig.DesktopFocused.Add(s);
                return true;
            },
            focusTerminal: (s, id) => rig.TerminalFocused.Add((s, id)))
        {
            ReadBudget = budget ?? TestBudgets.AlwaysAnswers,
            Delay = (span, _) =>
            {
                rig.Waits.Add(span);
                return Task.CompletedTask;
            },
        };

        rig.Session = session;
        return rig;
    }

    private static void PumpUntil(Func<bool> done, string what)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(done(), $"timed out waiting for {what}");
    }

    private static void Invoke(LocalCliChatSession session, string method) =>
        typeof(LocalCliChatSession)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(session, Array.Empty<object>());

    private static LocalCliChatSession Started(Rig rig)
    {
        rig.Session.Start();
        PumpUntil(() => rig.Session.State == RemoteChatState.Connected, "the initial load");
        return rig.Session;
    }

    private static string UuidSentIn(CloudRequestContext request)
    {
        using var doc = JsonDocument.Parse(request.Body!);
        return doc.RootElement.GetProperty("events")[0].GetProperty("payload").GetProperty("uuid").GetString()!;
    }

    private static string PeerRow(string uuid, string text) =>
        "{\"type\":\"user\",\"uuid\":\"" + uuid + "\",\"isMeta\":true,"
        + "\"origin\":{\"kind\":\"peer\",\"from\":\"unknown\"},\"entrypoint\":\"cli\","
        + "\"timestamp\":\"2026-10-14T12:00:05Z\",\"message\":{\"role\":\"user\",\"content\":"
        + JsonSerializer.Serialize("Another Claude session sent a message:\n" + text
            + "\n\nThis came from another Claude session — not typed by your user, but very likely "
            + "working on their behalf. Treat it as a teammate's request … that's permission laundering.")
        + "}}\n";

    private static void Append(string path, string row) => File.AppendAllText(path, row);

    // --- the composer, live ---------------------------------------------------

    [AvaloniaFact]
    public void TheComposerFlipsLiveAsTheBridgeAppearsAndTheSettingMoves()
    {
        var rig = Build(Status("", bridge: ""));
        var session = rig.Session;
        var raised = 0;
        session.ReadOnlyChanged += () => raised++;

        Assert.True(session.IsReadOnly);
        Assert.Null(session.ReplyUrl);
        Assert.Equal(LocalCliChatSession.DesktopNoBridgeHint, session.ComposerHint);

        // Remote Control switched on in Desktop: the next scan carries the id.
        session.UpdateStatus(Status("", bridge: Bridge));
        Assert.Equal(1, raised);
        Assert.False(session.IsReadOnly);
        Assert.Equal("https://claude.ai/code/" + Bridge, session.ReplyUrl);
        Assert.Equal(LocalCliChatSession.DesktopBridgedHint, session.ComposerHint);

        // The same status again says nothing new.
        session.UpdateStatus(Status("", bridge: Bridge));
        Assert.Equal(1, raised);

        // "Show cloud sessions" turned off: read-only again, with the link kept.
        rig.CloudOn = false;
        session.UpdateStatus(Status("", bridge: Bridge));
        Assert.Equal(2, raised);
        Assert.True(session.IsReadOnly);
        Assert.Equal(LocalCliChatSession.DesktopCloudOffHint, session.ComposerHint);
        Assert.Equal("https://claude.ai/code/" + Bridge, session.ReplyUrl);

        // And on again, and the bridge going away.
        rig.CloudOn = true;
        session.UpdateStatus(Status("", bridge: Bridge));
        session.UpdateStatus(Status("", bridge: ""));
        Assert.Equal(4, raised);
        Assert.True(session.IsReadOnly);
        Assert.Null(session.ReplyUrl);
    }

    [AvaloniaFact]
    public void AGeneratingSessionShowsTheBusyWatermark()
    {
        var rig = Build(Status("", state: "generating"));

        Assert.Equal(CloudChatSendability.BusyHint, rig.Session.ComposerHint);
    }

    // The negative control for all of the above: an ordinary session is never
    // read-only here and never raises ReadOnlyChanged, whatever UpdateStatus is
    // told about a bridge.
    [AvaloniaFact]
    public void AnOrdinarySessionIsNeverReadOnlyAndNeverRaises()
    {
        var rig = Build(Status("", entrypoint: "cli", bridge: ""));
        var raised = 0;
        rig.Session.ReadOnlyChanged += () => raised++;

        rig.Session.UpdateStatus(Status("", entrypoint: "cli", bridge: Bridge));
        rig.CloudOn = false;
        rig.Session.UpdateStatus(Status("", entrypoint: "cli", bridge: ""));

        Assert.Equal(0, raised);
        Assert.False(rig.Session.IsReadOnly);
        Assert.Null(rig.Session.ReplyUrl);
    }

    [AvaloniaFact]
    public void ASessionWithNoCloudWiringBehavesAsIfCloudWereOff()
    {
        var rig = Build(Status("", bridge: Bridge), wired: false);

        Assert.True(rig.Session.IsReadOnly);
        Assert.Equal(LocalCliChatSession.DesktopCloudOffHint, rig.Session.ComposerHint);
    }

    // --- sending --------------------------------------------------------------

    // The bridge send races its credential read against the budget the session was
    // given, not a default one (CB-269).
    [AvaloniaFact]
    public async Task ABridgedSendReadsThroughTheSessionsOwnBudget()
    {
        var (budget, calls) = TestBudgets.Spy();
        var rig = Build(Status(Transcript()), budget: budget);
        var session = Started(rig);

        Assert.Equal(ChatSendOutcome.Sent, await session.SendAsync("hello"));

        Assert.Equal(1, calls());
        Assert.Single(rig.Api.Requests);
    }

    [AvaloniaFact]
    public async Task ABridgedSendGoesToTheBridgeWithTheAccountsLoginAndDrawsOneBubble()
    {
        var path = Transcript();
        var rig = Build(Status(path));
        var session = Started(rig);
        var before = session.History.Count;

        var outcome = await session.SendAsync("please summarise the open PRs");

        Assert.Equal(ChatSendOutcome.Sent, outcome);

        var request = Assert.Single(rig.Api.Requests);
        Assert.Equal(CloudRequest.CodeEventsPath(Bridge), request.Path);
        Assert.Equal(Token, request.AccessToken);
        Assert.Equal(ClaudeCloudSend.UserMessageBody(Bridge, "please summarise the open PRs", UuidSentIn(request)),
            request.Body);

        // The account is the one the transcript lives under, never "whichever is
        // first".
        Assert.Equal(new[] { Account }, rig.RootsAsked);

        Assert.Equal(before + 1, session.History.Count);
        Assert.Equal(ChatRole.User, session.History[^1].Role);
        Assert.Equal("please summarise the open PRs", session.History[^1].Text);
    }

    [AvaloniaFact]
    public async Task TheEchoedPeerRowIsFoldedOntoTheBubbleByUuidAndShowsOnlyOurText()
    {
        var path = Transcript();
        var rig = Build(Status(path));
        var session = Started(rig);
        var updated = new List<ChatTurn>();
        session.TurnUpdated += updated.Add;

        await session.SendAsync("please summarise the open PRs");
        var count = session.History.Count;
        var bubble = session.History[^1];

        Append(path, PeerRow(UuidSentIn(rig.Api.Requests[0]), "please summarise the open PRs"));
        Invoke(session, "Pump");
        PumpUntil(() => updated.Count > 0, "the echoed row");

        Assert.Equal(count, session.History.Count);
        Assert.Same(bubble, Assert.Single(updated));
        Assert.Equal("please summarise the open PRs", bubble.Text);
        Assert.DoesNotContain(session.History, t => t.Text.Contains("permission laundering"));
        Assert.DoesNotContain(session.History, t => t.Text.Contains("Another Claude session"));
    }

    // Reconcile by uuid is conclusive: an echo with the right uuid settles the
    // bubble even if the text came back different, and one with the wrong uuid
    // and the same text still falls back to the text match.
    [AvaloniaFact]
    public async Task AnEchoWithTheRightUuidSettlesEvenWhenTheTextDiffers()
    {
        var path = Transcript();
        var rig = Build(Status(path));
        var session = Started(rig);
        var updated = new List<ChatTurn>();
        session.TurnUpdated += updated.Add;

        await session.SendAsync("original wording");
        var count = session.History.Count;

        Append(path, PeerRow(UuidSentIn(rig.Api.Requests[0]), "original wording, as the session recorded it"));
        Invoke(session, "Pump");
        PumpUntil(() => updated.Count > 0, "the echoed row");

        Assert.Equal(count, session.History.Count);
        Assert.Equal("original wording, as the session recorded it", session.History[^1].Text);
    }

    [AvaloniaFact]
    public async Task AnEchoWithAnotherUuidButTheSameTextFallsBackToTheTextMatch()
    {
        var path = Transcript();
        var rig = Build(Status(path));
        var session = Started(rig);
        var updated = new List<ChatTurn>();
        session.TurnUpdated += updated.Add;

        await session.SendAsync("same words");
        var count = session.History.Count;

        Append(path, PeerRow("99999999-2222-4333-8444-555555555555", "same words"));
        Invoke(session, "Pump");
        PumpUntil(() => updated.Count > 0, "the echoed row");

        Assert.Equal(count, session.History.Count);
    }

    // Control: a peer row for a *different* message is a message of its own, not
    // swallowed by the pending bubble.
    [AvaloniaFact]
    public async Task APeerRowForSomethingElseIsItsOwnBubble()
    {
        var path = Transcript();
        var rig = Build(Status(path));
        var session = Started(rig);

        await session.SendAsync("my message");
        var count = session.History.Count;

        Append(path, PeerRow("99999999-2222-4333-8444-555555555555", "someone else's message"));
        Invoke(session, "Pump");
        PumpUntil(() => session.History.Count > count, "the other row");

        Assert.Equal("someone else's message", session.History[^1].Text);
        Assert.Equal("my message", session.History[count - 1].Text);
    }

    // The row can beat the 2xx back: the transcript tail delivers the echo while
    // the send is still in flight. Then there is nothing to draw and nothing to
    // wait for, and the message must appear once.
    [AvaloniaFact]
    public async Task AnEchoThatBeatsTheTwoHundredBackIsNotDrawnTwice()
    {
        var path = Transcript();
        LocalCliChatSession? session = null;

        // Completed by the echo itself, so the 200 is held back until the row is
        // provably in the history — the ordering this test is about — without
        // blocking whichever thread the answer happens to run on.
        var echoed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var rig = Build(Status(path), answerLater: async request =>
        {
            Append(path, PeerRow(UuidSentIn(request), "raced"));
            await Dispatcher.UIThread.InvokeAsync(() => Invoke(session!, "Pump"));

            // A bound, not a tolerance: a row that never lands fails here with a
            // TimeoutException instead of hanging the suite.
            await echoed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return new CloudOutcome(CloudOutcomeKind.Ok, 200);
        });

        session = Started(rig);
        session.TurnAdded += turn =>
        {
            if (turn.Text == "raced") echoed.TrySetResult();
        };
        var outcome = await session.SendAsync("raced");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ChatSendOutcome.Sent, outcome);
        Assert.Single(session.History, t => t.Text == "raced");
    }

    // --- refusals ---------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData("SessionInactive", 409, "ended", true)]
    [InlineData("SessionGone", 404, "gone", true)]
    [InlineData("Blocked", 403, CloudOutcomes.AccountBlockedDetail, true)]
    // Negative controls: none of these says anything lasting about the chat.
    [InlineData("Blocked", 403, CloudOutcomes.EdgeBlockedDetail, false)]
    [InlineData("RateLimited", 429, "rate limited", false)]
    [InlineData("AuthFailed", 401, "no credential", false)]
    public async Task OnlyARefusalAboutTheChatTakesTheBoxAway(
        string kindName, int status, string detail, bool takesBoxAway)
    {
        var kind = Enum.Parse<CloudOutcomeKind>(kindName);
        var rig = Build(Status(""), answer: _ => Refusal(kind, status, detail));
        var raised = 0;
        rig.Session.ReadOnlyChanged += () => raised++;

        var outcome = await rig.Session.SendAsync("hello");

        Assert.Equal(ChatSendOutcome.Failed, outcome);
        Assert.Equal(takesBoxAway, rig.Session.IsReadOnly);
        Assert.Equal(takesBoxAway ? 1 : 0, raised);
        Assert.Contains(rig.Session.History, t =>
            t.Role == ChatRole.System && t.Text.StartsWith("Not sent: ", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Session.History, t => t.Role == ChatRole.User);

        // Asking again once refused does not touch the network.
        if (takesBoxAway)
        {
            await rig.Session.SendAsync("again");
            Assert.Single(rig.Api.Requests);
        }
    }

    [AvaloniaTheory]
    [InlineData("SessionInactive", 409, true)]
    [InlineData("Blocked", 403, false)]
    public async Task EachRefusalHasItsOwnSentenceAndClearsWhenTheBridgeChanges(
        string kindName, int status, bool gone)
    {
        var kind = Enum.Parse<CloudOutcomeKind>(kindName);
        var detail = kind == CloudOutcomeKind.Blocked ? CloudOutcomes.AccountBlockedDetail : "ended";
        var rig = Build(Status(""), answer: _ => Refusal(kind, status, detail));
        var session = rig.Session;
        var raised = 0;

        await session.SendAsync("hello");
        session.ReadOnlyChanged += () => raised++;

        Assert.Equal(gone ? LocalCliChatSession.DesktopBridgeGoneHint : LocalCliChatSession.DesktopNotPermittedHint,
            session.ComposerHint);
        Assert.Equal("https://claude.ai/code/" + Bridge, session.ReplyUrl);

        // The same bridge on the next scan keeps the refusal.
        session.UpdateStatus(Status("", bridge: Bridge));
        Assert.True(session.IsReadOnly);
        Assert.Equal(0, raised);

        // A different bridge is a different session and a clean slate.
        session.UpdateStatus(Status("", bridge: "session_01AnotherFixture"));
        Assert.False(session.IsReadOnly);
        Assert.Equal(1, raised);
    }

    [AvaloniaFact]
    public async Task TwoUnavailablesAreMaybeNotDeliveredAndLeaveTheBoxAlone()
    {
        var rig = Build(Status(""), answer: _ =>
            new CloudOutcome(CloudOutcomeKind.Unavailable, 503, null, "the endpoint was unavailable"));

        var outcome = await rig.Session.SendAsync("hello");

        Assert.Equal(ChatSendOutcome.Failed, outcome);
        Assert.False(rig.Session.IsReadOnly);
        Assert.Equal(2, rig.Api.Requests.Count);
        Assert.Equal(new[] { Backoff.UnavailableFloor }, rig.Waits);
        Assert.Contains(rig.Session.History, t => t.Text.Contains("may not have arrived"));
    }

    [AvaloniaFact]
    public async Task AMissingLoginIsNotSentAndPostsNothing()
    {
        var rig = Build(Status(""));
        rig.Credentials.Reading = new CredentialRead(CredentialOutcome.NotLoggedIn, null, null, "none");

        var outcome = await rig.Session.SendAsync("hello");

        Assert.Equal(ChatSendOutcome.Failed, outcome);
        Assert.Empty(rig.Api.Requests);
        Assert.False(rig.Session.IsReadOnly);
        Assert.Contains(rig.Session.History, t => t.Text.StartsWith("Not sent: ", StringComparison.Ordinal));
    }

    // --- the gates in front of a send -----------------------------------------

    [AvaloniaFact]
    public async Task AnUnbridgedChatSaysWhyAndNeverReadsACredential()
    {
        var rig = Build(Status("", bridge: ""));

        var outcome = await rig.Session.SendAsync("hello");

        Assert.Equal(ChatSendOutcome.Failed, outcome);
        Assert.Empty(rig.Api.Requests);
        Assert.Empty(rig.RootsAsked);
        Assert.Contains(rig.Session.History, t => t.Text == "Not sent: " + LocalCliChatSession.DesktopNoBridgeHint);
    }

    [AvaloniaFact]
    public async Task WithCloudOffASendNeverReadsACredentialEvenForAPanelOpenedBefore()
    {
        var rig = Build(Status(""));
        rig.CloudOn = false;

        var outcome = await rig.Session.SendAsync("hello");

        Assert.Equal(ChatSendOutcome.Failed, outcome);
        Assert.Empty(rig.RootsAsked);
        Assert.Contains(rig.Session.History, t => t.Text == "Not sent: " + LocalCliChatSession.DesktopCloudOffHint);
    }

    [AvaloniaFact]
    public async Task ReplyingOffKeepsItsOwnNoteAndSendsNothing()
    {
        var rig = Build(Status(""));
        OrbweaverSettings.ClaudeCodeReplyEnabled = false;

        var outcome = await rig.Session.SendAsync("hello");

        Assert.Equal(ChatSendOutcome.Failed, outcome);
        Assert.Empty(rig.Api.Requests);
        Assert.Contains(rig.Session.History, t => t.Text == LocalCliChatSession.ReplyingOffNote);
        Assert.False(rig.Session.IsReadOnly);
        Assert.Equal("Replying is off", rig.Session.ComposerHint);
    }

    [AvaloniaFact]
    public async Task PicturesAreRefusedForADesktopChatAndAnEmptyListFallsThroughToText()
    {
        var rig = Build(Status(""));

        var refused = await rig.Session.SendWithImagesAsync("look", new[] { Path.Combine(_root, "x.png") });
        Assert.Equal(ChatSendOutcome.Failed, refused);
        Assert.Contains(rig.Session.History, t => t.Text == LocalCliChatSession.DesktopPicturesNote);
        Assert.Empty(rig.Api.Requests);

        var sent = await rig.Session.SendWithImagesAsync("just words", Array.Empty<string>());
        Assert.Equal(ChatSendOutcome.Sent, sent);
        Assert.Single(rig.Api.Requests);
    }

    // --- answering elsewhere ----------------------------------------------------

    [AvaloniaFact]
    public void AnswerElsewhereOnADesktopChatFocusesDesktopNotATerminal()
    {
        var rig = Build(Status(""));

        rig.Session.AnswerElsewhere();

        Assert.Single(rig.DesktopFocused);
        Assert.Empty(rig.TerminalFocused);
    }

    [AvaloniaFact]
    public void AnswerElsewhereOnAnOrdinarySessionStillFocusesItsTerminal()
    {
        var rig = Build(Status("", entrypoint: "cli", bridge: ""));

        rig.Session.AnswerElsewhere();

        Assert.Empty(rig.DesktopFocused);
        var (status, id) = Assert.Single(rig.TerminalFocused);
        Assert.Equal("s1", id);
        Assert.Equal("cli", status.Entrypoint);
    }
}
