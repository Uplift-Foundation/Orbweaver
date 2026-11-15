using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-168's "New chat…" dialog.
//
// Toggle() does real OS-facing work (MacOSActivation, Show, Activate) the
// same way SettingsWindow.Toggle does — see SettingsWindowSmokeTest's own
// header — so this suite reaches the private constructor directly via
// reflection instead, and never Close()s the windows it builds (the same
// font-cache corruption SettingsWindowSmokeTest documents).
//
// [Collection("Settings")]: this window reads OrbweaverSettings
// (NewChatRecentFolders, NewChatLastCli), which is process-wide.
[Collection("Settings")]
public class NewChatWindowTests : IDisposable
{
    // Every account directory "exists" unless a test says otherwise, so the
    // CB-203 warning never depends on what is in the real home directory of
    // the machine running the suite.
    public NewChatWindowTests()
    {
        NewChatWindow.AccountDirectoryExistsForTests = _ => true;
    }

    public void Dispose()
    {
        NewChatWindow.AccountDirectoryExistsForTests = null;
        NewChatAvailability.CurrentForTests = null;
        NewChatLauncher.LaunchForTests = null;
        NewChatWindow.CurrentStatusesForTests = null;
        NewChatWindow.ChooseFolderForTests = null;
        NewChatWindow.OpenClawAvailabilityForTests = null;
        NewChatWindow.KnownAgentsForTests = null;
        NewChatWindow.StartOpenClawConversationForTests = null;
        NewChatWindow.OrbForTests = null;
        NewChatWindow.ReopenRowsForTests = null;
    }

    private static void FreshSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-newchat-window-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    private static NewChatOption Enabled(NewChatCli cli, string? warning = null) =>
        new(cli, Enabled: true, Reason: null, Warning: warning);

    private static NewChatOption Disabled(NewChatCli cli, string reason) =>
        new(cli, Enabled: false, Reason: reason, Warning: null);

    // The separator NewChatAccounts.Choices' labels actually carry on this
    // platform (via ChatHeaderMeta.HomeRelative) — CI's Windows leg caught a
    // hardcoded "~/" here expecting "~\.claude-board" instead, the same
    // "passes on the machine it was written on" shape NewChatAccountsTests'
    // own Tilde() helper exists to avoid.
    private static string Tilde(string rest) => "~" + Path.DirectorySeparatorChar + rest;

