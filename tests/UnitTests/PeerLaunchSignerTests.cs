using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-357: what PeerLaunchClient signs, checked by LaunchGate's own verifier
// rather than by a re-derivation of its rules, with negative controls that
// would pass if the signature were not actually bound to the receiver.
public class PeerLaunchSignerTests
{
    private static readonly X509Certificate2 Cert = MakeCert();
    private static readonly string Receiver = new('a', 64);

    private static X509Certificate2 MakeCert()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=sender", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var made = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    private static readonly PeerLaunchWire.LaunchRequest Request = new(
        "/w", "claude", "", "", "default", "", "", "AAAAAAAAAAAAAAAAAAAAAA", 1_800_000_000, "", "");

    private static LaunchFields Fields(PeerLaunchWire.LaunchRequest r) => new(
        r.Cwd, r.Cli, r.Persona, r.Model, r.ParentSessionId, r.FirstMessage, r.Nonce, r.ExpiresAt, r.Account);

    private static Task<PeerIdentity.Signature> Sign(byte[] m) => Task.FromResult(PeerIdentity.Sign(Cert, m));

    private static async Task<(string Sig, string Cert)> Signed(string machine = "mini")
    {
        var signer = PeerLaunchClient.SignerFor(m => m == machine ? Receiver : null, Sign);
        return (await signer(machine, Request))!.Value;
    }

    [Fact]
    public async Task TheGateAcceptsWhatTheClientSigned()
    {
        var (sig, cert) = await Signed();

        var gate = new LaunchGate.PeerSigner(PeerIdentity.PinOf(Cert), Receiver, Convert.FromBase64String(cert));

        Assert.True(LaunchGate.Verify(gate, Fields(Request), sig));
    }

    [Fact]
    public async Task ALaunchSignedForOneDeskIsRefusedByAnother()
    {
        var (sig, cert) = await Signed();

        var other = new LaunchGate.PeerSigner(PeerIdentity.PinOf(Cert), new string('b', 64), Convert.FromBase64String(cert));

        Assert.False(LaunchGate.Verify(other, Fields(Request), sig));
    }

    [Fact]
    public async Task ATamperedFieldIsRefused()
    {
        var (sig, cert) = await Signed();
        var gate = new LaunchGate.PeerSigner(PeerIdentity.PinOf(Cert), Receiver, Convert.FromBase64String(cert));

        Assert.False(LaunchGate.Verify(gate, Fields(Request with { Cwd = "/elsewhere" }), sig));
    }

    [Fact]
    public async Task ACertificateThatIsNotThePairedOneIsRefused()
    {
        var (sig, cert) = await Signed();

        var gate = new LaunchGate.PeerSigner(new string('c', 64), Receiver, Convert.FromBase64String(cert));

        Assert.False(LaunchGate.Verify(gate, Fields(Request), sig));
    }

    [Fact]
    public async Task AMachineWithNoPairingCannotBeSignedFor()
    {
        var signer = PeerLaunchClient.SignerFor(_ => null, Sign);
        var empty = PeerLaunchClient.SignerFor(_ => "", Sign);

        Assert.Null(await signer("stranger", Request));
        Assert.Null(await empty("stranger", Request));
    }
}
