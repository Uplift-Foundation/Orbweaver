using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// How an approval ended, end to end through TakeEnded: every ending carries a
// status and a reason, from the three places one can come from (a resolved
// broadcast, a session.approval terminal event, a feed that stopped listing it).
// The broadcast's own shape is declared, not measured (see OpenClawApprovalsTests);
// what is pinned here is what this app does with whichever status arrives.
[Collection("Settings")]
public class OpenClawApprovalEndStatusTests : IDisposable
{
    private const string Key = "agent:alpha:dashboard:11111111-2222-3333-4444-555555555555";

    private static readonly DateTime Now = new(
        DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private readonly bool _approve = OrbweaverSettings.OpenClawApproveEnabled;

    public OpenClawApprovalEndStatusTests()
    {
        OrbweaverSettings.OpenClawApproveEnabled = true;
        OpenClawSessions.Restart();
        OpenClawSessions.ForgetApprovalsForTests();
    }

    public void Dispose()
    {
        OpenClawSessions.Restart();
        OpenClawSessions.ForgetApprovalsForTests();
        OrbweaverSettings.OpenClawApproveEnabled = _approve;
    }

    private static string Row(string id, DateTime? expires = null) =>
        "{\"id\":\"" + id + "\",\"approvalKind\":\"exec\",\"request\":{\"command\":\"ls -la /tmp\"," +
        "\"host\":\"gateway\",\"agentId\":\"alpha\",\"sessionKey\":\"" + Key + "\"}," +
        "\"createdAtMs\":" + Ms(Now) + ",\"expiresAtMs\":" + Ms(expires ?? Now.AddMinutes(30)) + "}";

    private static void Requested(string id, DateTime? expires = null) =>
        OpenClawSessions.OnEvent("exec.approval.requested", Json(Row(id, expires)), Now);

    private static string Snapshot(string id) =>
        "{\"id\":\"" + id + "\",\"status\":\"pending\",\"createdAtMs\":" + Ms(Now) + ",\"expiresAtMs\":" +
        Ms(Now.AddMinutes(30)) + ",\"presentation\":{\"kind\":\"exec\",\"commandText\":\"ls\",\"agentId\":\"alpha\"," +
        "\"allowedDecisions\":[\"allow-once\",\"deny\"]}}";

    private static void SessionPending(string id) =>
        OpenClawSessions.OnEvent("session.approval", Json(
            "{\"sessionKey\":\"" + Key + "\",\"updatedAtMs\":" + Ms(Now) + ",\"phase\":\"pending\",\"approval\":" +
            Snapshot(id) + "}"), Now);

    [Theory]
    [InlineData("allowed", "allow-once", "user")]
    [InlineData("denied", "deny", "user")]
    [InlineData("expired", null, "timeout")]
    [InlineData("cancelled", null, "superseded")]
    public void AResolvedBroadcastCarriesItsStatusAndReason(string status, string? decision, string reason)
    {
        Requested("a1");
        var decisionJson = decision is null ? "" : ",\"decision\":\"" + decision + "\"";

        OpenClawSessions.OnEvent("exec.approval.resolved", Json(
            "{\"id\":\"a1\",\"status\":\"" + status + "\",\"reason\":\"" + reason + "\"" + decisionJson + "}"), Now);

        var ended = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal("resolved", ended.Cause);
        Assert.Equal(status, ended.Status);
        Assert.Equal(reason, ended.Reason);
        Assert.Equal(decision, ended.Decision);
    }

    [Theory]
    [InlineData("allowed")]
    [InlineData("denied")]
    [InlineData("expired")]
    [InlineData("cancelled")]
    public void ASessionTerminalEventCarriesItsStatusAndReason(string status)
    {
        SessionPending("s1");

        OpenClawSessions.OnEvent("session.approval", Json(
            "{\"sessionKey\":\"" + Key + "\",\"updatedAtMs\":" + Ms(Now) + ",\"phase\":\"terminal\",\"approval\":" +
            "{\"id\":\"s1\",\"status\":\"" + status + "\",\"reason\":\"why-" + status + "\"}}"), Now);

        var ended = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal(status, ended.Status);
        Assert.Equal("why-" + status, ended.Reason);
    }

    // A resolved broadcast that names no status is still an ending, and says so.
    [Fact]
    public void AResolvedBroadcastWithNoStatusIsEndedStatusUnknown()
    {
        Requested("a1");

        OpenClawSessions.OnEvent("exec.approval.resolved", Json("{\"id\":\"a1\"}"), Now);

        var ended = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal("unknown", ended.Status);
        Assert.Null(ended.Reason);
    }

    // A row the poll stopped listing before its expiry, with no event: nothing says
    // what happened, so the status is unknown and no reason is invented.
    [Fact]
    public void ARowThePollDroppedWithNoEventIsEndedStatusUnknown()
    {
        Requested("a1");

        OpenClawSessions.ReplacePending("exec", Array.Empty<OpenClawApproval>(), new DateTimeOffset(Now));

        var ended = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal("gone", ended.Cause);
        Assert.Equal("unknown", ended.Status);
        Assert.Null(ended.Reason);
    }

    // Dropped after its own expiry: this app's clock says it timed out.
    [Fact]
    public void ARowThePollDroppedAfterItsExpiryIsEndedExpiredWithATimeoutReason()
    {
        Requested("a1", Now.AddMinutes(5));

        OpenClawSessions.ReplacePending("exec", Array.Empty<OpenClawApproval>(), new DateTimeOffset(Now.AddMinutes(6)));

        var ended = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal("expired", ended.Cause);
        Assert.Equal("expired", ended.Status);
        Assert.Equal("timeout", ended.Reason);
    }

    // The session replay's omission takes the same two shapes.
    [Fact]
    public void AReplayThatOmitsARecordEndsItUnknownOrExpiredByTheSameRule()
    {
        SessionPending("s-gone");
        OpenClawSessions.ApplyReplay(Key, new OpenClawApprovalReplay(Array.Empty<OpenClawApproval>(), false), new DateTimeOffset(Now));

        var gone = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal("unknown", gone.Status);

        SessionPending("s-late");
        OpenClawSessions.ApplyReplay(
            Key, new OpenClawApprovalReplay(Array.Empty<OpenClawApproval>(), false), new DateTimeOffset(Now.AddHours(1)));

        var late = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal("expired", late.Status);
        Assert.Equal("timeout", late.Reason);
    }

    [Fact]
    public void AnEvictedRecordIsEndedStatusUnknown()
    {
        for (var i = 0; i < 9; i++) Requested("e" + i);

        var ended = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal("evicted", ended.Cause);
        Assert.Equal("unknown", ended.Status);
    }
}
