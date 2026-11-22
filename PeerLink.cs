using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;

namespace Orbweaver
{
    // A direct connection to another copy of this app.
    //
    // **This is the first socket this application has ever listened on**, which
    // is worth stating because it is the source of most of what can go wrong
    // here. Everything else in this app either connects out (OpenClawSocket) or
    // is handed something by the OS (the claude:// URL scheme). Listening is new,
    // and both platforms gate it: macOS behind Local Network consent, Windows
    // behind the firewall.
    //
    // What it replaces: frames typed into a hidden Claude Code session so a
    // *model* could retype them into a peer-messaging tool. That cost a model
    // turn per frame — 222 to 247 seconds for one 6KB chunk, measured — and
    // occasionally corrupted the text it carried. Here a transcript is a write
    // and a read.
    //
    // Two halves, deliberately symmetric: every machine both listens and
    // connects, because either end can be the one being looked at. A connection
    // carries traffic in both directions once established, so two machines need
    // one connection between them rather than one each way.
    internal sealed class PeerLink : IDisposable
    {
        // Everything this needs from the world outside itself, so the whole
        // request/response contract can be driven in a test with no socket, no
        // certificate and no second machine — the same arrangement
        // RemoteMirrorServer.Seams already uses, and for the same reason.
        internal sealed record Seams(
            Func<string, PeerProtocol.PeerMessage, Task> Deliver,
            Func<string, PeerIdentity.Peer?> KnownPeer,
            // Awaited, and allowed to time out (CB-301): the certificate is
            // loaded once and shared, and a load stuck on a keychain prompt
            // must fail this handshake rather than hold the thread.
            Func<CancellationToken, Task<X509Certificate2>> OwnCertificate,
            // The TLS handshakes themselves (CB-411). SslStream's Apple
            // implementation calls into Security.framework, which can block on
            // a locked login keychain or a pending keychain prompt and ignores
            // the token it was handed. Behind a seam so a test can stand in
            // a handshake that blocks its thread and ignores cancellation; null
            // means the real SslStream call.
            Func<SslStream, SslClientAuthenticationOptions, CancellationToken, Task>? ClientHandshake = null,
            Func<SslStream, SslServerAuthenticationOptions, CancellationToken, Task>? ServerHandshake = null)
        {
            internal Func<SslStream, SslClientAuthenticationOptions, CancellationToken, Task> AsClient =>
                ClientHandshake ?? ((tls, options, token) => tls.AuthenticateAsClientAsync(options, token));

            internal Func<SslStream, SslServerAuthenticationOptions, CancellationToken, Task> AsServer =>
                ServerHandshake ?? ((tls, options, token) => tls.AuthenticateAsServerAsync(options, token));
        }

        // Chosen from the unassigned range and fixed rather than negotiated: a
        // port that moved would have to be discovered before it could be
        // connected to, and discovery is what announces the port.
        public const int DefaultPort = 7677;

        private readonly Seams _seams;
        private readonly object _gate = new();
        private readonly Dictionary<string, Connected> _peers = new(StringComparer.OrdinalIgnoreCase);

        private TcpListener? _listener;
        private CancellationTokenSource? _stopping;
        private int _boundPort;

        internal event Action<string>? PeerDisconnected;
        internal event Action<string>? PeerConnected;

        // The port actually being listened on, which is not always the one that
        // was asked for: passing 0 lets the OS choose.
        //
        // Exposed because the alternative — a caller probing for a free port and
        // then asking for it — has a race in it. Between the probe closing and
        // the listener opening, anything else on the machine can take that port,
        // which is a flake that appears only under load and reads as a network
        // failure. Discovery announces this value, so a chosen port is as usable
        // as a fixed one.
        internal int BoundPort
        {
            get { lock (_gate) return _boundPort; }
        }

        internal PeerLink(Seams seams) => _seams = seams;

        // One live connection, and the lock that keeps two writers from
        // interleaving on it.
        //
        // A stream is not safe for concurrent writes, and both halves of the
        // mirror share this one: the server answering a fetch while the client
        // renews a watch is the ordinary case, not a rare one. The old transport
        // had the same problem and solved it the same way — one relay, one turn
        // at a time.
        private sealed class Connected(WatchedStream stream, string pin, string? endpoint = null)
            : IDisposable
        {
            public WatchedStream Stream { get; } = stream;
            public string Pin { get; } = pin;

            // When this side last sent a keepalive on this connection. Starts
            // at adoption, so a fresh link is not pinged the instant it opens.
            public DateTime LastPing { get; set; } = stream.LastRead;

            // When this connection was adopted, on the same clock as LastRead
            // (WatchedStream starts it at Now()). The hello bound counts from here.
            public DateTime Since { get; } = stream.LastRead;

            // Where we dialled, as "host:port", when we dialled. Null on a
            // connection that came to us — the source port of an inbound socket
            // is not the port that machine listens on, so guessing one would
            // record an address that never works.
            public string? Endpoint { get; } = endpoint;
            //
            // **Never disposed, deliberately.** A write that fails drops the
            // connection, and dropping disposes it, all while the sender that hit
            // the failure still holds this gate and others may be queued behind
            // it. A disposed SemaphoreSlim throws on Release and on Wait, so the
            // sender's own `finally` threw out of SendAsync (unobserved, for a
            // caller that does not await it) and nobody queued could be released.
            // The gate owns nothing unmanaged — the stream is what closes — so
            // leaving it alone costs nothing; queued senders then take it in turn,
            // find the stream closed, and report "not delivered".
            public SemaphoreSlim Writing { get; } = new(1, 1);

            public void Dispose() => Stream.Dispose();
        }

        // --- what the mirror halves use -------------------------------------------

