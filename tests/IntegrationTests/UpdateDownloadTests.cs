using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.IntegrationTests;

// CB-308's download path with the real HttpClient (HttpUpdateHttp) against a
// loopback HttpListener standing in for github.com — the seam the unit tests fake.
// What fails here and not there: status mapping off real responses, a body cut off
// mid-stream, a refused connection, and what is on disk afterwards.
public sealed class UpdateDownloadTests : IDisposable
{
    private const string Name = "Orbweaver-9.9.9-osx-arm64.dmg";
    private static readonly byte[] Payload = RandomNumberGenerator.GetBytes(300_000); // several buffer reads
    private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private readonly HttpListener _listener;
    private readonly string _root;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "upd-int-" + Guid.NewGuid().ToString("N"));
    private readonly HttpUpdateHttp _http = new();
    private readonly List<string> _paths = [];
    private Func<HttpListenerRequest, HttpListenerResponse, Task> _handler = (_, r) => { r.StatusCode = 404; return Task.CompletedTask; };

    public UpdateDownloadTests()
    {
        // Bound once, never probed and released (CB-314: the old pattern
        // collided with parallel tests as "Address already in use").
        (_listener, _root) = LoopbackHttp.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                _paths.Add(ctx.Request.Url!.PathAndQuery);
                try { await _handler(ctx.Request, ctx.Response); } catch { }
                try { ctx.Response.Close(); } catch { }
            }
        });
    }

    public void Dispose()
    {
        _listener.Close();
        _http.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static async Task Body(HttpListenerResponse r, byte[] bytes, int status = 200)
    {
        r.StatusCode = status;
        r.ContentLength64 = bytes.Length;
        await r.OutputStream.WriteAsync(bytes);
    }

    private UpdateRelease Release() => new("v9.9.9", "9.9.9", false, "",
        [new UpdateAsset(Name, _root + "/dl/" + Name, Payload.Length), new UpdateAsset("SHA256SUMS.txt", _root + "/dl/SHA256SUMS.txt", 100)]);

    private void Serve(byte[] installer, string sums) => _handler = async (req, res) =>
    {
        if (req.Url!.AbsolutePath.EndsWith("SHA256SUMS.txt")) await Body(res, Encoding.UTF8.GetBytes(sums));
        else await Body(res, installer);
    };

    [Fact]
    public async Task AGoodDownloadVerifiesAndLandsAtItsRealName()
    {
        Serve(Payload, $"{Hex(Payload)}  {Name}\n");
        var seen = new List<long>();

        var path = await UpdateDownloader.DownloadVerifiedAsync(Release(), "osx-arm64", _dir, _http,
            new SyncProgress(p => { if (p.Stage == UpdateStage.Downloading) seen.Add(p.BytesDone); }), default);

        Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(_dir));
        Assert.Equal(Payload.Length, seen.Max());
    }

    private sealed class SyncProgress(Action<UpdateProgress> a) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress v) => a(v);
    }

    [Fact]
    public async Task OneFlippedByteIsRefusedAndNothingIsLeftOnDisk()
    {
        var tampered = (byte[])Payload.Clone();
        tampered[tampered.Length / 2] ^= 0xFF;
        Serve(tampered, $"{Hex(Payload)}  {Name}\n");

        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            UpdateDownloader.DownloadVerifiedAsync(Release(), "osx-arm64", _dir, _http, null, default));

        Assert.Equal(UpdateFailure.ChecksumMismatch, ex.Kind);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task NoSumsEntryMeansTheInstallerIsNeverRequested()
    {
        Serve(Payload, $"{Hex(Payload)}  something-else.dmg\n");

        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            UpdateDownloader.DownloadVerifiedAsync(Release(), "osx-arm64", _dir, _http, null, default));

        Assert.Equal(UpdateFailure.ChecksumUnavailable, ex.Kind);
        Assert.DoesNotContain(_paths, p => p.EndsWith(Name));
    }

    [Fact]
    public async Task A404OnTheInstallerIsANetworkFailureWithNoFileLeft()
    {
        _handler = async (req, res) =>
        {
            if (req.Url!.AbsolutePath.EndsWith("SHA256SUMS.txt")) await Body(res, Encoding.UTF8.GetBytes($"{Hex(Payload)}  {Name}\n"));
            else res.StatusCode = 404;
        };

        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            UpdateDownloader.DownloadVerifiedAsync(Release(), "osx-arm64", _dir, _http, null, default));

        Assert.Equal(UpdateFailure.Network, ex.Kind);
        Assert.Contains("404", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    public async Task GitHubsRateLimitStatusesGetTheirOwnKind(int status)
    {
        _handler = (_, res) => { res.StatusCode = status; return Task.CompletedTask; };

        var ex = await Assert.ThrowsAsync<UpdateException>(() => _http.GetStringAsync(_root + "/releases/latest", default));
        Assert.Equal(UpdateFailure.RateLimited, ex.Kind);
    }

    [Fact]
    public async Task ABodyCutOffMidStreamIsANetworkFailureAndLeavesNoPartialFile()
    {
        _handler = async (req, res) =>
        {
            if (req.Url!.AbsolutePath.EndsWith("SHA256SUMS.txt")) { await Body(res, Encoding.UTF8.GetBytes($"{Hex(Payload)}  {Name}\n")); return; }
            res.ContentLength64 = Payload.Length;             // promise it all...
            await res.OutputStream.WriteAsync(Payload.AsMemory(0, 1000));
            await res.OutputStream.FlushAsync();
            res.Abort();                                       // ...deliver 1000 bytes
        };

        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            UpdateDownloader.DownloadVerifiedAsync(Release(), "osx-arm64", _dir, _http, null, default));

        Assert.Equal(UpdateFailure.Network, ex.Kind);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("disk full");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("disk full");
    }

    [Fact]
    public async Task AFailureWritingTheFileIsNotMistakenForTheNetwork()
    {
        // The other half of the read/write split: the server is fine and the body
        // arrives, and it is the destination that dies. That must stay an
        // IOException for the caller to call Storage, not become Network. (The
        // Windows-only bug was the reverse: a read failure surfacing as IOException
        // and being called Storage.)
        Serve(Payload, "");

        await Assert.ThrowsAsync<IOException>(() =>
            _http.DownloadAsync(_root + "/dl/" + Name, new ThrowingStream(), null, default));
    }

    [Fact]
    public async Task ARefusedConnectionIsANetworkFailure()
    {
        // Held but never listened on, so the refusal is guaranteed for the
        // whole test rather than until a parallel test binds the port.
        var (holder, dead) = LoopbackHttp.RefusingPort();
        using var _ = holder;

        var ex = await Assert.ThrowsAsync<UpdateException>(() => _http.GetStringAsync($"http://127.0.0.1:{dead}/x", default));
        Assert.Equal(UpdateFailure.Network, ex.Kind);

        using var sink = new MemoryStream();
        var ex2 = await Assert.ThrowsAsync<UpdateException>(() => _http.DownloadAsync($"http://127.0.0.1:{dead}/x", sink, null, default));
        Assert.Equal(UpdateFailure.Network, ex2.Kind);
    }

    [Fact]
    public async Task CallerCancellationStaysACancellation()
    {
        _handler = async (_, _) => await Task.Delay(TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _http.GetStringAsync(_root + "/slow", cts.Token));
    }

    [Fact]
    public async Task AServerThatNeverAnswersIsGivenUpOnAsANetworkFailure()
    {
        _handler = async (_, _) => await Task.Delay(TimeSpan.FromSeconds(30));
        using var impatient = new HttpUpdateHttp(TimeSpan.FromMilliseconds(150));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => impatient.GetStringAsync(_root + "/hang", default));
        Assert.Equal(UpdateFailure.Network, ex.Kind);
    }

    [Fact]
    public async Task TheFeedSpeaksToTheServerAtTheOverrideUrlAndSendsAUserAgent()
    {
        string? ua = null, accept = null;
        var latest = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "update", "latest.json"));
        _handler = async (req, res) =>
        {
            ua = req.UserAgent; accept = req.Headers["Accept"];
            await Body(res, Encoding.UTF8.GetBytes(latest));
        };

        var feed = new GitHubReleaseFeed(_http, _root + "/");
        var releases = await feed.FetchAsync(includePrerelease: false, default);

        Assert.Equal("/releases/latest", _paths[0]);
        Assert.Equal("v1.0.3", releases.Single().Tag);
        Assert.StartsWith("Orbweaver/", ua);
        Assert.Contains("application/vnd.github+json", accept);
    }

    [Fact]
    public async Task AGarbageFeedResponseIsMalformedNotACrash()
    {
        _handler = (_, res) => Body(res, Encoding.UTF8.GetBytes("<html>captive portal</html>"));

        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            new GitHubReleaseFeed(_http, _root).FetchAsync(true, default));
        Assert.Equal(UpdateFailure.Malformed, ex.Kind);
    }
}
