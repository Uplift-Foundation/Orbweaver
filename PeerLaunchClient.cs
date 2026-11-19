using System.Security.Cryptography;
using System.Text.Json;

namespace Orbweaver
{
    // The asking side of CB-357: sends `launch-info-get` and `launch` to a
    // paired desktop and waits for the reply that carries the same id.
    //
    // Three rules the window leans on:
    //   * **A reply is believed only from the machine it was asked of.** Ids
    //     are eight hex characters and travel in the clear on a link that
    //     carries several peers, so a second paired machine must not be able to
    //     answer for the first.
    //   * **Every failure is a value, never an exception**: no answer, a
    //     dropped link, a refusal. The window shows each in a reserved line.
    //   * **A dropped link fails every request pending on it at once**, rather
    //     than letting each run out its own clock.
    internal sealed class PeerLaunchClient : IPeerLauncher
    {
        internal static readonly TimeSpan InfoTimeout = TimeSpan.FromSeconds(5);
        // The receiver waits up to 30 s for the new session to check in.
        internal static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(45);

        // The launch is valid for this long. Inside the gate's 300 s ceiling,
        // and wide enough to tolerate clock skew between two desks.
        internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(120);

        // Signs a launch for one paired machine: base64url signature and the
        // sender's certificate as base64 DER, or null if this desk cannot sign
        // (no identity, no pairing). Supplied by PeerSessions.
        internal delegate Task<(string Sig, string Cert)?> Signer(
            string machine, PeerLaunchWire.LaunchRequest unsigned);

        internal sealed record Seams(
            Func<string, PeerProtocol.PeerMessage, Task<bool>> Send,
            Func<IReadOnlyList<PeerMachine>> Machines,
            Signer Sign,
            Func<DateTimeOffset> Now)
        {
            internal TimeSpan InfoWait { get; init; } = InfoTimeout;
            internal TimeSpan LaunchWait { get; init; } = LaunchTimeout;
            internal Func<string> NewNonce { get; init; } = RandomNonce;
        }

        private sealed record Pending(string Machine, TaskCompletionSource<PeerProtocol.PeerMessage?> Reply);

        private readonly Seams _seams;
        private readonly object _gate = new();
        private readonly Dictionary<string, Pending> _pending = new();

        internal PeerLaunchClient(Seams seams) => _seams = seams;

        public IReadOnlyList<PeerMachine> Machines() => _seams.Machines();

        public async Task<PeerLaunchInfo?> InfoAsync(string machine, CancellationToken ct)
        {
            var reply = await AskAsync(
                machine, PeerProtocol.LaunchInfoGet, null, PeerProtocol.LaunchInfo, _seams.InfoWait, ct)
                .ConfigureAwait(false);

            return reply?.Body is { } body ? PeerLaunchWire.ParseInfo(body) : null;
        }

        public async Task<PeerLaunchResult> LaunchAsync(
            string machine, PeerLaunchSpec spec, CancellationToken ct)
        {
            var unsigned = new PeerLaunchWire.LaunchRequest(
                spec.Cwd, spec.Cli, "", "", spec.Account, "", "",
                _seams.NewNonce(),
                _seams.Now().Add(Lifetime).ToUnixTimeSeconds(),
                "", "");

            (string Sig, string Cert)? signed;

            try { signed = await _seams.Sign(machine, unsigned).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // The real signer throws TimeoutException while the
                // certificate is still loading; that is a refusal to show,
                // not a crash in the window.
                return new PeerLaunchResult(false, PeerLaunchCodes.CannotSign, null, e.GetType().Name);
            }

            // Nothing was sent, so say so: this is the sender's own refusal.
            if (signed is not { } s)
                return new PeerLaunchResult(false, PeerLaunchCodes.CannotSign, null,
                    "no pairing for that machine");

            var body = PeerLaunchWire.EncodeLaunch(unsigned with { Sig = s.Sig, Cert = s.Cert });

            var reply = await AskAsync(
                machine, PeerProtocol.Launch, body, PeerProtocol.LaunchResult, _seams.LaunchWait, ct)
                .ConfigureAwait(false);

            if (reply is null)
            {
                bool lost;
                lock (_gate) lost = _lost.Contains(machine);
                return new PeerLaunchResult(false, lost ? PeerLaunchCodes.LinkLost : PeerLaunchCodes.NoAnswer, null, null);
            }

            var parsed = reply.Body is { } b ? PeerLaunchWire.ParseResult(b) : null;

            return parsed ?? new PeerLaunchResult(false, PeerLaunchCodes.NoAnswer, null, "unreadable reply");
        }

