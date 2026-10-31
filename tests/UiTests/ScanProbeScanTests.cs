using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// The scan's subprocess questions, through a real SessionManager: who owns
// each claimed tmux pane, what the daemon lists, who is attached, and which
// `claude agents` window watches a directory.
//
// These were answered on the UI thread, one pane at a time, until profiling a
// real machine found the dispatcher about 66% busy and inside
// TerminalFocuser.TmuxPaneOwner in six snapshots of six. ScheduleScan now
// answers them in its background half, and the reconciliation half on the UI
// thread only reads them. The first case here is the regression check for that
// move. The others check that moving the questions kept the answers in use:
// a stale pane claim is still dropped, a claim nobody could verify still is
// not, and a pid-less lead still adopts its viewer's pane.
//
// Every seam is handed over, so nothing here runs tmux, ps, lsof or `claude`.
[Collection("Settings")]
public class ScanProbeScanTests
{
    private static readonly int LivePid = Environment.ProcessId;

    private sealed class Scratch : IDisposable
    {
        public string Dir { get; } =
            Path.Combine(Path.GetTempPath(), "cb-probes-" + Guid.NewGuid());

        public Scratch() => Directory.CreateDirectory(Dir);

        public void Write(
            string sessionId, int pid, string termProgram = "", string tmuxPane = "",
            string cwd = "/Users/user/project", DateTime? written = null,
            string transcriptPath = "")
        {
            var path = Path.Combine(Dir, sessionId + ".txt");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new SessionStatus
            {
                State = "idle",
                Cli = "",
                Title = "",
                Cwd = cwd,
                SessionPid = pid,
                TermProgram = termProgram,
                Tty = termProgram.Length > 0 ? "/dev/ttys004" : "",
                TmuxPane = tmuxPane,
                TranscriptPath = transcriptPath,
            }));

            if (written is not null) File.SetLastWriteTimeUtc(path, written.Value);
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private static SessionManager Manager(
        Scratch scratch,
        Func<IReadOnlyList<SessionStatus>, IReadOnlyDictionary<TmuxPaneKey, string?>> paneOwners,
        Func<Dictionary<string, string>?>? jobListing = null,
        Func<HashSet<string>?>? attachClients = null,
        Func<string, AgentViewer?>? agentViewer = null,
        Func<IReadOnlyList<int>, IReadOnlyDictionary<int, AgentTeam.Membership>>? teams = null,
        Func<IEnumerable<(string SessionId, string TranscriptPath)>, IReadOnlyDictionary<string, AgentTeam.Membership>>? fileTeams = null,
        bool? onWindows = null)
    {
        OrbweaverSettings.ClaudeCodeEnabled = true;
        OrbweaverSettings.CodexEnabled = true;

        return new SessionManager(
            scratch.Dir,
            jobListing ?? (() => new Dictionary<string, string>(StringComparer.Ordinal)),
            attachClients ?? (() => new HashSet<string>(StringComparer.Ordinal)),
            dependents: _ => SessionDependents.Nothing,
            onWindows: onWindows,
            paneOwners: paneOwners,
            agentViewer: agentViewer ?? (_ => null),
            teams: teams ?? (pids => pids.ToDictionary(p => p, _ => AgentTeam.None)),
            // Never the real files: a scan suite must not read a transcript
            // that happens to exist on the machine running it.
            fileTeams: fileTeams ?? (_ => new Dictionary<string, AgentTeam.Membership>(StringComparer.Ordinal)));
    }

    private static IReadOnlyDictionary<TmuxPaneKey, string?> Owners(
        IReadOnlyList<SessionStatus> claims, string? owner) =>
        claims.ToDictionary(TmuxPaneKey.Of, _ => owner);

