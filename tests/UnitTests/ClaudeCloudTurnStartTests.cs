using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// CB-285: after a send to a cloud session, the open panel never showed the reply.
//
// Measured against a real cold session: its status read idle about two seconds
// after the POST, before the turn had started — exactly where the live loop's
// first read lands. The loop took that for the end of the turn, set the mask
// that stops a roster "working" from putting Stop back, and nothing ever read
// the reply. A trusted stream that said nothing about a send left nothing
// polling at all. These cases pin the fix: an idle read ends a sent turn only
// on evidence it happened, the mask can never hide a turn for good, and a
// silent stream is polled beside.
//
// The status sequences are the measured shapes, not invented ones: "idle,
// working, idle, working" is what the cold session actually read (see
// docs/claude-cloud-findings.md).
public partial class ClaudeCloudEventsTests
{
    // --- the fixture ---------------------------------------------------------

    // A session that answers every turn it is sent: its transcript holds each
    // posted message's echo — the payload as sent, which is a transcript row —
    // and a reply after it. What a real history shows once a turn has run.
    private static RoutingApi Replying() => Scripted(() => true);

    // The same session, but the reply is there only once `ran` says so. Until
    // then the transcript holds nothing at all: on the measured cold session the
    // echo itself was absent until the turn started.
    private static RoutingApi Scripted(Func<bool> ran)
    {
        RoutingApi? api = null;
        api = new RoutingApi(c => c.Method == HttpMethod.Post
            ? Answer(200, Receipt)
            : new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""),
                Envelope(ran() ? Answered(api!) : Array.Empty<string>())));
        return api;
    }

    private static string[] Answered(RoutingApi api) =>
        api.Posts.SelectMany((post, i) => new[]
        {
            EchoOf(post),
            AssistantRow("reply-" + i, "the reply"),
        }).ToArray();

    private static string EchoOf(CloudRequestContext post)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(post.Body!);
        return doc.RootElement.GetProperty("events")[0].GetProperty("payload").GetRawText();
    }

    // Status reads answer `buckets` in order, the last repeated. The turn has
    // "run" — its reply is in the transcript — from the read `ranFrom` on.
    private static (RoutingApi Api, Func<int> Reads) Turn(string[] buckets, int ranFrom)
    {
        var reads = 0;
        var api = Scripted(() => reads >= ranFrom);
        api.Status = () => Status(buckets[Math.Min(++reads, buckets.Length) - 1]);
        return (api, () => reads);
    }

    private static bool HasReply(ClaudeCloudChatSession chat) =>
        chat.History.Any(t => t.Role == ChatRole.Assistant && t.Text == "the reply");

    private static int Lines(ClaudeCloudChatSession chat, string containing) =>
        LogOf(chat).Count(l => l.Contains(containing, StringComparison.Ordinal));

    // Every wait on a loop or a watch in this file is bounded (CB-285 review).
    // QA's mutation of WatchSend — the previous watch never cancelled — made an
    // unbounded `await first` hang the run forever rather than fail, and a hung
    // test is how a CI leg dies with no message (CB-269, CB-336). With the fake
    // clock nothing here legitimately takes more than milliseconds, so five
    // seconds fails a regression fast and names what it was waiting for.
    private static async Task Bounded(Task? task, string what)
    {
        Assert.True(task is not null, what + " was never started");
        try
        {
            await task!.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            Assert.Fail(what + " did not finish within five seconds");
        }
    }

    private static readonly int GraceTicks =
        (int)(ClaudeCloudChatSession.UnmeasuredTurnStartGrace / Tick);

    // --- the loop, no stream ---------------------------------------------------

    // Case 1. Idle twice before the turn starts. Develop ended the turn at the
    // first read and the reply was never fetched.
    [Fact]
    public async Task IdleBeforeTheSentTurnStartsDoesNotEndIt()
    {
        var clock = new FakeClock();
        var (api, _) = Turn(new[] { "idle", "idle", "working", "working", "idle" }, ranFrom: 5);
        var chat = Sender(api, clock);
        chat.PanelOpened();
        var stopAtEachTick = new List<bool>();
        clock.OnDelay = (n, _) => { if (n > 1) stopAtEachTick.Add(chat.CanInterrupt); };

        await chat.SendAsync("hello");
        await Bounded(chat.LiveTask, "the live loop");

        Assert.True(HasReply(chat));
        Assert.Equal(5, api.Statuses.Count);
        Assert.All(stopAtEachTick, Assert.True); // on until the end...
        Assert.Equal(4, stopAtEachTick.Count);
        Assert.False(chat.CanInterrupt);         // ...and off at it

        Assert.Equal(1, Lines(chat, "watching by poll"));
        Assert.Equal(1, Lines(chat, "idle ignored, sent turn not started"));
        Assert.Equal(1, Lines(chat, "turn started after 6s"));
        Assert.Equal(1, Lines(chat, "turn over after 10s (reply in transcript)"));
    }

    // Case 2. The measured cold shape: idle, working, idle again with no echo,
    // then the turn. One "working" read is not proof the turn has happened.
    [Fact]
    public async Task TheMeasuredColdFlapDoesNotEndTheTurn()
    {
        var clock = new FakeClock();
        var (api, _) = Turn(new[] { "idle", "working", "idle", "working", "working", "idle" }, ranFrom: 6);
        var chat = Sender(api, clock);

        await chat.SendAsync("hello");
        await Bounded(chat.LiveTask, "the live loop");

        Assert.True(HasReply(chat));
        Assert.Equal(6, api.Statuses.Count);
        Assert.False(chat.CanInterrupt);
        Assert.Equal(1, Lines(chat, "idle ignored"));   // once per loop, not per idle read
        Assert.Equal(1, Lines(chat, "turn started"));
    }

    // Case 3. A turn that never starts. The loop stops watching at the grace,
    // hands busy to the roster without claiming the turn ended, and a roster
    // "working" puts Stop back and watches it.
    [Fact]
    public async Task ATurnThatNeverStartsIsGivenUpOnAtTheGraceAndTheRosterTakesOver()
    {
        var clock = new FakeClock();
        var (api, _) = Turn(new[] { "idle" }, ranFrom: int.MaxValue);
        var chat = Sender(api, clock);
        chat.PanelOpened();
        var stopAtSecondTick = false;
        clock.OnDelay = (n, _) => { if (n == 2) stopAtSecondTick = chat.CanInterrupt; };

        await chat.SendAsync("hello");
        var loop = chat.LiveTask!;
        await Bounded(loop, "the live loop");

        Assert.True(stopAtSecondTick);
        Assert.Equal(GraceTicks, api.Statuses.Count);
        Assert.Equal(15, GraceTicks); // the figure the plan and the constant's comment quote
        Assert.False(chat.CanInterrupt);
        Assert.Equal(1, Lines(chat, "idle ignored"));
        Assert.Equal(1, Lines(chat, "gave up after 30s, no reply seen, roster owns busy"));

        // Not masked: the roster seeing the turn puts Stop back and starts a
        // loop to watch it, which ends it on its own first idle read.
        chat.UpdateStatus(BusyRow());
        Assert.True(chat.CanInterrupt);
        Assert.NotSame(loop, chat.LiveTask);
        await Bounded(chat.LiveTask, "the live loop");
        Assert.Equal(1, Lines(chat, "turn over after 2s (joined)"));
    }

    // Case 4. A turn the roster sees running while the mask hides it, then sees
    // end: that turn's reply is read once, even though nobody watched it.
    [Fact]
    public async Task AMaskedWorkingIsCaughtUpWhenTheRosterSaysIdle()
    {
        var clock = new FakeClock();
        var late = false;
        RoutingApi? lateApi = null;
        lateApi = new RoutingApi(c => c.Method == HttpMethod.Post
            ? Answer(200, Receipt)
            : new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""), Envelope(late
                ? Answered(lateApi!).Append(AssistantRow("late", "a late reply")).ToArray()
                : Answered(lateApi!))));
        var chat = Sender(lateApi, clock);
        chat.PanelOpened();

        await chat.SendAsync("hello");
        await Bounded(chat.LiveTask, "the live loop");
        var reads = lateApi.Transcripts.Count;

        late = true;
        chat.UpdateStatus(BusyRow());
        Assert.False(chat.CanInterrupt);     // still masked
        Assert.Equal(reads, lateApi.Transcripts.Count);

        chat.UpdateStatus(Row());
        await Bounded(chat.LoadTask, "the transcript read");

        Assert.Equal(reads + 1, lateApi.Transcripts.Count);
        Assert.Contains(chat.History, t => t.Text == "a late reply");
        Assert.Equal(1, Lines(chat, "caught up after a masked working"));
    }

    // Case 4's control: no masked working, no extra read.
    [Fact]
    public async Task ARosterIdleWithNothingMaskedReadsNothing()
    {
        var clock = new FakeClock();
        var api = Replying();
        var chat = Sender(api, clock);
        chat.PanelOpened();

        await chat.SendAsync("hello");
        await Bounded(chat.LiveTask, "the live loop");
        var reads = api.Transcripts.Count;

        chat.UpdateStatus(Row());
        chat.UpdateStatus(Row());

        Assert.Equal(reads, api.Transcripts.Count);
        Assert.Equal(0, Lines(chat, "caught up"));
    }

    // A masked working remembered for a closed panel reads nothing (nobody is
    // looking; the next open reads anyway) and is forgotten.
    [Fact]
    public async Task AMaskedWorkingForAClosedPanelReadsNothing()
    {
        var clock = new FakeClock();
        var api = Replying();
        var chat = Sender(api, clock);

        await chat.SendAsync("hello");
        await Bounded(chat.LiveTask, "the live loop");
        var reads = api.Transcripts.Count;

        chat.UpdateStatus(BusyRow());
        chat.UpdateStatus(Row());

        Assert.Equal(reads, api.Transcripts.Count);
    }

    // Case 5. The negative control: a turn that finished inside the first tick
    // costs what it always did — one status read, one transcript read.
    [Fact]
    public async Task ATurnFinishedByTheFirstTickEndsThereForOneReadOfEach()
    {
        var clock = new FakeClock();
        var api = Replying();
        var chat = Sender(api, clock);

        await chat.SendAsync("hello");
        await Bounded(chat.LiveTask, "the live loop");

        Assert.Single(api.Statuses);
        Assert.Single(api.Transcripts);
        Assert.False(chat.CanInterrupt);
        Assert.True(HasReply(chat));
        Assert.Equal(1, Lines(chat, "turn over after 2s (reply in transcript)"));
        Assert.Equal(0, Lines(chat, "idle ignored"));
    }

    // Case 6. Stop was pressed: an idle read is the interrupt landing, reply or
    // not, and ends the turn at once.
    [Fact]
    public async Task AfterStopAnIdleReadEndsTheTurnWithoutAReply()
    {
        var clock = new FakeClock();
        var (api, _) = Turn(new[] { "idle" }, ranFrom: int.MaxValue);
        var chat = Sender(api, clock);
        clock.OnDelay = (n, _) => { if (n == 1) chat.Cancel(); };

        await chat.SendAsync("hello");
        await Bounded(chat.LiveTask, "the live loop");
        await Bounded(chat.InterruptTask, "the interrupt");

        Assert.Single(api.Statuses);
        Assert.False(chat.CanInterrupt);
        Assert.Equal(1, Lines(chat, "turn over after 2s (interrupt)"));
    }

    // Case 7. A 429 on the read that checks whether the turn has started is a
    // 429 like any other: the note, and the loop ends.
    [Fact]
    public async Task ARateLimitOnTheNotStartedReadPausesTheLoop()
    {
        var clock = new FakeClock();
        var api = new RoutingApi(Answer(200, Receipt), Answer(429));
        var chat = Sender(api, clock);

        await chat.SendAsync("hello");
        await Bounded(chat.LiveTask, "the live loop");

        Assert.Single(api.Statuses);
        Assert.Equal(ClaudeCloudChatSession.LivePausedNote, chat.History.Last().Text);
        Assert.Equal(1, Lines(chat, "paused, rate limited"));
    }

    // A read that fails some other way says nothing either way: the loop keeps
    // waiting as it would on an empty transcript.
    [Fact]
    public async Task AFailedNotStartedReadIsNoEvidenceEitherWay()
    {
        var clock = new FakeClock();
        var reads = 0;
        RoutingApi? api = null;
        api = new RoutingApi(c => c.Method == HttpMethod.Post ? Answer(200, Receipt)
            : ++reads == 1 ? Answer(503)
            : new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""), Envelope(Answered(api!))));
        var chat = Sender(api, clock);

        await chat.SendAsync("hello");
        await Bounded(chat.LiveTask, "the live loop");

        Assert.Equal(2, api.Statuses.Count);
        Assert.True(HasReply(chat));
    }

    // Case 8. A loop joined from the roster has no turn of its own to wait for,
    // and still ends on its first idle read — after which it reads the reply.
    [Fact]
    public async Task AJoinedLoopStillEndsOnItsFirstIdleRead()
    {
        var clock = new FakeClock();
        var api = new RoutingApi(Answer(200, Receipt));
        var chat = Sender(api, clock);
        chat.PanelOpened();

        chat.UpdateStatus(BusyRow());
        await Bounded(chat.LiveTask, "the live loop");
        await Bounded(chat.LoadTask, "the transcript read");

        Assert.Single(api.Statuses);
        Assert.Single(api.Transcripts);
        Assert.False(chat.CanInterrupt);
        Assert.Equal(1, Lines(chat, "turn over after 2s (joined)"));
    }

    // --- the pure rules --------------------------------------------------------

    private static ChatTranscript.Row R(string uuid, ChatRole role) =>
        new(uuid, new ChatTurn { Role = role, Text = uuid, IsComplete = true });

    public static IEnumerable<object[]> ReplyCases() => new[]
    {
        new object[] { "no echo", new[] { R("x", ChatRole.User), R("a", ChatRole.Assistant) }, false },
        new object[] { "echo last", new[] { R("a", ChatRole.Assistant), R("sent", ChatRole.User) }, false },
        new object[] { "echo then assistant", new[] { R("sent", ChatRole.User), R("a", ChatRole.Assistant) }, true },
        new object[] { "echo, tool row, then assistant",
            new[] { R("sent", ChatRole.User), R("t", ChatRole.System), R("a", ChatRole.Assistant) }, true },
        new object[] { "echo then a user row only", new[] { R("sent", ChatRole.User), R("y", ChatRole.User) }, false },
        new object[] { "empty", Array.Empty<ChatTranscript.Row>(), false },
    };

    [Theory]
    [MemberData(nameof(ReplyCases))]
    public void AReplyFollowsOnlyAnAssistantRowAfterTheEcho(string _, ChatTranscript.Row[] rows, bool expected)
    {
        Assert.Equal(expected, CloudTurnProgress.ReplyFollows(rows, "sent"));
    }

    [Theory]
    // not watching a send: idle ends it, whatever else is true
    [InlineData(false, false, false, 2, "TurnOver")]
    [InlineData(false, false, false, 60, "TurnOver")]
    // a reply, or Stop: over
    [InlineData(true, true, false, 2, "TurnOver")]
    [InlineData(true, false, true, 2, "TurnOver")]
    // at and past the grace: given up
    [InlineData(true, false, false, 30, "GaveUp")]
    [InlineData(true, false, false, 31, "GaveUp")]
    // inside it: not started yet
    [InlineData(true, false, false, 2, "NotStartedYet")]
    [InlineData(true, false, false, 29, "NotStartedYet")]
    public void TheIdleRulesApplyInOrder(bool watching, bool replied, bool stopped, int seconds,
        string expected)
    {
        Assert.Equal(Enum.Parse<IdleVerdict>(expected), CloudTurnProgress.OnIdle(watching, replied, stopped,
            TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(30)));
    }

    // --- a trusted stream that says nothing ------------------------------------

    private static (ClaudeCloudChatSession Chat, RoutingApi Api, FakeStream Stream, FakeClock Clock)
        ReplyingStream()
    {
        var api = Replying();
        var stream = new FakeStream();
        var clock = new FakeClock();
        return (Sender(api, clock, stream: stream), api, stream, clock);
    }

    // Case 11. Connected and trusted, and silent about the send: after the echo
    // grace, polling starts beside it and fetches the reply. Develop left Stop up
    // and the reply unread for good.
    [Fact]
    public async Task ASilentTrustedStreamIsPolledBesideAfterTheEchoGrace()
    {
        var (chat, api, stream, clock) = ReplyingStream();
        chat.PanelOpened();
        await stream.NextOpenAsync();

        await chat.SendAsync("hello");
        Assert.True(chat.StreamTrusted);
        Assert.Null(chat.LiveTask);
        Assert.Equal(1, Lines(chat, "watching by stream"));

        clock.ReleaseEchoGrace();
        await Bounded(chat.SendWatchTask, "the send watch");
        await Bounded(chat.LiveTask, "the live loop");

        Assert.True(HasReply(chat));
        Assert.False(chat.CanInterrupt);
        Assert.NotEmpty(api.Statuses);
        Assert.Equal(1, Lines(chat, "silent 10s after a send, polling beside it"));
        Assert.Equal(1, Lines(chat, "turn over after 12s (reply in transcript)"));

        await Close(chat);
    }

    // Case 12, the control. A stream that says anything about the turn inside
    // the grace owns it: no polling.
    [Fact]
    public async Task AStreamThatSpeaksInsideTheGraceIsLeftAlone()
    {
        var (chat, api, stream, clock) = ReplyingStream();
        chat.PanelOpened();
        var events = await stream.NextOpenAsync();

        await chat.SendAsync("hello");
        events.TryWrite(Durable(42, "system", "status", status: "requesting"));
        await Until(() => chat.StreamEventsSeen == 1);

        clock.ReleaseEchoGrace();
        await Bounded(chat.SendWatchTask, "the send watch");

        Assert.Null(chat.LiveTask);
        Assert.Empty(api.Statuses);
        Assert.Equal(0, Lines(chat, "silent"));

        await Close(chat);
    }

    // The stream ended the turn inside the grace: nothing left to watch.
    [Fact]
    public async Task AStreamThatEndsTheTurnInsideTheGraceIsLeftAlone()
    {
        var (chat, api, stream, clock) = ReplyingStream();
        chat.PanelOpened();
        var events = await stream.NextOpenAsync();

        await chat.SendAsync("hello");
        events.TryWrite(Durable(42, "result"));
        await Until(() => !chat.CanInterrupt);

        clock.ReleaseEchoGrace();
        await Bounded(chat.SendWatchTask, "the send watch");

        Assert.Null(chat.LiveTask);
        Assert.Equal(1, Lines(chat, "stream session_a: turn over (result)"));

        await Close(chat);
    }

    // A second send on a trusted stream replaces the first send's watch: one
    // grace per turn, from that turn's own send.
    [Fact]
    public async Task ASecondSendReplacesTheFirstSendsWatch()
    {
        var (chat, api, stream, clock) = ReplyingStream();
        chat.PanelOpened();
        await stream.NextOpenAsync();

        await chat.SendAsync("one");
        var first = chat.SendWatchTask!;
        await chat.SendAsync("two");
        await Bounded(first, "the first send's watch");

        Assert.NotSame(first, chat.SendWatchTask);
        Assert.Null(chat.LiveTask);

        clock.ReleaseEchoGrace();
        await Bounded(chat.SendWatchTask, "the send watch");
        await Bounded(chat.LiveTask, "the live loop");

        Assert.Equal(1, Lines(chat, "polling beside it"));
        Assert.True(HasReply(chat));

        await Close(chat);
    }

    // Case 13. Closing the panel during the grace ends the watch with it.
    [Fact]
    public async Task ClosingThePanelDuringTheGraceStartsNoPolling()
    {
        var (chat, api, stream, clock) = ReplyingStream();
        chat.PanelOpened();
        await stream.NextOpenAsync();

        await chat.SendAsync("hello");
        await Close(chat);
        await Bounded(chat.SendWatchTask, "the send watch");

        Assert.Null(chat.LiveTask);
        Assert.Empty(api.Statuses);
    }

    // Case 13, second half. A stream that falls back during the grace has
    // started polling itself — and for the sent turn, not as a joined loop that
    // would believe the first idle read — so the watch adds nothing.
    [Fact]
    public async Task AStreamThatFallsBackDuringTheGraceIsNotPolledTwice()
    {
        // Idle first, then the turn: a joined loop would end at the idle read
        // over an empty transcript and never see the reply.
        var (api, _) = Turn(new[] { "idle", "working", "idle" }, ranFrom: 3);
        var stream = new FakeStream();
        var clock = new FakeClock();
        var chat = Sender(api, clock, stream: stream);
        chat.PanelOpened();
        var first = await stream.NextOpenAsync();

        await chat.SendAsync("hello");
        Assert.True(chat.StreamTrusted);

        // Three opens in a row that deliver nothing.
        first.TryWrite(EndedWith(503));
        for (var i = 1; i < ClaudeCloudStreamPolicy.UnmeasuredFailuresBeforeFallback; i++)
        {
            (await stream.NextOpenAsync()).TryWrite(EndedWith(503));
        }

        await Until(() => !chat.StreamTrusted && chat.LiveTask is not null);
        var loop = chat.LiveTask!;
        await Bounded(loop, "the live loop");

        Assert.True(HasReply(chat));
        Assert.Equal(3, api.Statuses.Count);
        Assert.Equal(1, Lines(chat, "idle ignored"));

        clock.ReleaseEchoGrace();
        await Bounded(chat.SendWatchTask, "the send watch");

        Assert.Same(loop, chat.LiveTask);
        Assert.Equal(0, Lines(chat, "silent"));

        await Close(chat);
    }
}
