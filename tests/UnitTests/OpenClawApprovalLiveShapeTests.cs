using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// The live defect (installed-app check): the orb was not amber and the prompt did not
// show the command, yet it offered Allow once. These drive the exact shapes the live
// gateway sent, in the order they arrived, through the real store into the prompt.
[Collection("Settings")]
public class OpenClawApprovalLiveShapeTests : IDisposable
{
    // The throwaway the installed-app check used: a dashboard session of agent main.
    private const string Key = "agent:main:dashboard:00000000-0000-4000-8000-000000000002";

    private static readonly DateTime Now = new(
        DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private readonly bool _approve = OrbweaverSettings.OpenClawApproveEnabled;

    public OpenClawApprovalLiveShapeTests()
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

    private static string Request() => $$"""
        {"command":"true","systemRunBinding":null,"systemRunPlan":null,"cwd":"/tmp","nodeId":null,
         "host":null,"security":null,"ask":null,"warningText":null,"scope":null,
         "commandAnalysis":{"commandCount":1,"nestedCommandCount":0,"riskKinds":[],"warningLines":[]},
         "allowedDecisions":["allow-once","allow-always","deny"],"agentId":"main","resolvedPath":null,
         "sessionKey":"{{Key}}","sessionId":null,"runId":null,"toolCallId":null,"turnSourceChannel":null,
         "turnSourceTo":null,"turnSourceAccountId":null,"turnSourceThreadId":null,
         "cronExecutionSource":null,"cronOperationBinding":null}
        """;

    private static string Presentation() =>
        "{\"kind\":\"exec\",\"commandText\":\"true\",\"commandPreview\":null,\"warningText\":null,\"host\":null," +
        "\"nodeId\":null,\"agentId\":\"main\",\"allowedDecisions\":[\"allow-once\",\"allow-always\",\"deny\"]}";

    private static string Snapshot(DateTime expires) =>
        "{\"id\":\"approval-0001\",\"status\":\"pending\",\"presentation\":" + Presentation() +
        ",\"urlPath\":\"/approve/approval-0001\",\"createdAtMs\":" + Ms(Now) + ",\"expiresAtMs\":" + Ms(expires) + "}";

    private static void ArriveAsTheyDid(DateTime expires)
    {
        // The measured order: session.approval pending, then the legacy requested event.
        OpenClawSessions.OnEvent("session.approval", Json(
            "{\"sessionKey\":\"" + Key + "\",\"sourceSessionKey\":\"" + Key + "\",\"updatedAtMs\":" + Ms(Now) +
            ",\"phase\":\"pending\",\"approval\":" + Snapshot(expires) + "}"), Now);
        OpenClawSessions.OnEvent("exec.approval.requested", Json(
            "{\"approvalKind\":\"exec\",\"id\":\"approval-0001\",\"request\":" + Request() + ",\"createdAtMs\":" +
            Ms(Now) + ",\"expiresAtMs\":" + Ms(expires) + "}"), Now);
    }

    [Fact]
    public void TheMeasuredArrivalHoldsTheCommandAndTheSessionIsWaiting()
    {
        ArriveAsTheyDid(Now.AddMinutes(10));

        var held = Assert.Single(OpenClawSessions.PendingFor(Key));
        Assert.Equal("true", held.Command);
        Assert.Equal("waiting", OpenClawSessions.StateFor(Key, Now));
    }

    [Fact]
    public void ThePromptForTheMeasuredArrivalShowsTheCommand()
    {
        ArriveAsTheyDid(Now.AddMinutes(10));
        var chat = new OpenClawChatSession("openclaw:" + Key, Key, "Lilibeth");

        var prompt = chat.Prompt!;

        Assert.Contains("wants to run: true", prompt.Title);
        Assert.Contains(prompt.Options, o => o.Key == "allow-once");
    }

    // --- the orb: the session list row for the throwaway, with and without activity ---
    //
    // Shaped like a real dashboard row from sessions.list (values invented), and like
    // the row a fresh session has before anything has happened on it: no
    // lastActivityAt, no lastInteractionAt.

    private static string RealShapedRow(bool withActivity, string key = Key) =>
        "{\"key\":\"" + key + "\",\"visibility\":\"shared\",\"kind\":\"direct\",\"classification\":\"direct\"," +
        "\"agentId\":\"main\",\"isMain\":false,\"isDock\":false,\"isBackground\":false,\"displayName\":\"Probe check\"," +
        "\"chatType\":\"direct\",\"updatedAt\":" + Ms(Now) + ",\"archived\":false,\"pinned\":false,\"unread\":false," +
        (withActivity ? "\"lastInteractionAt\":" + Ms(Now) + ",\"lastActivityAt\":" + Ms(Now) + "," : "") +
        "\"sessionId\":\"00000000-0000-4000-8000-0000000000aa\"}";

    private static OpenClawSessions.Session? Parsed(string rows)
    {
        OrbweaverSettings.OpenClawNativeMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.Hidden;
        OrbweaverSettings.OpenClawActiveWithinMinutes = 15;

        var (sessions, _) = OpenClawSessions.Parse(Json("{\"sessions\":[" + rows + "]}"), Now);
        return sessions.SingleOrDefault(x => x.Key == Key);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheThrowawaysOrbIsAmberWithOrWithoutRecentActivity(bool withActivity)
    {
        ArriveAsTheyDid(Now.AddMinutes(10));

        var row = Parsed(RealShapedRow(withActivity));

        Assert.NotNull(row);
        Assert.Equal("waiting", row!.State);
    }

    // --- the orb does not wait for the next poll ---
    //
    // The orbs draw from the snapshot the poll rebuilds every five seconds, and the
    // manager reads that every two. A request that arrives by event and is clicked
    // within a few seconds is therefore never seen as amber if only the poll turns it
    // amber. The measured live run was clicked 4.6 s after it was raised.

    [Fact]
    public void AnApprovalEventTurnsTheSnapshotRowAmberWithoutWaitingForAPoll()
    {
        var enabled = OrbweaverSettings.OpenClawEnabled;
        try
        {
            OrbweaverSettings.OpenClawEnabled = true;
            var row = Parsed(RealShapedRow(withActivity: true))!;
            Assert.Equal("idle", row.State);
            OpenClawSessions.SetSnapshotForTests(new[] { row });

            ArriveAsTheyDid(Now.AddMinutes(10));

            Assert.Equal("waiting", Assert.Single(OpenClawSessions.Snapshot()).State);

            // And back when it ends, by either feed.
            OpenClawSessions.OnEvent("exec.approval.resolved", Json(
                "{\"id\":\"approval-0001\",\"decision\":\"deny\",\"resolvedBy\":\"gateway-client\",\"ts\":" + Ms(Now) + "}"), Now);

            Assert.Equal("idle", Assert.Single(OpenClawSessions.Snapshot()).State);
        }
        finally
        {
            OpenClawSessions.SetSnapshotForTests(Array.Empty<OpenClawSessions.Session>());
            OrbweaverSettings.OpenClawEnabled = enabled;
        }
    }
}
