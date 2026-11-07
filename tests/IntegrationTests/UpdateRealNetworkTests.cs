using Xunit;

namespace Orbweaver.IntegrationTests;

// Opt-in: talks to the real api.github.com and downloads a real ~35MB installer.
// CI must not hit github.com, so it is skipped unless asked:
//
//   ORBWEAVER_NETWORK_TESTS=1 dotnet test tests/IntegrationTests --filter Category=Network
//
// It exists because every other update test uses a fixture server, and a fixture
// can only prove the code matches the fixture. This is the one that proves the
// fixture matches GitHub.
public sealed class NetworkFactAttribute : FactAttribute
{
    public NetworkFactAttribute()
    {
        if (BrandEnv.Get(BrandEnv.NetworkTests) != "1")
            Skip = "opt-in: hits the real api.github.com and downloads an installer (set ORBWEAVER_NETWORK_TESTS=1)";
    }
}

[Trait("Category", "Network")]
public sealed class UpdateRealNetworkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "upd-net-" + Guid.NewGuid().ToString("N"));
    private readonly HttpUpdateHttp _http = new();

    public void Dispose() { _http.Dispose(); try { Directory.Delete(_dir, true); } catch { } }

    [NetworkFact]
    public async Task AnOldBuildPicksTheRealLatestAndDownloadsAndVerifiesItsInstaller()
    {
        var releases = await new GitHubReleaseFeed(_http, UpdateFeed.DefaultBaseUrl).FetchAsync(false, default);
        Assert.Single(releases);

        // The documented lever: pretend to be 0.0.1.
        var current = UpdateChecker.ResolveCurrentVersion("0.0.1", null, out var warning);
        Assert.NotNull(warning);
        var decision = UpdateFeed.Pick(releases, current, false, null);

        Assert.Equal(UpdateDecisionKind.Available, decision.Kind);
        var release = decision.Release!;
        Assert.False(release.Prerelease);
        Assert.True(UpdateVersion.Compare(UpdateVersion.TryParse(release.Version, out var v) ? v! : null!, UpdateVersion.TryParse("1.0.3", out var floor) ? floor! : null!) >= 0,
            $"expected at least v1.0.3, feed said {release.Tag}");

        var path = await UpdateDownloader.DownloadVerifiedAsync(
            release, UpdateAssets.CurrentRid(), _dir, _http, null, default);

        Assert.True(new FileInfo(path).Length > 1_000_000);
        Assert.Single(Directory.GetFiles(_dir));   // no .partial left over
    }

    [NetworkFact]
    public async Task ABuildAtTheLatestVersionIsOfferedNothing()
    {
        var releases = await new GitHubReleaseFeed(_http, UpdateFeed.DefaultBaseUrl).FetchAsync(false, default);
        var latest = releases.Single().Version;

        Assert.Equal(UpdateDecisionKind.UpToDate, UpdateFeed.Pick(releases, latest, false, null).Kind);
    }

    [NetworkFact]
    public async Task ThePrereleaseListNeverOffersAnOldBetaToTheLatestStable()
    {
        var all = await new GitHubReleaseFeed(_http, UpdateFeed.DefaultBaseUrl).FetchAsync(true, default);
        Assert.Contains(all, r => r.Prerelease);   // the list endpoint does see betas
        var latestStable = all.Where(r => !r.Prerelease).Select(r => r.Version)
            .OrderByDescending(x => x, Comparer<string>.Create((a, b) =>
                UpdateVersion.Compare(UpdateVersion.TryParse(a, out var x1) ? x1! : null!, UpdateVersion.TryParse(b, out var y1) ? y1! : null!)))
            .First();

        Assert.Equal(UpdateDecisionKind.UpToDate, UpdateFeed.Pick(all, latestStable, true, null).Kind);
    }
}
