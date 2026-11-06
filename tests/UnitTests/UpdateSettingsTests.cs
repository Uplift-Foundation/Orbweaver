using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-308's five settings keys: defaults, a real file round trip, a garbled file,
// and the one trap this file has bitten before — a key written by Save() that is
// missing from KnownKeys is also carried in _unknownKeys and throws on the next
// Save. Each case rewrites settings.json by hand and reloads, so what is
// asserted is what a second process reading the same file would see.
[Collection("Settings")]
public class UpdateSettingsTests
{
    private static void Write(string json)
    {
        Directory.CreateDirectory(OrbweaverSettings.Directory);
        File.WriteAllText(OrbweaverSettings.Path_, json);
        OrbweaverSettings.ReloadForTests();
    }

    private static JsonObject Read() =>
        JsonNode.Parse(File.ReadAllText(OrbweaverSettings.Path_))!.AsObject();

    [Fact]
    public void AFileWithNoUpdateKeysGetsTheDefaults()
    {
        Write("{}");
        Assert.True(OrbweaverSettings.AutoCheckForUpdates);
        Assert.False(OrbweaverSettings.IncludePrereleaseUpdates);
        Assert.Null(OrbweaverSettings.SkippedUpdateVersion);
        Assert.Null(OrbweaverSettings.LastUpdateCheckUtc);
        Assert.Null(OrbweaverSettings.LastRunVersion);
    }

    [Fact]
    public void EveryKeyRoundTripsThroughARealFile()
    {
        Write("{}");
        var at = new DateTimeOffset(2026, 11, 6, 12, 30, 0, TimeSpan.Zero);
        OrbweaverSettings.AutoCheckForUpdates = false;
        OrbweaverSettings.IncludePrereleaseUpdates = true;
        OrbweaverSettings.SkippedUpdateVersion = "1.0.4";
        OrbweaverSettings.LastUpdateCheckUtc = at;
        OrbweaverSettings.LastRunVersion = "1.0.3";

        var onDisk = Read();
        Assert.False(onDisk["autoCheckForUpdates"]!.GetValue<bool>());
        Assert.True(onDisk["includePrereleaseUpdates"]!.GetValue<bool>());
        Assert.Equal("1.0.4", onDisk["skippedUpdateVersion"]!.GetValue<string>());
        Assert.Equal("1.0.3", onDisk["lastRunVersion"]!.GetValue<string>());

        OrbweaverSettings.ReloadForTests();
        Assert.False(OrbweaverSettings.AutoCheckForUpdates);
        Assert.True(OrbweaverSettings.IncludePrereleaseUpdates);
        Assert.Equal("1.0.4", OrbweaverSettings.SkippedUpdateVersion);
        Assert.Equal(at, OrbweaverSettings.LastUpdateCheckUtc);
        Assert.Equal("1.0.3", OrbweaverSettings.LastRunVersion);
    }

    [Fact]
    public void ClearingTheNullableKeysWritesThemBackAsNothing()
    {
        Write("""{"skippedUpdateVersion":"1.0.4","lastRunVersion":"1.0.3","lastUpdateCheckUtc":"2026-11-06T12:30:00.0000000Z"}""");
        OrbweaverSettings.SkippedUpdateVersion = " ";
        OrbweaverSettings.LastRunVersion = null;
        OrbweaverSettings.LastUpdateCheckUtc = null;

        OrbweaverSettings.ReloadForTests();
        Assert.Null(OrbweaverSettings.SkippedUpdateVersion);
        Assert.Null(OrbweaverSettings.LastRunVersion);
        Assert.Null(OrbweaverSettings.LastUpdateCheckUtc);
    }

    [Fact]
    public void GarbledValuesDegradeToDefaultsWithoutLosingTheRestOfTheFile()
    {
        // A hand-edited file: wrong types everywhere, plus an unrelated key that
        // must survive. A throw here would be caught by Load's one catch and
        // replace the whole model.
        Write("""{"autoCheckForUpdates":"yes","includePrereleaseUpdates":7,"skippedUpdateVersion":5,"lastUpdateCheckUtc":"not a date","lastRunVersion":false,"peerLinkPort":9100}""");

        Assert.True(OrbweaverSettings.AutoCheckForUpdates);
        Assert.False(OrbweaverSettings.IncludePrereleaseUpdates);
        Assert.Null(OrbweaverSettings.SkippedUpdateVersion);
        Assert.Null(OrbweaverSettings.LastUpdateCheckUtc);
        Assert.Null(OrbweaverSettings.LastRunVersion);
        Assert.Equal(9100, OrbweaverSettings.PeerLinkPort);
    }

    [Fact]
    public void SavingAfterLoadingTheKeysDoesNotThrowOrDuplicateThem()
    {
        // The KnownKeys trap: had a key been left out of the list, this Save
        // would add it from both the model and _unknownKeys.
        Write("""{"autoCheckForUpdates":false,"includePrereleaseUpdates":true,"skippedUpdateVersion":"1.0.4","lastUpdateCheckUtc":"2026-11-06T12:30:00.0000000Z","lastRunVersion":"1.0.3","someFutureKey":42}""");

        OrbweaverSettings.PeerLinkEnabled = true; // any setter forces a Save

        var onDisk = Read();
        Assert.False(onDisk["autoCheckForUpdates"]!.GetValue<bool>());
        Assert.Equal("1.0.4", onDisk["skippedUpdateVersion"]!.GetValue<string>());
        Assert.Equal(42, onDisk["someFutureKey"]!.GetValue<int>());
    }

    [Fact]
    public void AnOlderBuildsUnknownKeyCarryIsNotNeededForTheseFive()
    {
        // Control for the test above: if the five names drift out of KnownKeys
        // this is what the failure would look like, so pin the names to the
        // exact JSON keys the model writes.
        Write("{}");
        OrbweaverSettings.PeerLinkEnabled = true;
        var onDisk = Read();
        foreach (var key in new[] { "autoCheckForUpdates", "includePrereleaseUpdates", "skippedUpdateVersion", "lastUpdateCheckUtc", "lastRunVersion" })
            Assert.True(onDisk.ContainsKey(key), key);
    }
}
