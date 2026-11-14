using System;
using System.Collections.Generic;
using System.Linq;

namespace Orbweaver
{
    // CB-276: one session the user asked not to see as an orb, as Settings
    // lists it. Everything but SessionId and HiddenAtUtc is captured at the
    // moment of hiding, because by the time anyone reads the list the session
    // has no orb to ask: the row has to say what it was without one.
    //
    // HiddenAtUtc is DateTime.MinValue when it is not known, which only a
    // hand-edited file produces. A bad timestamp costs the "hidden 2 h ago"
    // text, never the hide itself.
    //
    // Peer is the far machine's ref for a Remote Control session and null for
    // everything else. Pruning reads it: a remote session is only known to
    // have ended when the machine it ran on is answering and no longer lists
    // it, and a `rc:` key does not say which machine that is.
    internal sealed record HiddenOrb(
        string SessionId,
        DateTime HiddenAtUtc,
        string Title,
        string Source,
        string Detail,
        string? Peer = null);

    // The rules behind "Hide this orb", kept pure so they can be tested without
    // a window, a settings file or a scan: no statics read, nothing written.
    // SessionManager owns the call sites and OrbweaverSettings owns the storage;
    // this decides.
    internal static class HiddenOrbs
    {
        // How long a hidden session's own source has to keep saying "answering,
        // and not listing it" before the entry is dropped. Chosen, not measured:
        // it only has to outlast one missed tick of the slowest source that can
        // say "false" at all — the 2 s scan and the gateway's 5 s poll — and a
        // source that publishes nothing answers true or null, never false, so it
        // does not have to outlast the slow ones.
        internal static readonly TimeSpan PruneGrace = TimeSpan.FromMinutes(2);

        // THE predicate: every id that is hidden this pass. The explicit entries,
        // plus every session with a hidden ancestor through the leads given —
        // so hiding a lead hides its team and its team's team, and a member
        // hidden on its own hides nobody above or beside it.
        //
        // Cycle-safe because it walks upward with a visited set rather than
        // recursing; a lead that is not among `sessions` simply ends the walk.
        // An explicit id that is not in `sessions` is still returned: an entry
        // for a session that is not on screen this pass is still a hide.
        internal static HashSet<string> Effective(
            IReadOnlyCollection<string> explicitIds,
            IEnumerable<(string SessionId, string? Lead)> sessions)
        {
            var result = new HashSet<string>(explicitIds, StringComparer.OrdinalIgnoreCase);
            if (result.Count == 0) return result;

            var leadOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, lead) in sessions)
            {
                if (!string.IsNullOrEmpty(lead)) leadOf[id] = lead;
            }

            var explicitSet = new HashSet<string>(explicitIds, StringComparer.OrdinalIgnoreCase);
            foreach (var id in leadOf.Keys)
            {
                if (result.Contains(id)) continue;

                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
                var current = id;
                while (leadOf.TryGetValue(current, out var lead) && visited.Add(lead))
                {
                    if (explicitSet.Contains(lead))
                    {
                        result.Add(id);
                        break;
                    }

                    current = lead;
                }
            }

