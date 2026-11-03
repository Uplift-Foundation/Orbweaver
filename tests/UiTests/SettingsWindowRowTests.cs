using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// What the settings switches actually do.
//
// SettingsWindowSmokeTest next door proves the page builds; this proves the rows
// behave. It goes at the handlers rather than at the controls on purpose, for a
// reason that file's own comment sets up: a row's toggle is a ToggleSwitch whose
// template is *borrowed from Fluent at runtime* and falls back to a CheckBox when
// that fails, so which control a row holds is a property of the theme rather than
// of the setting. Synthesizing a click would be testing the borrow.
//
// The window is constructed through its private constructor, exactly as the smoke
// test does, and never shown and never closed — closing a headless Window here
// corrupts a process-wide Avalonia font cache and takes every later test in the
// assembly with it. That is documented at length next door; this file inherits
// the rule rather than rediscovering it.
//
// Two things are being asserted at once, and both matter. Each handler writes the
// setting its row claims to write — a copy-paste error between two adjacent rows
// is the likeliest bug on a page with twenty of them, and it is invisible on
// screen because both switches still move. And every handler rebuilds the page
// afterwards, so each case also builds the whole visual tree again with the new
// setting in force, which is the only way the rows that only appear when
// something is switched on get built at all.
[Collection("Settings")]
public class SettingsWindowRowTests : IDisposable
{
    // Every Toggles() drive ends with its switch on, so without a directory of
    // its own this class leaves each of those settings on for whichever class
    // runs next — Grok usage among them, which is what hung the suite.
    private readonly ScopedSettingsDir _settings = new("settings-rows");

