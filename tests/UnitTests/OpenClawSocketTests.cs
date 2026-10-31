using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Tls;
using Xunit;

namespace Orbweaver.Tests;

// The half of OpenClawSocket that is a protocol rather than a socket: the
// RFC 6455 upgrade, the HTTP response reader the media endpoint uses, and the
// certificate pin.
//
// Everything here is driven over an in-memory stream. The TLS transport itself
// is excluded from coverage and stays excluded — it needs a peer that completes
// a real TLS 1.3 handshake, which is the one thing a headless runner cannot
// produce — but the exchanges layered on top of it need no socket at all, and
// they are where the mistakes with teeth live: an upgrade that accepts a 101
// from something that is not a WebSocket, or a pin that trusts a certificate it
// should have refused.
public class OpenClawSocketTests
{
    // Headers are read a byte at a time on purpose (the frames that follow are
    // on the same stream), so this serves them that way and lets the caller see
    // exactly what was written before deciding what to answer.
    private sealed class ScriptedStream : Stream
    {
        private readonly Func<string, byte[]> _reply;
        private readonly MemoryStream _written = new();
        private byte[]? _pending;
        private int _read;

        public ScriptedStream(Func<string, byte[]> reply) => _reply = reply;

        public string Request => Encoding.ASCII.GetString(_written.ToArray());

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            // Materialised on the first read, because the answer depends on the
            // request: Sec-WebSocket-Accept is a hash of a key the caller
            // generated and nothing outside this method can know it in advance.
            _pending ??= _reply(Request);

            if (_read >= _pending.Length) return ValueTask.FromResult(0);

            var take = Math.Min(buffer.Length, _pending.Length - _read);
            _pending.AsSpan(_read, take).CopyTo(buffer.Span);
            _read += take;
            return ValueTask.FromResult(take);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            _written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count) =>
            _written.Write(buffer, offset, count);

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    // The server's half of the handshake: take the key we sent, append the fixed
    // GUID, hash, return it. Computed here the way a real server would rather
    // than copied out of the app, so an app that changed the GUID would fail.
    private static string AcceptFor(string request)
    {
        foreach (var line in request.Split("\r\n"))
        {
            if (!line.StartsWith("Sec-WebSocket-Key:", StringComparison.Ordinal)) continue;

            var key = line["Sec-WebSocket-Key:".Length..].Trim();
            return Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        }

        throw new InvalidOperationException("the upgrade request carried no Sec-WebSocket-Key");
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    // The request the gateway is actually asked to upgrade. Every header here is
    // required by RFC 6455 and a gateway that gets a malformed one answers 400,
    // which surfaces as "can't reach the gateway" with no hint as to why.
    [Fact]
    public async Task TheUpgradeRequestCarriesTheHeadersRfc6455Requires()
    {
        var stream = new ScriptedStream(request => Ascii(
            "HTTP/1.1 101 Switching Protocols\r\n"
            + "Upgrade: websocket\r\n"
            + $"Sec-WebSocket-Accept: {AcceptFor(request)}\r\n\r\n"));

        await OpenClawSocket.UpgradeAsync(stream, "gw.local", 4443, CancellationToken.None);

        Assert.StartsWith("GET / HTTP/1.1\r\n", stream.Request, StringComparison.Ordinal);
        Assert.Contains("Host: gw.local:4443\r\n", stream.Request);
        Assert.Contains("Upgrade: websocket\r\n", stream.Request);
        Assert.Contains("Connection: Upgrade\r\n", stream.Request);
        Assert.Contains("Sec-WebSocket-Version: 13\r\n", stream.Request);
        Assert.EndsWith("\r\n\r\n", stream.Request, StringComparison.Ordinal);
    }

    // A fresh key per upgrade. A fixed one would make the accept check
    // meaningless — anything that had ever seen one exchange could replay it.
    [Fact]
    public async Task EachUpgradeSendsADifferentKey()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 3; i++)
        {
            var stream = new ScriptedStream(request => Ascii(
                "HTTP/1.1 101 Switching Protocols\r\n"
                + $"Sec-WebSocket-Accept: {AcceptFor(request)}\r\n\r\n"));

            await OpenClawSocket.UpgradeAsync(stream, "gw.local", 4443, CancellationToken.None);

            keys.Add(AcceptFor(stream.Request));
        }

