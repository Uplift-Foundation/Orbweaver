using System.Diagnostics.CodeAnalysis;
using Avalonia.Threading;

namespace Orbweaver
{
    // The remote half of what SessionManager displays: sessions running on the
    // user's *other* machines, published as an immutable snapshot the scan reads
    // for free.
    //
    // **Everything here arrives over the direct link** (PeerSessions and
    // PeerMirrorHost) from an Orbweaver running on the far machine. This class
    // used to be the home of the Remote Control relay — a live Claude Code
    // session per account, polled for its peer list — and 937de9ec deleted that
    // relay. CB-238 then deleted what it left behind here: the table the relays
    // lived in, the poll, revive and idle machinery, the inbound-message router
    // and the colour and command answers it collected. None of it had a caller
    // the app could reach. What remains is the snapshot, the rows the link
    // supplies, and the mirror lookups a chat panel makes. A machine without
    // Buddy on it is not reachable from here at all.
    internal static class RemoteControlSessions
    {
        private static readonly object Gate = new();

        // Published whole and replaced whole, so the scan — which runs on the UI
        // thread and locks nothing else it reads — is handed a finished list.
        private static volatile IReadOnlyList<Remote> _snapshot = Array.Empty<Remote>();

        // Orb rows that came off the direct link — the only source there is.
        // Republish turns them into _snapshot.
        private static IReadOnlyList<Remote> _peerRows = Array.Empty<Remote>();

        // This machine's own sessions, for the mirror server to answer about.
        //
        // Named rather than written as a lambda where the server is built,
        // because the fallback is a real answer with a real consequence:
        // it is what a far machine's roster request is answered with before
        // SessionManager has started. That used to be "no sessions", which was
        // wrong in exactly the case the serve-on-launch setting exists for — a
        // headless machine whose screen never unlocks never starts
        // SessionManager at all (CB-24), so it answered every HELLO with
        // an empty roster and the far panel silently stayed a messaging
        // channel. Now the answer comes from the same scan rules, composed
        // without the UI — see SessionManager.HeadlessSnapshot.
        internal static IReadOnlyList<(string SessionId, SessionStatus Status)> LocalSessions()
        {
            lock (Gate)
            {
                if (_localSessionsAt is { } at && Now() - at < LocalSessionsFor)
                    return _localSessionsWere;
            }

            var fresh = HeadlessFallback();

            lock (Gate)
            {
                _localSessionsWere = fresh;
                _localSessionsAt = Now();
            }

            return fresh;
        }

        // **This used to prefer the orb list and fall back to a scan, and the
        // preference was the bug.** The orb list is what is on screen, and what
        // is on screen has had the user's orb-lifetime preference applied to it
        // — so an idle session that had stopped being drawn was reported to
        // every other machine as not existing. On a headless Mac it was worse
        // still: the orb list is filled by a scan on a dispatcher that never
        // pumps, so the list was not merely filtered but empty.
        //
        // Serving is a question of fact and the disk is what knows the answer,
        // so the disk is asked. See SessionManager.HeadlessSnapshot, which is
        // told to ignore orb lifetime for exactly this call.
        //
        // Memoised for a moment because a peer asks every ten seconds and the
        // scan reads a directory and a job listing. Two seconds is short enough
        // that a session starting is noticed at once and long enough that
        // several peers asking together cost one scan.
        private static readonly TimeSpan LocalSessionsFor = TimeSpan.FromSeconds(2);

        private static IReadOnlyList<(string SessionId, SessionStatus Status)> _localSessionsWere =
            Array.Empty<(string, SessionStatus)>();

        private static DateTime? _localSessionsAt;

        // Swappable because the real scan reads this machine's actual status
        // directory, which a unit test has no business depending on.
        internal static Func<IReadOnlyList<(string SessionId, SessionStatus Status)>>
            HeadlessFallback = () => SessionManager.HeadlessSnapshot(honourOrbLifetime: false);

