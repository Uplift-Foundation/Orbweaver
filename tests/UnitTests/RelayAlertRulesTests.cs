using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-358: the desktop's decision on which relay alerts a phone gets for
// agent-team members. Each arm of the rule has a case, and the leads-only
// rule is pinned against the control (all) so a broken filter cannot pass by
// suppressing everything.
public class RelayAlertRulesTests
{
    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Leads_withholds_the_done_push_from_a_member_and_keeps_it_for_a_lead_and_a_solo_session()
    {
        var leads = new AlertPrefs(DoneAlerts.Leads, AmberMembers: true);
        Assert.False(RelayAlertRules.PushDone(leads, member: true));
        Assert.True(RelayAlertRules.PushDone(leads, member: false));
    }

    [Fact]
    public void Off_withholds_every_done_push()
    {
        var off = new AlertPrefs(DoneAlerts.Off, AmberMembers: true);
        Assert.False(RelayAlertRules.PushDone(off, member: true));
        Assert.False(RelayAlertRules.PushDone(off, member: false));
    }

    [Fact]
    public void All_is_unchanged_from_today_for_members_and_solo_sessions()
    {
        var all = AlertPrefs.Default;
        Assert.Equal(DoneAlerts.All, all.DoneAlerts);
        Assert.True(RelayAlertRules.PushDone(all, member: true));
        Assert.True(RelayAlertRules.PushDone(all, member: false));
    }

    [Fact]
    public void An_orphaned_member_is_a_lead_because_its_parent_is_no_longer_in_the_roster()
    {
        Assert.False(RelayAlertRules.IsMember("gone-lead", id => id == "other"));
        Assert.True(RelayAlertRules.IsMember("lead", id => id == "lead"));
    }

    [Fact]
    public void A_session_with_no_parent_is_never_a_member()
    {
        Assert.False(RelayAlertRules.IsMember(null, _ => true));
        Assert.False(RelayAlertRules.IsMember("", _ => true));
    }

    [Fact]
    public void Amber_from_a_member_is_unaffected_by_default_and_withheld_only_when_its_toggle_is_off()
    {
        Assert.True(RelayAlertRules.PushAmber(AlertPrefs.Default, member: true));
        var quiet = new AlertPrefs(DoneAlerts.Leads, AmberMembers: false);
        Assert.False(RelayAlertRules.PushAmber(quiet, member: true));
        Assert.True(RelayAlertRules.PushAmber(quiet, member: false));
    }

    [Fact]
    public void Prefs_are_per_phone_so_two_phones_with_different_choices_each_keep_their_own()
    {
        var a = AlertPrefs.Parse(Body("""{"t":"prefs","done_alerts":"leads"}"""));
        var b = AlertPrefs.Parse(Body("""{"t":"prefs","done_alerts":"off"}"""));
        Assert.Equal(DoneAlerts.Leads, a!.Value.DoneAlerts);
        Assert.Equal(DoneAlerts.Off, b!.Value.DoneAlerts);
        Assert.False(RelayAlertRules.PushDone(a.Value, member: true));
        Assert.False(RelayAlertRules.PushDone(b.Value, member: false));
    }

    [Fact]
    public void Parse_reads_each_mode_and_the_amber_toggle()
    {
        Assert.Equal(DoneAlerts.All, AlertPrefs.Parse(Body("""{"done_alerts":"all"}"""))!.Value.DoneAlerts);
        Assert.Equal(DoneAlerts.Leads, AlertPrefs.Parse(Body("""{"done_alerts":"leads"}"""))!.Value.DoneAlerts);
        Assert.Equal(DoneAlerts.Off, AlertPrefs.Parse(Body("""{"done_alerts":"off"}"""))!.Value.DoneAlerts);
        Assert.True(AlertPrefs.Parse(Body("""{"done_alerts":"leads"}"""))!.Value.AmberMembers);
        Assert.False(AlertPrefs.Parse(Body("""{"done_alerts":"leads","amber_members":false}"""))!.Value.AmberMembers);
    }

    [Theory]
    [InlineData("""{"t":"prefs"}""")]
    [InlineData("""{"done_alerts":"sometimes"}""")]
    [InlineData("""{"done_alerts":5}""")]
    public void Parse_ignores_a_missing_or_unknown_mode_rather_than_guessing(string json)
    {
        Assert.Null(AlertPrefs.Parse(Body(json)));
    }

    [Fact]
    public void Summary_names_the_members_since_the_lead_last_alerted_and_is_silent_at_zero()
    {
        Assert.Equal("proj on mac", RelayAlertRules.WithMembersSince("proj on mac", 0));
        Assert.Equal("proj on mac · 1 member finished since", RelayAlertRules.WithMembersSince("proj on mac", 1));
        Assert.Equal("proj on mac · 3 members finished since", RelayAlertRules.WithMembersSince("proj on mac", 3));
    }

    // The file's spelling is the frame's, and each mode survives the round trip through it.
    [Theory]
    [InlineData((int)DoneAlerts.All, "all")]
    [InlineData((int)DoneAlerts.Leads, "leads")]
    [InlineData((int)DoneAlerts.Off, "off")]
    public void A_stored_choice_reads_back_as_the_one_that_was_written(int mode, string name)
    {
        var prefs = new AlertPrefs((DoneAlerts)mode, AmberMembers: false);
        Assert.Equal(name, prefs.DoneAlertsName);
        Assert.Equal(prefs, AlertPrefs.FromStored(prefs.DoneAlertsName, prefs.AmberMembers));
    }

    [Fact]
    public void A_stored_choice_with_no_mode_or_an_unknown_one_is_no_choice_and_a_missing_toggle_is_on()
    {
        Assert.Null(AlertPrefs.FromStored(null, null));
        Assert.Null(AlertPrefs.FromStored(null, false));
        Assert.Null(AlertPrefs.FromStored("sometimes", true));
        Assert.Equal(new AlertPrefs(DoneAlerts.Leads, AmberMembers: true), AlertPrefs.FromStored("leads", null));
    }
}
