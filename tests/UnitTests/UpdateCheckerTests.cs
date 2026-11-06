using Xunit;

namespace Orbweaver.Tests;

// The checker with every dependency replaced: a fake feed, in-memory settings, a
// manual clock and a timer that records what it was asked and fires on demand.
public class UpdateCheckerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static UpdateRelease Rel(string v, bool pre = false) => new("v" + v, v, pre, "notes", []);

    private sealed class Settings : IUpdateSettings
    {
        public bool AutoCheck { get; set; } = true;
        public bool IncludePrerelease { get; set; }
        public string? SkippedVersion { get; set; }
        public DateTimeOffset? LastCheck { get; set; }
    }

    private sealed class Feed : IReleaseFeed
    {
        public IReadOnlyList<UpdateRelease> Releases = [];
        public Exception? Throw;
        public int Calls;
        public bool? LastIncludePre;
        public TaskCompletionSource<bool>? Gate;

        public async Task<IReadOnlyList<UpdateRelease>> FetchAsync(bool includePrerelease, CancellationToken ct)
        {
            Calls++;
            LastIncludePre = includePrerelease;
            if (Gate is not null) await Gate.Task.WaitAsync(ct);
            if (Throw is not null) throw Throw;
            return Releases;
        }
    }

    private sealed class Rig
    {
        public Settings S = new();
        public Feed F = new();
        public DateTimeOffset Now = T0;
        public List<string> Log = [];
        public List<(TimeSpan Delay, Action Fire, Timer Handle)> Timers = [];
        public List<UpdateCheckResult> Results = [];
        public int PendingChanges;
        public UpdateChecker C;

        public Rig(string current = "1.0.3")
        {
            C = new UpdateChecker(F, S, current, () => Now, Log.Add, (d, a) =>
            {
                var t = new Timer(d, a);
                Timers.Add((d, a, t));
                return t;
            });
            C.Completed += Results.Add;
            C.PendingChanged += () => PendingChanges++;
        }

        public sealed class Timer(TimeSpan d, Action a) : IDisposable
        {
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        public TimeSpan LastDelay => Timers[^1].Delay;
    }

    // ---- schedule ------------------------------------------------------------

    [Fact]
    public void StartSchedulesTheStartupDelayWhenNeverChecked()
    {
        var r = new Rig();
        r.C.Start();
        Assert.Equal(UpdateSchedule.StartupDelay, r.LastDelay);
    }

    [Fact]
    public void StartHonoursTheLastCheckTime()
    {
        var r = new Rig();
        r.S.LastCheck = T0.AddHours(-2);
        r.C.Start();
        Assert.Equal(TimeSpan.FromHours(22), r.LastDelay);
    }

    [Fact]
    public void StartDoesNothingWhileAutoCheckIsOff()
    {
        var r = new Rig();
        r.S.AutoCheck = false;
        r.C.Start();
        Assert.Empty(r.Timers);
        Assert.Equal(0, r.F.Calls);   // and no network call either
    }

    [Fact]
    public void FlippingTheSwitchReschedulesAndReplacesTheOldTimer()
    {
        var r = new Rig();
        r.C.Start();
        r.S.AutoCheck = false;
        r.C.Reschedule();
        Assert.True(r.Timers[0].Handle.Disposed);
        Assert.Single(r.Timers);

        r.S.AutoCheck = true;
        r.C.Reschedule();
        Assert.Equal(2, r.Timers.Count);
    }

    [Fact]
    public async Task AnAutomaticSuccessReschedulesAFullInterval()
    {
        var r = new Rig();
        r.F.Releases = [Rel("1.0.3")];
        await r.C.RunAutomaticAsync();
        Assert.Equal(UpdateSchedule.Interval, r.LastDelay);
        Assert.Equal(T0, r.S.LastCheck);
    }

    [Fact]
    public async Task AnAutomaticFailureRetriesInAnHourAndLeavesLastCheckAlone()
    {
        var r = new Rig();
        r.F.Throw = new UpdateException(UpdateFailure.Network, "no route");
        await r.C.RunAutomaticAsync();
        Assert.Equal(UpdateSchedule.FailureRetry, r.LastDelay);
        Assert.Null(r.S.LastCheck);
    }

    [Fact]
    public async Task AnAutomaticRunWithAutoOffDoesNothing()
    {
        var r = new Rig();
        r.S.AutoCheck = false;
        await r.C.RunAutomaticAsync();
        Assert.Equal(0, r.F.Calls);
        Assert.Empty(r.Timers);
    }

    [Fact]
    public async Task TheTimerCallbackRunsACheckAndSchedulesTheNext()
    {
        var r = new Rig();
        r.F.Releases = [Rel("1.0.3")];
        r.C.Start();
        r.Timers[0].Fire();
        for (var i = 0; i < 400 && r.Timers.Count < 2; i++) await Task.Delay(5);
        Assert.Equal(1, r.F.Calls);
        Assert.Equal(2, r.Timers.Count);
    }

    // ---- reporting rule ------------------------------------------------------

    [Fact]
    public async Task AManualCheckAlwaysReportsUpToDate()
    {
        var r = new Rig();
        r.F.Releases = [Rel("1.0.3")];
        var res = await r.C.CheckNowAsync();
        Assert.Equal(UpdateCheckOutcome.UpToDate, res.Outcome);
        Assert.True(res.Manual);
        Assert.Single(r.Results);
        Assert.Null(r.C.Pending);
    }

    [Fact]
    public async Task AManualCheckAlwaysReportsAFailure()
    {
        var r = new Rig();
        r.F.Throw = new InvalidOperationException("kaboom");
        var res = await r.C.CheckNowAsync();
        Assert.Equal(UpdateCheckOutcome.Failed, res.Outcome);
        Assert.Equal("kaboom", res.Error);
        Assert.Single(r.Results);
        Assert.Contains(r.Log, l => l.Contains("kaboom"));
    }

    [Fact]
    public async Task AnAutomaticUpToDateOrFailureIsSilentButAnAvailableIsAnnounced()
    {
        var r = new Rig();
        r.F.Releases = [Rel("1.0.3")];
        await r.C.RunAutomaticAsync();
        r.F.Throw = new UpdateException(UpdateFailure.RateLimited, "limited");
        await r.C.RunAutomaticAsync();
        Assert.Empty(r.Results);
        Assert.Contains(r.Log, l => l.Contains("limited"));

        r.F.Throw = null;
        r.F.Releases = [Rel("1.0.4")];
        await r.C.RunAutomaticAsync();
        var announced = Assert.Single(r.Results);
        Assert.Equal(UpdateCheckOutcome.Available, announced.Outcome);
        Assert.False(announced.Manual);
        Assert.Equal("v1.0.4", r.C.Pending!.Tag);
        Assert.Equal(1, r.PendingChanges);
    }

    [Fact]
    public async Task AFailureKeepsWhateverWasPendingAndDoesNotFirePendingChanged()
    {
        var r = new Rig();
        r.F.Releases = [Rel("1.0.4")];
        await r.C.RunAutomaticAsync();
        r.F.Throw = new UpdateException(UpdateFailure.Network, "offline");
        await r.C.RunAutomaticAsync();
        Assert.Equal("v1.0.4", r.C.Pending!.Tag);
        Assert.Equal(1, r.PendingChanges);
    }

    [Fact]
    public async Task APendingUpdateClearsWhenALaterCheckSaysUpToDate()
    {
        var r = new Rig();
        r.F.Releases = [Rel("1.0.4")];
        await r.C.CheckNowAsync();
        r.F.Releases = [Rel("1.0.3")];
        await r.C.CheckNowAsync();
        Assert.Null(r.C.Pending);
        Assert.Equal(2, r.PendingChanges);
    }

    // ---- skip ----------------------------------------------------------------

    [Fact]
    public async Task AnAutomaticCheckStaysQuietAboutASkippedVersionAndLogsWhy()
    {
        var r = new Rig();
        r.S.SkippedVersion = "1.0.4";
        r.F.Releases = [Rel("1.0.4")];
        await r.C.RunAutomaticAsync();
        Assert.Empty(r.Results);
        Assert.Null(r.C.Pending);
        Assert.Contains(r.Log, l => l.Contains("skipped"));
    }

    [Fact]
    public async Task AManualCheckOffersTheSkippedVersionAnyway()
    {
        var r = new Rig();
        r.S.SkippedVersion = "1.0.4";
        r.F.Releases = [Rel("1.0.4")];
        var res = await r.C.CheckNowAsync();
        Assert.Equal(UpdateCheckOutcome.Available, res.Outcome);
        Assert.Equal("v1.0.4", r.C.Pending!.Tag);
    }

    [Fact]
    public async Task SkipPersistsTheVersionAndClearsPendingOnlyForThatRelease()
    {
        var r = new Rig();
        r.F.Releases = [Rel("1.0.4")];
        await r.C.CheckNowAsync();

        r.C.Skip(Rel("1.0.9"));                 // a different release: pending untouched
        Assert.Equal("1.0.9", r.S.SkippedVersion);
        Assert.NotNull(r.C.Pending);

        r.C.Skip(Rel("1.0.4"));
        Assert.Equal("1.0.4", r.S.SkippedVersion);
        Assert.Null(r.C.Pending);

        r.C.ClearSkipped();
        Assert.Null(r.S.SkippedVersion);
    }

    // ---- prerelease and the feed argument -------------------------------------

    [Fact]
    public async Task ThePrereleaseSettingReachesTheFeedAndThePick()
    {
        var r = new Rig();
        r.F.Releases = [Rel("1.1.0-beta", pre: true)];

        Assert.Equal(UpdateCheckOutcome.UpToDate, (await r.C.CheckNowAsync()).Outcome);
        Assert.False(r.F.LastIncludePre);

        r.S.IncludePrerelease = true;
        Assert.Equal(UpdateCheckOutcome.Available, (await r.C.CheckNowAsync()).Outcome);
        Assert.True(r.F.LastIncludePre);
    }

    // ---- coalescing and lifetime ----------------------------------------------

    [Fact]
    public async Task ASecondManualClickJoinsTheRunningCheck()
    {
        var r = new Rig();
        r.F.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        r.F.Releases = [Rel("1.0.3")];

        var a = r.C.CheckNowAsync();
        var b = r.C.CheckNowAsync();
        r.F.Gate.SetResult(true);
        await Task.WhenAll(a, b);

        Assert.Equal(1, r.F.Calls);
    }

    [Fact]
    public async Task AManualClickJoiningAnAutomaticCheckStillGetsAnAnswer()
    {
        var r = new Rig();
        r.F.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        r.F.Releases = [Rel("1.0.3")];

        var auto = r.C.RunAutomaticAsync();
        var manual = r.C.CheckNowAsync();
        r.F.Gate.SetResult(true);
        await auto;
        var res = await manual;

        Assert.Equal(1, r.F.Calls);
        Assert.True(res.Manual);
        Assert.Single(r.Results);                       // announced once, as the manual answer
        Assert.True(r.Results[0].Manual);
    }

    [Fact]
    public async Task AManualClickJoiningAnAutomaticAvailableIsNotAnnouncedTwice()
    {
        var r = new Rig();
        r.F.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        r.F.Releases = [Rel("1.0.4")];

        var auto = r.C.RunAutomaticAsync();
        var manual = r.C.CheckNowAsync();
        r.F.Gate.SetResult(true);
        await auto;
        var res = await manual;

        Assert.Equal(UpdateCheckOutcome.Available, res.Outcome);
        Assert.True(res.Manual);
        Assert.Single(r.Results);                       // the automatic announcement only
    }

    [Fact]
    public async Task AManualClickJoiningAnAutomaticCheckOfASkippedVersionIsOfferedItAnyway()
    {
        var r = new Rig();
        r.S.SkippedVersion = "1.0.4";
        r.F.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        r.F.Releases = [Rel("1.0.4")];

        var auto = r.C.RunAutomaticAsync();       // holds the fetch open
        var manual = r.C.CheckNowAsync();         // joins it
        r.F.Gate.SetResult(true);
        await auto;
        var res = await manual;

        Assert.Equal(1, r.F.Calls);               // re-decided from the same fetch: no second request
        Assert.Equal(UpdateCheckOutcome.Available, res.Outcome);
        Assert.Equal("v1.0.4", res.Release!.Tag);
        Assert.True(res.Manual);
        Assert.Equal("v1.0.4", r.C.Pending!.Tag);
        // The automatic half stayed quiet (negative control): the only announcement
        // is the manual one, and the skip setting itself was left alone.
        var announced = Assert.Single(r.Results);
        Assert.True(announced.Manual);
        Assert.Equal(UpdateCheckOutcome.Available, announced.Outcome);
        Assert.Equal("1.0.4", r.S.SkippedVersion);
    }

    [Fact]
    public async Task AManualClickJoiningAnAutomaticFailureIsToldAboutTheFailure()
    {
        var r = new Rig();
        r.F.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        r.F.Throw = new UpdateException(UpdateFailure.Network, "offline");

        var auto = r.C.RunAutomaticAsync();
        var manual = r.C.CheckNowAsync();
        r.F.Gate.SetResult(true);
        await auto;
        var res = await manual;

        Assert.Equal(UpdateCheckOutcome.Failed, res.Outcome);
        Assert.Equal("offline", res.Error);
        Assert.True(res.Manual);
        Assert.Equal(1, r.F.Calls);
        Assert.Single(r.Results);
    }

    [Fact]
    public async Task DisposeCancelsAnInFlightCheckAndStopsRescheduling()
    {
        var r = new Rig();
        r.F.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = r.C.RunAutomaticAsync();
        r.C.Dispose();
        await run;
        Assert.Empty(r.Timers);                         // no timer scheduled after dispose
        Assert.Contains(r.Log, l => l.Contains("update check failed"));

        r.C.Reschedule();                               // and a late Reschedule is inert
        Assert.Empty(r.Timers);
    }

    [Fact]
    public void DisposeDisposesTheTimer()
    {
        var r = new Rig();
        r.C.Start();
        r.C.Dispose();
        Assert.True(r.Timers[0].Handle.Disposed);
    }

    // ---- version override and wiring -------------------------------------------

    [Theory]
    [InlineData(null, "1.0.3+abc", "1.0.3", false)]
    [InlineData("", "1.0.3+abc", "1.0.3", false)]
    [InlineData("0.0.1", "1.0.3+abc", "0.0.1", true)]
    [InlineData(" 0.0.1 ", "1.0.3", "0.0.1", true)]
    [InlineData("banana", "1.0.3", "1.0.3", true)]
    [InlineData(null, null, "0.0.0", false)]
    public void TheVersionOverrideIsAppliedAndWarnedAboutLoudly(string? env, string? informational, string expected, bool warns)
    {
        var v = UpdateChecker.ResolveCurrentVersion(env, informational, out var warning);
        Assert.Equal(expected, v);
        Assert.Equal(warns, warning is not null);
        if (warns) Assert.StartsWith("WARNING:", warning);
    }

    [Fact]
    public void CreateReadsBothLeversThroughBrandEnvAndLogsThem()
    {
        var log = new List<string>();
        var env = new Dictionary<string, string>
        {
            ["ORBWEAVER_UPDATE_CURRENT_VERSION"] = "0.0.1",
            ["CLAUDE_BUDDY_UPDATE_FEED_URL"] = "http://127.0.0.1:1/",   // legacy spelling still works
        };
        using var c = UpdateChecker.Create(log.Add, k => env.GetValueOrDefault(k));

        Assert.Equal("0.0.1", c.CurrentVersion);
        Assert.Equal(2, log.Count);
        Assert.Contains(log, l => l.Contains("pretending to be 0.0.1"));
        Assert.Contains(log, l => l.Contains("http://127.0.0.1:1/"));
    }

    [Fact]
    public void CreateWithNoLeversIsSilentAndUsesTheBuildsOwnVersion()
    {
        var log = new List<string>();
        using var c = UpdateChecker.Create(log.Add, _ => null);
        Assert.Empty(log);
        Assert.True(UpdateVersion.TryParse(c.CurrentVersion, out _));
    }

    [Fact]
    public void CreateWithTheDefaultEnvironmentReaderDoesNotThrow()
    {
        using var c = UpdateChecker.Create(_ => { });
        Assert.NotNull(c);
    }

    [Fact]
    public async Task TheRealTimerFiresOnceAfterItsDelay()
    {
        var fired = new TaskCompletionSource();
        using var t = UpdateTimers.Threading(TimeSpan.FromMilliseconds(10), () => fired.TrySetResult());
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // ---- the real settings adapter ----------------------------------------------

    [Collection("Settings")]
    public class RealSettings
    {
        [Fact]
        public void TheAdapterForwardsToOrbweaverSettings()
        {
            var s = new OrbweaverUpdateSettings();
            var wasAuto = OrbweaverSettings.AutoCheckForUpdates;
            var wasPre = OrbweaverSettings.IncludePrereleaseUpdates;
            try
            {
                OrbweaverSettings.AutoCheckForUpdates = false;
                OrbweaverSettings.IncludePrereleaseUpdates = true;
                Assert.False(s.AutoCheck);
                Assert.True(s.IncludePrerelease);

                s.SkippedVersion = "1.2.3";
                Assert.Equal("1.2.3", OrbweaverSettings.SkippedUpdateVersion);
                Assert.Equal("1.2.3", s.SkippedVersion);

                var at = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
                s.LastCheck = at;
                Assert.Equal(at, s.LastCheck);
            }
            finally
            {
                OrbweaverSettings.AutoCheckForUpdates = wasAuto;
                OrbweaverSettings.IncludePrereleaseUpdates = wasPre;
                OrbweaverSettings.SkippedUpdateVersion = null;
                OrbweaverSettings.LastUpdateCheckUtc = null;
            }
        }
    }
}