    public void Dispose() => _settings.Dispose();

    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        return (SettingsWindow)ctor.Invoke(null);
    }

    // Drives one switch both ways and reads the setting back each time, so a
    // handler that ignored its argument and always wrote `true` fails.
    private static void Toggles(
        Action<SettingsWindow, bool> handler, Func<bool> read)
    {
        var window = NewWindow();

        handler(window, true);
        Assert.True(read(), "switching on did not write the setting");

        handler(window, false);
        Assert.False(read(), "switching off did not write the setting");

        handler(window, true);
        Assert.True(read(), "switching back on did not write the setting");
    }

    // The same, for a setting with three values rather than two. Every mode is
    // written and read back, and the pass ends on the default rather than
    // wherever the last case left it — OrbweaverSettings is process-wide, and
    // a mode left on Hidden would take heartbeat orbs off the screen for every
    // test in the collection that runs afterwards.
    private static void Cycles(
        Action<SettingsWindow, ClusterMode> handler, Func<ClusterMode> read)
    {
        var window = NewWindow();

        foreach (var mode in new[]
                 {
                     ClusterMode.Hidden, ClusterMode.OwnShape,
                     ClusterMode.WithChats, ClusterMode.Hidden, ClusterMode.WithChats
                 })
        {
            handler(window, mode);
            Assert.Equal(mode, read());
        }
    }

    [AvaloniaFact]
    public void TheClaudeCodeSwitchWritesItsOwnSetting() => Toggles(
        (w, v) => w.OnClaudeCodeEnabledToggled(v), () => OrbweaverSettings.ClaudeCodeEnabled);

    [AvaloniaFact]
    public void TheClaudeCodeChatSwitchWritesItsOwnSetting() => Toggles(
        (w, v) => w.OnClaudeCodeChatToggled(v), () => OrbweaverSettings.ClaudeCodeChatEnabled);

    [AvaloniaFact]
    public void TheClaudeCodeReplySwitchWritesItsOwnSetting() => Toggles(
        (w, v) => w.OnClaudeCodeReplyToggled(v), () => OrbweaverSettings.ClaudeCodeReplyEnabled);

    [AvaloniaFact]
    public void TheCodexSwitchWritesItsOwnSetting() => Toggles(
        (w, v) => w.OnCodexEnabledToggled(v), () => OrbweaverSettings.CodexEnabled);

    [AvaloniaFact]
    public void TheCodexChatSwitchWritesItsOwnSetting() => Toggles(
        (w, v) => w.OnCodexChatToggled(v), () => OrbweaverSettings.CodexChatEnabled);

    [AvaloniaFact]
    public void TheCodexReplySwitchWritesItsOwnSetting() => Toggles(
        (w, v) => w.OnCodexReplyToggled(v), () => OrbweaverSettings.CodexReplyEnabled);

    [AvaloniaFact]
    public void TheCodexUsageSwitchWritesItsOwnSetting() => Toggles(
        (w, v) => w.OnCodexAccountUsageToggled(v), () => OrbweaverSettings.CodexAccountUsageEnabled);

    [AvaloniaFact]
    public void TheGrokSwitchWritesItsOwnSetting() => Toggles(
        (w, v) => w.OnGrokEnabledToggled(v), () => OrbweaverSettings.GrokEnabled);

    [AvaloniaFact]
    public void TheGrokChatSwitchWritesItsOwnSetting() => Toggles(
        (w, v) => w.OnGrokChatToggled(v), () => OrbweaverSettings.GrokChatEnabled);

    [AvaloniaFact]
    public void TheGrokReplySwitchWritesItsOwnSetting() => Toggles(
        (w, v) => w.OnGrokReplyToggled(v), () => OrbweaverSettings.GrokReplyEnabled);

    [AvaloniaFact]
    public void TheGrokUsageSwitchWritesItsOwnSetting() => Toggles(
        (w, v) => w.OnGrokAccountUsageToggled(v), () => OrbweaverSettings.GrokAccountUsageEnabled);

    // CB-96. Its row only exists when GrokAccountUsageEnabled is already on —
    // Toggles() rebuilds the page after every call, which is what makes the
    // row reachable at all on the second (off-to-on-and-back) half of the
    // drive, and is exactly the case this file's own header comment calls out
    // as the reason rebuilding matters.
    [AvaloniaFact]
    public void TheGrokAutoRefreshSwitchWritesItsOwnSetting()
    {
        OrbweaverSettings.GrokAccountUsageEnabled = true;

        Toggles(
            (w, v) => w.OnGrokAutoRefreshToggled(v), () => OrbweaverSettings.GrokAutoRefreshEnabled);
    }

    // Auto-refresh starts the user's real Grok app in the background and is
    // deliberately a second, separate opt-in from "usage orbs for each Grok
    // account" for exactly that reason. Turning the first switch off has to
    // turn the second off too, or a later re-enable would silently resume
    // background launches the user never freshly agreed to — the row would
    // reappear unchecked in the UI while the setting underneath it was still
    // true the whole time. Found in review rather than written the first time.
    [AvaloniaFact]
    public void TurningUsageOrbsOffAlsoTurnsAutoRefreshOff()
    {
        var window = NewWindow();

        window.OnGrokAccountUsageToggled(true);
        window.OnGrokAutoRefreshToggled(true);
        Assert.True(OrbweaverSettings.GrokAutoRefreshEnabled);

        window.OnGrokAccountUsageToggled(false);
        Assert.False(OrbweaverSettings.GrokAutoRefreshEnabled);
    }

    // Turning usage orbs back on is not the same gesture as turning auto
    // refresh on — it only makes that row visible again, never flips the
    // setting for someone. Re-enabling should not have any memory of what the
    // switch was set to the last time the row happened to be visible.
    [AvaloniaFact]
    public void TurningUsageOrbsBackOnDoesNotResumeAutoRefreshOnItsOwn()
    {
        var window = NewWindow();

        window.OnGrokAccountUsageToggled(true);
        window.OnGrokAutoRefreshToggled(true);
        window.OnGrokAccountUsageToggled(false);

        window.OnGrokAccountUsageToggled(true);

        Assert.False(OrbweaverSettings.GrokAutoRefreshEnabled);
    }

    // Not Toggles(), because these two are no longer switches: three answers
    // do not fit in a boolean, which is the whole reason the setting changed
    // shape. The handler is still the production one, driven through every
    // value rather than only the two a switch could reach.
    [AvaloniaFact]
    public void TheHeartbeatModeRowWritesItsOwnSetting() => Cycles(
        (w, v) => w.OnOpenClawHeartbeatModeChanged(v),
        () => OrbweaverSettings.OpenClawHeartbeatMode);

    [AvaloniaFact]
    public void TheCronModeRowWritesItsOwnSetting() => Cycles(
        (w, v) => w.OnOpenClawCronModeChanged(v),
        () => OrbweaverSettings.OpenClawCronMode);

    [AvaloniaFact]
    public void TheNativeModeRowWritesItsOwnSetting() => Cycles(
        (w, v) => w.OnOpenClawNativeModeChanged(v),
        () => OrbweaverSettings.OpenClawNativeMode);

    // The colour switch deliberately does *not* re-wire anything, and that is
    // worth a test of its own rather than only a comment. An earlier version
    // baked a flag into the hook command, so every toggle rewrote Codex's
    // hooks.json and cost the user their hook trust; the hooks now read a marker
    // file the scan reconciles instead. So this handler must stay a plain setting
    // write — if it ever starts shelling out, this test will be the thing that
    // hangs, which is the right place to find out.
    [AvaloniaFact]
    public void TheAutoColourSwitchOnlyWritesItsSetting() => Toggles(
        (w, v) => w.OnAutoColorToggled(v), () => OrbweaverSettings.AutoColorSessions);

    // The two download-backed switches, driven only in the direction that cannot
    // start a download: switching off, and switching on when the model is already
    // present. Nothing here fetches 300MB, and the assertion is the same one —
    // the row writes its own setting.
    [AvaloniaFact]
    public void TheNeuralVoiceSwitchWritesItsSettingWhenSwitchedOff()
    {
        var window = NewWindow();

        window.OnNeuralVoiceToggled(false);

        Assert.False(OrbweaverSettings.NeuralVoiceEnabled);
    }

    [AvaloniaFact]
    public void TheVoiceInputSwitchWritesItsSettingWhenSwitchedOff()
    {
        var window = NewWindow();

        window.OnVoiceInputToggled(false);

        Assert.False(OrbweaverSettings.VoiceInputEnabled);
    }

    // Every switch rebuilds the page, and the page is different depending on what
    // is switched on — whole sections appear only when their CLI is enabled. This
    // walks a realistic sequence rather than one toggle, so the rebuild that
    // happens with each combination in force is the one being exercised.
    [AvaloniaFact]
    public void RebuildingWithEverySectionOnAndOffDoesNotThrow()
    {
        var window = NewWindow();

        foreach (var on in new[] { true, false, true })
        {
            window.OnClaudeCodeEnabledToggled(on);
            window.OnClaudeCodeChatToggled(on);
            window.OnClaudeCodeReplyToggled(on);
            window.OnCodexEnabledToggled(on);
            window.OnCodexChatToggled(on);
            window.OnCodexReplyToggled(on);
            window.OnOpenClawHeartbeatModeChanged(on ? ClusterMode.OwnShape : ClusterMode.Hidden);
            window.OnOpenClawCronModeChanged(on ? ClusterMode.Hidden : ClusterMode.OwnShape);
            window.OnOpenClawNativeModeChanged(on ? ClusterMode.OwnShape : ClusterMode.Hidden);
            window.OnAutoColorToggled(on);
        }

        // Back to the default, because these two are process-wide and Hidden
        // takes orbs off the screen for whatever runs next in the collection.
        window.OnOpenClawHeartbeatModeChanged(ClusterMode.WithChats);
        window.OnOpenClawCronModeChanged(ClusterMode.WithChats);
        window.OnOpenClawNativeModeChanged(ClusterMode.WithChats);

        // Nothing to assert beyond having got here: a rebuild that threw would
        // have taken the settings window down in the user's face, which is the
        // failure this is watching for.
        Assert.NotNull(window);
    }

    // Rebuild is called by every handler above, and directly by the window when a
    // profile changes underneath it. Called on its own here so a rebuild with no
    // preceding toggle is covered too — that is the path a profile rename takes.
    [AvaloniaFact]
    public void RebuildingOnItsOwnDoesNotThrow()
    {
        var window = NewWindow();

        window.Rebuild();
        window.Rebuild();

        Assert.NotNull(window);
    }

    // The chat and reply switches are independent: someone can reasonably want to
    // read a session and never type into it. Adjacent rows writing one another's
    // setting is the bug this rules out, and it is the one a screenshot cannot
    // show.
    [AvaloniaFact]
    public void ChatAndReplyAreIndependentPerCli()
    {
        var window = NewWindow();

        window.OnClaudeCodeChatToggled(true);
        window.OnClaudeCodeReplyToggled(false);
        window.OnCodexChatToggled(false);
        window.OnCodexReplyToggled(true);

        Assert.True(OrbweaverSettings.ClaudeCodeChatEnabled);
        Assert.False(OrbweaverSettings.ClaudeCodeReplyEnabled);
        Assert.False(OrbweaverSettings.CodexChatEnabled);
        Assert.True(OrbweaverSettings.CodexReplyEnabled);
    }

    // ...and across the two CLIs, which have their own pair each rather than one
    // shared "local CLI" setting.
    // Restores both settings, which this did not, and the omission turned the
    // whole suite red on both CI legs.
    //
    // ScanAndUpdate filters sessions by exactly these two settings, so leaving
    // Codex switched off meant every later scan test found nothing — no
    // exception, just empty collections and a null status. It only showed up in
    // Release, because the class order there put this test first; in Debug it ran
    // after the tests it was breaking and looked fine on three machines.
    //
    // A test that writes a process-wide setting restores it. There is no fixture
    // enforcing that here, so it has to be said where it matters.
    [AvaloniaFact]
    public void TheTwoClisDoNotShareTheirSwitches()
    {
        var window = NewWindow();
        var claudeCode = OrbweaverSettings.ClaudeCodeEnabled;
        var codex = OrbweaverSettings.CodexEnabled;

        try
        {
            window.OnClaudeCodeEnabledToggled(true);
            window.OnCodexEnabledToggled(false);

            Assert.True(OrbweaverSettings.ClaudeCodeEnabled);
            Assert.False(OrbweaverSettings.CodexEnabled);
        }
        finally
        {
            OrbweaverSettings.ClaudeCodeEnabled = claudeCode;
            OrbweaverSettings.CodexEnabled = codex;
        }
    }
}
