using Xunit;

namespace Orbweaver.Tests;

// CB-443: PaperclipChatSession's decisions, with an inline dispatcher, a fake feed and fake
// actions. The panel-level behaviour is in tests/UiTests/PaperclipChatPanelTests.
public class PaperclipChatSessionTests : IDisposable
{
    internal sealed class Feed : IPaperclipChatFeed
    {
        public event Action<ChatSnapshot>? Updated;
        public List<string> Calls { get; } = new();
        public void Opened() => Calls.Add("opened");
        public void Closed() => Calls.Add("closed");
        public void Nudge() => Calls.Add("nudge");
        public void Push(ChatSnapshot s) => Updated?.Invoke(s);
    }

    internal sealed class Actions : IPaperclipActions
    {
        public PaperclipSent Reply { get; set; } = new(new(PaperclipActionResult.Done), "c1", "i9");
        public Exception? Throw { get; set; }
        public List<string> Sent { get; } = new();
        public Task<PaperclipSent> CommentAsync(string issueId, string text, CancellationToken ct)
        { if (Throw != null) throw Throw; Sent.Add("comment:" + issueId + ":" + text); return Task.FromResult(Reply); }
        public Task<PaperclipSent> OpenTaskAsync(string agentId, string text, CancellationToken ct)
        { if (Throw != null) throw Throw; Sent.Add("open:" + agentId + ":" + text); return Task.FromResult(Reply); }
        public Task<PaperclipOutcome> DecideAsync(PaperclipNeed need, bool approve, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> WakeAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> PauseAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> ResumeAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
    }

    private static readonly DateTime T0 = new(2026, 11, 24, 4, 0, 0, DateTimeKind.Utc);
    private DateTime _now = T0;
    private PaperclipAgent[] _roster = [];
    private PaperclipLink _link = PaperclipLink.Connected;
    private PaperclipSessions.ChatView _view = Empty;
    private IPaperclipActions? _actions;
    private IReadOnlyList<PaperclipChat.AssignedIssue>? _assignedAnswer;
    private int _assignedReads;
    private readonly List<(TimeSpan After, Action Run)> _scheduled = new();

    private static readonly PaperclipSessions.ChatView Empty = new(
        Array.Empty<PaperclipApi.RawLiveRun>(), Array.Empty<PaperclipApi.RawRun>(), Array.Empty<PaperclipApi.RawIssue>());

    public void Dispose() => PaperclipSessions.Publish([], PaperclipLink.Off, "Paperclip is switched off.");

    private static PaperclipChat.TaskRef TaskOf(string id = "i1") => new(id, "CB-1", "t", "todo");
    private static PaperclipChat.Comment C(string id, string body, bool mine = false, string? run = null, int sec = 0) =>
        new(id, body, mine ? "ag1" : null, mine ? null : "u1", run, T0.AddSeconds(sec));
    private static ChatSnapshot Snap(string? taskId, bool refused = false, PaperclipChat.Activity? act = null, params PaperclipChat.Comment[] cs) =>
        new(taskId is null ? null : TaskOf(taskId), cs, act, refused);

    private (PaperclipChatSession S, Feed F, Actions A) Make(string name = "Nova", string? url = "https://p/a")
    {
        var f = new Feed();
        var a = new Actions();
        _actions = a;
        var s = new PaperclipChatSession("ag1", name, url, () => f, () => _actions, () => _roster, () => _view,
            async ct => { _assignedReads++; await Task.Yield(); return _assignedAnswer; },
            () => _link, () => _now, act => act(), (after, run) => _scheduled.Add((after, run)));
        s.PanelOpened();
        return (s, f, a);
    }

    private static PaperclipAgent Agent(string state = "idle", string status = "idle", string? task = null) =>
        new("ag1", "Nova", "r", "t", status, null, "paperclip_runner", state, PaperclipMark.None, null, [], task, null, null, null, null,
            T0, "u", false);

