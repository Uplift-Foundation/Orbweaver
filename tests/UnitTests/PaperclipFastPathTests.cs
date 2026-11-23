using Xunit;

namespace Orbweaver.Tests;

// CB-427 / CB-425: what an orb shows the moment its need is answered, and the one
// diagnostics line a state change writes. Both are pure; the wiring through
// SessionManager is tests/UiTests/PaperclipFastPathUiTests.
public class PaperclipFastPathTests
{
    private static SessionManager.ScanEntry E(string id, string state = "idle", string lead = "", string title = "Atlas") =>
        new(PaperclipScan.KeyFor(id), new SessionStatus
        {
            Source = SessionSource.Paperclip, State = state, Lead = lead, Title = title, PaperclipAgentId = id,
        }, DateTime.UtcNow);

    private static Dictionary<string, SessionStatus> Current(params SessionManager.ScanEntry[] e) =>
        e.ToDictionary(x => x.SessionId, x => x.Status);

    [Fact]
    public void The_same_set_of_orbs_is_planned_and_anything_else_falls_back()
    {
        var cur = Current(E("a", "waiting"), E("b"));
        cur["claude-code-1"] = new SessionStatus { Source = SessionSource.ClaudeCode };   // other sources are not looked at
        var plan = PaperclipFastPath.Plan([E("a"), E("b")], cur)!;
        Assert.Equal(["paperclip:a", "paperclip:b"], plan.Select(c => c.Key));
        Assert.Equal("waiting", plan[0].Existing.State);
        Assert.Equal("idle", plan[0].Fresh.State);

        Assert.Null(PaperclipFastPath.Plan([E("a"), E("b"), E("c")], cur));    // an orb appeared
        Assert.Null(PaperclipFastPath.Plan([E("a")], cur));                    // an orb went
        Assert.Null(PaperclipFastPath.Plan([E("a"), E("z")], cur));            // a different orb
        Assert.Null(PaperclipFastPath.Plan([E("a", lead: "paperclip:b"), E("b")], cur));   // a lead moved
        Assert.Null(PaperclipFastPath.Plan([E("a", title: "Renamed"), E("b")], cur));      // a rename
        Assert.Empty(PaperclipFastPath.Plan([], new Dictionary<string, SessionStatus>())!);
    }

    [Fact]
    public void An_applied_status_is_a_copy_the_original_is_untouched_and_what_the_pass_added_is_kept()
    {
        var need = new PaperclipNeed("approval", "p1", "Hire", "u");
        var old = new SessionStatus
        {
            Source = SessionSource.Paperclip, State = "waiting", Presence = OrbPresence.NeedsInput, Needs = [need],
            Agent = "persona-owned", Cwd = "keep", Lead = "L",
        };
        var fresh = new SessionStatus
        {
            State = "idle", Presence = OrbPresence.Present, Mark = PaperclipMark.Broke, MarkReason = "r", BudgetPercent = 40,
            FoldedCount = 2, FoldedNames = "x, y", HoverLines = ["h"], StatusDetail = "d", Url = "http://u",
            PaperclipAgentId = "a", PaperclipAgentStatus = "idle", PaperclipAgentUrl = "http://agent",
        };
        var applied = PaperclipFastPath.Applied(old, fresh);

        Assert.NotSame(old, applied);
        Assert.Equal(("idle", OrbPresence.Present, PaperclipMark.Broke, "r", 40, 2, "x, y", "d", "http://u", "a", "idle", "http://agent"),
            (applied.State, applied.Presence, applied.Mark, applied.MarkReason, applied.BudgetPercent, applied.FoldedCount, applied.FoldedNames,
             applied.StatusDetail, applied.Url, applied.PaperclipAgentId, applied.PaperclipAgentStatus, applied.PaperclipAgentUrl));
        Assert.Equal(["h"], applied.HoverLines);
        Assert.Empty(applied.Needs);
        Assert.Equal(("persona-owned", "keep", "L"), (applied.Agent, applied.Cwd, applied.Lead));

        // the object the relay may be holding did not move
        Assert.Equal(("waiting", OrbPresence.NeedsInput, PaperclipMark.None), (old.State, old.Presence, old.Mark));
        Assert.Equal([need], old.Needs);
    }

