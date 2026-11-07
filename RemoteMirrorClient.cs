using System.Diagnostics.CodeAnalysis;
namespace Orbweaver
{
    // This machine asking another machine's Buddy for what its sessions
    // actually say, and refusing anything it cannot prove.
    //
    // The client half of MirrorProtocol. It finds the far Buddy among the peers
    // the relay already lists, asks it about the sessions there are orbs for,
    // and then — for a panel that is open — pulls that session's transcript
    // across in hashed pieces and keeps it up to date.
    //
    // The rule that matters is what happens when something does not verify.
    // Nothing partially-good is ever handed on. A piece that fails its own hash
    // is asked for again, twice, and then the transfer fails; a set of pieces
    // that individually verify but do not add up to the digest the far side
    // computed fails immediately, because there is nothing useful to re-request.
    // Either way the caller is told the mirror failed and shows an error. It
    // never shows text that did not survive the trip, and it never quietly falls
    // back to the model-written version — that is the bug this exists to fix,
    // and doing it as a fallback would reintroduce it precisely when something
    // is going wrong.
    internal sealed class RemoteMirrorClient
    {
        internal sealed record Seams(Func<string, string, Task<bool>> SendFrame);

        private readonly string _account;
        private readonly Seams _seams;
        private readonly object _gate = new();

        public RemoteMirrorClient(string account, Seams seams)
        {
            _account = account;
            _seams = seams;
        }

        public string Account => _account;

        // CB-306: the sink for outcome lines, by session name and never by
        // message text. MirrorLog.Say in the app, a capture in a test; the
        // same arrangement as RemoteMirrorServer.GapLog.
        internal Action<string, string> Log { get; set; } = (what, detail) => MirrorLog.Say(what, detail);

        // --- what the far side said it has -----------------------------------

        // A session's mirror status, from this side's point of view.
        internal enum MirrorAvailability
        {
            // Nobody has answered yet. Distinct from Unavailable on purpose: a
            // panel that opens during the handshake should say it is checking,
            // not that there is nothing there.
            Unknown,

            Available,

            // Asked and answered no — there is no Buddy over there, or it has
            // no transcript for this session. Settled, and the panel says so.
            Unavailable
        }

        internal readonly record struct MirrorState(
            MirrorAvailability Availability, MirrorProtocol.MirrorRosterEntry? Entry);

        private readonly Dictionary<string, MirrorProtocol.MirrorRosterEntry> _roster =
            new(StringComparer.OrdinalIgnoreCase);

        // Which far relay answered for a given session, so a later request goes
        // back to the one that can serve it.
        private readonly Dictionary<string, string> _servedBy = new(StringComparer.OrdinalIgnoreCase);

        private static string EntryKey(MirrorProtocol.MirrorRosterEntry entry) =>
            string.IsNullOrWhiteSpace(entry.Route) ? entry.Name : entry.Route;

