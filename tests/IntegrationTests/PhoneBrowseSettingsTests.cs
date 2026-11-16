using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-339: the two settings behind the folder picker, through a real settings.json. The case that
// matters most is the difference between "no key" (automatic) and "an empty list" (a deliberate
// none): reading them as one would either share folders nobody chose or lose a choice to share
// none. And the home switch is off in a file that has never heard of it.
[Collection("Settings")]
public class PhoneBrowseSettingsTests : IDisposable
{
    private readonly string? _dirBefore = Environment.GetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", _dirBefore);
        OrbweaverSettings.ReloadForTests();
    }

    private static string PointAtNewDir(string? existing = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb339-browse-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        if (existing is not null) File.WriteAllText(Path.Combine(dir, "settings.json"), existing);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
        return dir;
    }

    private static JsonObject Saved(string dir) => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;

    [Fact]
    public void A_fresh_file_is_automatic_with_the_home_switch_off_and_looking_writes_nothing()
    {
        var dir = PointAtNewDir();
        Assert.Null(OrbweaverSettings.PhoneBrowseRoots);
        Assert.False(OrbweaverSettings.PhoneBrowseHome);
        Assert.False(File.Exists(Path.Combine(dir, "settings.json")));
    }

    [Fact]
    public void A_list_round_trips_in_order_and_an_empty_one_stays_explicit_and_null_goes_back_to_automatic()
    {
        var dir = PointAtNewDir();

        OrbweaverSettings.SetPhoneBrowseRoots(["/b", "", "  ", "/a"]);                // blanks are not folders
        Assert.Equal(["/b", "/a"], Saved(dir)["phoneBrowseRoots"]!.AsArray().Select(n => n!.GetValue<string>()));
        OrbweaverSettings.ReloadForTests();
        Assert.Equal(["/b", "/a"], OrbweaverSettings.PhoneBrowseRoots);

        OrbweaverSettings.SetPhoneBrowseRoots([]);
        OrbweaverSettings.ReloadForTests();
        Assert.Equal(0, OrbweaverSettings.PhoneBrowseRoots!.Count);                   // not null: a deliberate none

        OrbweaverSettings.SetPhoneBrowseRoots(null);
        Assert.Null(Saved(dir)["phoneBrowseRoots"]);
        OrbweaverSettings.ReloadForTests();
        Assert.Null(OrbweaverSettings.PhoneBrowseRoots);
    }

    [Fact]
    public void The_home_switch_round_trips()
    {
        var dir = PointAtNewDir();
        OrbweaverSettings.PhoneBrowseHome = true;
        Assert.True(Saved(dir)["phoneBrowseHome"]!.GetValue<bool>());
        OrbweaverSettings.ReloadForTests();
        Assert.True(OrbweaverSettings.PhoneBrowseHome);
        OrbweaverSettings.PhoneBrowseHome = false;
        OrbweaverSettings.ReloadForTests();
        Assert.False(OrbweaverSettings.PhoneBrowseHome);
    }

    [Fact]
    public void A_file_from_before_the_keys_existed_reads_automatic_and_off_and_keeps_keys_it_does_not_know()
    {
        var dir = PointAtNewDir("""{"version":1,"relayEnabled":true,"someFutureKey":42}""");
        Assert.Null(OrbweaverSettings.PhoneBrowseRoots);
        Assert.False(OrbweaverSettings.PhoneBrowseHome);

        OrbweaverSettings.PhoneBrowseHome = true;

        var root = Saved(dir);
        Assert.Equal(42, root["someFutureKey"]!.GetValue<int>());
        Assert.True(root["relayEnabled"]!.GetValue<bool>());
        Assert.True(root["phoneBrowseHome"]!.GetValue<bool>());
    }

    [Fact]
    public void A_hand_written_list_with_a_non_string_in_it_keeps_the_strings_and_a_non_array_is_automatic()
    {
        PointAtNewDir("""{"version":1,"phoneBrowseRoots":["/a",7,null,"/b"]}""");
        Assert.Equal(["/a", "/b"], OrbweaverSettings.PhoneBrowseRoots);

        PointAtNewDir("""{"version":1,"phoneBrowseRoots":"/a"}""");
        Assert.Null(OrbweaverSettings.PhoneBrowseRoots);
    }
}
