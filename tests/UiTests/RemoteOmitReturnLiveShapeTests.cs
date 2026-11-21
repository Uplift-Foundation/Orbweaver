using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-402 probe: the real lead had a persona with a picture, a team member and a live remote chat.
[Collection("Settings")]
public class RemoteOmitReturnLiveShapeTests
{
    private static SessionManager Manager(string statusDir) =>
        (SessionManager)typeof(SessionManager).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string) })!
            .Invoke(new object[] { statusDir });

    private static Dictionary<string, OrbWindow> Orbs(SessionManager m) =>
        (Dictionary<string, OrbWindow>)typeof(SessionManager).GetField(
            "_windows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static RemoteControlSessions.Remote Row(string route, string? lead = null, string? agent = null, bool avatar = true) =>
        new("Model identity question", "Warrens-Mac-mini", "generating", DateTime.UtcNow, ".claude",
            Route: route, LeadRoute: lead, Agent: agent,
            Persona: new MirrorProtocol.PeerPersona("Jennifer Voss", null, null, avatar ? Png : null, "abc123"));

    [AvaloniaFact]
    public void ALeadWithAPersonaAndALiveChatComesBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-402b-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            OrbweaverSettings.PeerLinkEnabled = true;
            const string leadKey = "rc:.claude:sid:369c40f8";
            var other = Row("sid:other");
            RemoteControlSessions.SetSnapshotForTests(new[] { Row("sid:369c40f8"), Row("sid:20b839b3", "sid:369c40f8", "wren"), other });

            var manager = Manager(dir);
            manager.ScanAndUpdate();
            Assert.NotNull(manager.RemoteChatFor(leadKey));

            RemoteControlSessions.SetSnapshotForTests(new[] { other });
            manager.ScanAndUpdate();
            Assert.DoesNotContain(leadKey, Orbs(manager).Keys);

            // Returns with the picture missing, as FillPicturesAsync hands it on after the held row is gone.
            RemoteControlSessions.SetSnapshotForTests(new[] { Row("sid:369c40f8", avatar: false), Row("sid:20b839b3", "sid:369c40f8", "wren", avatar: false), other });
            manager.ScanAndUpdate();
            manager.ScanAndUpdate();
            Assert.Contains(leadKey, Orbs(manager).Keys);
            Assert.True(Orbs(manager)[leadKey].IsVisible);
            Assert.NotNull(manager.RemoteChatFor(leadKey));
        }
        finally
        {
            RemoteControlSessions.SetSnapshotForTests(Array.Empty<RemoteControlSessions.Remote>());
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
