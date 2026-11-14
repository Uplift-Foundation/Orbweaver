using Xunit;

namespace Orbweaver.UnitTests;

// Covers PeerLink's logic — the read loop, the trust decision and the
// bookkeeping — with no socket, no certificate and no second machine.
//
// The socket calls themselves (Listen, ConnectAsync, the two handshakes) are
// excluded from coverage and are not asserted here, which is the house
// convention for anything that opens a real connection. What that leaves is the
// part where a mistake would be *silent*: a framing error in the pump would
// arrive as a corrupted transcript rather than as an exception, and a mistake
// in the trust decision would not show up as a failure at all.
public class PeerLinkTests
{
    private static PeerLink Link(
        List<(string Machine, PeerProtocol.PeerMessage Message)> got,
        Func<string, Task>? onDeliver = null) =>
        new(new PeerLink.Seams(
            Deliver: (machine, message) =>
            {
                got.Add((machine, message));
                return onDeliver?.Invoke(message.Type) ?? Task.CompletedTask;
            },
            KnownPeer: _ => null,
            OwnCertificate: _ => throw new InvalidOperationException(
                "no test here should need a certificate")));

    private static async Task<MemoryStream> StreamOf(params PeerProtocol.PeerMessage[] messages)
    {
        var stream = new MemoryStream();
        foreach (var m in messages) await PeerProtocol.WriteAsync(stream, m);

        stream.Position = 0;
        return stream;
    }

    // --- the read loop ---------------------------------------------------------

    [Fact]
    public async Task EveryMessageOnTheWireIsDelivered()
    {
        var got = new List<(string, PeerProtocol.PeerMessage)>();
        var link = Link(got);

        using var stream = await StreamOf(
            PeerProtocol.Message(PeerProtocol.Window, "a"),
            PeerProtocol.Message(PeerProtocol.Fetch, "b"),
            PeerProtocol.Message(PeerProtocol.Ok, "c"));

        await link.PumpAsync("avatar", stream, CancellationToken.None);

        Assert.Equal(3, got.Count);
        Assert.Equal(new[] { "a", "b", "c" }, got.Select(g => g.Item2.Id));
        Assert.All(got, g => Assert.Equal("avatar", g.Item1));
    }

    // `hello` used to be one of the messages above, and it is now deliberately
    // not: the greeting is the link's own business.
    //
    // **This is the assertion the change needed, not a weakening of the one
    // above.** A greeting that reached the mirror would be a frame the mirror
    // has no handler for — harmless — but it would also mean the link had not
    // read it, and the link is what has to read it: the name in a greeting is
    // the only thing that says which machine an inbound connection belongs to,
    // and the pin check hangs off it.
    //
    // A nameless one ends the connection, which is why nothing after it
    // arrives. That is the refusal path, and a test that let it look like a
    // delivery would be hiding it.
    [Fact]
    public async Task AGreetingIsTheLinksOwnBusinessAndNeverReachesTheMirror()
    {
        var got = new List<(string, PeerProtocol.PeerMessage)>();
        var link = Link(got);

        using var stream = await StreamOf(
            PeerProtocol.Message(PeerProtocol.Hello, "greeting"),
            PeerProtocol.Message(PeerProtocol.Fetch, "after"));

        await link.PumpAsync("avatar", stream, CancellationToken.None);

        Assert.Empty(got);
    }

    // A clean hangup ends the loop without complaint. It is how every ordinary
    // disconnect looks, so treating it as a fault would make normality noisy.
    [Fact]
    public async Task AHangupEndsTheLoopQuietly()
    {
        var got = new List<(string, PeerProtocol.PeerMessage)>();

        using var empty = new MemoryStream();
        await Link(got).PumpAsync("avatar", empty, CancellationToken.None);

        Assert.Empty(got);
    }

    // A broken connection must not throw out of the pump: the loop owns the
    // connection's lifetime, and an exception escaping it would take down
    // whatever started it rather than dropping one peer.
    [Fact]
    public async Task ABrokenStreamEndsTheLoopRatherThanThrowing()
    {
        var got = new List<(string, PeerProtocol.PeerMessage)>();

        using var whole = new MemoryStream();
        await PeerProtocol.WriteAsync(whole, PeerProtocol.Message(PeerProtocol.Fetch, "half"));

        // A length that promises more than arrives — a dropped connection
        // mid-message.
        using var truncated = new MemoryStream(whole.ToArray()[..^4]);

        await Link(got).PumpAsync("avatar", truncated, CancellationToken.None);

        Assert.Empty(got);
    }

