using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-207: the New chat dialog must never change height once it is on screen.
//
// On a real Mac, a SizeToContent window whose height changed while it was not
// presenting came back drawing its old-sized surface stretched over the new
// frame — scaled up, Start cut off, the layout no longer under the pixels.
// The fix reserves every slot whose occupant changes after show (see
// NewChatWindow.ReservedSlot), so the content — which is all SizeToContent
// follows — is the same height in every state.
//
// What this suite can and cannot prove. It pins the mechanism: the content's
// measured height is identical across every CLI switch and every status
// message, and each ghost measures exactly like the section it stands in for.
// It cannot prove the presentation fault itself is gone — headless Avalonia
// has no window server, never presents a frame, and cannot stretch a stale
// surface. That half was checked on a real Mac by reading the frame back out
// of CGWindowList and diffing window captures before and after the fix.
//
// Measured rather than shown: showing a NewChatWindow under the headless
// lifetime is what hung the whole suite once (HotkeyActionsTests' header), and
// the content's DesiredSize is exactly the input SizeToContent sizes from.
[Collection("Settings")]
public class NewChatWindowHeightTests : IDisposable
{
    public void Dispose()
    {
        NewChatAvailability.CurrentForTests = null;
        NewChatWindow.OpenClawAvailabilityForTests = null;
        NewChatWindow.KnownAgentsForTests = null;
        NewChatWindow.CurrentStatusesForTests = null;
        NewChatWindow.AccountDirectoryExistsForTests = null;
        NewChatWindow.ReopenRowsForTests = null;
        NewChatWindow.NowForTests = null;
        NewChatWindow.LastConnectFailureForTests = null;
        NewChatWindow.PeerLauncherForTests = null;
        NewChatWindow.PeerLinkOnForTests = null;
        NewChatWindow.TrustWaitForTests = null;
    }

    private static void FreshSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-newchat-height-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    private static NewChatOption Enabled(NewChatCli cli) => new(cli, Enabled: true, Reason: null, Warning: null);

    private static NewChatWindow NewWindow()
    {
        var ctor = typeof(NewChatWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: new[] { typeof(NewChatCli?), typeof(string), typeof(string) })
            ?? throw new MissingMethodException("NewChatWindow", ".ctor(NewChatCli?, string, string)");

        return (NewChatWindow)ctor.Invoke(new object?[] { null, null, null });
    }

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    // The window's own fixed width, so text wraps where it does on screen.
    private static double ContentHeight(NewChatWindow window)
    {
        Flush();
        var content = (Control)window.Content!;
        // The whole tree, not only the root: a window that is never shown is
        // not attached to a layout root, so invalidating a control that has
        // just become visible does not reach its parents and a cached
        // zero-size measure would stand. CB-329's Open existing pane, built
        // hidden and measured before it was shown, was exactly that.
        content.InvalidateMeasure();
        foreach (var child in content.GetLogicalDescendants().OfType<Layoutable>()) child.InvalidateMeasure();
        content.Measure(new Size(420, double.PositiveInfinity));
        return content.DesiredSize.Height;
    }

    private static void Select(NewChatWindow window, object tag) =>
        window.CliList.Children.OfType<RadioButton>().Single(r => Equals(r.Tag, tag)).IsChecked = true;

