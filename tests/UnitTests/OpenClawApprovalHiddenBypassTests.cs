using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// Warren's ruling (CB-270): a pending approval shows its orb even when the session's
// cluster is Hidden, because a person has to answer it and a hidden orb cannot ask.
// It is kept exactly where a pinned session is kept, but only while approving is on,
// it goes the moment the approval ends (on the event, not at the next poll), and an
// archived session stays gone.
[Collection("Settings")]
public class OpenClawApprovalHiddenBypassTests : IDisposable
{
    // `agent:<id>:main` is a heartbeat session, so with heartbeats Hidden it has no orb.
    private const string Key = "agent:alpha:main";

    private static readonly DateTime Now = new(
        DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private readonly bool _approve = OrbweaverSettings.OpenClawApproveEnabled;
    private readonly bool _enabled = OrbweaverSettings.OpenClawEnabled;
    private readonly ClusterMode _heartbeats = OrbweaverSettings.OpenClawHeartbeatMode;
    private readonly ClusterMode _crons = OrbweaverSettings.OpenClawCronMode;
    private readonly ClusterMode _native = OrbweaverSettings.OpenClawNativeMode;
    private readonly int _within = OrbweaverSettings.OpenClawActiveWithinMinutes;

    public OpenClawApprovalHiddenBypassTests()
    {
        OrbweaverSettings.OpenClawApproveEnabled = true;
        OrbweaverSettings.OpenClawEnabled = true;
        OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.Hidden;
        OrbweaverSettings.OpenClawCronMode = ClusterMode.Hidden;
        OrbweaverSettings.OpenClawNativeMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawActiveWithinMinutes = OrbweaverSettings.OpenClawActiveWithinAll;
        OpenClawSessions.Restart();
        OpenClawSessions.ForgetApprovalsForTests();
        OpenClawSessions.SetSnapshotForTests(Array.Empty<OpenClawSessions.Session>());
    }

    public void Dispose()
    {
        OpenClawSessions.SetSnapshotForTests(Array.Empty<OpenClawSessions.Session>());
        OpenClawSessions.Restart();
        OpenClawSessions.ForgetApprovalsForTests();
        OrbweaverSettings.OpenClawApproveEnabled = _approve;
        OrbweaverSettings.OpenClawEnabled = _enabled;
        OrbweaverSettings.OpenClawHeartbeatMode = _heartbeats;
        OrbweaverSettings.OpenClawCronMode = _crons;
        OrbweaverSettings.OpenClawNativeMode = _native;
        OrbweaverSettings.OpenClawActiveWithinMinutes = _within;
    }

    private static string ListRow(string key, bool pinned = false, bool archived = false, string extra = "") =>
        "{\"key\":\"" + key + "\",\"classification\":\"main\",\"isMain\":true,\"pinned\":" + (pinned ? "true" : "false") +
        ",\"archived\":" + (archived ? "true" : "false") + ",\"lastActivityAt\":" + Ms(Now) + extra + "}";

    private static IReadOnlyList<OpenClawSessions.Session> Poll(string rows)
    {
        var (sessions, _) = OpenClawSessions.Parse(Json("{\"sessions\":[" + rows + "]}"), Now);
        // What PollAsync does with the answer.
        OpenClawSessions.SetSnapshotForTests(sessions);
        return sessions;
    }

    private static string Row(string id, string key = Key) =>
        "{\"approvalKind\":\"exec\",\"id\":\"" + id + "\",\"request\":{\"command\":\"true\",\"sessionKey\":\"" + key + "\"}," +
        "\"createdAtMs\":" + Ms(Now) + ",\"expiresAtMs\":" + Ms(Now.AddHours(1)) + "}";

    private static void Requested(string id, string key = Key) =>
        OpenClawSessions.OnEvent("exec.approval.requested", Json(Row(id, key)), Now);

    private static void Resolved(string id, string key = Key) =>
        OpenClawSessions.OnEvent("exec.approval.resolved", Json(
            "{\"id\":\"" + id + "\",\"decision\":\"deny\",\"resolvedBy\":\"gateway-client\",\"ts\":" + Ms(Now) +
            ",\"request\":{\"sessionKey\":\"" + key + "\"}}"), Now);

    private static OpenClawSessions.Session? Shown(string key = Key) =>
        OpenClawSessions.Snapshot().SingleOrDefault(s => s.Key == key);

    // --- the poll keeps it ---

    [Fact]
    public void AHiddenHeartbeatMainWithAPendingApprovalIsKeptAndAmber()
    {
        Requested("a1");

        var sessions = Poll(ListRow(Key));

        var row = Assert.Single(sessions);
        Assert.Equal("waiting", row.State);
        Assert.True(row.Heartbeat);
    }

    [Fact]
    public void TheSameRowWithNothingPendingIsHidden()
    {
        Assert.Empty(Poll(ListRow(Key)));
    }

    [Theory]
    [InlineData("heartbeat", "agent:alpha:main", "")]
    [InlineData("cron", "agent:alpha:cron:job-1", ",\"label\":\"Cron: sweep\"")]
    public void EveryHiddenClusterKeepsAnOrbThatIsAskingForAPerson(string cluster, string key, string extra)
    {
        Requested("a1", key);

        var kept = Poll(ListRow(key, extra: extra));
        Assert.Equal("waiting", Assert.Single(kept).State);
        Assert.Equal(cluster == "heartbeat", kept[0].Heartbeat);

        Resolved("a1", key);
        Assert.Empty(Poll(ListRow(key, extra: extra)));
    }

    // A pinned session is shown whatever it is waiting on, and ending the approval
    // never takes it away: it was never kept by the approval.
    [Fact]
    public void APinnedRowIsUnchangedByAnApprovalStartingOrEnding()
    {
        var before = Poll(ListRow(Key, pinned: true));
        Assert.Equal("idle", Assert.Single(before).State);

        Requested("a1");
        Assert.Equal("waiting", Shown()!.State);

        Resolved("a1");
        var after = Shown();
        Assert.NotNull(after);
        Assert.Equal("idle", after!.State);

        // And a poll with the approval pending, then none, keeps it throughout.
        Requested("a2");
        Assert.Equal("waiting", Assert.Single(Poll(ListRow(Key, pinned: true))).State);
        Resolved("a2");
        Assert.Equal("idle", Assert.Single(Poll(ListRow(Key, pinned: true))).State);
    }

    // --- immediacy: it comes and goes on the event, not at the next poll ---

    [Fact]
    public void TheOrbAppearsOnTheEventAndGoesWhenTheApprovalEnds()
    {
        Assert.Empty(Poll(ListRow(Key)));
        Assert.Null(Shown());

        Requested("a1");
        Assert.Equal("waiting", Shown()!.State);

        Resolved("a1");
        Assert.Null(Shown());

        // Again, to show the row is not spent by the first.
        Requested("a2");
        Assert.Equal("waiting", Shown()!.State);
        Resolved("a2");
        Assert.Null(Shown());
    }

    [Fact]
    public void WithTwoPendingTheOrbStaysUntilTheLastEnds()
    {
        Poll(ListRow(Key));
        Requested("a1");
        Requested("a2");

        Resolved("a1");
        Assert.Equal("waiting", Shown()!.State);

        Resolved("a2");
        Assert.Null(Shown());
    }

    [Fact]
    public void ARowKeptByThePollGoesOnTheEventToo()
    {
        Requested("a1");
        Poll(ListRow(Key));
        Assert.NotNull(Shown());

        Resolved("a1");

        Assert.Null(Shown());
    }

    // --- approving off keeps nothing ---

    [Fact]
    public void WithApprovingOffNothingIsKeptByThePollOrByTheEvent()
    {
        Requested("a1");
        OrbweaverSettings.OpenClawApproveEnabled = false;

        Assert.Empty(Poll(ListRow(Key)));

        // An approval event that arrives anyway is ignored (nothing pending), and a
        // store change for the key adds nothing to the snapshot.
        Requested("a2");
        Assert.Null(Shown());
    }

    // The store can still be filled by a path the setting does not gate (a list read
    // that was already in flight when approving was switched off): the record is
    // held, and the row is still not shown, because approving is off.
    [Fact]
    public void AStoreChangeWhileApprovingIsOffShowsNothing()
    {
        Poll(ListRow(Key));
        OrbweaverSettings.OpenClawApproveEnabled = false;
        var at = new DateTimeOffset(Now);
        var record = OpenClawApprovals.ParseRecord(Json(Row("a1")), "exec", at)!;

        OpenClawSessions.ReplacePending("exec", new[] { record }, at);

        Assert.Equal("waiting", OpenClawSessions.StateFor(Key, Now));
        Assert.Null(Shown());
    }

    [Fact]
    public void ARecordStillHeldWhenApprovingIsSwitchedOffNoLongerKeepsTheRow()
    {
        Poll(ListRow(Key));
        Requested("a1");
        Assert.NotNull(Shown());

        OrbweaverSettings.OpenClawApproveEnabled = false;
        Assert.Empty(Poll(ListRow(Key)));
    }

    // --- archived stays gone ---

    [Fact]
    public void AnArchivedRowIsNeverKeptOrBroughtBack()
    {
        Requested("a1");

        Assert.Empty(Poll(ListRow(Key, archived: true)));
        Assert.Null(Shown());

        // A store change for it does not resurrect it from an earlier poll either.
        Requested("a2");
        Resolved("a2");
        Assert.Null(Shown());
    }

    [Fact]
    public void ARowArchivedAfterItWasHiddenIsNotBroughtBackByALaterApproval()
    {
        Poll(ListRow(Key));
        Poll(ListRow(Key, archived: true));

        Requested("a1");

        Assert.Null(Shown());
    }

    // A session this app has never been told about has no row to show.
    [Fact]
    public void AnApprovalForASessionThePollNeverListedAddsNoRow()
    {
        Poll(ListRow(Key));

        Requested("a1", "agent:other:main");

        Assert.Null(Shown("agent:other:main"));
        Assert.Null(Shown());
    }
}