    private static PaperclipApi.RawLiveRun Live(string run, string? issue, string agent = "ag1", int sec = 0) =>
        new(run, agent, "running", T0.AddSeconds(sec), issue, null, null);
    private static PaperclipApi.RawRun Done(string run, string? issue, int sec = 0) =>
        new(run, "ag1", "succeeded", T0, T0.AddSeconds(sec), null, issue, null, null, false);

    [Fact]
    public void IdentityAndDefaults()
    {
        var (s, f, _) = Make();
        Assert.Equal("paperclip:ag1", s.SessionId);
        Assert.Equal("Nova", s.DisplayName);
        Assert.Equal("https://p/a", s.ReplyUrl);
        Assert.Equal("Open in Paperclip", s.ReplyLabel);
        Assert.False(s.IsReadOnly);
        Assert.False(s.HasMore);
        Assert.Equal(RemoteChatState.Connecting, s.State);
        Assert.Equal("Message Nova. This opens a new task.", s.ComposerHint);
        Assert.Equal(new[] { "opened" }, f.Calls);
        s.Cancel();
        Assert.False(s.LoadOlderAsync(default).Result);
        var other = new PaperclipChatSession("ag2", " ", "", () => f, () => null);
        Assert.Equal("ag2", other.DisplayName);
        Assert.Null(other.ReplyUrl);
        other.HistoryPrepended += _ => { };
    }

    [Fact]
    public void LifetimeIsIdempotentAndStopsTheFeed()
    {
        var (s, f, _) = Make();
        s.PanelOpened();
        s.PanelClosed();
        s.PanelClosed();
        Assert.Equal(new[] { "opened", "closed" }, f.Calls);
        // Closed: a late snapshot changes nothing.
        s.Apply(Snap("i1"));
        Assert.Empty(s.History);
    }

    [Fact]
    public void FirstSnapshotReplacesHistory_ThenRowsAppendAndUpdateInPlace()
    {
        var (s, f, _) = Make();
        var replaced = 0; var added = new List<string>(); var updated = new List<string>();
        s.HistoryReplaced += () => replaced++;
        s.TurnAdded += t => added.Add(t.Text);
        s.TurnUpdated += t => updated.Add(t.Text);
        var states = new List<RemoteChatState>(); s.StateChanged += states.Add;

        f.Push(Snap("i1", cs: C("1", "hi")));
        Assert.Equal(1, replaced);
        Assert.Equal(2, s.History.Count);              // the task row, then the comment
        Assert.Equal(ChatRole.User, s.History[1].Role);
        Assert.Equal(new[] { RemoteChatState.Connected }, states);

        f.Push(Snap("i1", cs: [C("1", "hi"), C("2", "yo", mine: true, sec: 5)]));
        Assert.Equal(new[] { "yo" }, added);
        Assert.Equal(ChatRole.Assistant, s.History[2].Role);

        // The task row's own words change in place when the task's status moves.
        f.Push(new ChatSnapshot(new("i1", "CB-1", "t", "done"), [C("1", "hi"), C("2", "yo", mine: true, sec: 5)], null, false));
        Assert.Single(updated);
        Assert.Contains("done", updated[0]);

        // Unchanged rows raise nothing.
        f.Push(new ChatSnapshot(new("i1", "CB-1", "t", "done"), [C("1", "hi"), C("2", "yo", mine: true, sec: 5)], null, false));
        Assert.Single(updated);
        Assert.Single(added);
    }

    [Fact]
    public void ARunsActivityShowsAsToolLinesAndAFailedOneIsRed_AndTheAnswerIsReplacedByItsComment()
    {
        var (s, f, _) = Make();
        var act = PaperclipChat.Activity.Empty("r1") with
        {
            Ended = true,   // a comment stands for the answer only once the run has ended (measured shape, CB-443)
            Rows =
            [
                new("r:r1:t:a", ChatRole.System, "Bash: git status", T0.AddSeconds(2)),
                new("r:r1:t:b", ChatRole.System, "Bash: dotnet test", T0.AddSeconds(3), true),
                new("r:r1:answer", ChatRole.Assistant, "Done.", T0.AddSeconds(4)),
            ]
        };
        f.Push(Snap("i1", act: act, cs: C("1", "go")));
        Assert.Equal(5, s.History.Count);
        Assert.True(s.History[3].IsError);

        // The comment carrying the run's answer takes the answer row's place: no doubled bubble.
        var updated = new List<ChatTurn>(); s.TurnUpdated += updated.Add;
        f.Push(Snap("i1", act: act, cs: [C("1", "go"), C("2", "Done.", mine: true, run: "r1", sec: 5)]));
        Assert.Equal(5, s.History.Count);
        Assert.Empty(updated);   // same words, same place
    }

