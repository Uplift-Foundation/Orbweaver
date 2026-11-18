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

    private PeerMirrorHost _sender = null!;

    private async Task<(PeerMirrorHost Receiver, PeerLaunchClient Client)> Pair(
        PeerLaunchInfoSource? info,
        Func<string, JsonElement, Task<PeerLaunchResult>>? handler,
        TimeSpan? infoWait = null,
        TimeSpan? launchWait = null)
    {
        var receiver = new PeerMirrorHost();
        var sender = new PeerMirrorHost();
        _hosts.Add(receiver);
        _hosts.Add(sender);
        _sender = sender;

        var client = new PeerLaunchClient(new PeerLaunchClient.Seams(
            sender.Link.SendAsync,
            () => [new PeerMachine("receiver", sender.Link.IsConnected("receiver"))],
            (_, _) => Task.FromResult<(string, string)?>(("SIG", "CERT")),
            () => Now)
        {
            InfoWait = infoWait ?? TimeSpan.FromSeconds(10),
            LaunchWait = launchWait ?? TimeSpan.FromSeconds(10),
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

    // Nadia's must-fix: a launch waits up to 30 s for the session to check in,
    // and the link delivers one message at a time per connection. If the
    // handler ran on that queue, nothing else from this peer would arrive.
    [Fact]
    public async Task ALaunchThatNeverFinishesDoesNotHoldUpTheNextMessageFromThatPeer()
    {
        var never = new TaskCompletionSource<PeerLaunchResult>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (_, client) = await Pair(Source(true), (_, _) => { entered.TrySetResult(); return never.Task; });

        using var cts = new CancellationTokenSource();
        var stuck = client.LaunchAsync("receiver", new("claude", "/ok", "default"), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Negative control: the launch really is still pending, so the info
        // answer below is not just arriving after it finished.
        Assert.False(stuck.IsCompleted);

        var info = await client.InfoAsync("receiver", CancellationToken.None);

        Assert.True(info!.Allowed);

        cts.Cancel();
        never.TrySetResult(new PeerLaunchResult(true, "launched", "s", null));
    }

    // Nadia's no-unobserved-throw rule for the reply sends. PeerLink.SendAsync
    // swallows a write failure itself, so what can throw inside the info guard
    // is building the info, and that is what this fails. The launch-result
    // guard has no equivalent: the result's encoding cannot throw (a lone
    // surrogate is replaced, not rejected, which a first version of this test
    // found out), so that catch is defensive and unreached.
    [Fact]
    public async Task AnInfoThatCannotBeBuiltCostsOnlyThatAnswer()
    {
        var boom = true;
        var source = new PeerLaunchInfoSource(new PeerLaunchInfoSource.Seams(
            () => boom ? throw new InvalidOperationException("boom") : true,
            () => "macos", () => "/Users/a", () => true, _ => true, () => [], () => []));
        var (_, client) = await Pair(source, Decide, infoWait: TimeSpan.FromMilliseconds(400));

        Assert.Null(await client.InfoAsync("receiver", CancellationToken.None));

        // Control: with the fault gone the same query is answered, and a launch
        // on the same connection still gets through.
        boom = false;
        Assert.True((await client.InfoAsync("receiver", CancellationToken.None))!.Allowed);
        Assert.Equal("launched",
            (await client.LaunchAsync("receiver", new("claude", "/ok", "default"), CancellationToken.None)).Code);
    }

    [Fact]
    public async Task ADesksWithNoClientIgnoresAReplyAndAHangUpWithoutFailing()
    {
        var (receiver, client) = await Pair(Source(true), Decide);
        var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Link.PeerDisconnected += _ => gone.TrySetResult();

        // The receiver has no launch client bound, as a desk that only answers.
        // Stray replies reach it from a connected, named peer: dropped quietly.
        Assert.True(await _sender.Link.SendAsync("receiver", PeerProtocol.Message(PeerProtocol.LaunchInfo, "stray")));
        Assert.True(await _sender.Link.SendAsync("receiver", PeerProtocol.Message(PeerProtocol.LaunchResult, "stray")));

        // Control that the connection still carries messages afterwards.
        Assert.Equal("launched",
            (await client.LaunchAsync("receiver", new("claude", "/ok", "default"), CancellationToken.None)).Code);

        // Hanging up fires PeerDisconnected on a receiver with no client to tell.
        _sender.Link.Drop("receiver");
        await gone.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
