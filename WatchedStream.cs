namespace Orbweaver
{
    // A peer connection's stream, remembering when it last read a byte.
    //
    // CB-322: PeerLink's watchdog drops a connection that has read nothing for
    // a minute, and "nothing" has to mean bytes rather than messages. A large
    // message coming in slowly over a thin link is a connection doing its job,
    // and timing it by whole messages would drop exactly that one, again on
    // every redial. Stamping here, at the bottom of every read, makes any
    // progress at all count.
    //
    // Everything else passes straight through. The clock is a seam so a test
    // can step it rather than wait a minute.
    internal sealed class WatchedStream(Stream inner, Func<DateTime> now) : Stream
    {
        private long _lastReadTicks = now().Ticks;

        internal DateTime LastRead => new(Interlocked.Read(ref _lastReadTicks), DateTimeKind.Utc);

        private int Stamp(int read)
        {
            if (read > 0) Interlocked.Exchange(ref _lastReadTicks, now().Ticks);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Stamp(await inner.ReadAsync(buffer, ct).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            Stamp(inner.Read(buffer, offset, count));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            inner.WriteAsync(buffer, ct);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            inner.WriteAsync(buffer, offset, count, ct);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

        public override void Flush() => inner.Flush();

        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
