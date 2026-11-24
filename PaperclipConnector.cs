using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    internal enum PaperclipPhase { Idle, Approving, Failed }

    // What Settings drives. An interface so the UI suite runs against a fake and
    // never opens a browser, calls a Paperclip or touches the real credentials.
    internal interface IPaperclipConnector
    {
        PaperclipPhase Phase { get; }
        string Message { get; }
        IReadOnlyList<PaperclipDiscovery.Found> Found { get; }
        IReadOnlyList<PaperclipApi.Company> Companies { get; }

        Task DiscoverAsync(CancellationToken ct);
        Task LoadCompaniesAsync(CancellationToken ct);
        Task ConnectAsync(string baseUrl, CancellationToken ct);
        void CancelConnect();
        Task DisconnectAsync(CancellationToken ct);
        void ChooseCompany(string companyId);
    }

    // The real connector: PaperclipConnect's flow, plus what happens to its
    // result - the key into PaperclipCredentials, the choices into settings,
    // and the poll loop restarted. Every external thing is a constructor
    // argument, so the integration suite runs all of it against a loopback
    // server, a scratch settings directory and a recording browser.
    internal sealed class PaperclipConnector : IPaperclipConnector
    {
        private readonly Func<string, string?, IPaperclipApi> _apiFor;
        private readonly Func<string, bool> _openBrowser;
        private readonly PaperclipCredentials _creds;
        private readonly Func<DateTime> _now;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly Action _restart;
        private readonly Func<IReadOnlyList<PaperclipDiscovery.Candidate>> _candidates;
        private readonly Func<string, IPaperclipApi> _probeFor;
        private CancellationTokenSource? _connectCts;

        public PaperclipPhase Phase { get; private set; }
        public string Message { get; private set; } = "";
        public IReadOnlyList<PaperclipDiscovery.Found> Found { get; private set; } = Array.Empty<PaperclipDiscovery.Found>();
        public IReadOnlyList<PaperclipApi.Company> Companies { get; private set; } = Array.Empty<PaperclipApi.Company>();

        internal PaperclipConnector(
            Func<string, string?, IPaperclipApi> apiFor, Func<string, bool> openBrowser,
            PaperclipCredentials creds, Func<DateTime> now, Func<TimeSpan, CancellationToken, Task> delay,
            Action restart, Func<IReadOnlyList<PaperclipDiscovery.Candidate>> candidates,
            Func<string, IPaperclipApi>? probeFor = null)
        {
            _apiFor = apiFor; _openBrowser = openBrowser; _creds = creds; _now = now;
            _delay = delay; _restart = restart; _candidates = candidates;
            // Discovery asks each candidate address one question, and a miss is the
            // normal case, so it has its own client (short timeout, no failure log).
            _probeFor = probeFor ?? (u => _apiFor(u, null));
        }

        // How long a discovery probe waits for one address. The general request
        // timeout is five seconds; a Paperclip on this machine answers in
        // milliseconds, and a miss should be over quickly.
        internal static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromMilliseconds(1500);

        // The production wiring. Excluded from coverage: it names the real
        // HttpClient, the real browser launch and the real home directory.
        [ExcludeFromCodeCoverage]
        internal static PaperclipConnector Real() => new(
            (url, key) => new PaperclipHttp(url, key, log: PaperclipHttp.DefaultLog),
            url => CloudSessionLink.Open(url),
            PaperclipCredentials.Default(), () => DateTime.UtcNow, Task.Delay,
            PaperclipSessions.Restart,
            () => PaperclipDiscovery.Candidates(
                root => Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>(),
                f => File.Exists(f) ? File.ReadAllText(f) : null,
                PaperclipDiscovery.InstancesRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))),
            url => new PaperclipHttp(url, null, timeout: DiscoveryTimeout));

        // What Settings talks to. Tests replace it.
        private static IPaperclipConnector? _current;

        internal static IPaperclipConnector Current
        {
            get => _current ??= Real();
            set => _current = value;
        }

        public async Task DiscoverAsync(CancellationToken ct)
        {
            var candidates = _candidates();
            Found = await PaperclipDiscovery.FindAsync(candidates, u => _probeFor(u), ct).ConfigureAwait(false);

            // A miss is the normal case for almost every user and is not a failure:
            // it is written only when something was found, or when the user has
            // Paperclip switched on and so would want to know it was looked for.
            if (Found.Count > 0)
                DiagnosticsLog.Write("paperclip: discovery found " + string.Join(", ", Found.Select(f => f.BaseUrl)));
            else if (OrbweaverSettings.PaperclipEnabled)
                DiagnosticsLog.Write("paperclip: discovery found nothing at " + string.Join(", ", candidates.Select(k => k.BaseUrl)));
        }

        public async Task LoadCompaniesAsync(CancellationToken ct)
        {
            var url = OrbweaverSettings.PaperclipUrl;
            if (url.Length == 0) { Companies = Array.Empty<PaperclipApi.Company>(); return; }
            Companies = await PaperclipConnect.ListCompaniesAsync(_apiFor(url, _creds.Get(url)?.Key), ct).ConfigureAwait(false);
        }

        public async Task ConnectAsync(string baseUrl, CancellationToken ct)
        {
            baseUrl = baseUrl.Trim().TrimEnd('/');
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                Phase = PaperclipPhase.Failed;
                Message = "Enter the address as http://host:port.";
                return;
            }

            _connectCts?.Cancel();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _connectCts = cts;
            Phase = PaperclipPhase.Approving;
            Message = "Approve Orbweaver in your browser…";

            PaperclipConnectResult result;
            try
            {
                result = await PaperclipConnect.RunAsync(baseUrl, k => _apiFor(baseUrl, k), _openBrowser, _now, _delay, cts.Token,
                    m => Message = m).ConfigureAwait(false);
            }
            finally
            {
                if (ReferenceEquals(_connectCts, cts)) _connectCts = null;
            }

            switch (result.Outcome)
            {
                case PaperclipConnectOutcome.Connected:
                    _creds.Set(baseUrl, result.Key!, result.KeyExpires!.Value);
                    await FinishAsync(baseUrl, result.Key, ct).ConfigureAwait(false);
                    break;
                case PaperclipConnectOutcome.ConnectedNoKey:
                    _creds.Delete(baseUrl);
                    await FinishAsync(baseUrl, null, ct).ConfigureAwait(false);
                    break;
                case PaperclipConnectOutcome.Cancelled:
                    Phase = PaperclipPhase.Idle; Message = "";
                    break;
                case PaperclipConnectOutcome.Expired:
                    Phase = PaperclipPhase.Failed; Message = $"The approval at {(Uri.TryCreate(baseUrl, UriKind.Absolute, out var shown) ? shown.Authority : baseUrl)} timed out. Try again.";
                    break;
                default:
                    Phase = PaperclipPhase.Failed; Message = result.Message ?? "Could not connect to Paperclip.";
                    break;
            }
        }

        private async Task FinishAsync(string baseUrl, string? key, CancellationToken ct)
        {
            // Exactly one company means it is chosen; several means Settings shows
            // the picker and the loop waits for the choice.
            Companies = await PaperclipConnect.ListCompaniesAsync(_apiFor(baseUrl, key), ct).ConfigureAwait(false);
            var chosen = PaperclipConnect.ChooseCompany(Companies, OrbweaverSettings.PaperclipCompanyId);

            // A different address or company is a different picture: the remembered
            // roster of the old one is forgotten.
            if (!string.Equals(PaperclipRoster.Normalise(OrbweaverSettings.PaperclipUrl), PaperclipRoster.Normalise(baseUrl), StringComparison.Ordinal)
                || OrbweaverSettings.PaperclipCompanyId != (chosen?.Id ?? ""))
                PaperclipSessions.ForgetRoster();
            OrbweaverSettings.PaperclipUrl = baseUrl;
            OrbweaverSettings.PaperclipCompanyId = chosen?.Id ?? "";
            OrbweaverSettings.PaperclipEnabled = true;
            OrbweaverSettings.PaperclipHintShown = true;
            Phase = PaperclipPhase.Idle;
            Message = "";
            _restart();
        }

        public void CancelConnect() => _connectCts?.Cancel();

        public void ChooseCompany(string companyId)
        {
            if (OrbweaverSettings.PaperclipCompanyId != companyId) PaperclipSessions.ForgetRoster();
            OrbweaverSettings.PaperclipCompanyId = companyId;
            _restart();
        }

        public async Task DisconnectAsync(CancellationToken ct)
        {
            var url = OrbweaverSettings.PaperclipUrl;
            var key = url.Length == 0 ? null : _creds.Get(url)?.Key;
            var revoked = await PaperclipConnect.DisconnectAsync(
                key == null ? null : _apiFor(url, key), () => { if (url.Length > 0) _creds.Delete(url); }, ct).ConfigureAwait(false);

            // Disconnecting forgets the company: its picture is removed with its key.
            PaperclipSessions.ForgetRoster();
            OrbweaverSettings.PaperclipEnabled = false;
            OrbweaverSettings.PaperclipCompanyId = "";
            Companies = Array.Empty<PaperclipApi.Company>();
            Phase = PaperclipPhase.Idle;
            // A failed revoke still deleted the local copy; say the key may still
            // be listed in Paperclip rather than claim it is gone.
            Message = key != null && !revoked ? "Disconnected here, but the key may still be listed in Paperclip." : "";
            _restart();
        }
    }

    // The poll loop's view of the settings and the stored key.
    internal static class PaperclipConfigSource
    {
        internal static PaperclipSessions.PaperclipConfig Read() => Read(PaperclipCredentials.Default());

        internal static PaperclipSessions.PaperclipConfig Read(PaperclipCredentials creds)
        {
            if (!OrbweaverSettings.PaperclipEnabled) return PaperclipSessions.PaperclipConfig.Off;
            var url = OrbweaverSettings.PaperclipUrl;
            var entry = url.Length == 0 ? null : creds.Get(url);
            return new PaperclipSessions.PaperclipConfig(true, url, OrbweaverSettings.PaperclipCompanyId,
                entry?.Key ?? "", entry?.Expires, OrbweaverSettings.PaperclipSleeping);
        }
    }
}
