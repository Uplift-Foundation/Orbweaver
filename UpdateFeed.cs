using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orbweaver
{
    // Where releases come from and which one, if any, to offer (CB-308).
    //
    // Two seams, deliberately: IReleaseFeed hands back parsed releases and knows
    // nothing about versions; UpdateFeed.Pick takes releases and a current version
    // and knows nothing about HTTP. The first is what a fixture server stands in
    // for (ORBWEAVER_UPDATE_FEED_URL), the second is a pure function with a case
    // per outcome.
    internal interface IReleaseFeed
    {
        // For includePrerelease=false the feed is free to return just the newest
        // stable release; for true it returns recent releases of both kinds and
        // Pick chooses with the real comparator. Throws UpdateException.
        Task<IReadOnlyList<UpdateRelease>> FetchAsync(bool includePrerelease, CancellationToken cancellationToken);
    }

    internal static class UpdateFeed
    {
        internal const string DefaultBaseUrl = "https://api.github.com/repos/Uplift-Foundation/Orbweaver";

        // The override is the test lever: point it at tools/update-feed-fixture
        // and the check talks to that instead of github.com. Trailing slash
        // trimmed so "http://127.0.0.1:8099/" and "...:8099" behave alike.
        internal static string ResolveBaseUrl(string? overrideUrl) =>
            string.IsNullOrWhiteSpace(overrideUrl) ? DefaultBaseUrl : overrideUrl.Trim().TrimEnd('/');

        // /releases/latest: one object. GitHub already excludes prereleases and
        // drafts from that endpoint, so an object that is either is treated as
        // "nothing to offer" rather than trusted.
        internal static UpdateRelease? ParseLatest(string json)
        {
            var node = ParseNode(json);
            if (node is not JsonObject obj)
                throw new UpdateException(UpdateFailure.Malformed, "The update feed did not return a release.");
            return ParseRelease(obj);
        }

        // /releases?per_page=N: an array, newest first by creation. Drafts and
        // entries whose tag is not a version are dropped, not fatal: one odd tag
        // in the history must not stop the whole check.
        internal static IReadOnlyList<UpdateRelease> ParseList(string json)
        {
            if (ParseNode(json) is not JsonArray arr)
                throw new UpdateException(UpdateFailure.Malformed, "The update feed did not return a list of releases.");

            var releases = new List<UpdateRelease>();
            foreach (var item in arr)
                if (item is JsonObject obj && ParseRelease(obj) is { } release)
                    releases.Add(release);
            return releases;
        }

        private static JsonNode? ParseNode(string json)
        {
            try { return JsonNode.Parse(json); }
            catch (JsonException ex)
            {
                throw new UpdateException(UpdateFailure.Malformed, "The update feed returned something that is not JSON.", ex);
            }
        }

        // Every field read through a type-checked helper, the same defence
        // OrbweaverSettings.Text() makes: GetValue<T>() throws on a type
        // mismatch, and one odd field must not cost the whole check.
        private static UpdateRelease? ParseRelease(JsonObject obj)
        {
            if (Bool(obj["draft"])) return null;

            var tag = Str(obj["tag_name"]);
            if (tag is null || !UpdateVersion.TryParse(tag, out var parsed)) return null;

            var assets = new List<UpdateAsset>();
            if (obj["assets"] is JsonArray arr)
                foreach (var a in arr)
                {
                    if (a is not JsonObject ao) continue;
                    var name = Str(ao["name"]);
                    var url = Str(ao["browser_download_url"]);
                    if (name is null || url is null) continue;
                    assets.Add(new UpdateAsset(name, url, Long(ao["size"])));
                }

            return new UpdateRelease(
                tag,
                parsed.Normalized(),
                // GitHub's flag, or prerelease identifiers in the tag: a "2.0.0-beta"
                // someone forgot to flag must not be offered to a stable-only user.
                Bool(obj["prerelease"]) || parsed.Prerelease.Count > 0,
                Str(obj["body"]) ?? "",
                assets);
        }

        private static string? Str(JsonNode? n) =>
            n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

        private static bool Bool(JsonNode? n) =>
            n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

        private static long Long(JsonNode? n) =>
            n is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;

        // The decision. The highest eligible release wins, "eligible" meaning
        // stable unless the user opted into prereleases, and it is offered only if
        // strictly newer than what is running. Skipped means the highest release
        // is exactly the one the user said to skip: it stays quiet, and the next
        // release after it is offered again because it no longer equals the skip.
        //
        // An unparseable current version answers UpToDate: with no way to say what
        // "newer" means, nagging is the worse error.
        internal static UpdateDecision Pick(
            IReadOnlyList<UpdateRelease> releases,
            string currentVersion,
            bool includePrerelease,
            string? skippedVersion)
        {
            if (!UpdateVersion.TryParse(currentVersion, out var current))
                return new UpdateDecision(UpdateDecisionKind.UpToDate, null);

            UpdateRelease? best = null;
            UpdateVersion? bestVersion = null;
            foreach (var r in releases)
            {
                if (r.Prerelease && !includePrerelease) continue;
                if (!UpdateVersion.TryParse(r.Version, out var v)) continue;
                if (bestVersion is null || UpdateVersion.Compare(v, bestVersion) > 0)
                {
                    best = r;
                    bestVersion = v;
                }
            }

            if (best is null || UpdateVersion.Compare(bestVersion!, current) <= 0)
                return new UpdateDecision(UpdateDecisionKind.UpToDate, null);

            if (UpdateVersion.TryParse(skippedVersion, out var skipped)
                && UpdateVersion.Compare(bestVersion!, skipped) == 0)
                return new UpdateDecision(UpdateDecisionKind.Skipped, best);

            return new UpdateDecision(UpdateDecisionKind.Available, best);
        }
    }

    // GitHub's releases API behind the seam. The one rule here: a stable-only
    // check asks /releases/latest (one small response, and GitHub itself excludes
    // prereleases and drafts there), while the prerelease opt-in has to list
    // recent releases because /latest cannot see a prerelease at all.
    internal sealed class GitHubReleaseFeed : IReleaseFeed
    {
        private readonly IUpdateHttp _http;
        private readonly string _baseUrl;

        internal GitHubReleaseFeed(IUpdateHttp http, string baseUrl)
        {
            _http = http;
            _baseUrl = UpdateFeed.ResolveBaseUrl(baseUrl);
        }

        public async Task<IReadOnlyList<UpdateRelease>> FetchAsync(bool includePrerelease, CancellationToken cancellationToken)
        {
            if (includePrerelease)
            {
                var list = await _http.GetStringAsync($"{_baseUrl}/releases?per_page=10", cancellationToken).ConfigureAwait(false);
                return UpdateFeed.ParseList(list);
            }

            var latest = await _http.GetStringAsync($"{_baseUrl}/releases/latest", cancellationToken).ConfigureAwait(false);
            return UpdateFeed.ParseLatest(latest) is { } r ? [r] : [];
        }
    }
}
