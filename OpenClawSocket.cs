using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Orbweaver
{
    // Opens a WebSocket to an OpenClaw gateway without using .NET's TLS stack.
    //
    // Why this exists, because it is a lot of machinery to replace one line of
    // ClientWebSocket: the gateway hardcodes `minVersion: "TLSv1.3"`, and .NET
    // on macOS cannot speak TLS 1.3 at all — it fails the same way against
    // cloudflare.com, because SecureTransport tops out at 1.2. That is a
    // property of the platform, not of this gateway, and macOS is this app's
    // primary one. An ssh tunnel doesn't sidestep it either: the gateway
    // refuses plain HTTP on loopback, so a forwarded port arrives at the same
    // handshake. See docs/openclaw-findings.md.
    //
    // So the TLS is done by BouncyCastle, which is already a dependency for the
    // gateway's Ed25519 device identity and carries a managed TLS 1.3 client.
    // The WebSocket upgrade is then a plain HTTP/1.1 exchange over that stream,
    // and the framing goes back to the BCL via WebSocket.CreateFromStream —
    // only the transport is hand-rolled, not the protocol.
    internal static class OpenClawSocket
    {
        // RFC 6455's fixed GUID: the server appends it to our key, hashes, and
        // returns the result, which is how we know we're talking to a WebSocket
        // implementation rather than something that says 101 to anything.
        private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        internal sealed record Connection(WebSocket Socket, Stream Transport, string Fingerprint);

        // Excluded: every line below opens a real TCP connection and completes a
        // real TLS 1.3 handshake against a gateway. There is no seam that keeps
        // the TcpClient out of it — the handshake is what produces the stream
        // everything else needs — and a headless CI runner has no gateway to
        // hand it. What is *not* excluded is everything this method delegates
        // to: UpgradeAsync, ReadHeadersAsync and ReadResponseAsync all take a
        // Stream and are driven over an in-memory one by
        // tests/UnitTests/OpenClawSocketTests.cs.
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        public static async Task<Connection> ConnectAsync(
            string host, int port, string? pinnedFingerprint, CancellationToken ct)
        {
            var tcp = new TcpClient();
            TlsDuplexStream? stream = null;

            try
            {
                await tcp.ConnectAsync(host, port, ct);

                var client = new PinnedTlsClient(pinnedFingerprint);
                stream = await TlsDuplexStream.HandshakeAsync(tcp, client, ct);

                await UpgradeAsync(stream, host, port, ct);

                var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
                {
                    IsServer = false,
                    KeepAliveInterval = TimeSpan.FromSeconds(15)
                });

                return new Connection(socket, stream, client.Fingerprint ?? "");
            }
            catch
            {
                // A handshake that fails — a rotated certificate, a peer that
                // hangs up, an upgrade that isn't a 101 — used to leave the
                // socket connected and the stream's semaphore finalizable. One
                // per attempt, and the attempts retry.
                stream?.Dispose();
                tcp.Dispose();
                throw;
            }

        }

        // A plain HTTP GET over the same managed TLS, for the gateway's media
        // endpoints. It has to go through here rather than HttpClient for the
        // same reason the WebSocket does: the gateway is TLS 1.3 only and .NET
        // on macOS cannot speak it, so HttpClient fails before it sends a byte.
        //
        // One connection per fetch, closed at the end. Images are fetched once
        // and cached by the caller, so pooling would add machinery for a saving
        // nobody would notice.
        //
        // Excluded for the same reason as ConnectAsync: the two lines that are
        // not delegation open a real socket and complete a real TLS 1.3
        // handshake. The HTTP half — the request line, the 200 check, the
        // Content-Length rules and the size ceiling — was lifted into
        // GetRequest and ReadResponseAsync below precisely so it did not have to
        // be excluded with them, and both are driven over a MemoryStream in
        // tests/UnitTests/OpenClawSocketTests.cs.
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        public static async Task<byte[]?> GetAsync(
            string host, int port, string path, string bearer,
            string? pinnedFingerprint, CancellationToken ct)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, ct);

            var client = new PinnedTlsClient(pinnedFingerprint);

            // `using`, because this returns early on a non-200 and can throw
            // anywhere after: disposing the socket alone leaves the stream's
            // semaphore behind, and this runs once per picture.
            using var stream = await TlsDuplexStream.HandshakeAsync(tcp, client, ct);

            await stream.WriteAsync(Encoding.ASCII.GetBytes(GetRequest(host, port, path, bearer)), ct);

            return await ReadResponseAsync(stream, ct);
        }

        // The request line is written by hand, with the bearer token already in
        // the headers, so a CR or LF in the path ends the line early and
        // injects a header into a token-bearing request. Every url this app
        // fetches reaches here (pictures, clips, files and their meta), and
        // several arrive from the gateway's own transcript, so the refusal
        // lives at the one place that splices a path into the request rather
        // than at each producer that might forget it.
        internal static bool HasControlCharacter(string? text)
        {
            if (text is null) return false;

            foreach (var c in text)
            {
                if (c < ' ' || c == '\u007f') return true;
            }

            return false;
        }

        // `Connection: close` rather than keep-alive, because one connection per
        // fetch is the whole shape here — see GetAsync's header. Split out from
        // GetAsync so the exact bytes are assertable without a gateway: the
        // Authorization header is the one line that decides whether the gateway
        // serves a picture or a 401, and it is not otherwise visible from
        // anywhere.
        internal static string GetRequest(string host, int port, string path, string bearer) =>
            HasControlCharacter(path)
                ? throw new ArgumentException("A request path may not contain control characters.", nameof(path))
                : BuildRequest(host, port, path, bearer);

        private static string BuildRequest(string host, int port, string path, string bearer) =>
            $"GET {path} HTTP/1.1\r\n" +
            $"Host: {host}:{port}\r\n" +
            $"Authorization: Bearer {bearer}\r\n" +
            $"Accept: */*\r\n" +
            $"Connection: close\r\n" +
            $"\r\n";

        // The response, once something else has sent the request. Takes a Stream
        // rather than reaching for one, which is what makes the rules below
        // testable: a non-200 is a null and not an exception, and a body is
        // bounded whatever the headers claimed.
        internal static async Task<byte[]?> ReadResponseAsync(Stream stream, CancellationToken ct)
        {
            var headers = await ReadHeadersAsync(stream, ct);
            if (!headers.StartsWith("HTTP/1.1 200", StringComparison.Ordinal)) return null;

            var length = ContentLength(headers);

            // Connection: close means the body ends when the stream does, so a
            // missing Content-Length is read to exhaustion rather than treated
            // as an error.
            using var body = new MemoryStream();
            var buffer = new byte[16 * 1024];

            while (length < 0 || body.Length < length)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0) break;

                body.Write(buffer, 0, read);

                // A ceiling rather than a limit anyone should hit for a picture,
                // and a malformed length header shouldn't be able to fill memory.
                // Note what happens at it: the loop stops once the body has
                // *exceeded* the ceiling and returns what it has, truncated,
                // rather than null — so a body longer than MaxBodyBytes is by
                // construction incomplete, which CB-272's clip guard relies on.
                if (body.Length > MaxBodyBytes) break;
            }

            // A stream that ended before the declared length is a dropped
            // connection, not a short answer: returning the partial body would
            // let FetchMediaAsync cache it and, for a clip, hand half a file to
            // the player. The ceiling case is exempt on purpose — it stops
            // early by design and is what OpenClawVideo.IsTruncated catches.
            if (length >= 0 && body.Length < length && body.Length <= MaxBodyBytes) return null;

            return body.ToArray();
        }

        internal const long MaxBodyBytes = 32 * 1024 * 1024;

        internal static long ContentLength(string headers)
        {
            foreach (var line in headers.Split("\r\n"))
            {
                if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
                if (long.TryParse(line["Content-Length:".Length..].Trim(), out var value)) return value;
            }

            return -1;
        }

        // The HTTP/1.1 Upgrade half of RFC 6455. Hand-rolled because
        // ClientWebSocket owns both the TLS and the upgrade and won't hand over
        // a stream for one without the other.
        internal static async Task UpgradeAsync(Stream stream, string host, int port, CancellationToken ct)
        {
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

            var request =
                $"GET / HTTP/1.1\r\n" +
                $"Host: {host}:{port}\r\n" +
                $"Upgrade: websocket\r\n" +
                $"Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Key: {key}\r\n" +
                $"Sec-WebSocket-Version: 13\r\n" +
                $"\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
            await stream.FlushAsync(ct);

            var response = await ReadHeadersAsync(stream, ct);

            if (!response.StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
            {
                var statusLine = response.Split("\r\n")[0];
                throw new IOException($"gateway refused the WebSocket upgrade: {statusLine}");
            }

            var expected = Convert.ToBase64String(
                SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));

            if (!response.Contains(expected, StringComparison.Ordinal))
            {
                throw new IOException("gateway's Sec-WebSocket-Accept did not match the key we sent");
            }
        }

        // One byte at a time, deliberately. The response headers are followed
        // immediately by WebSocket frames on the same stream, so a buffered read
        // would swallow the first frame — and the frame it would swallow is
        // connect.challenge, without which nothing else can happen.
        internal static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken ct)
        {
            var buffer = new byte[1];
            var sb = new StringBuilder();

            while (sb.Length < 16 * 1024)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0) throw new IOException("gateway closed the connection during the upgrade");

                sb.Append((char)buffer[0]);

                if (sb.Length >= 4
                    && sb[^1] == '\n' && sb[^2] == '\r'
                    && sb[^3] == '\n' && sb[^4] == '\r')
                {
                    return sb.ToString();
                }
            }

            throw new IOException("gateway sent no end of headers");
        }

        // BouncyCastle's blocking TLS stream cannot be read and written at the
        // same time from different threads, and a WebSocket client does exactly
        // that: a receive loop parked in ReadAsync while requests are sent from
        // wherever the caller happens to be. Left that way it does not throw —
        // it silently transmits nothing. The gateway's challenge arrives, the
        // reply never leaves, and the connection is closed a moment later with
        // a perfectly ordinary 1000/NormalClosure and no explanation. The same
        // request sent sequentially, or from node, is answered normally, which
        // is how this was cornered.
        //
        // So the protocol is driven in BouncyCastle's non-blocking mode
        // instead: it never touches the network itself, we hand it ciphertext
        // and take ciphertext back, and the lock is held only for those
        // in-memory hand-offs. Every actual socket operation happens outside
        // it, so a parked read can't block a write.
        // Excluded whole, not member by member. Every method on it either
        // drives BouncyCastle's TLS state machine or reads and writes a
        // NetworkStream, and the two are not separable here: the class exists
        // precisely because the protocol hand-offs and the socket I/O have to
        // interleave in one object under one lock (see the comment above). Its
        // constructor takes a live NetworkStream and HandshakeAsync needs a peer
        // that completes a TLS 1.3 handshake, neither of which a headless runner
        // has. What the class is *for* — that a parked read must not block a
        // concurrent write — is a property of a real socket under real load and
        // is what docs/openclaw-findings.md records having measured.
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        private sealed class TlsDuplexStream : Stream
        {
            private readonly NetworkStream _net;
            private readonly TlsClientProtocol _tls;

            // Guards the protocol object alone — never held across socket I/O.
            private readonly object _gate = new();

            // Network writes are ordered by their own gate: TLS records must
            // reach the wire in the order the protocol produced them.
            //
            // Never disposed, deliberately: it owns nothing unmanaged, and
            // PumpOutAsync releases it in a `finally` that can run after Dispose
            // while a drain is mid-write. A disposed semaphore throws on Release
            // and on Wait, which replaced the write's real failure with an
            // ObjectDisposedException. Waiters on a closed stream fail on the
            // socket instead, which is the failure they should see.
            private readonly SemaphoreSlim _netWrite = new(1, 1);

            private readonly byte[] _cipherIn = new byte[16 * 1024];

            private TlsDuplexStream(NetworkStream net, TlsClientProtocol tls)
            {
                _net = net;
                _tls = tls;
            }

            public static async Task<TlsDuplexStream> HandshakeAsync(
                TcpClient tcp, TlsClient client, CancellationToken ct)
            {
                var net = tcp.GetStream();
                var tls = new TlsClientProtocol();
                var stream = new TlsDuplexStream(net, tls);

                // Starts the handshake; in non-blocking mode this returns with
                // the first flight waiting in the output buffer rather than
                // talking to anyone.
                tls.Connect(client);
                await stream.PumpOutAsync(ct);

                while (tls.IsHandshaking)
                {
                    var read = await net.ReadAsync(stream._cipherIn, ct);
                    if (read == 0) throw new IOException("gateway closed the connection during the TLS handshake");

                    lock (stream._gate) tls.OfferInput(stream._cipherIn, 0, read);
                    await stream.PumpOutAsync(ct);
                }

                return stream;
            }

            // Moves whatever ciphertext the protocol has produced onto the wire.
            private async Task PumpOutAsync(CancellationToken ct)
            {
                // The gate is taken for the whole drain, not per chunk.
                //
                // Both sides call this — a sender after handing the protocol
                // application data, and the receive loop after offering it a
                // record, because an inbound record can require an outbound one.
                // Taking a chunk and then queuing for the network separately let
                // two callers interleave: A pulls record 1, B pulls record 2, B
                // reaches the socket first, and the peer receives them out of
                // order. TLS does not survive that, and it would present as the
                // connection dropping under load rather than as a race.
                //
                // Held across the socket write, which is safe because the
                // protocol lock inside is only ever taken for in-memory work.
                await _netWrite.WaitAsync(ct);

                try
                {
                    while (true)
                    {
                        byte[]? chunk = null;

                        lock (_gate)
                        {
                            var available = _tls.GetAvailableOutputBytes();
                            if (available > 0)
                            {
                                chunk = new byte[available];
                                _tls.ReadOutput(chunk, 0, available);
                            }
                        }

                        if (chunk is null) return;

                        await _net.WriteAsync(chunk, ct);
                        await _net.FlushAsync(ct);
                    }
                }
                finally
                {
                    _netWrite.Release();
                }
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                while (true)
                {
                    lock (_gate)
                    {
                        if (_tls.GetAvailableInputBytes() > 0)
                        {
                            var take = Math.Min(buffer.Length, _tls.GetAvailableInputBytes());
                            var plain = new byte[take];
                            _tls.ReadInput(plain, 0, take);
                            plain.CopyTo(buffer.Span);
                            return take;
                        }
                    }

                    // Outside the lock on purpose — this is the parked read that
                    // would otherwise block every send.
                    var read = await _net.ReadAsync(_cipherIn, ct);
                    if (read == 0) return 0;

                    lock (_gate) _tls.OfferInput(_cipherIn, 0, read);

                    // A record arriving can require one to go back (key update,
                    // close_notify, an alert), so give the protocol its turn.
                    await PumpOutAsync(ct);
                }
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            {
                lock (_gate) _tls.WriteApplicationData(buffer.ToArray(), 0, buffer.Length);
                await PumpOutAsync(ct);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
                WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
                ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

            // The synchronous pair exists because Stream demands it. Nothing in
            // this path calls them: the WebSocket is async throughout.
            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

            public override void Write(byte[] buffer, int offset, int count) =>
                WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

            public override void Flush() { }
            public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

            public override bool CanRead => true;
            public override bool CanWrite => true;
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
                if (disposing)
                {
                    try { _net.Dispose(); } catch { }
                }

                base.Dispose(disposing);
            }
        }

        // Trusts exactly one certificate: the one whose sha256 matches the pin,
        // or — on first contact, with no pin yet — whatever it presents, which
        // the caller then records and shows the user.
        //
        // Name validation is deliberately absent rather than merely lax: the
        // certificate this gateway serves is self-signed with **no
        // subjectAltName at all**, so there is no name to check against, for
        // any host or address. The fingerprint is the whole of the identity
        // check, which is why it must not be skipped once known.
        internal sealed class PinnedTlsClient : DefaultTlsClient
        {
            private readonly string? _pinned;

            public string? Fingerprint { get; private set; }

            public PinnedTlsClient(string? pinned)
                : base(new BcTlsCrypto(new SecureRandom()))
            {
                _pinned = pinned;
            }

            // TLS 1.3 only. Offering 1.2 as well would be pointless — the
            // gateway answers a 1.2 hello with alert 70 — and it would make a
            // downgrade the silent outcome of a future misconfiguration rather
            // than an error.
            protected override ProtocolVersion[] GetSupportedVersions() =>
                ProtocolVersion.TLSv13.Only();

            public override TlsAuthentication GetAuthentication() =>
                new PinnedAuthentication(this);

            private sealed class PinnedAuthentication : TlsAuthentication
            {
                private readonly PinnedTlsClient _owner;

                public PinnedAuthentication(PinnedTlsClient owner) => _owner = owner;

                public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
                {
                    var chain = serverCertificate?.Certificate;
                    if (chain is null || chain.IsEmpty)
                        throw new TlsFatalAlert(AlertDescription.bad_certificate);

                    var leaf = chain.GetCertificateAt(0).GetEncoded();
                    _owner.Fingerprint = Convert.ToHexStringLower(SHA256.HashData(leaf));

                    if (string.IsNullOrEmpty(_owner._pinned)) return;   // trust on first use

                    if (!string.Equals(_owner.Fingerprint, _owner._pinned, StringComparison.OrdinalIgnoreCase))
                        throw new TlsFatalAlert(AlertDescription.bad_certificate);
                }

                // The gateway authenticates us with a signed device identity
                // inside the protocol, not with a client certificate.
                public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest) => null!;
            }
        }
    }
}
