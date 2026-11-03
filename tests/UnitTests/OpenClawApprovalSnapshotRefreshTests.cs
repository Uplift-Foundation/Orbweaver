using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// The three arcs coverage showed open in the recently-ended prune and in the orb's
// snapshot refresh (RefreshSnapshotState).
[Collection("Settings")]
public class OpenClawApprovalSnapshotRefreshTests : IDisposable
{
    private const string Key = "agent:main:dashboard:00000000-0000-4000-8000-000000000005";
    private const string OtherKey = "agent:main:dashboard:00000000-0000-4000-8000-000000000006";

    private static readonly DateTime Now = new(
        DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private readonly bool _approve = OrbweaverSettings.OpenClawApproveEnabled;
    private readonly bool _enabled = OrbweaverSettings.OpenClawEnabled;

    public OpenClawApprovalSnapshotRefreshTests()
    {
        OrbweaverSettings.OpenClawApproveEnabled = true;
        OrbweaverSettings.OpenClawEnabled = true;
        OpenClawSessions.Restart();
        OpenClawSessions.ForgetApprovalsForTests();
    }

    public void Dispose()
    {
        OpenClawSessions.SetSnapshotForTests(Array.Empty<OpenClawSessions.Session>());
        OpenClawSessions.Restart();
        OpenClawSessions.ForgetApprovalsForTests();
        OrbweaverSettings.OpenClawApproveEnabled = _approve;
        OrbweaverSettings.OpenClawEnabled = _enabled;
    }

    private static string Row(string id, string key, DateTime expires) =>
        "{\"approvalKind\":\"exec\",\"id\":\"" + id + "\",\"request\":{\"command\":\"true\",\"sessionKey\":\"" + key + "\"}," +
        "\"createdAtMs\":" + Ms(Now) + ",\"expiresAtMs\":" + Ms(expires) + "}";

    private static void Requested(string id, string key, DateTime? at = null) =>
        OpenClawSessions.OnEvent("exec.approval.requested", Json(Row(id, key, Now.AddHours(1))), at ?? Now);

    private static void Resolved(string id, string key, DateTime? at = null) =>
        OpenClawSessions.OnEvent("exec.approval.resolved", Json(
            "{\"id\":\"" + id + "\",\"decision\":\"deny\",\"resolvedBy\":\"gateway-client\",\"ts\":" + Ms(at ?? Now) +
            ",\"request\":{\"sessionKey\":\"" + key + "\"}}"), at ?? Now);

    private static OpenClawSessions.Session SnapshotRow(string key) =>
        OpenClawSessions.Parse(Json(
            "{\"sessions\":[{\"key\":\"" + key + "\",\"chatType\":\"direct\",\"lastActivityAt\":" + Ms(Now) + "}]}"), Now)
        .Sessions.Single();

    private static IReadOnlyDictionary<string, DateTimeOffset> RecentlyEnded()
    {
        var field = typeof(OpenClawSessions).GetField("RecentlyEnded", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (IReadOnlyDictionary<string, DateTimeOffset>)field.GetValue(null)!;
    }

    // --- 468: an entry older than the TTL is actually removed ---

    [Fact]
    public void AnEndingRecordedMoreThanTheTtlAfterAnEarlierOneRemovesTheEarlierFromTheSet()
    {
        Requested("old", Key);
        Resolved("old", Key);
        Assert.Contains("old", RecentlyEnded().Keys);

        var later = Now + OpenClawSessions.RecentlyEndedTtl + TimeSpan.FromSeconds(1);
        Requested("new", Key, later);
        Resolved("new", Key, later);

        Assert.DoesNotContain("old", RecentlyEnded().Keys);
        Assert.Contains("new", RecentlyEnded().Keys);
    }

    // The control: one that has not aged out stays.
    [Fact]
    public void AnEndingRecordedWithinTheTtlOfAnEarlierOneKeepsTheEarlierInTheSet()
    {
        Requested("old", Key);
        Resolved("old", Key);

        var later = Now + OpenClawSessions.RecentlyEndedTtl - TimeSpan.FromSeconds(1);
        Requested("new", Key, later);
        Resolved("new", Key, later);

        Assert.Contains("old", RecentlyEnded().Keys);
        Assert.Contains("new", RecentlyEnded().Keys);
    }

    // --- 515: another session ahead of the target in the snapshot ---

    [Fact]
    public void ARowForAnotherSessionAheadOfTheTargetIsLeftAloneWhileTheTargetTurnsAmber()
    {
        var other = SnapshotRow(OtherKey);
        var target = SnapshotRow(Key);
        OpenClawSessions.SetSnapshotForTests(new[] { other, target });

        Requested("a1", Key);

        var rows = OpenClawSessions.Snapshot();
        Assert.Equal(2, rows.Count);
        Assert.Equal(OtherKey, rows[0].Key);
        Assert.Equal("idle", rows[0].State);
        Assert.Equal("waiting", rows[1].State);
    }

    // --- 523: a refresh whose state does not change returns early ---

    [Fact]
    public void ARefreshThatChangesNothingLeavesTheSnapshotAsItWasAndOneThatChangesReplacesIt()
    {
        OpenClawSessions.SetSnapshotForTests(new[] { SnapshotRow(Key) });
        var before = OpenClawSessions.Snapshot();

        // Idle to waiting: the state changes, so the list is rebuilt.
        Requested("a1", Key);
        var afterFirst = OpenClawSessions.Snapshot();
        Assert.NotSame(before, afterFirst);
        Assert.Equal("waiting", afterFirst.Single().State);

        // A second approval on a session that is already waiting: nothing changes, so
        // the very same list is still there.
        Requested("a2", Key);
        Assert.Same(afterFirst, OpenClawSessions.Snapshot());
        Assert.Equal("waiting", OpenClawSessions.Snapshot().Single().State);

        // One ends and one remains: still waiting, still the same list.
        Resolved("a1", Key);
        Assert.Same(afterFirst, OpenClawSessions.Snapshot());

        // The last ends: back to idle, which is a change.
        Resolved("a2", Key);
        Assert.NotSame(afterFirst, OpenClawSessions.Snapshot());
        Assert.Equal("idle", OpenClawSessions.Snapshot().Single().State);
    }
}
