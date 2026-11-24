using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-421 (orb visuals): a Paperclip orb built from hand-made SessionStatus values and
// asserted on what a person would have seen. No Show() and no click: a click on a
// Paperclip orb ends in CloudSessionLink.Open, a real browser, which a headless run
// must not launch. The rule that decides where a click goes is ClickRouting's (and is
// covered with the wiring); what each menu row offers is PaperclipOrbDecisionTests.
//
// In the Settings collection because constructing an orb reads settings, and
// PaperclipSessions.Link is process-wide, which these cases set and put back.
[Collection("Settings")]
public class PaperclipOrbTests : IDisposable
{
    public void Dispose() => PaperclipSessions.SetLinkForTests(PaperclipLink.Off, "Paperclip is switched off.");

    private static SessionStatus Agent(
        string state = "idle", OrbPresence presence = OrbPresence.Present, PaperclipMark mark = PaperclipMark.None,
        int? budget = null, int folded = 0, string? foldedNames = null, string[]? hover = null,
        string url = "http://127.0.0.1:3100/CB/agents/a", string? avatarFile = null) => new()
    {
        Source = SessionSource.Paperclip,
        State = state,
        Title = "Atlas",
        Cwd = "",
        Presence = presence,
        Mark = mark,
        BudgetPercent = budget,
        FoldedCount = folded,
        FoldedNames = foldedNames,
        HoverLines = hover ?? [],
        Url = url,
        PaperclipAvatarFile = avatarFile,
    };

    private static OrbWindow Orb(SessionStatus status)
    {
        var orb = new OrbWindow("paperclip:" + Guid.NewGuid());
        orb.UpdateFrom(status);
        return orb;
    }

    // The tooltip's lines, as drawn: the title, then the body split at its line breaks.
    private static List<string> Bubble(OrbWindow orb)
    {
        var stack = Assert.IsType<StackPanel>(orb.CurrentThoughtBubble);
        var content = Assert.IsType<StackPanel>(Assert.IsType<Border>(stack.Children[0]).Child);
        return content.Children.OfType<TextBlock>().SelectMany(t => (t.Text ?? "").Split('\n')).ToList();
    }

    // ---- the badge and what is not on the orb -----------------------------------

    [AvaloniaFact]
    public void Every_paperclip_orb_wears_the_paperclip_badge_and_no_kind_badge()
    {
        foreach (var state in new[] { "idle", "generating", "waiting" })
        {
            var orb = Orb(Agent(state));
            Assert.Equal("paperclip", orb.CliMarkName);
            Assert.Equal("#5B5BD6", orb.CliMarkFill);
            Assert.True(orb.CliMarkVisible);
            Assert.False(orb.FindControl<Border>("KindBadge")!.IsVisible);
            Assert.Null(orb.KindLabel);
        }
    }

    [AvaloniaFact]
    public void An_asleep_agent_is_dimmed_and_an_amber_one_asks()
    {
        var asleep = Orb(Agent(presence: OrbPresence.Parked));
        Assert.Equal(OrbPresence.Parked, asleep.Presence);
        Assert.False(asleep.PresenceBadgeVisible);            // dimmed, with no corner mark of its own

        var amber = Orb(Agent("waiting", OrbPresence.NeedsInput));
        Assert.Equal("?", amber.FindControl<TextBlock>("PresenceGlyph")!.Text);
        Assert.True(amber.PresenceBadgeVisible);
    }

    // ---- the marks -----------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(PaperclipMark.Broke, "broke")]
    [InlineData(PaperclipMark.Paused, "paused")]
    [InlineData(PaperclipMark.OverBudget, "over budget")]
    public void Each_mark_fills_the_presence_corner_with_a_drawn_mark_and_no_typed_glyph(PaperclipMark mark, string name)
    {
        var orb = Orb(Agent(mark: mark));
        Assert.Equal(name, orb.PresenceMarkName);
        Assert.True(orb.PresenceMarkVisible);
        Assert.True(orb.PresenceBadgeVisible);
        var glyph = orb.FindControl<TextBlock>("PresenceGlyph")!;
        Assert.False(glyph.IsVisible);
        Assert.True(string.IsNullOrEmpty(glyph.Text));
    }

    [AvaloniaFact]
    public void No_mark_and_no_presence_leaves_the_corner_empty()
    {
        var orb = Orb(Agent());
        Assert.Null(orb.PresenceMarkName);
        Assert.False(orb.PresenceMarkVisible);
        Assert.False(orb.PresenceBadgeVisible);
    }

