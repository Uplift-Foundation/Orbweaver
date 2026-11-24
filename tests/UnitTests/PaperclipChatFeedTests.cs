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

    private static (PaperclipChatFeed Feed, FakePaperclip Api, List<ChatSnapshot> Got, Action<ChatTarget> Set) Rig(ChatTarget first)
    {
        var api = new FakePaperclip(); var got = new List<ChatSnapshot>();
        var target = first;
        var feed = new PaperclipChatFeed(api, "co", () => target);
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
}
