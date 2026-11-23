using System.Text;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-428 (frames-v16 broke_push), through the book and the link: the delta where a Paperclip row's
// mark becomes Broke rides push broke with a sealed e, once per entry, to a phone whose prefs ask for it;
// nothing else does; and a relay that predates the value costs the phone the alert and never the state.
public partial class RelayLinkTests
{
    private const string PaperclipA = "paperclip:3f2b8c1e-7a44-4d0e-9b61-5c2d8e0f1a77";
    private const string PaperclipB = "paperclip:00000000-0000-4000-8000-000000000003";

    private static RelaySession Agent(string state = "idle", string? broke = null, string id = PaperclipA, string title = "Ledger bot") =>
        new(id, "paperclip", title, "", state, null, null, broke);

    private static string? PushOf(string frame) => J(frame).GetProperty("push").GetString();
    private static string Pushed(IEnumerable<string> frames, string push) => frames.Single(f => PushOf(f) == push);

    // A paired phone that asked for broke alerts, past the baseline scan.
    private (RelayLink Link, FakePhone Phone) PairedForBroke(bool wantBroke = true, params RelaySession[] first)
    {
        var (link, _, phone, _, _) = Paired();
        if (wantBroke) link.OnFrame(phone.Sealed("pair", """{"t":"prefs","done_alerts":"all","amber_members":true,"broke_alerts":true}"""));
        if (first.Length != 1 || first[0].SessionId != "none") link.OnScan(first.Length > 0 ? first : [Agent()]);       // the baseline
        return (link, phone);
    }

    [Fact]
    public void A_row_whose_mark_becomes_Broke_is_one_delta_restating_state_and_since_with_its_why()
    {
        var book = new RelaySessionBook();
        book.Update([Agent()], _now);
        var u = book.Update([Agent(broke: "run_failed")], _now.AddSeconds(5));
        var d = Assert.Single(u.Deltas);
        Assert.Equal(("run_failed", true, false), (d.BrokeWhy, d.Broke, d.Finished));
        Assert.False(u.NeedsRoster);
        Assert.Equal(("idle", null), (d.State, d.Title));
        Assert.Equal(_now, book.Find(PaperclipA)!.Since);                     // restated, not moved
        Assert.Equal(RelayRoster.Timestamp(_now), RelayRoster.Timestamp(d.Since!.Value));

        Assert.True(book.Update([Agent(broke: "run_failed")], _now.AddSeconds(9)).IsEmpty);                                   // still Broke: nothing
        Assert.False(Assert.Single(book.Update([Agent("generating", "run_failed")], _now.AddSeconds(10)).Deltas).Broke);    // a plain state delta
        Assert.True(book.Update([Agent("generating", null)], _now.AddSeconds(11)).Deltas.All(x => !x.Broke));              // leaves Broke
        Assert.Equal("timed_out", Assert.Single(book.Update([Agent("generating", "timed_out")], _now.AddSeconds(12)).Deltas).BrokeWhy);   // breaks again: fires again
    }

    [Fact]
    public void A_row_first_seen_already_Broke_or_a_claude_row_never_fires()
    {
        var book = new RelaySessionBook();
        var first = book.Update([Agent(broke: "error")], _now);
        Assert.True(first.NeedsRoster);
        Assert.Empty(first.Deltas);
        Assert.Empty(book.Update([Agent(broke: "error")], _now.AddSeconds(1)).Deltas);

        var b2 = new RelaySessionBook();
        b2.Update([new RelaySession("s1", "claude", "p", "/c", "idle", null, null)], _now);
        Assert.Empty(b2.Update([new RelaySession("s1", "claude", "p", "/c", "idle", null, null, "error")], _now.AddSeconds(1)).Deltas);
    }

    [Theory]
    [InlineData("Last run failed", "run_failed")]
    [InlineData("Last run failed: process_lost", "run_failed")]
    [InlineData("Last run timed out", "timed_out")]
    [InlineData("Error", "error")]
    [InlineData("adapter exploded at /Users/x/secret", "error")]
    [InlineData(null, "error")]
    public void The_why_comes_from_the_marks_reason_and_nothing_else_of_it_is_kept(string? reason, string why) =>
        Assert.Equal(why, RelayRoster.WhyOf(reason));

