using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbweaver
{
    // The phone's signed `launch` (CB-316, frames-v4 sealed_plaintext.launch):
    // which checks it must pass, in the contract's check_order, before anything
    // is started. Pure — the filesystem, the CLI locator and the roster come in
    // as functions — so every refusal is a unit case and the vectors' signature
    // negatives replay against the shipped verifier.
    //
    // CB-357 gave it a second signer: another desktop over the peer link. The
    // rules after the signature are the same for both, so the only thing that
    // differs is which key, which domain and which nonce key — LaunchSigner.
    internal static class LaunchGate
    {
        internal const string Domain = "orbweaver-launch-v1";

        // The peer link's launch domain, distinct from the phone's so that a
        // signature made for one can never verify as the other, even if the
        // same body were signed with the same key.
        internal const string PeerDomain = "orbweaver-peer-launch-v1";
        internal const int TtlSlackSeconds = 300;
        internal const int MaxModelLength = 64;

        internal static readonly string[] Clis = ["claude", "codex", "grok"];

        private static readonly Regex ModelShape = new("^[A-Za-z0-9][A-Za-z0-9._:-]*$", RegexOptions.CultureInvariant);
        private static readonly Regex WindowsAbsolute = new(@"^[A-Za-z]:\\", RegexOptions.CultureInvariant);

        // CB-316 (PM decision): a Codex or Grok first message reaches a Windows
        // desk in argv through conhost.exe, and conhost expands %NAME% as it
        // starts the CLI — measured: %PATH% arrived as the machine's PATH. There
        // is no escape for it, so the expandable shape is refused, defined or
        // not: checkable from the phone, and no environment value can leak. A
        // lone %, "50%" or "% done %" is not that shape and passes verbatim.
        private static readonly Regex Expandable = new("%[A-Za-z_][A-Za-z0-9_]*%", RegexOptions.CultureInvariant);

        internal static bool ExpandsOnWindows(string message) => Expandable.IsMatch(message);

        // UTF-8 of exactly {cwd,cli,persona,model,parent_session_id,
        // first_message_sha256,nonce,expires_at}, canonical, rebuilt from the
        // frame's own fields — so any other byte layout the phone signed fails
        // as bad_signature rather than being trusted.
        internal static string Body(LaunchFields f) =>
            AccountField(new CanonicalJson().Str("cwd", f.Cwd).Str("cli", f.Cli).Str("persona", f.Persona).Str("model", f.Model), f)
                .Str("parent_session_id", f.ParentSessionId)
                .Str("first_message_sha256", RelayCrypto.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(f.FirstMessage))))
                .Str("nonce", f.Nonce).Num("expires_at", f.ExpiresAt).ToString();

        // account is signed only when the phone named one, between model and
        // parent_session_id, so a phone that never sends it (the iPhone app)
        // signs, and is checked against, exactly the body it always did.
        private static CanonicalJson AccountField(CanonicalJson body, LaunchFields f) =>
            f.Account.Length > 0 ? body.Str("account", f.Account) : body;

        internal static byte[] Message(string body) => [.. Encoding.UTF8.GetBytes(Domain), .. Encoding.UTF8.GetBytes(body)];

        // What a desktop signs to launch on another: the domain, then the
        // receiving desk's own pin, then the same body the phone signs. The pin
        // is fixed length (64 hex), so the concatenation is unambiguous, and it
        // binds the launch to one receiver: a desk that also trusts the sender
        // cannot be handed a launch meant for somebody else.
        internal static byte[] PeerMessage(string receiverPin, string body) =>
            [.. Encoding.UTF8.GetBytes(PeerDomain), .. Encoding.UTF8.GetBytes(receiverPin), .. Encoding.UTF8.GetBytes(body)];

        internal static bool Verify(byte[] phoneIdPublic, LaunchFields f, string sig) =>
            Verify(new PhoneSigner("", phoneIdPublic), f, sig);

        // Who signed a launch, and therefore how it is checked and whose nonce
        // window it is recorded in. Phones and peers never share a nonce key —
        // a peer's is prefixed — so one cannot spend the other's nonces.
        internal abstract record LaunchSigner(string NonceKey)
        {
            internal abstract bool Signed(LaunchFields f, byte[]? sig);
        }

        internal sealed record PhoneSigner(string PhoneId, byte[] IdPublic) : LaunchSigner(PhoneId)
        {
            internal override bool Signed(LaunchFields f, byte[]? sig) =>
                RelayCrypto.Verify(IdPublic, Message(Body(f)), sig);
        }

        // PinnedPin: the pin this desk paired with for the machine the link says
        // is asking, or "" when it has none. OwnPin: this desk's own pin, which
        // the sender must have signed. CertDer: the certificate the request
        // carried.
        //
        // The certificate is believed only because it hashes to the pin a
        // person paired with — never for anything it says about itself — and
        // only then is its public key used. RSA PKCS#1 v1.5 with SHA-256, the
        // padding the certificate's own self-signature already uses.
        internal sealed record PeerSigner(string PinnedPin, string OwnPin, byte[] CertDer) : LaunchSigner(PeerNoncePrefix + PinnedPin)
        {
            internal override bool Signed(LaunchFields f, byte[]? sig)
            {
                if (sig is null || sig.Length == 0 || OwnPin.Length == 0) return false;
                if (!PeerIdentity.Trusts(PinnedPin.Length == 0 ? null : new PeerIdentity.Peer(PinnedPin, ""),
                        PeerIdentity.PinOfDer(CertDer))) return false;
                try
                {
                    using var cert = X509CertificateLoader.LoadCertificate(CertDer);
                    using var key = cert.GetRSAPublicKey();
                    return key is not null && key.VerifyData(PeerMessage(OwnPin, Body(f)), sig,
                        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
                catch (CryptographicException)
                {
                    // Pinned bytes that are not a certificate. Unreachable without
                    // a SHA-256 preimage of a real certificate's pin, but a
                    // refusal rather than a crash.
                    return false;
                }
            }
        }

        internal const string PeerNoncePrefix = "peer:";

        internal static bool Verify(LaunchSigner signer, LaunchFields f, string sig) =>
            RelayCrypto.FromBase64Url(f.Nonce) is { Length: AnswerGate.NonceLength }
            && signer.Signed(f, RelayCrypto.FromBase64Url(sig));

        // What the gate needs to know about this desktop.
        internal sealed record Environment(
            bool IsWindows,
            Func<string, bool> CliInstalled,
            Func<string, bool> DirectoryExists,
            Func<string, bool> InRoster,
            // CB-422 (frames-v15): a parent that is a Paperclip row. InRoster is
            // about local CLI sessions, so without this a Paperclip parent would
            // read as unknown_parent, which tells the phone the session is gone
            // when it is a row on screen that simply cannot lead a team.
            Func<string, bool>? IsPaperclip = null);

        // The first failing check's code, or null when the launch may go ahead.
        // The nonce is recorded once the signature and expiry pass, as the
        // answer gate does: a replay of a launch refused for a later rule is
        // still a replay.
        internal static string? Decide(LaunchRequest r, string phoneId, byte[] phoneIdPublic, DateTimeOffset now,
            NonceWindow nonces, Environment env) =>
            Decide(r, new PhoneSigner(phoneId, phoneIdPublic), now, nonces, env);

        internal static string? Decide(LaunchRequest r, LaunchSigner signer, DateTimeOffset now,
            NonceWindow nonces, Environment env)
        {
            if (!Verify(signer, r.Fields, r.Sig)) return RelayReplies.BadSignature;
            return Rules(r.Fields, signer.NonceKey, now, nonces, env);
        }

        // Everything after the signature, in check_order.
        internal static string? Rules(LaunchFields f, string phoneId, DateTimeOffset now, NonceWindow nonces, Environment env)
        {
            var secs = now.ToUnixTimeSeconds();
            // A far-future expiry is a forged one; 300 s absorbs the Windows
            // box's ~1.5 s clock lead many times over.
            if (secs >= f.ExpiresAt || f.ExpiresAt > secs + TtlSlackSeconds) return RelayReplies.Expired;

            if (!nonces.TryAdd(phoneId, f.Nonce, now)) return RelayReplies.Replayed;

            if (Array.IndexOf(Clis, f.Cli) < 0 || !env.CliInstalled(f.Cli)) return RelayReplies.UnknownCli;
            if (!IsAbsolute(f.Cwd, env.IsWindows) || !env.DirectoryExists(f.Cwd)) return RelayReplies.UnknownCwd;
            if (!ModelOk(f.Model)) return RelayReplies.BadModel;
            if (f.Persona.Length > 0) return RelayReplies.UnsupportedPersona;
            if (f.ParentSessionId.Length > 0)
            {
                if (!env.InRoster(f.ParentSessionId))
                    return env.IsPaperclip?.Invoke(f.ParentSessionId) == true ? RelayReplies.UnsupportedParent : RelayReplies.UnknownParent;
                if (f.Cli != "claude") return RelayReplies.UnsupportedParent;
            }
            if (!RelayText.Typeable(f.FirstMessage, allowEmpty: true)) return RelayReplies.BadInput;
            if (env.IsWindows && f.Cli is "codex" or "grok" && ExpandsOnWindows(f.FirstMessage)) return RelayReplies.BadInput;
            return null;
        }

        // No fallback to the app's own directory (which the desk's New Chat
        // dialog has): a phone cannot see which folder it got.
        internal static bool IsAbsolute(string cwd, bool isWindows) =>
            isWindows ? WindowsAbsolute.IsMatch(cwd) : cwd.StartsWith('/');

        // "" is the CLI's default; anything else is passed to the CLI, which is
        // the only authority on its models, so only the charset is checked.
        internal static bool ModelOk(string model) =>
            model.Length == 0 || (model.Length <= MaxModelLength && ModelShape.IsMatch(model));

        internal static NewChatCli CliFor(string cli) => cli switch
        {
            "codex" => NewChatCli.Codex,
            "grok" => NewChatCli.Grok,
            _ => NewChatCli.ClaudeCode,
        };
    }
}
