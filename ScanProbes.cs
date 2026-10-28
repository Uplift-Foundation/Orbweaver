using System;
using System.Collections.Generic;
using System.Linq;

namespace Orbweaver
{
    // Where a `claude agents` viewer for a directory is sitting: its tmux
    // server and pane when it runs inside tmux, and its tty either way.
    internal readonly record struct AgentViewer(string Socket, string Pane, string Tty);

    // Which subprocess questions one scan pass needs answered, decided from the
    // status files alone, before any of them is asked.
    //
    // This exists because the scan's reconciliation half runs on the UI thread,
    // and until this change it asked those questions there: a `tmux
    // display-message` and a whole `ps` for every pane claim, a `ps` and an
    // `lsof` per viewer lookup, `claude agents --json` whenever its cache ran
    // out. Profiled on a machine with about fifteen tmux-hosted sessions and 890
    // processes, the UI thread was about 66% busy and inside one of those calls
    // in six snapshots out of six — so every menu, drag and chat panel waited on
    // `ps`. ScheduleScan's background half now answers these (ScanProbes.Gather)
    // and the UI half reads the answers.
    //
    // **Each gate here has to admit every case the UI half would ask about, or
    // that case silently gets "unknown".** Where a gate can be computed exactly
    // from the files it is, and says so; where the UI half's own condition
    // depends on verdicts only it reaches, the gate is a superset of it, built
    // from the facts those verdicts narrow. Asking a question nobody reads costs
    // one cached call on a background thread. Failing to ask one that is read
    // changes an orb.
    //
    // What a gate must never do is widen into a case the scan deliberately does
    // not pay for — a machine with nothing but ordinary terminal sessions asks
    // the daemon nothing and walks no process table, exactly as before. The
    // scan suites count those calls, so that is pinned rather than hoped.
    internal sealed record ScanProbePlan(
        IReadOnlyList<SessionStatus> PaneClaims,
        bool AskTheDaemon,
        bool AskAttachClients,
        IReadOnlyList<string> ViewerCwds)
    {
        // `found` is the status files as ReadStatusFiles returned them. The
        // entries the UI half adds afterwards — gateway, remote-control and
        // cloud sessions — never have a pid, a tmux pane or the Claude Code
        // source, so none of them could have changed an answer below.
        //
        // leadOf answers from the memberships this pass already gathered (see
        // TeamPids and ScanProbes.TeamOf); a seam so the gates can be tested
        // against invented teams.
        internal static ScanProbePlan For(List<SessionManager.ScanEntry> found, Func<int, string> leadOf)
        {
            // Exact: ReconcileTmuxPaneClaims probes every Claude Code entry with
            // a recorded pane, and runs before anything could give one a pane.
            var paneClaims = found
                .Where(e => e.Status.Source == SessionSource.ClaudeCode
                            && !string.IsNullOrEmpty(e.Status.TmuxPane))
                .Select(e => e.Status)
                .ToList();

            // Exact: this is the scan's own worthAsking, computed from the same
            // two functions over the same files at the same point in the pass.
            var sharingAPid = SessionManager.SharingAPid(found);
            var worthAsking = found.Any(e => SessionPresence.WorthAskingTheDaemon(
                e.Status, SessionManager.KnowsATerminal(e.Status), sharingAPid.Contains(e.SessionId)));

            // Every lead any process names, and whether a Claude Code one does.
            // The UI half narrows this to live, unsuperseded sessions; this does
            // not have those verdicts yet, and does not need them to be a
            // superset.
            var leadsNamed = new HashSet<string>(StringComparer.Ordinal);
            var aClaudeSessionNamesALead = false;
            foreach (var entry in found)
            {
                if (entry.Status.SessionPid <= 0) continue;

                var lead = leadOf(entry.Status.SessionPid);
                if (string.IsNullOrEmpty(lead) || lead == entry.SessionId) continue;

                leadsNamed.Add(lead);
                if (entry.Status.Source == SessionSource.ClaudeCode) aClaudeSessionNamesALead = true;
            }

            // The daemon is read in three places. The phase, gated by
            // worthAsking. Superseded, for an entry that shares its pid with a
            // newer one, which needs two entries sharing one. And a member's
            // presence, which asks whether its lead is a live job, which needs a
            // Claude Code session naming a lead.
            var askTheDaemon = worthAsking || sharingAPid.Count > 0 || aClaudeSessionNamesALead;

            // WantsAgentViewer, widened in the two places the UI half narrows
            // it: a terminal can only be gained between here and there
            // (reconciliation and inheritance fill fields, never clear them),
            // and leadsWithLiveAgents is a subset of leadsNamed.
            var viewerCwds = found
                .Where(e => e.Status.Source == SessionSource.ClaudeCode
                            && !e.Status.IsDesktopHosted
                            && !SessionManager.KnowsATerminal(e.Status)
                            && !string.IsNullOrEmpty(e.Status.Cwd)
                            && (e.Status.SessionPid <= 0 || leadsNamed.Contains(e.SessionId)))
                .Select(e => ScanProbes.ViewerKey(e.Status.Cwd))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // Exact: the attach-client scan is asked for when worthAsking is
            // true, and not otherwise.
            return new ScanProbePlan(paneClaims, askTheDaemon, worthAsking, viewerCwds);
        }

        // Every pid whose team membership this pass could read (CB-212): each
        // entry's own, exactly as `found` has it. Exact rather than a superset,
        // because both places the UI half asks — the live-agent pass and the
        // membership block — read an entry of `found` by its SessionPid, which
        // nothing in the pass reassigns, and the entries the UI half adds
        // itself (gateway, remote-control, cloud) have no pid. The plan's own
        // leadOf above asks about these same pids.
        internal static IReadOnlyList<int> TeamPids(IEnumerable<SessionManager.ScanEntry> found) =>
            found.Select(e => e.Status.SessionPid).Where(pid => pid > 0).Distinct().ToList();

        // The sessions whose team has to be read from files because there is no
        // pid to ask about: Claude Code sessions that recorded none and did
        // record a transcript. Exactly the WSL case (see AgentTeamFiles), and
        // deliberately nothing wider — a session with a pid is answered by its
        // command line, and reading a transcript for it as well would be a
        // second, slower opinion on a question already settled.
        internal static IReadOnlyList<(string SessionId, string TranscriptPath)> FileTeamCandidates(
            IEnumerable<SessionManager.ScanEntry> found) =>
            found.Where(e => e.Status.Source == SessionSource.ClaudeCode
                             && e.Status.SessionPid <= 0
                             && !string.IsNullOrEmpty(e.Status.TranscriptPath))
                .Select(e => (e.SessionId, e.Status.TranscriptPath!))
                .ToList();
    }

