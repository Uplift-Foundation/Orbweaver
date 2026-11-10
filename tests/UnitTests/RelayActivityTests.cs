using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-323: the activity snapshot (frames-v7 `activity`) and its 2 s debounce,
/// pure. Every count, the oldest-amber rule and its tie, every cut, the clamp,
/// the empty-prompt case, and each arm of the tracker — the deferral, the
/// flush, the push and welcome overrides, the retry after rate_limited — fed
/// in orders a real scan loop can produce, including the late ones.
/// </summary>
public class RelayActivityTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1794111792);

    private static RelaySession Row(string id, string state, DateTimeOffset? since = null, string title = "proj") =>
        new RelaySession(id, "claude", title, "/c", state, null, null) { Since = since ?? T0 };

    private static ActivityPrompt? None(string _) => null;

    // ---- the snapshot ----------------------------------------------------------

    [Fact]
    public void Counts_each_live_state_and_nothing_else()
    {
        var s = RelayActivity.Snapshot(
            [Row("a", "waiting"), Row("b", "waiting", T0.AddSeconds(5)), Row("c", "generating"),
             Row("d", "idle"), Row("e", "idle"), Row("f", "idle"), Row("g", "ended"), Row("h", "something-new")],
            None, "MacBook Pro");
        Assert.Equal(("MacBook Pro", 2, 1, 3), (s.Machine, s.Amber, s.Working, s.Idle));
    }

    [Fact]
    public void No_amber_means_no_oldest_and_the_frame_writes_null()
    {
        var s = RelayActivity.Snapshot([Row("a", "generating"), Row("b", "idle")], None, "MacBook Pro");
        Assert.Null(s.Oldest);
        Assert.Equal("""{"t":"activity","ref":"act-ok-2","boot":"9c1e3f7a5b2d8046","seq":2,"machine":"MacBook Pro","amber":0,"working":1,"idle":1,"oldest":null}""",
            RelayActivity.Frame("act-ok-2", "9c1e3f7a5b2d8046", 2, s));
    }

    [Fact]
    public void The_oldest_amber_is_the_smallest_since_whatever_the_row_order()
    {
        var s = RelayActivity.Snapshot(
            [Row("z-late", "waiting", T0.AddSeconds(30)), Row("y-first", "waiting", T0), Row("x-mid", "waiting", T0.AddSeconds(10))],
            None, "m");
        Assert.Equal(("y-first", T0.ToUnixTimeSeconds()), (s.Oldest!.SessionId, s.Oldest.Since));
    }

    [Fact]
    public void Equal_since_is_broken_by_the_smaller_session_id_at_whole_seconds()
    {
        // The wire's since is whole seconds, so two rows inside one second are
        // a tie there and must be a tie here — the sub-second order does not
        // get to pick a different oldest than the phone would compute.
        var s = RelayActivity.Snapshot(
            [Row("b", "waiting", T0.AddMilliseconds(100)), Row("a", "waiting", T0.AddMilliseconds(900)), Row("c", "waiting", T0)],
            None, "m");
        Assert.Equal("a", s.Oldest!.SessionId);
    }

    [Fact]
    public void An_amber_with_no_minted_prompt_carries_empty_request_tool_and_command()
    {
        var s = RelayActivity.Snapshot([Row("0f6c4f2a", "waiting", title: "orbweaver")], None, "MacBook Pro");
        Assert.Equal(new ActivityOldest("0f6c4f2a", "", "orbweaver", "", "", T0.ToUnixTimeSeconds()), s.Oldest);
        // frames-v7-vectors act-ok-3, byte for byte.
        Assert.Equal("""{"t":"activity","ref":"act-ok-3","boot":"9c1e3f7a5b2d8046","seq":3,"machine":"MacBook Pro","amber":1,"working":0,"idle":0,"oldest":{"session_id":"0f6c4f2a","request_id":"","title":"orbweaver","tool":"","command":"","since":1794111792}}""",
            RelayActivity.Frame("act-ok-3", "9c1e3f7a5b2d8046", 3, s));
    }

    [Fact]
    public void The_oldest_names_its_own_prompt_and_no_other_sessions()
    {
        var prompts = new Dictionary<string, ActivityPrompt>
        {
            ["0f6c4f2a"] = new("01JC9R0A5N2Q7W8X3E4T6Y9ZBD", "Bash", "dotnet test tests/UnitTests"),
            ["later"] = new("01JC9R0A5N2Q7W8X3E4T6Y9ZZZ", "Edit", "other.cs"),
        };
        var s = RelayActivity.Snapshot(
            [Row("0f6c4f2a", "waiting", title: "orbweaver"), Row("later", "waiting", T0.AddSeconds(1)),
             Row("w1", "generating"), Row("w2", "generating"), Row("i1", "idle"), Row("i2", "idle"), Row("i3", "idle")],
            id => prompts.GetValueOrDefault(id), "MacBook Pro");
        // frames-v7-vectors act-ok-1's body (with amber 2 here: two waiting).
        Assert.Equal("""{"t":"activity","ref":"act-ok-1","boot":"9c1e3f7a5b2d8046","seq":1,"machine":"MacBook Pro","amber":2,"working":2,"idle":3,"oldest":{"session_id":"0f6c4f2a","request_id":"01JC9R0A5N2Q7W8X3E4T6Y9ZBD","title":"orbweaver","tool":"Bash","command":"dotnet test tests/UnitTests","since":1794111792}}""",
            RelayActivity.Frame("act-ok-1", "9c1e3f7a5b2d8046", 1, s));
    }

    [Fact]
    public void Every_field_goes_through_the_cut_rule_at_its_own_limit()
    {
        var title = new string('T', 65);
        var tool = new string('t', 33);
        var command = new string('x', 159) + "😀y";          // the 160th unit would be half a pair
        var machine = new string('M', 33);
        var s = RelayActivity.Snapshot([Row("s", "waiting", title: title)], _ => new ActivityPrompt("r", tool, command), machine);
        Assert.Equal(new string('M', 31) + "…", s.Machine);
        Assert.Equal(new string('T', 63) + "…", s.Oldest!.Title);
        Assert.Equal(new string('t', 31) + "…", s.Oldest.Tool);
        Assert.Equal(new string('x', 159) + "…", s.Oldest.Command);
        Assert.Equal(160, s.Oldest.Command.Length);
    }

    [Fact]
    public void Fields_exactly_at_their_limit_are_not_cut()
    {
        var s = RelayActivity.Snapshot([Row("s", "waiting", title: new string('T', 64))],
            _ => new ActivityPrompt("r", new string('t', 32), new string('c', 160)), new string('M', 32));
        Assert.Equal((32, 64, 32, 160), (s.Machine.Length, s.Oldest!.Title.Length, s.Oldest.Tool.Length, s.Oldest.Command.Length));
        Assert.DoesNotContain("…", s.Machine + s.Oldest.Title + s.Oldest.Tool + s.Oldest.Command);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_desktop_with_no_name_still_names_itself(string machine) =>
        Assert.Equal(RelayActivity.UnnamedMachine, RelayActivity.Snapshot([], None, machine).Machine);

    [Fact]
    public void Counts_clamp_at_the_contracts_maximum()
    {
        var rows = Enumerable.Range(0, 10_001).Select(i => Row($"g{i:D5}", "generating")).ToList();
        var s = RelayActivity.Snapshot(rows, None, "m");
        Assert.Equal(RelayActivity.CountsMax, s.Working);
    }

    // ---- the command line out of a preview -------------------------------------

    [Theory]
    [InlineData("Bash command|dotnet test tests/UnitTests|Run the unit tests|Do you want to proceed?", "dotnet test tests/UnitTests")]
    [InlineData("Edit file|src/pages/index.astro|Do you want to make this edit?", "src/pages/index.astro")]
    [InlineData("Would you like to run the following command?||  $ git push origin feature/x  ", "$ git push origin feature/x")]
    [InlineData("Do you trust the files in this folder?", "Do you trust the files in this folder?")]
    [InlineData("Bash command", "")]
    [InlineData("", "")]
    public void The_command_is_the_first_line_after_the_tool_header(string preview, string expected) =>
        Assert.Equal(expected, RelayActivity.CommandOf(preview.Length == 0 ? [] : preview.Split('|')));

    // ---- boot, ref -------------------------------------------------------------

    [Fact]
    public void A_boot_is_sixteen_lowercase_hex_and_new_each_time()
    {
        var a = RelayActivity.NewBoot();
        Assert.Matches("^[0-9a-f]{16}$", a);
        Assert.NotEqual(a, RelayActivity.NewBoot());
    }

    [Fact]
    public void A_ref_is_in_the_envelope_id_grammar_and_recognisable()
    {
        var r = RelayActivity.Ref(new string('f', 16), 9_007_199_254_740_992);
        Assert.Matches("^[A-Za-z0-9_.:-]{1,64}$", r);
        Assert.True(RelayActivity.IsActivityRef(r));
        Assert.False(RelayActivity.IsActivityRef("unlink"));
        Assert.False(RelayActivity.IsActivityRef(null));
    }

    // ---- the debounce ----------------------------------------------------------

    [Fact]
    public void Decide_sends_a_change_now_or_at_the_end_of_the_interval()
    {
        Assert.Equal((false, (DateTimeOffset?)null), RelayActivity.Decide(T0, T0, changed: false));
        Assert.Equal((true, (DateTimeOffset?)null), RelayActivity.Decide(T0, null, changed: true));
        Assert.Equal((true, (DateTimeOffset?)null), RelayActivity.Decide(T0.AddSeconds(2), T0, changed: true));
        Assert.Equal((false, (DateTimeOffset?)T0.AddSeconds(2)), RelayActivity.Decide(T0.AddMilliseconds(1999), T0, changed: true));
    }

    private static ActivitySnapshot Snap(int amber, int working = 0) =>
        new("m", amber, working, 0, amber == 0 ? null : new ActivityOldest("s", "", "t", "", "", 1));

    private static long Seq(string frame) => System.Text.Json.JsonDocument.Parse(frame).RootElement.GetProperty("seq").GetInt64();
    private static int Amber(string frame) => System.Text.Json.JsonDocument.Parse(frame).RootElement.GetProperty("amber").GetInt32();

    [Fact]
    public void The_first_scan_sends_and_an_unchanged_one_sends_nothing()
    {
        var t = new RelayActivity.Tracker("0a0a0a0a0a0a0a01");
        var first = t.Offer(Snap(0, 2), T0, RelayActivity.Reason.Scan, out var due);
        Assert.False(due);
        Assert.Equal("""{"t":"activity","ref":"act-0a0a0a0a0a0a0a01-1","boot":"0a0a0a0a0a0a0a01","seq":1,"machine":"m","amber":0,"working":2,"idle":0,"oldest":null}""", first);
        Assert.Null(t.Offer(Snap(0, 2), T0.AddSeconds(10), RelayActivity.Reason.Scan, out due));
        Assert.False(due);
        Assert.Equal(1, t.Seq);
    }

    [Fact]
    public void Two_changes_inside_two_seconds_make_one_frame_carrying_the_second()
    {
        var t = new RelayActivity.Tracker("0a0a0a0a0a0a0a01");
        t.Offer(Snap(0), T0, RelayActivity.Reason.Scan, out _);
        Assert.Null(t.Offer(Snap(1), T0.AddMilliseconds(300), RelayActivity.Reason.Scan, out var due1));
        Assert.True(due1);
        Assert.Equal(T0.AddSeconds(2), t.DueAt);
        Assert.Null(t.Offer(Snap(2), T0.AddMilliseconds(900), RelayActivity.Reason.Scan, out var due2));
        Assert.False(due2);                                         // one flush, not two
        Assert.Null(t.Flush(Snap(2), T0.AddMilliseconds(1999)));   // too early: still due
        Assert.NotNull(t.DueAt);
        var sent = t.Flush(Snap(2), T0.AddSeconds(2))!;
        Assert.Equal((2L, 2), (Seq(sent), Amber(sent)));
        Assert.Null(t.DueAt);
        Assert.Null(t.Flush(Snap(3), T0.AddSeconds(3)));           // nothing due, nothing sent
    }

    [Fact]
    public void A_flush_whose_state_went_back_sends_nothing()
    {
        var t = new RelayActivity.Tracker("0a0a0a0a0a0a0a01");
        t.Offer(Snap(0), T0, RelayActivity.Reason.Scan, out _);
        t.Offer(Snap(1), T0.AddSeconds(1), RelayActivity.Reason.Scan, out _);
        Assert.Null(t.Flush(Snap(0), T0.AddSeconds(2)));
        Assert.Null(t.DueAt);
        Assert.Equal(1, t.Seq);
    }

    [Fact]
    public void A_scan_that_arrives_after_a_missed_flush_sends_it()
    {
        var t = new RelayActivity.Tracker("0a0a0a0a0a0a0a01");
        t.Offer(Snap(0), T0, RelayActivity.Reason.Scan, out _);
        t.Offer(Snap(1), T0.AddSeconds(1), RelayActivity.Reason.Scan, out _);
        var sent = t.Offer(Snap(3), T0.AddSeconds(5), RelayActivity.Reason.Scan, out var due);
        Assert.False(due);
        Assert.Equal(3, Amber(sent!));
    }

    [Fact]
    public void A_push_sends_a_changed_snapshot_at_once_and_an_unchanged_one_not_at_all()
    {
        var t = new RelayActivity.Tracker("0a0a0a0a0a0a0a01");
        t.Offer(Snap(0), T0, RelayActivity.Reason.Scan, out _);
        Assert.Null(t.Offer(Snap(1), T0.AddMilliseconds(100), RelayActivity.Reason.Scan, out _));
        var pushed = t.Offer(Snap(1), T0.AddMilliseconds(200), RelayActivity.Reason.Push, out var due);
        Assert.False(due);
        Assert.Equal((2L, 1), (Seq(pushed!), Amber(pushed!)));
        Assert.Null(t.DueAt);                                       // the deferred send is spent
        Assert.Null(t.Offer(Snap(1), T0.AddMilliseconds(300), RelayActivity.Reason.Push, out _));
    }

    [Fact]
    public void A_welcome_always_sends_even_when_nothing_changed()
    {
        var t = new RelayActivity.Tracker("0a0a0a0a0a0a0a01");
        t.Offer(Snap(1), T0, RelayActivity.Reason.Scan, out _);
        var again = t.Offer(Snap(1), T0.AddMilliseconds(10), RelayActivity.Reason.Welcome, out var due);
        Assert.False(due);
        Assert.Equal(2, Seq(again!));
    }

    [Fact]
    public void Reset_drops_a_deferred_send()
    {
        var t = new RelayActivity.Tracker("0a0a0a0a0a0a0a01");
        t.Offer(Snap(0), T0, RelayActivity.Reason.Scan, out _);
        t.Offer(Snap(1), T0.AddSeconds(1), RelayActivity.Reason.Scan, out _);
        t.Reset();
        Assert.Null(t.DueAt);
        Assert.Null(t.Flush(Snap(1), T0.AddSeconds(5)));
    }

    [Fact]
    public void A_rate_limited_frame_is_sent_again_after_the_interval_with_the_state_then()
    {
        var t = new RelayActivity.Tracker("0a0a0a0a0a0a0a01");
        t.Offer(Snap(1), T0, RelayActivity.Reason.Scan, out _);
        Assert.True(t.Retry(T0.AddMilliseconds(50)));
        Assert.Equal(T0.AddSeconds(2), t.DueAt);
        Assert.False(t.Retry(T0.AddMilliseconds(60)));             // already due: one resend
        var again = t.Flush(Snap(1), T0.AddSeconds(2))!;            // unchanged, and still sent
        Assert.Equal((2L, 1), (Seq(again), Amber(again)));
    }

    [Fact]
    public void A_retry_before_any_send_is_due_one_interval_from_now()
    {
        var t = new RelayActivity.Tracker("0a0a0a0a0a0a0a01");
        Assert.True(t.Retry(T0));
        Assert.Equal(T0.AddSeconds(2), t.DueAt);
    }
}
