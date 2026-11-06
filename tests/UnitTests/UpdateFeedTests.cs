using Xunit;

namespace Orbweaver.Tests;

// Against the real GitHub API responses captured 2026-11-06 (Fixtures/update),
// not hand-written JSON: latest.json is /releases/latest, releases.json is
// /releases?per_page=10 trimmed to v1.0.3, v1.0.2 and v0.5.11-beta.
public class UpdateFeedTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "update", name));

    private static UpdateRelease Rel(string tag, bool pre = false) =>
        new(tag, tag.TrimStart('v'), pre, "", []);

    [Fact]
    public void ParsesTheRealLatestResponse()
    {
        var r = UpdateFeed.ParseLatest(Fixture("latest.json"))!;
        Assert.Equal("v1.0.3", r.Tag);
        Assert.Equal("1.0.3", r.Version);
        Assert.False(r.Prerelease);
        Assert.False(string.IsNullOrWhiteSpace(r.Body));
        Assert.Contains(r.Assets, a => a.Name == "Orbweaver-1.0.3-osx-arm64.dmg"
            && a.Url.StartsWith("https://github.com/Uplift-Foundation/Orbweaver/releases/download/v1.0.3/")
            && a.Size > 1_000_000);
        Assert.Contains(r.Assets, a => a.Name == "SHA256SUMS.txt");
    }

    [Fact]
    public void ParsesTheRealListResponseIncludingThePrerelease()
    {
        var list = UpdateFeed.ParseList(Fixture("releases.json"));
        Assert.Equal(new[] { "v1.0.3", "v1.0.2", "v0.5.11-beta" }, list.Select(r => r.Tag));
        Assert.True(list.Single(r => r.Tag == "v0.5.11-beta").Prerelease);
        Assert.False(list.Single(r => r.Tag == "v1.0.2").Prerelease);
    }

    [Fact]
    public void DraftsAndUnparseableTagsAreDroppedWithoutFailingTheList()
    {
        var json = """
        [ {"tag_name":"v2.0.0","draft":true},
          {"tag_name":"nightly"},
          {"tag_name":null},
          7,
          {"tag_name":"v1.5.0","prerelease":"yes","body":null,"assets":[
              {"name":"ok.dmg","browser_download_url":"http://x/ok.dmg","size":12},
              {"name":"nosize.dmg","browser_download_url":"http://x/n.dmg","size":"big"},
              {"name":"nourl.dmg"}, {"browser_download_url":"http://x/noname"}, 5 ]},
          {"tag_name":"v1.4.0","assets":"not an array"} ]
        """;
        var list = UpdateFeed.ParseList(json);

        Assert.Equal(new[] { "v1.5.0", "v1.4.0" }, list.Select(r => r.Tag));
        var r = list[0];
        Assert.False(r.Prerelease);                 // wrong-typed flag reads as false, not a throw
        Assert.Equal("", r.Body);
        Assert.Equal(new[] { ("ok.dmg", 12L), ("nosize.dmg", 0L) }, r.Assets.Select(a => (a.Name, a.Size)));
        Assert.Empty(list[1].Assets);
    }

    [Fact]
    public void WrongTypedAndBlankFieldsReadAsAbsent()
    {
        var json = """
        [ {"tag_name":"v1.0.0","body":5,"assets":[
              {"name":"nosize.dmg","browser_download_url":"http://x/a"},
              {"name":"objsize.dmg","browser_download_url":"http://x/b","size":{"n":1}},
              {"name":"   ","browser_download_url":"http://x/c"} ]},
          {"tag_name":"   "} ]
        """;
        var list = UpdateFeed.ParseList(json);
        var r = Assert.Single(list);
        Assert.Equal("", r.Body);
        Assert.Equal(new[] { ("nosize.dmg", 0L), ("objsize.dmg", 0L) }, r.Assets.Select(a => (a.Name, a.Size)));
    }

    [Fact]
    public void BuildMetadataNeverReachesTheVersionThatNamesTheInstallerFile()
    {
        // "v1.0.4+x/../../evil" parses as a version; its build metadata must not
        // survive into release.Version, which UpdateAssets turns into a file name.
        var r = UpdateFeed.ParseLatest("""{"tag_name":"v1.0.4+x/../../evil"}""")!;
        Assert.Equal("1.0.4", r.Version);
        Assert.DoesNotContain('/', UpdateAssets.ExpectedAssetName(r.Version, "osx-arm64"));
        Assert.Equal("1.0.4-rc.1", UpdateFeed.ParseLatest("""{"tag_name":"V1.0.4-rc.1+meta"}""")!.Version);
    }

    [Fact]
    public void ATagWithPrereleaseIdentifiersIsAPrereleaseEvenIfGitHubDidNotFlagIt()
    {
        var mislabelled = UpdateFeed.ParseLatest("""{"tag_name":"v2.0.0-beta","prerelease":false}""")!;
        Assert.True(mislabelled.Prerelease);
        Assert.Equal(UpdateDecisionKind.UpToDate, UpdateFeed.Pick([mislabelled], "1.0.3", false, null).Kind);
        Assert.False(UpdateFeed.ParseLatest("""{"tag_name":"v2.0.0","prerelease":false}""")!.Prerelease);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2")]
    [InlineData("\"a string\"")]
    [InlineData("{}")]
    public void ParseListRejectsAnythingButAnArray(string json)
    {
        var ex = Assert.Throws<UpdateException>(() => UpdateFeed.ParseList(json));
        Assert.Equal(UpdateFailure.Malformed, ex.Kind);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    public void ParseLatestRejectsAnythingButAnObject(string json)
    {
        var ex = Assert.Throws<UpdateException>(() => UpdateFeed.ParseLatest(json));
        Assert.Equal(UpdateFailure.Malformed, ex.Kind);
    }

    [Fact]
    public void ParseLatestOfADraftOrBadTagIsNothingToOffer()
    {
        Assert.Null(UpdateFeed.ParseLatest("""{"tag_name":"v9.9.9","draft":true}"""));
        Assert.Null(UpdateFeed.ParseLatest("""{"tag_name":"latest"}"""));
    }

    // ---- Pick ---------------------------------------------------------------

    [Fact]
    public void ANewerStableIsAvailable()
    {
        var d = UpdateFeed.Pick([Rel("v1.0.4"), Rel("v1.0.3")], "1.0.3", false, null);
        Assert.Equal(UpdateDecisionKind.Available, d.Kind);
        Assert.Equal("v1.0.4", d.Release!.Tag);
    }

    [Fact]
    public void ThePickIsTheHighestNotTheFirst()
    {
        var d = UpdateFeed.Pick([Rel("v1.0.4"), Rel("v1.2.0"), Rel("v1.1.0")], "1.0.3", false, null);
        Assert.Equal("v1.2.0", d.Release!.Tag);
    }

    [Theory]
    [InlineData("1.0.3")]   // same
    [InlineData("1.0.4")]   // running newer than the feed
    public void NothingNewerIsUpToDate(string current)
    {
        var d = UpdateFeed.Pick([Rel("v1.0.3")], current, false, null);
        Assert.Equal(UpdateDecisionKind.UpToDate, d.Kind);
        Assert.Null(d.Release);
    }

    [Fact]
    public void AnEmptyFeedIsUpToDate() =>
        Assert.Equal(UpdateDecisionKind.UpToDate, UpdateFeed.Pick([], "1.0.3", true, null).Kind);

    [Fact]
    public void AnUnparseableCurrentVersionNeverNags() =>
        Assert.Equal(UpdateDecisionKind.UpToDate, UpdateFeed.Pick([Rel("v9.0.0")], "dev", false, null).Kind);

    [Fact]
    public void PrereleasesAreInvisibleUnlessOptedIn()
    {
        // The negative control from the plan: a real stable build must NOT be
        // offered the (older-numbered) beta, and a newer beta is hidden without the opt-in.
        var feed = new[] { Rel("v1.1.0-beta", pre: true), Rel("v1.0.3") };
        Assert.Equal(UpdateDecisionKind.UpToDate, UpdateFeed.Pick(feed, "1.0.3", false, null).Kind);

        var optedIn = UpdateFeed.Pick(feed, "1.0.3", true, null);
        Assert.Equal("v1.1.0-beta", optedIn.Release!.Tag);
    }

    [Fact]
    public void AnOldBetaIsNeverOfferedToAStableBuildEvenOptedIn()
    {
        var feed = new[] { Rel("v0.5.11-beta", pre: true) };
        Assert.Equal(UpdateDecisionKind.UpToDate, UpdateFeed.Pick(feed, "1.0.3", true, null).Kind);
    }

    [Fact]
    public void SkippingExactlyTheNewestKeepsItQuietButReportsIt()
    {
        var d = UpdateFeed.Pick([Rel("v1.0.4")], "1.0.3", false, "v1.0.4");
        Assert.Equal(UpdateDecisionKind.Skipped, d.Kind);
        Assert.Equal("v1.0.4", d.Release!.Tag);
    }

    [Fact]
    public void ANewerReleaseThanTheSkippedOneIsOfferedAgain()
    {
        Assert.Equal(UpdateDecisionKind.Available, UpdateFeed.Pick([Rel("v1.0.5")], "1.0.3", false, "1.0.4").Kind);
    }

    [Fact]
    public void AnUnparseableSkipValueSkipsNothing() =>
        Assert.Equal(UpdateDecisionKind.Available, UpdateFeed.Pick([Rel("v1.0.4")], "1.0.3", false, "garbage").Kind);

    [Fact]
    public void AReleaseWhoseVersionDoesNotParseIsIgnored()
    {
        // Constructed directly (ParseList never lets one through), because Pick is
        // a public-ish seam and must not throw on a record built by something else.
        var bad = new UpdateRelease("vX", "X", false, "", []);
        Assert.Equal(UpdateDecisionKind.UpToDate, UpdateFeed.Pick([bad], "1.0.3", false, null).Kind);
    }

    [Theory]
    [InlineData(null, UpdateFeed.DefaultBaseUrl)]
    [InlineData("", UpdateFeed.DefaultBaseUrl)]
    [InlineData("  ", UpdateFeed.DefaultBaseUrl)]
    [InlineData("http://127.0.0.1:8099/", "http://127.0.0.1:8099")]
    [InlineData(" http://127.0.0.1:8099 ", "http://127.0.0.1:8099")]
    public void TheFeedUrlOverrideIsNormalised(string? given, string expected) =>
        Assert.Equal(expected, UpdateFeed.ResolveBaseUrl(given));

    // ---- GitHubReleaseFeed ---------------------------------------------------

    private sealed class FakeHttp : IUpdateHttp
    {
        public List<string> Urls { get; } = [];
        public Func<string, string> Body { get; init; } = _ => "";
        public Task<string> GetStringAsync(string url, CancellationToken ct) { Urls.Add(url); return Task.FromResult(Body(url)); }
        public Task DownloadAsync(string url, Stream destination, IProgress<long>? p, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task AStableCheckAsksLatestAndTheOptInAsksTheList()
    {
        var http = new FakeHttp { Body = u => u.EndsWith("/latest") ? Fixture("latest.json") : Fixture("releases.json") };
        var feed = new GitHubReleaseFeed(http, "http://fixture/");

        var stable = await feed.FetchAsync(false, default);
        var all = await feed.FetchAsync(true, default);

        Assert.Equal(new[] { "http://fixture/releases/latest", "http://fixture/releases?per_page=10" }, http.Urls);
        Assert.Single(stable);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task AStableCheckWhoseLatestIsADraftReturnsNothing()
    {
        var http = new FakeHttp { Body = _ => """{"tag_name":"v9.9.9","draft":true}""" };
        Assert.Empty(await new GitHubReleaseFeed(http, "").FetchAsync(false, default));
        Assert.StartsWith(UpdateFeed.DefaultBaseUrl, http.Urls[0]);
    }
}