    [AvaloniaFact]
    public void The_question_mark_wins_the_corner_and_the_mark_returns_when_it_goes()
    {
        var orb = new OrbWindow("paperclip:" + Guid.NewGuid());

        orb.UpdateFrom(Agent("waiting", OrbPresence.NeedsInput, PaperclipMark.Broke));
        Assert.Equal("?", orb.FindControl<TextBlock>("PresenceGlyph")!.Text);
        Assert.Null(orb.PresenceMarkName);
        Assert.False(orb.PresenceMarkVisible);

        // The answer arrived: presence clears, and the mark it was hiding is drawn.
        orb.UpdateFrom(Agent("idle", OrbPresence.Present, PaperclipMark.Broke));
        Assert.Equal("broke", orb.PresenceMarkName);
        Assert.True(orb.PresenceMarkVisible);
        Assert.False(orb.FindControl<TextBlock>("PresenceGlyph")!.IsVisible);

        // And the mark cleared on the next good run.
        orb.UpdateFrom(Agent());
        Assert.Null(orb.PresenceMarkName);
        Assert.False(orb.PresenceBadgeVisible);
    }

    [AvaloniaFact]
    public void A_mark_never_appears_on_an_orb_that_is_not_a_paperclip_agent()
    {
        // A stale Mark on a reused status object: the corner is the session's own.
        var other = new SessionStatus { Source = SessionSource.ClaudeCode, State = "idle", Cwd = "/w", Mark = PaperclipMark.Broke, FoldedCount = 3, BudgetPercent = null };
        var orb = Orb(other);
        Assert.Null(orb.PresenceMarkName);
        Assert.False(orb.PresenceMarkVisible);
        Assert.False(orb.FoldedPillVisible);
        Assert.False(orb.OpenPaperclipVisible);
    }

    // ---- the budget ring -----------------------------------------------------

    [AvaloniaTheory]
    [InlineData(10, AccountOrbWindow.CalmHex)]
    [InlineData(75, AccountOrbWindow.WarnHex)]
    [InlineData(100, AccountOrbWindow.DangerHex)]
    public void A_reported_budget_draws_the_ring_in_the_account_orbs_colour_for_that_percent(int percent, string colour)
    {
        var orb = Orb(Agent(budget: percent));
        Assert.Equal(colour, orb.ContextRingColour);
        Assert.True(orb.FindControl<Canvas>("ContextRingLayer")!.IsVisible);
    }

    [AvaloniaFact]
    public void No_budget_draws_no_ring_and_clearing_it_removes_the_ring()
    {
        var orb = new OrbWindow("paperclip:" + Guid.NewGuid());
        orb.UpdateFrom(Agent(budget: null));
        Assert.Null(orb.ContextRingColour);
        Assert.False(orb.FindControl<Canvas>("ContextRingLayer")!.IsVisible);

        orb.UpdateFrom(Agent(budget: 40));
        Assert.NotNull(orb.ContextRingColour);
        orb.UpdateFrom(Agent(budget: null));
        Assert.Null(orb.ContextRingColour);
    }

    // The budget ring is the context ring's layer, gated by source so the two are never both
    // drawn: a Paperclip row draws only its budget, every other row only its context percent.
    [AvaloniaFact]
    public void A_paperclip_row_draws_its_budget_and_never_a_context_percent()
    {
        var both = Agent(budget: 100);
        both.ContextPercent = 10;
        Assert.Equal(AccountOrbWindow.DangerHex, Orb(both).ContextRingColour);

        var contextOnly = Agent(budget: null);
        contextOnly.ContextPercent = 10;
        Assert.Null(Orb(contextOnly).ContextRingColour);
    }

    [AvaloniaFact]
    public void A_non_paperclip_row_draws_its_context_percent_and_never_a_stale_budget()
    {
        var cloud = new SessionStatus { Source = SessionSource.ClaudeCloud, State = "idle", Cwd = "", Title = "c", ContextPercent = 10, BudgetPercent = 100 };
        Assert.Equal(AccountOrbWindow.CalmHex, Orb(cloud).ContextRingColour);

        var budgetOnly = new SessionStatus { Source = SessionSource.ClaudeCode, State = "idle", Cwd = "/w", BudgetPercent = 100 };
        Assert.Null(Orb(budgetOnly).ContextRingColour);
        Assert.Single(Orb(cloud).FindControl<Canvas>("ContextRingLayer")!.Children.OfType<Avalonia.Controls.Shapes.Path>());
    }

