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
        // The relay connection on its own, before the screen-lock wait
        // (Program.Main's serveOnLaunch), for the reason the peer link and
        // OpenClaw start there: it is a WebSocket on pool threads with no
        // display anywhere in it. Behind the wait, a Mac that restarted while
        // locked never connected at all, so it vanished from the phone until
        // someone typed a password at it. 1.0.7's auto-update restarted the
        // mini into exactly that and both stayed down for hours. Attach still
        // calls Enable once the UI is up; Enable hands back the client this
        // started, so the scan wiring joins the existing connection rather
        // than opening a second one. Until then the phone sees the machine
        // online with an empty roster, which is the truth: nothing has been
        // scanned yet.
        internal static void ServeEarly()
        {
            // CB-314's scripted pairing, here as well as in Attach: a Mac parked
            // at the lock screen never reaches Attach, so the always-locked mini
            // could never be paired headless at all. Before the RelayEnabled
            // check because the hooks turn the relay on themselves.
            StartHooksOnce();
            if (!OrbweaverSettings.RelayEnabled) return;
            Enable();
            // A connection with nothing behind it is a machine the phone lists
            // with no orbs, which is what the mini did all night on 1.0.8's
            // first cut. The live scan only starts with the UI, so until then
            // the roster is fed from SessionManager.HeadlessSnapshot, the
            // window-free scan the peer link already serves this machine's
            // sessions from for the same reason (CB-24). A plain Timer, not a
            // DispatcherTimer, because the dispatcher does not pump here.
            lock (HeadlessGate)
            {
                if (_attached || _headless is not null) return;
                _headless = new Timer(_ => HeadlessTick(), null, TimeSpan.Zero, HeadlessEvery);

                // CB-391: and usage, for the same reason. The poll used to hang
                // off SessionManager's DispatcherTimer too, so a Mac parked at
                // the lock screen sent the phone no usage at all. Inside this
                // guard so it starts once, only with the relay on, and never
                // after Attach — see HeadlessUsage.
                _usage = UsageFactory();
                _usage.Start(UsageFirstTick, UsageEvery);
            }
        }

        private static readonly object HeadlessGate = new();
        private static Timer? _headless;
        private static bool _attached;

        // CB-391: the pre-unlock usage poll, while there is one. Under
        // HeadlessGate with the roster timer, and gone after Attach.
        private static HeadlessUsage? _usage;

        internal static HeadlessUsage? UsageFeed { get { lock (HeadlessGate) return _usage; } }

        internal static Func<HeadlessUsage> UsageFactory { get; set; } = RealUsage;

        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static HeadlessUsage RealUsage() => new(HeadlessUsage.Production(HeadlessUsage.RealSource()));

        // The UI tick's two seconds, after a 5 s first wait (plan 11427 §3).
        internal static readonly TimeSpan DefaultUsageFirstTick = TimeSpan.FromSeconds(5);
        internal static readonly TimeSpan DefaultUsageEvery = TimeSpan.FromSeconds(2);
        internal static TimeSpan UsageFirstTick { get; set; } = DefaultUsageFirstTick;
        internal static TimeSpan UsageEvery { get; set; } = DefaultUsageEvery;

        // CB-393: honourOrbLifetime is false, the rule Remote Control's own
        // headless fallback already uses. The phone is sent what is *there*,
        // not what the orbs still draw: orb lifetime ("Keep orbs for") is a
        // display preference that tidies a screen, and CB-314's first cut
        // honoured it, so a session idle past the lifetime with its process
        // alive vanished from the phone for seven hours. Superseded,
        // process-gone, husk and the pid-less/WSL rules still apply, so only
        // expiry stops dropping a session. The desktop's orbs are unchanged.
        internal static List<(string SessionId, SessionStatus Status)> RelayScan(
            string? statusDir = null,
            Func<Dictionary<string, string>?>? jobListing = null,
            Func<int, bool>? isRunning = null,
            DateTime? nowUtc = null,
            bool? onWindows = null) =>
            SessionManager.HeadlessSnapshot(
                statusDir, jobListing, isRunning, nowUtc,
                honourOrbLifetime: false, onWindows: onWindows);

        internal static Func<IReadOnlyList<(string SessionId, SessionStatus Status)>> HeadlessScan { get; set; } =
            () => RelayScan();

        internal static TimeSpan HeadlessEvery { get; set; } = TimeSpan.FromSeconds(3);

        // One headless scan into the relay, until Attach hands the roster to
        // the live scan. Under the gate so a tick can never run alongside, or
        // after, the first live OnScanned. Never throws: it runs on a timer
        // that discards exceptions, and a silent timer is undiagnosable.
        internal static void HeadlessTick()
        {
            lock (HeadlessGate)
            {
                if (_attached) return;
                try
                {
                    var snapshot = HeadlessScan();
                    ResolveHeadlessPersonas(snapshot);
                    OnScanned(snapshot);
                }
                catch (Exception e) { MirrorLog.Say("relay", $"headless scan failed: {e.GetType().Name}"); }
            }
        }

        // The roster's personas come from LocalPersonas, and only the live
        // scan's ApplyPersona fills it. The headless feed above sent the
        // phone a parked Mac's orbs with no personas at all until this ran.
        // The same rules as ApplyPersona: candidates per (cwd, source, agent)
        // for one pass, the persona kept while its files' signature holds so
        // a tick every three seconds is not a re-resolve every three seconds,
        // and a session's persona forgotten once the session has gone, as
        // the live scan forgets it. Under HeadlessGate, from HeadlessTick.
        private static readonly Dictionary<string, (string Signature, LocalPersona.Persona Persona)> HeadlessPersonas =
            new(StringComparer.Ordinal);

        internal static void ResolveHeadlessPersonas(IReadOnlyList<(string SessionId, SessionStatus Status)> snapshot)
        {
            var dirs = UserConfigDirs();
            var candidatesByKey = new Dictionary<(string, SessionSource, string), IReadOnlyList<LocalPersona.Candidate>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (sessionId, status) in snapshot)
            {
                if (!status.IsLocalCli || string.IsNullOrWhiteSpace(status.Cwd)) continue;
                seen.Add(sessionId);
                var key = (status.Cwd, status.Source, status.Agent);
                if (!candidatesByKey.TryGetValue(key, out var candidates))
                    candidatesByKey[key] = candidates = LocalPersona.Candidates(status.Cwd, dirs, status.Source, status.Agent);

                var paths = candidates.Select(c => c.Path);
                var known = HeadlessPersonas.TryGetValue(sessionId, out var k) ? k : default;
                if (known.Persona is not null
                    && known.Signature == LocalPersona.Signature(paths.Concat(known.Persona.Watched)))
                {
                    LocalPersonas.Set(sessionId, known.Persona);
                    continue;
                }

                var persona = LocalPersona.ResolveFrom(candidates, status.Cwd);
                HeadlessPersonas[sessionId] = (LocalPersona.Signature(paths.Concat(persona.Watched)), persona);
                LocalPersonas.Set(sessionId, persona);
            }

            foreach (var gone in HeadlessPersonas.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                HeadlessPersonas.Remove(gone);
                LocalPersonas.Forget(gone);
            }
        }

        internal static void ResetHeadlessForTests()
        {
            lock (HeadlessGate)
            {
                _headless?.Dispose();
                _headless = null;
                _attached = false;
                HeadlessPersonas.Clear();
                _usage?.Dispose();
                _usage = null;
            }
            Interlocked.Exchange(ref _hooksStarted, 0);
            UsageFactory = RealUsage;
            UsageFirstTick = DefaultUsageFirstTick;
            UsageEvery = DefaultUsageEvery;
            HeadlessScan = () => RelayScan();
            HeadlessEvery = TimeSpan.FromSeconds(3);
        }

        internal static void Attach(SessionManager sessions)
        {
            HeadlessUsage? usage;
            lock (HeadlessGate)
            {
                _attached = true;
                _headless?.Dispose();
                _headless = null;
                usage = _usage;
                _usage = null;
            }
            // CB-391: the pump, readings and Grok schedule the pre-unlock
            // poll was running, handed to the orbs before UsageApplied is
            // subscribed, so the cadence carries across and nothing is read
            // twice. Null — nothing to adopt — when that poll never started.
            sessions.AdoptUsage(usage?.Handoff(sessions.UsageDelivery));
            sessions.SessionsScanned += OnScanned;
            AccountOrbs.UsageApplied += OnUsage;
            if (OrbweaverSettings.RelayEnabled) Enable();
            StartHooksOnce();
        }

        // CB-314's scripted pairing, only with the explicit env opt-in, and once
        // per process: ServeEarly may already have started it before Attach.
        private static int _hooksStarted;

        private static void StartHooksOnce()
        {
            if (!HooksEnabled()) return;
            if (Interlocked.Exchange(ref _hooksStarted, 1) != 0) return;
            _ = HooksRunner(Enable());
        }

        internal static Func<bool> HooksEnabled { get; set; } = () => RelayTestHooks.Enabled(Environment.GetEnvironmentVariable);

        internal static Func<IPairingClient, Task> HooksRunner { get; set; } = RunRealHooks;

        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage] // the real directory, clock and delay; RelayTestHooks itself is tested
        internal static Task RunRealHooks(IPairingClient client) =>
            RelayTestHooks.RunAsync(client, OrbweaverSettings.Directory, () => DateTimeOffset.UtcNow, Task.Delay, CancellationToken.None);

        // What one scan becomes for the phones: the roster rows, each persona
        // with its v5 id and offered picture, the personas frame's plaintext,
        // and (CB-321 addendum 2) that frame's default row as the persona a
        // limit push speaks as.
        internal static (IReadOnlyList<RelaySession> Rows, string Personas, PushPersona? Default) Project(
            IReadOnlyList<(string SessionId, SessionStatus Status)> snapshot)
        {
            var dirs = UserConfigDirs();
            var rows = RelayRoster.Project(snapshot, (sessionId, status) =>
                status.Source == SessionSource.Paperclip
                    ? RelayPersonaRows.ForPaperclip(sessionId,
                        TrayController.DisplayName(new TrayController.SessionEntry(sessionId, status)), status.PaperclipAvatarFile, OfferImage)
                    : RelayPersonaRows.For(PersonaName(status, dirs), status, LocalPersonaFor(sessionId), OfferImage));
            var personaRows = RelayPersonaRows.Rows(rows, DefaultPersona(dirs), OfferImage);
            return (rows, PersonaFrames.Personas(personaRows), PushPersona.Of(personaRows.FirstOrDefault(r => r.Default)));
        }

        internal static void OnScanned(IReadOnlyList<(string SessionId, SessionStatus Status)> snapshot)
        {
            var client = Client;
            if (client is null) return;
            var (rows, personas, defaultPersona) = Project(snapshot);
            client.OnScanned(rows);
            // CB-321: and the personas frame, which the link sends only when its
            // bytes change, with its default persona for limit pushes.
            client.OnPersonas(personas, defaultPersona);
            // The accounts a phone's launch may name, read off the settings at each scan.
            try { client.OnAccounts(RelayAccounts.Frame(RelayAccounts.From(RelayAccounts.Choices))); }
            catch (Exception e) { MirrorLog.Say("relay", $"accounts frame not built: {e.GetType().Name}"); }
            // CB-339: what this desk can do (the folder picker), after accounts.
            client.OnCaps(BrowseFrames.Caps);
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
