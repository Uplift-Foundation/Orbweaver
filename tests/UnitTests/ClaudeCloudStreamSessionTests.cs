using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// The chat session consuming the live event stream (CB-199).
//
// A partial of ClaudeCloudEventsTests so the same fakes drive both halves — the
// polling loop the stream falls back to, and the stream itself. The stream fake
// hands each open to the test as a channel, so a test writes a turn event by
// event, the way the measured contract says the server does, and watches what
// the panel would see between them.
public partial class ClaudeCloudEventsTests
{
    private sealed class FakeStream : ICloudEventStream
    {
        private readonly Channel<Channel<CloudStreamEvent>> _opened = Channel.CreateUnbounded<Channel<CloudStreamEvent>>();

        internal List<(string Token, string SessionId, long? From)> Opens { get; } = new();

        // What E1's real stream does on cancellation: the enumeration simply
        // ends, with no Ended event and no exception. Off by default, where a
        // cancelled read throws, which is what Task-based waits do.
        internal bool EndsQuietlyOnCancel { get; init; }

        public async IAsyncEnumerable<CloudStreamEvent> OpenAsync(string accessToken, string sessionId,
            long? fromSequenceNum, [EnumeratorCancellation] CancellationToken ct)
        {
            var events = Channel.CreateUnbounded<CloudStreamEvent>();
            lock (Opens) Opens.Add((accessToken, sessionId, fromSequenceNum));
            _opened.Writer.TryWrite(events);

            while (true)
            {
                CloudStreamEvent next;
                try
                {
                    if (!await events.Reader.WaitToReadAsync(ct)) yield break;
                    if (!events.Reader.TryRead(out next!)) continue;
                }
                catch (OperationCanceledException) when (EndsQuietlyOnCancel)
                {
                    yield break;
                }

                yield return next;
            }
        }

        // The session's current stream run — wired by Sender, read afresh on
        // every look because a close and a reopen start a new one.
        internal Func<Task?>? Run { get; set; }

        // The next connection the session opens, as something to write events to.
        //
        // **No clock (CB-230).** This used to give up after ten wall-clock
        // seconds, and on a loaded macOS runner it did: the session reopened
        // correctly, just later than that, and the test failed on how long the
        // thread pool took rather than on anything the session decided. Forced
        // by making the second credential read take eleven seconds, it failed
        // here with the runner's own OperationCanceledException every time.
        //
        // What actually means "no open is coming" is the run having ended — a
        // run that is still going will open again or end, and nothing else. So
        // that is the signal: the next open, or a failure as soon as the run
        // that would have made it has finished without making it. Slow is now
        // only slow; wrong still fails, and fails at once with a reason.
        //
        // **One backstop, and it is not the old timeout.** A run that parks —
        // alive, never opening again, never ending — would otherwise hang the
        // test, and a hang is the failure CI shows worst: a job timeout with no
        // message. So past Backstop the wait fails, naming what it saw. The
        // passing path never reaches it (an open or an ending arrives first),
        // and at minutes rather than seconds no plausible amount of runner load
        // gets there either: the whole UnitTests suite runs in under two
        // minutes on CI's slowest leg.
        internal TimeSpan Backstop { get; init; } = TimeSpan.FromMinutes(2);

        internal Task<ChannelWriter<CloudStreamEvent>> NextOpenAsync() => NextOpenAsync(Backstop);

