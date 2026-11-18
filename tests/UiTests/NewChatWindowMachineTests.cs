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
    }

    internal sealed class FakeLauncher : IPeerLauncher
    {
        public List<PeerMachine> Peers = new() { new("mini", true), new("pc", true), new("asleep", false) };
        public Dictionary<string, TaskCompletionSource<PeerLaunchInfo?>> Pending = new();
        public Dictionary<string, PeerLaunchInfo?> Answers = new();
        public PeerLaunchResult Result = new(true, "launched", "s1", null);
        public TaskCompletionSource<PeerLaunchResult>? PendingLaunch;
        public Exception? ThrowInfo, ThrowLaunch;
        public List<(string machine, PeerLaunchSpec spec)> Launches = new();

        public IReadOnlyList<PeerMachine> Machines() => Peers;

        public Task<PeerLaunchInfo?> InfoAsync(string machine, CancellationToken ct)
        {
            if (ThrowInfo is { } boom) throw boom;
            if (Pending.TryGetValue(machine, out var tcs))
            {
                ct.Register(() => tcs.TrySetCanceled(ct));
                return tcs.Task;
            }
            return Task.FromResult(Answers.GetValueOrDefault(machine));
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
        Assert.False(w.BrowseButton.IsEnabled);
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
}
