using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Orbweaver
{
    // CB-397: what the launching desktop signs to answer the far desk's
    // "trust this folder?" question, and how the far desk checks it.
    //
    // A signature over the receiving desk's pin, the pending launch as the far
    // desk remembers it, and this answer. cwd, cli, account and launch_nonce
    // are not on the wire: the far desk rebuilds them from its own record and
    // the near desk signs the values of the launch it sent, so a signature
    // verifies only for this pin, this launch and this answer. A domain string
    // of its own keeps a launch signature from ever verifying as a trust
    // answer, and the reverse.
    internal static class PeerTrustAnswer
    {
        internal const string Domain = "orbweaver-peer-launch-trust-v1";
        internal const string Trust = "trust";
        internal const string Cancel = "cancel";

        internal sealed record Fields(
            string Token, string Answer, string Cwd, string Cli, string Account,
            string LaunchNonce, string Nonce, long ExpiresAt);

        internal static string Body(Fields f) =>
            new CanonicalJson().Str("token", f.Token).Str("answer", f.Answer).Str("cwd", f.Cwd).Str("cli", f.Cli)
                .Str("account", f.Account).Str("launch_nonce", f.LaunchNonce).Str("nonce", f.Nonce)
                .Num("expires_at", f.ExpiresAt).ToString();

        internal static byte[] Message(string receiverPin, string body) =>
            [.. Encoding.UTF8.GetBytes(Domain), .. Encoding.UTF8.GetBytes(receiverPin), .. Encoding.UTF8.GetBytes(body)];

        // The same trust as LaunchGate.PeerSigner: the certificate is believed
        // only because it hashes to the pin paired for the machine the link
        // names, and then only its public key is used. RSA PKCS#1 v1.5, SHA-256.
        internal static bool Verify(string pinnedPin, string ownPin, byte[] certDer, Fields f, string sig)
        {
            if (RelayCrypto.FromBase64Url(f.Nonce) is not { Length: AnswerGate.NonceLength }) return false;
            if (RelayCrypto.FromBase64Url(sig) is not { Length: > 0 } raw || ownPin.Length == 0) return false;
            if (!PeerIdentity.Trusts(pinnedPin.Length == 0 ? null : new PeerIdentity.Peer(pinnedPin, ""),
                    PeerIdentity.PinOfDer(certDer))) return false;
            try
            {
                using var cert = X509CertificateLoader.LoadCertificate(certDer);
                using var key = cert.GetRSAPublicKey();
                return key is not null && key.VerifyData(Message(ownPin, Body(f)), raw,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch (CryptographicException)
            {
                return false;
            }
        }
    }
}