    [Fact]
    public void Project_marks_only_a_Paperclip_row_with_the_Broke_mark_and_names_why()
    {
        var rows = RelayRoster.Project([
            (PaperclipA, new SessionStatus { Source = SessionSource.Paperclip, State = "idle", Mark = PaperclipMark.Broke, MarkReason = "Last run timed out", Title = "Ledger bot" }),
            (PaperclipB, new SessionStatus { Source = SessionSource.Paperclip, State = "idle", Mark = PaperclipMark.Paused, Title = "x" }),
        ]);
        Assert.Equal(["timed_out", null], rows.OrderBy(r => r.SessionId == PaperclipA ? 0 : 1).Select(r => r.Broke).ToArray());
    }

    [Fact]
    public void The_break_rides_push_broke_to_a_phone_that_asked_with_a_sealed_e_naming_why_and_the_ordinary_delta()
    {
        var (link, phone) = PairedForBroke();
        var frames = link.OnScan([Agent(broke: "run_failed")]);
        var frame = Pushed(frames, "broke");
        Assert.Equal(("state", RelayCrypto.Ck(RelayCrypto.CoalesceKey(phone.Base()), PaperclipA)), (J(frame).GetProperty("class").GetString(), CkOf(frame)));
        var delta = phone.OpenFromDesktop(frame);
        Assert.Equal(("delta", PaperclipA, "idle"), (delta.GetProperty("t").GetString(), delta.GetProperty("session_id").GetString(), delta.GetProperty("state").GetString()));
        var e = Encoding.UTF8.GetString(PushSeal.Open(phone.KaKey, J(frame).GetProperty("e").GetString()!)!);
        Assert.Equal("""{"v":1,"t":"broke","session_id":"paperclip:3f2b8c1e-7a44-4d0e-9b61-5c2d8e0f1a77","label":"Ledger bot on ","title":"Ledger bot","why":"run_failed"}""", e);   // this link names no machine
        Assert.Single(frames, f => PushOf(f) == "broke");                     // not sent twice as plain and push
        Assert.Empty(link.OnScan([Agent(broke: "run_failed")]));
    }

    [Fact]
    public void A_phone_that_never_asked_or_turned_it_off_gets_the_delta_with_push_none()
    {
        var (link, phone) = PairedForBroke(wantBroke: false);
        var frames = link.OnScan([Agent(broke: "error")]);
        var plain = Assert.Single(frames);
        Assert.Equal("none", PushOf(plain));
        Assert.Equal(PaperclipA, phone.OpenFromDesktop(plain).GetProperty("session_id").GetString());

        link.OnFrame(phone.Sealed("pair", """{"t":"prefs","done_alerts":"all","broke_alerts":false}"""));
        link.OnScan([Agent()]);
        Assert.Equal("none", PushOf(Assert.Single(link.OnScan([Agent(broke: "error")]))));
        link.OnFrame(phone.Sealed("pair", """{"t":"prefs","done_alerts":"all","broke_alerts":"yes"}"""));    // not the JSON true
        link.OnScan([Agent()]);
        Assert.Equal("none", PushOf(Assert.Single(link.OnScan([Agent(broke: "error")]))));
    }

    [Fact]
    public void An_offline_phone_still_gets_the_push_and_a_roster_scan_does_not_swallow_it()
    {
        var (link, phone) = PairedForBroke();
        link.OnFrame(Presence(phone.Id, false));
        Assert.Single(link.OnScan([Agent(broke: "error")]), f => PushOf(f) == "broke");

        link.OnFrame(Presence(phone.Id, true));
        link.OnScan([Agent(), Agent(id: PaperclipB, title: "New")]);
        var both = link.OnScan([Agent(broke: "error"), Agent(id: PaperclipB, title: "New"), Agent(id: "paperclip:00000000-0000-4000-8000-000000000004", title: "Third")]);
        Assert.Contains(both, f => PushOf(f) == "broke");
        Assert.Contains(both, f => phone.OpenFromDesktop(f).GetProperty("t").GetString() == "roster");
    }

