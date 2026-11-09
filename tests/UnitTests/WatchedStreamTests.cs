using Xunit;

namespace Orbweaver.UnitTests;

// WatchedStream is what PeerLink's watchdog reads to decide a connection has
// stopped moving (CB-322). The one rule that matters is that a read returning
// bytes moves the clock and a read returning none does not; everything else
// must pass through untouched, because TLS sits on top of it.
public class WatchedStreamTests
{
    private static readonly DateTime T0 = new(2026, 11, 9, 16, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task EveryKindOfReadThatReturnsBytesMovesTheClockAndAnEmptyOneDoesNot()
    {
        var now = T0;
        using var watched = new WatchedStream(new MemoryStream(new byte[] { 1, 2, 3 }), () => now);
        Assert.Equal(T0, watched.LastRead);

        now = T0.AddSeconds(1);
        Assert.Equal(1, await watched.ReadAsync(new byte[1].AsMemory()));
        Assert.Equal(now, watched.LastRead);

        now = T0.AddSeconds(2);
        Assert.Equal(1, await watched.ReadAsync(new byte[1], 0, 1, CancellationToken.None));
        Assert.Equal(now, watched.LastRead);

        now = T0.AddSeconds(3);
        Assert.Equal(1, watched.Read(new byte[1], 0, 1));
        Assert.Equal(now, watched.LastRead);

        // At the end: nothing read, so the last real read stands.
        now = T0.AddSeconds(4);
        Assert.Equal(0, await watched.ReadAsync(new byte[1].AsMemory()));
        Assert.Equal(T0.AddSeconds(3), watched.LastRead);
    }

    [Fact]
    public async Task WritesAndFlushesPassStraightThrough()
    {
        var inner = new MemoryStream();
        using var watched = new WatchedStream(inner, () => T0);

        await watched.WriteAsync(new byte[] { 1 }.AsMemory());
        await watched.WriteAsync(new byte[] { 0, 2, 0 }, 1, 1, CancellationToken.None);
        watched.Write(new byte[] { 3 }, 0, 1);
        await watched.FlushAsync(CancellationToken.None);
        watched.Flush();

        Assert.Equal(new byte[] { 1, 2, 3 }, inner.ToArray());
        Assert.True(watched.CanRead);
        Assert.True(watched.CanWrite);
        Assert.False(watched.CanSeek);
    }

    // Not seekable, like the socket under it, and saying so rather than
    // answering about the inner stream's position.
    [Fact]
    public void SeekingIsNotSupported()
    {
        using var watched = new WatchedStream(new MemoryStream(), () => T0);

        Assert.Throws<NotSupportedException>(() => watched.Length);
        Assert.Throws<NotSupportedException>(() => watched.Position);
        Assert.Throws<NotSupportedException>(() => watched.Position = 0);
        Assert.Throws<NotSupportedException>(() => watched.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => watched.SetLength(0));
    }

    // Closing the watched stream closes the connection under it: PeerLink
    // drops a stalled peer by disposing exactly this.
    [Fact]
    public void DisposingItClosesTheStreamUnderneath()
    {
        var inner = new MemoryStream();
        var watched = new WatchedStream(inner, () => T0);

        watched.Dispose();

        Assert.False(inner.CanRead);
    }
}