    [Fact]
    public void ATaskChangeReplacesTheThread_AndANullTaskKeepsItsPlace()
    {
        var (s, f, _) = Make();
        var replaced = 0;
        s.HistoryReplaced += () => replaced++;
        f.Push(Snap("i1", cs: C("1", "a")));
        f.Push(Snap("i2", cs: C("9", "b")));
        Assert.Equal(2, replaced);
        Assert.Equal("b", s.History[^1].Text);
        f.Push(Snap(null, cs: C("9", "b")));
        Assert.Equal(3, replaced);   // from a task to none is a change too
    }

    [Fact]
    public void AnUnreadableCommentIdIsAdoptedByTheCommentThatArrives()
    {
        var (s, f, a) = Make();
        a.Reply = new(new(PaperclipActionResult.Done), null, null);
        f.Push(Snap("i1"));
        Assert.Equal(ChatSendOutcome.Sent, s.SendAsync("hello").Result);
        Assert.Equal(2, s.History.Count);
        f.Push(Snap("i1", cs: C("5", "hello", sec: 10)));
        Assert.Equal(2, s.History.Count);
    }

    [Fact]
    public void SendCommentsOnTheTask_RaisesOneBubble_NudgesTheFeed_AndTheCommentThatArrivesIsTheSameBubble()
    {
        var (s, f, a) = Make();
        f.Push(Snap("i1"));
        var added = new List<ChatTurn>();
        s.TurnAdded += added.Add;

        Assert.Equal(ChatSendOutcome.Sent, s.SendAsync("  update?  ").Result);

        Assert.Equal(new[] { "comment:i1:update?" }, a.Sent);
        Assert.Equal("update?", Assert.Single(added).Text);
        Assert.Equal(ChatRole.User, added[0].Role);
        Assert.Contains("nudge", f.Calls);
        Assert.Equal("Waking Nova…", s.ComposerHint);

        f.Push(Snap("i1", cs: C("c1", "update?", sec: 3)));
        Assert.Equal(2, s.History.Count);
    }

    [Fact]
    public void WithNoTaskTheMessageOpensOne_ThatTaskIsThePanelsUntilTheAgentRunsElsewhere()
    {
        var (s, f, a) = Make();
        f.Push(Snap(null));
        Assert.Equal(ChatSendOutcome.Sent, s.SendAsync("first line\nsecond").Result);
        Assert.Equal(new[] { "open:ag1:first line\nsecond" }, a.Sent);

        var target = s.Target();
        Assert.Equal("i9", target.Task!.Id);
        Assert.Equal("first line", target.Task.Title);

        // The agent's own run on another task wins from then on.
        _view = new([Live("rX", "i77")], [], []);
        Assert.Equal("i77", s.Target().Task!.Id);
        _view = Empty;
        Assert.Null(s.Target().Task);
    }

    [Theory]
    [InlineData("NeedsReconnect", null, "Reconnect to Paperclip to message Nova.")]
    [InlineData("Unreachable", "x", "Can't reach Paperclip.")]
    [InlineData("Refused", "Comment too long", "Comment too long")]
    [InlineData("Refused", " ", "Paperclip refused that.")]
    [InlineData("Refused", null, "Paperclip refused that.")]
    public void EveryRefusalIsOneRedLine_AndFailed(string resultName, string? why, string expected)
    {
        var (s, f, a) = Make();
        a.Reply = new(new(Enum.Parse<PaperclipActionResult>(resultName), why));
        f.Push(Snap("i1"));
        Assert.Equal(ChatSendOutcome.Failed, s.SendAsync("hi").Result);
        var line = s.History[^1];
        Assert.Equal(expected, line.Text);
        Assert.True(line.IsError);
        Assert.Equal(ChatRole.System, line.Role);
        Assert.DoesNotContain("nudge", f.Calls);
        Assert.Equal("Message Nova. It wakes to read it.", s.ComposerHint);
    }

