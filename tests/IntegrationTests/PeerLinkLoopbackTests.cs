using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Orbweaver.IntegrationTests;

// Two PeerLinks talking to each other over a real socket on loopback.
//
// **This is the one thing about the peer link that unit tests cannot reach and
// that most needs to be true.** Everything else — framing, the trust decision,
// the read loop — is asserted over a MemoryStream, deliberately. What that
// leaves untested is the handshake: a self-signed certificate offered by both
// ends, a client certificate demanded by the server, TLS 1.2 or 1.3 negotiated
// between two .NET processes on two different operating systems. That is
// exactly the shape of thing that compiles, passes every offline test, and then
// fails on one platform during bring-up.
//
// Loopback rather than a real interface on purpose: 127.0.0.1 is exempt from
// macOS Local Network consent, so this tests the TLS and framing without also
// testing a permission dialog that CI cannot answer. The permission is a
// packaging concern and is handled in the Info.plist, not here.
//
// Port 0 lets the OS pick, so two of these can never collide with each other or
// with a developer's running copy of the app.
public class PeerLinkLoopbackTests
{
    // One certificate, exported and reloaded rather than used as created.
    //
    // Not a formality: an ephemeral key is not always usable by SslStream on
    // macOS, and a certificate that works in-process but not in a handshake is
    // the least helpful possible test result. PeerIdentity stores PKCS#12 for
    // the same reason.
    private static readonly Lazy<X509Certificate2> Cert = new(() =>
    {
        using var key = RSA.Create(2048);

        var request = new CertificateRequest(
            "CN=loopback", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        using var made = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        return X509CertificateLoader.LoadPkcs12(
            made.Export(X509ContentType.Pkcs12), password: null, X509KeyStorageFlags.Exportable);
    });

    private static PeerLink Link(
        List<PeerProtocol.PeerMessage> got, string pin, TaskCompletionSource? arrived = null) =>
        new(new PeerLink.Seams(
            Deliver: (_, message) =>
            {
                got.Add(message);
                arrived?.TrySetResult();
                return Task.CompletedTask;
            },
            // Both ends present the same certificate here, so each is the peer
            // the other paired with.
            KnownPeer: machine => new PeerIdentity.Peer(pin, machine),
            OwnCertificate: () => Cert.Value));

    [Fact]
    public async Task TwoLinksCompleteATlsHandshakeAndExchangeAMessage()
    {
        var pin = PeerIdentity.PinOf(Cert.Value);

        var onServer = new List<PeerProtocol.PeerMessage>();
        var reachedServer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var server = Link(onServer, pin, reachedServer);
        using var client = Link(new List<PeerProtocol.PeerMessage>(), pin);
        var connected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.PeerConnected += machine => connected.TrySetResult(machine);
        client.PeerDisconnected += machine => disconnected.TrySetResult(machine);

        // Port 0: the OS picks, and the listener reports what it chose. Probing
        // for a free port and then asking for it has a race between the probe
        // closing and the listener opening.
        server.Listen(0);
        var port = server.BoundPort;

        Assert.True(
            await client.ConnectAsync("loopback", "127.0.0.1", port, Timeout(10)),
            "the client could not complete a TLS handshake against the listener");

        Assert.True(client.IsConnected("loopback"));
        Assert.Equal(MachineNames.Mine(), await connected.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        // Not a `hello`, which the link now consumes itself: the greeting is
        // what names an inbound connection, so it never reaches Deliver. Using
        // one here would have asserted that a refusal looked like a delivery.
        Assert.True(
            await client.SendAsync("loopback", PeerProtocol.Message(PeerProtocol.Window, "over-tls")),
            "the message was not accepted for sending");

        // Waited for rather than slept on: the far side reads on its own task,
        // and a sleep would be a wall-clock claim a loaded runner will not
        // honour — a mistake this repository has already fixed four times.
        await reachedServer.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var received = Assert.Single(onServer);
        Assert.Equal(PeerProtocol.Window, received.Type);
        Assert.Equal("over-tls", received.Id);

        // The greeting did its job: the listener's side of this connection is
        // filed under the machine that dialled rather than under the
        // placeholder it was accepted as. Without it the mirror would attribute
        // every session on the far machine to a peer called "(inbound …)".
        Assert.True(
            server.IsConnected(MachineNames.Mine()),
            "the greeting did not name the inbound connection");

        client.Drop("loopback");
        Assert.Equal("loopback", await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // The far end going away, rather than this end dropping it. The link's read
    // pump ends and Forget removes the connection by identity, then raises
    // PeerDisconnected if anyone is listening. Two cases, one per arm of that
    // `?.`, each asserting what its arm is for.
    //
    // CB-242: neither was asserted, and between them they are why PeerLink.cs's
    // branch figure moved on identical code (51/62 most runs, 52/62 in 2 of 29
    // on this Mac). The listened-to arm happened to be reached on every run by
    // other tests; the unlistened arm was reached only when a Forget beat its
    // own link's Dispose in some test's teardown — Dispose clears the table, so
    // a Forget after it finds no name and never gets this far. Measured rather
    // than reasoned: each case below alone covers one arm of line 474's `?.`,
    // the two together cover both, and the stability rounds afterwards read
    // 52/62 every time. The teardown race in PeerMirrorEndToEndTests is still
    // there; it is now harmless to the figure, because it can only re-reach an
    // arm these cases always cover.
    [Fact]
    public async Task AFarEndThatHangsUpFiresPeerDisconnectedOnThisSide()
    {
        var (server, client) = await ConnectedPair();
        using (server)
        using (client)
        {
            var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.PeerDisconnected += machine => disconnected.TrySetResult(machine);

            // The far machine quits. Nothing on this side is told to drop anything.
            server.Dispose();

            Assert.Equal("loopback", await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(client.IsConnected("loopback"));
        }
    }

    // The same departure on a link nobody is listening to — the case where
    // Forget's `?.` finds no subscriber. Still has to stop listing the far end:
    // a connection left in the table is one WorthDialling will never redial, so
    // the machine would read as connected forever. There is no event to wait
    // on, by construction, so this waits on the state itself, with a backstop
    // only for a pump that never ends.
    [Fact]
    public async Task ALinkNobodyListensToStillForgetsAFarEndThatHangsUp()
    {
        var (server, client) = await ConnectedPair();
        using (server)
        using (client)
        {
            server.Dispose();

            var backstop = System.Diagnostics.Stopwatch.StartNew();
            while (client.IsConnected("loopback"))
            {
                Assert.True(backstop.Elapsed < TimeSpan.FromSeconds(30), "the far end hung up and this side never noticed");
                await Task.Delay(10);
            }
        }
    }

    // A client dialled into a listening server, both fully greeted.
    private async Task<(PeerLink Server, PeerLink Client)> ConnectedPair()
    {
        var pin = PeerIdentity.PinOf(Cert.Value);
        var server = Link(new List<PeerProtocol.PeerMessage>(), pin);
        var client = Link(new List<PeerProtocol.PeerMessage>(), pin);
        var connected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.PeerConnected += machine => connected.TrySetResult(machine);

        server.Listen(0);
        Assert.True(
            await client.ConnectAsync("loopback", "127.0.0.1", server.BoundPort, Timeout(10)),
            "the client could not complete a TLS handshake against the listener");
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return (server, client);
    }

    // CB-132: dialing a peer already known by its real name — the ordinary
    // reconnect case, not the provisional by-address one — used to leave the
    // *dialer* with no PeerConnected of its own. Only the accepting side ever
    // fired one, via the inbound greeting's rename. A caller that dials and
    // waits on its own PeerConnected (OpenClawSessions.RequestPeerProfileVoices
    // is exactly this) could then wait forever for a connection already up.
    [Fact]
    public async Task TheDialerFiresItsOwnPeerConnectedForAnAlreadyKnownName()
    {
        var pin = PeerIdentity.PinOf(Cert.Value);

        using var server = Link(new List<PeerProtocol.PeerMessage>(), pin);
        using var client = Link(new List<PeerProtocol.PeerMessage>(), pin);
        var clientConnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PeerConnected += machine => clientConnected.TrySetResult(machine);

        server.Listen(0);

        Assert.True(
            await client.ConnectAsync("loopback", "127.0.0.1", server.BoundPort, Timeout(10)),
            "the client could not complete a TLS handshake against the listener");

        Assert.Equal("loopback", await clientConnected.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // The payload that motivated the whole change: a transcript-sized message,
    // sent whole. Under the old transport this was 6KB chunks of base64 retyped
    // by a model at roughly four minutes each.
    [Fact]
    public async Task ATranscriptSizedMessageCrossesInOnePiece()
    {
        var pin = PeerIdentity.PinOf(Cert.Value);

        var onServer = new List<PeerProtocol.PeerMessage>();
        var reachedServer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var server = Link(onServer, pin, reachedServer);
        using var client = Link(new List<PeerProtocol.PeerMessage>(), pin);

        // Port 0: the OS picks, and the listener reports what it chose. Probing
        // for a free port and then asking for it has a race between the probe
        // closing and the listener opening.
        server.Listen(0);
        var port = server.BoundPort;

        Assert.True(await client.ConnectAsync("loopback", "127.0.0.1", port, Timeout(10)));

        // Half a megabyte, which is the size of a real window measured off the
        // mini (bytes 3293843..3817982 of its transcript).
        var big = new string('x', 512 * 1024);

        Assert.True(await client.SendAsync(
            "loopback",
            PeerProtocol.Message(PeerProtocol.Window, "big", body: PeerProtocol.BodyOf(big))));

        await reachedServer.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var received = Assert.Single(onServer);
        Assert.Equal(big.Length, received.Body!.Value.GetString()!.Length);
    }

    [Fact]
    public async Task AnOptionalProfileVoiceMessageCrossesThePairedTlsLink()
    {
        var pin = PeerIdentity.PinOf(Cert.Value);
        var onServer = new List<PeerProtocol.PeerMessage>();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var server = Link(onServer, pin, arrived);
        using var client = Link(new List<PeerProtocol.PeerMessage>(), pin);
        server.Listen(0);

        Assert.True(await client.ConnectAsync("loopback", "127.0.0.1", server.BoundPort, Timeout(10)));
        var gatewayPin = new string('a', 64);
        var body = PeerProtocol.BodyOf(new OpenClawPeerIdentity.Request(gatewayPin, new[] { "main" }));
        Assert.True(await client.SendAsync("loopback", PeerProtocol.Message(
            PeerProtocol.OpenClawIdentityGet, "profile", body: body)));

        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var received = Assert.Single(onServer);
        Assert.Equal(PeerProtocol.OpenClawIdentityGet, received.Type);
        Assert.Equal("main", OpenClawPeerIdentity.RequestFrom(received.Body!.Value)!.AgentIds.Single());
    }

    // A machine offering a certificate we did not pair with is refused at the
    // client, before anything is exchanged. The pin is the only identity check
    // there is — the certificate is self-signed and has no name worth trusting —
    // so this is the whole of the link's security.
    [Fact]
    public async Task APeerWhosePinWeDoNotKnowIsRefused()
    {
        var onServer = new List<PeerProtocol.PeerMessage>();

        using var server = Link(onServer, PeerIdentity.PinOf(Cert.Value));

        // The client has paired with something else entirely.
        using var client = new PeerLink(new PeerLink.Seams(
            Deliver: (_, _) => Task.CompletedTask,
            KnownPeer: machine => new PeerIdentity.Peer(new string('0', 64), machine),
            OwnCertificate: () => Cert.Value));

        // Port 0: the OS picks, and the listener reports what it chose. Probing
        // for a free port and then asking for it has a race between the probe
        // closing and the listener opening.
        server.Listen(0);
        var port = server.BoundPort;

        Assert.False(
            await client.ConnectAsync("loopback", "127.0.0.1", port, Timeout(10)),
            "a peer offering an unknown certificate should be refused");

        Assert.False(client.IsConnected("loopback"));
    }

    // CB-322: both ends answering a request with something bigger than the
    // socket buffers, at the same moment. That is what two Macs did at
    // 15:59:33 on 9 Nov — each served the other's persona picture within 6 ms
    // — and the link never carried another byte between them.
    //
    // The reader used to await each message's handler before reading the next
    // one, and the handler here, like RemoteMirrorServer's AVATAR arm, writes
    // its answer before returning. A write that size cannot finish until the
    // far side reads; the far side's reader was parked in its own write. So
    // each end waited on the other's reader, which was waiting on it: no thread
    // blocked anywhere, two sockets ESTABLISHED for ever, and Recv-Q piling up.
    //
    // 24 MB is chosen to dwarf any send plus receive buffer, loopback's
    // included, so the deadlock is forced rather than hoped for. The control
    // below is the same exchange in one direction only, and it has always
    // passed, which is what shows this test fails for the deadlock and not
    // for the size.
    [Fact]
    public async Task BothEndsAnsweringWithALargeReplyAtOnceDoNotDeadlock()
    {
        var (server, client, serverGot, clientGot) = await AnsweringPair();
        using (server)
        using (client)
        {
            var serverName = MachineNames.Mine();

            // Both asks are on the wire before either answer starts.
            await Task.WhenAll(
                client.SendAsync("loopback", PeerProtocol.Message(PeerProtocol.Window, "ask")),
                server.SendAsync(serverName, PeerProtocol.Message(PeerProtocol.Window, "ask")));

            await Task.WhenAll(clientGot.Task, serverGot.Task).WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Equal(BigAnswer.Length, (await clientGot.Task).Length);
            Assert.Equal(BigAnswer.Length, (await serverGot.Task).Length);
        }
    }

    [Fact]
    public async Task OneEndAnsweringWithALargeReplyArrives()
    {
        var (server, client, _, clientGot) = await AnsweringPair();
        using (server)
        using (client)
        {
            await client.SendAsync("loopback", PeerProtocol.Message(PeerProtocol.Window, "ask"));

            Assert.Equal(BigAnswer.Length, (await clientGot.Task.WaitAsync(TimeSpan.FromSeconds(60))).Length);
        }
    }

    private static readonly string BigAnswer = new('p', 24 * 1024 * 1024);

    // Two links whose handler answers an "ask" with BigAnswer, awaited inside
    // the handler exactly as RemoteMirrorServer awaits its reply, and records
    // an "answer" when one arrives.
    private async Task<(PeerLink Server, PeerLink Client, TaskCompletionSource<string> ServerGot, TaskCompletionSource<string> ClientGot)>
        AnsweringPair()
    {
        var pin = PeerIdentity.PinOf(Cert.Value);
        var serverGot = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientGot = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        PeerLink? server = null, client = null;

        PeerLink.Seams Answering(Func<PeerLink> self, TaskCompletionSource<string> got) => new(
            Deliver: async (machine, message) =>
            {
                if (message.Id == "ask")
                    await self().SendAsync(machine, PeerProtocol.Message(
                        PeerProtocol.Window, "answer", body: PeerProtocol.BodyOf(BigAnswer)));
                else if (message.Id == "answer")
                    got.TrySetResult(message.Body!.Value.GetString()!);
            },
            KnownPeer: machine => new PeerIdentity.Peer(pin, machine),
            OwnCertificate: () => Cert.Value);

        server = new PeerLink(Answering(() => server!, serverGot));
        client = new PeerLink(Answering(() => client!, clientGot));

        var connected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.PeerConnected += machine => connected.TrySetResult(machine);

        server.Listen(0);
        Assert.True(await client.ConnectAsync("loopback", "127.0.0.1", server.BoundPort, Timeout(10)));
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));

        return (server, client, serverGot, clientGot);
    }

    private static CancellationToken Timeout(int seconds) =>
        new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

}