    [AvaloniaFact]
    public async Task ScheduleScanAsksEverySubprocessQuestionOffTheUIThread()
    {
        // One file for each question: a tmux-hosted terminal session (pane
        // ownership) and a pid-less session with no terminal, which makes the
        // pass worth asking the daemon and the attach scan about and wants a
        // viewer for its directory.
        using var scratch = new Scratch();
        scratch.Write("in-tmux", LivePid, termProgram: "iTerm.app", tmuxPane: "%1");
        scratch.Write("pidless", 0, cwd: "/work/lead");

        var asked = new List<(string Question, bool OnUIThread)>();
        void Record(string question)
        {
            lock (asked) asked.Add((question, Dispatcher.UIThread.CheckAccess()));
        }

        var manager = Manager(
            scratch,
            paneOwners: claims => { Record("panes"); return Owners(claims, null); },
            jobListing: () => { Record("daemon"); return new Dictionary<string, string>(); },
            attachClients: () => { Record("attached"); return new HashSet<string>(); },
            agentViewer: _ => { Record("viewer"); return null; },
            teams: pids => { Record("teams"); return new Dictionary<int, AgentTeam.Membership>(); });

        Assert.True(Dispatcher.UIThread.CheckAccess());
        await manager.ScheduleScan();

        Assert.Equal(new[] { "attached", "daemon", "panes", "teams", "viewer" },
            asked.Select(a => a.Question).OrderBy(q => q, StringComparer.Ordinal));
        Assert.All(asked, a => Assert.False(a.OnUIThread, a.Question + " was asked on the UI thread"));

        // And the scan it fed still finished on the UI thread.
        Assert.NotNull(manager.StatusFor("in-tmux"));
    }

    [AvaloniaFact]
    public async Task TheDispatcherKeepsRunningWorkWhileTheScanIsWaitingOnASubprocess()
    {
        // What the user felt: a menu, a drag or a chat panel waiting on `ps`.
        // Deterministic rather than timed. The pane question posts a job to the
        // dispatcher from inside itself and does not return until that job has
        // run. With the question on the background thread, the dispatcher is
        // free and the job runs straight away. With it on the UI thread, the job
        // cannot run until the question returns, so the wait times out and the
        // assertion names it. The ten seconds bounds a failure, never a pass.
        //
        // Posted from inside the question on purpose. A job the test posted
        // right after calling ScheduleScan would be queued ahead of the scan's
        // UI-thread continuation, so it would run first even if every question
        // were asked in that continuation, and the test would pass against the
        // very bug it exists for.
        using var scratch = new Scratch();
        scratch.Write("in-tmux", LivePid, termProgram: "iTerm.app", tmuxPane: "%1");

        using var dispatcherRan = new ManualResetEventSlim();
        var ranWhileAsking = false;

        var manager = Manager(scratch, paneOwners: claims =>
        {
            Dispatcher.UIThread.Post(dispatcherRan.Set);
            ranWhileAsking = dispatcherRan.Wait(TimeSpan.FromSeconds(10));
            return Owners(claims, null);
        });

        await manager.ScheduleScan();

        Assert.True(ranWhileAsking, "a dispatcher job waited for the scan's subprocess question");
    }

    [AvaloniaFact]
    public void AStalePaneClaimIsDroppedWhenItsPanesVerifiedOwnerIsInTheSameScan()
    {
        // "old" claims %9, and the pane's live process names "new". Both files
        // carry this process's pid, which is the only pid a test can be sure is
        // alive, and "old" is the newer file, so without reconciliation the
        // superseded rule keeps "old" and drops "new". With it, "old" goes before
        // that rule runs, and "new" inherits the pane it is actually in.
        using var scratch = new Scratch();
        scratch.Write("old", LivePid, termProgram: "iTerm.app", tmuxPane: "%9");
        scratch.Write("new", LivePid, written: DateTime.UtcNow.AddMinutes(-1));

        var manager = Manager(scratch, paneOwners: claims => Owners(claims, "new"));
        manager.ScanAndUpdate();

        Assert.Null(manager.StatusFor("old"));
        Assert.Equal("%9", manager.StatusFor("new")!.TmuxPane);
    }

    [AvaloniaFact]
    public void APaneClaimNobodyCouldVerifyChangesNothing()
    {
        // The paired control for the case above, with the same files and an
        // owner of "unknown". Nothing is removed on an absent answer, so the
        // superseded rule decides exactly as it would have with no probe at all.
        using var scratch = new Scratch();
        scratch.Write("old", LivePid, termProgram: "iTerm.app", tmuxPane: "%9");
        scratch.Write("new", LivePid, written: DateTime.UtcNow.AddMinutes(-1));

        var manager = Manager(scratch, paneOwners: claims => Owners(claims, null));
        manager.ScanAndUpdate();

        Assert.Equal("%9", manager.StatusFor("old")!.TmuxPane);
        Assert.Null(manager.StatusFor("new"));
    }

