using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Xunit;

namespace Orbweaver.Tests;

// CB-427: the amber orb's answer panel and the orb menu's Wake / Pause / Resume,
// driven with a fake IPaperclipActions so no Paperclip, browser or screen is
// reached. The routes and verdicts behind the fake are PaperclipActionsTests;
// what the user would have seen is asserted here.
[Collection("Settings")]
public class PaperclipActTests : IDisposable
{
    private readonly Func<IPaperclipActions?> _savedSource = OrbWindow.PaperclipActionsSource;

    public void Dispose()
    {
        OrbWindow.PaperclipActionsSource = _savedSource;
        PaperclipSessions.SetLinkForTests(PaperclipLink.Off, "Paperclip is switched off.");
    }

    internal sealed class FakeActions : IPaperclipActions
    {
        internal readonly List<string> Calls = [];
        internal Func<string, PaperclipOutcome> Answer = _ => new(PaperclipActionResult.Done);
        internal TaskCompletionSource? Gate;
        internal bool Throw;

        private async Task<PaperclipOutcome> Do(string call)
        {
            Calls.Add(call);
            if (Gate != null) await Gate.Task;
            if (Throw) throw new InvalidOperationException("boom");
            return Answer(call);
        }
        public Task<PaperclipOutcome> DecideAsync(PaperclipNeed n, bool approve, CancellationToken ct) => Do((approve ? "approve " : "decline ") + n.Id);
        public Task<PaperclipOutcome> WakeAsync(string a, CancellationToken ct) => Do("wake " + a);
        public Task<PaperclipOutcome> PauseAsync(string a, CancellationToken ct) => Do("pause " + a);
        public Task<PaperclipOutcome> ResumeAsync(string a, CancellationToken ct) => Do("resume " + a);
    }

    private static readonly PaperclipNeed Hire = new("approval", "ap1", "Hire: QA engineer", "http://p/CB/approvals/ap1");
    private static readonly PaperclipNeed Tool = new("action", "ac1", "Run deploy tool", "http://p/CB/inbox/requests");
    private static readonly PaperclipNeed Stuck = new("blocked", "i1", "Blocked: CB-4 Ship it", "http://p/CB/issues/i1");

    private static SessionStatus Amber(params PaperclipNeed[] needs) => new()
    {
        Source = SessionSource.Paperclip, State = "waiting", Title = "Atlas", Cwd = "",
        Presence = OrbPresence.NeedsInput, PaperclipAgentId = "a1", PaperclipAgentStatus = "idle",
        Needs = needs, Url = "http://p/CB/approvals/ap1",
    };

    private sealed class Rig
    {
        internal readonly FakeActions Actions = new();
        internal readonly List<string> Opened = [];
        internal readonly PaperclipNeedsPanel Panel;
        internal Rig(bool connected = true)
        {
            Panel = new PaperclipNeedsPanel(() => connected ? Actions : null, Opened.Add);
        }
    }

    private static Task Click(PaperclipNeedsPanel.NeedRow row, bool approve, Rig rig) =>
        rig.Panel.AnswerAsync(row, approve);

    // ---- what is shown -----------------------------------------------------------

    [AvaloniaFact]
    public void Each_need_shows_its_title_with_approve_decline_and_review()
    {
        var rig = new Rig();
        rig.Panel.Present(Amber(Hire, Tool));
        Assert.Equal("Atlas is waiting on 2 things", rig.Panel.HeadingText);
        Assert.Equal(2, rig.Panel.NeedRows.Count);
        foreach (var row in rig.Panel.NeedRows)
        {
            Assert.NotNull(row.ApproveButton); Assert.NotNull(row.DeclineButton);
            Assert.Equal("Approve", row.ApproveButton!.Content); Assert.Equal("Decline", row.DeclineButton!.Content);
            Assert.Equal("Review in Paperclip", row.ReviewButton.Content);
            Assert.True(row.ApproveButton.IsEnabled);
        }
        Assert.Equal("Hire: QA engineer", Texts(rig.Panel.RowFor("ap1")!).First());
    }

    private static IEnumerable<string> Texts(PaperclipNeedsPanel.NeedRow row) =>
        ((StackPanel)row.Card.Child!).Children.OfType<TextBlock>().Select(t => t.Text ?? "");

