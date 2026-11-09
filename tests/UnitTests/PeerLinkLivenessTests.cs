using Xunit;

namespace Orbweaver.UnitTests;

// CB-322: the peer link between two Macs stopped carrying anything after both
// ends served each other a persona picture at once, and stayed listed as
// connected for over an hour. Two things were missing, and these cover both
// without a socket: a read loop that keeps reading while a handler is busy
// writing, and a watchdog that notices when a connection has stopped reading
// and drops it so the dial tick can redial.
//
// The deadlock itself is forced over a real TLS socket in
// PeerLinkLoopbackTests.BothEndsAnsweringWithALargeReplyAtOnceDoNotDeadlock;
// what is here is the rule behind the fix, one case per arm.
public class PeerLinkLivenessTests
{
    private static readonly DateTime T0 = new(2026, 11, 9, 15, 59, 33, DateTimeKind.Utc);

    private static PeerLink Link(
        List<PeerProtocol.PeerMessage> got, Func<PeerProtocol.PeerMessage, Task>? onDeliver = null) =>
        new(new PeerLink.Seams(
            Deliver: (_, message) =>
            {
                lock (got) got.Add(message);
                return onDeliver?.Invoke(message) ?? Task.CompletedTask;
            },
            KnownPeer: _ => null,
            OwnCertificate: () => throw new InvalidOperationException("no certificate needed here")));

    // --- the read loop ---------------------------------------------------------

