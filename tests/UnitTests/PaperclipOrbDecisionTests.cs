using Xunit;

namespace Orbweaver.UnitTests;

// CB-421 (orb visuals): every decision a Paperclip orb makes, with no window.
// The drawing itself is PaperclipOrbTests in tests/UiTests.
public class PaperclipOrbDecisionTests
{
    // ---- the badge -----------------------------------------------------------

    [Fact]
    public void A_paperclip_orb_has_its_own_mark_distinct_from_every_cli()
    {
        var mark = CliMark.For(SessionSource.Paperclip);
        Assert.NotNull(mark);
        Assert.Equal(("paperclip", "#5B5BD6"), (mark!.Value.Name, mark.Value.FillHex));
        foreach (var other in new[] { SessionSource.ClaudeCode, SessionSource.Codex, SessionSource.Grok, SessionSource.OpenClaw })
        {
            Assert.NotEqual(CliMark.For(other)!.Value.FillHex, mark.Value.FillHex);
            Assert.NotEqual(CliMark.For(other)!.Value.GlyphPath, mark.Value.GlyphPath);
        }
        // The control: a cloud session still has no mark, as before.
        Assert.Null(CliMark.For(SessionSource.ClaudeCloud));
    }

    // ---- the drawn marks -----------------------------------------------------

    [Theory]
    [InlineData(PaperclipMark.Broke, "broke", AccountOrbWindow.DangerHex)]
    [InlineData(PaperclipMark.Paused, "paused", PaperclipOrb.WhiteHex)]
    [InlineData(PaperclipMark.OverBudget, "over budget", AccountOrbWindow.WarnHex)]
    public void Each_mark_has_a_name_and_a_colour(PaperclipMark mark, string name, string fill)
    {
        var look = PaperclipOrb.MarkFor(mark);
        Assert.NotNull(look);
        Assert.Equal((name, fill), (look!.Value.Name, look.Value.FillHex));
        Assert.False(string.IsNullOrWhiteSpace(look.Value.Path));
    }

    [Fact]
    public void Paused_and_over_budget_are_the_same_shape_and_broke_is_not_and_none_draws_nothing()
    {
        Assert.Equal(PaperclipOrb.MarkFor(PaperclipMark.Paused)!.Value.Path, PaperclipOrb.MarkFor(PaperclipMark.OverBudget)!.Value.Path);
        Assert.NotEqual(PaperclipOrb.MarkFor(PaperclipMark.Paused)!.Value.Path, PaperclipOrb.MarkFor(PaperclipMark.Broke)!.Value.Path);
        Assert.Equal(SymbolMarks.Alert, PaperclipOrb.MarkFor(PaperclipMark.Broke)!.Value.Path);
        Assert.Equal(SymbolMarks.Pause, PaperclipOrb.MarkFor(PaperclipMark.Paused)!.Value.Path);
        Assert.Null(PaperclipOrb.MarkFor(PaperclipMark.None));
    }

    // ---- dimming and the ring ---------------------------------------------------------

    private static SessionStatus Row(OrbPresence presence, PaperclipMark mark, SessionSource source = SessionSource.Paperclip) =>
        new() { Source = source, Presence = presence, Mark = mark };

    [Theory]
    [InlineData(OrbPresence.Present, PaperclipMark.None, false)]
    [InlineData(OrbPresence.Parked, PaperclipMark.None, true)]        // asleep
    [InlineData(OrbPresence.Present, PaperclipMark.Paused, true)]     // paused
    [InlineData(OrbPresence.Parked, PaperclipMark.Paused, true)]
    [InlineData(OrbPresence.NeedsInput, PaperclipMark.None, false)]   // needs you
    [InlineData(OrbPresence.NeedsInput, PaperclipMark.Paused, false)]
    [InlineData(OrbPresence.Present, PaperclipMark.Broke, false)]     // broke
    [InlineData(OrbPresence.Parked, PaperclipMark.Broke, false)]
    [InlineData(OrbPresence.Present, PaperclipMark.OverBudget, false)]
    [InlineData(OrbPresence.Parked, PaperclipMark.OverBudget, false)]
    public void Only_asleep_and_paused_paperclip_orbs_dim(OrbPresence presence, PaperclipMark mark, bool dims) =>
        Assert.Equal(dims, PaperclipOrb.Dims(Row(presence, mark)));

