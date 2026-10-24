using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Orbweaver
{
    // A connection to one OpenClaw gateway: TLS, the JSON-RPC handshake it
    // demands, and request/response correlation over a single socket.
    //
    // Deliberately not an ssh tunnel. The plan called for `ssh -N -L` on the
    // assumption the gateway was loopback-only; it isn't — `gateway.bind` is
    // `lan` — so the socket goes straight to wss://host:port and the whole
    // tunnel subsystem (child process, pid file, startup sweep, orphan hazards,
    // ssh's ControlMaster surprises) doesn't exist. A tunnel would not have
    // helped anyway: the gateway refuses plain HTTP on loopback too, so a
    // forwarded port arrives at the same TLS 1.3 handshake that OpenClawSocket
    // exists to complete. See docs/openclaw-findings.md.
    internal sealed class OpenClawGateway : IDisposable
    {
        // What the gateway calls us. `gateway-client` is its own generic id for
        // a third-party client (the alternatives name its own products), and
        // `ui` is the coarse mode for something a person looks at. Both are
        // closed enums server-side — an unrecognised value is refused during
        // schema validation, before authentication is even attempted, which is
        // how the docs' `mode: "operator"` was found to be wrong.
        private const string ClientId = "gateway-client";
        private const string ClientMode = "ui";
        private const string Role = "operator";

        // Which OS is asking. This was hardcoded "macos" and worked, because the
        // gateway recomputes the signature from the fields it was sent — so a
        // wrong value that is wrong consistently still verifies. It is reported
        // anyway, against the paired device record, and the one moment anybody
        // reads it is the approval prompt: deciding whether to trust a machine
        // while being told the wrong operating system is worse than being told
        // nothing. Confirmed accepted rather than assumed — "windows" passes the
        // gateway's schema validation, which happens before authentication, so a
        // rejected value would surface as a schema error and does not.
        //
        // Used by both the wire `client` block and the signed v3 payload, which
        // must agree to the character, hence one property rather than two
        // literals.
        //
        // Changing this value on an already-paired device is expensive, and
        // worse than a re-approval. Measured: the connect is refused with
        // "device identity changed and must be re-approved", and the gateway's
        // own CLI then reports the device as *already paired* and offers nothing
        // to approve — `devices approve --latest` cannot clear it. Reverting the
        // value doesn't clear it either. The way out is to remove the device on
        // the gateway and pair again from scratch.
        //
        // macOS is unaffected: it sends the same string it always sent. Nobody
        // has a Windows or Linux pairing yet, which is the only reason this
        // correction is cheap to make now.
        //
        // Keep the value coarse for the same reason. A version number in here
        // would strand every paired device on every OS update, and the remedy
        // would be a manual removal on the gateway each time.
        private static string Platform =>
            OperatingSystem.IsWindows() ? "windows" :
            OperatingSystem.IsMacOS() ? "macos" :
            OperatingSystem.IsLinux() ? "linux" : "unknown";

        // What we ask to be granted at pairing time. Read-only unless the user
        // has asked to be able to reply — an orb display that cannot make a
        // remote agent do anything is worth being true rather than merely
        // intended, so the wider scope is a separate decision.
        //
        // Read per connection rather than cached: changing the setting restarts
        // the connection, and the gateway treats the new scope set as a fresh
        // pairing to approve.
        private static string[] Scopes => OrbweaverSettings.OpenClawReplyEnabled
            ? new[] { "operator.read", "operator.write" }
            : new[] { "operator.read" };

        private readonly string _host;
        private readonly int _port;

        // The gateway's own auth token (its `gateway.auth.token`), which is a
        // separate thing from the device token and from the device signature.
        // All three are required at once: the gateway token says "this client
        // may talk to this gateway at all", the signature says "and it is the
        // device it claims to be", and the device token is what carries the
        // scopes once pairing has happened. Presenting only the identity is
        // refused with AUTH_TOKEN_MISSING before pairing is even considered.
        private readonly string _gatewayToken;

        private WebSocket? _socket;
        private Stream? _transport;
        private CancellationTokenSource? _cts;
        private Task? _receiveLoop;

        // A WebSocket permits exactly one send and one receive in flight. The
        // receive loop is the only reader by construction; senders queue here
        // rather than racing each other. (The socket is built by
        // WebSocket.CreateFromStream over BouncyCastle's TLS rather than by
        // ClientWebSocket — see OpenClawSocket for why — but the one-at-a-time
        // rule is the same.)
        private readonly SemaphoreSlim _sendGate = new(1, 1);

        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
        private long _nextId;

        // Why the gateway hung up, if it said. Read by the failure path below.
        private string? _closeReason;

        // Set by the handshake, read by callers deciding what to do next.
        public IReadOnlyList<string> GrantedScopes { get; private set; } = Array.Empty<string>();

        // Every method the gateway says it serves, from hello-ok's
        // features.methods (CB-170). Kept so a menu row that needs a method is
        // offered only where the method exists — but it is not a statement
        // about what *this* device may call: measured against 2026.9.2, the
        // list carries sessions.reset to a device whose scopes cannot use it.
        // So nothing reads this without also reading GrantedScopes.
        public IReadOnlySet<string> Methods { get; private set; } = new HashSet<string>();
        public string? ServerVersion { get; private set; }
        public int TickIntervalMs { get; private set; } = 30_000;
        public long MaxPayload { get; private set; } = 26_214_400;

        // The certificate actually presented, as a lowercase hex sha256. The
        // gateway serves a self-signed certificate with no subjectAltName, so
        // there is no name to validate and nothing for the system trust store
        // to say — the fingerprint is the entire identity check. Pinning lives
        // in OpenClawSocket; this is what it saw, so the settings window can
        // show a user the value they are being asked to trust.
        public string? ObservedFingerprint { get; private set; }

        public event Action<string, JsonElement>? EventReceived;

        // How the socket gets opened, and the two waits that would otherwise
        // make the failure paths untestable.
        //
        // OpenClawSocket.ConnectAsync is the only implementation in the app and
        // this is not a plug-in point — it exists because everything in this
        // class *except* opening the socket is protocol: the signed handshake,
        // request/response correlation, frame dispatch, and what happens when a
        // frame is malformed or the peer hangs up mid-request. All of that runs
        // over any WebSocket, and none of it can be reached at all while the one
        // way in is a real TLS 1.3 connection to a gateway that has to exist.
        // The alternative was excluding two thirds of the file, which would have
        // hidden the parts most worth checking. See tests/UnitTests/
        // OpenClawGatewayTests.cs, which drives the whole handshake over an
        // in-memory socket.
        //
        // The two timeouts are here for the same reason and are otherwise
        // constants: a test for "the gateway never answered" is a test that
        // waits out the real timeout, which is ten and twenty seconds of a suite
        // that runs in two.
        internal delegate Task<OpenClawSocket.Connection> Connector(
            string host, int port, string? pinnedFingerprint, CancellationToken ct);

        private readonly Connector _connect;
        private readonly TimeSpan _challengeTimeout;
        private TimeSpan _requestTimeout;

        // The same limit, changed after construction. Only tests need it: the
        // handshake is itself a request, so a gateway built with a 50ms request
        // timeout also has 50ms to finish connecting, and a test of "a request
        // nobody answers is cancelled" would then fail on a slow runner at the
        // handshake, before the request it is about had even been sent.
        internal TimeSpan RequestTimeout
        {
            set => _requestTimeout = value;
        }

        public OpenClawGateway(string host, int port, string gatewayToken)
            : this(host, port, gatewayToken, OpenClawSocket.ConnectAsync)
        {
        }

        internal OpenClawGateway(
            string host,
            int port,
            string gatewayToken,
            Connector connect,
            TimeSpan? challengeTimeout = null,
            TimeSpan? requestTimeout = null)
        {
            _host = host;
            _port = port;
            _gatewayToken = gatewayToken;
            _connect = connect;

            // The gateway speaks first and a machine that is up answers in
            // milliseconds, so ten seconds is a limit rather than an expectation.
            _challengeTimeout = challengeTimeout ?? TimeSpan.FromSeconds(10);

            // One turn's patience. A remote agent can be mid-tool-call, and the
            // supervisor treats a lapsed request as a dead socket and reconnects.
            _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(20);
        }

        internal enum Outcome
        {
            Connected,
            PairingPending,   // we're in the gateway's pending list; a human approves
            AuthRejected,     // terminal: bad token, wrong identity, scope refused
            CertificateMismatch,
            Unreachable
        }

        internal readonly record struct ConnectResult(Outcome Outcome, string? Detail = null);

        public async Task<ConnectResult> ConnectAsync(string? pinnedFingerprint, CancellationToken ct)
        {
            var identity = OpenClawIdentity.Current();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            try
            {
                // TLS and the upgrade both happen in OpenClawSocket, because
                // .NET's own TLS cannot reach this gateway on macOS — see the
                // comment at the top of that file.
                var connection = await _connect(
                    _host, _port, pinnedFingerprint, _cts.Token);

                _socket = connection.Socket;
                _transport = connection.Transport;
                ObservedFingerprint = connection.Fingerprint;
            }
            catch (Exception ex)
            {
                // The outermost message here is almost always "Unable to connect
                // to the remote server", which says nothing — the reason is
                // always one or two InnerExceptions down (a TLS failure, a
                // refused port, a name that didn't resolve). Flatten the chain
                // so the settings window can show something a person can act on.
                var flat = Flatten(ex);

                // BouncyCastle reports a pin mismatch as a fatal bad_certificate
                // alert, which is indistinguishable by type from any other
                // handshake failure — the text is what separates them.
                var mismatch = flat.Contains("bad_certificate", StringComparison.OrdinalIgnoreCase)
                    || flat.Contains("certificate", StringComparison.OrdinalIgnoreCase)
                       && pinnedFingerprint is not null;

                return new ConnectResult(
                    mismatch ? Outcome.CertificateMismatch : Outcome.Unreachable,
                    ExplainConnectFailure(ex, OperatingSystem.IsMacOS()));
            }

            // The gateway speaks first: connect.challenge carries the nonce the
            // device signature has to cover, so there is nothing to send until
            // it arrives. Subscribe *before* the receive loop starts — the
            // challenge is often already sitting on the socket (the test fake
            // queues it; a real gateway can write it the instant the upgrade
            // completes), and starting the loop first lets that frame fire
            // EventReceived with nobody listening. The handshake then sits out
            // its timeout and reports Unreachable. Windows CI failed that way
            // in OpenClawRoomSendTests at exactly the two-second challenge
            // timeout; OpenClawGatewayTests had widened its own to thirty
            // seconds, which papered over the same miss rather than finding it.
            // CB-23 took that deadline away entirely — a test over an in-memory
            // socket has nothing to wait for, so a lost frame there is now a
            // hang with a name rather than an assertion about scopes. CB-23's
            // own measurement — 25.5 seconds for this same handshake against a
            // deliberately starved pool — is the direct evidence that the next
            // comment builds on.
            //
            // CB-68: started on a dedicated (LongRunning) thread rather than a
            // plain Task.Run. This is the one piece of that ticket's
            // investigation that was actually confirmed rather than guessed,
            // so it is worth being precise about what it does and does not fix.
            //
            // Measured directly, with every ThreadPool worker pinned in a
            // blocking call (tests/UnitTests/OpenClawGatewayTests.cs has the
            // harness this was proven against): a plain
            // `Task.Run(() => ReceiveLoopAsync(...))` here is not guaranteed a
            // thread promptly — under sustained saturation, some queued item in
            // the process is left waiting for however long the pool stays
            // saturated, and which item that turns out to be is a race, not a
            // fixed victim. `TaskCreationOptions.LongRunning` removes the
            // receive loop from that race entirely: instrumented, it started on
            // a genuine non-pool thread (`Thread.IsThreadPoolThread == false`)
            // within tens of milliseconds even with the pool fully pinned,
            // every time. That is a real, verified improvement — the one thing
            // in this class that runs for the whole life of the connection no
            // longer depends on the pool having a spare worker to begin with.
            //
            // What this does **not** fix, and CB-68 should not be read as
            // closed on the strength of it alone: WaitForChallengeAsync's and
            // RequestAsync's TaskCompletionSources use
            // RunContinuationsAsynchronously (see the comment on the one in
            // RequestAsync for why that flag has to stay), which means the
            // continuation that resumes this method after `await challengeTask`
            // — and after every `await RequestAsync(...)` — is itself an
            // ordinary Task continuation scheduled onto the same ThreadPool.
            // Under the same saturation, that resumption can still queue.
            // Measured: even with this fix in place, ConnectAsync's overall
            // completion was still delayed by however long the saturation in
            // the test harness lasted. Eliminating that would mean not using
            // ordinary async/await for the rest of the handshake at all, which
            // is a different and much larger change than this ticket's scope.
            // Recorded here rather than left implicit, because a fix that reads
            // as "the hang is solved" when only one contributing cause was is
            // exactly the overclaim CLAUDE.md warns against.
            var challengeTask = WaitForChallengeAsync(_cts.Token);
            _receiveLoop = Task.Factory.StartNew(
                () => ReceiveLoopAsync(_cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default).Unwrap();
            var challenge = await challengeTask;
            if (challenge.Nonce is null)
            {
                return new ConnectResult(Outcome.Unreachable,
                    DescribeMissingChallenge(challenge.Arrived, _challengeTimeout));
            }

            var nonce = challenge.Nonce;

            var signedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var token = OpenClawIdentity.TokenFor(_host);

            // Which token goes into the *signature* is not obvious and not
            // documented: the gateway's resolveSignatureToken takes
            // `auth.token ?? auth.deviceToken ?? auth.bootstrapToken`, so once a
            // gateway token is being sent it is the gateway token that gets
            // signed — not the device token, and not both. Signing the wrong one
            // fails as DEVICE_AUTH_SIGNATURE_INVALID, which reads like a broken
            // key rather than a field-ordering mistake.
            var signatureToken =
                !string.IsNullOrEmpty(_gatewayToken) ? _gatewayToken :
                !string.IsNullOrEmpty(token) ? token : "";

            var payload = OpenClawIdentity.AuthPayload(
                identity.DeviceId, ClientId, ClientMode, Role, Scopes,
                signedAt, signatureToken, nonce, Platform, "");

            var auth = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(_gatewayToken)) auth["token"] = _gatewayToken;
            if (!string.IsNullOrEmpty(token)) auth["deviceToken"] = token;

            var connect = new Dictionary<string, object?>
            {
                ["minProtocol"] = 4,
                ["maxProtocol"] = 4,
                ["role"] = Role,
                ["scopes"] = Scopes,
                ["client"] = new Dictionary<string, object>
                {
                    ["id"] = ClientId,
                    ["version"] = "0.3.0",
                    ["platform"] = Platform,
                    ["mode"] = ClientMode
                },
                ["auth"] = auth,
                ["device"] = new Dictionary<string, object>
                {
                    ["id"] = identity.DeviceId,
                    ["publicKey"] = OpenClawIdentity.Base64Url(identity.PublicKey),
                    ["signature"] = OpenClawIdentity.Sign(identity, payload),
                    ["signedAt"] = signedAt,
                    ["nonce"] = nonce
                }
            };

            JsonElement response;
            try
            {
                response = await RequestAsync("connect", connect, _cts.Token);
            }
            catch (OpenClawRequestException ex)
            {
                return Classify(ex);
            }
            catch (Exception ex)
            {
                // Unreachable, not rejected.
                //
                // This used to return AuthRejected for anything that wasn't a
                // structured error from the gateway, and AuthRejected is
                // terminal — the supervisor stops for good. But everything that
                // lands here is transport: the socket dropping mid-handshake,
                // the twenty-second request timeout, a disposed connection. A
                // gateway restarted in the window between the WebSocket upgrade
                // and its answer to `connect` would therefore say "refused these
                // credentials" and never try again for the life of the app.
                //
                // Only the gateway saying no is a reason to stop asking.
                //
                // `_cts` is what separates "we gave up" from "it never
                // answered": the request timeout lives on its own linked source
                // inside RequestAsync and does not cancel this one, so a
                // cancellation with `_cts` still live can only be the timeout.
                //
                // `!` rather than `?.` deliberately. It is assigned at the top
                // of this method, so the null arm of a `?.` cannot be reached —
                // and an unreachable arm is not free: it is a branch in the
                // coverage denominator with no possible numerator, which reads
                // for ever after as a tested method that isn't.
                return new ConnectResult(Outcome.Unreachable,
                    DescribeHandshakeFailure(
                        ex, _cts!.IsCancellationRequested, _requestTimeout));
            }

            if (response.TryGetProperty("protocol", out var protocol)) ServerVersion =
                response.TryGetProperty("server", out var server)
                && server.TryGetProperty("version", out var version)
                    ? version.GetString()
                    : protocol.ToString();

            if (response.TryGetProperty("auth", out var authOut)
                && authOut.TryGetProperty("scopes", out var scopes)
                && scopes.ValueKind == JsonValueKind.Array)
            {
                GrantedScopes = scopes.EnumerateArray()
                    .Select(s => s.GetString() ?? "")
                    .Where(s => s.Length > 0)
                    .ToArray();
            }

            // CB-278: the device's own operator token, which hello-ok has always
            // carried and this method used to read past. Kept because the HTTP
            // media fetches need a credential, and the only one they had was
            // the gateway's shared secret, which the gateway scores as the
            // CLI's default scopes, admin included. A device token is scored as
            // exactly this device's approved scopes. Measured on 2026.10.5:
            // pictures and clips fetch identically under it, and two admin
            // gates that pass the shared secret refuse it.
            //
            // Stored but never cleared on absence. A gateway that stops
            // issuing one, or rotates it, leaves the old one here; a media
            // fetch with a stale token is a 401, so the picture does not load,
            // and the next connect that carries a token replaces it. Clearing
            // on absence would instead quietly put the admin-scored secret
            // back on every fetch, which is the thing this exists to stop.
            //
            // Sending it back as `auth.deviceToken` on the next connect, which
            // the code above already does whenever one is held, was measured
            // as harmless: the connect result was byte-identical, and so was
            // the result with a deliberately wrong one, because the shared
            // secret is what that gateway authenticates the connect by.
            var issued = DeviceTokenIn(response);
            if (issued is not null) OpenClawIdentity.SetTokenFor(_host, issued);

            Methods = ParseFeatures(response);

            if (response.TryGetProperty("policy", out var policy))
            {
                if (policy.TryGetProperty("tickIntervalMs", out var tick) && tick.TryGetInt32(out var ms))
                    TickIntervalMs = ms;
                if (policy.TryGetProperty("maxPayload", out var max) && max.TryGetInt64(out var bytes))
                    MaxPayload = bytes;
            }

            // Connected with nothing granted is the shape the spike hit with a
            // gateway token: every read method then refuses individually, which
            // reads like a broken feature rather than an unfinished pairing.
            // Say so here instead.
            if (GrantedScopes.Count == 0)
            {
                return new ConnectResult(Outcome.PairingPending,
                    "connected, but no scopes granted — approve this device on the gateway");
            }

            return new ConnectResult(Outcome.Connected);
        }

        // `auth.deviceToken` out of a hello-ok, or null when there is no usable
        // one. Pure, so each way a gateway can leave it out is a case of its own
        // rather than a fake handshake per arm. Whitespace counts as absent,
        // the same rule OpenClawMediaCredential applies, so nothing stored here
        // is something the fetch would then refuse to send.
        internal static string? DeviceTokenIn(JsonElement response) =>
            response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("auth", out var auth)
            && auth.ValueKind == JsonValueKind.Object
            && auth.TryGetProperty("deviceToken", out var token)
            && token.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(token.GetString())
                ? token.GetString()
                : null;

        internal static ConnectResult Classify(OpenClawRequestException ex)
        {
            // The gateway's own detail codes, which are far more specific than
            // the message text. PAIRING_REQUIRED is the expected first-run
            // answer, not a failure — a human has to approve the device before
            // a retry can succeed.
            // Only codes that mean "these credentials are wrong" are terminal.
            // Anything else the gateway might invent — a rate limit, an internal
            // error — is worth retrying, and treating the unknown as fatal made
            // every future gateway version a potential permanent outage.
            var outcome = ex.DetailCode switch
            {
                "PAIRING_REQUIRED" or "DEVICE_IDENTITY_REQUIRED" => Outcome.PairingPending,

                "AUTH_UNAUTHORIZED" or "AUTH_TOKEN_MISSING" or "AUTH_TOKEN_MISMATCH"
                    or "AUTH_TOKEN_NOT_CONFIGURED" or "AUTH_PASSWORD_MISMATCH"
                    or "AUTH_DEVICE_TOKEN_MISMATCH" or "AUTH_SCOPE_MISMATCH"
                    or "DEVICE_AUTH_INVALID" or "DEVICE_AUTH_SIGNATURE_INVALID"
                    or "DEVICE_AUTH_PUBLIC_KEY_INVALID" or "DEVICE_AUTH_DEVICE_ID_MISMATCH"
                    or "PROTOCOL_MISMATCH" => Outcome.AuthRejected,

                _ => Outcome.Unreachable
            };

            return new ConnectResult(outcome, ex.DetailCode is null
                ? ex.Message
                : $"{ex.DetailCode}: {ex.Message}");
        }

        // What a person should be told to check when the socket never opened.
        //
        // The raw errno for this failure is "No route to host", and on a LAN
        // where the gateway is plainly up that sends everybody to look at the
        // network — which is the one thing that is fine. macOS ties Local
        // Network access to an app's *code identity*, exactly the way it ties
        // Automation consent, so installing an upgrade over an existing install
        // hands the new bundle a new CDHash and silently drops the grant. The
        // app then gets EHOSTUNREACH for a host it can see, with no prompt
        // anybody noticed and nothing on screen that points at consent. Claude
        // Buddy is a menu-bar app with no Dock icon and no window, which is
        // close to the worst case for a consent alert being seen at all.
        //
        // Diagnosing it from the outside is worse than it sounds, because every
        // obvious check agrees with the wrong answer: `ping`, `nc`, `curl` and
        // `ssh` are all Apple-signed and therefore exempt from the gate, so they
        // cheerfully report the gateway reachable while the app cannot open a
        // socket to it. See CB-38.
        //
        // The raw detail is kept and the hint appended rather than substituted.
        // EHOSTUNREACH really can also mean an unplugged cable or a host that
        // has gone away, and a message that flatly asserted the wrong one of
        // those would replace a confusing failure with a misleading one.
        internal const string LocalNetworkHint =
            "macOS may be blocking local network access — check "
            + "System Settings → Privacy & Security → Local Network";

        // `onMacOS` is a parameter rather than a call to OperatingSystem.IsMacOS()
        // inside, for the same reason OrbGlyph takes the two-letter setting as an
        // argument: a test that reads the answer off the machine it happens to be
        // running on can only be written for one CI leg, and this repo runs every
        // suite on both. Passing it in means the Windows arm is asserted on macOS
        // and the macOS arm is asserted on Windows.
        internal static string ExplainConnectFailure(Exception ex, bool onMacOS)
        {
            var flat = Flatten(ex);

            return onMacOS && IsHostUnreachable(ex)
                ? flat + " — " + LocalNetworkHint
                : flat;
        }

        // Matched on the socket error rather than on the message text. The text
        // is the platform's, not ours: it is localised on some systems and has
        // been reworded between .NET versions, and this is precisely the sort of
        // string-sniffing the certificate arm above already has to apologise for.
        // The chain is walked because TcpClient.ConnectAsync's SocketException
        // arrives at the top today but is wrapped the moment anything is layered
        // over the connect.
        internal static bool IsHostUnreachable(Exception ex)
        {
            for (Exception? e = ex; e is not null; e = e.InnerException)
            {
                if (e is SocketException { SocketErrorCode: SocketError.HostUnreachable })
                    return true;

                // An AggregateException hides its causes beside InnerException,
                // not under it, so the walk above would step straight past a
                // socket failure that arrived through one.
                //
                // Written as a nested loop rather than the `is ... && .Any(...)`
                // one-liner it wants to be, because the two coverage engines do
                // not agree on how many arms that compound condition has —
                // coverlet counts four, Microsoft.CodeCoverage six — and since
                // the MTP suites never execute this method at all, the extra two
                // arrive as denominator with no possible numerator. That reads
                // as a permanent 4/6 branch gap on a fully-tested method. The
                // split is the same logic with an honest number.
                if (e is AggregateException agg)
                {
                    foreach (var inner in agg.InnerExceptions)
                    {
                        if (IsHostUnreachable(inner)) return true;
                    }
                }
            }

            return false;
        }

        // Why the handshake never got a nonce, and why the two arms are worth
        // separating.
        //
        // Both used to report "no connect.challenge", which reads as "the
        // gateway said nothing" — so a gateway that *did* answer, with a frame
        // missing a field, was described as silent. Those want opposite
        // responses: one is a machine that is asleep and worth retrying, the
        // other is a protocol disagreement that retrying cannot fix. The
        // supervisor prints this detail verbatim, so the sentence is the whole
        // diagnosis a person gets.
        internal static string DescribeMissingChallenge(bool arrived, TimeSpan waited) =>
            arrived
                ? "the gateway sent connect.challenge with no nonce"
                : $"no connect.challenge within {DescribeWait(waited)}";

        // Why the `connect` request itself came back empty-handed.
        //
        // Everything that isn't a cancellation is already a real exception
        // chain and Flatten says what it was. A cancellation is the case worth
        // spelling out: it reaches the user as "A task was canceled.", which
        // names neither what was being waited for nor for how long, and it is
        // the *likeliest* failure of the three — a gateway that accepted the
        // socket and then went quiet is what a sleeping machine, a restarted
        // gateway and a starved client scheduler all look like from here.
        //
        // CB-23 is what this is for. A handshake that lapsed on the request
        // timeout reported "A task was canceled." and left GrantedScopes at its
        // empty default, so the failure surfaced downstream as an empty
        // collection with no indication that a timeout was involved at all —
        // reproduced locally by starving the thread pool, where the receive
        // loop's continuation is queued behind whatever else is blocked.
        // hello-ok's features.methods as a set. Anything missing or malformed
        // is the empty set, which offers nothing: a row that needs a method the
        // gateway never named should be absent, not tried.
        internal static IReadOnlySet<string> ParseFeatures(JsonElement helloOk)
        {
            var methods = new HashSet<string>(StringComparer.Ordinal);

            if (helloOk.ValueKind != JsonValueKind.Object
                || !helloOk.TryGetProperty("features", out var features)
                || features.ValueKind != JsonValueKind.Object
                || !features.TryGetProperty("methods", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return methods;
            }

            // GetString() on a String element is never null, so it is asserted
            // rather than tested: a null arm here could never be reached, and an
            // unreachable arm is a branch with no possible numerator.
            foreach (var method in list.EnumerateArray())
            {
                if (method.ValueKind != JsonValueKind.String) continue;

                var name = method.GetString()!;
                if (name.Length > 0) methods.Add(name);
            }

            return methods;
        }

        internal static string DescribeHandshakeFailure(
            Exception ex, bool abandoned, TimeSpan waited) =>
            ex is not OperationCanceledException
                ? Flatten(ex)
                : abandoned
                    ? "the connection attempt was abandoned before the gateway answered connect"
                    : "the gateway accepted the socket but did not answer connect within "
                      + DescribeWait(waited);

        // Timeout.InfiniteTimeSpan is minus one millisecond, which would print
        // as "-0.001s" and read as a bug in the clock rather than as a deadline
        // that was deliberately removed. The tests configure exactly that — see
        // OpenClawGatewayTests' Gateway helper — so the case is real even though
        // a wait that never expires cannot reach the two callers above.
        internal static string DescribeWait(TimeSpan waited) =>
            waited == Timeout.InfiniteTimeSpan ? "no limit" : $"{waited.TotalSeconds:0.###}s";

        internal static string Flatten(Exception ex)
        {
            var parts = new List<string>();
            for (Exception? e = ex; e is not null; e = e.InnerException)
            {
                if (!parts.Contains(e.Message)) parts.Add(e.Message);
            }

            return string.Join(" — ", parts);
        }

        // What the challenge wait came back with. `Arrived` is the field that
        // did not exist: a null nonce used to mean both "nothing came" and "a
        // challenge came without one", and the caller could not tell which it
        // was being asked to report. `default` is deliberately the silent case,
        // so the timeout registration below stays a one-liner.
        private readonly record struct Challenge(string? Nonce, bool Arrived);

        private async Task<Challenge> WaitForChallengeAsync(CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<Challenge>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnEvent(string name, JsonElement payload)
            {
                if (name != "connect.challenge") return;
                tcs.TrySetResult(new Challenge(
                    payload.TryGetProperty("nonce", out var n) ? n.GetString() : null,
                    Arrived: true));
            }

            EventReceived += OnEvent;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_challengeTimeout);
                using (timeout.Token.Register(() => tcs.TrySetResult(default)))
                {
                    return await tcs.Task;
                }
            }
            finally
            {
                EventReceived -= OnEvent;
            }
        }

        public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _nextId).ToString();

            // RunContinuationsAsynchronously matters more than it looks: without
            // it every awaiting continuation runs inline on the receive loop, so
            // one slow consumer stops the socket being read at all.
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            var frame = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
            {
                ["type"] = "req",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new Dictionary<string, object>()
            });

            try
            {
                await _sendGate.WaitAsync(ct);
                try
                {
                    await _socket!.SendAsync(frame, WebSocketMessageType.Text, true, ct);
                }
                finally
                {
                    _sendGate.Release();
                }
            }
            catch
            {
                // A request that never left leaves nothing behind. Without this
                // its slot stayed in the table until the socket died and
                // FailPending swept it — harmless, but it meant the table was
                // not the list of things actually in flight, which is the only
                // thing it is for.
                _pending.TryRemove(id, out _);
                throw;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_requestTimeout);

            try
            {
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    return await tcs.Task;
                }
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        // CB-68: recorded the moment the loop actually starts running, on
        // whatever thread it got. A test seam rather than a debugging
        // leftover — it is the only way to assert the fix above from outside
        // the class, since "did this run on a pooled thread" has no other
        // externally observable trace once the loop is past its first line.
        internal bool? ReceiveLoopStartedOnAPooledThread { get; private set; }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            ReceiveLoopStartedOnAPooledThread = Thread.CurrentThread.IsThreadPoolThread;

            var buffer = new byte[16 * 1024];
            var message = new ArrayBufferWriter<byte>();

            try
            {
                while (!ct.IsCancellationRequested && _socket!.State == WebSocketState.Open)
                {
                    var result = await _socket.ReceiveAsync(buffer, ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // The close frame's description is the only place the
                        // gateway explains a rejected connect — it answers, then
                        // closes with 1008 and the reason as text. Dropping it
                        // and reporting "connection closed" would throw away the
                        // entire diagnosis.
                        _closeReason = string.IsNullOrEmpty(result.CloseStatusDescription)
                            ? result.CloseStatus?.ToString()
                            : result.CloseStatusDescription;
                        break;
                    }

                    message.Write(buffer.AsSpan(0, result.Count));

                    // Bounded by the server's own advertised limit rather than a
                    // number of ours: a frame larger than it accepts is a frame
                    // it never sent.
                    if (message.WrittenCount > MaxPayload) break;

                    if (!result.EndOfMessage) continue;

                    try
                    {
                        Dispatch(message.WrittenMemory);
                    }
                    catch (Exception ex)
                    {
                        // One frame the parser didn't expect — an `ok` that
                        // isn't a boolean, a subscriber that threw — used to
                        // take the whole loop down and cost a full TLS 1.3
                        // reconnect. A frame we can't read is a frame we skip.
                        Console.Error.WriteLine($"{Brand.DisplayName}: bad gateway frame: {ex.Message}");
                    }

                    message.Clear();
                }
            }
            catch (OperationCanceledException)
            {
                // Ordinary teardown.
            }
            catch (Exception ex)
            {
                FailPending(ex);
                return;
            }

            FailPending(new IOException(_closeReason is null
                ? "gateway connection closed"
                : $"gateway closed the connection: {_closeReason}"));
        }

        private void Dispatch(ReadOnlyMemory<byte> frame)
        {
            JsonElement root;
            try
            {
                root = JsonDocument.Parse(frame).RootElement.Clone();
            }
            catch
            {
                return;   // not JSON; nothing sensible to do with it
            }

            if (root.TryGetProperty("event", out var name))
            {
                root.TryGetProperty("payload", out var payload);
                EventReceived?.Invoke(name.GetString() ?? "", payload);
                return;
            }

            if (!root.TryGetProperty("id", out var idElement)) return;

            var id = idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : idElement.ToString();

            if (id is null || !_pending.TryRemove(id, out var tcs)) return;

            var ok = root.TryGetProperty("ok", out var okElement)
                && okElement.ValueKind == JsonValueKind.True;
            if (ok)
            {
                tcs.TrySetResult(root.TryGetProperty("payload", out var payload) ? payload : root);
                return;
            }

            string? code = null, detail = null, message = null;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("code", out var c)) code = c.GetString();
                if (error.TryGetProperty("message", out var m)) message = m.GetString();
                if (error.TryGetProperty("details", out var d) && d.TryGetProperty("code", out var dc))
                    detail = dc.GetString();
            }

            tcs.TrySetException(new OpenClawRequestException(message ?? code ?? "request failed", detail)
            {
                Code = code
            });
        }

        // A dead socket must not leave callers waiting out their own timeouts
        // one by one — they all failed for the same reason at the same moment.
        private void FailPending(Exception ex)
        {
            foreach (var id in _pending.Keys.ToList())
            {
                if (_pending.TryRemove(id, out var tcs)) tcs.TrySetException(ex);
            }
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _socket?.Abort(); } catch { }
            try { _socket?.Dispose(); } catch { }
            try { _transport?.Dispose(); } catch { }
            _cts?.Dispose();
            _sendGate.Dispose();
        }
    }

    internal sealed class OpenClawRequestException : Exception
    {
        public OpenClawRequestException(string message, string? detailCode) : base(message)
        {
            DetailCode = detailCode;
        }

        // The gateway's structured detail code (PAIRING_REQUIRED,
        // DEVICE_AUTH_SIGNATURE_INVALID, …). Far more useful than the prose,
        // and the only way to tell "approve this device" from "your signature
        // is wrong" without matching on message text.
        public string? DetailCode { get; }

        // The error's own top-level code (INVALID_REQUEST, UNAVAILABLE, …),
        // beside the detail code rather than instead of it. The gateway marks a
        // transient refusal UNAVAILABLE — an archive asked for while the run it
        // just aborted is still settling is the case CB-170 needed — and that is
        // the one thing that separates "try again" from "no" without reading
        // the message.
        public string? Code { get; init; }
    }
}