    [Fact]
    public void ATaskThatIsGoneNudgesTheFeedToFindTheCurrentOne()
    {
        var (s, f, a) = Make();
        a.Reply = new(new(PaperclipActionResult.Refused, PaperclipActions.TaskGoneWhy));
        f.Push(Snap("i1"));
        Assert.Equal(ChatSendOutcome.Failed, s.SendAsync("hi").Result);
        Assert.Equal("That task is no longer in Paperclip.", s.History[^1].Text);
        Assert.Contains("nudge", f.Calls);
    }

    [Fact]
    public void AThrowingWireIsUnreachable_NeverItsExceptionText()
    {
        var (s, _, a) = Make();
        a.Throw = new InvalidOperationException("secret body");
        Assert.Equal(ChatSendOutcome.Failed, s.SendAsync("hi").Result);
        Assert.Equal("Can't reach Paperclip.", s.History[^1].Text);
    }

    [Fact]
    public void NothingToSendAndNoActionsAreBothFailed()
    {
        var (s, _, _) = Make();
        Assert.Equal(ChatSendOutcome.Failed, s.SendAsync("   ").Result);
        Assert.Equal(ChatSendOutcome.Failed, s.SendAsync(null!).Result);
        Assert.Empty(s.History);
        _actions = null;
        Assert.Equal(ChatSendOutcome.Failed, s.SendAsync("hi").Result);
        Assert.Equal("Paperclip is not connected.", s.History[^1].Text);
    }

    [Fact]
    public void ASuccessAfterAFailureRemovesTheStaleRedLine_AndASecondFailureReusesTheLine()
    {
        var (s, f, a) = Make();
        f.Push(Snap("i1"));
        a.Reply = new(new(PaperclipActionResult.Unreachable));
        s.SendAsync("hi").Wait();
        var updated = new List<string>();
        s.TurnUpdated += t => updated.Add(t.Text);
        a.Reply = new(new(PaperclipActionResult.Refused, "No"));
        s.SendAsync("hi").Wait();
        s.SendAsync("hi").Wait();   // same words again: nothing to update
        Assert.Equal(new[] { "No" }, updated);
        Assert.Equal(2, s.History.Count);

        var replaced = 0; s.HistoryReplaced += () => replaced++;
        a.Reply = new(new(PaperclipActionResult.Done), "c3", null);
        s.SendAsync("hi").Wait();
        Assert.Equal(1, replaced);
        Assert.DoesNotContain(s.History, t => t.IsError);
    }

    [Fact]
    public void ARefusedKeyFreezesTheThreadAndRaisesReadOnlyOnce_AndRecovers()
    {
        var (s, f, _) = Make();
        var flips = 0; s.ReadOnlyChanged += () => flips++;
        var states = new List<RemoteChatState>(); s.StateChanged += states.Add;
        f.Push(Snap("i1", refused: true));
        f.Push(Snap("i1", refused: true));
        Assert.True(s.IsReadOnly);
        Assert.Equal(1, flips);
        Assert.Equal("Reconnect to Paperclip", s.ComposerHint);
        Assert.Equal(RemoteChatState.Error, s.State);

        f.Push(Snap("i1"));
        Assert.False(s.IsReadOnly);
        Assert.Equal(2, flips);
        Assert.Equal(new[] { RemoteChatState.Connected, RemoteChatState.Error, RemoteChatState.Connected }, states);
    }

    [Fact]
    public void ALinkThatNeedsReconnectIsReadOnlyBeforeAnyRead()
    {
        _link = PaperclipLink.NeedsReconnect;
        var (s, f, _) = Make();
        Assert.True(s.IsReadOnly);
        f.Push(Snap("i1"));
        Assert.Equal(RemoteChatState.Error, s.State);
        Assert.Equal("Reconnect to Paperclip", s.ComposerHint);
    }

