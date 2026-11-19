using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-357: "Allow other desktops to start sessions here" through a real
// settings.json. Off in a file that has never heard of it — a desk upgraded
// into this feature must not start accepting launches by default — and
// written under its own key, so a downgrade keeps it through the unknown-keys
// round trip and an upgrade back finds it where it was.
[Collection("Settings")]
public class PeerLaunchSettingsTests : IDisposable
{
    private readonly string? _dirBefore = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", _dirBefore);
        OrbweaverSettings.ReloadForTests();
    }

    private static string PointAtNewDir(string? existing = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb357-launch-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        if (existing is not null) File.WriteAllText(Path.Combine(dir, "settings.json"), existing);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
        return dir;
    }

    private static JsonObject Saved(string dir) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;

    [Fact]
    public void It_is_off_in_a_fresh_file_and_in_one_that_only_has_the_link_on()
    {
        PointAtNewDir();
        Assert.False(OrbweaverSettings.PeerLaunchInboundEnabled);

        PointAtNewDir("""{"version":1,"peerLinkEnabled":true}""");
        Assert.True(OrbweaverSettings.PeerLinkEnabled);
        Assert.False(OrbweaverSettings.PeerLaunchInboundEnabled);
    }

    [Fact]
    public void It_round_trips_under_its_own_key()
    {
        var dir = PointAtNewDir();

        OrbweaverSettings.PeerLaunchInboundEnabled = true;
        Assert.True(Saved(dir)["peerLaunchInboundEnabled"]!.GetValue<bool>());
        OrbweaverSettings.ReloadForTests();
        Assert.True(OrbweaverSettings.PeerLaunchInboundEnabled);
        Assert.False(OrbweaverSettings.PeerLinkEnabled);   // a separate switch, not the link's

        OrbweaverSettings.PeerLaunchInboundEnabled = false;
        OrbweaverSettings.ReloadForTests();
        Assert.False(OrbweaverSettings.PeerLaunchInboundEnabled);
    }

    // A value of the wrong type is read as off, not as a crash or as on.
    [Fact]
    public void A_malformed_value_reads_as_off()
    {
        PointAtNewDir("""{"version":1,"peerLaunchInboundEnabled":"yes"}""");
        Assert.False(OrbweaverSettings.PeerLaunchInboundEnabled);
    }
}
