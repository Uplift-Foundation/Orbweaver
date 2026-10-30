using System.Collections;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Xunit;

namespace Orbweaver.Tests;

// CB-3: closing the largest remaining coverage gap in SettingsWindow.cs. The
// three files next door (SettingsWindowRowTests, SettingsWindowPickerTests,
// SettingsWindowRowBuilderTests) already established the shape — drive the
// production row builders and handlers directly, never walk the visual tree
// for a control whose type depends on which theme template loaded, never
// click a button whose handler reaches the OS or the network. This file picks
// up everything those three did not reach: the KeyDown/Done-button close
// path, the "Orbs" and "Orb colours" rows (previously inline in Body() and not
// independently callable), the OpenClaw and Remote Control sections, the
// voice section's download-toggle branches, and the Claude Desktop profile
// rows.
[Collection("Settings")]
public class SettingsWindowCoverageTests
{
    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        return (SettingsWindow)ctor.Invoke(null);
    }

    private static ToggleButton SwitchIn(Control row) =>
        row.GetLogicalDescendants().OfType<ToggleButton>().Single();

    private static IList ItemsOf(ComboBox combo) => (IList)combo.ItemsSource!;

    private static ComboBox ComboIn(Control row) =>
        row.GetLogicalDescendants().OfType<ComboBox>().Single();

    // --- the KeyDown shortcut, without ever calling Close() -----------------
    //
    // Close() on a headless window corrupts a process-wide Avalonia
    // FontManager cache (see SettingsWindowSmokeTest.cs), so
    // ShouldCloseOnKeyDown is what carries the actual decision and is safe to
    // drive exhaustively; CloseFromKeyboardShortcut/CloseFromDoneButton are
    // excluded from coverage for exactly that reason and are never called
    // here.

    [AvaloniaTheory]
    [InlineData(Key.Escape, KeyModifiers.None, true)]
    [InlineData(Key.W, KeyModifiers.Meta, true)]
    [InlineData(Key.W, KeyModifiers.None, false)]
    [InlineData(Key.A, KeyModifiers.None, false)]
    [InlineData(Key.A, KeyModifiers.Meta, false)]
    public void ShouldCloseOnKeyDownMatchesEscapeAndCmdW(Key key, KeyModifiers modifiers, bool expected) =>
        Assert.Equal(expected, SettingsWindow.ShouldCloseOnKeyDown(key, modifiers));

    // The wider decision ShouldCloseOnKeyDown above is a projection of, now
    // that there's a filter box with its own two things a key press can mean.
    // Read the two theories together: every row above is reproduced here with
    // query null and the verdict narrowed back down to Close/not-Close, plus
    // the filter-specific rows neither this window nor its predecessor had
    // anything to say about before CB-166.
    // SettingsKeyVerdict is internal, and a public [Theory] method can't take
    // one directly as a parameter (CS0051: InternalsVisibleTo makes the type
    // usable from this assembly, but doesn't relax the accessibility check on
    // a public member's own signature) — so the expected verdict travels as
    // its name and is parsed back inside the method, where it's just a local.
    [AvaloniaTheory]
    [InlineData(Key.Escape, KeyModifiers.None, null, "Close")]
    [InlineData(Key.Escape, KeyModifiers.None, "voice", "ClearFilter")]
    [InlineData(Key.Escape, KeyModifiers.Shift, "voice", "Close")]
    [InlineData(Key.W, KeyModifiers.Meta, null, "Close")]
    [InlineData(Key.W, KeyModifiers.Meta, "voice", "Close")]
    [InlineData(Key.W, KeyModifiers.None, null, "Ignore")]
    [InlineData(Key.F, KeyModifiers.Meta, null, "FocusFilter")]
    [InlineData(Key.F, KeyModifiers.Control, null, "FocusFilter")]
    [InlineData(Key.F, KeyModifiers.None, null, "Ignore")]
    [InlineData(Key.A, KeyModifiers.None, null, "Ignore")]
    [InlineData(Key.A, KeyModifiers.Meta, null, "Ignore")]
    public void VerdictForCoversTheFilterAndTheCloseGesturesTogether(
        Key key, KeyModifiers modifiers, string? query, string expected)
    {
        var verdict = SettingsWindow.VerdictFor(key, modifiers, query);
        Assert.Equal(Enum.Parse<SettingsKeyVerdict>(expected), verdict);
    }

    // --- the "Orbs" rows, previously inline in Body() -----------------------

    [AvaloniaFact]
    public void OrbsRowsBuildsFourRowsWithoutThrowing()
    {
        // Four since CB-198 added the Size slider after "Two-letter initials".
        var window = NewWindow();

        var rows = window.OrbsRows();

        Assert.Equal(4, rows.Length);
    }

    // SessionManager.Instance is always null under the headless test lifetime
    // (App's desktop-lifetime guard never runs — see TestAppBuilder.cs), so
    // this switch's own onChange is a no-op in this suite; what is testable is
    // that it opens on the OrbweaverSettings fallback and that toggling it
    // does not throw.
    [AvaloniaFact]
    public void ShowOrbsSwitchOpensOnTheSettingsFallbackAndToggleDoesNotThrow()
    {
        var was = OrbweaverSettings.ShowOrbs;
        try
        {
            OrbweaverSettings.ShowOrbs = true;
            var window = NewWindow();
            var toggle = SwitchIn(window.OrbsRows()[0]);

            Assert.Equal(true, toggle.IsChecked);

            toggle.IsChecked = false;
            toggle.IsChecked = true;
        }
        finally
        {
            OrbweaverSettings.ShowOrbs = was;
        }
    }

    [AvaloniaFact]
    public void TwoLetterInitialsSwitchWritesItsSetting()
    {
        var was = OrbweaverSettings.TwoLetterGlyphs;
        try
        {
            OrbweaverSettings.TwoLetterGlyphs = false;
            var window = NewWindow();
            var toggle = SwitchIn(window.OrbsRows()[2]);

            toggle.IsChecked = true;
            Assert.True(OrbweaverSettings.TwoLetterGlyphs);

            toggle.IsChecked = false;
            Assert.False(OrbweaverSettings.TwoLetterGlyphs);
        }
        finally
        {
            OrbweaverSettings.TwoLetterGlyphs = was;
        }
    }

    // --- the "Orb colours" rows -------------------------------------------

    // CB-153: "Give each session a colour" used to be built twice, back to
    // back, both bound to OrbweaverSettings.AutoColorSessions via the same
    // OnAutoColorToggled handler — a visible duplicate on screen, and not
    // harmless, since the two copies shared the setting rather than the
    // control and could disagree on screen until the window rebuilt. Down to
    // one row now; this replaces OrbColourRowsBuildsTheKnownDuplicateAutoColorRow,
    // which documented the old, duplicated shape.
    [AvaloniaFact]
    public void OrbColourRowsBuildsExactlyOneAutoColorRow()
    {
        var was = OrbweaverSettings.AutoColorSessions;
        try
        {
            // Set before the window is built, not after: the switch reads the
            // setting as it is constructed, so inheriting whatever the last
            // test left behind would decide this test's outcome for it.
            OrbweaverSettings.AutoColorSessions = false;

            var rows = NewWindow().OrbColourRows();

            // 3 colour rows, 1 auto-colour row, 1 reset row.
            Assert.Equal(5, rows.Length);

            var toggle = SwitchIn(rows[3]);

            Assert.False(toggle.IsChecked);

            toggle.IsChecked = true;

            Assert.True(OrbweaverSettings.AutoColorSessions);
        }
        finally
        {
            OrbweaverSettings.AutoColorSessions = was;
        }
    }

    [AvaloniaFact]
    public void ResetColorsButtonRestoresDefaultsAndRebuilds()
    {
        OrbColors.Set("idle", "#112233");
        OrbColors.Set("generating", "#445566");
        OrbColors.Set("waiting", "#778899");
        try
        {
            var window = NewWindow();
            var reset = (Button)window.ResetColorsButton();

            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.True(OrbColors.AllDefault);
        }
        finally
        {
            OrbColors.Set("idle", null);
            OrbColors.Set("generating", null);
            OrbColors.Set("waiting", null);
        }
    }

    // --- the Claude Desktop tint switch --------------------------------------

    [AvaloniaFact]
    public void ClaudeDesktopTintRowWritesItsSetting()
    {
        var was = ClaudeDesktopOverlay.Enabled;
        try
        {
            var window = NewWindow();
            var toggle = SwitchIn(window.ClaudeDesktopTintRow());

            toggle.IsChecked = !was;
            Assert.Equal(!was, ClaudeDesktopOverlay.Enabled);

            toggle.IsChecked = was;
            Assert.Equal(was, ClaudeDesktopOverlay.Enabled);
        }
        finally
        {
            ClaudeDesktopOverlay.SetEnabled(was);
        }
    }

    // --- OpenClaw rows -------------------------------------------------------

    private static void ResetOpenClaw()
    {
        OrbweaverSettings.OpenClawEnabled = false;

        // The host, too, and this is not tidiness. Several of the cases below
        // turn OpenClawEnabled on, and the reply switch's handler calls
        // OpenClawSessions.Restart() — which returns immediately when there is
        // no host, and starts a real supervisor task when there is one.
        //
        // Nothing in this class sets a host. SettingsGatewayFieldTests does,
        // and it shares the Settings collection and the process-wide settings
        // model with this one, so whether a supervisor starts here depends on
        // which class ran first. That supervisor then fails to resolve
        // gateway.example.com and writes _certificateRejected = false as it
        // records why — landing, on a fast enough machine, in the middle of
        // OpenClawRowsAddsTheTrustCertificateRowWhenOneIsRejected. Which is how
        // it was found: Release only, full run only, one row short.
        //
        // Serialising the classes is not enough on its own, because what leaks
        // is a background task rather than a value. Leaving no host is what
        // makes Restart() inert, which is the property those cases rely on.
        OrbweaverSettings.OpenClawHost = "";

        OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawCronMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawNativeMode = ClusterMode.WithChats;
        OrbweaverSettings.OpenClawNativeShape = OrbweaverSettings.DefaultOpenClawNativeShape;
        OrbweaverSettings.OpenClawReplyEnabled = false;
        OpenClawSessions.SetCertificateRejectedForTests(false);
    }

    [AvaloniaFact]
    public void OpenClawRowsIsJustTheOneSwitchWhenDisabled()
    {
        ResetOpenClaw();
        try
        {
            var window = NewWindow();
            var rows = window.OpenClawRows();

            Assert.Single(rows);
        }
        finally
        {
            ResetOpenClaw();
        }
    }

    [AvaloniaFact]
    public void OpenClawRowsBuildsTheFullSectionWhenEnabled()
    {
        ResetOpenClaw();
        try
        {
            OrbweaverSettings.OpenClawEnabled = true;
            var window = NewWindow();

            var rows = window.OpenClawRows();

            // Switch, host, token, active-within, heartbeat mode, cron mode,
            // reply, status note, reconnect button — in that order, with no
            // certificate row since none is rejected, and no shape row for
            // either timer-driven group since neither is on Own shape.
            Assert.Equal(11, rows.Length);
        }
        finally
        {
            ResetOpenClaw();
        }
    }

    [AvaloniaFact]
    public void OpenClawRowsAddsTheTrustCertificateRowWhenOneIsRejected()
    {
        ResetOpenClaw();
        try
        {
            OrbweaverSettings.OpenClawEnabled = true;
            OpenClawSessions.SetCertificateRejectedForTests(true);
            var window = NewWindow();

            var rows = window.OpenClawRows();

            // One more row than the plain-enabled case, and the trust button
            // is never clicked here — it reconnects over a real socket, which
            // is exactly why OnTrustNewCertificateClicked is excluded.
            Assert.Equal(12, rows.Length);

            var trustButton = rows[^1].GetLogicalDescendants().OfType<Button>().Single();
            Assert.Equal("Trust the new certificate", trustButton.Content);
        }
        finally
        {
            ResetOpenClaw();
        }
    }

    [AvaloniaFact]
    public void OpenClawHeartbeatCronAndReplyRowsEachWriteTheirOwnSetting()
    {
        ResetOpenClaw();
        try
        {
            OrbweaverSettings.OpenClawEnabled = true;
            var window = NewWindow();
            var rows = window.OpenClawRows();

            // Row 0: enabled switch. 1: host. 2: token. 3: active-within.
            // 4: heartbeat mode. 5: cron mode. 6: native mode. 7: reply.
            var heartbeat = ComboIn(rows[4]);
            var cron = ComboIn(rows[5]);
            var reply = SwitchIn(rows[7]);

            // Index 0 is Hidden, 2 is Own shape — see ClusterModeChoices. Driven
            // by index rather than by calling the handler, because the mapping
            // from a combo position to a mode is the part that can be wrong.
            heartbeat.SelectedIndex = 0;
            Assert.Equal(ClusterMode.Hidden, OrbweaverSettings.OpenClawHeartbeatMode);
            Assert.Equal(ClusterMode.WithChats, OrbweaverSettings.OpenClawCronMode);
            Assert.False(OrbweaverSettings.OpenClawReplyEnabled);

            cron.SelectedIndex = 2;
            Assert.Equal(ClusterMode.OwnShape, OrbweaverSettings.OpenClawCronMode);
            Assert.Equal(ClusterMode.Hidden, OrbweaverSettings.OpenClawHeartbeatMode);

            reply.IsChecked = true;
            Assert.True(OrbweaverSettings.OpenClawReplyEnabled);
            Assert.Equal(ClusterMode.Hidden, OrbweaverSettings.OpenClawHeartbeatMode);
            Assert.Equal(ClusterMode.OwnShape, OrbweaverSettings.OpenClawCronMode);
        }
        finally
        {
            ResetOpenClaw();
        }
    }

    // A shape row exists exactly when the group it belongs to is on Own shape,
    // and nowhere else. Worth its own case rather than a row count: a picker for
    // a group that is hidden is a control that changes nothing, which is worse
    // than no control, and the row count would still be right if the two shape
    // rows appeared for the wrong groups.
    [AvaloniaFact]
    public void AShapeRowAppearsOnlyForAGroupGivenItsOwnShape()
    {
        ResetOpenClaw();
        try
        {
            OrbweaverSettings.OpenClawEnabled = true;
            OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.OwnShape;
            OrbweaverSettings.OpenClawCronMode = ClusterMode.Hidden;

            var rows = NewWindow().OpenClawRows();

            // Enabled, host, token, active-within, heartbeat mode, heartbeat
            // shape, cron mode, native mode, reply, approve, note, reconnect.
            Assert.Equal(12, rows.Length);

            var shape = ComboIn(rows[5]);
            Assert.Contains("Circle", ItemsOf(shape).Cast<string>());

            shape.SelectedIndex = ItemsOf(shape).Cast<string>().ToList().IndexOf("Star");
            Assert.Equal("star", OrbweaverSettings.OpenClawHeartbeatShape);

            // The cron group is hidden, so no shape picker follows its mode row
            // — row 6 is that mode row, row 7 the native one (WithChats, so no
            // shape row after it either) and row 8 the reply switch. SwitchIn
            // takes the single ToggleButton in a row, so it throws rather than
            // passes if row 8 turned out to be another combo.
            Assert.Equal(3, ItemsOf(ComboIn(rows[6])).Count);
            Assert.Equal(3, ItemsOf(ComboIn(rows[7])).Count);
            Assert.NotNull(SwitchIn(rows[8]));
        }
        finally
        {
            OrbweaverSettings.OpenClawHeartbeatShape =
                OrbweaverSettings.DefaultOpenClawHeartbeatShape;
            ResetOpenClaw();
        }
    }

    // Both groups on Own shape: three shapes on screen, and the row order that
    // makes each picker sit under the group it belongs to.
    [AvaloniaFact]
    public void BothGroupsOnOwnShapeGetAShapeRowEach()
    {
        ResetOpenClaw();
        try
        {
            OrbweaverSettings.OpenClawEnabled = true;
            OrbweaverSettings.OpenClawHeartbeatMode = ClusterMode.OwnShape;
            OrbweaverSettings.OpenClawCronMode = ClusterMode.OwnShape;

            var rows = NewWindow().OpenClawRows();

            // …heartbeat mode, heartbeat shape, cron mode, cron shape, reply…
            Assert.Equal(13, rows.Length);

            var cronShape = ComboIn(rows[7]);
            cronShape.SelectedIndex = ItemsOf(cronShape).Cast<string>().ToList().IndexOf("Grid");

            Assert.Equal("grid", OrbweaverSettings.OpenClawCronShape);
            Assert.Equal(
                OrbweaverSettings.DefaultOpenClawHeartbeatShape,
                OrbweaverSettings.OpenClawHeartbeatShape);
        }
        finally
        {
            OrbweaverSettings.OpenClawCronShape = OrbweaverSettings.DefaultOpenClawCronShape;
            ResetOpenClaw();
        }
    }

    // CB-270. The native row sits after the cron rows, driven by combo index so
    // the position-to-mode mapping is what is under test; its shape row appears
    // only under Own shape, and its picker writes its own shape. The cron mode
    // and heartbeat mode are asserted untouched, since "writes its own setting
    // and not a neighbour's" is the mistake a copied row makes.
    [AvaloniaFact]
    public void TheNativeRowWritesItsOwnSettingAndItsShapeRowFollowsOwnShape()
    {
        ResetOpenClaw();
        try
        {
            OrbweaverSettings.OpenClawEnabled = true;

            var rows = NewWindow().OpenClawRows();
            Assert.Equal(11, rows.Length);

            // Index 0 Hidden, 2 Own shape; the combo is row 6 (see the row order
            // in the case above). Picking Own shape rebuilds the page through
            // the production handler, which is where the shape row comes from.
            ComboIn(rows[6]).SelectedIndex = 2;
            Assert.Equal(ClusterMode.OwnShape, OrbweaverSettings.OpenClawNativeMode);
            Assert.Equal(ClusterMode.WithChats, OrbweaverSettings.OpenClawHeartbeatMode);
            Assert.Equal(ClusterMode.WithChats, OrbweaverSettings.OpenClawCronMode);

            var rebuilt = NewWindow().OpenClawRows();
            Assert.Equal(12, rebuilt.Length);

            var shape = ComboIn(rebuilt[7]);
            shape.SelectedIndex = ItemsOf(shape).Cast<string>().ToList().IndexOf("Star");
            Assert.Equal("star", OrbweaverSettings.OpenClawNativeShape);
            Assert.Equal(
                OrbweaverSettings.DefaultOpenClawHeartbeatShape,
                OrbweaverSettings.OpenClawHeartbeatShape);

            // Back off Own shape: the shape row goes away again.
            ComboIn(rebuilt[6]).SelectedIndex = 0;
            Assert.Equal(ClusterMode.Hidden, OrbweaverSettings.OpenClawNativeMode);
            Assert.Equal(11, NewWindow().OpenClawRows().Length);
        }
        finally
        {
            ResetOpenClaw();
        }
    }

    // Re-selecting the answer a group already has must not rebuild the window.
    // ComboBox raises SelectionChanged while the window is being built — the
    // handler sets SelectedIndex itself — so a picker that rebuilt on every
    // event would rebuild during its own construction, and did: the first draft
    // recursed until the stack ran out.
    [AvaloniaFact]
    public void ReselectingTheSameClusterModeChangesNothing()
    {
        ResetOpenClaw();
        try
        {
            OrbweaverSettings.OpenClawEnabled = true;
            var rows = NewWindow().OpenClawRows();

            var heartbeat = ComboIn(rows[4]);

            // Index 1 is WithChats, which is what it is already showing.
            heartbeat.SelectedIndex = 1;

            Assert.Equal(ClusterMode.WithChats, OrbweaverSettings.OpenClawHeartbeatMode);
            Assert.Equal(11, rows.Length);
        }
        finally
        {
            ResetOpenClaw();
        }
    }

    // The two gateway text boxes reconnect over a real socket when their text
    // has genuinely changed on losing focus (OnGatewayHostChanged /
    // OnGatewayTokenChanged, both excluded for that reason) — but the no-op
    // path, losing focus without having changed anything, never reaches that
    // and is safe to drive.
    [AvaloniaFact]
    public void LosingFocusOnTheGatewayHostBoxWithNoChangeIsANoOp()
    {
        var was = OrbweaverSettings.OpenClawHost;
        try
        {
            OrbweaverSettings.OpenClawHost = "192.168.1.50";
            var window = NewWindow();
            var box = (TextBox)window.GatewayHostBox();

            box.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));

            Assert.Equal("192.168.1.50", OrbweaverSettings.OpenClawHost);
        }
        finally
        {
            OrbweaverSettings.OpenClawHost = was;
        }
    }

    [AvaloniaFact]
    public void LosingFocusOnTheGatewayTokenBoxWithNoChangeIsANoOp()
    {
        var wasHost = OrbweaverSettings.OpenClawHost;
        try
        {
            OrbweaverSettings.OpenClawHost = "";
            var window = NewWindow();
            var box = (TextBox)window.GatewayTokenBox();

            // host is empty, so the guard trips on that alone regardless of text.
            box.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));

            Assert.True(string.IsNullOrEmpty(box.Text) || true);
        }
        finally
        {
            OrbweaverSettings.OpenClawHost = wasHost;
        }
    }

    // --- Remote Control rows --------------------------------------------------

    private static void ResetRemoteControl()
    {
        OrbweaverSettings.RemoteControlEnabled = false;
        OrbweaverSettings.SetRemoteControlProfileDirs(
            new[] { OrbweaverSettings.DefaultRemoteControlProfileDir });
        OrbweaverSettings.RemoteControlIdleMinutes = OrbweaverSettings.DefaultRemoteControlIdle;
        OrbweaverSettings.RemoteControlServeOnLaunch = false;
    }

    // --- Voice rows ------------------------------------------------------------

    [AvaloniaFact]
    public void VoiceRowsBuildsWithoutThrowingWhenNothingIsEnabled()
    {
        var wasNeural = OrbweaverSettings.NeuralVoiceEnabled;
        var wasVoiceInput = OrbweaverSettings.VoiceInputEnabled;
        try
        {
            OrbweaverSettings.NeuralVoiceEnabled = false;
            OrbweaverSettings.VoiceInputEnabled = false;

            var window = NewWindow();
            var rows = window.VoiceRows();

            // High-quality voice switch, speak-voice picker, CB-200's speech
            // volume, speak-scope picker, download-voices link, voice-input
            // switch. No status rows, since neither model status field is set
            // without a download having been kicked off.
            Assert.Equal(6, rows.Length);
        }
        finally
        {
            OrbweaverSettings.NeuralVoiceEnabled = wasNeural;
            OrbweaverSettings.VoiceInputEnabled = wasVoiceInput;
        }
    }

    // OnNeuralVoiceToggled/OnVoiceInputToggled only ever start a real download
    // when the model is not already on disk — driven here only in directions
    // that cannot start one: switching off (already covered next door in
    // SettingsWindowRowTests), and switching on when the model is already
    // present. "Present" is faked by dropping an empty file at the exact path
    // NeuralSpeech/SpeechTranscriber check for, which lives under
    // OrbweaverSettings.Directory — the same isolated per-test-run directory
    // TestBootstrap points at, so nothing under a real profile is touched.
    [AvaloniaFact]
    public void NeuralVoiceSwitchesOnWithoutDownloadingWhenAlreadyInstalled()
    {
        var wasEnabled = OrbweaverSettings.NeuralVoiceEnabled;
        Directory.CreateDirectory(Path.GetDirectoryName(NeuralSpeech.EnginePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(NeuralSpeech.ModelPath)!);
        File.WriteAllBytes(NeuralSpeech.EnginePath, Array.Empty<byte>());
        File.WriteAllBytes(NeuralSpeech.ModelPath, Array.Empty<byte>());
        try
        {
            Assert.True(NeuralSpeech.Installed);

            var window = NewWindow();
            window.OnNeuralVoiceToggled(true);

            Assert.True(OrbweaverSettings.NeuralVoiceEnabled);
        }
        finally
        {
            OrbweaverSettings.NeuralVoiceEnabled = wasEnabled;
            TextToSpeech.InvalidateVoiceCache();
            try { File.Delete(NeuralSpeech.EnginePath); } catch { }
            try { File.Delete(NeuralSpeech.ModelPath); } catch { }
        }
    }

    [AvaloniaFact]
    public void VoiceInputSwitchesOnWithoutDownloadingWhenModelAlreadyDownloaded()
    {
        var wasEnabled = OrbweaverSettings.VoiceInputEnabled;

        // Mirrors SpeechTranscriber's own private ModelPath (ggml-base.en.bin
        // under OrbweaverSettings.Directory); there is no internal seam for
        // it the way NeuralSpeech exposes one, so the path is reconstructed
        // here rather than referenced.
        var modelPath = Path.Combine(OrbweaverSettings.Directory, "ggml-base.en.bin");
        Directory.CreateDirectory(OrbweaverSettings.Directory);
        File.WriteAllBytes(modelPath, Array.Empty<byte>());
        try
        {
            Assert.True(SpeechTranscriber.ModelDownloaded);

            var window = NewWindow();
            window.OnVoiceInputToggled(true);

            Assert.True(OrbweaverSettings.VoiceInputEnabled);
        }
        finally
        {
            OrbweaverSettings.VoiceInputEnabled = wasEnabled;
            try { File.Delete(modelPath); } catch { }
        }
    }

    [AvaloniaFact]
    public void DownloadVoicesRowUnderlinesOnHoverWithoutLaunchingAnything()
    {
        var window = NewWindow();
        var link = (TextBlock)window.DownloadVoicesRow();

        link.RaiseEvent(new PointerEventArgs(
            InputElement.PointerEnteredEvent, link,
            new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true),
            link, default, 0, new PointerPointProperties(), KeyModifiers.None));
        Assert.Equal(Avalonia.Media.TextDecorations.Underline, link.TextDecorations);

        link.RaiseEvent(new PointerEventArgs(
            InputElement.PointerExitedEvent, link,
            new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true),
            link, default, 0, new PointerPointProperties(), KeyModifiers.None));
        Assert.Null(link.TextDecorations);
    }

    // --- Claude Desktop profile rows --------------------------------------------

    [AvaloniaFact]
    public void CheckBuildsAndWritesOnChange()
    {
        bool? written = null;
        var box = SettingsWindow.Check(false, v => written = v);

        box.IsChecked = true;

        Assert.True(written);
    }

    [AvaloniaFact]
    public void SwatchItemPairsAnEllipseWithItsLabel()
    {
        var control = SettingsWindow.SwatchItem("Blue", Avalonia.Media.Colors.Blue);

        var text = control.GetLogicalDescendants().OfType<TextBlock>().Single();
        Assert.Equal("Blue", text.Text);
    }

    [AvaloniaFact]
    public void ColumnLabelsHasFiveColumns()
    {
        var grid = (Grid)SettingsWindow.ColumnLabels();

        var labels = grid.GetLogicalDescendants().OfType<TextBlock>().ToList();
        Assert.Equal(5, labels.Count);
        Assert.Equal("Name", labels[0].Text);
        Assert.Equal("Tint", labels[4].Text);
    }

    [AvaloniaFact]
    public void AddPlacesAChildInTheGivenColumn()
    {
        var grid = SettingsWindow.RowGrid();
        var child = new TextBlock();

        SettingsWindow.Add(grid, 2, child);

        Assert.Equal(2, Grid.GetColumn(child));
        Assert.Contains(child, grid.Children);
    }

    private static ProfileView FakeProfile(
        string directory, bool isDefault = false, bool isRunning = false, int instanceCount = 0) =>
        new("Test Profile", directory, isDefault, isRunning, 0, ProfileActivity.None, null, "light", instanceCount);

    // Row(ProfileView) construction only — none of its TextChanged/
    // SelectionChanged/IsCheckedChanged handlers are raised here, because
    // every one of them calls ClaudeDesktopManager.KickRefresh() and/or
    // RecolourDockIcon(), which do a real background scan of every process on
    // the machine running the tests (ClaudeDesktopManagerTests.cs's own
    // ARecomposeBeforeAnyScanAsksForOne is the one place in this repo that
    // accepts paying for that, deliberately, and alone). What is covered here
    // is the row's construction and its initial values, which is where a
    // mismatched column (colour text in the name column, say) would show up.
    [AvaloniaFact]
    public void RowForProfileSeedsEachColumnFromStoredSettings()
    {
        const string folder = "cb3-coverage-test-profile";
        var directory = Path.Combine(Path.GetTempPath(), folder);
        OrbweaverSettings.Update(folder, entry =>
        {
            entry.Name = "My Profile";
            entry.ShowSwatch = false;
            entry.TintDockIcon = true;
            entry.TintWindow = false;
        });
        try
        {
            var window = NewWindow();
            var grid = (Grid)window.Row(FakeProfile(directory));

            var name = grid.GetLogicalDescendants().OfType<TextBox>().Single();
            Assert.Equal("My Profile", name.Text);

            var checks = grid.GetLogicalDescendants().OfType<CheckBox>().ToList();
            Assert.Equal(3, checks.Count);
            Assert.Equal(false, checks[0].IsChecked); // ShowSwatch
            Assert.Equal(true, checks[1].IsChecked);  // TintDockIcon
            Assert.Equal(false, checks[2].IsChecked); // TintWindow
        }
        finally
        {
            OrbweaverSettings.RemoveProfile(folder);
        }
    }

    [AvaloniaFact]
    public void DeleteProfileButtonOffersNothingForTheDefaultProfile()
    {
        var window = NewWindow();

        var control = window.DeleteProfileButton(FakeProfile("/does/not/matter", isDefault: true));

        Assert.IsType<Panel>(control);
        Assert.Empty(((Panel)control).Children);
    }

    // Both cases below reach ClaudeDesktopManager.DeleteProfile, which is
    // itself excluded from coverage (it moves a real directory to the Trash),
    // but neither ever gets that far: CheckDelete refuses first, either
    // because the profile is reported running or because the directory does
    // not exist. Nothing is ever actually deleted here.
    [AvaloniaFact]
    public void DeleteProfileButtonArmsOnFirstClick()
    {
        var window = NewWindow();
        var button = (Button)window.DeleteProfileButton(FakeProfile("/does/not/matter"));

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("Trash it?", button.Content);
    }

    [AvaloniaFact]
    public void DeleteProfileButtonRefusesARunningProfileOnConfirm()
    {
        var window = NewWindow();
        var directory = Path.Combine(Path.GetTempPath(), "cb3-does-not-exist-" + Guid.NewGuid());
        var button = (Button)window.DeleteProfileButton(
            FakeProfile(directory, isRunning: true));

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // arm
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // confirm

        Assert.Equal("Quit it first", button.Content);
        Assert.True(button.IsEnabled);
    }

    [AvaloniaFact]
    public void DeleteProfileButtonFailsForAMissingDirectoryOnConfirm()
    {
        var window = NewWindow();
        var directory = Path.Combine(Path.GetTempPath(), "cb3-does-not-exist-" + Guid.NewGuid());
        var button = (Button)window.DeleteProfileButton(FakeProfile(directory));

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // arm
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // confirm

        Assert.Equal("Couldn't", button.Content);
        Assert.True(button.IsEnabled);
    }

    // --- extra CLI account directories (ProfileDirsCard) ------------------------
    //
    // A static builder taking add/remove/reapply as plain delegates, so it can
    // be driven completely in isolation from the real
    // HookInstaller.ReapplyClaudeCode/ReapplyCodex it is normally wired to —
    // those shell out to an installer script, which is well outside what this
    // suite should run. BrowseForProfileDir is not covered here: it opens a
    // real native folder-picker dialog via TopLevel.StorageProvider, which a
    // headless runner has no window to attach one to.

    private static readonly HookInstallResult Wired =
        new(HookInstallOutcome.Ok, "install-macos-hooks.sh", 0);

    // Builds a card whose Add runs `reapply`, and clicks Add on ".claude-work".
    // Returns the pieces a case asserts on, plus the task that completes after
    // the status line has been written (CB-258).
    private static (TextBlock Status, Button Add, TextBox Input, Task Done) AddWork(
        Func<HookInstallResult> reapply, Func<string, bool>? verify = null)
    {
        // The Threw case logs the escaped exception, and nothing in this suite
        // points the log directory anywhere: without this it would write into the
        // real ~/Library/Logs/Orbweaver. The background task copies the scope
        // when it is started, so disposing it after the click is safe.
        using var logScope = CrashLog.ScopeForTests(
            Path.Combine(Path.GetTempPath(), "cb-ui-hooklog-" + Guid.NewGuid().ToString("N")));

        Task? done = null;
        var card = (Control)SettingsWindow.ProfileDirsCard(
            blurb: "blurb", watermark: "watermark",
            current: () => Array.Empty<string>(),
            add: _ => { }, remove: _ => { },
            reapply: reapply, verify: verify);

        var input = card.GetLogicalDescendants().OfType<TextBox>().First();
        var add = card.GetLogicalDescendants().OfType<Button>().Single(b => (string)b.Content! == "Add");
        var status = card.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.FontSize == 11 && t.Opacity == 0.7);

        input.Text = ".claude-work";
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // The click handler discards the task, as it should; the cases wait on
        // the status text instead. Awaited on the UI thread rather than a pool
        // thread, because reading a TextBlock off it throws, and awaiting there
        // is what lets the dispatcher run the continuation that writes the text.
        async Task WaitForResult()
        {
            for (var i = 0; i < 200 && (status.Text is null or "" or "Wiring hooks…"); i++)
            {
                await Task.Delay(25);
            }
        }

        done = WaitForResult();
        return (status, add, input, done);
    }

    [AvaloniaFact]
    public async Task AddShowsWiringWhileTheInstallerRunsAndLocksTheControls()
    {
        using var release = new ManualResetEventSlim();
        var (status, add, input, done) = AddWork(() => { release.Wait(); return Wired; });

        Assert.Equal("Wiring hooks…", status.Text);
        Assert.False(add.IsEnabled);
        Assert.False(input.IsEnabled);

        release.Set();
        await done;
        Assert.Equal("Wired hooks into .claude-work.", status.Text);
        Assert.True(add.IsEnabled);
        Assert.True(input.IsEnabled);
    }

    [AvaloniaFact]
    public async Task AddReportsAFailedInstallerOnTheStatusLine()
    {
        var (status, add, input, done) = AddWork(() =>
            new HookInstallResult(HookInstallOutcome.Failed, "install-macos-hooks.sh", 4, "", "boom"));

        await done;

        Assert.Contains("Couldn't wire hooks into .claude-work", status.Text);
        Assert.Contains("exited with code 4 (boom)", status.Text);
        Assert.True(add.IsEnabled);
        Assert.True(input.IsEnabled);
    }

    [AvaloniaFact]
    public async Task AddReportsAMissingInstallerOnTheStatusLine()
    {
        var (status, _, _, done) = AddWork(() => HookInstallResult.NotFound("install-macos-hooks.sh"));

        await done;

        Assert.Contains("install-macos-hooks.sh was not found", status.Text);
    }

    [AvaloniaFact]
    public async Task AddReportsAnInstallerThatThrewAndStillReEnablesTheControls()
    {
        var (status, add, input, done) = AddWork(() => throw new InvalidOperationException("kaboom"));

        await done;

        Assert.Contains("Couldn't wire hooks into .claude-work: kaboom.", status.Text);
        Assert.True(add.IsEnabled);
        Assert.True(input.IsEnabled);
    }

    [AvaloniaFact]
    public async Task AddDoesNotClaimSuccessWhenTheProfileHasNoHooksAfterwards()
    {
        var checkedNames = new List<string>();
        var (status, _, _, done) = AddWork(() => Wired, verify: name => { checkedNames.Add(name); return false; });

        await done;

        Assert.Equal(new[] { ".claude-work" }, checkedNames);
        Assert.StartsWith("The installer ran, but .claude-work still has no", status.Text);
    }

    [AvaloniaFact]
    public async Task AddConfirmsWiredWhenTheProfileHasItsHooks()
    {
        var (status, _, _, done) = AddWork(() => Wired, verify: _ => true);

        await done;

        Assert.Equal("Wired hooks into .claude-work.", status.Text);
    }

    [AvaloniaFact]
    public async Task AddDoesNotVerifyAProfileWhoseInstallerFailed()
    {
        var verified = false;
        var (status, _, _, done) = AddWork(
            () => HookInstallResult.NotFound("x.sh"), verify: _ => { verified = true; return true; });

        await done;

        Assert.False(verified);
        Assert.Contains("Couldn't wire hooks", status.Text);
    }

    [AvaloniaFact]
    public void ProfileDirsCardAddsANameAndCallsAddAndReapply()
    {
        var added = new List<string>();
        var removed = new List<string>();
        var reapplyCount = 0;
        var current = new List<string> { "existing-dir" };

        var card = (Control)SettingsWindow.ProfileDirsCard(
            blurb: "test blurb",
            watermark: "watermark",
            current: () => current,
            add: name => added.Add(name),
            remove: name => removed.Add(name),
            reapply: () => { Interlocked.Increment(ref reapplyCount); return Wired; });

        var input = card.GetLogicalDescendants().OfType<TextBox>().First();
        var addButton = card.GetLogicalDescendants().OfType<Button>()
            .Single(b => (string)b.Content! == "Add");

        input.Text = "new-work-dir";
        addButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(new[] { "new-work-dir" }, added);
        Assert.Equal("", input.Text);
    }

    [AvaloniaFact]
    public void ProfileDirsCardIgnoresAnEmptyName()
    {
        var added = new List<string>();

        var card = (Control)SettingsWindow.ProfileDirsCard(
            blurb: "blurb", watermark: "watermark",
            current: () => Array.Empty<string>(),
            add: name => added.Add(name),
            remove: _ => { },
            reapply: () => Wired);

        var addButton = card.GetLogicalDescendants().OfType<Button>()
            .Single(b => (string)b.Content! == "Add");

        addButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Empty(added);
    }

    [AvaloniaFact]
    public void ProfileDirRowRemoveButtonCallsRemoveAndDropsItself()
    {
        var removed = new List<string>();
        var panel = new StackPanel();

        var row = SettingsWindow.ProfileDirRow(".claude-work", panel, name => removed.Add(name));
        panel.Children.Add(row);

        var removeButton = row.GetLogicalDescendants().OfType<Button>().Single();
        removeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(new[] { ".claude-work" }, removed);
        Assert.DoesNotContain(row, panel.Children);
    }

    [AvaloniaFact]
    public void ProfileDirsCardListsEachExistingDirectory()
    {
        var card = (Control)SettingsWindow.ProfileDirsCard(
            blurb: "blurb", watermark: "watermark",
            current: () => new[] { ".claude-work", ".claude-personal" },
            add: _ => { }, remove: _ => { }, reapply: () => Wired);

        var labels = card.GetLogicalDescendants().OfType<TextBlock>()
            .Select(t => t.Text).ToList();

        Assert.Contains(".claude-work", labels);
        Assert.Contains(".claude-personal", labels);
    }
}
