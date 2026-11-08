using System.Reflection;

namespace Orbweaver
{
    // The one place the app holds its phone relay client (CB-314): created the
    // first time "Pair phone…" is used, and on every launch after that while
    // RelayEnabled is set, fed by SessionManager's end-of-scan event. Static
    // in the same way UpdateServices is — the tray, the pairing window and
    // Settings all need the same client, and there is exactly one per process.
    internal static class RelayService
    {
        private static readonly object Gate = new();
        private static RelayClient? _client;

        // Replaced by tests, which hand in a client over a fake transport.
        internal static Func<RelayClient> Factory { get; set; } = () =>
            new RelayClient(RelayIdentity.Shared, DefaultOptions(), new WebSocketRelayTransport());

        // Persona lookups for the roster. Through the off-thread cache the LAN
        // roster already uses, so a scan never waits on a persona file.
        internal static Func<IReadOnlyList<string>> UserConfigDirs { get; set; } = () => LocalPersona.UserConfigDirs();

        internal static RelayClient? Client { get { lock (Gate) return _client; } }

        internal static event Action ClientChanged = delegate { };

        internal static RelayClient.Options DefaultOptions() => new(
            RelayClient.BaseFrom(BrandEnv.Get(BrandEnv.RelayUrl)),
            OperatingSystem.IsWindows() ? "win" : "mac",
            AppVersion(),
            MachineNames.Mine());

        internal static string AppVersion() =>
            (typeof(RelayService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0")
                .Split('+')[0];

        // At launch: listen to the scan, and connect if the user has paired
        // before. Nothing connects for someone who never used the feature.
        internal static void Attach(SessionManager sessions)
        {
            sessions.SessionsScanned += OnScanned;
            if (OrbweaverSettings.RelayEnabled) Enable();
        }

        internal static void OnScanned(IReadOnlyList<(string SessionId, SessionStatus Status)> snapshot)
        {
            var client = Client;
            if (client is null) return;
            var dirs = UserConfigDirs();
            client.OnScanned(RelayRoster.Project(snapshot, status =>
                RelayRoster.PersonaFrom(PeerPersonaCache.Shared.Peek(status, dirs, picturesById: true)?.Name, status)));
        }

        // "Pair phone…" calls this. Idempotent.
        internal static RelayClient Enable()
        {
            RelayClient client;
            lock (Gate)
            {
                if (_client is not null) return _client;
                client = _client = Factory();
            }
            OrbweaverSettings.RelayEnabled = true;
            client.Start();
            ClientChanged();
            return client;
        }

        // A UI test's client, built over fakes and never started.
        internal static void UseForTests(RelayClient? client)
        {
            lock (Gate) _client = client;
        }

        // Tests, and a future "turn the relay off".
        internal static async Task DisableAsync()
        {
            RelayClient? client;
            lock (Gate)
            {
                client = _client;
                _client = null;
            }
            OrbweaverSettings.RelayEnabled = false;
            if (client is not null)
            {
                await client.StopAsync().ConfigureAwait(false);
                client.Dispose();
            }
            ClientChanged();
        }
    }
}
