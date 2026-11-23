using Xunit;

namespace Orbweaver.Tests;

// CB-425: a Paperclip change reaching its orbs without waiting behind the local
// scan. The decisions are pure; the wiring through SessionManager is in
// tests/UiTests/PaperclipFastPathUiTests.
public class PaperclipFastPathTests
{
    private static SessionManager.ScanEntry E(string id, string state = "idle", string lead = "", string title = "Atlas",
        OrbPresence p = OrbPresence.Present) =>
        new(PaperclipScan.KeyFor(id), new SessionStatus
        {
            Source = SessionSource.Paperclip, State = state, Lead = lead, Title = title, Presence = p, PaperclipAgentId = id,
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
    public void Copying_takes_what_paperclip_owns_and_keeps_what_the_pass_added()
    {
        var need = new PaperclipNeed("approval", "p1", "Hire", "u");
        var to = new SessionStatus { State = "waiting", Presence = OrbPresence.NeedsInput, Needs = [need], Agent = "persona-owned", Cwd = "keep" };
        var from = new SessionStatus
        {
            State = "idle", Presence = OrbPresence.Present, Mark = PaperclipMark.Broke, MarkReason = "r", BudgetPercent = 40,
            FoldedCount = 2, FoldedNames = "x, y", HoverLines = ["h"], StatusDetail = "d", Url = "http://u",
            PaperclipAgentId = "a", PaperclipAgentStatus = "idle",
        };
        PaperclipFastPath.CopyOwned(to, from);
        Assert.Equal(("idle", OrbPresence.Present, PaperclipMark.Broke, "r", 40, 2, "x, y", "d", "http://u", "a", "idle"),
            (to.State, to.Presence, to.Mark, to.MarkReason, to.BudgetPercent, to.FoldedCount, to.FoldedNames, to.StatusDetail, to.Url, to.PaperclipAgentId, to.PaperclipAgentStatus));
        Assert.Equal(["h"], to.HoverLines);
        Assert.Empty(to.Needs);
        Assert.Equal(("persona-owned", "keep"), (to.Agent, to.Cwd));
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