        // New peers address rows by their stable route. Existing callers still
        // hold the display name, so accept it only when it identifies exactly
        // one roster row; a shared title stays unresolved rather than sending
        // input to whichever session happened to arrive last.
        private string? ExistingKey(string keyOrName)
        {
            if (_roster.ContainsKey(keyOrName)) return keyOrName;
            var matches = _roster.Where(p => string.Equals(p.Value.Name, keyOrName,
                StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        // Which relay answered for a session, for a caller that wants to say
        // *where* it is rather than talk to it. It is the name of the peer that
        // served the session, which over the direct link is the far machine
        // itself — so this is
        // how the panel names the machine without anything new going on the
        // wire. Null before the roster has answered, which a panel can open
        // before.
        internal string? RelayFor(string name)
        {
            lock (_gate)
            {
                var key = ExistingKey(name);
                return key is not null && _servedBy.TryGetValue(key, out var relay) ? relay : null;
            }
        }

        // Everything this client currently knows about, with who serves it.
        //
        // The roster is already the answer to "what is out there"; over the
        // relay nothing read it that way because a second channel — the relay's
        // own ListAgents poll — was producing the orb rows. A direct link has
        // only this, so it has to be readable from outside.
        internal IReadOnlyList<(string Peer, MirrorProtocol.MirrorRosterEntry Entry)> Known()
        {
            lock (_gate)
            {
                return _roster.Values
                    .Select(e => (
                        Peer: _servedBy.TryGetValue(EntryKey(e), out var peer) ? peer : string.Empty,
                        Entry: e))
                    .ToList();
            }
        }

        // Names that have been asked about and came back with nothing. Kept so a
        // panel can say "no live view" definitively rather than sitting on
        // "checking…" forever.
        private readonly HashSet<string> _answeredNo = new(StringComparer.OrdinalIgnoreCase);

        // Names with a HELLO already on the wire.
        //
        // **Without this the wait for an answer manufactures the queue that
        // stops the answer arriving.** Discovery runs off the poll — every 20
        // seconds, or every 5 while anything is working — and asks about every
        // name not yet in the roster. A name only enters the roster when a reply
        // lands, so for as long as the far relay is slow, each tick sent another
        // HELLO: measured on the mini as a backlog of 166 roster requests queued
        // ahead of the window that had been asked for, each one costing a model
        // turn to answer. The busier the relay got, the harder it was asked.
        //
        // Silence still is not "no" — a request that genuinely fails clears its
        // name here and the next poll asks again, which is the retry the comment
        // in DiscoverAsync describes and is worth keeping. What is dropped is
        // only the *duplicate*: asking a second time for something already being
        // answered, which can never arrive sooner and always arrives slower.
        private readonly HashSet<string> _asking = new(StringComparer.OrdinalIgnoreCase);

        public event Action? RosterUpdated;

        internal MirrorState StateFor(string name)
        {
            lock (_gate)
            {
                var key = ExistingKey(name);
                if (key is not null && _roster.TryGetValue(key, out var entry))
                    return new MirrorState(MirrorAvailability.Available, entry);

                return new MirrorState(
                    _answeredNo.Contains(name) ? MirrorAvailability.Unavailable : MirrorAvailability.Unknown,
                    null);
            }
        }

        // --- discovery --------------------------------------------------------

        // Told directly who to ask.
        //
        // **There used to be an overload above this one taking a peer list and
        // filtering it by IsOwnRelay/IsOffline/IsRemoteControl** — three
        // properties that only meant anything while a far Buddy was reachable as
        // a session on a relay. It went with the relay. What it was working
        // towards is what a direct link simply knows: the machines it is
        // connected to.
        //
        // **Split out because the peer list is transport-shaped and discovery is
        // not.** The overload above takes BridgeProtocol.RemoteAgent and filters
        // it by IsOwnRelay/IsOffline/IsRemoteControl — three properties that
        // only mean anything when the far Buddy is reachable as a *session on a
        // relay*. A direct connection has no such list: it knows the machines it
        // is connected to, which is already the answer that filter was working
        // towards.
        //
        // Everything below this line is identical for both, which is the point:
        // asking a far Buddy what it has does not depend on how the question
        // travels.
        // Peers with an open-ended question outstanding, so it is asked once
        // rather than on every ten-second tick.
        private readonly HashSet<string> _askingAll = new(StringComparer.OrdinalIgnoreCase);

        // CB-216: the hash of the roster each peer last sent in full, handed back
        // on the next ask so an unchanged roster comes back as a bare OK instead
        // of 17 MB of persona pictures. See MirrorProtocol.RosterHashField.
        //
        // Held only while the roster it describes is exactly what this side
        // holds for that peer. Anything else that changes that peer's entries
        // forgets it (a disconnect, a relay answer), so the next ask is answered
        // in full and the result is always the one a full answer would give.
        private readonly Dictionary<string, string> _rosterHashes = new(StringComparer.OrdinalIgnoreCase);

        // CB-216: persona pictures fetched by id, so each distinct picture
        // crosses the wire once rather than inside every roster. Keyed by id,
        // which is the picture's hash and length, so two peers offering the same
        // file share one entry. Bounded for the reason PeerAvatarStore is: the
        // pictures are large. Forgetting one costs a single fetch.
        internal const int PictureCapacity = 16;
        private readonly PictureCache _pictures = new(PictureCapacity);

        // Asks a machine what it has, naming nothing.
        //
        // **A separate method because DiscoverAsync cannot express this, and
        // that gap cost a bring-up.** DiscoverAsync starts `if
        // (wantedNames.Count == 0) return;` — an empty list means "ask about
        // nothing", which is right when the caller is working from a list of
        // sessions it already knows. The server was taught in CB-67 that an
        // empty payload means "everything you have", and the client had no way
        // to send that: passing an empty list returned before writing a byte.
        //
        // So two machines connected over TLS in 22 milliseconds, greeted each
        // other, and then sat there — one able to answer a question the other
        // was structurally unable to ask. Nothing failed and nothing was logged,
        // because from the client's point of view it had been asked to do
        // nothing and had done it.
        //
        // Open-ended is the only possible *first* question on a direct link:
        // there is no prior list of session names to work from, so without this
        // there is no way to learn one.
        public async Task AskWhatTheyHaveAsync(IReadOnlyList<string> peers)
        {
            // A direct-link roster is a complete answer from each connected
            // machine, not a stream of additions. Forgetting a peer that is no
            // longer connected is therefore safe; retaining it made its last
            // roster permanent after a machine was switched off.
            var connected = new HashSet<string>(peers, StringComparer.OrdinalIgnoreCase);
            var disconnectedChanged = false;

            lock (_gate)
            {
                foreach (var name in _servedBy
                    .Where(pair => !connected.Contains(pair.Value))
                    .Select(pair => pair.Key)
                    .ToList())
                {
                    var displayName = _roster.TryGetValue(name, out var stale)
                        ? stale.Name : name;
                    _servedBy.Remove(name);
                    _roster.Remove(name);
                    _answeredNo.Add(name);
                    _answeredNo.Add(displayName);
                    disconnectedChanged = true;
                }

                foreach (var gone in _rosterHashes.Keys.Where(peer => !connected.Contains(peer)).ToList())
                {
                    _rosterHashes.Remove(gone);
                }
            }

            if (disconnectedChanged) RosterUpdated?.Invoke();

            foreach (var peer in peers)
            {
                lock (_gate)
                {
                    // Once per peer at a time. The answer names every session
                    // that machine has, so a second question in flight can only
                    // duplicate the first.
                    if (!_askingAll.Add(peer)) continue;
                }

                Reply reply;

                var fields = new Dictionary<string, string>
                {
                    ["pv"] = "1",
                    [MirrorProtocol.AvatarsByIdField] = "1",
                };
                lock (_gate)
                {
                    if (_rosterHashes.TryGetValue(peer, out var held))
                        fields[MirrorProtocol.RosterHashField] = held;
                }

                try
                {
                    reply = await RequestAsync(
                        peer, MirrorProtocol.Hello, fields,
                        payload: null, TimeSpan.FromSeconds(30))
                        .ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate) _askingAll.Remove(peer);
                }

                // Read on every answer, the bare "unchanged" OK included, so a
                // far Buddy upgraded mid-session is noticed on its next poll.
                NoteSpeaksPrompts(peer, reply);

                if (!reply.Ok || reply.Payload is null) continue;

                var decoded = MirrorProtocol.DecodeRoster(reply.Payload);
                if (decoded is null) continue;

                var (entries, allPictures) = await FillPicturesAsync(peer, decoded).ConfigureAwait(false);

                // Only once the roster has decoded and every picture in it has
                // arrived, so a hash never stands for a roster this side did not
                // fully take in. A picture that failed to arrive is asked for
                // again with the whole roster on the next poll.
                lock (_gate)
                {
                    if (HashToHold(reply.Fields, allPictures) is { } sent) _rosterHashes[peer] = sent;
                    else _rosterHashes.Remove(peer);
                }

                var offered = new Dictionary<string, MirrorProtocol.MirrorRosterEntry>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var entry in entries)
                {
                    if (entry.HasTranscript) offered[EntryKey(entry)] = entry;
                }

                var changed = false;

                lock (_gate)
                {
                    // This reply is authoritative for this peer alone. A
                    // similarly named session may have been answered by a
                    // different connected machine, so it must not be removed
                    // merely because this peer did not mention it.
                    foreach (var name in _servedBy
                        .Where(pair => string.Equals(pair.Value, peer,
                            StringComparison.OrdinalIgnoreCase)
                            && !offered.ContainsKey(pair.Key))
                        .Select(pair => pair.Key)
                        .ToList())
                    {
                        var displayName = _roster.TryGetValue(name, out var stale)
                            ? stale.Name : name;
                        _roster.Remove(name);
                        _servedBy.Remove(name);
                        _answeredNo.Add(name);
                        _answeredNo.Add(displayName);
                        changed = true;
                    }

                    foreach (var entry in offered.Values)
                    {
                        var key = EntryKey(entry);
                        if (!_roster.TryGetValue(key, out var previous)
                            || previous != entry
                            || !_servedBy.TryGetValue(key, out var servedBy)
                            || !string.Equals(servedBy, peer, StringComparison.OrdinalIgnoreCase))
                        {
                            changed = true;
                        }

                        _roster[key] = entry;
                        _servedBy[key] = peer;
                        _answeredNo.Remove(key);
                    }
                }

                MirrorLog.Say("roster-all", $"from={peer} entries={entries.Count}");

                if (changed) RosterUpdated?.Invoke();
            }
        }

        public async Task DiscoverAsync(
            IReadOnlyList<string> relays, IReadOnlyList<string> wantedNames)
        {
            if (wantedNames.Count == 0) return;

            if (relays.Count == 0)
            {
                // No Buddy over there. Every name asked about is settled as
                // unavailable, which is what turns a panel's "checking…" into
                // the honest "no live view" line.
                var changed = false;

                lock (_gate)
                {
                    foreach (var name in wantedNames)
                    {
                        if (ExistingKey(name) is not null) continue;
                        if (_answeredNo.Add(name)) changed = true;
                    }
                }

                if (changed) RosterUpdated?.Invoke();
                return;
            }

            foreach (var relay in relays)
            {
                List<string> ask;

                lock (_gate)
                {
                    // Only what is still unknown. A roster entry does not go
                    // stale in a way that matters — colour and commands are
                    // refreshed by asking again when a panel opens — and
                    // re-asking every poll would spend a model turn per tick for
                    // an answer that has not changed.
                    ask = wantedNames
                        .Where(n => ExistingKey(n) is null && !_asking.Contains(n))
                        .ToList();

                    foreach (var n in ask) _asking.Add(n);
                }

                if (ask.Count == 0) continue;

                Reply reply;

                try
                {
                    reply = await RequestAsync(
                        relay, MirrorProtocol.Hello, new Dictionary<string, string> { ["pv"] = "1" },
                        MirrorProtocol.PackRows(ask), TimeSpan.FromSeconds(120))
                        .ConfigureAwait(false);
                }
                finally
                {
                    // Released however it ended, including a throw. A name left
                    // marked as being asked about would never be asked again,
                    // which trades a flood for a silence and is the worse of the
                    // two failures: the flood was at least still trying.
                    lock (_gate) foreach (var n in ask) _asking.Remove(n);
                }

                NoteSpeaksPrompts(relay, reply);

                if (!reply.Ok || reply.Payload is null)
                {
                    // Silence is not "no". A relay that was busy or timed out is
                    // asked again on the next poll; only a real answer settles a
                    // name as unavailable.
                    continue;
                }

                var entries = MirrorProtocol.DecodeRoster(reply.Payload);
                if (entries is null) continue;

                lock (_gate)
                {
                    foreach (var entry in entries)
                    {
                        if (!entry.HasTranscript)
                        {
                            _answeredNo.Add(EntryKey(entry));
                            continue;
                        }

                        var key = EntryKey(entry);
                        _roster[key] = entry;
                        _servedBy[key] = relay;
                        _answeredNo.Remove(key);
                    }

                    // This relay's entries just changed by a route other than
                    // its full roster, so a hash held for it no longer
                    // describes what this side holds.
                    _rosterHashes.Remove(relay);

                    // Asked about and not mentioned means that Buddy does not
                    // have it — a session on a third machine, most likely.
                    foreach (var name in ask)
                    {
                        if (ExistingKey(name) is not null) continue;
                        _answeredNo.Add(name);
                    }
                }

                RosterUpdated?.Invoke();
            }
        }

        // The roster hash to hand back on the next ask, or null to hold none: only
        // a hash the server actually sent, and only once every picture the
        // roster named has arrived. An older server sends none, and a roster
        // this side has not fully taken in must be asked for in full again.
        internal static string? HashToHold(IReadOnlyDictionary<string, string>? fields, bool allPictures) =>
            allPictures
            && fields is not null
            && fields.TryGetValue(MirrorProtocol.RosterHashField, out var sent)
            && sent.Length > 0
                ? sent
                : null;

        // Gives every entry named by picture id its picture's bytes, from those
        // already held or with one AVATAR request per id not yet seen. Returns
        // whether every picture arrived.
        //
        // A picture that cannot be fetched, or whose bytes do not match the id
        // they were asked for, leaves the entry wearing whatever picture this
        // side already showed for it, rather than none. Swapping a face for
        // letters over a failed fetch would be a visible change caused by a
        // transport hiccup.
        private async Task<(List<MirrorProtocol.MirrorRosterEntry> Entries, bool AllPictures)> FillPicturesAsync(
            string peer, IReadOnlyList<MirrorProtocol.MirrorRosterEntry> entries)
        {
            var filled = new List<MirrorProtocol.MirrorRosterEntry>(entries.Count);
            var all = true;

            foreach (var entry in entries)
            {
                if (entry.Persona is not { AvatarId: { Length: > 0 } id, Avatar: null } persona)
                {
                    filled.Add(entry);
                    continue;
                }

                byte[]? bytes;
                lock (_gate) bytes = _pictures.Get(id);

                bytes ??= await FetchPictureAsync(peer, id).ConfigureAwait(false);

                if (bytes is null)
                {
                    all = false;
                    lock (_gate) bytes = _roster.GetValueOrDefault(EntryKey(entry))?.Persona?.Avatar;
                }

                filled.Add(entry with { Persona = persona with { Avatar = bytes } });
            }

            return (filled, all);
        }

        private async Task<byte[]?> FetchPictureAsync(string peer, string id)
        {
            var reply = await RequestAsync(
                peer, MirrorProtocol.Avatar,
                new Dictionary<string, string> { [MirrorProtocol.AvatarIdField] = id },
                payload: null, TimeSpan.FromSeconds(60))
                .ConfigureAwait(false);

            if (!reply.Ok || !MirrorProtocol.AvatarMatches(id, reply.Payload))
            {
                MirrorLog.Say("avatar-missed", $"from={peer} id={id} ok={reply.Ok}");
                return null;
            }

            lock (_gate) _pictures.Add(id, reply.Payload!);

            return reply.Payload;
        }

        // --- a session's feed --------------------------------------------------

        internal enum MirrorDelivery
        {
            // The opening read: everything the panel should show, replacing
            // whatever it had.
            Window,

            // Appended since.
            Delta
        }

        internal readonly record struct MirrorRows(
            string Name, MirrorDelivery Mode,
            IReadOnlyList<MirrorProtocol.MirrorTurn> Turns, string Cli, long Gen);

        // Verified rows, ready to be parsed by the same parsers a local panel
        // uses. Raised on a background thread; the chat session marshals.
        public event Action<MirrorRows>? Delivered;

        // The mirror could not be trusted. Carries wording-worthy detail, not a
        // code, because the panel says something specific about integrity here.
        public event Action<string, string>? Failed;

        private sealed class Feed
        {
            public required string Name;
            public required string Relay;
            public required string Cli;
            public long BacklogFrom;
            public long Gen;
            public string? WatchId;
            public DateTime RenewAt;
            public bool Loading;
        }

        private readonly Dictionary<string, Feed> _feeds = new(StringComparer.OrdinalIgnoreCase);

        // Whether any panel is currently mirroring a session through this client.
        //
        // **Exposed because watching was not counted as using.** Remote Control
        // retires its relays after RemoteControlIdleMinutes without use, and
        // "use" meant Touch(), which is called on send and nowhere else. A panel
        // sitting open and streaming a far machine's conversation touched
        // nothing, so the idle timer ran the whole time somebody was watching
        // and then shut the relays down underneath them.
        //
        // Measured overnight on 30 Aug 2026: a panel opened at 00:49 took 27
        // delta transfers and then stopped dead at 01:36 — about thirty minutes
        // after the last message was *sent*, which is what the timer was really
        // measuring. It showed 1 a.m. content at 8 a.m. with no indication that
        // it had stopped, because a mirror that has gone stale looks exactly
        // like one that is quiet.
        //
        // A feed exists between OpenAsync and CloseAsync, and CloseAsync runs
        // when the panel closes — so this is true for exactly as long as
        // somebody is looking, and no longer.
        internal bool Watching
        {
            get { lock (_gate) return _feeds.Count > 0; }
        }

        public bool HasMore(string name)
        {
            lock (_gate) return _feeds.TryGetValue(name, out var feed) && feed.BacklogFrom > 0;
        }

        // Opens a mirror for one session: the tail first, then a subscription so
        // what happens next arrives without being asked for.
        // One outstanding window per session, however many callers ask for it.
        //
        // The Loading flag below is not enough on its own, and CB-46 measured
        // why: it lives on the Feed, and CloseAsync *removes* the Feed. A panel
        // being rebound — closed and reopened, which clicking between two orbs
        // does constantly — therefore threw away the only record that a fetch
        // was already running, and the next PanelOpened started another. Four
        // distinct FETCHes for one session inside 78 seconds, against a
        // three-minute timeout, so none of them were retries.
        //
        // That is worse than wasted effort. Each one makes the far side build
        // and queue another whole window, so a relay already minutes deep in
        // chunk 0 acquires more behind it and the queue grows faster than a
        // model turn can drain it — the panel gets slower every time somebody
        // looks at it.
        //
        // Keyed outside the Feed so it survives the Feed being removed, and
        // handed out as the *same task* so a second caller waits on the first
        // answer rather than starting a second conversation about it.
        private readonly Dictionary<string, Task<bool>> _opening =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<bool> OpenAsync(string name)
        {
            lock (_gate)
            {
                if (_opening.TryGetValue(name, out var running)) return running;

                var started = OpenCoreAsync(name);

                // Only track it if it is actually still running: a synchronous
                // refusal (no roster entry) has already completed here, and
                // recording that would answer every later caller with the same
                // stale "no".
                if (!started.IsCompleted) _opening[name] = started;

                return started;
            }
        }

        private async Task<bool> OpenCoreAsync(string name)
        {
            try
            {
                return await OpenOnceAsync(name).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate) _opening.Remove(name);
            }
        }

