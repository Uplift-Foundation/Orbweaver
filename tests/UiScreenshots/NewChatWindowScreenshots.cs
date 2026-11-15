using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-168's "New chat…" dialog, captured through real Skia the same way every
// other window in this project is — see SettingsWindowScreenshots' own
// header for why this suite exists at all (ScreenshotHelper.Capture never
// runs through the null renderer tests/UiTests uses).
//
// Same reflection-constructor seam as NewChatWindowTests, and for the same
// reason: NewChatWindow.Toggle() makes real OS calls this project has no
// business making headless, so every scenario here reaches the private
// constructor directly and is never closed (SettingsWindowSmokeTest's own
// comment on the FontManager corruption a stray Close() caused once).
//
// [Collection("Settings")]: the window reads OrbweaverSettings
// (NewChatRecentFolders, NewChatLastCli, the OpenClaw settings the fourth
// row's availability depends on), which is process-wide — see
// SettingsCollection.cs.
//
// Seams cleared in the constructor as well as Dispose, not only at the end
// of each test method: an assertion failure or an exception between a seam
// being set and that end-of-method call used to leave it set for whatever
// ran next — the same class of leak ChatPanelPinTests' own CB-168 fix
// (01606f15) closed for ChatPanel's registry. The constructor clears first
// so a test never inherits whatever a differently-ordered previous test
// left behind either, matching NewChatWindowTests' own IDisposable shape.
[Collection("Settings")]
public class NewChatWindowScreenshots : IDisposable
{
    public NewChatWindowScreenshots()
    {
        FreshSettings();
        ClearSeams();

        // Every account directory "exists" unless a scenario says otherwise
        // (CB-203): otherwise the missing-home warning would depend on the
        // runner's real home directory — present on a dev Mac with
        // ~/.claude-board, absent on CI — and the same scenario would draw
        // differently on each.
        NewChatWindow.AccountDirectoryExistsForTests = _ => true;
    }

    public void Dispose() => ClearSeams();

    // CB-201's own review flag: AccountPickerWithTwoProfiles used to point
    // ORBWEAVER_SETTINGS_DIR at its own fresh directory but never put it
    // back, and ClearSeams only resets the delegate seams, not the settings
    // dir or ClaudeCodeProfileDirs itself. xUnit constructs a fresh instance
    // per test method but does not guarantee method order, so whichever
    // scenario ran right after AccountPickerWithTwoProfiles inherited its two
    // stray profiles — DefaultState drew the Account picker on the macOS CI
    // leg for exactly this reason, on a capture that is supposed to look
    // identical to CB-168's dialog. A fresh settings dir per scenario, the
    // same pattern NewChatWindowTests' own FreshSettings() already uses,
    // means every scenario starts from an empty profile list regardless of
    // what ran before it — order stops being able to matter at all, rather
    // than merely not mattering in whatever order happened to be tried.
    private static void FreshSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-newchat-screenshots-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    private static NewChatWindow NewWindow(
        NewChatCli? prefillCli = null, string? prefillCwd = null, string? prefillAgentId = null)
    {
        var ctor = typeof(NewChatWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: new[] { typeof(NewChatCli?), typeof(string), typeof(string) })
            ?? throw new MissingMethodException("NewChatWindow", ".ctor(NewChatCli?, string, string)");

        return (NewChatWindow)ctor.Invoke(new object?[] { prefillCli, prefillCwd, prefillAgentId });
    }

    private static NewChatOption Enabled(NewChatCli cli, string? warning = null) =>
        new(cli, Enabled: true, Reason: null, Warning: warning);

    private static NewChatOption Disabled(NewChatCli cli, string reason) =>
        new(cli, Enabled: false, Reason: reason, Warning: null);

    // The default state: every local CLI usable, OpenClaw present but
    // disabled (no gateway configured is the state a fresh install actually
    // has), Claude Code preselected as the first enabled row.
    [AvaloniaFact]
    public void DefaultState()
    {
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode),
            Enabled(NewChatCli.Codex),
            Enabled(NewChatCli.Grok)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();

        var window = NewWindow();