    [Fact]
    public void TheBoxFollowsTheRoster_AndTheRosterEventRewordsItWithoutAFeedTick()
    {
        var (s, f, _) = Make();
        var changes = 0; s.ComposerHintChanged += () => changes++;
        f.Push(Snap("i1"));
        Assert.Equal("Message Nova. It wakes to read it.", s.ComposerHint);
        Assert.Equal(1, changes);
        f.Push(Snap("i1"));
        Assert.Equal(1, changes);   // same words, no event

        _roster = [Agent(state: "generating")];
        PaperclipSessions.Publish(_roster, PaperclipLink.Connected, "Connected");
        Assert.Equal("Nova is working. Your message goes to this run.", s.ComposerHint);

        _roster = [Agent(status: "paused")];
        PaperclipSessions.Publish(_roster, PaperclipLink.Connected, "Connected 2");
        Assert.Equal("Nova is paused in Paperclip. It reads this when resumed.", s.ComposerHint);

        s.PanelClosed();   // closed: roster changes no longer reach it
        _roster = [Agent()];
        PaperclipSessions.Publish(_roster, PaperclipLink.Connected, "Connected 3");
        Assert.Equal("Nova is paused in Paperclip. It reads this when resumed.", s.ComposerHint);
    }

    [Fact]
    public void WakingEndsWhenARunShows_AndSaysItHasNotWokenAfterThirtySeconds()
    {
        var (s, f, _) = Make();
        f.Push(Snap("i1"));
        s.SendAsync("go").Wait();
        Assert.Equal("Waking Nova…", s.ComposerHint);
        _now = T0.AddSeconds(29);
        f.Push(Snap("i1"));
        Assert.Equal("Waking Nova…", s.ComposerHint);
        _now = T0.AddSeconds(30);
        f.Push(Snap("i1"));
        Assert.Equal("Nova hasn't woken yet. Wake it from the orb's menu.", s.ComposerHint);

        _view = new([Live("r1", "i1")], [], []);
        f.Push(Snap("i1", act: PaperclipChat.Activity.Empty("r1")));
        Assert.Equal("Nova is working. Your message goes to this run.", s.ComposerHint);
        _view = Empty;
        f.Push(Snap("i1", act: PaperclipChat.Activity.Empty("r1")));
        Assert.Equal("Message Nova. It wakes to read it.", s.ComposerHint);
    }

    [Fact]
    public void TheTargetPicksTheLiveRunsTask_ThenTheNewestFinishedRunOnIt_AndFillsATitleFromTheRoster()
    {
        var (s, _, _) = Make();
        Assert.Null(s.Target().Task);

        _view = new([Live("rLive", "i2", sec: 5), Live("rOld", "i3", sec: 1), Live("rOther", "i4", agent: "someone-else")], [Done("rA", "i2", 10)], []);
        var t = s.Target();
        Assert.Equal("i2", t.Task!.Id);
        Assert.Equal("rLive", t.RunId);
        Assert.True(t.RunLive);

        _roster = [Agent(task: "Fix the cookie")];
        Assert.Equal("Fix the cookie", s.Target().Task!.Title);

        _view = new([], [Done("rOld", "i2", 5), Done("rNew", "i2", 20), Done("rElse", "i9", 1)], []);
        t = s.Target();
        Assert.Equal("rNew", t.RunId);
        Assert.False(t.RunLive);

        _view = new([], [], []);
        Assert.Null(s.Target().RunId);
    }

    [Fact]
    public void WhenThePollHoldsNoTaskTheAssignedIssuesAreReadOnceOnOpen_AndNotWhenItDoes()
    {
        _assignedAnswer = [new(TaskOf("i5"), T0, false)];
        var (s, f, _) = Make();
        Assert.Equal(1, _assignedReads);
        // Reads happen on a continuation; the answer is in once it has run.
        SpinWait.SpinUntil(() => f.Calls.Contains("nudge"), 2000);
        Assert.Equal("i5", s.Target().Task!.Id);

        _assignedReads = 0;
        _view = new([Live("r1", "i1")], [], []);
        var (_, f2, _) = Make();
        Assert.Equal(0, _assignedReads);
        Assert.DoesNotContain("nudge", f2.Calls);
    }