    [AvaloniaFact]
    public void One_need_reads_as_a_single_ask_and_a_blocked_task_shows_only_the_link()
    {
        var rig = new Rig();
        rig.Panel.Present(Amber(Stuck));
        Assert.Equal("Atlas is waiting on you", rig.Panel.HeadingText);
        var row = rig.Panel.RowFor("i1")!;
        Assert.Null(row.ApproveButton); Assert.Null(row.DeclineButton);
        row.ReviewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(["http://p/CB/issues/i1"], rig.Opened);
    }

    // ---- answering ------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Approve_goes_through_the_seam_and_removes_the_row()
    {
        var rig = new Rig();
        rig.Panel.Present(Amber(Hire, Tool));
        await Click(rig.Panel.RowFor("ap1")!, true, rig);
        Assert.Equal(["approve ap1"], rig.Actions.Calls);
        Assert.Null(rig.Panel.RowFor("ap1"));
        Assert.NotNull(rig.Panel.RowFor("ac1"));
        Assert.Equal("Approved.", rig.Panel.FooterText);
    }

    [AvaloniaFact]
    public async Task Decline_goes_through_the_seam()
    {
        var rig = new Rig();
        rig.Panel.Present(Amber(Tool));
        await Click(rig.Panel.RowFor("ac1")!, false, rig);
        Assert.Equal(["decline ac1"], rig.Actions.Calls);
        Assert.Equal("Declined.", rig.Panel.FooterText);
    }

    [AvaloniaFact]
    public async Task A_failed_answer_says_why_and_leaves_the_item_answerable()
    {
        var rig = new Rig();
        rig.Actions.Answer = _ => new(PaperclipActionResult.Refused, "Paperclip said 500.");
        rig.Panel.Present(Amber(Hire));
        var row = rig.Panel.RowFor("ap1")!;
        await Click(row, true, rig);
        Assert.NotNull(rig.Panel.RowFor("ap1"));
        Assert.True(row.Status.IsVisible);
        Assert.Equal("Paperclip said 500.", row.Status.Text);
        Assert.True(row.ApproveButton!.IsEnabled && row.DeclineButton!.IsEnabled);
        // and it can be answered again
        rig.Actions.Answer = _ => new(PaperclipActionResult.Done);
        await Click(row, true, rig);
        Assert.Null(rig.Panel.RowFor("ap1"));
    }

    [AvaloniaFact]
    public async Task An_item_answered_elsewhere_is_reported_as_that_in_place_of_the_block_and_is_not_an_error()
    {
        var rig = new Rig();
        rig.Actions.Answer = _ => new(PaperclipActionResult.AlreadyDecided, "Already answered in Paperclip");
        rig.Panel.Present(Amber(Hire, Tool));
        var card = rig.Panel.RowFor("ap1")!.Card;
        await Click(rig.Panel.RowFor("ap1")!, true, rig);
        Assert.Null(rig.Panel.RowFor("ap1"));
        // the sentence stands where the block was, in ordinary text
        Assert.Equal("Already answered in Paperclip", Assert.IsType<TextBlock>(card.Child).Text);
        Assert.Null(rig.Panel.FooterText);
        // it stays through a stale poll and goes when the poll drops the item
        rig.Panel.Present(Amber(Hire, Tool));
        Assert.Equal("Already answered in Paperclip", Assert.IsType<TextBlock>(card.Child).Text);
        rig.Panel.Present(Amber(Tool));
        Assert.Null(card.Parent);
    }

    [AvaloniaFact]
    public async Task When_the_only_item_was_answered_elsewhere_the_sentence_is_all_that_is_left_until_the_poll_drops_it()
    {
        var rig = new Rig();
        rig.Actions.Answer = _ => new(PaperclipActionResult.AlreadyDecided, null);
        rig.Panel.Present(Amber(Hire));
        await Click(rig.Panel.RowFor("ap1")!, true, rig);
        Assert.Equal("Atlas", rig.Panel.HeadingText);
        Assert.Empty(rig.Panel.NeedRows);
        var card = Assert.Single(rig.Panel.Content is Border b ? ((StackPanel)b.Child!).Children.OfType<StackPanel>().First().Children : []);
        Assert.Equal("Already answered in Paperclip", Assert.IsType<TextBlock>(((Border)card).Child).Text);
        rig.Panel.Present(Amber());
        Assert.Empty(((StackPanel)((Border)rig.Panel.Content!).Child!).Children.OfType<StackPanel>().First().Children);
    }

