using System.Text.Json;

namespace Orbweaver
{
    // Carries the mirror over a direct connection instead of through a relay.
    //
    // The two mirror halves were already transport-agnostic — their whole
    // dependency on the wire is one `SendFrame` delegate each, plus a symmetric
    // way in. This is what satisfies those seams with a socket rather than with
    // a language model retyping base64.
    //
    // **It translates rather than rewrites, and that is deliberate.** The halves
    // still speak MirrorProtocol frames; this turns each one into a
    // PeerProtocol message on the way out and back again on the way in. That
    // looks like an indirection worth removing, and it will be removed — the
    // chunking, base64 and digests come out in the next step. Keeping the
    // translation for one step means the existing mirror suites run unchanged
    // against the new transport, which is the only cheap way to prove the swap
    // altered no behaviour. Doing both at once would mean rewriting the tests
    // that were supposed to be the check.
    internal sealed class PeerMirrorHost : IDisposable
    {
        // The host owns routing, while resolving a local workspace identity and
        // accepting one are OpenClaw concerns. Keeping that boundary explicit
        // lets the real TLS integration test model two machines without giving
        // either one a file-reading backdoor into the other.
        internal sealed record OpenClawIdentitySeams(
            Func<string, IReadOnlyList<string>, IReadOnlyList<OpenClawPeerIdentity.Row>> Resolve,
            Action<string, string, IReadOnlyList<OpenClawPeerIdentity.Row>> Apply);

        private readonly PeerLink _link;
        private readonly OpenClawIdentitySeams _openClawIdentity;
        private readonly object _gate = new();

        // The far machine a connection belongs to, once `hello` has said so.
        // Until then an inbound connection has no name — see PeerLink.Rename.
        private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);

        private RemoteMirrorClient? _client;
        private RemoteMirrorServer? _server;

        // CB-357. The answering half is two seams rather than a type of this
        // file's own: this desk's facts for `launch-info`, and the receiving
        // server's decision for `launch`. The asking half is the client the
        // window talks to; replies and dropped links are routed to it here.
        private PeerLaunchInfoSource? _launchInfo;
        private Func<string, JsonElement, Task<PeerLaunchResult>>? _launchHandler;
        private PeerLaunchClient? _launchClient;
        private PeerBrowseServer? _browse;
        private PeerPastSessionsServer? _past;

        internal PeerMirrorHost(OpenClawIdentitySeams? openClawIdentity = null)
        {
            _openClawIdentity = openClawIdentity ?? new OpenClawIdentitySeams(
                OpenClawSessions.PeerProfileVoices, OpenClawSessions.ApplyPeerProfileVoices);
            _link = new PeerLink(new PeerLink.Seams(
                Deliver: DeliverAsync,
                KnownPeer: PeerIdentity.PeerFor,
                OwnCertificate: PeerIdentity.HandshakeCertificateAsync));
            _link.PeerDisconnected += OpenClawSessions.ForgetPeerProfileVoices;
            _link.PeerDisconnected += machine => LaunchClient?.OnDisconnected(machine);
            _link.PeerDisconnected += machine => _launchDisconnected?.Invoke(machine);
            _link.PeerConnected += _ => OpenClawSessions.RequestPeerProfileVoices();
        }

        internal PeerLink Link => _link;

        private Func<string, JsonElement, Task<PeerLaunchResult>>? _trustHandler;
        private Action<string>? _launchDisconnected;

        internal PeerLaunchClient? LaunchClient
        {
            get { lock (_gate) return _launchClient; }
        }

        // Wires CB-357 in. Any argument may be null: a desk with no handler
        // answers a `launch` as switched off, and one with no info source
        // stays silent to `launch-info-get`, which an asker reads as an older
        // peer.
        internal void ServeLaunch(
            PeerLaunchInfoSource? info,
            Func<string, JsonElement, Task<PeerLaunchResult>>? handler,
            PeerLaunchClient? client,
            PeerBrowseServer? browse = null,
            Func<string, JsonElement, Task<PeerLaunchResult>>? trustHandler = null,
            Action<string>? onDisconnected = null,
            PeerPastSessionsServer? past = null)
        {
            lock (_gate)
            {
                _past = past;
                _trustHandler = trustHandler;
                _launchDisconnected = onDisconnected;
                _browse = browse;
                _launchInfo = info;
                _launchHandler = handler;
                _launchClient = client;
            }
        }