        private async Task<bool> OpenOnceAsync(string name)
        {
            string relay;
            string cli;

            lock (_gate)
            {
                var key = ExistingKey(name);
                if (key is null || !_roster.TryGetValue(key, out var entry)) return false;
                if (!_servedBy.TryGetValue(key, out var found)) return false;

                if (_feeds.TryGetValue(name, out var already) && already.Loading) return true;

                relay = found;
                cli = entry.Cli;

                _feeds[name] = new Feed { Name = name, Relay = relay, Cli = cli, Loading = true };
            }

            var reply = await RequestAsync(
                relay, MirrorProtocol.Fetch,
                new Dictionary<string, string>
                {
                    ["n"] = MirrorProtocol.Encode(name),
                    ["w"] = "tail"
                },
                null, TimeSpan.FromSeconds(MirrorProtocol.FetchTimeoutSeconds))
                .ConfigureAwait(false);

            lock (_gate)
            {
                if (_feeds.TryGetValue(name, out var feed)) feed.Loading = false;
            }

            if (!reply.Ok || reply.Payload is null)
            {
                var why = reply.Reason ?? "the other machine didn't answer";
                Log("open", $"n={name} failed why={why}");
                Failed?.Invoke(name, why);
                return false;
            }

            var turns = MirrorProtocol.DecodeTurns(reply.Payload);
            if (turns is null) return SayItArrivedUnreadable(name);

            long gen = 0;

            lock (_gate)
            {
                if (_feeds.TryGetValue(name, out var feed))
                {
                    feed.BacklogFrom = Num(reply.Fields, "wfrom");
                    feed.Gen = Num(reply.Fields, "gen");
                    gen = feed.Gen;
                }
            }

            Deliver(new MirrorRows(name, MirrorDelivery.Window, turns, cli, gen));

            await RenewWatchAsync(name).ConfigureAwait(false);

            // After the hand-over and the renewal rather than before: an "ok"
            // that is written ahead of a Delivered handler which throws would
            // read as success for exactly the failure this log exists to show.
            Log("open", $"n={name} ok");
            return true;
        }

