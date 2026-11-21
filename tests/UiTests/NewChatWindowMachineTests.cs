using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-357: the Machine row of the New chat window, driven through a fake
// IPeerLauncher. The window is never shown (a headless Show hangs this
// suite, see HotkeyActionsTests); the content's measured height is what
// SizeToContent follows, so that is what the CB-207 assertions read.
[Collection("Settings")]
public class NewChatWindowMachineTests : IDisposable
{
    public void Dispose()
    {
        NewChatAvailability.CurrentForTests = null;
        NewChatWindow.OpenClawAvailabilityForTests = null;
        NewChatWindow.KnownAgentsForTests = null;
        NewChatWindow.CurrentStatusesForTests = null;
        NewChatWindow.AccountDirectoryExistsForTests = null;
        NewChatWindow.PeerLauncherForTests = null;
        NewChatWindow.PeerLinkOnForTests = null;
        NewChatWindow.TrustWaitForTests = null;
    }

    internal sealed class FakeLauncher : IPeerLauncher
    {
        public List<PeerMachine> Peers = new() { new("mini", true), new("pc", true), new("asleep", false) };
        public Dictionary<string, TaskCompletionSource<PeerLaunchInfo?>> Pending = new();
        public Dictionary<string, PeerLaunchInfo?> Answers = new();
        public PeerLaunchResult Result = new(true, "launched", "s1", null);
        public TaskCompletionSource<PeerLaunchResult>? PendingLaunch;
        public Exception? ThrowInfo, ThrowLaunch;
        public bool IgnoreInfoToken;
        public List<(string machine, PeerLaunchSpec spec)> Launches = new();

        // CB-397: the far desk's question and what the window answers it with.
        public List<(string machine, PeerLaunchTrust trust, bool trusted)> Trusts = new();
        public PeerLaunchResult TrustResult = new(true, "launched", "s1", null);
        public TaskCompletionSource<PeerLaunchResult>? PendingTrust;
        public Exception? ThrowTrust;

        public Task<PeerLaunchResult> TrustAsync(string machine, PeerLaunchTrust trust, bool trusted, CancellationToken ct)
        {
            Trusts.Add((machine, trust, trusted));
            if (ThrowTrust is { } boom) throw boom;
            return PendingTrust?.Task ?? Task.FromResult(TrustResult);
        }

        public IReadOnlyList<PeerMachine> Machines() => Peers;

        public Task<PeerLaunchInfo?> InfoAsync(string machine, CancellationToken ct)
        {
            if (ThrowInfo is { } boom) throw boom;
            if (Pending.TryGetValue(machine, out var tcs))
            {
                if (!IgnoreInfoToken) ct.Register(() => tcs.TrySetCanceled(ct));
                return tcs.Task;
            }
            return Task.FromResult(Answers.GetValueOrDefault(machine));
        }

        public Dictionary<(string machine, string path), PeerDirResult> Dirs = new();
        public List<(string machine, string path)> Listed = new();

        public Task<PeerDirResult> ListDirAsync(string machine, string path, CancellationToken ct)
        {
            Listed.Add((machine, path));
            return Task.FromResult(Dirs.TryGetValue((machine, path), out var r) ? r : PeerDirResult.Fail(PeerLaunchCodes.NoAnswer));
        }

        public Task<PeerLaunchResult> LaunchAsync(string machine, PeerLaunchSpec spec, CancellationToken ct)
        {
            Launches.Add((machine, spec));
            if (PendingLaunch is { } tcs)
            {
                return tcs.Task;
            }
            if (ThrowLaunch is { } boom) throw boom;
            return Task.FromResult(Result);
        }
    }

    internal static PeerLaunchInfo Info(string[]? installed = null, string[]? recents = null, string? home = "/Users/w",
        bool terminal = true, bool allowed = true) =>
        new(allowed, "macos", home, terminal,
            new[] { "claude", "codex", "grok" }.Select(c => new PeerLaunchCli(c, (installed ?? ["claude"]).Contains(c))).ToList(),
            [new("claude", "default", "Default"), new("claude", "/p/work", "Work"), new("codex", "default", "Default")],
            (recents ?? ["/srv/app", "/srv/lib"]).ToList());

    private static void FreshSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-newchat-machine-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    private static NewChatOption Enabled(NewChatCli cli) => new(cli, Enabled: true, Reason: null, Warning: null);

    private static NewChatWindow Make(FakeLauncher? fake, bool linkOn = true)
    {
        FreshSettings();
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex), Enabled(NewChatCli.Grok)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => new[] { ("id-1", "Alexis") };
        NewChatWindow.AccountDirectoryExistsForTests = _ => true;
        NewChatWindow.PeerLauncherForTests = fake;
        NewChatWindow.PeerLinkOnForTests = linkOn;

