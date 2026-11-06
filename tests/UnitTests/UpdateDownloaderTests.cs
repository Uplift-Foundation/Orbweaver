using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Orbweaver.Tests;

// The downloader's decisions with the network replaced by a fake: ordering (sums
// before bytes), what is on disk after each kind of failure, and progress. The
// real HttpClient is covered in IntegrationTests/UpdateDownloadTests.
public class UpdateDownloaderTests : IDisposable
{
    private const string Rid = "osx-arm64";
    private const string Name = "Orbweaver-1.2.3-osx-arm64.dmg";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("the installer bytes");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "upd-dl-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static UpdateRelease Release(bool sums = true) => new("v1.2.3", "1.2.3", false, "",
        new UpdateAsset[] { new(Name, "http://f/installer", Payload.Length) }
            .Concat(sums ? new[] { new UpdateAsset("SHA256SUMS.txt", "http://f/sums", 10) } : []).ToList());

    private sealed class Fake : IUpdateHttp
    {
        public string Sums = "";
        public byte[] Body = Payload;
        public List<string> Calls { get; } = [];
        public Exception? FailDownloadWith;
        public bool WriteThenFail;

        public Task<string> GetStringAsync(string url, CancellationToken ct) { Calls.Add(url); return Task.FromResult(Sums); }

        public async Task DownloadAsync(string url, Stream destination, IProgress<long>? p, CancellationToken ct)
        {
            Calls.Add(url);
            if (WriteThenFail) { await destination.WriteAsync(Body.AsMemory(0, 3), ct); await destination.FlushAsync(ct); }
            if (FailDownloadWith is not null) throw FailDownloadWith;
            await destination.WriteAsync(Body, ct);
            p?.Report(Body.Length);
        }
    }

    private Fake Good() => new() { Sums = $"{Hex(Payload)}  {Name}\n" };

    [Fact]
    public async Task AGoodDownloadLeavesOnlyTheVerifiedFileAndFetchesSumsFirst()
    {
        var http = Good();
        var steps = new List<UpdateProgress>();
        var path = await UpdateDownloader.DownloadVerifiedAsync(Release(), Rid, _dir, http, new SyncProgress(steps.Add), default);

        Assert.Equal(Path.Combine(_dir, Name), path);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
        Assert.Equal(new[] { Name }, Directory.GetFiles(_dir).Select(Path.GetFileName));
        Assert.Equal(new[] { "http://f/sums", "http://f/installer" }, http.Calls);
        Assert.Equal(UpdateStage.Downloading, steps[0].Stage);
        Assert.Equal((long)Payload.Length, steps[0].BytesDone);
        Assert.Equal(UpdateStage.Verifying, steps[^1].Stage);
    }

    private sealed class SyncProgress(Action<UpdateProgress> a) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => a(value);
    }

    [Fact]
    public async Task ProgressIsOptional()
    {
        var path = await UpdateDownloader.DownloadVerifiedAsync(Release(), Rid, _dir, Good(), null, default);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task AStaleFileFromAnEarlierAttemptIsReplaced()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, Name), "old and wrong");
        await File.WriteAllTextAsync(Path.Combine(_dir, Name + ".partial"), "leftover");

        var path = await UpdateDownloader.DownloadVerifiedAsync(Release(), Rid, _dir, Good(), null, default);

        Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task AMismatchDiscardsEverythingAndSaysSo()
    {
        var http = Good();
        http.Body = Encoding.UTF8.GetBytes("tampered bytes!!!!");

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => UpdateDownloader.DownloadVerifiedAsync(Release(), Rid, _dir, http, null, default));

        Assert.Equal(UpdateFailure.ChecksumMismatch, ex.Kind);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task NoEntryInTheSumsMeansNoDownloadAtAll()
    {
        var http = new Fake { Sums = $"{Hex(Payload)}  some-other-file.dmg\n" };

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => UpdateDownloader.DownloadVerifiedAsync(Release(), Rid, _dir, http, null, default));

        Assert.Equal(UpdateFailure.ChecksumUnavailable, ex.Kind);
        Assert.Equal(new[] { "http://f/sums" }, http.Calls);   // the installer was never requested
    }

    [Fact]
    public async Task AReleaseWithoutSumsFailsBeforeAnyRequest()
    {
        var http = Good();
        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => UpdateDownloader.DownloadVerifiedAsync(Release(sums: false), Rid, _dir, http, null, default));
        Assert.Equal(UpdateFailure.NoChecksums, ex.Kind);
        Assert.Empty(http.Calls);
    }

    [Fact]
    public async Task ANetworkFailureMidDownloadLeavesNoPartialFile()
    {
        var http = Good();
        http.WriteThenFail = true;
        http.FailDownloadWith = new UpdateException(UpdateFailure.Network, "boom");

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => UpdateDownloader.DownloadVerifiedAsync(Release(), Rid, _dir, http, null, default));

        Assert.Equal(UpdateFailure.Network, ex.Kind);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task AnIoFailureFromTheDownloadStepIsStorageAndLeavesNoPartialFile()
    {
        var http = Good();
        http.WriteThenFail = true;
        http.FailDownloadWith = new IOException("disk full");

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => UpdateDownloader.DownloadVerifiedAsync(Release(), Rid, _dir, http, null, default));

        Assert.Equal(UpdateFailure.Storage, ex.Kind);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task CancellationLeavesNoPartialFileAndStaysACancellation()
    {
        var http = Good();
        http.WriteThenFail = true;
        http.FailDownloadWith = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => UpdateDownloader.DownloadVerifiedAsync(Release(), Rid, _dir, http, null, default));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task ADiskProblemBecomesAStorageFailure()
    {
        // A directory squatting on the .partial name: deleting it is refused
        // (swallowed), then opening it as a file fails, which must surface as
        // Storage rather than a raw UnauthorizedAccessException.
        Directory.CreateDirectory(Path.Combine(_dir, Name + ".partial"));

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => UpdateDownloader.DownloadVerifiedAsync(Release(), Rid, _dir, Good(), null, default));
        Assert.Equal(UpdateFailure.Storage, ex.Kind);
    }
}
