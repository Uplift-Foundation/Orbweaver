using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

/// <summary>
/// CB-397: the codec for the trust question and answer, the asking side's
/// handling of both, and the signature, checked by the far desk's own verifier
/// with negative controls.
/// </summary>
public class PeerLaunchTrustWireClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly PeerLaunchSpec Spec = new("claude", "/w", "default");
    private static readonly PeerLaunchTrust Question = new("TOKEN", "claude", "/w", 1_800_000_000);
    private static readonly X509Certificate2 Cert = MakeCert();
    private static readonly string Receiver = new('a', 64);
    private static readonly string Nonce = "AAAAAAAAAAAAAAAAAAAAAA";

    private static X509Certificate2 MakeCert()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=sender", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var made = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ---- the codec -----------------------------------------------------------------------

    [Fact]
    public void A_trust_needed_result_round_trips_with_its_question()
    {
        var back = PeerLaunchWire.ParseResult(PeerLaunchWire.EncodeResult(
            new PeerLaunchResult(false, PeerLaunchCodes.TrustNeeded, null, null, Question)))!;
        Assert.Equal(Question, back.Trust);
        Assert.Equal(PeerLaunchCodes.TrustNeeded, back.Code);
    }

    [Theory]
    [InlineData("""{"ok":false,"code":"trust_needed"}""")]
    [InlineData("""{"ok":false,"code":"trust_needed","trust":{"token":"","cli":"claude","cwd":"/w","expires_at":1}}""")]
    [InlineData("""{"ok":false,"code":"trust_needed","trust":{"token":"t","cli":"","cwd":"/w","expires_at":1}}""")]
    [InlineData("""{"ok":false,"code":"trust_needed","trust":{"token":"t","cli":"claude","cwd":"","expires_at":1}}""")]
    [InlineData("""{"ok":false,"code":"trust_needed","trust":{"token":"t","cli":"claude","cwd":"/w","expires_at":0}}""")]
    public void A_trust_needed_result_without_a_whole_question_is_not_believed(string json)
    {
        Assert.Null(PeerLaunchWire.ParseResult(J(json)));
    }

    [Fact]
    public void A_question_on_any_other_code_is_dropped()
    {
        var back = PeerLaunchWire.ParseResult(J("""{"ok":false,"code":"launch_failed","trust":{"token":"t","cli":"claude","cwd":"/w","expires_at":1}}"""))!;
        Assert.Null(back.Trust);
    }

    [Fact]
    public void A_result_with_no_question_writes_no_trust_member()
    {
        Assert.False(PeerLaunchWire.EncodeResult(new PeerLaunchResult(true, "launched", "s", null)).TryGetProperty("trust", out _));
    }

    [Fact]
    public void A_launch_writes_can_trust_only_when_true_and_reads_it_back()
    {
        var plain = PeerLaunchWire.EncodeLaunch(new PeerLaunchWire.LaunchRequest("/w", "claude", "", "", "default", "", "", "n", 5, "s", "c"));
        Assert.False(plain.TryGetProperty("can_trust", out _));
        Assert.False(PeerLaunchWire.ParseLaunch(plain)!.CanTrust);

        var asks = PeerLaunchWire.EncodeLaunch(new PeerLaunchWire.LaunchRequest("/w", "claude", "", "", "default", "", "", "n", 5, "s", "c", true));
        Assert.True(asks.GetProperty("can_trust").GetBoolean());
        Assert.True(PeerLaunchWire.ParseLaunch(asks)!.CanTrust);
    }

    [Fact]
    public void A_trust_request_round_trips_and_rejects_every_malformed_shape()
    {
        var ok = new PeerLaunchWire.TrustRequest("T", PeerTrustAnswer.Trust, "N", 5, "S", "C");
        Assert.Equal(ok, PeerLaunchWire.ParseTrust(PeerLaunchWire.EncodeTrust(ok)));
        Assert.Equal(PeerTrustAnswer.Cancel, PeerLaunchWire.ParseTrust(PeerLaunchWire.EncodeTrust(ok with { Answer = PeerTrustAnswer.Cancel }))!.Answer);

        Assert.Null(PeerLaunchWire.ParseTrust(J("[]")));
        Assert.Null(PeerLaunchWire.ParseTrust(J("""{"token":1}""")));
        Assert.Null(PeerLaunchWire.ParseTrust(J("null")));
        foreach (var bad in new[]
        {
            ok with { Token = "" }, ok with { Answer = "maybe" }, ok with { Nonce = "" }, ok with { ExpiresAt = 0 },
            ok with { Sig = "" }, ok with { Cert = "" }, ok with { Token = new string('x', 65) }, ok with { Token = "a\nb" },
        })
            Assert.Null(PeerLaunchWire.ParseTrust(PeerLaunchWire.EncodeTrust(bad)));
    }

    // ---- the signature -------------------------------------------------------------------

    private static PeerTrustAnswer.Fields Fields(string answer = "trust") =>
        new("TOKEN", answer, "/w", "claude", "default", "LAUNCHNONCE", Nonce, 1_800_000_000);

    private static Task<PeerIdentity.Signature> Sign(byte[] m) => Task.FromResult(PeerIdentity.Sign(Cert, m));

    private static async Task<(string Sig, string Cert)> Signed(PeerTrustAnswer.Fields f)
    {
        var signer = PeerLaunchClient.TrustSignerFor(m => m == "mini" ? Receiver : null, Sign);
        return (await signer("mini", f))!.Value;
    }

    [Fact]
    public async Task The_far_desk_accepts_what_the_client_signed_and_nothing_else()
    {
        var f = Fields();
        var (sig, cert) = await Signed(f);
        var der = Convert.FromBase64String(cert);
        var pin = PeerIdentity.PinOf(Cert);

        Assert.True(PeerTrustAnswer.Verify(pin, Receiver, der, f, sig));

        // Negative controls: each would pass if the signature were not bound to what it claims.
        Assert.False(PeerTrustAnswer.Verify(pin, new string('b', 64), der, f, sig));          // another desk
        Assert.False(PeerTrustAnswer.Verify(pin, Receiver, der, Fields("cancel"), sig));      // the other answer
        Assert.False(PeerTrustAnswer.Verify(pin, Receiver, der, f with { Cwd = "/x" }, sig)); // another folder
        Assert.False(PeerTrustAnswer.Verify(pin, Receiver, der, f with { Cli = "codex" }, sig));
        Assert.False(PeerTrustAnswer.Verify(pin, Receiver, der, f with { Account = "other" }, sig));
        Assert.False(PeerTrustAnswer.Verify(pin, Receiver, der, f with { LaunchNonce = "OTHER" }, sig));
        Assert.False(PeerTrustAnswer.Verify(pin, Receiver, der, f with { Token = "OTHER" }, sig));
        Assert.False(PeerTrustAnswer.Verify(pin, Receiver, der, f with { ExpiresAt = 1 }, sig));
        Assert.False(PeerTrustAnswer.Verify(new string('c', 64), Receiver, der, f, sig));     // not the paired certificate
        Assert.False(PeerTrustAnswer.Verify("", Receiver, der, f, sig));                      // no pairing
        Assert.False(PeerTrustAnswer.Verify(pin, "", der, f, sig));                           // no own pin
        Assert.False(PeerTrustAnswer.Verify(pin, Receiver, der, f with { Nonce = "short" }, sig));
        Assert.False(PeerTrustAnswer.Verify(pin, Receiver, der, f, ""));
        Assert.False(PeerTrustAnswer.Verify(pin, Receiver, [1, 2, 3], f, sig));
    }

    // Domain separation both ways: a launch signature is never a trust answer, and the reverse.
    [Fact]
    public async Task A_launch_signature_is_not_a_trust_answer_and_the_reverse()
    {
        var f = Fields();
        var body = PeerTrustAnswer.Body(f);
        var asLaunch = PeerIdentity.Sign(Cert, LaunchGate.PeerMessage(Receiver, body));
        Assert.False(PeerTrustAnswer.Verify(PeerIdentity.PinOf(Cert), Receiver, asLaunch.CertificateDer, f, RelayCrypto.Base64Url(asLaunch.Sig)));

        var (sig, cert) = await Signed(f);
        using var x = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(cert));
        using var key = x.GetRSAPublicKey()!;
        Assert.False(key.VerifyData(LaunchGate.PeerMessage(Receiver, body), RelayCrypto.FromBase64Url(sig)!, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task A_machine_with_no_pairing_cannot_be_signed_for()
    {
        var none = PeerLaunchClient.TrustSignerFor(_ => null, Sign);
        var empty = PeerLaunchClient.TrustSignerFor(_ => "", Sign);
        Assert.Null(await none("x", Fields()));
        Assert.Null(await empty("x", Fields()));
    }

    // ---- the asking side -----------------------------------------------------------------

    private sealed class Rig
    {
        public readonly List<PeerProtocol.PeerMessage> Sent = [];
        public Action<PeerProtocol.PeerMessage>? OnSend;
        public bool CanSign = true;
        public readonly PeerLaunchClient Client;
        public readonly List<PeerTrustAnswer.Fields> Signed = [];

        public Rig(bool trustSigner = true, bool throwsOnSign = false)
        {
            Client = new PeerLaunchClient(new PeerLaunchClient.Seams(
                (_, msg) => { Sent.Add(msg); OnSend?.Invoke(msg); return Task.FromResult(true); },
                () => [new PeerMachine("mini", true)],
                (_, _) => Task.FromResult<(string, string)?>(("SIG", "CERT")),
                () => Now)
            {
                LaunchWait = TimeSpan.FromMilliseconds(300),
                NewNonce = () => Nonce,
                TrustSign = !trustSigner ? null : (m, f) =>
                {
                    if (throwsOnSign) throw new TimeoutException("loading");
                    Signed.Add(f);
                    return Task.FromResult<(string, string)?>(CanSign ? ("TSIG", "TCERT") : null);
                },
            });
        }

        public void ReplyTo(PeerProtocol.PeerMessage m, PeerLaunchResult r, string from = "mini") =>
            Client.OnReply(from, PeerProtocol.Message(PeerProtocol.LaunchResult, m.Id, body: PeerLaunchWire.EncodeResult(r)));

        public async Task<PeerLaunchResult> AskedAsync(PeerLaunchTrust? q = null)
        {
            OnSend = m => ReplyTo(m, new PeerLaunchResult(false, PeerLaunchCodes.TrustNeeded, null, null, q ?? Question));
            var r = await Client.LaunchAsync("mini", Spec, CancellationToken.None);
            OnSend = null;
            return r;
        }
    }

    [Fact]
    public async Task A_launch_says_it_can_show_the_question_and_passes_the_question_up()
    {
        var rig = new Rig();
        var r = await rig.AskedAsync();

        Assert.Equal(PeerLaunchCodes.TrustNeeded, r.Code);
        Assert.Equal(Question, r.Trust);
        Assert.True(PeerLaunchWire.ParseLaunch(rig.Sent[0].Body!.Value)!.CanTrust);
    }

    [Theory]
    [InlineData("/elsewhere", "claude")]
    [InlineData("/w", "codex")]
    public async Task A_question_about_another_folder_or_cli_is_not_an_answer_to_this_launch(string cwd, string cli)
    {
        var rig = new Rig();
        var r = await rig.AskedAsync(Question with { Cwd = cwd, Cli = cli });
        Assert.Equal(new PeerLaunchResult(false, PeerLaunchCodes.NoAnswer, null, "unexpected trust question"), r);
    }

    [Fact]
    public async Task Trust_sends_a_signed_launch_trust_frame_over_the_launch_that_was_sent_and_reads_the_reply()
    {
        var rig = new Rig();
        await rig.AskedAsync();
        rig.OnSend = m => rig.ReplyTo(m, new PeerLaunchResult(true, PeerLaunchCodes.Launched, "sid", null));

        var r = await rig.Client.TrustAsync("mini", Question, trusted: true, CancellationToken.None);

        Assert.Equal(new PeerLaunchResult(true, PeerLaunchCodes.Launched, "sid", null), r);
        var frame = rig.Sent[^1];
        Assert.Equal(PeerProtocol.LaunchTrust, frame.Type);
        var body = PeerLaunchWire.ParseTrust(frame.Body!.Value)!;
        Assert.Equal(("TOKEN", "trust", "TSIG", "TCERT"), (body.Token, body.Answer, body.Sig, body.Cert));
        Assert.Equal(Now.AddSeconds(120).ToUnixTimeSeconds(), body.ExpiresAt);
        // Signed over the values of the launch this desk sent.
        var f = Assert.Single(rig.Signed);
        Assert.Equal(("/w", "claude", "default", Nonce), (f.Cwd, f.Cli, f.Account, f.LaunchNonce));
    }

    [Fact]
    public async Task Cancel_is_the_other_answer_and_each_question_is_answered_once()
    {
        var rig = new Rig();
        await rig.AskedAsync();
        rig.OnSend = m => rig.ReplyTo(m, new PeerLaunchResult(false, PeerLaunchCodes.TrustCancelled, null, null));

        var r = await rig.Client.TrustAsync("mini", Question, trusted: false, CancellationToken.None);
        Assert.Equal(PeerLaunchCodes.TrustCancelled, r.Code);
        Assert.Equal("cancel", PeerLaunchWire.ParseTrust(rig.Sent[^1].Body!.Value)!.Answer);

        var sentBefore = rig.Sent.Count;
        var again = await rig.Client.TrustAsync("mini", Question, trusted: true, CancellationToken.None);
        Assert.Equal(PeerLaunchCodes.TrustGone, again.Code);
        Assert.Equal(sentBefore, rig.Sent.Count);
    }

    [Fact]
    public async Task A_question_this_desk_was_never_asked_sends_nothing()
    {
        var rig = new Rig();
        Assert.Equal(PeerLaunchCodes.TrustGone, (await rig.Client.TrustAsync("mini", Question, true, CancellationToken.None)).Code);

        await rig.AskedAsync();
        var sent = rig.Sent.Count;
        Assert.Equal(PeerLaunchCodes.TrustGone, (await rig.Client.TrustAsync("pc", Question, true, CancellationToken.None)).Code);
        Assert.Equal(sent, rig.Sent.Count);
    }

    [Fact]
    public async Task An_answer_this_desk_cannot_sign_sends_nothing()
    {
        foreach (var rig in new[] { new Rig(trustSigner: false), new Rig { CanSign = false }, new Rig(throwsOnSign: true) })
        {
            await rig.AskedAsync();
            var sent = rig.Sent.Count;
            rig.CanSign = false;
            var r = await rig.Client.TrustAsync("mini", Question, true, CancellationToken.None);
            Assert.Equal(PeerLaunchCodes.CannotSign, r.Code);
            Assert.Equal(sent, rig.Sent.Count);
        }
    }

    [Fact]
    public async Task A_silent_far_desk_is_no_answer_and_a_dropped_link_is_link_lost()
    {
        var rig = new Rig();
        await rig.AskedAsync();
        Assert.Equal(PeerLaunchCodes.NoAnswer, (await rig.Client.TrustAsync("mini", Question, true, CancellationToken.None)).Code);

        var lost = new Rig();
        await lost.AskedAsync();
        lost.OnSend = _ => lost.Client.OnDisconnected("mini");
        Assert.Equal(PeerLaunchCodes.LinkLost, (await lost.Client.TrustAsync("mini", Question, true, CancellationToken.None)).Code);
    }

    [Fact]
    public async Task An_answer_is_believed_only_from_the_machine_it_was_asked_of()
    {
        var rig = new Rig();
        await rig.AskedAsync();
        rig.OnSend = m => rig.ReplyTo(m, new PeerLaunchResult(true, PeerLaunchCodes.Launched, "forged", null), from: "pc");
        Assert.Equal(PeerLaunchCodes.NoAnswer, (await rig.Client.TrustAsync("mini", Question, true, CancellationToken.None)).Code);
    }

    // A question the far desk has given up is dropped on the next launch; a live one is kept.
    [Fact]
    public async Task An_expired_question_is_dropped_on_the_next_launch_and_a_live_one_is_kept()
    {
        var rig = new Rig();
        var expired = Question with { Token = "OLD", ExpiresAt = Now.ToUnixTimeSeconds() };
        await rig.AskedAsync(expired);
        var live = Question with { Token = "LIVE" };
        await rig.AskedAsync(live);   // the launch sweeps OLD before recording LIVE

        var sent = rig.Sent.Count;
        Assert.Equal(PeerLaunchCodes.TrustGone, (await rig.Client.TrustAsync("mini", expired, true, CancellationToken.None)).Code);
        Assert.Equal(sent, rig.Sent.Count);

        rig.OnSend = m => rig.ReplyTo(m, new PeerLaunchResult(true, PeerLaunchCodes.Launched, "sid", null));
        Assert.True((await rig.Client.TrustAsync("mini", live, true, CancellationToken.None)).Ok);

    }

    // A certificate the pin vouches for but that cannot be loaded, and one that loads but has no RSA key.
    [Fact]
    public void A_pinned_certificate_that_cannot_be_used_is_refused_not_a_crash()
    {
        var f = Fields();
        var garbage = new byte[] { 1, 2, 3, 4 };
        Assert.False(PeerTrustAnswer.Verify(PeerIdentity.PinOfDer(garbage), Receiver, garbage, f, "AAAA"));

        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=ec", ec, HashAlgorithmName.SHA256);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        Assert.False(PeerTrustAnswer.Verify(PeerIdentity.PinOf(made), Receiver, made.RawData, f, "AAAA"));
    }
}
