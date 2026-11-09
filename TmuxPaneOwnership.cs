using System;
using System.Collections.Generic;
using System.Linq;

namespace Orbweaver
{
    // The answer to the one question a pane claim cannot answer: is the Claude
    // process displayed in this pane still running this *session*? A pane id is
    // a location and tmux deliberately reuses it; only the child's exact
    // --session-id is authority for a conversation.
    internal enum TmuxPaneOwnership
    {
        Match,
        Mismatch,
        Unknown
    }

    internal sealed record ProcessCommand(int Pid, int ParentPid, string Arguments);

    // What a pane claim is probed by: the tmux the hook recorded, the server it
    // recorded, and the pane. Taken raw off the status file rather than after
    // the binary is resolved, so the scan can look an answer up with nothing
    // but the status in hand — which is the whole point of gathering answers
    // on one thread and reading them on another.
    internal readonly record struct TmuxPaneKey(string Bin, string Socket, string Pane)
    {
        internal static TmuxPaneKey Of(SessionStatus status) =>
            new(status.TmuxBin ?? "", status.TmuxSocket ?? "", status.TmuxPane ?? "");
    }

    internal static class TmuxPaneOwnershipRules
    {
        // `ps -eo pid=,ppid=,args=`, one row per process. A row that does not
        // parse is skipped rather than failing the listing: the table is read
        // while it changes, and one torn row says nothing about the others.
        internal static List<ProcessCommand> ParseProcessListing(string? listing)
        {
            var processes = new List<ProcessCommand>();
            if (string.IsNullOrEmpty(listing)) return processes;

            foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3
                    || !int.TryParse(parts[0], out var pid)
                    || !int.TryParse(parts[1], out var parentPid)) continue;
                processes.Add(new ProcessCommand(pid, parentPid, parts[2]));
            }