    [AvaloniaFact]
    public void APidlessSessionAdoptsTheViewerForItsDirectory()
    {
        // Adoption used to be AgentTeamViewer.TryAdopt, walking ps and lsof on
        // the UI thread. The walk now happens in the background half, and this
        // is the UI half applying its answer: the session gets the viewer's
        // pane and tty, and no TmuxBin, which is how the click path knows the
        // pane is a guess.
        //
        // Listed by the daemon as a live job, since a pid-less Claude Code
        // session the daemon rules out is dropped as NotALiveJob whatever it
        // adopts.
        using var scratch = new Scratch();
        scratch.Write("pidless", 0, cwd: "/work/lead/");

        var viewersAsked = new List<string>();
        var manager = Manager(
            scratch,
            paneOwners: claims => Owners(claims, null),
            jobListing: () => new Dictionary<string, string> { ["pidless"] = "working" },
            agentViewer: cwd =>
            {
                viewersAsked.Add(cwd);
                return new AgentViewer("/tmp/viewer", "%4", "ttys009");
            });
        manager.ScanAndUpdate();

        Assert.Equal(new[] { "/work/lead" }, viewersAsked);
        var status = manager.StatusFor("pidless");
        Assert.NotNull(status);
        Assert.Equal("%4", status!.TmuxPane);
        Assert.Equal("/tmp/viewer", status.TmuxSocket);
        Assert.Equal("ttys009", status.Tty);
        Assert.Equal("", status.TmuxBin);
    }

    // --- agent-team membership (CB-212) ----------------------------------------
    //
    // AgentTeam reads a process's command line, which on Windows is a WMI query
    // measured at about 200 ms warm and up to 700 ms cold. CB-210 had the
    // background half ask it first, so the UI half's own AgentTeam.Of was
    // nearly always a cache hit — nearly, because an entry that aged out between
    // the two halves was re-read on the UI thread. These pin that the UI half
    // now reads only what the background half gathered.

    private static readonly AgentTeam.Membership Member = new("lead-id", "blue", "MenuUX");

    [AvaloniaFact]
    public async Task TheUIHalfDrawsTheTeamTheBackgroundHalfRead()
    {
        // The membership handed over is one this test process cannot carry on
        // its own command line, so it reaching the status proves where it came
        // from. Negative control: put AgentTeam.Of back in the membership block
        // and this reads "" — this process is in no team.
        using var scratch = new Scratch();
        scratch.Write("member", LivePid, termProgram: "iTerm.app");

        var asked = new List<(IReadOnlyList<int> Pids, bool OnUIThread)>();
        var manager = Manager(
            scratch,
            paneOwners: claims => Owners(claims, null),
            teams: pids =>
            {
                lock (asked) asked.Add((pids, Dispatcher.UIThread.CheckAccess()));
                return pids.ToDictionary(p => p, _ => Member);
            });

        await manager.ScheduleScan();

        var (pids, onUIThread) = Assert.Single(asked);
        Assert.Equal(new[] { LivePid }, pids);
        Assert.False(onUIThread, "team membership was read on the UI thread");

        var status = manager.StatusFor("member");
        Assert.NotNull(status);
        Assert.Equal("lead-id", status!.Lead);
        Assert.Equal("MenuUX", status.Agent);
    }

    // --- a team read from files, for a session with no pid (WSL) ---------------------
    //
    // A Claude Code in WSL records session_pid 0, so there is no command line to
    // read its team off, and its orb drew alone with no arrow to its lead. The
    // background half now reads such a session's transcript and team config
    // instead (AgentTeamFiles), and the UI half applies the answer exactly as it
    // applies a process's.

    private const string WslTranscript = "/home/user/.claude/projects/-home-user/5e6f7a8b.jsonl";

    private static IReadOnlyDictionary<string, AgentTeam.Membership> OnlyWslMember(
        IEnumerable<(string SessionId, string TranscriptPath)> asked) =>
        asked.Where(a => a.SessionId == "wsl-member")
            .ToDictionary(a => a.SessionId, _ => Member, StringComparer.Ordinal);

