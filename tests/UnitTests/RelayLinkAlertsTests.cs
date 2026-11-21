using System.Text;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-358, through the relay link: the phone's prefs frame is read under the
// pair key, and a team member's finish is withheld from the push while its
// delta still goes out, and the lead's next done alert names the members that
// finished since. The rules themselves are in RelayAlertRulesTests.
public partial class RelayLinkTests
{
    private static RelaySession Member(string id, string state, string parent) => new(id, "claude", "proj", "/c", state, parent, null);

    [Fact]
    public void A_prefs_frame_under_the_pair_key_is_acknowledged_and_a_malformed_one_is_refused_and_keeps_the_last()
    {
        var (link, _, phone, notes, _) = Paired();
        var ok = link.OnFrame(phone.Sealed("pair", """{"t":"prefs","done_alerts":"leads","amber_members":false}"""));
        var okReply = phone.OpenFromDesktop(Assert.Single(ok));
        Assert.Equal(("prefs.ok", "y"), (okReply.GetProperty("t").GetString(), okReply.GetProperty("ref").GetString()));
        Assert.Contains(notes, n => n.Contains("done_alerts=Leads") && n.Contains("amber_members=False"));

        var bad = link.OnFrame(phone.Sealed("pair", """{"t":"prefs","done_alerts":"sometimes"}"""));
        var badReply = phone.OpenFromDesktop(Assert.Single(bad));
        Assert.Equal(("prefs.err", "malformed"), (badReply.GetProperty("t").GetString(), badReply.GetProperty("code").GetString()));
        Assert.Contains(notes, n => n.Contains("malformed"));
    }

    [Fact]
    public void Under_leads_a_member_finish_rides_the_delta_without_a_push_and_the_lead_alert_counts_it()
    {
        var (link, _, phone, _, _) = Paired();
        link.OnFrame(phone.Sealed("pair", """{"t":"prefs","done_alerts":"leads"}"""));

        link.OnScan([Row("s1"), Member("m1", "idle", "s1")]);                          // roster
        link.OnScan([Row("s1"), Member("m1", "generating", "s1")]);

        // The member finishes: its delta is numbered and sent, but no done push.
        var memberDone = link.OnScan([Row("s1"), Member("m1", "idle", "s1")]);
        var memberFrame = Assert.Single(memberDone);
        Assert.Equal("none", J(memberFrame).GetProperty("push").GetString());
        Assert.Equal("delta", phone.OpenFromDesktop(memberFrame).GetProperty("t").GetString());

        // The lead goes generating, then finishes: its one done alert counts the member.
        link.OnScan([Row("s1", "generating"), Member("m1", "idle", "s1")]);
        var leadDone = link.OnScan([Row("s1", "idle"), Member("m1", "idle", "s1")]);
        var push = leadDone.Single(f => J(f).GetProperty("push").GetString() == "done");
        var e = J(push).GetProperty("e").GetString()!;
        var label = JsonDocument.Parse(Encoding.UTF8.GetString(PushSeal.Open(phone.KaKey, e)!)).RootElement.GetProperty("label").GetString();
        Assert.EndsWith("· 1 member finished since", label);
    }

    [Fact]
    public void Under_all_a_member_finish_still_pushes_as_it_always_has()
    {
        var (link, _, _, _, _) = Paired();
        link.OnScan([Row("s1"), Member("m1", "idle", "s1")]);
        link.OnScan([Row("s1"), Member("m1", "generating", "s1")]);
        var done = link.OnScan([Row("s1"), Member("m1", "idle", "s1")]);
        Assert.Contains(done, f => J(f).GetProperty("push").GetString() == "done");
    }

    [Fact]
    public void Under_leads_an_amber_from_a_member_loses_its_push_only_when_its_toggle_is_off()
    {
        var (link, _, phone, _, _) = Paired();
        link.OnFrame(phone.Sealed("pair", """{"t":"prefs","done_alerts":"leads","amber_members":false}"""));
        link.OnScan([Row("s1"), Member("m1", "idle", "s1")]);
        var quiet = link.SendPrompt("req-1", "m1", "Allow?", "plain");
        var prompt = Assert.Single(quiet, f => J(f).GetProperty("class").GetString() == "action");
        Assert.False(J(prompt).TryGetProperty("e", out _), "a member's prompt with its toggle off carries no alert");

        link.OnFrame(phone.Sealed("pair", """{"t":"prefs","done_alerts":"leads","amber_members":true}"""));
        var loud = link.SendPrompt("req-2", "m1", "Allow?", "plain");
        var alerting = Assert.Single(loud, f => J(f).GetProperty("class").GetString() == "action");
        Assert.True(J(alerting).TryGetProperty("e", out _), "with the toggle on, the member's prompt alerts as before");
    }