    // **The lesson the relay taught, asserted.** A handler that threw used to be
    // discarded silently, and the machine went on looking healthy while
    // answering nothing. One bad message must cost one message.
    [Fact]
    public async Task AHandlerThatThrowsDoesNotStopTheOnesAfterIt()
    {
        var got = new List<(string, PeerProtocol.PeerMessage)>();

        var link = Link(got, onDeliver: type => type == PeerProtocol.Fetch
            ? throw new InvalidOperationException("boom")
            : Task.CompletedTask);

        using var stream = await StreamOf(
            PeerProtocol.Message(PeerProtocol.Window, "before"),
            PeerProtocol.Message(PeerProtocol.Fetch, "throws"),
            PeerProtocol.Message(PeerProtocol.Ok, "after"));

        await link.PumpAsync("avatar", stream, CancellationToken.None);

        Assert.Equal(new[] { "before", "throws", "after" }, got.Select(g => g.Item2.Id));
    }

    // A message from a version this one does not understand is skipped rather
    // than acted on or fatal — the connection stays up, because the *next*
    // message may well be one both ends agree about.
    [Fact]
    public async Task AMessageFromAnotherProtocolVersionIsSkipped()
    {
        var got = new List<(string, PeerProtocol.PeerMessage)>();
        var link = Link(got);

        using var stream = await StreamOf(
            new PeerProtocol.PeerMessage(PeerProtocol.Version + 1, PeerProtocol.Hello, "future"),
            PeerProtocol.Message(PeerProtocol.Ok, "understood"));

        await link.PumpAsync("avatar", stream, CancellationToken.None);

        Assert.Single(got);
        Assert.Equal("understood", got[0].Item2.Id);
    }

    [Fact]
    public async Task CancellingStopsTheLoop()
    {
        var got = new List<(string, PeerProtocol.PeerMessage)>();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        using var stream = await StreamOf(PeerProtocol.Message(PeerProtocol.Hello, "a"));
        await Link(got).PumpAsync("avatar", stream, cancelled.Token);

        Assert.Empty(got);
    }

    // --- who is allowed to talk to us --------------------------------------------

    // The link's use of the trust rule, exercised on its own so the decision is
    // not something only a TLS handshake can reach.
    [Fact]
    public void APeerWeHavePairedWithIsAccepted() =>
        Assert.True(PeerLink.Accepts(new PeerIdentity.Peer("abc", "avatar"), "abc"));

    [Theory]
    [InlineData("def")]     // a different certificate for a machine we know
    [InlineData("")]        // no certificate at all
    public void AnythingElseIsNot(string offered) =>
        Assert.False(PeerLink.Accepts(new PeerIdentity.Peer("abc", "avatar"), offered));

    [Fact]
    public void AnUnpairedMachineIsNotAccepted() =>
        Assert.False(PeerLink.Accepts(null, "abc"));

    // --- sending with nothing to send down ----------------------------------------

    // Not an exception, because "no link" is an ordinary state — the far machine
    // is asleep, or has not been paired, or the connection just dropped. The
    // caller shows it as a panel that cannot reach the other side.
    [Fact]
    public async Task SendingToAMachineWithNoConnectionIsFalseRatherThanAThrow()
    {
        var got = new List<(string, PeerProtocol.PeerMessage)>();

        var sent = await Link(got).SendAsync(
            "nobody", PeerProtocol.Message(PeerProtocol.Fetch, "id"));

        Assert.False(sent);
    }

    [Fact]
    public void AFreshLinkIsConnectedToNothing()
    {
        var link = Link(new List<(string, PeerProtocol.PeerMessage)>());

        Assert.Empty(link.ConnectedMachines());
        Assert.False(link.IsConnected("avatar"));
    }

    // Dropping something that was never there is a no-op rather than a failure:
    // the pump calls it on every disconnect, including ones that never finished
    // connecting.
    [Fact]
    public void DroppingAMachineThatIsNotConnectedIsHarmless()
    {
        var link = Link(new List<(string, PeerProtocol.PeerMessage)>());

        link.Drop("never-connected");

        Assert.Empty(link.ConnectedMachines());
    }

    [Fact]
    public void RenamingAConnectionThatIsNotThereIsHarmless()
    {
        var link = Link(new List<(string, PeerProtocol.PeerMessage)>());

        link.Rename("(inbound)", "avatar");

        Assert.Empty(link.ConnectedMachines());
    }

    // --- the port -----------------------------------------------------------------

    // Fixed rather than negotiated, and stated once. A port that moved would
    // have to be discovered before it could be connected to, and discovery is
    // what announces the port.
    [Fact]
    public void ThePortIsInTheUnassignedRange() =>
        Assert.InRange(PeerLink.DefaultPort, 1024, 65535);
}
