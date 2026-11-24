using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Orbweaver.IntegrationTests;

// CB-343: one accept that threw something other than a cancellation ended the
// inbound listener for the rest of the process. The accept call is behind
// PeerLink.Seams.Accept so a test can make the first one throw the way a
// transient EMFILE does, with a real loopback listener behind the rest.
public class PeerAcceptLoopTests
{
    private sealed class Counting
    {
        public int Calls, Accepted;
        public readonly TaskCompletionSource FirstAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static PeerLink Link(Counting c, int failFirst) =>
        new(new PeerLink.Seams(
            Deliver: (_, _) => Task.CompletedTask,
            KnownPeer: _ => null,
            OwnCertificate: _ => throw new InvalidOperationException("handshake not under test"),
            Accept: async (listener, ct) =>
            {
                if (Interlocked.Increment(ref c.Calls) <= failFirst)
                    throw new SocketException((int)SocketError.TooManyOpenSockets);

                var client = await listener.AcceptTcpClientAsync(ct);
                Interlocked.Increment(ref c.Accepted);
                c.FirstAccepted.TrySetResult();
                return client;
            }));

    [Fact]
    public async Task ListenerKeepsAcceptingAfterAnAcceptThrows()
    {
        var c = new Counting();
        using var link = Link(c, failFirst: 2);
        link.Listen(0);

        using var probe = new TcpClient();
        await probe.ConnectAsync(IPAddress.Loopback, link.BoundPort);

        await c.FirstAccepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, c.Accepted);
        Assert.True(c.Calls >= 3);
    }

    [Fact]
    public async Task DisposingStopsTheLoopWithNoFurtherAccepts()
    {
        var c = new Counting();
        var link = Link(c, failFirst: 0);
        link.Listen(0);
        var port = link.BoundPort;

        // Let the loop reach its first accept, then dispose.
        while (Volatile.Read(ref c.Calls) < 1) await Task.Delay(10);
        link.Dispose();
        await Task.Delay(300);
        var calls = Volatile.Read(ref c.Calls);
        await Task.Delay(300);

        Assert.Equal(calls, Volatile.Read(ref c.Calls));
        Assert.Equal(0, c.Accepted);
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            using var late = new TcpClient();
            await late.ConnectAsync(IPAddress.Loopback, port);
        });
    }

    [Fact]
    public async Task DisposingDuringABackoffEndsTheLoop()
    {
        var c = new Counting();
        var link = Link(c, failFirst: int.MaxValue);
        link.Listen(0);

        while (Volatile.Read(ref c.Calls) < 1) await Task.Delay(10);
        link.Dispose();
        await Task.Delay(300);
        var calls = Volatile.Read(ref c.Calls);
        await Task.Delay(300);

        Assert.Equal(calls, Volatile.Read(ref c.Calls));
    }

    [Fact]
    public async Task AListenerDisposedUnderTheLoopEndsItWithoutRetrying()
    {
        var calls = 0;
        using var link = new PeerLink(new PeerLink.Seams(
            Deliver: (_, _) => Task.CompletedTask,
            KnownPeer: _ => null,
            OwnCertificate: _ => throw new InvalidOperationException("not used"),
            Accept: (_, _) =>
            {
                Interlocked.Increment(ref calls);
                throw new ObjectDisposedException("listener");
            }));
        link.Listen(0);

        await Task.Delay(500);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RetryDelayGrowsFromOneHundredMillisecondsToOneSecond()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(100), PeerLink.AcceptRetryDelay(0));
        Assert.Equal(TimeSpan.FromMilliseconds(100), PeerLink.AcceptRetryDelay(1));
        Assert.Equal(TimeSpan.FromMilliseconds(200), PeerLink.AcceptRetryDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(1), PeerLink.AcceptRetryDelay(5));
        Assert.Equal(TimeSpan.FromSeconds(1), PeerLink.AcceptRetryDelay(500));
        Assert.True(PeerLink.ShouldLogAcceptFailure(1));
        Assert.False(PeerLink.ShouldLogAcceptFailure(2));
    }
}
