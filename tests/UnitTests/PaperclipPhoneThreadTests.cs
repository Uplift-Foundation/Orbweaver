using Xunit;

namespace Orbweaver.Tests;

// CB-447: the shared per-agent thread a phone's watch holds, and the registry that counts its holders.
[Collection("PaperclipSessions statics")]
public class PaperclipPhoneThreadTests : IDisposable
{
    private const string Agent = "3f2a9c4e-6b1d-4e87-a5c3-9d0e7b2f1a64";
    private static readonly DateTime T0 = new(2026, 11, 24, 4, 0, 0, DateTimeKind.Utc);

    public void Dispose() => PaperclipSessions.Publish([], PaperclipLink.Off, "Paperclip is switched off.");

    private sealed class Feed : IPaperclipChatFeed
    {
        public event Action<ChatSnapshot>? Updated;
        public int Opens, Closes, Nudges;
        public bool Subscribed => Updated is not null;
        public void Opened() => Opens++;
        public void Closed() => Closes++;
        public void Nudge() => Nudges++;
        public void Push(ChatSnapshot s) => Updated?.Invoke(s);
    }

    private sealed class Actions : IPaperclipActions
    {
        public PaperclipSent Reply = new(new(PaperclipActionResult.Done), null, null);
        public readonly List<string> Sent = new();
        public Task<PaperclipSent> CommentAsync(string issueId, string text, CancellationToken ct) { Sent.Add("comment:" + issueId); return Task.FromResult(Reply); }
        public Task<PaperclipSent> OpenTaskAsync(string agentId, string text, CancellationToken ct) { Sent.Add("open:" + agentId); return Task.FromResult(Reply); }
        public Task<PaperclipOutcome> DecideAsync(PaperclipNeed need, bool approve, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> WakeAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> PauseAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> ResumeAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
    }

    private readonly Feed _feed = new();
    private PaperclipLink _link = PaperclipLink.Connected;
    private PaperclipSessions.ChatView _view = new(Array.Empty<PaperclipApi.RawLiveRun>(), Array.Empty<PaperclipApi.RawRun>(), Array.Empty<PaperclipApi.RawIssue>());
    private IReadOnlyList<PaperclipChat.AssignedIssue>? _assigned;
    private int _assignedReads;
    private bool _assignedThrows;
    private PaperclipAgent[] _roster = [];
    private readonly Actions _actions = new();

    // A gate the assigned read waits at, when a test wants it to land late.
    private TaskCompletionSource? _assignedGate;

    private PaperclipPhoneThreads Host(TimeSpan? assignedWait = null) => new(() => true, _ => _feed, () => _actions, () => _roster, () => _view, () => _link,
        async (_, ct) =>
        {
            _assignedReads++;
            await Task.Yield();
            if (_assignedGate is { } gate) await gate.Task.WaitAsync(ct);
            if (_assignedThrows) throw new InvalidOperationException("boom");
            return _assigned;
        },
        () => T0, assignedWait);

    private static ChatSnapshot Snap(string? task, bool refused = false, params PaperclipChat.Comment[] cs) =>
        new(task is null ? null : new PaperclipChat.TaskRef(task, "CB-1", "t", "todo"), cs, null, refused);

    private static PaperclipChat.Comment C(string id, string body, string? agent = null) =>
        new(id, body, agent, agent is null ? "u1" : null, null, T0.AddSeconds(1));

    [Fact]
    public void A_thread_is_made_on_the_first_acquire_shared_by_the_next_and_forgotten_after_the_last_release()
    {
        var host = Host();
        Assert.True(host.Available);
        var a = host.Acquire("paperclip:" + Agent, "Atlas")!;
        var b = host.Acquire("paperclip:" + Agent, "Atlas")!;
        Assert.Same(a, b);
        Assert.Equal((2, 1, 1), (a.Holders, _feed.Opens, host.Count));
        Assert.Equal(Agent, a.AgentId);

        a.Release();
        Assert.Equal((1, 0), (a.Holders, _feed.Closes));
        b.Release();
        b.Release();                                          // a stray extra release is a no-op
        Assert.Equal((0, 1, 0), (a.Holders, _feed.Closes, host.Count));
        Assert.False(_feed.Subscribed);
        Assert.NotSame(a, host.Acquire("paperclip:" + Agent, "Atlas"));
    }

    [Fact]
    public void An_id_without_the_prefix_works_and_a_host_that_is_not_available_acquires_nothing()
    {
        Assert.Equal(Agent, Host().Acquire(Agent, "Atlas")!.AgentId);
        var off = new PaperclipPhoneThreads(() => false, _ => _feed, () => _actions, () => [], () => _view, () => _link,
            (_, _) => Task.FromResult<IReadOnlyList<PaperclipChat.AssignedIssue>?>(null), () => T0);
        Assert.False(off.Available);
        Assert.Null(off.Acquire("paperclip:" + Agent, "Atlas"));
    }

