using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-361: Resume mode in the New chat dialog — list a local CLI's past
// sessions, pick one, and start the CLI with its resume arguments.
//
// Driven entirely through seams (PastSessionIndex.LoadForTests,
// NewChatLauncher.ResumeForTests, OrbForTests, CurrentStatusesForTests,
// NowForTests) with the load made synchronous, so no test waits on a disk, a
// thread or a CLI. Same rule as the other New chat suites: the window is built
// by reflection and never shown or Close()d.
[Collection("Settings")]
public class NewChatWindowResumeTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string Existing = Path.GetTempPath().TrimEnd('/', '\\');

    public NewChatWindowResumeTests()
    {
        NewChatWindow.AccountDirectoryExistsForTests = _ => true;
        NewChatWindow.NowForTests = () => Now;
        NewChatWindow.ResumeLoadInlineForTests = true;
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>();
        NewChatAvailability.CurrentForTests = () => new[]
        {
            Enabled(NewChatCli.ClaudeCode), Enabled(NewChatCli.Codex), Enabled(NewChatCli.Grok)
        };
        NewChatWindow.OpenClawAvailabilityForTests = () => OpenClawNewChatAvailability.Ready;
        NewChatWindow.KnownAgentsForTests = () => new[] { ("aurora", "Aurora") };
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
        NewChatAvailability.CurrentForTests = null;
        NewChatLauncher.ResumeForTests = null;
        PastSessionIndex.LoadForTests = null;
    }

    private static NewChatOption Enabled(NewChatCli cli) => new(cli, Enabled: true, Reason: null, Warning: null);

    private static void FreshSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-newchat-resume-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("ORBWEAVER_SETTINGS_DIR", dir);
        OrbweaverSettings.ReloadForTests();
    }

    private static NewChatWindow NewWindow()
    {
        var ctor = typeof(NewChatWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: new[] { typeof(NewChatCli?), typeof(string), typeof(string) })!;
        return (NewChatWindow)ctor.Invoke(new object?[] { null, null, null });
    }

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static PastSession Session(
        string id, string title, double hoursAgo = 2, string? cwd = null, NewChatCli cli = NewChatCli.ClaudeCode,
        bool missing = false, int messages = 4) =>
        new(cli, id, cwd ?? Existing, title, new DateTimeOffset(Now.AddHours(-hoursAgo), TimeSpan.Zero),
            messages, "/t/" + id + ".jsonl", missing);

    private static NewChatWindow ResumeWindow(params PastSession[] rows)
    {
        FreshSettings();
        PastSessionIndex.LoadForTests = (cli, _, _, _) => rows.Where(r => r.Cli == cli).ToList();
        var window = NewWindow();
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
    public void TheModeRowOffersNewChatAndResumeForALocalCli()
    {
        FreshSettings();
        var window = NewWindow();

        Assert.True(window.LocalModeRow.IsVisible);
        Assert.Equal("New chat", window.LocalNewRadio.Content);
        Assert.Equal("Resume", window.LocalResumeRadio.Content);
        Assert.True(window.LocalNewRadio.IsChecked);
        Assert.False(window.ResumePane.IsVisible);
        Assert.Equal("Start", window.StartButton.Content);
    }

    [AvaloniaFact]
    public void ResumeShowsThePaneInPlaceOfFolderAndRelabelsStart()
    {
        var window = ResumeWindow(Session("a", "Fix the parser"));

        Assert.True(window.ResumePane.IsVisible);
        Assert.False(window.FolderSection.IsVisible);
        Assert.Equal("Resume", window.StartButton.Content);

        window.LocalNewRadio.IsChecked = true;
        Flush();
        Assert.False(window.ResumePane.IsVisible);
        Assert.True(window.FolderSection.IsVisible);
        Assert.Equal("Start", window.StartButton.Content);
    }

    [AvaloniaFact]
    public void TheListIsNewestFirstAndShowsTitleFolderAgeAndCount()
    {
        var window = ResumeWindow(
            Session("old", "Older one", hoursAgo: 30), Session("new", "Newest", hoursAgo: 1, messages: 1));

        Assert.Equal(new[] { "new", "old" }, Shown(window).Select(r => r.Session.Id));
        Assert.Equal(Path.GetFileName(Existing) + " · 1 message", NewChatWindow.ResumeDetail(Shown(window)[0]));
        Assert.EndsWith("4 messages", NewChatWindow.ResumeDetail(Shown(window)[1]));
    }

    // CB-361: the listing leaves the count null; a row's count is read when the
    // row is realised, and "…" stands in until it lands.
    private static string[] RowTexts(NewChatWindow w, PastSessionRow row)
    {
        var control = (Control)w.ResumeList.ItemTemplate!.Build(row)!;
        return control.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
    }

    [AvaloniaFact]
    public void ADetailLineShowsEllipsisWhileCountingAndOmitsAnUnreadableCount()
    {
        var row = new PastSessionRow(Session("a", "T"), false);
        var folder = Path.GetFileName(Existing);

        Assert.Equal(folder + " · …", NewChatWindow.ResumeDetail(row, counting: true));
        Assert.Equal(folder + " · 1 message", NewChatWindow.ResumeDetail(row, 1));
        Assert.Equal(folder + " · 12 messages", NewChatWindow.ResumeDetail(row, 12));
        var unknown = new PastSessionRow(Session("a", "T") with { MessageCount = null }, false);
        Assert.Equal(folder, NewChatWindow.ResumeDetail(unknown));
    }

    [AvaloniaFact]
    public void TheCountIsReadForARealisedRowAndCachedPerFile()
    {
        var reads = new List<string>();
        PastSessions.CountForTests = (_, path) => { reads.Add(path); return 7; };
        try
        {
            var window = ResumeWindow(Session("a", "One") with { MessageCount = null });
            var row = Shown(window)[0];

            Assert.Contains(RowTexts(window, row), t => t.EndsWith("· 7 messages"));
            Assert.Contains(RowTexts(window, row), t => t.EndsWith("· 7 messages"));
            Assert.Single(reads);
        }
        finally { PastSessions.CountForTests = null; }
    }

    [AvaloniaFact]
    public void AnUnreadableTranscriptShowsNoCountRatherThanZero()
    {
        PastSessions.CountForTests = (_, _) => null;
        try
        {
            var window = ResumeWindow(Session("a", "One") with { MessageCount = null });
            var texts = RowTexts(window, Shown(window)[0]);

            Assert.DoesNotContain(texts, t => t.Contains("message") || t.Contains('…'));
        }
        finally { PastSessions.CountForTests = null; }
    }

    [AvaloniaFact]
    public void ACountThatThrowsIsTreatedAsUnreadable()
    {
        PastSessions.CountForTests = (_, _) => throw new IOException("gone");
        try
        {
            var window = ResumeWindow(Session("a", "One") with { MessageCount = null });
            Assert.DoesNotContain(RowTexts(window, Shown(window)[0]), t => t.Contains('…'));
        }
        finally { PastSessions.CountForTests = null; }
    }

    [AvaloniaFact]
    public void OnlyTheSelectedClisSessionsAreListed()
    {
        var window = ResumeWindow(Session("c", "Claude one"), Session("x", "Codex one", cli: NewChatCli.Codex));
        Assert.Equal(new[] { "c" }, Shown(window).Select(r => r.Session.Id));

        window.CliList.Children.OfType<RadioButton>().Single(r => Equals(r.Tag, NewChatCli.Codex)).IsChecked = true;
        Flush();
        Assert.Equal(new[] { "x" }, Shown(window).Select(r => r.Session.Id));
    }

    [AvaloniaFact]
    public void SearchFiltersOnTitleAndFolder()
    {
        var window = ResumeWindow(Session("a", "Fix the parser"), Session("b", "Write docs"));

        window.ResumeSearch.Text = "PARSER";
        Flush();
        Assert.Equal(new[] { "a" }, Shown(window).Select(r => r.Session.Id));

        window.ResumeSearch.Text = "nothing like it";
        Flush();
        Assert.Equal("No matches", window.ResumeEmpty.Text);
        Assert.False(window.ResumeList.IsVisible);
    }

    [AvaloniaFact]
    public void TheFolderFilterNarrowsToOneFolder()
    {
        var other = Path.Combine(Existing, "other");
        var window = ResumeWindow(Session("a", "Here"), Session("b", "There", cwd: other));

        Assert.Equal(3, window.ResumeFolderFilter.ItemCount); // All folders + two
        window.ResumeFolderFilter.SelectedItem = window.ResumeFolderFilter.Items.Cast<NewChatWindow.ResumeFolder>()
            .Single(f => f.Path == other);
        Flush();
        Assert.Equal(new[] { "b" }, Shown(window).Select(r => r.Session.Id));
    }

    [AvaloniaFact]
    public void ShowOlderReadsOlderSessionsOnlyWhenAsked()
    {
        FreshSettings();
        var asked = new List<bool>();
        PastSessionIndex.LoadForTests = (_, _, older, _) =>
        {
            asked.Add(older);
            return older
                ? new[] { Session("a", "Recent"), Session("z", "Ancient", hoursAgo: 24 * 90) }
                : new[] { Session("a", "Recent") };
        };
        var window = NewWindow();
        window.LocalResumeRadio.IsChecked = true;
        Flush();

        Assert.Equal(new[] { "a" }, Shown(window).Select(r => r.Session.Id));
        window.ResumeOlder.IsChecked = true;
        Flush();
        Assert.Equal(new[] { "a", "z" }, Shown(window).Select(r => r.Session.Id));
        Assert.Equal(new[] { false, true }, asked);
    }

    [AvaloniaFact]
    public void AnEmptyHistoryAndALoadFailureBothSayNothingWasFound()
    {
        var window = ResumeWindow();
        Assert.Equal("No sessions in the last 30 days", window.ResumeEmpty.Text);

        PastSessionIndex.LoadForTests = (_, _, _, _) => throw new IOException("disk gone");
        window.ResumeOlder.IsChecked = true;
        Flush();
        Assert.Equal("No past sessions", window.ResumeEmpty.Text);
    }

    [AvaloniaFact]
    public void TheListIsNotReReadByEveryVisibilityPass()
    {
        FreshSettings();
        var loads = 0;
        PastSessionIndex.LoadForTests = (_, _, _, _) => { loads++; return Array.Empty<PastSession>(); };
        var window = NewWindow();
        window.LocalResumeRadio.IsChecked = true;
        Flush();
        window.ResumeSearch.Text = "x";
        window.LocalNewRadio.IsChecked = true;
        window.LocalResumeRadio.IsChecked = true;
        Flush();

        Assert.Equal(1, loads);
    }

    [AvaloniaFact]
    public void AMissingFolderRowSaysSoAndNeverLaunches()
    {
        var launched = false;
        NewChatLauncher.ResumeForTests = (_, _) => { launched = true; return new LaunchResult(LaunchOutcome.Launched, "x"); };
        var window = ResumeWindow(Session("gone", "Lost project", cwd: "/no/such/dir", missing: true));

        Assert.Contains("folder missing", NewChatWindow.ResumeDetail(Shown(window)[0]));
        window.ResumeList.SelectedIndex = 0;
        Click(window.StartButton);

        Assert.False(launched);
        Assert.Equal("That session's folder no longer exists.", window.StatusLine.Text);
    }

    [AvaloniaFact]
    public void StartWithNothingChosenAsksForASession()
    {
        var window = ResumeWindow(Session("a", "One"));
        Click(window.StartButton);
        Assert.Equal("Choose a session first.", window.StatusLine.Text);
    }

    [AvaloniaFact]
    public void ALiveRowFocusesItsOrbInsteadOfLaunching()
    {
        var launched = false;
        NewChatLauncher.ResumeForTests = (_, _) => { launched = true; return new LaunchResult(LaunchOutcome.Launched, "x"); };
        var orb = new OrbWindow("live-1");
        var focused = new List<OrbWindow>();
        NewChatWindow.OrbForTests = id => id == "live-1" ? orb : null;
        NewChatWindow.FocusOrbForTests = focused.Add;
        var window = ResumeWindow(Session("live-1", "Running now"));
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>
        {
            ["live-1"] = new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = Existing }
        };
        window.ResumeSearch.Text = " "; // refresh the rows against the new statuses
        Flush();

        Assert.True(Shown(window)[0].IsLive);
        Assert.Contains("running", NewChatWindow.ResumeDetail(Shown(window)[0]));
        window.ResumeList.SelectedIndex = 0;
        Click(window.StartButton);

        Assert.False(launched);
        Assert.Same(orb, Assert.Single(focused));
        Assert.Equal("Already running — focused its orb.", window.StatusLine.Text);
    }

    // QA 3: live but with no window to go to (a hidden orb) must not claim a focus.
    [AvaloniaFact]
    public void ALiveSessionWithNoOrbWindowSaysItsOrbIsHiddenAndLaunchesNothing()
    {
        var launched = false;
        NewChatLauncher.ResumeForTests = (_, _) => { launched = true; return new LaunchResult(LaunchOutcome.Launched, "x"); };
        NewChatWindow.OrbForTests = _ => null;
        var focused = 0;
        NewChatWindow.FocusOrbForTests = _ => focused++;
        var window = ResumeWindow(Session("live-1", "Running now"));
        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>
        {
            ["live-1"] = new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = Existing }
        };
        window.ResumeList.SelectedIndex = 0;
        Click(window.StartButton);

        Assert.False(launched);
        Assert.Equal(0, focused);
        Assert.Equal("Already running — its orb is hidden.", window.StatusLine.Text);
    }

    // QA 2: the row was listed as not running; the orb started afterwards. The
    // click must ask again rather than start a second process on the session.
    [AvaloniaFact]
    public void AnOrbThatStartedAfterTheListWasBuiltIsStillNotLaunchedTwice()
    {
        var launched = false;
        NewChatLauncher.ResumeForTests = (_, _) => { launched = true; return new LaunchResult(LaunchOutcome.Launched, "x"); };
        var orb = new OrbWindow("late-1");
        var focused = new List<OrbWindow>();
        NewChatWindow.OrbForTests = _ => orb;
        NewChatWindow.FocusOrbForTests = focused.Add;
        var window = ResumeWindow(Session("late-1", "Started meanwhile"));
        Assert.False(Shown(window)[0].IsLive);

        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>
        {
            ["late-1"] = new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = Existing }
        };
        window.ResumeList.SelectedIndex = 0;
        Click(window.StartButton);

        Assert.False(launched);
        Assert.Single(focused);
        Assert.True(Shown(window).Single().IsLive); // and the list now says so
    }

    // QA 1: the launch (which probes the CLI's --help and --version) runs off
    // the UI thread, so a slow one leaves the dialog responsive.
    [AvaloniaFact]
    public void ASlowResumeDoesNotBlockTheUiThreadAndIsNotStartedTwice()
    {
        using var release = new ManualResetEventSlim();
        var calls = 0;
        NewChatLauncher.ResumeForTests = (_, _) =>
        {
            Interlocked.Increment(ref calls);
            release.Wait(TimeSpan.FromSeconds(30));
            return new LaunchResult(LaunchOutcome.Launched, "Claude Code resumed.");
        };
        var window = ResumeWindow(Session("slow-1", "Slow one"));
        NewChatWindow.ResumeLoadInlineForTests = false; // the launch goes to a worker
        window.ResumeList.SelectedIndex = 0;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Click(window.StartButton);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "the click blocked on the launch");
        Assert.Equal("Resuming…", window.StatusLine.Text);
        Assert.False(window.StartButton.IsEnabled);

        window.ResumeClicked(NewChatCli.ClaudeCode); // a second gesture while it runs
        release.Set();
        for (var i = 0; i < 200 && window.StatusLine.Text == "Resuming…"; i++)
        {
            Thread.Sleep(10);
            Flush();
        }

        Assert.Equal("Claude Code resumed.", window.StatusLine.Text);
        Assert.True(window.StartButton.IsEnabled);
        Assert.Equal(1, calls);
    }

    [AvaloniaFact]
    public void AResumeThatThrowsSaysSoAndReenablesStart()
    {
        NewChatLauncher.ResumeForTests = (_, _) => throw new InvalidOperationException("boom");
        var window = ResumeWindow(Session("a", "One"));
        window.ResumeList.SelectedIndex = 0;
        Click(window.StartButton);

        Assert.Equal("Couldn't resume that session: boom", window.StatusLine.Text);
        Assert.True(window.StartButton.IsEnabled);
    }

    // QA 4: when the resumed orb shows up the row stops reading "not running".
    [AvaloniaFact]
    public void TheListIsRefreshedWhenTheResumedOrbAppears()
    {
        var window = ResumeWindow(Session("want", "Mine"));
        Assert.False(Shown(window)[0].IsLive);
        window.ArmResumeWatchForTests("want", DateTime.UtcNow.AddSeconds(20));

        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>
        {
            ["want"] = new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = Existing }
        };
        window.OnWatchTick(null, EventArgs.Empty);

        Assert.True(Shown(window)[0].IsLive);
    }

    // QA 6: a Local-kind clock from a seam must not make the list throw.
    [AvaloniaTheory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void ANonUtcClockDoesNotBreakTheList(DateTimeKind kind)
    {
        NewChatWindow.NowForTests = () => DateTime.SpecifyKind(Now, kind);
        var window = ResumeWindow(Session("a", "One"));

        Assert.Single(Shown(window));
    }

    // QA 5: a persona picture is decoded once, small, and released.
    [AvaloniaFact]
    public void APersonaPictureIsDecodedOncePerFileAndShownInTheRow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-resume-persona-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        using (var bmp = new SkiaSharp.SKBitmap(200, 200))
        using (var image = SkiaSharp.SKImage.FromBitmap(bmp))
        using (var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(Path.Combine(dir, "cto.png"), data.ToArray());
        File.WriteAllLines(Path.Combine(dir, "CLAUDE.md"), new[] { "## Attributes", "Name Jennifer", "Profile Photo cto.png" });

        var window = ResumeWindow(Session("a", "One", cwd: dir), Session("b", "Two", cwd: dir));
        var rows = Shown(window);
        var first = (Control)window.ResumeList.ItemTemplate!.Build(rows[0])!;
        window.ResumeList.ItemTemplate!.Build(rows[1]);

        var picture = first.GetLogicalDescendants().OfType<Image>().Single();
        Assert.True(((Avalonia.Media.Imaging.Bitmap)picture.Source!).PixelSize.Width <= 48);
        Assert.Equal(1, window.ResumeAvatarCount); // two rows, one file, one decode
    }

    [AvaloniaFact]
    public void ChoosingARowLaunchesItsSessionUnderTheSelectedAccount()
    {
        PastSession? got = null;
        string? gotProfile = "unset";
        NewChatLauncher.ResumeForTests = (s, p) =>
        {
            got = s;
            gotProfile = p;
            return new LaunchResult(LaunchOutcome.Launched, "Claude Code resumed.");
        };
        var window = ResumeWindow(Session("abc-123", "Fix it"));
        window.ResumeList.SelectedIndex = 0;
        Click(window.StartButton);

        Assert.Equal("abc-123", got!.Id);
        Assert.Null(gotProfile); // Default account
        Assert.Equal("Claude Code resumed.", window.StatusLine.Text);
        Assert.Equal("ClaudeCode", OrbweaverSettings.NewChatLastCli);
    }

    [AvaloniaFact]
    public void ARefusedLaunchShowsItsMessageAndStartsNoWatch()
    {
        NewChatLauncher.ResumeForTests = (_, _) =>
            new LaunchResult(LaunchOutcome.Unsupported, "This version of claude can't resume from here.");
        var window = ResumeWindow(Session("a", "One"));
        window.ResumeList.SelectedIndex = 0;
        Click(window.StartButton);

        Assert.Equal("This version of claude can't resume from here.", window.StatusLine.Text);
        Assert.True(window.StartButton.IsEnabled);
        window.OnWatchTick(null, EventArgs.Empty); // nothing armed: must be a no-op
        Assert.Equal("This version of claude can't resume from here.", window.StatusLine.Text);
    }

    [AvaloniaFact]
    public void CodexSaysTheOrbAppearsOnTheFirstMessageAndWatchesNothing()
    {
        NewChatLauncher.ResumeForTests = (_, _) => new LaunchResult(LaunchOutcome.Launched, "Codex resumed.");
        var window = ResumeWindow(Session("c1", "Codex chat", cli: NewChatCli.Codex));
        window.CliList.Children.OfType<RadioButton>().Single(r => Equals(r.Tag, NewChatCli.Codex)).IsChecked = true;
        window.LocalResumeRadio.IsChecked = true;
        Flush();
        window.ResumeList.SelectedIndex = 0;
        Click(window.StartButton);

        Assert.Equal("Resumed — the orb appears when you send your first message.", window.StatusLine.Text);
        window.OnWatchTick(null, EventArgs.Empty);
        Assert.Equal("Resumed — the orb appears when you send your first message.", window.StatusLine.Text);
    }

    [AvaloniaFact]
    public void TheWatchMatchesTheExactSessionIdAndIgnoresOtherOrbsInTheFolder()
    {
        var window = ResumeWindow(Session("want", "Mine"));
        window.ArmResumeWatchForTests("want", DateTime.UtcNow.AddSeconds(20));

        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>
        {
            ["other"] = new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = Existing }
        };
        window.StatusLine.Text = null;
        window.OnWatchTick(null, EventArgs.Empty);
        Assert.Null(window.StatusLine.Text);

        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>
        {
            ["want"] = new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = Existing }
        };
        window.OnWatchTick(null, EventArgs.Empty);
        Assert.Equal("Resumed — its orb should be on screen.", window.StatusLine.Text);
    }

    [AvaloniaFact]
    public void TheWatchGivesUpAtItsDeadlineWithTheHookHint()
    {
        var window = ResumeWindow(Session("want", "Mine"));
        window.ArmResumeWatchForTests("want", DateTime.UtcNow.AddSeconds(-1));
        window.OnWatchTick(null, EventArgs.Empty);
        Assert.StartsWith("Terminal opened; no orb yet", window.StatusLine.Text);
    }

    [AvaloniaFact]
    public void AGrokLaunchArmsAWatchForItsSessionId()
    {
        NewChatLauncher.ResumeForTests = (_, _) => new LaunchResult(LaunchOutcome.Launched, "Grok resumed.");
        var window = ResumeWindow(Session("g1", "Grok chat", cli: NewChatCli.Grok));
        window.CliList.Children.OfType<RadioButton>().Single(r => Equals(r.Tag, NewChatCli.Grok)).IsChecked = true;
        window.LocalResumeRadio.IsChecked = true;
        Flush();
        window.ResumeList.SelectedIndex = 0;
        Click(window.StartButton);

        NewChatWindow.CurrentStatusesForTests = () => new Dictionary<string, SessionStatus>
        {
            ["g1"] = new SessionStatus { Source = SessionSource.Grok, Cwd = Existing }
        };
        window.OnWatchTick(null, EventArgs.Empty);
        Assert.Equal("Resumed — its orb should be on screen.", window.StatusLine.Text);
    }

    [AvaloniaFact]
    public void TheLastModeIsRememberedPerCli()
    {
        var window = ResumeWindow(Session("a", "One"));
        Assert.True(OrbweaverSettings.NewChatResumeModeFor(NewChatCli.ClaudeCode));
        Assert.False(OrbweaverSettings.NewChatResumeModeFor(NewChatCli.Codex));

        // Codex was never put in Resume, so it opens on New; going back to
        // Claude Code finds Resume again.
        window.CliList.Children.OfType<RadioButton>().Single(r => Equals(r.Tag, NewChatCli.Codex)).IsChecked = true;
        Flush();
        Assert.True(window.LocalNewRadio.IsChecked);
        window.CliList.Children.OfType<RadioButton>().Single(r => Equals(r.Tag, NewChatCli.ClaudeCode)).IsChecked = true;
        Flush();
        Assert.True(window.LocalResumeRadio.IsChecked);

        window.LocalNewRadio.IsChecked = true;
        Assert.False(OrbweaverSettings.NewChatResumeModeFor(NewChatCli.ClaudeCode));
    }

    [AvaloniaFact]
    public void OpenClawKeepsItsOwnModeRowAndHidesTheLocalOne()
    {
        var window = ResumeWindow(Session("a", "One"));
        window.OpenClawRadio().IsChecked = true;
        Flush();

        Assert.Equal(0, window.LocalModeRow.Opacity);
        Assert.False(window.LocalModeRow.IsHitTestVisible);
        Assert.False(window.ResumePane.IsVisible);
        Assert.Equal("Open existing", window.OpenExistingRadio.Content);
    }

    [AvaloniaFact]
    public async Task ResumeIsDisabledWhileARemoteMachineIsSelected()
    {
        FreshSettings();
        var fake = new NewChatWindowMachineTests.FakeLauncher();
        fake.Answers["mini"] = NewChatWindowMachineTests.Info();
        NewChatWindow.PeerLauncherForTests = fake;
        NewChatWindow.PeerLinkOnForTests = true;
        PastSessionIndex.LoadForTests = (_, _, _, _) => new[] { Session("a", "One") };
        var window = NewWindow();
        window.LocalResumeRadio.IsChecked = true;
        Flush();
        Assert.True(window.ResumePane.IsVisible);

        window.MachineCombo.SelectedIndex = 1;
        await Task.Delay(50);
        Flush();

        Assert.False(window.LocalResumeRadio.IsEnabled);
        Assert.False(window.ResumePane.IsVisible);
        Assert.Equal("Start", window.StartButton.Content);
        Assert.NotNull(ToolTip.GetTip(window.LocalResumeRadio));
    }

    [AvaloniaFact]
    public void PrefillOpensOnResumeForTheOrbsCliAccountAndFolder()
    {
        FreshSettings();
        var folder = Path.Combine(Existing, "proj");
        PastSessionIndex.LoadForTests = (_, _, _, _) => new[]
        {
            Session("a", "Elsewhere"), Session("b", "In the project", cwd: folder)
        };
        var window = NewWindow();
        window.PrefillResume(NewChatCli.Codex, folder, null);
        Flush();

        Assert.True(window.LocalResumeRadio.IsChecked);
        Assert.Equal(folder, (window.ResumeFolderFilter.SelectedItem as NewChatWindow.ResumeFolder)?.Path);
    }
}