    [Fact]
    public void AFailedOrCancelledAssignedReadLeavesNoTask()
    {
        _assignedAnswer = null;
        var (s, f, _) = Make();
        SpinWait.SpinUntil(() => _assignedReads == 1, 2000);
        Thread.Sleep(50);
        Assert.DoesNotContain("nudge", f.Calls);
        Assert.Null(s.Target().Task);

        var f2 = new Feed();
        var throwing = new PaperclipChatSession("ag1", "Nova", null, () => f2, () => null,
            readAssigned: _ => throw new InvalidOperationException("boom"));
        throwing.PanelOpened();
        Assert.DoesNotContain("nudge", f2.Calls);

        var gate = new TaskCompletionSource<IReadOnlyList<PaperclipChat.AssignedIssue>?>();
        var f3 = new Feed();
        var slow = new PaperclipChatSession("ag1", "Nova", null, () => f3, () => null, readAssigned: _ => gate.Task);
        slow.PanelOpened();
        slow.PanelClosed();   // cancelled while the read is out
        gate.SetResult([new(TaskOf("i5"), T0, false)]);
        Thread.Sleep(50);
        Assert.DoesNotContain("nudge", f3.Calls);
    }

    [Fact]
    public void AtTheRowCapTheThreadIsRebuiltRatherThanMisaligned()
    {
        var (s, f, _) = Make();
        var comments = Enumerable.Range(0, 250).Select(i => C("c" + i, "m" + i, sec: i)).ToArray();
        f.Push(Snap("i1", cs: comments.Take(199).ToArray()));
        Assert.Equal(200, s.History.Count);
        var replaced = 0; s.HistoryReplaced += () => replaced++;
        f.Push(Snap("i1", cs: comments.Take(200).ToArray()));   // one more: the cap drops the oldest
        Assert.Equal(1, replaced);
        Assert.Equal(200, s.History.Count);
        Assert.Equal("m199", s.History[^1].Text);
        Assert.Equal("m1", s.History[1].Text);
    }

    // QA B1: the sent message of a no-task send is the task's description, not a comment, so the thread
    // for the new task has no row for it; the first snapshot after the send must not wipe the bubble.
    [Fact]
    public void ANoTaskSendsBubbleSurvivesTheFirstSnapshotOfTheTaskItOpened()
    {
        var (s, f, _) = Make();
        f.Push(Snap(null));
        Assert.Equal(ChatSendOutcome.Sent, s.SendAsync("please start on the login bug").Result);
        var replaced = 0; s.HistoryReplaced += () => replaced++;

        f.Push(Snap("i9"));   // the new task, no comments yet
        Assert.Equal(0, replaced);
        Assert.Contains(s.History, t => t.Role == ChatRole.User && t.Text == "please start on the login bug");
        Assert.Contains("CB-1", s.History[0].Text);   // the task row now names the task

        f.Push(Snap("i9", cs: C("a1", "On it.", mine: true, sec: 5)));
        Assert.Equal(3, s.History.Count);
        Assert.Equal("On it.", s.History[^1].Text);
    }

    // QA B2: a run that starts and ends between two ticks is never seen live; the agent's own comment
    // newer than the send is the proof it woke, so the box must not later say it has not.
    [Fact]
    public void AnAgentReplyEndsWakingEvenWhenNoLiveRunWasSeen()
    {
        var (s, f, _) = Make();
        f.Push(Snap("i1"));
        s.SendAsync("go").Wait();
        f.Push(Snap("i1", cs: C("a1", "Done.", mine: true, sec: 5)));
        _now = T0.AddSeconds(45);
        f.Push(Snap("i1", cs: C("a1", "Done.", mine: true, sec: 5)));
        Assert.Equal("Message Nova. It wakes to read it.", s.ComposerHint);
    }