        // The client a panel talks to, when the link is what is carrying the
        // mirror. Null until Serve has built one.
        internal RemoteMirrorClient? Client
        {
            get { lock (_gate) return _client; }
        }

        // Puts a test's client where Serve would have put a real one. Only
        // PeerSessions.UseClientForTests calls this, on a host whose link is
        // never started.
        internal void UseClientForTests(RemoteMirrorClient client)
        {
            lock (_gate) _client = client;
        }

        // The serving half, exposed for the same reason as the asking one: both
        // have a TickAsync that has to be driven by a clock rather than by the
        // arrival of bytes, or a deadline never lapses and a watch quietly
        // expires. See RemoteControlSessions.MirrorTickAsync.
        internal RemoteMirrorServer? Server
        {
            get { lock (_gate) return _server; }
        }

        // Builds both halves over this link.
        //
        // **Not account-scoped, and that is a simplification worth naming.** A
        // relay is one hidden Claude Code session per account, because Remote
        // Control only reaches sessions signed into the same account — so the
        // relay path needs a client and a server per account and cannot avoid
        // it. A socket has no account: this machine talks to that machine, and
        // which Anthropic login either of them uses is not the socket's
        // business. One pair serves every session on the far side.
        //
        // The account string below is therefore a label rather than a scope. It
        // is still passed because RemoteMirrorServer's local seams use it to
        // read this machine's own agent roster, which *is* per-account.
        internal void Serve(
            Func<IReadOnlyList<string>> accounts,
            Func<IReadOnlyList<(string SessionId, SessionStatus Status)>> localSessions)
        {
            // Evaluated once: a label, not a scope. The roster itself re-reads
            // `accounts` on every build (CB-300).
            var first = accounts();
            var label = first.Count > 0 ? first[0] : ".claude";

            lock (_gate)
            {
                if (_client is not null) return;

                _client = new RemoteMirrorClient(
                    label, new RemoteMirrorClient.Seams(SendFrameAsync));

                var seams = RemoteMirrorServer.AllAccountSeams(
                    accounts, SendFrameAsync, localSessions);

                _server = new RemoteMirrorServer(label, seams with { PeerAllowed = MayAsk });
            }
        }

        internal void Bind(RemoteMirrorClient client, RemoteMirrorServer server)
        {
            lock (_gate)
            {
                _client = client;
                _server = server;
            }
        }