    // `onWindows: true` throughout, because that is what makes a pid-less
    // session with a Linux path a WSL one to the scan (see
    // SessionPresence.LocalDaemonCanAnswerFor): anywhere else the local daemon's
    // empty job listing rules a pid-less session out as "not a job", and the
    // session is dropped before there is any team to draw.

    [AvaloniaFact]
    public async Task APidlessSessionIsDrawnInItsTeamFromItsFiles()
    {
        // Negative control just below: with no file answer the same session
        // reads "" for its lead, which is how every WSL teammate drew before.
        using var scratch = new Scratch();
        scratch.Write("wsl-member", 0, termProgram: "iTerm.app", transcriptPath: WslTranscript);
        scratch.Write("native", LivePid, termProgram: "iTerm.app");

        var asked = new List<(IReadOnlyList<(string Id, string Path)> Sessions, bool OnUIThread)>();
        var manager = Manager(
            scratch,
            paneOwners: claims => Owners(claims, null),
            onWindows: true,
            fileTeams: sessions =>
            {
                var list = sessions.Select(s => (Id: s.SessionId, Path: s.TranscriptPath)).ToList();
                lock (asked) asked.Add((list, Dispatcher.UIThread.CheckAccess()));
                return OnlyWslMember(sessions);
            });

        await manager.ScheduleScan();

        var (sessions, onUIThread) = Assert.Single(asked);
        Assert.Equal(new[] { ("wsl-member", WslTranscript) }, sessions);   // not the session that has a pid
        Assert.False(onUIThread, "the team files were read on the UI thread");

        var status = manager.StatusFor("wsl-member");
        Assert.NotNull(status);
        Assert.Equal("lead-id", status!.Lead);
        Assert.Equal("MenuUX", status.Agent);
        Assert.Equal("blue", status.Color);   // the colour the team config gave it

        Assert.Equal("", manager.StatusFor("native")!.Lead);   // untouched: it has a pid and no team
    }

    [AvaloniaFact]
    public async Task APidlessSessionWithNoFileAnswerHasNoTeam()
    {
        using var scratch = new Scratch();
        scratch.Write("wsl-member", 0, termProgram: "iTerm.app", transcriptPath: WslTranscript);

        var manager = Manager(scratch, paneOwners: claims => Owners(claims, null), onWindows: true);

        await manager.ScheduleScan();

        var status = manager.StatusFor("wsl-member");
        Assert.NotNull(status);
        Assert.Equal("", status!.Lead);
        Assert.Equal("", status.Agent);
    }

    [AvaloniaFact]
    public void AWslMemberIsPresentWhileItsLeadIsOnTheScanAndParkedOnceItIsNot()
    {
        // Giving a WSL member a lead brings it under the orphan rule that every
        // other member is already under: a teammate whose lead is on the scan is
        // part of a live team, and one whose lead is gone is answering to
        // nothing. Both halves, since a change that made the first false would
        // dim every WSL team.
        using var withLead = new Scratch();
        withLead.Write("lead-id", 0, termProgram: "iTerm.app", cwd: "/home/user/lead");
        withLead.Write("wsl-member", 0, termProgram: "iTerm.app", transcriptPath: WslTranscript);

        var live = Manager(withLead, paneOwners: claims => Owners(claims, null), onWindows: true, fileTeams: OnlyWslMember);
        live.ScanAndUpdate();
        Assert.Equal(OrbPresence.Present, live.StatusFor("wsl-member")!.Presence);

        using var leadGone = new Scratch();
        leadGone.Write("wsl-member", 0, termProgram: "iTerm.app", transcriptPath: WslTranscript);

        var orphaned = Manager(leadGone, paneOwners: claims => Owners(claims, null), onWindows: true, fileTeams: OnlyWslMember);
        orphaned.ScanAndUpdate();
        Assert.Equal(OrbPresence.Parked, orphaned.StatusFor("wsl-member")!.Presence);
    }

