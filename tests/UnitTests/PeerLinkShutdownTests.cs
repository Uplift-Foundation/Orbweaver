using System.Runtime.CompilerServices;
using Xunit;

namespace Orbweaver.UnitTests;

// A write that fails drops the connection, and dropping disposes it — while the
// sender that hit the failure still holds the connection's write gate, and others
// may be queued behind it. The sender's `finally` then released a gate Dispose had
// already disposed (an ObjectDisposedException out of SendAsync, unobserved for a
// caller that does not await it), and anyone still queued waited on a semaphore
// that could no longer be signalled.
public class PeerLinkShutdownTests
{
    // Reads park until the stream is disposed, as a quiet socket does; writes wait for
    // the test to let them go and then fail, as one to a peer that has gone does.
    private sealed class DyingStream : Stream
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WritesMayFail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await _closed.Task.WaitAsync(ct).ConfigureAwait(false);
            return 0;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Writes);
            await WritesMayFail.Task.ConfigureAwait(false);
            throw new IOException("broken pipe");
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _closed.TrySetResult();
            base.Dispose(disposing);
        }
    }

    private static PeerLink Link() =>
        new(new PeerLink.Seams(
            Deliver: (_, _) => Task.CompletedTask,
            KnownPeer: _ => null,
            OwnCertificate: () => throw new InvalidOperationException("no certificate needed")));

    private static bool IsDisposedSemaphore(Exception e) => e switch
    {
        ObjectDisposedException ode => ode.ObjectName == "System.Threading.SemaphoreSlim",
        AggregateException aggregate => aggregate.Flatten().InnerExceptions.Any(IsDisposedSemaphore),
        _ => false
    };

    private static async Task WaitForWritesAsync(DyingStream stream, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref stream.Writes) < count && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(Volatile.Read(ref stream.Writes) >= count, "the write never started");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FireAndForget(PeerLink link, int count)
    {
        for (var i = 0; i < count; i++) _ = link.SendAsync("avatar", PeerProtocol.Message(PeerProtocol.Fetch, "id" + i));
    }

    [Fact]
    public async Task AWriteThatFailsReportsNotDeliveredRatherThanThrowingFromTheGate()
    {
        var link = Link();
        var stream = new DyingStream();
        link.Adopt("avatar", stream, "pin", CancellationToken.None);

        var send = link.SendAsync("avatar", PeerProtocol.Message(PeerProtocol.Fetch, "id"));
        await WaitForWritesAsync(stream, 1);
        stream.WritesMayFail.SetResult();

        Assert.False(await send);
        Assert.False(link.IsConnected("avatar"));
    }

    [Fact]
    public async Task SendersQueuedBehindAFailedWriteAreAllToldNotDeliveredAndNoneIsLeftWaiting()
    {
        var link = Link();
        var stream = new DyingStream();
        link.Adopt("avatar", stream, "pin", CancellationToken.None);

        var sends = Enumerable.Range(0, 20)
            .Select(i => link.SendAsync("avatar", PeerProtocol.Message(PeerProtocol.Fetch, "id" + i)))
            .ToList();
        await WaitForWritesAsync(stream, 1);
        stream.WritesMayFail.SetResult();

        var all = Task.WhenAll(sends);
        Assert.Same(all, await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.All(await all, delivered => Assert.False(delivered));
    }

    [Fact]
    public async Task AFireAndForgetSendThatLosesItsConnectionLeavesNoUnobservedDisposedSemaphore()
    {
        var link = Link();
        var stream = new DyingStream();
        link.Adopt("avatar", stream, "pin", CancellationToken.None);

        var seen = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            lock (seen) seen.Add(e.Exception);
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            FireAndForget(link, 5);
            await WaitForWritesAsync(stream, 1);
            stream.WritesMayFail.SetResult();
            await Task.Delay(200);

            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        lock (seen) Assert.DoesNotContain(seen, IsDisposedSemaphore);
    }
}
