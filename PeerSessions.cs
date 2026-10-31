using System.Diagnostics.CodeAnalysis;

namespace Orbweaver
{
    // The peer link as the app runs it: listening, announcing, and keeping a
    // connection to every machine the user has paired with.
    //
    // Shaped like RemoteControlSessions and OpenClawSessions next door — a
    // static with a private cache and a gate, off meaning nothing is
    // constructed at all. What differs is what "off" costs. A relay is a live
    // Claude Code session on the user's account, so it starts on demand and
    // stops itself when nobody is looking; this is a socket, so the only reason
    // it is not simply always on is that listening is a permission on both
    // platforms and should be the user's choice rather than a surprise.
    //
    // Nothing here spends anything. There is no model in the path, so a machine
    // that sits connected all day costs what an idle socket costs.
    internal static class PeerSessions
    {
        private static readonly object Gate = new();

        private static PeerMirrorHost? _host;
        private static PeerDiscovery? _discovery;
        private static Timer? _connecting;
        private static Timer? _pumping;

        // How often the two mirror halves are given a chance to notice a
        // deadline lapsing or a watch needing renewal.
        //
        // **A plain Timer, and that is the whole point of it.** This work used
        // to be a DispatcherTimer, and a DispatcherTimer does not fire on a Mac
        // whose screen never unlocks — which is exactly the machine this feature
        // exists to serve, and exactly the fault CB-39 spent a ticket on. The
        // relay grew a non-dispatcher stand-in for it; the relay is gone and the
        // reason is not, so the link owns one of its own.
        //
        // 1.5s, unchanged from the pump it replaces: fast enough that a lapsed
        // fetch is noticed while somebody is still looking at the panel, and far
        // too cheap to be worth tuning.
        internal static readonly TimeSpan PumpEvery = TimeSpan.FromMilliseconds(1500);

        // How often to try the machines we are paired with but not connected
        // to. A machine that has just woken should come back within a few
        // seconds of being seen, and a machine that is off costs one refused
        // connection per interval, which is nothing.
        internal static readonly TimeSpan ReconnectEvery = TimeSpan.FromSeconds(10);

        // The running link's host, or — only in a test, only when no real one
        // is running — the stand-in UseClientForTests installed. The real host
        // always wins, so a test cannot shadow a live link.
        internal static PeerMirrorHost? Host
        {
            get { lock (Gate) return _host ?? _hostForTests; }
        }

        // A host with no link started, holding a mirror client a test built
        // over a fake wire. Installed here because this is where every caller in
        // the app gets its client from (CB-238): a seam anywhere else would let
        // a test pass through a path production never takes. Never set by the
        // app.
        private static PeerMirrorHost? _hostForTests;

        internal static void UseClientForTests(RemoteMirrorClient? client)
        {
            PeerMirrorHost? retired = null;

            lock (Gate)
            {
                if (client is null)
                {
                    retired = _hostForTests;
                    _hostForTests = null;
                }
                else
                {
                    _hostForTests ??= new PeerMirrorHost();
                    _hostForTests.UseClientForTests(client);
                }
            }

            // Disposing a host disposes its link, which was never started; the
            // client belongs to the test and is left alone.
            retired?.Dispose();
        }

        internal static bool Running
        {
            get { lock (Gate) return _host is not null; }
        }

        // Which machines are worth trying, and where to try them.
        //
        // **The version this replaces only dialled machines it could currently
        // hear announcing, and that was wrong in exactly the case the fallback
        // exists for.** A machine added by address lives on a network that does
        // not carry the announcements — that is why it had to be added by hand —
        // so after a restart it was never dialled again, however plainly its
        // address was sitting in the identity file. Found by deploying to two
        // real machines and watching nothing happen: the log said "listening"
        // and then nothing at all, because there was nothing in the list.
        //
        // A live announcement still wins where there is one. It carries the
        // address the machine has *now*, and a stored one is only where it was
        // when we last paired — a DHCP lease outlives neither.
        //
        // Pure so the rule is testable without a socket or a settings file.
        internal static List<PeerDiscovery.Seen> WorthDialling(
            IReadOnlyList<PeerDiscovery.Seen> seen,
            IReadOnlyDictionary<string, PeerIdentity.Peer> paired,
            Func<string, bool> connected)
        {
            var announcing = seen.ToDictionary(p => p.Machine, StringComparer.OrdinalIgnoreCase);
            var worth = new List<PeerDiscovery.Seen>();

            foreach (var (name, peer) in paired)
            {
                if (connected(name)) continue;

                if (announcing.TryGetValue(name, out var heard))
                {
                    worth.Add(heard);
                    continue;
                }

                var stored = Address(peer.Address, PeerLink.DefaultPort);
                if (stored is null) continue;

                worth.Add(new PeerDiscovery.Seen(
                    name, stored.Value.Host, stored.Value.Port, peer.Pin, DateTime.MinValue));
            }

            return worth;
        }

