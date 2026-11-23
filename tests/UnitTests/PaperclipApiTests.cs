using Xunit;

namespace Orbweaver.Tests;

internal static class PaperclipFx
{
    internal static string Read(string rel) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PaperclipFixtures", rel));

    internal const string Base = "http://127.0.0.1:3100";
    internal static readonly DateTime Now = new(2026, 11, 23, 4, 0, 0, DateTimeKind.Utc);

    internal static PaperclipApi.RawAgent Agent(string id, string status = "idle", string? reportsTo = null,
        string? pauseReason = null, string? error = null, long cap = 0, long spent = 0, string name = "")
        => new(id, name == "" ? id : name, "engineer", "Engineer", status, reportsTo, "paperclip_runner",
            cap, spent, pauseReason, error, null);

    internal static PaperclipApi.ComposeInput Input(
        IReadOnlyList<PaperclipApi.RawAgent> agents,
        IReadOnlyList<PaperclipApi.RawLiveRun>? live = null,
        IReadOnlyList<PaperclipApi.RawRun>? runs = null,
        IReadOnlyList<PaperclipApi.RawApproval>? approvals = null,
        IReadOnlyList<PaperclipApi.RawActionRequest>? actions = null,
        IReadOnlyList<PaperclipApi.RawIssue>? blocked = null,
        IReadOnlyDictionary<string, string>? tasks = null) =>
        new(agents, live ?? [], runs ?? [], approvals ?? [], actions ?? [], blocked ?? [],
            tasks ?? new Dictionary<string, string>(), Base, "EX", Now);

    internal static PaperclipApi.RawRun Run(string agent, string status, string? code = null, string id = "r") =>
        new(id, agent, status, Now, Now, code, null, null, null, false);
}

public class PaperclipApiParseTests
{
    [Fact]
    public void NativeAgents_FourAgentsThreeReportToOne_ErrorAgentKeepsItsReason()
    {
        var agents = PaperclipApi.ParseAgents(PaperclipFx.Read("native/agents.json"));
        Assert.Equal(4, agents.Count);
        var lead = agents.Single(a => a.ReportsTo == null);
        Assert.Equal(3, agents.Count(a => a.ReportsTo == lead.Id));
        Assert.All(agents, a => Assert.Equal("paperclip_runner", a.AdapterType));
        var broken = agents.Where(a => a.Status == "error").ToList();
        Assert.Equal(2, broken.Count);
        Assert.All(broken, b => Assert.False(string.IsNullOrEmpty(b.ErrorReason)));
        Assert.All(agents, a => Assert.Equal(5000, a.BudgetCents));
    }

    [Fact]
    public void LegacyAgents_Parse()
    {
        var agents = PaperclipApi.ParseAgents(PaperclipFx.Read("legacy-claude-local/agents.json"));
        Assert.Equal(4, agents.Count);
        Assert.All(agents, a => Assert.Equal("claude_local", a.AdapterType));
    }

    [Fact]
    public void NativeRuns_RedactedAndFullRowsInOneListing()
    {
        var runs = PaperclipApi.ParseRuns(PaperclipFx.Read("native/heartbeat-runs.json"));
        Assert.Equal(3, runs.Count);
        // newest first: a full failed native row, a redacted row, a legacy success
        Assert.False(runs[0].Redacted);
        Assert.Equal(("failed", "native_provider_terminal_failed", null), (runs[0].Status, runs[0].ErrorCode, runs[0].ProcessPid));
        Assert.True(runs[1].Redacted);
        Assert.Equal("failed", runs[1].Status);
        Assert.Null(runs[1].ErrorCode);
        Assert.Equal(("succeeded", 41486), (runs[2].Status, runs[2].ProcessPid));
    }

    [Fact]
    public void LegacyRuns_CarryPidAndSession()
    {
        var runs = PaperclipApi.ParseRuns(PaperclipFx.Read("legacy-claude-local/heartbeat-runs.json"));
        Assert.Contains(runs, r => r.ProcessPid != null && r.SessionIdAfter != null);
    }

