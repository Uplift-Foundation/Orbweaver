using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// CB-404: the background bridge probe must never run on the thread that asks
// for the options. The live probe reads a credential before its first await,
// and where no one can answer a Keychain prompt that read never returns; run
// on a panel's UI thread it froze the whole UI suite on both CI legs
// (4ff18dbf).
public class RunControlsProbeThreadTests
{
    [Fact]
    public async Task AProbeThatBlocksBeforeItsFirstAwaitDoesNotHoldUpTheOptions()
    {
        using var gate = new ManualResetEventSlim(false);
        var host = new RunControlsHost(new RunControlsHost.Seams(
            _ => true, st => st.BridgeSessionId, () => true,
            _ => new RunReading("claude-opus-5-5", "high", DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), RunReadingSource.Transcript),
            _ => null, _ => null,
            (_, _) => Task.FromResult(true),
            (_, _, _) => Task.FromResult(MirrorSlashHost.ControlVerdict.Done),
            () => DateTimeOffset.FromUnixTimeSeconds(1_800_000_010), Log: (_, _) => { },
            // Blocks synchronously, the way a credential read with nobody at
            // the screen does, before it ever returns a task.
            ProbeBridge: (_, _) => { gate.Wait(TimeSpan.FromSeconds(30)); return Task.FromResult(true); }));
        var status = new SessionStatus { SessionId = "p1", Source = SessionSource.ClaudeCode, BridgeSessionId = "bridge-p", Cwd = "" };

        var clock = Stopwatch.StartNew();
        var options = host.Options(status)!;
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Options took {clock.Elapsed}");
        // Not probed yet: the warning shows before the first choice.
        Assert.NotNull(options.DefaultWarning);

        gate.Set();
        await host.LastProbe;
        Assert.Null(host.Options(status)!.DefaultWarning);
    }

    // Two sessions sharing a key (the UI suites' sessions often have no id)
    // with different bridges: each bridge is probed once, and reading both
    // over and over settles instead of re-probing forever.
    [Fact]
    public async Task TwoBridgesUnderOneKeyAreEachProbedOnce()
    {
        var probes = new System.Collections.Concurrent.ConcurrentBag<string>();
        var changed = 0;
        var host = new RunControlsHost(new RunControlsHost.Seams(
            _ => true, st => st.BridgeSessionId, () => true, _ => null, _ => null, _ => null,
            (_, _) => Task.FromResult(true),
            (_, _, _) => Task.FromResult(MirrorSlashHost.ControlVerdict.Done),
            () => DateTimeOffset.UnixEpoch, Log: (_, _) => { },
            ProbeBridge: (_, bridge) => { probes.Add(bridge); return Task.FromResult(true); }));
        host.Changed += _ => Interlocked.Increment(ref changed);
        var a = new SessionStatus { SessionId = "", Source = SessionSource.ClaudeCode, BridgeSessionId = "bridge-a", Cwd = "" };
        var b = new SessionStatus { SessionId = "", Source = SessionSource.ClaudeCode, BridgeSessionId = "bridge-b", Cwd = "" };

        for (var i = 0; i < 20; i++)
        {
            host.Options(a);
            host.Options(b);
            await host.LastProbe;
        }

        Assert.Equal(new[] { "bridge-a", "bridge-b" }, probes.Order());
        Assert.Equal(2, changed);
    }

    // A post that never reached the CLI marks that bridge unusable for every
    // session on it, and the warning shows from then on.
    [Fact]
    public async Task AnUnreachedPostMarksTheBridgeNotTheSession()
    {
        var host = new RunControlsHost(new RunControlsHost.Seams(
            _ => true, st => st.BridgeSessionId, () => true,
            _ => new RunReading("claude-opus-5-5", "high", DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), RunReadingSource.Transcript),
            _ => null, _ => null,
            (_, _) => Task.FromResult(true),
            (_, _, _) => Task.FromResult(MirrorSlashHost.ControlVerdict.Unreached("no credential")),
            () => DateTimeOffset.FromUnixTimeSeconds(1_800_000_010), Log: (_, _) => { },
            ProbeBridge: (_, _) => Task.FromResult(true)));
        var one = new SessionStatus { SessionId = "s1", Source = SessionSource.ClaudeCode, BridgeSessionId = "bridge-x", Cwd = "" };
        var two = new SessionStatus { SessionId = "s2", Source = SessionSource.ClaudeCode, BridgeSessionId = "bridge-x", Cwd = "" };
        host.Options(one);
        await host.LastProbe;
        Assert.Null(host.Options(two)!.DefaultWarning);

        await host.SetAsync(one, RunField.Model, "sonnet");

        Assert.NotNull(host.Options(two)!.DefaultWarning);
    }

    [Fact]
    public void TheSuitesProbeSwitchIsOn()
    {
        Assert.True(RunSourcesLive.ProbesOffForTests);
    }
}

