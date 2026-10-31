using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

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
            Func<X509Certificate2> OwnCertificate);

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
        private sealed class Connected(Stream stream, string pin, string? endpoint = null)
            : IDisposable
        {
            public Stream Stream { get; } = stream;
            public string Pin { get; } = pin;

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
        internal async Task PumpAsync(string machine, Stream stream, CancellationToken ct)
        {
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
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                MirrorLog.Say("peer-read-failed", $"from={machine} {ex.GetType().Name}: {ex.Message}");
            }
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
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _boundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
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

                _ = HandshakeInboundAsync(client, ct);
            }
        }

        [ExcludeFromCodeCoverage]
        private async Task HandshakeInboundAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                // No callback on the constructor: setting one here *and* in the
                // authentication options below is refused by .NET outright, and
                // the options are where the client certificate is judged.
                var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);

                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _seams.OwnCertificate(),
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null
                }, ct).ConfigureAwait(false);

                var offered = tls.RemoteCertificate is null
                    ? null
                    : PeerIdentity.PinOf(X509CertificateLoader.LoadCertificate(tls.RemoteCertificate.GetRawCertData()));

                // Which machine this is comes from `hello`, so the connection is
                // held unnamed until then; the pin is what will be checked
                // against the name it claims.
                Adopt(Unnamed(), tls, offered ?? "", ct);
            }
            catch (Exception ex)
            {
                MirrorLog.Say("peer-inbound-failed", $"{ex.GetType().Name}: {ex.Message}");
                client.Dispose();
            }
        }

        [ExcludeFromCodeCoverage]
        internal async Task<bool> ConnectAsync(
            string machine, string host, int port = DefaultPort, CancellationToken ct = default,
            string? pairingCode = null, bool nameIsProvisional = false)
        {
            if (IsConnected(machine)) return true;

            try
            {
                var client = new TcpClient();
                await client.ConnectAsync(host, port, ct).ConfigureAwait(false);

                var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);

                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = machine,
                    ClientCertificates = new X509Certificate2Collection(_seams.OwnCertificate()),
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,

                    // The certificate is self-signed and has no meaningful name
                    // to validate, exactly as the OpenClaw gateway's does. The
                    // fingerprint is the identity, and it is checked below
                    // against what we paired with rather than here against a
                    // chain that was never going to verify.
                    RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null
                }, ct).ConfigureAwait(false);

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
                MirrorLog.SayOnce($"peer-connect-failed:{machine}",
                    $"to={machine} {OpenClawGateway.ExplainConnectFailure(ex, OperatingSystem.IsMacOS())}");
                return false;
            }
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
            var peer = new Connected(stream, pin, endpoint);

            lock (_gate)
            {
                if (_peers.Remove(machine, out var existing)) existing.Dispose();
                _peers[machine] = peer;
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
            _ = PumpAsync(machine, stream, ct)
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
        internal static string Unnamed() => "(inbound " + PeerProtocol.NewId() + ")";

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
            List<Connected> peers;

            lock (_gate)
            {
                stopping = _stopping;
                listener = _listener;
                peers = _peers.Values.ToList();

                _stopping = null;
                _listener = null;
                _peers.Clear();
            }

            try { stopping?.Cancel(); } catch { }
            try { listener?.Stop(); } catch { }
            foreach (var peer in peers) peer.Dispose();
            stopping?.Dispose();
        }
    }
}
