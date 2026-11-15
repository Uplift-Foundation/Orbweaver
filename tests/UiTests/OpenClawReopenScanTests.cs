using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-329 (G2): the plan's assumption A1 as a measurement. A reopened session
// passes Parse's recency rule, but the orb only exists if SessionManager's own
// scan, with its own lifetime check, lets it through as well. If it did not,
// the lifetime would be a second filter and the plan would need revising.
//
// Same harness as GatewayScanTests: a SessionManager over a scratch status
// directory, Start() never called, the snapshot published from the real Parse.
[Collection("Settings")]
public class OpenClawReopenScanTests : IDisposable
{
    private readonly bool _enabled = OrbweaverSettings.OpenClawEnabled;
    private readonly int _within = OrbweaverSettings.OpenClawActiveWithinMinutes;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb329-scan-" + Guid.NewGuid());

    public OpenClawReopenScanTests()
    {
        Directory.CreateDirectory(_dir);
        OrbweaverSettings.OpenClawEnabled = true;
        OpenClawSessions.ForgetLastSeenForTests();
    }

    public void Dispose()
    {
        OpenClawSessions.SetSnapshotForTests(Array.Empty<OpenClawSessions.Session>());
        OpenClawSessions.SetListingCurrentForTests(false);
        OpenClawSessions.ForgetLastSeenForTests();
        OrbweaverSettings.RestoreAllHiddenOrbs();
        OrbweaverSettings.OpenClawEnabled = _enabled;
        OrbweaverSettings.OpenClawActiveWithinMinutes = _within;
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static long Stale => new DateTimeOffset(DateTime.UtcNow.AddDays(-2)).ToUnixTimeMilliseconds();

    internal static string Dm(int n) =>
        $$"""{"key":"agent:a:discord:direct:{{n}}","classification":"direct","chatType":"direct","lastActivityAt":{{Stale}}}""";

    private static SessionManager Manager(string dir) =>
        (SessionManager)typeof(SessionManager).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string) })!.Invoke(new object[] { dir });

    private static Dictionary<string, OrbWindow> Orbs(SessionManager m) =>
        (Dictionary<string, OrbWindow>)typeof(SessionManager).GetField(
            "_windows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;

    private static void Publish(string rows)
    {
        OrbweaverSettings.OpenClawActiveWithinMinutes = 5;
        var (shown, _) = OpenClawSessions.Parse(
            JsonDocument.Parse("{\"sessions\":[" + rows + "]}").RootElement, DateTime.UtcNow);
        OpenClawSessions.SetSnapshotForTests(shown);
    }

    [AvaloniaFact]
    public void AReopenedStaleDmGetsAnOrbAndItsUnreopenedTwinDoesNot()
    {
        const string marked = "agent:a:discord:direct:1", twin = "agent:a:discord:direct:2";
        var rows = Dm(1) + "," + Dm(2);

        // Control first: neither stale DM has an orb before anyone reopens one.
        Publish(rows);
        var before = Manager(_dir);
        before.ScanAndUpdate();
        Assert.DoesNotContain("openclaw:" + marked, Orbs(before).Keys);
        Assert.DoesNotContain("openclaw:" + twin, Orbs(before).Keys);

        // Reopen's mark, then the next poll, then a real scan (twice: surviving
        // one scan and expiring on the next is a different bug).
        OpenClawSessions.MarkActive(new[] { marked }, DateTime.UtcNow);
        Publish(rows);
        var manager = Manager(_dir);
        manager.ScanAndUpdate();
        manager.ScanAndUpdate();

        Assert.Contains("openclaw:" + marked, Orbs(manager).Keys);
        Assert.NotNull(manager.StatusFor("openclaw:" + marked));
        Assert.DoesNotContain("openclaw:" + twin, Orbs(manager).Keys);
        Assert.Null(manager.StatusFor("openclaw:" + twin));
    }
}
