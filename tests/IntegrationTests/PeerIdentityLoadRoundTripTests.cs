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
    // temporary keychain; the pin must make no import at all, so it must never
    // reach the loader — asserted against the real identity file with a loader
    // that fails the test if anything calls it.
    //
    // Process-local on purpose. Counting *.keychain files in the temp directory
    // would also count every other process's imports, and on a machine running
    // several suites that is a flake shipped knowingly. The on-machine delta is
    // measured at install time instead and reported in the PR.
    [Fact]
    public void TakingThePinFromTheRealIdentityNeverImportsAKey()
    {
        PeerIdentity.ForgetForTests();
        var expected = PeerIdentity.PinOf(
            X509CertificateLoader.LoadPkcs12(StoredPkcs12(), null, X509KeyStorageFlags.Exportable));

        var calls = 0;
        using (PeerIdentity.UseLoaderForTests(_ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("the pin must not import a key");
        }))
        {
            for (var i = 0; i < 20; i++)
            {
                PeerIdentity.ForgetForTests();
                Assert.Equal(expected, PeerIdentity.OwnPin());
            }
        }

        Assert.Equal(0, calls);
    }

    // The identity file as written, read independently of PeerIdentity.
    private static byte[] StoredPkcs12()
    {
        _ = PeerIdentity.OwnPin(); // makes sure the file exists
        var path = Path.Combine(OrbweaverSettings.Directory, "peer-identity.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return Convert.FromBase64String(doc.RootElement.GetProperty("pkcs12").GetString()!);
    }
}
