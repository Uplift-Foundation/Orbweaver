using Xunit;
using static Orbweaver.Tests.PaperclipFx;

namespace Orbweaver.Tests;

public class PaperclipScanTests
{
    private static PaperclipAgent A(string id, string? lead = null, string state = "idle", PaperclipMark mark = PaperclipMark.None,
        string? markReason = null, IReadOnlyList<PaperclipNeed>? needs = null, bool? sleeping = null, int? budget = null,
        string? task = null, string? activity = null, DateTime? started = null, string? last = null, DateTime? activityAt = null,
        string name = "", DateTime? finished = null) =>
        new(id, name == "" ? id : name, "r", "t", "idle", lead, "paperclip_runner", state, mark, markReason,
            needs ?? [], task, activity, started, last, budget, activityAt ?? Now.AddHours(-3),
            "http://127.0.0.1:3100/EX/agents/" + id, sleeping ?? (state == "idle" && mark == PaperclipMark.None && (needs?.Count ?? 0) == 0),
            null, null, 5000, budget == null ? 0 : budget.Value * 50, finished);

    private static readonly PaperclipNeed Need = new("approval", "p1", "Hire QA", "http://127.0.0.1:3100/EX/approvals/p1");

    private static IReadOnlyList<SessionManager.ScanEntry> Rows(PaperclipScan scan, IReadOnlyList<PaperclipAgent> a,
        PaperclipLink link = PaperclipLink.Connected, string text = "t", string sleeping = "fold", DateTime? now = null) =>
        scan.Entries(a, link, text, sleeping, now ?? Now);

    [Fact]
    public void EmptySnapshot_NoRows()
    {
        Assert.Empty(Rows(new PaperclipScan(), []));
    }

    [Fact]
    public void The_row_carries_what_the_flyout_and_menu_act_on_and_a_parked_row_carries_none_of_it()
    {
        var waiting = A("w", state: "waiting", needs: [Need]);
        var live = Rows(new PaperclipScan(), [waiting])[0].Status;
        Assert.Equal("w", live.PaperclipAgentId);
        Assert.Equal("idle", live.PaperclipAgentStatus);
        Assert.Equal("http://127.0.0.1:3100/EX/agents/w", live.PaperclipAgentUrl);
        Assert.Equal([Need], live.Needs);

        var parked = Rows(new PaperclipScan(), [waiting], PaperclipLink.Unreachable)[0].Status;
        Assert.Equal("w", parked.PaperclipAgentId);
        Assert.Equal("", parked.PaperclipAgentStatus);
        Assert.Empty(parked.Needs);

        // not waiting: no needs on the row even if the agent record holds some
        Assert.Empty(Rows(new PaperclipScan(), [A("i")])[0].Status.Needs);
    }

    [Fact]
    public void KeysLeadsSourceColourAndStamp()
    {
        var rows = Rows(new PaperclipScan(), [A("lead"), A("x", "lead"), A("y", "lead")]);
        Assert.Equal(new[] { "paperclip:lead", "paperclip:x", "paperclip:y" }, rows.Select(r => r.SessionId).ToArray());
        Assert.Equal("", rows[0].Status.Lead);
        Assert.Equal("paperclip:lead", rows[1].Status.Lead);
        Assert.All(rows, r =>
        {
            Assert.Equal(SessionSource.Paperclip, r.Status.Source);
            Assert.Equal(SessionKind.Unknown, r.Status.Kind);
            Assert.Equal("", r.Status.Cwd);
            Assert.Equal(Now.AddHours(-3), r.Written);   // the agent's own last activity, never now
        });
        Assert.Equal(OpenClawSessions.ColourForAgent("x"), rows[1].Status.Color);
        Assert.Equal(rows[1].Status.Color, Rows(new PaperclipScan(), [A("x")])[0].Status.Color);   // stable
    }

    [Fact]
    public void ALeadThatIsNotDrawn_IsNotPointedAt()
    {
        Assert.Equal("", Rows(new PaperclipScan(), [A("x", "ghost")])[0].Status.Lead);
    }

    [Fact]
    public void PresencePerState_AndWaitingUrlIsTheFirstNeed()
    {
        var rows = Rows(new PaperclipScan(), [
            A("asleep"),
            A("work", state: "generating", sleeping: false),
            A("wait", state: "waiting", needs: [Need, new("action", "a", "t", "http://x/inbox")], sleeping: false),
            A("broke", mark: PaperclipMark.Broke, markReason: "boom", sleeping: false),
        ]);
        Assert.Equal(new[] { OrbPresence.Parked, OrbPresence.Present, OrbPresence.NeedsInput, OrbPresence.Present },
            rows.Select(r => r.Status.Presence).ToArray());
        Assert.Equal(new[] { "idle", "generating", "waiting", "idle" }, rows.Select(r => r.Status.State).ToArray());
        Assert.Equal("http://127.0.0.1:3100/EX/approvals/p1", rows[2].Status.Url);
        Assert.Equal("http://127.0.0.1:3100/EX/agents/work", rows[1].Status.Url);
        Assert.Equal((PaperclipMark.Broke, "boom"), (rows[3].Status.Mark, rows[3].Status.MarkReason));
    }

