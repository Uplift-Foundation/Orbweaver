using System.Linq;
using Xunit;

namespace Orbweaver.Tests;

// CB-445: a Paperclip agent's picture, fetched with the board key and kept as a file.
public sealed class PaperclipPicturesTests : IDisposable
{
    // An 8x8 PNG, generated rather than drawn: a real one so the decoder tests below are real too.
    internal static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAAEklEQVR4nGP438DwHx9mGBkKAFPWn4ETg8N3AAAAAElFTkSuQmCC");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb445-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 11, 24, 4, 0, 0, DateTimeKind.Utc);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { } }

    private PaperclipPictureCache Cache() => new(_dir, () => _now);

    private static PaperclipBytes PngOk(byte[]? body = null, string type = "image/png") =>
        new(PaperclipHttpKind.Ok, 200, body ?? Png, type);

    private const string Asset = "/api/assets/a1/content";
    private const string Generated = "/api/agent-avatars/cap-v1/blue/rest.png?size=512&scale=1";

    [Theory]
    [InlineData("/api/assets/a1/content", true)]
    [InlineData("/api/agent-avatars/cap-v1/blue/rest.png?size=512&scale=1", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("/api/", false)]
    [InlineData("http://evil.example/api/x", false)]
    [InlineData("//evil.example/api/x", false)]
    [InlineData("/api//evil.example", false)]
    [InlineData("/other/assets/x", false)]
    [InlineData("/api/../secret", false)]
    [InlineData("/api/a\\b", false)]
    [InlineData("/api/a\nb", false)]
    [InlineData("/api/a b", false)]
    public void OnlyAnApiPathIsEverFetched(string? url, bool fetchable) =>
        Assert.Equal(fetchable, PaperclipPictureCache.IsFetchable(url));

    [Fact]
    public void AnOverlongPath_IsNotFetched() =>
        Assert.False(PaperclipPictureCache.IsFetchable("/api/" + new string('a', 600)));

    [Fact]
    public async Task APicture_IsFetchedOnce_KeptAsAFile_AndNotFetchedAgain()
    {
        var api = new FakePaperclip();
        api.Pictures[Asset] = PngOk();
        var cache = Cache();
        await cache.SyncAsync([Asset, Asset, null], api, default);
        var file = cache.FileFor(Asset);
        Assert.NotNull(file);
        Assert.Equal(Png, File.ReadAllBytes(file!));
        Assert.StartsWith(_dir, file);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file!));