    [AvaloniaTheory]
    [InlineData("waiting", OrbPresence.NeedsInput)]
    [InlineData("idle", OrbPresence.Present)]
    public void An_amber_or_broke_orb_with_a_budget_draws_both_its_ring_and_its_state(string state, OrbPresence presence)
    {
        var orb = Orb(Agent(state, presence, state == "idle" ? PaperclipMark.Broke : PaperclipMark.None, budget: 60));
        Assert.Equal(AccountOrbWindow.WarnHex, orb.ContextRingColour);
        Assert.Equal(1.0, orb.BodyOpacity);
    }

    // ---- dimming: only asleep and paused fade ----------------------------------------

    [AvaloniaFact]
    public void Asleep_and_paused_dim_and_amber_broke_and_over_budget_do_not()
    {
        Assert.Equal(0.45, Orb(Agent(presence: OrbPresence.Parked)).BodyOpacity, 6);                    // asleep
        Assert.Equal(0.45, Orb(Agent(presence: OrbPresence.Parked, mark: PaperclipMark.Paused)).BodyOpacity, 6);
        Assert.Equal(0.45, Orb(Agent(mark: PaperclipMark.Paused)).BodyOpacity, 6);                      // paused, not parked

        Assert.Equal(1.0, Orb(Agent("waiting", OrbPresence.NeedsInput)).BodyOpacity);                   // needs you
        Assert.Equal(1.0, Orb(Agent("waiting", OrbPresence.NeedsInput, PaperclipMark.Paused)).BodyOpacity);
        Assert.Equal(1.0, Orb(Agent(mark: PaperclipMark.Broke)).BodyOpacity);                           // broke
        Assert.Equal(1.0, Orb(Agent(presence: OrbPresence.Parked, mark: PaperclipMark.Broke)).BodyOpacity);
        Assert.Equal(1.0, Orb(Agent(mark: PaperclipMark.OverBudget)).BodyOpacity);                      // over budget
        Assert.Equal(1.0, Orb(Agent(presence: OrbPresence.Parked, mark: PaperclipMark.OverBudget)).BodyOpacity);
        Assert.Equal(1.0, Orb(Agent("generating")).BodyOpacity);                                        // working
    }

    [AvaloniaFact]
    public void The_pause_mark_stays_at_full_strength_on_a_dimmed_orb()
    {
        var orb = Orb(Agent(presence: OrbPresence.Parked, mark: PaperclipMark.Paused));
        Assert.Equal(0.45, orb.BodyOpacity, 6);
        var badge = orb.FindControl<Border>("PresenceBadge")!;
        Assert.True(badge.IsVisible);
        Assert.Equal(1.0, badge.Opacity);
        Assert.Equal(1.0, orb.FindControl<Grid>("Root")!.Opacity);
    }

    [AvaloniaFact]
    public void A_mark_arriving_or_clearing_on_a_parked_agent_moves_the_dimming_with_it()
    {
        var orb = new OrbWindow("paperclip:" + Guid.NewGuid());
        orb.UpdateFrom(Agent(presence: OrbPresence.Parked));
        Assert.Equal(0.45, orb.BodyOpacity, 6);
        orb.UpdateFrom(Agent(presence: OrbPresence.Parked, mark: PaperclipMark.Broke));    // presence unchanged
        Assert.Equal(1.0, orb.BodyOpacity);
        Assert.Equal(1.0, orb.FindControl<Border>("PresenceBadge")!.Opacity);
        orb.UpdateFrom(Agent(presence: OrbPresence.Parked));
        Assert.Equal(0.45, orb.BodyOpacity, 6);
    }

    // The control: a parked or needs-input LOCAL orb dims exactly as it always did, whole window.
    [AvaloniaTheory]
    [InlineData(OrbPresence.Parked)]
    [InlineData(OrbPresence.NeedsInput)]
    public void A_local_orb_dims_the_whole_window_as_before(OrbPresence presence)
    {
        var orb = Orb(new SessionStatus { Source = SessionSource.ClaudeCode, State = "idle", Cwd = "/w", Presence = presence });
        Assert.Equal(0.45, orb.FindControl<Grid>("Root")!.Opacity, 6);
        Assert.Equal(0.45, orb.BodyOpacity, 6);
        Assert.Equal(1.0, orb.FindControl<Border>("PresenceBadge")!.Opacity);   // untouched: the window carries the dim
    }

