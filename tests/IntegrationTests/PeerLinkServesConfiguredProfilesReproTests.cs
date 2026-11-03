using System.Reflection;
using Xunit;

namespace Orbweaver.Tests;

// CB-300's reproduction, written to compile against develop as well as this
// branch so the same assertion body is red before the fix and green after.
//
// What the peer link serves is asked the way the app asks it: on develop that
// is OrbweaverSettings.RemoteControlProfileDirs, a list no screen writes any
// more; on the fix it is PeerRosterDirs.Configured. The type is looked up by
// name so this file builds where PeerRosterDirs does not exist.
[Collection("Settings")]
public class PeerLinkServesConfiguredProfilesReproTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb300r-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");

    public PeerLinkServesConfiguredProfilesReproTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", _previous);
        OrbweaverSettings.ReloadForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static IReadOnlyList<string> ServedDirs()
    {
        var type = typeof(OrbweaverSettings).Assembly.GetType("Orbweaver.PeerRosterDirs");
        var configured = type?.GetMethod(
            "Configured", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

        return configured is null
            ? OrbweaverSettings.RemoteControlProfileDirs
            : (IReadOnlyList<string>)configured.Invoke(null, new object?[] { null })!;
    }

    [Fact]
    public void AProfileAddedInSettingsAfterTheRelayWasDeletedIsServedOverTheLink()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            "{\"claudeCodeProfileDirs\":[\".claude\",\".claude-board\",\".claude-loginid\"],"
            + "\"remoteControlProfileDirs\":[\".claude\",\".claude-board\"]}");
        OrbweaverSettings.ReloadForTests();

        Assert.Contains(".claude-loginid", ServedDirs());
    }
}
