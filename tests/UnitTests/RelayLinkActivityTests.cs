using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-323: where the activity frame goes in what RelayLink sends. First on
/// every welcome, ahead of everything; first in a scan; ahead of any frame
/// carrying push amber, done or resolve, at once even inside the debounce;
/// deferred otherwise, with the flush handed to the client. And what the
/// relay's replies do: ok passes quietly, activity_stale and bad_frame are
/// said, an old relay's err unexpected is said once per connection, and a
/// rate_limited frame is sent again.
/// </summary>
public partial class RelayLinkTests
{
    private const string Boot = "0a0a0a0a0a0a0a01";

    private static bool IsActivity(string frame) => T(frame) == "activity";

    private static JsonElement Activity(string frame)
    {
        Assert.True(IsActivity(frame), frame);
        return J(frame);
    }

    private (RelayLink Link, List<string> Notes, List<DateTimeOffset> Due, List<(string Ref, string T, string Code)> Replies) Watched(RelayLink link, List<string> notes)
    {
        var due = new List<DateTimeOffset>();
        var replies = new List<(string, string, string)>();
        link.ActivityDue += due.Add;
        link.ActivityReplied += (r, t, c) => replies.Add((r, t, c));
        return (link, notes, due, replies);
    }

    [Fact]
    public void A_welcome_sends_the_snapshot_before_anything_else_it_sends()
    {
        var (link, identity, notes) = New(activity: new RelayActivity.Tracker(Boot));
        identity.AddPendingUnlink("ph_00000000000000ee");
        var frames = link.OnFrame(Welcome());
        Assert.Equal(["activity", "unlink"], frames.Select(T));
        var a = Activity(frames[0]);
        Assert.Equal(("act-" + Boot + "-1", Boot, 1L, "Desktop", 0), (a.GetProperty("ref").GetString(), a.GetProperty("boot").GetString(),
            a.GetProperty("seq").GetInt64(), a.GetProperty("machine").GetString(), a.GetProperty("amber").GetInt32()));

        // A reconnect sends it again, unchanged or not, with the next seq.
        link.Disconnected();
        Assert.Equal(2, Activity(link.OnFrame(Welcome())[0]).GetProperty("seq").GetInt64());
    }

    [Fact]
    public void A_reconnect_sends_the_snapshot_before_the_phones_roster()
    {
        var (link, _, phone, _, _) = Paired(new RelayActivity.Tracker(Boot));
        link.OnScan([Row("s1", "waiting")]);
        link.Disconnected();
        var sent = new List<string>(link.OnFrame(Welcome()));
        sent.AddRange(link.OnFrame(Presence(phone.Id, online: true)));
        Assert.True(IsActivity(sent[0]));
        Assert.Equal(1, Activity(sent[0]).GetProperty("amber").GetInt32());
        Assert.Equal("roster", phone.OpenFromDesktop(sent[1]).GetProperty("t").GetString());
    }

    [Fact]
    public void Nothing_goes_while_not_welcomed()
    {
        var (link, _, _) = New(activity: new RelayActivity.Tracker(Boot));
        Assert.Empty(link.OnScan([Row("s1", "waiting")]));
        Assert.Empty(link.FlushActivity());
    }

    [Fact]
    public void A_scan_that_changes_the_counts_leads_with_the_snapshot_and_the_next_inside_two_seconds_waits()
    {
        var (link, _, phone, notes, _) = Paired(new RelayActivity.Tracker(Boot));
        var (_, _, due, _) = Watched(link, notes);
        _now = _now.AddSeconds(5);

        var first = link.OnScan([Row("s1", "generating")]);
        Assert.Equal(1, Activity(first[0]).GetProperty("working").GetInt32());
        Assert.Equal("roster", phone.OpenFromDesktop(first[1]).GetProperty("t").GetString());

        _now = _now.AddMilliseconds(500);
        var second = link.OnScan([Row("s1", "waiting")]);
        Assert.DoesNotContain(second, IsActivity);                  // deferred
        Assert.Equal("delta", phone.OpenFromDesktop(Assert.Single(second)).GetProperty("t").GetString());
        Assert.Equal([_now.AddMilliseconds(1500)], due);

        _now = _now.AddMilliseconds(300);
        link.OnScan([Row("s1", "idle")]);
        _now = _now.AddMilliseconds(100);
        link.OnScan([Row("s1", "waiting")]);                        // the one the flush must carry
        Assert.Single(due);                                          // still one flush

        _now = _now.AddMilliseconds(1100);
        var flushed = Activity(Assert.Single(link.FlushActivity()));
        Assert.Equal((1, 0, 0), (flushed.GetProperty("amber").GetInt32(), flushed.GetProperty("working").GetInt32(), flushed.GetProperty("idle").GetInt32()));
        Assert.Empty(link.FlushActivity());
    }

