using Xunit;

namespace Orbweaver.UnitTests;

// Who is let in, and what happens to a connection's name once it says one.
//
// PeerLink.Judge is the whole security of this transport. Everything else about
// the link fails loudly — a wrong port refuses, a wrong frame logs. This fails
// quietly: a machine that should not have been let in is indistinguishable from
// one that should, and what it gets is every transcript on this disk.
public class PeerGreetingTests
{
    private const string OurPin = "aa11";
    private const string TheirPin = "bb22";

    private static PeerIdentity.Peer Known(string pin) => new(pin, "mini");

    // --- the truth table -----------------------------------------------------

    [Fact]
    public void APinnedCertificateNeedsNoCode()
    {
        // The ordinary case: every reconnect after the first.
        Assert.Equal(
            PeerLink.Greeting.Trusted,
            PeerLink.Judge(Known(OurPin), OurPin, openCode: null, offeredCode: null));
    }

    [Fact]
    public void APinnedCertificateIsStillTrustedWhileAWindowIsOpen()
    {
        Assert.Equal(
            PeerLink.Greeting.Trusted,
            PeerLink.Judge(Known(OurPin), OurPin, openCode: "123456", offeredCode: null));
    }

    [Fact]
    public void AnUnknownMachineWithTheRightCodePairs()
    {
        Assert.Equal(
            PeerLink.Greeting.Paired,
            PeerLink.Judge(null, TheirPin, openCode: "123456", offeredCode: "123456"));
    }

    [Fact]
    public void AKnownMachineWithANewCertificateCanPairAgain()
    {
        // A reinstall. The certificate is genuinely different and genuinely
        // theirs; refusing would leave hand-editing a JSON file as the only
        // recovery. The code is the authority, and a person typed it.
        Assert.Equal(
            PeerLink.Greeting.Paired,
            PeerLink.Judge(Known(OurPin), TheirPin, openCode: "123456", offeredCode: "123456"));
    }

    [Fact]
    public void AKnownMachineWithANewCertificateAndNoCodeIsRefused()
    {
        // The same shape as the case above with the one thing that authorised
        // it removed — which is what an impostor presenting a fresh self-signed
        // certificate looks like.
        Assert.Equal(
            PeerLink.Greeting.Refused,
            PeerLink.Judge(Known(OurPin), TheirPin, openCode: null, offeredCode: null));
    }

    [Fact]
    public void AWrongCodeIsRefused()
    {
        Assert.Equal(
            PeerLink.Greeting.Refused,
            PeerLink.Judge(null, TheirPin, openCode: "123456", offeredCode: "123457"));
    }

