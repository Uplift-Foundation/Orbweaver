using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Orbweaver.IntegrationTests;

// CB-306 (reopened): the new instance started while the old one still held the
// peer port, the bind threw, and nothing ever tried again. This holds a real
// port, fails a real PeerLink against it (the negative control), then releases
// it and shows the retry loop binds the same port.
public class PeerListenHeldPortTests
{
    private static PeerLink Link() =>
        new(new PeerLink.Seams(
            Deliver: (_, _) => Task.CompletedTask,
            KnownPeer: _ => null,
            OwnCertificate: () => throw new InvalidOperationException("not used")));

    [Fact]
    public async Task ListenFailsWhileThePortIsHeldThenBindsOnceItIsReleased()
    {
        var holder = new TcpListener(IPAddress.Any, 0);
        holder.Start();
        var port = ((IPEndPoint)holder.LocalEndpoint).Port;

        using var link = Link();

        // Negative control: with the port held the bind really does refuse.
        Assert.Throws<SocketException>(() => link.Listen(port));

        var failures = new List<int>();
        var released = false;

        var count = await PeerListenRetry.RunAsync(
            () => link.Listen(port),
            (n, _, _) => failures.Add(n),
            (_, _) =>
            {
                // Release after the second failed retry, like an old instance exiting.
                if (failures.Count == 2 && !released) { holder.Stop(); released = true; }
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(released);
        Assert.Equal(2, count);
        Assert.Equal(port, link.BoundPort);

        // And it is genuinely accepting connections.
        using var probe = new TcpClient();
        await probe.ConnectAsync(IPAddress.Loopback, port);
        Assert.True(probe.Connected);
    }
}