        var ctor = typeof(NewChatWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: new[] { typeof(NewChatCli?), typeof(string), typeof(string) })!;
        return (NewChatWindow)ctor.Invoke(new object?[] { null, null, null });
    }

    private static NewChatWindow RebuildWith(FakeLauncher f)
    {
        NewChatWindow.PeerLauncherForTests = f;
        var ctor = typeof(NewChatWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: new[] { typeof(NewChatCli?), typeof(string), typeof(string) })!;
        return (NewChatWindow)ctor.Invoke(new object?[] { null, null, null });
    }

    private static FakeLauncher Ready()
    {
        var f = new FakeLauncher();
        f.Answers["mini"] = Info();
        f.Answers["pc"] = Info(installed: ["claude", "codex"], recents: []);
        return f;
    }

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Pick(NewChatWindow w, int index)
    {
        w.MachineCombo.SelectedIndex = index;
        Flush();
    }

    private static void Click(Button b)
    {
        b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Flush();
    }

    private static void Select(NewChatWindow w, object tag) => Radio(w, tag).IsChecked = true;

    private static RadioButton Radio(NewChatWindow w, object tag) =>
        w.CliList.Children.OfType<RadioButton>().Single(r => Equals(r.Tag, tag));

    private static double Height(NewChatWindow w)
    {
        Flush();
        var content = (Control)w.Content!;
        content.InvalidateMeasure();
        foreach (var c in content.GetLogicalDescendants().OfType<Layoutable>()) c.InvalidateMeasure();
        content.Measure(new Size(420, double.PositiveInfinity));
        return content.DesiredSize.Height;
    }

    private static string[] Labels(NewChatWindow w) =>
        w.MachineCombo.Items.OfType<ComboBoxItem>().Select(i => (string)i.Content!).ToArray();

    [AvaloniaFact]
    public void NoRowWithoutALauncherPeersOrTheLink()
    {
        Assert.Null(Make(null).MachineSection);
        Assert.Null(Make(new FakeLauncher { Peers = [] }).MachineSection);
        Assert.Null(Make(Ready(), linkOn: false).MachineSection);
    }

    [AvaloniaFact]
    public void TheRowListsThisMachineThenPeersAndGreysOutAnOfflineOne()
    {
        var w = Make(Ready());
        Assert.NotNull(w.MachineSection);
        Assert.Equal([OperatingSystem.IsWindows() ? "This PC" : "This Mac", "mini", "pc", "asleep — offline"], Labels(w));
        var items = w.MachineCombo.Items.OfType<ComboBoxItem>().ToList();
        Assert.True(items[1].IsEnabled);
        Assert.False(items[3].IsEnabled);
        Assert.Equal(0, w.MachineCombo.SelectedIndex);
    }

    [AvaloniaFact]
    public void AnOfflinePeerCannotBeSelected()
    {
        var f = Ready();
        var w = Make(f);
        Pick(w, 1);
        Pick(w, 3);
        Assert.Equal(1, w.MachineCombo.SelectedIndex); // bounced back to where it was
        Assert.Equal("mini", ((NewChatMachines.Entry)((ComboBoxItem)w.MachineCombo.SelectedItem!).Tag!).Name);
        w.MachineCombo.SelectedIndex = -1;            // an empty selection is ignored
        Flush();
    }

    [AvaloniaFact]
    public void ChoosingARemoteMachineSwapsFoldersAccountsAndClisThenBackRestoresTheLocalForm()
    {
        var w = Make(Ready());
        var localFolders = (w.FolderCombo.ItemsSource as IEnumerable<string>)!.ToList();
        Select(w, NewChatCli.Codex);

        Pick(w, 1); // mini: claude only
        Assert.True(Radio(w, NewChatCli.ClaudeCode).IsEnabled);
        Assert.False(Radio(w, NewChatCli.Codex).IsEnabled);
        Assert.False(Radio(w, NewChatCli.Grok).IsEnabled);
        Assert.False(Radio(w, NewChatWindow.OpenClawTag).IsEnabled);
        Assert.Equal(NewChatCli.ClaudeCode, w.SelectedCli);
        Assert.False(w.OpenClawSelected);
        Assert.Equal(["/srv/app", "/srv/lib"], (w.FolderCombo.ItemsSource as IEnumerable<string>)!.ToArray());
        Assert.True(w.BrowseButton.IsEnabled);
        Assert.True(w.AccountSection.IsVisible);
        Assert.Equal(["Default", "Work"], (w.AccountCombo.ItemsSource as IEnumerable<NewChatAccounts.Choice>)!.Select(c => c.Label).ToArray());
        Assert.Equal("Codex isn't installed on mini. Grok isn't installed on mini. " + PeerLaunchCopy.OpenClawLocalOnly, w.MachineNote.Text);
        Assert.Equal("Grok isn't installed on mini.", ToolTip.GetTip(Radio(w, NewChatCli.Grok)));
        Assert.Null(ToolTip.GetTip(Radio(w, NewChatCli.ClaudeCode)));
        Assert.Equal(PeerLaunchCopy.OpenClawLocalOnly, ToolTip.GetTip(Radio(w, NewChatWindow.OpenClawTag)));
        Assert.True(w.StartButton.IsEnabled);

        Pick(w, 0);
        Assert.Null(ToolTip.GetTip(Radio(w, NewChatCli.Grok)));
        Assert.Null(ToolTip.GetTip(Radio(w, NewChatWindow.OpenClawTag)));
        Assert.True(Radio(w, NewChatCli.Codex).IsEnabled);
        Assert.True(Radio(w, NewChatWindow.OpenClawTag).IsEnabled);
        Assert.Equal(NewChatCli.Codex, w.SelectedCli);
        Assert.Equal(localFolders, (w.FolderCombo.ItemsSource as IEnumerable<string>)!.ToList());
        Assert.True(w.BrowseButton.IsEnabled);
        Assert.Null(w.MachineNote.Text);
    }

    [AvaloniaFact]
    public void ReturningToThisMachineRestoresAnOpenClawSelection()
    {
        var w = Make(Ready());
        Select(w, NewChatWindow.OpenClawTag);
        Pick(w, 1);
        Assert.False(w.OpenClawSelected);
        Pick(w, 0);
        Assert.True(w.OpenClawSelected);
    }

    [AvaloniaFact]
    public void ARemoteMachineWithNoRecentsOffersItsHomeFolder()
    {
        var w = Make(Ready());
        Pick(w, 2); // pc: no recents
        Assert.Equal(["/Users/w"], (w.FolderCombo.ItemsSource as IEnumerable<string>)!.ToArray());
        Assert.Equal("/Users/w", w.FolderCombo.SelectedItem);
        Assert.True(Radio(w, NewChatCli.Codex).IsEnabled);
    }

    [AvaloniaFact]
    public void TheNoteSaysAskingWhileWaitingThenClears()
    {
        var f = Ready();
        var tcs = f.Pending["mini"] = new();
        var w = Make(f);
        Pick(w, 1);
        Assert.Equal("Asking mini…", w.MachineNote.Text);
        Assert.False(w.StartButton.IsEnabled);
        Assert.Empty(w.FolderCombo.ItemsSource as IEnumerable<string> ?? []);

        tcs.SetResult(Info());
        Flush();
        Assert.True(w.StartButton.IsEnabled);
        Assert.EndsWith(PeerLaunchCopy.OpenClawLocalOnly, w.MachineNote.Text);
    }

    [AvaloniaTheory]
    [InlineData("none", "didn't answer")]
    [InlineData("off", "doesn't allow other desktops")]
    [InlineData("noterm", "No terminal to start it in")]
    [InlineData("nocli", "No supported CLI")]
    public void AMachineThatCannotBeLaunchedOnDisablesStartAndSaysWhy(string kind, string expected)
    {
        var f = Ready();
        f.Answers["mini"] = kind switch
        {
            "none" => null,
            "off" => Info(allowed: false),
            "noterm" => Info(terminal: false),
            _ => Info(installed: []),
        };
        var w = Make(f);
        Pick(w, 1);
        Assert.False(w.StartButton.IsEnabled);
        Assert.Contains(expected, w.MachineNote.Text);
        Assert.Null(w.SelectedCli);

        // Start is off, but a stray click must still say why rather than launch.
        Click(w.StartButton);
        Assert.Contains(expected, w.StatusLine.Text);
        Assert.Empty(f.Launches);
    }

    [AvaloniaFact]
    public void AnAnswerThatArrivesAfterTheUserMovedOnIsIgnored()
    {
        var f = Ready();
        var slow = f.Pending["mini"] = new();
        var w = Make(f);
        Pick(w, 1);
        Pick(w, 2); // pc answers at once
        Assert.Equal(["/Users/w"], (w.FolderCombo.ItemsSource as IEnumerable<string>)!.ToArray());

        slow.TrySetResult(Info()); // already cancelled by the second pick
        Flush();
        Assert.Equal(["/Users/w"], (w.FolderCombo.ItemsSource as IEnumerable<string>)!.ToArray());
    }

    // A client that ignores the token still answers late; only the window's
    // own generation check stops that answer repainting the form.
    [AvaloniaFact]
    public void AnAnswerFromAClientThatIgnoresCancellationIsStillIgnored()
    {
        var f = Ready();
        f.IgnoreInfoToken = true;
        var slow = f.Pending["mini"] = new();
        var w = Make(f);
        Pick(w, 1);
        Pick(w, 2);
        slow.SetResult(Info());
        Flush();
        Assert.Equal(["/Users/w"], (w.FolderCombo.ItemsSource as IEnumerable<string>)!.ToArray());
    }

    [AvaloniaFact]
    public void AChosenCliTheFarMachineLacksMovesToTheFirstOneItHasAndAnInstalledOneStays()
    {
        var w = Make(Ready());
        Select(w, NewChatCli.Grok);
        Pick(w, 1); // mini: claude only
        Assert.Equal(NewChatCli.ClaudeCode, w.SelectedCli);
        Assert.True(Radio(w, NewChatCli.ClaudeCode).IsChecked);

        Pick(w, 0);
        Select(w, NewChatCli.Codex);
        Pick(w, 2); // pc: claude and codex
        Assert.Equal(NewChatCli.Codex, w.SelectedCli); // control: installed there, so it stays
    }

    [AvaloniaFact]
    public void ACliWithNoAccountsOnTheFarMachineLeavesTheAccountPickerEmpty()
    {
        var f = Ready();
        f.Answers["mini"] = Info(installed: ["claude", "grok"]);
        var w = Make(f);
        Pick(w, 1);
        Radio(w, NewChatCli.Grok).IsChecked = true;
        Flush();
        Assert.Equal(-1, w.AccountCombo.SelectedIndex);
        Assert.False(w.AccountSection.IsVisible);
        Click(w.StartButton);
        Assert.Equal("default", f.Launches.Single().spec.Account);
    }

    // A machine with no usable CLI of its own and no OpenClaw has nothing
    // selected, so on the way back there is no saved radio to restore and the
    // restore falls through to "the first CLI this machine can start": none.
    // Reaching a peer is exactly what such a machine is for.
    [AvaloniaFact]
    public void ReturningToAMachineWithNoUsableCliDisablesEveryRowAgain()
    {
        var f = Ready();
        var w = Make(f);
        NewChatAvailability.CurrentForTests = () => new[]
        {
            new NewChatOption(NewChatCli.ClaudeCode, Enabled: false, Reason: "not found", Warning: null),
            new NewChatOption(NewChatCli.Codex, Enabled: false, Reason: "not found", Warning: null),
            new NewChatOption(NewChatCli.Grok, Enabled: false, Reason: "not found", Warning: null),
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.NoGateway;
        w = RebuildWith(f);
        Assert.Null(w.SelectedCli);

        Pick(w, 1);
        Assert.Equal(NewChatCli.ClaudeCode, w.SelectedCli); // the peer has one
        Assert.True(w.StartButton.IsEnabled);

        // Back home: the restore runs its fall-through, which finds no CLI this
        // machine can start. Every row is disabled again and OpenClaw is not
        // selected. (What SelectedCli reads here is not pinned: it is left
        // holding the peer's pick, which is reported as a finding, not asserted.)
        Pick(w, 0);
        Assert.False(w.OpenClawSelected);
        Assert.All(w.CliList.Children.OfType<RadioButton>(), r => Assert.False(r.IsEnabled));
    }

    [AvaloniaFact]
    public void WithoutTheSeamTheRowFollowsTheRealPeerLinkSetting()
    {
        var f = Ready();
        var w = Make(f);
        NewChatWindow.PeerLinkOnForTests = null;
        w = RebuildWith(f);
        Assert.Null(w.MachineSection); // setting defaults off

        OrbweaverSettings.PeerLinkEnabled = true;
        try { Assert.NotNull(RebuildWith(f).MachineSection); }
        finally { OrbweaverSettings.PeerLinkEnabled = false; }
    }

    [AvaloniaFact]
    public void AnAnswerThatArrivesAfterReturningToThisMachineIsIgnored()
    {
        var f = Ready();
        var slow = f.Pending["mini"] = new();
        var w = Make(f);
        Pick(w, 1);
        Pick(w, 0);
        slow.TrySetResult(Info());
        Flush();
        Assert.True(Radio(w, NewChatCli.Codex).IsEnabled);
        Assert.Null(w.MachineNote.Text);
    }

    [AvaloniaFact]
    public void StartSendsTheSpecAndWritesNothingLocal()
    {
        var f = Ready();
        var w = Make(f);
        var lastCli = OrbweaverSettings.NewChatLastCli;
        var recents = OrbweaverSettings.NewChatRecentFolders.ToList();

        Pick(w, 1);
        w.FolderCombo.SelectedIndex = 1;
        w.AccountCombo.SelectedIndex = 1; // Work
        Click(w.StartButton);

        var (machine, spec) = Assert.Single(f.Launches);
        Assert.Equal("mini", machine);
        Assert.Equal(new PeerLaunchSpec("claude", "/srv/lib", "/p/work"), spec);
        Assert.Equal("Claude Code is starting on mini. Its orb appears when mini next reports its sessions.", w.StatusLine.Text);
        Assert.True(w.StartButton.IsEnabled);
        Assert.Equal(lastCli, OrbweaverSettings.NewChatLastCli);
        Assert.Equal(recents, OrbweaverSettings.NewChatRecentFolders.ToList());
    }

    [AvaloniaFact]
    public void TheDefaultAccountIsSentAsDefault()
    {
        var f = Ready();
        var w = Make(f);
        Pick(w, 1);
        Click(w.StartButton);
        Assert.Equal("default", f.Launches.Single().spec.Account);
    }

    [AvaloniaFact]
    public void AMachineWithOnlyTheDefaultAccountHidesThePickerAndSendsDefault()
    {
        var f = Ready();
        var w = Make(f);
        Pick(w, 2); // pc: claude has two accounts in Info(); switch to codex which has one
        Radio(w, NewChatCli.Codex).IsChecked = true;
        Flush();
        Assert.False(w.AccountSection.IsVisible);
        Click(w.StartButton);
        Assert.Equal(new PeerLaunchSpec("codex", "/Users/w", "default"), f.Launches.Single().spec);
    }

    [AvaloniaTheory]
    [InlineData("peer_launch_off", null, "mini doesn't allow other desktops to start sessions. Turn that on in its Settings.")]
    [InlineData("bad_signature", null, "mini could not verify this machine's signature. Nothing was started.")]
    [InlineData("unknown_cwd", null, "That folder doesn't exist on mini.")]
    [InlineData("no_terminal", null, "No terminal to start it in (no reason given).")]
    [InlineData("link_lost", null, "The link to mini dropped before it answered.")]
    [InlineData("cannot_sign", null, "This machine couldn't sign the request, so nothing was sent.")]
    [InlineData("whatever", "disk full", "mini refused it (whatever).")]
    public void EachRefusalShowsItsCopyInline(string code, string? why, string expected)
    {
        var f = Ready();
        f.Result = new PeerLaunchResult(false, code, null, why);
        var w = Make(f);
        Pick(w, 1);
        Click(w.StartButton);
        Assert.Equal(expected, w.StatusLine.Text);
        Assert.True(w.StartButton.IsEnabled);
    }

    // The far machine reports only a CLI this window has no row for, so the
    // scope says Start is possible while no radio can be chosen.
    [AvaloniaFact]
    public void AMachineWithOnlyAnUnknownCliSaysChooseACliOnAStrayClick()
    {
        var f = Ready();
        f.Answers["mini"] = new PeerLaunchInfo(true, "macos", "/h", true, [new("gemini", true)], [], ["/a"]);
        var w = Make(f);
        Pick(w, 1);
        Assert.Null(w.SelectedCli);
        Click(w.StartButton);
        Assert.Equal("Choose a CLI first.", w.StatusLine.Text);
        Assert.Empty(f.Launches);
    }

    [AvaloniaFact]
    public void StartWithNoFolderSaysSo()
    {
        var f = Ready();
        f.Answers["mini"] = Info(recents: [], home: null);
        var w = Make(f);
        Pick(w, 1);
        Click(w.StartButton);
        Assert.Equal("mini reported no folder to start in.", w.StatusLine.Text);
        Assert.Empty(f.Launches);
    }

    // Once the request is on the wire the far desk will start it, so the
    // Machine row is locked until the answer is on screen.
    [AvaloniaFact]
    public void TheMachineRowIsLockedWhileALaunchIsInFlight()
    {
        var f = Ready();
        var pending = f.PendingLaunch = new();
        var w = Make(f);
        Pick(w, 1);
        Click(w.StartButton);
        Assert.Equal("Starting on mini…", w.StatusLine.Text);
        Assert.False(w.StartButton.IsEnabled);
        Assert.False(w.MachineCombo.IsEnabled);

        pending.SetResult(new PeerLaunchResult(true, "launched", "x", null));
        Flush();
        Assert.True(w.MachineCombo.IsEnabled);
        Assert.True(w.StartButton.IsEnabled);
        Assert.Contains("is starting on mini", w.StatusLine.Text);
    }

    [AvaloniaFact]
    public void ALauncherThatThrowsOnInfoIsNoAnswerNotACrash()
    {
        var f = Ready();
        var w = Make(f);
        f.ThrowInfo = new TimeoutException("certificate still loading");
        Pick(w, 1);
        Assert.Contains("didn't answer", w.MachineNote.Text);
        Assert.False(w.StartButton.IsEnabled);

        f.ThrowInfo = null; // and the row still works afterwards
        Pick(w, 2);
        Assert.True(w.StartButton.IsEnabled);
    }

    [AvaloniaFact]
    public void ALauncherThatThrowsOnLaunchShowsLaunchFailedWithTheExceptionType()
    {
        var f = Ready();
        var w = Make(f);
        Pick(w, 1);
        f.ThrowLaunch = new TimeoutException("signer not ready");
        Click(w.StartButton);
        Assert.Equal("mini could not start it (TimeoutException).", w.StatusLine.Text);
        Assert.True(w.StartButton.IsEnabled);
        Assert.True(w.MachineCombo.IsEnabled);
    }

    // This machine's "Codex not found" line under its row describes this
    // machine. While another is chosen it is unreadable but keeps its height.
    [AvaloniaFact]
    public void ThisMachinesReasonLinesAreHiddenWhileARemoteMachineIsChosen()
    {
        var f = Ready();
        f.Answers["mini"] = Info(installed: ["claude", "codex"]);
        var w = Make(f);
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode),
            new NewChatOption(NewChatCli.Codex, Enabled: false, Reason: "Codex not found on this machine.", Warning: null),
            Enabled(NewChatCli.Grok)
        };
        w = RebuildWith(f);
        var opened = Height(w);
        var line = w.ReasonTextFor(NewChatCli.Codex)!;
        Assert.Equal(0.7, line.Opacity);

        Pick(w, 1);
        Assert.True(Radio(w, NewChatCli.Codex).IsEnabled);
        Assert.Equal(0, line.Opacity);
        Assert.Equal(opened, Height(w));

        Pick(w, 0);
        Assert.False(Radio(w, NewChatCli.Codex).IsEnabled);
        Assert.Equal(0.7, line.Opacity);
        Assert.Equal(opened, Height(w));
    }

    [AvaloniaFact]
    public void TheHeightNeverMovesAcrossMachinesCliPicksAndMessages()
    {
        var f = Ready();
        f.Answers["asleep"] = null;
        var tcs = f.Pending["mini"] = new();
        var w = Make(f);
        var opened = Height(w);

        Pick(w, 1);                                   // asking…
        Assert.Equal(opened, Height(w));
        tcs.SetResult(Info());                        // answered
        Flush();
        Assert.Equal(opened, Height(w));
        w.AccountCombo.SelectedIndex = 1;
        Assert.Equal(opened, Height(w));

        Click(w.StartButton);                         // a long message in the status line
        Assert.Equal(opened, Height(w));
        f.Result = new PeerLaunchResult(false, "whatever", new string('x', 400), null);
        Click(w.StartButton);
        Assert.Equal(opened, Height(w));

        Pick(w, 2);                                   // another machine, no recents, codex
        Radio(w, NewChatCli.Codex).IsChecked = true;
        Assert.Equal(opened, Height(w));

        f.Answers["pc"] = null;                       // no answer
        Pick(w, 1);
        Pick(w, 2);
        Assert.Equal(opened, Height(w));
        f.Answers["pc"] = Info(allowed: false);       // switch off
        Pick(w, 1);
        Pick(w, 2);
        Assert.Equal(opened, Height(w));

        Pick(w, 3);                                   // an offline pick bounces
        Assert.Equal(opened, Height(w));

        Pick(w, 0);                                   // and back home
        Assert.Equal(opened, Height(w));
        foreach (var cli in new object[] { NewChatCli.ClaudeCode, NewChatCli.Codex, NewChatCli.Grok, NewChatWindow.OpenClawTag })
        {
            Radio(w, cli).IsChecked = true;
            Assert.Equal(opened, Height(w));
        }
    }

    [AvaloniaFact]
    public void ALocalWindowWithoutTheRowIsNotTallerThanBefore()
    {
        var with = Height(Make(Ready()));
        var without = Height(Make(null));
        Assert.True(with > without); // the row is the only difference, and it costs its own height
    }

    // ---- CB-397: the far machine's trust question --------------------------------------

    private static readonly PeerLaunchTrust Asked = new("TOKEN", "claude", "/srv/lib", 1_800_000_000);

    private static FakeLauncher Asking()
    {
        var f = Ready();
        f.Result = new PeerLaunchResult(false, PeerLaunchCodes.TrustNeeded, null, null, Asked);
        return f;
    }

    // Opens on mini, picks /srv/lib and starts: the far desk answers with the question.
    private static NewChatWindow StartAsked(FakeLauncher f)
    {
        var w = Make(f);
        Pick(w, 1);
        w.FolderCombo.SelectedIndex = 1;
        Click(w.StartButton);
        return w;
    }

    [AvaloniaFact]
    public void A_trust_question_is_asked_inline_and_locks_the_form_without_moving_the_height()
    {
        var w = Make(Asking());
        var opened = Height(w);
        Pick(w, 1);
        w.FolderCombo.SelectedIndex = 1;
        Assert.False(w.TrustPanel.IsVisible);
        Click(w.StartButton);

        Assert.True(w.TrustPanel.IsVisible);
        Assert.Equal("Trust this folder on mini?", w.TrustTitle.Text);
        Assert.Equal("/srv/lib", w.TrustFolder.Text);
        Assert.Equal("Claude Code will be able to read, edit and run files there.", w.TrustWarning.Text);
        Assert.False(w.TrustHooks.IsVisible);
        Assert.Equal("Claude Code on mini is asking whether to trust this folder.", w.StatusLine.Text);
        Assert.True(w.TrustYes.IsEnabled);
        Assert.True(w.TrustNo.IsEnabled);
        Assert.False(w.StartButton.IsEnabled);
        Assert.False(w.MachineCombo.IsEnabled);
        Assert.Equal(opened, Height(w));
        Assert.True(w.TrustGhost.IsVisible);
    }

    [AvaloniaFact]
    public void A_local_window_reserves_no_trust_space()
    {
        Assert.False(Make(null).TrustGhost.IsVisible);
    }

    [AvaloniaFact]
    public void Trust_is_sent_once_even_if_clicked_twice_and_a_launch_ends_the_question()
    {
        var f = Asking();
        var pending = f.PendingTrust = new();
        var w = StartAsked(f);

        Click(w.TrustYes);
        Assert.Equal("Trusting it on mini…", w.StatusLine.Text);
        Assert.False(w.TrustYes.IsEnabled);
        Assert.False(w.TrustNo.IsEnabled);
        Click(w.TrustYes);
        Click(w.TrustNo);
        Assert.Equal([("mini", Asked, true)], f.Trusts);
        Assert.False(w.StartButton.IsEnabled);

        pending.SetResult(new PeerLaunchResult(true, "launched", "x", null));
        Flush();
        Assert.False(w.TrustPanel.IsVisible);
        Assert.Equal("Claude Code is starting on mini. Its orb appears when mini next reports its sessions.", w.StatusLine.Text);
        Assert.True(w.StartButton.IsEnabled);
        Assert.True(w.MachineCombo.IsEnabled);
    }

    [AvaloniaTheory]
    [InlineData("trust_cancelled", "Cancelled. Nothing was trusted, and mini closed the session.")]
    [InlineData("no_answer", "Cancelled here. mini didn't confirm, and closes the session itself within two minutes.")]
    [InlineData("link_lost", "Cancelled here. mini didn't confirm, and closes the session itself within two minutes.")]
    [InlineData("trust_gone", "Nobody answered in time, so mini closed the session. Nothing was trusted.")]
    public void Cancel_says_what_the_far_desk_confirmed(string code, string expected)
    {
        var f = Asking();
        f.TrustResult = new PeerLaunchResult(false, code, null, null);
        var w = StartAsked(f);

        Click(w.TrustNo);

        Assert.Equal([("mini", Asked, false)], f.Trusts);
        Assert.Equal(expected, w.StatusLine.Text);
        Assert.False(w.TrustPanel.IsVisible);
        Assert.True(w.StartButton.IsEnabled);
        Assert.True(w.MachineCombo.IsEnabled);
    }

    [AvaloniaTheory]
    [InlineData("trust_gone")]
    [InlineData("expired")]
    public void A_trust_click_that_finds_no_question_says_nobody_answered_in_time(string code)
    {
        var f = Asking();
        f.TrustResult = new PeerLaunchResult(false, code, null, null);
        var w = StartAsked(f);
        Click(w.TrustYes);
        Assert.Equal("Nobody answered in time, so mini closed the session. Nothing was trusted.", w.StatusLine.Text);
    }

    [AvaloniaFact]
    public void An_answer_the_far_desk_could_not_apply_is_its_launch_failed_sentence()
    {
        var f = Asking();
        f.TrustResult = new PeerLaunchResult(false, "launch_failed", null, "The trust question was no longer on screen.");
        var w = StartAsked(f);
        Click(w.TrustYes);
        Assert.Equal("mini could not start it (The trust question was no longer on screen.).", w.StatusLine.Text);
    }

    [AvaloniaFact]
    public void A_launcher_that_throws_on_the_answer_is_launch_failed_not_a_crash()
    {
        var f = Asking();
        f.ThrowTrust = new InvalidOperationException();
        var w = StartAsked(f);
        Click(w.TrustYes);
        Assert.Equal("mini could not start it (InvalidOperationException).", w.StatusLine.Text);
        Assert.False(w.TrustPanel.IsVisible);
    }

    [AvaloniaFact]
    public void Nobody_answering_for_the_far_desks_two_minutes_lapses_the_question()
    {
        var wait = new TaskCompletionSource();
        NewChatWindow.TrustWaitForTests = d => { Assert.Equal(TimeSpan.FromSeconds(120), d); return wait.Task; };
        var f = Asking();
        var w = StartAsked(f);
        Assert.True(w.TrustPanel.IsVisible);

        wait.SetResult();
        Flush();

        Assert.False(w.TrustPanel.IsVisible);
        Assert.Equal("Nobody answered in time, so mini closed the session. Nothing was trusted.", w.StatusLine.Text);
        Assert.True(w.StartButton.IsEnabled);
        Assert.True(w.MachineCombo.IsEnabled);
        // A click after the lapse does nothing.
        Click(w.TrustYes);
        Assert.Empty(f.Trusts);
    }

    [AvaloniaFact]
    public void The_lapse_leaves_an_answer_in_flight_and_an_answered_question_alone()
    {
        var wait = new TaskCompletionSource();
        NewChatWindow.TrustWaitForTests = _ => wait.Task;
        var f = Asking();
        var pending = f.PendingTrust = new();
        var w = StartAsked(f);
        Click(w.TrustYes);

        wait.SetResult();
        Flush();
        Assert.Equal("Trusting it on mini…", w.StatusLine.Text);

        pending.SetResult(new PeerLaunchResult(true, "launched", "x", null));
        Flush();
        Assert.Contains("is starting on mini", w.StatusLine.Text);

        // An old timer after the question was answered: nothing changes.
        var again = new TaskCompletionSource();
        var f2 = Asking();
        NewChatWindow.TrustWaitForTests = _ => again.Task;
        var w2 = StartAsked(f2);
        Click(w2.TrustYes);
        again.SetResult();
        Flush();
        Assert.Contains("is starting on mini", w2.StatusLine.Text);
    }

    [AvaloniaFact]
    public void Closing_the_window_while_it_asks_sends_cancel_once()
    {
        var f = Asking();
        var w = StartAsked(f);

        w.CancelPendingTrustOnClose();
        w.CancelPendingTrustOnClose();

        Assert.Equal([("mini", Asked, false)], f.Trusts);
    }

    [AvaloniaFact]
    public void Closing_with_no_question_or_an_answer_already_in_flight_sends_nothing()
    {
        var f = Asking();
        var w = Make(f);
        w.CancelPendingTrustOnClose();
        Assert.Empty(f.Trusts);

        var f2 = Asking();
        f2.PendingTrust = new();
        var w2 = StartAsked(f2);
        Click(w2.TrustYes);
        w2.CancelPendingTrustOnClose();
        Assert.Single(f2.Trusts);
    }

    [AvaloniaFact]
    public void A_trust_needed_with_no_question_is_just_a_refusal()
    {
        var f = Ready();
        f.Result = new PeerLaunchResult(false, PeerLaunchCodes.TrustNeeded, null, null);
        var w = StartAsked(f);
        Assert.False(w.TrustPanel.IsVisible);
        Assert.Equal("mini refused it (trust_needed).", w.StatusLine.Text);
        Assert.True(w.StartButton.IsEnabled);
    }

    [AvaloniaFact]
    public void The_hooks_line_shows_only_when_the_far_screen_warned_of_hooks_and_the_height_holds()
    {
        var f = Ready();
        f.Result = new PeerLaunchResult(false, PeerLaunchCodes.TrustNeeded, null, null, Asked with { Hooks = true });
        var w = Make(f);
        var opened = Height(w);
        Pick(w, 1);
        w.FolderCombo.SelectedIndex = 1;
        Click(w.StartButton);

        Assert.True(w.TrustHooks.IsVisible);
        Assert.Equal("This folder declares hooks that run commands on mini without asking.", w.TrustHooks.Text);
        Assert.Equal(opened, Height(w));

        // A later question without the warning hides the line again.
        Click(w.TrustNo);
        f.Result = new PeerLaunchResult(false, PeerLaunchCodes.TrustNeeded, null, null, Asked);
        Click(w.StartButton);
        Assert.False(w.TrustHooks.IsVisible);
        Assert.Null(w.TrustHooks.Text);
    }

    // The window's own OnClosed, run the way Avalonia runs it when the window closes (the
    // suite never Show()s a window, so it is invoked directly): it answers Cancel to a
    // question still being asked, once, and says nothing when nothing is asked.
    [AvaloniaFact]
    public void Closing_the_window_while_it_asks_sends_cancel_through_its_own_OnClosed()
    {
        var onClosed = typeof(NewChatWindow).GetMethod("OnClosed", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var f = Asking();
        var w = StartAsked(f);
        Assert.True(w.TrustPanel.IsVisible);

        onClosed.Invoke(w, new object[] { EventArgs.Empty });

        Assert.Equal([("mini", Asked, false)], f.Trusts);

        var quiet = new FakeLauncher();
        var w2 = Make(Ready());
        onClosed.Invoke(w2, new object[] { EventArgs.Empty });
        Assert.Empty(quiet.Trusts);
    }
}
