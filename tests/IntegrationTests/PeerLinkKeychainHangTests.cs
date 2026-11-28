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
        { DialTimeout = dial ?? Short, InboundSetupTimeout = dial ?? Short, StallGrace = TimeSpan.FromMilliseconds(50) };

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
    public async Task ADialPastTheStuckCapFailsAtOnceAndNeverTouchesTheSocket()
    {
        using var keychain = new Keychain();
        using var link = Link(keychain);
        var (listener, port, accepted) = Silent();
        var heard = new List<(string, string)>();
        var h = Hear(heard);
        MirrorLog.Heard += h;

        try
        {
            // CB-416: below the cap each dial still reaches the seam.
            var cap = PeerLink.MaxStuckHandshakes;
            for (var i = 0; i < cap; i++)
                Assert.False(await link.ConnectAsync($"kc-dial-two-{i}", "127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(10)));
            await Within(TimeSpan.FromSeconds(5), () => accepted() >= cap);
            Assert.Equal(cap, accepted());

            // No stopwatch: that the seam, the socket and the count are all
            // untouched below is what says it failed fast, without a wall-clock
            // tolerance a loaded runner would break.
            Assert.False(await link.ConnectAsync("kc-dial-two-over", "127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(10)));

            await Task.Delay(100);
            Assert.Equal(cap, accepted());               // no further TCP connection
            Assert.Equal(cap, keychain.Entered);         // no further native call
            Assert.Equal(cap, link.StuckHandshakes);     // leak bounded to the cap
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
            await Within(TimeSpan.FromSeconds(5), () => accepted() >= 2); // the listener counts asynchronously
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

        var cap = PeerLink.MaxStuckHandshakes;
        var held = new List<TcpClient>();
        for (var i = 1; i <= cap; i++)
        {
            var c = new TcpClient();
            held.Add(c);
            await c.ConnectAsync(IPAddress.Loopback, port);
            await Within(TimeSpan.FromSeconds(5), () => keychain.Entered == i);

            // Stuck: the inbound setup clock expires and the handshake is abandoned.
            await Within(TimeSpan.FromSeconds(5), () => server.StuckHandshakes == i);
        }

        // The loop is still accepting, and with the cap held the newcomer is
        // closed rather than given a second native call.
        using var second = new TcpClient();
        await second.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(5));
        var read = await second.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, read);                            // closed by the server
        Assert.Equal(cap, keychain.Entered);
        Assert.Equal(cap, server.StuckHandshakes);

        // Released: the cap clears and a third client reaches the handshake again.
        keychain.Gate.Set();
        await Within(TimeSpan.FromSeconds(5), () => server.StuckHandshakes == 0);
        using var third = new TcpClient();
        await third.ConnectAsync(IPAddress.Loopback, port);
        await Within(TimeSpan.FromSeconds(5), () => keychain.Entered == cap + 1);
        foreach (var c in held) c.Dispose();
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
            var cap = PeerLink.MaxStuckHandshakes;
            for (var i = 0; i < cap; i++)
                Assert.False(await link.ConnectAsync($"kc-cross-out-{i}", "127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(cap, link.StuckHandshakes);

            using var inbound = new TcpClient();
            await inbound.ConnectAsync(IPAddress.Loopback, link.BoundPort);
            Assert.Equal(0, await inbound.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cap, keychain.Entered); // the inbound never reached the seam
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
            var cap = PeerLink.MaxStuckHandshakes;
            var held = new List<TcpClient>();
            for (var i = 1; i <= cap; i++)
            {
                var inbound = new TcpClient();
                held.Add(inbound);
                await inbound.ConnectAsync(IPAddress.Loopback, link.BoundPort);
                await Within(TimeSpan.FromSeconds(5), () => link.StuckHandshakes == i);
            }

            Assert.False(await link.ConnectAsync("kc-cross-in", "127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(10)));
            await Task.Delay(100);
            Assert.Equal(0, accepted());       // no TCP connection was made
            Assert.Equal(cap, keychain.Entered); // and no further native call
            foreach (var c in held) c.Dispose();
        }
        finally { listener.Stop(); }
    }

    // The CB-301 shape: TCP accepted, TLS silent, and a handshake that honours
    // its token. That is a silent peer, not a keychain, and the old messages
    // stand. The seam observes the token and otherwise never completes.
    private static PeerLink Cooperative(TimeSpan timeout) =>
        Warm() ? new PeerLink(new PeerLink.Seams(
            Deliver: (_, _) => Task.CompletedTask,
            KnownPeer: _ => null,
            OwnCertificate: _ => Task.FromResult(Cert.Value),
            ClientHandshake: async (_, _, ct) => await Task.Delay(Timeout.Infinite, ct),
            ServerHandshake: async (_, _, ct) => await Task.Delay(Timeout.Infinite, ct)))
        { DialTimeout = timeout, InboundSetupTimeout = timeout, StallGrace = TimeSpan.FromSeconds(5) }
        : throw new InvalidOperationException();

    [Fact]
    public async Task ASilentFarSideIsReportedAsNoAnswerNotAsAKeychain()
    {
        using var link = Cooperative(TimeSpan.FromMilliseconds(300));
        var (listener, port, _) = Silent();
        var heard = new List<(string What, string Detail)>();
        void H(string w, string d) { lock (heard) heard.Add((w, d)); }
        MirrorLog.Heard += H;

        try
        {
            Assert.False(await link.ConnectAsync("kc-silent-out", "127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(10)));

            lock (heard)
            {
                var line = Assert.Single(heard, e => e.What == "peer-connect-failed:kc-silent-out");
                Assert.Contains("no answer within", line.Detail);
                Assert.DoesNotContain("keychain", line.Detail);
            }

            Assert.Equal(0, link.StuckHandshakes);
        }
        finally { MirrorLog.Heard -= H; listener.Stop(); }
    }

    [Fact]
    public async Task ASilentClientIsReportedAsNoHandshakeNotAsAKeychain()
    {
        // 200 ms: its detail line ("no handshake within 0.2s") is unique among
        // the tests, because SayOnce swallows an identical repeat.
        using var server = Cooperative(TimeSpan.FromMilliseconds(200));
        server.Listen(0);
        var heard = new List<(string What, string Detail)>();
        var said = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(string w, string d)
        {
            lock (heard) heard.Add((w, d));
            // MirrorLog.Heard is process-wide and the inbound line carries no
            // endpoint, so another test's failed inbound (PeerAcceptLoopTests
            // logs "handshake not under test") can arrive first. Only this
            // server's own detail completes the wait (CB-495).
            if (w == "peer-inbound-failed" && d.Contains("no handshake within 0.2s")) said.TrySetResult();
        }
        MirrorLog.Heard += H;

        try
        {
            using var silent = new TcpClient();
            await silent.ConnectAsync(IPAddress.Loopback, server.BoundPort);
            await said.Task.WaitAsync(TimeSpan.FromSeconds(10));

            lock (heard)
            {
                var line = heard.Last(e => e.What == "peer-inbound-failed" && e.Detail.Contains("no handshake within 0.2s"));
                Assert.DoesNotContain("keychain", line.Detail);
            }

            Assert.Equal(0, server.StuckHandshakes);
        }
        finally { MirrorLog.Heard -= H; }
    }
}
