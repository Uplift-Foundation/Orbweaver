using System;
using System.Collections.Generic;
using System.IO;
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
            foreach (var stale in absentSince.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                absentSince.Remove(stale);
            }

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

        private static string FolderName(string cwd)
        {
            if (string.IsNullOrWhiteSpace(cwd)) return "";
            return Path.GetFileName(cwd.TrimEnd('/', '\\'));
        }
    }
}