    [Fact]
    public void ARunsRowNewerThanTheSendAlsoEndsWaking_AndAnOlderCommentDoesNot()
    {
        var (s, f, _) = Make();
        f.Push(Snap("i1"));
        s.SendAsync("go").Wait();
        // An agent comment from before the send proves nothing.
        f.Push(Snap("i1", cs: C("old", "earlier", mine: true, sec: -30)));
        Assert.Equal("Waking Nova…", s.ComposerHint);
        var act = PaperclipChat.Activity.Empty("r1") with { Rows = [new("r:r1:t:a", ChatRole.System, "Bash: ls", T0.AddSeconds(3))] };
        f.Push(Snap("i1", act: act, cs: C("old", "earlier", mine: true, sec: -30)));
        Assert.Equal("Message Nova. It wakes to read it.", s.ComposerHint);
    }

    // QA B3: nothing ticks at 30 s when the read is unchanged, so a one-shot rewords the box.
    [Fact]
    public void ASendSchedulesTheThirtySecondRewording()
    {
        var (s, f, _) = Make();
        f.Push(Snap("i1"));
        var changes = 0; s.ComposerHintChanged += () => changes++;
        s.SendAsync("go").Wait();
        var (after, run) = Assert.Single(_scheduled);
        Assert.True(after > PaperclipChat.WakeGrace);
        Assert.Equal("Waking Nova…", s.ComposerHint);
        var before = changes;

        _now = T0.AddSeconds(31);
        run();
        Assert.Equal("Nova hasn't woken yet. Wake it from the orb's menu.", s.ComposerHint);
        Assert.Equal(before + 1, changes);
    }

    // QA B4: the feed's thread asks for the target while the UI thread sends and applies.
    [Fact]
    public void TheTargetCanBeAskedFromAnotherThreadWhileTheUiThreadSendsAndApplies()
    {
        var (s, f, a) = Make();
        _view = new([Live("r1", "i1")], [Done("r0", "i1", 1)], []);
        var stop = false;
        var reader = Task.Run(() => { while (!stop) { var t = s.Target(); Assert.NotNull(t.Task); } });
        for (var i = 0; i < 300; i++)
        {
            f.Push(Snap("i1", cs: C("c" + i, "m" + i, sec: i)));
            s.SendAsync("x" + i).Wait();
        }
        stop = true;
        reader.Wait();
        Assert.Equal(300, a.Sent.Count);
    }

    // QA B7: the feed stops itself on a 401; once the person reconnects and the link is Connected
    // again, the panel starts it over instead of staying frozen until it is closed and reopened.
    [Fact]
    public void ReconnectingUnfreezesAPanelThatWasLeftOpen()
    {
        var (s, f, _) = Make();
        f.Push(Snap("i1", refused: true));
        Assert.True(s.IsReadOnly);
        Assert.Equal("Reconnect to Paperclip", s.ComposerHint);
        var flips = 0; s.ReadOnlyChanged += () => flips++;

        _roster = [Agent()];
        PaperclipSessions.Publish(_roster, PaperclipLink.Connected, "Connected");

        Assert.False(s.IsReadOnly);
        Assert.Equal(1, flips);
        Assert.Equal(new[] { "opened", "closed", "opened" }, f.Calls);
        Assert.Equal("Message Nova. It wakes to read it.", s.ComposerHint);

        // A roster change that is not a reconnect leaves a healthy feed alone.
        _roster = [Agent(state: "generating")];
        PaperclipSessions.Publish(_roster, PaperclipLink.Connected, "Connected 2");
        Assert.Equal(new[] { "opened", "closed", "opened" }, f.Calls);

        // And a key that is still refused while the link says NeedsReconnect stays frozen.
        f.Push(Snap("i1", refused: true));
        _link = PaperclipLink.NeedsReconnect;
        PaperclipSessions.Publish(_roster, PaperclipLink.NeedsReconnect, "Reconnect");
        Assert.True(s.IsReadOnly);
        Assert.Equal(new[] { "opened", "closed", "opened" }, f.Calls);
    }
}
