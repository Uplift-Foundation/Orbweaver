using System.Text.Json;

namespace Orbweaver
{
    // What a paired phone is told about this machine's sessions: the plaintext
    // inside `ct` for milestone 2's `roster` and `delta` (CB-314 plan §2.4).
    // Pure — no sockets, no clock, no settings — so every rule about which
    // session is offered, what it is called and when a change becomes a frame
    // is a unit test away.
    //
    // Three pieces, in the order a scan flows through them:
    //
    //  * Project — the scan's SessionStatus objects into immutable rows. Runs
    //    on the scan's own thread, synchronously, because SessionStatus is
    //    mutable and the scan reuses it; nothing downstream ever holds one.
    //  * RelaySessionBook — the machine's current rows plus when each entered
    //    its state. One per desktop, shared by every phone, so two phones never
    //    disagree about `since`.
    //  * RelayPhoneStream — one per phone, numbering frames. `seq` starts at 1
    //    on every roster and counts up through deltas; a phone that sees a gap
    //    asks for a resync and gets a fresh roster.
    //
    // A delta can only carry state, title and since (plan §2.4), so anything
    // else changing — a session arriving, its folder, its lead, its persona —
    // is sent as a whole roster. Rosters are a few hundred bytes per session,
    // and arrivals are rare next to state flips, which is what deltas are for.
    internal sealed record RelayPersona(string Name, string Initials, string? Color);

    internal sealed record RelaySession(
        string SessionId,
        string Cli,
        string Title,
        string Cwd,
        string State,
        string? ParentSessionId,
        RelayPersona? Persona)
    {
        // When this session entered State, as this desktop saw it. Not part of
        // what Project produces — the book stamps it — and not compared when
        // deciding whether anything changed.
        internal DateTimeOffset Since { get; init; }

        internal bool SameShape(RelaySession other) =>
            Cli == other.Cli && Cwd == other.Cwd && ParentSessionId == other.ParentSessionId && Persona == other.Persona;
    }

    internal sealed record RelayDelta(string SessionId, string? State, string? Title, DateTimeOffset? Since, bool Removed);

    internal readonly record struct RelayUpdate(bool NeedsRoster, IReadOnlyList<RelayDelta> Deltas)
    {
        internal bool IsEmpty => !NeedsRoster && Deltas.Count == 0;
    }

    internal static class RelayRoster
    {
        internal const string Idle = "idle";
        internal const string Generating = "generating";
        internal const string Waiting = "waiting";
        internal const string Ended = "ended";

        // The orb's own vocabulary is already the spec's for the live states —
        // the hook writes "generating" and "waiting", OrbWindow.ApplyState
        // animates exactly those two, and treats everything else as idle. So
        // does this, rather than guessing a fourth state the phone would have
        // to draw.
        internal static string MapState(string? state) => state switch
        {
            Generating => Generating,
            Waiting => Waiting,
            _ => Idle,
        };

        // A local CLI session only, as the LAN roster offers — an OpenClaw
        // conversation, a Remote Control bridge or a cloud session is not a
        // session on this machine, and a peer's mirrored orb is the peer's to
        // report. Ordered by id so two projections of one scan are equal.
        internal static IReadOnlyList<RelaySession> Project(
            IEnumerable<(string SessionId, SessionStatus Status)> sessions,
            Func<SessionStatus, RelayPersona?>? persona = null) =>
            sessions
                .Where(s => s.Status.IsLocalCli)
                .OrderBy(s => s.SessionId, StringComparer.Ordinal)
                .Select(s => new RelaySession(
                    s.SessionId,
                    MirrorProtocol.CliFor(s.Status.Source),
                    TrayController.DisplayName(new TrayController.SessionEntry(s.SessionId, s.Status)),
                    s.Status.Cwd,
                    MapState(s.Status.State),
                    s.Status.Source == SessionSource.ClaudeCode && !string.IsNullOrEmpty(s.Status.Lead) ? s.Status.Lead : null,
                    persona?.Invoke(s.Status)))
                .ToList();

        // The persona a phone draws for a session: the name the persona files
        // give it, the orb's own two-letter glyph for that name, and the team
        // colour if Claude Code assigned one, else the session's /color. No
        // picture — milestone 2's roster carries none — and nothing at all
        // without a name, since initials of nothing are not a persona.
        internal static RelayPersona? PersonaFrom(string? name, SessionStatus status)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var color = PersonaColor(status.AgentColor) ?? PersonaColor(status.Color);
            return new RelayPersona(name, OrbGlyph.Initials(name), color);
        }

