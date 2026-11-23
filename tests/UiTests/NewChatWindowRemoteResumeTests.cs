using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-434: Resume with a remote machine selected in New chat, driven through a
// fake IRemotePastSessions. Same rules as the local suite: seams, synchronous
// loads, a window that is built by reflection and never shown.
[Collection("Settings")]
public class NewChatWindowRemoteResumeTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    public NewChatWindowRemoteResumeTests()
    {
        NewChatWindow.AccountDirectoryExistsForTests = _ => true;
        NewChatWindow.NowForTests = () => Now;
        NewChatWindow.ResumeLoadInlineForTests = true;
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => new[]
        {
            new NewChatOption(NewChatCli.ClaudeCode, true, null, null),
            new NewChatOption(NewChatCli.Codex, true, null, null),
            new NewChatOption(NewChatCli.Grok, true, null, null)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => new[] { ("aurora", "Aurora") };
        PastSessionIndex.LoadForTests = (_, _, _, _) => throw new InvalidOperationException("a remote list must not read this machine's disk");
    }

    public void Dispose()
    {
        NewChatWindow.AccountDirectoryExistsForTests = null;
        NewChatWindow.NowForTests = null;
        NewChatWindow.ResumeLoadInlineForTests = false;
        NewChatWindow.CurrentStatusesForTests = null;
        NewChatWindow.OrbForTests = null;
        NewChatWindow.FocusOrbForTests = null;
        NewChatWindow.OpenClawAvailabilityForTests = null;
        NewChatWindow.KnownAgentsForTests = null;
        NewChatWindow.PeerLauncherForTests = null;
        NewChatWindow.PeerLinkOnForTests = null;
        NewChatWindow.RemotePastForTests = null;
        NewChatAvailability.CurrentForTests = null;
        PastSessionIndex.LoadForTests = null;
    }

    internal sealed class FakeRemote : IRemotePastSessions
    {
        public RemoteListResult List = new(RemoteListOutcome.Ok, Array.Empty<PastSessionRow>(), null);
        public RemoteResumeResult Resume = new(RemoteResumeOutcome.Launched, null, false);
        public Exception? ThrowList, ThrowResume;
        public List<(string machine, NewChatCli cli, string profile, bool older)> Listed = new();
        public List<(string machine, NewChatCli cli, string id, string profile)> Resumed = new();
        public List<string> Cwds = new();

        public Task<RemoteListResult> ListAsync(string machine, NewChatCli cli, string profile, bool showOlder, CancellationToken ct)
        {
            Listed.Add((machine, cli, profile, showOlder));
            if (ThrowList is { } e) throw e;
            return Task.FromResult(List);
        }

        public Task<RemoteResumeResult> ResumeAsync(string machine, NewChatCli cli, string sessionId, string cwd, string profile, CancellationToken ct)
        {
            Resumed.Add((machine, cli, sessionId, profile));
            Cwds.Add(cwd);
            if (ThrowResume is { } e) throw e;
            return Task.FromResult(Resume);
        }
    }

    // The key a mirrored orb really has: RemoteControlSessions.Remote.Key over the
    // far desk's route for the session.
    private static string MirrorKey(string sessionId) =>
        new RemoteControlSessions.Remote("far name", sessionId, "idle", DateTime.UtcNow, "acct", Route: RemoteMirrorServer.RouteFor(sessionId)).Key;

    private static void FreshSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-newchat-remote-resume-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    internal static PastSessionRow Row(string id, string title, double hoursAgo = 2, string cwd = "/srv/app",
        bool live = false, bool missing = false, int? messages = 6, NewChatCli cli = NewChatCli.ClaudeCode) =>
        new(new PastSession(cli, id, cwd, title, new DateTimeOffset(Now.AddHours(-hoursAgo), TimeSpan.Zero),
            messages, "", missing), live);

    private static NewChatWindow Window(FakeRemote remote, NewChatWindowMachineTests.FakeLauncher? launcher = null, int resumeLevel = 1)
    {
        FreshSettings();
        launcher ??= new NewChatWindowMachineTests.FakeLauncher();
        launcher.Answers["mini"] = NewChatWindowMachineTests.Info(installed: ["claude", "codex"]) with { Resume = resumeLevel };
        NewChatWindow.PeerLauncherForTests = launcher;
        NewChatWindow.PeerLinkOnForTests = true;
        NewChatWindow.RemotePastForTests = remote;
        var ctor = typeof(NewChatWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: new[] { typeof(NewChatCli?), typeof(string), typeof(string) })!;
        var window = (NewChatWindow)ctor.Invoke(new object?[] { null, null, null });
        window.MachineCombo.SelectedIndex = 1;
        Flush();
        window.LocalResumeRadio.IsChecked = true;
        Flush();
        return window;
    }

    private static IReadOnlyList<PastSessionRow> Shown(NewChatWindow w) =>
        ((IEnumerable<PastSessionRow>)w.ResumeList.ItemsSource!).ToList();

    private static void Click(Button b)
    {
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Flush();
    }

    [AvaloniaFact]
    public void ResumeIsEnabledAndListsTheFarMachinesSessions()
    {
        var remote = new FakeRemote
        {
            List = new(RemoteListOutcome.Ok, new[] { Row("a", "Fix the parser", 1), Row("b", "Old work", 5) }, null)
        };
        var window = Window(remote);

        Assert.True(window.LocalResumeRadio.IsEnabled);
        Assert.True(window.ResumePane.IsVisible);
        Assert.False(window.FolderSection.IsVisible);
        Assert.Equal("Resume", window.StartButton.Content);
        Assert.Equal(new[] { "a", "b" }, Shown(window).Select(r => r.Session.Id));
        Assert.Equal(("mini", NewChatCli.ClaudeCode, RelayAccounts.Default, false), remote.Listed.Last());
        Assert.Null(ToolTip.GetTip(window.LocalResumeRadio));
    }

    [AvaloniaFact]
    public void SearchFolderFilterAndOlderWorkLikeLocally()
    {
        var remote = new FakeRemote
        {
            List = new(RemoteListOutcome.Ok, new[]
            {
                Row("a", "Fix the parser", 1, "/srv/app"), Row("b", "Write docs", 2, "/srv/lib"),
                Row("c", "Ancient", 24 * 40, "/srv/app")
            }, null)
        };
        var window = Window(remote);

        // 30-day cut: the ancient one is hidden until Older, and asking for it
        // goes back to the machine.
        Assert.Equal(new[] { "a", "b" }, Shown(window).Select(r => r.Session.Id));
        window.ResumeSearch.Text = "docs";
        Assert.Equal(new[] { "b" }, Shown(window).Select(r => r.Session.Id));
        window.ResumeSearch.Text = null;

        var folders = ((IEnumerable<NewChatWindow.ResumeFolder>)window.ResumeFolderFilter.ItemsSource!).ToList();
        window.ResumeFolderFilter.SelectedItem = folders.Single(f => f.Path == "/srv/lib");
        Assert.Equal(new[] { "b" }, Shown(window).Select(r => r.Session.Id));
        window.ResumeFolderFilter.SelectedIndex = 0;

        var before = remote.Listed.Count;
        window.ResumeOlder.IsChecked = true;
        Flush();
        Assert.Equal(before + 1, remote.Listed.Count);
        Assert.True(remote.Listed.Last().older);
        Assert.Equal(new[] { "a", "b", "c" }, Shown(window).Select(r => r.Session.Id));
    }

    [AvaloniaFact]
    public void NoLocalDiskIsReadForARemoteRowsCountOrPicture()
    {
        PastSessions.CountForTests = (_, _) => throw new InvalidOperationException("must not read a count from this disk");
        try
        {
            var remote = new FakeRemote
            {
                List = new(RemoteListOutcome.Ok, new[] { Row("a", "No count", messages: null), Row("b", "Counted", messages: 3) }, null)
            };
            var window = Window(remote);

            var texts = Shown(window).Select(r =>
                ((Control)window.ResumeList.ItemTemplate!.Build(r)!).GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray()).ToList();
            Assert.Contains(texts[0], t => t == "app");
            Assert.Contains(texts[1], t => t == "app · 3 messages");
            Assert.Equal(0, window.ResumeAvatarCount);
        }
        finally { PastSessions.CountForTests = null; }
    }

    [AvaloniaFact]
    public void ALoadingListSaysWhoIsBeingAsked()
    {
        NewChatWindow.ResumeLoadInlineForTests = false;
        var remote = new FakeRemote();
        var window = Window(remote);

        Assert.True(window.ResumeEmpty.IsVisible);
        Assert.Equal(PeerResumeCopy.Loading("mini"), window.ResumeEmpty.Text);
    }

    [AvaloniaFact]
    public void ANeedsUpdateAnswerIsAClearStatusNotAnEmptyList()
    {
        var window = Window(new FakeRemote { List = new(RemoteListOutcome.NeedsUpdate, Array.Empty<PastSessionRow>(), null) });

        Assert.True(window.ResumeEmpty.IsVisible);
        Assert.Equal(PeerResumeCopy.NeedsUpdate("mini"), window.ResumeEmpty.Text);
        Assert.False(window.ResumeList.IsVisible);
    }

    [AvaloniaFact]
    public void UnreachableAndAThrowingProviderBothSayTheMachineCouldNotBeReached()
    {
        var window = Window(new FakeRemote { List = new(RemoteListOutcome.Unreachable, Array.Empty<PastSessionRow>(), "timed out") });
        Assert.Equal("Couldn't reach mini for its sessions (timed out).", window.ResumeEmpty.Text);

        var throwing = Window(new FakeRemote { ThrowList = new InvalidOperationException("boom") });
        Assert.Equal("Couldn't reach mini for its sessions (InvalidOperationException).", throwing.ResumeEmpty.Text);
    }

    [AvaloniaFact]
    public void AnEmptyRemoteListSaysSo()
    {
        var window = Window(new FakeRemote());
        Assert.Equal("No sessions in the last 30 days", window.ResumeEmpty.Text);
    }

    [AvaloniaFact]
    public void ResumeLaunchesOnTheMachineWithTheChosenSessionAndAccount()
    {
        var remote = new FakeRemote { List = new(RemoteListOutcome.Ok, new[] { Row("a", "One") }, null) };
        var window = Window(remote);
        window.ResumeList.SelectedIndex = 0;

        Click(window.StartButton);

        Assert.Equal(("mini", NewChatCli.ClaudeCode, "a", RelayAccounts.Default), remote.Resumed.Single());
        Assert.Equal("/srv/app", remote.Cwds.Single());
        Assert.Equal(PeerResumeCopy.Resumed("mini"), window.StatusLine.Text);
        Assert.True(window.StartButton.IsEnabled);
        Assert.True(window.MachineCombo.IsEnabled);
    }

    [AvaloniaFact]
    public void ACodexResumeSaysTheOrbAppearsAfterTheFirstMessage()
    {
        var remote = new FakeRemote
        {
            List = new(RemoteListOutcome.Ok, new[] { Row("c1", "Codex one", cli: NewChatCli.Codex) }, null),
            Resume = new(RemoteResumeOutcome.Launched, null, IsCodex: true)
        };
        var window = Window(remote);
        window.CliList.Children.OfType<RadioButton>().Single(r => Equals(r.Tag, NewChatCli.Codex)).IsChecked = true;
        Flush();
        window.LocalResumeRadio.IsChecked = true;
        Flush();
        window.ResumeList.SelectedIndex = 0;

        Click(window.StartButton);

        Assert.Equal(PeerResumeCopy.ResumedCodex("mini"), window.StatusLine.Text);
    }

    [AvaloniaFact]
    public void ALiveRowFocusesItsOrbWhenThisSideHasOneAndNamesTheMachineWhenNot()
    {
        var remote = new FakeRemote
        {
            List = new(RemoteListOutcome.Ok, new[] { Row("live", "Running", live: true) }, null),
            Resume = new(RemoteResumeOutcome.AlreadyRunning, null, false)
        };
        var window = Window(remote);
        window.ResumeList.SelectedIndex = 0;
        Assert.Contains(Shown(window), r => r.IsLive);

        Click(window.StartButton);
        Assert.Equal(PeerResumeCopy.AlreadyRunning("mini"), window.StatusLine.Text);

        OrbWindow? focused = null;
        var orb = new OrbWindow(MirrorKey("live"));
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>
        {
            [MirrorKey("live")] = new SessionStatus { Source = SessionSource.RemoteControl }
        };
        NewChatWindow.OrbForTests = id => id == MirrorKey("live") ? orb : null;
        NewChatWindow.FocusOrbForTests = o => focused = o;
        window.ResumeList.SelectedIndex = 0;
        Click(window.StartButton);

        Assert.Same(orb, focused);
        Assert.Equal(PeerResumeCopy.AlreadyRunningFocused("mini"), window.StatusLine.Text);
    }

    [AvaloniaTheory]
    [InlineData("Unsupported", null, "The claude on mini is too old to resume sessions.")]
    [InlineData("Unsupported", "Update Claude Code on mini.", "Update Claude Code on mini.")]
    [InlineData("NeedsUpdate", null, "mini needs an Orbweaver update before it can list or resume sessions.")]
    [InlineData("Unreachable", "timed out", "Couldn't reach mini for its sessions (timed out).")]
    [InlineData("Failed", null, "Couldn't resume that session on mini. Try again.")]
    // launch_failed on the far desk (its probe could not read the CLI's help) is a
    // Failed carrying its own sentence, never the "too old" one.
    [InlineData("Failed", "mini could not start it (the CLI help could not be read).", "mini could not start it (the CLI help could not be read).")]
    [InlineData("Failed", "No terminal.", "No terminal.")]
    public void EveryRefusalHasItsOwnStatusAndReEnablesTheForm(string outcomeName, string? message, string expected)
    {
        var outcome = Enum.Parse<RemoteResumeOutcome>(outcomeName);
        var remote = new FakeRemote
        {
            List = new(RemoteListOutcome.Ok, new[] { Row("a", "One") }, null),
            Resume = new(outcome, message, false)
        };
        var window = Window(remote);
        window.ResumeList.SelectedIndex = 0;

        Click(window.StartButton);

        Assert.Equal(expected, window.StatusLine.Text);
        Assert.True(window.StartButton.IsEnabled);
        Assert.True(window.MachineCombo.IsEnabled);
    }

    [AvaloniaFact]
    public void AThrowingResumeIsAFailureStatus()
    {
        var remote = new FakeRemote
        {
            List = new(RemoteListOutcome.Ok, new[] { Row("a", "One") }, null),
            ThrowResume = new InvalidOperationException("x")
        };
        var window = Window(remote);
        window.ResumeList.SelectedIndex = 0;
        Click(window.StartButton);
        Assert.Equal("Couldn't resume that session: InvalidOperationException", window.StatusLine.Text);
    }

    [AvaloniaFact]
    public void ChoosingNothingOrAMissingFolderNeverReachesTheMachine()
    {
        var remote = new FakeRemote
        {
            List = new(RemoteListOutcome.Ok, new[] { Row("gone", "Gone", missing: true) }, null)
        };
        var window = Window(remote);

        Click(window.StartButton);
        Assert.Equal("Choose a session first.", window.StatusLine.Text);

        window.ResumeList.SelectedIndex = 0;
        Click(window.StartButton);
        Assert.Equal("That session's folder no longer exists.", window.StatusLine.Text);
        Assert.Empty(remote.Resumed);
    }

    [AvaloniaFact]
    public void ANewChatOnTheMachineStillStartsRatherThanResumes()
    {
        var launcher = new NewChatWindowMachineTests.FakeLauncher();
        var window = Window(new FakeRemote(), launcher);
        window.LocalNewRadio.IsChecked = true;
        Flush();

        Assert.Equal("Start", window.StartButton.Content);
        Assert.True(window.FolderSection.IsVisible);
        Assert.False(window.ResumePane.IsVisible);
        Click(window.StartButton);
        Assert.Single(launcher.Launches);
    }

    [AvaloniaFact]
    public void ReturningToThisMachineReadsLocalSessionsAgain()
    {
        var remote = new FakeRemote { List = new(RemoteListOutcome.Ok, new[] { Row("r", "Remote one") }, null) };
        var window = Window(remote);
        Assert.Equal("r", Shown(window).Single().Session.Id);

        PastSessionIndex.LoadForTests = (cli, _, _, _) => new[]
        {
            new PastSession(cli, "l", Path.GetTempPath().TrimEnd('/'), "Local one", new DateTimeOffset(Now.AddHours(-1), TimeSpan.Zero), 2, "/t", false)
        };
        window.MachineCombo.SelectedIndex = 0;
        Flush();

        Assert.Equal("l", Shown(window).Single().Session.Id);
    }

    [AvaloniaFact]
    public void AnOlderDeskThatDoesNotAdvertiseResumeSaysToUpdateWithoutAsking()
    {
        var remote = new FakeRemote { List = new(RemoteListOutcome.Ok, new[] { Row("a", "One") }, null) };
        var window = Window(remote, resumeLevel: 0);

        Assert.False(window.LocalResumeRadio.IsEnabled);
        Assert.False(window.ResumePane.IsVisible);
        Assert.Equal("Update Orbweaver on mini to resume sessions there.", ToolTip.GetTip(window.LocalResumeRadio));
        Assert.Empty(remote.Listed);
    }

    [AvaloniaFact]
    public void AMachineThatCannotListKeepsResumeOffWithTheReason()
    {
        NewChatWindow.RemotePastForTests = null;
        var window = Window(new FakeRemote());
        NewChatWindow.RemotePastForTests = null;
        window.MachineCombo.SelectedIndex = 0;
        Flush();
        window.MachineCombo.SelectedIndex = 1;
        Flush();

        Assert.False(window.LocalResumeRadio.IsEnabled);
        Assert.False(window.ResumePane.IsVisible);
        Assert.NotNull(ToolTip.GetTip(window.LocalResumeRadio));
    }

    [AvaloniaFact]
    public void ACappedListSaysItIsTheNewestN()
    {
        var rows = Enumerable.Range(0, 3).Select(i => Row("r" + i, "S" + i)).ToList();
        var capped = Window(new FakeRemote { List = new(RemoteListOutcome.Ok, rows, null, Total: 2500) });
        Assert.Equal("Showing the newest 3 sessions on mini.", capped.StatusLine.Text);

        var whole = Window(new FakeRemote { List = new(RemoteListOutcome.Ok, rows, null, Total: 3) });
        Assert.Null(whole.StatusLine.Text);
        var unknown = Window(new FakeRemote { List = new(RemoteListOutcome.Ok, rows, null) });
        Assert.Null(unknown.StatusLine.Text);
    }

    [AvaloniaTheory]
    [InlineData(@"C:\src\app", true, "app")]
    [InlineData(@"C:\src\app\", true, "app")]
    [InlineData("/srv/app/", true, "app")]
    [InlineData(@"C:\", true, "C:")]
    [InlineData("/", true, "/")]
    [InlineData("/srv/app/", false, "app")]
    public void AFolderLabelCutsARemotePathAtEitherSeparator(string path, bool remote, string expected) =>
        Assert.Equal(expected, NewChatWindow.FolderLabel(path, remote));

    [AvaloniaFact]
    public void AWindowsDesksFoldersGroupFilterAndLabelOnAnyHost()
    {
        var remote = new FakeRemote
        {
            List = new(RemoteListOutcome.Ok, new[]
            {
                Row("a", "One", 1, @"C:\Users\w\src\app"), Row("b", "Two", 2, @"C:\Users\w\src\lib"),
                Row("c", "Three", 3, @"C:\Users\w\src\app\")
            }, null)
        };
        var window = Window(remote);

        var folders = ((IEnumerable<NewChatWindow.ResumeFolder>)window.ResumeFolderFilter.ItemsSource!).ToList();
        Assert.Equal(new[] { "All folders", "app", "lib" }, folders.Select(f => f.ToString()));

        window.ResumeFolderFilter.SelectedItem = folders.Single(f => f.ToString() == "app");
        Assert.Equal(new[] { "a", "c" }, Shown(window).Select(r => r.Session.Id));
        Assert.Equal("app · 6 messages", NewChatWindow.ResumeDetail(Shown(window)[0], remote: true));
    }

    [AvaloniaFact]
    public void ARowWithNoFolderIsGreyedAndNeverSentAnEmptyCwd()
    {
        var remote = new FakeRemote
        {
            List = new(RemoteListOutcome.Ok, new[] { Row("nofolder", "No folder", cwd: "") with { } }, null)
        };
        var window = Window(remote);
        window.ResumeList.SelectedIndex = 0;

        Click(window.StartButton);

        Assert.Equal(PeerResumeCopy.NoFolder("mini"), window.StatusLine.Text);
        Assert.Empty(remote.Resumed);
    }

    [AvaloniaFact]
    public void AFailedResumeReloadsTheStaleListAndKeepsItsStatus()
    {
        var remote = new FakeRemote
        {
            List = new(RemoteListOutcome.Ok, new[] { Row("a", "One") }, null),
            Resume = new(RemoteResumeOutcome.Failed, "mini no longer has that session in that folder.", false)
        };
        var window = Window(remote);
        window.ResumeList.SelectedIndex = 0;
        var loads = remote.Listed.Count;

        remote.List = new(RemoteListOutcome.Ok, Array.Empty<PastSessionRow>(), null);
        Click(window.StartButton);

        Assert.Equal(loads + 1, remote.Listed.Count);
        Assert.Equal("mini no longer has that session in that folder.", window.StatusLine.Text);
        Assert.Empty(Shown(window));
        Assert.True(window.StartButton.IsEnabled);
    }

    // The off-UI-thread path: the same answers, reached through Task.Run and the
    // dispatcher instead of inline.
    private static void WaitFor(Func<bool> done)
    {
        for (var i = 0; i < 300 && !done(); i++)
        {
            Thread.Sleep(10);
            Flush();
        }

        Assert.True(done(), "timed out waiting for the UI thread to be handed the result");
    }

    [AvaloniaFact]
    public void TheAsyncPathLoadsListsFailuresAndResumes()
    {
        NewChatWindow.ResumeLoadInlineForTests = false;
        var remote = new FakeRemote { List = new(RemoteListOutcome.Ok, new[] { Row("a", "One") }, null) };
        var window = Window(remote);
        WaitFor(() => window.ResumeList.IsVisible);
        Assert.Equal("a", Shown(window).Single().Session.Id);

        window.ResumeList.SelectedIndex = 0;
        Click(window.StartButton);
        WaitFor(() => window.StatusLine.Text == PeerResumeCopy.Resumed("mini"));
        Assert.True(window.StartButton.IsEnabled);

        remote.ThrowResume = new InvalidOperationException("x");
        Click(window.StartButton);
        WaitFor(() => window.StatusLine.Text == "Couldn't resume that session: InvalidOperationException");

        var throwing = Window(new FakeRemote { ThrowList = new InvalidOperationException("boom") });
        WaitFor(() => throwing.ResumeEmpty.Text == "Couldn't reach mini for its sessions (InvalidOperationException).");
    }
}