    [Fact]
    public void A_lead_alert_counts_its_members_once_and_the_next_one_starts_from_zero()
    {
        var (link, _, phone, _, _) = Paired();
        link.OnFrame(phone.Sealed("pair", """{"t":"prefs","done_alerts":"leads"}"""));
        link.OnScan([Row("s1"), Member("m1", "idle", "s1")]);
        link.OnScan([Row("s1"), Member("m1", "generating", "s1")]);
        link.OnScan([Row("s1"), Member("m1", "idle", "s1")]);
        link.OnScan([Row("s1", "generating"), Member("m1", "idle", "s1")]);
        var first = link.OnScan([Row("s1", "idle"), Member("m1", "idle", "s1")]);
        Assert.EndsWith("· 1 member finished since", DoneLabel(link, phone, first));

        link.OnScan([Row("s1", "generating"), Member("m1", "idle", "s1")]);
        var second = link.OnScan([Row("s1", "idle"), Member("m1", "idle", "s1")]);
        Assert.DoesNotContain("finished since", DoneLabel(link, phone, second));
    }

    [Fact]
    public void Members_finishing_while_off_are_counted_for_the_leads_next_alert()
    {
        var (link, _, phone, _, _) = Paired();
        link.OnFrame(phone.Sealed("pair", """{"t":"prefs","done_alerts":"off"}"""));
        link.OnScan([Row("s1"), Member("m1", "idle", "s1"), Member("m2", "idle", "s1")]);
        link.OnScan([Row("s1"), Member("m1", "generating", "s1"), Member("m2", "generating", "s1")]);
        link.OnScan([Row("s1"), Member("m1", "idle", "s1"), Member("m2", "idle", "s1")]);

        link.OnFrame(phone.Sealed("pair", """{"t":"prefs","done_alerts":"leads"}"""));
        link.OnScan([Row("s1", "generating"), Member("m1", "idle", "s1"), Member("m2", "idle", "s1")]);
        var leadDone = link.OnScan([Row("s1", "idle"), Member("m1", "idle", "s1"), Member("m2", "idle", "s1")]);
        Assert.EndsWith("· 2 members finished since", DoneLabel(link, phone, leadDone));
    }

    private static string DoneLabel(RelayLink link, FakePhone phone, IReadOnlyList<string> frames)
    {
        var push = frames.Single(f => J(f).GetProperty("push").GetString() == "done");
        var e = J(push).GetProperty("e").GetString()!;
        return JsonDocument.Parse(Encoding.UTF8.GetString(PushSeal.Open(phone.KaKey, e)!)).RootElement.GetProperty("label").GetString()!;
    }

    [Fact]
    public void A_withheld_members_own_count_does_not_resurface_when_it_later_alerts()
    {
        // m2 is a member of m1, which is a member of s1. Under "leads" m2 and m1 both finish withheld, so m1's
        // own count (m2's finish) must be dropped: m1 never sends a summary. Switching to "all" then has m1
        // alert itself, and that alert must not carry the count it earned while withheld.
        var (link, _, phone, _, _) = Paired();
        link.OnFrame(phone.Sealed("pair", """{"t":"prefs","done_alerts":"leads"}"""));
        link.OnScan([Row("s1"), Member("m1", "idle", "s1"), Member("m2", "idle", "m1")]);
        link.OnScan([Row("s1"), Member("m1", "idle", "s1"), Member("m2", "generating", "m1")]);
        link.OnScan([Row("s1"), Member("m1", "idle", "s1"), Member("m2", "idle", "m1")]);
        link.OnScan([Row("s1"), Member("m1", "generating", "s1"), Member("m2", "idle", "m1")]);
        link.OnScan([Row("s1"), Member("m1", "idle", "s1"), Member("m2", "idle", "m1")]);

        link.OnFrame(phone.Sealed("pair", """{"t":"prefs","done_alerts":"all"}"""));
        link.OnScan([Row("s1"), Member("m1", "generating", "s1"), Member("m2", "idle", "m1")]);
        var m1Alert = link.OnScan([Row("s1"), Member("m1", "idle", "s1"), Member("m2", "idle", "m1")]);
        Assert.DoesNotContain("finished since", DoneLabel(link, phone, m1Alert));
    }

