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
        internal static Func<RelayClient> Factory { get; set; } = RealClient;

        // The production client: the real identity (Keychain or DPAPI) and a
        // real socket. Excluded because a test must never build it.
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static RelayClient RealClient() =>
            new(RelayIdentity.Shared, DefaultOptions(), new WebSocketRelayTransport());

        // Persona names for the roster, through the off-thread cache the LAN
        // roster already uses, so a scan never waits on a persona file. A seam
        // so a test can hand in names; the default is one line of glue into
        // PeerPersonaCache and LocalPersona, which have suites of their own.
        internal static Func<SessionStatus, IReadOnlyList<string>, string?> PersonaName { get; set; } = CachedPersonaName;
        internal static Func<IReadOnlyList<string>> UserConfigDirs { get; set; } = DefaultUserConfigDirs;

        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static string? CachedPersonaName(SessionStatus status, IReadOnlyList<string> dirs) =>
            PeerPersonaCache.Shared.Peek(status, dirs, picturesById: true)?.Name;

        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static IReadOnlyList<string> DefaultUserConfigDirs() => LocalPersona.UserConfigDirs();

        // CB-321: the registry persona a session wears (its id and picture
        // path), the picture store's hash for a path, and the default persona —
        // each a seam so the roster's v5 keys and the personas frame are tested
        // without a scan, a file or a decode.
        internal static Func<string, LocalPersona.Persona?> LocalPersonaFor { get; set; } = LocalPersonas.For;
        internal static Func<string, string?> OfferImage { get; set; } = DefaultOfferImage;
        internal static Func<IReadOnlyList<string>, LocalPersona.Persona?> DefaultPersona { get; set; } = CachedDefaultPersona;

        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static string? DefaultOfferImage(string path) => PersonaImageStore.Shared.Offer(path);

        // The default persona: what a Claude Code session started in the user's
        // home folder would wear, i.e. the walk from home (home's own CLAUDE.md
        // and what it @-imports, then each parent) followed by the user config
        // dirs. The config dirs alone were the first reading (Candidates with no
        // cwd is exactly their CLAUDE.md files), and on a real Mac that came to
        // nothing: the persona everyone treats as the default is named by
        // ~/CLAUDE.md, which only a walk from home reaches (CB-321, Juno's
        // simulator rehearsal; the plan's stated fallback). Read off the scan
        // thread like every other persona read here, and refreshed every 30 s.
        internal static Func<string?> Home { get; set; } = () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        internal static LocalPersona.Persona ResolveDefault(string? home, IReadOnlyList<string> dirs) =>
            LocalPersona.Resolve(string.IsNullOrEmpty(home) ? null : home, SessionSource.ClaudeCode, dirs);

        private static readonly OffThreadCache<(string Home, string Dirs), LocalPersona.Persona?> DefaultCache = new(new(
            key => ResolveDefault(key.Home, key.Dirs.Length == 0 ? [] : key.Dirs.Split('\0')),
            _ => null,
            () => DateTime.UtcNow,
            OffThread.OnOwnThread), PeerPersonaCache.RefreshAfter);

        internal static LocalPersona.Persona? CachedDefaultPersona(IReadOnlyList<string> dirs) =>
            DefaultCache.Peek((Home() ?? "", string.Join('\0', dirs)));

        internal static RelayClient? Client { get { lock (Gate) return _client; } }

        internal static event Action ClientChanged = delegate { };

        internal static RelayClient.Options DefaultOptions() => new(
            RelayClient.BaseFrom(BrandEnv.Get(BrandEnv.RelayUrl)),
            PlatformName(OperatingSystem.IsWindows()),
            AppVersion(),
            MachineNames.Mine());

        internal static string PlatformName(bool isWindows) => isWindows ? "win" : "mac";

        // The SDK always stamps an informational version from <Version>.
        internal static string AppVersion() =>
            typeof(RelayService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion
                .Split('+')[0];

        // At launch: listen to the scan, and connect if the user has paired
        // before. Nothing connects for someone who never used the feature.
        internal static void Attach(SessionManager sessions)
        {
            sessions.SessionsScanned += OnScanned;
            AccountOrbs.UsageApplied += OnUsage;
            if (OrbweaverSettings.RelayEnabled) Enable();
            // CB-314's scripted pairing, only with the explicit env opt-in.
            if (HooksEnabled()) _ = HooksRunner(Enable());
        }

        internal static Func<bool> HooksEnabled { get; set; } = () => RelayTestHooks.Enabled(Environment.GetEnvironmentVariable);

        internal static Func<IPairingClient, Task> HooksRunner { get; set; } = RunRealHooks;

        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage] // the real directory, clock and delay; RelayTestHooks itself is tested
        internal static Task RunRealHooks(IPairingClient client) =>
            RelayTestHooks.RunAsync(client, OrbweaverSettings.Directory, () => DateTimeOffset.UtcNow, Task.Delay, CancellationToken.None);

        // What one scan becomes for the phones: the roster rows, each persona
        // with its v5 id and offered picture, and the personas frame's plaintext.
        internal static (IReadOnlyList<RelaySession> Rows, string Personas) Project(
            IReadOnlyList<(string SessionId, SessionStatus Status)> snapshot)
        {
            var dirs = UserConfigDirs();
            var rows = RelayRoster.Project(snapshot, (sessionId, status) =>
                RelayPersonaRows.For(PersonaName(status, dirs), status, LocalPersonaFor(sessionId), OfferImage));
            return (rows, PersonaFrames.Personas(RelayPersonaRows.Rows(rows, DefaultPersona(dirs), OfferImage)));
        }

        internal static void OnScanned(IReadOnlyList<(string SessionId, SessionStatus Status)> snapshot)
        {
            var client = Client;
            if (client is null) return;
            var (rows, personas) = Project(snapshot);
            client.OnScanned(rows);
            // CB-321: and the personas frame, which the link sends only when its
            // bytes change.
            client.OnPersonas(personas);
            // CB-315: and the prompt watcher, off this thread — it captures panes.
            _ = client.OnStatuses(snapshot);
        }

        // "Pair phone…" calls this. Idempotent.
        // CB-316: the usage orbs' readings, to the relay's usage frame.
        internal static void OnUsage(IReadOnlyList<RelayUsage.Account> accounts) => Client?.OnUsage(accounts);

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
