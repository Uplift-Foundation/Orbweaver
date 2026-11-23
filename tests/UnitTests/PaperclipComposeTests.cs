using Xunit;
using static Orbweaver.Tests.PaperclipFx;

namespace Orbweaver.Tests;

public class PaperclipStateTests
{
    private static PaperclipAgent One(PaperclipApi.ComposeInput i, string id) =>
        PaperclipApi.Compose(i).Single(a => a.Id == id);

    private static PaperclipApi.RawLiveRun Live(string agent, string status = "running", string? issue = null,
        string? msg = null, string? tool = null) =>
        new("L", agent, status, Now.AddMinutes(-3), issue, msg, tool);

    [Fact]
    public void TerminatedAgent_HasNoOrb()
    {
        Assert.Empty(PaperclipApi.Compose(Input([Agent("a", "terminated")])));
    }

    [Theory]
    [InlineData("idle")] [InlineData("active")] [InlineData("paused")] [InlineData("error")] [InlineData("whatever")]
    public void QuietStatuses_AreIdle(string status)
    {
        Assert.Equal("idle", One(Input([Agent("a", status)]), "a").State);
    }

    [Fact]
    public void LiveRunRunningOrQueued_IsGenerating_WithActivityAndStart()
    {
        foreach (var s in new[] { "running", "queued" })
        {
            var a = One(Input([Agent("a")], live: [Live("a", s, tool: "Bash", msg: "m")]), "a");
            Assert.Equal("generating", a.State);
            Assert.Equal("Bash", a.Activity);
            Assert.Equal(Now.AddMinutes(-3), a.RunStarted);
        }
        Assert.Equal("m", One(Input([Agent("a")], live: [Live("a", msg: "m")]), "a").Activity);
    }

    [Fact]
    public void AgentRunningWithNoLiveRow_IsGenerating()
    {
        Assert.Equal("generating", One(Input([Agent("a", "running")]), "a").State);
    }

    [Fact]
    public void LiveRunInOtherStatus_DoesNotCount()
    {
        Assert.Equal("idle", One(Input([Agent("a")], live: [Live("a", "succeeded")]), "a").State);
    }

    [Fact]
    public void PendingApprovalStatusWithNoNeedFound_SynthesisesOne()
    {
        var a = One(Input([Agent("a", "pending_approval")]), "a");
        Assert.Equal("waiting", a.State);
        var n = Assert.Single(a.Needs);
        Assert.Equal("approval", n.Kind);
        Assert.Equal("http://127.0.0.1:3100/EX/approvals/pending", n.Url);
    }

    [Fact]
    public void ANeedBeatsALiveRun_AndTerminatedBeatsEverything()
    {
        var ap = new PaperclipApi.RawApproval("p1", "hire_agent", "pending", "Hire QA", "a", null);
        var i = Input([Agent("a"), Agent("t", "terminated")], live: [Live("a"), Live("t")], approvals: [ap]);
        var all = PaperclipApi.Compose(i);
        Assert.Equal("waiting", all.Single(x => x.Id == "a").State);
        Assert.DoesNotContain(all, x => x.Id == "t");
    }

    [Fact]
    public void TaskLine_ComesFromTheIssueTheLiveRunNames()
    {
        var i = Input([Agent("a")], live: [Live("a", issue: "i1")], tasks: new Dictionary<string, string> { ["i1"] = "Fix it" });
        Assert.Equal("Fix it", One(i, "a").Task);
        Assert.Null(One(Input([Agent("a")], live: [Live("a", issue: "i2")]), "a").Task);
    }

    [Fact]
    public void Sleeping_OnlyWhenIdleUnmarkedAndNotWaiting()
    {
        Assert.True(One(Input([Agent("a")]), "a").Sleeping);
        Assert.False(One(Input([Agent("a", "paused")]), "a").Sleeping);
        Assert.False(One(Input([Agent("a")], live: [Live("a")]), "a").Sleeping);
    }

    // ---- marks ----

