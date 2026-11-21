using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Orbweaver.IntegrationTests;

// CB-397: the trust question over the real link, through the real wiring
// (PeerSessions.ServeLaunchOn, PeerLaunchServer.Shared) and the real signer, with
// the start and the pane faked (LaunchRunner.StartForTests and TrustForTests), so
// nothing here starts, reads or kills a real terminal.
//
// A far desk on Windows never reads its console (the plan's named gap), so these
// have nothing to prove there and return at once.
[Collection("Settings")]
public class PeerLaunchTrustLoopbackTests : IDisposable
{
    private readonly List<PeerMirrorHost> _hosts = new();
    private readonly bool _saved = OrbweaverSettings.PeerLaunchInboundEnabled;
    private readonly string _folder = Directory.CreateTempSubdirectory("cb397-loop").FullName;
    private readonly string _screen;
    private readonly List<string> _closed = [];
    private readonly List<string> _presses = [];
    private string _current;

    public PeerLaunchTrustLoopbackTests()
    {
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TrustFixtures", "tmux-claude-trustdialog.txt"));
        _screen = fixture.Replace("/private/tmp/cb327-scratch/proj", _folder);
        _current = _screen;

        NewChatLauncher.LocateFreshForTests = cli => cli == NewChatCli.ClaudeCode ? "/bin/claude" : null;
        LaunchRunner.StartForTests = new LaunchRunner.StartOverride((_, _) => ("%77", null), _ => null);
        LaunchRunner.TrustForTests = new LaunchRunner.TrustSeams(
            _ => Task.FromResult<string?>(_current),
            (_, key, approved) =>
            {
                if (!approved(_current)) return Task.FromResult((false, (string?)_current));
                lock (_presses) _presses.Add(key);
                return Task.FromResult((true, (string?)_current));
            },
            pane => { lock (_closed) _closed.Add(pane); return Task.CompletedTask; });
        OrbweaverSettings.PeerLaunchInboundEnabled = true;
    }

    public void Dispose()
    {
        NewChatLauncher.LocateFreshForTests = null;
        LaunchRunner.StartForTests = null;
        LaunchRunner.TrustForTests = null;
        OrbweaverSettings.PeerLaunchInboundEnabled = _saved;
        foreach (var host in _hosts) host.Dispose();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private async Task<(PeerLaunchClient Client, PeerMirrorHost Receiver, PeerMirrorHost Sender)> Pair(PeerLaunchClient.TrustSigner? trustSigner = null)
    {
        var receiver = new PeerMirrorHost();
        var sender = new PeerMirrorHost();
        _hosts.Add(receiver);
        _hosts.Add(sender);

        var client = new PeerLaunchClient(new PeerLaunchClient.Seams(
            sender.Link.SendAsync,
            () => [new PeerMachine("receiver", true)],
            PeerLaunchClient.SignerFor(m => PeerIdentity.PeerFor(m)?.Pin, message => PeerIdentity.SignAsync(message)),
            () => DateTimeOffset.UtcNow)
        {
            InfoWait = TimeSpan.FromSeconds(10),
            LaunchWait = TimeSpan.FromSeconds(15),
            TrustSign = trustSigner ?? PeerLaunchClient.TrustSignerFor(m => PeerIdentity.PeerFor(m)?.Pin, message => PeerIdentity.SignAsync(message)),
        });

        PeerSessions.ServeLaunchOn(receiver, null);
        sender.ServeLaunch(null, null, client);

        PeerIdentity.Remember(new PeerIdentity.Peer(PeerIdentity.OwnPin(), "receiver"));
        PeerIdentity.Remember(new PeerIdentity.Peer(PeerIdentity.OwnPin(), MachineNames.Mine()));

        receiver.Link.Listen(0);
        Assert.True(await sender.Link.ConnectAsync("receiver", "127.0.0.1", receiver.Link.BoundPort,
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token));
        return (client, receiver, sender);
    }

    private PeerLaunchSpec Spec => new("claude", _folder, "default");

    [Fact]
    public async Task A_launch_that_lands_on_the_prompt_is_asked_and_cancel_closes_only_that_pane()
    {
        if (OperatingSystem.IsWindows()) return;
        var (client, _, _) = await Pair();

        var asked = await client.LaunchAsync("receiver", Spec, CancellationToken.None);
        Assert.Equal(PeerLaunchCodes.TrustNeeded, asked.Code);
        Assert.Equal(_folder, asked.Trust!.Cwd);
        Assert.Empty(_closed);

        var r = await client.TrustAsync("receiver", asked.Trust, trusted: false, CancellationToken.None);

        Assert.Equal(PeerLaunchCodes.TrustCancelled, r.Code);
        Assert.Equal(["%77"], _closed);
        Assert.Empty(_presses);

        // Single use, across the wire: the question is gone on this desk.
        Assert.Equal(PeerLaunchCodes.TrustGone, (await client.TrustAsync("receiver", asked.Trust, false, CancellationToken.None)).Code);
    }

    [Fact]
    public async Task Another_identity_cannot_answer_and_a_dropped_link_closes_the_question()
    {
        if (OperatingSystem.IsWindows()) return;

        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=stranger", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var stranger = X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);

        var (client, _, sender) = await Pair(PeerLaunchClient.TrustSignerFor(
            m => PeerIdentity.PeerFor(m)?.Pin, message => Task.FromResult(PeerIdentity.Sign(stranger, message))));

        var asked = await client.LaunchAsync("receiver", Spec, CancellationToken.None);
        Assert.Equal(PeerLaunchCodes.TrustNeeded, asked.Code);

        var r = await client.TrustAsync("receiver", asked.Trust!, trusted: true, CancellationToken.None);
        Assert.Equal(RelayReplies.BadSignature, r.Code);
        Assert.Empty(_presses);
        Assert.Empty(_closed);

        // The link drops: that machine's question is closed under the guard.
        sender.Dispose();
        Assert.True(SpinWait.SpinUntil(() => { lock (_closed) return _closed.Count == 1; }, TimeSpan.FromSeconds(10)));
        Assert.Equal(["%77"], _closed);
    }

    // An older desktop sends no can_trust: the far desk closes the pane at once and says why.
    [Fact]
    public async Task A_desktop_that_cannot_ask_gets_launch_failed_and_the_pane_is_closed()
    {
        if (OperatingSystem.IsWindows()) return;
        await Pair();
        var f = new LaunchFields(_folder, "claude", "", "", "", "", RelayCrypto.Base64Url(RandomNumberGenerator.GetBytes(16)),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120, "default");
        var signed = await PeerIdentity.SignAsync(LaunchGate.PeerMessage(PeerIdentity.OwnPin(), LaunchGate.Body(f)));
        var body = PeerLaunchWire.EncodeLaunch(new PeerLaunchWire.LaunchRequest(
            f.Cwd, f.Cli, "", "", f.Account, "", "", f.Nonce, f.ExpiresAt,
            RelayCrypto.Base64Url(signed.Sig), Convert.ToBase64String(signed.CertificateDer)));

        // The frame an older desktop sends: no can_trust, so the far desk cannot ask.
        await _hosts[1].Link.SendAsync("receiver", PeerProtocol.Message(PeerProtocol.Launch, "oldid01", body: body));
        Assert.True(SpinWait.SpinUntil(() => { lock (_closed) return _closed.Count == 1; }, TimeSpan.FromSeconds(10)));
        Assert.Equal(["%77"], _closed);
    }
}
