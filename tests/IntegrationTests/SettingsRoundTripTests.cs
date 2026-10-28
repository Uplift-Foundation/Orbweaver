using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// OrbweaverSettings is a static class: one model shared by the whole
// process, guarded by its own lock but with no isolation between test
// cases. Every test here repoints ORBWEAVER_SETTINGS_DIR and calls
// ReloadForTests() before touching anything, and the whole class is
// [Collection("Settings")] so xUnit never runs two of them at once — without
// that, two settings tests running in parallel would stomp each other's
// env var and both read/write whichever directory won the race.
[CollectionDefinition("Settings")]
public class SettingsCollection
{
}

[Collection("Settings")]
public class SettingsRoundTripTests
{
    private static string NewSettingsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-integrationtests-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void PointSettingsAt(string dir)
    {
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    [Fact]
    public void SettingWithADirectSetter_IsWrittenToDiskImmediately()
    {
        // TwoLetterGlyphs's setter calls Save() directly (OrbweaverSettings.cs
        // ~line 601), not SaveSoon() — no debounce, so it should be on disk the
        // instant the setter returns.
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        OrbweaverSettings.TwoLetterGlyphs = true;

        var settingsPath = Path.Combine(dir, "settings.json");
        Assert.True(File.Exists(settingsPath));

        var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        Assert.NotNull(root);
        Assert.True(root!["twoLetterGlyphs"]!.GetValue<bool>());
    }

    [Fact]
    public void ClusterModes_DefaultToWithChatsAndRoundTripBothWays()
    {
        // Default-WithChats is load-bearing rather than incidental: these orbs
        // are on screen today and in the same shape as everything else, and an
        // upgrade that removed several of somebody's agents — or moved them to
        // the far side of the screen — would read as the gateway having dropped
        // them rather than as a new setting having a default.
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        Assert.Equal(ClusterMode.WithChats, OrbweaverSettings.OpenClawHeartbeatMode);
        Assert.Equal(ClusterMode.WithChats, OrbweaverSettings.OpenClawCronMode);

        // Hidden in particular for the heartbeats, because that is the value a
        // bug would produce by accident: a missing key reads as WithChats, so
        // only Hidden proves the write happened.
        OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.Hidden;
        OrbweaverSettings.OpenClawCronMode = ClusterMode.OwnShape;
        OrbweaverSettings.OpenClawCronShape = "star";

        var settingsPath = Path.Combine(dir, "settings.json");
        var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;

        Assert.Equal("hidden", root!["openclawHeartbeatMode"]!.GetValue<string>());
        Assert.Equal("own", root["openclawCronMode"]!.GetValue<string>());
        Assert.Equal("star", root["openclawCronShape"]!.GetValue<string>());

        // The legacy boolean is still written, derived from the mode, so a build
        // that has never heard of openclawHeartbeatMode still honours the one
        // thing it can understand. See the comment on it in Save().
        Assert.False(root["openclawShowHeartbeats"]!.GetValue<bool>());

        // And all of it survives a reload, which is what the settings are for.
        PointSettingsAt(dir);
        Assert.Equal(ClusterMode.Hidden, OrbweaverSettings.OpenClawHeartbeatMode);
        Assert.Equal(ClusterMode.OwnShape, OrbweaverSettings.OpenClawCronMode);
        Assert.Equal("star", OrbweaverSettings.OpenClawCronShape);
    }

    [Theory]
    [InlineData(false, ClusterMode.Hidden)]
    [InlineData(true, ClusterMode.WithChats)]
    public void LegacyShowHeartbeatsBoolean_MigratesToAMode(bool shown, ClusterMode expected)
    {
        // The upgrade path, written as a real settings.json from the version
        // before this one rather than by calling the parser: what has to keep
        // working is a file already on somebody's disk, and the thing that
        // breaks is Load reaching for a key that isn't there.
        var dir = NewSettingsDir();
        var json = new JsonObject
        {
            ["version"] = 1,
            ["openclawEnabled"] = true,
            ["openclawShowHeartbeats"] = shown
        };
        File.WriteAllText(Path.Combine(dir, "settings.json"), json.ToJsonString());

        PointSettingsAt(dir);

        Assert.Equal(expected, OrbweaverSettings.OpenClawHeartbeatMode);

        // Crons had no setting at all before this, so there is nothing to
        // migrate and the default is the behaviour they already had.
        Assert.Equal(ClusterMode.WithChats, OrbweaverSettings.OpenClawCronMode);
    }

    [Fact]
    public void HeartbeatMode_WinsOverTheLegacyBooleanWhenBothArePresent()
    {
        // The file this app writes has both keys in it, and they can disagree —
        // any older build that saves once will rewrite the boolean without
        // touching the mode. The mode is the one the user chose in this version,
        // so it has to be the one that survives; reading the boolean first would
        // silently undo a choice of Own shape every time an older build ran.
        var dir = NewSettingsDir();
        var json = new JsonObject
        {
            ["version"] = 1,
            ["openclawShowHeartbeats"] = true,
            ["openclawHeartbeatMode"] = "hidden"
        };
        File.WriteAllText(Path.Combine(dir, "settings.json"), json.ToJsonString());

        PointSettingsAt(dir);

        Assert.Equal(ClusterMode.Hidden, OrbweaverSettings.OpenClawHeartbeatMode);
    }

    [Fact]
    public void ClusterModeKeys_SurviveAHandEditedGarbageValue()
    {
        // Load wraps the whole model in one catch that replaces it with
        // defaults, so a value of the wrong *type* reaching GetValue<T>() costs
        // the user every profile name and dragged orb position in the file. That
        // is why these read through Text/TryGetValue — see ClusterModeFrom.
        var dir = NewSettingsDir();
        var json = new JsonObject
        {
            ["version"] = 1,
            ["twoLetterGlyphs"] = true,
            ["openclawShowHeartbeats"] = "no thanks",
            ["openclawHeartbeatMode"] = 7,
            ["openclawCronMode"] = "sideways"
        };
        File.WriteAllText(Path.Combine(dir, "settings.json"), json.ToJsonString());

        PointSettingsAt(dir);

        // Both fall back to the default rather than throwing...
        Assert.Equal(ClusterMode.WithChats, OrbweaverSettings.OpenClawHeartbeatMode);
        Assert.Equal(ClusterMode.WithChats, OrbweaverSettings.OpenClawCronMode);

        // ...and, the point of the test, the rest of the file is still there.
        Assert.True(OrbweaverSettings.TwoLetterGlyphs);
    }

    [Fact]
    public void ShowHeartbeats_IsInKnownKeys_SoItIsWrittenExactlyOnce()
    {
        // The trap KnownKeys' own comment describes: a key that Save writes but
        // that Load didn't recognise round-trips through _unknownKeys *as well
        // as* being written properly, and JsonObject rejects the duplicate. That
        // failure is a hard throw on every Save, so it is worth one assertion.
        //
        // Written as a real second launch rather than by reading KnownKeys: it is
        // the interaction between Load and Save that breaks, not the list.
        var dir = NewSettingsDir();
        PointSettingsAt(dir);
        OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.Hidden;

        PointSettingsAt(dir);
        OrbweaverSettings.TwoLetterGlyphs = true;   // any Save at all

        var text = File.ReadAllText(Path.Combine(dir, "settings.json"));
        var root = JsonNode.Parse(text) as JsonObject;

        Assert.False(root!["openclawShowHeartbeats"]!.GetValue<bool>());

        // One occurrence each, not two. All four of the new keys as well as the
        // legacy one, since every one of them is a fresh chance to miss the
        // KnownKeys entry.
        foreach (var key in new[]
                 {
                     "openclawShowHeartbeats", "openclawHeartbeatMode",
                     "openclawHeartbeatShape", "openclawCronMode", "openclawCronShape"
                 })
        {
            Assert.Equal(1, text.Split($"\"{key}\"").Length - 1);
        }
    }

    // The single most valuable untested piece of OrbweaverSettings.cs, per
    // its own comment on _unknownKeys (~line 38): Save() rebuilds the whole
    // document from the in-memory model, so any key it doesn't know about
    // would otherwise be silently deleted the next time anything is saved —
    // observed for real as speakCommand/neuralVoiceEnabled/neuralVoice
    // vanishing when an older build ran briefly next to a newer settings.json.
    // _unknownKeys exists so a build that has never heard of a key still
    // writes it back untouched.
    [Fact]
    public void AnUnknownSettingsKey_SurvivesASaveTriggeredByAnUnrelatedChange()
    {
        var dir = NewSettingsDir();
        var settingsPath = Path.Combine(dir, "settings.json");

        File.WriteAllText(settingsPath, """
            {
              "showOrbs": true,
              "twoLetterGlyphs": false,
              "futureFeatureFlag": true
            }
            """);

        PointSettingsAt(dir);

        // Any real setter works here; TwoLetterGlyphs's Save() is direct and
        // synchronous, so the write below is guaranteed to have happened by
        // the time this method returns.
        OrbweaverSettings.TwoLetterGlyphs = true;

        var rewritten = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        Assert.NotNull(rewritten);
        Assert.True(
            rewritten!.ContainsKey("futureFeatureFlag"),
            "a key this build doesn't recognise must round-trip through _unknownKeys " +
            "rather than being silently dropped on the next save");
        Assert.True(rewritten["futureFeatureFlag"]!.GetValue<bool>());

        // And the real change this Save() was actually for still landed.
        Assert.True(rewritten["twoLetterGlyphs"]!.GetValue<bool>());
    }

    // ArrangeAnchor is the shape's saved on-screen centre (see
    // SessionManager.ArrangementAnchor) — it must survive a restart or the
    // next "arrange" click after a relaunch silently falls back to the
    // screen-centre default, which reads to a user as "it forgot where I put
    // it". Written manually into Save()'s JsonObject and parsed back out of
    // Load()'s, the same way OrbPositions is a few lines above each — this
    // test exists because the first cut of this feature added the property
    // to Model without wiring either side, so it worked for the life of one
    // process and silently reset on every restart.
    [Fact]
    public void ArrangeAnchor_SurvivesARestart()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        OrbweaverSettings.ArrangeAnchor = new OrbweaverSettings.OrbPlacement(640, 360);

        // Simulate a relaunch: force the next access to re-read settings.json
        // from disk rather than serve the in-memory model.
        PointSettingsAt(dir);

        var restored = OrbweaverSettings.ArrangeAnchor;
        Assert.NotNull(restored);
        Assert.Equal(640, restored!.X);
        Assert.Equal(360, restored.Y);
    }