    // One pass's subprocess answers, gathered off the UI thread and read on it.
    //
    // Fresher than a cache would be, not staler: each answer is from the same
    // pass that reads it, a few milliseconds earlier. No pane-ownership cache
    // was added on purpose. Keyed by (server, pane, pane pid), a cache would
    // keep answering for a shell in which somebody quit one conversation and
    // started another, since the pane and its shell are unchanged. That is the
    // reuse ReconcileTmuxPaneClaims exists to catch. Once one listing serves
    // every claim, the question costs one `ps` and one `list-panes` per server
    // per pass, on a thread nobody is waiting for.
    internal sealed class ScanProbes
    {
        internal static readonly ScanProbes Nothing = new(
            new Dictionary<TmuxPaneKey, string?>(), null, null,
            new Dictionary<string, AgentViewer?>(StringComparer.Ordinal),
            new Dictionary<int, AgentTeam.Membership>(),
            new Dictionary<string, AgentTeam.Membership>(StringComparer.Ordinal));

        private readonly IReadOnlyDictionary<TmuxPaneKey, string?> _paneOwners;
        private readonly IReadOnlyDictionary<string, AgentViewer?> _viewers;
        private readonly IReadOnlyDictionary<int, AgentTeam.Membership> _teams;
        private readonly IReadOnlyDictionary<string, AgentTeam.Membership> _fileTeams;

        private ScanProbes(
            IReadOnlyDictionary<TmuxPaneKey, string?> paneOwners,
            Dictionary<string, string>? jobs,
            HashSet<string>? attachClients,
            IReadOnlyDictionary<string, AgentViewer?> viewers,
            IReadOnlyDictionary<int, AgentTeam.Membership> teams,
            IReadOnlyDictionary<string, AgentTeam.Membership> fileTeams)
        {
            _paneOwners = paneOwners;
            Jobs = jobs;
            AttachClients = attachClients;
            _viewers = viewers;
            _teams = teams;
            _fileTeams = fileTeams;
        }

