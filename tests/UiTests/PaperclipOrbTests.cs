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
        string url = "http://127.0.0.1:3100/CB/agents/a") => new()
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
}
