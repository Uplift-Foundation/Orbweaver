using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-357 (reopened): the remote folder browser and the New Chat window's Browse…
// for a remote machine. A headless Show hangs this suite, so the browser is
// driven directly: NavigateAsync is what OnOpened calls, and OnOpened itself is
// invoked by reflection.
[Collection("Settings")]
public class RemoteFolderBrowserTests : IDisposable
{
    public void Dispose()
    {
        NewChatWindow.ChooseRemoteFolderForTests = null;
        NewChatWindow.PeerLauncherForTests = null;
        NewChatWindow.PeerLinkOnForTests = null;
        NewChatWindow.CurrentStatusesForTests = null;
        NewChatAvailability.CurrentForTests = null;
        NewChatWindow.OpenClawAvailabilityForTests = null;
        NewChatWindow.KnownAgentsForTests = null;
        NewChatWindow.AccountDirectoryExistsForTests = null;
    }

    private static PeerDirResult Top() => new(true, "ok", new PeerDirListing("", "", false,
        [new PeerDirItem("dev", "/Users/dev", null, true), new PeerDirItem("work", "D:\\work", "D:")]));

    private static PeerDirResult Dir(string path, string parent, bool truncated = false, params string[] names) =>
        new(true, "ok", new PeerDirListing(path, parent, truncated, names.Select(n => new PeerDirItem(n)).ToList()));

    private sealed class DirLauncher : IPeerLauncher
    {
        public Dictionary<string, PeerDirResult> Dirs = new();
        public List<string> Asked = new();
        public bool Throw;
        public TaskCompletionSource<PeerDirResult>? Hold;

        public IReadOnlyList<PeerMachine> Machines() => [new("mini", true)];
        public Task<PeerLaunchInfo?> InfoAsync(string machine, CancellationToken ct) =>
            Task.FromResult<PeerLaunchInfo?>(new PeerLaunchInfo(true, "macos", "/Users/dev", true,
                [new PeerLaunchCli("claude", true)], [], ["/srv/app"]));
        public Task<PeerLaunchResult> LaunchAsync(string machine, PeerLaunchSpec spec, CancellationToken ct) =>
            Task.FromResult(new PeerLaunchResult(true, "launched", "s", null));

        public Task<PeerDirResult> ListDirAsync(string machine, string path, CancellationToken ct)
        {
            Asked.Add(path);
            if (Throw) throw new InvalidOperationException("boom");
            if (Hold is { } h && path == "HOLD") return h.Task;
            return Task.FromResult(Dirs.TryGetValue(path, out var r) ? r : PeerDirResult.Fail("unknown_path"));
        }
    }

