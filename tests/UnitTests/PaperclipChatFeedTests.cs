using Orbweaver;
using Xunit;

namespace Orbweaver.Tests;

// CB-443: the feed's decisions, one tick at a time against FakePaperclip (no timer, no listener).
// The loop, the cadence and the open/closed silence are the integration suite's.
public class PaperclipChatFeedTests
{
    private static readonly PaperclipChat.TaskRef T1 = new("is-1", "CB-1", "One", "todo");
    private static readonly PaperclipChat.TaskRef T2 = new("is-2", "CB-2", "Two", "todo");
    private const string Cm = "[{\"id\":\"c1\",\"body\":\"hi\",\"authorUserId\":\"u\",\"createdAt\":\"2026-11-24T03:00:00Z\"}]";
    private const string Ev1 = "[{\"seq\":3,\"eventType\":\"run.result.proposed\",\"createdAt\":\"2026-11-24T03:00:00Z\",\"payload\":{\"prpEvent\":{\"itemId\":\"i\",\"payload\":{\"summary\":\"done\"}}}}]";

    private static (PaperclipChatFeed Feed, FakePaperclip Api, List<ChatSnapshot> Got, Action<ChatTarget> Set) Rig(ChatTarget first, TimeSpan? commentsEvery = null)
    {
        var api = new FakePaperclip(); var got = new List<ChatSnapshot>();
        var target = first;
        var feed = new PaperclipChatFeed(api, "co", () => target, commentsEvery: commentsEvery);
        feed.Updated += got.Add;
        return (feed, api, got, t => target = t);
    }

    [Fact]
    public async Task With_no_task_nothing_is_read_and_one_snapshot_says_so()
    {
        var (feed, api, got, _) = Rig(new ChatTarget("a", null, null, false));
        await feed.TickAsync(true, default);
        Assert.Empty(api.Calls);
        Assert.Null(Assert.Single(got).Task);
        await feed.TickAsync(true, default);
        Assert.Single(got);                                    // nothing changed, nothing raised
    }

    [Fact]
    public async Task A_task_reads_its_comments_and_raises_only_when_they_change()
    {
        var (feed, api, got, _) = Rig(new ChatTarget("a", T1, null, false));
        api.Gets["/api/issues/is-1/comments"] = FakePaperclip.Ok(Cm);
        await feed.TickAsync(true, default);
        Assert.Equal("c1", Assert.Single(Assert.Single(got).Comments).Id);
        await feed.TickAsync(true, default);
        Assert.Single(got);                                    // same comments again: no raise
        await feed.TickAsync(false, default);
        Assert.Equal(2, api.Calls.Count);                      // a tick with comments=false made no comments read
    }

    [Fact]
    public async Task A_failed_comments_read_keeps_what_was_shown_and_a_403_sets_reconnect()
    {
        var (feed, api, got, _) = Rig(new ChatTarget("a", T1, null, false));
        api.Gets["/api/issues/is-1/comments"] = FakePaperclip.Ok(Cm);
        await feed.TickAsync(true, default);
        api.Gets["/api/issues/is-1/comments"] = FakePaperclip.Status(PaperclipHttpKind.ServerError, 500);
        await feed.TickAsync(true, default);
        Assert.Single(got);
        api.Gets["/api/issues/is-1/comments"] = FakePaperclip.Status(PaperclipHttpKind.Other, 403);
        await feed.TickAsync(true, default);
        Assert.True(got[^1].NeedsReconnect);
        Assert.Single(got[^1].Comments);                       // frozen, not cleared
        var n = got.Count;
        await feed.TickAsync(true, default);
        Assert.Equal(n, got.Count);                            // already known: no second raise
    }

