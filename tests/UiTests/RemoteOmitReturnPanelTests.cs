using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-402: the real lead's chat panel was opened three seconds before its peer left it out.
[Collection("Settings")]
public class RemoteOmitReturnPanelTests
{
    private static SessionManager Manager(string statusDir) =>
        (SessionManager)typeof(SessionManager).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string) })!
            .Invoke(new object[] { statusDir });

    private static Dictionary<string, OrbWindow> Orbs(SessionManager m) =>
        (Dictionary<string, OrbWindow>)typeof(SessionManager).GetField(
            "_windows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;

    private static RemoteControlSessions.Remote Row(string route, string? lead = null, string? agent = null) =>
        new("Model identity question", "Warrens-Mac-mini", "generating", DateTime.UtcNow, ".claude",
            Route: route, LeadRoute: lead, Agent: agent);

    [AvaloniaFact]
    public void ALeadWhosePanelWasOpenWhenItLeftComesBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-402p-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            OrbweaverSettings.PeerLinkEnabled = true;
            const string leadKey = "rc:.claude:sid:369c40f8";
            var other = Row("sid:other");
            RemoteControlSessions.SetSnapshotForTests(new[] { Row("sid:369c40f8"), Row("sid:20b839b3", "sid:369c40f8", "wren"), other });

            var manager = Manager(dir);
            manager.ScanAndUpdate();
            var first = Orbs(manager)[leadKey];
            ChatPanel.OpenFor(first, manager.RemoteChatFor(leadKey)!);
            Assert.True(ChatPanel.IsOpenFor(leadKey));

            RemoteControlSessions.SetSnapshotForTests(new[] { other });
            manager.ScanAndUpdate();
            Assert.DoesNotContain(leadKey, Orbs(manager).Keys);

            RemoteControlSessions.SetSnapshotForTests(new[] { Row("sid:369c40f8"), Row("sid:20b839b3", "sid:369c40f8", "wren"), other });
            manager.ScanAndUpdate();
            manager.ScanAndUpdate();

            Assert.Contains(leadKey, Orbs(manager).Keys);
            var back = Orbs(manager)[leadKey];
            Assert.NotSame(first, back);
            Assert.True(back.IsVisible, "returned orb not visible");
            Assert.True(back.Opacity > 0.5, $"returned orb opacity {back.Opacity}");
        }
        finally
        {
            RemoteControlSessions.SetSnapshotForTests(Array.Empty<RemoteControlSessions.Remote>());
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
