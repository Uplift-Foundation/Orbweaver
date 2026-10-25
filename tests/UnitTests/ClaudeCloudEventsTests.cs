using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// Covers reading a cloud session's transcript, and the read-only chat session
// built on it.
//
// **The events endpoint returns Claude Code's own transcript format**, which is
// why nothing here parses a turn: ChatTranscript already does that, is covered
// case by case in tests/TranscriptTests, and a second parser over one format is
// how a panel comes to show something a terminal does not. What this file covers
// is the envelope around it, the reconciliation, and the refusal to send.
//
// **Every value in the fixture is invented.** The row shape is real — captured
// field by field from a /events response — and the uuids, text and instants are
// not.
public partial class ClaudeCloudEventsTests
{
    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";

    // --- the fixture ---------------------------------------------------------

    // Built by substitution rather than interpolation. These rows are dense with
    // braces, and a raw interpolated string whose content ends in `}}` is a
    // counting exercise that fails as a compile error — which is a fine way to
    // fail, and a worse way to read.
    private const string UserTemplate =
        """{"type":"user","uuid":"UUID","timestamp":"2026-09-19T10:00:00Z","message":{"role":"user","content":[{"type":"text","text":"TEXT"}]}}""";

    private const string AssistantTemplate =
        """{"type":"assistant","uuid":"UUID","timestamp":"2026-09-19T10:00:05Z","message":{"role":"assistant","model":"claude-opus-5","content":[{"type":"text","text":"TEXT"}],"usage":{"input_tokens":10,"output_tokens":20},"stop_reason":"end_turn"}}""";

    private static string UserRow(string uuid, string text) =>
        UserTemplate.Replace("UUID", uuid, StringComparison.Ordinal)
            .Replace("TEXT", text, StringComparison.Ordinal);

    private static string AssistantRow(string uuid, string text) =>
        AssistantTemplate.Replace("UUID", uuid, StringComparison.Ordinal)
            .Replace("TEXT", text, StringComparison.Ordinal);

    private static string Envelope(IEnumerable<string> rows, bool hasMore = false, string? lastId = null)
    {
        var last = lastId is null ? "null" : "\"" + lastId + "\"";
        return "{\"data\":[" + string.Join(",", rows) + "],\"has_more\":"
               + (hasMore ? "true" : "false") + ",\"last_id\":" + last + "}";
    }

    // --- the envelope --------------------------------------------------------

    [Fact]
    public void APageOfEventsBecomesTurnsThroughTheExistingParser()
    {
        var page = ClaudeCloudEvents.ParsePage(Envelope(new[]
        {
            UserRow("u1", "run the tests"),
            AssistantRow("a1", "running them now"),
        }, hasMore: true, lastId: "evt_9"));

        Assert.True(page.Parsed);
        Assert.True(page.HasMore);
        Assert.Equal("evt_9", page.LastId);
        Assert.Equal(2, page.Rows.Count);

        Assert.Equal(ChatRole.User, page.Rows[0].Turn.Role);
        Assert.Equal("run the tests", page.Rows[0].Turn.Text);
        Assert.Equal("u1", page.Rows[0].Uuid);

        Assert.Equal(ChatRole.Assistant, page.Rows[1].Turn.Role);
        Assert.Equal("running them now", page.Rows[1].Turn.Text);
    }

    // **The reason the rows are re-serialised rather than handed over raw.**
    // ChatTranscript.IsInteresting is a substring test over `"type":"assistant"` —
    // deliberately, so a megabyte of file history is never turned into a
    // JsonDocument — and that test is sensitive to whitespace. A pretty-printed
    // response would make every row uninteresting and the panel would show an
    // empty conversation with nothing anywhere saying why.
    [Fact]
    public void APrettyPrintedResponseStillParses()
    {
        const string pretty = """
        {
          "data": [
            {
              "type": "assistant",
              "uuid": "a1",
              "timestamp": "2026-09-19T10:00:05Z",
              "message": {
                "role": "assistant",
                "content": [ { "type": "text", "text": "spaced out" } ]
              }
            }
          ],
          "has_more": false,
          "last_id": null
        }
        """;

        var page = ClaudeCloudEvents.ParsePage(pretty);

        Assert.True(page.Parsed);
        Assert.Equal("spaced out", Assert.Single(page.Rows).Turn.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html>")]
    [InlineData("""{"no_data":true}""")]
    [InlineData("""{"data":"nope"}""")]
    [InlineData("""[]""")]
    public void AnUnusablePageSaysItDidNotParse(string? body)
    {
        var page = ClaudeCloudEvents.ParsePage(body);

        Assert.False(page.Parsed);
        Assert.Empty(page.Rows);
    }

    // Row types the parser does not handle are skipped, not fatal. A Claude Code
    // upgrade that adds a row type degrades rather than breaks, which is the
    // property ChatTranscript.Map was written for and is worth pinning here too.
    [Fact]
    public void RowTypesTheParserDoesNotHandleAreSkippedRatherThanFatal()
    {
        var page = ClaudeCloudEvents.ParsePage(Envelope(new[]
        {
            """{"type":"system","uuid":"s1","subtype":"init"}""",
            """{"type":"control_request","uuid":"c1"}""",
            """{"type":"result","uuid":"r1","duration_ms":1200}""",
            AssistantRow("a1", "still here"),
        }));

        Assert.True(page.Parsed);
        Assert.Equal("still here", Assert.Single(page.Rows).Turn.Text);
    }

    [Fact]
    public void ABlankLastIdIsNoCursor()
    {
        Assert.Null(ClaudeCloudEvents.ParsePage(
            """{"data":[],"has_more":true,"last_id":"  "}""").LastId);
    }

    // --- the chat session ----------------------------------------------------

    private sealed class FakeApi : ICloudApi
    {
        private readonly Func<string, CloudApiResult> _answer;

        internal FakeApi(Func<string, CloudApiResult> answer) => _answer = answer;

        // The whole context, not just the path, so a test can see the method
        // and body a call went out with as well as where it went.
        internal List<CloudRequestContext> Requests { get; } = new();

        internal List<string> Paths => Requests.Select(r => r.Path).ToList();

        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            Requests.Add(context);
            return Task.FromResult(_answer(context.Path));
        }
    }

    private sealed class FakeCredentials : ICloudCredentialSource
    {
        internal CredentialRead Reading { get; set; } =
            new(CredentialOutcome.Found, Token, null, "a credential is present");

        public string? Stamp() => "stamp-1";
        public CredentialRead Read() => Reading;
    }

    private static ClaudeCloudSessions.Session Session(string id = "session_a",
        string title = "a cloud session") =>
        new(id, title, "idle", new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc),
            "https://claude.ai/code/" + id, "idle", false, null, null, null, null);

    // Events are raised synchronously in tests by handing the session a post that
    // simply runs the action. In the app it is Dispatcher.UIThread.Post, which is
    // requirement 1 on the interface.
    private static ClaudeCloudChatSession Build(ICloudApi api, ICloudCredentialSource? creds = null,
        ClaudeCloudSessions.Session? session = null) =>
        new(session ?? Session(), api, creds ?? new FakeCredentials(), action => action());