    [Fact]
    public void LegacyLiveRun_HasActivityFields()
    {
        var live = PaperclipApi.ParseLiveRuns(PaperclipFx.Read("legacy-claude-local/live-runs-running.json"));
        var r = Assert.Single(live);
        Assert.Equal("running", r.Status);
        Assert.Equal("Receiving agent output", r.StatusMessage);
    }

    [Fact]
    public void DecidedApprovals_BothParse()
    {
        var a = PaperclipApi.ParseApprovals(PaperclipFx.Read("native/approvals-decided.json"));
        Assert.Equal(new[] { "approved", "rejected" }, a.Select(x => x.Status).ToArray());
        Assert.All(a, x => Assert.NotNull(x.PayloadAgentId));
        Assert.All(a, x => Assert.Equal("request_board_approval", x.Type));
    }

    [Fact]
    public void Companies_Health_RuntimeInfo_Config()
    {
        var c = Assert.Single(PaperclipApi.ParseCompanies(PaperclipFx.Read("companies.json")));
        Assert.Equal("EX", c.IssuePrefix);
        var h = PaperclipApi.ParseHealth(PaperclipFx.Read("health.json"))!;
        Assert.Equal(("ok", "authenticated"), (h.Status, h.DeploymentMode));
        Assert.Equal(3100, PaperclipApi.ParseRuntimeInfo(PaperclipFx.Read("native/runtime-info.json"))!.Port);
        Assert.Equal(3100, PaperclipApi.ParseConfigPort(PaperclipFx.Read("native/config-server-section.json"))!.Port);
    }

    [Fact]
    public void EmptyLists_AreEmpty()
    {
        Assert.Empty(PaperclipApi.ParseLiveRuns(PaperclipFx.Read("native/live-runs-idle.json")));
        Assert.Empty(PaperclipApi.ParseApprovals(PaperclipFx.Read("native/approvals-pending-empty.json")));
        Assert.Empty(PaperclipApi.ParseActionRequests(PaperclipFx.Read("native/tool-action-requests-empty.json")));
    }

    [Fact]
    public void RealBlockedIssue_CarriesItsAttentionVerdictAndAssignee()
    {
        var i = Assert.Single(PaperclipApi.ParseIssues(PaperclipFx.Read("native/issues-blocked.json")));
        Assert.Equal(("blocked", "EX-6", "needs_attention"), (i.Status, i.Identifier, i.BlockerState));
        Assert.EndsWith("4", i.AssigneeAgentId);
        Assert.Null(Assert.Single(PaperclipApi.ParseIssues("[{\"id\":\"i\",\"blockerAttention\":5}]")).BlockerState);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[1,2,\"x\",null]")]
    [InlineData("[{}]")]
    [InlineData("[{\"id\":5,\"agentId\":7}]")]
    [InlineData("[{\"status\":\"running\"}]")]
    [InlineData("{\"actionRequests\":5}")]
    [InlineData("{\"actionRequests\":[1,{}]}")]
    [InlineData("[{\"id\":\"a\",\"payload\":5}")]
    [InlineData("not json {")]
    public void Malformed_NeverThrowsAndYieldsNothing(string? json)
    {
        Assert.Empty(PaperclipApi.ParseAgents(json));
        Assert.Empty(PaperclipApi.ParseLiveRuns(json));
        Assert.Empty(PaperclipApi.ParseRuns(json));
        Assert.Empty(PaperclipApi.ParseCompanies(json));
        Assert.Empty(PaperclipApi.ParseIssues(json));
        Assert.Empty(PaperclipApi.ParseActionRequests(json));
        Assert.Null(PaperclipApi.ParseHealth(json));
        Assert.Null(PaperclipApi.ParseChallenge(json));
        Assert.Null(PaperclipApi.ParseChallengeStatus(json));
        Assert.Null(PaperclipApi.ParseRuntimeInfo(json));
        Assert.Null(PaperclipApi.ParseConfigPort(json));
    }