        ScreenshotHelper.Capture(window, "new-chat-window-default.png");
    }

    // One local CLI disabled with its reason — Codex not found — beside the
    // other two still usable.
    [AvaloniaFact]
    public void OneCliDisabled()
    {
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode),
            Disabled(NewChatCli.Codex, "Codex not found on PATH or in its usual install locations."),
            Enabled(NewChatCli.Grok)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();

        var window = NewWindow();

        ScreenshotHelper.Capture(window, "new-chat-window-cli-disabled.png");
    }

    // The inline failure a launch attempt renders — the status line is the
    // whole surface a failed Start has, so this is what "it didn't work"
    // looks like to whoever is looking at the dialog when it happens.
    [AvaloniaFact]
    public void LaunchError()
    {
        // All four rows present — the same default availability every other
        // scenario uses — so this capture shows what a user actually sees:
        // the failure line under a dialog with every entry still there, not
        // a fixture-narrowed page missing Codex and Grok.
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex), Enabled(NewChatCli.Grok)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatLauncher.LaunchForTests = (_, _, _) => new LaunchResult(
            LaunchOutcome.NotFound, "Claude Code not found on PATH or in its usual install locations.");

        var window = NewWindow();
        window.Show();
        ScreenshotHelper.Flush();

        window.StartButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        ScreenshotHelper.Flush();

        // A screenshot is only checked by eye — this is the assertion that
        // would have settled, in one run, whether a failed Start ever
        // touches the row list: all four rows still present, still in the
        // same enabled state, after the click. BuildCliList only ever runs
        // once (from the constructor), so this should be inert by
        // construction, but "should be" is exactly the gap a capture alone
        // can't close.
        var radios = window.CliList.Children.OfType<RadioButton>().ToList();
        Assert.Equal(4, radios.Count);
        Assert.Equal(3, radios.Count(r => r.Tag is NewChatCli));
        Assert.Single(radios, r => Equals(r.Tag, NewChatWindow.OpenClawTag));
        Assert.All(radios.Where(r => r.Tag is NewChatCli), r => Assert.True(r.IsEnabled));
        Assert.False(radios.Single(r => Equals(r.Tag, NewChatWindow.OpenClawTag)).IsEnabled);

        ScreenshotHelper.CaptureAlreadyShown(window, "new-chat-window-launch-error.png");
    }

    // CB-201's Account section: Claude Code selected with two configured
    // profiles beside Default — the picker sitting between the CLI list and
    // the Folder section, exactly where BuildCliList's own comment places it.
    [AvaloniaFact]
    public void AccountPickerWithTwoProfiles()
    {
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex), Enabled(NewChatCli.Grok)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();

        // The constructor's own FreshSettings() already gave this instance an
        // isolated, empty settings dir — no extra dir setup needed here, only
        // the two profiles this scenario is actually about.
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-work");
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-board");

        var window = NewWindow();

        ScreenshotHelper.Capture(window, "new-chat-window-account-picker.png");
    }

    // CB-207: the same two profiles, but Codex selected. Codex has no extra
    // homes of its own here, so its picker is hidden (CB-201, and CB-203's
    // "Default alone means no picker") while its space is kept, so switching CLIs never resizes the
    // window on screen. This is the one state where that reserved space is
    // visible, and it should read as the same window as the capture above,
    // at the same height.
    [AvaloniaFact]
    public void AccountSpaceKeptForCodex()
    {
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex), Enabled(NewChatCli.Grok)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-work");
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-board");

        var window = NewWindow(prefillCli: NewChatCli.Codex);

        ScreenshotHelper.Capture(window, "new-chat-window-account-space-kept.png");
    }

    // CB-203: Codex selected with one extra CODEX_HOME. The picker offers
    // Codex's own list — "Default (~/.codex)" and the extra — never the two
    // Claude Code profiles also configured here, which is the whole point of
    // capturing it beside AccountPickerWithTwoProfiles. Settings are the
    // constructor's isolated FreshSettings() dir, so nothing here leaks into
    // the next scenario (CB-201's review flag).
    [AvaloniaFact]
    public void CodexAccountPicker()
    {
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex), Enabled(NewChatCli.Grok)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-work");
        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-board");
        OrbweaverSettings.AddCodexHome(".codex-work");
        OrbweaverSettings.SetNewChatLastProfile(NewChatCli.Codex, ".codex-work");

        var window = NewWindow(prefillCli: NewChatCli.Codex);

        ScreenshotHelper.Capture(window, "new-chat-codex-accounts.png");
    }

    // CB-203's missing-home warning: Codex with an extra CODEX_HOME whose
    // directory does not exist. Codex refuses to start there, so the line
    // under the picker says so before Start is pressed rather than leaving a
    // terminal that flashes and closes.
    [AvaloniaFact]
    public void CodexAccountWithMissingHome()
    {
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex), Enabled(NewChatCli.Grok)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatWindow.AccountDirectoryExistsForTests = _ => false;
        OrbweaverSettings.AddCodexHome(".codex-work");
        OrbweaverSettings.SetNewChatLastProfile(NewChatCli.Codex, ".codex-work");

        var window = NewWindow(prefillCli: NewChatCli.Codex);

        ScreenshotHelper.Capture(window, "new-chat-codex-missing-home.png");
    }

    // The OpenClaw row selected, Ready, with agents loaded — the agent
    // section replacing the folder section is CB-168's own decision record
    // ("OpenClaw selected: an agent picker replaces the folder field"), and
    // this is the only scenario that shows it actually happening.
    [AvaloniaFact]
    public void OpenClawRowEnabledWithAgents()
    {
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex), Enabled(NewChatCli.Grok)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => new[]
        {
            ("agent-1", "Alexis"), ("agent-2", "Amber"), ("agent-3", "Hope")
        };
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();

        var window = NewWindow();

        var openClawRow = window.CliList.Children.OfType<RadioButton>()
            .Single(r => (string)r.Content! == "OpenClaw");
        window.Show();
        ScreenshotHelper.Flush();
        openClawRow.IsChecked = true;
        ScreenshotHelper.Flush();

        ScreenshotHelper.CaptureAlreadyShown(window, "new-chat-window-openclaw-enabled.png");
    }

    // OpenClaw disabled — no gateway configured, the state a fresh install
    // actually has. The row itself (greyed, unselectable) is what this
    // captures; the reason text is a tooltip, which only a real hover shows
    // and which NewChatWindowTests already asserts directly
    // (TheOpenClawRowIsDisabledWithItsReasonWhenThereIsNoGateway) — a static capture
    // can't demonstrate a hover state any more usefully than describing it.
    [AvaloniaFact]
    public void OpenClawRowDisabledWithReason()
    {
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex), Enabled(NewChatCli.Grok)
        };
        // NoGateway since CB-329: that is the one state that still disables the
        // row. ReplyDisabled keeps it live for Open existing, and has its own
        // capture below.
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();

        var window = NewWindow();

        ScreenshotHelper.Capture(window, "new-chat-window-openclaw-disabled.png");
    }

    // --- CB-329: Open existing ------------------------------------------

    // A fixed clock so "2 h ago" reads the same in every capture, and rows
    // anchored to it. Three shapes, one of each the picker has to tell apart:
    // a DM, a channel room (two agents wearing one row) and an agent's Main.
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    private static OpenClawReopenRow[] PickerRows() => new[]
    {
        new OpenClawReopenRow(OpenClawReopenTarget.Session, "openclaw:agent:aurora:discord:direct:1",
            "wtvamp", "wtvamp", SessionKind.Direct, OpenClawNativeKind.None, false,
            new[] { "aurora" }, new[] { "agent:aurora:discord:direct:1" }, Now.AddHours(-2)),
        new OpenClawReopenRow(OpenClawReopenTarget.Room, "openclaw:room:discord:900",
            "#general", "#general", SessionKind.Channel, OpenClawNativeKind.None, false,
            new[] { "aurora", "nova" },
            new[] { "agent:aurora:discord:channel:900", "agent:nova:discord:channel:900" }, Now.AddHours(-24)),
        new OpenClawReopenRow(OpenClawReopenTarget.Session, "openclaw:agent:nova:main",
            "Nova", "Nova", SessionKind.Main, OpenClawNativeKind.None, false,
            new[] { "nova" }, new[] { "agent:nova:main" }, Now.AddDays(-3))
    };

    private static NewChatWindow OpenExistingWindow(
        OpenClawNewChatAvailability availability, Func<IReadOnlyList<OpenClawReopenRow>?> rows)
    {
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex), Enabled(NewChatCli.Grok)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => availability;
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatWindow.KnownAgentsForTests = () => new[] { ("aurora", "Aurora"), ("nova", "Nova") };
        NewChatWindow.NowForTests = () => Now;
        NewChatWindow.ReopenRowsForTests = rows;

        var window = NewWindow();
        window.CliList.Children.OfType<RadioButton>()
            .Single(r => Equals(r.Tag, NewChatWindow.OpenClawTag)).IsChecked = true;
        window.OpenExistingRadio.IsChecked = true;
        return window;
    }

    // The picker with a DM, a channel room and a Main row, and the first row
    // chosen so the selection colour is on the capture too.
    [AvaloniaFact]
    public void NewChatOpenExistingPicker()
    {
        var window = OpenExistingWindow(OpenClawNewChatAvailability.Ready, PickerRows);
        window.ExistingList.SelectedIndex = 0;

        ScreenshotHelper.Capture(window, "new-chat-window-open-existing.png");

        Assert.True(window.ExistingList.IsVisible);
        Assert.Equal(3, window.ExistingList.ItemCount);
    }

    // Before the first poll has completed: "Loading conversations…", not
    // "No conversations".
    [AvaloniaFact]
    public void NewChatOpenExistingLoading()
    {
        var window = OpenExistingWindow(OpenClawNewChatAvailability.Ready, () => null);

        ScreenshotHelper.Capture(window, "new-chat-window-open-existing-loading.png");

        Assert.Equal("Loading conversations…", window.ExistingEmpty.Text);
    }

    // A completed poll that listed nothing.
    [AvaloniaFact]
    public void NewChatOpenExistingEmpty()
    {
        var window = OpenExistingWindow(
            OpenClawNewChatAvailability.Ready, () => Array.Empty<OpenClawReopenRow>());

        ScreenshotHelper.Capture(window, "new-chat-window-open-existing-empty.png");

        Assert.Equal("No conversations", window.ExistingEmpty.Text);
    }

    // The gateway never answered inside the budget: the headline and the
    // known failure, in the list's own cell.
    [AvaloniaFact]
    public void NewChatOpenExistingUnreachable()
    {
        var now = Now;
        var window = OpenExistingWindow(OpenClawNewChatAvailability.Ready, () => null);
        NewChatWindow.NowForTests = () => now.AddSeconds(30);
        NewChatWindow.LastConnectFailureForTests = () => null;
        window.OnReopenLoadTick(null, EventArgs.Empty);

        ScreenshotHelper.Capture(window, "new-chat-window-open-existing-unreachable.png");

        Assert.Equal("Can't reach the OpenClaw gateway", window.ExistingEmpty.Text);
    }

    // The reply switch off: Open existing is the one live mode, with the
    // reason sitting under the disabled "New conversation".
    [AvaloniaFact]
    public void NewChatOpenExistingReplyDisabled()
    {
        var window = OpenExistingWindow(OpenClawNewChatAvailability.ReplyDisabled, PickerRows);

        ScreenshotHelper.Capture(window, "new-chat-window-open-existing-reply-off.png");

        Assert.False(window.NewConversationRadio.IsEnabled);
        Assert.True(window.OpenExistingRadio.IsChecked);
    }

    private static void ClearSeams()
    {
        NewChatAvailability.CurrentForTests = null;
        NewChatLauncher.LaunchForTests = null;
        NewChatWindow.CurrentStatusesForTests = null;
        NewChatWindow.ChooseFolderForTests = null;
        NewChatWindow.OpenClawAvailabilityForTests = null;
        NewChatWindow.KnownAgentsForTests = null;
        NewChatWindow.StartOpenClawConversationForTests = null;
        NewChatWindow.OrbForTests = null;
        NewChatWindow.AccountDirectoryExistsForTests = null;
        NewChatWindow.ReopenRowsForTests = null;
        NewChatWindow.ReopenOpenClawForTests = null;
        NewChatWindow.NowForTests = null;
        NewChatWindow.LastConnectFailureForTests = null;
    }
}
