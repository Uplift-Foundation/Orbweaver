using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Orbweaver
{
    // Reading a cloud session's transcript.
    //
    // **`/v2/ccr-sessions/<id>/events` returns Claude Code's own transcript
    // format** — rows with a `type`, a `message` carrying a `role` and content
    // blocks of `text`, `thinking`, `tool_use` and `tool_result`. That is the
    // format ChatTranscript already reads, tested by
    // `dotnet run --project tests/TranscriptTests` and covered case by case. So
    // this file parses an envelope and hands the rows to that parser rather than
    // writing a second one: two parsers over one format is how a panel comes to
    // show something a terminal does not.
    internal static class ClaudeCloudEvents
    {
        // What one page of events yielded.
        internal sealed record Page(
            IReadOnlyList<ChatTranscript.Row> Rows,
            bool HasMore,
            string? LastId,
            bool Parsed);

        internal static Page ParsePage(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new Page(Array.Empty<ChatTranscript.Row>(), false, null, false);
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return new Page(Array.Empty<ChatTranscript.Row>(), false, null, false);
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array)
                {
                    return new Page(Array.Empty<ChatTranscript.Row>(), false, null, false);
                }

                // **Re-serialised rather than handed over as raw text.**
                // ChatTranscript.IsInteresting is a substring test over
                // `"type":"assistant"` — deliberately, so a megabyte of file
                // history is never turned into a JsonDocument — and that test is
                // sensitive to whitespace the way any substring test is. An API
                // that pretty-printed its response would make every row
                // uninteresting and the panel would show an empty conversation
                // with nothing anywhere saying why. JsonSerializer writes the
                // element back compactly and in document order, which is the shape
                // the parser was written against.
                var lines = data.EnumerateArray()
                    .Select(element => JsonSerializer.Serialize(element))
                    .ToList();

                var rows = ChatTranscript.Map(lines);

                var hasMore = root.TryGetProperty("has_more", out var more)
                              && more.ValueKind == JsonValueKind.True;

                var lastId = root.TryGetProperty("last_id", out var last)
                             && last.ValueKind == JsonValueKind.String
                    ? last.GetString()
                    : null;

                return new Page(rows, hasMore,
                    string.IsNullOrWhiteSpace(lastId) ? null : lastId, true);
            }
        }
    }


    // Whether a cloud session will take a message from this panel.
    //
    // **Busy is not here, and that is a measurement rather than an omission.**
    // CB-199's gate sent into a session mid-turn and got a 200, and the message
    // landed as the next turn once the running one finished — the server queues
    // it. So a busy session is Sendable, and the only thing busy changes is what
    // the empty box says.
    internal enum CloudSendability
    {
        Sendable,

        // It no longer takes input: a 409 on a write — measured by CB-225 as
        // `session_not_active` on an archived session — or a roster row whose
        // bucket says the session is over.
        Ended,

        // It is not there any more: a 404 on a write, or a cached session whose
        // roster row has disappeared. The roster drops archived rows, so a
        // vanished row is archived *or* deleted and the hint says both.
        Gone,

        // The API refused this login a write to this session — a 403 that came
        // from the API rather than from something in front of it. Learned from
        // the first refusal, because there is no pre-flight: the gate measured
        // an empty POST answering 400 to a real token and a bogus one alike, so
        // nothing short of a real message tells a permitted login from a
        // refused one. Per session, not process-wide: one refusal is evidence
        // about one session.
        NotPermitted,
    }

    // The decisions behind CloudSendability, with no session, no network and
    // no dispatcher behind them, so each is testable a case at a time.
    internal static class CloudChatSendability
    {
        // The roster buckets that mean a session is over. **Not measured**: the
        // gate saw "idle", "working" and "blocked" and never an ended session,
        // and the roster already drops `archived` rows before they get here. These
        // two are the plausible names and nothing more, which is why a bucket
        // this list does not know reads as sendable — a refused send then says
        // what actually happened, where a wrong guess here would hide the box
        // over a session that would have taken the message.
        internal static readonly string[] UnmeasuredEndedBuckets = { "archived", "failed" };

        // What a write's refusal says about the session, or null when it says
        // nothing lasting — a timeout, a rate limit, a token the CLI has not
        // refreshed yet, or an edge block, none of which is a fact about *this*
        // session and all of which the next attempt may not repeat.
        internal static CloudSendability? RefusalFor(CloudOutcome outcome) => outcome.Kind switch
        {
            CloudOutcomeKind.SessionInactive => CloudSendability.Ended,
            CloudOutcomeKind.SessionGone => CloudSendability.Gone,

            // Only the API's own refusal. An edge block says nothing about the
            // account, which is the CB-164 lesson CloudOutcomes.EdgeBlockedDetail
            // is written around, and it must not take the box away.
            CloudOutcomeKind.Blocked when outcome.Detail == CloudOutcomes.AccountBlockedDetail =>
                CloudSendability.NotPermitted,

            _ => null,
        };

        internal static bool RosterSaysEnded(ClaudeCloudSessions.Session row) =>
            UnmeasuredEndedBuckets.Any(b =>
                string.Equals(row.StatusBucket, b, StringComparison.OrdinalIgnoreCase));

        // A refusal the server gave outranks anything the roster says, because it
        // is an answer to the question actually being asked. Past that, a row
        // that has gone is Gone and one whose bucket says it is over is Ended.
        internal static CloudSendability For(CloudSendability? refusal,
            ClaudeCloudSessions.Session? row)
        {
            if (refusal is { } refused) return refused;
            if (row is null) return CloudSendability.Gone;
            return RosterSaysEnded(row) ? CloudSendability.Ended : CloudSendability.Sendable;
        }

        internal const string SendableHint = "Message…";

        // Short, because it is a watermark. Says the one thing a person would
        // otherwise get wrong: the message is not lost, it is waiting.
        internal const string BusyHint = "Message… (queued after this turn)";

        internal const string EndedHint = "This session has ended and no longer takes messages.";

        internal const string GoneHint = "This session was archived or deleted, so it can't take messages.";

        internal const string NotPermittedHint = "The API refused messages to this session from this login.";

        internal static string HintFor(CloudSendability sendability, bool busy) => sendability switch
        {
            CloudSendability.Ended => EndedHint,
            CloudSendability.Gone => GoneHint,
            CloudSendability.NotPermitted => NotPermittedHint,
            _ => busy ? BusyHint : SendableHint,
        };
    }

    // What the session's own record on the write host says about its turn.
    //
    // Minimal on purpose: the live loop needs one fact, whether a turn is
    // running, and reading more of a body nobody here controls is more to break.
    // **The field is measured, its place in the body is not.** CB-199's gate read
    // `status_bucket` "working" off `GET /v1/code/sessions/<id>` mid-turn; the
    // findings record the value and not the nesting, so this reads it at the top
    // level first and then one object down. A body with neither is "unknown",
    // which the loop treats as no news rather than as idle — flipping Stop off
    // on a shape change would be exactly the silent wrong answer CB-164 warns of.
    internal static class CloudLiveStatus
    {
        internal const string BucketField = "status_bucket";

        internal static string? BucketFrom(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                if (StringField(root) is { } top) return top;

                foreach (var property in root.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Object
                        && StringField(property.Value) is { } nested)
                    {
                        return nested;
                    }
                }

                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Null for unknown; otherwise whether the bucket is the working one —
        // the same bucket, and the same comparison, as ClaudeCloudRoster.IsBusy.
        internal static bool? WorkingFrom(string? body) =>
            BucketFrom(body) is { } bucket
                ? string.Equals(bucket, ClaudeCloudRoster.WorkingBucket, StringComparison.OrdinalIgnoreCase)
                : null;

        private static string? StringField(JsonElement element) =>
            element.TryGetProperty(BucketField, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;
    }

    // The pure half of reading the stream that is the panel's rather than the
    // transport's: a durable event's payload as the transcript rows the history
    // already uses.
    internal static class CloudStreamRows
    {
        // Is this `user` row a message somebody typed, rather than a tool result
        // handed back mid-turn? Text content — a string, or blocks with a text
        // block among them — and no tool_result block. Everything else, and
        // anything this cannot read, is not a new turn.
        internal static bool IsTypedMessage(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;

                var row = root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object
                    ? payload
                    : root;

                if (!row.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("content", out var content))
                {
                    return false;
                }

                if (content.ValueKind == JsonValueKind.String) return !string.IsNullOrWhiteSpace(content.GetString());
                if (content.ValueKind != JsonValueKind.Array) return false;

                var text = false;
                foreach (var block in content.EnumerateArray())
                {
                    var type = block.ValueKind == JsonValueKind.Object
                               && block.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString()
                        : null;

                    if (type == "tool_result") return false;
                    if (type == "text") text = true;
                }

                return text;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        // The row is the event's `payload` object when there is one, otherwise
        // the event itself — the contract names a payload, and nobody has seen
        // the wrapper with their own eyes. Re-serialised compactly for the reason
        // ClaudeCloudEvents.ParsePage gives: ChatTranscript's row test is a
        // substring test and cares about whitespace.
        internal static IReadOnlyList<ChatTranscript.Row> RowsFrom(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<ChatTranscript.Row>();

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return Array.Empty<ChatTranscript.Row>();

                var row = root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object
                    ? payload
                    : root;

                return ChatTranscript.Map(new[] { JsonSerializer.Serialize(row) });
            }
            catch (JsonException)
            {
                return Array.Empty<ChatTranscript.Row>();
            }
        }
    }

    // A cloud session, as the chat panel sees it.
    //
    // Polled rather than streamed, and able to send. There is no live stream to
    // subscribe to here the way OpenClaw's gateway offers one, so the panel
    // re-reads the events endpoint and reconciles what came back against what it
    // is already showing — by `uuid`, which the transcript rows carry, so a turn
    // that grew a paragraph updates in place rather than appearing twice.
    //
    // **Two hosts, one each way, both measured.** History is read from
    // `/v2/ccr-sessions/{id}/events`, as before; a message goes out through
    // `POST /v1/code/sessions/{id}/events`, which is where the Claude Code CLI
    // writes. CB-199's gate sent through the second and read the result back
    // through the first: one User row, carrying the uuid this class minted,
    // about ten seconds after the POST. That round trip is what lets the bubble
    // this class raises on a 2xx and the row the history later returns be the
    // same bubble.
    internal sealed class ClaudeCloudChatSession : IRemoteChatSession, IRemoteChatComposer,
        IRemoteChatReadOnly, IRemoteChatMachine, IRemoteChatInterrupt
    {
        // How much of a long transcript the panel is given. The interface's own
        // contract is that history arrives already bounded and ordered oldest to
        // newest, and the panel never trims — so the trimming is here.
        //
        // **Not measured.** Nobody has counted how long a cloud session's
        // transcript runs, and this is a round number chosen to be more than a
        // person scrolls back through in a panel. It is the kind of constant that
        // becomes a bug when a real transcript turns out to be shorter than it.
        internal const int HistoryTurns = 200;

        // How many pages of events one load will ask for. Same posture as the
        // roster's page cap, same absence of a measurement behind the number, and
        // the same rule that hitting it must not be silent — see LoadAsync.
        internal const int MaxEventPages = 10;

        // How the live loop paces itself while a panel watches a reply.
        //
        // **Why a loop of its own at all.** The roster is the only other source
        // of "is a turn running", and its short cycle is thirty seconds — so a
        // ten-to-thirty second reply was usually never seen as busy: Stop never
        // appeared, and the reply arrived all at once or not until reopen. That
        // was found on a real machine, not predicted by a test. The session's own
        // record answers the question directly, and an interrupt was measured
        // taking it to idle within about a second, so a short status interval is
        // what lets Stop go away when it should.
        //
        // **All three are not measured.** Two seconds is "about as long as an
        // interrupt takes to show"; five seconds of transcript is "a paragraph
        // at a time" and is re-read whole each time, so it is the costlier of
        // the two; the cap bounds a panel left open on a runaway turn. Past it,
        // the roster push owns the busy state again, and still fetches the
        // finished reply when it sees the turn end.
        internal static readonly TimeSpan UnmeasuredLiveStatusInterval = TimeSpan.FromSeconds(2);
        internal static readonly TimeSpan UnmeasuredLiveTranscriptInterval = TimeSpan.FromSeconds(5);
        internal static readonly TimeSpan UnmeasuredLiveCap = TimeSpan.FromMinutes(2);

        internal const string LivePausedNote =
            "Live updates paused: the endpoint is rate limiting us. The reply will still appear once the turn ends.";

        private readonly ICloudApi _api;
        private ICloudCredentialSource _credentials;
        private readonly Action<Action> _post;
        private readonly List<ChatTurn> _history = new();
        private readonly Dictionary<string, ChatTurn> _byUuid = new(StringComparer.Ordinal);
        private readonly object _loadGate = new();

        private RemoteChatState _state = RemoteChatState.Connecting;

        // What the roster last said, and what the server has refused. Kept
        // apart because they fail differently: a row can come back (the roster is
        // replaced whole, and a Restart empties it for a moment), a 404 cannot.
        private ClaudeCloudSessions.Session? _row;
        private CloudSendability? _refusal;
        private volatile bool _busy;
        private bool _interruptSent;
        private bool _panelOpen;

        // Set when the live loop saw the turn end, and cleared once the roster
        // agrees. Between the two, a roster row still saying "working" is older
        // news than the live read and is not allowed to put Stop back.
        private bool _liveSaidIdle;

        private Task<LoadResult>? _inFlight;

        // The stream (CB-199): the live assistant bubble a text_delta is growing.
        // Touched only inside _post, on the UI thread. Where a reconnect resumes
        // is not here: it belongs to one run of StreamAsync, so a run that has
        // been cancelled cannot move the next run's starting point.
        private ChatTurn? _liveTurn;

        // Whether this panel has already said that live updates stopped for want
        // of a login. Once per open or send, so a poll tick and a stream reopen
        // do not each repeat the same sentence.
        private bool _notedLivePause;

        // Set once the stored assistant message has taken over the live bubble,
        // cleared by the next message_start. Measured: order is not guaranteed
        // across event kinds, and the durable message can arrive before the
        // trailing deltas of the message it stores — which, drawn, would start a
        // second bubble repeating the end of the first.
        private bool _deltasSuperseded;
        private volatile bool _streamFellBack;
        private CancellationTokenSource? _streamCts;
        private CancellationTokenSource? _live;

        // How long a credential read is given before this panel gives up on it.
        // An init-only property rather than a constructor parameter so the shape
        // the UI layer constructs stays four arguments; tests set it to
        // milliseconds to drive the give-up path. See
        // ClaudeCliCredentials.ReadWithinAsync for why a budget exists at all.
        internal CredentialBudget ReadBudget { get; init; } = CredentialBudget.Unmeasured;

        // The clock the live loop and the one retry wait on. Task.Delay in
        // the app; a test hands in one that returns at once and records what it
        // was asked for, so the cadence is asserted rather than slept through.
        internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

        // The clock a stream connection's age is read off, for the policy's
        // "stayed open long enough to count as healthy". A seam for the reason
        // Delay is one: a test does not wait a minute to reach that arm.
        internal Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

        // The settings gate, read at the moment of sending. Off means no
        // credential read and no socket, which is the promise the settings copy
        // makes for the whole cloud arm; a panel opened before the switch was
        // flipped must not be a way round it. A seam so a test does not depend on
        // the settings file of the machine running it.
        internal Func<bool> Enabled { get; init; } = () => OrbweaverSettings.ClaudeCloudEnabled;

        // The live event stream, primary whenever there is one. Null means none —
        // the polling loop is then the only live source, which is also what this
        // session falls back to when the stream cannot be kept open.
        internal ICloudEventStream? Stream { get; init; }



        // The seam that keeps this class testable without an Avalonia app.
        //
        // Requirement 1 on any IRemoteChatSession is that every event is raised on
        // the UI thread, and the implementation owns that rather than every
        // consumer hopping threads by hand. A parameter rather than a direct
        // Dispatcher call so a test can pass `action => action()` and assert on
        // what was raised, which is the same argument OrbGlyph makes for taking
        // the two-letter setting instead of reading it.
        internal ClaudeCloudChatSession(
            ClaudeCloudSessions.Session session,
            ICloudApi api,
            ICloudCredentialSource credentials,
            Action<Action>? post = null)
        {
            SessionId = session.Id;
            DisplayName = string.IsNullOrWhiteSpace(session.Title) ? session.Id : session.Title;
            ReplyUrl = string.IsNullOrWhiteSpace(session.Url) ? null : session.Url;
            _row = session;
            _busy = ClaudeCloudRoster.IsBusy(session);
            _api = api;
            _credentials = credentials;
            _post = post ?? (action => Dispatcher.UIThread.Post(action));
        }

        // Re-point at the login that owns the session now. Loads, sends and Stop
        // all re-read the source on every call and none keeps a token, so swapping
        // it is all a change of owner needs.
        internal void UseCredentials(ICloudCredentialSource credentials) => _credentials = credentials;

        public string SessionId { get; }

        public string DisplayName { get; }

        public RemoteChatState State => _state;

        public IReadOnlyList<ChatTurn> History => _history;

        public event Action<ChatTurn>? TurnAdded;
        public event Action<ChatTurn>? TurnUpdated;
        public event Action<RemoteChatState>? StateChanged;
        public event Action? ReadOnlyChanged;
        public event Action? InterruptChanged;

        internal CloudSendability Sendability => CloudChatSendability.For(_refusal, _row);

        public bool IsReadOnly => Sendability != CloudSendability.Sendable;

        // Only while a turn is running, only while the session still takes
        // input, and not again once Stop has been pressed for this turn — the
        // press is optimistic, and a button that stayed up after it would invite
        // a second request for something the first has already asked for.
        public bool CanInterrupt => _busy && !IsReadOnly && !_interruptSent;

        // The watermark while the box is up, and the reason in its place when it
        // is not. Never the address — ReplyUrl is the link beside it.
        public string ComposerHint => CloudChatSendability.HintFor(Sendability, _busy);

        // The session's own address, which is what the panel's link opens.
        //
        // Carried from the roster row rather than rebuilt, for the reason
        // ClaudeCloudSessions.Session.Url gives: the payload's own session_url is
        // empty on every row measured, and the id is what the address is made of.
        // Null for a row that somehow arrived without one, and the panel then
        // draws no link rather than a link to nowhere.
        public string? ReplyUrl { get; }

        // What the live loop and the interrupt are doing, for a test to await.
        // Never awaited by the app: both are fire and forget by design.
        internal Task? LiveTask { get; private set; }

        // While the live loop runs it owns the busy state; the roster push only
        // updates the row. One owner at a time is what keeps a thirty-second-old
        // roster answer from arguing with a two-second-old live one.
        private bool LiveRunning => LiveTask is { IsCompleted: false };

        internal Task? StreamTask { get; private set; }

        // Running *and not told to stop*. A closed panel cancels its run, but the
        // run is still unwinding — awaiting a read or a wait — for a moment after,
        // and a panel reopened in that moment must start a new one rather than
        // be told one is already running. StartStream sets the token source
        // before the task, so a task means a source.
        private bool StreamRunning =>
            StreamTask is { IsCompleted: false } && !_streamCts!.IsCancellationRequested;

        // Up and trusted: running, and not fallen back. A stream the policy has
        // given up waiting on may still be retrying behind the polling loop, and
        // until it delivers again it is not what the panel relies on.
        private bool StreamLive => StreamRunning && !_streamFellBack;

        // The same answer, for a test to wait on rather than sleep for — and how
        // many stream events have been handled, so a test can know an event it
        // wrote has been seen before asserting what it did not change.
        internal bool StreamTrusted => StreamLive;

        internal int StreamEventsSeen => Volatile.Read(ref _streamEventsSeen);

        private int _streamEventsSeen;

        // Whoever is watching live owns busy; the roster only updates the row.
        private bool LiveOwnsBusy => LiveRunning || StreamLive;

        // The transcript read in flight, if any — so a test can see that the end
        // of a turn started one, or that it did not.
        internal Task? LoadTask
        {
            get { lock (_loadGate) return _inFlight; }
        }
        internal Task? InterruptTask { get; private set; }

        // **Answered, and the answer is not a machine.**
        //
        // ChatHeaderMeta.MachineFor treats a session that names no machine as
        // being on this one, and its comment says why that is a rule rather than
        // a guess: the only implementer was the mirror, so silence meant a local
        // CLI session or a gateway conversation, both of which are read where
        // they run. A cloud session is the first thing that is silent and *not*
        // here, and left silent it would have put the user's own laptop's name
        // in the header of a session running in Anthropic's data centre —
        // quietly, and in the one line of the panel that exists to answer
        // "where is this".
        //
        // So it names the cloud instead. Not a hostname, because there is no
        // hostname the user could act on and inventing a plausible one would be
        // worse than the wrong-laptop bug it replaces; the header's job here is
        // to say "not one of yours", and that is what this says.
        //
        // Never null and never changes, so MachineChanged is declared to satisfy
        // the interface and deliberately never raised — the panel subscribes and
        // reads the property at bind, which is already the whole answer.
        public string? MachineName => "Anthropic's cloud";

        public event Action? MachineChanged
        {
            add { }
            remove { }
        }

        // --- sending ---------------------------------------------------------

        // Send a message into the session.
        //
        // **The user's bubble appears after the 2xx, never before.** Requirement 3
        // on the interface is that SendAsync raises TurnAdded for the user's own
        // turn; the contract for Failed is that nothing was queued anywhere and
        // the only copy of the text is the one still in the box. A bubble raised
        // before the POST would have to be taken back on a refusal, and a panel
        // that shows a message and then withdraws it has told the user twice
        // something only one of which was true. So a refusal writes a note and
        // leaves the box alone, and a 2xx raises the bubble with the uuid it
        // went out with — the uuid the history's echo carries, so Reconcile
        // folds the two into one.
        //
        // **One retry, on Unavailable only, with the same uuid.** Measured: a
        // repeated uuid comes back `duplicate: true` and is not written twice,
        // so a send whose answer was lost can safely go again. Nothing else is
        // retried. A 401 is not, because the credential was re-read a moment
        // earlier and a second read would return the same token; a 429 is not,
        // because retrying into a rate limit is how a guest loses the invitation.
        public async Task<ChatSendOutcome> SendAsync(string text)
        {
            if (IsReadOnly)
            {
                Note("Not sent: " + ComposerHint);
                return ChatSendOutcome.Failed;
            }

            if (!Enabled())
            {
                Note("Not sent: cloud sessions are switched off in Settings.");
                return ChatSendOutcome.Failed;
            }

            // The credential read, the POST, the one retry and the wording live in
            // CloudTurnSender, shared with the Desktop-bridge send (CB-266).
            var attempt = await CloudTurnSender.SendUserTurnAsync(
                _api, _credentials, ReadBudget, SessionId, text, Delay, CancellationToken.None)
                .ConfigureAwait(false);

            if (attempt.Result != CloudSendResult.Sent)
            {
                Note(attempt.Note!);

                // Only a refusal the API gave is about the session; a missing
                // credential or a double timeout has no outcome to learn from.
                if (attempt.Result == CloudSendResult.NotSent && attempt.Outcome is { } refused)
                    Refused(refused);

                return ChatSendOutcome.Failed;
            }

            var uuid = attempt.Uuid;

            // A 2xx whose receipt this version cannot read is still a 2xx: the
            // event almost certainly landed, and ClaudeCloudSend.ParseSendResult
            // says why "not sent" would be the wrong thing to tell anyone. The
            // receipt itself carries nothing this panel shows, so it is not read.
            var turn = new ChatTurn { Role = ChatRole.User, Text = text, IsComplete = true };
            _post(() =>
            {
                _history.Add(turn);
                _byUuid[uuid] = turn;
                TurnAdded?.Invoke(turn);

                // The server has queued the turn — measured, including into a
                // busy session — so it is busy from here, and Stop shows now
                // rather than whenever the roster next looks.
                var before = Affordances();
                _busy = true;
                _interruptSent = false;
                _liveSaidIdle = false;
                Announce(before);
            });

            // A send is a fresh go: a login that failed a live read a moment ago
            // has just worked, so say so again if it fails again, and bring the
            // stream back if an open panel lost it. The stream, when it is up,
            // reports this turn's start and end itself; polling is for when it
            // is not.
            _notedLivePause = false;
            if (_panelOpen) StartStream();
            if (!StreamLive) StartLive();
            return ChatSendOutcome.Sent;
        }

        // Say why a send did not go, and take the box away if the reason is
        // about the session rather than about this attempt.
        private void Refused(CloudOutcome outcome)
        {
            if (CloudChatSendability.RefusalFor(outcome) is { } refusal)
            {
                _post(() =>
                {
                    var before = Affordances();
                    _refusal ??= refusal;
                    Announce(before);
                });
            }
        }

        // --- stopping --------------------------------------------------------

        // Stop the turn in flight.
        //
        // Measured: an interrupt takes a running session to idle within about a
        // second, and is harmless against an idle one. Harmless is not the same
        // as free, so a press with nothing running spends no request — the panel
        // hides the button then anyway (CanInterrupt), and this is the belt to
        // that braces.
        //
        // Fire and forget, like the click it answers. CanInterrupt goes false at
        // once so the button goes; if the request is refused the button comes
        // back and a note says why, since the turn is then still running.
        public void Cancel()
        {
            if (!CanInterrupt) return;

            var before = Affordances();
            _interruptSent = true;
            Announce(before);
            InterruptTask = InterruptAsync();
        }

        private async Task InterruptAsync()
        {
            var read = await ClaudeCliCredentials
                .ReadWithinAsync(_credentials, ReadBudget, CancellationToken.None).ConfigureAwait(false);

            string? failure;
            if (read.Outcome != CredentialOutcome.Found || read.AccessToken is not { } token)
            {
                failure = ClaudeCliCredentials.StatusFor(read);
            }
            else
            {
                // CodeEventsPath cannot be null here: Cancel is only reachable
                // through CanInterrupt, and a session with a malformed id never
                // got a turn running from this panel — but the roster can report
                // one busy regardless, so the refusal is handled rather than
                // assumed away.
                var path = CloudRequest.CodeEventsPath(SessionId);
                if (path is null)
                {
                    failure = $"this session's id is not in a shape {Brand.ShortName} will write to";
                }
                else
                {
                    var result = await _api.SendAsync(
                        new CloudRequestContext(token, path, HttpMethod.Post,
                            ClaudeCloudSend.InterruptBody(Guid.NewGuid().ToString(),
                                Guid.NewGuid().ToString())),
                        CancellationToken.None).ConfigureAwait(false);

                    failure = result.Outcome.Kind == CloudOutcomeKind.Ok ? null : result.Outcome.Detail;
                }
            }

            // Delivered: something has to see the turn end and put the state
            // right, and if a loop is not already watching, that is this one.
            if (failure is null)
            {
                if (!StreamLive) EnsureLive();
                return;
            }

            Note("Stop did not reach the session: " + failure + ".");
            _post(() =>
            {
                var before = Affordances();
                _interruptSent = false;
                Announce(before);
            });
        }

        // --- live state ------------------------------------------------------

        // What the roster says about this session now. Null means its row is not
        // in the roster at all.
        //
        // Pushed by SessionManager's scan, which already walks the roster every
        // pass to draw the orb — so this costs nothing on the network, and is
        // what moves Stop and the composer while the panel is open. Called on
        // the UI thread.
        internal void UpdateStatus(ClaudeCloudSessions.Session? row)
        {
            var wasBusy = _busy;

            var before = Affordances();

            _row = row;
            var rosterBusy = row is not null && ClaudeCloudRoster.IsBusy(row);

            // The live loop owns busy while it runs. Otherwise the roster does —
            // except that a roster still saying "working" after the live read saw
            // idle is the older of two observations, and waits until the roster
            // itself catches up.
            if (!LiveOwnsBusy)
            {
                if (!rosterBusy) _liveSaidIdle = false;
                _busy = rosterBusy && !_liveSaidIdle;
            }

            // The turn that Stop was pressed for is over; the next one is
            // stoppable again.
            if (!_busy) _interruptSent = false;

            Announce(before);

            if (!_panelOpen || IsReadOnly) return;

            // The turn just finished, so the reply is complete now and worth one
            // read. Or one has just started somewhere else — claude.ai, another
            // machine — and a panel someone is looking at should watch it live.
            if (wasBusy && !_busy) _ = RefreshAsync();
            else if (!wasBusy && _busy && !StreamLive) EnsureLive();
        }

        // The panel's lifetime, called by ChatPanel from Bind and Unbind.
        //
        // Closing stops the live loop: nobody is looking, and the next open reads
        // the transcript anyway. It does not stop a turn — closing a window
        // should never cancel work someone asked for, which is what Cancel's
        // own comment on the interface says. Opening onto a turn already running
        // starts watching it.
        internal void PanelOpened()
        {
            _panelOpen = true;

            // A new panel gets a fresh go at the stream, whatever the last one
            // concluded about it, and a fresh chance to say why live updates
            // stopped.
            _streamFellBack = false;
            _notedLivePause = false;
            StartStream();

            if (_busy && !IsReadOnly && !StreamLive) EnsureLive();
        }

        // Whether a panel is bound right now. Read by the panel's own tests,
        // which cannot see it any other way: the read it licenses is single
        // flight, so a request count cannot tell "started while closed" from
        // "started afterwards".
        internal bool PanelOpen => _panelOpen;

        internal void PanelClosed()
        {
            _panelOpen = false;
            _live?.Cancel();
            _streamCts?.Cancel();
        }

        // Every change to what the panel offers is bracketed by these two: read
        // both answers, change the state, and say which of them moved. One pair
        // rather than a helper per event, because most changes can move both — a
        // refusal takes Stop away along with the box.
        private (bool ReadOnly, bool Interrupt) Affordances() => (IsReadOnly, CanInterrupt);

        private void Announce((bool ReadOnly, bool Interrupt) before)
        {
            if (before.ReadOnly != IsReadOnly) ReadOnlyChanged?.Invoke();
            if (before.Interrupt != CanInterrupt) InterruptChanged?.Invoke();
        }

        // --- reading ---------------------------------------------------------

        // --- the stream --------------------------------------------------------

        // One stream per open panel. Opened from the newest durable event, so the
        // server sends only what happens from here — measured: without a
        // sequence number it replays the whole history first.
        private void StartStream()
        {
            if (Stream is not { } stream || _streamFellBack || StreamRunning || IsReadOnly) return;

            var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _streamCts, cts)?.Cancel();
            StreamTask = StreamAsync(stream, cts.Token);
        }

        // One run: from a panel opening (or a send bringing the stream back) to
        // the panel closing or the stream being given up on. **Everything a run
        // decides with is local to it**, and it checks its token after every
        // wait before touching anything shared — so a run cancelled by a close
        // and still unwinding cannot undo what the run a reopen started has
        // already done.
        private async Task StreamAsync(ICloudEventStream stream, CancellationToken ct)
        {
            var state = ClaudeCloudStreamPolicy.State.Initial;
            var knowWhereToStart = false;
            long? resumeFrom = null;
            var notedRateLimit = false;

            while (true)
            {
                var read = await ClaudeCliCredentials
                    .ReadWithinAsync(_credentials, ReadBudget, CancellationToken.None).ConfigureAwait(false);

                if (ct.IsCancellationRequested) return;

                // No login means no stream and no poll either. Said once, in the
                // credential layer's own words, rather than stopping in silence:
                // the next open or send tries again.
                if (read.Outcome != CredentialOutcome.Found || read.AccessToken is not { } token)
                {
                    NoteLivePause(read);
                    return;
                }

                CloudOutcome outcome;
                var healthy = false;

                // Where to start, asked once per run. A read that failed is a
                // reason to poll, never a reason to open without a sequence
                // number and take the whole history again; a read that worked
                // and found no events is a new session with no history to
                // replay, and the stream opens from the beginning. A failed read
                // is otherwise one more failed connection: the panel polls, and
                // the stream is tried again behind it at the policy's wait.
                StartRead? start = knowWhereToStart ? null : await ReadStartAsync(token).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;

                if (start is { Ok: false } failed)
                {
                    if (failed.Outcome is not { } refused) return; // no path: a malformed id

                    FallBack();
                    outcome = refused;
                }
                else
                {
                    if (start is { } found)
                    {
                        resumeFrom = found.Sequence;
                        knowWhereToStart = true;
                    }

                    CloudOutcome? ended = null;
                    var deliveredEvent = false;
                    var openedAt = Now();

                    try
                    {
                        await foreach (var e in stream.OpenAsync(token, SessionId, resumeFrom, ct)
                                           .WithCancellation(ct).ConfigureAwait(false))
                        {
                            if (e.Kind == CloudStreamEventKind.Ended)
                            {
                                ended = e.Outcome;
                                break;
                            }

                            // A stream that delivers again after the panel fell
                            // back is trusted again, and the polling loop runs
                            // out on its own — but only on an event the policy
                            // counts. A keepalive says the socket is open, not
                            // that anything will come down it.
                            if (ClaudeCloudStreamPolicy.CountsTowardHealth(e))
                            {
                                deliveredEvent = true;
                                _streamFellBack = false;
                            }

                            resumeFrom = ClaudeCloudStreamPolicy.ResumeFrom(resumeFrom, e);
                            Handle(e);
                            Interlocked.Increment(ref _streamEventsSeen);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (ct.IsCancellationRequested) return;

                    // An enumeration that stopped without saying why is a
                    // transport failure, not a clean end.
                    outcome = ended
                              ?? new CloudOutcome(CloudOutcomeKind.Unavailable, 0, null,
                                  ClaudeCloudStreamEvents.EndOfStreamDetail);
                    healthy = ClaudeCloudStreamPolicy.WasHealthy(deliveredEvent, Now() - openedAt);
                }

                // The session ended or went: the same answer a send would get,
                // and the box goes for the same reason.
                if (CloudChatSendability.RefusalFor(outcome) is { } refusal)
                {
                    _post(() =>
                    {
                        var before = Affordances();
                        _refusal ??= refusal;
                        Announce(before);
                    });
                    return;
                }

                if (outcome.Kind is CloudOutcomeKind.TokenRefused or CloudOutcomeKind.AuthFailed) return;

                if (outcome.Kind == CloudOutcomeKind.RateLimited && !notedRateLimit)
                {
                    notedRateLimit = true;
                    Note(LivePausedNote);
                }

                // The shared policy decides the wait, and when the panel should
                // stop waiting on the stream. Falling back with a wait still
                // left means "poll now, and keep trying the stream behind it";
                // no wait means the stream is done for this panel.
                var decision = ClaudeCloudStreamPolicy.Next(state, outcome, healthy);
                state = decision.Next;

                if (decision.FallBackToPolling) FallBack();
                if (decision.Wait is not { } next) return;

                try
                {
                    await Delay(next, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (ct.IsCancellationRequested) return;
            }
        }

        // Stop relying on the stream for this panel and let polling carry any
        // turn that is running.
        private void FallBack()
        {
            _streamFellBack = true;
            if (_busy && !IsReadOnly) EnsureLive();
        }

        // Live updates have stopped because the login could not be read. Said
        // once per open or send, whichever of the stream and the poll noticed
        // first.
        private void NoteLivePause(CredentialRead read)
        {
            if (_notedLivePause) return;
            _notedLivePause = true;
            Note("Live updates paused: " + ClaudeCliCredentials.StatusFor(read) + ".");
        }

        // Where the stream starts: the newest durable sequence number, from the
        // write host's event list read newest first. Ok with a null Sequence
        // means the session has no events yet. Not Ok carries the outcome that
        // refused the read, or no outcome when there is no path to read.
        private readonly record struct StartRead(bool Ok, long? Sequence, CloudOutcome? Outcome);

        private async Task<StartRead> ReadStartAsync(string token)
        {
            if (ClaudeCloudStreamRequest.NewestSequencePath(SessionId) is not { } path)
            {
                return new StartRead(false, null, null);
            }

            var result = await _api.SendAsync(new CloudRequestContext(token, path), CancellationToken.None)
                .ConfigureAwait(false);

            return result.Outcome.Kind == CloudOutcomeKind.Ok
                ? new StartRead(true, ClaudeCloudStreamRequest.ParseNewestSequence(result.Body), null)
                : new StartRead(false, null, result.Outcome);
        }

        // One event off the stream. Runs on the stream's thread; everything that
        // touches what the panel shows goes through _post.
        private void Handle(CloudStreamEvent e)
        {
            switch (e.Kind)
            {
                case CloudStreamEventKind.Durable:
                    HandleDurable(e);
                    break;

                case CloudStreamEventKind.Ephemeral when e.InnerType == "message_start":
                    _post(() => _deltasSuperseded = false);
                    break;

                case CloudStreamEventKind.Ephemeral when e.TextDelta is { } delta:
                    _post(() =>
                    {
                        if (!_deltasSuperseded) AppendDelta(delta);
                    });
                    break;
            }
        }

        private void HandleDurable(CloudStreamEvent e)
        {
            switch (e.PayloadType)
            {
                // The turn has started — measured: `system init`, then a
                // `status` of "requesting".
                case "system" when e.Subtype == "init" || (e.Subtype == "status" && e.StatusValue == "requesting"):
                    _post(() => SetBusy(true));
                    break;

                // The turn is over, whatever the subtype — success, or
                // error_during_execution after an interrupt.
                case "result":
                    _post(EndTurn);
                    break;

                // Someone typed a message — us, or the same session on claude.ai or
                // in the CLI — so a turn is starting. Measured: an idle session
                // took about four seconds after the echo before its first delta
                // while the environment woke, so waiting for a `status` would
                // leave Stop missing for exactly that stretch. A tool result is
                // also a `user` row and arrives mid-turn; it is not a new turn.
                case "user":
                    var typed = CloudStreamRows.IsTypedMessage(e.PayloadJson);
                    var users = CloudStreamRows.RowsFrom(e.PayloadJson);
                    _post(() =>
                    {
                        FoldDurable(users);
                        if (typed) SetBusy(true);
                    });
                    break;

                case "assistant":
                    var rows = CloudStreamRows.RowsFrom(e.PayloadJson);
                    _post(() => FoldDurable(rows));
                    break;
            }
        }

        private void SetBusy(bool busy)
        {
            var before = Affordances();
            _busy = busy;
            if (!busy) _interruptSent = false;
            Announce(before);
        }

        private void EndTurn()
        {
            _liveSaidIdle = true;
            _deltasSuperseded = false;

            // An interrupted reply has no durable message to replace it, so the
            // bubble that was growing is simply finished where it stands. A live
            // bubble is always unfinished — the stored message clears it — so
            // there is nothing to check first.
            if (_liveTurn is { } live)
            {
                _liveTurn = null;
                live.IsComplete = true;
                TurnUpdated?.Invoke(live);
            }

            SetBusy(false);
        }

        // Text as it is written: one in-progress bubble, added once and grown.
        private void AppendDelta(string delta)
        {
            if (_liveTurn is { } live)
            {
                live.Text += delta;
                TurnUpdated?.Invoke(live);
            }
            else
            {
                _liveTurn = new ChatTurn { Role = ChatRole.Assistant, Text = delta, IsComplete = false };
                _history.Add(_liveTurn);
                TurnAdded?.Invoke(_liveTurn);
            }

            if (!_busy) SetBusy(true);
        }

        // A durable row. The stored assistant message takes over the live bubble
        // in place — same row on screen, now carrying the uuid a later history
        // read will match — and everything else reconciles by uuid as a history
        // read would, which is how the echo of our own message finds its bubble.
        private void FoldDurable(IReadOnlyList<ChatTranscript.Row> rows)
        {
            var rest = new List<ChatTranscript.Row>();

            foreach (var row in rows)
            {
                // Whatever deltas are still in flight for this message are
                // already in it.
                if (row.Turn.Role == ChatRole.Assistant) _deltasSuperseded = true;

                if (_liveTurn is { } live && row.Turn.Role == ChatRole.Assistant
                    && (row.Uuid is null || !_byUuid.ContainsKey(row.Uuid)))
                {
                    _liveTurn = null;
                    live.Text = row.Turn.Text;
                    live.IsComplete = row.Turn.IsComplete;
                    if (row.Uuid is { } uuid) _byUuid[uuid] = live;
                    TurnUpdated?.Invoke(live);
                    continue;
                }

                rest.Add(row);
            }

            Reconcile(rest);
        }

        // Watch the turn: its state every status interval, its transcript every
        // transcript interval while it runs, one last read when it ends. A send
        // replaces a running loop (the cap starts again for the new turn); every
        // other caller joins the one that is running.
        private void StartLive()
        {
            var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _live, cts)?.Cancel();
            LiveTask = LiveAsync(cts.Token);
        }

        private void EnsureLive()
        {
            if (!LiveRunning) StartLive();
        }

        private async Task LiveAsync(CancellationToken ct)
        {
            var elapsed = TimeSpan.Zero;
            var sinceTranscript = TimeSpan.Zero;

            while (elapsed < UnmeasuredLiveCap)
            {
                try
                {
                    await Delay(UnmeasuredLiveStatusInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (ct.IsCancellationRequested || IsReadOnly) return;
                elapsed += UnmeasuredLiveStatusInterval;
                sinceTranscript += UnmeasuredLiveStatusInterval;

                // No path means a malformed id, which no tick will fix. No login
                // is said once and ends the loop; the next open or send tries
                // again.
                if (CloudRequest.CodeSessionPath(SessionId) is not { } path) return;

                var read = await ClaudeCliCredentials
                    .ReadWithinAsync(_credentials, ReadBudget, CancellationToken.None).ConfigureAwait(false);

                if (read.Outcome != CredentialOutcome.Found || read.AccessToken is not { } token)
                {
                    NoteLivePause(read);
                    return;
                }

                var result = await _api.SendAsync(new CloudRequestContext(token, path), CancellationToken.None)
                    .ConfigureAwait(false);

                var outcome = result.Outcome;

                // Any 429 ends it, with no retry of its own: the budget being
                // spent is the account's. The roster push still sees the turn
                // end, which is what the note promises.
                if (outcome.Kind == CloudOutcomeKind.RateLimited)
                {
                    Note(LivePausedNote);
                    return;
                }

                // A session that has ended or gone says so here as surely as on
                // a send, and the box goes for the same reason.
                if (CloudChatSendability.RefusalFor(outcome) is { } refusal)
                {
                    _post(() =>
                    {
                        var before = Affordances();
                        _refusal ??= refusal;
                        Announce(before);
                    });
                    return;
                }

                // A login the endpoint no longer takes will not start working on
                // the next tick. The next send explains it in words.
                if (outcome.Kind is CloudOutcomeKind.TokenRefused or CloudOutcomeKind.AuthFailed) return;

                // Anything else that is not an answer — a timeout, a 5xx, a body
                // with no bucket in it — is no news, and the loop asks again.
                if (outcome.Kind != CloudOutcomeKind.Ok
                    || CloudLiveStatus.WorkingFrom(result.Body) is not { } working)
                {
                    continue;
                }

                if (!working)
                {
                    _post(() =>
                    {
                        var before = Affordances();
                        _busy = false;
                        _interruptSent = false;
                        _liveSaidIdle = true;
                        Announce(before);
                    });

                    await RefreshAsync().ConfigureAwait(false);
                    return;
                }

                if (sinceTranscript >= UnmeasuredLiveTranscriptInterval)
                {
                    sinceTranscript = TimeSpan.Zero;

                    if ((await RefreshAsync().ConfigureAwait(false)).Kind == CloudOutcomeKind.RateLimited)
                    {
                        Note(LivePausedNote);
                        return;
                    }
                }
            }
        }

        // Read the transcript and reconcile it against what is already shown.
        //
        // Returns false when nothing could be read at all — no credential, or the
        // endpoint refused us — which is the caller's cue to leave the panel in
        // Error rather than showing an empty conversation as though the session
        // had nothing in it. An empty transcript and an unreadable one look
        // identical on screen and only one of them is worth saying something about.
        //
        // **Single flight.** Every open starts one of these, and so do the
        // live loop and the end of a turn; two overlapping reads of the same
        // transcript would both reconcile, and at best spend a request each for
        // one answer. A call that arrives while one is running gets that one's
        // answer. The cancellation token is accepted for the callers that have
        // one and not passed down, because a read shared between callers cannot
        // be cancelled by one of them.
        internal async Task<bool> LoadAsync(CancellationToken ct) =>
            (await RefreshAsync().ConfigureAwait(false)).Kind == CloudOutcomeKind.Ok;

        private readonly record struct LoadResult(CloudOutcomeKind Kind);

        private Task<LoadResult> RefreshAsync()
        {
            lock (_loadGate)
            {
                if (_inFlight is { IsCompleted: false } running) return running;
                return _inFlight = ReadTranscriptAsync();
            }
        }

        private async Task<LoadResult> ReadTranscriptAsync()
        {
            // Budgeted for the same reason the arm's read is: the secret read can
            // block indefinitely with no window server session, and a panel whose
            // load never returns is a spinner that never stops.
            var read = await ClaudeCliCredentials
                .ReadWithinAsync(_credentials, ReadBudget, CancellationToken.None).ConfigureAwait(false);

            if (read.Outcome != CredentialOutcome.Found || read.AccessToken is not { } token)
            {
                Publish(RemoteChatState.Error);
                return new LoadResult(CloudOutcomeKind.AuthFailed);
            }

            var rows = new List<ChatTranscript.Row>();
            string? after = null;

            for (var i = 0; i < MaxEventPages; i++)
            {
                var result = await _api.SendAsync(
                    new CloudRequestContext(token,
                        CloudRequest.EventsPath(SessionId, CloudRequest.MaxPageSize, after)),
                    CancellationToken.None).ConfigureAwait(false);

                if (result.Outcome.Kind != CloudOutcomeKind.Ok)
                {
                    Publish(RemoteChatState.Error);
                    return new LoadResult(result.Outcome.Kind);
                }

                var page = ClaudeCloudEvents.ParsePage(result.Body);
                if (!page.Parsed)
                {
                    Publish(RemoteChatState.Error);
                    return new LoadResult(CloudOutcomeKind.ShapeChanged);
                }

                rows.AddRange(page.Rows);

                if (!page.HasMore || page.LastId is null) break;
                after = page.LastId;
            }

            // Oldest to newest is the order the events endpoint returns and the
            // order the interface promises, so the trim takes the *tail*. Trimming
            // the head would hand the panel the beginning of a conversation and
            // call it the end of one.
            var kept = rows.Count > HistoryTurns
                ? rows.Skip(rows.Count - HistoryTurns).ToList()
                : rows;

            _post(() => Reconcile(kept));
            return new LoadResult(CloudOutcomeKind.Ok);
        }

        // Fold a freshly-read transcript into the one on screen.
        //
        // By `uuid`, not by position. A transcript being re-read while its last
        // turn is still being written means the same turn comes back longer than
        // it was, and matching by position would work right up until a row was
        // skipped — ChatTranscript drops sidechain rows and noise, so the indices
        // on two reads of a growing transcript are not the same indices. The same
        // key is what makes a sent message's echo land on its own bubble: SendAsync
        // files the bubble under the uuid it sent, and the echo carries it back.
        //
        // Requirement 2 on the interface is that TurnUpdated carries the whole
        // turn already mutated rather than a delta, which is what mutating Text in
        // place and then raising does.
        private void Reconcile(IReadOnlyList<ChatTranscript.Row> rows)
        {
            foreach (var row in rows)
            {
                // A row with no uuid cannot be reconciled with anything, so it is
                // appended once and never matched again. Rare enough not to be
                // worth a second key, and appending a duplicate is a better
                // failure than silently replacing an unrelated turn.
                if (row.Uuid is { } uuid && _byUuid.TryGetValue(uuid, out var existing))
                {
                    if (existing.Text == row.Turn.Text
                        && existing.IsComplete == row.Turn.IsComplete)
                    {
                        continue;
                    }

                    existing.Text = row.Turn.Text;
                    existing.IsComplete = row.Turn.IsComplete;
                    TurnUpdated?.Invoke(existing);
                    continue;
                }

                _history.Add(row.Turn);
                if (row.Uuid is { } key) _byUuid[key] = row.Turn;
                TurnAdded?.Invoke(row.Turn);
            }

            SetState(RemoteChatState.Connected);
        }

        // A line in the transcript from this app rather than from the session.
        // Every caller builds it from fixed prose and the endpoint's Detail
        // strings, none of which is derived from the credential.
        private void Note(string text) => _post(() =>
        {
            var turn = new ChatTurn { Role = ChatRole.System, IsComplete = true, Text = text };
            _history.Add(turn);
            TurnAdded?.Invoke(turn);
        });

        private void Publish(RemoteChatState state) => _post(() => SetState(state));

        private void SetState(RemoteChatState state)
        {
            if (_state == state) return;
            _state = state;
            StateChanged?.Invoke(state);
        }
    }
}