        // A panel that is being looked at again.
        //
        // Distinct from OpenAsync so that rebinding a panel to a session it is
        // already showing costs a renewal rather than another full read of the
        // tail — the panel is a singleton and clicking between two orbs rebinds
        // constantly.
        public async Task ReopenAsync(string name)
        {
            bool open;
            lock (_gate) open = _feeds.ContainsKey(name);

            if (open) await RenewWatchAsync(name).ConfigureAwait(false);
            else await OpenAsync(name).ConfigureAwait(false);
        }

        // One page further back, for scrollback. Null when there is no more, or
        // when the request failed — the two are told apart by HasMore.
        public async Task<IReadOnlyList<MirrorProtocol.MirrorTurn>?> LoadOlderAsync(string name)
        {
            string relay;
            long to;

            lock (_gate)
            {
                if (!_feeds.TryGetValue(name, out var feed) || feed.BacklogFrom <= 0) return null;

                relay = feed.Relay;
                to = feed.BacklogFrom;
            }

            var from = Math.Max(0, to - MirrorProtocol.PageBytes);

            var reply = await RequestAsync(
                relay, MirrorProtocol.Fetch,
                new Dictionary<string, string>
                {
                    ["n"] = MirrorProtocol.Encode(name),
                    ["w"] = "range",
                    ["from"] = from.ToString(),
                    ["to"] = to.ToString()
                },
                null, TimeSpan.FromSeconds(MirrorProtocol.FetchTimeoutSeconds))
                .ConfigureAwait(false);

            if (!reply.Ok || reply.Payload is null) return null;

            var turns = MirrorProtocol.DecodeTurns(reply.Payload);
            if (turns is null) return null;

            lock (_gate)
            {
                if (_feeds.TryGetValue(name, out var feed))
                {
                    // The aligned offset, not the one asked for. A window that
                    // landed inside one enormous row reports where it started,
                    // which steps over it rather than reading it forever — see
                    // RemoteMirrorServer.ReadWindow.
                    var aligned = Num(reply.Fields, "wfrom", from);
                    feed.BacklogFrom = aligned >= to ? 0 : aligned;
                }
            }

            return turns;
        }

