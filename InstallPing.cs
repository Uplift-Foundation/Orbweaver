using System.Text;
using System.Text.Json.Nodes;

namespace Orbweaver
{
    // CB-466: the opt-out anonymous active-install count.
    //
    // At most one small ping per UTC day per install, riding on an update check,
    // carrying exactly four things: a random install ID, the build's version, the
    // OS and the CPU type. Everything that decides *whether* and *what* is a pure
    // function here, so the promise in the README ("exactly four things") is a
    // test of BuildBody's key set rather than a sentence.
    internal static class InstallPing
    {
        // The Uplift-run Umami (v3.5.0, per CB-466-infra) and the app's own website
        // UUID, separate from the site's so app pings never pollute site stats. An
        // unreachable host fails quietly (logged, day not stamped).
        internal const string DefaultBaseUrl = "https://analytics.uplifttech.org";
        internal const string WebsiteId = "79fdc897-5ee9-4203-8141-d7ec20c821ed";

        internal const string EventName_ = "install-ping";
        internal const string InternalEventName = "install-ping-internal";

        // Bounded so a hung analytics host can never hold a thread for long.
        internal static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);

        // "0", "off" or "false" (any case) is an explicit no.
        internal static bool IsOffValue(string? value) =>
            value is not null && value.Trim().ToLowerInvariant() is "0" or "off" or "false";

        // DO_NOT_TRACK=1 is the cross-tool convention; "true" is accepted as the same.
        internal static bool IsDoNotTrack(string? value) =>
            value is not null && value.Trim().ToLowerInvariant() is "1" or "true";

        // Due when never sent, last sent on an earlier UTC date, or last sent "in the
        // future" (the clock moved back; bounded because the stamp is then rewritten).
        internal static bool Decide(bool settingOn, string? envPing, string? doNotTrack,
            DateTimeOffset? lastPingUtc, DateTimeOffset nowUtc)
        {
            if (!settingOn || IsOffValue(envPing) || IsDoNotTrack(doNotTrack)) return false;
            if (lastPingUtc is not { } last) return true;
            var lastUtc = last.UtcDateTime;
            var now = nowUtc.UtcDateTime;
            return lastUtc > now || lastUtc.Date < now.Date;
        }

        internal static bool IsInternal(string? envValue, bool settingFlag) =>
            settingFlag || envValue?.Trim() == "1";

        internal static string EventName(bool isInternal) => isInternal ? InternalEventName : EventName_;

        internal static string OsName(bool isWindows, bool isMacOS) =>
            isWindows ? "windows" : isMacOS ? "macos" : "linux";