        [ExcludeFromCodeCoverage]
        internal static void Start()
        {
            if (!OrbweaverSettings.PeerLinkEnabled) return;

            PeerMirrorHost host;
            PeerDiscovery discovery;

            lock (Gate)
            {
                if (_host is not null) return;

                _host = host = new PeerMirrorHost();
                _discovery = discovery = new PeerDiscovery();
            }

            // Both halves, before anything connects. A machine that is dialled
            // must be able to answer immediately, and a panel opened the moment
            // the app starts must find a client rather than a null.
            var account = OrbweaverSettings.DefaultRemoteControlProfileDir;

            // Every configured account, not just the first. See
            // RemoteMirrorServer.AllAccountSeams — a socket is not account
            // scoped, and reading one roster made that claim untrue.
            // CB-300: every Claude Code profile the user configured, re-read per
            // hello — see PeerRosterDirs for why the old list alone was not it.
            host.Serve(
                () => PeerRosterDirs.Configured(),
                RemoteControlSessions.LocalSessions);

            // A roster landing is what puts an orb on screen and what tells an
            // open panel to look again. Both, in that order: the panel reads the
            // published rows, so raising the event first would hand it the list
            // it already had.
            if (host.Client is { } client)
            {
                client.RosterUpdated += () =>
                {
                    RemoteControlSessions.RepublishFromLink();
                    RemoteControlSessions.RaiseMirrorChanged(account);
                };
            }

            try
            {
                host.Link.Listen(OrbweaverSettings.PeerLinkPort);
                discovery.Start(host.Link.BoundPort);

                MirrorLog.Say("peer-listening", $"port={host.Link.BoundPort}");
            }
            catch (Exception ex)
            {
                // On macOS a missing Local Network grant surfaces here or at the
                // first connect, and looks like an ordinary network error. The
                // hint is appended rather than substituted, exactly as the
                // gateway does it.
                MirrorLog.Say("peer-listen-failed",
                    OpenClawGateway.ExplainConnectFailure(ex, OperatingSystem.IsMacOS()));
            }

            lock (Gate)
            {
                _connecting = new Timer(
                    _ => _ = DialAsync(), null, TimeSpan.Zero, ReconnectEvery);

                _pumping = new Timer(
                    _ => _ = PumpAsync(), null, PumpEvery, PumpEvery);
            }
        }