    [Fact]
    public void A_thread_that_was_released_and_reacquired_in_between_is_not_dropped_by_the_late_forget()
    {
        var host = Host();
        var a = host.Acquire(Agent, "Atlas")!;
        var again = host.Acquire(Agent, "Atlas")!;
        a.Release();
        Assert.Equal(1, host.Count);
        Assert.Same(a, host.Acquire(Agent, "Atlas"));
    }

    [Fact]
    public void The_rows_are_the_panels_rows_and_a_new_snapshot_moves_the_version()
    {
        var t = Host().Acquire(Agent, "Atlas")!;
        var v0 = t.Current().Version;
        _feed.Push(Snap("i1", cs: [C("1", "hi"), C("2", "yo", Agent)]));
        var now = t.Current();
        Assert.True(now.Version > v0);
        Assert.Equal("i1", now.TaskId);
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant], now.Rows.Select(r => r.Role));
        Assert.Equal(now.Rows.Count, t.Current().Rows.Count);
        Assert.Equal(Agent, t.Target().AgentId);
    }

    [Fact]
    public async Task A_task_that_changes_replaces_the_rows_and_one_that_ends_clears_the_task()
    {
        var t = Host().Acquire(Agent, "Atlas")!;
        _feed.Push(Snap("i1", cs: C("1", "a")));
        _feed.Push(Snap("i2", cs: C("9", "b")));
        Assert.Equal(["b"], t.Current().Rows.Where(r => r.Role == ChatRole.User).Select(r => r.Text));
        _feed.Push(Snap(null));
        Assert.Null(t.Current().TaskId);
        Assert.Equal("No task yet.", t.Current().Rows[0].Text);

        // A message sent to an agent with no task, then the task it opened: the line stays (the task's
        // description is not a comment).
        Assert.Equal(PaperclipSendResult.Sent, (await t.SendAsync("start here")).Result);
        _feed.Push(Snap("i9"));
        Assert.Contains(t.Current().Rows, r => r.Role == ChatRole.User && r.Text == "start here");
    }

    [Fact]
    public async Task A_refused_key_freezes_the_thread_and_a_reconnected_link_starts_the_feed_over()
    {
        var t = Host().Acquire(Agent, "Atlas")!;
        _feed.Push(Snap("i1", refused: true));
        _link = PaperclipLink.NeedsReconnect;
        PaperclipSessions.Publish([], PaperclipLink.NeedsReconnect, "Reconnect");           // still refused: not restarted
        Assert.Equal((1, 0), (_feed.Opens, _feed.Closes));

        _link = PaperclipLink.Connected;
        PaperclipSessions.Publish([], PaperclipLink.Connected, "Connected");
        Assert.Equal((2, 1), (_feed.Opens, _feed.Closes));
        PaperclipSessions.Publish([], PaperclipLink.Connected, "Connected 2");              // healthy: left alone
        Assert.Equal((2, 1), (_feed.Opens, _feed.Closes));

        // Nobody holding: a roster change restarts nothing.
        _feed.Push(Snap("i1", refused: true));
        t.Release();
        PaperclipSessions.Publish([], PaperclipLink.Connected, "Connected 3");
        Assert.Equal(2, _feed.Opens);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_assigned_issues_are_read_once_when_the_poll_holds_no_task_and_adopted()
    {
        _assigned = [new(new PaperclipChat.TaskRef("i5", "CB-5", "t", "todo"), T0, false)];
        var t = Host().Acquire(Agent, "Atlas")!;
        SpinWait.SpinUntil(() => _feed.Nudges > 0, 2000);
        Assert.Equal((1, "i5"), (_assignedReads, t.Target().Task!.Id));
        await Task.CompletedTask;

        // The poll already holds a task: the read is not made.
        _assignedReads = 0;
        _view = new([new PaperclipApi.RawLiveRun("r1", Agent, "running", T0, "i1", null, null)], [], []);
        var second = Host().Acquire(Agent, "Atlas")!;
        await Task.Delay(50);
        Assert.Equal(0, _assignedReads);
        second.Release();
    }

    [Fact]
    public void A_failed_cancelled_or_empty_assigned_read_leaves_no_task_and_says_nothing()
    {
        _assignedThrows = true;
        var t = Host().Acquire(Agent, "Atlas")!;
        SpinWait.SpinUntil(() => _assignedReads == 1, 2000);
        Thread.Sleep(50);
        Assert.Equal(0, _feed.Nudges);
        Assert.Null(t.Target().Task);
        t.Release();

        _assignedThrows = false;
        _assigned = null;
        var again = Host().Acquire(Agent, "Atlas")!;
        SpinWait.SpinUntil(() => _assignedReads == 2, 2000);
        Thread.Sleep(50);
        Assert.Equal(0, _feed.Nudges);
        again.Release();
    }

    [Fact]
    public async Task A_read_cancelled_by_the_last_release_ends_quietly()
    {
        var started = new TaskCompletionSource();
        var host = new PaperclipPhoneThreads(() => true, _ => _feed, () => _actions, () => [], () => _view, () => _link,
            async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return null; }, () => T0);
        var t = host.Acquire(Agent, "Atlas")!;
        await started.Task;
        t.Release();
        await Task.Delay(50);
        Assert.Equal(0, _feed.Nudges);
    }

    [Fact]
    public async Task A_send_with_an_unreadable_comment_id_is_adopted_by_the_comment_that_arrives_and_a_gone_task_nudges_the_feed()
    {
        var t = Host().Acquire(Agent, "Atlas")!;
        _feed.Push(Snap("i1"));
        Assert.Equal(PaperclipSendResult.Sent, (await t.SendAsync("hello")).Result);
        var before = t.Current().Rows.Count;
        _feed.Push(Snap("i1", cs: C("c5", "hello")));
        Assert.Equal(before, t.Current().Rows.Count);

        _actions.Reply = new(new(PaperclipActionResult.Refused, PaperclipActions.TaskGoneWhy));
        var nudges = _feed.Nudges;
        var (result, why) = await t.SendAsync("again");
        Assert.Equal((PaperclipSendResult.Failed, "That task is no longer in Paperclip."), (result, why));
        Assert.Equal(nudges + 1, _feed.Nudges);
    }

    [Fact]
    public async Task The_assigned_read_the_manager_uses_is_null_while_paperclip_is_off_and_when_the_read_fails()
    {
        var before = PaperclipSessions.ConfigSource;
        try
        {
            PaperclipSessions.ConfigSource = () => PaperclipSessions.PaperclipConfig.Off;
            Assert.Null(await PaperclipTaskPicker.ReadAssignedLiveAsync(Agent, default));
            // Connected to nothing: the read cannot succeed, which is null, not an empty list.
            PaperclipSessions.ConfigSource = () => new(true, "http://127.0.0.1:1", "co1", "k", null, "fold");
            Assert.Null(await PaperclipTaskPicker.ReadAssignedLiveAsync(Agent, default));
        }
        finally { PaperclipSessions.ConfigSource = before; }
    }

    // QA batch 1, item 1: an agent whose only task is one the poll holds no run for (a done one) has no task
    // until the once-per-hold assigned read lands. A message sent before that must not open a second task.
    [Fact]
    public async Task A_message_sent_before_the_assigned_read_lands_is_a_comment_on_that_task_not_a_new_task()
    {
        _assigned = [new(new PaperclipChat.TaskRef("i7", "CB-7", "Done earlier", "done"), T0, false)];
        _assignedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = Host().Acquire(Agent, "Atlas")!;

        var send = t.SendAsync("one more thing");
        await Task.Delay(50);
        Assert.False(send.IsCompleted);                    // waiting on the read, not deciding on no task
        Assert.Empty(_actions.Sent);

        _assignedGate.SetResult();
        Assert.Equal(PaperclipSendResult.Sent, (await send).Result);
        Assert.Equal(["comment:i7"], _actions.Sent);       // a comment on the task the read found, and nothing opened
    }

    [Fact]
    public async Task A_message_sent_before_the_assigned_read_lands_opens_a_task_only_when_the_read_found_none_or_outlasted_its_bound()
    {
        // The read lands with no assigned issue: the agent really has no task, so the message opens one.
        _assigned = [];
        _assignedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var none = Host().Acquire(Agent, "Atlas")!;
        var first = none.SendAsync("hello");
        await Task.Delay(30);
        _assignedGate.SetResult();
        Assert.Equal(PaperclipSendResult.Sent, (await first).Result);
        Assert.Equal([$"open:{Agent}"], _actions.Sent);

        // The read never lands: the wait is a bound, not a hang, and past it the message opens a task.
        _actions.Sent.Clear();
        _assignedGate = new TaskCompletionSource();
        var slow = Host(TimeSpan.FromMilliseconds(60)).Acquire(Agent + "-b", "Atlas")!;
        var started = DateTime.UtcNow;
        Assert.Equal(PaperclipSendResult.Sent, (await slow.SendAsync("hello")).Result);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
        Assert.Equal([$"open:{Agent}-b"], _actions.Sent);
        slow.Release();                                    // cancels the gated read
    }

    [Fact]
    public async Task A_message_to_an_agent_the_poll_already_knows_the_task_of_does_not_wait_for_the_read()
    {
        _view = new([new PaperclipApi.RawLiveRun("r1", Agent, "running", T0, "i1", null, null)], [], []);
        _assignedGate = new TaskCompletionSource();
        var t = Host().Acquire(Agent, "Atlas")!;
        Assert.Equal(PaperclipSendResult.Sent, (await t.SendAsync("go")).Result);
        Assert.Equal(["comment:i1"], _actions.Sent);
        t.Release();
    }
}