    [Fact]
    public void A_scan_with_a_done_push_sends_the_changed_snapshot_first_even_inside_the_interval()
    {
        var (link, _, phone, _, _) = Paired(new RelayActivity.Tracker(Boot));
        link.OnScan([Row("s1", "generating")]);
        _now = _now.AddMilliseconds(200);
        var frames = link.OnScan([Row("s1", "idle")]);
        Assert.Equal(2, frames.Count);
        var a = Activity(frames[0]);
        Assert.Equal((0, 1), (a.GetProperty("working").GetInt32(), a.GetProperty("idle").GetInt32()));
        Assert.Equal("done", J(frames[1]).GetProperty("push").GetString());
        Assert.Equal("delta", phone.OpenFromDesktop(frames[1]).GetProperty("t").GetString());
    }

    [Fact]
    public void A_done_with_no_phone_to_push_to_is_just_a_debounced_scan()
    {
        var (link, _, notes) = New(activity: new RelayActivity.Tracker(Boot));
        var (_, _, due, _) = Watched(link, notes);
        link.OnFrame(Welcome());
        link.OnScan([Row("s1", "generating")]);
        Assert.Empty(link.OnScan([Row("s1", "idle")]));             // inside 2 s, no channel, no push
        Assert.Single(due);
    }

    [Fact]
    public void An_amber_prompt_goes_after_a_snapshot_that_already_names_it()
    {
        var (link, _, phone, _, _) = Paired(new RelayActivity.Tracker(Boot));
        var prompts = new Dictionary<string, ActivityPrompt>();
        link.ActivityPrompts = id => prompts.GetValueOrDefault(id);
        _now = _now.AddSeconds(5);                                  // past the welcome's interval
        var scan = link.OnScan([new RelaySession("s1", "claude", "orbweaver", "/c", "waiting", null, null)]);
        Assert.Equal("", Activity(scan[0]).GetProperty("oldest").GetProperty("request_id").GetString());

        // The pipeline minted it a moment later, inside the debounce.
        _now = _now.AddMilliseconds(300);
        prompts["s1"] = new ActivityPrompt(Rid, "Bash", "dotnet test tests/UnitTests");
        var frames = link.SendPrompt(Rid, "s1", "{\"t\":\"prompt\"}", "{\"v\":1}");
        Assert.Equal(2, frames.Count);
        var oldest = Activity(frames[0]).GetProperty("oldest");
        Assert.Equal((Rid, "Bash", "dotnet test tests/UnitTests"),
            (oldest.GetProperty("request_id").GetString(), oldest.GetProperty("tool").GetString(), oldest.GetProperty("command").GetString()));
        Assert.Equal(("amber", Rid), (J(frames[1]).GetProperty("push").GetString(), J(frames[1]).GetProperty("id").GetString()));

        // Answered at the desk: the resolve goes after a snapshot without it.
        prompts.Clear();
        link.OnScan([new RelaySession("s1", "claude", "orbweaver", "/c", "generating", null, null)]);
        var resolved = link.SendResolved(Rid, "s1", "desk");
        Assert.Equal(2, resolved.Count);
        Assert.Equal(JsonValueKind.Null, Activity(resolved[0]).GetProperty("oldest").ValueKind);
        Assert.Equal("resolve", J(resolved[1]).GetProperty("push").GetString());
        _ = phone;
    }

    [Fact]
    public void A_push_that_reached_no_phone_sends_no_snapshot()
    {
        var (link, _, _) = New(activity: new RelayActivity.Tracker(Boot));
        link.OnFrame(Welcome());
        link.OnScan([Row("s1", "waiting")]);
        Assert.Empty(link.SendPrompt(Rid, "s1", "{}", null));
        Assert.Empty(link.SendResolved(Rid, "s1", "desk"));
    }

    [Fact]
    public void An_unchanged_snapshot_is_not_sent_again_ahead_of_a_push()
    {
        var (link, _, _, _, _) = Paired(new RelayActivity.Tracker(Boot));
        _now = _now.AddSeconds(5);
        Assert.Contains(link.OnScan([Row("s1", "waiting")]), IsActivity);
        Assert.DoesNotContain(link.SendPrompt(Rid, "s1", "{}", null), IsActivity);
    }