    // Everything on: all three local CLIs, an extra account, and a usable
    // OpenClaw — the configuration with the most slots that can change.
    private static NewChatWindow EverythingAvailable()
    {
        FreshSettings();
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-board");
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex), Enabled(NewChatCli.Grok)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => new[] { ("id-1", "Alexis") };
        // Every account directory exists, so no CB-203 warning line is
        // reserved and the measurements below are the same on a machine that
        // happens to have ~/.claude-board and a CI runner that does not.
        NewChatWindow.AccountDirectoryExistsForTests = _ => true;
        return NewWindow();
    }

    // CB-203: the same shape, but every extra account's directory is missing,
    // so the warning line under the picker is in play and its lines are
    // reserved.
    private static NewChatWindow EverythingAvailableWithMissingHomes()
    {
        FreshSettings();
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-board");
        OrbweaverSettings.AddCodexHome(".codex-work");
        OrbweaverSettings.AddGrokHome(".grok-work");
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex), Enabled(NewChatCli.Grok)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => new[] { ("id-1", "Alexis") };
        NewChatWindow.AccountDirectoryExistsForTests = _ => false;
        return NewWindow();
    }

    private static void SelectAccount(NewChatWindow window, int index)
    {
        window.AccountCombo.SelectedIndex = index;
        Flush();
    }

    [AvaloniaFact]
    public void TheHeightHoldsAsTheMissingHomeWarningComesAndGoes()
    {
        var window = EverythingAvailableWithMissingHomes();
        var opened = ContentHeight(window);
        Assert.True(window.AccountWarningGhost.IsVisible);

        foreach (var cli in new object[] { NewChatCli.ClaudeCode, NewChatCli.Codex, NewChatCli.Grok })
        {
            Select(window, cli);
            SelectAccount(window, 1);
            Assert.True(window.AccountWarning.IsVisible);
            Assert.Equal(opened, ContentHeight(window));

            SelectAccount(window, 0);
            Assert.False(window.AccountWarning.IsVisible);
            Assert.Equal(opened, ContentHeight(window));
        }

        Select(window, NewChatWindow.OpenClawTag);
        Assert.Equal(opened, ContentHeight(window));
    }

    [AvaloniaFact]
    public void NoWarningLineIsReservedWhenEveryAccountDirectoryExists()
    {
        var window = EverythingAvailable();
        Flush();

        Assert.False(window.AccountWarningGhost.IsVisible);
        Assert.False(window.AccountWarning.IsVisible);
    }

    // The warning shown, with its lines reserved, measures exactly like
    // the ghost that holds its place.
    [AvaloniaFact]
    public void TheAccountGhostMeasuresLikeTheSectionShowingAWarning()
    {
        var window = EverythingAvailableWithMissingHomes();
        Select(window, NewChatCli.Codex);
        SelectAccount(window, 1);

        ContentHeight(window);
        Assert.Equal(window.AccountSection.DesiredSize.Height, window.AccountGhost.DesiredSize.Height);
    }

    // Each warning fits the lines reserved for it at the dialog's content
    // width, in the headless font — the captures show the real one.
    [AvaloniaTheory]
    [InlineData("ClaudeCode")]
    [InlineData("Codex")]
    [InlineData("Grok")]
    public void EveryMissingHomeWarningFitsTheReservedLines(string cliName)
    {
        var window = EverythingAvailableWithMissingHomes();
        Flush();

        var unbounded = new TextBlock
        {
            Text = NewChatAccountWarning.TextFor(Enum.Parse<NewChatCli>(cliName)),
            TextWrapping = window.AccountWarning.TextWrapping,
            LineHeight = window.AccountWarning.LineHeight,
            FontSize = window.AccountWarning.FontSize,
            FontFamily = window.AccountWarning.FontFamily
        };
        unbounded.Measure(new Size(420 - 40, double.PositiveInfinity));

        Assert.True(unbounded.DesiredSize.Height <= window.AccountWarning.Height,
            $"{unbounded.DesiredSize.Height} > {window.AccountWarning.Height}");
    }

    [AvaloniaFact]
    public void TheHeightHoldsAcrossEveryCliSwitch()
    {
        var window = EverythingAvailable();
        var opened = ContentHeight(window);

        // A real measurement, not two zeros agreeing: three CLI rows, the
        // account and folder sections and Start cannot fit in less.
        Assert.True(opened > 200, $"content measured {opened}");

        foreach (var tag in new object[]
                 {
                     NewChatCli.Codex, NewChatCli.ClaudeCode, NewChatCli.Grok, NewChatWindow.OpenClawTag,
                     NewChatCli.ClaudeCode, NewChatWindow.OpenClawTag, NewChatCli.Codex
                 })
        {
            Select(window, tag);
            Assert.Equal(opened, ContentHeight(window));
        }
    }

    [AvaloniaFact]
    public void TheAccountPickerStillHidesForCodexWhileItsSpaceIsKept()
    {
        var window = EverythingAvailable();

        Select(window, NewChatCli.Codex);
        Flush();

        // CB-201's rule is untouched — the picker itself is gone...
        Assert.False(window.AccountSection.IsVisible);
        // ...and only the ghost holds its place.
        Assert.True(window.AccountGhost.IsVisible);
    }

    [AvaloniaFact]
    public void TheHeightHoldsWhateverTheStatusLineSays()
    {
        var window = EverythingAvailable();
        var opened = ContentHeight(window);

        foreach (var text in new[]
                 {
                     "Starting…",
                     "Claude Code started in /Users/someone/Source/a/rather/deep/folder/that/wraps/onto/a/second/line/and/then/a/third/one/and/keeps/going/long/past/that.",
                     "Terminal opened; no orb yet — the CLI's hook may not be installed / trusted.",
                     ""
                 })
        {
            window.StatusLine.Text = text;
            Assert.Equal(opened, ContentHeight(window));
        }
    }

    // The reserve is sized to the longest fixed message (CB-207 QA): each
    // one, laid out unbounded at the dialog's content width, has to fit in
    // the lines reserved, or it would be trimmed on screen. Measured in the
    // headless font, not the macOS or Windows system font. The captures in
    // tests/UiScreenshots are what show the real one.
    [AvaloniaTheory]
    [InlineData("Terminal opened; no orb yet — the CLI's hook may not be installed / trusted.")]
    [InlineData("Conversation created; no orb yet — check your gateway connection.")]
    [InlineData("Conversation started with Alexis. Waiting for its orb…")]
    [InlineData("Claude Code is running — its orb should be on screen.")]
    [InlineData("Codex not found on PATH or in its usual install locations.")]
    [InlineData("Starting a new chat isn't supported on this platform yet.")]
    public void EveryFixedStatusMessageFitsTheReservedLines(string message)
    {
        var window = EverythingAvailable();
        Flush();

        var unbounded = new TextBlock
        {
            Text = message,
            TextWrapping = window.StatusLine.TextWrapping,
            LineHeight = window.StatusLine.LineHeight,
            FontSize = window.StatusLine.FontSize,
            FontFamily = window.StatusLine.FontFamily
        };
        unbounded.Measure(new Size(420 - 40, double.PositiveInfinity));

        Assert.True(unbounded.DesiredSize.Height <= window.StatusLine.MinHeight,
            $"{unbounded.DesiredSize.Height} > {window.StatusLine.MinHeight}");
    }

    [AvaloniaFact]
    public void TheStatusLineCarriesItsFullTextAsATooltip()
    {
        var window = EverythingAvailable();

        window.StatusLine.Text = "Couldn't open a terminal for Claude Code.";

        Assert.Equal("Couldn't open a terminal for Claude Code.", ToolTip.GetTip(window.StatusLine));
    }

    // CB-361: Resume is a mode of the local CLIs, so flipping it, filling its
    // list, and moving between CLIs and OpenClaw must not move the window.
    [AvaloniaFact]
    public void TheHeightHoldsAcrossResumeModeAndItsList()
    {
        var window = EverythingAvailable();
        PastSessionIndex.LoadForTests = (cli, _, _, _) => Enumerable.Range(0, 9).Select(i =>
            new PastSession(cli, "id-" + i, "/tmp", "Session " + i, DateTimeOffset.UtcNow, 3, "/t", false)).ToList();
        NewChatWindow.ResumeLoadInlineForTests = true;
        try
        {
            var opened = ContentHeight(window);
            Assert.True(opened > 200, $"content measured {opened}");

            window.LocalResumeRadio.IsChecked = true;
            Assert.Equal(opened, ContentHeight(window));

            window.ResumeSearch.Text = "no such session";
            Assert.Equal(opened, ContentHeight(window));
            window.ResumeSearch.Text = null;

            foreach (var tag in new object[] { NewChatCli.Codex, NewChatWindow.OpenClawTag, NewChatCli.Grok, NewChatWindow.OpenClawTag })
            {
                Select(window, tag);
                Assert.Equal(opened, ContentHeight(window));
            }

            window.LocalNewRadio.IsChecked = true;
            Assert.Equal(opened, ContentHeight(window));
        }
        finally
        {
            PastSessionIndex.LoadForTests = null;
            NewChatWindow.ResumeLoadInlineForTests = false;
        }
    }

    [AvaloniaFact]
    public void TheResumeGhostMeasuresLikeThePaneItStandsIn()
    {
        var window = EverythingAvailable();
        PastSessionIndex.LoadForTests = (_, _, _, _) => Array.Empty<PastSession>();
        NewChatWindow.ResumeLoadInlineForTests = true;
        try
        {
            window.LocalResumeRadio.IsChecked = true;
            ContentHeight(window);
            Assert.Equal(window.ResumePane.DesiredSize.Height, window.ResumeGhost.DesiredSize.Height);
        }
        finally
        {
            PastSessionIndex.LoadForTests = null;
            NewChatWindow.ResumeLoadInlineForTests = false;
        }
    }

    [AvaloniaFact]
    public void EachGhostMeasuresExactlyLikeTheSectionItStandsIn()
    {
        var window = EverythingAvailable();

        // Claude Code, at open: the account and folder sections are real, so
        // each can be set against its ghost directly.
        ContentHeight(window);
        Assert.Equal(window.AccountSection.DesiredSize.Height, window.AccountGhost.DesiredSize.Height);
        Assert.Equal(window.FolderSection.DesiredSize.Height, window.FolderGhost.DesiredSize.Height);

        Select(window, NewChatWindow.OpenClawTag);
        ContentHeight(window);
        Assert.Equal(window.AgentSection.DesiredSize.Height, window.AgentGhost.DesiredSize.Height);
    }

    [AvaloniaFact]
    public void AGhostCanBeNeitherClickedNorFocusedNorAnnounced()
    {
        var window = EverythingAvailable();
        Flush();

        foreach (var ghost in new[] { window.AccountGhost, window.FolderGhost, window.AgentGhost, window.ExistingGhost })
        {
            Assert.Equal(0, ghost.Opacity);
            Assert.False(ghost.IsHitTestVisible);
            Assert.False(ghost.IsEnabled);
            Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(ghost));
            // Tab can land on nothing inside it: every control that could take
            // focus is both disabled and unfocusable in its own right.
            var focusables = ghost.GetLogicalDescendants().OfType<TemplatedControl>().ToList();
            Assert.NotEmpty(focusables);
            Assert.All(focusables, c =>
            {
                Assert.False(c.Focusable);
                Assert.False(c.IsEnabled);
            });
        }
    }

    [AvaloniaFact]
    public void NoSpaceIsReservedForAPickerThatCanNeverAppear()
    {
        // No extra accounts: CB-201's "empty list means no picker", and the
        // dialog must read exactly as it did before CB-201 — no gap, no ghost,
        // no StackPanel spacing collected by an empty slot.
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;

        var window = NewWindow();
        Flush();

        Assert.False(window.AccountGhost.IsVisible);
        Assert.False(((Control)window.AccountSection.Parent!).IsVisible);
        Assert.False(window.AgentGhost.IsVisible);
        Assert.True(window.FolderGhost.IsVisible);
    }

    // QA (Hana, CB-207): Settings is not modal, so an account can be added
    // while this dialog is open. The picker must not appear in space that was
    // never reserved for it; it waits for the next time the dialog opens.
    [AvaloniaFact]
    public void AnAccountAddedWhileOpenDoesNotGrowTheWindow()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex) };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;

        var window = NewWindow();
        var opened = ContentHeight(window);
        Assert.False(window.AccountGhost.IsVisible);

        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-board");
        Select(window, NewChatCli.Codex);
        Select(window, NewChatCli.ClaudeCode);

        Assert.Equal(opened, ContentHeight(window));
        Assert.False(window.AccountSection.IsVisible);

        // ...and a freshly opened dialog does offer it.
        Assert.True(NewWindow().AccountSection.IsVisible);
    }

    [AvaloniaFact]
    public void AnAccountRemovedWhileOpenHidesThePickerAndKeepsTheHeight()
    {
        var window = EverythingAvailable();
        var opened = ContentHeight(window);

        OrbweaverSettings.RemoveClaudeCodeProfileDir(".claude-board");
        Select(window, NewChatCli.Codex);
        Select(window, NewChatCli.ClaudeCode);

        Assert.False(window.AccountSection.IsVisible);
        Assert.Equal(opened, ContentHeight(window));
    }

    [AvaloniaFact]
    public void AnAccountListWithoutAUsableClaudeCodeReservesNothing()
    {
        FreshSettings();
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-board");
        NewChatAvailability.CurrentForTests = () => new[]
        {
            new NewChatOption(NewChatCli.ClaudeCode, Enabled: false, Reason: "not installed", Warning: null),
            Enabled(NewChatCli.Codex)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;

        var window = NewWindow();
        Flush();

        Assert.False(window.AccountGhost.IsVisible);
    }

    [AvaloniaFact]
    public void OnlyOpenClawUsableReservesNoFolderSpace()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => new[] { ("id-1", "Alexis") };

        var window = NewWindow();
        Flush();

        Assert.False(window.FolderGhost.IsVisible);
        Assert.True(window.AgentGhost.IsVisible);
        Assert.True(window.OpenClawSelected);
    }

    // --- CB-329: Open existing --------------------------------------------

    private static OpenClawReopenRow Row(string key, string title) =>
        new(OpenClawReopenTarget.Session, "openclaw:" + key, title, title, SessionKind.Direct,
            OpenClawNativeKind.None, false, new[] { "id-1" }, new[] { key }, DateTime.UtcNow.AddHours(-2));

    private static void OpenExisting(NewChatWindow window)
    {
        Select(window, NewChatWindow.OpenClawTag);
        window.OpenExistingRadio.IsChecked = true;
        Flush();
    }

    // The ceiling the PM set: a work area as short as 720 px must hold the
    // dialog. Measured here as the content height SizeToContent follows, in
    // the three configurations that reserve the most: everything on, every
    // extra account missing a directory (the CB-203 warning lines reserved),
    // and ReplyDisabled (the mode reason line reserved). If this fails, shrink
    // NewChatWindow.ExistingListHeight; do not raise the bound.
    // Before CB-329 the same windows measured 378 and 429 (develop d8b3aa48).
    [AvaloniaTheory]
    [InlineData("everything")]
    [InlineData("missing-homes")]
    [InlineData("reply-disabled")]
    public void WithAGatewayTheDialogFitsA720WorkArea(string configuration)
    {
        var window = configuration == "missing-homes" ? EverythingAvailableWithMissingHomes() : EverythingAvailable();
        if (configuration == "reply-disabled")
        {
            NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.ReplyDisabled;
            window = NewWindow();
        }

        var height = ContentHeight(window);

        Assert.True(window.ExistingGhost.IsVisible);
        Assert.True(height > 400, $"content measured {height}");
        Assert.True(height <= 700, $"content measured {height}, over the 700 dip bound");
    }

    // CB-397: the window that HAS the Machine row is the only one where the trust question is ever
    // asked, in its tallest state: the gateway present, every extra account missing its folder, a
    // remote machine chosen, and the question open with the hooks line. The invariant is that the
    // question adds NO height: the window measures exactly what the same window measures with the
    // trust layers hidden. It is not an absolute bound, because a window with the Machine row already
    // exceeded 700 on Windows before this feature (789 measured on develop, 660 on the Mac); the
    // absolute 700 bound is held above for the windows without the row, on both rids.
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheTrustQuestionAddsNoHeightToAWindowWithAMachineRow(bool asking)
    {
        var peers = new NewChatWindowMachineTests.FakeLauncher
        {
            Peers = [new("mini", true)],
            Result = new PeerLaunchResult(false, PeerLaunchCodes.TrustNeeded, null, null,
                new PeerLaunchTrust("T", "claude", "/srv/app", 1_800_000_000, Hooks: true)),
        };
        peers.Answers["mini"] = NewChatWindowMachineTests.Info();
        NewChatWindow.PeerLauncherForTests = peers;
        NewChatWindow.PeerLinkOnForTests = true;

        // The baseline: the same window with the trust layers hidden before its first measure.
        var bare = EverythingAvailableWithMissingHomes();
        Assert.NotNull(bare.MachineSection);
        bare.TrustGhost.IsVisible = false;
        var baseline = ContentHeight(bare);

        var window = EverythingAvailableWithMissingHomes();
        Assert.True(window.TrustGhost.IsVisible);

        if (asking)
        {
            window.MachineCombo.SelectedIndex = 1;
            Flush();
            window.StartButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Flush();
            Assert.True(window.TrustPanel.IsVisible);
            Assert.True(window.TrustHooks.IsVisible);
        }

        var height = ContentHeight(window);

        Assert.True(baseline > 400, $"baseline measured {baseline}");
        Assert.True(height == baseline, $"content measured {height} with the trust layers, {baseline} without: the question must add no height");
    }

    // The with-row window's absolute bound, where it is true: the Mac measures 660 here, and a
    // with-row window measures 789 on Windows at develop (CB-357), so this one is Mac-only and
    // shows as skipped there rather than passing without checking.
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnTheMacAWindowWithAMachineRowFitsA720WorkAreaWithOrWithoutTheQuestion(bool asking)
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "a with-row window already measures 789 dip headless on Windows at develop (CB-357)");
        var peers = new NewChatWindowMachineTests.FakeLauncher
        {
            Peers = [new("mini", true)],
            Result = new PeerLaunchResult(false, PeerLaunchCodes.TrustNeeded, null, null,
                new PeerLaunchTrust("T", "claude", "/srv/app", 1_800_000_000, Hooks: true)),
        };
        peers.Answers["mini"] = NewChatWindowMachineTests.Info();
        NewChatWindow.PeerLauncherForTests = peers;
        NewChatWindow.PeerLinkOnForTests = true;
        var window = EverythingAvailableWithMissingHomes();
        if (asking)
        {
            window.MachineCombo.SelectedIndex = 1;
            Flush();
            window.StartButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Flush();
            Assert.True(window.TrustPanel.IsVisible);
        }

        var height = ContentHeight(window);

        Assert.True(height > 400, $"content measured {height}");
        Assert.True(height <= 700, $"content measured {height}, over the 700 dip bound");
    }

    [AvaloniaFact]
    public void TheHeightHoldsAcrossEveryModeAndListState()
    {
        var window = EverythingAvailable();
        var opened = ContentHeight(window);
        var rows = (IReadOnlyList<OpenClawReopenRow>?)null;
        NewChatWindow.ReopenRowsForTests = () => rows;

        var states = new IReadOnlyList<OpenClawReopenRow>?[]
        {
            null,                                   // Loading
            Array.Empty<OpenClawReopenRow>(),       // No conversations
            new[] { Row("a", "One"), Row("b", "Two"), Row("c", "Three"), Row("d", "Four"),
                    Row("e", "Five"), Row("f", "Six"), Row("g", "Seven") },   // more than fit
            new[] { Row("a", "One") }
        };

        foreach (var state in states)
        {
            rows = state;
            OpenExisting(window);
            Assert.Equal(opened, ContentHeight(window));

            // Narrowing to nothing swaps the list for the empty text.
            window.ExistingSearch.Text = "zzz-nothing";
            Flush();
            Assert.Equal(opened, ContentHeight(window));
            window.ExistingSearch.Text = "";

            window.NewConversationRadio.IsChecked = true;
            Flush();
            Assert.Equal(opened, ContentHeight(window));

            Select(window, NewChatCli.Codex);
            Assert.Equal(opened, ContentHeight(window));
        }
    }

    // The unreachable state, with a failure long enough to want more than
    // three lines, holds the height too: the detail is trimmed inside the
    // list's fixed cell, not grown.
    [AvaloniaFact]
    public void TheHeightHoldsWhenTheGatewayIsUnreachable()
    {
        var window = EverythingAvailable();
        var opened = ContentHeight(window);
        var start = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        var now = start;
        NewChatWindow.NowForTests = () => now;
        NewChatWindow.ReopenRowsForTests = () => null;
        NewChatWindow.LastConnectFailureForTests = () => string.Join(" ",
            Enumerable.Repeat("Couldn't connect to the gateway at the address in Settings; check Local Network access.", 6));
        OpenExisting(window);

        now = start.AddSeconds(21);
        window.OnReopenLoadTick(null, EventArgs.Empty);

        Assert.True(window.ExistingEmptyDetail.IsVisible);
        Assert.Equal(opened, ContentHeight(window));
        NewChatWindow.NowForTests = null;
        NewChatWindow.LastConnectFailureForTests = null;
    }

    [AvaloniaFact]
    public void TheExistingGhostMeasuresLikeTheOpenExistingSection()
    {
        var window = EverythingAvailable();
        NewChatWindow.ReopenRowsForTests = () => new[] { Row("a", "One") };

        Select(window, NewChatWindow.OpenClawTag);
        ContentHeight(window);
        Assert.Equal(window.AgentSection.DesiredSize.Height, window.AgentGhost.DesiredSize.Height);

        OpenExisting(window);
        ContentHeight(window);
        Assert.Equal(window.AgentSection.DesiredSize.Height, window.ExistingGhost.DesiredSize.Height);
    }

    // ReplyDisabled's one extra line is reserved by the ghosts too, so the
    // section shown still measures like the ghost that stands in for it.
    [AvaloniaFact]
    public void TheReplyDisabledReasonLineIsReservedInBothGhosts()
    {
        var window = EverythingAvailable();
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.ReplyDisabled;
        NewChatWindow.ReopenRowsForTests = () => new[] { Row("a", "One") };
        window = NewWindow();

        Select(window, NewChatWindow.OpenClawTag);
        ContentHeight(window);
        Assert.True(window.ModeReason.IsVisible);
        Assert.Equal(window.AgentSection.DesiredSize.Height, window.ExistingGhost.DesiredSize.Height);
        Assert.True(window.AgentSection.DesiredSize.Height >= window.AgentGhost.DesiredSize.Height);
    }

    // No gateway: nothing is reserved for a pane that can never appear, so
    // the dialog is as tall as the same dialog with the OpenClaw ghosts
    // switched off — which is how it read before CB-329 (378 on develop
    // d8b3aa48 in this configuration) — plus the one thing NoGateway always
    // added: the reason line under the disabled OpenClaw row and its spacing.
    [AvaloniaFact]
    public void NoGatewayReservesNothingForTheExistingPane()
    {
        var withGateway = EverythingAvailable();
        withGateway.AgentGhost.IsVisible = false;
        withGateway.ExistingGhost.IsVisible = false;
        var expected = ContentHeight(withGateway);

        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;
        var window = NewWindow();

        Assert.False(window.ExistingGhost.IsVisible);
        Assert.False(window.AgentGhost.IsVisible);
        var reason = window.ReasonTextFor(NewChatWindow.OpenClawTag)!;
        var actual = ContentHeight(window);
        // 6 is _cliList's Spacing, between the OpenClaw row and its reason.
        Assert.Equal(expected + 6 + reason.DesiredSize.Height, actual);

        // CB-361: the slot is no longer shorter without a gateway, because the
        // Resume pane (a local CLI's own pane) is reserved whenever a local CLI
        // is usable and is as tall as the Open existing list. What stays true
        // is that nothing is held for OpenClaw: with Resume's ghost off as
        // well, the slot falls back to the Folder section's own height.
        window.ResumeGhost.IsVisible = false;
        Assert.True(ContentHeight(window) < actual);
    }
}