        // Which agent team a process belongs to, as this pass's background half
        // read it off the process's command line (CB-212). The UI half asks
        // this and never AgentTeam, because on Windows AgentTeam's read is a WMI
        // query: about 200 ms warm and up to 700 ms for a process's first one,
        // measured on the Windows PC. CB-210 had already made the background
        // half ask first, which kept AgentTeam's one-minute cache warm for the
        // UI half — but only nearly. An entry that aged out between the two
        // halves was re-read on the UI thread, and that was reproduced: 332 ms
        // of WMI on the UI thread in one pass.
        //
        // A pid this pass did not gather is None, "not known to be in a team",
        // the same answer a failed read gives. TeamPids makes that unreachable
        // for any pid the UI half asks about today. If a future change asks
        // about one it did not gather, that orb is drawn without its team for
        // one pass and the next pass, two seconds later, has gathered it — a
        // missing arrow for a tick is the better failure than a frozen UI.
        internal AgentTeam.Membership TeamOf(int pid) =>
            _teams.TryGetValue(pid, out var membership) ? membership : AgentTeam.None;

        internal string LeadOf(int pid) => TeamOf(pid).Lead;

        // The same question for a session that may have no pid to ask about.
        //
        // A WSL session's status file records no pid, because the hook that
        // wrote it is a Windows process and the `claude` is a Linux one (see
        // AgentTeamFiles), so the process read has nothing to be asked. Those
        // sessions are answered from their transcript and team config, which
        // the background half read under the session's id. The process read
        // still wins whenever there is a pid: this is only the answer for the
        // sessions that have none, never a second opinion on the ones that do.
        internal AgentTeam.Membership TeamOf(string sessionId, int pid) =>
            pid > 0 ? TeamOf(pid)
            : _fileTeams.TryGetValue(sessionId, out var membership) ? membership
            : AgentTeam.None;

        internal string LeadOf(string sessionId, int pid) => TeamOf(sessionId, pid).Lead;

        // The daemon's listing, or null when it was not asked or could not be
        // read. The scan already treats those two alike, since both mean it
        // knows nothing about which sessions are jobs.
        internal Dictionary<string, string>? Jobs { get; }

        // Every `claude attach` client, or null for "could not tell", which the
        // scan treats as its own answer (see SessionPresence.HasAttachClient).
        internal HashSet<string>? AttachClients { get; }

        // The session id in a claimed pane's live Claude process, or null when
        // that could not be established. The same contract the per-pane probe
        // always had, and a claim the plan did not include gets the same null.
        internal string? PaneOwner(SessionStatus status) =>
            _paneOwners.TryGetValue(TmuxPaneKey.Of(status), out var owner) ? owner : null;

        // What AgentTeamViewer.TryAdopt did once its process walk returned:
        // point a session that names no terminal at the viewer for its
        // directory. Returns whether anything was learned. TmuxBin is left
        // empty on purpose, because it records where the *hook* found tmux, and
        // this answer did not come from a hook — which is also what lets the
        // click path recognise an adopted pane as a guess (bin='').
        internal bool AdoptViewer(SessionStatus status)
        {
            if (string.IsNullOrEmpty(status.Cwd)) return false;
            if (!_viewers.TryGetValue(ViewerKey(status.Cwd), out var viewer) || viewer is not { } found)
                return false;

            status.TmuxSocket = found.Socket;
            status.TmuxPane = found.Pane;
            status.Tty = found.Tty;
            return true;
        }

        // The directory as AgentTeamViewer caches it.
        internal static string ViewerKey(string cwd) => cwd.TrimEnd('/');

        // Runs the plan. Every argument is a seam, so this is the one place the
        // scan spends a subprocess and it can be exercised without spending one.
        //
        // teams is what the plan was built from, carried for the UI half to
        // read: memberships are gathered before the plan, because its viewer
        // and daemon gates depend on them.
        internal static ScanProbes Gather(
            ScanProbePlan plan,
            IReadOnlyDictionary<int, AgentTeam.Membership> teams,
            Func<IReadOnlyList<SessionStatus>, IReadOnlyDictionary<TmuxPaneKey, string?>> paneOwners,
            Func<Dictionary<string, string>?> jobListing,
            Func<HashSet<string>?> attachClients,
            Func<string, AgentViewer?> viewerFor,
            IReadOnlyDictionary<string, AgentTeam.Membership>? fileTeams = null)
        {
            var owners = plan.PaneClaims.Count > 0
                ? paneOwners(plan.PaneClaims)
                : new Dictionary<TmuxPaneKey, string?>();

            var viewers = new Dictionary<string, AgentViewer?>(StringComparer.Ordinal);
            foreach (var cwd in plan.ViewerCwds) viewers[cwd] = viewerFor(cwd);

            return new ScanProbes(
                owners,
                plan.AskTheDaemon ? jobListing() : null,
                plan.AskAttachClients ? attachClients() : null,
                viewers,
                teams,
                fileTeams ?? new Dictionary<string, AgentTeam.Membership>(StringComparer.Ordinal));
        }
    }
}
