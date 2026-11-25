using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-424: the launch's first scan must not wait on a slow `claude agents --json`
// before orbs can be drawn.
public class FirstScanJobListingTests
{
    private static ScanProbes Gather(Func<Dictionary<string, string>?> listing, TimeSpan? budget) =>
        ScanProbes.Gather(
            new ScanProbePlan(Array.Empty<SessionStatus>(), true, false, Array.Empty<string>()),
            new Dictionary<int, AgentTeam.Membership>(),
            _ => new Dictionary<TmuxPaneKey, string?>(),
            listing,
            () => null,
            _ => null,
            jobListingBudget: budget);

    [Fact]
    public void ASlowListingDoesNotHoldTheFirstGatherPastItsBudget()
    {
        using var release = new ManualResetEventSlim(false);
        var started = Stopwatch.StartNew();

        var probes = Gather(() =>
        {
            release.Wait(TimeSpan.FromSeconds(30));
            return new Dictionary<string, string> { ["a"] = "running" };
        }, TimeSpan.FromMilliseconds(100));

        var waited = started.Elapsed;
        release.Set();

        Assert.Null(probes.Jobs);
        Assert.True(waited < TimeSpan.FromSeconds(10), $"waited {waited.TotalMilliseconds} ms for the listing");
    }

    [Fact]
    public void AListingInsideTheBudgetIsUsed()
    {
        var probes = Gather(() => new Dictionary<string, string> { ["a"] = "running" }, TimeSpan.FromSeconds(30));

        Assert.Equal("running", probes.Jobs!["a"]);
    }

    [Fact]
    public void WithoutABudgetTheListingIsWaitedFor()
    {
        var probes = Gather(() =>
        {
            Thread.Sleep(200);
            return new Dictionary<string, string> { ["a"] = "idle" };
        }, null);

        Assert.Equal("idle", probes.Jobs!["a"]);
    }

    [Fact]
    public void AListingThatThrowsInsideTheBudgetStillThrowsLikeTheUnbudgetedCall()
    {
        Assert.ThrowsAny<Exception>(() =>
            Gather(() => throw new InvalidOperationException("no daemon"), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void TheFirstScanBudgetIsShorterThanTheScanTick()
    {
        Assert.InRange(SessionManager.FirstScanJobListingBudget, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }
}
