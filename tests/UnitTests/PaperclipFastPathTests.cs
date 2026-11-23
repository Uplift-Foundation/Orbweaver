using Xunit;

namespace Orbweaver.Tests;

// CB-427 / CB-425: what an orb shows the moment its need is answered, and the one
// diagnostics line a state change writes. Both are pure; the wiring through
// SessionManager is tests/UiTests/PaperclipFastPathUiTests.
public class PaperclipFastPathTests
{
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