    // ---- the choice outlives the process -----------------------------------------------------------------------
    // The phone resends prefs only when it is running to see this desktop come back. A suspended iPhone is not,
    // so the desktop has to remember: each case below restarts over the same files and sends no new prefs frame.

    private (RelayLink Link, List<string> Notes) Restarted(FakePhone phone)
    {
        var (restarted, _, notes) = New(new RelayIdentity(_dir, _store, () => _now));
        restarted.OnFrame(Welcome(phone.Id));
        restarted.OnFrame(Presence(phone.Id, true));
        return (restarted, notes);
    }

    private static IReadOnlyList<string> MemberFinishes(RelayLink link)
    {
        link.OnScan([Row("s1"), Member("m1", "idle", "s1")]);
        link.OnScan([Row("s1"), Member("m1", "generating", "s1")]);
        return link.OnScan([Row("s1"), Member("m1", "idle", "s1")]);
    }

    [Fact]
    public void After_a_restart_a_member_finish_is_still_withheld_with_no_new_prefs_frame()
    {
        var (_, _, phone, _, _) = Paired();
        var (first, _) = Restarted(phone);                                       // control: before any prefs, a restart pushes
        Assert.Contains(MemberFinishes(first), f => J(f).GetProperty("push").GetString() == "done");

        first.OnFrame(phone.Sealed("pair", """{"t":"prefs","done_alerts":"leads","amber_members":false}"""));
        Assert.Equal(new AlertPrefs(DoneAlerts.Leads, AmberMembers: false), new RelayIdentity(_dir, _store).PhoneById(phone.Id)!.Alerts);

        var (second, _) = Restarted(phone);
        var done = Assert.Single(MemberFinishes(second));
        Assert.Equal("none", J(done).GetProperty("push").GetString());
        Assert.Equal("delta", phone.OpenFromDesktop(done).GetProperty("t").GetString());

        // The other half of the frame came back too: the member's prompt carries no alert.
        var prompt = Assert.Single(second.SendPrompt("req-1", "m1", "Allow?", "plain"), f => J(f).GetProperty("class").GetString() == "action");
        Assert.False(J(prompt).TryGetProperty("e", out _));
    }

    [Fact]
    public void A_phone_that_never_sent_prefs_has_none_on_disk_and_a_refused_frame_writes_none()
    {
        var (link, identity, phone, _, _) = Paired();
        Assert.Null(identity.PhoneById(phone.Id)!.Alerts);
        link.OnFrame(phone.Sealed("pair", """{"t":"prefs","done_alerts":"sometimes"}"""));
        Assert.Null(new RelayIdentity(_dir, _store).PhoneById(phone.Id)!.Alerts);
        Assert.DoesNotContain("done_alerts", File.ReadAllText(Path.Combine(_dir, RelayIdentity.FileName)));
    }

    [Fact]
    public void Prefs_that_cannot_be_written_are_acknowledged_and_hold_only_until_the_restart()
    {
        var (link, identity, phone, notes, _) = Paired();
        var moved = Block();
        var reply = phone.OpenFromDesktop(Assert.Single(link.OnFrame(phone.Sealed("pair", """{"t":"prefs","done_alerts":"leads"}"""))));
        Unblock(moved);
        Assert.Equal("prefs.ok", reply.GetProperty("t").GetString());
        Assert.Contains(notes, n => n.Contains("not saved"));
        Assert.Equal("none", J(Assert.Single(MemberFinishes(link))).GetProperty("push").GetString());   // in force now

        var (restarted, _) = Restarted(phone);                                                          // and gone after
        Assert.Contains(MemberFinishes(restarted), f => J(f).GetProperty("push").GetString() == "done");
        Assert.False(identity.SaveAlerts("ph_nobody", AlertPrefs.Default));
    }
}