        // The colour names a phone may be sent (CB-314 addendum 4): exactly the
        // /color names an orb itself draws (OrbWindow.AgentColors, pinned equal
        // by RelaySettingsAndServiceTests), lowercase, never hex. Published as
        // contracts/persona-colors.json. Anything else — a hex value, a name
        // Claude Code adds later — is omitted and the phone draws neutral.
        internal static readonly IReadOnlyList<string> PersonaColors =
        [
            "red", "orange", "yellow", "green", "teal", "cyan", "blue",
            "purple", "violet", "magenta", "pink", "gray", "grey", "white",
        ];

        internal static string? PersonaColor(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var lower = name.Trim().ToLowerInvariant();
            return PersonaColors.Contains(lower) ? lower : null;
        }

        // since on the wire: integer Unix seconds, the same type and unit as
        // the envelope's ts and presence's last_seen (addendum 4).
        internal static long Timestamp(DateTimeOffset t) => t.ToUnixTimeSeconds();

        internal static byte[] RosterJson(long seq, IEnumerable<RelaySession> sessions)
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("t", "roster");
                w.WriteNumber("seq", seq);
                w.WriteStartArray("sessions");
                foreach (var s in sessions)
                {
                    w.WriteStartObject();
                    w.WriteString("session_id", s.SessionId);
                    w.WriteString("cli", s.Cli);
                    w.WriteString("title", s.Title);
                    w.WriteString("cwd", s.Cwd);
                    w.WriteString("state", s.State);
                    w.WriteNumber("since", Timestamp(s.Since));
                    if (s.ParentSessionId is { } parent) w.WriteString("parent_session_id", parent);
                    if (s.Persona is { } p)
                    {
                        w.WriteStartObject("persona");
                        w.WriteString("name", p.Name);
                        w.WriteString("initials", p.Initials);
                        if (p.Color is { } color) w.WriteString("color", color);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return buffer.ToArray();
        }

        internal static byte[] DeltaJson(long seq, RelayDelta d)
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("t", "delta");
                w.WriteNumber("seq", seq);
                w.WriteString("session_id", d.SessionId);
                if (d.State is { } state) w.WriteString("state", state);
                if (d.Title is { } title) w.WriteString("title", title);
                if (d.Since is { } since) w.WriteNumber("since", Timestamp(since));
                if (d.Removed) w.WriteBoolean("removed", true);
                w.WriteEndObject();
            }
            return buffer.ToArray();
        }
    }

    // The desktop's current rows, and what changed since the last scan.
    internal sealed class RelaySessionBook
    {
        private readonly Dictionary<string, RelaySession> _rows = new(StringComparer.Ordinal);

        internal IReadOnlyList<RelaySession> Snapshot() =>
            _rows.Values.OrderBy(r => r.SessionId, StringComparer.Ordinal).ToList();

        internal RelayUpdate Update(IReadOnlyList<RelaySession> current, DateTimeOffset now)
        {
            var needsRoster = false;
            var deltas = new List<RelayDelta>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var row in current)
            {
                seen.Add(row.SessionId);
                if (!_rows.TryGetValue(row.SessionId, out var old))
                {
                    _rows[row.SessionId] = row with { Since = now };
                    needsRoster = true;
                    continue;
                }

                if (!row.SameShape(old)) needsRoster = true;

                var stateChanged = row.State != old.State;
                var titleChanged = row.Title != old.Title;
                var since = stateChanged ? now : old.Since;
                _rows[row.SessionId] = row with { Since = since };

                if (stateChanged || titleChanged)
                    deltas.Add(new RelayDelta(row.SessionId,
                        stateChanged ? row.State : null,
                        titleChanged ? row.Title : null,
                        stateChanged ? since : null,
                        Removed: false));
            }

            // Gone from the scan: one last delta saying it ended, and the row
            // goes. A later roster simply does not list it.
            var gone = new List<string>();
            foreach (var id in _rows.Keys)
                if (!seen.Contains(id)) gone.Add(id);
            foreach (var id in gone)
            {
                _rows.Remove(id);
                deltas.Add(new RelayDelta(id, RelayRoster.Ended, null, now, Removed: true));
            }

            return new RelayUpdate(needsRoster, deltas);
        }
    }

    // One phone's numbering. Not thread-safe; RelayClient owns one per phone
    // and touches it only from its send loop.
    internal sealed class RelayPhoneStream
    {
        internal long Seq { get; private set; }

        internal byte[] Roster(IEnumerable<RelaySession> sessions)
        {
            Seq = 1;
            return RelayRoster.RosterJson(Seq, sessions);
        }

        // Null before any roster: a delta numbered against nothing is a gap by
        // definition, so the phone must see a roster first.
        internal byte[]? Delta(RelayDelta delta)
        {
            if (Seq == 0) return null;
            Seq++;
            return RelayRoster.DeltaJson(Seq, delta);
        }
    }
}