        // Optional feature negotiation: old peers ignore this new message, so
        // the caller receives no response and retains its existing fallback.
        internal async Task RequestOpenClawProfileVoicesAsync(string gatewayPin,
            IReadOnlyList<string> agentIds)
        {
            if (!OpenClawPeerIdentity.ValidPin(gatewayPin)) return;
            var ids = agentIds.Where(OpenClawPeerIdentity.ValidAgentId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(OpenClawPeerIdentity.MaxAgents).ToList();
            if (ids.Count == 0) return;
            var request = new OpenClawPeerIdentity.Request(gatewayPin, ids);
            foreach (var machine in _link.ConnectedMachines())
                await _link.SendAsync(machine, PeerProtocol.Message(PeerProtocol.OpenClawIdentityGet,
                    PeerProtocol.NewId(), body: PeerProtocol.BodyOf(request))).ConfigureAwait(false);
        }

        // --- outbound --------------------------------------------------------------

        // The SendFrame seam both halves take.
        //
        // A MirrorProtocol frame goes out as the *text* of a PeerProtocol
        // message rather than being re-encoded field by field. That keeps this
        // step to one moving part: the frame is already a self-describing line,
        // and the next step replaces it wholesale rather than teaching this to
        // decompose it first.
        internal Task<bool> SendFrameAsync(string machine, string frame) =>
            _link.SendAsync(
                machine,
                PeerProtocol.Message(PeerProtocol.Fetch, PeerProtocol.NewId(),
                    body: PeerProtocol.BodyOf(frame)));

        // --- inbound ---------------------------------------------------------------

        internal async Task DeliverAsync(string machine, PeerProtocol.PeerMessage message)
        {
            if (message.Type == PeerProtocol.OpenClawIdentityGet && message.Body is { } requestBody)
            {
                var request = OpenClawPeerIdentity.RequestFrom(requestBody);
                if (request is null || !MayAsk(machine)) return;
                var rows = _openClawIdentity.Resolve(request.GatewayPin, request.AgentIds);
                var response = new OpenClawPeerIdentity.Response(request.GatewayPin, rows);
                await _link.SendAsync(machine, PeerProtocol.Message(PeerProtocol.OpenClawIdentity,
                    message.Id, body: PeerProtocol.BodyOf(response))).ConfigureAwait(false);
                return;
            }

            if (message.Type == PeerProtocol.OpenClawIdentity && message.Body is { } responseBody)
            {
                var response = OpenClawPeerIdentity.ResponseFrom(responseBody);
                if (response is not null && MayAsk(machine))
                    _openClawIdentity.Apply(machine, response.GatewayPin, response.Voices);
                return;
            }

            switch (message.Type)
            {
                // Both answers leave the link's in-order queue (CB-322): a
                // launch waits up to 30 s for the session to check in, and an
                // info answer locates CLIs and tmux on disk. Awaited here they
                // would hold every other message from this peer — roster,
                // transcript windows, chat input — behind them.
                case PeerProtocol.LaunchInfoGet:
                    if (MayAsk(machine)) _ = Task.Run(() => AnswerLaunchInfoAsync(machine, message));
                    return;

                case PeerProtocol.Launch:
                    if (MayAsk(machine)) _ = Task.Run(() => AnswerLaunchAsync(machine, message));
                    return;

                // CB-397: the signed answer to a trust question. Off the
                // link's queue for the same reason: it can wait 30 s for a
                // check-in. Answered as a launch-result on the same id.
                case PeerProtocol.LaunchTrust:
                    if (MayAsk(machine)) _ = Task.Run(() => AnswerLaunchAsync(machine, message, trust: true));
                    return;

                case PeerProtocol.ListDirGet:
                    if (MayAsk(machine)) _ = Task.Run(() => AnswerListDirAsync(machine, message));
                    return;

                // CB-434: reads every transcript head in a window, so off the queue too.
                case PeerProtocol.PastSessionsGet:
                    if (MayAsk(machine)) _ = Task.Run(() => AnswerPastSessionsAsync(machine, message));
                    return;

                case PeerProtocol.LaunchInfo:
                case PeerProtocol.LaunchResult:
                case PeerProtocol.ListDir:
                case PeerProtocol.PastSessions:
                    if (MayAsk(machine)) LaunchClient?.OnReply(machine, message);
                    return;
            }

            var text = message.Body?.ValueKind == JsonValueKind.String
                ? message.Body.Value.GetString()
                : null;

            if (string.IsNullOrEmpty(text)) return;

            var frame = MirrorProtocol.TryParseFrame(text);

            if (frame is null)
            {
                MirrorLog.Say("peer-frame-unparseable", $"from={machine} len={text.Length}");
                return;
            }

            RemoteMirrorClient? client;
            RemoteMirrorServer? server;

            lock (_gate)
            {
                client = _client;
                server = _server;
            }

            // Which half answers is decided by the frame rather than by who sent
            // it — a request is for the server here, a reply for the client, and
            // one connection carries both directions because both machines are
            // asking each other.
            switch (frame.Type)
            {
                case MirrorProtocol.Chunk:
                case MirrorProtocol.Ok:
                case MirrorProtocol.Err:
                    if (client is not null) await client.OnFrameAsync(machine, frame).ConfigureAwait(false);
                    else MirrorLog.Say("peer-dropped", $"t={frame.Type} no client bound");
                    break;

                default:
                    if (server is not null) await server.HandleAsync(machine, frame).ConfigureAwait(false);
                    else MirrorLog.Say("peer-dropped", $"t={frame.Type} no server bound");
                    break;
            }
        }

        private async Task AnswerLaunchInfoAsync(string machine, PeerProtocol.PeerMessage message)
        {
            PeerLaunchInfoSource? source;
            lock (_gate) source = _launchInfo;

            if (source is null) return;

            // Nothing may escape: this runs unobserved, off the queue.
            try
            {
                await _link.SendAsync(machine, PeerProtocol.Message(PeerProtocol.LaunchInfo, message.Id,
                    body: PeerLaunchWire.EncodeInfo(source.Build()))).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                MirrorLog.Say("peer-launch-info-failed", $"from={machine} {e.GetType().Name}");
            }
        }

        private async Task AnswerListDirAsync(string machine, PeerProtocol.PeerMessage message)
        {
            PeerBrowseServer? server;
            lock (_gate) server = _browse;

            // No server wired is an older desk's silence: the asker times out.
            if (server is null) return;

            try
            {
                var result = await server.HandleAsync(message.Body ?? default).ConfigureAwait(false);
                await _link.SendAsync(machine, PeerProtocol.Message(PeerProtocol.ListDir, message.Id,
                    body: PeerLaunchWire.EncodeDir(result))).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                MirrorLog.Say("peer-list-dir-failed", $"from={machine} {e.GetType().Name}");
            }
        }

        private async Task AnswerPastSessionsAsync(string machine, PeerProtocol.PeerMessage message)
        {
            PeerPastSessionsServer? server;
            lock (_gate) server = _past;

            // No server wired is an older desk's silence, as for list-dir.
            if (server is null) return;

            try
            {
                var body = server.Handle(message.Body ?? default, out var result);
                // Codes and counts only: titles and folders never reach the log.
                MirrorLog.Say("peer-past-sessions", $"from={machine} {result.Code} rows={result.Rows.Count}");
                await _link.SendAsync(machine, PeerProtocol.Message(PeerProtocol.PastSessions, message.Id, body: body))
                    .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                MirrorLog.Say("peer-past-sessions-failed", $"from={machine} {e.GetType().Name}");
            }
        }

        private async Task AnswerLaunchAsync(string machine, PeerProtocol.PeerMessage message, bool trust = false)
        {
            Func<string, JsonElement, Task<PeerLaunchResult>>? handler;
            lock (_gate) handler = trust ? _trustHandler : _launchHandler;

            PeerLaunchResult result;

            if (handler is null)
                result = new PeerLaunchResult(false, PeerLaunchCodes.PeerLaunchOff, null, null);
            else
                try
                {
                    result = await handler(machine, message.Body ?? default).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    // A throwing handler must still answer: the asker is
                    // waiting up to 45 s on this id.
                    MirrorLog.Say("peer-launch-failed", $"from={machine} {e.GetType().Name}");
                    result = new PeerLaunchResult(false, RelayReplies.LaunchFailed, null, null);
                }

            try
            {
                await _link.SendAsync(machine, PeerProtocol.Message(PeerProtocol.LaunchResult, message.Id,
                    body: PeerLaunchWire.EncodeResult(result))).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                MirrorLog.Say("peer-launch-reply-failed", $"from={machine} {e.GetType().Name}");
            }
        }

        // --- who may ask -------------------------------------------------------------

        // The PeerAllowed seam. Over this transport the question is mostly
        // answered by the time a message arrives: a connection we dialled
        // checked the far certificate against its pin before it was adopted.
        //
        // An inbound one did not. Its TLS handshake accepts any certificate,
        // and the pin is judged only when its hello arrives — until then it is
        // listed as connected under an "(inbound …)" name. Reading this as
        // "connected means pinned" was CB-344: anything that completed TLS
        // could ask. PeerLink now discards a stranger's frames before they get
        // here, and this refuses the unnamed entry as well, so the one
        // assumption is not the whole defence.
        //
        // Otherwise it stays a connection check rather than a name check —
        // the difference between a guard and a boundary.
        internal bool MayAsk(string machine) =>
            !PeerLink.IsUnnamed(machine) && _link.IsConnected(machine);

        public void Dispose() => _link.Dispose();
    }
}