    [Fact]
    public async Task A_live_run_is_read_every_tick_one_more_time_when_it_ends_then_kept()
    {
        var (feed, api, got, set) = Rig(new ChatTarget("a", T1, "run-1", true));
        api.Gets["/api/issues/is-1/comments"] = FakePaperclip.Ok("[]");
        api.Gets["/api/heartbeat-runs/run-1/events?afterSeq=0&limit=500"] = FakePaperclip.Ok(Ev1);
        api.Gets["/api/heartbeat-runs/run-1/events?afterSeq=3&limit=500"] = FakePaperclip.Ok("[]");
        await feed.TickAsync(false, default);
        Assert.Equal("done", Assert.Single(got[^1].Activity!.Rows).Text);
        await feed.TickAsync(false, default);                  // live: read again, empty
        set(new ChatTarget("a", T1, "run-1", false));
        await feed.TickAsync(false, default);                  // just ended: the tail read
        var reads = api.Calls.Count(c => c.Contains("/events"));
        Assert.Equal(3, reads);
        await feed.TickAsync(false, default);                  // finished and read: kept, not re-read
        Assert.Equal(reads, api.Calls.Count(c => c.Contains("/events")));
    }

    [Fact]
    public async Task A_run_already_finished_when_the_panel_opens_is_read_once()
    {
        var (feed, api, got, _) = Rig(new ChatTarget("a", T1, "run-9", false));
        api.Gets["/api/issues/is-1/comments"] = FakePaperclip.Ok("[]");
        api.Gets["/api/heartbeat-runs/run-9/events?afterSeq=0&limit=500"] = FakePaperclip.Ok(Ev1);
        await feed.TickAsync(true, default);
        await feed.TickAsync(true, default);
        Assert.Equal(1, api.Calls.Count(c => c.Contains("/events")));
        Assert.Equal("run-9", got[^1].Activity!.RunId);
    }

    [Fact]
    public async Task A_failed_events_read_is_retried_and_a_401_sets_reconnect()
    {
        var (feed, api, got, _) = Rig(new ChatTarget("a", T1, "run-1", true));
        api.Gets["/api/issues/is-1/comments"] = FakePaperclip.Ok("[]");
        api.Gets["/api/heartbeat-runs/run-1/events?afterSeq=0&limit=500"] = FakePaperclip.Status(PaperclipHttpKind.Timeout);
        await feed.TickAsync(true, default);
        Assert.Empty(got.Where(g => g.Activity?.Rows.Count > 0));
        api.Gets["/api/heartbeat-runs/run-1/events?afterSeq=0&limit=500"] = FakePaperclip.Status(PaperclipHttpKind.Unauthorized, 401);
        await feed.TickAsync(false, default);
        Assert.True(got[^1].NeedsReconnect);
        var before = api.Calls.Count;
        await feed.TickAsync(false, default);                  // refused: no events read at all any more
        Assert.Equal(before, api.Calls.Count);
    }

    [Fact]
    public async Task A_change_of_task_starts_over_and_a_run_that_goes_away_clears_the_activity()
    {
        var (feed, api, got, set) = Rig(new ChatTarget("a", T1, "run-1", false));
        api.Gets["/api/issues/is-1/comments"] = FakePaperclip.Ok(Cm);
        api.Gets["/api/issues/is-2/comments"] = FakePaperclip.Ok("[]");
        api.Gets["/api/heartbeat-runs/run-1/events?afterSeq=0&limit=500"] = FakePaperclip.Ok(Ev1);
        await feed.TickAsync(false, default);                  // first tick of a new task reads comments although comments=false
        Assert.Single(got[^1].Comments);
        Assert.NotNull(got[^1].Activity);

        set(new ChatTarget("a", T2, "run-1", false));          // another task, same run id shown by mistake: still starts over
        await feed.TickAsync(false, default);
        Assert.Equal("is-2", got[^1].Task!.Id);
        Assert.Empty(got[^1].Comments);

        set(new ChatTarget("a", T2, null, false));
        await feed.TickAsync(false, default);
        Assert.Null(got[^1].Activity);
    }