    // ---- the folded count ----------------------------------------------------

    [AvaloniaFact]
    public void A_lead_with_folded_agents_wears_the_count_and_one_without_wears_none()
    {
        var lead = new OrbWindow("paperclip:" + Guid.NewGuid());
        lead.UpdateFrom(Agent(folded: 3, foldedNames: "Ann, Bo, Cy"));
        Assert.True(lead.FoldedPillVisible);
        Assert.Equal("+3", lead.FoldedPillLabel);
        Assert.Equal("+3", lead.FindControl<TextBlock>("FoldedPillText")!.Text);

        lead.UpdateFrom(Agent(folded: 0));
        Assert.False(lead.FoldedPillVisible);
        Assert.Null(lead.FoldedPillLabel);
    }

    // ---- the hover -----------------------------------------------------------

    [AvaloniaFact]
    public void The_hover_says_every_line_in_the_order_given_with_the_folded_names_last()
    {
        var orb = Orb(Agent("waiting", OrbPresence.NeedsInput, PaperclipMark.Paused,
            hover: ["Hire: QA engineer", "Paused: over budget", "CB-12 Fix the build", "calling Bash", "running for 3 m", "last run succeeded 2 h ago", "$0.26 of $50.00 this month"],
            folded: 2, foldedNames: "Ann, Bo"));

        Assert.Equal(
            ["Atlas · needs input", "Hire: QA engineer", "Paused: over budget", "CB-12 Fix the build", "calling Bash",
             "running for 3 m", "last run succeeded 2 h ago", "$0.26 of $50.00 this month", "+2 asleep: Ann, Bo"],
            Bubble(orb));
    }

    [AvaloniaFact]
    public void An_agent_with_nothing_to_say_shows_its_name_alone()
    {
        Assert.Equal(["Atlas"], Bubble(Orb(Agent())));
    }

    [AvaloniaFact]
    public void The_hover_is_not_rebuilt_while_nothing_changed_and_is_when_a_line_does()
    {
        var orb = new OrbWindow("paperclip:" + Guid.NewGuid());
        orb.UpdateFrom(Agent(hover: ["running for 3 m"]));
        var first = orb.CurrentThoughtBubble;
        orb.UpdateFrom(Agent(hover: ["running for 3 m"]));
        Assert.Same(first, orb.CurrentThoughtBubble);       // an open tooltip is not torn down on every poll

        orb.UpdateFrom(Agent(hover: ["running for 4 m"]));
        Assert.NotSame(first, orb.CurrentThoughtBubble);
    }

    // ---- the menu ------------------------------------------------------------

    [AvaloniaFact]
    public void An_ordinary_agent_offers_open_in_paperclip_and_nothing_to_review_or_reconnect()
    {
        PaperclipSessions.SetLinkForTests(PaperclipLink.Connected, "Connected.");
        var orb = Orb(Agent());
        Assert.True(orb.OpenPaperclipVisible);
        Assert.False(orb.ReviewPaperclipVisible);
        Assert.False(orb.ReconnectPaperclipVisible);
    }

    [AvaloniaFact]
    public void An_amber_agent_offers_review_as_well()
    {
        PaperclipSessions.SetLinkForTests(PaperclipLink.Connected, "Connected.");
        var orb = Orb(Agent("waiting", OrbPresence.NeedsInput));
        Assert.True(orb.OpenPaperclipVisible);
        Assert.True(orb.ReviewPaperclipVisible);
    }

    [AvaloniaFact]
    public void Reconnect_appears_when_the_key_is_gone_and_runs_what_the_settings_wiring_installed()
    {
        PaperclipSessions.SetLinkForTests(PaperclipLink.NeedsReconnect, "Reconnect to Paperclip.");
        var orb = Orb(Agent(presence: OrbPresence.Parked));
        Assert.True(orb.ReconnectPaperclipVisible);

        var asked = 0;
        var before = OrbWindow.PaperclipReconnect;
        try
        {
            OrbWindow.PaperclipReconnect = null;
            orb.ReconnectPaperclip_Click(null, null!);        // nothing installed: no throw
            OrbWindow.PaperclipReconnect = () => asked++;
            orb.ReconnectPaperclip_Click(null, null!);
            Assert.Equal(1, asked);
        }
        finally { OrbWindow.PaperclipReconnect = before; }

        // The control: connected again, and the row goes.
        PaperclipSessions.SetLinkForTests(PaperclipLink.Connected, "Connected.");
        orb.UpdateFrom(Agent());
        Assert.False(orb.ReconnectPaperclipVisible);
    }

