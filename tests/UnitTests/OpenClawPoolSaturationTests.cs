using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// The one test in this assembly that rewrites the thread pool's ceiling for the
// whole process, so it runs alone (CB-269).
//
// It used to sit in OpenClawGatewayTests, whose `Settings` collection only
// serialises *its own members*: every class outside it ran in parallel, and for
// the six seconds this held every pool worker, a neighbour's `Task.Run` could not
// get a thread. CloudTurnSenderTests reported NotSent after exactly its budget,
// reproduced 3 of 6 under `DOTNET_PROCESSOR_COUNT=3`; and with the ceiling
// pinned while other tests had workers parked, nobody was left to fire the timer
// that would restore it, which is the shape of the six-hour silent hang in
// CB-269's evidence. A collection with `DisableParallelization` runs after the
// parallel ones and by itself, which also satisfies the settings dependency
// OpenClawGatewayTests documents.
[Collection("ThreadPoolCeiling")]
public class OpenClawPoolSaturationTests
{
    // The case CB-68 was actually filed over: the pool with no spare worker to
    // hand out. Deterministic rather than timing-dependent — the saturation is
    // built by pinning the pool's own ceiling to exactly as many blocking
    // callbacks as are queued, not by hoping a slow machine reproduces it, so
    // this either passes reliably or fails reliably rather than flaking either
    // way. Same technique CB-23's own measurement above used to get its
    // 25.5-second figure, aimed here at one specific, narrower claim.
    //
    // What this proves: the receive loop still gets a thread of its own and
    // starts running within a bound far short of the thirty-second CI failure
    // this ticket reports, even with literally nothing free in the pool. What
    // it does not prove — and CB-68 should not be read as fully closed by it —
    // is that the *whole* handshake is immune to the pool being saturated:
    // ConnectAsync's later awaits still resume via ordinary Task continuations,
    // which this test does not exercise under saturation. See the comment on
    // ConnectAsync's Task.Factory.StartNew call for why that remainder is out
    // of this ticket's scope.
    //
    // A raw, non-pooled Thread is the escape hatch that keeps this test itself
    // from ever hanging: every blocking callback below only returns via
    // `release.Set()`, called from a thread that never asks the ThreadPool for
    // anything, so a wrong result here is a fast assertion failure rather than
    // a CI run that never comes back.
    [Fact]
    public async Task TheReceiveLoopStartsPromptlyEvenWithTheThreadPoolFullySaturated()
    {
        ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
        ThreadPool.GetMinThreads(out var minWorkers, out var minIo);

        var pin = Math.Max(minWorkers, Environment.ProcessorCount);
        var release = new ManualResetEventSlim(false);
        var started = 0;

        var safetyRelease = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(6));
            release.Set();
        })
        { IsBackground = true };
        safetyRelease.Start();

        try
        {
            // Checked, because an ignored false here would mean the pool was never
            // pinned and the test below proved nothing about saturation.
            Assert.True(ThreadPool.SetMaxThreads(pin, maxIo), "the pool ceiling could not be pinned");
            Assert.True(ThreadPool.SetMinThreads(pin, minIo), "the pool floor could not be pinned");

            for (var i = 0; i < pin; i++)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Interlocked.Increment(ref started);
                    release.Wait();
                });
            }

            var saturating = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref started) < pin && saturating.Elapsed < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(10);
            }

            var socket = OpenClawGatewayTests.Accepting();
            using var gateway = OpenClawGatewayTests.Gateway(socket, challengeTimeout: TimeSpan.FromSeconds(20),
                requestTimeout: TimeSpan.FromSeconds(20));

            // Started but not awaited yet, on purpose: this test's claim is
            // about how fast the *loop* starts, not about the rest of the
            // handshake finishing, which is the part CB-68 did not fully close
            // (see ConnectAsync's comment). Awaiting ConnectAsync itself here
            // would fold that open question into this assertion and make the
            // test fail for a reason it does not name.
            var connecting = gateway.ConnectAsync(null, CancellationToken.None);

            var startSw = System.Diagnostics.Stopwatch.StartNew();
            while (gateway.ReceiveLoopStartedOnAPooledThread is null
                   && startSw.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(5);
            }

            Assert.NotNull(gateway.ReceiveLoopStartedOnAPooledThread);
            Assert.False(gateway.ReceiveLoopStartedOnAPooledThread);
            Assert.True(startSw.Elapsed < TimeSpan.FromSeconds(2),
                $"the receive loop took {startSw.Elapsed} to start with the pool fully saturated");

            // Let the saturation go so the connection can actually finish and
            // the gateway disposes cleanly, rather than leaving `connecting`
            // to be abandoned mid-handshake.
            release.Set();
            await connecting;
        }
        finally
        {
            release.Set();
            ThreadPool.SetMinThreads(minWorkers, minIo);
            ThreadPool.SetMaxThreads(maxWorkers, maxIo);
        }
    }
}

[CollectionDefinition("ThreadPoolCeiling", DisableParallelization = true)]
public class ThreadPoolCeilingCollection { }