    private static DirLauncher Tree()
    {
        var l = new DirLauncher();
        l.Dirs[""] = Top();
        l.Dirs["/Users/dev"] = Dir("/Users/dev", "", false, "Source", "Docs");
        l.Dirs["/Users/dev/Source"] = Dir("/Users/dev/Source", "/Users/dev", true, "app");
        l.Dirs["/Users/dev/Source/app"] = Dir("/Users/dev/Source/app", "/Users/dev/Source");
        l.Dirs["D:\\work"] = Dir("D:\\work", "", false, "proj");
        l.Dirs["D:\\work\\proj"] = Dir("D:\\work\\proj", "D:\\work");
        return l;
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    // Waits for a state, not for a time: the handlers under test are async void.
    private static async Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < deadline)
        {
            Flush();
            await Task.Delay(10);
        }
        Assert.True(done(), "state not reached");
    }

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public async Task OpeningStartsAtTheTopLevelWithFoldersOnlyAndNothingToChooseYet()
    {
        var peers = Tree();
        var b = new RemoteFolderBrowser(peers, "mini");

        typeof(RemoteFolderBrowser).GetMethod("OnOpened", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(b, [EventArgs.Empty]);
        await Until(() => b.Listing is not null);

        Assert.Equal([""], peers.Asked);
        Assert.Equal("Choose a folder on mini", b.Title);
        Assert.False(b.CanResize);
        Assert.Equal(["dev", "work (D:)"], b.Rows);
        Assert.Equal("mini", b.Where.Text);
        Assert.False(b.ChooseButton.IsEnabled);
        Assert.Null(b.Chosen);
        Assert.Null(b.StatusText);
    }

    [AvaloniaFact]
    public async Task WalkDownAndBackUpThenChooseWhereYouAre()
    {
        var peers = Tree();
        var b = new RemoteFolderBrowser(peers, "mini");
        await b.NavigateAsync("");

        await b.OpenAsync(0);                         // dev (a root: its own path)
        Assert.Equal(["↑ Up", "Source", "Docs"], b.Rows);
        Assert.Equal("/Users/dev", b.Where.Text);
        Assert.True(b.ChooseButton.IsEnabled);

        await b.OpenAsync(1);                         // Source: listing path + name
        Assert.Equal(["↑ Up", "app"], b.Rows);
        Assert.Equal("Only the first folders are shown.", b.StatusText);

        await b.OpenAsync(0);                         // up to dev
        Assert.Equal("/Users/dev", b.Listing!.Path);
        await b.OpenAsync(0);                         // up from a root: the top level
        Assert.Equal("", b.Listing!.Path);
        Assert.False(b.ChooseButton.IsEnabled);

        await b.OpenAsync(0);
        await b.OpenAsync(1);
        await b.OpenAsync(1);                         // app, which is empty
        Assert.Equal(["↑ Up"], b.Rows);
        Assert.Equal("No folders here.", b.StatusText);
        Assert.Equal("/Users/dev/Source/app", b.Chosen);
    }

    [AvaloniaFact]
    public void ARowShowsWhatTheFarDeskFoundInTheFolder()
    {
        Assert.Equal("app", RemoteFolderBrowser.RowText(new PeerDirItem("app")));
        Assert.Equal("app  ·  git, CLAUDE.md, persona",
            RemoteFolderBrowser.RowText(new PeerDirItem("app", Git: true, Claude: true, Persona: true)));
        Assert.Equal("proj (D:)  ·  git", RemoteFolderBrowser.RowText(new PeerDirItem("proj", "D:\\proj", "D:", Git: true)));
    }

    [AvaloniaFact]
    public async Task AWindowsPathIsJoinedWithABackslashAndARootKeepsItsFullPath()
    {
        var b = new RemoteFolderBrowser(Tree(), "pc");
        await b.NavigateAsync("");
        await b.OpenAsync(1);                         // work (D:), a root
        await b.OpenAsync(1);                         // proj
        Assert.Equal("D:\\work\\proj", b.Chosen);

        Assert.Equal("D:\\x", RemoteFolderBrowser.ChildPath(
            new PeerDirListing("D:\\", "", false, []), new PeerDirItem("x")));
        Assert.Equal("/a/x", RemoteFolderBrowser.ChildPath(
            new PeerDirListing("/a", "", false, []), new PeerDirItem("x")));
    }

    [AvaloniaFact]
    public async Task OpeningWithNothingSelectedOrBeforeAListingOrPastTheEndDoesNothing()
    {
        var peers = Tree();
        var b = new RemoteFolderBrowser(peers, "mini");

        await b.OpenAsync(0);                         // nothing listed yet
        await b.OpenSelectedAsync();                  // nothing selected
        Assert.Empty(peers.Asked);

        await b.NavigateAsync("");
        await b.OpenSelectedAsync();                  // listed, still nothing selected
        await b.OpenAsync(9);                         // past the end
        await b.OpenAsync(-1);                        // before the start (no Up row at the top)
        Assert.Equal([""], peers.Asked);

        b.List.SelectedIndex = 0;
        await b.OpenSelectedAsync();                  // selected: opens it
        Assert.Equal(["", "/Users/dev"], peers.Asked);
    }

    [AvaloniaFact]
    public async Task ARefusalSaysWhyInTheMachinesNameAndLeavesTheListAlone()
    {
        var peers = Tree();
        peers.Dirs["/Users/dev/Source"] = PeerDirResult.Fail("forbidden_path");
        var b = new RemoteFolderBrowser(peers, "mini");
        await b.NavigateAsync("/Users/dev");
        var before = b.Rows;

        await b.OpenAsync(1);

        Assert.Equal("mini doesn't share that folder.", b.StatusText);
        Assert.Equal(before, b.Rows);
        Assert.Equal("/Users/dev", b.Chosen);
    }

    [AvaloniaFact]
    public async Task ASilentOrThrowingLauncherReadsAsNoAnswer()
    {
        var peers = Tree();
        peers.Throw = true;
        var b = new RemoteFolderBrowser(peers, "mini");

        await b.NavigateAsync("");

        Assert.Equal(PeerLaunchCopy.ExplainBrowse("no_answer", "mini"), b.StatusText);
        Assert.Null(b.Listing);
    }

    [AvaloniaFact]
    public async Task ASlowAnswerThatIsOvertakenIsDropped()
    {
        var peers = Tree();
        peers.Hold = new TaskCompletionSource<PeerDirResult>();
        var b = new RemoteFolderBrowser(peers, "mini");

        var slow = b.NavigateAsync("HOLD");
        await b.NavigateAsync("/Users/dev");          // the newer navigation lands first
        peers.Hold.SetResult(Dir("/stale", "", false, "old"));
        await slow;

        Assert.Equal("/Users/dev", b.Listing!.Path);
    }

    [AvaloniaFact]
    public async Task EnterAndDoubleTapOpenTheSelectedFolderAndTheButtonsAreWired()
    {
        var peers = Tree();
        var b = new RemoteFolderBrowser(peers, "mini");
        await b.NavigateAsync("");
        b.List.SelectedIndex = 0;

        b.List.RaiseEvent(new Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.A,
        });
        b.List.RaiseEvent(new Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter,
        });
        await Until(() => b.Listing!.Path == "/Users/dev");

        b.List.SelectedIndex = 1;
        b.List.RaiseEvent(new Avalonia.Input.TappedEventArgs(InputElement.DoubleTappedEvent, null!));
        await Until(() => b.Listing!.Path == "/Users/dev/Source");

        // Click handlers close the dialog; with no owner window shown Close is a no-op,
        // so the proof is that neither throws.
        Click(b.CancelButton);
        Click(b.ChooseButton);
    }

    // --- the New Chat window -------------------------------------------------------

    private static NewChatWindow MakeWindow(DirLauncher peers)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-remote-browse-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => [new NewChatOption(NewChatCli.ClaudeCode, true, null, null)];
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => [("id-1", "Alexis")];
        NewChatWindow.AccountDirectoryExistsForTests = _ => true;
        NewChatWindow.PeerLauncherForTests = peers;
        NewChatWindow.PeerLinkOnForTests = true;

        return (NewChatWindow)typeof(NewChatWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: [typeof(NewChatCli?), typeof(string), typeof(string)])!.Invoke([null, null, null]);
    }

    private static void ClickBrowse(NewChatWindow w)
    {
        Click(w.BrowseButton);
        Flush();
    }

    [AvaloniaFact]
    public void BrowseIsEnabledForAReadyRemoteMachineAndOffWhileItIsNotReady()
    {
        var w = MakeWindow(Tree());
        Assert.True(w.BrowseButton.IsEnabled);        // this machine
        w.MachineCombo.SelectedIndex = 1;             // mini answers at once
        Flush();
        Assert.True(w.BrowseButton.IsEnabled);

        // The negative controls: a machine that declines, is silent or is still
        // being asked is not Ready, and Browse… follows Ready alone.
        Assert.Equal(NewChatMachines.InfoState.Ready, NewChatMachines.From("mini", new PeerLaunchInfo(true, "macos")).State);
        Assert.NotEqual(NewChatMachines.InfoState.Ready, NewChatMachines.From("mini", new PeerLaunchInfo(false, "macos")).State);
        Assert.NotEqual(NewChatMachines.InfoState.Ready, NewChatMachines.From("mini", null).State);
        Assert.NotEqual(NewChatMachines.InfoState.Ready, NewChatMachines.Loading("mini").State);
    }

    [AvaloniaFact]
    public void BrowseStaysOffForAMachineThatDeclines()
    {
        var peers = Tree();
        var w = MakeWindow(peers);
        NewChatWindow.PeerLauncherForTests = new DecliningLauncher();
        var w2 = (NewChatWindow)typeof(NewChatWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: [typeof(NewChatCli?), typeof(string), typeof(string)])!.Invoke([null, null, null]);
        w2.MachineCombo.SelectedIndex = 1;
        Flush();
        Assert.False(w2.BrowseButton.IsEnabled);
        _ = w;
    }

    private sealed class DecliningLauncher : IPeerLauncher
    {
        public IReadOnlyList<PeerMachine> Machines() => [new("mini", true)];
        public Task<PeerLaunchInfo?> InfoAsync(string machine, CancellationToken ct) =>
            Task.FromResult<PeerLaunchInfo?>(new PeerLaunchInfo(false, "macos"));
        public Task<PeerLaunchResult> LaunchAsync(string machine, PeerLaunchSpec spec, CancellationToken ct) =>
            Task.FromResult(new PeerLaunchResult(false, "peer_launch_off", null, null));
        public Task<PeerDirResult> ListDirAsync(string machine, string path, CancellationToken ct) =>
            Task.FromResult(PeerDirResult.Fail("peer_launch_off"));
    }

    [AvaloniaFact]
    public async Task WalkingAndRefusingNeverResizeTheDialogAndAnErrorKeepsTheLastChoice()
    {
        var peers = Tree();
        peers.Dirs["/Users/dev/Source"] = PeerDirResult.Fail("peer_launch_off");
        var b = new RemoteFolderBrowser(peers, "mini");
        var (w, h) = (b.Width, b.Height);

        await b.NavigateAsync("");
        await b.OpenAsync(0);
        await b.OpenAsync(1);                         // refused

        Assert.Equal((w, h), (b.Width, b.Height));
        Assert.False(b.CanResize);
        Assert.Equal(PeerLaunchCopy.ExplainBrowse("peer_launch_off", "mini"), b.StatusText);
        Assert.Equal("/Users/dev", b.Chosen);         // still the folder we were in
    }

    [AvaloniaFact]
    public void ARemoteFolderIsNotCarriedToAnotherMachineAndStartUsesIt()
    {
        var w = MakeWindow(Tree());
        w.MachineCombo.SelectedIndex = 1;
        Flush();
        NewChatWindow.ChooseRemoteFolderForTests = _ => Task.FromResult<string?>("/Users/dev/Source/app");
        ClickBrowse(w);
        Assert.Equal("/Users/dev/Source/app", w.FolderCombo.SelectedItem);
        Assert.True(w.StartButton.IsEnabled);

        w.MachineCombo.SelectedIndex = 0;             // back to this machine
        Flush();
        Assert.DoesNotContain("/Users/dev/Source/app", (IEnumerable<string>)w.FolderCombo.ItemsSource!);
    }

    [AvaloniaFact]
    public void BrowsingARemoteMachineAddsTheFolderToTheComboAndSelectsIt()
    {
        var w = MakeWindow(Tree());
        w.MachineCombo.SelectedIndex = 1;
        Flush();
        string? askedFor = null;
        NewChatWindow.ChooseRemoteFolderForTests = m => { askedFor = m; return Task.FromResult<string?>("/Users/dev/Source/app"); };

        ClickBrowse(w);

        Assert.Equal("mini", askedFor);
        Assert.Equal("/Users/dev/Source/app", w.FolderCombo.SelectedItem);
        Assert.Equal("/Users/dev/Source/app", ((IEnumerable<string>)w.FolderCombo.ItemsSource!).First());
        Assert.Contains("/srv/app", (IEnumerable<string>)w.FolderCombo.ItemsSource!);
    }

    [AvaloniaFact]
    public void CancellingTheRemoteBrowserChangesNothing()
    {
        var w = MakeWindow(Tree());
        w.MachineCombo.SelectedIndex = 1;
        Flush();
        NewChatWindow.ChooseRemoteFolderForTests = _ => Task.FromResult<string?>(null);

        ClickBrowse(w);

        Assert.Equal("/srv/app", w.FolderCombo.SelectedItem);
    }

    [AvaloniaFact]
    public void BrowseOnThisMachineStillUsesTheLocalSeam()
    {
        var w = MakeWindow(Tree());
        var remote = false;
        NewChatWindow.ChooseRemoteFolderForTests = _ => { remote = true; return Task.FromResult<string?>("/x"); };
        NewChatWindow.ChooseFolderForTests = () => Task.FromResult<string?>("/local/pick");
        try
        {
            ClickBrowse(w);
            Assert.False(remote);
            Assert.Equal("/local/pick", w.FolderCombo.SelectedItem);
        }
        finally { NewChatWindow.ChooseFolderForTests = null; }
    }
}