    [AvaloniaFact]
    public async Task An_approval_whose_tool_then_failed_says_so_in_the_footer()
    {
        var rig = new Rig();
        rig.Actions.Answer = _ => new(PaperclipActionResult.Done, "Approved, but the tool failed");
        rig.Panel.Present(Amber(Tool));
        await Click(rig.Panel.RowFor("ac1")!, true, rig);
        Assert.Equal("Approved, but the tool failed", rig.Panel.FooterText);
    }

    [AvaloniaFact]
    public void A_need_with_a_summary_shows_it_between_the_title_and_the_buttons_cut_when_long()
    {
        var rig = new Rig();
        var withSummary = Hire with { Summary = "Needs QA before the release." };
        var longOne = Tool with { Summary = new string('s', 300) };
        rig.Panel.Present(Amber(withSummary, longOne));
        var texts = Texts(rig.Panel.RowFor("ap1")!).ToList();
        Assert.Equal(["Hire: QA engineer", "Needs QA before the release.", ""], texts);
        var cut = Texts(rig.Panel.RowFor("ac1")!).ElementAt(1);
        Assert.Equal(241, cut.Length);
        Assert.EndsWith("…", cut);
    }

    [AvaloniaTheory]
    [InlineData("Refused")]
    [InlineData("Unreachable")]
    [InlineData("NeedsReconnect")]
    public async Task Every_failure_kind_shows_its_words_and_leaves_the_item_answerable(string result)
    {
        var rig = new Rig();
        rig.Actions.Answer = _ => new(Enum.Parse<PaperclipActionResult>(result), "why " + result);
        rig.Panel.Present(Amber(Hire));
        var row = rig.Panel.RowFor("ap1")!;
        await Click(row, true, rig);
        Assert.Equal("why " + result, row.Status.Text);
        Assert.True(row.ApproveButton!.IsEnabled);
    }

    [AvaloniaFact]
    public async Task A_failure_with_no_words_says_the_default()
    {
        var rig = new Rig();
        rig.Actions.Answer = _ => new(PaperclipActionResult.Refused, null);
        rig.Panel.Present(Amber(Hire));
        await Click(rig.Panel.RowFor("ap1")!, true, rig);
        Assert.Equal("Paperclip refused that", rig.Panel.RowFor("ap1")!.Status.Text);
    }

    [AvaloniaFact]
    public async Task A_throwing_seam_is_a_failure_not_a_crash()
    {
        var rig = new Rig();
        rig.Actions.Throw = true;
        rig.Panel.Present(Amber(Hire));
        var row = rig.Panel.RowFor("ap1")!;
        await Click(row, true, rig);
        Assert.Equal("boom", row.Status.Text);
        Assert.NotNull(rig.Panel.RowFor("ap1"));
    }

    [AvaloniaFact]
    public async Task While_an_answer_is_in_flight_the_buttons_are_held_and_a_second_press_does_nothing()
    {
        var rig = new Rig();
        rig.Actions.Gate = new TaskCompletionSource();
        rig.Panel.Present(Amber(Hire));
        var row = rig.Panel.RowFor("ap1")!;
        var first = Click(row, true, rig);
        Assert.False(row.ApproveButton!.IsEnabled);
        Assert.False(row.DeclineButton!.IsEnabled);
        await Click(row, false, rig);
        Assert.Equal(["approve ap1"], rig.Actions.Calls);
        rig.Actions.Gate.SetResult();
        await first;
        Assert.Null(rig.Panel.RowFor("ap1"));
    }

    [AvaloniaFact]
    public async Task With_no_connection_nothing_is_called_and_the_row_says_so()
    {
        var rig = new Rig(connected: false);
        rig.Panel.Present(Amber(Hire));
        var row = rig.Panel.RowFor("ap1")!;
        await Click(row, true, rig);
        Assert.Empty(rig.Actions.Calls);
        Assert.Equal("Paperclip is not connected.", row.Status.Text);
    }

