using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Orbweaver.IntegrationTests;

// CB-411: a locked login keychain (or a keychain prompt nobody has answered)
// blocks SslStream's native Apple handshake, and that call ignores its token.
// The dial then never returned, its EndDial never ran, and the dial loop logged
// "previous tick still running" for over an hour with nothing connected.
//
// The handshake seam stands in for the native call: it blocks its own thread on
// a gate with no token at all, which is the one behaviour that matters. Real
// sockets, because the claims are about what reaches the wire — the second dial
// must not even open a TCP connection, and the accept loop must keep accepting.
public class PeerLinkKeychainHangTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(400);

    private static readonly Lazy<X509Certificate2> Cert = new(() =>
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=hang", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    });

    // Blocks the calling thread until released, deaf to cancellation, then fails
    // the handshake: what is stuck is the thread, not the outcome.
    private sealed class Keychain : IDisposable
    {
        public readonly ManualResetEventSlim Gate = new();
        public int Entered;
        public volatile bool ReturnNormally; // how a released call ends
        public void Dispose() => Gate.Set();
    }

    // The certificate is generated here, not inside the first dial: RSA-2048
    // keygen on a cold runner can outlast the short DialTimeout these tests use,
    // which would time the dial out before it ever reached the handshake.
    private static PeerLink Link(Keychain keychain, TimeSpan? dial = null) =>
        Warm() ? Build(keychain, dial) : throw new InvalidOperationException();

    private static bool Warm() => Cert.Value is not null;

    private static PeerLink Build(Keychain keychain, TimeSpan? dial) =>
        new(new PeerLink.Seams(
            Deliver: (_, _) => Task.CompletedTask,
            KnownPeer: _ => null,
            OwnCertificate: _ => Task.FromResult(Cert.Value),
            ClientHandshake: (_, _, _) =>
            {
                Interlocked.Increment(ref keychain.Entered);
                keychain.Gate.Wait(); // no token: this is the native call
                if (keychain.ReturnNormally) return Task.CompletedTask;
                throw new InvalidOperationException("keychain released; handshake abandoned");
            },
            ServerHandshake: (_, _, _) =>
            {
                Interlocked.Increment(ref keychain.Entered);
                keychain.Gate.Wait();
                if (keychain.ReturnNormally) return Task.CompletedTask;
                throw new InvalidOperationException("keychain released; handshake abandoned");
            }))
        { DialTimeout = dial ?? Short, InboundSetupTimeout = dial ?? Short };

    private static (TcpListener Listener, int Port, Func<int> Accepted) Silent()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = 0;
        _ = Task.Run(async () =>
        {
            try { while (true) { (await listener.AcceptTcpClientAsync()).Dispose(); Interlocked.Increment(ref accepted); } }
            catch { }
        });
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port, () => Volatile.Read(ref accepted));
    }

    private static async Task Within(TimeSpan span, Func<bool> done)
    {
        var until = DateTime.UtcNow + span;
        while (!done()) { Assert.True(DateTime.UtcNow < until, "condition not reached in time"); await Task.Delay(10); }
    }

    private static Action<string, string> Hear(List<(string What, string Detail)> heard)
    {
        void H(string w, string d) { lock (heard) heard.Add((w, d)); }
        return H;
    }

    [Fact]
    public async Task ADialStuckInTheKeychainReturnsFalseWithinItsTimeoutAndNamesTheKeychain()
    {
        using var keychain = new Keychain();
        using var link = Link(keychain);
        var (listener, port, _) = Silent();
        var heard = new List<(string, string)>();
        var h = Hear(heard);
        MirrorLog.Heard += h;

        try
        {
            var dial = link.ConnectAsync("kc-dial-one", "127.0.0.1", port);

            Assert.False(await dial.WaitAsync(TimeSpan.FromSeconds(10)),
                "the dial stayed hostage to a handshake that ignores cancellation");

            Assert.Equal(1, link.StuckHandshakes);
            lock (heard)
                Assert.Contains(heard, e => e.Item1 == "peer-connect-failed:kc-dial-one"
                    && e.Item2.Contains("keychain") && e.Item2.Contains("did not finish"));

            // EndDial ran: the machine is dialable again, which is what the
            // SingleFlight tick was waiting on for an hour.
            Assert.Equal(PeerLink.DialStart.Dial, link.BeginDial("kc-dial-one"));
        }
        finally { MirrorLog.Heard -= h; listener.Stop(); }
    }

    [Fact]
    public async Task ASecondDialWhileOneIsStuckFailsAtOnceAndNeverTouchesTheSocket()
    {
        using var keychain = new Keychain();
        using var link = Link(keychain);
        var (listener, port, accepted) = Silent();
        var heard = new List<(string, string)>();
        var h = Hear(heard);
        MirrorLog.Heard += h;

        try
        {
            Assert.False(await link.ConnectAsync("kc-dial-two-a", "127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(10)));
            await Within(TimeSpan.FromSeconds(5), () => accepted() >= 1);
            Assert.Equal(1, accepted());

            var before = Stopwatch.GetTimestamp();
            Assert.False(await link.ConnectAsync("kc-dial-two-b", "127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(Stopwatch.GetElapsedTime(before) < Short / 2, "the refusal waited instead of failing fast");

            await Task.Delay(100);
            Assert.Equal(1, accepted());                 // no second TCP connection
            Assert.Equal(1, keychain.Entered);           // no second native call
            Assert.Equal(1, link.StuckHandshakes);       // leak bounded to one
            lock (heard)
                Assert.Contains(heard, e => e.Item1 == "peer-handshake-blocked" && e.Item2.Contains("keychain"));
        }
        finally { MirrorLog.Heard -= h; listener.Stop(); }
    }

    [Fact]
    public async Task ReleasingTheKeychainClearsTheCapAndTheNextDialStartsAgain()
    {
        using var keychain = new Keychain();
        using var link = Link(keychain);
        var (listener, port, accepted) = Silent();

        try
        {
            Assert.False(await link.ConnectAsync("kc-dial-three", "127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, link.StuckHandshakes);

            keychain.Gate.Set();
            await Within(TimeSpan.FromSeconds(5), () => link.StuckHandshakes == 0);

            // A new handshake is attempted again (it reaches the seam and, still
            // gated open, fails on its own terms rather than being refused).
            Assert.False(await link.ConnectAsync("kc-dial-three", "127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(2, keychain.Entered);
            Assert.Equal(2, accepted());
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task AStuckInboundHandshakeDoesNotStopTheAcceptLoopAndLaterClientsAreClosedAtOnce()
    {
        using var keychain = new Keychain();
        using var server = Link(keychain, TimeSpan.FromMilliseconds(300));
        server.Listen(0);
        var port = server.BoundPort;

        using var first = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, port);
        await Within(TimeSpan.FromSeconds(5), () => keychain.Entered == 1);

        // Stuck: the inbound setup clock expires and the handshake is abandoned.
        await Within(TimeSpan.FromSeconds(5), () => server.StuckHandshakes == 1);

        // The loop is still accepting, and with the cap held the newcomer is
        // closed rather than given a second native call.
        using var second = new TcpClient();
        await second.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(5));
        var read = await second.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, read);                            // closed by the server
        Assert.Equal(1, keychain.Entered);
        Assert.Equal(1, server.StuckHandshakes);

        // Released: the cap clears and a third client reaches the handshake again.
        keychain.Gate.Set();
        await Within(TimeSpan.FromSeconds(5), () => server.StuckHandshakes == 0);
        using var third = new TcpClient();
        await third.ConnectAsync(IPAddress.Loopback, port);
        await Within(TimeSpan.FromSeconds(5), () => keychain.Entered == 2);
    }

    // A released call may also simply return. The clear is in a continuation,
    // so both endings must lower the count, and the next dial reaches the seam.
    [Fact]
    public async Task AStuckCallThatReturnsNormallyClearsTheCapToo()
    {
        using var keychain = new Keychain { ReturnNormally = true };
        using var link = Link(keychain);
        var (listener, port, _) = Silent();

        try
        {
            Assert.False(await link.ConnectAsync("kc-dial-normal", "127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, link.StuckHandshakes);

            keychain.Gate.Set();
            await Within(TimeSpan.FromSeconds(5), () => link.StuckHandshakes == 0);

            await link.ConnectAsync("kc-dial-normal", "127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, keychain.Entered); // reached the seam again, not refused
        }
        finally { listener.Stop(); }
    }

    // One cap for both directions: a stuck dial blocks the accept side.
    [Fact]
    public async Task AStuckDialBlocksAnInboundHandshake()
    {
        using var keychain = new Keychain();
        using var link = Link(keychain);
        link.Listen(0);
        var (listener, port, _) = Silent();

        try
        {
            Assert.False(await link.ConnectAsync("kc-cross-out", "127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, link.StuckHandshakes);

            using var inbound = new TcpClient();
            await inbound.ConnectAsync(IPAddress.Loopback, link.BoundPort);
            Assert.Equal(0, await inbound.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, keychain.Entered); // the inbound never reached the seam
        }
        finally { listener.Stop(); }
    }

    // ...and a stuck inbound handshake blocks the dial side.
    [Fact]
    public async Task AStuckInboundHandshakeBlocksADial()
    {
        using var keychain = new Keychain();
        using var link = Link(keychain);
        link.Listen(0);
        var (listener, port, accepted) = Silent();

        try
        {
            using var inbound = new TcpClient();
            await inbound.ConnectAsync(IPAddress.Loopback, link.BoundPort);
            await Within(TimeSpan.FromSeconds(5), () => link.StuckHandshakes == 1);

            Assert.False(await link.ConnectAsync("kc-cross-in", "127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(10)));
            await Task.Delay(100);
            Assert.Equal(0, accepted());       // no TCP connection was made
            Assert.Equal(1, keychain.Entered); // and no second native call
        }
        finally { listener.Stop(); }
    }
}