    [Fact]
    public void A_link_without_a_tracker_sends_no_activity_at_all()
    {
        var (link, _, notes) = New();
        var (_, _, due, replies) = Watched(link, notes);
        Assert.DoesNotContain(link.OnFrame(Welcome()), IsActivity);
        Assert.Empty(link.FlushActivity());
        link.OnFrame("""{"t":"err","ref":"act-0a0a0a0a0a0a0a01-1","code":"rate_limited"}""");
        Assert.Empty(due);
        Assert.Single(replies);
    }

    [Fact]
    public void Ok_activity_is_quiet_and_every_reply_reaches_the_log()
    {
        var (link, notes, _, replies) = Watched(New(activity: new RelayActivity.Tracker(Boot)).Link, []);
        link.Note += notes.Add;
        link.OnFrame(Welcome());
        Assert.Empty(link.OnFrame($$"""{"t":"ok","ref":"act-{{Boot}}-1","code":"activity"}"""));
        Assert.Equal([("act-" + Boot + "-1", "ok", "activity")], replies);
        Assert.Empty(notes);
    }

    [Theory]
    [InlineData("ok", "activity_stale")]
    [InlineData("err", "bad_frame")]
    [InlineData("err", "forbidden")]
    public void Any_other_answer_is_said_by_code(string t, string code)
    {
        var (link, notes, _, replies) = Watched(New(activity: new RelayActivity.Tracker(Boot)).Link, []);
        link.Note += notes.Add;
        link.OnFrame(Welcome());
        link.OnFrame($$"""{"t":"{{t}}","ref":"act-{{Boot}}-1","code":"{{code}}","message":"oldest"}""");
        Assert.Equal((t, code), (replies.Single().T, replies.Single().Code));
        Assert.Equal($"activity act-{Boot}-1 answered {t} code={code}", Assert.Single(notes));
    }

    [Fact]
    public void A_reply_with_no_code_is_still_logged()
    {
        var (link, notes, _, replies) = Watched(New(activity: new RelayActivity.Tracker(Boot)).Link, []);
        link.OnFrame($$"""{"t":"ok","ref":"act-{{Boot}}-1"}""");
        Assert.Equal("", replies.Single().Code);
    }

    [Fact]
    public void An_old_relays_err_unexpected_is_said_once_per_connection()
    {
        var (link, notes, _, replies) = Watched(New(activity: new RelayActivity.Tracker(Boot)).Link, []);
        link.Note += notes.Add;
        link.OnFrame(Welcome());
        for (var i = 1; i <= 3; i++)
            link.OnFrame($$"""{"t":"err","ref":"act-{{Boot}}-{{i}}","code":"unexpected"}""");
        Assert.Single(notes, n => n.Contains("err unexpected"));
        Assert.Equal(3, replies.Count);

        link.Disconnected();
        link.OnFrame(Welcome());
        link.OnFrame($$"""{"t":"err","ref":"act-{{Boot}}-4","code":"unexpected"}""");
        Assert.Equal(2, notes.Count(n => n.Contains("err unexpected")));
    }

    [Fact]
    public void A_rate_limited_snapshot_is_sent_again_after_the_interval()
    {
        var (link, notes, due, replies) = Watched(New(activity: new RelayActivity.Tracker(Boot)).Link, []);
        link.OnFrame(Welcome());
        link.OnFrame($$"""{"t":"err","ref":"act-{{Boot}}-1","code":"rate_limited"}""");
        Assert.Equal([("act-" + Boot + "-1", "err", "rate_limited")], replies);
        Assert.Equal([_now.AddSeconds(2)], due);
        link.OnFrame($$"""{"t":"err","ref":"act-{{Boot}}-1","code":"rate_limited"}""");
        Assert.Single(due);                                          // one resend scheduled, not two

        _now = _now.AddSeconds(2);
        Assert.Equal(2, Activity(Assert.Single(link.FlushActivity())).GetProperty("seq").GetInt64());
        _ = notes;
    }

    [Fact]
    public void A_disconnect_drops_the_deferred_send_and_the_welcome_covers_it()
    {
        var (link, notes, due, _) = Watched(New(activity: new RelayActivity.Tracker(Boot)).Link, []);
        link.OnFrame(Welcome());
        _now = _now.AddMilliseconds(100);
        link.OnScan([Row("s1", "waiting")]);
        Assert.Single(due);
        link.Disconnected();
        Assert.Empty(link.FlushActivity());
        Assert.Equal(1, Activity(link.OnFrame(Welcome())[0]).GetProperty("amber").GetInt32());
        _now = _now.AddSeconds(5);
        Assert.Empty(link.FlushActivity());
        _ = notes;
    }
}