        // Subscribes, or renews a subscription that is about to lapse.
        public async Task RenewWatchAsync(string name)
        {
            string relay;
            string? existing;

            lock (_gate)
            {
                if (!_feeds.TryGetValue(name, out var feed)) return;
                if (feed.WatchId is not null && DateTime.UtcNow < feed.RenewAt) return;

                relay = feed.Relay;
                existing = feed.WatchId;
                feed.RenewAt = DateTime.UtcNow.AddSeconds(MirrorProtocol.WatchRenewSeconds);
            }

            // Renewing keeps the same id, so the far side updates one
            // subscription instead of collecting a new one every ninety seconds.
            var id = existing ?? MirrorProtocol.NewId();

            lock (_gate)
            {
                if (_feeds.TryGetValue(name, out var feed)) feed.WatchId = id;
            }

            await RequestAsync(
                relay, MirrorProtocol.Watch,
                new Dictionary<string, string>
                {
                    ["n"] = MirrorProtocol.Encode(name),
                    ["ttl"] = MirrorProtocol.WatchTtlSeconds.ToString()
                },
                null, TimeSpan.FromSeconds(120), id)
                .ConfigureAwait(false);
        }

        // The panel closed. Told rather than left to lapse, so a far relay is
        // not kept awake by something nobody is looking at.
        public async Task CloseAsync(string name)
        {
            string? relay = null, watch = null;

            lock (_gate)
            {
                if (_feeds.Remove(name, out var feed))
                {
                    relay = feed.Relay;
                    watch = feed.WatchId;
                }
            }

            if (relay is null || watch is null) return;

            await RequestAsync(relay, MirrorProtocol.Unwatch, null, null,
                TimeSpan.FromSeconds(30), watch, awaitReply: false).ConfigureAwait(false);
        }

        // The richer answer to an INPUT. A typed send has nothing more to say
        // than whether it worked; a CB-105 delivery over the far session's
        // messaging socket also carries which channel was used and that
        // session's own agent status, which the composer hint and the
        // delivered-note both need to say something honest.
        internal readonly record struct InputOutcome(string? Error, string? Via, string? AgentStatus, string? Why = null,
            string? Verdict = null);

        // Types a line into the far session's own terminal, or — since
        // CB-105 — hands it to that session's own messaging socket when
        // there is no terminal to type into at all.
        //
        // Null Error on success; otherwise the error code, which the caller
        // turns into wording. A typed send is the path that makes /color
        // work again: the text is typed into that CLI's input line by the
        // Buddy running beside it, so its own command handler runs it,
        // exactly as it would locally. A delivered send has no such
        // guarantee — see MirrorProtocol's note on why there is no ack on
        // this wire at all.
        public async Task<InputOutcome> SendInputDetailedAsync(string name, string text, bool slash = false)
        {
            string relay;

            lock (_gate)
            {
                var key = ExistingKey(name);
                if (key is null || !_servedBy.TryGetValue(key, out var found))
                {
                    Log("input", $"n={name} -> err code={MirrorProtocol.ErrNoSession}");
                    return new InputOutcome(MirrorProtocol.ErrNoSession, null, null);
                }
                relay = found;
            }

            var reply = await RequestAsync(
                relay, MirrorProtocol.Input,
                SlashFields(name, slash),
                System.Text.Encoding.UTF8.GetBytes(text),
                TimeSpan.FromSeconds(MirrorProtocol.InputTimeoutSeconds))
                .ConfigureAwait(false);

            if (!reply.Ok)
            {
                var code = reply.ErrCode ?? MirrorProtocol.ErrUnsupported;
                Log("input", $"n={name} -> err code={code}");

                // CB-307 addendum: the far side's reason rides along as Why,
                // so a refusal that names a dialog reaches the panel. The
                // reason is msg, or the code itself when the far side sent
                // none, so it is never the meaningful part only by accident.
                return new InputOutcome(code, null, null,
                    reply.Reason is { } reason && reason != code ? reason : null);
            }

            Log("input", $"n={name} -> ok via="
                + (reply.Fields is not null && reply.Fields.TryGetValue("via", out var sentVia) ? sentVia : "typed"));

            return new InputOutcome(
                null,
                reply.Fields is not null && reply.Fields.TryGetValue("via", out var via) ? via : null,
                reply.Fields is not null && reply.Fields.TryGetValue("agent", out var agent) ? agent : null,
                reply.Fields is not null && reply.Fields.TryGetValue("why", out var why)
                    ? MirrorProtocol.Decode(why) : null,
                reply.Fields is not null && reply.Fields.TryGetValue(MirrorProtocol.VerdictField, out var verdict)
                    ? verdict : null);
        }

        // --- answering a dialog (CB-307 addendum item 3) -----------------------

        // What the far session is waiting on: its dialog or AskUserQuestion
        // form, read off its own pane by the far Buddy. Error is the far side's
        // code (ErrNoPrompt, ErrReplyOff, ErrNoPane, ErrUnsupported from a
        // Buddy that predates this) and Prompt is null whenever Error is set.
        private readonly HashSet<string> _speaksPrompts = new(StringComparer.OrdinalIgnoreCase);

        // Both HELLO paths (the named ask and the complete roster) record it.
        private void NoteSpeaksPrompts(string peer, Reply reply)
        {
            if (!reply.Ok) return;

            lock (_gate)
            {
                if (reply.Fields?.GetValueOrDefault(MirrorProtocol.PromptsField) == "1") _speaksPrompts.Add(peer);
                else _speaksPrompts.Remove(peer);
            }
        }