        // The SendFrame seam, in the shape both mirror halves already take.
        //
        // False means "not delivered" and nothing more — the same contract the
        // relay's version had. A reply, when there is one, arrives through
        // Deliver rather than from here.
        internal async Task<bool> SendAsync(string machine, PeerProtocol.PeerMessage message)
        {
            Connected? peer;
            lock (_gate) _peers.TryGetValue(machine, out peer);

            if (peer is null)
            {
                MirrorLog.Say("peer-send-no-link", $"to={machine} t={message.Type}");
                return false;
            }

            await peer.Writing.WaitAsync().ConfigureAwait(false);

            try
            {
                await PeerProtocol.WriteAsync(peer.Stream, message).ConfigureAwait(false);

                // A keepalive every fifteen seconds per peer would bury the
                // lines worth reading, so it is the one write not logged.
                if (message.Type != PeerProtocol.Ping)
                    MirrorLog.Say("peer-sent", $"to={machine} t={message.Type} id={message.Id}");

                return true;
            }
            catch (Exception ex)
            {
                // A write that fails has killed the connection: TLS cannot
                // resynchronise mid-record, so there is nothing to retry on it.
                MirrorLog.Say("peer-send-failed", $"to={machine} {ex.GetType().Name}: {ex.Message}");
                Drop(machine);
                return false;
            }
            finally
            {
                if (peer.Writing.CurrentCount == 0) peer.Writing.Release();
            }
        }

        internal IReadOnlyList<string> ConnectedMachines()
        {
            lock (_gate) return _peers.Keys.ToList();
        }

        internal bool IsConnected(string machine)
        {
            lock (_gate) return _peers.ContainsKey(machine);
        }

        // --- reading ---------------------------------------------------------------

        // Reads until the peer hangs up or the connection breaks.
        //
        // Internal and taking a Stream so the whole loop can be driven over a
        // MemoryStream: this is where a framing mistake would show up as a
        // corrupted transcript rather than an error, so it is the part that most
        // wants asserting without a socket in the way.
        //
        // **The loop reads; it never waits for a message to be handled (CB-322).**
        // It used to await Deliver for each message before reading the next,
        // and a handler that answers writes its answer before it returns. A
        // persona picture was one 15MB frame, a write that cannot finish until
        // the far side reads it — and when both Macs served each other's
        // picture in the same few milliseconds, each side's reader was parked
        // inside its own write, waiting on a reader that was waiting on it. No
        // thread blocked anywhere, both sockets stayed ESTABLISHED, Recv-Q
        // climbed to 1.8MB on the mini and stayed there. So messages now go
        // into a queue that one task drains in arrival order: order is kept,
        // because a transfer's pieces and its replies depend on it, and reading
        // carries on whatever a handler is waiting for.
        //
        // The queue is unbounded on purpose. A bound would bring the deadlock
        // back the moment it filled, and what can arrive is limited by a paired
        // peer's own writes, each under PeerProtocol.MaxMessageBytes. The loop
        // still returns only once everything it read has been handled, which is
        // what lets a test over a MemoryStream assert on what was delivered.
        internal async Task PumpAsync(string machine, Stream stream, CancellationToken ct)
        {
            var inbox = Channel.CreateUnbounded<(string Machine, PeerProtocol.PeerMessage Message)>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

            var handling = HandleInOrderAsync(inbox.Reader);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var message = await PeerProtocol.ReadAsync(stream, ct).ConfigureAwait(false);

                    // Null is a clean hangup between messages — an ordinary
                    // disconnect, not a fault worth reporting.
                    if (message is null) break;

                    if (message.Version != PeerProtocol.Version)
                    {
                        MirrorLog.Say("peer-version",
                            $"from={machine} theirs={message.Version} ours={PeerProtocol.Version}");
                        continue;
                    }

                    // The keepalive has done its job by arriving: reading it is
                    // what moved this connection's last-read time on.
                    if (message.Type == PeerProtocol.Ping) continue;

                    // CB-344: an inbound connection is unauthenticated until its
                    // hello has been judged. TLS accepted whatever certificate it
                    // offered, and the pin is checked only in GreetedAsync — so
                    // until then nothing it sends may reach the mirror, whose own
                    // gate asks only whether a connection exists.
                    //
                    // Discarded rather than dropped. A dial Adopts before it
                    // writes its hello, so a legitimate peer can race a request
                    // ahead of the greeting; that costs it a retry, where
                    // dropping would cost a reconnect. A stranger gains nothing
                    // either way. `machine` is reassigned only by an accepted
                    // hello and a refused one breaks the loop, so "still
                    // unnamed" is exactly "not yet accepted".
                    if (IsUnnamed(machine) && message.Type != PeerProtocol.Hello)
                    {
                        MirrorLog.SayOnce("peer-before-hello", $"t={message.Type} dropped");
                        continue;
                    }

                    MirrorLog.Say("peer-in", $"from={machine} t={message.Type} id={message.Id}");

                    // The greeting is the link's own business and never reaches
                    // the mirror. It is also the only message that can change
                    // what this connection is called, which is why the loop
                    // carries `machine` in a local it is allowed to reassign
                    // rather than reading it back out of the dictionary.
                    if (message.Type == PeerProtocol.Hello)
                    {
                        var named = await GreetedAsync(machine, message).ConfigureAwait(false);
                        if (named is null) break;

                        machine = named;
                        continue;
                    }

                    // The answer to our own greeting: it names the far machine,
                    // and if we were pairing it is them saying the code was
                    // right.
                    //
                    // The name matters even when we thought we knew it. A dial
                    // by address does not know one — there is nothing to know
                    // until the far end speaks — so it files the connection
                    // under the address and this is what corrects it. Without
                    // that, a machine added by hand would appear on screen as
                    // "198.51.100.10".
                    if (message.Type == PeerProtocol.Ok && IsGreetingAnswer(machine, message))
                    {
                        machine = Settle(machine, message.Name);
                        continue;
                    }

                    if (message.Type == PeerProtocol.Err && PairingWith(machine))
                    {
                        MirrorLog.Say("peer-pair-refused", $"by={machine} code={message.Code}");
                        Drop(machine);
                        break;
                    }

                    inbox.Writer.TryWrite((machine, message));
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                MirrorLog.Say("peer-read-failed", $"from={machine} {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                inbox.Writer.TryComplete();
                await handling.ConfigureAwait(false);
            }
        }

