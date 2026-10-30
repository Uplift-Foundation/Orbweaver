using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// Two holes QA found in the measured-shape fixes (97543887).
//
// 1. Expiry on the legacy broadcast is inferred only from the shape the live gateway
//    sent for an expiry: no status, resolvedBy null, stamped at or after the record's
//    expiry. A person's answer carries resolvedBy ("gateway-client" when measured),
//    so one stamped late, early, absurdly or against an already-past expiry is still
//    the person's answer. Reading an Allow as "expired unanswered" would tell the
//    user nothing ran when it did.
// 2. An approval that ended recently is not brought back by a re-sent announcement
//    (a `requested` event, or a session.approval pending phase). The list poll and
//    the subscribe replay are the gateway's own account of what is pending, so they
//    still add one, and doing so takes the id out of the set.
[Collection("Settings")]
public class OpenClawApprovalStaleTests : IDisposable
{
    private const string Key = "agent:main:dashboard:00000000-0000-4000-8000-000000000001";

    private static readonly DateTime Now = new(
        DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private readonly bool _approve = OrbweaverSettings.OpenClawApproveEnabled;

    public OpenClawApprovalStaleTests()
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

    private static string Row(string id, DateTime expires, string key = Key) =>
        "{\"approvalKind\":\"exec\",\"id\":\"" + id + "\",\"request\":{\"command\":\"true\",\"cwd\":null,\"host\":null," +
        "\"allowedDecisions\":[\"allow-once\",\"allow-always\",\"deny\"],\"agentId\":\"main\",\"sessionKey\":\"" + key + "\"}," +
        "\"createdAtMs\":" + Ms(Now) + ",\"expiresAtMs\":" + Ms(expires) + "}";

    private static string Snapshot(string id, string status, DateTime expires, string tail = "") =>
        "{\"id\":\"" + id + "\",\"status\":\"" + status + "\",\"presentation\":{\"kind\":\"exec\",\"commandText\":\"true\"," +
        "\"allowedDecisions\":[\"allow-once\",\"allow-always\",\"deny\"]},\"urlPath\":\"/approve/" + id + "\"," +
        "\"createdAtMs\":" + Ms(Now) + ",\"expiresAtMs\":" + Ms(expires) + tail + "}";

    private static string SessionEvent(string phase, string snapshot, DateTime at) =>
        "{\"sessionKey\":\"" + Key + "\",\"sourceSessionKey\":\"" + Key + "\",\"updatedAtMs\":" + Ms(at) +
        ",\"phase\":\"" + phase + "\",\"approval\":" + snapshot + "}";

    private static void Event(string name, string json, DateTime? at = null) =>
        OpenClawSessions.OnEvent(name, Json(json), at ?? Now);

    private static string Ids(DateTime? at = null) =>
        string.Join(",", OpenClawSessions.PendingFor(Key, new DateTimeOffset(at ?? Now)).Select(a => a.Id));

    private static string Resolved(string id, string? decision, string? by, long ts) =>
        "{\"id\":\"" + id + "\"," + (decision is null ? "" : "\"decision\":\"" + decision + "\",") +
        "\"resolvedBy\":" + (by is null ? "null" : "\"" + by + "\"") + ",\"ts\":" + ts + ",\"request\":{\"sessionKey\":\"" + Key + "\"}}";

    // --- 1. the expiry inference needs resolvedBy to be null ---

    [Theory]
    [InlineData("deny")]
    [InlineData("allow-once")]
    [InlineData("allow-always")]
    public void APersonsAnswerStampedAfterTheExpiryIsStillTheirAnswer(string decision)
    {
        var expires = Now.AddMinutes(5);
        Event("exec.approval.requested", Row("a1", expires));

        Event("exec.approval.resolved", Resolved("a1", decision, "gateway-client", Ms(expires.AddMinutes(1))));

        var ended = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal("resolved", ended.Cause);
        Assert.Equal(decision, ended.Decision);
        Assert.Equal("gateway-client", ended.ResolvedBy);
        Assert.NotEqual("expired", ended.Status);
    }

    [Fact]
    public void AnAnswerWithAFarFutureTimestampIsStillTheirAnswer()
    {
        Event("exec.approval.requested", Row("a1", Now.AddMinutes(5)));

        Event("exec.approval.resolved", Resolved("a1", "allow-once", "gateway-client", 253402300799999));

        var ended = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal(("resolved", "allow-once"), (ended.Cause, ended.Decision));
    }

    // A row whose own expiry is long past is clamped to a second from now, so any
    // answer a moment later is "after the expiry". It is still a person's answer.
    [Fact]
    public void AnAnswerAgainstAnAlreadyPastExpiryIsStillTheirAnswer()
    {
        Event("exec.approval.requested", Row("a1", Now.AddHours(-1)));
        Assert.Equal("a1", Ids());

        Event("exec.approval.resolved", Resolved("a1", "deny", "gateway-client", Ms(Now.AddSeconds(30))));

        var ended = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal(("resolved", "deny"), (ended.Cause, ended.Decision));
    }

    // The control: the measured expiry shape still reads as an expiry.
    [Fact]
    public void TheMeasuredExpiryShapeStillReadsAsExpired()
    {
        var expires = Now.AddMinutes(5);
        Event("exec.approval.requested", Row("a1", expires));

        Event("exec.approval.resolved", Resolved("a1", "deny", null, Ms(expires.AddMilliseconds(7))));

        var ended = Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal(("expired", "expired", "timeout"), (ended.Cause, ended.Status, ended.Reason));
        Assert.Null(ended.Decision);
    }

    // --- 2. a re-sent announcement of an ended approval is ignored ---

    // Each way an approval can end, as a function that ends "a1" (held first).
    public static TheoryData<string> Endings() => new()
    {
        "legacy resolved event",
        "session terminal event",
        "poll no longer lists it",
        "poll after its expiry",
        "replay omits it",
    };

    private static DateTime End(string ending)
    {
        var at = Now;
        var expires = Now.AddMinutes(5);

        switch (ending)
        {
            case "legacy resolved event":
                Event("exec.approval.requested", Row("a1", expires));
                Event("exec.approval.resolved", Resolved("a1", "deny", "gateway-client", Ms(Now)));
                break;
            case "session terminal event":
                Event("session.approval", SessionEvent("pending", Snapshot("a1", "pending", expires), Now));
                Event("session.approval", SessionEvent("terminal", Snapshot("a1", "denied", expires, ",\"reason\":\"user\",\"decision\":\"deny\""), Now));
                break;
            case "poll no longer lists it":
                Event("exec.approval.requested", Row("a1", expires));
                OpenClawSessions.ReplacePending("exec", Array.Empty<OpenClawApproval>(), new DateTimeOffset(Now));
                break;
            case "poll after its expiry":
                Event("exec.approval.requested", Row("a1", expires));
                at = Now.AddMinutes(6);
                OpenClawSessions.ReplacePending("exec", Array.Empty<OpenClawApproval>(), new DateTimeOffset(at));
                break;
            case "replay omits it":
                Event("session.approval", SessionEvent("pending", Snapshot("a1", "pending", expires), Now));
                OpenClawSessions.ApplyReplay(Key, new OpenClawApprovalReplay(Array.Empty<OpenClawApproval>(), false), new DateTimeOffset(Now));
                break;
        }

        Assert.Single(OpenClawSessions.TakeEnded(Key));
        Assert.Equal("", Ids(at));
        return at;
    }

    [Theory]
    [MemberData(nameof(Endings))]
    public void AReSentRequestedEventForAnEndedApprovalIsIgnored(string ending)
    {
        var at = End(ending);

        Event("exec.approval.requested", Row("a1", at.AddMinutes(30)), at);

        Assert.Equal("", Ids(at));
    }

    [Theory]
    [MemberData(nameof(Endings))]
    public void AReSentSessionPendingForAnEndedApprovalIsIgnored(string ending)
    {
        var at = End(ending);

        Event("session.approval", SessionEvent("pending", Snapshot("a1", "pending", at.AddMinutes(30)), at), at);

        Assert.Equal("", Ids(at));
    }

    // The controls: the gateway's own list, and its replay, still add the id, and
    // doing so takes it out of the set (shown by a plain re-announcement being
    // accepted again after the record leaves by a path that records no ending).
    [Theory]
    [MemberData(nameof(Endings))]
    public void ThePollListStillAddsAnEndedIdAndClearsItFromTheSet(string ending)
    {
        var at = End(ending);
        var at2 = new DateTimeOffset(at);
        var listed = OpenClawApprovals.ParseRecord(Json(Row("a1", at.AddMinutes(30))), "exec", at2)!;

        OpenClawSessions.ReplacePending("exec", new[] { listed }, at2);
        Assert.Equal("a1", Ids(at));

        // Take it away without recording an ending (an unlisted session is purged
        // silently), then announce it again: the set no longer holds it.
        OpenClawSessions.PurgeUnlistedApprovals();
        Assert.Equal("", Ids(at));

        Event("exec.approval.requested", Row("a1", at.AddMinutes(30)), at);
        Assert.Equal("a1", Ids(at));
    }

    [Theory]
    [MemberData(nameof(Endings))]
    public void TheSubscribeReplayStillAddsAnEndedIdAndClearsItFromTheSet(string ending)
    {
        var at = End(ending);
        var at2 = new DateTimeOffset(at);
        var listed = OpenClawApprovals.ParseSnapshot(Json(Snapshot("a1", "pending", at.AddMinutes(30))), Key, at2)!;

        OpenClawSessions.ApplyReplay(Key, new OpenClawApprovalReplay(new[] { listed }, false), at2);
        Assert.Equal("a1", Ids(at));

        OpenClawSessions.PurgeUnlistedApprovals();
        Assert.Equal("", Ids(at));

        Event("session.approval", SessionEvent("pending", Snapshot("a1", "pending", at.AddMinutes(30)), at), at);
        Assert.Equal("a1", Ids(at));
    }

    // --- bounded by age and by count ---

    [Fact]
    public void TheSetForgetsAnIdAfterTenMinutesAndNotBefore()
    {
        Event("exec.approval.requested", Row("a1", Now.AddHours(1)));
        Event("exec.approval.resolved", Resolved("a1", "deny", "gateway-client", Ms(Now)));
        OpenClawSessions.TakeEnded(Key);

        var justBefore = Now.AddMinutes(10).AddSeconds(-1);
        Event("exec.approval.requested", Row("a1", Now.AddHours(1)), justBefore);
        Assert.Equal("", Ids(justBefore));

        var after = Now.AddMinutes(10);
        Event("exec.approval.requested", Row("a1", Now.AddHours(1)), after);
        Assert.Equal("a1", Ids(after));
    }

    [Fact]
    public void TheSetIsCappedByCountAndForgetsTheOldestFirst()
    {
        var count = OpenClawSessions.MaxRecentlyEnded + 1;
        for (var i = 0; i < count; i++)
        {
            Event("exec.approval.requested", Row("e" + i, Now.AddHours(1)), Now.AddSeconds(i));
            Event("exec.approval.resolved", Resolved("e" + i, "deny", "gateway-client", Ms(Now)), Now.AddSeconds(i));
            OpenClawSessions.TakeEnded(Key);
        }

        var at = Now.AddSeconds(count);

        // The oldest was pushed out, so it is announced and held again; the newest is still remembered.
        Event("exec.approval.requested", Row("e0", Now.AddHours(1)), at);
        Event("exec.approval.requested", Row("e" + (count - 1), Now.AddHours(1)), at);
        Assert.Equal("e0", Ids(at));
    }

    // An approval pushed out by this app's own cap has not ended: the gateway still
    // holds it and may announce it again, and that is accepted.
    [Fact]
    public void AnEvictedApprovalIsNotRememberedAsEnded()
    {
        for (var i = 0; i < OpenClawApprovals.MaxPerSession + 1; i++)
            Event("exec.approval.requested", Row("p" + i, Now.AddHours(1)));

        Assert.DoesNotContain("p0", Ids().Split(','));

        Event("exec.approval.requested", Row("p0", Now.AddHours(1)));

        Assert.Contains("p0", Ids().Split(','));
    }
}