    [Fact]
    public void Mark_PausedByBudget_PausedOther_Error()
    {
        var b = One(Input([Agent("a", "paused", pauseReason: "budget")]), "a");
        Assert.Equal((PaperclipMark.OverBudget, "Paused: over budget"), (b.Mark, b.MarkReason));
        var m = One(Input([Agent("a", "paused", pauseReason: "manual")]), "a");
        Assert.Equal((PaperclipMark.Paused, "Paused: paused by hand"), (m.Mark, m.MarkReason));
        Assert.Equal("Paused: paused by Paperclip", One(Input([Agent("a", "paused", pauseReason: "system")]), "a").MarkReason);
        Assert.Equal("Paused: company archived", One(Input([Agent("a", "paused", pauseReason: "company_archived")]), "a").MarkReason);
        Assert.Equal("Paused: odd thing", One(Input([Agent("a", "paused", pauseReason: "odd_thing")]), "a").MarkReason);
        Assert.Equal("Paused", One(Input([Agent("a", "paused")]), "a").MarkReason);
        var e = One(Input([Agent("a", "error", error: "boom")]), "a");
        Assert.Equal((PaperclipMark.Broke, "boom"), (e.Mark, e.MarkReason));
        Assert.Equal("Error", One(Input([Agent("a", "error")]), "a").MarkReason);
        Assert.Equal(140, One(Input([Agent("a", "error", error: new string('x', 400))]), "a").MarkReason!.Length);
    }

    [Fact]
    public void NativeFixture_BothErrorAgentsAreBroke_AndTheBlockedOneIsAmberAndBroke()
    {
        var agents = PaperclipApi.ParseAgents(Read("native/agents.json"));
        var blocked = PaperclipApi.ParseIssues(Read("native/issues-blocked.json"));
        var all = PaperclipApi.Compose(Input(agents, blocked: blocked));
        Assert.Equal(4, all.Count);
        Assert.Equal(2, all.Count(a => a.Mark == PaperclipMark.Broke));
        Assert.All(all.Where(a => a.Mark == PaperclipMark.Broke), b => Assert.False(string.IsNullOrEmpty(b.MarkReason)));
        // The real agent that is both in error and the assignee of a blocked task.
        var both = all.Single(a => a.Id.EndsWith("4"));
        Assert.Equal(("waiting", PaperclipMark.Broke), (both.State, both.Mark));
        var need = Assert.Single(both.Needs);
        Assert.Equal(("blocked", "Blocked: EX-6 Example task"), (need.Kind, need.Title));
    }