        // Hands each queued message to the mirror, one at a time and in the
        // order it was read.
        private async Task HandleInOrderAsync(
            ChannelReader<(string Machine, PeerProtocol.PeerMessage Message)> inbox)
        {
            await foreach (var (machine, message) in inbox.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    await _seams.Deliver(machine, message).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // One bad message must not take the connection down with
                    // it. The relay learned this the expensive way: a
                    // handler that threw was discarded silently and the
                    // machine went on looking healthy while answering
                    // nothing.
                    MirrorLog.Say("peer-handler-threw",
                        $"t={message.Type} {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // --- liveness ----------------------------------------------------------------

        // How often each side says it is still there, and how long a connection
        // may go without reading a single byte before it is treated as dead.
        //
        // CB-322 is why the second exists. The wedged link read nothing for
        // over an hour while both ends logged `connected=2` every ten seconds,
        // because nothing ever asked whether bytes were moving — a socket that
        // is ESTABLISHED is not one that works. Dropping it is the whole
        // recovery: the dial tick redials anything not connected.
        //
        // Sixty seconds is four missed keepalives, so one slow write cannot trip
        // it, and it is measured in bytes rather than messages, so a large
        // message arriving slowly counts as progress the whole way through. A
        // peer on a build without keepalives still sends its mirror HELLO about
        // every ten seconds (the mini's log shows the cadence), which keeps a
        // healthy idle link to it well inside the deadline.
        internal static readonly TimeSpan KeepaliveEvery = TimeSpan.FromSeconds(15);
        internal static readonly TimeSpan StalledAfter = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan WatchEvery = TimeSpan.FromSeconds(5);

        private Timer? _watchdog;

        // One pass of the watchdog: drop what has stalled, ping what is due.
        //
        // Internal and driven by Now so a test can step the clock rather than
        // wait a minute. The stalled connection is dropped by identity, through
        // Forget, for the reason Adopt gives: a name can have moved to a newer
        // connection since this one was listed.
        internal void CheckLiveness()
        {
            List<(string Name, Connected Peer)> peers;
            lock (_gate) peers = _peers.Select(p => (p.Key, p.Value)).ToList();

            var now = Now();

            foreach (var (name, peer) in peers)
            {
                // Before the stalled check: a client that keeps sending anything
                // but hello is never quiet, so the 60 s rule never reaches it
                // (CB-342). Dropped by identity. Known tiny race: a hello that
                // renames the connection between the snapshot above and Forget
                // loses a just-named link at exactly the deadline; the cost is
                // one redial, so it is documented rather than locked.
                if (Unannounced(name, peer.Since, now, InboundSetupTimeout))
                {
                    MirrorLog.SayOnce("peer-inbound-unannounced",
                        $"no hello within {InboundSetupTimeout.TotalSeconds:0.#}s");
                    Forget(peer);
                    continue;
                }

                var quiet = now - peer.Stream.LastRead;

                if (quiet > StalledAfter)
                {
                    MirrorLog.Say("peer-stalled", $"machine={name} quiet={quiet.TotalSeconds:0}s");
                    Forget(peer);
                    continue;
                }

                if (now - peer.LastPing < KeepaliveEvery) continue;

                peer.LastPing = now;
                _ = SendAsync(name, PeerProtocol.Message(PeerProtocol.Ping, PeerProtocol.NewId()));
            }
        }

        // The timer's callback. Excluded from coverage: it is the try/catch
        // that stops a throwing PeerDisconnected subscriber from ending the
        // process on a timer thread, around a method tested on its own.
        [ExcludeFromCodeCoverage]
        private void Watch()
        {
            try { CheckLiveness(); }
            catch (Exception ex) { MirrorLog.Say("peer-watchdog-threw", $"{ex.GetType().Name}: {ex.Message}"); }
        }

        // --- listening and connecting -----------------------------------------------

        [ExcludeFromCodeCoverage]
        internal void Listen(int port = DefaultPort)
        {
            lock (_gate)
            {
                if (_listener is not null) return;

                _stopping ??= new CancellationTokenSource();

                // Any address rather than loopback: the whole point is another
                // machine. On macOS this is the line that needs Local Network
                // consent, and without it the failure arrives at the *client* as
                // EHOSTUNREACH rather than here as anything at all.
                //
                // Assigned only once Start() has succeeded. It used to be
                // assigned first, so a bind that threw left a dead listener in the
                // field and every later Listen returned at the guard above —
                // which made a retry a silent no-op (CB-306).
                var listener = new TcpListener(IPAddress.Any, port);

                try { listener.Start(); }
                catch { listener.Stop(); throw; }

                _listener = listener;
                _boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            }

            _ = AcceptLoopAsync(_stopping!.Token);
        }

        [ExcludeFromCodeCoverage]
        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    MirrorLog.Say("peer-accept-failed", $"{ex.GetType().Name}: {ex.Message}");
                    break;
                }

                // Task.Run so no synchronous stretch of a handshake can hold
                // this loop (CB-411): a native call that blocks before its first
                // await would otherwise stop every later connection being accepted.
                _ = Task.Run(() => HandshakeInboundAsync(client, ct));
            }
        }

        [ExcludeFromCodeCoverage]
        private async Task HandshakeInboundAsync(TcpClient client, CancellationToken ct)
        {
            // Bounded, and only the setup (CB-342). This used to run on the
            // listener's own token, which fires only at Dispose, so a client
            // that connected and never sent a ClientHello held a task, a socket
            // and a descriptor for the life of the process. Adopt below keeps
            // `ct`: the link lives on the listener's token, never this one.
            if (HandshakeBlocked("inbound"))
            {
                client.Dispose();
                return;
            }

            using var setup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            setup.CancelAfter(InboundSetupTimeout);

            SslStream? tls = null;
            var adopted = false;

            try
            {
                // No callback on the constructor: setting one here *and* in the
                // authentication options below is refused by .NET outright, and
                // the options are where the client certificate is judged.
                var own = await _seams.OwnCertificate(setup.Token).ConfigureAwait(false);
                tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);

                var serverOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = own,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null
                };
                var serving = tls;

                await BoundedHandshakeAsync(
                    () => _seams.AsServer(serving, serverOptions, setup.Token), ct, setup.Token,
                    $"{InboundSetupTimeout.TotalSeconds:0.#}s").ConfigureAwait(false);

