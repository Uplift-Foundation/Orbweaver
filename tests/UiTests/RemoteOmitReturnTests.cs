using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-402: a peer's roster briefly leaves a session out, and the next roster has it back.
[Collection("Settings")]
public class RemoteOmitReturnTests
{
    private static SessionManager Manager(string statusDir) =>
        (SessionManager)typeof(SessionManager).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string) })!
            .Invoke(new object[] { statusDir });

    private static Dictionary<string, OrbWindow> Orbs(SessionManager manager) =>
        (Dictionary<string, OrbWindow>)typeof(SessionManager).GetField(
            "_windows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;

    private static RemoteControlSessions.Remote Row(string route, string? lead = null, string? agent = null) =>
        new("Model identity question", "Warrens-Mac-mini", "generating", DateTime.UtcNow, ".claude",
            Route: route, LeadRoute: lead, Agent: agent);

    [AvaloniaFact]
    public void ALeadLeftOutOfOneRosterIsBackAfterTheNext()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-402-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            OrbweaverSettings.PeerLinkEnabled = true;
            var lead = Row("sid:369c40f8");
            var member = Row("sid:20b839b3", "sid:369c40f8", "wren");
            var other = Row("sid:other");
            RemoteControlSessions.SetSnapshotForTests(new[] { lead, member, other });

            var manager = Manager(dir);
            manager.ScanAndUpdate();
            Assert.Contains("rc:.claude:sid:369c40f8", Orbs(manager).Keys);

            RemoteControlSessions.SetSnapshotForTests(new[] { other });
            manager.ScanAndUpdate();
            Assert.DoesNotContain("rc:.claude:sid:369c40f8", Orbs(manager).Keys);

            RemoteControlSessions.SetSnapshotForTests(new[] { Row("sid:369c40f8"), Row("sid:20b839b3", "sid:369c40f8", "wren"), other });
            manager.ScanAndUpdate();
            Assert.Contains("rc:.claude:sid:369c40f8", Orbs(manager).Keys);
            Assert.Contains("rc:.claude:sid:20b839b3", Orbs(manager).Keys);
        }
        finally
        {
            RemoteControlSessions.SetSnapshotForTests(Array.Empty<RemoteControlSessions.Remote>());
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