        // Whether the machine serving this session said it answers PROMPT and
        // ANSWER (MirrorProtocol.PromptsField). False before its first reply.
        public bool SpeaksPrompts(string name)
        {
            lock (_gate)
            {
                var key = ExistingKey(name);
                return key is not null && _servedBy.TryGetValue(key, out var relay) && _speaksPrompts.Contains(relay);
            }
        }

        internal readonly record struct PromptOutcome(MirrorProtocol.WirePrompt? Prompt, string? Error);

        public async Task<PromptOutcome> FetchPromptAsync(string name)
        {
            if (RelayFor(name) is not { } relay)
            {
                Log("prompt", $"n={name} -> err code={MirrorProtocol.ErrNoSession}");
                return new PromptOutcome(null, MirrorProtocol.ErrNoSession);
            }

            var reply = await RequestAsync(relay, MirrorProtocol.Prompt,
                new Dictionary<string, string> { ["n"] = MirrorProtocol.Encode(name) }, payload: null,
                TimeSpan.FromSeconds(MirrorProtocol.InputTimeoutSeconds)).ConfigureAwait(false);

            if (!reply.Ok)
            {
                var code = reply.ErrCode ?? MirrorProtocol.ErrUnsupported;
                Log("prompt", $"n={name} -> err code={code}");
                return new PromptOutcome(null, code);
            }

            var prompt = MirrorProtocol.DecodePrompt(reply.Payload);
            Log("prompt", prompt is null ? $"n={name} -> err unreadable" : $"n={name} -> ok fp={prompt.Fingerprint}");
            return prompt is null
                ? new PromptOutcome(null, MirrorProtocol.ErrUnsupported)
                : new PromptOutcome(prompt, null);
        }

        // Take one option of the dialog Fingerprint names. Null on success,
        // otherwise the far side's code — ErrPromptChanged means the screen
        // moved on and nothing was pressed, so ask FetchPromptAsync again.
        public Task<string?> AnswerPromptAsync(string name, string key, string fingerprint) =>
            SendAnswerAsync(name, fingerprint, key, action: null, text: null);

        // The "Type something" option with this text. Refused here, before
        // anything is sent, when the text is empty, blank or more than one
        // line: Enter on an empty field declines the whole form, and the far
        // side refuses the same text for the same reason (ErrBadKey).
        public Task<string?> AnswerFreeTextAsync(string name, string key, string fingerprint, string text)
        {
            if (!FormAnswer.IsUsableFreeText(text))
            {
                Log("answer", $"n={name} key={key} -> err code={MirrorProtocol.ErrBadKey} (no text)");
                return Task.FromResult<string?>(MirrorProtocol.ErrBadKey);
            }

            return SendAnswerAsync(name, fingerprint, key, action: null, text);
        }

        // Move a multi-select question on with its ticks kept.
        public Task<string?> CommitPromptAsync(string name, string fingerprint) =>
            SendAnswerAsync(name, fingerprint, key: null, MirrorProtocol.ActionCommit, text: null);

        private async Task<string?> SendAnswerAsync(string name, string fingerprint, string? key, string? action, string? text)
        {
            var what = action is null ? $"key={key}" : $"act={action}";
            if (RelayFor(name) is not { } relay)
            {
                Log("answer", $"n={name} {what} -> err code={MirrorProtocol.ErrNoSession}");
                return MirrorProtocol.ErrNoSession;
            }

            var fields = new Dictionary<string, string>
            {
                ["n"] = MirrorProtocol.Encode(name),
                [MirrorProtocol.FingerprintField] = fingerprint,
            };
            if (key is not null) fields[MirrorProtocol.KeyField] = key;
            if (action is not null) fields[MirrorProtocol.ActionField] = action;

            var reply = await RequestAsync(relay, MirrorProtocol.Answer, fields,
                text is null ? null : System.Text.Encoding.UTF8.GetBytes(text),
                TimeSpan.FromSeconds(MirrorProtocol.InputTimeoutSeconds)).ConfigureAwait(false);

            var error = reply.Ok ? null : reply.ErrCode ?? MirrorProtocol.ErrUnsupported;
            Log("answer", $"n={name} {what} -> " + (error is null ? "ok" : $"err code={error}"));
            return error;
        }

        // CB-306: the INPUT's fields, with the slash flag only when it is set,
        // so an ordinary send is byte-for-byte the frame it always was.
        internal static Dictionary<string, string> SlashFields(string name, bool slash)
        {
            var fields = new Dictionary<string, string> { ["n"] = MirrorProtocol.Encode(name) };
            if (slash) fields[MirrorProtocol.SlashField] = "1";
            return fields;
        }

        // Kept for callers that only ever cared whether a send worked.
        public async Task<string?> SendInputAsync(string name, string text) =>
            (await SendInputDetailedAsync(name, text).ConfigureAwait(false)).Error;

        // Excluded from coverage: reaching it needs a payload that passes every
        // per-piece hash *and* the whole-payload hash and still is not a turn
        // list — which means the far machine gzipped something else under the
        // right digests, not a courier mangling anything. No test can build that
        // without also building a second, dishonest MirrorProtocol.
        //
        // Kept because a version skew is exactly how it would happen, and
        // saying so beats a live view that quietly shows nothing. What it says
        // is asserted in MirrorAndRoutingArmsTests, which covers DecodeTurns
        // answering null for every input a test can construct.
        [ExcludeFromCodeCoverage]
        private bool SayItArrivedUnreadable(string name)
        {
            Log("open", $"n={name} failed why=the transcript arrived unreadable");
            Failed?.Invoke(name, "the transcript arrived unreadable");
            return false;
        }

        // Excluded from coverage: a watch is renewed 90 seconds before a
        // 120-second TTL expires, so nothing is ever due inside a test — the
        // alternative is a test that waits a minute and a half, which is exactly
        // the shape of timing dependence this repository has repeatedly removed
        // rather than added.
        //
        // The loop that consumes it stays measured, because a tick with nothing
        // due is the ordinary case and is what every test exercises.
        [ExcludeFromCodeCoverage]
        private List<string> DueForRenewal()
        {
            lock (_gate)
            {
                return _feeds.Values
                    .Where(f => f.WatchId is not null && DateTime.UtcNow >= f.RenewAt)
                    .Select(f => f.Name)
                    .ToList();
            }
        }

