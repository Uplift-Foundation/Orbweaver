using Xunit;

namespace Orbweaver.IntegrationTests;

// CB-357 against this machine's real peer identity.
//
// The plan's open question: can the certificate's private key sign anything
// but a TLS handshake? On macOS the key lives in the temporary keychain the
// PKCS#12 import made, and nothing before this ticket ever asked it for a
// signature directly. This runs on both CI legs, so it is the proof for each.
//
// "Settings" because PeerIdentity reads and writes under ORBWEAVER_SETTINGS_DIR.
[Collection("Settings")]
public class PeerLaunchSignatureTests
{
    [Fact]
    public async Task TheRealIdentitySignsALaunchThatTheGateAccepts()
    {
        var receiverPin = new string('b', 64);
        var fields = new LaunchFields("/w", "claude", "", "", "", "", RelayCrypto.Base64Url(new byte[16]),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120, "default");

        var signed = await PeerIdentity.SignAsync(LaunchGate.PeerMessage(receiverPin, LaunchGate.Body(fields)));
        var sig = RelayCrypto.Base64Url(signed.Sig);

        // The certificate it hands back is the one the far side pinned.
        Assert.Equal(PeerIdentity.OwnPin(), PeerIdentity.PinOfDer(signed.CertificateDer));

        Assert.True(LaunchGate.Verify(
            new LaunchGate.PeerSigner(PeerIdentity.OwnPin(), receiverPin, signed.CertificateDer), fields, sig));

        // Control: the same signature for a desk it was not meant for.
        Assert.False(LaunchGate.Verify(
            new LaunchGate.PeerSigner(PeerIdentity.OwnPin(), new string('c', 64), signed.CertificateDer), fields, sig));
    }
}