        Assert.Equal(3, keys.Count);
    }

    // Anything but a 101 is refused, and the status line goes into the message —
    // that line is the whole diagnosis for a gateway answering on the wrong port
    // or behind something that isn't it.
    [Fact]
    public async Task AnythingButA101IsRefusedWithTheStatusLine()
    {
        var stream = new ScriptedStream(_ => Ascii(
            "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\n\r\n"));

        var ex = await Assert.ThrowsAsync<IOException>(
            () => OpenClawSocket.UpgradeAsync(stream, "gw.local", 4443, CancellationToken.None));

        Assert.Contains("401 Unauthorized", ex.Message);
    }

    // The reason the accept hash is checked at all: something that says 101 to
    // anything is not a WebSocket implementation, and going on to frame against
    // it produces nonsense rather than an error.
    [Fact]
    public async Task A101WithTheWrongAcceptIsRefused()
    {
        var stream = new ScriptedStream(_ => Ascii(
            "HTTP/1.1 101 Switching Protocols\r\n"
            + "Sec-WebSocket-Accept: bm90IHRoZSByaWdodCBoYXNo\r\n\r\n"));

        var ex = await Assert.ThrowsAsync<IOException>(
            () => OpenClawSocket.UpgradeAsync(stream, "gw.local", 4443, CancellationToken.None));

        Assert.Contains("Sec-WebSocket-Accept", ex.Message);
    }

    // Reading stops at the blank line and not a byte further. This is the one
    // property the byte-at-a-time loop exists for: the first WebSocket frame
    // follows immediately on the same stream, and it is connect.challenge —
    // swallow it and nothing else in the protocol can happen.
    [Fact]
    public async Task HeaderReadingStopsAtTheBlankLineAndLeavesTheNextFrameAlone()
    {
        const string headers = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n\r\n";
        var stream = new ScriptedStream(_ => Ascii(headers + "FRAME-BYTES"));

        var read = await OpenClawSocket.ReadHeadersAsync(stream, CancellationToken.None);

        Assert.Equal(headers, read);

        var rest = new byte[64];
        var count = await stream.ReadAsync(rest, CancellationToken.None);

        Assert.Equal("FRAME-BYTES", Encoding.ASCII.GetString(rest, 0, count));
    }

    // A peer that hangs up mid-headers says so, rather than looping or returning
    // a half-read header block that would then fail an unrelated check.
    [Fact]
    public async Task AStreamThatEndsMidHeadersIsAnError()
    {
        var stream = new ScriptedStream(_ => Ascii("HTTP/1.1 101 Switch"));

        var ex = await Assert.ThrowsAsync<IOException>(
            () => OpenClawSocket.ReadHeadersAsync(stream, CancellationToken.None));

        Assert.Contains("closed the connection", ex.Message);
    }

    // Bounded, because a peer that never sends a blank line would otherwise be
    // read forever into a StringBuilder.
    [Fact]
    public async Task HeadersThatNeverEndAreRefusedRatherThanReadForever()
    {
        var stream = new ScriptedStream(_ => Ascii(new string('x', 32 * 1024)));

        var ex = await Assert.ThrowsAsync<IOException>(
            () => OpenClawSocket.ReadHeadersAsync(stream, CancellationToken.None));

        Assert.Contains("no end of headers", ex.Message);
    }

    // The media GET. The Authorization header is the one line that decides
    // whether the gateway serves a picture or a 401, and nothing else in the app
    // can show you what was sent.
    [Fact]
    public void TheMediaRequestCarriesTheBearerTokenAndClosesTheConnection()
    {
        var request = OpenClawSocket.GetRequest("gw.local", 4443, "/media/abc.png", "tok-123");

        Assert.StartsWith("GET /media/abc.png HTTP/1.1\r\n", request, StringComparison.Ordinal);
        Assert.Contains("Host: gw.local:4443\r\n", request);
        Assert.Contains("Authorization: Bearer tok-123\r\n", request);

        // One connection per fetch — see GetAsync's header for why pooling would
        // be machinery for a saving nobody would notice.
        Assert.Contains("Connection: close\r\n", request);
        Assert.EndsWith("\r\n\r\n", request, StringComparison.Ordinal);
    }

    // Stops at the declared length rather than waiting for the peer to hang up.
    // The stream here never ends, so a loop that ignored Content-Length would
    // read until the 32MB ceiling instead of returning five bytes — which is the
    // difference between a picture appearing and a panel that sits there.
    [Fact]
    public async Task ABodyIsReadToTheLengthTheHeadersDeclared()
    {
        var stream = new EndlessBody("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nPNG..");

        var bytes = await OpenClawSocket.ReadResponseAsync(stream, CancellationToken.None);

        Assert.Equal("PNG..", Encoding.ASCII.GetString(bytes!));
    }

    // `Connection: close` means the body ends when the stream does, so a missing
    // Content-Length is read to exhaustion rather than treated as an error.
    [Fact]
    public async Task ABodyWithNoDeclaredLengthIsReadToTheEndOfTheStream()
    {
        var stream = new ScriptedStream(_ => Ascii(
            "HTTP/1.1 200 OK\r\nContent-Type: image/png\r\n\r\nall of it"));

        var bytes = await OpenClawSocket.ReadResponseAsync(stream, CancellationToken.None);

        Assert.Equal("all of it", Encoding.ASCII.GetString(bytes!));
    }

    // A length header that is present but not a number is the same situation as
    // one that is absent, not a failure.
    [Fact]
    public async Task AnUnparseableLengthFallsBackToReadingToTheEnd()
    {
        var stream = new ScriptedStream(_ => Ascii(
            "HTTP/1.1 200 OK\r\nContent-Length: lots\r\n\r\nbody"));

        var bytes = await OpenClawSocket.ReadResponseAsync(stream, CancellationToken.None);

        Assert.Equal("body", Encoding.ASCII.GetString(bytes!));
    }

    // Header names are case-insensitive in HTTP, and a gateway is free to change
    // its casing between versions.
    [Fact]
    public void TheLengthHeaderIsFoundWhateverItsCasing()
    {
        Assert.Equal(42, OpenClawSocket.ContentLength("HTTP/1.1 200 OK\r\ncontent-length: 42\r\n\r\n"));
        Assert.Equal(-1, OpenClawSocket.ContentLength("HTTP/1.1 200 OK\r\n\r\n"));
    }

    // CB-272: a stream that ends short of its declared length is a dropped
    // connection. Returning the partial body would let a half-downloaded clip
    // be cached and opened.
    [Fact]
    public async Task AStreamThatEndsShortOfItsDeclaredLengthIsNull()
    {
        var stream = new ScriptedStream(_ => Ascii(
            "HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n0123456789"));

        Assert.Null(await OpenClawSocket.ReadResponseAsync(stream, CancellationToken.None));
    }

    // A non-200 is a null, not an exception: a picture that won't load is a
    // picture that won't load, and the message it belongs to still reads.
    [Theory]
    [InlineData("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("HTTP/1.1 401 Unauthorized\r\nContent-Length: 3\r\n\r\nno!")]
    [InlineData("HTTP/1.1 500 Internal Server Error\r\n\r\n")]
    public async Task ANonSuccessResponseIsANullRatherThanAThrow(string response)
    {
        var stream = new ScriptedStream(_ => Ascii(response));

        Assert.Null(await OpenClawSocket.ReadResponseAsync(stream, CancellationToken.None));
    }

    // The ceiling. Not a limit anyone should reach — these are pictures in a
    // chat window — but a malformed length header must not be able to fill
    // memory, and the loop's own exit condition is what enforces that.
    [Fact]
    public async Task ABodyIsBoundedEvenWhenTheHeadersClaimMore()
    {
        // A stream that answers every read with a full buffer, forever, and
        // declares a length far past the ceiling. Without the ceiling this test
        // does not finish.
        var stream = new EndlessBody("HTTP/1.1 200 OK\r\nContent-Length: 999999999999\r\n\r\n");

        var bytes = await OpenClawSocket.ReadResponseAsync(stream, CancellationToken.None);

        Assert.NotNull(bytes);

        // Cut at the ceiling and returned, not refused: the loop only stops once
        // the body has exceeded it, so a body over MaxBodyBytes is a truncated
        // one. CB-272's clip guard depends on exactly this, so it is asserted
        // strictly rather than as "about 32MB".
        Assert.True(bytes!.Length > OpenClawSocket.MaxBodyBytes);
        Assert.InRange(bytes.Length, OpenClawSocket.MaxBodyBytes, OpenClawSocket.MaxBodyBytes + 64 * 1024);
    }

    // Serves a fixed prefix — headers, and optionally the whole body — and then
    // 'A' forever. A reader that stops when it should terminates; one that waits
    // for the stream to close does not.
    private sealed class EndlessBody : Stream
    {
        private readonly byte[] _headers;
        private int _sent;

        public EndlessBody(string prefix) => _headers = Encoding.ASCII.GetBytes(prefix);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_sent < _headers.Length)
            {
                var take = Math.Min(buffer.Length, _headers.Length - _sent);
                _headers.AsSpan(_sent, take).CopyTo(buffer.Span);
                _sent += take;
                return ValueTask.FromResult(take);
            }

            buffer.Span.Fill((byte)'A');
            return ValueTask.FromResult(buffer.Length);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) { }
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // ---- the certificate pin -------------------------------------------------

    // The gateway serves a self-signed certificate with no subjectAltName, so
    // there is no name to validate and the fingerprint is the entire identity
    // check. That makes these four cases the whole of the security of this
    // transport, which is why they are tested against a certificate generated
    // here rather than assumed from the code reading correctly.

    private sealed record ServerCertificate(Certificate Certificate) : TlsServerCertificate
    {
        public CertificateStatus CertificateStatus => null!;
    }

    private static (TlsServerCertificate Presented, string Fingerprint) SelfSigned(string name)
    {
        using var key = RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var der = cert.RawData;

        // The same derivation OpenClawSocket uses and the gateway's own CLI
        // prints, computed independently here.
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(der));

        var crypto = new Org.BouncyCastle.Tls.Crypto.Impl.BC.BcTlsCrypto(
            new Org.BouncyCastle.Security.SecureRandom());

        var chain = new Certificate(new[] { crypto.CreateCertificate(der) });

        return (new ServerCertificate(chain), fingerprint);
    }

    private static string? Present(string? pinned, TlsServerCertificate presented)
    {
        var client = new OpenClawSocket.PinnedTlsClient(pinned);
        client.GetAuthentication().NotifyServerCertificate(presented);
        return client.Fingerprint;
    }

    // First contact, with no pin yet: whatever it presents is trusted, and the
    // fingerprint is recorded so the settings window can show the user the value
    // they are being asked to trust.
    [Fact]
    public void WithNoPinTheCertificateIsTrustedAndItsFingerprintRecorded()
    {
        var (presented, fingerprint) = SelfSigned("gw.local");

        Assert.Equal(fingerprint, Present(null, presented));
        Assert.Equal(fingerprint, Present("", presented));
    }

    [Fact]
    public void AMatchingPinIsAccepted()
    {
        var (presented, fingerprint) = SelfSigned("gw.local");

        Assert.Equal(fingerprint, Present(fingerprint, presented));

        // Hex casing is not part of the identity, and a pin written by hand or
        // copied from a different tool can arrive either way.
        Assert.Equal(fingerprint, Present(fingerprint.ToUpperInvariant(), presented));
    }

    // The entire point of pinning. BouncyCastle reports this as a fatal
    // bad_certificate alert, which is what OpenClawGateway then classifies as a
    // mismatch rather than as an unreachable host.
    [Fact]
    public void ADifferentCertificateIsRefused()
    {
        var (presented, _) = SelfSigned("gw.local");
        var (_, otherFingerprint) = SelfSigned("someone-else.local");

        var alert = Assert.Throws<TlsFatalAlert>(() => Present(otherFingerprint, presented));

        Assert.Equal(AlertDescription.bad_certificate, alert.AlertDescription);
    }

    // An empty chain is refused rather than treated as "no pin to check". A peer
    // that presents nothing has not identified itself, and this transport has no
    // other identity check to fall back on.
    [Fact]
    public void AnEmptyCertificateChainIsRefused()
    {
        var alert = Assert.Throws<TlsFatalAlert>(
            () => Present(null, new ServerCertificate(Certificate.EmptyChain)));

        Assert.Equal(AlertDescription.bad_certificate, alert.AlertDescription);
    }

    // TLS 1.3 only, and offering 1.2 as well would be pointless — the gateway
    // answers a 1.2 hello with alert 70. Stated here so a future "let's be
    // tolerant" change has to argue with a test rather than slip through as a
    // silent downgrade.
    //
    // Read off the ClientHello this client actually produces rather than off the
    // property that produces it. BouncyCastle's non-blocking mode is what makes
    // that possible with no network at all: the handshake is started into an
    // output buffer and talks to nobody, which is the same call
    // TlsDuplexStream.HandshakeAsync opens with.
    [Fact]
    public void TheClientHelloOffersTls13AndNothingElse()
    {
        var tls = new TlsClientProtocol();
        tls.Connect(new OpenClawSocket.PinnedTlsClient(null));

        var hello = new byte[tls.GetAvailableOutputBytes()];
        tls.ReadOutput(hello, 0, hello.Length);

        Assert.NotEmpty(hello);

        // extension 43 (supported_versions), 3 bytes, one 2-byte version: 0304.
        // Asserted as the exact bytes because a hello offering 0303 as well
        // would still connect to plenty of servers and would silently undo the
        // decision this test exists to hold.
        Assert.Contains("002b0003020304", Convert.ToHexStringLower(hello));
    }

    // The gateway authenticates us with a signed device identity inside the
    // protocol, not with a client certificate — so there is nothing to offer
    // when it asks.
    [Fact]
    public void NoClientCertificateIsOffered()
    {
        var client = new OpenClawSocket.PinnedTlsClient(null);

        Assert.Null(client.GetAuthentication().GetClientCredentials(null!));
    }
}
