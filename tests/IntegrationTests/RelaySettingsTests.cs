using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-314: RelayEnabled through a real settings.json. Found on the installed
// app: "Pair phone…" turned the relay on in memory, but Save() writes an
// explicit key list and relayEnabled was not on it, so a restart read it as
// off and a paired phone went dark until someone paired again. This is the
// round trip that would have caught it.
[Collection("Settings")]
public class RelaySettingsTests : IDisposable
{
    private readonly string? _dirBefore = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", _dirBefore);
        OrbweaverSettings.ReloadForTests();
    }

    private static string PointAtNewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb314-relay-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
        return dir;
    }

    [Fact]
    public void Off_by_default_and_on_survives_a_reload()
    {
        var dir = PointAtNewDir();
        Assert.False(OrbweaverSettings.RelayEnabled);

        OrbweaverSettings.RelayEnabled = true;
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;
        Assert.True(root["relayEnabled"]!.GetValue<bool>());

        OrbweaverSettings.ReloadForTests();                     // a restart
        Assert.True(OrbweaverSettings.RelayEnabled);

        OrbweaverSettings.RelayEnabled = false;
        OrbweaverSettings.ReloadForTests();
        Assert.False(OrbweaverSettings.RelayEnabled);
    }

    // An older file without the key reads as off, and saving after turning
    // it on keeps the key exactly once (the known-keys list and Save() agree;
    // a key written but not listed would be carried twice and throw).
    [Fact]
    public void An_older_file_reads_off_and_a_save_writes_the_key_once()
    {
        var dir = PointAtNewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{"version":1,"peerLinkEnabled":true}""");
        OrbweaverSettings.ReloadForTests();
        Assert.False(OrbweaverSettings.RelayEnabled);

        OrbweaverSettings.RelayEnabled = true;
        var text = File.ReadAllText(Path.Combine(dir, "settings.json"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "\"relayEnabled\""));
        Assert.True(OrbweaverSettings.PeerLinkEnabled);
    }
}