    [Fact]
    public void ApprovalWithBadPayload_StillParses()
    {
        var a = Assert.Single(PaperclipApi.ParseApprovals("[{\"id\":\"a\",\"payload\":5,\"type\":\"hire_agent\"}]"));
        Assert.Null(a.Title);
    }

    [Fact]
    public void PortOutOfRange_IsRejected()
    {
        Assert.Null(PaperclipApi.ParseRuntimeInfo("{\"port\":0}"));
        Assert.Null(PaperclipApi.ParseRuntimeInfo("{\"port\":70000}"));
        Assert.Null(PaperclipApi.ParseConfigPort("{\"server\":5}"));
        Assert.Null(PaperclipApi.ParseConfigPort("{\"server\":{\"port\":0}}"));
    }

    [Fact]
    public void Challenge_Parses_AndClampsPollInterval()
    {
        const string j = "{\"id\":\"c\",\"token\":\"t\",\"boardApiToken\":\"k\",\"approvalUrl\":\"http://x/y\",\"pollPath\":\"/api/p\",\"expiresAt\":\"2026-11-23T04:10:00Z\",\"suggestedPollIntervalMs\":1}";
        var c = PaperclipApi.ParseChallenge(j)!;
        Assert.Equal(250, c.PollIntervalMs);
        Assert.NotNull(c.ExpiresAt);
        Assert.Equal(1000, PaperclipApi.ParseChallenge(j.Replace(",\"suggestedPollIntervalMs\":1", ""))!.PollIntervalMs);
        Assert.Null(PaperclipApi.ParseChallenge("{\"id\":\"c\"}"));
        Assert.Equal("approved", PaperclipApi.ParseChallengeStatus("{\"status\":\"approved\"}"));
    }

    [Fact]
    public void ActionRequests_TryTheLikelyKeys_AndSkipNonPending()
    {
        const string j = "{\"actionRequests\":[" +
            "{\"id\":\"1\",\"agentId\":\"A\",\"summary\":\"Post a comment\"}," +
            "{\"id\":\"2\",\"requestedByAgentId\":\"B\",\"toolName\":\"jira.comment\"}," +
            "{\"id\":\"3\",\"agent\":{\"id\":\"C\"},\"tool\":\"t\"}," +
            "{\"id\":\"4\",\"title\":\"x\"}," +
            "{\"id\":\"5\",\"status\":\"approved\",\"agentId\":\"A\"}," +
            "{\"id\":\"6\",\"status\":\"pending\",\"agentId\":\"A\"}," +
            "{\"summary\":\"no id\"}]}";
        var r = PaperclipApi.ParseActionRequests(j);
        Assert.Equal(new[] { "1", "2", "3", "4", "6" }, r.Select(x => x.Id).ToArray());
        Assert.Equal(new string?[] { "A", "B", "C", null, "A" }, r.Select(x => x.AgentId).ToArray());
        Assert.Equal("Post a comment", r[0].Summary);
        Assert.Equal("jira.comment", r[1].Summary);
        Assert.Equal("t", r[2].Summary);
        Assert.Equal("x", r[3].Summary);
        Assert.Null(r[4].Summary);
    }

    [Fact]
    public void Time_ParsesIso_RejectsJunk()
    {
        Assert.Equal(new DateTime(2026, 11, 23, 3, 1, 19, 419, DateTimeKind.Utc), PaperclipApi.Time("2026-11-23T03:01:19.419Z"));
        Assert.Null(PaperclipApi.Time("soon"));
        Assert.Null(PaperclipApi.Time(null));
    }

    [Fact]
    public void Numbers_AcceptFloatsAndIgnoreWrongTypes()
    {
        var a = PaperclipApi.ParseAgents("[{\"id\":\"a\",\"budgetMonthlyCents\":5000.0,\"spentMonthlyCents\":\"x\"}]");
        Assert.Equal(5000, a[0].BudgetCents);
        Assert.Equal(0, a[0].SpentCents);
    }
}
