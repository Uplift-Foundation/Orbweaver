using System.Text.Json;
using Xunit;

namespace Orbweaver.IntegrationTests;

// CB-357: two real PeerMirrorHosts over loopback TLS, one asking the other to
// start a session. What this reaches that the unit suites cannot is the routing
// in PeerMirrorHost.DeliverAsync and the real link under it: a reply crossing a
// socket, the greeting naming the inbound connection, a dropped link failing a
// pending request.
//
// The receiving *decision* is a fake here, keyed on the folder asked for, so
// each refusal code is shown to survive the trip and be surfaced by the
// client. The real gate is covered by its own suite.
[Collection("Settings")]
public class PeerLaunchLoopbackTests : IDisposable
{
    private readonly List<PeerMirrorHost> _hosts = new();

    public void Dispose()
    {
        foreach (var host in _hosts) host.Dispose();
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static readonly PeerLaunchInfo Info = new(
        true, "macos", "/Users/a", true,
        [new PeerLaunchCli("claude", true)],
        [new PeerLaunchAccount("claude", "default", "Default")],
        ["/Users/a/work"]);

    private async Task<(PeerMirrorHost Receiver, PeerLaunchClient Client)> Pair(
        PeerLaunchInfoSource? info,
        Func<string, JsonElement, Task<PeerLaunchResult>>? handler,
        TimeSpan? infoWait = null)
    {
        var receiver = new PeerMirrorHost();
        var sender = new PeerMirrorHost();
        _hosts.Add(receiver);
        _hosts.Add(sender);

        var client = new PeerLaunchClient(new PeerLaunchClient.Seams(
            sender.Link.SendAsync,
            () => [new PeerMachine("receiver", sender.Link.IsConnected("receiver"))],
            (_, _) => Task.FromResult<(string, string)?>(("SIG", "CERT")),
            () => Now)
        {
            InfoWait = infoWait ?? TimeSpan.FromSeconds(10),
            LaunchWait = TimeSpan.FromSeconds(10),
        });

        receiver.ServeLaunch(info, handler, null);
        sender.ServeLaunch(null, null, client);

        // Each side pairs with the other by its own pin: one process, one identity.
        PeerIdentity.Remember(new PeerIdentity.Peer(PeerIdentity.OwnPin(), "receiver"));
        PeerIdentity.Remember(new PeerIdentity.Peer(PeerIdentity.OwnPin(), MachineNames.Mine()));

        receiver.Link.Listen(0);
        Assert.True(await sender.Link.ConnectAsync("receiver", "127.0.0.1", receiver.Link.BoundPort,
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token));

        return (receiver, client);
    }

    private static PeerLaunchInfoSource Source(bool allowed) => new(new PeerLaunchInfoSource.Seams(
        () => allowed, () => "macos", () => "/Users/a", () => true, _ => true,
        () => [new RelayAccounts.Account("claude", "default", "Default")],
        () => ["/Users/a/work"]));

    private static Task<PeerLaunchResult> Decide(string machine, JsonElement body)
    {
        var request = PeerLaunchWire.ParseLaunch(body);
        if (request is null) return Task.FromResult(new PeerLaunchResult(false, RelayReplies.BadFrame, null, null));

        // The folder asked for names the outcome, so one fake covers every code.
        return Task.FromResult(request.Cwd switch
        {
            "/ok" => new PeerLaunchResult(true, PeerLaunchCodes.Launched, "sid-1", null),
            var code => new PeerLaunchResult(false, code.TrimStart('/'), null, code == "/no_terminal" ? "tmux is not installed" : null),
        });
    }

    [Fact]
    public async Task InfoAndLaunchRoundTripOverTheRealLink()
    {
        var (_, client) = await Pair(Source(true), Decide);

        var info = await client.InfoAsync("receiver", CancellationToken.None);

        Assert.True(info!.Allowed);
        Assert.Equal("macos", info.Os);
        Assert.Equal(["/Users/a/work"], info.Recents);
        Assert.Equal([new PeerLaunchAccount("claude", "default", "Default")], info.Accounts);

        var result = await client.LaunchAsync("receiver", new("claude", "/ok", "default"), CancellationToken.None);

        Assert.Equal(new PeerLaunchResult(true, "launched", "sid-1", null), result);
        Assert.Equal([new PeerMachine("receiver", true)], client.Machines());
    }

    [Theory]
    [InlineData("peer_launch_off")]
    [InlineData("bad_signature")]
    [InlineData("replayed")]
    [InlineData("expired")]
    [InlineData("unknown_cwd")]
    [InlineData("no_terminal")]
    [InlineData("no_session")]
    public async Task EachRefusalCodeSurvivesTheTripAndIsSurfaced(string code)
    {
        var (_, client) = await Pair(Source(true), Decide);

        var result = await client.LaunchAsync("receiver", new("claude", "/" + code, "default"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(code, result.Code);
        Assert.Equal(code == "no_terminal" ? "tmux is not installed" : null, result.Why);
    }

    [Fact]
    public async Task ADeskWithTheSwitchOffAnswersInfoWithNothingButItsOs()
    {
        var (_, client) = await Pair(Source(false), Decide);

        Assert.Equal(new PeerLaunchInfo(false, "macos"), await client.InfoAsync("receiver", CancellationToken.None));
    }

    [Fact]
    public async Task APeerThatNeverAnswersInfoIsNoAnswer()
    {
        // No info source bound: what an older build looks like on the wire.
        var (_, client) = await Pair(null, Decide, infoWait: TimeSpan.FromMilliseconds(300));

        Assert.Null(await client.InfoAsync("receiver", CancellationToken.None));
    }

    [Fact]
    public async Task ALaunchToADeskWithNoHandlerIsAnsweredAsSwitchedOff()
    {
        var (_, client) = await Pair(Source(true), handler: null);

        Assert.Equal(PeerLaunchCodes.PeerLaunchOff,
            (await client.LaunchAsync("receiver", new("claude", "/ok", "default"), CancellationToken.None)).Code);
    }

    [Fact]
    public async Task AHandlerThatThrowsStillAnswersLaunchFailed()
    {
        var (_, client) = await Pair(Source(true), (_, _) => throw new InvalidOperationException("boom"));

        Assert.Equal(RelayReplies.LaunchFailed,
            (await client.LaunchAsync("receiver", new("claude", "/ok", "default"), CancellationToken.None)).Code);
    }
}