    // The rule that broke: a handler that has not returned must not stop the
    // next message being read. The first handler here is held until every byte
    // on the stream has been consumed, which on the old loop never happened —
    // it read one message and then waited on the handler that was waiting on
    // this test.
    [Fact]
    public async Task TheLoopKeepsReadingWhileAHandlerIsStillBusy()
    {
        var got = new List<PeerProtocol.PeerMessage>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = Link(got, message => message.Id == "a" ? release.Task : Task.CompletedTask);

        using var stream = await StreamOf(
            PeerProtocol.Message(PeerProtocol.Window, "a"),
            PeerProtocol.Message(PeerProtocol.Window, "b"),
            PeerProtocol.Message(PeerProtocol.Window, "c"));

        var pump = link.PumpAsync("mini", stream, CancellationToken.None);

        await WaitUntil(() => stream.Position == stream.Length);
        Assert.Equal(stream.Length, stream.Position);

        // Read, but not yet handled: order is kept, so b and c wait behind a.
        lock (got) Assert.Equal(new[] { "a" }, got.Select(m => m.Id));
        Assert.False(pump.IsCompleted, "the loop returned before what it read was handled");

        release.SetResult();
        await pump.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "a", "b", "c" }, got.Select(m => m.Id));
    }

    // A keepalive is the link's own business, like a greeting: reading it is
    // the point, and the mirror never sees one.
    [Fact]
    public async Task AKeepaliveIsReadButNeverDelivered()
    {
        var got = new List<PeerProtocol.PeerMessage>();

        using var stream = await StreamOf(
            PeerProtocol.Message(PeerProtocol.Ping, "p1"),
            PeerProtocol.Message(PeerProtocol.Window, "real"));

        await Link(got).PumpAsync("mini", stream, CancellationToken.None);

        Assert.Equal("real", Assert.Single(got).Id);
    }

    // --- the watchdog ----------------------------------------------------------

    [Fact]
    public async Task AKeepaliveGoesOutOnceItIsDueAndNotBefore()
    {
        var now = T0;
        using var link = Link(new List<PeerProtocol.PeerMessage>());
        link.Now = () => now;

        using var wire = new Parked();
        link.Adopt("mini", wire, "pin", CancellationToken.None);

        now = T0 + PeerLink.KeepaliveEvery - TimeSpan.FromSeconds(1);
        link.CheckLiveness();
        Assert.Empty(await wire.WrittenAsync());

        now = T0 + PeerLink.KeepaliveEvery;
        link.CheckLiveness();
        await WaitUntil(() => wire.BytesWritten > 0);

        // Same instant again: the one just sent is recent, so nothing more.
        link.CheckLiveness();

        var sent = await wire.WrittenAsync();
        Assert.Equal(PeerProtocol.Ping, Assert.Single(sent).Type);
        Assert.True(link.IsConnected("mini"));
    }

    // The recovery CB-322 was missing. A connection that has read nothing for
    // longer than StalledAfter is dropped and reported, which is what makes the
    // dial tick redial it — it skips anything still listed as connected.
    [Fact]
    public void AConnectionThatHasReadNothingPastTheDeadlineIsDroppedAndReported()
    {
        var now = T0;
        using var link = Link(new List<PeerProtocol.PeerMessage>());
        link.Now = () => now;

        var dropped = new List<string>();
        link.PeerDisconnected += dropped.Add;

        using var wire = new Parked();
        link.Adopt("mini", wire, "pin", CancellationToken.None);

        // Exactly at the deadline is still alive.
        now = T0 + PeerLink.StalledAfter;
        link.CheckLiveness();
        Assert.True(link.IsConnected("mini"));

        now = T0 + PeerLink.StalledAfter + TimeSpan.FromSeconds(1);
        link.CheckLiveness();

        Assert.False(link.IsConnected("mini"));
        Assert.Equal("mini", Assert.Single(dropped));
    }

    // Bytes arriving are what count, so a connection that read something
    // recently survives a check that would have dropped it otherwise.
    [Fact]
    public async Task BytesArrivingPushTheDeadlineOut()
    {
        var now = T0;
        using var link = Link(new List<PeerProtocol.PeerMessage>());
        link.Now = () => now;

        using var wire = new Parked();
        link.Adopt("mini", wire, "pin", CancellationToken.None);

        now = T0 + TimeSpan.FromSeconds(50);
        await wire.FeedAsync(PeerProtocol.Encode(PeerProtocol.Message(PeerProtocol.Ping, "alive")));

        now = T0 + PeerLink.StalledAfter + TimeSpan.FromSeconds(10);
        link.CheckLiveness();

        Assert.True(link.IsConnected("mini"));
    }

    // --- helpers ---------------------------------------------------------------

    private static async Task<MemoryStream> StreamOf(params PeerProtocol.PeerMessage[] messages)
    {
        var stream = new MemoryStream();
        foreach (var m in messages) await PeerProtocol.WriteAsync(stream, m);

        stream.Position = 0;
        return stream;
    }

    private static async Task WaitUntil(Func<bool> settled)
    {
        for (var i = 0; i < 500 && !settled(); i++)
            await Task.Delay(10);
    }

    // A live connection that delivers only what the test feeds it, records
    // what is written to it, and ends cleanly when disposed. Its reads stay
    // pending in between, which is how an idle socket looks to the pump.
    private sealed class Parked : Stream
    {
        private readonly object _gate = new();
        private readonly MemoryStream _written = new();
        private readonly Queue<byte> _pending = new();
        private TaskCompletionSource _more = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed;

        public int BytesWritten
        {
            get { lock (_gate) return (int)_written.Length; }
        }

        // Hands the pump some bytes and waits until it has taken them.
        public async Task FeedAsync(byte[] bytes)
        {
            lock (_gate)
            {
                foreach (var b in bytes) _pending.Enqueue(b);
                _more.TrySetResult();
            }

            for (var i = 0; i < 500; i++)
            {
                lock (_gate) if (_pending.Count == 0) return;
                await Task.Delay(10);
            }
        }

        public async Task<List<PeerProtocol.PeerMessage>> WrittenAsync()
        {
            byte[] copy;
            lock (_gate) copy = _written.ToArray();

            var messages = new List<PeerProtocol.PeerMessage>();
            using var read = new MemoryStream(copy);
            while (await PeerProtocol.ReadAsync(read) is { } message) messages.Add(message);
            return messages;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (true)
            {
                Task wait;

                lock (_gate)
                {
                    if (_pending.Count > 0)
                    {
                        var n = 0;
                        while (n < buffer.Length && _pending.Count > 0) buffer.Span[n++] = _pending.Dequeue();
                        return n;
                    }

                    if (_disposed) return 0;

                    _more = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = _more.Task;
                }

                await wait.WaitAsync(ct).ConfigureAwait(false);
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            lock (_gate) _written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            lock (_gate)
            {
                _disposed = true;
                _more.TrySetResult();
            }

            base.Dispose(disposing);
        }
    }
}
