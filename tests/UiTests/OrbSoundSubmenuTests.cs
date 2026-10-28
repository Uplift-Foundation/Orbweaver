using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-167's orb half: the right-click "Sound" submenu, driven the same way
// OrbWindowPresenceTests drives ApplyEndSessionGuard — through
// RebuildSoundSubmenus() directly rather than a synthesized right-click,
// since opening a real ContextMenu needs a shown window and a working popup
// that nothing else in this suite depends on.
//
// [Collection("Settings")] because every case here reads and writes
// OrbweaverSettings.OrbTurnSounds, which is process-wide the same way
// every other settings-touching suite in this collection is.
[Collection("Settings")]
public class OrbSoundSubmenuTests : IDisposable
{
    private readonly List<string> _clearedKeys = new();
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        SettingsWindow.ChooseSoundFileForTests = null;
        foreach (var key in _clearedKeys) OrbweaverSettings.ClearOrbTurnSound(key);
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    // A real file on disk, the same reason SoundSettingsRowTests' own
    // version exists — a made-up path is indistinguishable from a real one
    // to everything except SystemSoundCatalog.Resolve's File.Exists check,
    // which is exactly the thing QA round 2 found silently skipped.
    private string NewRealTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "cb-orb-sound-" + Guid.NewGuid() + ".wav");
        File.WriteAllBytes(path, Array.Empty<byte>());
        _tempFiles.Add(path);
        return path;
    }

    private OrbWindow NewOrb(string? soundKey = null)
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString())
        {
            SoundKey = soundKey ?? "orb-sound-test-" + Guid.NewGuid()
        };
        _clearedKeys.Add(orb.SoundKey);
        return orb;
    }

    private static List<string> SystemSounds() => SystemSoundCatalog.List(
        SystemSoundCatalog.DefaultDirectory, SystemSoundCatalog.DefaultExtensions);

    // FindControl rather than reflection into the compiler-generated field —
    // it resolves through the same NameScope a real x:Name reference does,
    // and needs no BindingFlags guess about the generated field's visibility.
    private static MenuItem FinishedMenu(OrbWindow orb) =>
        orb.FindControl<MenuItem>("SoundFinishedMenuItem")!;

    private static MenuItem AttentionMenu(OrbWindow orb) =>
        orb.FindControl<MenuItem>("SoundAttentionMenuItem")!;

    // --- building the list ---

    [AvaloniaFact]
    public void RebuildingPopulatesBothSubmenusWithTheDefaultCheckedByDefault()
    {
        var orb = NewOrb();

        orb.RebuildSoundSubmenus();

        var finished = FinishedMenu(orb).Items.Cast<MenuItem>().ToList();
        var attention = AttentionMenu(orb).Items.Cast<MenuItem>().ToList();

        Assert.Equal($"Default ({SystemSoundCatalog.DefaultFinishedSoundName})", finished[0].Header);
        Assert.True(finished[0].IsChecked);
        Assert.All(finished.Skip(1), item => Assert.False(item.IsChecked));

        Assert.Equal($"Default ({SystemSoundCatalog.DefaultAttentionSoundName})", attention[0].Header);
        Assert.True(attention[0].IsChecked);

        // needsAttentionSound has no vibe-summary value.
        Assert.DoesNotContain(attention, item => Equals(item.Header, SettingsWindow.VibeSummaryLabel));
        Assert.Contains(finished, item => Equals(item.Header, SettingsWindow.VibeSummaryLabel));
    }

    // Rebuilt fresh: a saved override is reflected as the checked item, not
    // just written and forgotten.
    [AvaloniaFact]
    public void TheSavedOverrideIsTheCheckedItem()
    {
        var orb = NewOrb();
        OrbweaverSettings.SetOrbTurnSound(orb.SoundKey, "off", null);

        orb.RebuildSoundSubmenus();

        var finished = FinishedMenu(orb).Items.Cast<MenuItem>().ToList();
        var checkedItem = Assert.Single(finished, item => item.IsChecked);
        Assert.Equal("Off", checkedItem.Header);

        var attention = AttentionMenu(orb).Items.Cast<MenuItem>().ToList();
        Assert.True(attention[0].IsChecked); // still Default: only Finished was overridden
    }

    // --- selecting writes the override, and leaves the other trigger alone ---

    [AvaloniaFact]
    public void ClickingOffWritesTheFinishedOverrideWithoutTouchingAttention()
    {
        var orb = NewOrb();
        OrbweaverSettings.SetOrbTurnSound(orb.SoundKey, null, "Ping");

        orb.RebuildSoundSubmenus();
        var offItem = FinishedMenu(orb).Items.Cast<MenuItem>().Single(i => Equals(i.Header, "Off"));
        offItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

        var over = OrbweaverSettings.OrbTurnSoundFor(orb.SoundKey);
        Assert.Equal("off", over?.Finished);
        Assert.Equal("Ping", over?.Attention);
    }

    [AvaloniaFact]
    public void ClickingDefaultClearsJustThatTriggersOverride()
    {
        var orb = NewOrb();
        OrbweaverSettings.SetOrbTurnSound(orb.SoundKey, "off", "Ping");

        orb.RebuildSoundSubmenus();
        var defaultItem = FinishedMenu(orb).Items.Cast<MenuItem>().First();
        defaultItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

        var over = OrbweaverSettings.OrbTurnSoundFor(orb.SoundKey);
        Assert.Null(over?.Finished);
        Assert.Equal("Ping", over?.Attention);
    }

    // Clearing both triggers removes the entry entirely — SetOrbTurnSound's
    // own rule, reached here from the menu rather than asserted only against
    // the settings layer directly.
    [AvaloniaFact]
    public void ClickingDefaultOnBothTriggersRemovesTheEntryEntirely()
    {
        var orb = NewOrb();
        OrbweaverSettings.SetOrbTurnSound(orb.SoundKey, "off", "off");

        orb.RebuildSoundSubmenus();
        FinishedMenu(orb).Items.Cast<MenuItem>().First()
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        orb.RebuildSoundSubmenus();
        AttentionMenu(orb).Items.Cast<MenuItem>().First()
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Null(OrbweaverSettings.OrbTurnSoundFor(orb.SoundKey));
    }

    [AvaloniaFact]
    public void ClickingASystemSoundWritesItsNameForAttention()
    {
        var systemSounds = SystemSounds();
        if (systemSounds.Count == 0) return; // nothing installed on this runner to assert against

        var orb = NewOrb();
        orb.RebuildSoundSubmenus();

        var item = AttentionMenu(orb).Items.Cast<MenuItem>()
            .Single(i => Equals(i.Header, systemSounds[0]));
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal(systemSounds[0], OrbweaverSettings.OrbTurnSoundFor(orb.SoundKey)?.Attention);
    }

    // --- Choose file… ---

    [AvaloniaFact]
    public async Task ChoosingChooseFileWritesThePickedPathForFinished()
    {
        var picked = NewRealTempFile();
        SettingsWindow.ChooseSoundFileForTests = () => Task.FromResult<string?>(picked);

        var orb = NewOrb();
        orb.RebuildSoundSubmenus();

        var chooseItem = FinishedMenu(orb).Items.Cast<MenuItem>().Single(i => Equals(i.Header, "Choose file…"));
        chooseItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

        // async void Click handler: yield so its continuation (which runs
        // past the already-completed seam task) gets a turn before the
        // assertion reads the setting it writes.
        await Task.Yield();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(picked, OrbweaverSettings.OrbTurnSoundFor(orb.SoundKey)?.Finished);
    }

    [AvaloniaFact]
    public async Task CancellingChooseFileWritesNothing()
    {
        SettingsWindow.ChooseSoundFileForTests = () => Task.FromResult<string?>(null);

        var orb = NewOrb();
        orb.RebuildSoundSubmenus();

        var chooseItem = FinishedMenu(orb).Items.Cast<MenuItem>().Single(i => Equals(i.Header, "Choose file…"));
        chooseItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

        await Task.Yield();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Null(OrbweaverSettings.OrbTurnSoundFor(orb.SoundKey));
    }

    // --- an orb with no stable key yet ---

    // SetOrbTurnSound/OrbTurnSoundFor both no-op on an empty key (the same
    // rule ChatPanelSizeFor's neighbours follow), so an orb whose
    // RestoreOrbPosition hasn't run yet — SoundKey still "" — offers a menu
    // that simply writes nothing, rather than throwing or crashing the
    // context menu open.
    [AvaloniaFact]
    public void AnOrbWithNoSoundKeyYetDoesNotThrow()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        Assert.Equal("", orb.SoundKey);

        orb.RebuildSoundSubmenus();
        var offItem = FinishedMenu(orb).Items.Cast<MenuItem>().Single(i => Equals(i.Header, "Off"));
        offItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Null(OrbweaverSettings.OrbTurnSoundFor(""));
    }

    // SessionMenu_Opening reaches RebuildSoundSubmenus() even when no
    // SessionManager is current — the guard below it is for
    // ApplyEndSessionGuard's own DependentsOf call, and must not also skip
    // the sound submenus, which have nothing to do with dependents.
    [AvaloniaFact]
    public void SessionMenuOpeningPopulatesTheSubmenusWithNoSessionManagerCurrent()
    {
        Assert.Null(SessionManager.Instance);

        var orb = NewOrb();
        orb.SessionMenu_Opening(null, new System.ComponentModel.CancelEventArgs());

        Assert.NotEmpty(FinishedMenu(orb).Items.Cast<MenuItem>());
        Assert.NotEmpty(AttentionMenu(orb).Items.Cast<MenuItem>());
    }

    // --- SoundKey staleness (QA round 2, HIGH) ---
    //
    // These build orbs the way SessionManager actually does — through
    // UpdateFrom, not NewOrb()'s direct SoundKey assignment — since the bug
    // is specifically about SoundKey being wrong *because* it was cached
    // once instead of tracking the status that computes it.

    // The finding itself: an untitled orb keys on its own session id
    // (SessionManager.PositionKeyFor's CB-10 fix, inherited by SoundKeyFor).
    // A menu choice made in that window used to be stranded there forever,
    // because SoundKey was never recomputed once the real title arrived and
    // the scan moved on to looking the override up under the new key — the
    // menu kept showing the choice ticked while the orb went on chiming.
    [AvaloniaFact]
    public void TheMenuStillShowsAndWritesUnderTheKeyAfterTheOrbIsTitled()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var cwd = "/Users/user/project-" + Guid.NewGuid();

        orb.UpdateFrom(new SessionStatus { State = "idle", Cwd = cwd, Title = "" });
        var oldKey = orb.SoundKey;
        Assert.Equal(orb.SessionId, oldKey); // untitled: keys on the session id
        _clearedKeys.Add(oldKey);

        orb.RebuildSoundSubmenus();
        var offItem = FinishedMenu(orb).Items.Cast<MenuItem>().Single(i => Equals(i.Header, "Off"));
        offItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal("off", OrbweaverSettings.OrbTurnSoundFor(oldKey)?.Finished);

        // Claude Code names the session.
        orb.UpdateFrom(new SessionStatus { State = "idle", Cwd = cwd, Title = "claude-buddy" });
        var newKey = orb.SoundKey;
        _clearedKeys.Add(newKey);

        Assert.NotEqual(oldKey, newKey);
        // QA round 3, finding 3: copied onto the new key, not moved off the
        // old one — two orbs can share a key (same cwd and title, no agent
        // name to tell them apart), and clearing the old key on the way out
        // would silently unmute whichever sibling is still called by that
        // name. An orphaned entry under a key nothing looks up any more is
        // harmless, so it is left exactly where it was.
        Assert.Equal("off", OrbweaverSettings.OrbTurnSoundFor(oldKey)?.Finished);
        Assert.Equal("off", OrbweaverSettings.OrbTurnSoundFor(newKey)?.Finished);

        // And the menu, reopened, agrees — reads the override back under the
        // key it now actually lives at, not the one it was set under.
        orb.RebuildSoundSubmenus();
        var checkedItem = Assert.Single(FinishedMenu(orb).Items.Cast<MenuItem>(), i => i.IsChecked);
        Assert.Equal("Off", checkedItem.Header);
    }

    // QA round 3 (a): an orb that never gets titled at all — a background
    // job, a session Claude Code never named — must keep working on its
    // session-id key across every poll, not just the first one. Every
    // UpdateFrom recomputes the key (that is the whole fix), so this is the
    // negative control for the migration case above: the key must be
    // exactly as stable here as it was before this ticket touched it.
    [AvaloniaFact]
    public void AnOrbThatIsNeverTitledKeepsWorkingOnItsSessionIdKey()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var cwd = "/Users/user/project-" + Guid.NewGuid();

        orb.UpdateFrom(new SessionStatus { State = "idle", Cwd = cwd, Title = "" });
        var key = orb.SoundKey;
        Assert.Equal(orb.SessionId, key);
        _clearedKeys.Add(key);

        orb.RebuildSoundSubmenus();
        var offItem = FinishedMenu(orb).Items.Cast<MenuItem>().Single(i => Equals(i.Header, "Off"));
        offItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal("off", OrbweaverSettings.OrbTurnSoundFor(key)?.Finished);

        // Several more polls, still untitled — the key must not drift, and
        // the override must keep reading back correctly every time.
        for (var i = 0; i < 3; i++)
        {
            orb.UpdateFrom(new SessionStatus { State = "idle", Cwd = cwd, Title = "" });
            Assert.Equal(key, orb.SoundKey);
            Assert.Equal("off", OrbweaverSettings.OrbTurnSoundFor(orb.SoundKey)?.Finished);
        }

        orb.RebuildSoundSubmenus();
        var checkedItem = Assert.Single(FinishedMenu(orb).Items.Cast<MenuItem>(), i => i.IsChecked);
        Assert.Equal("Off", checkedItem.Header);
    }

    // The no-op path: a key that hasn't changed writes nothing and migrates
    // nothing, whether or not an override exists — RefreshSoundKey's own
    // `if (key == SoundKey) return;` guard, exercised both with and without
    // an override present so a future change can't quietly turn this into
    // an unconditional re-write.
    [AvaloniaFact]
    public void RefreshSoundKeyIsANoOpWhenTheKeyHasNotChanged()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var status = new SessionStatus { State = "idle", Cwd = "/Users/user/project", Title = "claude-buddy" };

        orb.UpdateFrom(status);
        var key = orb.SoundKey;
        _clearedKeys.Add(key);
        OrbweaverSettings.SetOrbTurnSound(key, "off", null);

        orb.UpdateFrom(status); // the identical status again

        Assert.Equal(key, orb.SoundKey);
        Assert.Equal("off", OrbweaverSettings.OrbTurnSoundFor(key)?.Finished);
    }

    // CB-268 T2's member offset rides the SoundKey like the size does, so it is carried
    // by the same rule when the key changes: copied, and never over an offset the new
    // key already has.
    [AvaloniaFact]
    public void RefreshSoundKeyCarriesAMemberOffsetToTheNewKeyWithoutMovingIt()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(new SessionStatus { State = "idle", Cwd = "/Users/user/project", Title = "" });
        var oldKey = orb.SoundKey;
        OrbweaverSettings.SetOrbMemberOffset(oldKey, (30.0, -20.0));

        orb.UpdateFrom(new SessionStatus { State = "idle", Cwd = "/Users/user/project", Title = "titled-later" });
        var newKey = orb.SoundKey;
        try
        {
            Assert.NotEqual(oldKey, newKey);
            Assert.Equal(((double, double)?)(30.0, -20.0), OrbweaverSettings.OrbMemberOffsetFor(newKey));
            Assert.Equal(((double, double)?)(30.0, -20.0), OrbweaverSettings.OrbMemberOffsetFor(oldKey));
        }
        finally
        {
            OrbweaverSettings.SetOrbMemberOffset(oldKey, null);
            OrbweaverSettings.SetOrbMemberOffset(newKey, null);
        }
    }

    // The key changes but there was never an override to move — just the
    // key itself updates, and nothing gets written under either key.
    [AvaloniaFact]
    public void RefreshSoundKeyJustUpdatesTheKeyWhenThereWasNoOverrideToMigrate()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var cwd = "/Users/user/project-" + Guid.NewGuid();

        orb.UpdateFrom(new SessionStatus { State = "idle", Cwd = cwd, Title = "" });
        var oldKey = orb.SoundKey;
        _clearedKeys.Add(oldKey);

        orb.UpdateFrom(new SessionStatus { State = "idle", Cwd = cwd, Title = "claude-buddy" });
        var newKey = orb.SoundKey;
        _clearedKeys.Add(newKey);

        Assert.NotEqual(oldKey, newKey);
        Assert.Null(OrbweaverSettings.OrbTurnSoundFor(oldKey));
        Assert.Null(OrbweaverSettings.OrbTurnSoundFor(newKey));
    }

    // The end-to-end version, copied and adapted from QA round 2's own
    // demonstrating suite (Cb167QaRound2UiTests.cs, written to fail on
    // 5370ac56) rather than reinvented — this is the acceptance test QA
    // actually asked for by this name, driving a real SessionManager over
    // real status files rather than calling RebuildSoundSubmenus() and
    // UpdateFrom() directly the way the rest of this file does. It proves
    // the thing a user would actually notice: not just that the key
    // migrates, but that the orb genuinely stays quiet afterward, through
    // the real scan → TurnSoundPolicy → ChimePlayer path.
    //
    // Its own settings directory (ORBWEAVER_SETTINGS_DIR +
    // OrbweaverSettings.ReloadForTests()) rather than this file's usual
    // _clearedKeys cleanup — the same isolation TurnSoundScanTests uses,
    // since a real SessionManager scan reads OrbweaverSettings.
    // TurnSoundsEnabled and friends beyond just OrbTurnSounds, and mixing a
    // scan-level case into a suite that otherwise only pokes individual
    // settings keys is exactly where a leftover value from a sibling test
    // would matter.
    [AvaloniaFact]
    public void AnOverrideSetFromTheMenuAfterTheOrbIsTitledStillApplies()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-orbsound-e2e-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            Environment.SetEnvironmentVariable(
                "ORBWEAVER_SETTINGS_DIR", Path.Combine(dir, "settings"));
            OrbweaverSettings.ReloadForTests();
            TurnSounds.ResetForTests();

            var played = new List<string>();
            ChimePlayer.PlayForTests = p => { lock (played) played.Add(p); };
            OrbweaverSettings.ClaudeCodeEnabled = true;

            void WriteLocal(string id, string state, string title) => File.WriteAllText(
                Path.Combine(dir, id + ".txt"),
                System.Text.Json.JsonSerializer.Serialize(new SessionStatus
                {
                    State = state,
                    Title = title,
                    Cwd = "/Users/user/project",
                    SessionPid = Environment.ProcessId,
                    TermProgram = "iTerm.app",
                    Tty = "/dev/ttys004",
                }));

            WriteLocal("sess-1", "idle", title: "");
            var manager = new SessionManager(dir);
            manager.ScanAndUpdate(); // orb created untitled

            WriteLocal("sess-1", "generating", title: "fix-login");
            manager.ScanAndUpdate(); // now titled — SoundKey must have followed

            var windows = (Dictionary<string, OrbWindow>)typeof(SessionManager)
                .GetField("_windows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(manager)!;
            var window = windows["sess-1"];

            var expectedKey = SessionManager.SoundKeyFor(new SessionStatus
            {
                Source = SessionSource.ClaudeCode, Cwd = "/Users/user/project", Title = "fix-login",
            }, "sess-1");
            Assert.Equal(expectedKey, window.SoundKey);

            // What the menu's "Off" click does — set directly, since this
            // case is about what the scan does with the override once it
            // exists, not about the menu's own click handling (covered
            // elsewhere in this file).
            OrbweaverSettings.SetOrbTurnSound(window.SoundKey, "off", null);

            WriteLocal("sess-1", "idle", title: "fix-login"); // generating -> idle: Finished
            manager.ScanAndUpdate();

            // Playback is a background Task.Run now (QA round 2's other
            // fix), so give it a moment to have run if it was going to.
            Thread.Sleep(500);
            lock (played) Assert.Empty(played);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // The migration case QA round 2 asked for by name: the new key already
    // carries its own override — a second run of the same now-titled
    // session that had already had a choice made under this exact key.
    // Migrating the old key's override on top would silently throw that
    // choice away, so RefreshSoundKey leaves both alone rather than
    // guessing which one the user meant to keep.
    [AvaloniaFact]
    public void RefreshSoundKeyDoesNotOverwriteAnExistingOverrideUnderTheNewKey()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var cwd = "/Users/user/project-" + Guid.NewGuid();

        orb.UpdateFrom(new SessionStatus { State = "idle", Cwd = cwd, Title = "" });
        var oldKey = orb.SoundKey;
        _clearedKeys.Add(oldKey);
        OrbweaverSettings.SetOrbTurnSound(oldKey, "off", null);

        var newKey = SessionManager.SoundKeyFor(
            new SessionStatus { State = "idle", Cwd = cwd, Title = "claude-buddy" }, orb.SessionId);
        _clearedKeys.Add(newKey);
        OrbweaverSettings.SetOrbTurnSound(newKey, "Ping", null);

        orb.UpdateFrom(new SessionStatus { State = "idle", Cwd = cwd, Title = "claude-buddy" });

        Assert.Equal(newKey, orb.SoundKey);
        Assert.Equal("Ping", OrbweaverSettings.OrbTurnSoundFor(newKey)?.Finished);
        Assert.Equal("off", OrbweaverSettings.OrbTurnSoundFor(oldKey)?.Finished);
    }

    // QA round 3, finding 2 (LOW-MEDIUM), copied and adapted from
    // Cb167QaRound3UiTests.cs rather than reinvented: a blank key reads as
    // "free" to both OrbTurnSoundFor (its own empty-key guard) and
    // SetOrbTurnSound (an empty-key guard that makes the write a no-op) —
    // so migrating *into* one used to look like a safe migration onto an
    // unoccupied key, write nothing there, and still clear the real
    // override out from under the old key on the way out. Reachable on a
    // real machine: OrbweaverHook.sh:70 writes cwd with no fallback, so
    // one status write missing it is enough to blank the key for a single
    // poll.
    [AvaloniaFact]
    public void ALiveOrbWhoseCwdGoesBlankKeepsItsOverride()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var cwd = "/Users/user/project-" + Guid.NewGuid();

        orb.UpdateFrom(new SessionStatus { State = "idle", Cwd = cwd, Title = "fix-login" });
        var key = orb.SoundKey;
        _clearedKeys.Add(key);
        OrbweaverSettings.SetOrbTurnSound(key, "off", null);

        orb.UpdateFrom(new SessionStatus { State = "idle", Cwd = "", Title = "fix-login" }); // one status write without cwd
        orb.UpdateFrom(new SessionStatus { State = "idle", Cwd = cwd, Title = "fix-login" }); // back to normal

        Assert.Equal(key, orb.SoundKey); // never drifted onto the blank key
        Assert.NotNull(OrbweaverSettings.OrbTurnSoundFor(orb.SoundKey));
        Assert.Equal("off", OrbweaverSettings.OrbTurnSoundFor(orb.SoundKey)?.Finished);
    }

    // QA round 3, finding 3 (LOW-MEDIUM), copied and adapted from
    // Cb167QaRound3UiTests.cs: two live orbs sharing one key (same cwd and
    // title, no agent name — exactly the collision SoundKeyFor cannot tell
    // apart) both read a mute set under that shared key. Renaming one of
    // them used to MOVE the override onto its own new key and clear the
    // shared key on the way out, so the other orb — still called by the
    // old name — silently started chiming again. Copying instead of moving
    // is the fix; this is the case that fix exists for.
    [AvaloniaFact]
    public void RenamingOneOfTwoOrbsThatShareAKeyDoesNotUnmuteTheOther()
    {
        var x = new OrbWindow(Guid.NewGuid().ToString());
        var y = new OrbWindow(Guid.NewGuid().ToString());
        var cwd = "/Users/user/project-" + Guid.NewGuid();

        x.UpdateFrom(new SessionStatus { State = "idle", Cwd = cwd, Title = "build" });
        y.UpdateFrom(new SessionStatus { State = "idle", Cwd = cwd, Title = "build" });
        Assert.Equal(x.SoundKey, y.SoundKey);
        _clearedKeys.Add(y.SoundKey);
        OrbweaverSettings.SetOrbTurnSound(y.SoundKey, "off", null);

        x.UpdateFrom(new SessionStatus { State = "idle", Cwd = cwd, Title = "deploy" });
        _clearedKeys.Add(x.SoundKey);

        Assert.NotEqual(x.SoundKey, y.SoundKey);
        Assert.Equal("off", OrbweaverSettings.OrbTurnSoundFor(y.SoundKey)?.Finished);
    }
}