    [Theory]
    [InlineData("runner span: runner.process.launch (124 ms)", true)]
    [InlineData("Runner Span: x", true)]
    [InlineData("runner.process.launch", true)]
    [InlineData("runner.process.launch (124 ms)", true)]
    [InlineData("Receiving agent output", false)]
    [InlineData("Done. Next up", false)]
    [InlineData("Reading the repo.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void InternalLookingStatusMessages_AreRecognised(string? msg, bool internalText) =>
        Assert.Equal(internalText, PaperclipApi.LooksInternal(msg));

    [Fact]
    public void Activity_PrefersToolName_DropsInternalSpanText_KeepsASentence()
    {
        Assert.Equal("Bash", One(Input([Agent("a")], live: [Live("a", msg: "runner span: x", tool: "Bash")]), "a").Activity);
        Assert.Null(One(Input([Agent("a")], live: [Live("a", msg: "runner span: runner.process.launch (124 ms)")]), "a").Activity);
        Assert.Equal("Receiving agent output", One(Input([Agent("a")], live: [Live("a", msg: "Receiving agent output")]), "a").Activity);
    }

    [Fact]
    public void NewestFinishedRun_FailedOrTimedOut_IsBroke_ButCancelledInterruptedAreNot()
    {
        var f = One(Input([Agent("a")], runs: [Run("a", "failed", "adapter_failed")]), "a");
        Assert.Equal((PaperclipMark.Broke, "Last run failed: adapter_failed"), (f.Mark, f.MarkReason));
        Assert.Equal("Last run failed", One(Input([Agent("a")], runs: [Run("a", "failed")]), "a").MarkReason);
        Assert.Equal("Last run timed out", One(Input([Agent("a")], runs: [Run("a", "timed_out")]), "a").MarkReason);
        foreach (var s in new[] { "cancelled", "interrupted", "succeeded" })
            Assert.Equal(PaperclipMark.None, One(Input([Agent("a")], runs: [Run("a", s)]), "a").Mark);
    }

    [Fact]
    public void BrokeClearsOnANewerSucceededRun_BecauseRunsAreNewestFirst()
    {
        var i = Input([Agent("a")], runs: [Run("a", "succeeded", id: "new"), Run("a", "failed", id: "old")]);
        Assert.Equal(PaperclipMark.None, One(i, "a").Mark);
        Assert.Equal("succeeded", One(i, "a").LastResult);
    }

    [Fact]
    public void RunsStillInFlight_AreNotTheNewestFinished()
    {
        var i = Input([Agent("a")], runs: [Run("a", "running", id: "n"), Run("a", "queued", id: "q"),
            Run("a", "scheduled_retry", id: "s"), Run("a", "", id: "e"), Run("a", "failed", id: "old")]);
        Assert.Equal(PaperclipMark.Broke, One(i, "a").Mark);
    }

    [Fact]
    public void ARunsErrorTextNeverReachesAnyOutput()
    {
        // The parser does not read `error`, so a command in it cannot surface.
        var json = "[{\"id\":\"r\",\"agentId\":\"a\",\"status\":\"failed\",\"errorCode\":\"adapter_failed\",\"error\":\"SECRET-COMMAND --token abc\"}]";
        var a = One(Input([Agent("a")], runs: PaperclipApi.ParseRuns(json)), "a");
        Assert.DoesNotContain("SECRET", System.Text.Json.JsonSerializer.Serialize(a));
    }

    [Theory]
    [InlineData("succeeded", "succeeded")] [InlineData("failed", "failed")] [InlineData("timed_out", "timed out")]
    [InlineData("cancelled", "cancelled")] [InlineData("interrupted", "interrupted")]
    [InlineData("scheduled_retry", "retry scheduled")] [InlineData("zzz", "zzz")]
    public void LastResultWording(string status, string words) => Assert.Equal(words, PaperclipApi.ResultWording(status));

    [Fact]
    public void RunPidAndSession_AreCarriedFromTheNewestFinishedRun()
    {
        var run = new PaperclipApi.RawRun("r", "a", "succeeded", Now, Now, null, null, 41486, "sess", false);
        var a = One(Input([Agent("a")], runs: [run]), "a");
        Assert.Equal((41486, "sess"), (a.RunPid, a.RunSessionId));
    }

    [Fact]
    public void LastActivity_IsTheNewestTimestamp_NeverNowUnlessNothingElse()
    {
        var hb = Now.AddHours(-3);
        var ag = Agent("a") with { LastHeartbeatAt = hb };
        Assert.Equal(hb, One(Input([ag]), "a").LastActivity);
        Assert.Equal(Now, One(Input([Agent("b")]), "b").LastActivity);
    }

    [Fact]
    public void Ring_OnlyWithCapAndSpend_CappedAt100()
    {
        Assert.Null(PaperclipApi.RingPercent(0, 0));
        Assert.Null(PaperclipApi.RingPercent(5000, 0));
        Assert.Null(PaperclipApi.RingPercent(0, 300));
        Assert.Equal(1, PaperclipApi.RingPercent(5000, 26));
        Assert.Equal(52, PaperclipApi.RingPercent(5000, 2600));
        Assert.Equal(100, PaperclipApi.RingPercent(5000, 9000));
        Assert.Equal(26, One(Input([Agent("a", cap: 5000, spent: 1300)]), "a").BudgetPercent);
    }

    // ---- needs ----

    private static PaperclipApi.RawApproval Ap(string id, string type = "request_board_approval", string? title = null,
        string? payloadAgent = null, string? by = null) => new(id, type, "pending", title, payloadAgent, by);

    private static readonly PaperclipApi.RawAgent[] Team = [Agent("lead"), Agent("x", reportsTo: "lead"), Agent("y", reportsTo: "lead")];

    [Fact]
    public void Approval_GoesToPayloadAgent_ThenRequester_ThenTopLead()
    {
        var all = PaperclipApi.Compose(Input(Team, approvals: [Ap("1", payloadAgent: "x", by: "y"), Ap("2", by: "y"), Ap("3"),
            Ap("4", payloadAgent: "ghost", by: "ghost")]));
        Assert.Equal(new[] { "1" }, all.Single(a => a.Id == "x").Needs.Select(n => n.Id).ToArray());
        Assert.Equal(new[] { "2" }, all.Single(a => a.Id == "y").Needs.Select(n => n.Id).ToArray());
        Assert.Equal(new[] { "3", "4" }, all.Single(a => a.Id == "lead").Needs.Select(n => n.Id).ToArray());
    }

    [Theory]
    [InlineData("hire_agent", "Hire")] [InlineData("budget_override_required", "Budget override")]
    [InlineData("approve_ceo_strategy", "Strategy")] [InlineData("request_board_approval", "Approval")] [InlineData("zzz", "Approval")]
    public void ApprovalWording_PerType(string type, string words)
    {
        var n = Assert.Single(PaperclipApi.Compose(Input([Agent("a")], approvals: [Ap("1", type, payloadAgent: "a")]))[0].Needs);
        Assert.Equal(words, n.Title);
        Assert.Equal("http://127.0.0.1:3100/EX/approvals/1", n.Url);
    }

    [Fact]
    public void ApprovalTitle_Wins_WhenThePayloadHasOne()
    {
        var n = Assert.Single(PaperclipApi.Compose(Input([Agent("a")], approvals: [Ap("1", title: "Hire QA", payloadAgent: "a")]))[0].Needs);
        Assert.Equal("Hire QA", n.Title);
    }

    // Invented rows: the tool action request shape has never been captured.
    [Fact]
    public void ToolAction_GoesToItsAgent_OrTheLead_WithRequestsUrl()
    {
        var all = PaperclipApi.Compose(Input(Team, actions: [new("a1", "x", "Post a comment"), new("a2", null, null), new("a3", "ghost", "t")]));
        var x = Assert.Single(all.Single(a => a.Id == "x").Needs);
        Assert.Equal(("action", "Post a comment", "http://127.0.0.1:3100/EX/inbox/requests"), (x.Kind, x.Title, x.Url));
        var lead = all.Single(a => a.Id == "lead").Needs;
        Assert.Equal(new[] { "Tool action", "t" }, lead.Select(n => n.Title).ToArray());
    }

    [Fact]
    public void BlockedIssue_AssignedOnly_UnassignedAndUnknownIgnored()
    {
        var issues = new PaperclipApi.RawIssue[]
        {
            new("i1", "EX-1", "Do it", "blocked", "x", "needs_attention"),
            new("i2", "EX-2", "Nobody", "blocked", null),
            new("i3", "EX-3", "Ghost", "blocked", "ghost"),
            new("i4", "", "", "blocked", "y"),
        };
        var all = PaperclipApi.Compose(Input(Team, blocked: issues));
        var x = Assert.Single(all.Single(a => a.Id == "x").Needs);
        Assert.Equal(("blocked", "Blocked: EX-1 Do it", "http://127.0.0.1:3100/EX/issues/i1"), (x.Kind, x.Title, x.Url));
        Assert.Equal("Blocked: ", all.Single(a => a.Id == "y").Needs[0].Title);
        Assert.Empty(all.Single(a => a.Id == "lead").Needs);
    }

    [Fact]
    public void BlockedIssue_OnlyNeedsAPersonWhenPaperclipSaysSo()
    {
        // Edited copies of the real row: the other verdicts have never been captured.
        PaperclipApi.RawIssue Row(string? state) => new("i", "EX-1", "T", "blocked", "x", state);
        Assert.Single(PaperclipApi.Compose(Input(Team, blocked: [Row("needs_attention")])).Single(a => a.Id == "x").Needs);
        Assert.Single(PaperclipApi.Compose(Input(Team, blocked: [Row(null)])).Single(a => a.Id == "x").Needs);
        Assert.Empty(PaperclipApi.Compose(Input(Team, blocked: [Row("covered")])).Single(a => a.Id == "x").Needs);
        Assert.Equal("idle", PaperclipApi.Compose(Input(Team, blocked: [Row("covered")])).Single(a => a.Id == "x").State);
    }

    [Fact]
    public void NoLead_NeedWithNowhereToGo_IsDropped()
    {
        var cyc = new[] { Agent("a", reportsTo: "b"), Agent("b", reportsTo: "a") };
        // both report to a known agent, so "top lead" falls to the first with an unknown lead: none.
        var all = PaperclipApi.Compose(Input(cyc, approvals: [Ap("1")]));
        Assert.All(all, a => Assert.Empty(a.Needs));
    }

    [Fact]
    public void Urls_EscapeAndTolerateNoPrefix()
    {
        Assert.Equal("http://h:1/EX/agents/a%20b", PaperclipApi.AgentUrl("http://h:1/", "EX", "a b"));
        Assert.Equal("http://h:1/issues/i", PaperclipApi.IssueUrl("http://h:1", "", "i"));
    }
}

public class PaperclipFoldTests
{
    private static PaperclipAgent A(string id, string? lead = null, bool sleeping = true, PaperclipMark mark = PaperclipMark.None) =>
        new(id, id, "", "", "idle", lead, "", sleeping ? "idle" : "generating", mark, null, [], null, null, null, null, null,
            DateTime.UtcNow, "", sleeping && mark == PaperclipMark.None);

    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    private static IReadOnlyList<PaperclipAgent> Company(int total)
    {
        var l = new List<PaperclipAgent> { A("lead") };
        for (var i = 1; i < total; i++) l.Add(A("m" + i, "lead"));
        return l;
    }

    [Fact]
    public void AtTheLimit_EveryoneIsDrawn()
    {
        var r = PaperclipApi.Fold(Company(8), "fold", 8, None);
        Assert.Equal(8, r.Drawn.Count);
        Assert.Empty(r.FoldedInto);
    }

    [Fact]
    public void OverTheLimit_SleepingNonLeadsFoldIntoTheirLead_WithNames()
    {
        var r = PaperclipApi.Fold(Company(9), "fold", 8, None);
        Assert.Equal(new[] { "lead" }, r.Drawn.Select(a => a.Id).ToArray());
        var (count, names) = r.FoldedInto["lead"];
        Assert.Equal(8, count);
        Assert.Equal("m1", names[0]);
    }

    [Fact]
    public void Unfolds_OnGenerating_Waiting_EachMark_AndRecentlyAwake()
    {
        var c = Company(9).ToList();
        c[1] = A("m1", "lead", sleeping: false);
        c[2] = A("m2", "lead", mark: PaperclipMark.Broke);
        c[3] = A("m3", "lead", mark: PaperclipMark.Paused);
        c[4] = A("m4", "lead", mark: PaperclipMark.OverBudget);
        var r = PaperclipApi.Fold(c, "fold", 8, new HashSet<string> { "m5" });
        Assert.Equal(new[] { "lead", "m1", "m2", "m3", "m4", "m5" }, r.Drawn.Select(a => a.Id).ToArray());
        Assert.Equal(3, r.FoldedInto["lead"].Count);
    }

    [Fact]
    public void LeadsAndLeadlessAgents_NeverFold_ThreeLevelChainFoldsOnlyTheLeaf()
    {
        var c = new List<PaperclipAgent> { A("ceo"), A("mid", "ceo"), A("leaf", "mid"), A("loner") };
        for (var i = 0; i < 6; i++) c.Add(A("x" + i, "ceo", sleeping: false));
        var r = PaperclipApi.Fold(c, "fold", 8, None);
        Assert.Contains(r.Drawn, a => a.Id == "ceo");
        Assert.Contains(r.Drawn, a => a.Id == "mid");
        Assert.Contains(r.Drawn, a => a.Id == "loner");
        Assert.DoesNotContain(r.Drawn, a => a.Id == "leaf");
        Assert.Equal(1, r.FoldedInto["mid"].Count);
    }

    [Fact]
    public void AgentWhoseLeadIsNotListed_DoesNotFold()
    {
        var c = Company(9).Append(A("orphan", "ghost")).ToList();
        Assert.Contains(PaperclipApi.Fold(c, "fold", 8, None).Drawn, a => a.Id == "orphan");
    }

    [Fact]
    public void Show_DrawsEveryone_Hide_DropsWithoutCounting_UnknownModeIsFold()
    {
        Assert.Equal(12, PaperclipApi.Fold(Company(12), "show", 8, None).Drawn.Count);
        var h = PaperclipApi.Fold(Company(12), "hide", 8, None);
        Assert.Single(h.Drawn);
        Assert.Empty(h.FoldedInto);
        Assert.Equal(11, PaperclipApi.Fold(Company(12), "banana", 8, None).FoldedInto["lead"].Count);
    }

    [Fact]
    public void Hide_StillShowsTheOnesThatAreAwake()
    {
        var c = Company(12).ToList();
        c[3] = A("m3", "lead", sleeping: false);
        Assert.Equal(new[] { "lead", "m3" }, PaperclipApi.Fold(c, "hide", 8, None).Drawn.Select(a => a.Id).ToArray());
    }

    [Fact]
    public void TrackAwake_StampsNow_HoldsForAMinute_ThenReleases()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var busy = new[] { A("a", "l", sleeping: false), A("b", "l") };
        var (s1, awake1) = PaperclipApi.TrackAwake(new Dictionary<string, DateTime>(), busy, t0);
        Assert.Equal(new[] { "a" }, awake1.ToArray());

        var quiet = new[] { A("a", "l"), A("b", "l") };
        var (s2, awake2) = PaperclipApi.TrackAwake(s1, quiet, t0.AddSeconds(30));
        Assert.Contains("a", awake2);
        var (s3, awake3) = PaperclipApi.TrackAwake(s2, quiet, t0.AddSeconds(61));
        Assert.Empty(awake3);
        Assert.False(s3.ContainsKey("b"));
    }
}

public class PaperclipRunProcessTests
{
    private static readonly IReadOnlySet<int> NoPids = new HashSet<int>();
    private static readonly IReadOnlySet<string> NoSessions = new HashSet<string>();

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void TheThreeRealLegacyHookFiles_AreRunProcesses(int n)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(PaperclipFx.Read($"legacy-claude-local/hook-status-during-run-{n}.json"));
        var r = doc.RootElement;
        Assert.True(PaperclipApi.IsRunProcess(r.GetProperty("entrypoint").GetString(), r.GetProperty("cwd").GetString(),
            r.GetProperty("session_pid").GetInt32(), null, NoPids, NoSessions));
    }