    // ---- following the orb ----------------------------------------------------------

    [AvaloniaFact]
    public async Task A_stale_snapshot_does_not_bring_back_an_answered_item_and_a_gone_one_leaves()
    {
        var rig = new Rig();
        rig.Panel.Present(Amber(Hire, Tool));
        await Click(rig.Panel.RowFor("ap1")!, true, rig);
        // the poll has not caught up: the answered item is still listed
        rig.Panel.Present(Amber(Hire, Tool));
        Assert.Null(rig.Panel.RowFor("ap1"));
        // the poll catches up: Tool goes too, and the answered mark is forgotten
        rig.Panel.Present(Amber());
        Assert.Empty(rig.Panel.NeedRows);
        rig.Panel.Present(Amber(Hire));
        Assert.NotNull(rig.Panel.RowFor("ap1"));
    }

    [AvaloniaFact]
    public void A_new_need_on_an_open_panel_is_added_and_a_known_one_is_kept()
    {
        var rig = new Rig();
        rig.Panel.Present(Amber(Hire));
        var before = rig.Panel.RowFor("ap1");
        rig.Panel.Present(Amber(Hire, Tool));
        Assert.Same(before, rig.Panel.RowFor("ap1"));
        Assert.NotNull(rig.Panel.RowFor("ac1"));
    }

    [AvaloniaFact]
    public void Refresh_with_no_panel_open_does_nothing()
    {
        PaperclipNeedsPanel.Refresh(Amber(Hire));
        Assert.False(PaperclipNeedsPanel.IsOpen);
    }

    // ---- the orb menu ----------------------------------------------------------------

    private static OrbWindow Orb(SessionStatus status, PaperclipLink link = PaperclipLink.Connected)
    {
        PaperclipSessions.SetLinkForTests(link, "t");
        var orb = new OrbWindow("paperclip:" + Guid.NewGuid());
        orb.UpdateFrom(status);
        return orb;
    }

    private static SessionStatus Plain(string status, string state = "idle") => new()
    {
        Source = SessionSource.Paperclip, State = state, Title = "Atlas", Cwd = "", Presence = OrbPresence.Present,
        PaperclipAgentId = "a1", PaperclipAgentStatus = status, Url = "http://p/CB/agents/a1",
    };

    private static bool Visible(OrbWindow o, string name) => o.FindControl<MenuItem>(name)!.IsVisible;

    [AvaloniaTheory]
    [InlineData("idle", "idle", true, true, false)]
    [InlineData("running", "generating", false, true, false)]
    [InlineData("paused", "idle", false, false, true)]
    public void The_menu_shows_only_the_rows_that_apply(string status, string state, bool wake, bool pause, bool resume)
    {
        var orb = Orb(Plain(status, state));
        Assert.Equal((wake, pause, resume),
            (Visible(orb, "WakePaperclipItem"), Visible(orb, "PausePaperclipItem"), Visible(orb, "ResumePaperclipItem")));
        Assert.True(Visible(orb, "OpenPaperclipItem"));
    }

    [AvaloniaFact]
    public void An_agent_paused_for_budget_is_offered_neither_pause_nor_resume_nor_wake()
    {
        var s = Plain("paused"); s.Mark = PaperclipMark.OverBudget;
        var orb = Orb(s);
        Assert.False(Visible(orb, "WakePaperclipItem") || Visible(orb, "PausePaperclipItem") || Visible(orb, "ResumePaperclipItem"));
    }

    [AvaloniaFact]
    public void A_parked_company_offers_no_action_rows_and_a_non_paperclip_orb_none_either()
    {
        var orb = Orb(Plain("idle"), PaperclipLink.Unreachable);
        Assert.False(Visible(orb, "WakePaperclipItem") || Visible(orb, "PausePaperclipItem") || Visible(orb, "ResumePaperclipItem"));
        var local = new OrbWindow("local-1");
        local.UpdateFrom(new SessionStatus { Source = SessionSource.ClaudeCode, State = "idle", Title = "x", Cwd = "/tmp" });
        Assert.False(Visible(local, "WakePaperclipItem"));
    }

