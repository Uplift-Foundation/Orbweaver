using System.Net.Security;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-411: the decisions behind the keychain-hang fix, with no socket and no
// certificate. PeerLink.BoundedHandshakeAsync is what ConnectAsync and
// HandshakeInboundAsync (both [ExcludeFromCodeCoverage]) hand their handshake
// to, so the bound, the cap and the dial bound are covered here rather than
// excluded. The wiring through real sockets is PeerLinkKeychainHangTests.
public class PeerHandshakeBoundTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);

    private static PeerLink Link() => new(new PeerLink.Seams(
        Deliver: (_, _) => Task.CompletedTask,
        KnownPeer: _ => null,
        OwnCertificate: _ => throw new InvalidOperationException("no certificate in this test")))
    { StallGrace = TimeSpan.FromMilliseconds(50) };

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Until(Func<bool> done)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done()) { Assert.True(DateTime.UtcNow < until, "condition not reached"); await Task.Delay(5); }
    }

    // A handshake that has not returned when the setup clock fires is abandoned
    // as a stall, whose message names the keychain; the cap goes up.
    [Fact]
    public async Task AHandshakeThatIgnoresItsTokenIsAbandonedAsAKeychainStall()
    {
        using var link = Link();
        var gate = Gate();
        using var setup = new CancellationTokenSource(Short);

        var stalled = await Assert.ThrowsAsync<PeerLink.HandshakeStalled>(() =>
            link.BoundedHandshakeAsync(() => gate.Task, CancellationToken.None, setup.Token, "0.1s", "mini"));

        Assert.Contains("to=mini", stalled.Message);
        Assert.Contains("keychain", stalled.Message);
        Assert.Contains("0.1s", stalled.Message);
        Assert.Equal(1, link.StuckHandshakes);

        gate.SetResult();
        await Until(() => link.StuckHandshakes == 0);
    }

    // The inbound side passes no machine; the line then has no "to=".
    [Fact]
    public async Task AnInboundStallNamesNoMachine()
    {
        using var link = Link();
        var gate = Gate();
        using var setup = new CancellationTokenSource(Short);

        var stalled = await Assert.ThrowsAsync<PeerLink.HandshakeStalled>(() =>
            link.BoundedHandshakeAsync(() => gate.Task, CancellationToken.None, setup.Token, "0.1s"));

        Assert.DoesNotContain("to=", stalled.Message);
        gate.SetResult();
        await Until(() => link.StuckHandshakes == 0);
    }

    // Control: a handshake that finishes sets nothing.
    [Fact]
    public async Task AHandshakeThatCompletesLeavesNoCap()
    {
        using var link = Link();
        using var setup = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await link.BoundedHandshakeAsync(() => Task.CompletedTask, CancellationToken.None, setup.Token, "10s");

        Assert.Equal(0, link.StuckHandshakes);
    }

    // Control: a handshake that fails (rather than hangs) surfaces its own
    // exception on the old failure path and sets no cap.
    [Fact]
    public async Task AHandshakeThatThrowsKeepsItsOwnExceptionAndSetsNoCap()
    {
        using var link = Link();
        using var setup = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<InvalidOperationException>(() => link.BoundedHandshakeAsync(
            () => throw new InvalidOperationException("bad certificate"),
            CancellationToken.None, setup.Token, "10s"));

        Assert.Equal(0, link.StuckHandshakes);
    }

    // Shutdown during a hang is a cancellation, not a "keychain" stall. The
    // thread is still stuck in the native call, though, so it is still counted:
    // reporting it as free would let a new handshake strand a second thread.
    [Fact]
    public async Task ShutdownDuringAHangIsACancellationButTheThreadIsStillCounted()
    {
        using var link = Link();
        var gate = Gate();
        using var shutdown = new CancellationTokenSource();
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);

        var pending = link.BoundedHandshakeAsync(() => gate.Task, shutdown.Token, setup.Token, "10s");
        shutdown.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, link.StuckHandshakes);

        gate.SetResult();
        await Until(() => link.StuckHandshakes == 0);
    }

    // The ordinary CB-301 shape: a far side that accepts TCP and never speaks.
    // The real handshake honours its token but takes a moment to unwind, so
    // IsCompleted is still false when the clock fires. That is a silent peer,
    // not a keychain, and it must neither say so nor take the cap.
    [Fact]
    public async Task ACooperativeHandshakeThatUnwindsAfterTheClockIsNotAKeychainStall()
    {
        using var link = Link();
        link.StallGrace = TimeSpan.FromSeconds(5); // far longer than the 100 ms unwind
        using var setup = new CancellationTokenSource(Short);

        var thrown = await Record.ExceptionAsync(() => link.BoundedHandshakeAsync(async () =>
        {
            try { await Task.Delay(Timeout.Infinite, setup.Token); }
            catch (OperationCanceledException)
            {
                await Task.Delay(100); // unwinding takes a moment
                throw;
            }
        }, CancellationToken.None, setup.Token, "0.1s", "mini"));

        Assert.IsNotType<PeerLink.HandshakeStalled>(thrown);
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.Equal(0, link.StuckHandshakes);
    }

    // Shutdown does not wait out the grace: a 30 s grace must not delay it.
    [Fact]
    public async Task ShutdownSkipsTheGrace()
    {
        using var link = Link();
        link.StallGrace = TimeSpan.FromSeconds(30);
        var gate = Gate();
        using var shutdown = new CancellationTokenSource();
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);

        var pending = link.BoundedHandshakeAsync(() => gate.Task, shutdown.Token, setup.Token, "10s");
        shutdown.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, link.StuckHandshakes);

        gate.SetResult();
        await Until(() => link.StuckHandshakes == 0);
    }

    // A deaf call (it never looks at its token) that nevertheless returns inside
    // the grace was only slow, not stuck: no stall, no cap.
    [Fact]
    public async Task ADeafCallThatReturnsInsideTheGraceIsNotStuck()
    {
        using var link = Link();
        link.StallGrace = TimeSpan.FromSeconds(5);
        var gate = Gate();
        using var setup = new CancellationTokenSource(Short);

        var pending = link.BoundedHandshakeAsync(() => gate.Task, CancellationToken.None, setup.Token, "0.1s");
        await Task.Delay(Short * 2); // the clock has fired and the call is still out
        gate.SetResult();

        Assert.Null(await Record.ExceptionAsync(() => pending));
        Assert.Equal(0, link.StuckHandshakes);
    }

    // A counter, not a flag: two abandoned handshakes hold the cap until both return.
    [Fact]
    public async Task TheCapHoldsUntilEveryAbandonedHandshakeHasReturned()
    {
        using var link = Link();
        var first = Gate();
        var second = Gate();

        foreach (var gate in new[] { first, second })
        {
            using var setup = new CancellationTokenSource(Short);
            await Assert.ThrowsAsync<PeerLink.HandshakeStalled>(() =>
                link.BoundedHandshakeAsync(() => gate.Task, CancellationToken.None, setup.Token, "0.1s"));
        }

        Assert.Equal(2, link.StuckHandshakes);

        first.SetResult();
        await Until(() => link.StuckHandshakes == 1);
        await Task.Delay(50);
        Assert.Equal(1, link.StuckHandshakes); // still blocked

        // The second returns by throwing: the clear is not on the happy path only.
        second.SetException(new InvalidOperationException("late failure"));
        await Until(() => link.StuckHandshakes == 0);
    }

    // The timeout and the stuck call's return landing together must leave the
    // count at exactly zero, never negative and never stuck on.
    [Fact]
    public async Task ATimeoutRacingTheStuckCallsReturnAlwaysEndsAtZero()
    {
        using var link = Link();

        for (var i = 0; i < 200; i++)
        {
            var gate = Gate();
            using var setup = new CancellationTokenSource();
            using var go = new ManualResetEventSlim();

            var pending = link.BoundedHandshakeAsync(() => gate.Task, CancellationToken.None, setup.Token, "x");

            var cancel = Task.Run(() => { go.Wait(); setup.Cancel(); });
            var finish = Task.Run(() => { go.Wait(); gate.TrySetResult(); });
            go.Set();
            await Task.WhenAll(cancel, finish);

            try { await pending; } catch (PeerLink.HandshakeStalled) { }

            await Until(() => link.StuckHandshakes == 0);
            Assert.True(link.StuckHandshakes >= 0);
        }
    }

    // The stuck call returning after the link is gone neither throws nor leaves
    // the count behind.
    [Fact]
    public async Task ALateReturnAfterDisposeIsHarmless()
    {
        var link = Link();
        var gate = Gate();
        using var setup = new CancellationTokenSource(Short);

        await Assert.ThrowsAsync<PeerLink.HandshakeStalled>(() =>
            link.BoundedHandshakeAsync(() => gate.Task, CancellationToken.None, setup.Token, "x"));

        link.Dispose();
        gate.SetResult();

        await Until(() => link.StuckHandshakes == 0);
    }

    // The abandoned task's later fault is observed, not left for the finalizer.
    [Fact]
    public async Task AnAbandonedHandshakesLaterFaultIsObserved()
    {
        var unobserved = new List<string>();
        void Hear(object? _, UnobservedTaskExceptionEventArgs e)
        {
            lock (unobserved) unobserved.AddRange(e.Exception.Flatten().InnerExceptions.Select(x => x.Message));
        }

        TaskScheduler.UnobservedTaskException += Hear;
        try
        {
            await Abandon();

            for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(10); }

            lock (unobserved) Assert.DoesNotContain("cb411-late-fault", unobserved);
        }
        finally { TaskScheduler.UnobservedTaskException -= Hear; }

        static async Task Abandon()
        {
            using var link = Link();
            var gate = Gate();
            using var setup = new CancellationTokenSource(Short);

            await Assert.ThrowsAsync<PeerLink.HandshakeStalled>(() =>
                link.BoundedHandshakeAsync(() => gate.Task, CancellationToken.None, setup.Token, "x"));

            gate.SetException(new InvalidOperationException("cb411-late-fault"));
            await Until(() => link.StuckHandshakes == 0);
        }
    }

    // A handshake whose start blocks the calling thread synchronously (the real
    // native call) must not block the caller: the bound still fires.
    [Fact]
    public async Task ASynchronouslyBlockingStartDoesNotBlockTheCaller()
    {
        using var link = Link();
        using var release = new ManualResetEventSlim();
        using var setup = new CancellationTokenSource(Short);

        await Assert.ThrowsAsync<PeerLink.HandshakeStalled>(() => link.BoundedHandshakeAsync(
            () => { release.Wait(); return Task.CompletedTask; },
            CancellationToken.None, setup.Token, "x"));

        release.Set();
        await Until(() => link.StuckHandshakes == 0);
    }

    // --- HandshakeBlocked / the seam defaults ---------------------------------------

    [Fact]
    public void TheRealSslStreamCallsAreTheDefaults()
    {
        var seams = new PeerLink.Seams(
            Deliver: (_, _) => Task.CompletedTask, KnownPeer: _ => null,
            OwnCertificate: _ => throw new InvalidOperationException());

        Assert.NotNull(seams.AsClient);
        Assert.NotNull(seams.AsServer);

        Func<SslStream, SslClientAuthenticationOptions, CancellationToken, Task> mine = (_, _, _) => Task.CompletedTask;
        Assert.Same(mine, (seams with { ClientHandshake = mine }).AsClient);
    }

    // --- closing what never became a link -------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscardClosesTheStreamWhetherOrNotTheHandshakeStalled(bool stalled)
    {
        var inner = new Tracked();
        var tls = new SslStream(inner, leaveInnerStreamOpen: false);

        PeerLink.Discard(tls, client: null, stalled);

        await Until(() => inner.Disposed);
    }

    // Shutdown while a handshake is stuck: the SslStream the stuck call holds may
    // block on Dispose, and closing it must not take the caller with it.
    [Fact]
    public async Task DiscardAfterAStallDoesNotBlockOnADisposeThatBlocks()
    {
        using var release = new ManualResetEventSlim();
        var inner = new BlocksOnDispose(release);
        var tls = new SslStream(inner, leaveInnerStreamOpen: false);

        await Task.Run(() => PeerLink.Discard(tls, client: null, stalled: true)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(inner.Disposed);   // still blocked inside Dispose, off-thread
        release.Set();
        await Until(() => inner.Disposed);
    }

    private sealed class BlocksOnDispose(ManualResetEventSlim release) : MemoryStream
    {
        private int _disposed;
        public bool Disposed => Volatile.Read(ref _disposed) == 1;
        protected override void Dispose(bool disposing)
        {
            release.Wait();
            Volatile.Write(ref _disposed, 1);
            base.Dispose(disposing);
        }
    }

    private sealed class Tracked : MemoryStream
    {
        private int _disposed;
        public bool Disposed => Volatile.Read(ref _disposed) == 1;
        protected override void Dispose(bool disposing) { Volatile.Write(ref _disposed, 1); base.Dispose(disposing); }
    }

    // --- the dial bound -------------------------------------------------------------

    [Fact]
    public void TheOuterBoundIsLongerThanTheDialsOwn() =>
        Assert.True(PeerSessions.DialBound(TimeSpan.FromSeconds(15)) > TimeSpan.FromSeconds(15));

    // A dial that never returns is cut off, logged and reported as not connected.
    [Fact]
    public async Task ADialThatNeverReturnsIsAbandonedAtItsBound()
    {
        var heard = new List<(string What, string Detail)>();
        void H(string w, string d) { lock (heard) heard.Add((w, d)); }
        MirrorLog.Heard += H;
        try
        {
            var never = Gate();
            var ok = await PeerSessions.DialPeerAsync("cb411-hang", async () => { await never.Task; return true; }, Short);

            Assert.False(ok);
            lock (heard) Assert.Contains(heard, e => e.What == "peer-dial-abandoned:cb411-hang");
        }
        finally { MirrorLog.Heard -= H; }
    }

    // A slow dial that is not hung is not cut off, and its answer is returned.
    [Fact]
    public async Task ASlowButFinishingDialIsNotCutOff()
    {
        var gate = Gate();
        var dial = PeerSessions.DialPeerAsync("cb411-slow", async () => { await gate.Task; return true; }, TimeSpan.FromSeconds(10));

        await Task.Delay(Short);
        Assert.False(dial.IsCompleted);

        gate.SetResult();
        Assert.True(await dial);
    }

    [Fact]
    public async Task ADialThatThrowsIsLoggedNotPropagated()
    {
        var heard = new List<(string What, string Detail)>();
        void H(string w, string d) { lock (heard) heard.Add((w, d)); }
        MirrorLog.Heard += H;
        try
        {
            var ok = await PeerSessions.DialPeerAsync(
                "cb411-throws", () => throw new InvalidOperationException("boom"), TimeSpan.FromSeconds(10));

            Assert.False(ok);
            lock (heard) Assert.Contains(heard, e => e.What == "peer-dial-failed:cb411-throws" && e.Detail.Contains("boom"));
        }
        finally { MirrorLog.Heard -= H; }
    }

    // The tick's shape: one peer hangs, the others are still dialled, whatever
    // follows the loop still runs, and the next tick is not skipped because the
    // single-flight latch was released.
    [Fact]
    public async Task OneHungPeerCostsTheTickItsBoundAndNothingElse()
    {
        var tick = new SingleFlight();
        var never = Gate();
        var dialled = new List<string>();
        var afterLoop = 0;

        async Task Body()
        {
            foreach (var machine in new[] { "a-hangs", "b-ok", "c-ok" })
            {
                await PeerSessions.DialPeerAsync(machine, async () =>
                {
                    lock (dialled) dialled.Add(machine);
                    if (machine == "a-hangs") await never.Task;
                    return true;
                }, Short);
            }

            afterLoop++; // AskWhatTheyHaveAsync's place
        }

        Assert.True(await tick.RunAsync(Body));
        Assert.Equal(new[] { "a-hangs", "b-ok", "c-ok" }, dialled);
        Assert.Equal(1, afterLoop);
        Assert.False(tick.Running);

        Assert.True(await tick.RunAsync(() => Task.CompletedTask)); // not skipped
    }
}
