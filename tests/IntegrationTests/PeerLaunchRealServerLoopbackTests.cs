using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Orbweaver.IntegrationTests;

// CB-357: the real receiving server behind the real wiring
// (PeerSessions.ServeLaunchOn), two hosts over loopback TLS, the real signer.
//
// Every case here is refused by the gate before anything is started, on
// purpose: a launch that passes would start a real terminal on whoever runs
// the suite. The launched path is the server's own suite's business, behind its
// seams. What this proves is that the switch, the signature, the pairing, the
// nonce window and the expiry each hold across the wire, and that the switch
// takes effect live.
[Collection("Settings")]
public class PeerLaunchRealServerLoopbackTests : IDisposable
{
    private readonly List<PeerMirrorHost> _hosts = new();
    private readonly bool _saved = OrbweaverSettings.PeerLaunchInboundEnabled;

    // The real server (PeerLaunchServer.Shared, through the production wiring)
    // with one thing pinned: which CLIs are installed. The gate checks the CLI
    // before the folder, so without this a runner with no claude answers
    // unknown_cli where a developer's machine answers unknown_cwd. Every case
    // is refused by the gate, so no launch seam is ever reached.
    //
    // The other pin is the start itself: Shared's start goes through
    // LaunchRunner.DefaultLaunchSeams, and these seams count and refuse, so a
    // gate bug that let a launch through fails Dispose's zero-start assertion
    // instead of opening a terminal on whoever ran the suite.
    private int _started;

    public PeerLaunchRealServerLoopbackTests()
    {
        NewChatLauncher.LocateFreshForTests = cli => cli == NewChatCli.ClaudeCode
            ? (OperatingSystem.IsWindows() ? "C:\\c\\claude.exe" : "/bin/claude")
            : null;
        LaunchRunner.StartForTests = new LaunchRunner.StartOverride(
            (_, _) => { Interlocked.Increment(ref _started); return (null, "refused by the test"); },
            _ => { Interlocked.Increment(ref _started); return null; });
    }

    public void Dispose()
    {
        var started = Volatile.Read(ref _started);
        NewChatLauncher.LocateFreshForTests = null;
        LaunchRunner.StartForTests = null;
        Assert.Equal(0, started);
        OrbweaverSettings.PeerLaunchInboundEnabled = _saved;
        foreach (var host in _hosts) host.Dispose();
    }

    private static readonly PeerLaunchSpec Missing = new("claude", "/orbweaver-cb357/does/not/exist", "default");

    private async Task<PeerLaunchClient> Pair(
        DateTimeOffset? now = null,
        PeerLaunchClient.Signer? signer = null,
        string? nonce = null)
    {
        var receiver = new PeerMirrorHost();
        var sender = new PeerMirrorHost();
        _hosts.Add(receiver);
        _hosts.Add(sender);

        var seams = new PeerLaunchClient.Seams(
            sender.Link.SendAsync,
            () => [new PeerMachine("receiver", true)],
            signer ?? PeerLaunchClient.SignerFor(m => PeerIdentity.PeerFor(m)?.Pin,
                message => PeerIdentity.SignAsync(message)),
            () => now ?? DateTimeOffset.UtcNow)
        {
            InfoWait = TimeSpan.FromSeconds(10),
            LaunchWait = TimeSpan.FromSeconds(10),
        };
        if (nonce is not null) seams = seams with { NewNonce = () => nonce };

        var client = new PeerLaunchClient(seams);

        // The production wiring, not a copy of it.
        PeerSessions.ServeLaunchOn(receiver, null);
        sender.ServeLaunch(null, null, client);

        PeerIdentity.Remember(new PeerIdentity.Peer(PeerIdentity.OwnPin(), "receiver"));
        PeerIdentity.Remember(new PeerIdentity.Peer(PeerIdentity.OwnPin(), MachineNames.Mine()));

        receiver.Link.Listen(0);
        Assert.True(await sender.Link.ConnectAsync("receiver", "127.0.0.1", receiver.Link.BoundPort,
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token));

        return client;
    }