    [Fact]
    public void The_first_scan_after_a_welcome_only_sets_the_baseline_and_an_unchanged_first_scan_does_too()
    {
        var (link, phone) = PairedForBroke();
        link.Disconnected();
        link.OnFrame(Welcome(phone.Id));
        link.OnFrame(Presence(phone.Id, true));
        // Broke on the first scan after the reconnect: a delta, no push (baseline).
        Assert.Equal("none", PushOf(Assert.Single(link.OnScan([Agent(broke: "error")]))));

        // A reconnect whose first scan changes nothing still spends the baseline: the next break pushes.
        link.OnScan([Agent()]);
        link.Disconnected();
        link.OnFrame(Welcome(phone.Id));
        link.OnFrame(Presence(phone.Id, true));
        Assert.Empty(link.OnScan([Agent()]));
        Assert.Contains(link.OnScan([Agent(broke: "error")]), f => PushOf(f) == "broke");
    }

    [Fact]
    public void An_agent_woken_from_this_desktop_in_the_last_thirty_seconds_breaks_without_a_push()
    {
        var woke = new Dictionary<string, DateTimeOffset> { [PaperclipA] = _now.AddSeconds(-10) };
        var (link, phone) = PairedForBroke();
        link.OwnWake = (id, now) => woke.TryGetValue(id, out var at) && now - at <= TimeSpan.FromSeconds(30);
        Assert.Equal("none", PushOf(Assert.Single(link.OnScan([Agent(broke: "error")]))));

        link.OnScan([Agent()]);
        woke[PaperclipA] = _now.AddSeconds(-31);                                           // the control: long ago
        Assert.Contains(link.OnScan([Agent(broke: "error")]), f => PushOf(f) == "broke");
        Assert.NotNull(phone);
    }

    [Fact]
    public void A_relay_that_answers_bad_envelope_to_a_broke_stops_broke_pushes_until_the_next_welcome()
    {
        var (link, phone) = PairedForBroke(true, Agent(), Agent(id: PaperclipB, title: "Other"));
        var frame = Pushed(link.OnScan([Agent(broke: "error"), Agent(id: PaperclipB, title: "Other")]), "broke");
        var id = J(frame).GetProperty("id").GetString()!;

        Assert.Empty(link.OnFrame($$"""{"t":"err","ref":"{{id}}","code":"bad_envelope","message":"push must be none, amber, resolve, done or limit"}"""));

        // Told once: the other agent's break goes as a plain delta, and a repeat of the old refusal is just a log line.
        var next = link.OnScan([Agent(broke: "error"), Agent(id: PaperclipB, title: "Other", broke: "error")]);
        Assert.Equal("none", PushOf(Assert.Single(next)));
        Assert.Equal(PaperclipB, phone.OpenFromDesktop(next[0]).GetProperty("session_id").GetString());
        Assert.Empty(link.OnFrame($$"""{"t":"err","ref":"{{id}}","code":"bad_envelope"}"""));

        // A new connection may be to an upgraded relay, once its first scan has set the baseline.
        link.Disconnected();
        link.OnFrame(Welcome(phone.Id));
        link.OnFrame(Presence(phone.Id, true));
        link.OnScan([Agent(), Agent(id: PaperclipB, title: "Other")]);
        link.OnScan([Agent(), Agent(id: PaperclipB, title: "Other")]);
        Assert.Contains(link.OnScan([Agent(broke: "error"), Agent(id: PaperclipB, title: "Other")]), f => PushOf(f) == "broke");
    }

