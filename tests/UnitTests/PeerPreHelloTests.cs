using Xunit;

namespace Orbweaver.UnitTests;

// CB-344: nothing an inbound connection sends reaches the mirror until its
// hello has been accepted.
//
// An inbound TLS handshake accepts any certificate; the pin is judged only when
// the hello arrives. Before this, every other frame from the still-unnamed
// connection was delivered, and the mirror's gate (MayAsk) asked only whether a
// connection existed, which it did. So a machine nobody paired could ask for
// anything a paired one could.
//
// The accepted-hello case is here as the control: a gate that delivered nothing
// at all would pass the other two.
public class PeerPreHelloTests
{
    private const string Pin = "aa11";

    private static PeerLink Link(
        List<(string Machine, PeerProtocol.PeerMessage Message)> got,
        Func<string, PeerIdentity.Peer?> knownPeer) =>
        new(new PeerLink.Seams(
            Deliver: (machine, message) =>
            {
                lock (got) got.Add((machine, message));
                return Task.CompletedTask;
            },
            KnownPeer: knownPeer,
            OwnCertificate: _ => throw new InvalidOperationException("no socket in this test")));

    private static async Task<byte[]> BytesOf(params PeerProtocol.PeerMessage[] messages)
    {
        using var stream = new MemoryStream();
        foreach (var m in messages) await PeerProtocol.WriteAsync(stream, m);
        return stream.ToArray();
    }

    // Adopts the bytes under a fresh unnamed name, as the accept loop does, and
    // waits for the connection to be forgotten. At a clean end Forget runs only
    // after the pump has awaited every delivery it queued, so once nothing is
    // connected, nothing more can be delivered. (A refused hello is unlisted by
    // Drop before the pump finishes; nothing is queued there, so the same
    // holds.) Bounded so a regression fails rather than hangs.
    private static async Task<Duplex> PumpUnnamedAsync(PeerLink link, byte[] bytes)
    {
        var stream = new Duplex(bytes);
        link.Adopt(PeerLink.Unnamed(), stream, Pin, CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (link.ConnectedMachines().Count > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the connection was never forgotten");
            await Task.Delay(10);
        }

        return stream;
    }

    [Fact]
    public async Task AFrameBeforeHelloIsNeverDelivered()
    {
        var got = new List<(string, PeerProtocol.PeerMessage)>();
        using var link = Link(got, _ => null);

        await PumpUnnamedAsync(link, await BytesOf(
            PeerProtocol.Message(PeerProtocol.Window, "a"),
            PeerProtocol.Message(PeerProtocol.Fetch, "b"),
            PeerProtocol.Message(PeerProtocol.Ok, "c")));

        Assert.Empty(got);
    }

    [Fact]
    public async Task AFrameAfterAnAcceptedHelloIsDelivered()
    {
        var got = new List<(string Machine, PeerProtocol.PeerMessage Message)>();
        using var link = Link(got, name => new PeerIdentity.Peer(Pin, name));

        await PumpUnnamedAsync(link, await BytesOf(
            PeerProtocol.Message(PeerProtocol.Hello, "h", name: "mini"),
            PeerProtocol.Message(PeerProtocol.Window, "b")));

        var only = Assert.Single(got);
        Assert.Equal("mini", only.Machine);
        Assert.Equal("b", only.Message.Id);
    }

    [Fact]
    public async Task AFrameBeforeAnAcceptedHelloIsStillDiscarded()
    {
        // The race a legitimate dialer can lose: a request ahead of its own
        // greeting. It is discarded, and what follows the hello still arrives.
        var got = new List<(string Machine, PeerProtocol.PeerMessage Message)>();
        using var link = Link(got, name => new PeerIdentity.Peer(Pin, name));

        await PumpUnnamedAsync(link, await BytesOf(
            PeerProtocol.Message(PeerProtocol.Window, "early"),
            PeerProtocol.Message(PeerProtocol.Hello, "h", name: "mini"),
            PeerProtocol.Message(PeerProtocol.Window, "late")));

        Assert.Equal(new[] { "late" }, got.Select(g => g.Message.Id));
    }

    [Fact]
    public async Task AFrameAfterARefusedHelloIsNeverDelivered()
    {
        var got = new List<(string, PeerProtocol.PeerMessage)>();
        using var link = Link(got, _ => null);

        var stream = await PumpUnnamedAsync(link, await BytesOf(
            PeerProtocol.Message(PeerProtocol.Hello, "h", name: "mini"),
            PeerProtocol.Message(PeerProtocol.Window, "c")));

        Assert.Empty(got);

        // And it was a refusal, not a connection that happened to end first.
        var answers = await stream.WrittenAsync();
        Assert.Contains(answers, m => m.Type == PeerProtocol.Err && m.Code == PeerProtocol.ErrUntrusted);
    }

    [Fact]
    public void AnUnnamedConnectionIsRecognised()
    {
        Assert.True(PeerLink.IsUnnamed(PeerLink.Unnamed()));
    }

    [Theory]
    [InlineData("mini")]
    [InlineData("x(inbound ")]
    [InlineData("(inbound)")]
    [InlineData("")]
    public void ANameIsNotUnnamed(string name)
    {
        Assert.False(PeerLink.IsUnnamed(name));
    }

    // --- the mirror's own gate -------------------------------------------------

    [Fact]
    public void AnUnnamedConnectionMayNotAsk()
    {
        using var host = new PeerMirrorHost();
        using var stream = new Parked();

        var unnamed = PeerLink.Unnamed();
        host.Link.Adopt(unnamed, stream, Pin, CancellationToken.None);

        // Connected, which is all MayAsk used to ask.
        Assert.True(host.Link.IsConnected(unnamed));
        Assert.False(host.MayAsk(unnamed));
    }

    [Fact]
    public void ANamedConnectionMayAsk()
    {
        using var host = new PeerMirrorHost();
        using var stream = new Parked();

        host.Link.Adopt("mini", stream, Pin, CancellationToken.None);

        Assert.True(host.MayAsk("mini"));
    }

    // Reads a fixed run of bytes then a clean end; keeps what is written to it
    // apart, so an answer the link sends cannot overwrite a frame not yet read
    // (which is what a single MemoryStream does).
    private sealed class Duplex(byte[] incoming) : Stream
    {
        private readonly MemoryStream _in = new(incoming);
        private readonly MemoryStream _out = new();

        public async Task<List<PeerProtocol.PeerMessage>> WrittenAsync()
        {
            byte[] bytes;
            lock (_out) bytes = _out.ToArray();

            using var reader = new MemoryStream(bytes);
            var all = new List<PeerProtocol.PeerMessage>();
            while (await PeerProtocol.ReadAsync(reader, CancellationToken.None) is { } m) all.Add(m);
            return all;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _in.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) { lock (_out) _out.Write(buffer, offset, count); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            lock (_out) _out.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // A connection that stays open until disposed, then ends cleanly.
    private sealed class Parked : Stream
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await _disposed.Task.WaitAsync(ct);
            return 0;
        }
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => ValueTask.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _disposed.TrySetResult();
            base.Dispose(disposing);
        }
    }
}