    [Fact]
    public async Task LoadingReadsTheEventsPathAndFillsTheHistory()
    {
        var api = new FakeApi(_ => new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""),
            Envelope(new[] { UserRow("u1", "hello"), AssistantRow("a1", "hi") })));

        var chat = Build(api);
        var added = new List<ChatTurn>();
        chat.TurnAdded += added.Add;

        Assert.True(await chat.LoadAsync(CancellationToken.None));

        Assert.Equal("/v2/ccr-sessions/session_a/events?limit=100", Assert.Single(api.Paths));
        Assert.Equal(new[] { "hello", "hi" }, chat.History.Select(t => t.Text));
        Assert.Equal(2, added.Count);
        Assert.Equal(RemoteChatState.Connected, chat.State);
    }

    [Fact]
    public async Task TheEventsWalkFollowsItsCursor()
    {
        var pages = new Queue<string>(new[]
        {
            Envelope(new[] { UserRow("u1", "first") }, hasMore: true, lastId: "evt_1"),
            Envelope(new[] { AssistantRow("a1", "second") }),
        });

        var api = new FakeApi(_ => new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""), pages.Dequeue()));
        var chat = Build(api);

        Assert.True(await chat.LoadAsync(CancellationToken.None));

        Assert.Equal(new[]
        {
            "/v2/ccr-sessions/session_a/events?limit=100",
            "/v2/ccr-sessions/session_a/events?limit=100&after_id=evt_1",
        }, api.Paths);

        Assert.Equal(new[] { "first", "second" }, chat.History.Select(t => t.Text));
    }

    [Fact]
    public async Task TheEventsWalkStopsAtItsPageCap()
    {
        var page = 0;
        var api = new FakeApi(_ =>
        {
            page++;
            return new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""),
                Envelope(new[] { UserRow($"u{page}", $"turn {page}") }, hasMore: true, lastId: $"evt_{page}"));
        });

        var chat = Build(api);
        Assert.True(await chat.LoadAsync(CancellationToken.None));

        Assert.Equal(ClaudeCloudChatSession.MaxEventPages, api.Paths.Count);
    }

    // Oldest to newest is what the interface promises, so a transcript longer than
    // the cap is trimmed at the **head**. Trimming the tail would hand the panel
    // the beginning of a conversation and call it the end of one.
    [Fact]
    public async Task ALongTranscriptIsTrimmedToItsMostRecentTurns()
    {
        var rows = Enumerable.Range(0, ClaudeCloudChatSession.HistoryTurns + 50)
            .Select(i => UserRow($"u{i}", $"turn {i}"));

        var api = new FakeApi(_ => new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""),
            Envelope(rows)));

        var chat = Build(api);
        Assert.True(await chat.LoadAsync(CancellationToken.None));

        Assert.Equal(ClaudeCloudChatSession.HistoryTurns, chat.History.Count);
        Assert.Equal("turn 50", chat.History[0].Text);
        Assert.Equal($"turn {ClaudeCloudChatSession.HistoryTurns + 49}", chat.History[^1].Text);
    }

    // **By uuid, not by position.** A transcript re-read while its last turn is
    // still being written comes back longer than it was, and matching by position
    // would work right up until a row was skipped — which ChatTranscript does, for
    // sidechains and noise.
    [Fact]
    public async Task ASecondReadUpdatesAGrowingTurnInPlaceRatherThanAppendingIt()
    {
        var reads = 0;
        var api = new FakeApi(_ =>
        {
            reads++;
            return new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""), Envelope(new[]
            {
                UserRow("u1", "run the tests"),
                AssistantRow("a1", reads == 1 ? "running" : "running them now, all green"),
            }));
        });

        var chat = Build(api);
        await chat.LoadAsync(CancellationToken.None);

        var added = new List<ChatTurn>();
        var updated = new List<ChatTurn>();
        chat.TurnAdded += added.Add;
        chat.TurnUpdated += updated.Add;

        await chat.LoadAsync(CancellationToken.None);

        Assert.Equal(2, chat.History.Count);
        Assert.Empty(added);
        Assert.Equal("running them now, all green", Assert.Single(updated).Text);
        Assert.Equal("running them now, all green", chat.History[1].Text);

        // Requirement 2: the event carries the whole turn, already mutated, and the
        // list item is the same object rather than a replacement.
        Assert.Same(chat.History[1], updated[0]);
    }

    [Fact]
    public async Task ASecondReadRaisesNothingWhenNothingChanged()
    {
        var api = new FakeApi(_ => new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""),
            Envelope(new[] { UserRow("u1", "unchanged") })));

        var chat = Build(api);
        await chat.LoadAsync(CancellationToken.None);

        var raised = 0;
        chat.TurnAdded += _ => raised++;
        chat.TurnUpdated += _ => raised++;

        await chat.LoadAsync(CancellationToken.None);

        Assert.Equal(0, raised);
        Assert.Single(chat.History);
    }

    [Fact]
    public async Task ANewTurnOnASecondReadIsAppendedAndAnnounced()
    {
        var reads = 0;
        var api = new FakeApi(_ =>
        {
            reads++;
            var rows = reads == 1
                ? new[] { UserRow("u1", "first") }
                : new[] { UserRow("u1", "first"), AssistantRow("a1", "second") };
            return new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""), Envelope(rows));
        });

        var chat = Build(api);
        await chat.LoadAsync(CancellationToken.None);

        var added = new List<ChatTurn>();
        chat.TurnAdded += added.Add;

        await chat.LoadAsync(CancellationToken.None);

        Assert.Equal("second", Assert.Single(added).Text);
        Assert.Equal(2, chat.History.Count);
    }

    // An empty transcript and an unreadable one look identical on screen, and only
    // one of them is worth saying something about. So a refusal leaves the session
    // in Error rather than showing nothing as though there were nothing.
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task ARefusedFetchLeavesTheSessionInErrorAndNotEmpty(int status)
    {
        var api = new FakeApi(_ => new CloudApiResult(CloudOutcomes.OutcomeFor(status, null), null));

        var chat = Build(api);
        var states = new List<RemoteChatState>();
        chat.StateChanged += states.Add;

        Assert.False(await chat.LoadAsync(CancellationToken.None));

        Assert.Equal(RemoteChatState.Error, chat.State);
        Assert.Equal(RemoteChatState.Error, Assert.Single(states));
        Assert.Empty(chat.History);
    }

    [Fact]
    public async Task ABodyThatWillNotParseIsAnErrorRatherThanAnEmptyConversation()
    {
        var api = new FakeApi(_ => new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""), "<html>"));

        var chat = Build(api);

        Assert.False(await chat.LoadAsync(CancellationToken.None));
        Assert.Equal(RemoteChatState.Error, chat.State);
    }

    // The panel's read is budgeted for the same reason the arm's is: the secret
    // read can block indefinitely with no window server session, and a panel whose
    // load never returns is a spinner that never stops.
    [Fact]
    public async Task AStoreThatNeverAnswersLeavesThePanelInErrorRatherThanLoadingForever()
    {
        var api = new FakeApi(_ => throw new InvalidOperationException("must not be called"));

        // Disposed at the end, which is what lets the abandoned read's pool thread
        // go. It used to park for the rest of the process, and a pool with enough
        // of those left in it has nobody to fire a timer (CB-269).
        using var credentials = new HangingCredentials();

        var chat = new ClaudeCloudChatSession(Session(), api,
            credentials, action => action())
        {
            ReadBudget = TestBudgets.ExpiresAtOnce,
        };

        // Task.Run for the reason the arm's equivalent test spells out: an async
        // method runs synchronously up to its first real await, so an un-budgeted
        // read would block the calling thread before there was a Task to race.
        var load = Task.Run(() => chat.LoadAsync(CancellationToken.None));
        var finished = await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(ReferenceEquals(finished, load),
            "LoadAsync did not return: the credential read parked the panel.");

        Assert.False(await load);
        Assert.Equal(RemoteChatState.Error, chat.State);
        Assert.Empty(api.Paths);
        Assert.Empty(chat.History);
    }

    // Blocks in Read and answers instantly in Stamp, which is the measured
    // asymmetry — the data query hangs with no window server session and the
    // attributes-only query does not.
    private sealed class HangingCredentials : ICloudCredentialSource, IDisposable
    {
        private readonly ManualResetEventSlim _gate = new(false);

        // Releases the parked read. The gate itself is deliberately not disposed:
        // the thread being freed may still be inside Wait, and disposing under it
        // would turn a clean exit into an ObjectDisposedException nobody reads.
        public void Dispose() => _gate.Set();

        public string? Stamp() => "stamp-1";

        public CredentialRead Read()
        {
            _gate.Wait();
            return new CredentialRead(CredentialOutcome.Found, Token, null, "too late");
        }
    }

    [Fact]
    public async Task NoCredentialMeansNoRequestAtAll()
    {
        var api = new FakeApi(_ => throw new InvalidOperationException("must not be called"));
        var chat = Build(api, new FakeCredentials
        {
            Reading = new CredentialRead(CredentialOutcome.NotLoggedIn, null, null, "…"),
        });

        Assert.False(await chat.LoadAsync(CancellationToken.None));

        Assert.Empty(api.Paths);
        Assert.Equal(RemoteChatState.Error, chat.State);
    }

    // --- sending ------------------------------------------------------------
    //
    // CB-199. What is measured and what these tests therefore pin as behaviour:
    // POST /v1/code/sessions/{id}/events takes a user row and answers 200 with a
    // receipt; the same uuid again is `duplicate: true`; the echo reaches the /v2
    // history carrying the uuid that was sent; a deleted session is 404. The 409
    // and the account 403 are not measured and are pinned here as what the code
    // does with them, not as what the server sends.

    private const string Receipt =
        """{"results":[{"duplicate":false,"sequence_num":"20","event_id":"e"}]}""";

    private const string DuplicateReceipt =
        """{"results":[{"duplicate":true,"sequence_num":"20","event_id":"e"}]}""";

    // A JSON error carrying a request_id, which is what makes a 403 the API's
    // own refusal rather than an edge block (CloudOutcomes.LooksLikeEdgeBlock).
    private const string ApiErrorBody =
        """{"type":"error","error":{"type":"permission_error","message":"no"},"request_id":"req_1"}""";

    private static CloudApiResult Answer(int status, string? body = null) =>
        new(CloudOutcomes.OutcomeFor(status, body ?? ""), status is >= 200 and < 300 ? body : null);

    private static readonly CloudApiResult TimedOut =
        new(new CloudOutcome(CloudOutcomeKind.Unavailable, 0, null, "the request timed out"), null);

    // Answers writes and reads separately, since one send can be followed by a
    // read of the history, and records every context whole.
    private sealed class RoutingApi : ICloudApi
    {
        private readonly Func<CloudRequestContext, CloudApiResult> _answer;

        internal RoutingApi(Func<CloudRequestContext, CloudApiResult> answer) => _answer = answer;

        internal RoutingApi(CloudApiResult write, CloudApiResult? read = null)
            : this(c => c.Method == HttpMethod.Post
                ? write
                : read ?? new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""), Envelope(Array.Empty<string>())))
        {
        }

        // What the live loop's status read answers. Idle by default, so a loop a
        // send starts sees the turn end at its first tick and every test that
        // is not about the loop is over quickly.
        internal Func<CloudApiResult> Status { get; set; } = () => new CloudApiResult(
            CloudOutcomes.OutcomeFor(200, ""), "{\"status_bucket\":\"idle\"}");

        // What the stream's newest-event read answers: sequence 41 by default.
        internal Func<CloudApiResult> Newest { get; set; } = () => new CloudApiResult(
            CloudOutcomes.OutcomeFor(200, ""), "{\"data\":[{\"sequence_num\":\"41\"}]}");

        internal List<CloudRequestContext> Requests { get; } = new();

        internal List<CloudRequestContext> Posts => Where(r => r.Method == HttpMethod.Post);

        // The transcript reads, on the /v2 events path.
        internal List<CloudRequestContext> Transcripts =>
            Where(r => r.Method is null && r.Path.Contains("/events?", StringComparison.Ordinal)
                                        && !r.Path.StartsWith(CloudRequest.CodeSessionsPath, StringComparison.Ordinal));

        internal List<CloudRequestContext> NewestReads =>
            Where(r => r.Path == ClaudeCloudStreamRequest.NewestSequencePath("session_a"));

        // The live loop's reads of the session's own record.
        internal List<CloudRequestContext> Statuses =>
            Where(r => r.Path == CloudRequest.CodeSessionPath(r.Path.Split('/').Last()));

        private List<CloudRequestContext> Where(Func<CloudRequestContext, bool> keep)
        {
            lock (Requests) return Requests.Where(keep).ToList();
        }

        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            lock (Requests) Requests.Add(context);
            var isStatus = context.Method is null
                           && context.Path.StartsWith(CloudRequest.CodeSessionsPath + "/", StringComparison.Ordinal)
                           && !context.Path.Contains("/events", StringComparison.Ordinal);
            var isNewest = context.Path.EndsWith("?limit=1&sort_order=desc", StringComparison.Ordinal);
            return Task.FromResult(isStatus ? Status() : isNewest ? Newest() : _answer(context));
        }
    }

    private sealed class CountingCredentials : ICloudCredentialSource
    {
        internal CredentialRead Reading { get; set; } =
            new(CredentialOutcome.Found, Token, null, "a credential is present");

        internal int Reads { get; private set; }

        // Runs inside the read, before it answers — how a test closes the panel
        // while a read is in flight.
        internal Action? OnRead { get; set; }

        public string? Stamp() => "stamp-1";

        public CredentialRead Read()
        {
            Reads++;
            OnRead?.Invoke();
            return Reading;
        }
    }

    // Records what the session waited for and returns at once, so the cadence is
    // asserted rather than slept through. OnDelay runs before the wait returns,
    // which is how a test changes the world between two refreshes.
    private sealed class FakeClock
    {
        private readonly List<TimeSpan> _waits = new();
        private readonly List<CancellationToken> _tokens = new();

        // Snapshots, taken under the lock: a stream that keeps retrying waits on
        // its own thread while a test is reading these.
        internal List<TimeSpan> Waits
        {
            get { lock (_waits) return _waits.ToList(); }
        }

        internal List<CancellationToken> Tokens
        {
            get { lock (_waits) return _tokens.ToList(); }
        }

        internal Action<int, CancellationToken>? OnDelay { get; set; }

        // Park every wait until its token is cancelled, so a test can look at the
        // state a send leaves before the live loop's first tick.
        internal bool Hold { get; set; }

        internal Task Delay(TimeSpan wait, CancellationToken ct)
        {
            int count;
            lock (_waits)
            {
                _waits.Add(wait);
                _tokens.Add(ct);
                count = _waits.Count;
            }

            OnDelay?.Invoke(count, ct);
            return Hold ? Task.Delay(Timeout.Infinite, ct) : Task.CompletedTask;
        }
    }

    private static ClaudeCloudSessions.Session Row(string id = "session_a", string state = "idle",
        string bucket = "idle") =>
        new(id, "a cloud session", state, new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc),
            "https://claude.ai/code/" + id, bucket, false, null, null, null, null);

    private static ClaudeCloudSessions.Session BusyRow(string id = "session_a") =>
        Row(id, "generating", "working");

    private static ClaudeCloudChatSession Sender(ICloudApi api, FakeClock? clock = null,
        ICloudCredentialSource? creds = null, ClaudeCloudSessions.Session? session = null,
        bool enabled = true, ICloudEventStream? stream = null, CredentialBudget? budget = null)
    {
        var tick = clock ?? new FakeClock();
        var chat = new ClaudeCloudChatSession(session ?? Row(), api, creds ?? new CountingCredentials(),
            action => action())
        {
            ReadBudget = budget ?? TestBudgets.AlwaysAnswers,
            Delay = tick.Delay,
            Enabled = () => enabled,
            Stream = stream,
        };

        // So a wait for the next open can tell "not yet" from "never" by the
        // run itself rather than by a clock (CB-230).
        if (stream is FakeStream fake) fake.Run = () => chat.StreamTask;
        return chat;
    }

    private static string PayloadUuid(CloudRequestContext context)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(context.Body!);
        return doc.RootElement.GetProperty("events")[0].GetProperty("payload")
            .GetProperty("uuid").GetString()!;
    }

    private static List<ChatTurn> Notes(ClaudeCloudChatSession chat) =>
        chat.History.Where(t => t.Role == ChatRole.System).ToList();

    [Fact]
    public async Task ASendPostsTheMessageToTheWritePathAndRaisesTheBubbleOnlyAfterTheAnswer()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api);

        var postsWhenRaised = -1;
        chat.TurnAdded += _ => postsWhenRaised = api.Posts.Count;

        Assert.Equal(ChatSendOutcome.Sent, await chat.SendAsync("please do the thing"));
        await chat.LiveTask!;

        var post = Assert.Single(api.Posts);
        Assert.Equal(CloudRequest.CodeEventsPath("session_a"), post.Path);
        Assert.Equal(Token, post.AccessToken);
        Assert.Contains("please do the thing", post.Body!, StringComparison.Ordinal);

        // After the POST had answered, never before it.
        Assert.Equal(1, postsWhenRaised);

        var turn = Assert.Single(chat.History);
        Assert.Equal(ChatRole.User, turn.Role);
        Assert.Equal("please do the thing", turn.Text);
    }

    // The measured round trip: the echo comes back through /v2 carrying the uuid
    // that was sent, and it lands on the bubble already there.
    [Fact]
    public async Task TheEchoOfASentMessageFoldsIntoItsOwnBubble()
    {
        string? sentUuid = null;
        var api = new RoutingApi(c =>
        {
            if (c.Method == HttpMethod.Post)
            {
                sentUuid = PayloadUuid(c);
                return Answer(200, Receipt);
            }

            return new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""),
                Envelope(new[] { UserRow(sentUuid!, "please do the thing"), AssistantRow("a1", "done") }));
        });
        var chat = Sender(api);

        await chat.SendAsync("please do the thing");
        await chat.LiveTask!;

        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant }, chat.History.Select(t => t.Role));
    }

    // A duplicate is the retry having worked; an unreadable 2xx still landed.
    [Theory]
    [InlineData(DuplicateReceipt)]
    [InlineData("not json")]
    public async Task ADuplicateOrUnreadableReceiptStillCountsAsSent(string receipt)
    {
        var chat = Sender(new RoutingApi(Answer(200, receipt)));

        Assert.Equal(ChatSendOutcome.Sent, await chat.SendAsync("hello"));
        await chat.LiveTask!;
        Assert.Equal(ChatRole.User, Assert.Single(chat.History).Role);
    }

    // Each refusal that says nothing lasting about the session: a note, Failed,
    // no bubble, one request, and the box stays.
    [Theory]
    [InlineData(401, CloudOutcomes.TokenRefusedMarker, "Run `claude`")]
    [InlineData(401, "", "Run `claude`")]
    [InlineData(429, "", "Wait a minute")]
    [InlineData(413, "", "shorter message")]
    [InlineData(400, "", "request shape")]
    [InlineData(403, "<html>challenge</html>", "in front of the endpoint")]
    public async Task ARefusalAboutThisAttemptWritesANoteAndKeepsTheBox(int status, string body,
        string expected)
    {
        var api = new RoutingApi(Answer(status, body));
        var chat = Sender(api);
        var flips = 0;
        var announced = 0;
        chat.ReadOnlyChanged += () => flips++;
        chat.TurnAdded += _ => announced++;

        Assert.Equal(ChatSendOutcome.Failed, await chat.SendAsync("hello"));

        Assert.Single(api.Posts);
        Assert.Equal(1, announced);
        var note = Assert.Single(chat.History);
        Assert.Equal(ChatRole.System, note.Role);
        Assert.StartsWith("Not sent: ", note.Text, StringComparison.Ordinal);
        Assert.Contains(expected, note.Text, StringComparison.Ordinal);
        Assert.False(chat.IsReadOnly);
        Assert.Equal(0, flips);
    }

    // The three refusals that are facts about the session: the box goes, once,
    // and the next send spends no request.
    [Theory]
    [InlineData(409, "", "Ended", CloudChatSendability.EndedHint)]
    [InlineData(404, "", "Gone", CloudChatSendability.GoneHint)]
    [InlineData(403, ApiErrorBody, "NotPermitted", CloudChatSendability.NotPermittedHint)]
    public async Task ARefusalAboutTheSessionTakesTheBoxAway(int status, string body,
        string expected, string hint)
    {
        var api = new RoutingApi(Answer(status, body));
        var chat = Sender(api);
        var flips = 0;
        chat.ReadOnlyChanged += () => flips++;

        Assert.Equal(ChatSendOutcome.Failed, await chat.SendAsync("hello"));

        Assert.Single(api.Posts);
        Assert.Equal(ChatRole.System, Assert.Single(chat.History).Role);
        Assert.True(chat.IsReadOnly);
        Assert.Equal(expected, chat.Sendability.ToString());
        Assert.Equal(hint, chat.ComposerHint);
        Assert.Equal(1, flips);

        // The belt to the panel's braces: no box, and no request if one is sent anyway.
        Assert.Equal(ChatSendOutcome.Failed, await chat.SendAsync("again"));
        Assert.Single(api.Posts);
        Assert.Equal(1, flips);
        Assert.Contains(hint, Notes(chat).Last().Text, StringComparison.Ordinal);
    }

    // A refusal is an answer to the question actually asked, so a roster row that
    // still looks fine does not bring the box back.
    [Fact]
    public async Task ARefusalOutranksARosterRowThatStillLooksWritable()
    {
        var chat = Sender(new RoutingApi(Answer(404)));
        await chat.SendAsync("hello");

        chat.UpdateStatus(Row());

        Assert.Equal(CloudSendability.Gone, chat.Sendability);
    }

    [Fact]
    public async Task ATimeoutIsRetriedOnceWithTheSameUuidAndThenSaysItMayNotHaveArrived()
    {
        var clock = new FakeClock();
        var api = new RoutingApi(TimedOut);
        var chat = Sender(api, clock);

        Assert.Equal(ChatSendOutcome.Failed, await chat.SendAsync("hello"));

        Assert.Equal(2, api.Posts.Count);
        Assert.Equal(PayloadUuid(api.Posts[0]), PayloadUuid(api.Posts[1]));
        Assert.Equal(new[] { Backoff.UnavailableFloor }, clock.Waits);

        var note = Assert.Single(chat.History);
        Assert.Contains("may not have arrived", note.Text, StringComparison.Ordinal);
        Assert.False(chat.IsReadOnly);
    }

    [Fact]
    public async Task ATimeoutFollowedByAnAnswerIsSentWithOneBubble()
    {
        var calls = 0;
        var api = new RoutingApi(c => c.Method == HttpMethod.Post
            ? ++calls == 1 ? TimedOut : Answer(200, DuplicateReceipt)
            : new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""), Envelope(Array.Empty<string>())));
        var chat = Sender(api);

        Assert.Equal(ChatSendOutcome.Sent, await chat.SendAsync("hello"));
        await chat.LiveTask!;

        Assert.Equal(2, api.Posts.Count);
        Assert.Equal(PayloadUuid(api.Posts[0]), PayloadUuid(api.Posts[1]));
        Assert.Equal(ChatRole.User, Assert.Single(chat.History).Role);
    }

    // A timeout then a real refusal is reported as the refusal.
    [Fact]
    public async Task ATimeoutFollowedByARefusalReportsTheRefusal()
    {
        var calls = 0;
        var api = new RoutingApi(c => ++calls == 1 ? TimedOut : Answer(409));
        var chat = Sender(api);

        Assert.Equal(ChatSendOutcome.Failed, await chat.SendAsync("hello"));

        Assert.Equal(CloudSendability.Ended, chat.Sendability);
    }

    [Theory]
    [InlineData("NotLoggedIn")]
    [InlineData("Denied")]
    [InlineData("Found")] // Found, but with no token in it
    public async Task NoUsableCredentialMeansANoteAndNoRequest(string name)
    {
        var outcome = Enum.Parse<CredentialOutcome>(name);
        var api = new RoutingApi(Answer(200, Receipt));
        var creds = new CountingCredentials { Reading = new CredentialRead(outcome, null, null, "…") };
        var chat = Sender(api, creds: creds);

        Assert.Equal(ChatSendOutcome.Failed, await chat.SendAsync("hello"));

        Assert.Empty(api.Requests);
        Assert.Equal("Not sent: " + ClaudeCliCredentials.Describe(outcome) + ".",
            Assert.Single(chat.History).Text);
    }

    // An expired login says so, in the credential layer's own words, rather
    // than the generic "no login found" its outcome alone would describe.
    [Fact]
    public async Task AnExpiredLoginSaysItExpiredAndHowToRefreshIt()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var creds = new CountingCredentials
        {
            Reading = new CredentialRead(CredentialOutcome.NotLoggedIn, null, null, "…",
                Lead: ClaudeCliCredentials.ExpiredLead),
        };
        var chat = Sender(api, creds: creds, session: BusyRow());

        Assert.Equal(ChatSendOutcome.Failed, await chat.SendAsync("hello"));
        chat.Cancel();
        await chat.InterruptTask!;

        Assert.Empty(api.Requests);
        Assert.Equal(new[]
        {
            "Not sent: " + ClaudeCliCredentials.ExpiredLead + ".",
            "Stop did not reach the session: " + ClaudeCliCredentials.ExpiredLead + ".",
        }, chat.History.Select(t => t.Text));
    }

    // Off means no credential read and no socket, even for a panel opened before
    // the switch was flipped.
    [Fact]
    public async Task WithTheFeatureOffNothingIsReadAndNothingIsSent()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var creds = new CountingCredentials();
        var chat = Sender(api, creds: creds, enabled: false);

        Assert.Equal(ChatSendOutcome.Failed, await chat.SendAsync("hello"));

        Assert.Empty(api.Requests);
        Assert.Equal(0, creds.Reads);
        Assert.Contains("switched off", Assert.Single(chat.History).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedIdIsRefusedBeforeAnyRequest()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var creds = new CountingCredentials();
        var chat = Sender(api, creds: creds, session: Row("not a session"));

        Assert.Equal(ChatSendOutcome.Failed, await chat.SendAsync("hello"));

        Assert.Empty(api.Requests);
        Assert.Equal(0, creds.Reads);
        Assert.Contains("id", Assert.Single(chat.History).Text, StringComparison.Ordinal);
    }

    // The send path reads the source it was handed, once per send, and builds
    // none of its own.
    [Fact]
    public async Task TheSendReadsTheInjectedSourceOncePerMessage()
    {
        var creds = new CountingCredentials();
        var clock = new FakeClock();
        var chat = Sender(new RoutingApi(Answer(200, Receipt)), clock, creds);

        // Close the panel at the first wait, so no live-loop read joins the count.
        clock.OnDelay = (_, _) => chat.PanelClosed();

        await chat.SendAsync("one");
        await chat.LiveTask!;
        Assert.Equal(1, creds.Reads);

        await chat.SendAsync("two");
        await chat.LiveTask!;
        Assert.Equal(2, creds.Reads);
    }

    // The canary: the token reaches the Authorization header and nothing else —
    // not a note, not the hint, not a turn, and not the body that went out.
    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(413)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task TheTokenNeverReachesANoteTheHintATurnOrTheBody(int status)
    {
        var api = new RoutingApi(Answer(status, status == 200 ? Receipt : ApiErrorBody));
        var chat = Sender(api);

        await chat.SendAsync("hello");
        if (chat.LiveTask is { } followUp) await followUp;

        Assert.DoesNotContain(Token, chat.ComposerHint, StringComparison.Ordinal);
        Assert.All(chat.History,
            turn => Assert.DoesNotContain(Token, turn.Text, StringComparison.Ordinal));
        Assert.All(api.Requests,
            r => Assert.DoesNotContain(Token, r.Body ?? "", StringComparison.Ordinal));
    }

    // --- the live loop after a send ------------------------------------------
    //
    // Found on a real machine: the roster's thirty-second cycle meant a short
    // reply was never seen as busy, so Stop never appeared and the reply did not
    // build up. These pin the fix — busy from the 2xx, then the session's own
    // record every status interval, the transcript every transcript interval
    // while it runs, and one last read when it ends.

    private static CloudApiResult Status(string bucket) =>
        new(CloudOutcomes.OutcomeFor(200, ""), "{\"id\":\"session_a\",\"status_bucket\":\"" + bucket + "\"}");

    private static readonly TimeSpan Tick = ClaudeCloudChatSession.UnmeasuredLiveStatusInterval;

    [Fact]
    public async Task ASentMessageShowsStopAtOnceWithoutWaitingForTheRoster()
    {
        var clock = new FakeClock { Hold = true };
        var chat = Sender(new RoutingApi(Answer(200, Receipt)), clock);
        var stops = 0;
        chat.InterruptChanged += () => stops++;

        Assert.Equal(ChatSendOutcome.Sent, await chat.SendAsync("write me an essay"));

        Assert.True(chat.CanInterrupt);
        Assert.Equal(1, stops);
        Assert.Equal(CloudChatSendability.BusyHint, chat.ComposerHint);

        chat.PanelClosed();
        await chat.LiveTask!;
    }

    // The end of the turn, seen live: Stop goes, the finished reply is read
    // once, and the loop stops asking.
    [Fact]
    public async Task WhenTheTurnEndsStopGoesTheReplyIsReadAndTheLoopStops()
    {
        var clock = new FakeClock();
        var api = new RoutingApi(Answer(200, Receipt));
        var calls = 0;
        api.Status = () => Status(++calls < 3 ? "working" : "idle");
        var chat = Sender(api, clock);
        var stops = 0;
        chat.InterruptChanged += () => stops++;

        await chat.SendAsync("hello");
        await chat.LiveTask!;

        Assert.Equal(3, api.Statuses.Count);
        Assert.All(clock.Waits, w => Assert.Equal(Tick, w));
        Assert.Single(api.Transcripts); // the final read; two ticks is under the transcript interval
        Assert.False(chat.CanInterrupt);
        Assert.Equal(2, stops); // on at the send, off at the end
        Assert.Equal(CloudChatSendability.SendableHint, chat.ComposerHint);
    }

    // While it runs, the transcript is re-read on its own, longer interval, so
    // a reply builds up rather than arriving whole.
    [Fact]
    public async Task WhileTheTurnRunsTheTranscriptIsReadOnItsOwnInterval()
    {
        var clock = new FakeClock();
        var api = new RoutingApi(Answer(200, Receipt));
        var calls = 0;
        api.Status = () => Status(++calls < 7 ? "working" : "idle");
        var chat = Sender(api, clock);

        await chat.SendAsync("hello");
        await chat.LiveTask!;

        // Six working ticks of two seconds: a transcript read at the tick where
        // five seconds have gone by (the third) and again three ticks later,
        // then the final read at idle.
        var perRead = (int)Math.Ceiling(ClaudeCloudChatSession.UnmeasuredLiveTranscriptInterval / Tick);
        Assert.Equal(6 / perRead + 1, api.Transcripts.Count);
        Assert.Equal(7, api.Statuses.Count);
    }

    [Fact]
    public async Task ARunawayTurnIsWatchedOnlyUntilTheCap()
    {
        var clock = new FakeClock();
        var api = new RoutingApi(Answer(200, Receipt)) { Status = () => Status("working") };
        var chat = Sender(api, clock);

        await chat.SendAsync("hello");
        await chat.LiveTask!;

        var ticks = (int)(ClaudeCloudChatSession.UnmeasuredLiveCap / Tick);
        Assert.Equal(ticks, api.Statuses.Count);

        // Still busy: the roster owns the answer from here.
        Assert.True(chat.CanInterrupt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARateLimitPausesTheLoopWithANoteAndNoRetry(bool onTheTranscript)
    {
        var clock = new FakeClock();
        var api = onTheTranscript
            ? new RoutingApi(Answer(200, Receipt), Answer(429)) { Status = () => Status("working") }
            : new RoutingApi(Answer(200, Receipt)) { Status = () => Answer(429) };
        var chat = Sender(api, clock);

        await chat.SendAsync("hello");
        await chat.LiveTask!;

        Assert.Equal(ClaudeCloudChatSession.LivePausedNote, chat.History.Last().Text);
        Assert.Equal(onTheTranscript ? 3 : 1, api.Statuses.Count);
    }

    [Theory]
    [InlineData(404, "Gone")]
    [InlineData(409, "Ended")]
    public async Task ASessionThatEndsOrGoesMidTurnTakesTheBoxAway(int status, string expected)
    {
        var clock = new FakeClock();
        var api = new RoutingApi(Answer(200, Receipt)) { Status = () => Answer(status) };
        var chat = Sender(api, clock);
        var flips = 0;
        chat.ReadOnlyChanged += () => flips++;

        await chat.SendAsync("hello");
        await chat.LiveTask!;

        Assert.Equal(expected, chat.Sendability.ToString());
        Assert.Equal(1, flips);
        Assert.False(chat.CanInterrupt);
        Assert.Single(api.Statuses);
    }

    [Theory]
    [InlineData(401)]
    public async Task ALoginTheEndpointRefusesStopsTheLoopQuietly(int status)
    {
        var clock = new FakeClock();
        var api = new RoutingApi(Answer(200, Receipt)) { Status = () => Answer(status) };
        var chat = Sender(api, clock);

        await chat.SendAsync("hello");
        await chat.LiveTask!;

        Assert.Single(api.Statuses);
        Assert.Equal(ChatRole.User, Assert.Single(chat.History).Role);
    }

    // A timeout, a 5xx or a body with no bucket is no news: the loop asks again
    // rather than guessing, and above all does not read it as idle.
    [Fact]
    public async Task NoNewsIsAskedAgainAndNeverReadAsIdle()
    {
        var clock = new FakeClock();
        var api = new RoutingApi(Answer(200, Receipt));
        var calls = 0;
        api.Status = () => ++calls switch
        {
            1 => TimedOut,
            2 => Answer(500),
            3 => new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""), "{\"id\":\"session_a\"}"),
            _ => Status("idle"),
        };
        var chat = Sender(api, clock);
        var busyAtThird = false;
        clock.OnDelay = (n, _) => { if (n == 4) busyAtThird = chat.CanInterrupt; };

        await chat.SendAsync("hello");
        await chat.LiveTask!;

        Assert.True(busyAtThird);
        Assert.Equal(4, api.Statuses.Count);
        Assert.False(chat.CanInterrupt);
    }

    [Fact]
    public async Task NoCredentialForTheStatusReadStopsTheLoop()
    {
        var clock = new FakeClock();
        var creds = new CountingCredentials();
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api, clock, creds);
        clock.OnDelay = (_, _) =>
            creds.Reading = new CredentialRead(CredentialOutcome.NotLoggedIn, null, null, "…");

        await chat.SendAsync("hello");
        await chat.LiveTask!;

        Assert.Empty(api.Statuses);
        Assert.True(chat.CanInterrupt);
    }

    // Closing the panel stops the loop, whether the wait notices by throwing
    // (Task.Delay) or by returning into a cancelled token.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClosingThePanelStopsTheLoop(bool throws)
    {
        var clock = new FakeClock();
        var api = new RoutingApi(Answer(200, Receipt)) { Status = () => Status("working") };
        var chat = Sender(api, clock);
        chat.PanelOpened();
        clock.OnDelay = (_, ct) =>
        {
            chat.PanelClosed();
            if (throws) ct.ThrowIfCancellationRequested();
        };

        await chat.SendAsync("hello");
        await chat.LiveTask!;

        Assert.Empty(api.Statuses);
    }

    // The roster only updates the row while the live loop owns busy.
    [Fact]
    public async Task WhileTheLoopRunsTheRosterDoesNotDecideBusy()
    {
        var clock = new FakeClock { Hold = true };
        var chat = Sender(new RoutingApi(Answer(200, Receipt)), clock);

        await chat.SendAsync("hello");
        chat.UpdateStatus(Row()); // the roster has not seen the turn yet

        Assert.True(chat.CanInterrupt);

        chat.PanelClosed();
        await chat.LiveTask!;
    }

    // After the live read saw idle, a roster row still saying "working" is the
    // older observation and does not put Stop back — until the roster itself
    // has caught up once, after which it is believed again.
    [Fact]
    public async Task AStaleRosterRowDoesNotPutStopBack()
    {
        var clock = new FakeClock();
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api, clock);

        await chat.SendAsync("hello");
        await chat.LiveTask!;
        Assert.False(chat.CanInterrupt);

        chat.UpdateStatus(BusyRow());
        Assert.False(chat.CanInterrupt);

        chat.UpdateStatus(Row());
        chat.UpdateStatus(BusyRow());
        Assert.True(chat.CanInterrupt);
    }

    // A turn started somewhere else is watched live too, while a panel is open.
    [Fact]
    public async Task ATurnStartedElsewhereIsWatchedWhileThePanelIsOpen()
    {
        var clock = new FakeClock();
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api, clock);
        chat.PanelOpened();

        chat.UpdateStatus(BusyRow());
        await chat.LiveTask!;

        Assert.Single(api.Statuses);
        Assert.False(chat.CanInterrupt);
    }

    [Fact]
    public void ATurnStartedElsewhereIsNotWatchedForAClosedPanel()
    {
        var chat = Sender(new RoutingApi(Answer(200, Receipt)));

        chat.UpdateStatus(BusyRow());

        Assert.Null(chat.LiveTask);
    }

    // Opening onto a turn that is already running starts watching it; opening
    // onto an idle one, or a read-only one, does not.
    [Fact]
    public async Task OpeningOntoARunningTurnWatchesIt()
    {
        var clock = new FakeClock();
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api, clock, session: BusyRow());

        chat.PanelOpened();
        await chat.LiveTask!;

        Assert.Single(api.Statuses);
    }

    [Fact]
    public void OpeningOntoAnIdleOrEndedSessionWatchesNothing()
    {
        var idle = Sender(new RoutingApi(Answer(200, Receipt)));
        idle.PanelOpened();
        Assert.Null(idle.LiveTask);

        var ended = Sender(new RoutingApi(Answer(200, Receipt)),
            session: Row(state: "generating", bucket: "archived"));
        ended.PanelOpened();
        Assert.Null(ended.LiveTask);
    }

    // A session that stops taking input mid-loop — the roster's own answer —
    // ends the loop at the next tick without another request.
    [Fact]
    public async Task TheLoopStopsWhenTheRosterSaysTheSessionHasGone()
    {
        var clock = new FakeClock();
        var api = new RoutingApi(Answer(200, Receipt)) { Status = () => Status("working") };
        var chat = Sender(api, clock);
        clock.OnDelay = (n, _) => { if (n == 2) chat.UpdateStatus(null); };

        await chat.SendAsync("hello");
        await chat.LiveTask!;

        Assert.Single(api.Statuses);
    }

    // After a delivered Stop, something has to see the turn end; if no loop is
    // watching, the interrupt starts one.
    [Fact]
    public async Task ADeliveredStopStartsWatchingIfNothingIs()
    {
        var clock = new FakeClock();
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api, clock, session: BusyRow());

        chat.Cancel();
        await chat.InterruptTask!;
        await chat.LiveTask!;

        Assert.Single(api.Statuses);
        Assert.False(chat.CanInterrupt);
    }

    // The polling fallback, too, says once why it stopped when the login cannot
    // be read, rather than stopping in silence.
    [Fact]
    public async Task ThePollSaysWhyWhenTheLoginCannotBeRead()
    {
        var clock = new FakeClock();
        var api = new RoutingApi(Answer(200, Receipt));
        var creds = new CountingCredentials
        {
            Reading = new CredentialRead(CredentialOutcome.Denied, null, null, "…"),
        };
        var chat = Sender(api, clock, creds, session: BusyRow());

        chat.PanelOpened();
        await chat.LiveTask!;

        Assert.Empty(api.Requests);
        Assert.Equal("Live updates paused: " + ClaudeCliCredentials.Describe(CredentialOutcome.Denied) + ".",
            Assert.Single(chat.History).Text);
    }

    // A malformed id has no status path, so the loop stops without asking.
    [Fact]
    public async Task AMalformedIdIsNotPolled()
    {
        var clock = new FakeClock();
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api, clock, session: BusyRow("not a session"));

        chat.PanelOpened();
        await chat.LiveTask!;

        Assert.Empty(api.Requests);
    }

    // --- the /v1 status body ---------------------------------------------------

    [Theory]
    [InlineData("{\"status_bucket\":\"working\"}", "working")]
    [InlineData("{\"session\":{\"status_bucket\":\"idle\"}}", "idle")]
    [InlineData("{\"a\":1,\"session\":{\"status_bucket\":\"blocked\"}}", "blocked")]
    [InlineData("{\"status_bucket\":\"\",\"session\":{\"status_bucket\":\"idle\"}}", "idle")]
    public void TheBucketIsReadAtTheTopOrOneObjectDown(string body, string expected)
    {
        Assert.Equal(expected, CloudLiveStatus.BucketFrom(body));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"status_bucket\":3}")]
    [InlineData("{\"session\":{\"other\":\"x\"},\"n\":1}")]
    public void ABodyWithNoBucketIsUnknown(string? body)
    {
        Assert.Null(CloudLiveStatus.BucketFrom(body));
        Assert.Null(CloudLiveStatus.WorkingFrom(body));
    }

    [Theory]
    [InlineData("working", true)]
    [InlineData("WORKING", true)]
    [InlineData("idle", false)]
    [InlineData("blocked", false)]
    public void WorkingIsTheRostersWorkingBucket(string bucket, bool working)
    {
        Assert.Equal(working, CloudLiveStatus.WorkingFrom("{\"status_bucket\":\"" + bucket + "\"}"));
    }

    // The canary for the new request: the token rides the Authorization header
    // only, and the read goes through the injected source.
    [Fact]
    public async Task TheStatusReadCarriesTheTokenOnlyInTheHeader()
    {
        var clock = new FakeClock();
        var creds = new CountingCredentials();
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api, clock, creds);

        await chat.SendAsync("hello");
        await chat.LiveTask!;

        var status = Assert.Single(api.Statuses);
        Assert.Equal(Token, status.AccessToken);
        Assert.Null(status.Body);
        Assert.DoesNotContain(Token, status.Path, StringComparison.Ordinal);
        Assert.Equal(3, creds.Reads); // the send, the status read, the final transcript read
    }

    // Closing a panel that never sent anything has nothing to stop.
    [Fact]
    public void ClosingAPanelThatNeverSentIsHarmless()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api);

        chat.PanelOpened();
        chat.PanelClosed();

        Assert.Null(chat.LiveTask);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task ASecondSendReplacesTheFirstSendsLoop()
    {
        var clock = new FakeClock();
        var chat = Sender(new RoutingApi(Answer(200, Receipt)), clock);

        await chat.SendAsync("one");
        var first = chat.LiveTask!;
        await chat.SendAsync("two");
        await first;
        await chat.LiveTask!;

        Assert.True(clock.Tokens[0].IsCancellationRequested);
        Assert.False(clock.Tokens[1].IsCancellationRequested);
    }

    // Single flight: an open, the live loop and the end of a turn can all ask at
    // once, and the transcript is read once for all of them.
    [Fact]
    public async Task OverlappingLoadsShareOneRead()
    {
        var gate = new TaskCompletionSource<CloudApiResult>();
        var api = new GatedApi(gate.Task);
        var chat = Build(api);

        var first = chat.LoadAsync(CancellationToken.None);
        var second = chat.LoadAsync(CancellationToken.None);

        gate.SetResult(new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""),
            Envelope(new[] { UserRow("u1", "hello") })));

        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(1, api.Calls);
        Assert.Single(chat.History);

        // And once it has finished, the next load reads again.
        Assert.True(await chat.LoadAsync(CancellationToken.None));
        Assert.Equal(2, api.Calls);
    }

    private sealed class GatedApi : ICloudApi
    {
        private readonly Task<CloudApiResult> _first;

        internal GatedApi(Task<CloudApiResult> first) => _first = first;

        internal int Calls { get; private set; }

        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            Calls++;
            return _first;
        }
    }

    // --- live state ------------------------------------------------------------

    [Fact]
    public void BusyIsSendableAndSaysTheMessageWillWait()
    {
        var chat = Sender(new RoutingApi(Answer(200, Receipt)), session: BusyRow());

        Assert.False(chat.IsReadOnly);
        Assert.True(chat.CanInterrupt);
        Assert.Equal(CloudChatSendability.BusyHint, chat.ComposerHint);
    }

    [Fact]
    public void AnIdleSessionSaysOnlyMessage()
    {
        var chat = Sender(new RoutingApi(Answer(200, Receipt)));

        Assert.False(chat.IsReadOnly);
        Assert.False(chat.CanInterrupt);
        Assert.Equal(CloudChatSendability.SendableHint, chat.ComposerHint);
    }

    [Fact]
    public void TheRosterMovesStopBothWaysAndSaysSoOnce()
    {
        var chat = Sender(new RoutingApi(Answer(200, Receipt)));
        var changes = 0;
        chat.InterruptChanged += () => changes++;

        chat.UpdateStatus(BusyRow());
        Assert.True(chat.CanInterrupt);
        chat.UpdateStatus(BusyRow());
        Assert.Equal(1, changes);

        chat.UpdateStatus(Row());
        Assert.False(chat.CanInterrupt);
        Assert.Equal(2, changes);
    }

    // A vanished row reads as Gone, and a row that comes back — the roster is
    // replaced whole, and a Restart empties it for a moment — brings the box back.
    [Fact]
    public void AVanishedRowIsGoneUntilItComesBack()
    {
        var chat = Sender(new RoutingApi(Answer(200, Receipt)), session: BusyRow());
        var flips = 0;
        var stops = 0;
        chat.ReadOnlyChanged += () => flips++;
        chat.InterruptChanged += () => stops++;

        chat.UpdateStatus(null);
        Assert.Equal(CloudSendability.Gone, chat.Sendability);
        Assert.Equal(CloudChatSendability.GoneHint, chat.ComposerHint);
        Assert.False(chat.CanInterrupt);
        Assert.Equal(1, flips);
        Assert.Equal(1, stops);

        chat.UpdateStatus(Row());
        Assert.False(chat.IsReadOnly);
        Assert.Equal(2, flips);
    }

    [Fact]
    public void AnEndedBucketIsEnded()
    {
        var chat = Sender(new RoutingApi(Answer(200, Receipt)));

        chat.UpdateStatus(Row(bucket: "failed"));

        Assert.Equal(CloudSendability.Ended, chat.Sendability);
        Assert.Equal(CloudChatSendability.EndedHint, chat.ComposerHint);
    }

    // The end of a turn is worth one read, for a panel someone is looking at.
    [Fact]
    public async Task TheEndOfATurnIsReadOnceWhileThePanelIsOpen()
    {
        // The live loop opening the panel starts is stopped at once (a login the
        // status read is refused with), so the roster is what sees the turn end.
        var api = new RoutingApi(Answer(200, Receipt)) { Status = () => Answer(401) };
        var chat = Sender(api, session: BusyRow());
        chat.PanelOpened();
        await chat.LiveTask!;

        chat.UpdateStatus(Row());

        Assert.NotNull(chat.LoadTask);
        await chat.LoadTask!;
        Assert.Contains("/events?", Assert.Single(api.Transcripts).Path, StringComparison.Ordinal);

        // Idle to idle is not the end of a turn.
        chat.UpdateStatus(Row());
        Assert.Single(api.Transcripts);
    }

    [Fact]
    public void TheEndOfATurnIsNotReadForAClosedPanel()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api, session: BusyRow());

        chat.UpdateStatus(Row());

        Assert.Null(chat.LoadTask);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task TheEndOfATurnIsNotReadForASessionThatHasGone()
    {
        var api = new RoutingApi(Answer(200, Receipt)) { Status = () => Answer(401) };
        var chat = Sender(api, session: BusyRow());
        chat.PanelOpened();
        await chat.LiveTask!;

        chat.UpdateStatus(null);

        Assert.Null(chat.LoadTask);
        Assert.Empty(api.Transcripts);
    }

    // --- stopping -------------------------------------------------------------

    [Fact]
    public async Task StopPostsAnInterruptOnlyWhileBusyAndHidesItselfAtOnce()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api, session: BusyRow());
        var changes = 0;
        chat.InterruptChanged += () => changes++;

        chat.Cancel();

        Assert.False(chat.CanInterrupt);
        Assert.Equal(1, changes);
        await chat.InterruptTask!;

        var post = Assert.Single(api.Posts);
        Assert.Equal(CloudRequest.CodeEventsPath("session_a"), post.Path);
        Assert.Contains("\"subtype\":\"interrupt\"", post.Body!, StringComparison.Ordinal);
        Assert.Empty(chat.History);

        // A second press for the same turn spends nothing.
        chat.Cancel();
        Assert.Single(api.Posts);
    }

    [Fact]
    public void StopOnAnIdleSessionSpendsNoRequest()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api);

        chat.Cancel();

        Assert.Null(chat.InterruptTask);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task AStopThatDoesNotArriveSaysSoAndComesBack()
    {
        var api = new RoutingApi(Answer(500));
        var chat = Sender(api, session: BusyRow());
        var changes = 0;
        chat.InterruptChanged += () => changes++;

        chat.Cancel();
        await chat.InterruptTask!;

        Assert.True(chat.CanInterrupt);
        Assert.Equal(2, changes);
        Assert.StartsWith("Stop did not reach the session: ", Assert.Single(chat.History).Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStopWithNoCredentialSaysWhy()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var creds = new CountingCredentials
        {
            Reading = new CredentialRead(CredentialOutcome.NotLoggedIn, null, null, "…"),
        };
        var chat = Sender(api, creds: creds, session: BusyRow());

        chat.Cancel();
        await chat.InterruptTask!;

        Assert.Empty(api.Requests);
        Assert.Contains(ClaudeCliCredentials.Describe(CredentialOutcome.NotLoggedIn),
            Assert.Single(chat.History).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStopForAMalformedIdIsRefusedBeforeAnyRequest()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api, session: BusyRow("not a session"));

        chat.Cancel();
        await chat.InterruptTask!;

        Assert.Empty(api.Requests);
        Assert.Contains("id", Assert.Single(chat.History).Text, StringComparison.Ordinal);
    }

    // After a Stop, the turn ending re-arms it for the next one; so does a send.
    [Fact]
    public async Task StopComesBackForTheNextTurn()
    {
        var clock = new FakeClock();
        var chat = Sender(new RoutingApi(Answer(200, Receipt)), clock, session: BusyRow());

        // Hidden at once, then the loop the delivered Stop starts sees idle.
        chat.Cancel();
        Assert.False(chat.CanInterrupt);
        await chat.InterruptTask!;
        await chat.LiveTask!;
        Assert.False(chat.CanInterrupt);

        // A later turn the roster reports is stoppable again.
        chat.UpdateStatus(Row());
        chat.UpdateStatus(BusyRow());
        Assert.True(chat.CanInterrupt);

        chat.Cancel();
        Assert.False(chat.CanInterrupt);
        await chat.InterruptTask!;
        await chat.LiveTask!;

        // And so is the turn a send starts. The clock is parked first, so the
        // loop that send starts cannot see "idle" before this looks.
        clock.Hold = true;
        await chat.SendAsync("and another thing");
        Assert.True(chat.CanInterrupt);
        chat.PanelClosed();
        await chat.LiveTask!;
    }

    // --- the rules, without a session -------------------------------------------

    [Theory]
    [InlineData(409, "", "Ended")]
    [InlineData(404, "", "Gone")]
    [InlineData(403, ApiErrorBody, "NotPermitted")]
    public void ARefusalAboutTheSessionIsRecognised(int status, string body, string expected)
    {
        Assert.Equal(expected,
            CloudChatSendability.RefusalFor(CloudOutcomes.OutcomeFor(status, body)).ToString());
    }

    [Theory]
    [InlineData(200, "")]
    [InlineData(401, "")]
    [InlineData(403, "<html></html>")]
    [InlineData(413, "")]
    [InlineData(429, "")]
    [InlineData(500, "")]
    public void EverythingElseSaysNothingLastingAboutTheSession(int status, string body)
    {
        Assert.Null(CloudChatSendability.RefusalFor(CloudOutcomes.OutcomeFor(status, body)));
    }

    [Fact]
    public void TheRulesCombineInOrder()
    {
        Assert.Equal(CloudSendability.NotPermitted,
            CloudChatSendability.For(CloudSendability.NotPermitted, Row()));
        Assert.Equal(CloudSendability.Gone, CloudChatSendability.For(null, null));
        Assert.Equal(CloudSendability.Ended, CloudChatSendability.For(null, Row(bucket: "ARCHIVED")));
        Assert.Equal(CloudSendability.Sendable, CloudChatSendability.For(null, Row(bucket: "blocked")));
    }

    [Fact]
    public void EachStateHasItsOwnHint()
    {
        Assert.Equal(CloudChatSendability.SendableHint,
            CloudChatSendability.HintFor(CloudSendability.Sendable, busy: false));
        Assert.Equal(CloudChatSendability.BusyHint,
            CloudChatSendability.HintFor(CloudSendability.Sendable, busy: true));
        Assert.Equal(CloudChatSendability.EndedHint,
            CloudChatSendability.HintFor(CloudSendability.Ended, busy: true));
        Assert.Equal(CloudChatSendability.GoneHint,
            CloudChatSendability.HintFor(CloudSendability.Gone, busy: false));
        Assert.Equal(CloudChatSendability.NotPermittedHint,
            CloudChatSendability.HintFor(CloudSendability.NotPermitted, busy: false));
    }

    // Still true: the session says where it can be opened, and the hint beside
    // the link is never empty.
    [Fact]
    public void ItSaysWhereItCanBeOpenedAndItsHintIsNeverEmpty()
    {
        var chat = Build(new FakeApi(_ => throw new InvalidOperationException()));

        var readOnly = Assert.IsAssignableFrom<IRemoteChatReadOnly>(chat);

        Assert.False(readOnly.IsReadOnly);
        Assert.Contains("claude.ai/code", readOnly.ReplyUrl!, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(
            Assert.IsAssignableFrom<IRemoteChatComposer>(chat).ComposerHint));
    }

    // A row that arrived with no address gets no link rather than a link to
    // nowhere. The roster builds the url from the id so this should not happen,
    // which is exactly why it is worth pinning: the panel's guard is on null, and
    // an empty string would sail past it.
    [Fact]
    public void ASessionWithNoAddressOffersNoReplyUrl()
    {
        var chat = Build(
            new FakeApi(_ => throw new InvalidOperationException()),
            session: new ClaudeCloudSessions.Session(
                "session_a", "a cloud session", "idle",
                new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc),
                "", "idle", false, null, null, null, null));

        Assert.Null(Assert.IsAssignableFrom<IRemoteChatReadOnly>(chat).ReplyUrl);
    }

    // --- identity ------------------------------------------------------------

    [Fact]
    public void ThePanelTitleIsTheSessionTitle()
    {
        var chat = Build(new FakeApi(_ => throw new InvalidOperationException()),
            session: Session("session_a", "refactor the parser"));

        Assert.Equal("session_a", chat.SessionId);
        Assert.Equal("refactor the parser", chat.DisplayName);
    }

    // A cloud session can have no title — an id is a worse name than a title and a
    // better one than an empty header.
    [Fact]
    public void AnUntitledSessionFallsBackToItsId()
    {
        var chat = Build(new FakeApi(_ => throw new InvalidOperationException()),
            session: Session("session_a", "   "));

        Assert.Equal("session_a", chat.DisplayName);
    }

    [Fact]
    public void ANewSessionStartsConnectingRatherThanConnected()
    {
        Assert.Equal(RemoteChatState.Connecting,
            Build(new FakeApi(_ => throw new InvalidOperationException())).State);
    }

    // The token reaches nothing the panel shows.
    [Fact]
    public async Task TheTokenNeverReachesTheTranscriptOrTheHint()
    {
        var api = new FakeApi(_ => new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""),
            Envelope(new[] { UserRow("u1", "hello") })));

        var chat = Build(api);
        await chat.LoadAsync(CancellationToken.None);

        Assert.DoesNotContain(Token, chat.ComposerHint, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, chat.DisplayName, StringComparison.Ordinal);
        Assert.All(chat.History,
            turn => Assert.DoesNotContain(Token, turn.Text, StringComparison.Ordinal));
    }
}