        // Which build is installed, not what the hardware could run.
        internal static string ArchName(System.Runtime.InteropServices.Architecture arch) => arch switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            _ => arch.ToString().ToLowerInvariant(),
        };

        internal static string ResolveBaseUrl(string? overrideUrl) =>
            string.IsNullOrWhiteSpace(overrideUrl) ? DefaultBaseUrl : overrideUrl.Trim().TrimEnd('/');

        // The warning logged when the base URL is redirected, as the update feed does.
        internal static string? OverrideWarning(string? overrideUrl) =>
            string.IsNullOrWhiteSpace(overrideUrl) ? null
                : $"WARNING: {BrandEnv.Name(BrandEnv.InstallPingUrl)} is set; sending the install ping to {overrideUrl.Trim()} instead of the real endpoint.";

        // Umami drops a request whose User-Agent looks like a bot, and the updater's
        // bare "Orbweaver/1.0.7" does; the parenthesised platform is what makes it
        // pass. Measured against isbot 5.2.4 in the plan, not against the deployed one.
        internal static string UserAgent(string version, string os, string arch) =>
            $"{Brand.ShortName}/{version} ({os}; {arch})";

        // 2xx and not Umami's silent bot answer ({"beep":"boop"} with a 200).
        internal static bool Accepted(int statusCode, string? body) =>
            statusCode is >= 200 and < 300
            && (body is null || !body.Contains("beep", StringComparison.OrdinalIgnoreCase));

        // The whole payload. The key set is pinned by a test: nothing else is ever sent.
        // "ip" is a local address so Umami derives no location from the sender's.
        internal static string BuildBody(string websiteId, string installId, string version,
            string os, string arch, bool isInternal)
        {
            var name = EventName(isInternal);
            var body = new JsonObject
            {
                ["type"] = "event",
                ["payload"] = new JsonObject
                {
                    ["website"] = websiteId,
                    ["hostname"] = "app.orbweaver",
                    ["url"] = "/" + name,
                    ["name"] = name,
                    ["id"] = installId,
                    ["ip"] = "127.0.0.1",
                    ["data"] = new JsonObject
                    {
                        ["v"] = version,
                        ["os"] = os,
                        ["arch"] = arch,
                        ["iid"] = installId,
                    },
                },
            };
            return body.ToJsonString();
        }
    }

    // The slice of OrbweaverSettings the sender reads and writes.
    internal interface IInstallPingSettings
    {
        // The setting AND automatic update checks being on: with auto-check off the
        // Settings promise is that nothing is contacted at all.
        bool Enabled { get; }
        string? InstallId { get; set; }
        DateTimeOffset? LastPing { get; set; }
        bool Internal { get; }
    }

    internal sealed class OrbweaverInstallPingSettings : IInstallPingSettings
    {
        public bool Enabled => OrbweaverSettings.InstallPingEnabled && OrbweaverSettings.AutoCheckForUpdates;
        public string? InstallId
        {
            get => OrbweaverSettings.InstallId;
            set => OrbweaverSettings.InstallId = value;
        }
        public DateTimeOffset? LastPing
        {
            get => OrbweaverSettings.LastInstallPingUtc;
            set => OrbweaverSettings.LastInstallPingUtc = value;
        }
        public bool Internal => OrbweaverSettings.InternalInstall;
    }

    // POST JSON, return the status and body. GET-only IUpdateHttp should not grow
    // this, and a ping needs its own short bound.
    internal interface IInstallPingHttp
    {
        Task<(int Status, string Body)> PostJsonAsync(string url, string json, string userAgent, CancellationToken ct);
    }

    internal sealed class HttpInstallPingHttp : IInstallPingHttp, IDisposable
    {
        private readonly HttpClient _http = new();
        private readonly TimeSpan _timeout;

        // The timeout is a parameter only so a test can make it 200 ms.
        internal HttpInstallPingHttp(TimeSpan? timeout = null) => _timeout = timeout ?? InstallPing.HttpTimeout;

        public async Task<(int Status, string Body)> PostJsonAsync(string url, string json, string userAgent, CancellationToken ct)
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(_timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            using var response = await _http.SendAsync(request, bounded.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(bounded.Token).ConfigureAwait(false);
            return ((int)response.StatusCode, body);
        }

        public void Dispose() => _http.Dispose();
    }

    // Decide, then send, then stamp. Catches everything: nothing in the ping path may
    // change an update check's result or schedule, and the user is never told.
    internal sealed class InstallPingSender
    {
        private readonly IInstallPingSettings _settings;
        private readonly IInstallPingHttp _http;
        private readonly Func<DateTimeOffset> _now;
        private readonly Action<string> _log;
        private readonly Func<string, string?> _env;
        private readonly string _version, _os, _arch, _baseUrl;

        internal InstallPingSender(IInstallPingSettings settings, IInstallPingHttp http,
            Func<DateTimeOffset> now, Action<string> log, Func<string, string?> env,
            string version, string os, string arch, string baseUrl)
        {
            _settings = settings; _http = http; _now = now; _log = log; _env = env;
            _version = version; _os = os; _arch = arch; _baseUrl = baseUrl;
        }

        internal async Task TrySendAsync(CancellationToken ct = default)
        {
            try
            {
                var now = _now();
                if (!InstallPing.Decide(_settings.Enabled,
                        BrandEnv.Get(BrandEnv.InstallPing, _env), _env("DO_NOT_TRACK"),
                        _settings.LastPing, now))
                    return;

                var id = _settings.InstallId;
                if (string.IsNullOrWhiteSpace(id))
                {
                    id = Guid.NewGuid().ToString("N");
                    _settings.InstallId = id;
                }

                var isInternal = InstallPing.IsInternal(BrandEnv.Get(BrandEnv.InternalInstall, _env), _settings.Internal);
                var json = InstallPing.BuildBody(InstallPing.WebsiteId, id, _version, _os, _arch, isInternal);
                var (status, body) = await _http.PostJsonAsync(
                    $"{_baseUrl}/api/send", json, InstallPing.UserAgent(_version, _os, _arch), ct).ConfigureAwait(false);

                if (InstallPing.Accepted(status, body))
                    _settings.LastPing = now;
                else
                    _log($"install ping not accepted (HTTP {status}); will try again at the next check.");
            }
            catch (Exception ex)
            {
                _log($"install ping failed: {ex.Message}");
            }
        }
    }
}