        // Renews anything due. Called on the same timer that drains the relay.
        public async Task TickAsync()
        {
            var due = DueForRenewal();

            foreach (var name in due) await RenewWatchAsync(name).ConfigureAwait(false);
        }

        public bool Busy
        {
            get { lock (_gate) return _feeds.Count > 0; }
        }

        // --- request / reply ---------------------------------------------------

        private readonly record struct Reply(
            bool Ok, byte[]? Payload, IReadOnlyDictionary<string, string>? Fields,
            string? ErrCode, string? Reason);

        private sealed class Pending
        {
            public required TaskCompletionSource<Reply> Waiter;
            public required string Relay;
            public required MirrorProtocol.MirrorAssembly Assembly;
            public int Resends;

            // When to give up, and it moves. A flat deadline asks the wrong
            // question on this wire: a transfer is not late because it is
            // broken, it is late because every chunk costs a model turn, so a
            // multi-chunk answer can be arriving perfectly and still miss any
            // fixed cut-off. Each chunk that verifies buys another full
            // interval, so what is actually being timed is *silence* rather
            // than duration — which is the thing that means something has gone
            // wrong. Bounded regardless, because chunks are finite: `of` says
            // how many there are and a far side cannot extend this for ever
            // without sending real, hash-checked payload each time.
            public DateTime Deadline;
            public TimeSpan Grace;
        }

        private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

        // Whether this client is waiting on an answer right now.
        //
        // **Exposed so the poll can get out of the way.** A relay carries one
        // frame per model turn, and Buddy's own poll asks it for ListAgents on
        // every tick — every 20 seconds, or every 5 while anything is working.
        // A ListAgents answer was measured at about 6 seconds on a real relay,
        // so at the fast cadence the relay is never idle, and a FETCH the user
        // is actually waiting for queues behind an unbroken run of polls.
        //
        // Measured on 30 Aug 2026: a panel opened, and its FETCH was not typed
        // into the relay for roughly eight minutes, by which time the client's
        // deadline had already burned. The far machine then answered correctly
        // and the reply was dropped for arriving too late — the same ending as
        // CB-54 and a completely different cause, which is exactly why it was
        // mistaken for CB-54 not being fixed.
        //
        // The poll is refreshing a peer list that changes rarely; the fetch is
        // the thing somebody is looking at. When the two compete for the same
        // relay, the fetch wins. Bounded by construction: every pending request
        // carries a deadline, so this cannot stay true for longer than one.
        internal bool Waiting
        {
            get { lock (_gate) return _pending.Count > 0; }
        }

        // Transfers arriving unbidden — the deltas a subscription pushes. Keyed
        // by the transfer's own id, which is fresh each time, and matched back
        // to a feed by the `sub` field it carries.
        private readonly Dictionary<string, MirrorProtocol.MirrorAssembly> _unsolicited =
            new(StringComparer.Ordinal);

        // Shortens every wait below, for tests only.
        //
        // The real timeouts are minutes, because a request has to survive the
        // far relay's model being mid-turn on something else. A test that
        // deliberately drops a reply would otherwise sit out the whole of one —
        // and "the relay never answered" is a path worth covering, not one worth
        // two minutes of a CI run.
        internal TimeSpan? TimeoutOverrideForTests { get; set; }

        // How many times a wait has been pushed out by a verified chunk
        // arriving.
        //
        // For tests, and it exists because the obvious assertion cannot be made
        // honestly. The idle timeout's claim is "progress resets the wait", and
        // the tempting way to check that is to run a transfer slower than one
        // interval and time it — which is a wall-clock claim a loaded CI runner
        // will not honour, and it duly went red on macOS while passing on
        // Windows. Widening the window would only move the failure; a sleep
        // would be the same mistake this repository has already fixed four
        // times.
        //
        // Counting the resets asserts the same mechanism with no clock in it at
        // all: the deadline is pushed out once per intermediate chunk,
        // unconditionally, so the count is a property of the transfer's shape
        // rather than of how fast the machine happened to be. A flat deadline
        // scores zero, which is exactly the regression worth catching.
        internal int TimeoutExtensionsForTests { get; private set; }

        private async Task<Reply> RequestAsync(
            string relay, string type,
            IReadOnlyDictionary<string, string>? fields, byte[]? payload,
            TimeSpan timeout, string? id = null, bool awaitReply = true)
        {
            if (TimeoutOverrideForTests is { } shorter) timeout = shorter;

            id ??= MirrorProtocol.NewId();

            var waiter = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_gate)
            {
                _pending[id] = new Pending
                {
                    Waiter = waiter,
                    Relay = relay,
                    Assembly = new MirrorProtocol.MirrorAssembly(),
                    Deadline = DateTime.UtcNow + timeout,
                    Grace = timeout
                };
            }

            try
            {
                var frame = MirrorProtocol.BuildFrame(type, id, fields, payload);

                if (!await Send(relay, frame).ConfigureAwait(false))
                    return new Reply(false, null, null, null, "couldn't reach the other machine");

                if (!awaitReply) return new Reply(true, null, null, null, null);

                // Held rather than looked up each pass. Nothing removes it
                // before the finally below, so a lookup here can only ever
                // succeed — and a miss arm that cannot run reads as an untested
                // branch for ever.
                Pending mine;
                lock (_gate) mine = _pending[id];

                while (true)
                {
                    TimeSpan left;
                    lock (_gate) left = mine.Deadline - DateTime.UtcNow;

                    if (left <= TimeSpan.Zero) break;

                    var done = await Task.WhenAny(waiter.Task, Task.Delay(left)).ConfigureAwait(false);
                    if (done == waiter.Task) return waiter.Task.Result;

                    // The delay won, but a chunk may have landed while it was
                    // running and pushed the deadline out. Looping re-reads it
                    // rather than assuming this wait was the last one.
                }

                return new Reply(false, null, null, null, "the other machine didn't answer in time");
            }
            finally
            {
                lock (_gate) _pending.Remove(id);
            }
        }

        // Every piece verified individually and the reassembled whole still did
        // not match. Fatal for the transfer, unlike a single bad piece: there is
        // nothing to re-request usefully, because asking for any one of them
        // would return the same bytes — so the panel is told rather than left
        // looking healthy.
        //
        // Reached by a courier that re-signs what it alters, which is a
        // realistic relay rather than a contrived one — anything that reformats
        // a line and re-frames it lands here. See
        // ATransferWhoseWholeDigestFailsIsReportedRatherThanRetriedForEver.
        private void SettleAsUnverifiable(string id, MirrorProtocol.AssemblyResult result) =>
            Settle(id, new Reply(
                false, null, null, MirrorProtocol.ErrBadHash,
                result.Reason ?? "it failed its integrity check"));