    private static Task<PeerLaunchResult> Launch(PeerLaunchClient c) =>
        c.LaunchAsync("receiver", Missing, CancellationToken.None);

    [Fact]
    public async Task TheSwitchTakesEffectLiveOffThenOnThenOff()
    {
        var client = await Pair();

        OrbweaverSettings.PeerLaunchInboundEnabled = false;
        Assert.Equal(PeerLaunchCodes.PeerLaunchOff, (await Launch(client)).Code);
        Assert.Equal(new PeerLaunchInfo(false, OperatingSystem.IsWindows() ? "windows" : "macos"),
            await client.InfoAsync("receiver", CancellationToken.None));

        // On: the same request now gets past the switch and is stopped by the
        // gate for the folder, which is the control that the first answer was
        // the switch and not a broken pairing or signature.
        OrbweaverSettings.PeerLaunchInboundEnabled = true;
        Assert.Equal(RelayReplies.UnknownCwd, (await Launch(client)).Code);
        var info = await client.InfoAsync("receiver", CancellationToken.None);
        Assert.True(info!.Allowed);
        Assert.NotNull(info.Recents);

        OrbweaverSettings.PeerLaunchInboundEnabled = false;
        Assert.Equal(PeerLaunchCodes.PeerLaunchOff, (await Launch(client)).Code);
        Assert.False((await client.InfoAsync("receiver", CancellationToken.None))!.Allowed);
    }

    [Fact]
    public async Task ACertificateThatIsNotThePairedOneIsBadSignature()
    {
        OrbweaverSettings.PeerLaunchInboundEnabled = true;

        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=stranger", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var stranger = X509CertificateLoader.LoadPkcs12(
            made.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);

        var client = await Pair(signer: PeerLaunchClient.SignerFor(
            m => PeerIdentity.PeerFor(m)?.Pin, message => Task.FromResult(PeerIdentity.Sign(stranger, message))));

        Assert.Equal(RelayReplies.BadSignature, (await Launch(client)).Code);
    }

    [Fact]
    public async Task AnExpiredLaunchIsRefused()
    {
        OrbweaverSettings.PeerLaunchInboundEnabled = true;

        var client = await Pair(now: DateTimeOffset.UtcNow.AddHours(-2));

        Assert.Equal(RelayReplies.Expired, (await Launch(client)).Code);
    }

    [Fact]
    public async Task ASecondLaunchWithTheSameNonceIsReplayed()
    {
        OrbweaverSettings.PeerLaunchInboundEnabled = true;

        // 16 bytes, base64url, unique to this run so a shared server's window
        // from another test cannot have seen it.
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var client = await Pair(nonce: nonce);

        // First is refused for the folder, which is after the nonce is spent.
        Assert.Equal(RelayReplies.UnknownCwd, (await Launch(client)).Code);
        Assert.Equal(RelayReplies.Replayed, (await Launch(client)).Code);
    }

    // The other half of the same ordering: with the CLI missing, the gate
    // says so before it ever looks at the folder.
    [Fact]
    public async Task AMissingCliIsRefusedAsUnknownCliBeforeTheFolderIsLookedAt()
    {
        OrbweaverSettings.PeerLaunchInboundEnabled = true;
        NewChatLauncher.LocateFreshForTests = _ => null;

        var client = await Pair();

        Assert.Equal(RelayReplies.UnknownCli, (await Launch(client)).Code);
    }

    // The control for every zero-start assertion in this class: without it,
    // "no starts" would also hold if the override were never wired in. The
    // seams are built by the same method Shared's come from, and one start is
    // invoked through whichever of the two this platform uses.
    [Fact]
    public void TheStartCounterIsLive()
    {
        var seams = LaunchRunner.DefaultLaunchSeams();

        if (OperatingSystem.IsWindows()) seams.StartWindows(new System.Diagnostics.ProcessStartInfo("claude"));
        else seams.PlaceMac("claude", "/w");

        Assert.Equal(1, Volatile.Read(ref _started));

        // Put back, so Dispose's zero-start assertion judges the rest of the
        // class and not this deliberate call.
        Interlocked.Exchange(ref _started, 0);
    }
}
