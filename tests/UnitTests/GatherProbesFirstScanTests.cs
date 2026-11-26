using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-424: SessionManager.GatherProbes is what decides that the launch's first
// scan gives the job listing a budget and every later scan does not.
// FirstScanJobListingTests covers ScanProbes.ListJobs; this covers the wiring.
public class GatherProbesFirstScanTests
{
    private static readonly TimeSpan ListingTime = TimeSpan.FromMilliseconds(2500);

    private static List<SessionManager.ScanEntry> OneSessionWorthAsking() => new()
    {
        new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            new SessionStatus { Source = SessionSource.ClaudeCode, SessionPid = 0, Cwd = "/work/project" },
            DateTime.UtcNow),
    };

    private static SessionManager Manager(Func<Dictionary<string, string>?> listing) =>
        new(System.IO.Path.GetTempPath(),
            listing,
            attachClients: () => null,
            paneOwners: _ => new Dictionary<TmuxPaneKey, string?>(),
            agentViewer: _ => null,
            teams: _ => new Dictionary<int, AgentTeam.Membership>());

    [Fact]
    public void OnlyTheFirstGatherGivesTheJobListingABudget()
    {
        var calls = 0;
        var manager = Manager(() =>
        {
            Interlocked.Increment(ref calls);
            Thread.Sleep(ListingTime);
            return new Dictionary<string, string> { ["a"] = "running" };
        });
        var found = OneSessionWorthAsking();

        var first = Stopwatch.StartNew();
        var firstProbes = manager.GatherProbes(found);
        var firstWaited = first.Elapsed;

        var second = Stopwatch.StartNew();
        var secondProbes = manager.GatherProbes(found);
        var secondWaited = second.Elapsed;

        // First scan: gave up on the listing and showed orbs without it.
        Assert.Null(firstProbes.Jobs);
        Assert.True(firstWaited < ListingTime, $"first gather waited {firstWaited.TotalMilliseconds} ms");

        // Later scan: waits for the listing as before.
        Assert.NotNull(secondProbes.Jobs);
        Assert.Equal("running", secondProbes.Jobs!["a"]);
        Assert.True(secondWaited >= TimeSpan.FromMilliseconds(1500), $"second gather waited {secondWaited.TotalMilliseconds} ms");
    }
}