        // Also clears the memo, or one test's answer is still being served two
        // seconds into the next one.
        internal static void ForgetLocalSessionsForTests()
        {
            lock (Gate)
            {
                _localSessionsWere = Array.Empty<(string, SessionStatus)>();
                _localSessionsAt = null;
            }
        }

        // A session on another machine, as the orb scan wants it. Kept separate
        // from BridgeProtocol.RemoteAgent so the parser stays a parser: this one
        // carries which account it was seen through and when, neither of which
        // the peer list has an opinion about.
        internal sealed record Remote(
            string Name, string Ref, string Status, DateTime Seen, string Account, string? Color = null,
            string? Cli = null, string? Route = null, MirrorProtocol.PeerPersona? Persona = null,
            string? LeadRoute = null, string? Agent = null, string? AgentColor = null)
        {
            // CB-223: the key of this session's lead's own orb, when the far
            // machine said it is a team member and offered the lead too. Built
            // the same way as Key, from the same account, so the near side's
            // team links pair on the dictionary key with nothing translated.
            public string? LeadKey => string.IsNullOrEmpty(LeadRoute) ? null : "rc:" + Account + ":" + LeadRoute;

            // The account is in the key, not just the record.
            //
            // Two accounts can hold identically-named sessions — the same person
            // naming things the same way twice is the normal case, not a corner
            // one — and without the account they would collapse onto one orb and
            // one chat panel, with messages going to whichever the dictionary
            // happened to hold. The prefix keeps them apart from local sessions
            // for the same reason OpenClaw's keys do.
            public string Key => "rc:" + Account + ":" + (Route ?? Name);

            // "running" is the one that matters, and it is the one the first
            // version of this missed.
            //
            // The peer list's vocabulary is **not** the same as `claude agents
            // --json`'s. That prints "busy" for a working local session, so this
            // was written against "busy" — and a remote session actually reports
            // `running`, which meant the orb sat still for the entire time a
            // machine elsewhere was working. Caught only by watching a real
            // relay transcript: idle → running → idle across four polls while
            // nothing on screen moved.
            //
            // Exactly the mistake this repo's fixture rule exists to prevent,
            // made by taking a vocabulary from the wrong source rather than from
            // the output being parsed. The other two are kept as tolerance, not
            // because either has been seen here.
            public bool Working =>
                Status.Contains("running", StringComparison.OrdinalIgnoreCase)
                || Status.Contains("busy", StringComparison.OrdinalIgnoreCase)
                || Status.Contains("working", StringComparison.OrdinalIgnoreCase);
        }

        // Empty whenever the feature is off, which is what makes the scan's job
        // trivial — it never has to know why.
        //
        // A test seam, matching OpenClawSessions.SetSnapshotForTests and there for
        // the same reason: the only thing that publishes a snapshot in production
        // is RepublishFromLink, which reads a live link and is excluded. Without
        // this, the scan entries built from a remote session are unreachable for
        // a reason that has nothing to do with the code being hard to test.
        internal static void SetSnapshotForTests(IReadOnlyList<Remote> remotes)
        {
            _snapshot = remotes;
        }

        public static IReadOnlyList<Remote> Snapshot() =>
            Visible(_snapshot, OrbweaverSettings.PeerLinkEnabled);

        // Whether remote orbs are shown at all.
        //
        // **One switch again, and a different one.** This asked about the relay
        // for as long as a relay was the only way a remote row could exist; then
        // briefly about both; and now about the link alone, because the relay is
        // gone and the link is the only thing that fills the list.
        //
        // Worth keeping as a function rather than collapsing into the property:
        // it is the reason the scan never has to know *why* the list is empty,
        // and the arms have been wrong once already — the two-transport version
        // shipped drawing nothing on exactly the machine most likely to have
        // rows, because it insisted on a switch the user had been told to turn
        // off.
        internal static IReadOnlyList<Remote> Visible(IReadOnlyList<Remote> rows, bool linkOn) =>
            linkOn ? rows : Array.Empty<Remote>();