            return result;
        }

        // Which lead propagates a hide. Agent teams only: a Claude Code team,
        // and the same team seen from another machine. An OpenClaw session's
        // Lead field holds its *room*, and a room is not a team lead for this
        // purpose — hiding a room hides the room orb and leaves its members
        // standing, which is the requester's decision 4.
        internal static string PropagatingLead(SessionSource source, string? lead) =>
            source is SessionSource.ClaudeCode or SessionSource.RemoteControl
                ? lead ?? ""
                : "";

        // What Settings shows for a session, captured as it is hidden.
        internal static HiddenOrb Describe(
            string sessionId, SessionStatus status, DateTime nowUtc, string? peer)
        {
            var folder = FolderName(status.Cwd);
            var title = !string.IsNullOrWhiteSpace(status.Title) ? status.Title.Trim()
                : folder.Length > 0 ? folder
                : sessionId;

            // The thing that tells two rows with the same title apart: the
            // member's name in its team first, since every member inherits its
            // lead's title; then where it runs; then, for a session on another
            // machine, which machine.
            var detail = !string.IsNullOrWhiteSpace(status.Agent) ? status.Agent.Trim()
                : !string.IsNullOrWhiteSpace(status.Cwd) ? status.Cwd
                : status.Source == SessionSource.RemoteControl ? peer ?? ""
                : "";

            return new HiddenOrb(
                sessionId,
                nowUtc,
                title,
                SourceLabel(status),
                detail,
                status.Source == SessionSource.RemoteControl ? peer : null);
        }

        // The badge text for a session's source, in the words the rest of the
        // app uses for it.
        internal static string SourceLabel(SessionStatus status) => status.Source switch
        {
            SessionSource.ClaudeCode => status.Shape == LocalSessionShape.Background
                ? "Background job"
                : "Claude Code",
            SessionSource.Codex => "Codex",
            SessionSource.Grok => "Grok",
            SessionSource.OpenClaw => status.IsRoom ? "OpenClaw room" : "OpenClaw",
            SessionSource.RemoteControl => "Other machine",
            SessionSource.ClaudeCloud => "Claude cloud",
            _ => status.Source.ToString()
        };

        // "hidden 2 h ago". Empty when the time is unknown, rather than
        // something invented. A timestamp in the future — a clock that moved
        // back, or a hand edit — reads as just now rather than as negative.
        internal static string WhenText(DateTime hiddenAtUtc, DateTime nowUtc)
        {
            if (hiddenAtUtc == DateTime.MinValue) return "";

            var age = nowUtc - hiddenAtUtc;
            if (age < TimeSpan.FromMinutes(1)) return "hidden just now";
            if (age < TimeSpan.FromHours(1)) return $"hidden {(int)age.TotalMinutes} min ago";
            if (age < TimeSpan.FromDays(1)) return $"hidden {(int)age.TotalHours} h ago";

            var days = (int)age.TotalDays;
            return days == 1 ? "hidden 1 day ago" : $"hidden {days} days ago";
        }

        // The orb menu's wording, decided before the click so a lead says what
        // it is about to do to its team.
        internal static string MenuHeader(bool leadsATeam) =>
            leadsATeam ? "Hide this orb and its team" : "Hide this orb";

        // Which hidden ids are due to be forgotten. listed(id) answers true when
        // the session's own source lists it, false when that source is
        // answering and does not, and null when the source cannot say right now
        // — off, disconnected, halted, not yet listed. Only an unbroken run of
        // false for `grace` prunes; a true or a null resets the clock. That null
        // is what keeps a gateway restart or a dropped network from un-hiding
        // anything.
        //
        // Mutates absentSince: starts a clock on the first false, clears it on
        // anything else, and forgets ids that are no longer hidden or that are
        // returned here as due.
        internal static IReadOnlyList<string> DuePrunes(
            IEnumerable<string> hiddenIds,
            Func<string, bool?> listed,
            IDictionary<string, DateTime> absentSince,
            DateTime now,
            TimeSpan grace)
        {
            var ids = new HashSet<string>(hiddenIds, StringComparer.OrdinalIgnoreCase);

            // Two plain loops rather than a Where(...).ToList(): the LINQ form
            // carried a branch arc coverage could not attribute to anything a
            // test chooses, and this form has only the two that matter — a
            // clock for an id that is no longer hidden, and one that still is.
            var stale = new List<string>();
            foreach (var key in absentSince.Keys)
            {
                if (!ids.Contains(key)) stale.Add(key);
            }

            foreach (var key in stale) absentSince.Remove(key);

            var due = new List<string>();
            foreach (var id in ids)
            {
                if (listed(id) != false)
                {
                    absentSince.Remove(id);
                    continue;
                }

                if (!absentSince.TryGetValue(id, out var since))
                {
                    absentSince[id] = now;
                    continue;
                }

                if (now - since >= grace)
                {
                    due.Add(id);
                    absentSince.Remove(id);
                }
            }

            return due;
        }

        // Whether a hidden session's own source still lists it: the `listed`
        // that DuePrunes is handed, with every source's listing passed in so
        // the rule can be tested without one of them running.
        //
        // Each listing is null when that source cannot say right now — the
        // status folder could not be read, OpenClaw is off or has not listed
        // on this connection, a cloud account is not authoritative, a peer is
        // not answering — and a null listing answers null, never false. The
        // id's own prefix decides which source is asked, and an id whose
        // prefix this does not know answers null: an unrecognised entry is
        // never pruned.
        //
        // OpenClaw keys are the gateway's own, without this app's
        // "openclaw:" prefix; cloud ids are the account API's own, without
        // "cloud:"; Remote Control keys are the orb keys, "rc:…", grouped by
        // the peer ref that lists them.
        internal static bool? Listed(
            string id,
            string? peer,
            IReadOnlySet<string>? localIds,
            IReadOnlySet<string>? openClawKeys,
            IReadOnlySet<string>? cloudIds,
            IReadOnlyDictionary<string, IReadOnlySet<string>>? remoteByPeer)
        {
            const string room = "openclaw:room:";
            const string openClaw = "openclaw:";
            const string cloud = "cloud:";
            const string remote = "rc:";

            if (id.StartsWith(room, StringComparison.Ordinal))
            {
                if (openClawKeys is null) return null;
                var roomKey = id.Substring(room.Length);
                return openClawKeys.Any(k => OpenClawSessionKind.RoomOf(k) == roomKey);
            }

            if (id.StartsWith(openClaw, StringComparison.Ordinal))
            {
                return openClawKeys?.Contains(id.Substring(openClaw.Length));
            }

            if (id.StartsWith(cloud, StringComparison.Ordinal))
            {
                return cloudIds?.Contains(id.Substring(cloud.Length));
            }

            if (id.StartsWith(remote, StringComparison.Ordinal))
            {
                // A peer that lists nothing is a peer that is off, asleep or
                // disconnected as far as this can tell — not one that has
                // positively said the session ended.
                if (string.IsNullOrEmpty(peer) || remoteByPeer is null) return null;
                if (!remoteByPeer.TryGetValue(peer, out var keys) || keys.Count == 0) return null;
                return keys.Contains(id);
            }

            // Anything else with a colon in it is a source this rule does not
            // know; a bare id is a local status file.
            if (id.Contains(':')) return null;
            return localIds?.Contains(id);
        }

        private static string FolderName(string cwd)
        {
            // Both separators on every platform rather than Path.GetFileName,
            // which splits on '\\' only on Windows: a row's name must not
            // depend on which machine reads the settings file.
            if (string.IsNullOrWhiteSpace(cwd)) return "";
            var trimmed = cwd.TrimEnd('/', '\\');
            return trimmed[(trimmed.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];
        }
    }
}