    [Fact]
    public void ACodeOfferedWithNoWindowOpenIsRefused()
    {
        Assert.Equal(
            PeerLink.Greeting.Refused,
            PeerLink.Judge(null, TheirPin, openCode: null, offeredCode: "123456"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoCertificateIsRefusedEvenWithACorrectCode(string? pin)
    {
        // Checked first on purpose. An empty pin cannot be trusted, and it must
        // not be pairable either, or an open window would accept anonymous
        // connections and then pin nothing.
        Assert.Equal(
            PeerLink.Greeting.Refused,
            PeerLink.Judge(null, pin, openCode: "123456", offeredCode: "123456"));
    }

    // --- the pairing window --------------------------------------------------

    private static PeerLink Link() => new(new PeerLink.Seams(
        Deliver: (_, _) => Task.CompletedTask,
        KnownPeer: _ => null,
        OwnCertificate: _ => throw new InvalidOperationException("no socket in this test")));

    [Fact]
    public void AWindowIsShutUntilItIsOpened()
    {
        using var link = Link();

        Assert.False(link.PairingOpen);
    }

    [Fact]
    public void OpeningAWindowYieldsASixDigitCode()
    {
        using var link = Link();

        var code = link.OpenForPairing();

        Assert.True(link.PairingOpen);
        Assert.Equal(6, code.Length);
        Assert.All(code, c => Assert.InRange(c, '0', '9'));
    }

    [Fact]
    public void ClosingAWindowShutsIt()
    {
        using var link = Link();

        link.OpenForPairing();
        link.ClosePairing();

        Assert.False(link.PairingOpen);
    }

    [Fact]
    public void EachWindowGetsItsOwnCode()
    {
        using var link = Link();

        // Not a strong statement about entropy — one collision in a million is
        // possible and would be a flake. This is here to catch the version that
        // returns a constant, which is the mistake that would actually be made.
        var codes = Enumerable.Range(0, 8).Select(_ => link.OpenForPairing()).ToHashSet();

        Assert.True(codes.Count > 1);
    }

    // --- what a name change does to a connection -----------------------------

    [Fact]
    public void AConnectionAnswersToTheNameItWasAdoptedUnder()
    {
        using var link = Link();
        using var stream = new BlocksUntilDisposed();

        link.Adopt("mini", stream, OurPin, CancellationToken.None);

        Assert.True(link.IsConnected("mini"));
    }

    [Fact]
    public void RenamingMovesTheConnectionRatherThanCopyingIt()
    {
        // A MemoryStream ends the moment Adopt's pump reads it, which races
        // Forget against this assertion — the pump can remove "mini" before
        // IsConnected ever runs, and did, under load. Held open instead, the
        // same fix TwoUnnamedConnectionsDoNotCollide already needed below.
        using var link = Link();
        using var stream = new BlocksUntilDisposed();

        var provisional = PeerLink.Unnamed();

        link.Adopt(provisional, stream, OurPin, CancellationToken.None);
        link.Rename(provisional, "mini");

        Assert.False(link.IsConnected(provisional));
        Assert.True(link.IsConnected("mini"));
    }

    [Fact]
    public async Task ARenamedConnectionIsForgottenWhenItCloses()
    {
        // The bug this exists for: the pump's continuation used to close over
        // the name the connection had when it opened, so a renamed one removed
        // an entry that was already gone and stayed listed as connected
        // forever. Nothing redials a machine that is already connected, so the
        // link would go quiet and look healthy.
        using var link = Link();

        // Empty, so the pump reads a clean end of stream and exits at once —
        // deliberately, unlike the two tests above: this one is asserting what
        // happens *after* the pump exits, so it needs the pump to actually do
        // that rather than to stay open.
        var stream = new MemoryStream();

        var provisional = PeerLink.Unnamed();

        link.Adopt(provisional, stream, OurPin, CancellationToken.None);
        link.Rename(provisional, "mini");

        await WaitUntil(() => !link.IsConnected("mini"));

        Assert.False(link.IsConnected("mini"));
    }

    [Fact]
    public void TwoUnnamedConnectionsDoNotCollide()
    {
        // One literal "(inbound)" for every connection that has not said who it
        // is meant two machines dialling at once silently disposed each other's
        // stream — rare, and indistinguishable from a network drop.
        //
        // Held open rather than backed by a MemoryStream. A MemoryStream ends
        // the moment it is read, the pump exits, and the connection is forgotten
        // — so the count this asserts was a race against the reaper rather than
        // a statement about collisions, and it lost. Widening the assertion
        // would have hidden the thing it exists to check.
        using var link = Link();
        using var first = new HeldOpen();
        using var second = new HeldOpen();

        var a = PeerLink.Unnamed();
        var b = PeerLink.Unnamed();

        Assert.NotEqual(a, b);

        link.Adopt(a, first, OurPin, CancellationToken.None);
        link.Adopt(b, second, TheirPin, CancellationToken.None);

        Assert.Equal(2, link.ConnectedMachines().Count);
    }

    // A stream that blocks on read until it is disposed, then reports a clean
    // end of stream — what a live connection looks like right up until it is
    // actually closed. Exists so Adopt's pump has somewhere to spend the test
    // instead of racing Forget against the assertion that follows.
    //
    // HeldOpen below never resolves at all, which is fine where nothing waits
    // on the pump exiting, but would leave a suspended read hanging past the
    // end of any test that later awaited disconnection. This resolves on
    // Dispose specifically so nothing outlives the `using` block that owns it.
    private sealed class BlocksUntilDisposed : Stream
    {
        private readonly TaskCompletionSource _disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _disposed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        // Idempotent, same reason as HeldOpen below: the link disposes the
        // stream when it drops the connection and the test's `using` disposes
        // it again on the way out.
        protected override void Dispose(bool disposing)
        {
            _disposed.TrySetResult();
            base.Dispose(disposing);
        }
    }

    // A stream that never delivers a byte and never ends, which is what an idle
    // socket looks like. Only ReadAsync is reached — the pump does nothing else
    // with a connection it is waiting on.
    private sealed class HeldOpen : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Delay(Timeout.Infinite, cancellationToken)
                .ContinueWith(_ => 0, TaskScheduler.Default));

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        // Nothing to release, and deliberately safe to call twice: the link
        // disposes the stream when it drops the connection and the test's
        // `using` disposes it again on the way out. A stream that threw the
        // second time would fail the test for a reason that has nothing to do
        // with what it asserts.
        protected override void Dispose(bool disposing) => base.Dispose(disposing);
    }

    // Polls rather than sleeps: the pump exits on its own thread, and a fixed
    // wait would be either a flake in Release or dead time in Debug. Same rule
    // the rest of this repo's async tests follow.
    private static async Task WaitUntil(Func<bool> settled)
    {
        for (var i = 0; i < 200 && !settled(); i++)
            await Task.Delay(10);
    }
}