        // The same wait with its own bound, for the one test whose point is the
        // bound itself. Kept apart from `Backstop` so a short bound can cover the
        // park it is asserting and not the setup before it: the first open needs a
        // pool read, and a 200 ms bound over that failed 30 of 30 on a slow runner
        // while asserting nothing about parking (CB-269).
        internal async Task<ChannelWriter<CloudStreamEvent>> NextOpenAsync(TimeSpan bound)
        {
            var backstop = Task.Delay(bound);

            while (true)
            {
                if (_opened.Reader.TryRead(out var open)) return open.Writer;

                var run = Run?.Invoke()
                          ?? throw new Xunit.Sdk.XunitException("no stream run was started, so nothing will open");

                if (run.IsCompleted)
                {
                    // An open the run made just before ending was written before
                    // the run completed, so it is in the channel by now.
                    if (_opened.Reader.TryRead(out open)) return open.Writer;

                    await run; // a run that faulted says why, as itself
                    throw new Xunit.Sdk.XunitException("the stream run ended without opening again");
                }

                await Task.WhenAny(_opened.Reader.WaitToReadAsync().AsTask(), run, backstop);

                // An open or an ending that landed alongside the backstop wins:
                // the top of the loop takes either. Only neither is a park.
                if (backstop.IsCompleted && _opened.Reader.Count == 0 && !run.IsCompleted)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"the stream run is still alive after {bound} with no new open and no ending " +
                        $"(opens so far: {OpenCount}, run status: {run.Status}) — the session has parked");
                }
            }
        }

        internal int OpenCount
        {
            get { lock (Opens) return Opens.Count; }
        }
    }

    private static CloudStreamEvent Durable(long seq, string type, string? subtype = null,
        string? status = null, string? payload = null) =>
        new(CloudStreamEventKind.Durable, "client_event", seq,
            payload is null ? "{}" : "{\"event_type\":\"client_event\",\"payload\":" + payload + "}",
            PayloadType: type, Subtype: subtype, StatusValue: status);

    private static CloudStreamEvent Delta(string text) =>
        new(CloudStreamEventKind.Ephemeral, "ephemeral_event", null, "{}",
            PayloadType: "stream_event", TextDelta: text, InnerType: "content_block_delta");

    private static CloudStreamEvent EndedWith(int status) =>
        new(CloudStreamEventKind.Ended, null, null, null, Outcome: CloudOutcomes.OutcomeFor(status, ""));

    private static readonly CloudStreamEvent EndedCleanly =
        new(CloudStreamEventKind.Ended, null, null, null, Outcome: CloudOutcomes.OutcomeFor(200, ""));

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++) await Task.Delay(5);
        Assert.True(condition(), "the condition did not become true within five seconds");
    }

    private static (ClaudeCloudChatSession Chat, RoutingApi Api, FakeStream Stream, FakeClock Clock)
        Streaming(ClaudeCloudSessions.Session? session = null, ICloudCredentialSource? creds = null,
            CredentialBudget? budget = null)
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var stream = new FakeStream();
        var clock = new FakeClock();
        return (Sender(api, clock, creds: creds, session: session, stream: stream, budget: budget),
            api, stream, clock);
    }

    private static async Task Close(ClaudeCloudChatSession chat)
    {
        chat.PanelClosed();
        if (chat.StreamTask is { } task) await task;
    }

    // --- a normal turn ---------------------------------------------------------

    [Fact]
    public async Task AStreamedReplyGrowsOneBubbleAndStopFollowsTheTurn()
    {
        var (chat, api, stream, _) = Streaming();
        var added = new List<ChatTurn>();
        var texts = new List<string>();
        chat.TurnAdded += t => added.Add(t);
        chat.TurnUpdated += t => texts.Add(t.Text);

        chat.PanelOpened();
        var events = await stream.NextOpenAsync();

        // Opened live, from the newest durable event, on the session's login.
        Assert.Equal((Token, "session_a", (long?)41), stream.Opens.Single());
        Assert.Single(api.NewestReads);

        events.TryWrite(Durable(42, "system", "init"));
        await Until(() => chat.CanInterrupt);

        events.TryWrite(Delta("Hel"));
        events.TryWrite(Delta("lo, "));
        events.TryWrite(Delta("world"));
        await Until(() => added.Count == 1 && added[0].Text == "Hello, world");
        Assert.False(added[0].IsComplete);
        Assert.Equal(ChatRole.Assistant, added[0].Role);
        Assert.True(chat.CanInterrupt);

        events.TryWrite(Durable(43, "assistant", payload: AssistantRow("a1", "Hello, world.")));
        events.TryWrite(Durable(44, "result", "success"));
        await Until(() => !chat.CanInterrupt);

        // The durable message replaced the live bubble in place: one turn,
        // finished, carrying the stored text.
        var turn = Assert.Single(chat.History);
        Assert.Same(added[0], turn);
        Assert.True(turn.IsComplete);
        Assert.Equal("Hello, world.", turn.Text);
        // The first delta added the bubble; each later one grew it in place.
        Assert.Equal(new[] { "Hello, ", "Hello, world", "Hello, world." }, texts.Distinct());

        // And a later history read carrying the same uuid finds that bubble.
        await chat.LoadAsync(CancellationToken.None);
        Assert.Single(chat.History);

        await Close(chat);
    }

    // The turn start the contract names second — a status of "requesting" — is
    // enough on its own.
    [Theory]
    [InlineData("status", "requesting", true)]
    [InlineData("status", "idle", false)]
    [InlineData("post_turn_summary", null, false)]
    public async Task OnlyTheMeasuredTurnStartsMakeTheSessionBusy(string subtype, string? status, bool busy)
    {
        var (chat, _, stream, _) = Streaming();
        chat.PanelOpened();
        var events = await stream.NextOpenAsync();

        events.TryWrite(Durable(42, "system", subtype, status));

        // A marker that does not touch busy: once it shows, the event before it
        // has been handled.
        events.TryWrite(Durable(43, "assistant", payload: AssistantRow("a1", "marker")));
        await Until(() => chat.History.Count == 1);

        Assert.Equal(busy, chat.CanInterrupt);
        await Close(chat);
    }

    // The echo of our own message folds into the bubble the 2xx added, and a
    // send while the stream is up does not start the polling loop.
    [Fact]
    public async Task TheEchoOfOurSendFoldsIntoItsBubbleAndNoPollingStarts()
    {
        var (chat, api, stream, _) = Streaming();
        chat.PanelOpened();
        var events = await stream.NextOpenAsync();

        Assert.Equal(ChatSendOutcome.Sent, await chat.SendAsync("hello"));
        Assert.Null(chat.LiveTask);
        Assert.True(chat.CanInterrupt);

        var uuid = PayloadUuid(api.Posts.Single());
        events.TryWrite(Durable(42, "user", payload: UserRow(uuid, "hello")));
        events.TryWrite(Durable(43, "assistant", payload: AssistantRow("a1", "hi")));
        await Until(() => chat.History.Count == 2);

        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant }, chat.History.Select(t => t.Role));
        await Close(chat);
    }

    // Interrupted mid-reply: the partial message is what the stored row says,
    // and a turn with no stored message at all is finished where it stands.
    [Fact]
    public async Task AnInterruptedReplyIsFinishedWhereItStands()
    {
        var (chat, _, stream, _) = Streaming();
        var updated = 0;
        chat.TurnUpdated += _ => updated++;
        chat.PanelOpened();
        var events = await stream.NextOpenAsync();

        events.TryWrite(Durable(42, "system", "init"));
        events.TryWrite(Delta("Once upon"));
        await Until(() => chat.History.Count == 1);

        events.TryWrite(Durable(43, "control_response"));
        events.TryWrite(Durable(44, "result", "error_during_execution"));
        await Until(() => !chat.CanInterrupt);

        var turn = Assert.Single(chat.History);
        Assert.True(turn.IsComplete);
        Assert.Equal("Once upon", turn.Text);
        Assert.Equal(1, updated);

        // A second end of turn with nothing live changes nothing.
        events.TryWrite(Durable(45, "result", "success"));
        events.TryWrite(Delta("next"));
        await Until(() => chat.History.Count == 2);
        Assert.Equal(1, updated);

        await Close(chat);
    }

    // A reply that arrives whole — no deltas — is added as a history read
    // would add it. Events the panel has no use for change nothing.
    [Fact]
    public async Task AWholeReplyIsAddedAndEverythingElseIsIgnored()
    {
        var (chat, _, stream, _) = Streaming();
        chat.PanelOpened();
        var events = await stream.NextOpenAsync();

        events.TryWrite(new CloudStreamEvent(CloudStreamEventKind.Keepalive, null, null, null));
        events.TryWrite(new CloudStreamEvent(CloudStreamEventKind.Session, "session_update", null, "{}"));
        events.TryWrite(new CloudStreamEvent(CloudStreamEventKind.Delivery, "delivery_update", null, "{}",
            DeliveryStatus: "DELIVERY_STATUS_RECEIVED"));
        events.TryWrite(new CloudStreamEvent(CloudStreamEventKind.Ephemeral, "ephemeral_event", null, "{}",
            PayloadType: "system", Subtype: "post_turn_summary"));
        events.TryWrite(new CloudStreamEvent(CloudStreamEventKind.Other, "surprise", null, "{}"));
        events.TryWrite(new CloudStreamEvent(CloudStreamEventKind.Durable, "client_event", null, "{}",
            PayloadType: "prompt_suggestion"));
        events.TryWrite(Durable(42, "assistant", payload: AssistantRow("a1", "all at once")));
        await Until(() => chat.History.Count == 1);

        Assert.Equal("all at once", chat.History[0].Text);
        Assert.False(chat.CanInterrupt);
        await Close(chat);
    }

    // Measured: the stored assistant message can arrive before the trailing
    // deltas of the message it stores. Those are already in it, and must not
    // start a second bubble — until the next message begins.
    [Fact]
    public async Task LateDeltasAfterTheStoredMessageAreIgnoredUntilTheNextMessage()
    {
        var (chat, _, stream, _) = Streaming();
        chat.PanelOpened();
        var events = await stream.NextOpenAsync();

        events.TryWrite(Durable(42, "system", "init"));
        events.TryWrite(Delta("The first "));
        events.TryWrite(Durable(43, "assistant", payload: AssistantRow("a1", "The first answer.")));
        events.TryWrite(Delta("answer."));
        events.TryWrite(new CloudStreamEvent(CloudStreamEventKind.Ephemeral, "ephemeral_event", null, "{}",
            PayloadType: "stream_event", InnerType: "message_stop"));
        events.TryWrite(new CloudStreamEvent(CloudStreamEventKind.Ephemeral, "ephemeral_event", null, "{}",
            PayloadType: "stream_event", InnerType: "message_start"));
        events.TryWrite(Delta("A second message"));
        await Until(() => chat.History.Count == 2);

        Assert.Equal("The first answer.", chat.History[0].Text);
        Assert.True(chat.History[0].IsComplete);
        Assert.Equal("A second message", chat.History[1].Text);
        Assert.False(chat.History[1].IsComplete);

        await Close(chat);
    }

    // A stored reply with nothing live before it still supersedes whatever
    // deltas trail it.
    [Fact]
    public async Task LateDeltasAfterAWholeStoredReplyAreIgnoredToo()
    {
        var (chat, _, stream, _) = Streaming();
        chat.PanelOpened();
        var events = await stream.NextOpenAsync();

        events.TryWrite(Durable(42, "assistant", payload: AssistantRow("a1", "whole")));
        events.TryWrite(Delta("trailing"));
        events.TryWrite(Durable(43, "result", "success"));
        events.TryWrite(Delta("the next turn"));
        await Until(() => chat.History.Count == 2);

        Assert.Equal(new[] { "whole", "the next turn" }, chat.History.Select(t => t.Text));
        await Close(chat);
    }

    // A turn somebody else started — the same session typed into on claude.ai
    // or in the CLI — shows Stop from its echo, before any delta; its result
    // hides it.
    [Fact]
    public async Task AMessageTypedElsewhereShowsStopAndItsResultHidesIt()
    {
        var (chat, _, stream, _) = Streaming();
        chat.PanelOpened();
        var events = await stream.NextOpenAsync();

        events.TryWrite(Durable(42, "user", payload: UserRow("f1", "typed on the web")));
        await Until(() => chat.CanInterrupt);
        Assert.Equal(ChatRole.User, Assert.Single(chat.History).Role);

        events.TryWrite(Durable(43, "result", "success"));
        await Until(() => !chat.CanInterrupt);

        await Close(chat);
    }

    private const string ToolResultRow =
        """{"type":"user","uuid":"t1","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"x","content":"ok"}]}}""";

    // A tool result is a `user` row too, handed back mid-turn; it is not a turn.
    [Fact]
    public async Task AToolResultRowDoesNotStartATurn()
    {
        var (chat, _, stream, _) = Streaming();
        chat.PanelOpened();
        var events = await stream.NextOpenAsync();

        events.TryWrite(Durable(42, "user", payload: ToolResultRow));
        events.TryWrite(Durable(43, "assistant", payload: AssistantRow("a1", "marker")));
        await Until(() => chat.History.Any(t => t.Text == "marker"));

        Assert.False(chat.CanInterrupt);
        await Close(chat);
    }

    // With nobody subscribed the reducer still does its work: a reply grows,
    // finishes where it stands when interrupted, and the history says so.
    [Fact]
    public async Task AReplyGrowsAndFinishesWithNobodyListening()
    {
        var (chat, _, stream, _) = Streaming();
        chat.PanelOpened();
        var events = await stream.NextOpenAsync();

        events.TryWrite(Delta("one "));
        events.TryWrite(Delta("two"));
        events.TryWrite(Durable(42, "result", "error_during_execution"));
        await Until(() => chat.History.Count == 1 && chat.History[0].IsComplete);

        Assert.Equal("one two", chat.History[0].Text);
        await Close(chat);
    }

    // A stored assistant row with no uuid still takes over the live bubble; one
    // whose uuid is already on screen updates that turn and leaves the live
    // bubble to the turn's end.
    [Fact]
    public async Task AStoredRowWithNoUuidTakesOverTheLiveBubble()
    {
        var (chat, _, stream, _) = Streaming();
        chat.PanelOpened();
        var events = await stream.NextOpenAsync();

        events.TryWrite(Delta("partial"));
        events.TryWrite(Durable(42, "assistant", payload:
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"whole"}]}}"""));
        await Until(() => chat.History.Count == 1 && chat.History[0].IsComplete);

        Assert.Equal("whole", chat.History[0].Text);
        await Close(chat);
    }

    [Fact]
    public async Task AStoredRowAlreadyOnScreenUpdatesItAndLeavesTheLiveBubble()
    {
        var api = new RoutingApi(Answer(200, Receipt),
            new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""), Envelope(new[] { AssistantRow("a1", "earlier") })));
        var stream = new FakeStream();
        var chat = Sender(api, stream: stream);
        await chat.LoadAsync(CancellationToken.None);

        chat.PanelOpened();
        var events = await stream.NextOpenAsync();

        events.TryWrite(Delta("live"));
        events.TryWrite(Durable(42, "assistant", payload: AssistantRow("a1", "earlier, amended")));
        events.TryWrite(Durable(43, "result", "success"));
        await Until(() => chat.History.Count == 2 && chat.History[1].IsComplete);

        Assert.Equal(new[] { "earlier, amended", "live" }, chat.History.Select(t => t.Text));
        await Close(chat);
    }

    // A keepalive says the socket is open, not that anything will come down it:
    // it does not restore trust in a stream the panel has fallen back from.
    [Fact]
    public async Task AKeepaliveAloneDoesNotRestoreTrustAfterAFallback()
    {
        var (chat, _, stream, _) = Streaming();
        chat.PanelOpened();

        for (var i = 0; i < ClaudeCloudStreamPolicy.UnmeasuredFailuresBeforeFallback; i++)
        {
            (await stream.NextOpenAsync()).TryWrite(EndedWith(503));
        }

        var retry = await stream.NextOpenAsync();
        var seen = chat.StreamEventsSeen;
        retry.TryWrite(new CloudStreamEvent(CloudStreamEventKind.Keepalive, null, null, null));
        retry.TryWrite(new CloudStreamEvent(CloudStreamEventKind.Session, "session_update", null, "{}"));
        await Until(() => chat.StreamEventsSeen == seen + 2);
        Assert.False(chat.StreamTrusted);

        // The negative control: a real event on the same connection does.
        retry.TryWrite(Durable(42, "assistant", payload: AssistantRow("a1", "marker")));
        await Until(() => chat.StreamTrusted);
        await Close(chat);
    }

    // The real stream ends quietly when cancelled — no Ended event, no
    // exception — and that is not read as a transport failure to reconnect from.
    [Fact]
    public async Task AStreamThatEndsQuietlyOnCancelIsNotReopened()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var stream = new FakeStream { EndsQuietlyOnCancel = true };
        var clock = new FakeClock();
        var chat = Sender(api, clock, stream: stream);

        chat.PanelOpened();
        await stream.NextOpenAsync();
        chat.PanelClosed();
        await chat.StreamTask!;

        Assert.Equal(1, stream.OpenCount);
        Assert.Empty(clock.Waits);
    }

    // --- ending, reconnecting, falling back ------------------------------------

    // A clean end is a server closing the connection: reopen from the last
    // durable sequence seen, after the floor wait.
    [Fact]
    public async Task ACleanEndReopensFromTheLastDurableEvent()
    {
        var (chat, _, stream, clock) = Streaming();
        chat.PanelOpened();
        var first = await stream.NextOpenAsync();

        first.TryWrite(Durable(57, "assistant", payload: AssistantRow("a1", "hi")));
        first.TryWrite(EndedCleanly);
        await stream.NextOpenAsync();

        Assert.Equal(57, stream.Opens[1].From);
        Assert.Equal(ClaudeCloudStreamPolicy.UnmeasuredReconnectAfterEnd, clock.Waits.Single());
        await Close(chat);
    }

    // An enumeration that just stops — no Ended event — is treated as a
    // transport failure and reopened, not as a clean end.
    [Fact]
    public async Task AStreamThatStopsWithoutSayingWhyIsReopened()
    {
        var (chat, _, stream, clock) = Streaming();
        chat.PanelOpened();

        (await stream.NextOpenAsync()).Complete();
        await stream.NextOpenAsync();

        Assert.Equal(Backoff.UnavailableFloor, clock.Waits.Single());
        await Close(chat);
    }

    [Theory]
    [InlineData(404, "Gone")]
    [InlineData(409, "Ended")]
    public async Task ASessionThatGoesOrEndsMidStreamTakesTheBoxAwayAndStops(int status, string expected)
    {
        var (chat, _, stream, _) = Streaming();
        chat.PanelOpened();

        (await stream.NextOpenAsync()).TryWrite(EndedWith(status));
        await chat.StreamTask!;

        Assert.Equal(expected, chat.Sendability.ToString());
        Assert.Equal(1, stream.OpenCount);
    }

    // The control for NextOpenAsync having no clock (CB-230): a run that ends
    // without opening again fails the wait at once and says so, rather than
    // leaving the test parked on an open that is never coming. Without this,
    // a green suite is equally consistent with every wait simply hanging
    // until something else gave up.
    [Fact]
    public async Task AWaitForAnOpenThatIsNeverComingFailsAsSoonAsTheRunEnds()
    {
        var (chat, _, stream, _) = Streaming();
        chat.PanelOpened();

        (await stream.NextOpenAsync()).TryWrite(EndedWith(404));

        var refused = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(stream.NextOpenAsync);
        Assert.Contains("ended without opening again", refused.Message);
        Assert.True(chat.StreamTask!.IsCompleted);
    }

    // The control for the backstop: a run parked in a held wait — alive, not
    // reopening, not ending — fails by name instead of hanging the suite. The
    // backstop is shortened for this one stream only; every other test keeps
    // the two-minute one its passing path never reaches.
    [Fact]
    public async Task AParkedRunFailsByNameRatherThanHanging()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var stream = new FakeStream();
        var clock = new FakeClock { Hold = true };
        var chat = Sender(api, clock, stream: stream);
        chat.PanelOpened();

        // The first open gets the long backstop: it needs a credential read on the
        // pool, which this test says nothing about. Only the wait for a *second*
        // open, which the held clock must prevent, is bounded short.
        (await stream.NextOpenAsync()).TryWrite(EndedWith(503));

        var parked = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(
            () => stream.NextOpenAsync(TimeSpan.FromMilliseconds(200)));
        Assert.Contains("has parked", parked.Message);
        Assert.Contains("opens so far: 1", parked.Message);
        Assert.False(chat.StreamTask!.IsCompleted);

        await Close(chat);
    }

    [Fact]
    public async Task AWaitForAnOpenWithNoRunStartedFailsAtOnce()
    {
        var (_, _, stream, _) = Streaming();

        var refused = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(stream.NextOpenAsync);
        Assert.Contains("no stream run was started", refused.Message);
    }

    [Fact]
    public async Task ARefusedLoginStopsTheStreamQuietly()
    {
        var (chat, _, stream, _) = Streaming();
        chat.PanelOpened();

        (await stream.NextOpenAsync()).TryWrite(EndedWith(401));
        await chat.StreamTask!;

        Assert.Equal(1, stream.OpenCount);
        Assert.Empty(chat.History);
        Assert.False(chat.IsReadOnly);
    }

    // A 429 says so once, backs off at least the rate-limit floor, and reopens.
    [Fact]
    public async Task ARateLimitNotesOnceAndBacksOff()
    {
        var (chat, _, stream, clock) = Streaming();
        chat.PanelOpened();

        (await stream.NextOpenAsync()).TryWrite(EndedWith(429));
        var second = await stream.NextOpenAsync();
        second.TryWrite(Delta("x"));
        second.TryWrite(EndedWith(429));
        await stream.NextOpenAsync();

        Assert.All(clock.Waits, w => Assert.True(w >= Backoff.RateLimitFloor));
        Assert.Single(chat.History, t => t.Text == ClaudeCloudChatSession.LivePausedNote);
        await Close(chat);
    }

    // Opens that fail without delivering anything, three in a row, and the panel
    // stops trying and polls instead — straight away if a turn is running.
    [Fact]
    public async Task OpensThatKeepFailingFallBackToPolling()
    {
        var (chat, api, stream, _) = Streaming(BusyRow());
        chat.PanelOpened();

        for (var i = 0; i < ClaudeCloudStreamPolicy.UnmeasuredFailuresBeforeFallback; i++)
        {
            (await stream.NextOpenAsync()).TryWrite(EndedWith(503));
        }

        await Until(() => chat.LiveTask is not null);
        await chat.LiveTask!;
        Assert.NotEmpty(api.Statuses);

        // The stream keeps trying behind the polling...
        var retry = await stream.NextOpenAsync();

        // ...and a send meanwhile polls rather than waiting on it.
        var before = chat.LiveTask;
        await chat.SendAsync("hello");
        Assert.NotSame(before, chat.LiveTask);
        await chat.LiveTask!;

        // A stream that delivers again is trusted again: the next send leaves
        // the turn to it.
        Assert.False(chat.StreamTrusted);
        retry.TryWrite(new CloudStreamEvent(CloudStreamEventKind.Ephemeral, "ephemeral_event", null, "{}",
            PayloadType: "system", Subtype: "commands_changed"));
        await Until(() => chat.StreamTrusted);
        var polled = chat.LiveTask;
        await chat.SendAsync("again");
        Assert.Same(polled, chat.LiveTask);

        await Close(chat);
    }

    // A connection is healthy if it carried a real event, or if it stayed open
    // long enough, and a healthy one is not a failure towards falling back. A
    // keepalive alone is neither: the negative control, which does fall back.
    [Theory]
    [InlineData("event", true)]
    [InlineData("old", true)]
    [InlineData("keepalive", false)]
    public async Task OnlyAHealthyConnectionKeepsTheStreamTrusted(string what, bool trusted)
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var stream = new FakeStream();
        var now = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
        var chat = new ClaudeCloudChatSession(Row(), api, new CountingCredentials(), a => a())
        {
            ReadBudget = TestBudgets.AlwaysAnswers,
            Delay = new FakeClock().Delay,
            Enabled = () => true,
            Stream = stream,

            // Each reading moves the clock on: an open and its end are one
            // reading apart, so "old" connections read as open past the age.
            Now = () => now += what == "old"
                ? ClaudeCloudStreamPolicy.UnmeasuredHealthyConnectionAge
                : TimeSpan.FromSeconds(1),
        };
        stream.Run = () => chat.StreamTask;
        chat.PanelOpened();

        for (var i = 0; i < ClaudeCloudStreamPolicy.UnmeasuredFailuresBeforeFallback; i++)
        {
            var events = await stream.NextOpenAsync();
            var seen = chat.StreamEventsSeen;
            events.TryWrite(what == "event"
                ? Durable(42 + i, "prompt_suggestion")
                : new CloudStreamEvent(CloudStreamEventKind.Keepalive, null, null, null));
            await Until(() => chat.StreamEventsSeen == seen + 1);
            events.TryWrite(EndedWith(503));
        }

        await stream.NextOpenAsync();
        Assert.Equal(trusted, chat.StreamTrusted);
        await Close(chat);
    }

    // An answer waiting will not change — an edge block, here — ends the
    // stream for this panel, and a running turn is polled instead.
    [Fact]
    public async Task AnAnswerThatWillNotChangeEndsTheStreamAndPollsInstead()
    {
        var (chat, api, stream, _) = Streaming(BusyRow());
        chat.PanelOpened();

        (await stream.NextOpenAsync()).TryWrite(EndedWith(403)); // no JSON body: an edge block
        await chat.StreamTask!;
        await chat.LiveTask!;

        Assert.Equal(1, stream.OpenCount);
        Assert.False(chat.IsReadOnly);
        Assert.NotEmpty(api.Statuses);
    }

    // Not knowing where to start is a reason to poll — never to open without a
    // sequence number and take the whole history again.
    // A start point that cannot be read means no stream yet — never one opened
    // without a sequence number — and polling carries a running turn, while
    // the start read is tried again behind it at the policy's wait.
    [Fact]
    public async Task AStartingPointThatCannotBeReadPollsAndKeepsTrying()
    {
        var (chat, api, stream, clock) = Streaming(BusyRow());
        api.Newest = () => Answer(500);

        chat.PanelOpened();
        await Until(() => chat.LiveTask is not null && api.NewestReads.Count >= 2);
        await chat.LiveTask!;

        Assert.Equal(0, stream.OpenCount);
        Assert.NotEmpty(api.Statuses);
        Assert.Contains(Backoff.UnavailableFloor, clock.Waits);

        // And once it can be read, the stream opens from it.
        api.Newest = () => new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""),
            "{\"data\":[{\"sequence_num\":\"9\"}]}");
        await stream.NextOpenAsync();
        Assert.Equal(9, stream.Opens.Single().From);

        await Close(chat);
    }

    // A start read the session itself refuses — it has been deleted — takes the
    // box away rather than being retried.
    [Fact]
    public async Task AStartingPointRefusedAsGoneTakesTheBoxAway()
    {
        var (chat, api, stream, _) = Streaming();
        api.Newest = () => Answer(404);

        chat.PanelOpened();
        await chat.StreamTask!;

        Assert.Equal(CloudSendability.Gone, chat.Sendability);
        Assert.Equal(0, stream.OpenCount);
    }

    // A session with no events yet has no history to replay, so opening from
    // the beginning is the right answer rather than a fallback.
    [Fact]
    public async Task ASessionWithNoEventsYetIsStreamedFromTheBeginning()
    {
        var (chat, api, stream, _) = Streaming();
        api.Newest = () => new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""), "{\"data\":[]}");

        chat.PanelOpened();
        await stream.NextOpenAsync();

        Assert.Null(stream.Opens.Single().From);
        await Close(chat);
    }

    // A malformed id never gets as far as asking where to start.
    [Fact]
    public async Task AMalformedIdIsNotStreamed()
    {
        var (chat, api, stream, _) = Streaming(Row("not a session"));

        chat.PanelOpened();
        await chat.StreamTask!;

        Assert.Equal(0, stream.OpenCount);
        Assert.Empty(api.NewestReads);
    }

    [Fact]
    public async Task NoLoginMeansNoStreamAndNoPolling()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var stream = new FakeStream();
        var creds = new CountingCredentials
        {
            Reading = new CredentialRead(CredentialOutcome.NotLoggedIn, null, null, "…"),
        };
        var chat = Sender(api, creds: creds, stream: stream);

        chat.PanelOpened();
        await chat.StreamTask!;

        Assert.Equal(0, stream.OpenCount);
        Assert.Empty(api.Requests);
        Assert.Null(chat.LiveTask);

        // Not in silence: once, in the credential layer's own words.
        Assert.Equal("Live updates paused: " + ClaudeCliCredentials.Describe(CredentialOutcome.NotLoggedIn) + ".",
            Assert.Single(chat.History).Text);
    }

    // Said once however many live sources notice: the stream stops, then the
    // roster reports a turn and the polling loop that starts hits the same
    // missing login.
    [Fact]
    public async Task ALiveLoginPauseIsSaidOnceAcrossTheStreamAndThePoll()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var creds = new CountingCredentials
        {
            Reading = new CredentialRead(CredentialOutcome.NotLoggedIn, null, null, "…",
                Lead: ClaudeCliCredentials.ExpiredLead),
        };
        var chat = Sender(api, creds: creds, stream: new FakeStream());

        chat.PanelOpened();
        await chat.StreamTask!;
        chat.UpdateStatus(BusyRow());
        await chat.LiveTask!;

        Assert.Equal("Live updates paused: " + ClaudeCliCredentials.ExpiredLead + ".",
            Assert.Single(chat.History).Text);

        // A new open is a fresh chance to say it.
        chat.PanelClosed();
        chat.PanelOpened();
        await chat.StreamTask!;
        Assert.Equal(2, chat.History.Count);
    }

    // After a login pause, a send that goes through brings the stream back.
    [Fact]
    public async Task ASendAfterALoginPauseBringsTheStreamBack()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var stream = new FakeStream();
        var creds = new CountingCredentials
        {
            Reading = new CredentialRead(CredentialOutcome.NotLoggedIn, null, null, "…"),
        };
        var chat = Sender(api, creds: creds, stream: stream);

        chat.PanelOpened();
        await chat.StreamTask!;
        Assert.Equal(0, stream.OpenCount);

        creds.Reading = new CredentialRead(CredentialOutcome.Found, Token, null, "a credential is present");
        Assert.Equal(ChatSendOutcome.Sent, await chat.SendAsync("hello"));
        await stream.NextOpenAsync();

        Assert.Null(chat.LiveTask);
        await Close(chat);
    }

    // --- the panel's lifetime ----------------------------------------------------

    // Closing ends the stream; reopening starts a fresh one from the newest
    // event, even after the last panel fell back.
    [Fact]
    public async Task ClosingEndsTheStreamAndReopeningStartsAFreshOne()
    {
        var (chat, api, stream, _) = Streaming();
        chat.PanelOpened();

        for (var i = 0; i < ClaudeCloudStreamPolicy.UnmeasuredFailuresBeforeFallback; i++)
        {
            (await stream.NextOpenAsync()).TryWrite(EndedWith(503));
        }

        await stream.NextOpenAsync(); // still retrying behind the fallback
        var first = chat.StreamTask!;
        chat.PanelClosed();
        await first;

        chat.PanelOpened();
        await stream.NextOpenAsync();

        Assert.Equal(2, api.NewestReads.Count);
        await Close(chat);
        Assert.True(chat.StreamTask!.IsCompleted);
    }

    // QA's defect F: a close and an immediate reopen — ChatPanel.Bind unbinds
    // the previous session first, so a same-session re-bind does exactly this —
    // must leave a stream running. The closed run is still unwinding when the
    // reopen arrives, and it must neither block the new run nor, exiting late,
    // clobber it.
    [Fact]
    public async Task ACloseAndAnImmediateReopenLeavesAStreamRunning()
    {
        var (chat, _, stream, _) = Streaming();
        chat.PanelOpened();
        await stream.NextOpenAsync();
        var first = chat.StreamTask!;

        chat.PanelClosed();
        chat.PanelOpened();

        var events = await stream.NextOpenAsync();
        await first;

        Assert.NotSame(first, chat.StreamTask);
        Assert.True(chat.StreamTrusted);

        events.TryWrite(Durable(42, "system", "init"));
        await Until(() => chat.CanInterrupt);
        await Close(chat);
    }

    // The same, with the closed run parked in its reconnect wait rather than in
    // a read: it wakes cancelled and leaves the new run's resume point and
    // trust alone.
    [Fact]
    public async Task AReopenDuringTheReconnectWaitIsNotUndoneByTheOldRun()
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var stream = new FakeStream();
        var clock = new FakeClock { Hold = true };
        var chat = Sender(api, clock, stream: stream);

        chat.PanelOpened();
        var events = await stream.NextOpenAsync();
        events.TryWrite(Durable(57, "assistant", payload: AssistantRow("a1", "hi")));
        events.TryWrite(EndedCleanly);
        await Until(() => clock.Waits.Count == 1);
        var first = chat.StreamTask!;

        chat.PanelClosed();
        chat.PanelOpened();
        await stream.NextOpenAsync();
        await first;

        // The new run started from the newest event, not from the old run's 57.
        Assert.Equal(41, stream.Opens[1].From);
        Assert.True(chat.StreamTrusted);
        await Close(chat);
    }

    // Closed while a read is in flight — the login, or the start point — the
    // run notices when the read answers and opens nothing.
    [Theory]
    [InlineData("login")]
    [InlineData("start")]
    public async Task ClosedDuringAReadOpensNothing(string which)
    {
        var api = new RoutingApi(Answer(200, Receipt));
        var stream = new FakeStream();
        var creds = new CountingCredentials();
        var chat = Sender(api, creds: creds, stream: stream);

        if (which == "login")
        {
            creds.OnRead = () => chat.PanelClosed();
        }
        else
        {
            api.Newest = () =>
            {
                chat.PanelClosed();
                return new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""), "{\"data\":[{\"sequence_num\":\"41\"}]}");
            };
        }

        chat.PanelOpened();
        await chat.StreamTask!;

        Assert.Equal(0, stream.OpenCount);
        Assert.Empty(chat.History);
        Assert.Equal(which == "login" ? 0 : 1, api.NewestReads.Count);
    }

    // A second PanelOpened while one stream runs does not open a second.
    [Fact]
    public async Task OneStreamPerOpenPanel()
    {
        var (chat, _, stream, _) = Streaming();
        chat.PanelOpened();
        await stream.NextOpenAsync();

        chat.PanelOpened();

        Assert.Equal(1, stream.OpenCount);
        await Close(chat);
    }

    // Closed while waiting to reconnect: the wait is cancelled, whether it
    // notices by throwing or by returning into a cancelled token.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClosingDuringTheReconnectWaitStops(bool throws)
    {
        var (chat, _, stream, clock) = Streaming();
        clock.OnDelay = (_, ct) =>
        {
            chat.PanelClosed();
            if (throws) ct.ThrowIfCancellationRequested();
        };
        chat.PanelOpened();

        (await stream.NextOpenAsync()).TryWrite(EndedCleanly);
        await chat.StreamTask!;

        Assert.Equal(1, stream.OpenCount);
    }

    // No stream configured, or a session that no longer takes input: nothing
    // is opened.
    [Fact]
    public void NoStreamIsOpenedWithoutAStreamOrForAReadOnlySession()
    {
        var plain = Sender(new RoutingApi(Answer(200, Receipt)));
        plain.PanelOpened();
        Assert.Null(plain.StreamTask);

        var stream = new FakeStream();
        var ended = Sender(new RoutingApi(Answer(200, Receipt)), stream: stream, session: Row(bucket: "archived"));
        ended.PanelOpened();
        Assert.Null(ended.StreamTask);
    }

    // --- ownership of busy ---------------------------------------------------------

    // While the stream is up it owns busy: the roster only updates the row, and
    // after the stream saw the turn end, a stale roster row does not put Stop
    // back.
    [Fact]
    public async Task WhileTheStreamIsUpTheRosterDoesNotDecideBusy()
    {
        var (chat, _, stream, _) = Streaming();
        chat.PanelOpened();
        var events = await stream.NextOpenAsync();

        events.TryWrite(Durable(42, "system", "init"));
        await Until(() => chat.CanInterrupt);

        chat.UpdateStatus(Row());
        Assert.True(chat.CanInterrupt);

        events.TryWrite(Durable(43, "result", "success"));
        await Until(() => !chat.CanInterrupt);

        chat.UpdateStatus(BusyRow());
        Assert.False(chat.CanInterrupt);
        Assert.Null(chat.LiveTask); // and no polling starts beside the stream

        await Close(chat);
    }

    // A delivered Stop with the stream up leaves it to the stream to report the
    // turn's end.
    [Fact]
    public async Task ADeliveredStopLeavesTheEndToTheStream()
    {
        var (chat, _, stream, _) = Streaming(BusyRow());
        chat.PanelOpened();
        await stream.NextOpenAsync();

        chat.Cancel();
        await chat.InterruptTask!;

        Assert.Null(chat.LiveTask);
        await Close(chat);
    }

    // --- the canary -----------------------------------------------------------------

    // The token reaches the stream as its credential and the newest-event read
    // as its header, and nothing the panel shows.
    [Fact]
    public async Task TheTokenGoesOnlyWhereItIsSent()
    {
        var (chat, api, stream, _) = Streaming();
        chat.PanelOpened();
        var events = await stream.NextOpenAsync();
        events.TryWrite(EndedWith(429));
        await stream.NextOpenAsync();

        Assert.Equal(Token, stream.Opens[0].Token);
        Assert.Equal(Token, api.NewestReads.Single().AccessToken);
        Assert.DoesNotContain(Token, api.NewestReads.Single().Path, StringComparison.Ordinal);
        Assert.All(chat.History, t => Assert.DoesNotContain(Token, t.Text, StringComparison.Ordinal));
        await Close(chat);
    }

    // --- the pure half --------------------------------------------------------------

    [Fact]
    public void ADurableRowIsThePayloadWhenThereIsOne()
    {
        var rows = CloudStreamRows.RowsFrom("{\"event_type\":\"client_event\",\"payload\":"
                                            + AssistantRow("a1", "hi") + "}");
        Assert.Equal("a1", Assert.Single(rows).Uuid);
    }

    [Fact]
    public void ADurableRowIsTheEventItselfWhenThereIsNoPayload()
    {
        Assert.Equal("u1", Assert.Single(CloudStreamRows.RowsFrom(UserRow("u1", "hi"))).Uuid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"payload\":\"not an object\",\"type\":\"nothing\"}")]
    public void AnUnusableDurableEventHasNoRows(string? json)
    {
        Assert.Empty(CloudStreamRows.RowsFrom(json));
    }

    [Theory]
    [InlineData("{\"payload\":{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}}", true)]
    [InlineData("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}", true)]
    [InlineData("{\"message\":{\"content\":[{\"type\":\"image\"},{\"type\":\"text\",\"text\":\"look\"}]}}", true)]
    [InlineData("{\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"x\"},{\"type\":\"tool_result\"}]}}", false)]
    [InlineData("{\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"ok\"}]}}", false)]
    [InlineData("{\"message\":{\"content\":[{\"type\":\"image\"}]}}", false)]
    [InlineData("{\"message\":{\"content\":[3,{\"type\":4}]}}", false)]
    [InlineData("{\"message\":{\"content\":\"   \"}}", false)]
    [InlineData("{\"message\":{\"content\":3}}", false)]
    [InlineData("{\"message\":{}}", false)]
    [InlineData("{\"message\":\"hi\"}", false)]
    [InlineData("{\"type\":\"user\"}", false)]
    [InlineData("[]", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyATypedMessageIsANewTurn(string? json, bool typed)
    {
        Assert.Equal(typed, CloudStreamRows.IsTypedMessage(json));
    }
}