        // Lets the link raise the event a panel listens on. The event itself
        // cannot be raised from outside the class it is declared in, and the
        // link deliberately lives outside — see PeerMirrorHost's note on why it
        // is not account-scoped.
        [ExcludeFromCodeCoverage]
        internal static void RaiseMirrorChanged(string account) => MirrorChanged?.Invoke(account);

        // ---- test seams ----------------------------------------------------

        internal static IReadOnlyList<Remote> SnapshotForTests
        {
            get { lock (Gate) return _snapshot; }
        }

        // Raised when a far Buddy has answered about what it can mirror, so an
        // open panel can upgrade itself from a messaging channel to a live view
        // without being reopened.
        public static event Action<string>? MirrorChanged;

        public static IReadOnlyList<SlashCommand> CommandsFor(string account, string name)
        {
            // A roster answer wins over a CB-INFO one, and it is a better answer
            // in every way: it was read off the far machine's own disk by its
            // Buddy rather than recited by a model, it carries built-ins as well
            // as custom commands — which now genuinely run, because a mirrored
            // send is typed into that session's input line — and it cannot come
            // back mangled, because it arrived hashed.
            //
            // The roster is now the only answer: the CB-INFO fallback this used
            // to consult was filled by the relay's inbound-message router, which
            // went with the relay (CB-238).
            return CommandsFrom(MirrorFor(account, name));
        }

        // The rule, pure (CB-306): what the far Buddy said this session can
        // run, as the panel should offer it.
        //
        //  * Route kinds present: the far Buddy resolved each command on its own
        //    machine, so believe it — Unavailable ones are hidden, which is the
        //    fix for messaging-mode panels offering /color that silently did
        //    nothing. A routes list that does not line up with the commands is
        //    treated as absent rather than guessed at.
        //  * No route kinds: an older far Buddy, whose commands only ever meant
        //    keystrokes into a pane. They are offered only when there is a pane
        //    (a live view can exist); a session with none gets nothing, because
        //    every one of them would be offered and then do nothing.
        internal static IReadOnlyList<SlashCommand> CommandsFrom(MirrorProtocol.MirrorRosterEntry? entry)
        {
            if (entry?.Commands is not { Count: > 0 } commands) return Array.Empty<SlashCommand>();

            if (entry.CommandRoutes is { } routes && routes.Count == commands.Count)
                return commands
                    .Select((c, i) => new SlashCommand(c, "", MirrorProtocol.RouteFromWord(routes[i])))
                    .Where(c => c.Route != SlashRouteKind.Unavailable)
                    .ToList();

            return entry.HasPane
                ? commands.Select(c => new SlashCommand(c, "")).ToList()
                : Array.Empty<SlashCommand>();
        }

        // Whether the far Buddy understands the slash INPUT flag, which it
        // announces by sending route kinds at all.
        internal static bool SpeaksSlash(MirrorProtocol.MirrorRosterEntry? entry) =>
            entry?.CommandRoutes is not null;

        // What the far Buddy said about one session, or null if none has.
        internal static MirrorProtocol.MirrorRosterEntry? MirrorFor(string account, string name) =>
            MirrorStateFor(account, name).Entry;

        // **Through MirrorClientFor, not into the relay table.** This read the
        // table directly, which was the same thing while a relay was the only
        // client there was — and stopped being the same thing the moment the
        // link arrived, because the link's client is not in that table at all.
        //
        // What it produced is the worst shape of failure this panel has: not an
        // error, not a refusal, but `Unknown` forever. The panel says "checking
        // whether a live view is available…" and means it, and there is nothing
        // anywhere to read. Watched happen on a real machine with the roster
        // arriving every ten seconds the whole time — the answer was in the
        // client this never asked.
        //
        // Found by a person clicking an orb, which is the one test I could not
        // run. CB-69 fixed MirrorClientFor and did not go looking for the
        // callers that went around it.
        internal static RemoteMirrorClient.MirrorState MirrorStateFor(string account, string name)
        {
            var client = MirrorClientFor(account);

            return client?.StateFor(name)
                   ?? new RemoteMirrorClient.MirrorState(
                       RemoteMirrorClient.MirrorAvailability.Unknown, null);
        }

