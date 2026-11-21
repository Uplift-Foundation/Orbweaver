using System.Text.Json;

namespace Orbweaver
{
    // CB-357: the receiving desk's answer to another desktop's `launch`.
    //
    // This is where a process gets started on this machine on another
    // machine's say-so, so the order of the checks is the design:
    //
    //   1. the asker is a paired, connected peer — PeerMirrorHost.MayAsk,
    //      before this is ever called, and a stranger is dropped silently;
    //   2. this desk's switch, "Allow other desktops to start sessions here".
    //      First, before any parsing or signature work, and nothing is
    //      recorded — so a desk that has not opted in spends nothing on a
    //      request and remembers nothing about it;            peer_launch_off
    //   3. the body is a whole request                           bad_frame
    //   4. LaunchGate with the peer as signer: the certificate is the one
    //      paired for this machine name, the signature is over this desk's
    //      own pin, then expiry, nonce, cli, cwd and the rest   (the gate's)
    //   5. no first message: the desktop window sends none, so one arriving
    //      is not something a person asked for                 bad_input
    //   6. LaunchRunner: account, start, check-in               (the runner's)
    //
    // Pure apart from its seams, so each step is a unit case.
    internal sealed class PeerLaunchServer
    {
        internal sealed record Seams(
            Func<bool> Enabled,
            Func<string, PeerIdentity.Peer?> KnownPeer,
            Func<string> OwnPin,
            Func<IReadOnlyList<(string SessionId, SessionStatus Status)>> Snapshot,
            RelayRemote.LaunchSeams Launch,
            RelayRemote.AccountSeams Accounts,
            Func<DateTimeOffset> Now,
            Func<TimeSpan, Task> Delay,
            Action<string> Log)
        {
            // CB-397: what the 120 s question lifetime waits on. Defaults to
            // Delay; a suite supplies one it can release.
            internal Func<TimeSpan, Task>? TrustDelay { get; init; }
        }

        // How long the far desk holds a trust question open.
        internal static readonly TimeSpan TrustLifetime = TimeSpan.FromSeconds(120);

        internal const string FirstMessageWhy = "A launch from another desktop carries no first message.";

        private readonly Seams _seams;
        private readonly LaunchRunner _runner;

        // Peers' own window. Phone nonces live in the relay's, under keys a
        // peer's ("peer:<pin>") can never equal, so the two never meet.
        private readonly NonceWindow _nonces = new();

        internal PeerLaunchServer(Seams seams)
        {
            _seams = seams;
            _runner = new LaunchRunner(new LaunchRunner.Seams(seams.Launch, seams.Accounts, seams.Snapshot, seams.Now, seams.Delay));
        }

        internal int NoncesSeen => _nonces.Count;

        // The shape PeerMirrorHost.ServeLaunch takes. `machine` is the name the
        // link gave the connection, which is what this desk paired under.
        internal async Task<PeerLaunchResult> HandleAsync(string machine, JsonElement body)
        {
            if (!_seams.Enabled()) return Refuse(machine, PeerLaunchCodes.PeerLaunchOff);

            if (PeerLaunchWire.ParseLaunch(body) is not { } request || CertificateOf(request) is not { } certDer)
                return Refuse(machine, RelayReplies.BadFrame);

            var fields = new LaunchFields(request.Cwd, request.Cli, request.Persona, request.Model,
                request.ParentSessionId, request.FirstMessage, request.Nonce, request.ExpiresAt, request.Account);
            var signer = new LaunchGate.PeerSigner(_seams.KnownPeer(machine)?.Pin ?? "", _seams.OwnPin(), certDer);

            if (LaunchGate.Decide(new LaunchRequest("peer", fields, request.Sig), signer, _seams.Now(), _nonces,
                    _runner.Environment()) is { } refused)
                return Refuse(machine, refused);

            // Refused on purpose, not a missing feature: do not "fix" this by
            // wiring up RelayRemote's typing path. The desktop window never
            // sends a first message, so one arriving is not something a person
            // asked for; Codex and Grok would receive it in argv, and a Claude
            // one would be dropped silently because only the phone path types
            // it. After the gate, so the nonce is spent: a refused launch is
            // still a replay. (CB-357, accepted by the PM and the architect.)
            if (fields.FirstMessage.Length > 0) return Refuse(machine, RelayReplies.BadInput, FirstMessageWhy);

            var outcome = await _runner.RunAsync(fields, request.CanTrust ? LaunchRunner.TrustPolicy.Ask : LaunchRunner.TrustPolicy.Close).ConfigureAwait(false);
            if (outcome is { Code: PeerLaunchCodes.TrustNeeded, Held: { } held }) return Hold(machine, fields, held);
            if (outcome.SessionId is not { } sessionId) return Refuse(machine, outcome.Code, outcome.Why);

            // The machine name and the code only, never the folder: the mirror
            // log is something people paste into bug reports.
            _seams.Log($"peer-launch {machine} {PeerLaunchCodes.Launched}");
            return new PeerLaunchResult(true, PeerLaunchCodes.Launched, sessionId, null);
        }

