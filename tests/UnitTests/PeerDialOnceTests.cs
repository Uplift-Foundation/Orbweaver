using Xunit;

namespace Orbweaver.UnitTests;

// CB-301's second defect: a backlog of dials to one machine, released at once,
// produced one connection each — 44 ESTABLISHED to the mini. Two guards, both
// asserted here without a socket: one dial per machine at a time in PeerLink,
// and one dial tick at a time in PeerSessions.
public class PeerDialOnceTests
{
    private static PeerLink Link() => new(new PeerLink.Seams(
        Deliver: (_, _) => Task.CompletedTask,
        KnownPeer: _ => null,
        OwnCertificate: _ => throw new InvalidOperationException("no socket in this test")));

    // --- the decision -----------------------------------------------------------

    [Theory]
    [InlineData(false, false, "Dial")]
    [InlineData(false, true, "InFlight")]
    [InlineData(true, false, "AlreadyConnected")]
    // Connected wins: whatever else is happening, the caller has its link.
    [InlineData(true, true, "AlreadyConnected")]
    // By name, because the enum is internal and a test method is public.
    public void EachStateAdmitsOneAnswer(bool connected, bool inFlight, string expected) =>
        Assert.Equal(expected, PeerLink.Admit(connected, inFlight).ToString());

    // --- the claim ----------------------------------------------------------------

    // The first dial claims the machine; every dial after it, until the first
    // ends, is told one is already in flight.
    [Fact]
    public void ASecondDialToTheSameMachineIsRefusedUntilTheFirstEnds()
    {
        using var link = Link();

        Assert.Equal(PeerLink.DialStart.Dial, link.BeginDial("mini"));
        Assert.Equal(PeerLink.DialStart.InFlight, link.BeginDial("mini"));
        Assert.Equal(PeerLink.DialStart.InFlight, link.BeginDial("MINI")); // names are case-blind everywhere else

        link.EndDial("mini");

        Assert.Equal(PeerLink.DialStart.Dial, link.BeginDial("mini"));
    }

    // Per machine, not global: one slow peer must not stop the others being dialled.
    [Fact]
    public void DialsToDifferentMachinesDoNotWaitOnEachOther()
    {
        using var link = Link();

        Assert.Equal(PeerLink.DialStart.Dial, link.BeginDial("mini"));
        Assert.Equal(PeerLink.DialStart.Dial, link.BeginDial("windows"));
    }

    // A machine already connected answers "connected" without being claimed,
    // so once it drops nothing is left behind to refuse the next dial.
    [Fact]
    public void AConnectedMachineIsNotClaimed()
    {
        using var link = Link();
        using var wire = new BlocksUntilDisposed();
        link.Adopt("mini", wire, "pin", CancellationToken.None);

        Assert.Equal(PeerLink.DialStart.AlreadyConnected, link.BeginDial("mini"));

        link.Drop("mini");

        Assert.Equal(PeerLink.DialStart.Dial, link.BeginDial("mini"));
    }

    // Many callers at once: exactly one is admitted.
    [Fact]
    public async Task OfManySimultaneousDialsExactlyOneIsAdmitted()
    {
        using var link = Link();
        using var go = new ManualResetEventSlim();

        var answers = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => Task.Run(() =>
        {
            go.Wait(TimeSpan.FromSeconds(10));
            return link.BeginDial("mini");
        })).Append(Task.Run(() => { go.Set(); return PeerLink.DialStart.InFlight; })));

        Assert.Single(answers, a => a == PeerLink.DialStart.Dial);
    }

    // --- one tick at a time ----------------------------------------------------------

    [Fact]
    public async Task ATickArrivingWhileOneRunsIsRefused()
    {
        var flight = new SingleFlight();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;

        var first = flight.RunAsync(async () => { runs++; await hold.Task; });
        Assert.True(flight.Running);

        Assert.False(await flight.RunAsync(() => { runs++; return Task.CompletedTask; }));

        hold.SetResult();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(flight.Running);

        // And the next tick after it runs.
        Assert.True(await flight.RunAsync(() => { runs++; return Task.CompletedTask; }));
        Assert.Equal(2, runs);
    }

    // A tick that throws still lets the next one run — otherwise one fault
    // would silence the dial loop for good, which is the failure CB-61 named.
    [Fact]
    public async Task ATickThatThrowsStillReleases()
    {
        var flight = new SingleFlight();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => flight.RunAsync(() => throw new InvalidOperationException("tick failed")));

        Assert.False(flight.Running);
        Assert.True(await flight.RunAsync(() => Task.CompletedTask));
    }

    // An idle connection: no byte arrives until it is disposed.
    private sealed class BlocksUntilDisposed : Stream
    {
        private readonly TaskCompletionSource _disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _disposed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _disposed.TrySetResult();
            base.Dispose(disposing);
        }
    }
}
