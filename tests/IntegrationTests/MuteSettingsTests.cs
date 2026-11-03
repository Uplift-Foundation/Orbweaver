using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-298's two keys through a real settings.json on disk, modelled on
// TurnSoundSettingsTests: the unit tests cover what the mute decides, this
// covers the seam its inputs are read through, which fails differently — the
// parser loses the field, or a garbage value costs the whole file.
[Collection("Settings")]
public class MuteSettingsTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "cb-mute-log-" + Guid.NewGuid());
    private readonly IDisposable _logScope;

    public MuteSettingsTests()
    {
        _logScope = CrashLog.ScopeForTests(_logDir);
        HotkeyLog.ResetForTests();
    }

    public void Dispose()
    {
        _logScope.Dispose();
        HotkeyLog.ResetForTests();
        try { Directory.Delete(_logDir, recursive: true); } catch { }
    }

    private static string NewSettingsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-mute-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void PointSettingsAt(string dir)
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    private static JsonObject Root(string dir) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;

    [Fact]
    public void MutedDefaultsToOff()
    {
        PointSettingsAt(NewSettingsDir());

        Assert.False(OrbweaverSettings.Muted);
    }

    [Fact]
    public void MutedRoundTripsBothWaysThroughTheFile()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        OrbweaverSettings.Muted = true;
        Assert.True(Root(dir)["muted"]!.GetValue<bool>());
        PointSettingsAt(dir);
        Assert.True(OrbweaverSettings.Muted);

        OrbweaverSettings.Muted = false;
        PointSettingsAt(dir);
        Assert.False(OrbweaverSettings.Muted);
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    public void AGarbageMutedCostsOnlyItselfNotEveryOtherSetting(string garbage)
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            "{ \"speakScope\": \"summary\", \"turnFinishedSound\": \"Hero\", \"muted\": " + garbage + " }");

        PointSettingsAt(dir);

        Assert.Equal(SpeakScope.Summary, OrbweaverSettings.SpeakScope);
        Assert.Equal("Hero", OrbweaverSettings.TurnFinishedSound);
        Assert.False(OrbweaverSettings.Muted);
    }

    // The downgrade protection: a key this build does not know survives a save
    // that changes a key it does.
    [Fact]
    public void AnUnknownKeyBesideItSurvivesASet()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"someFutureKey\": 42 }");
        PointSettingsAt(dir);

        OrbweaverSettings.Muted = true;

        var root = Root(dir);
        Assert.Equal(42, root["someFutureKey"]!.GetValue<int>());
        Assert.True(root["muted"]!.GetValue<bool>());
    }

    [Fact]
    public void ToggleMuteHotkeyDefaultsToNullAndRoundTrips()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);
        Assert.Null(OrbweaverSettings.ToggleMuteHotkey);

        OrbweaverSettings.ToggleMuteHotkey = "Ctrl+Shift+M";
        PointSettingsAt(dir);

        Assert.Equal("Ctrl+Shift+M", OrbweaverSettings.ToggleMuteHotkey);
        Assert.Equal("Ctrl+Shift+M", Root(dir)["toggleMuteHotkey"]!.GetValue<string>());
    }

    // The registry falls back from a spelling it cannot parse; the file is not
    // "corrected" behind the user's back.
    [Fact]
    public void AnUnparseableHotkeyIsKeptVerbatimInTheFileAndFallsBack()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"toggleMuteHotkey\": \"Hyper+M\" }");
        PointSettingsAt(dir);

        Assert.Equal("Hyper+M", OrbweaverSettings.ToggleMuteHotkey);
        Assert.Equal(HotkeyRegistry.Default(HotkeyAction.ToggleMute),
            HotkeyRegistry.Resolve(HotkeyAction.ToggleMute, OrbweaverSettings.ToggleMuteHotkey));

        OrbweaverSettings.Muted = true;   // any save
        Assert.Equal("Hyper+M", Root(dir)["toggleMuteHotkey"]!.GetValue<string>());
    }

    // A mute chord that loses a collision falls back, and says so in hotkeys.log.
    [Fact]
    public void AMuteChordCollidingWithTheTogglesWritesTheFallbackNote()
    {
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"toggleMuteHotkey\": \"Ctrl+Alt+H\" }");
        PointSettingsAt(dir);

        HotkeyActions.Plan();

        var lines = File.ReadAllLines(HotkeyLog.Path_);
        Assert.Contains(lines, l => l.Contains("ToggleMute") && l.Contains("Ctrl+Alt+M"));
    }
}
