using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

public class PeerLaunchClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly PeerLaunchSpec Spec = new("claude", "/w", "default");

    private sealed class Rig
    {
        public readonly List<(string Machine, PeerProtocol.PeerMessage Message)> Sent = new();
        public bool SendResult = true;
        public (string, string)? Signature = ("SIG", "CERT");
        public PeerLaunchClient Client;
        public Action<string, PeerProtocol.PeerMessage>? OnSend;

        public Rig(TimeSpan? info = null, TimeSpan? launch = null)
        {
            Client = new PeerLaunchClient(new PeerLaunchClient.Seams(
                (m, msg) => { Sent.Add((m, msg)); OnSend?.Invoke(m, msg); return Task.FromResult(SendResult); },
                () => [new PeerMachine("mini", true), new PeerMachine("pc", false)],
                (_, _) => Task.FromResult(Signature is { } s ? ((string, string)?)s : null),
                () => Now)
            {
                InfoWait = info ?? TimeSpan.FromSeconds(10),
                LaunchWait = launch ?? TimeSpan.FromSeconds(10),
                NewNonce = () => "NONCE",
            });
        }

        // Answers whatever was just sent, from the given machine.
        public void Reply(string from, string type, JsonElement body) =>
            Client.OnReply(from, PeerProtocol.Message(type, Sent[^1].Message.Id, body: body));
    }

    [Fact]
    public void MachinesAreWhatTheLinkSays()
    {
        Assert.Equal([new PeerMachine("mini", true), new PeerMachine("pc", false)], new Rig().Client.Machines());
    }

    [Fact]
    public async Task InfoIsAskedOfTheMachineAndAnsweredByIt()
    {
        var rig = new Rig();
        rig.OnSend = (_, _) => rig.Reply("mini", PeerProtocol.LaunchInfo,
            PeerLaunchWire.EncodeInfo(new PeerLaunchInfo(true, "windows")));

        var info = await rig.Client.InfoAsync("mini", CancellationToken.None);

        Assert.Equal("windows", info!.Os);
        var (machine, message) = Assert.Single(rig.Sent);
        Assert.Equal("mini", machine);
        Assert.Equal(PeerProtocol.LaunchInfoGet, message.Type);
        Assert.Null(message.Body);
    }

    [Fact]
    public async Task AnInfoThatNeverComesIsNoAnswer()
    {
        var rig = new Rig(info: TimeSpan.FromMilliseconds(50));

        Assert.Null(await rig.Client.InfoAsync("mini", CancellationToken.None));
    }

    [Fact]
    public async Task ACancelledInfoIsNoAnswer()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource(50);

        Assert.Null(await rig.Client.InfoAsync("mini", cts.Token));
    }

    [Fact]
    public async Task AnInfoThatCannotBeSentIsNoAnswer()
    {
        var rig = new Rig { SendResult = false };

        Assert.Null(await rig.Client.InfoAsync("mini", CancellationToken.None));
    }

    [Fact]
    public async Task AnUnreadableInfoIsNoAnswer()
    {
        var rig = new Rig();
        rig.OnSend = (_, _) => rig.Reply("mini", PeerProtocol.LaunchInfo, JsonSerializer.SerializeToElement("junk"));

        Assert.Null(await rig.Client.InfoAsync("mini", CancellationToken.None));
    }

    [Fact]
    public async Task AReplyOfTheWrongTypeIsNotTakenForTheAnswer()
    {
        var rig = new Rig(info: TimeSpan.FromSeconds(2));
        rig.OnSend = (_, _) => rig.Reply("mini", PeerProtocol.LaunchResult,
            PeerLaunchWire.EncodeResult(new PeerLaunchResult(true, "launched", "s", null)));

        Assert.Null(await rig.Client.InfoAsync("mini", CancellationToken.None));
    }

    [Fact]
    public async Task AReplyFromAnotherMachineIsIgnored()
    {
        var rig = new Rig(info: TimeSpan.FromMilliseconds(200));
        rig.OnSend = (_, _) => rig.Reply("pc", PeerProtocol.LaunchInfo,
            PeerLaunchWire.EncodeInfo(new PeerLaunchInfo(true, "macos")));

        // Negative control: the same reply from the machine asked is accepted
        // (InfoIsAskedOfTheMachineAndAnsweredByIt), so this null is the
        // machine check and not a broken rig.
        Assert.Null(await rig.Client.InfoAsync("mini", CancellationToken.None));
    }

    [Fact]
    public async Task AMachineNameMatchesWithoutRegardToCase()
    {
        var rig = new Rig();
        rig.OnSend = (_, _) => rig.Reply("MINI", PeerProtocol.LaunchInfo,
            PeerLaunchWire.EncodeInfo(new PeerLaunchInfo(true, "macos")));

        Assert.NotNull(await rig.Client.InfoAsync("mini", CancellationToken.None));
    }

    [Fact]
    public void AReplyWithAnUnknownIdIsIgnored()
    {
        new Rig().Client.OnReply("mini", PeerProtocol.Message(PeerProtocol.LaunchInfo, "nobody"));
    }

    [Fact]
    public async Task ALaunchIsSignedAndCarriesTheSpec()
    {
        var rig = new Rig();
        rig.OnSend = (_, _) => rig.Reply("mini", PeerProtocol.LaunchResult,
            PeerLaunchWire.EncodeResult(new PeerLaunchResult(true, "launched", "sid", null)));

        var result = await rig.Client.LaunchAsync("mini", Spec, CancellationToken.None);

        Assert.Equal(new PeerLaunchResult(true, "launched", "sid", null), result);
        var sent = PeerLaunchWire.ParseLaunch(rig.Sent[0].Message.Body!.Value)!;
        Assert.Equal(PeerProtocol.Launch, rig.Sent[0].Message.Type);
        Assert.Equal(("/w", "claude", "default"), (sent.Cwd, sent.Cli, sent.Account));
        Assert.Equal(("NONCE", "SIG", "CERT"), (sent.Nonce, sent.Sig, sent.Cert));
        Assert.Equal(Now.AddSeconds(120).ToUnixTimeSeconds(), sent.ExpiresAt);
        Assert.Equal(("", "", "", ""), (sent.Persona, sent.Model, sent.ParentSessionId, sent.FirstMessage));
    }

    [Fact]
    public async Task ALaunchThisDeskCannotSignIsRefusedWithoutSendingAnything()
    {
        var rig = new Rig { Signature = null };

        var result = await rig.Client.LaunchAsync("mini", Spec, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(RelayReplies.BadSignature, result.Code);
        Assert.Empty(rig.Sent);
    }

    [Theory]
    [InlineData("no_terminal")]
    [InlineData("peer_launch_off")]
    [InlineData("unknown_cwd")]
    public async Task APeersRefusalComesBackAsItWasSent(string code)
    {
        var rig = new Rig();
        rig.OnSend = (_, _) => rig.Reply("mini", PeerProtocol.LaunchResult,
            PeerLaunchWire.EncodeResult(new PeerLaunchResult(false, code, null, "why")));

        Assert.Equal(new PeerLaunchResult(false, code, null, "why"),
            await rig.Client.LaunchAsync("mini", Spec, CancellationToken.None));
    }

    [Fact]
    public async Task ALaunchThatNeverGetsAReplyIsNoAnswer()
    {
        var rig = new Rig(launch: TimeSpan.FromMilliseconds(50));

        var result = await rig.Client.LaunchAsync("mini", Spec, CancellationToken.None);

        Assert.Equal(PeerLaunchCodes.NoAnswer, result.Code);
    }

    [Fact]
    public async Task ALaunchThatCannotBeSentIsLinkLost()
    {
        var rig = new Rig { SendResult = false };

        Assert.Equal(PeerLaunchCodes.LinkLost,
            (await rig.Client.LaunchAsync("mini", Spec, CancellationToken.None)).Code);
    }

    [Fact]
    public async Task AnUnreadableResultIsNoAnswer()
    {
        var rig = new Rig();
        rig.OnSend = (_, _) => rig.Reply("mini", PeerProtocol.LaunchResult, JsonSerializer.SerializeToElement("junk"));

        var result = await rig.Client.LaunchAsync("mini", Spec, CancellationToken.None);

        Assert.Equal(PeerLaunchCodes.NoAnswer, result.Code);
    }

    [Fact]
    public async Task ADroppedLinkFailsEveryPendingRequestOnThatMachineAtOnce()
    {
        var rig = new Rig(info: TimeSpan.FromSeconds(30), launch: TimeSpan.FromSeconds(30));
        var sent = 0;
        rig.OnSend = (_, _) => { if (Interlocked.Increment(ref sent) == 2) rig.Client.OnDisconnected("mini"); };

        var info = rig.Client.InfoAsync("mini", CancellationToken.None);
        var launch = await rig.Client.LaunchAsync("mini", Spec, CancellationToken.None);

        Assert.Equal(PeerLaunchCodes.LinkLost, launch.Code);
        Assert.Null(await info.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ADropOnAnotherMachineLeavesThisRequestAlone()
    {
        var rig = new Rig();
        rig.OnSend = (_, _) =>
        {
            rig.Client.OnDisconnected("pc");
            rig.Reply("mini", PeerProtocol.LaunchInfo, PeerLaunchWire.EncodeInfo(new PeerLaunchInfo(true, "macos")));
        };

        Assert.NotNull(await rig.Client.InfoAsync("mini", CancellationToken.None));
    }

    [Fact]
    public async Task ANewRequestForgetsAnEarlierDrop()
    {
        var rig = new Rig(launch: TimeSpan.FromMilliseconds(50));
        rig.Client.OnDisconnected("mini");

        // The link came back; this one simply is not answered.
        Assert.Equal(PeerLaunchCodes.NoAnswer,
            (await rig.Client.LaunchAsync("mini", Spec, CancellationToken.None)).Code);
    }

    [Fact]
    public void TheRealNonceIsSixteenBytesOfBase64Url()
    {
        var seams = new PeerLaunchClient.Seams((_, _) => Task.FromResult(true), () => [],
            (_, _) => Task.FromResult<(string, string)?>(null), () => Now);

        var a = seams.NewNonce();

        Assert.Equal(22, a.Length);
        Assert.DoesNotContain('=', a);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.NotEqual(a, seams.NewNonce());
    }
}
