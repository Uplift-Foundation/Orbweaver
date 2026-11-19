using System.Text.Json;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-357: what PeerMirrorHost does with the four launch messages from a
// machine that is not a connected, named peer. The answer is nothing at all —
// no reply, no handler call, no info built — like every other peer message.
public class PeerLaunchHostRoutingTests
{
    [Fact]
    public async Task AStrangerGetsNoInfoNoLaunchAndNoReplyDelivery()
    {
        using var host = new PeerMirrorHost();
        var built = 0;
        var handled = 0;
        var delivered = 0;

        var info = new PeerLaunchInfoSource(new PeerLaunchInfoSource.Seams(
            () => { built++; return true; }, () => "macos", () => null, () => true, _ => true, () => [], () => []));
        var client = new PeerLaunchClient(new PeerLaunchClient.Seams(
            (_, _) => { delivered++; return Task.FromResult(true); }, () => [],
            (_, _) => Task.FromResult<(string, string)?>(null), () => DateTimeOffset.UtcNow));
        host.ServeLaunch(info, (_, _) => { handled++; return Task.FromResult(new PeerLaunchResult(true, "launched", "s", null)); }, client);

        foreach (var type in new[]
            { PeerProtocol.LaunchInfoGet, PeerProtocol.Launch, PeerProtocol.LaunchInfo, PeerProtocol.LaunchResult })
            await host.DeliverAsync("ghost", PeerProtocol.Message(type, "id1", body: JsonSerializer.SerializeToElement(new { })));

        Assert.Equal((0, 0, 0), (built, handled, delivered));
        Assert.Same(client, host.LaunchClient);
    }

    [Fact]
    public async Task AnUnboundHostIgnoresTheLaunchMessages()
    {
        using var host = new PeerMirrorHost();

        Assert.Null(host.LaunchClient);

        foreach (var type in new[]
            { PeerProtocol.LaunchInfoGet, PeerProtocol.Launch, PeerProtocol.LaunchInfo, PeerProtocol.LaunchResult })
            await host.DeliverAsync("ghost", PeerProtocol.Message(type, "id1"));
    }
}

public class PeerSessionsMachinesTests
{
    [Fact]
    public void PairedMachinesAreListedByNameWithTheirLinkState()
    {
        var machines = PeerSessions.MachinesFrom(
            ["pc", "Mini", "(inbound 10.0.0.9)"], name => name == "Mini");

        // Sorted without regard to case; an unnamed inbound entry is not a machine.
        Assert.Equal([new PeerMachine("Mini", true), new PeerMachine("pc", false)], machines);
    }

    [Fact]
    public void NobodyPairedIsNoMachines() =>
        Assert.Empty(PeerSessions.MachinesFrom([], _ => true));
}

public class PeerSessionsLauncherTests
{
    [Fact]
    public void TheLauncherIsNullUntilALinkIsRunningAndIsTheHostsClientAfter()
    {
        PeerSessions.UseClientForTests(null);
        Assert.Null(PeerSessions.Launcher);

        try
        {
            // The stand-in host a test installs is a real host with no launch
            // client until one is served onto it.
            PeerSessions.UseClientForTests(new RemoteMirrorClient(
                ".claude", new RemoteMirrorClient.Seams((_, _) => Task.FromResult(true))));
            Assert.Null(PeerSessions.Launcher);

            var launch = new PeerLaunchClient(new PeerLaunchClient.Seams(
                (_, _) => Task.FromResult(true), () => [],
                (_, _) => Task.FromResult<(string, string)?>(null), () => DateTimeOffset.UtcNow));
            PeerSessions.Host!.ServeLaunch(null, null, launch);

            Assert.Same(launch, PeerSessions.Launcher);
        }
        finally
        {
            PeerSessions.UseClientForTests(null);
        }
    }
}