        internal static RemoteMirrorClient? MirrorClientFor(string account)
        {
            // The direct link first, when there is one.
            //
            // **This is the line that moves the panel off the relay.** Both
            // clients are the same class doing the same work; what differs is
            // what carries their frames — a socket, or a model retyping base64
            // at 222 to 247 seconds a chunk.
            //
            // The peer client is not account-scoped, because a socket is not:
            // this machine talks to that machine whatever either is signed into.
            // Asking for one by account and getting the machine-wide one is
            // therefore correct rather than sloppy, and is why the parameter is
            // ignored on this path.
            //
            // The relay fallback this used to fall through to went with the
            // relay (CB-238), so the link's client is the only answer — and a
            // test installs its client there too, through PeerSessions.
            return PeerSessions.Host?.Client;
        }

        // Installs a mirror client without a relay behind it.
        //
        // A test seam, and the same kind as `Now` above: the alternative is a
        // test that starts a real Claude Code session to prove that a chat panel
        // renders a transcript, which would cost the person running it money and
        // still not be deterministic. The client handed in here is the real one,
        // wired to a fake wire — see MirrorRoundTripTests, which does the same
        // thing with a real server on the other end of it.
        //
        // Never called by the app, whose only client is the direct link's. It
        // used to park the client in the relay table; since CB-238 it installs
        // it on PeerSessions' host, which is where the app reads it from. The
        // account is kept for the MirrorChanged event, which panels match on.
        internal static void UseMirrorClientForTests(string account, RemoteMirrorClient? client)
        {
            PeerSessions.UseClientForTests(client);

            if (client is not null) client.RosterUpdated += () => MirrorChanged?.Invoke(account);
        }

        // Puts the statics back, so one test cannot leave a mirror installed for
        // every test after it — the mistake bugfix/rc-tests-leak-remote-setting
        // already had to fix once for the Remote Control setting itself.
        internal static void ResetForTests()
        {
            lock (Gate)
            {
                _snapshot = Array.Empty<Remote>();
                _peerRows = Array.Empty<Remote>();

                // The memoised local-session scan, or one test's answer is
                // still being served two seconds into the next one.
                _localSessionsWere = Array.Empty<(string, SessionStatus)>();
                _localSessionsAt = null;
            }

            // Chat sessions subscribe to this in their constructor and are
            // deliberately never disposed, so without clearing it every session
            // any earlier test built stays subscribed for the rest of the run.
            MirrorChanged = null;
            PeerSessions.UseClientForTests(null);

            Now = () => DateTime.UtcNow;
        }

        // Injectable so the local-session memo and the link's row timestamps
        // can be tested against a fixed clock. Never replaced in the app.
        internal static Func<DateTime> Now = () => DateTime.UtcNow;

        // The guard that keeps MirrorTickAsync to one round at a time. Internal
        // so a test can hold it and watch a round decline — the only way to
        // prove from outside that the pump actually asks.
        //
        // It replaced a plain bool when a second pump (the relay's serve pump,
        // calling from the pool) shared it. That pump went with the relay
        // (CB-238); the gate stays, because PeerSessions' timer can still fire
        // while a slow round is in flight. See TickGate.
        internal static readonly TickGate PumpGate = new();

        // One round of the direct link's two mirror halves.
        //
        // **Repointed at the link rather than deleted with the relay, and that
        // distinction matters.** What this used to do was pump a tmux pane and
        // then tick the two mirror halves that read it. The pane is gone; the
        // halves are not, and neither is their reason for wanting a tick —
        // deadlines lapse and watches renew on this clock, not on the arrival of
        // bytes. Deleting it wholesale would have left a fetch that never times
        // out and a watch that quietly expires, both of which look like the far
        // machine having gone quiet.
        //
        // Excluded from coverage: the one line that reads the live link. What a
        // round does is ServeOneAsync, which is tested with real halves.
        [ExcludeFromCodeCoverage]
        internal static async Task MirrorTickAsync()
        {
            var host = PeerSessions.Host;
            if (host is null) return;

            await ServeOneAsync(host.Server, host.Client).ConfigureAwait(true);
        }