    [Fact]
    public void AWaitingAgentWithNoNeedList_FallsBackToItsPage()
    {
        var r = Rows(new PaperclipScan(), [A("w", state: "waiting", sleeping: false)])[0];
        Assert.Equal("http://127.0.0.1:3100/EX/agents/w", r.Status.Url);
    }

    [Theory]
    [InlineData("NeedsReconnect")]
    [InlineData("Unreachable")]
    public void ALinkThatIsGone_ParksEveryRow_FrozenIdle_NoMarkNoRing_AndSaysWhy(string link)
    {
        var rows = Rows(new PaperclipScan(), [
            A("lead"),
            A("w", "lead", state: "waiting", needs: [Need], sleeping: false, budget: 30),
            A("b", "lead", mark: PaperclipMark.Broke, markReason: "boom", sleeping: false),
        ], Enum.Parse<PaperclipLink>(link), "Reconnect to Paperclip");
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(OrbPresence.Parked, r.Status.Presence);
            Assert.Equal("idle", r.Status.State);
            Assert.Equal(PaperclipMark.None, r.Status.Mark);
            Assert.Null(r.Status.MarkReason);
            Assert.Null(r.Status.BudgetPercent);
            Assert.Equal(new[] { "Reconnect to Paperclip" }, r.Status.HoverLines.ToArray());
        });
        Assert.Equal("paperclip:lead", rows[1].Status.Lead);   // the picture is kept
    }

    [Fact]
    public void Reconnect_BringsBackTheBrokeMarkTheRingAndAmber_FromFreshData()
    {
        var scan = new PaperclipScan();
        var data = new[]
        {
            A("lead"),
            A("b", "lead", mark: PaperclipMark.Broke, markReason: "boom", sleeping: false),
            A("o", "lead", mark: PaperclipMark.OverBudget, markReason: "Paused: over budget", sleeping: false, budget: 100),
            A("w", "lead", state: "waiting", needs: [Need], sleeping: false),
        };
        var gone = Rows(scan, data, PaperclipLink.NeedsReconnect, "Reconnect to Paperclip");
        Assert.All(gone, r => { Assert.Equal(PaperclipMark.None, r.Status.Mark); Assert.Equal(OrbPresence.Parked, r.Status.Presence); });

        var back = Rows(scan, data, PaperclipLink.Connected);
        Assert.Equal(PaperclipMark.Broke, back.Single(r => r.SessionId == "paperclip:b").Status.Mark);
        Assert.Equal(PaperclipMark.OverBudget, back.Single(r => r.SessionId == "paperclip:o").Status.Mark);
        Assert.Equal(100, back.Single(r => r.SessionId == "paperclip:o").Status.BudgetPercent);
        Assert.Equal(OrbPresence.NeedsInput, back.Single(r => r.SessionId == "paperclip:w").Status.Presence);
    }

    [Fact]
    public void StatusDetail_IsTheFirstHoverLine_Short_OrNull()
    {
        var rows = Rows(new PaperclipScan(), [A("w", state: "waiting", needs: [Need], sleeping: false, task: "Fix it"), A("q")]);
        Assert.Equal("Hire QA", rows[0].Status.StatusDetail);
        Assert.Null(rows[1].Status.StatusDetail);
    }

    [Fact]
    public void ConnectingAndConnected_AreNotParked()
    {
        foreach (var l in new[] { PaperclipLink.Connecting, PaperclipLink.Connected })
            Assert.Equal(OrbPresence.NeedsInput,
                Rows(new PaperclipScan(), [A("w", state: "waiting", needs: [Need], sleeping: false)], l)[0].Status.Presence);
    }

    [Fact]
    public void Ring_IsCarried_AndHidden_WhenNull()
    {
        var rows = Rows(new PaperclipScan(), [A("a", budget: 26), A("b")]);
        Assert.Equal(26, rows[0].Status.BudgetPercent);
        Assert.Null(rows[1].Status.BudgetPercent);
    }

    private static IReadOnlyList<PaperclipAgent> Company(int n)
    {
        var l = new List<PaperclipAgent> { A("lead", name: "Lead") };
        for (var i = 1; i < n; i++) l.Add(A("m" + i, "lead", name: "M" + i));
        return l;
    }

    [Fact]
    public void TenAgents_Fold_TheCountAndNamesLandOnTheLead()
    {
        var rows = Rows(new PaperclipScan(), Company(10));
        var lead = Assert.Single(rows);
        Assert.Equal(9, lead.Status.FoldedCount);
        Assert.StartsWith("M1, M2", lead.Status.FoldedNames);
    }

    [Fact]
    public void Show_And_Hide_ModesFlowThrough()
    {
        Assert.Equal(10, Rows(new PaperclipScan(), Company(10), sleeping: "show").Count);
        var hide = Assert.Single(Rows(new PaperclipScan(), Company(10), sleeping: "hide"));
        Assert.Equal(0, hide.Status.FoldedCount);
        Assert.Null(hide.Status.FoldedNames);
    }

    [Fact]
    public void AFoldedCount_IsNotShownWhileParked()
    {
        var rows = Rows(new PaperclipScan(), Company(10), PaperclipLink.NeedsReconnect);
        Assert.Equal(0, Assert.Single(rows).Status.FoldedCount);
    }

    [Fact]
    public void TheHold_KeepsAShortRunUnfolded_ForAMinute_ThenItFoldsAgain()
    {
        var scan = new PaperclipScan();
        var awake = Company(10).ToList();
        awake[3] = A("m3", "lead", state: "generating", sleeping: false, name: "M3");
        Assert.Equal(2, Rows(scan, awake, now: Now).Count);

        var asleep = Company(10);
        Assert.Equal(2, Rows(scan, asleep, now: Now.AddSeconds(30)).Count);
        Assert.Single(Rows(scan, asleep, now: Now.AddSeconds(95)));
    }

    // ---- hover ----

    [Fact]
    public void HoverLines_InThePlansOrder_EachOmittedWhenEmpty()
    {
        var a = A("a", state: "waiting", needs: [Need, new("blocked", "i", "Blocked: EX-6 x", "u")], sleeping: false,
            mark: PaperclipMark.Broke, markReason: "ACP agent failed.", task: "Fix it", activity: "Bash",
            started: Now.AddMinutes(-3), last: "succeeded", budget: 52);
        // generating is needed for the running line; waiting wins the state, so build one that is generating too.
        var gen = a with { State = "generating", Needs = [] };
        Assert.Equal(new[]
        {
            "ACP agent failed.", "Fix it", "Bash", "running for 3 m", "$26.00 of $50.00 this month",
        }, PaperclipHover.Lines(gen, Now).ToArray());

        Assert.Equal(new[]
        {
            "Hire QA", "Blocked: EX-6 x", "ACP agent failed.", "Fix it", "Bash",
            "last run succeeded 3 h ago", "$26.00 of $50.00 this month",
        }, PaperclipHover.Lines(a with { LastRunFinished = Now.AddHours(-3) }, Now).ToArray());
    }

    [Fact]
    public void Hover_NamesTheSubTasksAnAgentIsOnlyWaitingFor()
    {
        Assert.Equal(new[] { "Waiting on 1 sub-task" }, PaperclipHover.Lines(A("a") with { WaitingOnTasks = 1 }, Now).ToArray());
        Assert.Equal(new[] { "Waiting on 3 sub-tasks" }, PaperclipHover.Lines(A("a") with { WaitingOnTasks = 3 }, Now).ToArray());
    }

    [Fact]
    public void Hover_NothingToSay_IsNull_AndLastRunIsNotRepeatedByItsOwnMark()
    {
        Assert.Empty(PaperclipHover.Lines(A("a"), Now));
        var failed = A("a", mark: PaperclipMark.Broke, markReason: "Last run failed: x", last: "failed", sleeping: false);
        Assert.Equal(new[] { "Last run failed: x" }, PaperclipHover.Lines(failed, Now).ToArray());
    }

    [Fact]
    public void LastRun_TakesItsTimeFromTheRunsOwnFinish_NotFromLastActivity()
    {
        // Amber with a run in flight: LastActivity is just now (the live run's
        // start) but the previous run ended hours ago.
        var a = A("a", state: "waiting", needs: [Need], sleeping: false, last: "failed",
            activityAt: Now.AddSeconds(-5), finished: Now.AddHours(-4));
        var line = PaperclipHover.Lines(a, Now).Single(l => l.StartsWith("last run"));
        Assert.Equal("last run failed 4 h ago", line);
    }

    [Fact]
    public void LastRun_WithNoFinishedAt_PrintsTheResultWithNoTime()
    {
        var a = A("a", sleeping: false, last: "succeeded", activityAt: Now.AddSeconds(-5));
        Assert.Equal("last run succeeded", PaperclipHover.Lines(a, Now).Single());
    }

    [Fact]
    public void Hover_MarkWithNoReason_AddsNoBlankLine()
    {
        Assert.Empty(PaperclipHover.Lines(A("a", mark: PaperclipMark.Paused, sleeping: false), Now));
    }

    [Theory]
    [InlineData(0, "0 s")] [InlineData(45, "45 s")] [InlineData(59, "59 s")] [InlineData(60, "1 m")] [InlineData(179, "2 m")]
    [InlineData(3600, "1 h")] [InlineData(7300, "2 h")] [InlineData(86400, "1 d")] [InlineData(200000, "2 d")] [InlineData(-5, "0 s")]
    public void Span_IsTheLargestWholeUnit(int seconds, string expected) =>
        Assert.Equal(expected, PaperclipHover.Span(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Ago_And_Money()
    {
        Assert.Equal("just now", PaperclipHover.Ago(TimeSpan.FromSeconds(2)));
        Assert.Equal("2 h ago", PaperclipHover.Ago(TimeSpan.FromHours(2)));
        Assert.Equal("$0.26", PaperclipHover.Money(26));
        Assert.Equal("$50.00", PaperclipHover.Money(5000));
        Assert.Equal("$1234.56", PaperclipHover.Money(123456));
    }
}