    [Fact]
    public void ThePreconfiguredModeRow_SdkTsInAnIsolatedWorkspace_IsARunProcess()
    {
        // Built from the measured values (plan 1.6): the status file itself was
        // never captured, so sdk-ts is inferred from the transcript, not observed.
        Assert.True(PaperclipApi.IsRunProcess("sdk-ts",
            "/Users/u/.paperclip/instances/default/isolated-workspaces/co/uuid", null, null, NoPids, NoSessions));
        Assert.False(PaperclipApi.IsRunProcess("sdk-ts", "/Users/u/src/repo", null, null, NoPids, NoSessions));
    }

    [Fact]
    public void Negatives()
    {
        const string ws = "/Users/u/.paperclip/instances/default/workspaces/abc";
        Assert.False(PaperclipApi.IsRunProcess("cli", ws, 1, null, NoPids, NoSessions));                  // not sdk-cli
        Assert.False(PaperclipApi.IsRunProcess(null, ws, 1, null, NoPids, NoSessions));
        Assert.False(PaperclipApi.IsRunProcess("sdk-cli", "/Users/u/src/repo", 1, "s", NoPids, NoSessions)); // ordinary repo
        Assert.False(PaperclipApi.IsRunProcess("sdk-cli", "/Users/u/.paperclip/instances/default/data", 1, null, NoPids, NoSessions));
        Assert.False(PaperclipApi.IsRunProcess("sdk-cli", "/Users/u/.paperclip/instances", 1, null, NoPids, NoSessions));
        Assert.False(PaperclipApi.IsRunProcess("sdk-cli", "", null, null, NoPids, NoSessions));
        Assert.False(PaperclipApi.IsRunProcess("sdk-cli", null, null, "", NoPids, NoSessions));
    }

    [Fact]
    public void KnownPidOrSession_MatchesAnywhere_ButOnlyForSdkCli()
    {
        var pids = new HashSet<int> { 41486 };
        var sess = new HashSet<string> { "s1" };
        Assert.True(PaperclipApi.IsRunProcess("sdk-cli", "/repo", 41486, null, pids, NoSessions));
        Assert.True(PaperclipApi.IsRunProcess("sdk-cli", "/repo", null, "s1", NoPids, sess));
        Assert.False(PaperclipApi.IsRunProcess("cli", "/repo", 41486, "s1", pids, sess));
        Assert.False(PaperclipApi.IsRunProcess("sdk-cli", "/repo", 9, "other", pids, sess));
    }

    [Fact]
    public void WindowsSeparatorsAndIsolatedWorkspaces()
    {
        Assert.True(PaperclipApi.IsRunProcess("sdk-cli", @"C:\Users\u\.paperclip\instances\default\workspaces\abc", null, null, NoPids, NoSessions));
        Assert.True(PaperclipApi.IsRunProcess("sdk-cli", "/h/.paperclip/instances/prod/isolated-workspaces/co/uuid", null, null, NoPids, NoSessions));
    }
}
