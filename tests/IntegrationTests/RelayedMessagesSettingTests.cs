using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-330: how the chat panel shows relayed teammate and peer messages, through
// a real settings file. Covered here as well as in the unit suite because the
// two fail differently: RelayRow can parse every word correctly while the key
// is never written, is read under another name, or comes back through
// _unknownKeys as well and makes Save reject the duplicate.
[Collection("Settings")]
public class RelayedMessagesSettingTests
{
    private static string NewSettingsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb330-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void PointSettingsAt(string dir)
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    private static JsonObject Saved(string dir) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;

    [Fact]
    public void ItDefaultsToCollapsedWhenTheFileHasNoSuchKey()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"version\": 1 }");

        PointSettingsAt(dir);

        Assert.Equal(RelayedMessageDisplay.Collapsed, OrbweaverSettings.RelayedMessages);
    }

    [Theory]
    [InlineData(RelayedMessageDisplay.Collapsed, "collapsed")]
    [InlineData(RelayedMessageDisplay.Expanded, "expanded")]
    [InlineData(RelayedMessageDisplay.Hidden, "hidden")]
    public void EachValueIsWrittenUnderItsOwnKeyAndSurvivesAReload(RelayedMessageDisplay value, string word)
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        // Through a different value first, so Collapsed is a real write rather
        // than the default being read back.
        OrbweaverSettings.RelayedMessages = value == RelayedMessageDisplay.Hidden
            ? RelayedMessageDisplay.Expanded
            : RelayedMessageDisplay.Hidden;
        OrbweaverSettings.RelayedMessages = value;

        Assert.Equal(word, Saved(dir)["relayedMessages"]!.GetValue<string>());

        PointSettingsAt(dir);
        Assert.Equal(value, OrbweaverSettings.RelayedMessages);
    }

    [Theory]
    [InlineData("\"sideways\"")]
    [InlineData("7")]
    [InlineData("null")]
    [InlineData("{ \"a\": 1 }")]
    public void AHandEditedValueReadsAsCollapsedAndDoesNotThrow(string json)
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"version\": 1, \"relayedMessages\": " + json + " }");

        PointSettingsAt(dir);

        Assert.Equal(RelayedMessageDisplay.Collapsed, OrbweaverSettings.RelayedMessages);
    }

    [Fact]
    public void TheKeyIsKnownSoItDoesNotAlsoRoundTripAsAnUnknownOneBesideARealUnknownKey()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"version\": 1, \"relayedMessages\": \"hidden\", \"somethingFromANewerBuild\": \"keep me\" }");

        PointSettingsAt(dir);
        Assert.Equal(RelayedMessageDisplay.Hidden, OrbweaverSettings.RelayedMessages);

        // Any write forces a Save; a duplicate key would have thrown or lost it.
        OrbweaverSettings.RelayedMessages = RelayedMessageDisplay.Expanded;

        var root = Saved(dir);
        Assert.Equal("expanded", root["relayedMessages"]!.GetValue<string>());
        Assert.Equal("keep me", root["somethingFromANewerBuild"]!.GetValue<string>());
    }
}