        // Connects to everything paired that we can see and are not already
        // talking to.
        //
        // A plain Timer rather than a DispatcherTimer, deliberately: this has to
        // keep working on a machine whose screen never unlocks, where the
        // Avalonia dispatcher does not pump. That is not hypothetical — it is
        // exactly the state the mini was found in, and the reason ServePump
        // exists at all (CB-39, CB-61).
        // Excluded from coverage: every line of it reaches a socket or the
        // identity file. What it decides is WorthDialling, which is pure.
        //
        // **Wrapped, and noisy about it, because the version without either was
        // undiagnosable.** The timer discards this task — `_ = DialAsync()` —
        // so anything thrown inside it went nowhere at all: no log, no crash,
        // no tick. Deployed to two machines, the log said "listening" and then
        // stopped, and there was no way to tell a tick that found nothing from a
        // tick that never ran from a tick that threw on its first line.
        //
        // That is the same lesson CB-61 already paid for on the serve path, and
        // it arrived here because this code was written after that one and did
        // not copy it.
        [ExcludeFromCodeCoverage]
        private static async Task DialAsync()
        {
            try
            {
                await DialOnceAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MirrorLog.Say("peer-dial-tick-threw", $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        [ExcludeFromCodeCoverage]
        private static async Task DialOnceAsync()
        {
            PeerMirrorHost? host;
            PeerDiscovery? discovery;

            lock (Gate)
            {
                host = _host;
                discovery = _discovery;
            }

            if (host is null || discovery is null)
            {
                MirrorLog.Say("peer-dial-tick", "no host yet");
                return;
            }

            // Before dialling, because a machine with no screen has to be able
            // to *accept* a pairing, and this is the only thing that opens its
            // window. One interval — ten seconds — between writing the file and
            // the window being open.
            HonourPairingFile(host);

            var seen = discovery.Peers();
            var paired = PeerIdentity.Peers();
            var worth = WorthDialling(seen, paired, host.Link.IsConnected);

            // One line a tick, saying which of the three "nothing happened"
            // cases this is: nobody paired, nobody reachable, or everybody
            // already connected. Guessing between those from silence is what
            // cost this ticket an hour.
            // SayOnce, keyed on nothing — the *detail* is the state, so any
            // change in it prints immediately and a steady state restates every
            // five minutes.
            //
            // Both properties are needed and they pull against each other. CB-61
            // was about a loop that had gone silent and could not be
            // distinguished from one that was running; this line exists to
            // answer that. But an unchanging heartbeat every ten seconds is
            // 8,640 lines a day into a 1MB log, which answers it by destroying
            // everything else. A machine connecting, a peer appearing, a pairing
            // landing — each changes the counts and lands at once, which is the
            // only time anybody is reading this.
            MirrorLog.SayOnce("peer-dial-tick",
                $"seen={seen.Count} paired={paired.Count} "
                + $"connected={host.Link.ConnectedMachines().Count} dialling={worth.Count}");

            foreach (var peer in worth)
            {
                try
                {
                    await host.Link
                        .ConnectAsync(peer.Machine, peer.Address, peer.Port)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    MirrorLog.SayOnce($"peer-dial-failed:{peer.Machine}",
                        $"to={peer.Machine} {ex.GetType().Name}: {ex.Message}");
                }
            }

            await AskWhatTheyHaveAsync(host).ConfigureAwait(false);
        }

        // Asks every connected machine what sessions it has.
        //
        // With no names, which means "everything" — see CB-67. That is the only
        // possible first question on a link with no prior list, and it is what
        // puts an orb on screen for a session on another machine.
        //
        // Cheap to repeat: the client only asks about names it does not already
        // know (CB-55), so a machine whose roster has already arrived costs
        // nothing per tick.
        [ExcludeFromCodeCoverage]
        private static async Task AskWhatTheyHaveAsync(PeerMirrorHost host)
        {
            var client = host.Client;
            if (client is null) return;

            var connected = host.Link.ConnectedMachines();

            try
            {
                await client.AskWhatTheyHaveAsync(connected).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MirrorLog.Say("peer-roster-failed", $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // Gives the mirror halves their tick.
        //
        // Wrapped for the same reason DialAsync is: the timer discards this
        // task, so anything thrown inside it would go nowhere at all — no log,
        // no crash, and a pump that had silently stopped.
        [ExcludeFromCodeCoverage]
        private static async Task PumpAsync()
        {
            try
            {
                await RemoteControlSessions.MirrorTickAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MirrorLog.SayOnce("peer-pump-threw", $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // --- pairing without a screen -------------------------------------------------

        // The file that opens a pairing window on a machine nobody is sitting at.
        //
        // **This is not a convenience, it is the only way some machines can be
        // paired at all.** Pairing is a button in Settings and a code read off
        // the screen; a Mac mini serving its sessions headless has neither, and
        // that machine is the whole reason this feature exists. Without this,
        // the one deployment the direct link is *for* is the one deployment that
        // cannot join.
        //
        // The shape is borrowed rather than invented: MirrorLog already turns
        // itself on with a marker file in this directory, for exactly this
        // machine and exactly this reason — an env var does not reach a process
        // that launchd started. Same directory, same idea, one file.
        //
        //     ssh mini
        //     echo 123456 > "~/Library/Application Support/Orbweaver/pair-open"
        //
        // Then pair from the machine that does have a screen. The file is read
        // once and deleted, and the window it opens lapses on its own — so a
        // file forgotten on disk is not a standing invitation, which is the one
        // thing a mechanism like this must not become.
        internal const string PairingFileName = "pair-open";

        // Whether a file's contents are a code worth opening a window for.
        //
        // Pure, and strict about it: this is the only place in the app where a
        // pairing window opens without a person having clicked anything, so what
        // it accepts is the security of the headless path. Six digits, trimmed
        // of the newline `echo` adds, and nothing else — a file holding a stray
        // shell error or a half-written line opens nothing.
        internal static string? CodeInFile(string? contents) =>
            contents is not null
            && contents.Trim() is { Length: 6 } code
            && code.All(char.IsAsciiDigit)
                ? code
                : null;

        [ExcludeFromCodeCoverage]
        internal static string PairingFilePath() =>
            Path.Combine(OrbweaverSettings.Directory, PairingFileName);

        // Reads the file, opens the window, and deletes the file.
        //
        // Deleted rather than left in place, and deleted whether or not the
        // contents were usable: a file that stayed would re-open the window on
        // every tick, which would defeat the lapse entirely and make the code
        // permanent. One file, one window.
        //
        // Excluded from coverage: reads and deletes a real file. What it accepts
        // is CodeInFile and how long the window then lasts is PeerLink.StillOpen,
        // both pure and both tested.
        [ExcludeFromCodeCoverage]
        private static void HonourPairingFile(PeerMirrorHost host)
        {
            var path = PairingFilePath();

            string? contents;

            try
            {
                if (!File.Exists(path)) return;

                contents = File.ReadAllText(path);
                File.Delete(path);
            }
            catch (Exception ex)
            {
                MirrorLog.Say("peer-pair-file-failed", $"{ex.GetType().Name}: {ex.Message}");
                return;
            }

            var code = CodeInFile(contents);

            if (code is null)
            {
                MirrorLog.Say("peer-pair-file-ignored", "not six digits");
                return;
            }

            host.Link.OpenForPairing(code);
            MirrorLog.Say("peer-pair-file", "window opened from " + PairingFileName);
        }

        // --- pairing ----------------------------------------------------------------

        // Everything the settings pane needs to pair two machines, in the four
        // calls it takes: show a code, dial with one, list what is out there,
        // and forget one.
        //
        // Deliberately thin. The decisions live in PeerLink.Judge and
        // PeerIdentity, which are pure and tested; this layer exists so the
        // window never touches a link or an identity file directly.

        // Opens this machine to one pairing and returns the code to read out.
        [ExcludeFromCodeCoverage]
        internal static string? OpenForPairing() => Host?.Link.OpenForPairing();

        [ExcludeFromCodeCoverage]
        internal static void ClosePairing() => Host?.Link.ClosePairing();

        // Dials a machine we have seen announcing itself, offering a code read
        // off its screen. True only means the connection opened and the
        // greeting went out — the far side answers `ok` or `err`, and a refusal
        // arrives on the connection rather than from here.
        [ExcludeFromCodeCoverage]
        internal static async Task<bool> PairAsync(PeerDiscovery.Seen peer, string code)
        {
            var host = Host;
            if (host is null) return false;

            // Any existing connection to this machine is on a certificate we
            // may be about to replace, and the greeting is only sent by a fresh
            // dial. Dropping first is what makes re-pairing after a reinstall
            // work rather than silently doing nothing.
            host.Link.Drop(peer.Machine);

            return await host.Link
                .ConnectAsync(peer.Machine, peer.Address, peer.Port, pairingCode: code)
                .ConfigureAwait(false);
        }

        // The announcement a machine name came from, which is where its address
        // and port live. Null for a paired machine that is not currently on the
        // network — which is exactly why pairing needs it and forgetting does
        // not.
        [ExcludeFromCodeCoverage]
        internal static PeerDiscovery.Seen? SeenFor(string machine)
        {
            PeerDiscovery? discovery;
            lock (Gate) discovery = _discovery;

            return discovery?.Peers()
                .FirstOrDefault(p => string.Equals(
                    p.Machine, machine, StringComparison.OrdinalIgnoreCase));
        }

        // What someone typed into "add a machine by address".
        //
        // **The fallback the plan called for, and today it stopped being
        // hypothetical.** Discovery is UDP multicast, and multicast is the first
        // thing a network gets wrong: a guest VLAN, a VPN, two subnets, an AP
        // that does not forward it. It is also the first thing macOS Local
        // Network consent blocks — measured on this machine, where a third-party
        // binary got EHOSTUNREACH opening plain TCP to a host that `ssh` reaches
        // from the same shell. A feature whose only way in is discovery is a
        // feature that fails with no way forward.
        //
        // Host or host:port; the port defaults to the one discovery would have
        // announced. Pure, and null for anything that is not a plausible
        // address — a typo should say so in the window rather than spend a
        // connection and come back "refused", which reads as the pairing having
        // failed.
        internal static (string Host, int Port)? Address(string? typed, int fallbackPort)
        {
            var text = typed?.Trim();
            if (string.IsNullOrEmpty(text)) return null;

            string host;
            var port = fallbackPort;

            // Three shapes, told apart by counting colons rather than by
            // reaching for the last one.
            //
            // Counting is what an IPv6 literal forces. `fe80::1` has several
            // colons and no port; a last-colon rule truncates it to `fe80::`
            // and the dial then fails as an unreachable host, which reads as a
            // network problem rather than as a parse. The bracketed form is the
            // only way to write v6 *with* a port, and it needs its own arm — an
            // earlier version of this claimed the last-colon rule covered it and
            // it did not.
            var colons = text.Count(c => c == ':');

            if (text.StartsWith('[') && text.Contains("]:", StringComparison.Ordinal))
            {
                var close = text.IndexOf("]:", StringComparison.Ordinal);

                host = text[1..close];
                if (!int.TryParse(text[(close + 2)..], out port)) return null;
            }
            else if (colons == 1)
            {
                // Exactly one colon is always host:port. Not "sometimes a
                // hostname that happens to contain a colon" — a colon is not
                // legal in a host name — so `mini:` is a half-typed address and
                // has to be refused rather than dialled as a host called
                // "mini:".
                var mark = text.IndexOf(':');

                host = text[..mark];
                if (!int.TryParse(text[(mark + 1)..], out port)) return null;
            }
            else
            {
                // No colons, or several: a bare name, a v4 address, or a v6
                // literal with no port.
                host = text.StartsWith('[') && text.EndsWith(']') ? text[1..^1] : text;
            }

            if (port is < 1 or > 65535) return null;
            if (host.Length == 0 || host.Any(char.IsWhiteSpace)) return null;

            return (host, port);
        }

        // Dials an address nobody announced.
        //
        // The machine is filed under the address until the far end says what it
        // is called — see PeerLink.Settle. That is why this needs no name from
        // the user: asking for one would be asking them to guess a value the
        // protocol already carries, and a wrong guess would file the pairing
        // under a name the far machine never uses.
        [ExcludeFromCodeCoverage]
        internal static async Task<bool> PairAtAsync(string host, int port, string code)
        {
            var link = Host?.Link;
            if (link is null) return false;

            return await link
                .ConnectAsync(host, host, port, pairingCode: code, nameIsProvisional: true)
                .ConfigureAwait(false);
        }

        [ExcludeFromCodeCoverage]
        internal static void Unpair(string machine)
        {
            Host?.Link.Drop(machine);
            PeerIdentity.Forget(machine);
        }

        // What the settings pane lists: everything seen announcing, plus
        // everything paired, whether or not it is currently reachable.
        //
        // Pure, because "which of these do I show and in what state" is a rule
        // and not a socket read. A paired machine that has gone quiet still has
        // a row — it is the row that says the machine is off, which is a
        // different answer from having nothing to say about it.
        internal sealed record Listed(string Machine, bool Paired, bool Connected, bool Seen);

        internal static IReadOnlyList<Listed> Listing(
            IReadOnlyList<PeerDiscovery.Seen> seen,
            IEnumerable<string> paired,
            Func<string, bool> connected)
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var peer in seen) names.Add(peer.Machine);
            foreach (var machine in paired) names.Add(machine);

            var announcing = seen
                .Select(p => p.Machine)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var known = paired.ToHashSet(StringComparer.OrdinalIgnoreCase);

            return names
                .Select(name => new Listed(
                    name, known.Contains(name), connected(name), announcing.Contains(name)))
                .ToList();
        }

        [ExcludeFromCodeCoverage]
        internal static IReadOnlyList<Listed> Listing()
        {
            PeerMirrorHost? host;
            PeerDiscovery? discovery;

            lock (Gate)
            {
                host = _host;
                discovery = _discovery;
            }

            return Listing(
                discovery?.Peers() ?? Array.Empty<PeerDiscovery.Seen>(),
                PeerIdentity.Peers().Keys,
                machine => host?.Link.IsConnected(machine) ?? false);
        }

        // What one row says about itself.
        //
        // Four states rather than two, because "paired and unreachable" and
        // "here but not paired" are different problems with different next
        // steps, and collapsing them is the whole complaint about the transport
        // this replaces.
        internal static string RowStatus(Listed row) =>
            row switch
            {
                { Connected: true } => "Connected",
                { Paired: true, Seen: true } => "Paired — connecting…",
                { Paired: true } => "Paired — not on this network",
                _ => "Found — not paired",
            };

        // What the settings window shows, and the tray beside it.
        //
        // Deliberately says which of the three states it is in rather than
        // "connected" or nothing: a link that is on but has found nobody, and a
        // link that is off, are different problems with different fixes, and
        // telling them apart is the whole complaint about the transport this
        // replaces.
        internal static string StatusText(
            bool enabled, bool running, int listening, int connected, int seen,
            IReadOnlyList<RemoteMirrorServer.RosterGap>? gaps = null)
        {
            if (!enabled) return "Off";
            if (!running) return "Starting…";

            return Plain(listening, connected, seen) + GapSentence(gaps);
        }

        private static string Plain(int listening, int connected, int seen)
        {
            if (connected > 0)
                return connected == 1
                    ? $"Connected to 1 machine, listening on {listening}"
                    : $"Connected to {connected} machines, listening on {listening}";

            if (seen > 0)
                return seen == 1
                    ? $"1 machine found, not paired yet — listening on {listening}"
                    : $"{seen} machines found, none paired yet — listening on {listening}";

            return $"Listening on {listening}, no machines found yet";
        }

        // CB-300: one sentence when live sessions exist under a Claude Code
        // folder the link was never told to read, which is otherwise an empty
        // roster on the far machine with nothing to say why. Only actionable
        // gaps count — see RemoteMirrorServer.RosterGap — and with none, the
        // text is exactly what it was before this existed. The largest root is
        // named and the rest counted, since a line has room for one folder.
        internal static string GapSentence(IReadOnlyList<RemoteMirrorServer.RosterGap>? gaps)
        {
            var actionable = gaps?.Where(g => g.Actionable).ToList();
            if (actionable is null || actionable.Count == 0) return "";

            var total = actionable.Sum(g => g.Sessions);
            var largest = actionable.OrderByDescending(g => g.Sessions).ThenBy(g => g.Root, StringComparer.Ordinal).First();
            var more = actionable.Count - 1;

            var sessions = total == 1 ? "1 session" : $"{total} sessions";
            var where = more == 0
                ? largest.Root
                : more == 1 ? $"{largest.Root} and 1 more folder" : $"{largest.Root} and {more} more folders";
            var verb = total == 1 ? "isn't" : "aren't";
            var folder = more == 0 ? "that folder" : "those folders";

            return $" — {sessions} under {where} {verb} offered; add {folder} under Settings → Claude Code.";
        }

        [ExcludeFromCodeCoverage]
        internal static string StatusText()
        {
            PeerMirrorHost? host;
            PeerDiscovery? discovery;

            lock (Gate)
            {
                host = _host;
                discovery = _discovery;
            }

            return StatusText(
                OrbweaverSettings.PeerLinkEnabled,
                host is not null || _gapsForTests is not null,
                host?.Link.BoundPort ?? 0,
                host?.Link.ConnectedMachines().Count ?? 0,
                discovery?.Peers().Count ?? 0,
                host?.Server?.LastRosterGaps ?? _gapsForTests);
        }

        // A gap list a test hands the status line without a server behind it;
        // while one is set the link reads as running, since the sentence is only
        // ever shown for a running link. Never set by the app.
        private static IReadOnlyList<RemoteMirrorServer.RosterGap>? _gapsForTests;

        internal static void UseRosterGapsForTests(IReadOnlyList<RemoteMirrorServer.RosterGap>? gaps) =>
            _gapsForTests = gaps;

        [ExcludeFromCodeCoverage]
        internal static void Stop()
        {
            PeerMirrorHost? host;
            PeerDiscovery? discovery;
            Timer? connecting;
            Timer? pumping;

            lock (Gate)
            {
                host = _host;
                discovery = _discovery;
                connecting = _connecting;
                pumping = _pumping;

                _host = null;
                _discovery = null;
                _connecting = null;
                _pumping = null;
            }

            connecting?.Dispose();
            pumping?.Dispose();
            discovery?.Dispose();
            host?.Dispose();
        }

        // Turning the setting off should take the socket down with it rather
        // than waiting for a restart, and turning it on should not need one.
        [ExcludeFromCodeCoverage]
        internal static void Restart()
        {
            Stop();
            Start();
        }
    }
}