        // One turn of both mirror halves, under PumpGate.
        //
        // Takes the halves as arguments rather than reading the link, so a test
        // can hand it real ones with no dispatcher and no socket — which is how
        // CB-39's "serves with no UI thread" fix stays asserted. False means the
        // gate was held, which is never an error: the timer comes back around.
        // Each half is ticked inside its own try, because one half throwing must
        // cost that half's round and nothing else on a machine nobody watches.
        internal static async Task<bool> ServeOneAsync(
            RemoteMirrorServer? server, RemoteMirrorClient? client)
        {
            if (!PumpGate.TryEnter()) return false;

            try
            {
                if (server is not null)
                {
                    try { await server.TickAsync().ConfigureAwait(true); } catch { }
                }

                if (client is not null)
                {
                    try { await client.TickAsync().ConfigureAwait(true); } catch { }
                }
            }
            finally
            {
                PumpGate.Exit();
            }

            return true;
        }

        // A mirror roster, as orb rows.
        //
        // Pure, and the only place the two vocabularies meet: a roster entry
        // says what a session *is* (name, CLI, transcript, colour), and a Remote
        // says what to draw. The peer name doubles as the Ref, because over a
        // direct link the machine that served a session is the machine it is on
        // — which is the fact the relay path had to parse back out of a session
        // name (see RemoteControlChatSession.MachineName).
        //
        // An entry nobody has claimed is dropped rather than drawn with a blank
        // machine: it would be an orb the panel could not then ask anyone about.
        internal static IReadOnlyList<Remote> RemotesFromRoster(
            string account,
            IReadOnlyList<(string Peer, MirrorProtocol.MirrorRosterEntry Entry)> known,
            DateTime now) =>
            known
                .Where(k => !string.IsNullOrWhiteSpace(k.Peer))
                .Select(k => new Remote(
                    k.Entry.Name,
                    k.Peer,
                    k.Entry.Status ?? "idle",
                    now,
                    account,
                    string.IsNullOrWhiteSpace(k.Entry.Color) ? null : k.Entry.Color,
                    k.Entry.Cli, k.Entry.Route, k.Entry.Persona,
                    string.IsNullOrWhiteSpace(k.Entry.Lead) ? null : k.Entry.Lead,
                    string.IsNullOrWhiteSpace(k.Entry.Agent) ? null : k.Entry.Agent,
                    string.IsNullOrWhiteSpace(k.Entry.AgentColor) ? null : k.Entry.AgentColor))
                .ToList();

        // Excluded from coverage: reads the live link. What it decides is
        // RemotesFromRoster, which is tested; this is the two lines that fetch
        // and store.
        [ExcludeFromCodeCoverage]
        internal static void RepublishFromLink()
        {
            var client = PeerSessions.Host?.Client;

            var rows = client is null
                ? Array.Empty<Remote>()
                : RemotesFromRoster(
                    OrbweaverSettings.DefaultRemoteControlProfileDir,
                    client.Known(),
                    Now());

            lock (Gate) _peerRows = rows;

            Republish();
        }

        // The link's rows, one per session, as the list the scan reads.
        internal static void Republish()
        {
            lock (Gate) _snapshot = OnePerSession(_peerRows);
        }

        // One row per session key, the first one winning.
        //
        // This used to merge two transports — a relay's rows and the link's —
        // and prefer the link's, because both could list one session and two
        // orbs for one terminal was the "two Claude Buddys" complaint in a
        // smaller form. The relay is gone (CB-238), but a roster can still name
        // one key twice in a moment of churn, and one orb per key is still the
        // rule. Pure, so that stays a test rather than a hope.
        internal static IReadOnlyList<Remote> OnePerSession(IReadOnlyList<Remote> rows)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return rows.Where(row => seen.Add(row.Key)).ToList();
        }

    }
}