    private static NewChatWindow NewWindow(
        NewChatCli? prefillCli = null, string? prefillCwd = null, string? prefillAgentId = null)
    {
        var ctor = typeof(NewChatWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: new[] { typeof(NewChatCli?), typeof(string), typeof(string) })
            ?? throw new MissingMethodException("NewChatWindow", ".ctor(NewChatCli?, string, string)");

        return (NewChatWindow)ctor.Invoke(new object?[] { prefillCli, prefillCwd, prefillAgentId });
    }

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Flush();
    }

    [AvaloniaFact]
    public void ConstructsHeadlessWithNoException()
    {
        FreshSettings();

        var window = NewWindow();

        Assert.NotNull(window);
    }

    [AvaloniaFact]
    public void EveryEnabledCliOffersARow()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode),
            Enabled(NewChatCli.Codex),
            Enabled(NewChatCli.Grok)
        };

        var window = NewWindow();

        // Local rows only — the CLI list always carries a fourth, OpenClaw
        // row alongside these three; OpenClawRowTests below cover it.
        var radios = window.CliList.Children.OfType<RadioButton>()
            .Where(r => r.Tag is NewChatCli).ToList();
        Assert.Equal(3, radios.Count);
        Assert.All(radios, r => Assert.True(r.IsEnabled));

        // No reason to state for a plain enabled row — the text line is
        // absent entirely, not present-and-empty.
        Assert.All(radios, r => Assert.Null(window.ReasonTextFor(r.Tag!)));
    }

    [AvaloniaFact]
    public void ADisabledCliShowsItsReasonAndCannotBeSelected()
    {
        FreshSettings();
        const string reason = "Codex not found on PATH or in its usual install locations.";
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode),
            Disabled(NewChatCli.Codex, reason),
            Enabled(NewChatCli.Grok)
        };

        var window = NewWindow();

        var codexRow = window.CliList.Children.OfType<RadioButton>()
            .Single(r => r.Tag is NewChatCli cli && cli == NewChatCli.Codex);

        Assert.False(codexRow.IsEnabled);
        Assert.Equal(reason, ToolTip.GetTip(codexRow));

        // Stated on screen, not only in a tooltip nobody can see in a
        // screenshot or without hovering — team-lead's own review flag.
        var reasonText = window.ReasonTextFor(NewChatCli.Codex);
        Assert.NotNull(reasonText);
        Assert.Equal(reason, reasonText!.Text);
        Assert.True(reasonText.IsVisible);
    }

    [AvaloniaFact]
    public void AWarningIsShownForAnEnabledCliMissingItsHook()
    {
        FreshSettings();
        const string warning = "launches, but no orb until hooks are installed — Settings → Hooks";
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode, warning) };

        var window = NewWindow();

        var row = window.CliList.Children.OfType<RadioButton>().Single(r => r.Tag is NewChatCli);
        Assert.Equal(warning, ToolTip.GetTip(row));

        var reasonText = window.ReasonTextFor(NewChatCli.ClaudeCode);
        Assert.NotNull(reasonText);
        Assert.Equal(warning, reasonText!.Text);
        Assert.True(reasonText.IsVisible);
    }

    // CB-255 §1: a machine still wired to the pre-Orbweaver hook copy — a DMG
    // user who never re-ran "Install Hooks.command", or a Windows box whose
    // installer has not re-wired — has working hooks, and the dialog must not
    // tell it otherwise. Driven through the real rule (HookCopyCandidates +
    // AnyExists via NewChatAvailability.Evaluate) against a temp tree holding
    // only the legacy copy, on both platforms' layouts; only the CLI lookup is
    // faked, since a real one would depend on what this machine has on PATH.
    //
    // The Windows Claude Code row is the pre-existing bug this also fixed
    // (filed against CB-168): its copy lives under %LOCALAPPDATA%, and the
    // dialog used to look under ~/.claude and warn on every wired machine.
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoHookWarningWhenOnlyTheLegacyCopyIsInstalled(bool onWindows)
    {
        FreshSettings();
        var root = Path.Combine(Path.GetTempPath(), "cb-newchat-legacyhook-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var local = Path.Combine(root, "local");
        try
        {
            foreach (var cli in NewChatAvailability.AllClis)
            {
                var legacyCopy = NewChatHookState.HookCopyCandidates(cli, onWindows, home, local, _ => null)[1];
                Directory.CreateDirectory(Path.GetDirectoryName(legacyCopy)!);
                File.WriteAllText(legacyCopy, "");
            }

            NewChatAvailability.CurrentForTests = () => NewChatAvailability.Evaluate(
                _ => "/usr/local/bin/cli",
                cli => NewChatHookState.AnyExists(
                    NewChatHookState.HookCopyCandidates(cli, onWindows, home, local, _ => null)));

            var window = NewWindow();

            foreach (var cli in NewChatAvailability.AllClis)
            {
                Assert.Null(window.ReasonTextFor(cli));
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    // The negative control: the same rule over an empty tree does warn, so
    // the case above is the legacy copies' doing and not a dialog that never
    // shows the warning.
    [AvaloniaFact]
    public void TheHookWarningIsShownWhenNeitherCopyIsInstalled()
    {
        FreshSettings();
        var root = Path.Combine(Path.GetTempPath(), "cb-newchat-nohook-" + Guid.NewGuid().ToString("N"));

        NewChatAvailability.CurrentForTests = () => NewChatAvailability.Evaluate(
            _ => "/usr/local/bin/cli",
            cli => NewChatHookState.AnyExists(
                NewChatHookState.HookCopyCandidates(cli, onWindows: false, root, root, _ => null)));

        var window = NewWindow();

        var reasonText = window.ReasonTextFor(NewChatCli.ClaudeCode);
        Assert.NotNull(reasonText);
        Assert.Equal(NewChatAvailability.HookMissingWarning, reasonText!.Text);
        Assert.True(reasonText.IsVisible);
    }

    [AvaloniaFact]
    public void ThePrefilledCliIsSelectedOverTheLastChoiceAndTheFirstEnabledRow()
    {
        FreshSettings();
        OrbweaverSettings.SetNewChatLastCli("Codex");
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode),
            Enabled(NewChatCli.Codex),
            Enabled(NewChatCli.Grok)
        };

        var window = NewWindow(prefillCli: NewChatCli.Grok);

        Assert.Equal(NewChatCli.Grok, window.SelectedCli);
    }

    [AvaloniaFact]
    public void WithNoPrefillTheLastChoiceIsSelected()
    {
        FreshSettings();
        OrbweaverSettings.SetNewChatLastCli("Codex");
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode),
            Enabled(NewChatCli.Codex)
        };

        var window = NewWindow();

        Assert.Equal(NewChatCli.Codex, window.SelectedCli);
    }

    [AvaloniaFact]
    public void APrefilledFolderIsAddedToTheComboAndSelected()
    {
        FreshSettings();
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };

        var window = NewWindow(prefillCwd: "/repo/prefilled");

        Assert.Equal("/repo/prefilled", window.FolderCombo.SelectedItem);
    }

    [AvaloniaFact]
    public void BrowseGoesThroughTheSeamAndSelectsThePickedFolder()
    {
        FreshSettings();
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        NewChatWindow.ChooseFolderForTests = () => Task.FromResult<string?>("/repo/browsed");

        var window = NewWindow();
        Click(window.BrowseButton);

        Assert.Equal("/repo/browsed", window.FolderCombo.SelectedItem);
    }

    [AvaloniaFact]
    public void ACancelledBrowseLeavesTheSelectionAlone()
    {
        FreshSettings();
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        NewChatWindow.ChooseFolderForTests = () => Task.FromResult<string?>(null);

        var window = NewWindow(prefillCwd: "/repo/prefilled");
        Click(window.BrowseButton);

        Assert.Equal("/repo/prefilled", window.FolderCombo.SelectedItem);
    }

    [AvaloniaFact]
    public void StartWithNoCliChosenSaysSoAndDoesNotLaunch()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        var launched = false;
        NewChatLauncher.LaunchForTests = (_, _, _) => { launched = true; return new LaunchResult(LaunchOutcome.Launched, "x"); };

        var window = NewWindow();
        Click(window.StartButton);

        Assert.False(launched);
        Assert.Equal("Choose a CLI first.", window.StatusLine.Text);
    }

    [AvaloniaFact]
    public void StartRendersAFailureMessageFromTheFakeLauncher()
    {
        FreshSettings();
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        NewChatLauncher.LaunchForTests = (_, _, _) =>
            new LaunchResult(LaunchOutcome.NotFound, "Claude Code not found on PATH or in its usual install locations.");

        var window = NewWindow();
        Click(window.StartButton);

        Assert.Equal(
            "Claude Code not found on PATH or in its usual install locations.", window.StatusLine.Text);
        Assert.True(window.StartButton.IsEnabled);
    }

    [AvaloniaFact]
    public void ASuccessfulStartSavesTheLastCliAndRecentFolder()
    {
        FreshSettings();
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.Codex) };
        NewChatLauncher.LaunchForTests = (_, _, _) => new LaunchResult(LaunchOutcome.Launched, "Codex started in /repo/one.");

        var window = NewWindow(prefillCli: NewChatCli.Codex, prefillCwd: "/repo/one");
        Click(window.StartButton);

        Assert.Equal("Codex", OrbweaverSettings.NewChatLastCli);
        Assert.Contains("/repo/one", OrbweaverSettings.NewChatRecentFolders);
    }

    // --- CB-201's Account picker ---

    [AvaloniaFact]
    public void TheAccountPickerIsHiddenWithNoConfiguredProfiles()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };

        var window = NewWindow();

        Assert.False(window.AccountSection.IsVisible);
    }

    [AvaloniaFact]
    public void TheAccountPickerListsTheConfiguredProfilesAlongsideDefault()
    {
        FreshSettings();
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-board");
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };

        var window = NewWindow();

        Assert.True(window.AccountSection.IsVisible);
        var items = window.AccountCombo.ItemsSource!.Cast<NewChatAccounts.Choice>().ToList();
        Assert.Equal(new[] { NewChatAccounts.DefaultLabelFor(NewChatCli.ClaudeCode), Tilde(".claude-board") }, items.Select(i => i.Label));
    }

    [AvaloniaFact]
    public void TheAccountPickerIsHiddenForCodexWhenOnlyClaudeCodeHasExtras()
    {
        FreshSettings();
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-board");
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.Codex) };

        var window = NewWindow(prefillCli: NewChatCli.Codex);

        Assert.False(window.AccountSection.IsVisible);
    }

    [AvaloniaFact]
    public void TheAccountPickerIsHiddenForGrokWhenOnlyClaudeCodeHasExtras()
    {
        FreshSettings();
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-board");
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.Grok) };

        var window = NewWindow(prefillCli: NewChatCli.Grok);

        Assert.False(window.AccountSection.IsVisible);
    }

    [AvaloniaFact]
    public void TheAccountPickerIsHiddenWhenOpenClawIsSelected()
    {
        FreshSettings();
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-board");
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => new[] { ("id-1", "Alexis") };

        var window = NewWindow();
        var openClawRow = window.CliList.Children.OfType<RadioButton>().Single(r => (string)r.Content! == "OpenClaw");
        openClawRow.IsChecked = true;

        Assert.False(window.AccountSection.IsVisible);
    }

    [AvaloniaFact]
    public void StartWithDefaultAccountPassesNullProfileDirToTheLauncher()
    {
        FreshSettings();
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-board");
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        string? seenProfileDir = "not set yet";
        NewChatLauncher.LaunchForTests = (_, _, profileDir) =>
        {
            seenProfileDir = profileDir;
            return new LaunchResult(LaunchOutcome.Launched, "Claude Code started in /repo/one.");
        };

        var window = NewWindow(prefillCli: NewChatCli.ClaudeCode, prefillCwd: "/repo/one");
        Click(window.StartButton);

        Assert.Null(seenProfileDir);
        Assert.Null(OrbweaverSettings.NewChatLastProfileFor(NewChatCli.ClaudeCode));
    }

    [AvaloniaFact]
    public void StartWithAChosenAccountPassesItsProfileDirToTheLauncher()
    {
        FreshSettings();
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-board");
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        string? seenProfileDir = "not set yet";
        NewChatLauncher.LaunchForTests = (_, _, profileDir) =>
        {
            seenProfileDir = profileDir;
            return new LaunchResult(LaunchOutcome.Launched, "Claude Code started in /repo/one.");
        };

        var window = NewWindow(prefillCli: NewChatCli.ClaudeCode, prefillCwd: "/repo/one");
        var choices = window.AccountCombo.ItemsSource!.Cast<NewChatAccounts.Choice>().ToList();
        window.AccountCombo.SelectedItem = choices.Single(c => c.ProfileDir == ".claude-board");
        Click(window.StartButton);

        Assert.Equal(".claude-board", seenProfileDir);
        Assert.Equal(".claude-board", OrbweaverSettings.NewChatLastProfileFor(NewChatCli.ClaudeCode));
    }

    [AvaloniaFact]
    public void TheLastChosenAccountIsRestoredOnReopen()
    {
        FreshSettings();
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-board");
        OrbweaverSettings.SetNewChatLastProfile(NewChatCli.ClaudeCode, ".claude-board");
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };

        var window = NewWindow(prefillCli: NewChatCli.ClaudeCode);

        var selected = (NewChatAccounts.Choice)window.AccountCombo.SelectedItem!;
        Assert.Equal(".claude-board", selected.ProfileDir);
    }

    [AvaloniaFact]
    public void ARemovedSavedAccountFallsBackToDefault()
    {
        FreshSettings();
        OrbweaverSettings.SetNewChatLastProfile(NewChatCli.ClaudeCode, ".claude-gone");
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };

        var window = NewWindow(prefillCli: NewChatCli.ClaudeCode);

        var selected = (NewChatAccounts.Choice)window.AccountCombo.SelectedItem!;
        Assert.Null(selected.ProfileDir);
    }

    // --- CB-203: Codex and Grok accounts ---

    private static List<NewChatAccounts.Choice> Items(NewChatWindow window) =>
        window.AccountCombo.ItemsSource!.Cast<NewChatAccounts.Choice>().ToList();

    private static void Select(NewChatWindow window, NewChatCli cli)
    {
        window.CliList.Children.OfType<RadioButton>().Single(r => r.Tag is NewChatCli c && c == cli).IsChecked = true;
        Flush();
    }

    [AvaloniaFact]
    public void TheAccountPickerListsCodexHomesForCodexAndNeverClaudeProfiles()
    {
        FreshSettings();
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-board");
        OrbweaverSettings.AddCodexHome(".codex-work");
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.Codex) };

        var window = NewWindow(prefillCli: NewChatCli.Codex);

        Assert.True(window.AccountSection.IsVisible);
        Assert.Equal(
            new[] { NewChatAccounts.DefaultLabelFor(NewChatCli.Codex), Tilde(".codex-work") },
            Items(window).Select(i => i.Label));
    }

    [AvaloniaFact]
    public void TheAccountPickerListsGrokHomesForGrokAndNeverClaudeProfiles()
    {
        FreshSettings();
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-board");
        OrbweaverSettings.AddCodexHome(".codex-work");
        OrbweaverSettings.AddGrokHome(".grok-work");
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.Grok) };

        var window = NewWindow(prefillCli: NewChatCli.Grok);

        Assert.True(window.AccountSection.IsVisible);
        Assert.Equal(
            new[] { NewChatAccounts.DefaultLabelFor(NewChatCli.Grok), Tilde(".grok-work") },
            Items(window).Select(i => i.Label));
    }

    // Switching CLI rebuilds the list from the newly selected CLI's own
    // settings, and hides the picker for one whose list is Default alone.
    [AvaloniaFact]
    public void SwitchingCliRebuildsTheAccountListForThatCli()
    {
        FreshSettings();
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-board");
        OrbweaverSettings.AddCodexHome(".codex-work");
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex), Enabled(NewChatCli.Grok)
        };

        var window = NewWindow(prefillCli: NewChatCli.ClaudeCode);
        Assert.Equal(".claude-board", Items(window)[1].ProfileDir);

        Select(window, NewChatCli.Codex);
        Assert.True(window.AccountSection.IsVisible);
        Assert.Equal(".codex-work", Items(window)[1].ProfileDir);

        Select(window, NewChatCli.Grok);
        Assert.False(window.AccountSection.IsVisible);

        Select(window, NewChatCli.ClaudeCode);
        Assert.True(window.AccountSection.IsVisible);
        Assert.Equal(".claude-board", Items(window)[1].ProfileDir);
    }

    // The slot is reserved when only Codex has extras — the CB-207 ghost
    // used to be decided by Claude Code's list alone.
    [AvaloniaFact]
    public void TheAccountSlotIsReservedWhenOnlyCodexHasExtras()
    {
        FreshSettings();
        OrbweaverSettings.AddCodexHome(".codex-work");
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex) };

        var window = NewWindow(prefillCli: NewChatCli.ClaudeCode);

        Assert.True(window.AccountGhost.IsVisible);
        Assert.False(window.AccountSection.IsVisible);

        Select(window, NewChatCli.Codex);
        Assert.True(window.AccountSection.IsVisible);
    }

    // A disabled CLI's extras reserve nothing: it can never be selected, so
    // its picker can never appear.
    [AvaloniaFact]
    public void ADisabledCliExtrasReserveNoSlot()
    {
        FreshSettings();
        OrbweaverSettings.AddGrokHome(".grok-work");
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Disabled(NewChatCli.Grok, "Grok isn't installed.")
        };

        var window = NewWindow();

        Assert.False(window.AccountGhost.IsVisible);
    }

    [AvaloniaTheory]
    [InlineData("Codex", ".codex-work")]
    [InlineData("Grok", ".grok-work")]
    public void StartWithAChosenCodexOrGrokAccountPassesItAndRemembersItForThatCliOnly(string cliName, string home)
    {
        var cli = Enum.Parse<NewChatCli>(cliName);
        FreshSettings();
        if (cli == NewChatCli.Codex) OrbweaverSettings.AddCodexHome(home); else OrbweaverSettings.AddGrokHome(home);
        OrbweaverSettings.SetNewChatLastProfile(NewChatCli.ClaudeCode, ".claude-board");
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(cli) };
        (NewChatCli Cli, string? Dir)? seen = null;
        NewChatLauncher.LaunchForTests = (launchedCli, _, profileDir) =>
        {
            seen = (launchedCli, profileDir);
            return new LaunchResult(LaunchOutcome.Launched, "started in /repo/one.");
        };

        var window = NewWindow(prefillCli: cli, prefillCwd: "/repo/one");
        window.AccountCombo.SelectedItem = Items(window).Single(c => c.ProfileDir == home);
        Click(window.StartButton);

        Assert.Equal((cli, (string?)home), seen);
        Assert.Equal(home, OrbweaverSettings.NewChatLastProfileFor(cli));
        Assert.Equal(".claude-board", OrbweaverSettings.NewChatLastProfileFor(NewChatCli.ClaudeCode));
    }

    [AvaloniaFact]
    public void StartWithCodexDefaultAccountPassesNull()
    {
        FreshSettings();
        OrbweaverSettings.AddCodexHome(".codex-work");
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.Codex) };
        string? seenProfileDir = "not set yet";
        NewChatLauncher.LaunchForTests = (_, _, profileDir) =>
        {
            seenProfileDir = profileDir;
            return new LaunchResult(LaunchOutcome.Launched, "Codex started in /repo/one.");
        };

        var window = NewWindow(prefillCli: NewChatCli.Codex, prefillCwd: "/repo/one");
        Click(window.StartButton);

        Assert.Null(seenProfileDir);
        Assert.Null(OrbweaverSettings.NewChatLastProfileFor(NewChatCli.Codex));
    }

    // Each CLI restores its own remembered account — a Claude Code pick is
    // never restored onto Codex's picker, even when the names collide.
    [AvaloniaFact]
    public void EachCliRestoresItsOwnLastAccount()
    {
        FreshSettings();
        OrbweaverSettings.AddClaudeCodeProfileDir(".shared-work");
        OrbweaverSettings.AddCodexHome(".shared-work");
        OrbweaverSettings.AddGrokHome(".grok-work");
        OrbweaverSettings.SetNewChatLastProfile(NewChatCli.ClaudeCode, ".shared-work");
        OrbweaverSettings.SetNewChatLastProfile(NewChatCli.Grok, ".grok-work");
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex), Enabled(NewChatCli.Grok)
        };

        var window = NewWindow(prefillCli: NewChatCli.ClaudeCode);
        Assert.Equal(".shared-work", ((NewChatAccounts.Choice)window.AccountCombo.SelectedItem!).ProfileDir);

        Select(window, NewChatCli.Codex);
        Assert.Null(((NewChatAccounts.Choice)window.AccountCombo.SelectedItem!).ProfileDir);

        Select(window, NewChatCli.Grok);
        Assert.Equal(".grok-work", ((NewChatAccounts.Choice)window.AccountCombo.SelectedItem!).ProfileDir);
    }

    // The picker for an account added while the dialog was open stays hidden
    // (CB-207), and Start must not launch under a selection nobody could see —
    // even when that hidden selection is a remembered extra.
    [AvaloniaFact]
    public void AHiddenAccountComboNeverChoosesTheLaunchAccount()
    {
        FreshSettings();
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.Codex), Enabled(NewChatCli.Grok) };
        string? seenProfileDir = "not set yet";
        NewChatLauncher.LaunchForTests = (_, _, profileDir) =>
        {
            seenProfileDir = profileDir;
            return new LaunchResult(LaunchOutcome.Launched, "Codex started in /repo/one.");
        };

        var window = NewWindow(prefillCli: NewChatCli.Codex, prefillCwd: "/repo/one");
        OrbweaverSettings.AddCodexHome(".codex-work");
        OrbweaverSettings.SetNewChatLastProfile(NewChatCli.Codex, ".codex-work");
        Select(window, NewChatCli.Grok);
        Select(window, NewChatCli.Codex);

        Assert.False(window.AccountSection.IsVisible);
        Assert.Equal(".codex-work", ((NewChatAccounts.Choice)window.AccountCombo.SelectedItem!).ProfileDir);

        Click(window.StartButton);

        Assert.Null(seenProfileDir);
    }

    // --- CB-203: the missing-home warning under the picker ---

    [AvaloniaTheory]
    [InlineData("ClaudeCode", ".claude-gone", "Claude Code will start first-run setup there.")]
    [InlineData("Codex", ".codex-gone", "Codex will refuse to start.")]
    [InlineData("Grok", ".grok-gone", "Grok will create a fresh, logged-out account there.")]
    public void AMissingAccountDirectoryIsWarnedUnderThePickerAndStartStaysEnabled(
        string cliName, string home, string consequence)
    {
        var cli = Enum.Parse<NewChatCli>(cliName);
        FreshSettings();
        switch (cli)
        {
            case NewChatCli.Codex: OrbweaverSettings.AddCodexHome(home); break;
            case NewChatCli.Grok: OrbweaverSettings.AddGrokHome(home); break;
            default: OrbweaverSettings.AddClaudeCodeProfileDir(home); break;
        }
        NewChatWindow.AccountDirectoryExistsForTests = _ => false;
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(cli) };

        var window = NewWindow(prefillCli: cli);
        Assert.False(window.AccountWarning.IsVisible);

        window.AccountCombo.SelectedItem = Items(window).Single(c => c.ProfileDir == home);
        Flush();

        Assert.True(window.AccountWarning.IsVisible);
        Assert.Equal("This folder doesn't exist; " + consequence, window.AccountWarning.Text);
        Assert.Equal(window.AccountWarning.Text, ToolTip.GetTip(window.AccountWarning));
        Assert.True(window.StartButton.IsEnabled);

        window.AccountCombo.SelectedIndex = 0;
        Flush();
        Assert.False(window.AccountWarning.IsVisible);
    }

    // The real filesystem, with the seam unset: an existing directory is not
    // warned about, a missing one is. Absolute temp paths, so the answer does
    // not depend on the home directory of the machine running this.
    [AvaloniaFact]
    public void TheWarningReadsTheRealFilesystemWhenNoSeamIsSet()
    {
        FreshSettings();
        NewChatWindow.AccountDirectoryExistsForTests = null;
        var existing = Path.Combine(Path.GetTempPath(), "cb203-home-" + Guid.NewGuid());
        Directory.CreateDirectory(existing);
        var missing = Path.Combine(Path.GetTempPath(), "cb203-missing-" + Guid.NewGuid());
        OrbweaverSettings.AddCodexHome(existing);
        OrbweaverSettings.AddCodexHome(missing);
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.Codex) };

        var window = NewWindow(prefillCli: NewChatCli.Codex);

        window.AccountCombo.SelectedItem = Items(window).Single(c => c.ProfileDir == existing);
        Flush();
        Assert.False(window.AccountWarning.IsVisible);

        window.AccountCombo.SelectedItem = Items(window).Single(c => c.ProfileDir == missing);
        Flush();
        Assert.True(window.AccountWarning.IsVisible);
    }

    // A missing home on a CLI that is disabled reserves nothing: it can never
    // be selected, so its warning can never appear.
    [AvaloniaFact]
    public void ADisabledCliMissingHomeReservesNoWarningLine()
    {
        FreshSettings();
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-work");
        OrbweaverSettings.AddGrokHome(".grok-gone");
        NewChatWindow.AccountDirectoryExistsForTests = dir => !dir.EndsWith(".grok-gone", StringComparison.Ordinal);
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Disabled(NewChatCli.Grok, "Grok isn't installed.")
        };

        var window = NewWindow();

        Assert.True(window.AccountGhost.IsVisible);
        Assert.False(window.AccountWarningGhost.IsVisible);
    }

    // --- the watch tick, driven directly per LocalCliChatSessionTests' own
    // pattern for a debounce timer: no real ~20s wall-clock wait needed. ---

    [AvaloniaFact]
    public void TheWatchReportsTheOrbOnceAMatchingSessionAppears()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>
        {
            ["new-id"] = new() { Cwd = "/repo/one", Source = SessionSource.ClaudeCode }
        };

        var window = NewWindow();
        window.ArmWatchForTests(
            new HashSet<string>(), NewChatCli.ClaudeCode, "/repo/one", DateTime.UtcNow.AddSeconds(20));
        window.OnWatchTick(null, EventArgs.Empty);

        Assert.Equal("Claude Code is running — its orb should be on screen.", window.StatusLine.Text);
    }

    [AvaloniaFact]
    public void TheWatchSaysSoOnceTheDeadlinePasses()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();

        var window = NewWindow();
        window.ArmWatchForTests(
            new HashSet<string>(), NewChatCli.ClaudeCode, "/repo/one", DateTime.UtcNow.AddSeconds(-1));
        window.OnWatchTick(null, EventArgs.Empty);

        Assert.Equal(
            "Terminal opened; no orb yet — the CLI's hook may not be installed / trusted.", window.StatusLine.Text);
    }

    [AvaloniaFact]
    public void TheWatchKeepsQuietBeforeTheDeadlineWithNoMatch()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();

        var window = NewWindow();
        window.ArmWatchForTests(
            new HashSet<string>(), NewChatCli.ClaudeCode, "/repo/one", DateTime.UtcNow.AddSeconds(20));
        window.StatusLine.Text = "Starting…";
        window.OnWatchTick(null, EventArgs.Empty);

        Assert.Equal("Starting…", window.StatusLine.Text);
    }

    // --- ShouldClose: pure, never calls the real Close() ---

    [Fact]
    public void EscapeCloses()
    {
        Assert.True(NewChatWindow.ShouldClose(Key.Escape, KeyModifiers.None));
    }

    [Fact]
    public void CmdWCloses()
    {
        Assert.True(NewChatWindow.ShouldClose(Key.W, KeyModifiers.Meta));
    }

    [Fact]
    public void PlainWDoesNotClose()
    {
        Assert.False(NewChatWindow.ShouldClose(Key.W, KeyModifiers.None));
    }

    [Fact]
    public void AnUnrelatedKeyDoesNotClose()
    {
        Assert.False(NewChatWindow.ShouldClose(Key.A, KeyModifiers.None));
    }

    // --- OpenClaw row ---

    private static FakeChatSession OpenClawFake(string sessionId = "openclaw:abc123") =>
        new(null) { SessionId = sessionId, DisplayName = "Fake OpenClaw" };

    // CB-329: only NoGateway shuts the row now. ReplyDisabled used to as well;
    // it keeps the row live for Open existing and disables just "New
    // conversation", with the reason under it (NewChatWindowReopenTests has the
    // full ReplyDisabled cases).
    [AvaloniaFact]
    public void TheOpenClawRowIsDisabledWithItsReasonWhenThereIsNoGateway()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;

        var window = NewWindow();

        var row = window.CliList.Children.OfType<RadioButton>().Single(r => (string)r.Content! == "OpenClaw");
        Assert.False(row.IsEnabled);
        Assert.Equal("No gateway configured.", ToolTip.GetTip(row));

        var reasonText = window.ReasonTextFor(NewChatWindow.OpenClawTag);
        Assert.NotNull(reasonText);
        Assert.Equal("No gateway configured.", reasonText!.Text);
        Assert.True(reasonText.IsVisible);
    }

    [AvaloniaFact]
    public void TheOpenClawRowStaysEnabledWhenOnlyReplyIsOff()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.ReplyDisabled;
        NewChatWindow.ReopenRowsForTests = () => Array.Empty<OpenClawReopenRow>();

        var window = NewWindow();

        var row = window.CliList.Children.OfType<RadioButton>().Single(r => (string)r.Content! == "OpenClaw");
        Assert.True(row.IsEnabled);
        Assert.Null(ToolTip.GetTip(row));
        Assert.Null(window.ReasonTextFor(NewChatWindow.OpenClawTag));
        Assert.False(window.NewConversationRadio.IsEnabled);
        Assert.Equal("Turn on \"Allow replying to agents\" in Settings.", window.ModeReason.Text);

        NewChatWindow.ReopenRowsForTests = null;
    }

    [AvaloniaFact]
    public void TheOpenClawRowIsEnabledWhenReady()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;

        var window = NewWindow();

        var row = window.CliList.Children.OfType<RadioButton>().Single(r => (string)r.Content! == "OpenClaw");
        Assert.True(row.IsEnabled);
        Assert.Null(window.ReasonTextFor(NewChatWindow.OpenClawTag));
    }

    // With no local CLI usable at all, OpenClaw (if ready) is the fallback
    // selection — the dialog never opens with nothing chosen when it has at
    // least one usable option.
    [AvaloniaFact]
    public void OpenClawIsSelectedWhenNoLocalCliIsUsable()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => Array.Empty<(string, string)>();

        var window = NewWindow();

        Assert.True(window.OpenClawSelected);
        Assert.Null(window.SelectedCli);
    }

    // A local CLI, when usable, still wins over OpenClaw by default — the
    // fourth row is a fallback, not a preference.
    [AvaloniaFact]
    public void ALocalCliIsPreferredOverOpenClawWhenBothAreUsable()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;

        var window = NewWindow();

        Assert.False(window.OpenClawSelected);
        Assert.Equal(NewChatCli.ClaudeCode, window.SelectedCli);
    }

    [AvaloniaFact]
    public void SelectingOpenClawShowsTheAgentSectionAndHidesTheFolderSection()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => new[] { ("id-1", "Alexis") };

        var window = NewWindow();
        Assert.True(window.FolderSection.IsVisible);
        Assert.False(window.AgentSection.IsVisible);

        var openClawRow = window.CliList.Children.OfType<RadioButton>().Single(r => (string)r.Content! == "OpenClaw");
        openClawRow.IsChecked = true;

        Assert.False(window.FolderSection.IsVisible);
        Assert.True(window.AgentSection.IsVisible);
        Assert.True(window.OpenClawSelected);
    }

    [AvaloniaFact]
    public void TheAgentComboIsPopulatedFromKnownAgentsSortedAsGiven()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => new[] { ("id-1", "Alexis"), ("id-2", "Bram") };

        var window = NewWindow();

        var items = window.AgentCombo.ItemsSource!.Cast<NewChatWindow.AgentItem>().ToList();
        Assert.Equal(new[] { "Alexis", "Bram" }, items.Select(i => i.Name));
        Assert.Equal("Alexis", ((NewChatWindow.AgentItem)window.AgentCombo.SelectedItem!).Name);
    }

    [AvaloniaFact]
    public void StartOpenClawWithNoAgentChosenSaysSoAndDoesNotCallTheSeam()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => Array.Empty<(string, string)>();
        var called = false;
        NewChatWindow.StartOpenClawConversationForTests = (_, _) =>
        {
            called = true;
            return Task.FromResult<(IRemoteChatSession?, string?)>((null, null));
        };

        var window = NewWindow();
        Click(window.StartButton);

        Assert.False(called);
        Assert.Equal("Choose an agent first.", window.StatusLine.Text);
    }

    [AvaloniaFact]
    public void StartOpenClawRendersAFailureMessageFromTheFakeSeam()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => new[] { ("id-1", "Alexis") };
        NewChatWindow.StartOpenClawConversationForTests = (_, _) =>
            Task.FromResult<(IRemoteChatSession?, string?)>((null, "not connected to the gateway"));

        var window = NewWindow();
        Click(window.StartButton);

        Assert.Equal("not connected to the gateway", window.StatusLine.Text);
        Assert.True(window.StartButton.IsEnabled);
    }

    [AvaloniaFact]
    public void ASuccessfulOpenClawStartShowsTheAgentNameAndArmsTheWatch()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => new[] { ("id-1", "Alexis") };
        var fake = OpenClawFake();
        NewChatWindow.StartOpenClawConversationForTests = (agentId, _) =>
        {
            Assert.Equal("id-1", agentId);
            return Task.FromResult<(IRemoteChatSession?, string?)>((fake, null));
        };

        var window = NewWindow();
        Click(window.StartButton);

        Assert.Equal("Conversation started with Alexis. Waiting for its orb…", window.StatusLine.Text);
    }

    // --- the OpenClaw watch tick, driven directly ---

    [AvaloniaFact]
    public void TheOpenClawWatchOpensAChatPanelOnceTheScanBuildsTheOrb()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        var fake = OpenClawFake("openclaw:watch-match");
        var orb = new OrbWindow(fake.SessionId);
        NewChatWindow.OrbForTests = id => id == fake.SessionId ? orb : null;

        var window = NewWindow();
        window.ArmOpenClawWatchForTests(fake, DateTime.UtcNow.AddSeconds(20));
        window.OnWatchTick(null, EventArgs.Empty);

        Assert.Equal("Its orb should be on screen.", window.StatusLine.Text);
        Assert.True(ChatPanel.IsOpenFor(fake.SessionId));

        ChatPanel.CloseFor(fake.SessionId);
    }

    [AvaloniaFact]
    public void TheOpenClawWatchSaysSoOnceTheDeadlinePassesWithNoOrb()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        NewChatWindow.OrbForTests = _ => null;
        var fake = OpenClawFake();

        var window = NewWindow();
        window.ArmOpenClawWatchForTests(fake, DateTime.UtcNow.AddSeconds(-1));
        window.OnWatchTick(null, EventArgs.Empty);

        Assert.Equal("Conversation created; no orb yet — check your gateway connection.", window.StatusLine.Text);
    }

    [AvaloniaFact]
    public void TheOpenClawWatchKeepsQuietBeforeTheDeadlineWithNoOrb()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        NewChatWindow.OrbForTests = _ => null;
        var fake = OpenClawFake();

        var window = NewWindow();
        window.ArmOpenClawWatchForTests(fake, DateTime.UtcNow.AddSeconds(20));
        window.StatusLine.Text = "Starting…";
        window.OnWatchTick(null, EventArgs.Empty);

        Assert.Equal("Starting…", window.StatusLine.Text);
    }

    // Neither watch armed — a fresh window that never launched anything.
    // OnWatchTick's own comment says the two watches are mutually exclusive
    // and exactly one block ever has something to check; this proves the
    // third case, where neither does, falls through to the plain stop
    // rather than throwing or matching by accident.
    [AvaloniaFact]
    public void TheWatchStopsTheTimerWhenNothingIsArmed()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();

        var window = NewWindow();

        var timerField = typeof(NewChatWindow).GetField("_watchTimer", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Start();
        Assert.True(timer.IsEnabled);
        timerField.SetValue(window, timer);

        window.OnWatchTick(null, EventArgs.Empty);

        Assert.False(timer.IsEnabled);
    }

    // --- OpenClaw agent prefill: an OpenClaw orb's own "New chat here" ---

    [AvaloniaFact]
    public void ThePrefilledAgentWinsOverTheLastUsedCliAndTheDefault()
    {
        FreshSettings();
        OrbweaverSettings.SetNewChatLastCli("Codex");
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => new[] { ("id-1", "Alexis"), ("id-2", "Bram") };

        var window = NewWindow(prefillAgentId: "id-2");

        Assert.True(window.OpenClawSelected);
        Assert.Null(window.SelectedCli);
        Assert.Equal("Bram", ((NewChatWindow.AgentItem)window.AgentCombo.SelectedItem!).Name);
    }

    // The prefill only wins when OpenClaw is actually usable — otherwise it
    // falls through to the ordinary local-CLI preference, the same as no
    // prefill at all.
    [AvaloniaFact]
    public void AnAgentPrefillIsIgnoredWhenOpenClawIsNotReady()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => new[] { Enabled(NewChatCli.ClaudeCode) };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;

        var window = NewWindow(prefillAgentId: "id-2");

        Assert.False(window.OpenClawSelected);
        Assert.Equal(NewChatCli.ClaudeCode, window.SelectedCli);
    }

    // --- OnWindowKeyDown's no-op half, driven directly ---

    // Never supplies Escape or Cmd-W here — that would reach the real
    // Close() through CloseForReal, which is excluded rather than tested
    // for the same font-cache-corruption reason SettingsWindowSmokeTest
    // documents. This proves the branch that does *not* close still runs
    // cleanly when driven directly, rather than only through ShouldClose's
    // own pure-function test.
    [AvaloniaFact]
    public void KeyDownWithNoClosingKeyIsANoOp()
    {
        FreshSettings();
        NewChatAvailability.CurrentForTests = () => Array.Empty<NewChatOption>();
        var window = NewWindow();

        window.OnWindowKeyDown(null, new KeyEventArgs { Key = Key.A, KeyModifiers = KeyModifiers.None });

        // Still here and unclosed — the assertion is that nothing happened.
        Assert.NotNull(window);
    }
}