    // IdleColor/GeneratingColor/WaitingColor are the only three setters that
    // go through SaveSoon() instead of Save() directly (grep confirms — see
    // OrbweaverSettings.cs ~line 630-646), because the colour pickers raise
    // a change event on every pointer move and a direct Save() per event
    // would thrash the disk. SaveSoon() debounces via a DispatcherTimer.
    //
    // The task brief for this test assumed that constructing/starting that
    // timer with no Avalonia dispatcher running throws, and that the write
    // therefore falls through to a synchronous Save() immediately. Checked
    // by hand with a reflection probe before writing this: it does not.
    // Avalonia.Threading.DispatcherTimer constructs and Starts() without any
    // exception in a plain xUnit process (Avalonia lazily creates a
    // per-thread dispatcher rather than requiring Application.Run() first),
    // and 1 full second of Thread.Sleep afterwards was not enough for
    // settings.json to appear — because nothing in a console test process
    // ever pumps that dispatcher's queue, the timer is left "enabled" but
    // never actually ticks, and Save() is simply never called.
    //
    // So the real, exercisable contract is FlushPendingSave() — the method
    // this class exposes specifically for "a pending deferred write must not
    // be lost", per its own comment ("A deferred write that never happens is
    // a preference silently lost, so anything that might be the last thing
    // to happen calls this."). This test proves that contract: the debounced
    // setter alone leaves nothing on disk, and FlushPendingSave() is what
    // actually gets it there.
    [Fact]
    public void ADeferredColorSetting_IsNotOnDiskUntilFlushPendingSaveIsCalled()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);
        var settingsPath = Path.Combine(dir, "settings.json");

        OrbweaverSettings.IdleColor = "green";

        Assert.False(
            File.Exists(settingsPath),
            "SaveSoon() debounces via a DispatcherTimer that nothing in this test process pumps, " +
            "so the write should still be pending immediately after the setter returns");

        OrbweaverSettings.FlushPendingSave();

        Assert.True(
            File.Exists(settingsPath),
            "FlushPendingSave() exists precisely so a pending deferred write is not silently lost");

        var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        Assert.NotNull(root);
        var orbColors = root!["orbColors"] as JsonObject;
        Assert.NotNull(orbColors);
        Assert.Equal("green", orbColors!["idle"]!.GetValue<string>());
    }

    // The three Remote Control settings, round-tripped together.
    //
    // The specific hazard this guards is the one KnownKeys exists for: a key
    // written by Save() but missing from KnownKeys is *also* replayed out of
    // _unknownKeys, and JsonObject throws on the duplicate — so a forgotten
    // KnownKeys entry doesn't degrade, it breaks every Save from then on.
    // Reading the values back through a reload is what proves all five steps
    // (model field, accessor, Load parse, Save write, KnownKeys) were done.
    [Fact]
    public void RemoteControlSettings_RoundTripThroughDiskAndReload()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        OrbweaverSettings.RemoteControlEnabled = true;
        OrbweaverSettings.RemoteControlProfileDir = ".claude-board";
        OrbweaverSettings.RemoteControlIdleMinutes = 25;
        OrbweaverSettings.RemoteControlServeOnLaunch = true;

        // Back from disk, not from the in-memory model the setters just wrote.
        PointSettingsAt(dir);

        Assert.True(OrbweaverSettings.RemoteControlEnabled);
        Assert.Equal(".claude-board", OrbweaverSettings.RemoteControlProfileDir);
        Assert.Equal(25, OrbweaverSettings.RemoteControlIdleMinutes);
        Assert.True(OrbweaverSettings.RemoteControlServeOnLaunch);
    }

    [Fact]
    public void RemoteControlSettings_HaveSafeDefaultsBeforeAnyoneChoosesThem()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        // Off by default: enabling it is what permits Buddy to start a real
        // Claude Code session on the user's account, which costs them quota.
        Assert.False(OrbweaverSettings.RemoteControlEnabled);

        // Never empty — the bridge has to launch under some config directory.
        Assert.Equal(
            OrbweaverSettings.DefaultRemoteControlProfileDir,
            OrbweaverSettings.RemoteControlProfileDir);

        Assert.Equal(
            OrbweaverSettings.DefaultRemoteControlIdle,
            OrbweaverSettings.RemoteControlIdleMinutes);

        // Also off by default, for the same reason as the switch above: it
        // makes every app launch start a quota-spending relay.
        Assert.False(OrbweaverSettings.RemoteControlServeOnLaunch);
    }

    // A negative idle would read as "already expired" to every comparison
    // downstream, stopping the bridge the instant it started — which would look
    // like the feature being broken rather than a bad setting.
    // RemoteControlIdleNever is the deliberate way to say "never stop".
    [Fact]
    public void RemoteControlIdleMinutes_ClampsANegativeToNever()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        OrbweaverSettings.RemoteControlIdleMinutes = -5;

        Assert.Equal(OrbweaverSettings.RemoteControlIdleNever, OrbweaverSettings.RemoteControlIdleMinutes);
    }

    // An unchosen profile stays null on disk rather than being written as a copy
    // of today's default, so changing the shipped default still reaches users
    // who never picked one.
    [Fact]
    public void RemoteControlProfileDir_IsNotWrittenToDiskUntilChosen()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);
        var settingsPath = Path.Combine(dir, "settings.json");

        OrbweaverSettings.RemoteControlEnabled = true;

        var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        Assert.NotNull(root);
        Assert.True(root!.ContainsKey("remoteControlProfileDir"));
        Assert.Null(root["remoteControlProfileDir"]);

        // The accessor still answers with the default, so callers never see null.
        Assert.Equal(
            OrbweaverSettings.DefaultRemoteControlProfileDir,
            OrbweaverSettings.RemoteControlProfileDir);
    }

    // One panel size per agent, keyed and reloaded independently — the whole
    // point of the setting, and the part a shared-singleton bug would break
    // silently: ChatPanel is one window serving every session, so a size
    // stored under the wrong key looks fine until a second agent is opened.
    [Fact]
    public void ChatPanelSizes_RoundTripPerAgentThroughDisk()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        OrbweaverSettings.SetChatPanelSize("agent-a", 500, 600);
        OrbweaverSettings.SetChatPanelSize("agent-b", 300.4, 250.6);

        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json"))) as JsonObject;
        var sizes = root!["chatPanelSizes"] as JsonObject;
        Assert.NotNull(sizes);

        // Rounded on the way in, so a drag that ends on a fraction of a DIP
        // doesn't leave 250.60000000000002 in a file people hand-edit.
        Assert.Equal(300, sizes!["agent-b"]!["width"]!.GetValue<double>());
        Assert.Equal(251, sizes["agent-b"]!["height"]!.GetValue<double>());

        // Read back from disk rather than from the model that just wrote it:
        // the parse side is its own code path and had to be added by hand to
        // Load, which is exactly the step speakVoice was once missing.
        PointSettingsAt(dir);

        var a = OrbweaverSettings.ChatPanelSizeFor("agent-a");
        Assert.NotNull(a);
        Assert.Equal(500, a!.Width);
        Assert.Equal(600, a.Height);

        Assert.Equal(300, OrbweaverSettings.ChatPanelSizeFor("agent-b")!.Width);

        // Never resized means null, not a copy of the shipped default — that
        // is what lets ChatPanel keep owning what "default" means.
        Assert.Null(OrbweaverSettings.ChatPanelSizeFor("agent-never-opened"));

        // A session with no stable identity (a local CLI orb with no cwd) has
        // nothing to save under, and saying so must not create a "" entry.
        OrbweaverSettings.SetChatPanelSize("", 400, 400);
        Assert.Null(OrbweaverSettings.ChatPanelSizeFor(""));
    }
    // The hazard this file already documents for colours, in the newest block
    // to read numbers: Load() sits inside one catch that replaces the *whole*
    // model with defaults, so a wrong-typed value reaching GetValue<double>()
    // would cost every profile name and dragged orb position in the file over
    // one bad panel size. Number() is why it doesn't.
    [Fact]
    public void AMalformedChatPanelSize_CostsOnlyThatEntry()
    {
        var dir = NewSettingsDir();
        var settingsPath = Path.Combine(dir, "settings.json");

        // Hand-written rather than round-tripped through the setters: these
        // are shapes the app itself would never produce, and a file people
        // edit is exactly where they come from.
        File.WriteAllText(settingsPath, """
        {
          "twoLetterGlyphs": true,
          "orbPositions": { "/some/repo": { "x": 12, "y": 34 } },
          "chatPanelSizes": {
            "agent-string-width": { "width": "wide", "height": 400 },
            "agent-missing-height": { "width": 500 },
            "agent-not-an-object": 7,
            "agent-null-width": { "width": null, "height": 400 },
            "agent-fine": { "width": 480, "height": 500 }
          }
        }
        """);

        PointSettingsAt(dir);

        // The good entry survives...
        var fine = OrbweaverSettings.ChatPanelSizeFor("agent-fine");
        Assert.NotNull(fine);
        Assert.Equal(480, fine!.Width);

        // ...each broken one is dropped rather than half-applied...
        Assert.Null(OrbweaverSettings.ChatPanelSizeFor("agent-string-width"));
        Assert.Null(OrbweaverSettings.ChatPanelSizeFor("agent-missing-height"));
        Assert.Null(OrbweaverSettings.ChatPanelSizeFor("agent-not-an-object"));
        Assert.Null(OrbweaverSettings.ChatPanelSizeFor("agent-null-width"));

        // ...and — the actual point — nothing else in the file was lost to it.
        Assert.True(OrbweaverSettings.TwoLetterGlyphs);
        var position = OrbweaverSettings.OrbPositionFor("/some/repo");
        Assert.NotNull(position);
        Assert.Equal(12, position!.X);
    }

    // A panel is re-bound every time its orb is clicked, and the drag handler
    // saves on every pointer release — so the guard against rewriting an
    // unchanged value is what keeps that from being a file write each time.
    // Asserted by deleting the file and checking nothing recreates it, which
    // is deterministic where comparing timestamps would not be.
    [Fact]
    public void SettingAChatPanelSizeToTheValueItAlreadyHas_WritesNothing()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);
        var settingsPath = Path.Combine(dir, "settings.json");

        OrbweaverSettings.SetChatPanelSize("agent-a", 500, 600);
        Assert.True(File.Exists(settingsPath));

        File.Delete(settingsPath);

        // Same size, including a fraction that rounds to the same whole DIPs
        // as the stored value — the comparison happens after rounding.
        OrbweaverSettings.SetChatPanelSize("agent-a", 500, 600);
        OrbweaverSettings.SetChatPanelSize("agent-a", 500.2, 599.8);

        Assert.False(
            File.Exists(settingsPath),
            "an unchanged size should return before Save(), so nothing recreates the deleted file");

        // A real change still writes, so the guard isn't just refusing to save.
        OrbweaverSettings.SetChatPanelSize("agent-a", 501, 600);
        Assert.True(File.Exists(settingsPath));
    }

    // chatPanelSizes is a key an older build has never heard of, which is the
    // situation _unknownKeys exists for — worth pinning for this key
    // specifically, since the generic test uses an invented one that no
    // version of this app will ever write.
    [Fact]
    public void ChatPanelSizes_SurviveASaveByABuildThatOnlyKnowsOtherKeys()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        OrbweaverSettings.SetChatPanelSize("agent-a", 500, 600);
        OrbweaverSettings.TwoLetterGlyphs = true;
        OrbweaverSettings.IdleColor = "green";
        OrbweaverSettings.FlushPendingSave();

        PointSettingsAt(dir);

        var a = OrbweaverSettings.ChatPanelSizeFor("agent-a");
        Assert.NotNull(a);
        Assert.Equal(500, a!.Width);
        Assert.Equal(600, a.Height);
    }

    // CB-111: one pinned position per agent, keyed and reloaded independently
    // — the same shape ChatPanelSizes_RoundTripPerAgentThroughDisk already
    // proves for sizes, checked here for the sibling dictionary the pin
    // feature added.
    [Fact]
    public void PinnedChatPanels_RoundTripPerAgentThroughDisk()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        OrbweaverSettings.SetPinnedChatPanelPosition("agent-a", 100, 200);
        OrbweaverSettings.SetPinnedChatPanelPosition("agent-b", -40, 900);

        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json"))) as JsonObject;
        var pinned = root!["pinnedChatPanels"] as JsonObject;
        Assert.NotNull(pinned);
        Assert.Equal(-40, pinned!["agent-b"]!["x"]!.GetValue<int>());
        Assert.Equal(900, pinned["agent-b"]!["y"]!.GetValue<int>());

        // Read back from disk rather than from the model that just wrote it —
        // the parse side is its own code path in Load, and it is exactly the
        // step a new setting can be added everywhere else and forgotten here.
        PointSettingsAt(dir);

        var a = OrbweaverSettings.PinnedChatPanelPositionFor("agent-a");
        Assert.NotNull(a);
        Assert.Equal(100, a!.X);
        Assert.Equal(200, a.Y);

        Assert.Equal(-40, OrbweaverSettings.PinnedChatPanelPositionFor("agent-b")!.X);

        // Never pinned means null — there is no "pinned" bool to default
        // false, presence in the dictionary is the whole of what pinned means.
        Assert.Null(OrbweaverSettings.PinnedChatPanelPositionFor("agent-never-pinned"));

        // No stable identity, nothing to save under — same guard SetChatPanelSize
        // has for the identical reason (a local CLI orb with no cwd).
        OrbweaverSettings.SetPinnedChatPanelPosition("", 1, 1);
        Assert.Null(OrbweaverSettings.PinnedChatPanelPositionFor(""));
    }

    // ClearPinnedChatPanelPosition is the only thing CB-111 lets forget a
    // pin — see ChatPanel.Unpin's own comment on why the panel closing or the
    // app quitting must not reach this. Proven here at the settings layer,
    // independent of ChatPanel: the entry really leaves the dictionary and
    // really leaves the file, not just the in-memory model.
    [Fact]
    public void ClearingAPinnedChatPanelPosition_RemovesItFromDiskToo()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        OrbweaverSettings.SetPinnedChatPanelPosition("agent-a", 10, 20);
        Assert.NotNull(OrbweaverSettings.PinnedChatPanelPositionFor("agent-a"));

        OrbweaverSettings.ClearPinnedChatPanelPosition("agent-a");
        Assert.Null(OrbweaverSettings.PinnedChatPanelPositionFor("agent-a"));

        var root = JsonNode.Parse(
            File.ReadAllText(Path.Combine(dir, "settings.json"))) as JsonObject;
        var pinned = root!["pinnedChatPanels"] as JsonObject;
        Assert.NotNull(pinned);
        Assert.False(pinned!.ContainsKey("agent-a"));

        // Clearing a key that was never there is a no-op, not an error — the
        // same shape ClearOrbPosition already has, checked here so the two
        // don't drift apart.
        OrbweaverSettings.ClearPinnedChatPanelPosition("agent-never-there");
    }

    // Same hazard SettingAChatPanelSizeToTheValueItAlreadyHas_WritesNothing
    // guards against, for the dictionary a drag now writes into on every
    // PositionChanged event rather than only once per gesture (ChatPanel has
    // no equivalent of OnResizePointerReleased for a native move drag — see
    // the comment on ChatPanel's PositionChanged handler) — so the guard here
    // is what keeps a drag from being a file write per pixel once the pointer
    // stops moving and the platform's last few events repeat the same point.
    [Fact]
    public void SettingAPinnedChatPanelPositionToTheValueItAlreadyHas_WritesNothing()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);
        var settingsPath = Path.Combine(dir, "settings.json");

        OrbweaverSettings.SetPinnedChatPanelPosition("agent-a", 100, 200);
        Assert.True(File.Exists(settingsPath));

        File.Delete(settingsPath);

        OrbweaverSettings.SetPinnedChatPanelPosition("agent-a", 100, 200);
        Assert.False(
            File.Exists(settingsPath),
            "an unchanged position should return before Save(), so nothing recreates the deleted file");

        OrbweaverSettings.SetPinnedChatPanelPosition("agent-a", 101, 200);
        Assert.True(File.Exists(settingsPath));
    }

    // A malformed entry costs only itself, the same guarantee
    // AMalformedChatPanelSize_CostsOnlyThatEntry proves for sizes — Load()
    // sits inside one catch that replaces the *whole* model with defaults on
    // an unhandled exception, so a bad pinned-panel entry must be dropped by
    // the per-entry null checks rather than ever reaching that catch.
    [Fact]
    public void AMalformedPinnedChatPanelEntry_CostsOnlyThatEntry()
    {
        var dir = NewSettingsDir();
        var settingsPath = Path.Combine(dir, "settings.json");

        File.WriteAllText(settingsPath, """
        {
          "twoLetterGlyphs": true,
          "orbPositions": { "/some/repo": { "x": 12, "y": 34 } },
          "pinnedChatPanels": {
            "agent-string-x": { "x": "wide", "y": 400 },
            "agent-missing-y": { "x": 500 },
            "agent-not-an-object": 7,
            "agent-fine": { "x": 480, "y": 500 }
          }
        }
        """);

        PointSettingsAt(dir);

        var fine = OrbweaverSettings.PinnedChatPanelPositionFor("agent-fine");
        Assert.NotNull(fine);
        Assert.Equal(480, fine!.X);

        Assert.Null(OrbweaverSettings.PinnedChatPanelPositionFor("agent-string-x"));
        Assert.Null(OrbweaverSettings.PinnedChatPanelPositionFor("agent-missing-y"));
        Assert.Null(OrbweaverSettings.PinnedChatPanelPositionFor("agent-not-an-object"));

        Assert.True(OrbweaverSettings.TwoLetterGlyphs);
        var position = OrbweaverSettings.OrbPositionFor("/some/repo");
        Assert.NotNull(position);
        Assert.Equal(12, position!.X);
    }

    // Unknown-key preservation, pinned (no pun intended) for this key
    // specifically — the same reason ChatPanelSizes_SurviveASaveByABuildThatOnlyKnowsOtherKeys
    // exists beside the generic unknown-keys test elsewhere in this file: a
    // key this build understands has to survive a save that only touches
    // other settings, which is a different failure mode than an old build
    // never having heard of the key at all.
    [Fact]
    public void PinnedChatPanels_SurviveASaveByABuildThatOnlyKnowsOtherKeys()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        OrbweaverSettings.SetPinnedChatPanelPosition("agent-a", 100, 200);
        OrbweaverSettings.TwoLetterGlyphs = true;
        OrbweaverSettings.IdleColor = "green";
        OrbweaverSettings.FlushPendingSave();

        PointSettingsAt(dir);

        var a = OrbweaverSettings.PinnedChatPanelPositionFor("agent-a");
        Assert.NotNull(a);
        Assert.Equal(100, a!.X);
        Assert.Equal(200, a.Y);
    }

    // CB-155: HotkeyRegistry.Resolve is what actually falls back to the
    // built-in default for a null/invalid override — this only has to prove
    // the string itself survives a save and a reload, the same round trip
    // every other setting here is checked against.
    [Fact]
    public void ToggleOrbsHotkey_DefaultsToNullAndRoundTripsAnOverride()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        Assert.Null(OrbweaverSettings.ToggleOrbsHotkey);

        OrbweaverSettings.ToggleOrbsHotkey = "Ctrl+Shift+H";

        var settingsPath = Path.Combine(dir, "settings.json");
        var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        Assert.Equal("Ctrl+Shift+H", root!["toggleOrbsHotkey"]!.GetValue<string>());

        PointSettingsAt(dir);
        Assert.Equal("Ctrl+Shift+H", OrbweaverSettings.ToggleOrbsHotkey);
    }

    // The new-chat hotkey's override, on the toggle's terms: null by default,
    // and the string survives a save and a reload.
    [Fact]
    public void NewChatHotkey_DefaultsToNullAndRoundTripsAnOverride()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        Assert.Null(OrbweaverSettings.NewChatHotkey);

        OrbweaverSettings.NewChatHotkey = "Ctrl+Shift+N";

        var settingsPath = Path.Combine(dir, "settings.json");
        var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        Assert.Equal("Ctrl+Shift+N", root!["newChatHotkey"]!.GetValue<string>());

        PointSettingsAt(dir);
        Assert.Equal("Ctrl+Shift+N", OrbweaverSettings.NewChatHotkey);

        // Clearing it goes back to "use the built-in binding" across a reload
        // too, rather than the old value resurfacing from anywhere.
        OrbweaverSettings.NewChatHotkey = null;
        PointSettingsAt(dir);
        Assert.Null(OrbweaverSettings.NewChatHotkey);
    }

    // CB-220's usage-orb hotkey, on the same terms as the other two.
    [Fact]
    public void ToggleUsageOrbsHotkey_DefaultsToNullAndRoundTripsAnOverride()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        Assert.Null(OrbweaverSettings.ToggleUsageOrbsHotkey);

        OrbweaverSettings.ToggleUsageOrbsHotkey = "Ctrl+Shift+U";

        var settingsPath = Path.Combine(dir, "settings.json");
        var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        Assert.Equal("Ctrl+Shift+U", root!["toggleUsageOrbsHotkey"]!.GetValue<string>());

        PointSettingsAt(dir);
        Assert.Equal("Ctrl+Shift+U", OrbweaverSettings.ToggleUsageOrbsHotkey);
    }

    // showUsageOrbs inherits showOrbs until someone sets it, and is written
    // back as null rather than as a copy — so a file from before CB-220 with
    // orbs hidden keeps its usage orbs hidden through any number of saves,
    // and turning orbs back on brings the usage orbs with them.
    [Fact]
    public void ShowUsageOrbs_UnsetInheritsShowOrbsAndStaysUnsetAcrossASave()
    {
        var dir = NewSettingsDir();
        var settingsPath = Path.Combine(dir, "settings.json");
        File.WriteAllText(settingsPath, """{ "showOrbs": false }""");

        PointSettingsAt(dir);
        Assert.False(OrbweaverSettings.ShowUsageOrbs);
        Assert.Null(OrbweaverSettings.ShowUsageOrbsStored);

        OrbweaverSettings.TwoLetterGlyphs = true;   // any Save at all
        OrbweaverSettings.FlushPendingSave();

        var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        Assert.True(root!.ContainsKey("showUsageOrbs"));
        Assert.Null(root["showUsageOrbs"]);

        PointSettingsAt(dir);
        Assert.Null(OrbweaverSettings.ShowUsageOrbsStored);
        OrbweaverSettings.ShowOrbs = true;
        Assert.True(OrbweaverSettings.ShowUsageOrbs);
    }

    [Fact]
    public void ShowUsageOrbs_AnExplicitValueRoundTripsAndOverridesShowOrbs()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        OrbweaverSettings.ShowUsageOrbs = false;

        var settingsPath = Path.Combine(dir, "settings.json");
        var root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        Assert.False(root!["showUsageOrbs"]!.GetValue<bool>());
        Assert.True(root["showOrbs"]!.GetValue<bool>());

        PointSettingsAt(dir);
        Assert.False(OrbweaverSettings.ShowUsageOrbs);
        Assert.True(OrbweaverSettings.ShowOrbs);

        // And the other way round: shown while every session orb is hidden.
        File.WriteAllText(settingsPath, """{ "showOrbs": false, "showUsageOrbs": true }""");
        PointSettingsAt(dir);
        Assert.True(OrbweaverSettings.ShowUsageOrbs);
        Assert.Equal(true, OrbweaverSettings.ShowUsageOrbsStored);

        OrbweaverSettings.ShowUsageOrbsStored = null;
        PointSettingsAt(dir);
        Assert.False(OrbweaverSettings.ShowUsageOrbs);
    }

    // Every override survives a relaunch followed by a save that touches
    // neither, each is written exactly once, and a key this build has never
    // heard of rides along untouched beside them.
    //
    // What this cannot catch, stated so nobody reads it as covering it: a
    // hotkey key missing from KnownKeys. toggleOrbsHotkey was missing from
    // CB-155 until the new-chat hotkey was added, and the file came out
    // byte-identical either way, because Save skips any _unknownKeys entry
    // the model has already written. The "written once" assertion is
    // for the day that guard is removed, not a test of the list.
    [Fact]
    public void HotkeyOverrides_SurviveAnUnrelatedSaveAfterARelaunch_AndAreWrittenOnce()
    {
        var dir = NewSettingsDir();
        var settingsPath = Path.Combine(dir, "settings.json");
        File.WriteAllText(settingsPath,
            """{ "toggleOrbsHotkey": "Ctrl+Shift+H", "newChatHotkey": "Ctrl+Shift+N", "toggleUsageOrbsHotkey": "Ctrl+Shift+U", "showUsageOrbs": false, "someFutureSetting": 7 }""");

        PointSettingsAt(dir);
        OrbweaverSettings.TwoLetterGlyphs = true;   // any Save at all
        OrbweaverSettings.FlushPendingSave();

        var text = File.ReadAllText(settingsPath);
        var root = JsonNode.Parse(text) as JsonObject;
        Assert.Equal("Ctrl+Shift+H", root!["toggleOrbsHotkey"]!.GetValue<string>());
        Assert.Equal("Ctrl+Shift+N", root["newChatHotkey"]!.GetValue<string>());
        Assert.Equal("Ctrl+Shift+U", root["toggleUsageOrbsHotkey"]!.GetValue<string>());
        Assert.False(root["showUsageOrbs"]!.GetValue<bool>());
        Assert.Equal(7, root["someFutureSetting"]!.GetValue<int>());
        foreach (var key in new[] { "toggleOrbsHotkey", "newChatHotkey", "toggleUsageOrbsHotkey", "showUsageOrbs" })
        {
            Assert.Equal(1, text.Split($"\"{key}\"").Length - 1);
        }

        // And the overrides are what the model reads back, not merely bytes
        // that survived: a key replayed only out of _unknownKeys would pass
        // the file assertions above and still read as null here.
        PointSettingsAt(dir);
        Assert.Equal("Ctrl+Shift+H", OrbweaverSettings.ToggleOrbsHotkey);
        Assert.Equal("Ctrl+Shift+N", OrbweaverSettings.NewChatHotkey);
        Assert.Equal("Ctrl+Shift+U", OrbweaverSettings.ToggleUsageOrbsHotkey);
        Assert.False(OrbweaverSettings.ShowUsageOrbs);
    }

    // --- the shared persona pool (CB-291) ------------------------------------

    [Fact]
    public void PersonaPoolDir_SurvivesARestartAndIsNullUntilChosen()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        // Null rather than a copy of the default, so PersonaPool owns what "not
        // chosen" means (the environment, then the home directory).
        Assert.Null(OrbweaverSettings.PersonaPoolDir);

        OrbweaverSettings.PersonaPoolDir = "  /shared/personas  ";

        // Back from disk, trimmed, not from the in-memory model the setter wrote.
        PointSettingsAt(dir);
        Assert.Equal("/shared/personas", OrbweaverSettings.PersonaPoolDir);

        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!.AsObject();
        Assert.Equal("/shared/personas", saved["personaPoolDir"]!.GetValue<string>());
    }

    [Fact]
    public void PersonaPoolDir_ABlankValueClearsIt()
    {
        var dir = NewSettingsDir();
        PointSettingsAt(dir);

        OrbweaverSettings.PersonaPoolDir = "/shared/personas";
        OrbweaverSettings.PersonaPoolDir = "   ";

        PointSettingsAt(dir);
        Assert.Null(OrbweaverSettings.PersonaPoolDir);
    }

    [Fact]
    public void PersonaPoolDir_IsKnownSoItDoesNotAlsoRoundTripAsAnUnknownKey()
    {
        // Save rejects a key that is both written and carried through
        // _unknownKeys as a duplicate, so a setting missing from KnownKeys
        // throws on the first save after it is read back.
        var dir = NewSettingsDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"personaPoolDir\": \"/from/disk\" }");
        PointSettingsAt(dir);

        Assert.Equal("/from/disk", OrbweaverSettings.PersonaPoolDir);

        OrbweaverSettings.ShowOrbs = !OrbweaverSettings.ShowOrbs;   // any save at all

        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!.AsObject();
        Assert.Equal("/from/disk", saved["personaPoolDir"]!.GetValue<string>());
    }
}
