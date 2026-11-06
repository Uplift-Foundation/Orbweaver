using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Orbweaver.Tests;

// CB-308's UI-side decisions, all of them pure: when the post-update notice
// shows, what a checker result should put on screen, the sentences the Settings
// group writes, and which failure markers a finisher left behind. None of this
// needs a window, which is why it is here and not in UiTests.
public class UpdateUiLogicTests
{
    // ---- UpdatePostInstallNotice.Decide ------------------------------------

    [Fact]
    public void WindowsNeverGetsTheNoticeBecauseThereIsNoGrantToLose()
    {
        var d = UpdatePostInstallNotice.Decide(false, "0.5.0", "0.6.0", true, true);
        Assert.False(d.Show);
        Assert.False(d.OfferLocalNetwork);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AFreshInstallHasNoPreviousRunAndIsNotAnUpdate(string? last)
    {
        Assert.False(UpdatePostInstallNotice.Decide(true, last, "0.6.0", true, true).Show);
    }

    [Fact]
    public void TheSameVersionAsLastTimeIsJustALaunch()
    {
        Assert.False(UpdatePostInstallNotice.Decide(true, "0.6.0", "0.6.0", true, true).Show);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void AMacUpdateAlwaysSaysTheSentenceAndOffersTheButtonOnlyWhenSomethingNeedsLocalNetwork(
        bool openClaw, bool peerLink, bool button)
    {
        var d = UpdatePostInstallNotice.Decide(true, "0.5.0", "0.6.0", openClaw, peerLink);
        Assert.True(d.Show);
        Assert.Equal(button, d.OfferLocalNetwork);
    }

    [Fact]
    public void TheSentenceNamesEveryGrantAnUpdateResets()
    {
        foreach (var grant in new[] { "Local Network", "Automation", "Accessibility", "Microphone" })
            Assert.Contains(grant, UpdatePostInstallNotice.Sentence);
    }

    [Fact]
    public void RunForRecordsTheVersionBeforeShowingAndShowsOnlyWhenDecided()
    {
        var order = new List<string>();

        var shown = UpdatePostInstallNotice.RunFor(true, "0.5.0", "0.6.0", true, false,
            v => order.Add("record:" + v), d => order.Add("show:" + d.OfferLocalNetwork));
        Assert.True(shown.Show);
        Assert.Equal(new[] { "record:0.6.0", "show:True" }, order);

        order.Clear();
        var quiet = UpdatePostInstallNotice.RunFor(true, "0.6.0", "0.6.0", true, false,
            v => order.Add("record:" + v), d => order.Add("show"));
        Assert.False(quiet.Show);
        Assert.Equal(new[] { "record:0.6.0" }, order);
    }

    // ---- UpdateServices.Route ----------------------------------------------

    private static UpdateRelease Release(string version = "0.6.0") =>
        new("v" + version, version, false, "notes", Array.Empty<UpdateAsset>());

    private static UpdateCheckResult Result(UpdateCheckOutcome outcome, bool manual, UpdateRelease? release = null,
        string? error = null) => new(outcome, release, error, manual);

    [Fact]
    public void AnAutomaticAvailableUpdateOpensTheNoticeOncePerReleasePerSession()
    {
        var offered = new HashSet<string>();
        var result = Result(UpdateCheckOutcome.Available, false, Release());

        Assert.Equal(UpdateServices.Step.OpenNotice, UpdateServices.Route(result, "0.5.0", offered).Do);
        Assert.Equal(UpdateServices.Step.None, UpdateServices.Route(result, "0.5.0", offered).Do);

        var newer = Result(UpdateCheckOutcome.Available, false, Release("0.7.0"));
        Assert.Equal(UpdateServices.Step.OpenNotice, UpdateServices.Route(newer, "0.5.0", offered).Do);
    }

    [Fact]
    public void AManualAvailableUpdateAlwaysOpensTheNoticeEvenIfOfferedBefore()
    {
        var offered = new HashSet<string> { "v0.6.0" };
        var result = Result(UpdateCheckOutcome.Available, true, Release());
        Assert.Equal(UpdateServices.Step.OpenNotice, UpdateServices.Route(result, "0.5.0", offered).Do);
    }

    [Fact]
    public void AManualCheckAlwaysAnswersUpToDateAndFailure()
    {
        var offered = new HashSet<string>();

        var upToDate = UpdateServices.Route(Result(UpdateCheckOutcome.UpToDate, true), "0.5.0", offered);
        Assert.Equal(UpdateServices.Step.ShowMessage, upToDate.Do);
        Assert.Contains("0.5.0", upToDate.Message);

        var failed = UpdateServices.Route(Result(UpdateCheckOutcome.Failed, true, error: "offline"), "0.5.0", offered);
        Assert.Equal(UpdateServices.Step.ShowMessage, failed.Do);
        Assert.Contains("offline", failed.Message);

        var noReason = UpdateServices.Route(Result(UpdateCheckOutcome.Failed, true), "0.5.0", offered);
        Assert.Equal("The update check failed.", noReason.Message);
    }

    // The negative control for the rule above: an automatic check that finds
    // nothing, or fails, must say nothing at all.
    [Fact]
    public void AnAutomaticCheckThatFindsNothingStaysQuiet()
    {
        foreach (var outcome in new[] { UpdateCheckOutcome.UpToDate, UpdateCheckOutcome.Failed })
            Assert.Equal(UpdateServices.Step.None,
                UpdateServices.Route(Result(outcome, false, error: "x"), "0.5.0", new HashSet<string>()).Do);
    }

    [Fact]
    public void AnAvailableResultWithNoReleaseIsIgnoredRatherThanCrashing()
    {
        Assert.Equal(UpdateServices.Step.None,
            UpdateServices.Route(Result(UpdateCheckOutcome.Available, true), "0.5.0", new HashSet<string>()).Do);
    }

    [Fact]
    public void OnCompletedDrivesTheOfferAndMessageSeams()
    {
        var offers = new List<string>();
        var messages = new List<string>();
        UpdateServices.OfferForTests = r => offers.Add(r.Version);
        UpdateServices.MessageForTests = (t, m) => messages.Add(t);
        try
        {
            UpdateServices.OnCompleted(Result(UpdateCheckOutcome.Available, true, Release("9.9.9")), "0.5.0");
            UpdateServices.OnCompleted(Result(UpdateCheckOutcome.UpToDate, true), "0.5.0");
            UpdateServices.OnCompleted(Result(UpdateCheckOutcome.UpToDate, false), "0.5.0");
        }
        finally
        {
            UpdateServices.OfferForTests = null;
            UpdateServices.MessageForTests = null;
        }

        Assert.Equal(new[] { "9.9.9" }, offers);
        Assert.Equal(new[] { "You're up to date" }, messages);
    }

    // ---- UpdateSettingsText -------------------------------------------------

    [Fact]
    public void LastCheckedReadsAsAnAgeInTheLargestSensibleUnit()
    {
        var now = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("Not checked yet.", UpdateSettingsText.LastChecked(null, now));
        Assert.Equal("Last checked just now.", UpdateSettingsText.LastChecked(now.AddSeconds(-30), now));
        Assert.Equal("Last checked just now.", UpdateSettingsText.LastChecked(now.AddMinutes(5), now));
        Assert.Equal("Last checked 5 min ago.", UpdateSettingsText.LastChecked(now.AddMinutes(-5), now));
        Assert.Equal("Last checked 3 h ago.", UpdateSettingsText.LastChecked(now.AddHours(-3), now));
        Assert.Equal("Last checked 2 d ago.", UpdateSettingsText.LastChecked(now.AddDays(-2), now));
    }

    [Fact]
    public void ResultTextCoversEveryOutcome()
    {
        Assert.Contains("0.6.0", UpdateSettingsText.Result(Result(UpdateCheckOutcome.Available, true, Release())));
        Assert.Contains("offline", UpdateSettingsText.Result(Result(UpdateCheckOutcome.Failed, true, error: "offline")));
        Assert.Contains("up to date", UpdateSettingsText.Result(Result(UpdateCheckOutcome.UpToDate, true)));
    }

    // ---- UpdateFailureNotice ------------------------------------------------

    private static string Scratch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb308-failure-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void FindReadsEveryFailedMarkerAndIgnoresFoldersWithout()
    {
        var root = Scratch();
        Directory.CreateDirectory(Path.Combine(root, "0.5.1"));
        Directory.CreateDirectory(Path.Combine(root, "0.6.0"));
        Directory.CreateDirectory(Path.Combine(root, "0.7.0"));
        File.WriteAllText(Path.Combine(root, "0.6.0", "failed"), "  disk full \n");
        File.WriteAllText(Path.Combine(root, "0.7.0", "failed"), "");
        File.WriteAllText(Path.Combine(root, "0.5.1", "finished"), "0.5.1");

        var found = UpdateFailureNotice.Find(root);

        Assert.Equal(new[] { "0.6.0", "0.7.0" }, found.Select(f => f.Version));
        Assert.Equal("disk full", found[0].Reason);
        Assert.Equal("The installer did not say why.", found[1].Reason);
    }

    [Fact]
    public void FindOnAMissingFolderIsEmpty()
    {
        Assert.Empty(UpdateFailureNotice.Find(Path.Combine(Scratch(), "nope")));
    }

    [Fact]
    public void RunShowsEachFailureOnceThenClearsItsMarker()
    {
        var root = Scratch();
        Directory.CreateDirectory(Path.Combine(root, "0.6.0"));
        var marker = Path.Combine(root, "0.6.0", "failed");
        File.WriteAllText(marker, "codesign said no");
        var shown = new List<string>();

        Assert.Equal(1, UpdateFailureNotice.Run(root, (t, m) => shown.Add(m)));
        Assert.Contains("0.6.0", shown.Single());
        Assert.Contains("codesign said no", shown.Single());
        Assert.False(File.Exists(marker));

        // A second launch finds nothing: the control that says Clear worked.
        Assert.Equal(0, UpdateFailureNotice.Run(root, (t, m) => shown.Add(m)));
        Assert.Single(shown);
    }

    [Fact]
    public void ClearingAMarkerThatIsNotThereIsHarmless()
    {
        UpdateFailureNotice.Clear(Scratch(), "9.9.9");
    }

    // ---- UpdateMenu ---------------------------------------------------------

    [Fact]
    public void TheMenuLabelNamesTheVersionOnlyWhenOneIsPending()
    {
        Assert.Equal("Check for updates…", UpdateMenu.Label(null));
        Assert.Equal("Update to 0.6.0 available…", UpdateMenu.Label("0.6.0"));
    }

    [Fact]
    public void ActivateOpensThePendingReleaseOrElseChecks()
    {
        var calls = new List<string>();
        UpdateMenu.OfferForTests = r => calls.Add("offer:" + r.Version);
        UpdateMenu.CheckForTests = () => calls.Add("check");
        try
        {
            UpdateMenu.PendingForTests = () => null;
            UpdateMenu.Activate();

            UpdateMenu.PendingForTests = () => Release("0.6.0");
            UpdateMenu.Activate();
            Assert.Equal("0.6.0", UpdateMenu.PendingVersion());
        }
        finally
        {
            UpdateMenu.PendingForTests = null;
            UpdateMenu.OfferForTests = null;
            UpdateMenu.CheckForTests = null;
        }

        Assert.Equal(new[] { "check", "offer:0.6.0" }, calls);
    }

    [Fact]
    public void WithNoCheckerAtAllNothingIsPending()
    {
        Assert.Null(UpdateMenu.PendingVersion());
    }

    // ---- UpdateServices with a checker --------------------------------------

    private sealed class FakeSettings : IUpdateSettings
    {
        public bool AutoCheck { get; set; } = true;
        public bool IncludePrerelease { get; set; }
        public string? SkippedVersion { get; set; }
        public DateTimeOffset? LastCheck { get; set; }
    }

    private sealed class FakeFeed : IReleaseFeed
    {
        public List<UpdateRelease> Releases = new();
        public System.Threading.Tasks.Task<IReadOnlyList<UpdateRelease>> FetchAsync(
            bool includePrerelease, System.Threading.CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<UpdateRelease>>(Releases);
    }

    private sealed class NoTimer : IDisposable { public void Dispose() { } }

    private static (UpdateChecker Checker, FakeSettings Settings, List<TimeSpan> Scheduled, FakeFeed Feed) Checker()
    {
        var settings = new FakeSettings();
        var scheduled = new List<TimeSpan>();
        var feed = new FakeFeed();
        var checker = new UpdateChecker(feed, settings, "0.5.0", () => DateTimeOffset.UtcNow, _ => { },
            (delay, _) => { scheduled.Add(delay); return new NoTimer(); });
        return (checker, settings, scheduled, feed);
    }

    [Fact]
    public async System.Threading.Tasks.Task SkipClearAndRescheduleGoThroughTheCheckerWhenThereIsOne()
    {
        var (checker, settings, scheduled, feed) = Checker();
        feed.Releases.Add(Release("0.6.0"));
        UpdateServices.Checker = checker;
        try
        {
            var result = await UpdateServices.CheckNowAsync();
            Assert.Equal(UpdateCheckOutcome.Available, result.Outcome);
            Assert.NotNull(checker.Pending);

            UpdateServices.Skip(Release("0.6.0"));
            Assert.Equal("0.6.0", settings.SkippedVersion);
            Assert.Null(checker.Pending);

            UpdateServices.ClearSkipped();
            Assert.Null(settings.SkippedVersion);

            UpdateServices.Reschedule();
            Assert.NotEmpty(scheduled);
        }
        finally
        {
            UpdateServices.Checker = null;
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task WithNoCheckerACheckNowSaysSoAndSkipFallsBackToTheSetting()
    {
        UpdateServices.Checker = null;

        var result = await UpdateServices.CheckNowAsync();
        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);

        UpdateServices.Reschedule();
        UpdateServices.Skip(Release("0.6.0"));
        Assert.Equal("0.6.0", OrbweaverSettings.SkippedUpdateVersion);
        UpdateServices.ClearSkipped();
        Assert.Null(OrbweaverSettings.SkippedUpdateVersion);
    }
}