// CB-404: the live host under the suites' switch, and the credential gate a
// control post takes. Neither may leave a test (or, in production, usage and
// cloud reads) waiting on a Keychain nobody can answer. In the Settings
// collection because one case turns on BridgeCommandsEnabled.
[Collection("Settings")]
public class RunControlsLiveBoundsTests
{
    private sealed class HungSource : IGatedCredentialSource
    {
        public SemaphoreSlim Gate { get; init; } = new(1, 1);
        public ManualResetEventSlim Release { get; } = new(false);
        public bool Hangs = true;
        public string? Stamp() => "s";
        public CredentialRead Read()
        {
            if (Hangs) Release.Wait(TimeSpan.FromSeconds(60));
            return new CredentialRead(CredentialOutcome.Found, "tok-x", null, "present");
        }
        public ICloudCredentialSource Choose() => this;
        public void Report(CredentialRead read) { }
    }

    private sealed class NoNetwork : ICloudApi
    {
        public int Calls;
        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new CloudApiResult(CloudOutcomes.OutcomeFor(200, "{}"), "{\"data\":[]}"));
        }
    }

    private static readonly SessionStatus Bridged = new()
    {
        SessionId = "live-1", Source = SessionSource.ClaudeCode, Entrypoint = SessionStatus.DesktopEntrypoint,
        BridgeSessionId = "session_01FixtureOnly", Cwd = "",
    };

    [Fact]
    public async Task UnderTheSuitesSwitchTheLiveHostNeitherProbesNorPosts()
    {
        Assert.True(RunSourcesLive.ProbesOffForTests);
        var host = RunControlsLive.Shared;
        var was = OrbweaverSettings.BridgeCommandsEnabled;
        OrbweaverSettings.BridgeCommandsEnabled = true;
        RunRouteDecision route;
        RunSetResult result;
        var clock = Stopwatch.StartNew();
        try
        {
            // Probe unwired: a bridged session is a usable Control route at once.
            route = host.RouteFor(Bridged, RunCli.Claude);
            result = await host.SetAsync(Bridged, RunField.Model, "sonnet");
        }
        finally
        {
            clock.Stop();
            OrbweaverSettings.BridgeCommandsEnabled = was;
        }

        Assert.Equal((RunRoute.Control, false), (route.Route, route.SetsNewSessionDefault));

        Assert.Equal(new RunSetResult(RunSetKind.Failed, RunControlsHost.ControlOffInTests), result);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task AHungCredentialReadIsCutAtTheBudgetAndTheNextReadGetsThrough()
    {
        var hung = new HungSource();
        var api = new NoNetwork();
        var post = RunControlsHost.BoundedPost(api, _ => hung, TimeSpan.FromMilliseconds(300), Task.Delay);

        var clock = Stopwatch.StartNew();
        var attempt = await post(Bridged, "bridge-live", new System.Text.Json.Nodes.JsonObject { ["subtype"] = "set_model", ["model"] = "sonnet" });
        clock.Stop();

        Assert.NotNull(attempt.Failure);
        Assert.Equal(0, api.Calls);                    // nothing was posted without a credential
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");

        // The gate is free again: a read through it answers at once, while the
        // abandoned read is still hanging.
        var next = new HungSource { Gate = hung.Gate, Hangs = false };
        var read = await ClaudeCliCredentials.ReadWithinAsync(next, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(CredentialOutcome.Found, read.Outcome);
        hung.Release.Set();
    }

    [Fact]
    public async Task APostQueuedBehindAHungOneIsDroppedAtItsBudget()
    {
        var hung = new HungSource();
        var post = RunControlsHost.BoundedPost(new NoNetwork(), _ => hung, TimeSpan.FromMilliseconds(300), Task.Delay);
        var request = new System.Text.Json.Nodes.JsonObject { ["subtype"] = "set_model", ["model"] = "sonnet" };

        var clock = Stopwatch.StartNew();
        var both = await Task.WhenAll(post(Bridged, "b", request), post(Bridged, "b", request));
        clock.Stop();

        Assert.All(both, a => Assert.NotNull(a.Failure));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        hung.Release.Set();
    }

    [Fact]
    public async Task ABoundedPostThatAnswersIsPassedThrough()
    {
        var ok = new HungSource { Hangs = false };
        var api = new NoNetwork();
        var post = RunControlsHost.BoundedPost(api, _ => ok, TimeSpan.FromSeconds(5), (_, _) => Task.CompletedTask);

        var attempt = await post(Bridged, "session_01FixtureOnly", new System.Text.Json.Nodes.JsonObject { ["subtype"] = "set_model", ["model"] = "sonnet" });

        Assert.Null(attempt.Failure);
        Assert.True(api.Calls >= 1);
    }
}
