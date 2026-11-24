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
    internal sealed class PeerLaunchClient : IPeerLauncher, IRemotePastSessions
    {
        internal static readonly TimeSpan InfoTimeout = TimeSpan.FromSeconds(5);
        // The far desk gives up reading a folder after 8 s and says so; this is
        // a little longer so its answer wins over our own clock.
        internal static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(10);
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

        // CB-397: signs a trust answer for one paired machine; null when this
        // desk cannot (no identity, no pairing).
        internal delegate Task<(string Sig, string Cert)?> TrustSigner(
            string machine, PeerTrustAnswer.Fields fields);

        internal sealed record Seams(
            Func<string, PeerProtocol.PeerMessage, Task<bool>> Send,
            Func<IReadOnlyList<PeerMachine>> Machines,
            Signer Sign,
            Func<DateTimeOffset> Now)
        {
            internal TimeSpan InfoWait { get; init; } = InfoTimeout;
            internal TimeSpan LaunchWait { get; init; } = LaunchTimeout;
            internal TimeSpan ListWait { get; init; } = ListTimeout;
            internal Func<string> NewNonce { get; init; } = RandomNonce;
            internal TrustSigner? TrustSign { get; init; }
        }

        private sealed record Pending(string Machine, TaskCompletionSource<PeerProtocol.PeerMessage?> Reply);

        private readonly Seams _seams;
        private readonly object _gate = new();
        private readonly Dictionary<string, Pending> _pending = new();

        // CB-397: the launch behind each question this desk was asked, so the
        // answer is signed over the values this desk actually sent.
        private sealed record Asked(string Machine, string Cwd, string Cli, string Account, string LaunchNonce, long ExpiresAt);
        private readonly Dictionary<string, Asked> _asked = new();

        internal PeerLaunchClient(Seams seams) => _seams = seams;

        public IReadOnlyList<PeerMachine> Machines() => _seams.Machines();

        public async Task<PeerLaunchInfo?> InfoAsync(string machine, CancellationToken ct)
        {
            var reply = await AskAsync(
                machine, PeerProtocol.LaunchInfoGet, null, PeerProtocol.LaunchInfo, _seams.InfoWait, ct)
                .ConfigureAwait(false);

            return reply?.Body is { } body ? PeerLaunchWire.ParseInfo(body) : null;
        }

        // A question the far desk has given up is dropped here, on the next
        // launch or answer: nothing else would ever remove it, and a desktop
        // app runs for weeks.
        private void SweepExpired()
        {
            var now = _seams.Now().ToUnixTimeSeconds();
            lock (_gate)
                foreach (var token in _asked.Where(a => a.Value.ExpiresAt <= now).Select(a => a.Key).ToList())
                    _asked.Remove(token);
        }

        public async Task<PeerLaunchResult> LaunchAsync(
            string machine, PeerLaunchSpec spec, CancellationToken ct)
        {
            SweepExpired();
            var unsigned = new PeerLaunchWire.LaunchRequest(
                spec.Cwd, spec.Cli, "", "", spec.Account, "", "",
                _seams.NewNonce(),
                _seams.Now().Add(Lifetime).ToUnixTimeSeconds(),
                "", "",
                // A resume cannot show the trust question (the Resume seam has
                // no round trip for it), so the far desk closes such a pane and
                // says why rather than holding it for two minutes.
                CanTrust: spec.ResumeSessionId.Length == 0,
                ResumeSessionId: spec.ResumeSessionId.Length > 0 ? spec.ResumeSessionId : null);

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

            var result = await AskForResultAsync(machine, PeerProtocol.Launch, body, ct).ConfigureAwait(false);

            if (result.Code != PeerLaunchCodes.TrustNeeded) return result;

            // The question is believed only for the folder and CLI this desk
            // asked for; anything else is not an answer to this launch.
            if (result.Trust is not { } trust || trust.Cwd != spec.Cwd || trust.Cli != spec.Cli)
                return new PeerLaunchResult(false, PeerLaunchCodes.NoAnswer, null, "unexpected trust question");

            lock (_gate) _asked[trust.Token] = new Asked(machine, spec.Cwd, spec.Cli, spec.Account, unsigned.Nonce, trust.ExpiresAt);
            return result;
        }

        public async Task<PeerLaunchResult> TrustAsync(
            string machine, PeerLaunchTrust trust, bool trusted, CancellationToken ct)
        {
            SweepExpired();
            Asked? asked;
            lock (_gate)
                asked = _asked.GetValueOrDefault(trust.Token) is { } a
                    && string.Equals(a.Machine, machine, StringComparison.OrdinalIgnoreCase) ? a : null;

            // Never a question this desk was asked: nothing is sent.
            if (asked is null) return new PeerLaunchResult(false, PeerLaunchCodes.TrustGone, null, null);

            var fields = new PeerTrustAnswer.Fields(
                trust.Token, trusted ? PeerTrustAnswer.Trust : PeerTrustAnswer.Cancel,
                asked.Cwd, asked.Cli, asked.Account, asked.LaunchNonce,
                _seams.NewNonce(), _seams.Now().Add(Lifetime).ToUnixTimeSeconds());

            (string Sig, string Cert)? signed;
            try { signed = _seams.TrustSign is { } sign ? await sign(machine, fields).ConfigureAwait(false) : null; }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return new PeerLaunchResult(false, PeerLaunchCodes.CannotSign, null, e.GetType().Name);
            }

            if (signed is not { } s)
                return new PeerLaunchResult(false, PeerLaunchCodes.CannotSign, null, "no pairing for that machine");

            lock (_gate) _asked.Remove(trust.Token);

            var body = PeerLaunchWire.EncodeTrust(new PeerLaunchWire.TrustRequest(
                fields.Token, fields.Answer, fields.Nonce, fields.ExpiresAt, s.Sig, s.Cert));
            return await AskForResultAsync(machine, PeerProtocol.LaunchTrust, body, ct).ConfigureAwait(false);
        }

        private async Task<PeerLaunchResult> AskForResultAsync(
            string machine, string type, JsonElement body, CancellationToken ct)
        {
            var reply = await AskAsync(
                machine, type, body, PeerProtocol.LaunchResult, _seams.LaunchWait, ct)
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

        // ---- CB-434: IRemotePastSessions --------------------------------------------

        // How many rows a window asks for (contract amendment 1).
        internal const int WindowLimit = 2000;

        public async Task<RemoteListResult> ListAsync(string machine, NewChatCli cli, string profile, bool showOlder, CancellationToken ct)
        {
            var wire = RelayAccounts.CliName(cli);
            var page = await PastSessionsAsync(machine, new PastSessionQuery(wire, profile, showOlder, "", "", 0, WindowLimit), ct)
                .ConfigureAwait(false);

            if (!page.Ok)
                return new RemoteListResult(RemoteListOutcome.Unreachable, [], ListWhy(page.Code));

            var rows = page.Rows.Select(r => new PastSessionRow(
                new PastSession(cli, r.Id, r.Cwd, r.Title, DateTimeOffset.FromUnixTimeSeconds(r.At), r.Count, "", r.Missing),
                r.Live)).ToList();
            return new RemoteListResult(RemoteListOutcome.Ok, rows, null, page.Total);
        }

        // A short reason for the window's "Couldn't reach <machine> for its
        // sessions (…)"; null where the sentence needs none.
        internal static string? ListWhy(string code) => code switch
        {
            PeerLaunchCodes.NoAnswer => null,
            PeerLaunchCodes.LinkLost => "the link dropped",
            PeerLaunchCodes.PeerLaunchOff => "it doesn't allow other desktops to start sessions",
            RelayReplies.UnknownAccount => "it has no account by that name",
            RelayReplies.UnknownCli => "it doesn't have that CLI",
            RelayReplies.Unreadable => "it couldn't read its session history",
            _ => code,
        };

        public async Task<RemoteResumeResult> ResumeAsync(
            string machine, NewChatCli cli, string sessionId, string cwd, string profile, CancellationToken ct)
        {
            var wire = RelayAccounts.CliName(cli);
            var result = await LaunchAsync(machine, new PeerLaunchSpec(wire, cwd, profile) { ResumeSessionId = sessionId }, ct)
                .ConfigureAwait(false);
            return Resumed(result, cli, machine);
        }

        // The wire's answer as the window's outcome. resume_started is Codex's
        // launched-without-a-watch; already_running carries the id to focus.
        internal static RemoteResumeResult Resumed(PeerLaunchResult r, NewChatCli cli, string machine)
        {
            var name = RelayAccounts.CliName(cli);
            return r.Code switch
            {
                PeerLaunchCodes.Launched when r.Ok => new(RemoteResumeOutcome.Launched, null, false, r.SessionId),
                PeerLaunchCodes.ResumeStarted when r.Ok => new(RemoteResumeOutcome.Launched, null, true, r.SessionId),
                RelayReplies.AlreadyRunning when r.Ok => new(RemoteResumeOutcome.AlreadyRunning, null, false, r.SessionId),
                RelayReplies.ResumeUnsupported => new(RemoteResumeOutcome.Unsupported, r.Why, false),
                RelayReplies.UnknownResume => new(RemoteResumeOutcome.Failed,
                    machine + " no longer has that session in that folder.", false),
                PeerLaunchCodes.NoAnswer => new(RemoteResumeOutcome.Unreachable, null, false),
                PeerLaunchCodes.LinkLost => new(RemoteResumeOutcome.Unreachable, "the link dropped", false),
                _ => new(RemoteResumeOutcome.Failed, PeerLaunchCopy.Explain(r.Code, r.Why, name, machine), false),
            };
        }

        // CB-434: one page of the far desk's past sessions, on the list-dir
        // timeout. Every failure is a result with a code, as ListDirAsync's are.
        public async Task<PeerPastSessionsResult> PastSessionsAsync(string machine, PastSessionQuery query, CancellationToken ct)
        {
            var reply = await AskAsync(
                machine, PeerProtocol.PastSessionsGet, PastSessionsWire.EncodeQuery(query),
                PeerProtocol.PastSessions, _seams.ListWait, ct).ConfigureAwait(false);

            if (reply is null)
            {
                bool lost;
                lock (_gate) lost = _lost.Contains(machine);
                return PeerPastSessionsResult.Fail(lost ? PeerLaunchCodes.LinkLost : PeerLaunchCodes.NoAnswer);
            }

            return reply.Body is { } b && PastSessionsWire.ParsePage(b) is { } parsed
                ? parsed
                : PeerPastSessionsResult.Fail(PeerLaunchCodes.NoAnswer);
        }

        public async Task<PeerDirResult> ListDirAsync(string machine, string path, CancellationToken ct)
        {
            var reply = await AskAsync(
                machine, PeerProtocol.ListDirGet, PeerLaunchWire.EncodeListDirRequest(path),
                PeerProtocol.ListDir, _seams.ListWait, ct).ConfigureAwait(false);

            if (reply is null)
            {
                bool lost;
                lock (_gate) lost = _lost.Contains(machine);
                return PeerDirResult.Fail(lost ? PeerLaunchCodes.LinkLost : PeerLaunchCodes.NoAnswer);
            }

            return reply.Body is { } b && PeerLaunchWire.ParseDir(b) is { } parsed
                ? parsed
                : PeerDirResult.Fail(PeerLaunchCodes.NoAnswer);
        }

        // Fed by PeerMirrorHost for `launch-info`, `launch-result` and `list-dir`.
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
                    request.FirstMessage, request.Nonce, request.ExpiresAt, request.Account,
                    request.ResumeSessionId ?? "");

                var signature = await sign(LaunchGate.PeerMessage(receiverPin, LaunchGate.Body(fields)))
                    .ConfigureAwait(false);

                return (RelayCrypto.Base64Url(signature.Sig), Convert.ToBase64String(signature.CertificateDer));
            };

        // The real trust signer: the same key and certificate as SignerFor,
        // over PeerTrustAnswer's own domain.
        internal static TrustSigner TrustSignerFor(
            Func<string, string?> pinFor, Func<byte[], Task<PeerIdentity.Signature>> sign) =>
            async (machine, fields) =>
            {
                if (pinFor(machine) is not { Length: > 0 } receiverPin) return null;
                var signature = await sign(PeerTrustAnswer.Message(receiverPin, PeerTrustAnswer.Body(fields))).ConfigureAwait(false);
                return (RelayCrypto.Base64Url(signature.Sig), Convert.ToBase64String(signature.CertificateDer));
            };

        private static string RandomNonce() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(AnswerGate.NonceLength))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
