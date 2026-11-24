using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    // CB-361: Resume mode — a running session, a recent one, and one whose
    // folder is gone (greyed), the first chosen so the selection colour is on
    // the capture too.
    [AvaloniaFact]
    public void NewChatResumePicker()
    {
        var folder = Path.GetTempPath().TrimEnd('/', '\\');
        PastSession Row(string id, string title, double hoursAgo, int messages, bool missing = false) =>
            new(NewChatCli.ClaudeCode, id, missing ? "/no/such/project" : folder, title,
                new DateTimeOffset(Now.AddHours(-hoursAgo), TimeSpan.Zero), messages, "/t/" + id + ".jsonl", missing);

        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex), Enabled(NewChatCli.Grok)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => new[] { ("aurora", "Aurora") };
        NewChatWindow.NowForTests = () => Now;
        NewChatWindow.ResumeLoadInlineForTests = true;
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>
        {
            ["live-1"] = new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = folder }
        };
        PastSessionIndex.LoadForTests = (_, _, _, _) => new[]
        {
            Row("live-1", "Wire up the hook installer", 0.2, 42),
            Row("b", "Why does the arrangement overlap?", 5, 118),
            Row("gone", "Old prototype", 72, 9, missing: true)
        };

        var window = NewWindow();
        window.LocalResumeRadio.IsChecked = true;
        window.ResumeList.SelectedIndex = 1;

        ScreenshotHelper.Capture(window, "new-chat-window-resume.png");

        Assert.True(window.ResumePane.IsVisible);
        Assert.Equal(3, window.ResumeList.ItemCount);
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

    // Fluent's ListBoxItem drew rows about 54 dip tall on Windows, so the
    // fixed-height list showed two and a half and the third row was cut off.
    // This suite is the one that shows windows (the headless UiTests never
    // apply control templates to a window that is not shown), and CI runs it
    // on both rids, so each theme checks itself: every realized row is exactly
    // ExistingRowHeight, and three rows fit without scrolling.
    [AvaloniaFact]
    public void EveryRealizedRowIsTheFixedHeightAndThreeFitWithoutScrolling()
    {
        var window = OpenExistingWindow(OpenClawNewChatAvailability.Ready, PickerRows);
        window.Show();
        ScreenshotHelper.Flush();

        var items = window.ExistingList.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.Equal(NewChatWindow.ExistingRowHeight, i.Bounds.Height));

        var scroll = window.ExistingList.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.True(scroll.Extent.Height <= scroll.Viewport.Height,
            $"extent {scroll.Extent.Height} > viewport {scroll.Viewport.Height}");
    }

    // Control: with a fourth row the same list does scroll, so the check above can fail.
    [AvaloniaFact]
    public void AFourthRowScrollsTheList()
    {
        var four = PickerRows().Append(PickerRows()[0] with { OrbSessionId = "openclaw:agent:x:4", Title = "fourth" }).ToArray();
        var window = OpenExistingWindow(OpenClawNewChatAvailability.Ready, () => four);
        window.Show();
        ScreenshotHelper.Flush();

        var scroll = window.ExistingList.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
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

    // CB-357: the Machine row. A fake launcher answers at once, so each capture
    // is the form after the far machine's launch-info has been applied.
    private sealed class FakePeers : IPeerLauncher
    {
        public PeerLaunchResult Result = new(true, "launched", "s1", null);

        public IReadOnlyList<PeerMachine> Machines() =>
            new[] { new PeerMachine("mini", true), new PeerMachine("Warren's PC", true), new PeerMachine("studio-imac", false) };

        public Task<PeerLaunchInfo?> InfoAsync(string machine, CancellationToken ct) =>
            Task.FromResult<PeerLaunchInfo?>(new PeerLaunchInfo(true, "macos", "/Users/warren", true,
                new[] { new PeerLaunchCli("claude", true), new PeerLaunchCli("codex", true), new PeerLaunchCli("grok", false) },
                new[] { new PeerLaunchAccount("claude", "default", "Default"), new PeerLaunchAccount("claude", "/Users/warren/.claude-work", "~/.claude-work") },
                new[] { "/Users/warren/Source/ledger", "/Users/warren/Source/ledger-web" }));

        public Task<PeerLaunchResult> LaunchAsync(string machine, PeerLaunchSpec spec, CancellationToken ct) =>
            Task.FromResult(Result);

        // CB-397: the far machine's trust question and the window's answer to it.
        public PeerLaunchResult TrustResult = new(true, "launched", "s1", null);
        public TaskCompletionSource<PeerLaunchResult>? PendingTrust;

        public Task<PeerLaunchResult> TrustAsync(string machine, PeerLaunchTrust trust, bool trusted, CancellationToken ct) =>
            PendingTrust?.Task ?? Task.FromResult(TrustResult);

        // CB-357 (reopened): a far machine's folders, one level under its root.
        public Task<PeerDirResult> ListDirAsync(string machine, string path, CancellationToken ct) =>
            Task.FromResult(path == "/Users/forbidden" ? PeerDirResult.Fail("forbidden_path")
                : path.Length == 0
                ? new PeerDirResult(true, "ok", new PeerDirListing("", "", false,
                    [new PeerDirItem("warren", "/Users/warren", null, true), new PeerDirItem("Source", "/Users/warren/Source")]))
                : new PeerDirResult(true, "ok", new PeerDirListing(path, "/Users/warren", false,
                    [new PeerDirItem("ledger"), new PeerDirItem("ledger-web"), new PeerDirItem("orbweaver"), new PeerDirItem("scratch")])));
    }

    // CB-434: a launcher that can also list a far machine's past sessions.
    private sealed class FakePeersWithSessions : IPeerLauncher, IRemotePastSessions
    {
        private readonly FakePeers _inner = new();
        public IReadOnlyList<PastSessionRow> Rows = Array.Empty<PastSessionRow>();
        public IReadOnlyList<PeerMachine> Machines() => _inner.Machines();
        public async Task<PeerLaunchInfo?> InfoAsync(string machine, CancellationToken ct) =>
            (await _inner.InfoAsync(machine, ct))! with { Resume = 1 };
        public Task<PeerLaunchResult> LaunchAsync(string machine, PeerLaunchSpec spec, CancellationToken ct) => _inner.LaunchAsync(machine, spec, ct);
        public Task<PeerDirResult> ListDirAsync(string machine, string path, CancellationToken ct) => _inner.ListDirAsync(machine, path, ct);
        public Task<RemoteListResult> ListAsync(string machine, NewChatCli cli, string profile, bool showOlder, CancellationToken ct) =>
            Task.FromResult(new RemoteListResult(RemoteListOutcome.Ok, Rows, null));
        public Task<RemoteResumeResult> ResumeAsync(string machine, NewChatCli cli, string sessionId, string cwd, string profile, CancellationToken ct) =>
            Task.FromResult(new RemoteResumeResult(RemoteResumeOutcome.Launched, null, false));
    }

    // CB-434: Resume with a remote machine selected — its own sessions, one
    // running there, one whose folder is gone, the first chosen.
    [AvaloniaFact]
    public void NewChatResumePickerOnARemoteMachine()
    {
        PastSessionRow Row(string id, string title, double hoursAgo, int messages, bool live = false, bool missing = false) =>
            new(new PastSession(NewChatCli.ClaudeCode, id, missing ? "/Users/warren/Source/old-proto" : "/Users/warren/Source/ledger",
                title, new DateTimeOffset(Now.AddHours(-hoursAgo), TimeSpan.Zero), messages, "", missing), live);

        var peers = new FakePeersWithSessions
        {
            Rows = new[]
            {
                Row("live-1", "Reconcile the March ledger", 0.3, 64, live: true),
                Row("b", "Why is the export off by a cent?", 6, 31),
                Row("gone", "Old prototype", 72, 9, missing: true)
            }
        };
        NewChatWindow.NowForTests = () => Now;
        NewChatWindow.ResumeLoadInlineForTests = true;
        NewChatWindow.PeerLauncherForTests = peers;
        NewChatWindow.RemotePastForTests = peers;
        NewChatWindow.PeerLinkOnForTests = true;
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex), Enabled(NewChatCli.Grok)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();

        var window = NewWindow();
        window.MachineCombo.SelectedIndex = 1;
        window.LocalResumeRadio.IsChecked = true;
        window.ResumeList.SelectedIndex = 1;

        ScreenshotHelper.Capture(window, "new-chat-window-resume-remote.png");

        Assert.True(window.ResumePane.IsVisible);
        Assert.Equal(3, window.ResumeList.ItemCount);
    }

    private static NewChatWindow PeerWindow(FakePeers peers)
    {
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex), Enabled(NewChatCli.Grok)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatWindow.PeerLauncherForTests = peers;
        NewChatWindow.PeerLinkOnForTests = true;
        return NewWindow();
    }

    // CB-357 (reopened): the remote folder browser, one level into the far
    // machine's Source folder, with the Choose button live.
    [AvaloniaFact]
    public async Task RemoteFolderBrowserInsideAFolder()
    {
        var browser = new RemoteFolderBrowser(new FakePeers(), "mini");
        browser.Show();
        await browser.NavigateAsync("/Users/warren/Source");
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        ScreenshotHelper.Capture(browser, "new-chat-remote-folder-browser.png");

        Assert.True(browser.ChooseButton.IsEnabled);
        Assert.Equal("/Users/warren/Source", browser.Chosen);
        browser.Close();
    }

    // The same browser after the far desk refused a folder.
    [AvaloniaFact]
    public async Task RemoteFolderBrowserRefused()
    {
        var browser = new RemoteFolderBrowser(new FakePeers(), "mini");
        browser.Show();
        await browser.NavigateAsync("");
        await browser.NavigateAsync("/Users/forbidden");
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        ScreenshotHelper.Capture(browser, "new-chat-remote-folder-browser-refused.png");
        browser.Close();
    }

    // A remote machine selected: its recents, its accounts, its CLIs (Grok
    // not installed there), OpenClaw greyed with the reason in the note line.
    [AvaloniaFact]
    public void MachineRowWithARemoteMachineSelected()
    {
        var window = PeerWindow(new FakePeers());
        window.Show();
        ScreenshotHelper.Flush();
        window.MachineCombo.SelectedIndex = 1;
        ScreenshotHelper.Flush();

        ScreenshotHelper.CaptureAlreadyShown(window, "new-chat-window-machine-remote.png");
    }

    // The offline peer listed: the dropdown open over the form, so the greyed
    // "studio-imac — offline" entry is on screen.
    [AvaloniaFact]
    public void MachineRowWithAnOfflinePeerListed()
    {
        var window = PeerWindow(new FakePeers());
        window.Show();
        ScreenshotHelper.Flush();
        window.MachineCombo.IsDropDownOpen = true;
        ScreenshotHelper.Flush();

        ScreenshotHelper.CaptureAlreadyShown(window, "new-chat-window-machine-offline-peer.png");
    }

    // The far machine's refusal, inline in the status line.
    [AvaloniaFact]
    public void MachineRowWithARefusalShown()
    {
        var peers = new FakePeers { Result = new PeerLaunchResult(false, PeerLaunchCodes.PeerLaunchOff, null, null) };
        var window = PeerWindow(peers);
        window.Show();
        ScreenshotHelper.Flush();
        window.MachineCombo.SelectedIndex = 1;
        ScreenshotHelper.Flush();
        window.StartButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        ScreenshotHelper.Flush();

        ScreenshotHelper.CaptureAlreadyShown(window, "new-chat-window-machine-refused.png");
    }

    // CB-397: the far machine is asking whether to trust the folder; Start sends the
    // question's own folder, so the panel's text is what a person would read.
    private static NewChatWindow AskedWindow(FakePeers peers, bool hooks = false)
    {
        peers.Result = new PeerLaunchResult(false, PeerLaunchCodes.TrustNeeded, null, null,
            new PeerLaunchTrust("TOKEN", "claude", "/Users/warren/Source/ledger", 1_800_000_000, hooks));
        var window = PeerWindow(peers);
        window.Show();
        ScreenshotHelper.Flush();
        window.MachineCombo.SelectedIndex = 1;
        ScreenshotHelper.Flush();
        window.StartButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        ScreenshotHelper.Flush();
        return window;
    }

    private static void ClickTrust(Button b)
    {
        b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        ScreenshotHelper.Flush();
    }

    [AvaloniaFact]
    public void TrustQuestionAsking()
    {
        var window = AskedWindow(new FakePeers());

        ScreenshotHelper.CaptureAlreadyShown(window, "new-chat-window-trust-asking.png");

        Assert.True(window.TrustPanel.IsVisible);
        Assert.Equal("Trust this folder on mini?", window.TrustTitle.Text);
    }

    // The far screen carried Claude Code's own hooks warning: the body gains one line.
    [AvaloniaFact]
    public void TrustQuestionAskingWithAHooksWarning()
    {
        var window = AskedWindow(new FakePeers(), hooks: true);

        ScreenshotHelper.CaptureAlreadyShown(window, "new-chat-window-trust-asking-hooks.png");

        Assert.True(window.TrustHooks.IsVisible);
    }

    [AvaloniaFact]
    public void TrustQuestionTrusting()
    {
        var window = AskedWindow(new FakePeers { PendingTrust = new() });
        ClickTrust(window.TrustYes);

        ScreenshotHelper.CaptureAlreadyShown(window, "new-chat-window-trust-trusting.png");

        Assert.Equal("Trusting it on mini…", window.StatusLine.Text);
        Assert.False(window.TrustYes.IsEnabled);
    }

    [AvaloniaFact]
    public void TrustQuestionCancelled()
    {
        var window = AskedWindow(new FakePeers { TrustResult = new PeerLaunchResult(false, PeerLaunchCodes.TrustCancelled, null, null) });
        ClickTrust(window.TrustNo);

        ScreenshotHelper.CaptureAlreadyShown(window, "new-chat-window-trust-cancelled.png");

        Assert.Equal("Cancelled. Nothing was trusted, and mini closed the session.", window.StatusLine.Text);
        Assert.False(window.TrustPanel.IsVisible);
    }

    [AvaloniaFact]
    public void TrustQuestionExpired()
    {
        var wait = new TaskCompletionSource();
        NewChatWindow.TrustWaitForTests = _ => wait.Task;
        var window = AskedWindow(new FakePeers());
        wait.SetResult();
        ScreenshotHelper.Flush();

        ScreenshotHelper.CaptureAlreadyShown(window, "new-chat-window-trust-expired.png");

        Assert.Equal("Nobody answered in time, so mini closed the session. Nothing was trusted.", window.StatusLine.Text);
        Assert.False(window.TrustPanel.IsVisible);
    }

    private static void ClearSeams()
    {
        NewChatWindow.TrustWaitForTests = null;
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
        NewChatWindow.PeerLauncherForTests = null;
        NewChatWindow.RemotePastForTests = null;
        NewChatWindow.PeerLinkOnForTests = null;
        NewChatWindow.ResumeLoadInlineForTests = false;
        PastSessionIndex.LoadForTests = null;
    }
}