    [Theory]
    [InlineData(SessionSource.ClaudeCode)]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.ClaudeCloud)]
    public void A_row_that_is_not_a_paperclip_agent_has_no_verdict_so_the_presence_rule_stands(SessionSource source) =>
        Assert.Null(PaperclipOrb.Dims(Row(OrbPresence.NeedsInput, PaperclipMark.Broke, source)));

    [Fact]
    public void The_ring_is_the_budget_for_paperclip_and_the_context_percent_for_everything_else()
    {
        var pc = new SessionStatus { Source = SessionSource.Paperclip, BudgetPercent = 40, ContextPercent = 90 };
        Assert.Equal(40, PaperclipOrb.RingPercent(pc));
        Assert.Null(PaperclipOrb.RingPercent(new SessionStatus { Source = SessionSource.Paperclip, ContextPercent = 90 }));
        var cloud = new SessionStatus { Source = SessionSource.ClaudeCloud, BudgetPercent = 40, ContextPercent = 90 };
        Assert.Equal(90, PaperclipOrb.RingPercent(cloud));
        Assert.Null(PaperclipOrb.RingPercent(new SessionStatus { Source = SessionSource.ClaudeCode, BudgetPercent = 40 }));
    }

    // ---- the folded count ----------------------------------------------------

    [Theory]
    [InlineData(SessionSource.Paperclip, 3, "+3")]
    [InlineData(SessionSource.Paperclip, 1, "+1")]
    [InlineData(SessionSource.Paperclip, 0, null)]
    [InlineData(SessionSource.Paperclip, -2, null)]
    // The control: a stale count on another kind of orb draws no pill.
    [InlineData(SessionSource.ClaudeCode, 3, null)]
    [InlineData(SessionSource.OpenClaw, 3, null)]
    public void The_pill_says_how_many_are_folded_and_only_on_a_paperclip_orb(SessionSource source, int folded, string? label) =>
        Assert.Equal(label, PaperclipOrb.FoldedLabel(new SessionStatus { Source = source, FoldedCount = folded }));

    // ---- the hover -----------------------------------------------------------

    [Fact]
    public void The_hover_keeps_the_lines_in_the_order_given_and_puts_the_folded_names_last()
    {
        var body = PaperclipOrb.TipBody(["Hire: QA engineer", "Paused: over budget", "CB-12 Fix the build"], 2, " Ann, Bo ");
        Assert.Equal("Hire: QA engineer\nPaused: over budget\nCB-12 Fix the build\n+2 asleep: Ann, Bo", body);
    }

    [Theory]
    [InlineData(null, 0, null, null)]
    [InlineData(new string[0], 0, null, null)]
    [InlineData(new[] { "", "  " }, 0, null, null)]
    [InlineData(new[] { " running for 3 m " }, 0, null, "running for 3 m")]
    [InlineData(null, 4, null, "+4 asleep")]
    [InlineData(null, 4, "  ", "+4 asleep")]
    [InlineData(null, 4, "A, B", "+4 asleep: A, B")]
    [InlineData(new[] { "x" }, -1, "A", "x")]
    public void An_empty_hover_is_null_so_the_orb_shows_the_name_alone(string[]? lines, int folded, string? names, string? expected) =>
        Assert.Equal(expected, PaperclipOrb.TipBody(lines, folded, names));

    // ---- the menu ------------------------------------------------------------

    private static SessionStatus Agent(string url = "http://127.0.0.1:3100/CB/agents/a", OrbPresence presence = OrbPresence.Present) =>
        new() { Source = SessionSource.Paperclip, Url = url, Presence = presence };

    [Fact]
    public void An_ordinary_agent_offers_open_and_nothing_else()
    {
        var offer = PaperclipOrb.Offer(Agent(), PaperclipLink.Connected);
        Assert.Equal(new PaperclipOrb.MenuOffer(Open: true, Review: false, Reconnect: false), offer);
    }

    [Fact]
    public void An_amber_agent_also_offers_review()
    {
        var offer = PaperclipOrb.Offer(Agent(presence: OrbPresence.NeedsInput), PaperclipLink.Connected);
        Assert.Equal(new PaperclipOrb.MenuOffer(true, true, false), offer);
    }

    [Theory]
    [InlineData("NeedsReconnect", true)]
    [InlineData("Connected", false)]
    [InlineData("Unreachable", false)]
    [InlineData("Connecting", false)]
    [InlineData("Off", false)]
    public void Reconnect_is_offered_only_while_the_key_is_gone(string link, bool reconnect) =>
        Assert.Equal(reconnect, PaperclipOrb.Offer(Agent(), Enum.Parse<PaperclipLink>(link)).Reconnect);

    [Theory]
    [InlineData("")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://user@evil.example/")]
    [InlineData("http://127.0.0.1:3100/a b")]
    public void An_address_a_browser_may_not_be_handed_offers_neither_open_nor_review(string url)
    {
        var offer = PaperclipOrb.Offer(Agent(url, OrbPresence.NeedsInput), PaperclipLink.Connected);
        Assert.False(offer.Open);
        Assert.False(offer.Review);
    }

    [Theory]
    [InlineData(SessionSource.ClaudeCode)]
    [InlineData(SessionSource.ClaudeCloud)]
    [InlineData(SessionSource.OpenClaw)]
    public void Another_kind_of_orb_offers_no_paperclip_row_even_with_the_key_gone(SessionSource source) =>
        Assert.Equal(default, PaperclipOrb.Offer(
            new SessionStatus { Source = source, Url = "https://claude.ai/code/x", Presence = OrbPresence.NeedsInput },
            PaperclipLink.NeedsReconnect));

    [Fact]
    public void A_missing_status_offers_nothing() =>
        Assert.Equal(default, PaperclipOrb.Offer(null, PaperclipLink.NeedsReconnect));

    // ---- the places a source is named ---------------------------------------

    [Fact]
    public void A_hidden_paperclip_orb_is_labelled_for_what_it_is() =>
        Assert.Equal("Paperclip agent", HiddenOrbs.SourceLabel(new SessionStatus { Source = SessionSource.Paperclip }));

    [Fact]
    public void Auto_grouping_by_cli_makes_a_paperclip_group_not_a_claude_one()
    {
        var value = OrbAutoGroups.Classify(AutoGroupBy.Cli,
            new AutoGroupFacts(SessionSource.Paperclip, "", "", null, null, null), "/Users/ann");
        Assert.Equal(new AutoGroupValue("paperclip", "Paperclip"), value);
    }

    [Fact]
    public void A_paperclip_agent_has_no_account_and_no_local_persona()
    {
        Assert.Null(AutoGroupAccounts.HomeOf(SessionSource.Paperclip, "/Users/ann/.claude/projects/-x/a.jsonl"));
        Assert.Null(new AutoGroupAccounts("/Users/ann", read: _ => null).Of(SessionSource.Paperclip, "/Users/ann/.claude"));
        // The walk would otherwise climb from an empty cwd to the root and hand an agent a file it never named.
        Assert.Empty(LocalPersona.CandidatesBelow("", Array.Empty<string>(), SessionSource.Paperclip, "Atlas", ceiling: null));
        // The control: a Claude session in a real folder still finds its candidates.
        Assert.NotEmpty(LocalPersona.CandidatesBelow(Path.GetTempPath(), Array.Empty<string>(), SessionSource.ClaudeCode, "", ceiling: null));
    }

    // The two drawn marks are one filled closed path each, in the 16x16 box every
    // SymbolMarks path is drawn in, so they scale with the corner like the others.
    [Theory]
    [InlineData(SymbolMarks.Alert)]
    [InlineData(SymbolMarks.Pause)]
    [InlineData(CliMark_PaperclipPath)]
    public void The_new_paths_stay_inside_the_sixteen_box(string path)
    {
        var numbers = System.Text.RegularExpressions.Regex.Matches(path, @"-?\d+(\.\d+)?").Select(m => double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.All(numbers, n => Assert.InRange(n, 0, 16));
        Assert.EndsWith("Z", path.TrimEnd());
    }

    private const string CliMark_PaperclipPath =
        "M11.2,10 V5 A3.2,3.2 0 0 0 4.8,5 V11.5 A1.7,1.7 0 0 0 8.2,11.5 V6 H9.8 "
        + "V11.5 A3.3,3.3 0 0 1 3.2,11.5 V5 A4.8,4.8 0 0 1 12.8,5 V10 Z";

    [Fact]
    public void The_paperclip_glyph_in_the_source_is_the_path_this_file_checks() =>
        Assert.Equal(CliMark_PaperclipPath, CliMark.Paperclip.GlyphPath);
}