        // Excluded from coverage: exists to be the try/catch around the relay,
        // and the swallow is what turns a relay that has gone away into
        // "couldn't reach the other machine" in the panel rather than an exception on a
        // background task. Asserted through a courier that throws in
        // MirrorRoundTripTests; only the swallow itself is unmeasured.
        [ExcludeFromCodeCoverage]
        private async Task<bool> Send(string relay, string frame)
        {
            try { return await _seams.SendFrame(relay, frame).ConfigureAwait(false); }
            catch { return false; }
        }

        // --- inbound -----------------------------------------------------------

        // One frame from a far Buddy. Called on the relay's pump thread.
        public async Task OnFrameAsync(string fromRelay, MirrorProtocol.MirrorFrame frame)
        {
            switch (frame.Type)
            {
                case MirrorProtocol.Chunk:
                    await ChunkAsync(fromRelay, frame).ConfigureAwait(false);
                    break;

                case MirrorProtocol.Ok:
                    Settle(frame.Id, new Reply(true, null, frame.Fields, null, null));
                    break;

                case MirrorProtocol.Err:
                    Settle(frame.Id, new Reply(
                        false, null, frame.Fields,
                        frame.Get("code"),
                        frame.Text("msg") ?? frame.Get("code")));
                    break;
            }
        }

        private async Task ChunkAsync(string fromRelay, MirrorProtocol.MirrorFrame frame)
        {
            Pending? pending;
            lock (_gate) _pending.TryGetValue(frame.Id, out pending);

            if (pending is not null)
            {
                var result = pending.Assembly.Offer(frame);

                switch (result.State)
                {
                    case MirrorProtocol.AssemblyState.Incomplete:
                        // Progress. The far side is working through a transfer
                        // one model turn at a time, so the wait starts again
                        // rather than counting down towards a cut-off the
                        // transfer was never going to meet.
                        lock (_gate)
                        {
                            pending.Deadline = DateTime.UtcNow + pending.Grace;
                            TimeoutExtensionsForTests++;
                        }

                        return;

                    case MirrorProtocol.AssemblyState.Complete:
                        Settle(frame.Id, new Reply(true, result.Payload, frame.Fields, null, null));
                        return;

                    case MirrorProtocol.AssemblyState.BadChunk:
                        // Asked for again rather than abandoned: on a long
                        // transcript one mangled piece out of thirty should cost
                        // one round trip, not the whole transfer.
                        if (++pending.Resends <= MirrorProtocol.ResendAttempts)
                        {
                            await Send(pending.Relay, MirrorProtocol.BuildFrame(
                                MirrorProtocol.Resend, frame.Id,
                                new Dictionary<string, string> { ["seq"] = result.BadSeq.ToString() }))
                                .ConfigureAwait(false);
                            return;
                        }

                        Settle(frame.Id, new Reply(
                            false, null, null, MirrorProtocol.ErrBadHash,
                            $"a piece of it failed its integrity check {pending.Resends} times"));
                        return;

                    default:
                        SettleAsUnverifiable(frame.Id, result);
                        return;
                }
            }

            // Not a reply to anything outstanding, so it is a subscription
            // pushing what's new.
            var sub = frame.Get("sub");
            if (sub is null) return;

            MirrorProtocol.MirrorAssembly assembly;
            lock (_gate)
            {
                if (!_unsolicited.TryGetValue(frame.Id, out var existing))
                {
                    existing = new MirrorProtocol.MirrorAssembly();
                    _unsolicited[frame.Id] = existing;

                    // Bounded so a far side that starts a transfer and never
                    // finishes it cannot accumulate.
                    while (_unsolicited.Count > 16)
                    {
                        var oldest = _unsolicited.Keys.First();
                        if (oldest == frame.Id) break;
                        _unsolicited.Remove(oldest);
                    }
                }

                assembly = existing;
            }

            var delta = assembly.Offer(frame);
            if (delta.State == MirrorProtocol.AssemblyState.Incomplete) return;

            lock (_gate) _unsolicited.Remove(frame.Id);

            string? name = null;
            string cli = MirrorProtocol.CliClaudeCode;

            lock (_gate)
            {
                foreach (var feed in _feeds.Values)
                {
                    if (!string.Equals(feed.WatchId, sub, StringComparison.Ordinal)) continue;

                    name = feed.Name;
                    cli = feed.Cli;
                    break;
                }
            }

            if (name is null) return;

            if (delta.State != MirrorProtocol.AssemblyState.Complete || delta.Payload is null)
            {
                SayTheUpdateWasBroken(name, delta);
                return;
            }

            var turns = MirrorProtocol.DecodeTurns(delta.Payload);
            if (turns is null || turns.Count == 0) return;

            var gen = Num(frame.Fields, "gen");

            // The far transcript was replaced — /clear, most likely. Everything
            // held is about a file that no longer exists, so the feed re-anchors
            // instead of appending to it.
            long known;
            lock (_gate) known = _feeds.TryGetValue(name, out var feed) ? feed.Gen : gen;

            if (gen != known)
            {
                _ = OpenAsync(name);
                return;
            }

            Deliver(new MirrorRows(name, MirrorDelivery.Delta, turns, cli, gen));
        }

        private void Deliver(MirrorRows rows)
        {
            Log("delivered",
                $"n={rows.Name} mode={(rows.Mode == MirrorDelivery.Window ? "window" : "delta")} turns={rows.Turns.Count}");
            Delivered?.Invoke(rows);
        }

        private void Settle(string id, Reply reply)
        {
            Pending? pending;
            lock (_gate) _pending.TryGetValue(id, out pending);

            pending?.Waiter.TrySetResult(reply);
        }

        // A delta cannot be re-requested usefully — the far side has already
        // moved its offset past it — so the honest answer is to say the live
        // view is broken rather than skip a message and carry on looking
        // healthy. Skipping is the tempting alternative and the wrong one: a
        // panel that has quietly lost a message is worse than one that says it
        // stopped.
        private void SayTheUpdateWasBroken(string name, MirrorProtocol.AssemblyResult delta) =>
            Failed?.Invoke(name, delta.Reason ?? "an update failed its integrity check");

        private static long Num(IReadOnlyDictionary<string, string>? fields, string key, long fallback = 0) =>
            fields is not null && fields.TryGetValue(key, out var v) && long.TryParse(v, out var n)
                ? n
                : fallback;
    }
}
