using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-329 amendment 1: reopening un-hides through the SessionManager when there
// is one (which also schedules a scan), exactly as Settings > Hidden orbs does.
// Lives here rather than in UnitTests because a SessionManager needs the
// headless platform. The manager-less path is covered in UnitTests.
[Collection("Settings")]
public class OpenClawReopenRestoreTests
{
    [AvaloniaFact]
    public void RestoreHiddenVia_AManager_RemovesTheEntryAndLeavesATwin()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "cb329-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        OrbweaverSettings.RestoreAllHiddenOrbs();
        var manager = new SessionManager(scratch);
        try
        {
            OrbweaverSettings.HideOrb(new HiddenOrb("openclaw:agent:a:main", DateTime.UtcNow, "A", "OpenClaw", ""));
            OrbweaverSettings.HideOrb(new HiddenOrb("openclaw:agent:b:main", DateTime.UtcNow, "B", "OpenClaw", ""));

            OpenClawSessions.RestoreHiddenVia(manager, "openclaw:agent:a:main");

            var hidden = OrbweaverSettings.HiddenOrbIds();
            Assert.DoesNotContain("openclaw:agent:a:main", hidden);
            Assert.Contains("openclaw:agent:b:main", hidden);
        }
        finally
        {
            OrbweaverSettings.RestoreAllHiddenOrbs();
            try { Directory.Delete(scratch, true); } catch { }
        }
    }
}