    [Fact]
    public void A_bad_envelope_for_anything_else_is_only_logged_and_the_in_flight_ids_are_bounded()
    {
        var (link, _) = PairedForBroke();
        Assert.Empty(link.OnFrame("""{"t":"err","ref":"01JNOTBROKE000000000000000","code":"bad_envelope"}"""));
        var ok = link.OnScan([Agent(broke: "error")]);                                       // still pushing: that did not latch
        Assert.Contains(ok, f => PushOf(f) == "broke");

    }

    [Fact]
    public void Seventy_breaks_in_flight_forget_the_oldest_so_a_late_refusal_of_the_first_latches_nothing()
    {
        var (link2, _) = PairedForBroke(true, Agent());
        var ids = new List<string>();
        for (var i = 0; i < 70; i++)
        {
            var id = $"paperclip:00000000-0000-4000-8000-{i:D12}";
            link2.OnScan([Agent(), Agent(id: id)]);
            ids.Add(J(Pushed(link2.OnScan([Agent(), Agent(id: id, broke: "error")]), "broke")).GetProperty("id").GetString()!);
        }
        link2.OnFrame($$"""{"t":"err","ref":"{{ids[0]}}","code":"bad_envelope"}""");
        link2.OnScan([Agent(), Agent(id: "paperclip:00000000-0000-4000-8000-000000000999")]);
        Assert.Contains(link2.OnScan([Agent(), Agent(id: "paperclip:00000000-0000-4000-8000-000000000999", broke: "error")]), f => PushOf(f) == "broke");
        link2.OnFrame($$"""{"t":"err","ref":"{{ids[69]}}","code":"bad_envelope"}""");
        link2.OnScan([Agent(), Agent(id: "paperclip:00000000-0000-4000-8000-000000000999")]);
        Assert.Equal("none", PushOf(Assert.Single(link2.OnScan([Agent(), Agent(id: "paperclip:00000000-0000-4000-8000-000000000999", broke: "timed_out")]))));
    }

    [Fact]
    public void BrokeE_is_canonical_and_names_the_machine_when_the_agent_has_no_title()
    {
        Assert.Equal("""{"v":1,"t":"broke","session_id":"paperclip:x","label":"Ledger bot on Mac","title":"Ledger bot","why":"timed_out"}""", RelayLink.BrokeE("paperclip:x", "Ledger bot", "Mac", "timed_out"));
        Assert.Equal("""{"v":1,"t":"broke","session_id":"paperclip:x","label":"Mac","title":"","why":"error"}""", RelayLink.BrokeE("paperclip:x", "", "Mac", "error"));
    }

    // The contract's own firing table (frames-v16-vectors.json broke.transitions), replayed through a real link:
    // one poll is one scan, "reset" is a reconnect before that scan, a missing row is a row that is not there.
    public static IEnumerable<object[]> FiringCases() =>
        Enumerable.Range(0, PaperclipVectorTests.V["broke"]!["transitions"]!.AsArray().Count).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(FiringCases))]
    public void Every_firing_case_in_the_vectors_fires_exactly_where_the_contract_says(int index)
    {
        var t = PaperclipVectorTests.V["broke"]!["transitions"]![index]!;
        {
            var (link, phone) = PairedForBroke(true, new RelaySession("none", "x", "", "", "idle", null, null));
            var fired = new List<int>();
            var i = 0;
            foreach (var p in t!["polls"]!.AsArray())
            {
                if (p!["reset"]!.GetValue<bool>())
                {
                    link.Disconnected();
                    link.OnFrame(Welcome(phone.Id));
                    link.OnFrame(Presence(phone.Id, true));
                }
                var mark = p["mark"]?.GetValue<string>();
                var rows = new List<RelaySession>();
                if (mark is not null && p["roster"]!.GetValue<bool>()) rows.Add(Agent(broke: mark == "Broke" ? "error" : null));
                else rows.Add(Agent(id: PaperclipB, title: "Keeper"));          // the roster is never empty; this one is somebody else
                var frames = link.OnScan(rows);
                if (frames.Any(f => PushOf(f) == "broke")) fired.Add(i);
                i++;
            }
            Assert.Equal(t["fires"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray(), fired.ToArray());
        }
    }
}