    [AvaloniaFact]
    public void ALeadIsKeptForALiveAgentTheBackgroundHalfFound()
    {
        // The other place the UI half asks: a lead naming no terminal is kept
        // because a live member names it. A Codex entry and a Claude Code one
        // share this process's pid without superseding each other, the trick
        // SessionScanTests uses, so the one answer serves both: the member's
        // "my lead is lead-id", and the lead's "that is me". Negative control:
        // put AgentTeam.LeadOf back in the live-agent pass and the lead is
        // dropped for having no terminal.
        using var scratch = new Scratch();
        File.WriteAllText(Path.Combine(scratch.Dir, "codex-member.txt"),
            System.Text.Json.JsonSerializer.Serialize(new SessionStatus
            {
                State = "idle", Cli = "codex", SessionPid = LivePid,
                TermProgram = "iTerm.app", Tty = "/dev/ttys004", Cwd = "/Users/user/project",
            }));
        File.WriteAllText(Path.Combine(scratch.Dir, "lead-id.txt"),
            System.Text.Json.JsonSerializer.Serialize(new SessionStatus
            {
                State = "idle", SessionPid = LivePid,
            }));

        var manager = Manager(
            scratch,
            paneOwners: claims => Owners(claims, null),
            teams: pids => pids.ToDictionary(p => p, _ => Member));
        manager.ScanAndUpdate();

        Assert.NotNull(manager.StatusFor("codex-member"));
        Assert.NotNull(manager.StatusFor("lead-id"));
    }

    [AvaloniaFact]
    public async Task AnAnswerThatAgesOutBetweenTheTwoHalvesIsNotReadAgainOnTheUIThread()
    {
        // The race itself, through the real AgentTeam and its real cache. An
        // invented membership is seeded for this process's pid, fresh, so the
        // background half answers from it; then, still on the background thread
        // and after the team read, the pane question ages that entry past the
        // minute. Before CB-212 the UI half asked AgentTeam.Of again, found the
        // entry expired, and re-read this process for real on the UI thread —
        // reproduced on the Windows PC as one 332 ms WMI query, and here it
        // would read "", since this process is in no team. Now the UI half reads
        // what the background half gathered, so the seeded lead is what it sees.
        //
        // Deterministic: the ageing is done by the scan's own background step,
        // not by a clock, so there is nothing to race.
        var cache = (Dictionary<int, (AgentTeam.Membership Value, long Stamp)>)typeof(AgentTeam)
            .GetField("Cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;

        lock (cache) cache.Clear();
        try
        {
            lock (cache) cache[LivePid] = (Member, Environment.TickCount64);

            using var scratch = new Scratch();
            scratch.Write("member", LivePid, termProgram: "iTerm.app", tmuxPane: "%1");

            var aged = false;
            OrbweaverSettings.ClaudeCodeEnabled = true;
            var manager = new SessionManager(
                scratch.Dir,
                () => new Dictionary<string, string>(StringComparer.Ordinal),
                () => new HashSet<string>(StringComparer.Ordinal),
                dependents: _ => SessionDependents.Nothing,
                paneOwners: claims =>
                {
                    lock (cache) cache[LivePid] = (Member, Environment.TickCount64 - 61_000);
                    aged = true;
                    return Owners(claims, null);
                },
                agentViewer: _ => null);

            await manager.ScheduleScan();

            Assert.True(aged, "the pane question never ran, so nothing was aged");
            Assert.Equal("lead-id", manager.StatusFor("member")!.Lead);
        }
        finally
        {
            lock (cache) cache.Clear();
        }
    }

    [AvaloniaFact]
    public void AMachineOfTerminalSessionsSpendsOnlyThePaneQuestion()
    {
        // The daemon, the attach scan and the viewer hunt are each counted,
        // because the point is that they do not happen. A pass with nothing
        // background-ish on it paid for none of them before this change either.
        using var scratch = new Scratch();
        scratch.Write("in-tmux", LivePid, termProgram: "iTerm.app", tmuxPane: "%1");

        var other = 0;
        var panes = 0;
        var manager = Manager(
            scratch,
            paneOwners: claims => { panes++; return Owners(claims, null); },
            jobListing: () => { other++; return null; },
            attachClients: () => { other++; return null; },
            agentViewer: _ => { other++; return null; });
        manager.ScanAndUpdate();

        Assert.Equal(1, panes);
        Assert.Equal(0, other);
    }
}
