using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-447 (frames-v18): a phone's watch and input on a Paperclip row, served by the real PaperclipPhoneThreads
// over a fake feed and fake actions. No Paperclip, no network, no UI thread.
[Collection("Settings")]
public class RelayRemotePaperclipTests : IDisposable
{
    private const string Agent = "paperclip:3f2a9c4e-6b1d-4e87-a5c3-9d0e7b2f1a64";
    private static readonly DateTime T0 = new(2026, 11, 24, 4, 0, 0, DateTimeKind.Utc);

    public void Dispose() => PaperclipSessions.Publish([], PaperclipLink.Off, "Paperclip is switched off.");

    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;

    private sealed class Feed : IPaperclipChatFeed
    {
        public event Action<ChatSnapshot>? Updated;
        public int Opens, Closes, Nudges;
        public Func<ChatTarget>? Target;
        public void Opened() => Opens++;
        public void Closed() => Closes++;
        public void Nudge() => Nudges++;
        public void Push(ChatSnapshot s) => Updated?.Invoke(s);
    }

    private sealed class Actions : IPaperclipActions
    {
        public PaperclipSent Reply = new(new(PaperclipActionResult.Done), "c1", "i9");
        public Exception? Throw;
        public readonly List<string> Sent = new();
        public Task<PaperclipSent> CommentAsync(string issueId, string text, CancellationToken ct)
        { if (Throw != null) throw Throw; Sent.Add("comment:" + issueId + ":" + text); return Task.FromResult(Reply); }
        public Task<PaperclipSent> OpenTaskAsync(string agentId, string text, CancellationToken ct)
        { if (Throw != null) throw Throw; Sent.Add("open:" + agentId + ":" + text); return Task.FromResult(Reply); }
        public Task<PaperclipOutcome> DecideAsync(PaperclipNeed need, bool approve, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> WakeAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> PauseAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipOutcome> ResumeAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Sent : IRemoteSender
    {
        public readonly List<(string Phone, string? Session, JsonElement Body)> Replies = [];
        public readonly List<(string Phone, string Session, JsonElement Body)> Windows = [];
        public void Reply(string phoneId, string? sessionId, string plaintext) { lock (this) Replies.Add((phoneId, sessionId, J(plaintext))); }
        public void Window(string phoneId, string sessionId, string plaintext) { lock (this) Windows.Add((phoneId, sessionId, J(plaintext))); }
        public void Run(string phoneId, string sessionId, string plaintext) { }
        public bool ImageWindow(string phoneId, string hash, string plaintext) => false;
    }

    private sealed class World
    {
        public readonly Feed Feed = new();
        public readonly Actions Actions = new();
        public readonly Sent Sent = new();
        public readonly List<Feed> Feeds = new();
        public bool Available = true;
        public IPaperclipActions? ActionsSeam;
        public PaperclipSessions.ChatView View = new(Array.Empty<PaperclipApi.RawLiveRun>(), Array.Empty<PaperclipApi.RawRun>(), Array.Empty<PaperclipApi.RawIssue>());
        public readonly PaperclipPhoneThreads Host;
        public readonly RelayRemote Remote;
        public string Title = "Atlas";

        public World(bool withHost = true)
        {
            ActionsSeam = Actions;
            Host = new PaperclipPhoneThreads(() => Available, target => { var f = new Feed { Target = target }; Feeds.Add(f); return f; },
                () => ActionsSeam, () => [], () => View, () => PaperclipLink.Connected, (_, _) => Task.FromResult<IReadOnlyList<PaperclipChat.AssignedIssue>?>(Array.Empty<PaperclipChat.AssignedIssue>()),
                () => T0);
            Remote = new RelayRemote(new RelayRemote.Seams(
                () => DateTimeOffset.FromUnixTimeSeconds(1794111792), _ => Task.CompletedTask,
                _ => throw new InvalidOperationException("no transcript for a Paperclip row"),
                new RelayInput(new RelayInput.Seams(_ => TerminalTyping.Route.None, _ => Task.FromResult<string?>(null), (_, _) => Task.FromResult(false), _ => Task.CompletedTask)),
                new RelayRemote.LaunchSeams(false, _ => null, _ => true, () => "", (_, _) => (null, null), _ => null),
                _ => { })
            { Paperclip = withHost ? Host : null }, Sent, new NonceWindow());
            Remote.OnStatuses([(Agent, new SessionStatus { Source = SessionSource.Paperclip, State = "idle", Title = Title })]);
        }

        public Feed OnlyFeed => Feeds.Single();
        public void Tick() => Remote.Tick(DateTimeOffset.FromUnixTimeSeconds(1794111792));
    }

    private static ChatSnapshot Snap(string? task, params PaperclipChat.Comment[] comments) =>
        new(task is null ? null : new PaperclipChat.TaskRef(task, "CB-1", "Triage", "in_progress"), comments, null, false);

    private static PaperclipChat.Comment C(string id, string body, bool agent = false, int sec = 0) =>
        new(id, body, agent ? "3f2a9c4e-6b1d-4e87-a5c3-9d0e7b2f1a64" : null, agent ? null : "u1", null, T0.AddSeconds(sec));

    private static string Code(World w, int i) => w.Sent.Replies[i].Body.GetProperty("code").GetString()!;

    // ---- watch -------------------------------------------------------------------------------

    [Fact]
    public async Task A_watch_answers_total_zero_at_first_then_the_backfill_when_the_first_read_lands_then_tails()
    {
        var w = new World();
        await w.Remote.HandleAsync("ph", [], new WatchRequest("r1", Agent, null));
        var ok = w.Sent.Replies.Single();
        Assert.Equal(("watching", Agent, 0), (ok.Body.GetProperty("code").GetString(), ok.Session, ok.Body.GetProperty("total").GetInt32()));
        Assert.Empty(w.Sent.Windows);
        Assert.Equal(1, w.OnlyFeed.Opens);
        Assert.True(w.Remote.IsWatching("ph", Agent));
        Assert.Equal(1, w.Remote.WatchCount);

        // The first read lands: the whole thread, live false.
        w.OnlyFeed.Push(Snap("i1", C("1", "hi", sec: 1), C("2", "yo", agent: true, sec: 2)));
        w.Tick();
        var backfill = Assert.Single(w.Sent.Windows);
        Assert.Equal(("ph", Agent, 0, 3, false), (backfill.Phone, backfill.Session, backfill.Body.GetProperty("from_line").GetInt32(),
            backfill.Body.GetProperty("total").GetInt32(), backfill.Body.GetProperty("live").GetBoolean()));
        var lines = backfill.Body.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(("system", 0), (lines[0].GetProperty("role").GetString(), lines[0].GetProperty("at").GetInt64()));   // the task row: at 0
        Assert.Contains("CB-1", lines[0].GetProperty("text").GetString());
        Assert.Equal(("user", "hi"), (lines[1].GetProperty("role").GetString(), lines[1].GetProperty("text").GetString()));
        Assert.Equal("assistant", lines[2].GetProperty("role").GetString());

        // A tick with nothing new sends nothing.
        w.Tick();
        Assert.Single(w.Sent.Windows);

        // A new comment: a live tail from the old count.
        w.OnlyFeed.Push(Snap("i1", C("1", "hi", sec: 1), C("2", "yo", agent: true, sec: 2), C("3", "more", sec: 3)));
        w.Tick();
        var tail = w.Sent.Windows[1].Body;
        Assert.Equal((3, 4, true), (tail.GetProperty("from_line").GetInt32(), tail.GetProperty("total").GetInt32(), tail.GetProperty("live").GetBoolean()));

        // The task changed: a reset backfill from 0.
        w.OnlyFeed.Push(Snap("i2", C("9", "other", sec: 9)));
        w.Tick();
        var reset = w.Sent.Windows[2].Body;
        Assert.Equal((0, 2, false, true), (reset.GetProperty("from_line").GetInt32(), reset.GetProperty("total").GetInt32(),
            reset.GetProperty("live").GetBoolean(), reset.GetProperty("reset").GetBoolean()));
    }

    [Fact]
    public async Task A_watch_on_a_thread_that_already_has_rows_answers_its_total_and_sends_the_backfill_at_once()
    {
        var w = new World();
        await w.Remote.HandleAsync("ph1", [], new WatchRequest("r1", Agent, null));
        w.OnlyFeed.Push(Snap("i1", C("1", "hi", sec: 1)));
        w.Sent.Replies.Clear();

        await w.Remote.HandleAsync("ph2", [], new WatchRequest("r2", Agent, null));
        Assert.Equal(2, w.Sent.Replies.Single().Body.GetProperty("total").GetInt32());
        var window = Assert.Single(w.Sent.Windows, x => x.Phone == "ph2");
        Assert.False(window.Body.GetProperty("live").GetBoolean());
        Assert.Equal(2, window.Body.GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public async Task A_row_marked_error_in_the_middle_of_the_thread_reaches_the_phone_as_a_tail_from_that_row()
    {
        var w = new World();
        await w.Remote.HandleAsync("ph", [], new WatchRequest("r1", Agent, null));
        var act = PaperclipChat.Activity.Empty("r1") with
        {
            Rows = [new("r:r1:t:a", ChatRole.System, "Bash: dotnet test", T0.AddSeconds(2)), new("r:r1:answer", ChatRole.Assistant, "done", T0.AddSeconds(5))]
        };
        w.OnlyFeed.Push(new ChatSnapshot(new("i1", "CB-1", "Triage", "in_progress"), [C("1", "hi", sec: 1)], act, false));
        w.Tick();
        w.Sent.Windows.Clear();

        var red = act with { Rows = [act.Rows[0] with { Error = true }, act.Rows[1]] };
        w.OnlyFeed.Push(new ChatSnapshot(new("i1", "CB-1", "Triage", "in_progress"), [C("1", "hi", sec: 1)], red, false));
        w.Tick();
        var tail = Assert.Single(w.Sent.Windows).Body;
        Assert.Equal((2, true), (tail.GetProperty("from_line").GetInt32(), tail.GetProperty("live").GetBoolean()));
        var lines = tail.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);   // the red tool line and the row after it, restated
        Assert.True(lines[0].GetProperty("error").GetBoolean());
    }

    [Fact]
    public async Task Watch_before_is_ok_with_the_held_total_and_no_window_and_takes_no_share_of_the_thread()
    {
        var w = new World();
        await w.Remote.HandleAsync("ph", [], new WatchRequest("r0", Agent, 5));
        Assert.Equal(0, w.Sent.Replies.Single().Body.GetProperty("total").GetInt32());
        Assert.Equal(0, w.Host.Count);   // nothing acquired
        Assert.Empty(w.Feeds);

        await w.Remote.HandleAsync("ph", [], new WatchRequest("r1", Agent, null));
        w.OnlyFeed.Push(Snap("i1", C("1", "hi"), C("2", "yo")));
        w.Tick();
        w.Sent.Replies.Clear();
        w.Sent.Windows.Clear();
        await w.Remote.HandleAsync("ph", [], new WatchRequest("r2", Agent, 5));
        Assert.Equal(3, w.Sent.Replies.Single().Body.GetProperty("total").GetInt32());
        Assert.Empty(w.Sent.Windows);
    }

    [Fact]
    public async Task Without_the_cap_a_paperclip_row_is_answered_as_frames_v15_did_and_nothing_is_acquired()
    {
        foreach (var w in new[] { new World(withHost: false), new World { Available = false } })
        {
            await w.Remote.HandleAsync("ph", [], new WatchRequest("r1", Agent, null));
            await w.Remote.HandleAsync("ph", [], new WatchRequest("r2", Agent, 3));
            await w.Remote.HandleAsync("ph", [], new InputRequest("r3", Agent, "go"));
            Assert.Equal(["watching", "watching", "untypeable"], Enumerable.Range(0, 3).Select(i => Code(w, i)));
            Assert.Equal(0, w.Sent.Replies[0].Body.GetProperty("total").GetInt32());
            Assert.False(w.Sent.Replies[2].Body.TryGetProperty("why", out _));
            Assert.Empty(w.Sent.Windows);
            Assert.Empty(w.Feeds);
            Assert.Equal(0, w.Remote.WatchCount);
            Assert.Empty(w.Actions.Sent);
        }
    }

    // ---- the lifetime: a phone watching counts as an open panel, and stops when it stops ------

    [Fact]
    public async Task The_last_release_closes_the_feed_and_a_second_phone_on_the_same_agent_does_not_start_a_second_one()
    {
        var w = new World();
        await w.Remote.HandleAsync("ph1", [], new WatchRequest("r1", Agent, null));
        await w.Remote.HandleAsync("ph2", [], new WatchRequest("r2", Agent, null));
        Assert.Single(w.Feeds);
        Assert.Equal((1, 0), (w.OnlyFeed.Opens, w.OnlyFeed.Closes));
        Assert.Equal(1, w.Host.Count);

        await w.Remote.HandleAsync("ph1", [], new UnwatchRequest("r3", Agent));
        Assert.Equal((1, 0), (w.OnlyFeed.Opens, w.OnlyFeed.Closes));        // one phone still watching
        Assert.Equal("unwatched", Code(w, 2));

        await w.Remote.HandleAsync("ph2", [], new UnwatchRequest("r4", Agent));
        Assert.Equal((1, 1), (w.OnlyFeed.Opens, w.OnlyFeed.Closes));        // the last release closes it
        Assert.Equal(0, w.Host.Count);
        Assert.Equal(0, w.Remote.WatchCount);

        // A phone that watches again starts a fresh one.
        await w.Remote.HandleAsync("ph1", [], new WatchRequest("r5", Agent, null));
        Assert.Equal(2, w.Feeds.Count);
        Assert.Equal(1, w.Feeds[1].Opens);
    }

    [Fact]
    public async Task A_second_watch_by_the_same_phone_replaces_the_first_without_taking_a_second_share()
    {
        var w = new World();
        await w.Remote.HandleAsync("ph", [], new WatchRequest("r1", Agent, null));
        await w.Remote.HandleAsync("ph", [], new WatchRequest("r2", Agent, null));
        Assert.Equal((1, 0), (w.OnlyFeed.Opens, w.OnlyFeed.Closes));
        Assert.Equal(1, w.Remote.WatchCount);
        await w.Remote.HandleAsync("ph", [], new UnwatchRequest("r3", Agent));
        Assert.Equal(1, w.OnlyFeed.Closes);
    }

    [Fact]
    public async Task A_phone_going_offline_the_socket_closing_and_the_row_leaving_the_roster_each_end_the_watch()
    {
        var w = new World();
        await w.Remote.HandleAsync("ph", [], new WatchRequest("r1", Agent, null));
        w.Remote.PhoneOffline("ph");
        Assert.Equal((0, 1), (w.Remote.WatchCount, w.OnlyFeed.Closes));

        await w.Remote.HandleAsync("ph", [], new WatchRequest("r2", Agent, null));
        w.Remote.PhoneOffline("someone-else");                                   // another phone: this watch stays
        Assert.Equal(1, w.Remote.WatchCount);
        w.Remote.Disconnected();
        Assert.Equal((0, 1), (w.Remote.WatchCount, w.Feeds[1].Closes));

        await w.Remote.HandleAsync("ph", [], new WatchRequest("r3", Agent, null));
        w.Remote.OnStatuses([]);                                                // the agent left the roster: no final window
        Assert.Equal((0, 1), (w.Remote.WatchCount, w.Feeds[2].Closes));
        Assert.Empty(w.Sent.Windows);

        // A scan that still lists it keeps the watch.
        await w.Remote.HandleAsync("ph", [], new WatchRequest("r4", "paperclip:gone", null));
        w.Remote.OnStatuses([(Agent, new SessionStatus { Source = SessionSource.Paperclip, Title = "Atlas" })]);
        await w.Remote.HandleAsync("ph", [], new WatchRequest("r5", Agent, null));
        w.Remote.OnStatuses([(Agent, new SessionStatus { Source = SessionSource.Paperclip, Title = "Atlas" })]);
        Assert.Equal(1, w.Remote.WatchCount);
    }

    // ---- input ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_message_to_an_agent_with_a_task_is_a_comment_ok_typed_and_arrives_as_a_user_line_in_the_next_tail()
    {
        var w = new World();
        await w.Remote.HandleAsync("ph", [], new WatchRequest("r1", Agent, null));
        w.OnlyFeed.Push(Snap("i1", C("1", "hi", sec: 1)));
        w.Tick();
        w.Sent.Replies.Clear();
        w.Sent.Windows.Clear();

        await w.Remote.HandleAsync("ph", [], new InputRequest("r2", Agent, "  look at logout  "));
        Assert.Equal(["comment:i1:look at logout"], w.Actions.Sent);
        Assert.Equal(("typed", Agent), (Code(w, 0), w.Sent.Replies[0].Session));
        Assert.Equal(1, w.OnlyFeed.Nudges);

        w.Tick();
        var tail = Assert.Single(w.Sent.Windows).Body;
        Assert.Equal((2, true), (tail.GetProperty("from_line").GetInt32(), tail.GetProperty("live").GetBoolean()));
        var line = tail.GetProperty("lines")[0];
        Assert.Equal(("user", "look at logout"), (line.GetProperty("role").GetString(), line.GetProperty("text").GetString()));

        // The comment arriving on the next read is the same line, not a second one.
        w.Sent.Windows.Clear();
        w.OnlyFeed.Push(Snap("i1", C("1", "hi", sec: 1), C("c1", "look at logout", sec: 3)));
        w.Tick();
        Assert.Empty(w.Sent.Windows);
    }

    [Fact]
    public async Task A_message_to_an_agent_with_no_task_opens_one_and_a_second_phone_sees_the_line()
    {
        var w = new World();
        await w.Remote.HandleAsync("ph1", [], new WatchRequest("r1", Agent, null));
        await w.Remote.HandleAsync("ph2", [], new WatchRequest("r2", Agent, null));
        w.OnlyFeed.Push(Snap(null));
        w.Tick();
        w.Sent.Windows.Clear();

        await w.Remote.HandleAsync("ph1", [], new InputRequest("r3", Agent, "start on the login bug"));
        Assert.Equal(["open:3f2a9c4e-6b1d-4e87-a5c3-9d0e7b2f1a64:start on the login bug"], w.Actions.Sent);
        Assert.Equal("typed", Code(w, w.Sent.Replies.Count - 1));
        w.Tick();
        Assert.Equal(["ph1", "ph2"], w.Sent.Windows.Select(x => x.Phone).Order());
        // The task the message opened is the thread's task until the agent runs on another.
        Assert.Equal("i9", w.OnlyFeed.Target!().Task!.Id);
    }

    [Theory]
    [InlineData("NeedsReconnect", null, "Reconnect to Paperclip to message Atlas.")]
    [InlineData("Unreachable", "x", "Can't reach Paperclip.")]
    [InlineData("Refused", "Comment too long", "Comment too long")]
    [InlineData("Refused", null, "Paperclip refused that.")]
    [InlineData("Refused", "That task is no longer in Paperclip.", "That task is no longer in Paperclip.")]
    public async Task A_post_that_did_not_go_through_is_send_failed_with_the_desks_sentence_and_nothing_is_shown(string result, string? why, string sentence)
    {
        var w = new World();
        w.Actions.Reply = new(new(Enum.Parse<PaperclipActionResult>(result), why));
        await w.Remote.HandleAsync("ph", [], new WatchRequest("r1", Agent, null));
        w.OnlyFeed.Push(Snap("i1"));
        w.Tick();
        w.Sent.Replies.Clear();
        w.Sent.Windows.Clear();

        await w.Remote.HandleAsync("ph", [], new InputRequest("r2", Agent, "hello"));
        var err = w.Sent.Replies.Single();
        Assert.Equal(("send_failed", sentence, Agent), (err.Body.GetProperty("code").GetString(), err.Body.GetProperty("why").GetString(), err.Session));
        w.Tick();
        Assert.Empty(w.Sent.Windows);                                                  // no line for a message that did not land
        Assert.Equal(why == "That task is no longer in Paperclip." ? 1 : 0, w.OnlyFeed.Nudges);
    }

    [Fact]
    public async Task A_thrown_post_nothing_to_send_and_no_actions_are_each_send_failed_and_never_an_exception_text()
    {
        var w = new World();
        w.Actions.Throw = new InvalidOperationException("secret body");
        await w.Remote.HandleAsync("ph", [], new InputRequest("r1", Agent, "hi"));
        await w.Remote.HandleAsync("ph", [], new InputRequest("r2", Agent, "   "));
        w.ActionsSeam = null;
        await w.Remote.HandleAsync("ph", [], new InputRequest("r3", Agent, "hi"));
        Assert.Equal(["Can't reach Paperclip.", "Nothing to send.", "Paperclip is not connected."],
            w.Sent.Replies.Select(r => r.Body.GetProperty("why").GetString()));
        Assert.All(w.Sent.Replies, r => Assert.Equal("send_failed", r.Body.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task A_phone_that_is_not_watching_borrows_the_thread_for_the_send_and_gives_it_back()
    {
        var w = new World();
        await w.Remote.HandleAsync("ph", [], new InputRequest("r1", Agent, "hi"));
        Assert.Equal("typed", Code(w, 0));
        Assert.Equal((1, 1), (w.OnlyFeed.Opens, w.OnlyFeed.Closes));
        Assert.Equal(0, w.Host.Count);
        Assert.Equal(0, w.Remote.WatchCount);

        // The borrowed share is given back when the send fails too.
        w.Actions.Reply = new(new(PaperclipActionResult.Unreachable));
        await w.Remote.HandleAsync("ph", [], new InputRequest("r2", Agent, "hi"));
        Assert.Equal((1, 1), (w.Feeds[1].Opens, w.Feeds[1].Closes));
    }

    [Fact]
    public async Task A_host_that_has_just_stopped_being_available_answers_a_watch_and_a_send_without_a_thread()
    {
        var w = new World();
        // Available is read at each request: a desk that disconnects from Paperclip mid-session answers as v15 from then on.
        w.Available = false;
        Assert.Null(w.Host.Acquire(Agent, "Atlas"));
        await w.Remote.HandleAsync("ph", [], new WatchRequest("r1", Agent, null));
        await w.Remote.HandleAsync("ph", [], new InputRequest("r2", Agent, "hi"));
        Assert.Equal(["watching", "untypeable"], [Code(w, 0), Code(w, 1)]);
    }

    [Fact]
    public void A_long_why_is_cut_to_140_characters_on_a_scalar_boundary_and_a_short_one_is_untouched()
    {
        Assert.Equal("Can't reach Paperclip.", RelayRemote.Clip("Can't reach Paperclip."));
        Assert.Equal("", RelayRemote.Clip(null));
        var exact = new string('a', 140);
        Assert.Equal(exact, RelayRemote.Clip(exact));
        var cut = RelayRemote.Clip(new string('a', 141));
        Assert.Equal(140, cut.Length);
        Assert.EndsWith("…", cut);
        // A pair of UTF-16 units straddling the cut is dropped whole, never split.
        var astral = RelayRemote.Clip(new string('a', 138) + "😀" + "tail");
        Assert.True(astral.Length <= 140);
        Assert.EndsWith("…", astral);
        Assert.False(char.IsHighSurrogate(astral[^2]));
    }

    [Fact]
    public async Task A_long_agent_name_cannot_push_the_reconnect_sentence_past_the_limit()
    {
        var w = new World();
        w.Actions.Reply = new(new(PaperclipActionResult.NeedsReconnect));
        w.Remote.OnStatuses([(Agent, new SessionStatus { Source = SessionSource.Paperclip, Title = new string('N', 200) })]);
        await w.Remote.HandleAsync("ph", [], new InputRequest("r1", Agent, "hi"));
        var why = w.Sent.Replies.Single().Body.GetProperty("why").GetString()!;
        Assert.True(why.Length <= 140);
        Assert.StartsWith("Reconnect to Paperclip to message NNN", why);
    }

    // QA batch 1, item 3: the caps name is what promises send_failed, and it drops when the link leaves Connected.
    // The intent, pinned: a watch that is already up STAYS (a thread that was working is not torn down by a blip, and
    // the roster still lists the agent, dimmed); it just stops being served new windows by a frozen feed, and a
    // message typed meanwhile is frames-v15's untypeable, the answer of a desk without the cap, until the cap returns.
    [Fact]
    public async Task A_watch_already_up_stays_when_the_cap_drops_and_input_is_untypeable_until_it_returns()
    {
        var w = new World();
        await w.Remote.HandleAsync("ph", [], new WatchRequest("r1", Agent, null));
        w.OnlyFeed.Push(Snap("i1", C("1", "hi", sec: 1)));
        w.Tick();
        w.Sent.Replies.Clear();
        w.Sent.Windows.Clear();

        w.Available = false;                                                     // the link left Connected: the name drops
        Assert.True(w.Remote.IsWatching("ph", Agent));                           // the watch stays...
        Assert.Equal(1, w.Remote.WatchCount);
        Assert.Equal(0, w.OnlyFeed.Closes);                                      // ...and keeps its share of the feed
        w.Tick();
        Assert.Empty(w.Sent.Windows);                                            // a frozen thread sends nothing

        await w.Remote.HandleAsync("ph", [], new InputRequest("r2", Agent, "hello"));
        var refusal = w.Sent.Replies.Single();
        Assert.Equal("untypeable", refusal.Body.GetProperty("code").GetString());   // not send_failed: that code needs the cap
        Assert.False(refusal.Body.TryGetProperty("why", out _));
        Assert.Empty(w.Actions.Sent);

        // The cap comes back: the same watch carries on, a tail reaches the phone, and input works again.
        w.Available = true;
        w.OnlyFeed.Push(Snap("i1", C("1", "hi", sec: 1), C("2", "back", agent: true, sec: 5)));
        w.Tick();
        Assert.Equal(1, Assert.Single(w.Sent.Windows).Body.GetProperty("lines").GetArrayLength());
        w.Sent.Replies.Clear();
        await w.Remote.HandleAsync("ph", [], new InputRequest("r3", Agent, "hello again"));
        Assert.Equal("typed", Code(w, 0));
    }
}