        // --- the trust question (CB-397) -------------------------------------

        private sealed record Pending(string Token, string Machine, LaunchFields Fields, LaunchRunner.HeldTrust Held, long ExpiresAt);

        // In memory, keyed by token, one per machine. A far desk that quits
        // while a question is pending forgets it and the pane stays: a named gap.
        private readonly Dictionary<string, Pending> _pending = new();
        private readonly object _gate = new();

        internal int PendingCount { get { lock (_gate) return _pending.Count; } }

        private PeerLaunchResult Hold(string machine, LaunchFields fields, LaunchRunner.HeldTrust held)
        {
            var token = RelayCrypto.Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            var expires = _seams.Now().Add(TrustLifetime).ToUnixTimeSeconds();
            var pending = new Pending(token, machine, fields, held, expires);
            List<Pending> older;
            lock (_gate)
            {
                older = _pending.Values.Where(p => string.Equals(p.Machine, machine, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var o in older) _pending.Remove(o.Token);
                _pending[token] = pending;
            }
            foreach (var o in older) _ = CloseAsync(o, "replaced");
            _ = ExpireAsync(pending);
            _seams.Log($"peer-launch {machine} {PeerLaunchCodes.TrustNeeded} pane={held.Pane}");
            return new PeerLaunchResult(false, PeerLaunchCodes.TrustNeeded, null, null,
                new PeerLaunchTrust(token, fields.Cli, fields.Cwd, expires));
        }

        private async Task ExpireAsync(Pending pending)
        {
            await (_seams.TrustDelay ?? _seams.Delay)(TrustLifetime).ConfigureAwait(false);
            if (Take(pending.Token) is { } mine && ReferenceEquals(mine, pending)) await CloseAsync(pending, "expired").ConfigureAwait(false);
        }

        private Pending? Take(string token)
        {
            lock (_gate) return _pending.Remove(token, out var p) ? p : null;
        }

        private async Task CloseAsync(Pending p, string why)
        {
            await _runner.CloseIfStillAskingAsync(p.Fields.Cwd, LaunchGate.CliFor(p.Fields.Cli), p.Held.Pane).ConfigureAwait(false);
            _seams.Log($"peer-launch {p.Machine} trust_{why} pane={p.Held.Pane}");
        }

        // A dropped link closes that machine's question under the same guard.
        internal async Task OnDisconnectedAsync(string machine)
        {
            List<Pending> mine;
            lock (_gate)
            {
                mine = _pending.Values.Where(p => string.Equals(p.Machine, machine, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var p in mine) _pending.Remove(p.Token);
            }
            foreach (var p in mine) await CloseAsync(p, "dropped").ConfigureAwait(false);
        }

        // The answer, checked in this order: switch, whole frame, a question
        // this machine owns, the signature, expiry, replay — then the record is
        // taken, single use, and only then acted on.
        internal async Task<PeerLaunchResult> HandleTrustAsync(string machine, JsonElement body)
        {
            if (!_seams.Enabled()) return Refuse(machine, PeerLaunchCodes.PeerLaunchOff);

            if (PeerLaunchWire.ParseTrust(body) is not { } request || CertificateOf(request.Cert) is not { } certDer)
                return Refuse(machine, RelayReplies.BadFrame);

            Pending? pending;
            lock (_gate)
                pending = _pending.GetValueOrDefault(request.Token) is { } p
                    && string.Equals(p.Machine, machine, StringComparison.OrdinalIgnoreCase) ? p : null;
            if (pending is null) return Refuse(machine, PeerLaunchCodes.TrustGone);

            var pin = _seams.KnownPeer(machine)?.Pin ?? "";
            var fields = new PeerTrustAnswer.Fields(request.Token, request.Answer, pending.Fields.Cwd, pending.Fields.Cli,
                pending.Fields.Account, pending.Fields.Nonce, request.Nonce, request.ExpiresAt);
            if (!PeerTrustAnswer.Verify(pin, _seams.OwnPin(), certDer, fields, request.Sig))
                return Refuse(machine, RelayReplies.BadSignature);

            var now = _seams.Now();
            var secs = now.ToUnixTimeSeconds();
            if (secs >= request.ExpiresAt || request.ExpiresAt > secs + LaunchGate.TtlSlackSeconds)
                return Refuse(machine, RelayReplies.Expired);

            if (!_nonces.TryAdd(LaunchGate.PeerNoncePrefix + pin, request.Nonce, now)) return Refuse(machine, RelayReplies.Replayed);

            if (Take(request.Token) is not { } taken || !ReferenceEquals(taken, pending)) return Refuse(machine, PeerLaunchCodes.TrustGone);

            if (request.Answer == PeerTrustAnswer.Cancel)
            {
                await CloseAsync(taken, "cancelled").ConfigureAwait(false);
                return Refuse(machine, PeerLaunchCodes.TrustCancelled);
            }

            var outcome = await _runner.AnswerTrustAsync(taken.Fields, taken.Held).ConfigureAwait(false);
            if (outcome.SessionId is not { } sessionId) return Refuse(machine, outcome.Code, outcome.Why);
            _seams.Log($"peer-launch {machine} {PeerLaunchCodes.Launched}");
            return new PeerLaunchResult(true, PeerLaunchCodes.Launched, sessionId, null);
        }

        private static byte[]? CertificateOf(PeerLaunchWire.LaunchRequest request) => CertificateOf(request.Cert);

        private static byte[]? CertificateOf(string cert)
        {
            try { return Convert.FromBase64String(cert); }
            catch (FormatException) { return null; }
        }

        private PeerLaunchResult Refuse(string machine, string code, string? why = null)
        {
            _seams.Log($"peer-launch {machine} {code}");
            return new PeerLaunchResult(false, code, null, why);
        }

        // The real desk: the setting as it is on each request, the pinned
        // peers and this desk's own pin from PeerIdentity, and the same launch
        // and account seams the phone path uses. Excluded: each is one line
        // into the settings file, the identity file or a real process.
        //
        // Wire Shared, not Create(): one server per process, so a peer link
        // restart (PeerSessions.Restart) cannot hand a replayed nonce a fresh
        // window.
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static PeerLaunchServer Shared => SharedServer.Value;

        private static readonly Lazy<PeerLaunchServer> SharedServer = new(Create);

        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        private static PeerLaunchServer Create() => new(RealSeams());

        // Internal so PeerLaunchRealSnapshotTests can run a launch through the
        // real session source with only the process start faked: the one
        // seam whose absence a fake snapshot would hide.
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static Seams RealSeams() => new(
            () => OrbweaverSettings.PeerLaunchInboundEnabled,
            PeerIdentity.PeerFor,
            PeerIdentity.OwnPin,
            // Pulled, not pushed, so there is no subscription for the wiring to
            // forget: an unfed snapshot would start the CLI and then tell the
            // sender no_session after 30 s. Read before relying on it: each call
            // is a fresh SessionManager.HeadlessSnapshot of the status directory
            // (memoized for 2 s, independent of scans and of roster requests),
            // and a new session is in it as soon as its hook has written a
            // status file and its process is alive — Cwd and Source come from
            // that file, which is all the check-in compares. The same provider
            // the peer link already serves its roster from.
            RemoteControlSessions.LocalSessions,
            LaunchRunner.DefaultLaunchSeams(),
            new RelayRemote.AccountSeams(RelayAccounts.Choices, _ => null),
            () => DateTimeOffset.UtcNow,
            Task.Delay,
            line => MirrorLog.Say("peer-launch", line));
    }
}