    private static readonly PaperclipNeed A = new("approval", "a", "Hire QA", "http://p/CB/approvals/a");
    private static readonly PaperclipNeed B = new("action", "b", "Run deploy", "http://p/CB/inbox/requests");

    private static SessionStatus Amber(params PaperclipNeed[] needs) => new()
    {
        Source = SessionSource.Paperclip, State = "waiting", Presence = OrbPresence.NeedsInput, Needs = needs,
        HoverLines = needs.Select(n => n.Title).Append("last run succeeded").ToList(),
        StatusDetail = needs[0].Title, Url = needs[0].Url, PaperclipAgentUrl = "http://p/CB/agents/x",
    };

    [Fact]
    public void Answering_one_of_two_leaves_the_orb_amber_and_points_at_the_other()
    {
        var s = Amber(A, B);
        Assert.True(PaperclipFastPath.WithoutNeed(s, "a"));
        Assert.Equal([B], s.Needs);
        Assert.Equal(("waiting", OrbPresence.NeedsInput, B.Url), (s.State, s.Presence, s.Url));
        Assert.Equal(["Run deploy", "last run succeeded"], s.HoverLines);
        Assert.Equal("Run deploy", s.StatusDetail);
    }

    [Fact]
    public void Answering_the_last_need_stops_the_asking_and_points_at_the_agent()
    {
        var s = Amber(A);
        Assert.True(PaperclipFastPath.WithoutNeed(s, "a"));
        Assert.Empty(s.Needs);
        Assert.Equal(("idle", OrbPresence.Present, "http://p/CB/agents/x"), (s.State, s.Presence, s.Url));
        Assert.Equal(["last run succeeded"], s.HoverLines);

        var noPage = Amber(A); noPage.PaperclipAgentUrl = "";
        PaperclipFastPath.WithoutNeed(noPage, "a");
        Assert.Equal(A.Url, noPage.Url);          // nothing better to point at: unchanged
    }

    [Fact]
    public void A_need_the_orb_does_not_hold_or_a_row_that_is_not_paperclip_changes_nothing()
    {
        var s = Amber(A);
        Assert.False(PaperclipFastPath.WithoutNeed(s, "zzz"));
        Assert.Equal(("waiting", OrbPresence.NeedsInput), (s.State, s.Presence));
        var local = Amber(A); local.Source = SessionSource.ClaudeCode;
        Assert.False(PaperclipFastPath.WithoutNeed(local, "a"));
        Assert.Single(local.Needs);
    }

    [Fact]
    public void An_answered_orb_with_no_hover_lines_has_no_detail()
    {
        var s = new SessionStatus { Source = SessionSource.Paperclip, Needs = [A], HoverLines = [] };
        Assert.True(PaperclipFastPath.WithoutNeed(s, "a"));
        Assert.Null(s.StatusDetail);
    }

    [Theory]
    [InlineData("waiting", "idle", true)]
    [InlineData("idle", "idle", false)]
    public void A_line_is_written_only_for_a_state_that_moved(string from, string to, bool line)
    {
        var l = PaperclipFastPath.Line("paperclip:0123456789abcdef", from, to, TimeSpan.FromMilliseconds(412.6));
        Assert.Equal(line, l != null);
        if (line) Assert.Equal("paperclip: 01234567 waiting -> idle reached the orb in 412 ms", l);
    }

    [Fact]
    public void The_line_names_no_title_and_survives_a_short_key_and_a_negative_clock()
    {
        Assert.Equal("paperclip: ab idle -> waiting reached the orb in 0 ms",
            PaperclipFastPath.Line("ab", "idle", "waiting", TimeSpan.FromSeconds(-1)));
        Assert.Equal("paperclip: ab idle -> waiting reached the orb in 3300 ms",
            PaperclipFastPath.Line("paperclip:ab", "idle", "waiting", TimeSpan.FromMilliseconds(3300)));
    }
}
