using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Orbweaver.IntegrationTests;

// CB-301 against the real loader and a real identity file.
//
// The unit tests stand a fake loader in for the PKCS#12 import, because a
// keychain prompt cannot be summoned on demand. What they cannot show is that
// the real import still hands SslStream a certificate it can use, and that the
// pin — now read out of the PKCS#12 by BouncyCastle rather than off a loaded
// certificate — is still the pin of that certificate. If the two ever
// disagreed, every paired machine would refuse this one as an interception.
//
// "Settings" because PeerIdentity reads and writes under ORBWEAVER_SETTINGS_DIR,
// beside PeerMirrorEndToEndTests which seeds pins in the same file.
[Collection("Settings")]
public class PeerIdentityLoadRoundTripTests
{
    [Fact]
    public async Task TheRealLoadGivesAUsableCertificateWithThePinWeAnnounce()
    {
        PeerIdentity.ForgetForTests();

        var cert = await PeerIdentity.CertificateAsync(TimeSpan.FromSeconds(30));

        Assert.True(cert.HasPrivateKey, "SslStream needs the private key to offer this certificate");
        Assert.Equal(PeerIdentity.PinOf(cert), PeerIdentity.OwnPin());

        // Loaded once: the handshake path gets the very same instance.
        Assert.Same(cert, await PeerIdentity.HandshakeCertificateAsync(CancellationToken.None));

        // And it is the identity on disk, not one made up per process: read
        // again from the same file, the pin is unchanged.
        var pin = PeerIdentity.OwnPin();
        PeerIdentity.ForgetForTests();

        Assert.Equal(pin, PeerIdentity.OwnPin());
        Assert.Equal(pin, PeerIdentity.PinOf(await PeerIdentity.CertificateAsync(TimeSpan.FromSeconds(30))));
    }

    // The pin is read without the OS key store. On macOS every import makes a
    // temporary keychain file; taking the pin must make none.
    [Fact]
    public void TakingThePinCreatesNoTemporaryKeychain()
    {
        PeerIdentity.ForgetForTests();
        _ = PeerIdentity.OwnPin(); // the identity file exists before counting
        PeerIdentity.ForgetForTests();

        var temp = Path.GetTempPath();
        var before = Directory.GetFiles(temp, "*.keychain").Length;

        for (var i = 0; i < 20; i++)
        {
            PeerIdentity.ForgetForTests();
            _ = PeerIdentity.OwnPin();
        }

        // Other processes share this directory, so this is a ceiling rather
        // than an exact count: twenty imports would be twenty files.
        Assert.True(Directory.GetFiles(temp, "*.keychain").Length - before < 5);
    }
}