    [Fact]
    public void Open_and_close_are_idempotent_and_a_nudge_and_dispose_are_safe()
    {
        var feed = new PaperclipChatFeed(new FakePaperclip(), "co", () => new ChatTarget("a", null, null, false),
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10));
        Assert.False(feed.IsOpen);
        feed.Closed();                                          // closing a closed feed is a no-op
        feed.Opened(); feed.Opened();
        Assert.True(feed.IsOpen);
        feed.Nudge();
        feed.Dispose();
        Assert.False(feed.IsOpen);
    }

    // An API whose comments read waits on a gate, so a tick can be caught in flight.
    private sealed class GatedApi : IPaperclipApi
    {
        internal readonly TaskCompletionSource Release = new();
        internal readonly TaskCompletionSource Entered = new();
        public async Task<PaperclipResponse> GetAsync(string path, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task;
            return FakePaperclip.Ok(Cm);
        }
        public Task<PaperclipResponse> PostAsync(string path, string jsonBody, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_tick_in_flight_when_the_feed_closes_writes_and_raises_nothing()
    {
        var api = new GatedApi(); var got = new List<ChatSnapshot>();
        var feed = new PaperclipChatFeed(api, "co", () => new ChatTarget("a", T1, null, false));
        feed.Updated += got.Add;
        using var cts = new CancellationTokenSource();
        var tick = feed.TickAsync(true, cts.Token);
        await api.Entered.Task;                                // the read is in flight
        cts.Cancel();                                          // the panel closed (Closed() cancels this token)
        api.Release.SetResult();
        await tick;
        Assert.Empty(got);                                     // nothing raised after close

        // and nothing was written: a fresh tick on a new token still sees a task change, so it raises
        var fresh = new FakePaperclip();
        fresh.Gets["/api/issues/is-1/comments"] = FakePaperclip.Ok(Cm);
        var feed2 = new PaperclipChatFeed(fresh, "co", () => new ChatTarget("a", T1, null, false));
        var got2 = new List<ChatSnapshot>(); feed2.Updated += got2.Add;
        await feed2.TickAsync(true, default);
        Assert.Single(got2);
    }

    [Fact]
    public async Task A_cancelled_tick_leaves_the_state_for_the_next_one_to_start_from()
    {
        var api = new GatedApi(); var got = new List<ChatSnapshot>();
        var feed = new PaperclipChatFeed(api, "co", () => new ChatTarget("a", T1, null, false));
        feed.Updated += got.Add;
        using var cts = new CancellationTokenSource();
        var tick = feed.TickAsync(true, cts.Token);
        await api.Entered.Task; cts.Cancel(); api.Release.SetResult(); await tick;
        // the reopened feed's first tick on a live token publishes as a first tick (state was not poisoned)
        await feed.TickAsync(true, default);
        Assert.Single(got);
        Assert.Single(got[0].Comments);
    }

    [Fact]
    public async Task A_404_on_the_comments_of_a_deleted_task_keeps_the_thread_and_is_tried_again_next_tick()
    {
        var (feed, api, got, _) = Rig(new ChatTarget("a", T1, null, false));
        api.Gets["/api/issues/is-1/comments"] = FakePaperclip.Ok(Cm);
        await feed.TickAsync(true, default);
        api.Gets.Remove("/api/issues/is-1/comments");           // the fake now answers 404
        await feed.TickAsync(true, default);
        Assert.Single(got);                                    // pinned: silent, nothing raised, nothing cleared
        api.Gets["/api/issues/is-1/comments"] = FakePaperclip.Ok("[{\"id\":\"c1\",\"body\":\"hi\",\"authorUserId\":\"u\"},{\"id\":\"c2\",\"body\":\"more\",\"authorUserId\":\"u\"}]");
        await feed.TickAsync(true, default);
        Assert.Equal(2, got[^1].Comments.Count);               // the next tick read again and recovered
        Assert.Equal(3, api.Calls.Count(c => c.EndsWith("/comments")));
    }

    // CB-453: the task a finished run names is an id to the poll (it maps blocked and in-progress issues alone), so the
    // task line read exactly "Task". The feed reads the issue itself once and keeps it.
    private static readonly PaperclipChat.TaskRef IdOnly = new("is-9", "", "", "");
    private const string Issue9 = "{\"id\":\"is-9\",\"identifier\":\"ORB-121\",\"title\":\"CB-443 live proof fixture\",\"status\":\"done\"}";

    [Fact]
    public async Task A_finished_task_the_poll_holds_no_title_for_is_read_once_and_its_line_names_it()
    {
        var (feed, api, got, _) = Rig(new ChatTarget("a", IdOnly, null, false));
        api.Gets["/api/issues/is-9/comments"] = FakePaperclip.Ok("[]");
        api.Gets["/api/issues/is-9"] = FakePaperclip.Ok(Issue9);
        await feed.TickAsync(true, default);

        var task = Assert.Single(got).Task!;
        Assert.Equal(("is-9", "ORB-121", "CB-443 live proof fixture", "done"), (task.Id, task.Identifier, task.Title, task.Status));
        Assert.Equal("ORB-121 CB-443 live proof fixture · done", PaperclipChat.TaskLine(task));
        Assert.NotEqual("Task", PaperclipChat.TaskLine(task));
        Assert.Equal("ORB-121 CB-443 live proof fixture · done", PaperclipChat.Build(task, "a", new Dictionary<string, string>(), [], null)[0].Text);

        // Kept: a later tick makes no second read of the issue and raises nothing.
        await feed.TickAsync(true, default);
        Assert.Single(got);
        Assert.Equal(1, api.Calls.Count(c => c.EndsWith("/api/issues/is-9", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_task_whose_title_the_poll_already_holds_is_not_read_again()
    {
        var (feed, api, got, _) = Rig(new ChatTarget("a", T1, null, false));
        api.Gets["/api/issues/is-1/comments"] = FakePaperclip.Ok("[]");
        await feed.TickAsync(true, default);
        Assert.Equal("CB-1", Assert.Single(got).Task!.Identifier);
        Assert.DoesNotContain(api.Calls, c => c.EndsWith("/api/issues/is-1", StringComparison.Ordinal));
    }

    private static int IssueReads(FakePaperclip api, string id) =>
        api.Calls.Count(c => c.EndsWith("/api/issues/" + id, StringComparison.Ordinal));

    // A failed, unreadable or other-issue answer is tried again, at the comments cadence (zero here, so each tick).
    [Fact]
    public async Task A_failed_or_unreadable_issue_read_leaves_the_id_only_task_and_is_tried_again_at_the_comments_cadence()
    {
        var (feed, api, got, _) = Rig(new ChatTarget("a", IdOnly, null, false), TimeSpan.Zero);
        api.Gets["/api/issues/is-9/comments"] = FakePaperclip.Ok("[]");
        api.Gets["/api/issues/is-9"] = FakePaperclip.Status(PaperclipHttpKind.ServerError, 500);
        await feed.TickAsync(true, default);
        Assert.Equal("", Assert.Single(got).Task!.Title);

        api.Gets["/api/issues/is-9"] = FakePaperclip.Ok("not json");                                          // unreadable
        await feed.TickAsync(true, default);
        api.Gets["/api/issues/is-9"] = FakePaperclip.Ok("{\"id\":\"someone-else\",\"title\":\"x\"}");   // another issue's body
        await feed.TickAsync(true, default);
        Assert.Single(got);
        Assert.Equal(3, IssueReads(api, "is-9"));

        api.Gets["/api/issues/is-9"] = FakePaperclip.Ok(Issue9);                                             // Paperclip answers
        await feed.TickAsync(true, default);
        Assert.Equal("ORB-121", got[^1].Task!.Identifier);
    }

    // QA CB-453 finding 2: a read that keeps failing is not made every tick.
    [Fact]
    public async Task A_failing_issue_read_is_not_repeated_inside_the_comments_cadence_and_a_404_or_an_empty_answer_is_read_once()
    {
        var (feed, api, _, _) = Rig(new ChatTarget("a", IdOnly, null, false));                               // the default 4 s cadence
        api.Gets["/api/issues/is-9/comments"] = FakePaperclip.Ok("[]");
        api.Gets["/api/issues/is-9"] = FakePaperclip.Status(PaperclipHttpKind.ServerError, 500);
        for (var i = 0; i < 4; i++) await feed.TickAsync(true, default);
        Assert.Equal(1, IssueReads(api, "is-9"));

        var (gone, api2, _, _) = Rig(new ChatTarget("a", IdOnly, null, false), TimeSpan.Zero);
        api2.Gets["/api/issues/is-9/comments"] = FakePaperclip.Ok("[]");
        api2.Gets["/api/issues/is-9"] = FakePaperclip.Status(PaperclipHttpKind.Other, 404);                  // the task was deleted
        for (var i = 0; i < 4; i++) await gone.TickAsync(true, default);
        Assert.Equal(1, IssueReads(api2, "is-9"));

        var (empty, api3, _, _) = Rig(new ChatTarget("a", IdOnly, null, false), TimeSpan.Zero);
        api3.Gets["/api/issues/is-9/comments"] = FakePaperclip.Ok("[]");
        api3.Gets["/api/issues/is-9"] = FakePaperclip.Ok("{\"id\":\"is-9\"}");                            // answered, with nothing to show
        for (var i = 0; i < 4; i++) await empty.TickAsync(true, default);
        Assert.Equal(1, IssueReads(api3, "is-9"));
    }

    // QA CB-453 finding 1: the status moves with the run (a done task a message reopens, one that finishes), so the
    // fields are read again when the target's run or its liveness changes, and a failure then keeps the last good line.
    [Fact]
    public async Task The_status_in_the_line_follows_the_task_as_its_run_changes_and_a_failed_refresh_keeps_the_last_good_one()
    {
        var (feed, api, got, set) = Rig(new ChatTarget("a", IdOnly, null, false), TimeSpan.Zero);
        api.Gets["/api/issues/is-9/comments"] = FakePaperclip.Ok("[]");
        api.Gets["/api/heartbeat-runs/run-1/events?afterSeq=0&limit=500"] = FakePaperclip.Ok("[]");
        api.Gets["/api/issues/is-9"] = FakePaperclip.Ok(Issue9.Replace("done", "in_progress"));
        await feed.TickAsync(true, default);
        Assert.Equal("in_progress", got[^1].Task!.Status);

        await feed.TickAsync(true, default);                                                                // nothing moved: no new read
        Assert.Equal(1, IssueReads(api, "is-9"));

        set(new ChatTarget("a", IdOnly, "run-1", true));                                                    // a run starts
        api.Gets["/api/issues/is-9"] = FakePaperclip.Ok(Issue9.Replace("done", "in_review"));
        await feed.TickAsync(true, default);
        Assert.Equal("in_review", got[^1].Task!.Status);

        set(new ChatTarget("a", IdOnly, "run-1", false));                                                   // it ends, and the task is done
        api.Gets["/api/issues/is-9"] = FakePaperclip.Ok(Issue9);
        await feed.TickAsync(true, default);
        Assert.Equal("ORB-121 CB-443 live proof fixture · done", PaperclipChat.TaskLine(got[^1].Task!));

        set(new ChatTarget("a", IdOnly, "run-2", true));                                                    // a message reopens it; the read fails
        api.Gets["/api/issues/is-9"] = FakePaperclip.Status(PaperclipHttpKind.ServerError, 500);
        await feed.TickAsync(true, default);
        Assert.Equal("done", got[^1].Task!.Status);                                                          // the last good one stands
    }

    // QA CB-453 finding 3: a read in flight when the panel closed keeps nothing.
    [Fact]
    public async Task An_issue_read_that_finished_after_the_panel_closed_is_not_kept()
    {
        var (feed, api, _, _) = Rig(new ChatTarget("a", IdOnly, null, false));
        api.Gets["/api/issues/is-9/comments"] = FakePaperclip.Ok("[]");
        api.Gets["/api/issues/is-9"] = FakePaperclip.Ok(Issue9);
        using var closed = new CancellationTokenSource();
        closed.Cancel();
        await feed.TickAsync(true, closed.Token);                                                           // a tick that outlived its panel
        await feed.TickAsync(true, default);
        Assert.Equal(2, IssueReads(api, "is-9"));                                                           // so the next open reads it afresh
    }

    [Fact]
    public async Task A_refused_key_on_the_issue_read_is_reconnect()
    {
        var (feed, api, got, _) = Rig(new ChatTarget("a", IdOnly, null, false));
        api.Gets["/api/issues/is-9/comments"] = FakePaperclip.Ok("[]");
        api.Gets["/api/issues/is-9"] = FakePaperclip.Status(PaperclipHttpKind.Other, 401);
        await feed.TickAsync(true, default);
        Assert.True(Assert.Single(got).NeedsReconnect);
    }
}

