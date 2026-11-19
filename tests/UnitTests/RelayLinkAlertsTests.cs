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
    public void A_prefs_frame_under_the_pair_key_sets_the_phone_choice_and_a_malformed_one_keeps_the_last()
    {
        var (link, _, phone, notes, _) = Paired();
        link.OnFrame(phone.Sealed("pair", """{"t":"prefs","done_alerts":"leads","amber_members":false}"""));
        Assert.Contains(notes, n => n.Contains("done_alerts=Leads") && n.Contains("amber_members=False"));

        link.OnFrame(phone.Sealed("pair", """{"t":"prefs","done_alerts":"sometimes"}"""));
        Assert.Contains(notes, n => n.Contains("prefs") && n.Contains("malformed"));
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
}
