using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-357: the receiving desk's handling of another desktop's `launch`, in the
/// order PeerLaunchServer documents. Each refusal is paired with the request
/// that passes, differing in the one thing the refusal is about.
/// </summary>
public class PeerLaunchServerTests
{
    private static readonly X509Certificate2 Sender = Cert("mbp");
    private static readonly X509Certificate2 Stranger = Cert("x");
    private static readonly string SenderPin = PeerIdentity.PinOf(Sender);
    private static readonly string OwnPin = new('b', 64);

    private static X509Certificate2 Cert(string name)
    {
        using var key = RSA.Create(2048);
        return new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private sealed class World
    {
        public readonly LaunchRunnerTests.Desk Desk = new();
        public bool Enabled = true;
        public readonly Dictionary<string, PeerIdentity.Peer> Paired = new(StringComparer.OrdinalIgnoreCase)
        {
            ["mbp"] = new(SenderPin, "mbp"),
        };
        public readonly List<string> Log = [];
        public int KnownPeerCalls;
        public readonly PeerLaunchServer Server;

        public World()
        {
            Server = new PeerLaunchServer(new PeerLaunchServer.Seams(
                () => Enabled,
                m => { KnownPeerCalls++; return Paired.GetValueOrDefault(m); },
                () => OwnPin,
                Desk.Launch(),
                Desk.Accounts(),
                () => Desk.Now,
                Desk.Delay,
                Log.Add));
        }

        public Task<PeerLaunchResult> Handle(JsonElement body, string machine = "mbp") => Server.HandleAsync(machine, body);

        public void ChecksIn(string id = "new")
        {
            var polls = 0;
            Desk.OnDelay = () => { if (++polls == 1) Server.OnScanned([(id, LaunchRunnerTests.Status())]); };
        }
    }

    private static JsonElement Body(World w, string cwd = "/w", string account = "default", string message = "",
        X509Certificate2? by = null, string? receiver = null, long? expires = null, string? nonce = null,
        Func<LaunchFields, LaunchFields>? tamper = null, string? cert = null)
    {
        var f = new LaunchFields(cwd, "claude", "", "", "", message,
            nonce ?? RelayCrypto.Base64Url(RandomNumberGenerator.GetBytes(16)),
            expires ?? w.Desk.Now.ToUnixTimeSeconds() + 120, account);
        var signed = PeerIdentity.Sign(by ?? Sender, LaunchGate.PeerMessage(receiver ?? OwnPin, LaunchGate.Body(f)));
        var sent = tamper?.Invoke(f) ?? f;
        return PeerLaunchWire.EncodeLaunch(new PeerLaunchWire.LaunchRequest(
            sent.Cwd, sent.Cli, sent.Persona, sent.Model, sent.Account, sent.ParentSessionId, sent.FirstMessage,
            sent.Nonce, sent.ExpiresAt, RelayCrypto.Base64Url(signed.Sig),
            cert ?? Convert.ToBase64String(signed.CertificateDer)));
    }

    private static void Refused(PeerLaunchResult r, string code)
    {
        Assert.False(r.Ok);
        Assert.Equal(code, r.Code);
        Assert.Null(r.SessionId);
    }

    [Fact]
    public async Task A_signed_launch_from_the_paired_peer_starts_and_names_the_session()
    {
        var w = new World();
        w.ChecksIn();

        var r = await w.Handle(Body(w));

        Assert.Equal(new PeerLaunchResult(true, PeerLaunchCodes.Launched, "new", null), r);
        Assert.Equal("/w", w.Desk.MacStarts.Single().Cwd);
        Assert.Equal(["peer-launch mbp launched"], w.Log);
    }

    // The switch first: before parsing, before any signature work, and with
    // nothing recorded — so the very same request passes once it is on.
    [Fact]
    public async Task With_the_switch_off_nothing_is_parsed_verified_or_remembered()
    {
        var w = new World { Enabled = false };
        var body = Body(w);

        Refused(await w.Handle(body), PeerLaunchCodes.PeerLaunchOff);
        Refused(await w.Handle(JsonDocument.Parse("42").RootElement), PeerLaunchCodes.PeerLaunchOff);
        Assert.Equal(0, w.KnownPeerCalls);
        Assert.Equal(0, w.Server.NoncesSeen);
        Assert.Empty(w.Desk.MacStarts);

        w.Enabled = true;
        w.ChecksIn();
        Assert.True((await w.Handle(body)).Ok);
        Assert.Equal(1, w.Server.NoncesSeen);
    }

    [Fact]
    public async Task An_unreadable_body_is_bad_frame()
    {
        var w = new World();

        Refused(await w.Handle(JsonDocument.Parse("[]").RootElement), RelayReplies.BadFrame);
        Refused(await w.Handle(Body(w, cert: "not base64!")), RelayReplies.BadFrame);
        Assert.Equal(0, w.Server.NoncesSeen);
    }

    // A real phone's signature over the same body, carried in a peer launch.
    [Fact]
    public async Task A_phone_signature_presented_as_a_peers_is_refused()
    {
        var w = new World();
        var phone = RelayCrypto.GenerateKeyPair();
        var f = new LaunchFields("/w", "claude", "", "", "", "", RelayCrypto.Base64Url(new byte[16]),
            w.Desk.Now.ToUnixTimeSeconds() + 120, "default");
        var body = PeerLaunchWire.EncodeLaunch(new PeerLaunchWire.LaunchRequest(f.Cwd, f.Cli, "", "", f.Account, "", "",
            f.Nonce, f.ExpiresAt, RelayCrypto.Base64Url(RelayCrypto.Sign(phone, LaunchGate.Message(LaunchGate.Body(f)))),
            Convert.ToBase64String(Sender.RawData)));

        Refused(await w.Handle(body), RelayReplies.BadSignature);
        Assert.Empty(w.Desk.MacStarts);
    }

    // The sender's own key, but under the phone's domain.
    [Fact]
    public async Task A_phone_domain_signature_by_the_peer_is_refused()
    {
        var w = new World();
        var f = new LaunchFields("/w", "claude", "", "", "", "", RelayCrypto.Base64Url(new byte[16]),
            w.Desk.Now.ToUnixTimeSeconds() + 120, "default");
        var signed = PeerIdentity.Sign(Sender, LaunchGate.Message(LaunchGate.Body(f)));
        var body = PeerLaunchWire.EncodeLaunch(new PeerLaunchWire.LaunchRequest(f.Cwd, f.Cli, "", "", f.Account, "", "",
            f.Nonce, f.ExpiresAt, RelayCrypto.Base64Url(signed.Sig), Convert.ToBase64String(signed.CertificateDer)));

        Refused(await w.Handle(body), RelayReplies.BadSignature);
    }

    [Fact]
    public async Task A_certificate_that_is_not_the_paired_machines_is_refused()
    {
        var w = new World();
        Refused(await w.Handle(Body(w, by: Stranger)), RelayReplies.BadSignature);

        // Control: pair the stranger under that name and the same shape passes.
        w.Paired["mbp"] = new(PeerIdentity.PinOf(Stranger), "mbp");
        w.ChecksIn();
        Assert.True((await w.Handle(Body(w, by: Stranger))).Ok);
    }

    [Fact]
    public async Task A_machine_this_desk_never_paired_is_refused()
    {
        var w = new World();
        Refused(await w.Handle(Body(w), machine: "stranger"), RelayReplies.BadSignature);
        Assert.Equal(0, w.Server.NoncesSeen);
    }

    [Fact]
    public async Task A_launch_signed_for_another_receiver_is_refused()
    {
        var w = new World();
        Refused(await w.Handle(Body(w, receiver: new string('c', 64))), RelayReplies.BadSignature);
        Assert.Empty(w.Desk.MacStarts);
    }

    [Fact]
    public async Task A_tampered_folder_is_refused()
    {
        var w = new World();
        Refused(await w.Handle(Body(w, tamper: f => f with { Cwd = "/etc" })), RelayReplies.BadSignature);
    }

    [Fact]
    public async Task Expired_and_replayed_requests_are_refused()
    {
        var w = new World();
        Refused(await w.Handle(Body(w, expires: w.Desk.Now.ToUnixTimeSeconds())), RelayReplies.Expired);

        w.ChecksIn();
        var body = Body(w);
        Assert.True((await w.Handle(body)).Ok);
        Refused(await w.Handle(body), RelayReplies.Replayed);
        Assert.Single(w.Desk.MacStarts);
    }

    // The gate's later rules and the runner's refusals reach the sender as
    // their own codes, with the runner's sentence where it has one.
    [Fact]
    public async Task Later_refusals_arrive_as_their_own_codes()
    {
        var w = new World();
        w.Desk.DirExists = false;
        Refused(await w.Handle(Body(w)), RelayReplies.UnknownCwd);

        w.Desk.DirExists = true;
        Refused(await w.Handle(Body(w, account: "gone")), RelayReplies.UnknownAccount);

        w.Desk.MacPlace = (null, TerminalScripts.NoTmuxWhy);
        var noTmux = await w.Handle(Body(w));
        Refused(noTmux, RelayReplies.NoTerminal);
        Assert.Equal(TerminalScripts.NoTmuxWhy, noTmux.Why);

        w.Desk.MacPlace = ("%1", null);
        Refused(await w.Handle(Body(w)), RelayReplies.NoSession);
    }

    // The desktop window sends no first message, so one arriving is refused
    // after the gate (it is still a spent nonce) and nothing starts.
    [Fact]
    public async Task A_first_message_is_refused_and_starts_nothing()
    {
        var w = new World();
        var r = await w.Handle(Body(w, message: "rm -rf"));

        Refused(r, RelayReplies.BadInput);
        Assert.Equal(PeerLaunchServer.FirstMessageWhy, r.Why);
        Assert.Empty(w.Desk.MacStarts);
        Assert.Equal(1, w.Server.NoncesSeen);
    }
}