        // Fed by PeerMirrorHost for `launch-info` and `launch-result`.
        internal void OnReply(string machine, PeerProtocol.PeerMessage message)
        {
            Pending? pending;

            lock (_gate)
            {
                if (!_pending.TryGetValue(message.Id, out pending)
                    || !string.Equals(pending.Machine, machine, StringComparison.OrdinalIgnoreCase))
                    return;

                _pending.Remove(message.Id);
            }

            pending.Reply.TrySetResult(message);
        }

        // Fed by PeerLink.PeerDisconnected.
        internal void OnDisconnected(string machine)
        {
            List<Pending> dropped;

            lock (_gate)
            {
                var ids = _pending.Where(p => string.Equals(p.Value.Machine, machine, StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.Key).ToList();
                dropped = ids.Select(id => _pending[id]).ToList();
                foreach (var id in ids) _pending.Remove(id);
                _lost.Add(machine);
            }

            foreach (var p in dropped) p.Reply.TrySetResult(null);
        }

        // Machines whose link dropped under a request, so a null reply can say
        // "link lost" rather than "no answer". Cleared when the next request to
        // that machine starts.
        private readonly HashSet<string> _lost = new(StringComparer.OrdinalIgnoreCase);

        private async Task<PeerProtocol.PeerMessage?> AskAsync(
            string machine, string type, JsonElement? body, string replyType,
            TimeSpan wait, CancellationToken ct)
        {
            var id = PeerProtocol.NewId();
            var tcs = new TaskCompletionSource<PeerProtocol.PeerMessage?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_gate)
            {
                _lost.Remove(machine);
                _pending[id] = new Pending(machine, tcs);
            }

            try
            {
                var sent = await _seams.Send(machine, PeerProtocol.Message(type, id, body: body))
                    .ConfigureAwait(false);

                if (!sent)
                {
                    lock (_gate) _lost.Add(machine);
                    return null;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(wait);

                try
                {
                    return await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false) is { } m
                        && m.Type == replyType ? m : null;
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
            finally
            {
                lock (_gate) _pending.Remove(id);
            }
        }

        // The real signer: binds the launch to the receiving desk's pin, signs
        // the gate's own canonical body with the peer certificate's key, and
        // hands back the signature (base64url) and our certificate (base64
        // DER). Null when this desk has no pairing for that machine, because
        // a signature for a pin it does not know would only be refused.
        //
        // Both lookups are seams so the signature can be checked against
        // LaunchGate's own verifier with a certificate generated in the test.
        internal static Signer SignerFor(
            Func<string, string?> pinFor, Func<byte[], Task<PeerIdentity.Signature>> sign) =>
            async (machine, request) =>
            {
                if (pinFor(machine) is not { Length: > 0 } receiverPin) return null;

                var fields = new LaunchFields(
                    request.Cwd, request.Cli, request.Persona, request.Model, request.ParentSessionId,
                    request.FirstMessage, request.Nonce, request.ExpiresAt, request.Account);

                var signature = await sign(LaunchGate.PeerMessage(receiverPin, LaunchGate.Body(fields)))
                    .ConfigureAwait(false);

                return (RelayCrypto.Base64Url(signature.Sig), Convert.ToBase64String(signature.CertificateDer));
            };

        private static string RandomNonce() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(AnswerGate.NonceLength))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
