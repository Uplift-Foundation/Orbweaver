using Avalonia;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace Orbweaver.Tests;

// Matches tests/UiTests/SettingsWindowSmokeTest.cs's one scenario. Same
// private-constructor-via-reflection seam, same reason (Toggle() makes real
// OS calls this project has no business making headless), same "never
// closed" rule (see that test's own comment on the FontManager corruption a
// stray Close() caused once, in this exact suite).
//
// In the Settings collection because the Remote Control scenario below flips
// a process-wide setting before constructing its window — the same reason
// OrbClusterScreenshots is there.
[Collection("Settings")]
public class SettingsWindowScreenshots
{
    [AvaloniaFact]
    public void ConstructsHeadlessWithNoException()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        var window = (Avalonia.Controls.Window)ctor.Invoke(null);

        ScreenshotHelper.Capture(window, "settings-window-constructs-headless.png");
    }

    // One section folded above several open ones, driven through the model
    // (Sections["orbs"].IsOpen) rather than a click — there's no gesture
    // being tested here, only the resting look of a mixed page, and going
    // through IsOpen is what the collapse half's own tests already do for
    // the same reason.
    //
    // This is also where the header-hand-roll-vs-Expander argument in the
    // ticket's plan gets settled empirically rather than argued: a
    // hand-rolled ToggleButton header is identical logic on both platforms,
    // where Expander is templated separately by Devolutions on macOS and by
    // Fluent on Windows. Both rids should show the same chevron and the same
    // header chrome here, modulo system font — a divergence in this capture
    // is the parity regression the hand-roll was chosen to avoid.
    [AvaloniaFact]
    public void OneSectionFoldedAboveSeveralOpenOnes()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        var window = (SettingsWindow)ctor.Invoke(null);

        window.Sections["orbs"].IsOpen = false;

        ScreenshotHelper.Capture(window, "settings-sections-collapsed.png");
    }

    // The filter box holding a live query, so a reviewer can see the
    // search-narrowed page rather than infer it from SettingsFilterTests'
    // plain facts. "voice" rather than a broader term because it matches
    // exactly one section by title (SettingsFilter's own rule: a title match
    // shows the section entire), so the capture is a short, predictable page
    // rather than however much of the window still matches "code" or "the".
    //
    // Note for whoever reads this next to settings-window-constructs-
    // headless.png: that capture changed shape once the filter box landed —
    // it now carries a search bar docked above the scroller that didn't
    // exist before. Expected churn from this ticket, not a regression.
    [AvaloniaFact]
    public void FilterNarrowsToTheMatchingSection()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        var window = (SettingsWindow)ctor.Invoke(null);

        // Shown and flushed unfiltered first, so every section gets one real
        // layout pass while still visible — setting the filter on an
        // unshown window applies IsVisible=false to the non-matching
        // sections before they have ever been arranged, and a hidden
        // section's descendants that were never laid out at all can hand
        // back stale or degenerate bounds that land, coincidentally, inside
        // the frame the filtered page renders to.
        window.Show();
        ScreenshotHelper.Flush();

        var filterBox = window.GetLogicalDescendants()
            .OfType<TextBox>()
            .Single(box => box.Watermark == "Search settings");

        filterBox.Text = "voice";
        ScreenshotHelper.Flush();

        ScreenshotHelper.CaptureAlreadyShown(window, "settings-filter-active.png");
    }

    // The Claude Desktop group, which CB-4 added a row to: the switch that
    // decides whether Orbweaver claims Claude Desktop's URL schemes.
    //
    // Captured on its own rather than trusting the whole-window shot above,
    // because that one renders at the window's own height and this group sits
    // below the fold of a scrolling settings page — it would not appear at all.
    //
    // The row is macOS-only by design (the collision it works around is caused
    // by the tinted clone bundles, which have no Windows analogue), so the two
    // runners' captures are *expected* to differ here. That is the point of
    // having it on both: a reviewer comparing the rids can see the gate is
    // deliberate rather than a macOS-only implementation that forgot Windows.
    [AvaloniaFact]
    public void ClaudeDesktopGroupShowsTheUrlRoutingRowOnMacOs()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        var window = (Avalonia.Controls.Window)ctor.Invoke(null);

        // Shown and flushed so the settings page is measured and arranged —
        // the stack panels here are not virtualized, so every row is laid out
        // even though most of them are outside the viewport.
        window.Show();
        ScreenshotHelper.Flush();

        // Anchor on the row that exists on both platforms, so the capture is
        // taken from the same place whichever runner it is on. Climb from
        // there to the group carrying the "Claude Desktop" heading rather
        // than trusting how big the ancestor measures — a 480x151 capture of
        // this same group, missing the heading and the footer note, once
        // passed a bounds-only search on develop without failing anything.
        CaptureGroup(window, "Tint the active window", "Claude Desktop",
            "settings-claude-desktop-group.png");
    }

    // The speech group, so the mode picker CB-165 added is in frame.
    //
    // Its own capture for the same reason the Claude Desktop group has one:
    // the voice rows sit below the fold of a scrolling settings page and would
    // not appear in the whole-window shot at all.
    //
    // Unlike that one, this row has no platform gate — the picker is two names
    // in a combo box and nothing behind it is OS-specific — so the two rids
    // should show the same card. A Windows rid missing the row is the
    // regression worth seeing here, and it is the failure mode the repo's
    // parity rule exists for.
    [AvaloniaFact]
    public void SpeechGroupShowsTheSpeakScopePicker()
    {
        var wasScope = OrbweaverSettings.SpeakScope;
        try
        {
            // Captured on the non-default mode deliberately: it shows the
            // picker holding a saved choice rather than its initial state, so
            // the capture would change if the round trip stopped working.
            OrbweaverSettings.SpeakScope = SpeakScope.Summary;

            var ctor = typeof(SettingsWindow).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                types: Type.EmptyTypes)
                ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

            var window = (Avalonia.Controls.Window)ctor.Invoke(null);

            window.Show();
            ScreenshotHelper.Flush();

            // The group's own heading is "Voice", not "Speech" — the
            // scenario name refers to the feature, the search has to use the
            // string actually on screen. This is the scenario that shipped
            // the worst of the two silent crops on develop: 452x96 instead
            // of the full 480x441, missing the heading and three of the four
            // rows this comment claims to capture, and still comfortably
            // past a 60x200 bounds floor. Only reading the pixels found it,
            // which is why the group is now also asserted to contain its own
            // heading rather than merely measured.
            CaptureGroup(window, "Speaks", "Voice", "settings-speak-scope.png");
        }
        finally
        {
            OrbweaverSettings.SpeakScope = wasScope;
        }
    }

    // CB-222's preview button beside the Speak voice picker, in the two states
    // worth a reviewer's eye: idle (a drawn triangle) and playing (blue, a drawn
    // stop square). The glyphs are drawn geometry precisely because a text
    // glyph renders as a colour emoji on Windows (CB-173), and only a real Skia
    // capture on the Windows rid can show that they did not.
    //
    // The same Voice group as settings-speak-scope.png, which changes with it:
    // the row now carries the button.
    private static void CaptureVoicePreview(bool playing, string fileName)
    {
        VoicePreview.ResetForTests();
        VoicePreview.SpeakForTests = (_, _) =>
        {
            TextToSpeech.Cancel();
            TextToSpeech.Enter(TextToSpeech.SpeakState.Speaking);
        };
        VoicePreview.CancelForTests = () => { };
        VoicePreview.ResolveSavedForTests = () => new TextToSpeech.VoiceOption(
            TextToSpeech.SpeakEngine.System, "Voice", "Voice (system)");

        try
        {
            var ctor = typeof(SettingsWindow).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                types: Type.EmptyTypes)
                ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

            var window = (Avalonia.Controls.Window)ctor.Invoke(null);
            window.Show();
            ScreenshotHelper.Flush();

            if (playing)
            {
                // Through the real click path, so the capture is of what a click
                // produces and not of a look set by hand.
                var label = window.GetLogicalDescendants().OfType<TextBlock>()
                    .First(t => t.Text == "Speak voice");
                var button = label.GetLogicalParent()!.GetLogicalDescendants()
                    .OfType<Button>().Single();
                button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                VoicePreview.Drained().GetAwaiter().GetResult();
                ScreenshotHelper.Flush();
            }

            CaptureGroup(window, "Speak voice", "Voice", fileName);
        }
        finally
        {
            VoicePreview.ResetForTests();
            TextToSpeech.Cancel();
            TextToSpeech.Enter(TextToSpeech.SpeakState.Idle);
        }
    }

    [AvaloniaFact]
    public void VoiceGroupShowsThePreviewButtonIdle() =>
        CaptureVoicePreview(playing: false, "settings-voice-preview-idle.png");

    [AvaloniaFact]
    public void VoiceGroupShowsThePreviewButtonPlaying() =>
        CaptureVoicePreview(playing: true, "settings-voice-preview-playing.png");

    // CB-167's Sounds group, captured with a non-default value saved so the
    // capture shows the picker holding a real choice rather than its initial
    // "Default (…)" state — the same reason SpeechGroupShowsTheSpeakScopePicker
    // seeds a non-default mode before capturing.
    //
    // No platform gate, same reasoning as PeerLinkGroupShowsThePairingControls:
    // the system-sound list differs between the two rids (aiff names on macOS,
    // wav names on Windows), but the row itself — the switch, both pickers,
    // both preview buttons — is identical code on both, so a reviewer comparing
    // the two captures should see the same shape with different sound names in
    // it, not a platform gate.
    [AvaloniaFact]
    public void SoundsGroupShowsThePickers()
    {
        var wasFinished = OrbweaverSettings.TurnFinishedSound;
        try
        {
            OrbweaverSettings.TurnFinishedSound = "off";

            var ctor = typeof(SettingsWindow).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                types: Type.EmptyTypes)
                ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

            var window = (Avalonia.Controls.Window)ctor.Invoke(null);

            window.Show();
            ScreenshotHelper.Flush();

            CaptureGroup(window, "When a turn finishes", "Sounds", "settings-sounds-group.png");
        }
        finally
        {
            OrbweaverSettings.TurnFinishedSound = wasFinished;
        }
    }

    // CB-200's two volume sliders, each captured holding a saved non-default
    // level so a broken round trip would show as a thumb back at the right
    // edge. No platform gate: every engine that speaks on either platform
    // honours a level, so both rids should show the same enabled slider.
    [AvaloniaFact]
    public void SpeechVolumeSliderHoldsASavedLevel() =>
        WithVolumes("system", speech: 0.4, alert: 1.0, () =>
            CaptureGroup(ShownSettings(), "Speech volume", "Voice", "settings-speech-volume.png"));

    // A custom speak command is told the level through
    // CLAUDEBUDDY_SPEECH_VOLUME (CB-200 second review), so the slider is live;
    // the note under it says the command has to read it.
    [AvaloniaFact]
    public void SpeechVolumeIsGreyedAndLabelledForACustomCommand() =>
        WithVolumes("custom", speech: 0.4, alert: 1.0, () =>
            CaptureGroup(ShownSettings(), "Speech volume", "Voice", "settings-speech-volume-custom.png"));

    // And the Alert slider, which no engine choice affects — captured with
    // the custom engine selected on purpose, so this one shows it enabled in
    // exactly the state that greys the Speech one.
    [AvaloniaFact]
    public void AlertVolumeSliderHoldsASavedLevel() =>
        WithVolumes("custom", speech: 1.0, alert: 0.3, () =>
            CaptureGroup(ShownSettings(), "Alert volume", "Sounds", "settings-alert-volume.png"));

    // CB-200 review: Kokoro selected while only an older engine is on disk —
    // just after an upgrade, or a dev build whose engine was never published.
    // The older engine ignores the level, so the row carries a note under a
    // slider that stays enabled. Look for the note, and for the "Speech
    // volume" label still whole beside it.
    [AvaloniaFact]
    public void SpeechVolumeIsNotedWhileAnOlderEngineSpeaks() =>
        WithVolumes("neural", speech: 0.4, alert: 1.0, () =>
            WithOlderEngineOnly(() =>
                CaptureGroup(ShownSettings(), "Speech volume", "Voice", "settings-speech-volume-fallback.png")));

    // CB-200 second review, Warren's defect: the global engine is a system
    // voice, but an orb's persona speaks through a custom command. The note
    // under the live slider is the one for orbs with their own custom-command
    // voice — before this, the row said nothing at all.
    [AvaloniaFact]
    public void SpeechVolumeIsNotedForAnOrbOnACustomCommand() =>
        WithVolumes("system", speech: 0.4, alert: 1.0, () =>
        {
            LocalPersonas.SetForTests(new Dictionary<string, LocalPersona.Persona>
            {
                ["orb-1"] = new("Jennifer", "female_03", null, null, null, Array.Empty<string>())
            });
            TextToSpeech.SetVoiceOptionsForTests(new()
            {
                new(TextToSpeech.SpeakEngine.System, "Samantha", "Samantha (system)"),
                new(TextToSpeech.SpeakEngine.Custom, "female_03", "female_03 (custom)"),
            });
            try
            {
                CaptureGroup(ShownSettings(), "Speech volume", "Voice", "settings-speech-volume-orb-custom.png");
            }
            finally
            {
                LocalPersonas.SetForTests(new Dictionary<string, LocalPersona.Persona>());
                TextToSpeech.InvalidateVoiceCache();
            }
        });

    private static void WithOlderEngineOnly(Action capture)
    {
        var wasEnabled = OrbweaverSettings.NeuralVoiceEnabled;
        var directory = System.IO.Path.Combine(NeuralSpeech.Root, "0.0.1-older");
        var modelExisted = System.IO.File.Exists(NeuralSpeech.ModelPath);
        System.IO.Directory.CreateDirectory(directory);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, NeuralSpeech.EngineExeName), Array.Empty<byte>());
        if (!modelExisted) System.IO.File.WriteAllBytes(NeuralSpeech.ModelPath, Array.Empty<byte>());
        try
        {
            OrbweaverSettings.NeuralVoiceEnabled = true;
            capture();
        }
        finally
        {
            OrbweaverSettings.NeuralVoiceEnabled = wasEnabled;
            System.IO.Directory.Delete(directory, recursive: true);
            if (!modelExisted) System.IO.File.Delete(NeuralSpeech.ModelPath);
        }
    }

    private static Avalonia.Controls.Window ShownSettings()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        var window = (Avalonia.Controls.Window)ctor.Invoke(null);
        window.Show();
        ScreenshotHelper.Flush();
        return window;
    }

    private static void WithVolumes(string engine, double speech, double alert, Action capture)
    {
        var wasEngine = OrbweaverSettings.SpeakEngine;
        var wasSpeech = OrbweaverSettings.SpeechVolume;
        var wasAlert = OrbweaverSettings.AlertVolume;
        var wasCommand = OrbweaverSettings.SpeakCommand;
        try
        {
            OrbweaverSettings.SpeakEngine = engine;
            OrbweaverSettings.SpeechVolume = speech;
            OrbweaverSettings.AlertVolume = alert;
            // A command that exists, so "custom" is the engine that will
            // really speak and the greyed state is the honest one. Never run:
            // nothing here opens the voice picker.
            OrbweaverSettings.SpeakCommand = "my-own-tts";
            capture();
        }
        finally
        {
            OrbweaverSettings.SpeakEngine = wasEngine;
            OrbweaverSettings.SpeechVolume = wasSpeech;
            OrbweaverSettings.AlertVolume = wasAlert;
            OrbweaverSettings.SpeakCommand = wasCommand;
        }
    }

    // The direct link's card, switched on, so the pairing controls are in frame.
    //
    // **Unlike every other scenario in this file, this one has no platform
    // gate — and that is the thing to look at when comparing the two rids.**
    // The relay card above is macOS-only because it lives in tmux; this is a
    // socket, and the whole reason it uses SslStream rather than the gateway's
    // hand-rolled TLS is that it behaves identically on Windows. So the two
    // captures should show the *same* card. A Windows rid that shows a
    // "macOS-only" note here, or nothing at all, is the regression this exists
    // to make visible, and no unit test can show it.
    [AvaloniaFact]
    public void PeerLinkGroupShowsThePairingControls()
    {
        var wasEnabled = OrbweaverSettings.PeerLinkEnabled;
        try
        {
            OrbweaverSettings.PeerLinkEnabled = true;

            var ctor = typeof(SettingsWindow).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                types: Type.EmptyTypes)
                ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

            var window = (Avalonia.Controls.Window)ctor.Invoke(null);

            window.Show();
            ScreenshotHelper.Flush();

            // Used to be found by searching for "No other machines yet"
            // rather than the group's own heading, because a bounds-based
            // search — first ancestor over 60 by 200 — landed on the whole
            // card on macOS and on one row on Windows, making the two rids
            // look like a platform gate when there isn't one. The heading
            // search below is immune to that: "Other machines" sits at the
            // top of the same SettingsSection on both platforms.
            CaptureGroup(window, "Let another machine pair with this one",
                "Other machines", "settings-peer-link-group.png");
        }
        finally
        {
            OrbweaverSettings.PeerLinkEnabled = wasEnabled;
        }
    }

    // CB-300: the same card with the sentence that says live sessions exist
    // under a Claude Code folder the link was never told to read. A new visible
    // state of an existing surface, and the thing to compare across the two
    // rids is that the sentence is whole and wraps rather than being clipped.
    [AvaloniaFact]
    public void PeerLinkGroupNamesAFolderWhoseSessionsAreNotOffered()
    {
        var wasEnabled = OrbweaverSettings.PeerLinkEnabled;
        try
        {
            OrbweaverSettings.PeerLinkEnabled = true;
            PeerSessions.UseRosterGapsForTests(new[]
            {
                new RemoteMirrorServer.RosterGap("~/.claude-loginid", 23, Asked: false, Actionable: true)
            });

            var ctor = typeof(SettingsWindow).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                types: Type.EmptyTypes)
                ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

            var window = (SettingsWindow)ctor.Invoke(null);

            window.Show();
            window.OnStatusTick(null, EventArgs.Empty);
            ScreenshotHelper.Flush();

            CaptureGroup(window, "Let another machine pair with this one",
                "Other machines", "settings-peer-link-roster-gap.png");
        }
        finally
        {
            PeerSessions.UseRosterGapsForTests(null);
            OrbweaverSettings.PeerLinkEnabled = wasEnabled;
        }
    }

    // The Codex group, now with the usage-orb row. Same reason as Grok: the
    // whole-window shot is the window's own height, and this section sits
    // below Claude Code.
    [AvaloniaFact]
    public void CodexGroupShowsTheUsageRow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        var window = (Avalonia.Controls.Window)ctor.Invoke(null);

        window.Show();
        ScreenshotHelper.Flush();

        CaptureGroup(window, "Show Codex sessions", "Codex", "settings-codex-group.png");
    }

    // The Grok Build group. Same reason the Claude Desktop group is captured
    // on its own: the whole-window shot is the window's own height, and this
    // section sits below Codex. A reviewer comparing rids should see the new
    // CLI on both, not infer it from a cropped page.
    [AvaloniaFact]
    public void GrokBuildGroupShowsTheCliRows()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        var window = (Avalonia.Controls.Window)ctor.Invoke(null);

        window.Show();
        ScreenshotHelper.Flush();

        CaptureGroup(window, "Show Grok Build sessions", "Grok Build",
            "settings-grok-build-group.png");
    }

    // CB-96's row, which only exists once Grok's usage orbs are already on —
    // the default capture above never shows it, and adding a UiTests scenario
    // for the toggle does not add its screenshot the way this repo's own rule
    // says it should not.
    [AvaloniaFact]
    public void GrokBuildGroupShowsTheAutoRefreshRowOnceUsageOrbsAreOn()
    {
        OrbweaverSettings.GrokAccountUsageEnabled = true;

        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        var window = (Avalonia.Controls.Window)ctor.Invoke(null);

        window.Show();
        ScreenshotHelper.Flush();

        CaptureGroup(window, "Keep Grok usage fresh automatically", "Grok Build",
            "settings-grok-auto-refresh.png");

        OrbweaverSettings.GrokAccountUsageEnabled = false;
    }

    // The one ancestor-climb every control-scoped scenario above shares,
    // pulled out because that is exactly where the copies used to drift:
    // two of them climbed by measuring the ancestor (first one over 60 by
    // 200), four climbed by content, and nobody noticed the two measuring
    // ancestors were silently wrong until the baseline comparison below
    // caught it. One helper means there is only one place left to drift.
    //
    // Finds the row named by `anchorText`, climbs to the nearest ancestor
    // whose descendants include a TextBlock reading `headingText`, and
    // asserts three separate things before capturing rather than one:
    // that a group was found at all, that it measures large enough to be
    // worth a screenshot, and — the assertion that actually has teeth —
    // that the control being captured still contains the heading it was
    // found by. That last check looks redundant against the search
    // predicate immediately above it, and today it is: but it is a
    // separate, independent statement that survives a future edit to the
    // search (back to a bounds guess, say) in a way a check folded into
    // the predicate would not. `settings-speak-scope.png` was 452x96 on
    // develop — comfortably past 60x200 in both dimensions, showing only
    // one row of five with no heading in frame at all — which is exactly
    // the shape of defect no size check catches and this one does.
    private static void CaptureGroup(
        Avalonia.Controls.Window window, string anchorText, string headingText, string fileName)
    {
        var anchor = window.GetLogicalDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(block => block.Text == anchorText);

        Assert.NotNull(anchor);

        var group = anchor!.GetLogicalAncestors().OfType<Control>()
            .FirstOrDefault(control => control.GetLogicalDescendants()
                .OfType<TextBlock>()
                .Any(block => block.Text == headingText));

        Assert.NotNull(group);
        AssertWorthCapturing(group!);
        AssertContainsHeading(group!, headingText);

        ScreenshotHelper.CaptureControl(group!, fileName);
    }

    // A 1x1 and a "plausible but cropped" are different failures, so this
    // stays alongside AssertContainsHeading rather than being replaced by
    // it — a control could pass the heading check and still have collapsed
    // to a sliver if the heading itself sits in a thin strip above content
    // that failed to lay out.
    private static void AssertWorthCapturing(Control control)
    {
        Assert.True(control.Bounds.Height > 60 && control.Bounds.Width > 200,
            $"capture target measured {control.Bounds.Width}x{control.Bounds.Height}");
    }

    // The structural check: a control can measure comfortably past
    // AssertWorthCapturing's floor and still be the wrong control, missing
    // the very heading a reviewer expects the capture to show — that is
    // what settings-claude-desktop-group.png (480x151, heading and footer
    // both cropped) and settings-speak-scope.png (452x96, four of five
    // rows missing) both did on develop, silently, through a green suite.
    private static void AssertContainsHeading(Control control, string headingText)
    {
        var containsHeading = control.GetLogicalDescendants()
            .OfType<TextBlock>()
            .Any(block => block.Text == headingText);

        Assert.True(containsHeading,
            $"capture target does not contain a heading reading \"{headingText}\"");
    }

    // A real mouse click on a section header, in the *padding* rather than on
    // the heading text.
    //
    // This is the regression guard for a bug CB-166 introduced and caught
    // before landing: the header's ControlTemplate is a bare ContentPresenter
    // (App.axaml's "settings-disclosure" style) which did not consume the
    // Background TemplateBinding, so nothing painted a surface across the
    // button's bounds. An unpainted area is not hit-testable in Avalonia, and
    // neither a TextBlock's glyphs nor a Path's stroke are surfaces of their
    // own, so the whole header was a dead zone no mouse click could reach.
    // GetVisualsAt at the header's own centre returned no ToggleButton at all,
    // while keyboard activation on the focused header worked -- which is what
    // localised it to the template rather than the handler.
    //
    // It lives here, and not beside the rest of the collapse tests in
    // tests/UiTests, because it is the one case that genuinely needs a shown,
    // laid-out window: hit-testing has no meaning without one. Showing this
    // window in tests/UiTests is exactly what could not be done -- that
    // assembly never closes a window, so by the time the collapse tests run
    // some 1260 earlier tests have left theirs alive, and showing the largest
    // window in the app then pumping the dispatcher re-lays-out every one of
    // them. Measured: with the Show the suite never finished; without it, 23
    // seconds. This suite shows windows as a matter of course and is unaffected.
    [AvaloniaFact]
    public void AMouseClickOnTheHeaderPaddingTogglesTheSection()
    {
        var was = OrbweaverSettings.IsSettingsSectionCollapsed("orbs");
        try
        {
            OrbweaverSettings.SetSettingsSectionCollapsed("orbs", false);

            var ctor = typeof(SettingsWindow).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
                ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

            var window = (SettingsWindow)ctor.Invoke(null);

            window.Show();
            ScreenshotHelper.Flush();

            var section = window.Sections["orbs"];
            var header = section.GetLogicalDescendants()
                .OfType<Avalonia.Controls.Primitives.ToggleButton>()
                .Single(tb => tb.Classes.Contains("settings-disclosure"));

            // Far right of the header's own bounds -- past the chevron and the
            // heading text, in padding a real click could easily land in
            // without anyone aiming for a letter.
            var point = header.TranslatePoint(
                new Point(header.Bounds.Width - 2, header.Bounds.Height / 2), window)!.Value;

            Assert.True(section.IsOpen);

            window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            ScreenshotHelper.Flush();
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            ScreenshotHelper.Flush();

            Assert.False(section.IsOpen);
            Assert.True(OrbweaverSettings.IsSettingsSectionCollapsed("orbs"));
        }
        finally
        {
            OrbweaverSettings.SetSettingsSectionCollapsed("orbs", was);
        }
    }
}