            return processes;
        }

        // The format ListPanesFormat asks tmux for.
        internal const string ListPanesFormat = "#{pane_id} #{pane_pid}";

        // `list-panes -a -F ListPanesFormat`: every pane on one server, keyed
        // by id. One call per server where the scan used to make one
        // `display-message` per pane, and the answer is the same answer — a
        // pane that has gone is simply absent, exactly as display-message
        // against it would have failed.
        internal static Dictionary<string, int> ParsePanePids(string? listing)
        {
            var panes = new Dictionary<string, int>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(listing)) return panes;

            foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !parts[0].StartsWith('%')
                    || !int.TryParse(parts[1], out var pid) || pid <= 0) continue;
                panes[parts[0]] = pid;
            }

            return panes;
        }

        // Every pane claim in one pass, answered with one process listing and
        // one pane listing per tmux server, where the scan used to spend a
        // `display-message` *and* a whole `ps` on each claim. On the machine
        // this was measured on that was about fifteen claims and 890 processes
        // every two seconds, on the UI thread.
        //
        // Each answer means what TerminalFocuser.TmuxPaneOwner's always did —
        // the one session id in the pane's live Claude process, or null for
        // anything the probe could not establish — because it is decided by the
        // same SessionIdIn over the same kind of listing. Only the plumbing is
        // shared.
        //
        // panesOf lists one server's panes, or returns null when it could not
        // (no tmux, no server). panePidOf answers a single target that is not a
        // pane id, which list-panes cannot key on; the hook only ever records
        // $TMUX_PANE, which always is one, so that arm is for a status file
        // somebody wrote by hand and exists so that it keeps the answer it
        // always got. processes is asked at most once, and only once some pane
        // has resolved to a pid, so a machine whose recorded panes are all gone
        // does not pay for the process table at all.
        internal static Dictionary<TmuxPaneKey, string?> OwnersFor(
            IEnumerable<SessionStatus> claims,
            Func<string, string, IReadOnlyDictionary<string, int>?> panesOf,
            Func<string, string, string, int?> panePidOf,
            Func<IReadOnlyList<ProcessCommand>?> processes)
        {
            var panePids = new Dictionary<TmuxPaneKey, int>();
            var servers = new Dictionary<(string Bin, string Socket), IReadOnlyDictionary<string, int>?>();

            foreach (var key in claims.Select(TmuxPaneKey.Of).Distinct())
            {
                if (key.Pane.Length == 0) continue;

                int? pid;
                if (key.Pane.StartsWith('%'))
                {
                    if (!servers.TryGetValue((key.Bin, key.Socket), out var panes))
                    {
                        panes = panesOf(key.Bin, key.Socket);
                        servers[(key.Bin, key.Socket)] = panes;
                    }

                    pid = panes is not null && panes.TryGetValue(key.Pane, out var found) ? found : null;
                }
                else
                {
                    pid = panePidOf(key.Bin, key.Socket, key.Pane);
                }

                if (pid is > 0) panePids[key] = pid.Value;
            }

            var owners = new Dictionary<TmuxPaneKey, string?>();
            if (panePids.Count == 0) return owners;

            var table = processes();
            foreach (var (key, pid) in panePids)
            {
                owners[key] = SessionIdIn(table, pid);
            }

            return owners;
        }

        internal static string? SessionIdIn(IEnumerable<ProcessCommand>? processes, int panePid)
        {
            if (panePid <= 0 || processes is null) return null;

            var all = processes.ToList();
            var descendants = new HashSet<int> { panePid };
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var process in all)
                {
                    if (descendants.Contains(process.ParentPid) && descendants.Add(process.Pid))
                        changed = true;
                }
            }

            var ids = all.Where(process => descendants.Contains(process.Pid))
                .Select(process => SessionIdFrom(process.Arguments))
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return ids.Count == 1 ? ids[0] : null;
        }

        // CB-316: whether a pane's process tree holds this pid — the CLI-neutral
        // form of SessionIdIn, for a CLI (Codex) whose argv names no session.
        // Null when there is nothing to judge, so a caller can fail closed.
        internal static bool? Holds(IEnumerable<ProcessCommand>? processes, int panePid, int pid)
        {
            if (panePid <= 0 || pid <= 0 || processes is null) return null;
            if (panePid == pid) return true;
            var all = processes.ToList();
            var descendants = new HashSet<int> { panePid };
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var process in all)
                    if (descendants.Contains(process.ParentPid) && descendants.Add(process.Pid)) changed = true;
            }
            return descendants.Contains(pid);
        }

        internal static TmuxPaneOwnership For(
            string? expectedSessionId, int panePid, IEnumerable<ProcessCommand>? processes)
        {
            if (string.IsNullOrEmpty(expectedSessionId) || panePid <= 0 || processes is null)
                return TmuxPaneOwnership.Unknown;

            var owner = SessionIdIn(processes, panePid);
            if (owner is null) return TmuxPaneOwnership.Unknown;
            return string.Equals(owner, expectedSessionId, StringComparison.OrdinalIgnoreCase)
                ? TmuxPaneOwnership.Match
                : TmuxPaneOwnership.Mismatch;
        }

        // Whether an *action* -- focusing a pane, typing a keystroke into it --
        // may proceed against a pane whose live owner resolved to resolvedOwner.
        // This is the caller's half of the answer above: For and SessionIdIn
        // report the truth about what the probe could establish, including when
        // that truth is "nothing" -- it is deliberately not their job to collapse
        // Unknown into a refusal just because a refusal looks like the safe
        // default.
        //
        // resolvedOwner is null or empty when tmux or ps failed, or -- the
        // ordinary case -- when the pane's claude process carries no
        // --session-id in its argv at all, which is true of a plain interactive
        // session and of an agent-team member alike (its argv names
        // --agent-id/--team-name, never --session-id). Refusing on that would
        // mean every click and every dictation into an ordinary session's pane
        // does nothing, silently, forever: "I cannot prove this pane is yours"
        // and "I have proven this pane belongs to someone else" would read as
        // the same failure, and they are not the same risk. An indeterminate
        // probe has found no evidence the pane changed hands, so there is
        // nothing here to refuse on. A resolvedOwner that positively names a
        // *different* session -- tmux having reused the pane id for a new
        // conversation after the old one exited -- is a real answer, and that
        // one still fails closed; see commit 7e9fd51a for why this half must
        // never soften. An empty expectedSessionId permits for the same reason:
        // with nothing to compare against, there is no expectation left to
        // violate.
        internal static bool PermitsAction(string? expectedSessionId, string? resolvedOwner)
        {
            if (string.IsNullOrEmpty(expectedSessionId) || string.IsNullOrEmpty(resolvedOwner)) return true;
            return string.Equals(resolvedOwner, expectedSessionId, StringComparison.OrdinalIgnoreCase);
        }

        // Claude's argv uses two words, and deliberately not a substring
        // search: a prompt, path, or child tool merely mentioning a UUID must
        // never become authority for a pane.
        internal static string? SessionIdFrom(string? arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments)) return null;
            var words = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || !SessionPresence.LooksLikeClaudeBinary(words[0])) return null;

            for (var i = 0; i + 1 < words.Length; i++)
            {
                if (words[i] == "--session-id") return words[i + 1];
            }

            return null;
        }
    }
}