                var offered = tls.RemoteCertificate is null
                    ? null
                    : PeerIdentity.PinOf(X509CertificateLoader.LoadCertificate(tls.RemoteCertificate.GetRawCertData()));

                // Which machine this is comes from `hello`, so the connection is
                // held unnamed until then; the pin is what will be checked
                // against the name it claims.
                Adopt(Unnamed(), tls, offered ?? "", ct);
                adopted = true;

                // Ended explicitly: a using-disposed source never cancels, so
                // without this "the link is not on the setup token" cannot be
                // tested (CB-348 found the same on the dial side).
                setup.Cancel();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown, not a failure. Closed off-thread: a handshake left
                // stuck may hold this very SslStream (CB-411).
                if (!adopted) Discard(tls, client, stalled: true);
            }
            catch (Exception ex)
            {
                // SayOnce with a fixed key and no endpoint: a scanner or a flood
                // of silent sockets collapses to one line and a restatement
                // every so often, instead of evicting the log's history.
                if (!adopted) Discard(tls, client, ex is HandshakeStalled or OperationCanceledException);

                var why = ex is HandshakeStalled
                    ? ex.Message
                    : !adopted && setup.IsCancellationRequested && !ct.IsCancellationRequested
                    ? $"no handshake within {InboundSetupTimeout.TotalSeconds:0.#}s"
                    : $"{ex.GetType().Name}: {ex.Message}";

                MirrorLog.SayOnce("peer-inbound-failed", why);
            }
        }

        [ExcludeFromCodeCoverage]
        internal async Task<bool> ConnectAsync(
            string machine, string host, int port = DefaultPort, CancellationToken ct = default,
            string? pairingCode = null, bool nameIsProvisional = false)
        {
            switch (BeginDial(machine))
            {
                case DialStart.AlreadyConnected:
                    return true;

                case DialStart.InFlight:
                    MirrorLog.SayOnce($"peer-dial-in-flight:{machine}", $"to={machine} already dialling");
                    return false;
            }

            // Before the socket and before the setup clock (CB-411): while an
            // earlier handshake is still stuck in the keychain, another one would
            // only strand a second thread, and the far side would be left holding
            // a connection that is never going to speak.
            if (HandshakeBlocked("dial", machine))
            {
                EndDial(machine);
                return false;
            }

            // Bounded, and only the setup (CB-301). A far side that accepts
            // the TCP connection and then never speaks — a frozen app, which
            // is exactly the 2026-10-30 shape — used to hang this dial with no
            // end, and with one dial per machine and one tick at a time that
            // would hold the machine, and the whole dial loop, forever. The
            // link itself is not bound by this: Adopt below keeps the caller's
            // token, which is what the connection lives on.
            using var setup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            setup.CancelAfter(DialTimeout);

            TcpClient? client = null;
            SslStream? tls = null;
            var adopted = false;

            try
            {
                // Before the socket, not after: a certificate that is still
                // loading should cost this dial nothing, rather than a TCP
                // connection the far side has already accepted and will hold
                // half-open until it times out (CB-301).
                var own = await _seams.OwnCertificate(setup.Token).ConfigureAwait(false);

                client = new TcpClient();
                await client.ConnectAsync(host, port, setup.Token).ConfigureAwait(false);

                tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);

                var clientOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = machine,
                    ClientCertificates = new X509Certificate2Collection(own),
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,

                    // The certificate is self-signed and has no meaningful name
                    // to validate, exactly as the OpenClaw gateway's does. The
                    // fingerprint is the identity, and it is checked below
                    // against what we paired with rather than here against a
                    // chain that was never going to verify.
                    RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null
                };
                var dialling = tls;

                await BoundedHandshakeAsync(
                    () => _seams.AsClient(dialling, clientOptions, setup.Token), ct, setup.Token,
                    $"{DialTimeout.TotalSeconds:0.#}s", machine).ConfigureAwait(false);

                var offered = tls.RemoteCertificate is null
                    ? ""
                    : PeerIdentity.PinOf(X509CertificateLoader.LoadCertificate(tls.RemoteCertificate.GetRawCertData()));

                var pairing = !string.IsNullOrEmpty(pairingCode);

                // A pairing dial is the one case where an unpinned certificate
                // is allowed through, and it is not a hole: the pin is still
                // recorded, it is simply recorded *now* instead of having been
                // recorded before. What authorises it is the code, and the far
                // machine checks that before answering — so a dial with a wrong
                // code gets an `err` and the connection closes without either
                // side having remembered anything.
                if (!pairing && !Accepts(_seams.KnownPeer(machine), offered))
                {
                    MirrorLog.Say("peer-refused", $"to={machine} pin-not-trusted");
                    tls.Dispose();
                    client.Dispose();
                    return false;
                }

                lock (_gate)
                {
                    if (pairing) _pairingWith.Add(machine);
                    if (nameIsProvisional) _provisional.Add(machine);
                }

                Adopt(machine, tls, offered, ct, endpoint: host + ":" + port);
                adopted = true;

                // The setup bound ends here, explicitly (CB-348). Everything it
                // covered (certificate, TCP, TLS) is done, so it is spent.
                // Cancelled rather than left to `using`, because disposing a
                // CancellationTokenSource stops its timer: a link that had been
                // handed setup.Token by mistake would then never be cancelled at
                // all, and no test could tell. Cancelling here makes that mistake
                // kill the link at once, in every test that makes one.
                setup.Cancel();

                // Said immediately rather than lazily, because until this
                // arrives the far side has a connection it cannot name — and an
                // unnamed connection is one the mirror would attribute every
                // session on to a machine called "(inbound)".
                await SendAsync(machine, PeerProtocol.Message(
                    PeerProtocol.Hello, PeerProtocol.NewId(),
                    name: MachineNames.Mine(), code: pairingCode))
                    .ConfigureAwait(false);

                // Fired here, not left to Rename: Rename only runs for the
                // accepting side's inbound hello, or for a provisional (by-
                // address) dial once the far end's real name arrives — neither
                // of which covers the ordinary case dialing here, reconnecting
                // to a peer already known by its real name. Without this, the
                // dialer never learns its own connection came up at all, which
                // is what let OpenClawSessions.RequestPeerProfileVoices() sit
                // forever having asked nobody: its own gateway load can finish
                // before this connect does, and there was nothing left to
                // retry it (CB-132). A provisional dial still gets exactly one
                // firing, later, once Settle knows the real name to report.
                if (!nameIsProvisional) PeerConnected?.Invoke(machine);

                return true;
            }
            catch (Exception ex)
            {
                // SayOnce, not Say: a machine that cannot be dialled cannot be
                // dialled every ten seconds either, and the identical line
                // repeated forever is what evicts the history worth reading.
                // A socket that never became a link is closed here rather than
                // left to a finalizer — a silent far side would otherwise cost
                // one open connection per tick.
                if (!adopted) Discard(tls, client, ex is HandshakeStalled or OperationCanceledException);

                var why = ex is HandshakeStalled
                    ? ex.Message
                    : !adopted && setup.IsCancellationRequested && !ct.IsCancellationRequested
                    ? $"no answer within {DialTimeout.TotalSeconds:0.#}s"
                    : OpenClawGateway.ExplainConnectFailure(ex, OperatingSystem.IsMacOS());

                MirrorLog.SayOnce($"peer-connect-failed:{machine}", $"to={machine} {why}");
                return false;
            }
            finally
            {
                EndDial(machine);
            }
        }

        // --- a handshake that cannot be cancelled (CB-411) ------------------------

        // **A locked login keychain hung the peer link for over an hour.**
        // SslStream's Apple handshake is a native call that never looks at its
        // token, so `setup.Token` firing did nothing: ConnectAsync never
        // returned, EndDial never ran, and every dial tick after it logged
        // "previous tick still running" with connected=0.
        //
        // The handshake therefore runs on its own pool thread (so a blocking
        // stretch before its first await cannot hold the caller) and is awaited
        // through WaitAsync, which completes on the token whether or not the
        // native call ever does. What cannot be recovered is the thread inside
        // the native call, so the abandoned ones are counted: while any is
        // still out, no new handshake starts (HandshakeBlocked), which bounds
        // the leak to one thread instead of one per tick. When the keychain
        // unlocks the stuck call returns, the count falls, and the next tick
        // reconnects.
        private int _stuckHandshakes;

        // How long an abandoned handshake may keep unwinding before it is called
        // stuck. Settable so a test of the non-cooperative case need not wait it out.
        internal TimeSpan StallGrace { get; set; } = TimeSpan.FromSeconds(2);

        // Abandoned handshakes whose native call has not returned yet.
        internal int StuckHandshakes => Volatile.Read(ref _stuckHandshakes);

        internal sealed class HandshakeStalled(string message) : Exception(message);

        internal async Task BoundedHandshakeAsync(
            Func<Task> start, CancellationToken outer, CancellationToken setup, string bound,
            string? machine = null)
        {
            var handshake = Task.Run(start);

            try
            {
                await handshake.WaitAsync(setup).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (handshake.IsCompleted)
            {
                // The clock and the call finished in the same tick: the call's
                // own outcome is the truth, so a handshake that succeeded is not
                // reported as cancelled.
                await handshake.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A handshake that honours its token still needs a moment to
                // unwind, so IsCompleted is usually false at this instant even
                // for the ordinary CB-301 shape (a far side that accepts TCP
                // and never speaks). That is a silent peer, not a keychain:
                // give the call a short grace, and only one still running after
                // it is counted as stuck. A call that finishes within it yields
                // its own outcome (normally the cancellation), so the callers'
                // "no answer within" arm runs and the count is untouched.
                using var grace = new CancellationTokenSource();
                await Task.WhenAny(handshake, Task.Delay(StallGrace, grace.Token)).ConfigureAwait(false);
                grace.Cancel();

                if (handshake.IsCompleted)
                {
                    // Its own outcome stands: a cooperative call rethrows its
                    // cancellation here, one that won the race simply succeeds.
                    await handshake.ConfigureAwait(false);
                    return;
                }

                Interlocked.Increment(ref _stuckHandshakes);

                // Reading Exception observes it, so a stuck call that finally
                // throws does not surface as an unobserved task exception.
                _ = handshake.ContinueWith(
                    t => { _ = t.Exception; Interlocked.Decrement(ref _stuckHandshakes); },
                    TaskScheduler.Default);

                // Shutdown is not a stall; leave it to the callers' own arm.
                if (outer.IsCancellationRequested) throw;

                throw new HandshakeStalled(
                    (machine is null ? "" : $"to={machine} ")
                    + $"tls handshake did not finish within {bound} "
                    + "\u2014 a locked keychain or pending keychain prompt holds it");
            }
        }

        // True, and logged, while an abandoned handshake is still out.
        private bool HandshakeBlocked(string direction, string? machine = null)
        {
            if (StuckHandshakes < 1) return false;

            MirrorLog.SayOnce("peer-handshake-blocked",
                (machine is null ? "" : $"to={machine} ")
                + $"{direction} refused: an earlier tls handshake is still stuck "
                + "\u2014 a locked keychain or pending keychain prompt holds it");

            return true;
        }

        // Closes a connection that never became a link. After a stall the
        // SslStream is closed off this thread: it may be the very object the
        // stuck native call holds, and disposing it must not take the caller down with it.
        internal static void Discard(SslStream? tls, TcpClient? client, bool stalled)
        {
            if (!stalled)
            {
                tls?.Dispose();
                client?.Dispose();
                return;
            }

            client?.Dispose();
            if (tls is not null) _ = Task.Run(() => { try { tls.Dispose(); } catch { } });
        }

        // --- one dial per machine at a time (CB-301) ------------------------------

        // **The connected check alone let a backlog of dials all complete.**
        // ConnectAsync used to ask IsConnected only on entry, before any await.
        // When the identity import froze the dial loop on 2026-10-30, about
        // twenty-five ticks queued up, every one of them having already passed
        // that check; when the freeze released, each made its own TCP and TLS
        // connection. This side kept the last one Adopt was handed, but the
        // far side had accepted all of them — 44 ESTABLISHED to the mini, and
        // over two hundred FIN_WAIT_1 behind them. Any slow handshake opens the
        // same window, the awaited certificate load included.
        //
        // So a machine being dialled is remembered until the dial ends, and a
        // second dial to it returns at once. Outbound only, and only in this
        // process: an inbound connection is never refused here, and Adopt and
        // Rename keep their replace-the-old-link semantics, which is what
        // tools/peer-probe --evict relies on (CB-306).
        private readonly HashSet<string> _dialling = new(StringComparer.OrdinalIgnoreCase);

        internal enum DialStart { Dial, AlreadyConnected, InFlight }

        // How long a dial may take to become a link: certificate, TCP and TLS
        // together. Longer than the certificate wait (PeerIdentity.HandshakeWait)
        // so a slow load fails as a stall rather than as a dial timeout; short
        // enough that a silent machine costs one tick or two, not the loop.
        // Per link so a test can shorten it without touching any other.
        internal TimeSpan DialTimeout { get; init; } = TimeSpan.FromSeconds(15);

        // How long an inbound connection may spend on each of its two pre-link
        // steps (CB-342): certificate and TLS, then the hello that names it.
        // Sibling of DialTimeout and the same 15 s. The two bounds are counted
        // separately — the setup token bounds TLS, and CheckLiveness counts the
        // hello from Adopt — so the worst case from accept to eviction is twice
        // this plus up to one WatchEvery (about 35 s), not once. Counting from
        // accept would need a timestamp plumbed through Adopt, which is not
        // worth it for a bound whose job is to be finite.
        //
        // Settable rather than init so a test can shorten it on one live server
        // to time out a silent client, then restore it before dialling a real
        // peer whose cold TLS must not race the shortened bound. A TimeSpan
        // write is atomic on 64-bit, and nothing here needs more.
        internal TimeSpan InboundSetupTimeout { get; set; } = TimeSpan.FromSeconds(15);

        // Pure: connected wins over in-flight, because a connected machine is
        // the answer the caller wanted either way.
        internal static DialStart Admit(bool connected, bool inFlight) =>
            connected ? DialStart.AlreadyConnected
            : inFlight ? DialStart.InFlight
            : DialStart.Dial;

        // Decides and, on Dial, claims the machine — one step under the lock,
        // so two callers cannot both see it free.
        internal DialStart BeginDial(string machine)
        {
            lock (_gate)
            {
                var start = Admit(_peers.ContainsKey(machine), _dialling.Contains(machine));
                if (start == DialStart.Dial) _dialling.Add(machine);
                return start;
            }
        }

        internal void EndDial(string machine)
        {
            lock (_gate) _dialling.Remove(machine);
        }

        // Registers the connection and starts reading it.
        //
        // **The pump is started, not awaited, and that distinction was a real
        // bug.** Returning the pump's task made ConnectAsync wait for the
        // connection to *close* before reporting that it had opened — so
        // connecting appeared to hang until its own timeout, and then reported
        // failure on a handshake that had in fact succeeded. Caught by the
        // loopback test, which is the only thing that could have caught it: it
        // is invisible over a MemoryStream, where the stream ends immediately.
        [ExcludeFromCodeCoverage]
        // Internal rather than private so a test can put a MemoryStream under a
        // name and then watch what closing it does. Rename-then-close is
        // precisely the sequence that was broken, and it is unreachable from
        // outside without this.
        internal void Adopt(
            string machine, Stream stream, string pin, CancellationToken ct,
            string? endpoint = null)
        {
            var watched = new WatchedStream(stream, () => Now());
            var peer = new Connected(watched, pin, endpoint);

            lock (_gate)
            {
                if (_peers.Remove(machine, out var existing)) existing.Dispose();
                _peers[machine] = peer;

                _watchdog ??= new Timer(_ => Watch(), null, WatchEvery, WatchEvery);
            }

            // **Dropped by identity, not by the name it had when it opened.**
            // A connection that arrives unnamed is renamed the moment `hello`
            // says who it is, so a continuation closing over the *old* name
            // removes an entry that is no longer there and leaves the renamed
            // one listed as connected forever. Nothing then redials it —
            // WorthDialling skips anything already connected — so the link goes
            // quiet and stays quiet, looking healthy the whole time.
            //
            // That is the exact failure the relay spent six tickets on, and it
            // would have arrived here the day inbound connections started being
            // named. Following the instance costs a scan of a dictionary that
            // holds one entry per machine.
            _ = PumpAsync(machine, watched, ct)
                .ContinueWith(_ => Forget(peer), TaskScheduler.Default);
        }

        // Removes whichever name currently holds this connection.
        private void Forget(Connected peer)
        {
            string? name = null;

            lock (_gate)
            {
                foreach (var held in _peers)
                {
                    if (!ReferenceEquals(held.Value, peer)) continue;

                    name = held.Key;
                    break;
                }

                if (name is not null) _peers.Remove(name);
            }

            MirrorLog.Say("peer-dropped", $"machine={name ?? "(already gone)"}");
            if (name is not null) PeerDisconnected?.Invoke(name);
            peer.Dispose();
        }

        // A name no inbound connection can collide on.
        //
        // It used to be the literal "(inbound)", which is one name for every
        // connection that has not said who it is yet — so two machines dialling
        // at once, or one machine reconnecting before its old connection had
        // been reaped, silently disposed each other's stream. Rare, and
        // indistinguishable from a network drop when it happened.
        internal static string Unnamed() => UnnamedPrefix + PeerProtocol.NewId() + ")";

        internal const string UnnamedPrefix = "(inbound ";

        // Whether a connection is still under the name Unnamed() gave it, i.e.
        // its hello has not been accepted. On an inbound connection that is the
        // difference between a paired machine and anybody who completed TLS
        // (CB-344).
        internal static bool IsUnnamed(string machine) =>
            machine.StartsWith(UnnamedPrefix, StringComparison.Ordinal);

        // Whether a connection that has not yet said who it is has had long
        // enough (CB-342). Pure so the boundary is pinned: exactly at the bound
        // is still inside it.
        internal static bool Unannounced(string name, DateTime since, DateTime now, TimeSpan bound) =>
            IsUnnamed(name) && now - since > bound;

        // --- the decision ------------------------------------------------------------

        // Whether a certificate offered by a machine is the one we paired with.
        //
        // Split out and pure because it is the security of the link, and because
        // the alternative is asserting it through a TLS handshake — which would
        // make the one decision that must never be wrong the hardest thing here
        // to test. See PeerIdentity.Trusts, which this defers to; this exists so
        // the *link's* use of it can be exercised on its own.
        internal static bool Accepts(PeerIdentity.Peer? known, string offeredPin) =>
            PeerIdentity.Trusts(known, offeredPin);

        // What a greeting earns.
        internal enum Greeting
        {
            // Already paired, and the certificate matches what was pinned.
            Trusted,

            // Not paired, but the person at this machine has a pairing window
            // open and the code offered is the one on their screen.
            Paired,

            // Neither. The connection is closed rather than answered.
            Refused,
        }

        // The one security decision on this transport, in one pure function.
        //
        // Everything else about the link is plumbing that fails loudly. This
        // fails *quietly* if it is wrong — a machine that should not have been
        // let in reads exactly like one that should, and what it gets is every
        // transcript on this disk. So it is a function with a truth table rather
        // than a chain of ifs inside a socket handler, and every row of it is a
        // test.
        //
        // The order matters and is deliberate:
        //
        // - **No certificate is refused first.** An empty pin cannot be trusted
        //   and must not be pairable either, or a pairing window would accept
        //   anonymous connections outright.
        // - **A pinned match needs no code.** This is the ordinary case, every
        //   reconnect after the first.
        // - **A correct code pairs, even for a machine already known under a
        //   different pin.** That is a reinstall, and refusing it would leave
        //   the only recovery being to hand-edit a JSON file. The user typed a
        //   code off the other screen; that is the whole authority pairing has.
        // - **Everything else is refused**, including a matching code offered
        //   when no window is open — CodeMatches already says no to a null
        //   expectation, and this states it rather than relying on it.
        internal static Greeting Judge(
            PeerIdentity.Peer? known, string? offeredPin, string? openCode, string? offeredCode)
        {
            if (string.IsNullOrWhiteSpace(offeredPin)) return Greeting.Refused;
            if (PeerIdentity.Trusts(known, offeredPin)) return Greeting.Trusted;
            if (string.IsNullOrEmpty(openCode)) return Greeting.Refused;

            return PeerIdentity.CodeMatches(openCode, offeredCode)
                ? Greeting.Paired
                : Greeting.Refused;
        }

        // --- greeting ---------------------------------------------------------------

        // Answers a `hello`, and names the connection it arrived on.
        //
        // Returns the name to carry from here on, or null when the connection
        // has been refused and closed. Excluded from coverage: it is the
        // plumbing around Judge, which is where the decision lives and which is
        // tested on its own — everything here is a dictionary lookup, a write,
        // and a rename.
        [ExcludeFromCodeCoverage]
        private async Task<string?> GreetedAsync(string machine, PeerProtocol.PeerMessage hello)
        {
            var claimed = hello.Name;

            if (string.IsNullOrWhiteSpace(claimed))
            {
                MirrorLog.Say("peer-greeting-nameless", $"from={machine}");
                Drop(machine);
                return null;
            }

            string pin;

            lock (_gate) pin = _peers.TryGetValue(machine, out var peer) ? peer.Pin : "";

            // Through OpenCode, not off the field: a lapsed window has to read
            // as no window at all, which is the whole point of having one.
            var open = OpenCode();

            var verdict = Judge(_seams.KnownPeer(claimed), pin, open, hello.Code);

            if (verdict == Greeting.Refused)
            {
                MirrorLog.Say("peer-greeting-refused", $"from={claimed}");

                await SendAsync(machine, PeerProtocol.Message(
                    PeerProtocol.Err, hello.Id, code: PeerProtocol.ErrUntrusted))
                    .ConfigureAwait(false);

                Drop(machine);
                return null;
            }

            if (verdict == Greeting.Paired)
            {
                // One window, one pairing. Leaving it open would turn a code
                // read out once into a standing invitation — which the expiry
                // above now also guards, but this is the stronger statement and
                // does not wait five minutes to be true.
                ClosePairing();

                PeerIdentity.Remember(new PeerIdentity.Peer(pin, claimed));
                MirrorLog.Say("peer-paired", $"with={claimed}");
            }

            Rename(machine, claimed);

            await SendAsync(claimed, PeerProtocol.Message(
                PeerProtocol.Ok, hello.Id, name: MachineNames.Mine()))
                .ConfigureAwait(false);

            return claimed;
        }

        // Whether an `ok` is the answer to the greeting we sent.
        //
        // Named rather than inlined because the two arms are different
        // questions. A connection whose name we made up is waiting to be told
        // the real one; a pairing is waiting to be confirmed. Treating every
        // `ok` as ours would swallow acknowledgements the mirror may later want.
        private bool IsGreetingAnswer(string machine, PeerProtocol.PeerMessage message) =>
            (Provisional(machine) && !string.IsNullOrWhiteSpace(message.Name))
            || PairingWith(machine);

        // Whether we are filing this connection under a name we invented.
        //
        // **Only these get renamed, and the alternative was worse than it
        // looked.** The first cut renamed on any named `ok`, which is correct in
        // the abstract and wrong in practice: a caller that dialled a machine
        // under a label — a test, or anything holding a name it chose — finds
        // its connection gone from under it a few milliseconds later, and every
        // send by that name fails with "no link". It showed up as two different
        // integration tests failing on two different runs, which is what a race
        // looks like before you find it.
        //
        // Renaming is only *needed* where the dialled name was never real: an
        // add-by-address dial has an IP and nothing else, and the far end is the
        // only thing that knows what that machine is called.
        private bool Provisional(string machine)
        {
            lock (_gate) return _provisional.Contains(machine);
        }

        // Files the connection under the name the far end gave, and records the
        // pairing if we were making one.
        //
        // Excluded from coverage: the rename and the identity write both need a
        // live connection behind them. Returns the name to carry from here on,
        // the same contract GreetedAsync has.
        [ExcludeFromCodeCoverage]
        private string Settle(string machine, string? theirName)
        {
            var named = Provisional(machine) && !string.IsNullOrWhiteSpace(theirName)
                ? theirName!
                : machine;

            if (!string.Equals(named, machine, StringComparison.OrdinalIgnoreCase))
            {
                Rename(machine, named);

                lock (_gate)
                {
                    _provisional.Remove(machine);

                    // The pairing was recorded against the name we dialled with,
                    // which for an add-by-address was an IP. Move it, or the
                    // pairing is remembered under something the far machine will
                    // never call itself and the next reconnect is refused.
                    if (_pairingWith.Remove(machine)) _pairingWith.Add(named);
                }

                MirrorLog.Say("peer-named", $"{machine} -> {named}");
            }

            if (PairingWith(named)) RememberFromGreeting(named);

            return named;
        }

        // Machines we have greeted with a code and not yet heard back from.
        private readonly HashSet<string> _pairingWith = new(StringComparer.OrdinalIgnoreCase);

        // Connections filed under a name we invented, waiting to be told a real
        // one. Only an add-by-address dial puts anything in here.
        private readonly HashSet<string> _provisional = new(StringComparer.OrdinalIgnoreCase);

        private bool PairingWith(string machine)
        {
            lock (_gate) return _pairingWith.Contains(machine);
        }

        // Excluded from coverage: one write to the identity file, on a path that
        // needs a real TLS handshake to have produced a pin.
        [ExcludeFromCodeCoverage]
        private void RememberFromGreeting(string machine)
        {
            string pin;
            string? endpoint;

            lock (_gate)
            {
                _pairingWith.Remove(machine);

                var known = _peers.TryGetValue(machine, out var peer) ? peer : null;

                pin = known?.Pin ?? "";
                endpoint = known?.Endpoint;
            }

            if (string.IsNullOrWhiteSpace(pin)) return;

            // The address is recorded here and nowhere else, and it is the whole
            // reason a machine on an odd network stays reachable after the first
            // time. Without it a peer added by address pairs once and is never
            // dialled again — the reconnect loop only knows about machines it
            // can hear announcing, which on that network is precisely none.
            PeerIdentity.Remember(new PeerIdentity.Peer(pin, machine, endpoint));
            MirrorLog.Say("peer-paired", $"with={machine} at={endpoint ?? "(unknown)"}");
        }

        // --- pairing window ---------------------------------------------------------

        private string? _openCode;
        private DateTime _openUntil;

        // How long a shown code is good for.
        //
        // Five minutes is long enough to read a code off one screen and type it
        // into another in the next room, and short enough that a code left on
        // screen over lunch is not an invitation. It is not a guess at how fast
        // people type — the window reopens with one click.
        internal static readonly TimeSpan PairingWindowLife = TimeSpan.FromMinutes(5);

        // Injectable for the same reason RemoteMirrorServer.Now is: a window
        // lapsing has to be assertable without waiting five minutes for it.
        internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

        // Whether a window is still open, given when it was opened.
        //
        // **This exists because the version without it overclaimed.** The first
        // cut of this had no expiry and a comment saying the window is closed
        // by "the pairing completing or the user closing the settings pane".
        // The first half was true. The second was not wired to anything — no
        // caller of ClosePairing existed anywhere in the app — so a code shown
        // once stayed valid until Buddy was restarted, which is the standing
        // invitation that comment claimed to have ruled out.
        //
        // Pure so the lapse is a test rather than a five-minute wait, and so
        // that the rule reads in one place instead of being an inequality
        // buried in a lock.
        internal static string? StillOpen(string? code, DateTime until, DateTime now) =>
            code is not null && now < until ? code : null;

        // Opens this machine to one pairing, and returns the code to read out.
        internal string OpenForPairing() => OpenForPairing(PeerIdentity.NewPairingCode());

        // The same, with the code given rather than generated — which is how a
        // machine with no screen is paired. See PeerSessions.HonourPairingFile.
        internal string OpenForPairing(string code)
        {
            var until = Now() + PairingWindowLife;

            lock (_gate)
            {
                _openCode = code;
                _openUntil = until;
            }

            MirrorLog.Say("peer-pairing-open", $"window open for {PairingWindowLife.TotalMinutes:0} min");
            return code;
        }

        internal void ClosePairing()
        {
            lock (_gate)
            {
                _openCode = null;
                _openUntil = default;
            }
        }

        internal bool PairingOpen => OpenCode() is not null;

        // The code a greeting is judged against, or null once it has lapsed.
        private string? OpenCode()
        {
            string? code;
            DateTime until;

            lock (_gate)
            {
                code = _openCode;
                until = _openUntil;
            }

            return StillOpen(code, until, Now());
        }

        internal void Drop(string machine)
        {
            Connected? peer;
            lock (_gate) _peers.Remove(machine, out peer);

            if (peer is null) return;

            MirrorLog.Say("peer-dropped", $"machine={machine}");
            PeerDisconnected?.Invoke(machine);
            peer.Dispose();
        }

        // A connection that arrived before it said who it was, renamed once
        // `hello` settles it. Kept separate from Adopt so the naming is one step
        // with one reason rather than a special case inside the handshake.
        internal void Rename(string from, string to)
        {
            lock (_gate)
            {
                if (!_peers.Remove(from, out var peer)) return;

                // The same object under a new key, deliberately: Forget finds a
                // closing connection by identity, and a copy would leave it
                // unable to find this one at all.
                if (_peers.Remove(to, out var replaced) && !ReferenceEquals(replaced, peer))
                    replaced.Dispose();

                _peers[to] = peer;
            }

            PeerConnected?.Invoke(to);
        }

        [ExcludeFromCodeCoverage]
        public void Dispose()
        {
            CancellationTokenSource? stopping;
            TcpListener? listener;
            Timer? watchdog;
            List<Connected> peers;

            lock (_gate)
            {
                stopping = _stopping;
                listener = _listener;
                watchdog = _watchdog;
                peers = _peers.Values.ToList();

                _stopping = null;
                _listener = null;
                _watchdog = null;
                _peers.Clear();
            }

            watchdog?.Dispose();

            try { stopping?.Cancel(); } catch { }
            try { listener?.Stop(); } catch { }
            foreach (var peer in peers) peer.Dispose();
            stopping?.Dispose();
        }
    }
}
