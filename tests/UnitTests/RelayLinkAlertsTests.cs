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
}