    [AvaloniaFact]
    public async Task Wake_acts_at_once()
    {
        var fake = new FakeActions();
        OrbWindow.PaperclipActionsSource = () => fake;
        var orb = Orb(Plain("idle"));
        await orb.PaperclipActAsync("wake");
        Assert.Equal(["wake a1"], fake.Calls);
        Assert.Null(orb.PaperclipRowNote);
    }

    [AvaloniaFact]
    public async Task Pause_and_resume_are_armed_then_confirmed()
    {
        var fake = new FakeActions();
        OrbWindow.PaperclipActionsSource = () => fake;
        var orb = Orb(Plain("idle"));
        await orb.PaperclipActAsync("pause");
        Assert.Empty(fake.Calls);
        Assert.Equal("pause", orb.PaperclipArmed);
        Assert.Equal("Click again to pause", orb.FindControl<MenuItem>("PausePaperclipItem")!.Header);
        await orb.PaperclipActAsync("pause");
        Assert.Equal(["pause a1"], fake.Calls);
        Assert.Null(orb.PaperclipArmed);
        Assert.Equal("Pause", orb.FindControl<MenuItem>("PausePaperclipItem")!.Header);

        var paused = Orb(Plain("paused"));
        await paused.PaperclipActAsync("resume");
        Assert.Equal("Click again to resume", paused.FindControl<MenuItem>("ResumePaperclipItem")!.Header);
        await paused.PaperclipActAsync("resume");
        Assert.Equal("resume a1", fake.Calls.Last());
    }

    [AvaloniaFact]
    public async Task Arming_one_verb_and_pressing_another_re_arms_instead_of_acting()
    {
        var fake = new FakeActions();
        OrbWindow.PaperclipActionsSource = () => fake;
        var orb = Orb(Plain("idle"));
        await orb.PaperclipActAsync("pause");
        await orb.PaperclipActAsync("resume");
        Assert.Empty(fake.Calls);
        Assert.Equal("resume", orb.PaperclipArmed);
        orb.DisarmPaperclip();
        Assert.Null(orb.PaperclipArmed);
    }

    [AvaloniaFact]
    public async Task A_failed_action_keeps_its_reason()
    {
        var fake = new FakeActions { Answer = _ => new(PaperclipActionResult.Refused, "Paperclip said 409.") };
        OrbWindow.PaperclipActionsSource = () => fake;
        var orb = Orb(Plain("idle"));
        await orb.PaperclipActAsync("wake");
        Assert.Equal("Paperclip said 409.", orb.PaperclipRowNote);
    }

    [AvaloniaFact]
    public async Task Without_a_connection_or_when_the_seam_throws_an_action_fails_with_a_sentence()
    {
        OrbWindow.PaperclipActionsSource = () => null;
        var orb = Orb(Plain("idle"));
        await orb.PaperclipActAsync("wake");
        Assert.Equal("Paperclip is not connected.", orb.PaperclipRowNote);

        OrbWindow.PaperclipActionsSource = () => new FakeActions { Throw = true };
        await orb.PaperclipActAsync("wake");
        Assert.Equal("boom", orb.PaperclipRowNote);
    }

    [AvaloniaFact]
    public async Task An_action_on_an_orb_that_is_not_a_paperclip_agent_does_nothing()
    {
        var fake = new FakeActions();
        OrbWindow.PaperclipActionsSource = () => fake;
        var local = new OrbWindow("local-2");
        local.UpdateFrom(new SessionStatus { Source = SessionSource.ClaudeCode, State = "idle", Title = "x", Cwd = "/tmp" });
        await local.PaperclipActAsync("wake");
        Assert.Empty(fake.Calls);
    }

    [AvaloniaFact]
    public async Task The_menu_click_handlers_reach_the_same_actions()
    {
        var fake = new FakeActions();
        OrbWindow.PaperclipActionsSource = () => fake;
        var orb = Orb(Plain("idle"));
        orb.FindControl<MenuItem>("WakePaperclipItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Task.Delay(50);
        Assert.Equal(["wake a1"], fake.Calls);
        orb.FindControl<MenuItem>("PausePaperclipItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal("pause", orb.PaperclipArmed);
        orb.DisarmPaperclip();
        var paused = Orb(Plain("paused"));
        paused.FindControl<MenuItem>("ResumePaperclipItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal("resume", paused.PaperclipArmed);
    }
}