        await cache.SyncAsync([Asset], api, default);
        Assert.Single(api.PictureCalls);
        Assert.Null(cache.FileFor("/api/assets/other/content"));
        Assert.Null(cache.FileFor(null));
    }

    [Fact]
    public async Task AChangedPicture_IsANewUrl_SoItIsFetchedAndTheOldOneStaysUntilTheCapacityPushesItOut()
    {
        var api = new FakePaperclip();
        api.Pictures[Asset] = PngOk();
        api.Pictures["/api/assets/a2/content"] = PngOk();
        var cache = Cache();
        await cache.SyncAsync([Asset], api, default);
        await cache.SyncAsync(["/api/assets/a2/content"], api, default);
        Assert.NotEqual(cache.FileFor(Asset), cache.FileFor("/api/assets/a2/content"));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task TheOldestPictureGoesPastTheCapacity_ButNeverOneAnAgentStillNames()
    {
        var api = new FakePaperclip();
        var first = Enumerable.Range(0, PaperclipPictureCache.Capacity).Select(i => $"/api/assets/p{i}/content").ToList();
        foreach (var u in first) api.Pictures[u] = PngOk();
        api.Pictures["/api/assets/new1/content"] = PngOk();
        api.Pictures["/api/assets/new2/content"] = PngOk();
        var cache = Cache();
        await cache.SyncAsync(first, api, default);
        Assert.Equal(PaperclipPictureCache.Capacity, cache.Count);
        var oldest = cache.FileFor(first[0])!;
        var kept = cache.FileFor(first[1])!;

        // p1 is still named, so it survives; p0 and p2 are the oldest not named.
        await cache.SyncAsync(["/api/assets/new1/content", "/api/assets/new2/content", first[1]], api, default);
        Assert.Equal(PaperclipPictureCache.Capacity, cache.Count);
        Assert.Null(cache.FileFor(first[0]));
        Assert.False(File.Exists(oldest));
        Assert.NotNull(cache.FileFor(first[1]));
        Assert.True(File.Exists(kept));
        Assert.Null(cache.FileFor(first[2]));
        Assert.NotNull(cache.FileFor("/api/assets/new2/content"));
    }

    [Theory]
    [InlineData((int)PaperclipHttpKind.NotFound, 404)]
    [InlineData((int)PaperclipHttpKind.Unauthorized, 401)]
    [InlineData((int)PaperclipHttpKind.ServerError, 500)]
    [InlineData((int)PaperclipHttpKind.Timeout, 0)]
    [InlineData((int)PaperclipHttpKind.Unreachable, 0)]
    public async Task ARefusedOrFailedFetch_IsNoPicture_AndRetriedOnlyAfterAMinute(int kind, int code)
    {
        var api = new FakePaperclip();
        api.Pictures[Asset] = new PaperclipBytes((PaperclipHttpKind)kind, code, Array.Empty<byte>(), null);
        var cache = Cache();
        await cache.SyncAsync([Asset], api, default);
        Assert.Null(cache.FileFor(Asset));
        await cache.SyncAsync([Asset], api, default);
        Assert.Single(api.PictureCalls);

        _now += PaperclipPictureCache.RetryAfter;
        api.Pictures[Asset] = PngOk();
        await cache.SyncAsync([Asset], api, default);
        Assert.Equal(2, api.PictureCalls.Count);
        Assert.NotNull(cache.FileFor(Asset));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/jpeg")]
    [InlineData(null)]
    public async Task AnAnswerThatIsNotAPng_IsNoPicture(string? type)
    {
        var api = new FakePaperclip();
        api.Pictures[Asset] = PngOk(type: type!);
        var logged = new List<string>();
        var cache = Cache();
        await cache.SyncAsync([Asset], api, default, logged.Add);
        Assert.Null(cache.FileFor(Asset));
        Assert.Contains(logged, l => l.Contains("not a PNG"));
    }

    [Fact]
    public async Task AnImageTypeOverBytesThatAreNotAPng_IsNoPicture()
    {
        var api = new FakePaperclip();
        api.Pictures[Asset] = PngOk(body: System.Text.Encoding.UTF8.GetBytes("<html>sign in</html>"));
        var cache = Cache();
        await cache.SyncAsync([Asset], api, default);
        Assert.Null(cache.FileFor(Asset));
        Assert.True(PaperclipPictureCache.LooksLikePng(Png));
        Assert.False(PaperclipPictureCache.LooksLikePng(new byte[] { 0x89, 0x50 }));
    }

    [Fact]
    public async Task AUrlThatIsNotAnApiPath_IsNeverAskedFor()
    {
        var api = new FakePaperclip();
        await Cache().SyncAsync(["http://evil.example/api/x", "//evil.example/a"], api, default);
        Assert.Empty(api.PictureCalls);
    }

    [Fact]
    public async Task AFileThatCannotBeWritten_IsNoPicture_AndSaysSo()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "cb445-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "a file where the directory should be");
        try
        {
            var api = new FakePaperclip();
            api.Pictures[Asset] = PngOk();
            var logged = new List<string>();
            var cache = new PaperclipPictureCache(Path.Combine(blocker, "sub"), () => _now);
            await cache.SyncAsync([Asset], api, default, logged.Add);
            Assert.Null(cache.FileFor(Asset));
            Assert.Contains(logged, l => l.Contains("could not be kept"));
        }
        finally { File.Delete(blocker); }
    }

    [Fact]
    public async Task ACancelledFetch_IsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        var api = new CancellingApi(cts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Cache().SyncAsync([Asset], api, cts.Token));
    }

    private sealed class CancellingApi(CancellationTokenSource cts) : IPaperclipApi
    {
        public Task<PaperclipResponse> GetAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipResponse> PostAsync(string path, string jsonBody, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaperclipBytes> GetBytesAsync(string path, CancellationToken ct)
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new PaperclipBytes(PaperclipHttpKind.Ok, 200, Array.Empty<byte>(), null));
        }
    }

    [Fact]
    public async Task FilesLeftByAnEarlierRun_AreDroppedOnce_AndANewOneIsKept()
    {
        Directory.CreateDirectory(_dir);
        var stale = Path.Combine(_dir, "old.png");
        File.WriteAllBytes(stale, Png);
        var api = new FakePaperclip();
        api.Pictures[Asset] = PngOk();
        var cache = Cache();
        await cache.SyncAsync([Asset], api, default);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(cache.FileFor(Asset)!));
        // Once only: a later sync does not delete what the cache is holding.
        await cache.SyncAsync([Asset], api, default);
        Assert.True(File.Exists(cache.FileFor(Asset)!));
    }

    [Fact]
    public async Task APruneThatCannotListTheDirectory_IsNotAnError()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "cb445-notdir-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "x");
        try { await new PaperclipPictureCache(blocker, () => _now).SyncAsync([Asset], new FakePaperclip(), default); }
        finally { File.Delete(blocker); }
    }

    [Fact]
    public void TheAgentsListCarriesItsPictureUrl_AndOneWithoutReadsNull()
    {
        var rows = PaperclipApi.ParseAgents(
            "[{\"id\":\"a\",\"avatarUrl\":\"/api/assets/a1/content\"},{\"id\":\"b\"},{\"id\":\"c\",\"avatarUrl\":\"\"}]");
        Assert.Equal(new string?[] { "/api/assets/a1/content", null, null }, rows.Select(r => r.AvatarUrl).ToArray());
    }

    [Fact]
    public void ComposeCarriesTheUrl_ButNotAFile()
    {
        var raw = new PaperclipApi.RawAgent("a", "A", "r", "t", "idle", null, "x", 0, 0, null, null, null, Asset);
        var a = Assert.Single(PaperclipApi.Compose(new PaperclipApi.ComposeInput(
            [raw], [], [], [], [], [], new Dictionary<string, string>(), "http://127.0.0.1:3100", "EX", DateTime.UtcNow)));
        Assert.Equal((Asset, null), (a.AvatarUrl, a.AvatarFile));
    }

    [Fact]
    public void ThePictureOfAnAgent_IsPartOfWhatIsDrawn_SoItsArrivalRedrawsTheOrb()
    {
        string Sig(string? file) => PaperclipSessions.DrawSignature(
            [new PaperclipAgent("a", "A", "r", "t", "idle", null, "x", "idle", PaperclipMark.None, null, [], null, null, null, null, null,
                DateTime.UtcNow, "u", false, AvatarFile: file)], PaperclipLink.Connected, "ok");
        Assert.NotEqual(Sig(null), Sig("/x/1.png"));
        Assert.NotEqual(Sig("/x/1.png"), Sig("/x/2.png"));
        Assert.Equal(Sig("/x/1.png"), Sig("/x/1.png"));
    }

    // ---- through the poll step ----

    private static string AgentsWithPictures(params (string Id, string? Url)[] agents) =>
        "[" + string.Join(",", agents.Select(a => $"{{\"id\":\"{a.Id}\",\"name\":\"{a.Id}\",\"status\":\"idle\"" +
            (a.Url is null ? "" : $",\"avatarUrl\":\"{a.Url}\"") + "}")) + "]";

    private static FakePaperclip Company(string agents)
    {
        var f = new FakePaperclip();
        string P(string t) => "/api/companies/co1/" + t;
        f.Gets["/api/companies"] = FakePaperclip.Ok("[{\"id\":\"co1\",\"name\":\"C\",\"issuePrefix\":\"EX\"}]");
        f.Gets[P("agents")] = FakePaperclip.Ok(agents);
        f.Gets[P("live-runs")] = FakePaperclip.Ok("[]");
        f.Gets[P("approvals?status=pending")] = FakePaperclip.Ok("[]");
        f.Gets[P("tools/action-requests")] = FakePaperclip.Ok("{\"actionRequests\":[]}");
        f.Gets[P("issues?status=blocked")] = FakePaperclip.Ok("[]");
        f.Gets[P("heartbeat-runs?limit=20")] = FakePaperclip.Ok("[]");
        f.Gets[P("issues?status=in_progress")] = FakePaperclip.Ok("[]");
        return f;
    }

    private static readonly PaperclipSessions.PaperclipConfig Cfg = new(true, PaperclipFx.Base, "co1", "KEY", null, "fold");

    [Fact]
    public async Task ThePollFetchesEachAgentsPicture_AndPublishesTheFile_WhileAFailedOneKeepsInitialsAndTheLinkConnected()
    {
        var f = Company(AgentsWithPictures(("a", Asset), ("b", Generated), ("c", "/api/assets/gone/content"), ("d", null)));
        f.Pictures[Asset] = PngOk();
        f.Pictures[Generated] = PngOk();
        var cache = Cache();
        var r = await PaperclipSessions.StepAsync(f, Cfg, PaperclipSessions.PollState.Initial, PaperclipFx.Now, default, null, cache);
        Assert.Equal(PaperclipLink.Connected, r.Next.Link);
        var by = r.Next.Published!.ToDictionary(a => a.Id);
        Assert.Equal(cache.FileFor(Asset), by["a"].AvatarFile);
        Assert.Equal(cache.FileFor(Generated), by["b"].AvatarFile);
        Assert.NotNull(by["a"].AvatarFile);
        Assert.Null(by["c"].AvatarFile);
        Assert.Null(by["d"].AvatarFile);
        Assert.DoesNotContain("couldn't", r.Next.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, f.PictureCalls.Count);
    }

    [Fact]
    public async Task APictureThatArrivesOnALaterTick_ReachesThePublishedAgent()
    {
        var f = Company(AgentsWithPictures(("a", Asset)));
        var cache = Cache();
        var first = await PaperclipSessions.StepAsync(f, Cfg, PaperclipSessions.PollState.Initial, PaperclipFx.Now, default, null, cache);
        Assert.Null(first.Next.Published!.Single().AvatarFile);
        f.Pictures[Asset] = PngOk();
        _now += PaperclipPictureCache.RetryAfter;
        var second = await PaperclipSessions.StepAsync(f, Cfg, first.Next, PaperclipFx.Now.AddMinutes(2), default, null, cache);
        Assert.NotNull(second.Next.Published!.Single().AvatarFile);
    }
}