    [AvaloniaFact]
    public void An_agent_whose_address_a_browser_may_not_open_offers_no_open_row()
    {
        PaperclipSessions.SetLinkForTests(PaperclipLink.Connected, "Connected.");
        var orb = Orb(Agent("waiting", OrbPresence.NeedsInput, url: "javascript:alert(1)"));
        Assert.False(orb.OpenPaperclipVisible);
        Assert.False(orb.ReviewPaperclipVisible);
    }

    [AvaloniaFact]
    public void Reset_to_idle_is_disabled_with_paperclips_own_explanation_and_new_chat_here_is_hidden()
    {
        var orb = Orb(Agent());
        var reset = orb.FindControl<MenuItem>("ResetIdleItem")!;
        Assert.False(reset.IsEnabled);
        Assert.Equal("Paperclip controls this agent's state", reset.Header);
        Assert.False(orb.FindControl<MenuItem>("NewChatHereItem")!.IsVisible);
    }

    // ---- CB-445: the agent's own picture ----------------------------------------

    private static readonly byte[] Portrait = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAAEklEQVR4nGP438DwHx9mGBkKAFPWn4ETg8N3AAAAAElFTkSuQmCC");

    private static string PictureFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb445-orb-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "face.png");
        File.WriteAllBytes(file, Portrait);
        return file;
    }

    [AvaloniaFact]
    public void An_agent_with_a_cached_picture_wears_it_and_loses_its_letters()
    {
        var file = PictureFile();
        try
        {
            var orb = Orb(Agent(avatarFile: file));
            Assert.False(orb.Glyph.IsVisible, "the letters give way to the picture");
            Assert.True(orb.AvatarImage.IsVisible && orb.AvatarImage.Source is not null);
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }

    [AvaloniaFact]
    public void An_agent_with_no_picture_keeps_its_initials()
    {
        var orb = Orb(Agent());
        Assert.True(orb.Glyph.IsVisible);
        Assert.False(orb.AvatarImage.IsVisible);
    }

    [AvaloniaFact]
    public void A_picture_that_cannot_be_read_is_initials_never_a_broken_image()
    {
        var orb = Orb(Agent(avatarFile: Path.Combine(Path.GetTempPath(), "cb445-missing-" + Guid.NewGuid() + ".png")));
        Assert.True(orb.Glyph.IsVisible);
        Assert.False(orb.AvatarImage.IsVisible);
    }

    [AvaloniaFact]
    public void A_picture_that_arrives_later_replaces_the_letters_and_one_that_goes_brings_them_back()
    {
        var file = PictureFile();
        try
        {
            var orb = Orb(Agent());
            Assert.True(orb.Glyph.IsVisible);
            orb.UpdateFrom(Agent(avatarFile: file));
            Assert.False(orb.Glyph.IsVisible);
            Assert.True(orb.AvatarImage.IsVisible);
            orb.UpdateFrom(Agent());
            Assert.True(orb.Glyph.IsVisible);
            Assert.False(orb.AvatarImage.IsVisible);
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }

    [AvaloniaFact]
    public void The_picture_decodes_from_its_file_and_the_panel_header_finds_it_by_session_key()
    {
        var file = PictureFile();
        var id = Guid.NewGuid().ToString();
        try
        {
            Assert.NotNull(PaperclipAvatars.For(file));
            PaperclipSessions.SetSnapshotForTests([new PaperclipAgent(id, "Atlas", "r", "t", "idle", null, "x", "idle",
                PaperclipMark.None, null, [], null, null, null, null, null, DateTime.UtcNow, "u", false, AvatarFile: file)]);
            var face = SessionIdentity.For(PaperclipScan.KeyFor(id));
            Assert.NotNull(face.Avatar);
            Assert.Equal("Atlas", face.Name);
            Assert.True(face.DrawsItsOwnCircle);
        }
        finally
        {
            PaperclipSessions.SetSnapshotForTests([]);
            Directory.Delete(Path.GetDirectoryName(file)!, true);
        }
    }
}
