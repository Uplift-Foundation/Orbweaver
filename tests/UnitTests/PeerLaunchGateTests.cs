using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-357: LaunchGate's second signer, another desktop over the peer link.
///
/// This is a trust boundary — a passing request starts a process on this
/// machine — so every refusal is paired with the control that passes, and
/// each control differs from its refusal in exactly the one thing the
/// refusal is about. A refusal without its control could be passing because
/// the setup is broken rather than because the rule holds.
/// </summary>
public class PeerLaunchGateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1794111792);

    // Two desktops, A (the sender) and B (this desk, receiving), plus a third
    // C that B also trusts. Real RSA 2048 certificates, the shape
    // PeerIdentity.Create makes.
    private static readonly X509Certificate2 A = Cert("a");
    private static readonly X509Certificate2 C = Cert("c");
    private static readonly X509Certificate2 B = Cert("b");

    private static readonly string PinA = PeerIdentity.PinOf(A);
    private static readonly string PinB = PeerIdentity.PinOf(B);
    private static readonly string PinC = PeerIdentity.PinOf(C);

    private static readonly RelayCrypto.KeyPair Phone = RelayCrypto.GenerateKeyPair();

    private static X509Certificate2 Cert(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(Now.AddDays(-1), Now.AddYears(10));
    }

    private static LaunchFields F(string cwd = "/w", string? nonce = null, long? expires = null) =>
        new(cwd, "claude", "", "", "", "", nonce ?? RelayCrypto.Base64Url(new byte[16]),
            expires ?? Now.ToUnixTimeSeconds() + 120, "default");

    // A signs `f` for `receiver` the way the sending desk does.
    private static LaunchRequest PeerSigned(LaunchFields f, X509Certificate2? by = null, string? receiver = null) =>
        new("r", f, RelayCrypto.Base64Url(
            PeerIdentity.Sign(by ?? A, LaunchGate.PeerMessage(receiver ?? PinB, LaunchGate.Body(f))).Sig));

    // B's view of a request from the machine it paired as A.
    private static LaunchGate.PeerSigner AsA(byte[]? certDer = null, string? pinned = null, string? own = null) =>
        new(pinned ?? PinA, own ?? PinB, certDer ?? A.RawData);

    private static LaunchGate.Environment Env() => new(false, _ => true, _ => true, _ => false);

    private static string? Decide(LaunchRequest r, LaunchGate.LaunchSigner signer, NonceWindow? nonces = null) =>
        LaunchGate.Decide(r, signer, Now, nonces ?? new NonceWindow(), Env());

    [Fact]
    public void A_launch_signed_by_the_paired_peer_for_this_desk_passes() =>
        Assert.Null(Decide(PeerSigned(F()), AsA()));

    // The domains are the separation between the two signer types. The same
    // RSA key over the same body under the phone's domain must not verify.
    [Fact]
    public void A_phone_domain_signature_by_the_peers_own_key_is_refused()
    {
        var f = F();
        var phoneDomain = new LaunchRequest("r", f, RelayCrypto.Base64Url(
            PeerIdentity.Sign(A, LaunchGate.Message(LaunchGate.Body(f))).Sig));

        Assert.Equal(RelayReplies.BadSignature, Decide(phoneDomain, AsA()));
        Assert.Null(Decide(PeerSigned(f), AsA()));
    }

    // A real phone's P-256 signature, offered as though a peer had made it.
    [Fact]
    public void A_phone_signature_presented_as_a_peers_is_refused()
    {
        var f = F();
        var phone = new LaunchRequest("r", f, RelayCrypto.Base64Url(
            RelayCrypto.Sign(Phone, LaunchGate.Message(LaunchGate.Body(f)))));

        Assert.Equal(RelayReplies.BadSignature, Decide(phone, AsA()));
        Assert.Null(Decide(phone, new LaunchGate.PhoneSigner("ph", Phone.Public)));   // control: it is a good phone launch
    }

    // And the other way: a peer's signature offered to the phone's verifier,
    // including the phone's own overloads, which RelayRemote still calls.
    [Fact]
    public void A_peer_signature_presented_as_a_phones_is_refused()
    {
        var r = PeerSigned(F());

        Assert.False(LaunchGate.Verify(Phone.Public, r.Fields, r.Sig));
        Assert.Equal(RelayReplies.BadSignature,
            LaunchGate.Decide(r, "ph", Phone.Public, Now, new NonceWindow(), Env()));
        Assert.True(LaunchGate.Verify(AsA(), r.Fields, r.Sig));
    }

    // C is trusted by B too, but B paired *A* under this machine name: a valid
    // signature by C's key, carrying C's certificate, is not A's.
    [Fact]
    public void A_certificate_whose_pin_is_not_the_paired_machines_is_refused()
    {
        var byC = PeerSigned(F(), by: C);

        Assert.Equal(RelayReplies.BadSignature, Decide(byC, AsA(certDer: C.RawData)));
        Assert.Null(Decide(byC, AsA(certDer: C.RawData, pinned: PinC)));
    }

    // Carrying A's real certificate does not help a signature A did not make.
    [Fact]
    public void The_paired_certificate_with_another_keys_signature_is_refused() =>
        Assert.Equal(RelayReplies.BadSignature, Decide(PeerSigned(F(), by: C), AsA()));

    // No pairing for the machine the link named: the server passes "".
    [Fact]
    public void An_unknown_peer_is_refused()
    {
        var r = PeerSigned(F());

        Assert.Equal(RelayReplies.BadSignature, Decide(r, AsA(pinned: "")));
        Assert.Null(Decide(r, AsA()));
    }

    // Signed by A for C. B trusts A, but this launch was never meant for B.
    [Fact]
    public void A_launch_signed_for_a_different_receiver_is_refused()
    {
        var forC = PeerSigned(F(), receiver: PinC);

        Assert.Equal(RelayReplies.BadSignature, Decide(forC, AsA()));
        Assert.Null(Decide(forC, AsA(own: PinC)));   // control: it is a good launch for C
    }

    [Fact]
    public void A_tampered_field_is_refused()
    {
        var r = PeerSigned(F());

        Assert.Equal(RelayReplies.BadSignature, Decide(r with { Fields = r.Fields with { Cwd = "/etc" } }, AsA()));
        Assert.Equal(RelayReplies.BadSignature, Decide(r with { Fields = r.Fields with { Account = "work" } }, AsA()));
    }

    [Fact]
    public void An_expired_or_far_future_request_is_refused()
    {
        var secs = Now.ToUnixTimeSeconds();

        Assert.Equal(RelayReplies.Expired, Decide(PeerSigned(F(expires: secs)), AsA()));
        Assert.Equal(RelayReplies.Expired, Decide(PeerSigned(F(expires: secs + LaunchGate.TtlSlackSeconds + 1)), AsA()));
        Assert.Null(Decide(PeerSigned(F(expires: secs + LaunchGate.TtlSlackSeconds)), AsA()));
    }

    [Fact]
    public void A_replayed_nonce_is_refused()
    {
        var nonces = new NonceWindow();
        var r = PeerSigned(F());

        Assert.Null(Decide(r, AsA(), nonces));
        Assert.Equal(RelayReplies.Replayed, Decide(r, AsA(), nonces));
        Assert.Null(Decide(PeerSigned(F(nonce: RelayCrypto.Base64Url(Enumerable.Repeat((byte)1, 16).ToArray()))), AsA(), nonces));
    }

    // A peer's nonces live under a prefixed key, so a phone whose id happened
    // to equal a pin could not spend them, nor they its.
    [Fact]
    public void Peer_and_phone_nonces_are_kept_apart()
    {
        var nonces = new NonceWindow();
        var f = F();
        var phone = new LaunchRequest("r", f, RelayCrypto.Base64Url(
            RelayCrypto.Sign(Phone, LaunchGate.Message(LaunchGate.Body(f)))));

        Assert.Equal("peer:" + PinA, AsA().NonceKey);
        Assert.Null(Decide(PeerSigned(f), AsA(), nonces));
        Assert.Null(Decide(phone, new LaunchGate.PhoneSigner(PinA, Phone.Public), nonces));
    }

    // The arms inside PeerSigner that a well-formed request never reaches.
    [Fact]
    public void Malformed_peer_material_is_refused_not_thrown()
    {
        var r = PeerSigned(F());

        Assert.Equal(RelayReplies.BadSignature, Decide(r with { Sig = "" }, AsA()));
        Assert.Equal(RelayReplies.BadSignature, Decide(r with { Sig = "!" }, AsA()));
        Assert.Equal(RelayReplies.BadSignature, Decide(r, AsA(own: "")));

        // Pinned bytes that are not a certificate at all.
        byte[] junk = [1, 2, 3, 4];
        Assert.Equal(RelayReplies.BadSignature, Decide(r, AsA(certDer: junk, pinned: PeerIdentity.PinOfDer(junk))));

        // A real, pinned certificate with no RSA key in it.
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var ecCert = new CertificateRequest("CN=ec", ec, HashAlgorithmName.SHA256).CreateSelfSigned(Now.AddDays(-1), Now.AddDays(1));
        Assert.Equal(RelayReplies.BadSignature, Decide(r, AsA(certDer: ecCert.RawData, pinned: PeerIdentity.PinOf(ecCert))));

        // A nonce of the wrong length, signed properly.
        Assert.Equal(RelayReplies.BadSignature, Decide(PeerSigned(F(nonce: RelayCrypto.Base64Url(new byte[15]))), AsA()));
    }

    [Fact]
    public void Signing_needs_the_private_key()
    {
        using var publicOnly = X509CertificateLoader.LoadCertificate(A.RawData);

        Assert.Throws<CryptographicException>(() => PeerIdentity.Sign(publicOnly, [1]));
        Assert.Equal(A.RawData, PeerIdentity.Sign(A, [1]).CertificateDer);
    }

    [Fact]
    public void The_peer_message_is_domain_then_receiver_pin_then_body()
    {
        var bytes = LaunchGate.PeerMessage(PinB, "{}");

        Assert.Equal("orbweaver-peer-launch-v1" + PinB + "{}", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.NotEqual(LaunchGate.Domain, LaunchGate.PeerDomain);
    }
}
