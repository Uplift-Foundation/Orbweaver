using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// A real scan, over real status files, producing real orb windows.
//
// This is the seam the rest of SessionManager's tests stop at: Superseded,
// JudgeLiveness and the rest are pure and covered in tests/UnitTests, but
// nothing there proves the scan wires them to a window — that a file appearing
// puts an orb on screen, that deleting it takes the orb away with its status
// and its place in the stacking order, or that expiring one does the same. Those
// are the failures a user actually reports ("I seem to have lost all my orbs"),
// and every one of them lives in the plumbing rather than in the rules.
//
// Two things make it runnable without touching the machine it runs on.
//
// The status directory is handed over rather than inherited: SessionManager's
// internal constructor takes one, so these files land in a scratch directory of
// this test's own and a real session's orb is never disturbed. Start() is
// deliberately never called — it would create a TrayController, subscribe to the
// speech engine and start a two-second timer, none of which this is about, and
// with no tray attached UpdateTray becomes a no-op on a null.
//
// And every fake session is shaped so that nothing here shells out. Both
// BackgroundJobs.IsLiveJob (`claude agents --json`) and AgentTeamViewer.TryAdopt
// are reached only by a session with no terminal or no pid, so every file below
// names both — a term_program and this test process's own pid, which is the one
// pid on the machine that is certainly alive. Two files never share a pid *and*
// a CLI either, which is what would put Superseded's job-list lookup in play.
// The rules those calls sit behind are covered in ScanVerdictTests, where they
// are passed in rather than performed.
[Collection("Settings")]
public class SessionScanTests
{
    // Alive for certain, and asking the kernel about it is a kill(pid, 0) —
    // nothing is signalled and nothing is spawned. An invented pid would be a
    // coin toss on whether some unrelated process happened to hold it.
    private static readonly int LivePid = Environment.ProcessId;

    private sealed class Scratch : IDisposable
    {
        public string Dir { get; } =
            Path.Combine(Path.GetTempPath(), "cb-scan-" + Guid.NewGuid());

        public Scratch() => Directory.CreateDirectory(Dir);

        // A status file the way the hooks write one: JSON, named for the
        // session id, with the app-derived fields (Lead, Agent, Source) absent
        // because they are [JsonIgnore] and the hook has never heard of them.
        // tty and transcriptPath default to what an interactive session has —
        // a terminal, and no transcript recorded — so every case written before
        // the NothingToShow rule existed is untouched by it.
        public void Write(
            string sessionId, string state = "idle", string cli = "",
            string title = "", string cwd = "/Users/user/project",
            int? pid = null, string termProgram = "iTerm.app",
            DateTime? written = null, string tty = "/dev/ttys004",
            string tmuxPane = "", string transcriptPath = "",
            string entrypoint = "")
        {
            var path = Path.Combine(Dir, sessionId + ".txt");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new SessionStatus
            {
                State = state,
                Cli = cli,
                Title = title,
                Cwd = cwd,
                SessionPid = pid ?? LivePid,
                TermProgram = termProgram,
                Tty = tty,
                TmuxPane = tmuxPane,
                TranscriptPath = transcriptPath,
                Entrypoint = entrypoint,
            }));

            if (written is not null) File.SetLastWriteTimeUtc(path, written.Value);
        }

        // A transcript on disk, so the rule under test is answered by a real
        // File.Exists rather than by a predicate a test handed in. `rows` adds
        // identity records for the sessions that are about to be asked what
        // they are called.
        public string WriteTranscript(string sessionId, params string[] rows)
        {
            var path = Path.Combine(Dir, sessionId + ".jsonl");
            var lines = new List<string> { "{\"type\":\"user\",\"message\":{\"content\":\"hi\"}}" };
            lines.AddRange(rows);
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
            return path;
        }

        public string MissingTranscript(string sessionId) =>
            Path.Combine(Dir, sessionId + "-never-written.jsonl");

        public void Delete(string sessionId) =>
            File.Delete(Path.Combine(Dir, sessionId + ".txt"));

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private static SessionManager Scan(Scratch scratch, bool enableBothClis = true)
    {
        // Both CLIs on, said out loud rather than inherited.
        //
        // ScanAndUpdate filters every session through ClaudeCodeEnabled or
        // CodexEnabled depending on its source (SessionManager.cs ~line 378), so a
        // scan test that does not state this is asserting about whatever the last
        // test to touch those settings left behind. That is not hypothetical: it
        // is what turned this suite red on both CI legs. SettingsWindowRowTests'
        // TheTwoClisDoNotShareTheirSwitches deliberately switches Codex off and
        // used to leave it off, and in Release — where the class order differs
        // from Debug — it ran first. Every scan here then found nothing, with no
        // exception and no hint as to why: StatusFor returned null and the orb
        // collections came back empty.
        //
        // That leak is fixed at its source too. This is the other half, and the
        // half that keeps working: a test that depends on a setting sets it.
        // enableBothClis: false for the one test that switches a CLI off ON
        // PURPOSE, to prove such a session is filtered out — it has to be able to
        // opt out of the guarantee the rest rely on.
        if (enableBothClis)
        {
            OrbweaverSettings.ClaudeCodeEnabled = true;
            OrbweaverSettings.CodexEnabled = true;
        }

        var manager = new SessionManager(scratch.Dir);
        manager.ScanAndUpdate();
        return manager;
    }

    // _windows is what the scan actually maintains; StatusFor only proves the
    // status survived. Read rather than widened, the same reasoning
    // TrayRemoteItemTests records for reaching TrayController's private menu.
    private static IReadOnlyCollection<string> OrbIds(SessionManager manager)
    {
        var field = typeof(SessionManager).GetField(
            "_windows", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);

        var windows = (System.Collections.IDictionary)field!.GetValue(manager)!;
        return windows.Keys.Cast<string>().ToList();
    }

    // The stacking order, which is not the same list as the orbs: teams are
    // gathered and hand-placed orbs keep their spot.
    private static List<string> DisplayOrder(SessionManager manager)
    {
        var method = typeof(SessionManager).GetMethod(
            "DisplayOrder", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        return (List<string>)method!.Invoke(manager, null)!;
    }

    [AvaloniaFact]
    public void AStatusFileBecomesAnOrbCarryingWhatTheFileSaid()
    {
        using var scratch = new Scratch();
        scratch.Write("session-a", state: "waiting", title: "Windows and Mac parity");

        var manager = Scan(scratch);

        Assert.Equal(new[] { "session-a" }, OrbIds(manager));

        var status = manager.StatusFor("session-a");
        Assert.NotNull(status);
        Assert.Equal("waiting", status!.State);
        Assert.Equal("Windows and Mac parity", status.Title);
        Assert.Equal(SessionSource.ClaudeCode, status.Source);
    }

    [AvaloniaFact]
    public void ACodexFileIsResolvedToCodexRatherThanArrivingAsTheEnumsDefault()
    {
        // Source is [JsonIgnore] and so arrives as ClaudeCode however the file
        // is written; SourceOf exists because missing that resolution does not
        // fail loudly — it produces a Codex session that claims to be a Claude
        // Code one, which is long enough to send a click to the wrong place.
        using var scratch = new Scratch();
        scratch.Write("codex-1", cli: "codex");

        var manager = Scan(scratch);

        Assert.Equal(SessionSource.Codex, manager.StatusFor("codex-1")!.Source);
    }

    // The row Claude Code appends when a turn is backgrounded, verbatim off the
    // machine the duplicate "Ub" orbs were photographed on (identifiers
    // scrubbed) — the rule it drives is covered in TranscriptHandoffTests, and
    // what this file adds is the plumbing: that a scan over a real status file
    // naming a real transcript reads it, hides the husk, and lets it back the
    // moment the conversation moves again.
    private const string BackgroundingMarker =
        """{"parentUuid":"1b5cf160-79bf-4e2b-a01f-6511aee6b36b","isSidechain":false,"type":"system","subtype":"informational","content":"Backgrounding after the current tool finishes…","isMeta":false,"timestamp":"2026-08-28T17:53:15.295Z","uuid":"4f19d42a-80a5-4f9e-afe6-f234587acbf5","level":"warning","userType":"external","entrypoint":"cli","cwd":"/Users/w/project","sessionId":"6d3a9d57-10c6-4e9d-bf25-38194fae23c0","version":"2.1.251","gitBranch":"develop"}""";

    [AvaloniaFact]
    public void ABackgroundedTurnsHuskLosesItsOrbAndGetsItBackIfTheSessionMovesAgain()
    {
        // The duplicate-orb shape, end to end. The husk is healthy by every
        // rule that existed before TranscriptHandoff — live pid, a terminal,
        // "generating" — and the only thing wrong with it is that its own
        // transcript ends with the handoff. The control alongside it is a
        // Codex file for the reason this class's own header gives: a second
        // ClaudeCode file on this test's pid would read as an Agent View pid
        // and send the scan to the real daemon.
        using var scratch = new Scratch();

        var huskMarker = BackgroundingMarker.Replace(
            "6d3a9d57-10c6-4e9d-bf25-38194fae23c0", "husk", StringComparison.Ordinal);
        var huskTail = scratch.WriteTranscript("husk", huskMarker);
        scratch.Write("husk", state: "generating", title: "Unmerged branches and PRs",
            transcriptPath: huskTail);

        scratch.Write("control", cli: "codex", state: "generating");

        var manager = Scan(scratch);

        Assert.Equal(new[] { "control" }, OrbIds(manager));

        // The self-correcting direction: a user row appended after the marker
        // means somebody is talking here again, and the next scan must bring
        // the orb back — the transcript grew, so the cached answer is stale by
        // its own key. The revived husk is also the negative case: a
        // ClaudeCode session whose tail is ordinary keeps its orb.
        File.AppendAllText(huskTail,
            "{\"type\":\"user\",\"message\":{\"content\":\"back here\"}}\n");
        manager.ScanAndUpdate();

        Assert.Equal(2, OrbIds(manager).Count);
        Assert.NotNull(manager.StatusFor("husk"));
    }

    [AvaloniaFact]
    public void AnInheritedBackgroundMarkerDoesNotHideAForkBeforeItsFirstReply()
    {
        // BackgroundJobs has not listed the new job yet, so the phase remains
        // Unknown. The copied marker belongs to the parent and must not make
        // the fork disappear in that first scan window.
        using var scratch = new Scratch();
        var forkTail = scratch.WriteTranscript("fork", BackgroundingMarker);
        scratch.Write("fork", state: "generating", title: "Unmerged branches and PRs",
            transcriptPath: forkTail);

        var manager = Scan(scratch);

        Assert.Contains("fork", OrbIds(manager));
    }

    [AvaloniaFact]
    public void DeletingAStatusFileTakesTheOrbTheStatusAndThePlaceInTheStack()
    {
        // SessionEnd's graceful path. All three have to go together: an orb left
        // in _windows is a window nobody closes, a status left behind answers
        // StatusFor for a session that has ended, and an id left in _order
        // reserves a slot in the stack that nothing occupies.
        using var scratch = new Scratch();
        scratch.Write("stays");
        scratch.Write("goes", cli: "codex");

        var manager = Scan(scratch);
        Assert.Equal(2, OrbIds(manager).Count);

        scratch.Delete("goes");
        manager.ScanAndUpdate();

        Assert.Equal(new[] { "stays" }, OrbIds(manager));
        Assert.Null(manager.StatusFor("goes"));
        Assert.Equal(new[] { "stays" }, DisplayOrder(manager));
    }

    [AvaloniaFact]
    public void AQuietSessionLosesItsOrbOnceTheLifetimeHasPassed()
    {
        // The lifetime timer, end to end: an mtime older than the setting is
        // the only thing wrong with this file, and it is enough.
        var before = OrbweaverSettings.OrbLifetimeMinutes;
        try
        {
            OrbweaverSettings.OrbLifetimeMinutes = 5;

            using var scratch = new Scratch();
            scratch.Write("fresh");
            scratch.Write("ancient", cli: "codex",
                written: DateTime.UtcNow - TimeSpan.FromHours(2));

            var manager = Scan(scratch);

            Assert.Equal(new[] { "fresh" }, OrbIds(manager));

            // And "forever" brings it straight back, without the file changing.
            OrbweaverSettings.OrbLifetimeMinutes = OrbweaverSettings.OrbLifetimeForever;
            manager.ScanAndUpdate();

            Assert.Contains("ancient", OrbIds(manager));
        }
        finally
        {
            OrbweaverSettings.OrbLifetimeMinutes = before;
        }
    }

    [AvaloniaFact]
    public void ACliSwitchedOffIsIgnoredWithoutItsFileBeingTouched()
    {
        // "A CLI switched off is ignored, not unwired." The hook keeps writing —
        // it is the user's own config, and for Codex rewriting it would cost
        // them their hook trust — so the file must still be there afterwards.
        var before = OrbweaverSettings.CodexEnabled;
        try
        {
            using var scratch = new Scratch();
            scratch.Write("claude-1");
            scratch.Write("codex-1", cli: "codex");

            OrbweaverSettings.CodexEnabled = false;
            var manager = Scan(scratch, enableBothClis: false);

            Assert.Equal(new[] { "claude-1" }, OrbIds(manager));
            Assert.True(File.Exists(Path.Combine(scratch.Dir, "codex-1.txt")));

            OrbweaverSettings.CodexEnabled = true;
            manager.ScanAndUpdate();

            Assert.Contains("codex-1", OrbIds(manager));
        }
        finally
        {
            OrbweaverSettings.CodexEnabled = before;
        }
    }

    [AvaloniaFact]
    public void AMalformedOrEmptyFileIsSkippedRatherThanTakingTheScanDown()
    {
        // A file caught mid-write is the ordinary case here, not a corruption:
        // the hook truncates and rewrites, and the scan runs every two seconds.
        // "retry next tick" only works if one bad file cannot stop the others
        // being read.
        using var scratch = new Scratch();
        scratch.Write("good");
        File.WriteAllText(Path.Combine(scratch.Dir, "half-written.txt"), "{\"state\": \"gen");
        File.WriteAllText(Path.Combine(scratch.Dir, "null.txt"), "null");

        var manager = Scan(scratch);

        Assert.Equal(new[] { "good" }, OrbIds(manager));
    }

    [AvaloniaFact]
    public void TheAutoColourMarkerIsReconciledOnEveryScanRatherThanOnlyWhenToggled()
    {
        // The marker is how the hooks learn the setting, and it lives in the
        // temp path, which the OS is entitled to clear out. A marker that
        // vanished would turn the feature off with nothing said — so a scan
        // puts it back, and this deletes it behind the scan's back to prove it.
        var before = OrbweaverSettings.AutoColorSessions;
        try
        {
            using var scratch = new Scratch();
            var marker = Path.Combine(scratch.Dir, ".auto-color");

            OrbweaverSettings.AutoColorSessions = true;
            var manager = Scan(scratch);
            Assert.True(File.Exists(marker));

            File.Delete(marker);
            manager.ScanAndUpdate();
            Assert.True(File.Exists(marker));

            OrbweaverSettings.AutoColorSessions = false;
            manager.ScanAndUpdate();
            Assert.False(File.Exists(marker));
        }
        finally
        {
            OrbweaverSettings.AutoColorSessions = before;
        }
    }

    [AvaloniaFact]
    public void ResettingASessionToIdleRewritesItsFileAndKeepsEverythingElse()
    {
        // "Keep everything but the state (cwd, terminal info) intact" — the orb
        // has to stay clickable afterwards, and the CLI has to survive the round
        // trip or a reset turns a Codex session into a Claude Code one.
        using var scratch = new Scratch();
        scratch.Write("codex-1", state: "waiting", cli: "codex", title: "a name");

        var manager = Scan(scratch);
        manager.ResetSessionToIdle("codex-1");

        var status = manager.StatusFor("codex-1");
        Assert.Equal("idle", status!.State);
        Assert.Equal(SessionSource.Codex, status.Source);
        Assert.Equal("a name", status.Title);
        Assert.Equal("iTerm.app", status.TermProgram);

        // And on disk, which is what the next scan will read back.
        var onDisk = System.Text.Json.JsonSerializer.Deserialize<SessionStatus>(
            File.ReadAllText(Path.Combine(scratch.Dir, "codex-1.txt")));
        Assert.Equal("idle", onDisk!.State);
        Assert.Equal("codex", onDisk.Cli);
        Assert.Equal(SessionSource.Codex, SessionManager.SourceOf(onDisk));
    }

    [AvaloniaFact]
    public void ResetAllReachesEverySessionAndSessionsOwnedElsewhereAreLeftToTheirOwners()
    {
        // A gateway session has no status file to rewrite, and the path this
        // would build from its key ("openclaw:agent:main:…") is not one this app
        // should be writing at all. ResetAll walks the same code, so a key with
        // a colon in it must be declined rather than turned into a filename.
        using var scratch = new Scratch();
        scratch.Write("local-1", state: "waiting");

        var manager = Scan(scratch);

        var gateway = new SessionStatus { Source = SessionSource.OpenClaw, State = "generating" };
        var remote = new SessionStatus { Source = SessionSource.RemoteControl, State = "waiting" };
        Statuses(manager)["openclaw:agent:main"] = gateway;
        Statuses(manager)["remote:work:session"] = remote;

        manager.ResetAllSessionsToIdle();

        Assert.Equal("idle", manager.StatusFor("local-1")!.State);
        Assert.Equal("generating", gateway.State);
        Assert.Equal("waiting", remote.State);
        Assert.False(Directory.EnumerateFiles(scratch.Dir, "openclaw*").Any());
        Assert.False(Directory.EnumerateFiles(scratch.Dir, "remote*").Any());
    }

    [AvaloniaFact]
    public void ResettingASessionWhoseFileHasVanishedStillResolvesItsCli()
    {
        // `existing ?? new SessionStatus()` is the case the SourceOf call is
        // there for: a file that could not be read has no "cli" either, so the
        // fallback has to go through the same resolution rather than inheriting
        // whatever the caller assumed.
        using var scratch = new Scratch();
        var manager = new SessionManager(scratch.Dir);

        manager.ResetSessionToIdle("never-existed");

        var status = manager.StatusFor("never-existed");
        Assert.Equal("idle", status!.State);
        Assert.Equal(SessionSource.ClaudeCode, status.Source);
    }

    // --- teams --------------------------------------------------------------
    //
    // The scan fills Lead in from the member process's own command line
    // (AgentTeam.Of), which this test process does not carry — so the field is
    // set directly on the status the scan produced. That is the same field, on
    // the same object the rest of the app reads, and it is the only part of a
    // team that cannot be faked from a file: everything downstream of it —
    // gathering, arrows, drag-along — reads nothing else.

    private static IDictionary<string, SessionStatus> Statuses(SessionManager manager)
    {
        var field = typeof(SessionManager).GetField(
            "_statuses", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);

        return (IDictionary<string, SessionStatus>)field!.GetValue(manager)!;
    }

    [AvaloniaFact]
    public void ATeamsMemberIsGatheredBehindItsLeadAndTheWholeTeamMovesFromEitherOrb()
    {
        // Two orbs and no more, for the reason the class comment gives: two
        // files sharing a pid *and* a CLI are what put Superseded's job-list
        // lookup in play, and a third live session would have to share both
        // with one of these. What a third orb would add — the unrelated session
        // the gathering has to step over — is covered purely, and exhaustively,
        // by SessionScanRulesTests.GatherTeams.
        using var scratch = new Scratch();
        scratch.Write("lead");
        scratch.Write("member", cli: "codex");

        var manager = Scan(scratch);
        manager.StatusFor("member")!.Lead = "lead";

        Assert.Equal(new[] { "lead", "member" }, DisplayOrder(manager));

        // CB-268 T2: a plain drag on the lead *or* the member moves the whole
        // team, root first — no longer "a member is dragged on its own".
        IOrbDragHost host = manager;
        Assert.Equal(new[] { "lead", "member" }, host.TeamIdsOf("lead"));
        Assert.Equal(new[] { "lead", "member" }, host.TeamIdsOf("member"));

        // The next scan builds a fresh status from the file, which has no lead
        // in it — the field is the app's, not the hook's — so the team is gone
        // again. Asserted rather than worked around: it is what says the
        // membership above came from the field this test set and not from
        // anything the file could have carried.
        manager.ScanAndUpdate();
        Assert.Equal(new[] { "lead" }, host.TeamIdsOf("lead"));
    }

    [AvaloniaFact]
    public void HidingTheOrbsKeepsTrackingThemAndShowingThemAgainRestoresThem()
    {
        // "Sessions keep being tracked either way, so the tray icon and its menu
        // stay accurate" — the orbs going away must not look like the sessions
        // going away.
        var before = OrbweaverSettings.ShowOrbs;
        try
        {
            using var scratch = new Scratch();
            scratch.Write("session-a");

            var manager = Scan(scratch);
            Assert.True(manager.OrbsVisible);

            manager.SetOrbsVisible(false);
            Assert.False(manager.OrbsVisible);
            Assert.False(OrbweaverSettings.ShowOrbs);
            Assert.Equal(new[] { "session-a" }, OrbIds(manager));
            Assert.NotNull(manager.StatusFor("session-a"));

            // Asked for what it already is, and nothing happens — the early
            // return is what stops a scan's worth of reflow per tick.
            manager.SetOrbsVisible(false);
            Assert.False(manager.OrbsVisible);

            manager.SetOrbsVisible(true);
            Assert.True(manager.OrbsVisible);
            Assert.True(OrbweaverSettings.ShowOrbs);
        }
        finally
        {
            OrbweaverSettings.ShowOrbs = before;
        }
    }

    // --- arrangement --------------------------------------------------------

    [AvaloniaFact]
    public void ArrangingIsAToggleAndLeavesEveryOrbSomewhereOnTheScreen()
    {
        var beforeAnchor = OrbweaverSettings.ArrangeAnchor;
        try
        {
            OrbweaverSettings.ArrangeAnchor = null;

            using var scratch = new Scratch();
            scratch.Write("a");
            scratch.Write("b", cli: "codex");

            var manager = Scan(scratch);
            Assert.False(manager.IsArranged);

            manager.ArrangeOrbsInPattern();
            Assert.True(manager.IsArranged);

            // The anchor is saved on the first arrangement so a later arrival
            // re-fits around where the shape already is rather than around the
            // screen's middle.
            Assert.NotNull(OrbweaverSettings.ArrangeAnchor);

            // Asked again mid-glide, it declines rather than fighting the
            // animation that is already running.
            manager.ArrangeOrbsInPattern();
            Assert.True(manager.IsArranged);

            // What a Shift-drag or a backdrop drag pulls with it (CB-268): the
            // whole group, which is every orb while only `main` is in use.
            IOrbDragHost host = manager;
            Assert.Equal(new[] { "a", "b" }, host.OrbIdsInGroup(host.GroupIdOf("a")).Order());
        }
        finally
        {
            OrbweaverSettings.ArrangeAnchor = beforeAnchor;
        }
    }

    [AvaloniaFact]
    public void ReapplyingTheArrangementDoesNothingWhileNothingIsArranged()
    {
        using var scratch = new Scratch();
        scratch.Write("a");

        var manager = Scan(scratch);

        // The settings slider's live preview does nothing at all until the
        // orbs are arranged; the new spacing is simply saved for next time.
        manager.ReapplyArrangement();
        Assert.False(manager.IsArranged);
    }

    // --- dragged positions --------------------------------------------------

    [AvaloniaFact]
    public void AnOrbGoesBackToWhereItWasDraggedAndReturningItToTheStackForgets()
    {
        using var scratch = new Scratch();
        scratch.Write("session-a", title: "a name");

        var key = SessionManager.PositionKeyFor(
            new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = "/Users/user/project", Title = "a name" },
            "session-a");

        try
        {
            var manager = Scan(scratch);

            Assert.Equal(key, PositionKeyOf(manager, "session-a"));

            // A drag: the window moves and the app is told to remember it.
            var window = WindowFor(manager, "session-a");
            window.PinAt(new PixelPoint(300, 200));
            var resetPosition = window.FindControl<MenuItem>("ResetPositionItem")!;
            Assert.True(resetPosition.IsVisible);
            Assert.Equal("Reset this orb's position", resetPosition.Header);
            manager.RememberOrbPosition(window);

            Assert.Equal(300, OrbweaverSettings.OrbPositionFor(key)!.X);

            // A second manager over the same directory restores it — which is
            // the whole point of the key surviving a restart.
            var restored = Scan(scratch);
            Assert.Equal(new PixelPoint(300, 200), WindowFor(restored, "session-a").Position);

            manager.ReturnOrbToStack("session-a");
            Assert.Null(OrbweaverSettings.OrbPositionFor(key));

            // An id nobody is tracking is declined rather than throwing.
            manager.ReturnOrbToStack("not-a-session");
        }
        finally
        {
            OrbweaverSettings.ClearOrbPosition(key);
        }
    }

    // --- pinned chat panels (CB-111) ----------------------------------------

    // The scan-driven half of CB-111, proven the same way
    // AnOrbGoesBackToWhereItWasDraggedAndReturningItToTheStackForgets proves
    // it for the orb itself: a second SessionManager over the same directory
    // is what a restarted app's first scan looks like, and that is the only
    // restart this process can actually perform. The panel is closed with
    // CloseFor rather than unpinned first — CloseFor is what OrbWindow's own
    // Closed handler reaches, which is also what the desktop lifetime's
    // Shutdown() reaches for every window when the app quits — so this is
    // the real path a pin has to survive, not a shortcut around it.
    [AvaloniaFact]
    public void APinnedChatPanelReopensAtItsSavedPositionAfterARestart()
    {
        using var scratch = new Scratch();
        scratch.Write("session-a", title: "a name");

        var key = SessionManager.PositionKeyFor(
            new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = "/Users/user/project", Title = "a name" },
            "session-a");

        try
        {
            var manager = Scan(scratch);
            var window = WindowFor(manager, "session-a");

            var chat = manager.RemoteChatFor("session-a");
            Assert.NotNull(chat);

            ChatPanel.OpenFor(window, chat!);
            Dispatcher.UIThread.RunJobs();

            var panel = ChatPanel.PanelFor("session-a")!;
            panel.TogglePin();
            panel.Position = new PixelPoint(444, 333);
            Dispatcher.UIThread.RunJobs();

            var saved = OrbweaverSettings.PinnedChatPanelPositionFor(key);
            Assert.NotNull(saved);
            Assert.Equal(444, saved!.X);
            Assert.Equal(333, saved.Y);

            // The app quitting, simulated: every window closes, nothing is
            // unpinned.
            ChatPanel.CloseFor("session-a");
            Dispatcher.UIThread.RunJobs();
            Assert.Null(ChatPanel.PanelFor("session-a"));

            var restored = Scan(scratch);
            Dispatcher.UIThread.RunJobs();

            var restoredPanel = ChatPanel.PanelFor("session-a");
            Assert.NotNull(restoredPanel);
            Assert.True(restoredPanel!.IsPinned);
            Assert.Equal(new PixelPoint(444, 333), restoredPanel.Position);
            Assert.True(ChatPanel.IsOpenFor("session-a"));

            // Not a stray manager-independent fact — the restored window is
            // the one actually reflected in the new manager's own registry.
            Assert.NotNull(WindowFor(restored, "session-a"));
        }
        finally
        {
            ChatPanel.CloseFor("session-a");
            OrbweaverSettings.ClearPinnedChatPanelPosition(key);
        }
    }

    // --- CB-198: orb sizes ---------------------------------------------------

    [AvaloniaFact]
    public void ReapplyingSizesResizesEveryOrbAndTheStackAdvancesByEachOrbsOwnSize()
    {
        var before = OrbweaverSettings.OrbSize;
        string? key = null;
        try
        {
            using var scratch = new Scratch();
            scratch.Write("a");
            scratch.Write("b", cli: "codex");

            var manager = Scan(scratch);
            var a = WindowFor(manager, "a");
            var b = WindowFor(manager, "b");
            key = b.SoundKey;

            OrbweaverSettings.OrbSize = 1.5;
            OrbweaverSettings.SetOrbSize(key, 2.0);
            manager.ReapplyOrbSizes();

            Assert.Equal(1.5, a.OrbSize);
            Assert.Equal(2.0, b.OrbSize);

            // The unarranged stack: right edges line up a margin in from the
            // screen, and whichever orb is lower sits below the other's *own*
            // height plus the spacing, not below a shared 56.
            var screen = a.Screens.Primary ?? a.Screens.All.First();
            var scale = screen.Scaling;
            var margin = (int)(24 * scale);
            Assert.Equal(screen.WorkingArea.Right - margin, a.Position.X + SessionManager.OrbPixels(a, scale));
            Assert.Equal(screen.WorkingArea.Right - margin, b.Position.X + SessionManager.OrbPixels(b, scale));

            var (upper, lower) = a.Position.Y < b.Position.Y ? (a, b) : (b, a);
            Assert.Equal(upper.Position.Y + SessionManager.OrbPixels(upper, scale) + (int)(12 * scale), lower.Position.Y);
        }
        finally
        {
            OrbweaverSettings.OrbSize = before;
            if (key is not null) OrbweaverSettings.SetOrbSize(key, null);
        }
    }

    [AvaloniaFact]
    public void ReapplyingSizesWhileArrangedReArrangesAroundTheNewSizes()
    {
        var beforeSize = OrbweaverSettings.OrbSize;
        var beforeAnchor = OrbweaverSettings.ArrangeAnchor;
        try
        {
            OrbweaverSettings.ArrangeAnchor = null;

            using var scratch = new Scratch();
            scratch.Write("a");
            scratch.Write("b", cli: "codex");

            var manager = Scan(scratch);
            manager.ArrangeOrbsInPattern();
            FinishArrangeAnimation(manager);

            var a = WindowFor(manager, "a");
            var b = WindowFor(manager, "b");

            static double CentreDistance(OrbWindow p, OrbWindow q)
            {
                var sp = SessionManager.OrbPixels(p, p.DesktopScaling) / 2.0;
                var sq = SessionManager.OrbPixels(q, q.DesktopScaling) / 2.0;
                var dx = (p.Position.X + sp) - (q.Position.X + sq);
                var dy = (p.Position.Y + sp) - (q.Position.Y + sq);
                return Math.Sqrt(dx * dx + dy * dy);
            }

            var atDefault = CentreDistance(a, b);

            OrbweaverSettings.OrbSize = OrbSizing.Max;
            manager.ReapplyOrbSizes();

            Assert.Equal(OrbSizing.Max, a.OrbSize);

            // The control: the layout must actually have opened up for the
            // bigger orbs. Without it, a 1.0 layout that happens to clear two
            // 2x circles would pass the check below with no sizes reaching
            // OrbArrangement at all.
            Assert.True(CentreDistance(a, b) > atDefault,
                $"centres {CentreDistance(a, b):0.0}px apart at 2x, {atDefault:0.0}px at 1x — the arrangement did not grow");

            // Two orbs at the largest size must not overlap — the arrangement
            // was asked for their real sizes. *Circles*, not windows: that is
            // the invariant ArrangementSweep holds the arrangement to, and
            // windows already overlap at 1.0 whenever the spacing is tight. (A
            // first version of this test compared window rectangles and failed
            // on a correct arrangement.)
            var scale = (a.Screens.Primary ?? a.Screens.All.First()).Scaling;
            var radii = 2 * OrbSizing.Max * 18 * scale;
            Assert.True(CentreDistance(a, b) >= radii - 1,
                $"centres {CentreDistance(a, b):0.0}px apart, two {OrbSizing.Max}x circles need {radii:0.0}");
        }
        finally
        {
            OrbweaverSettings.OrbSize = beforeSize;
            OrbweaverSettings.ArrangeAnchor = beforeAnchor;
        }
    }

    [AvaloniaFact]
    public void AnOrbLeftOffscreenAtDoubleSizeIsClampedBackWithItsWholeWindowOnScreen()
    {
        var before = OrbweaverSettings.OrbSize;
        try
        {
            using var scratch = new Scratch();
            scratch.Write("session-a");

            var manager = Scan(scratch);
            OrbweaverSettings.OrbSize = 2.0;
            manager.ReapplyOrbSizes();

            var window = WindowFor(manager, "session-a");
            window.Position = new PixelPoint(999_999, 999_999);
            manager.ScanAndUpdate();

            var screen = window.Screens.Primary ?? window.Screens.All.First();
            var size = SessionManager.OrbPixels(window, screen.Scaling);
            Assert.True(screen.WorkingArea.Contains(new PixelRect(window.Position, new PixelSize(size, size))),
                $"{window.Position} + {size} is not inside {screen.WorkingArea}");
        }
        finally
        {
            OrbweaverSettings.OrbSize = before;
        }
    }

    [AvaloniaFact]
    public void ALaterKeyCarryingADifferentSizeReLaysTheOrbsOut()
    {
        var before = OrbweaverSettings.OrbSize;
        var titledKey = "";
        try
        {
            using var scratch = new Scratch();
            scratch.Write("session-a");

            var manager = Scan(scratch);
            var window = WindowFor(manager, "session-a");
            Assert.Equal(1.0, window.OrbSize);

            titledKey = SessionManager.SoundKeyFor(
                new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = "/Users/user/project", Title = "named" },
                "session-a");
            OrbweaverSettings.SetOrbSize(titledKey, 2.0);

            scratch.Write("session-a", title: "named");
            manager.ScanAndUpdate();

            Assert.Equal(titledKey, window.SoundKey);
            Assert.Equal(2.0, window.OrbSize);

            var screen = window.Screens.Primary ?? window.Screens.All.First();
            Assert.Equal(screen.WorkingArea.Right - (int)(24 * screen.Scaling),
                window.Position.X + SessionManager.OrbPixels(window, screen.Scaling));
        }
        finally
        {
            OrbweaverSettings.OrbSize = before;
            if (titledKey.Length > 0) OrbweaverSettings.SetOrbSize(titledKey, null);
        }
    }

    [AvaloniaFact]
    public void AnOrbSavedAtDoubleSizeComesBackCentredWhereItWasAtTheDefault()
    {
        var before = OrbweaverSettings.OrbSize;
        using var scratch = new Scratch();
        scratch.Write("session-a", title: "sized");
        var key = SessionManager.PositionKeyFor(
            new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = "/Users/user/project", Title = "sized" },
            "session-a");
        try
        {
            OrbweaverSettings.OrbSize = 2.0;
            var manager = Scan(scratch);
            var window = WindowFor(manager, "session-a");
            window.PinAt(new PixelPoint(300, 200));
            manager.RememberOrbPosition(window);
            Assert.Equal(2.0, OrbweaverSettings.OrbPositionFor(key)!.Size);

            var scale = (window.Screens.ScreenFromPoint(window.Position) ?? window.Screens.Primary)!.Scaling;
            var centreThen = 300 + OrbSizing.CentreDip(2.0) * scale;

            OrbweaverSettings.OrbSize = 1.0;
            var restored = WindowFor(Scan(scratch), "session-a");

            Assert.Equal(1.0, restored.OrbSize);
            Assert.Equal(centreThen, restored.Position.X + OrbSizing.CentreDip(1.0) * scale, 0);
            Assert.Equal(200 + OrbSizing.CentreDip(2.0) * scale, restored.Position.Y + OrbSizing.CentreDip(1.0) * scale, 0);
        }
        finally
        {
            OrbweaverSettings.OrbSize = before;
            OrbweaverSettings.ClearOrbPosition(key);
        }
    }

    [AvaloniaFact]
    public void ASpotSavedBeforeOrbsHadASizeIsReadAsTheDefaultSize()
    {
        var before = OrbweaverSettings.OrbSize;
        using var scratch = new Scratch();
        scratch.Write("session-a", title: "legacy");
        var key = SessionManager.PositionKeyFor(
            new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = "/Users/user/project", Title = "legacy" },
            "session-a");
        try
        {
            // No size, exactly as an older build writes it.
            OrbweaverSettings.SetOrbPosition(key, 300, 200);
            OrbweaverSettings.OrbSize = 1.0;

            Assert.Equal(new PixelPoint(300, 200), WindowFor(Scan(scratch), "session-a").Position);

            // And at 2x it grows around the centre a 1.0 orb had there.
            OrbweaverSettings.OrbSize = 2.0;
            var big = WindowFor(Scan(scratch), "session-a");
            var scale = (big.Screens.ScreenFromPoint(big.Position) ?? big.Screens.Primary)!.Scaling;
            Assert.Equal(300 + 28 * scale, big.Position.X + 56 * scale, 0);
        }
        finally
        {
            OrbweaverSettings.OrbSize = before;
            OrbweaverSettings.ClearOrbPosition(key);
        }
    }

    [AvaloniaFact]
    public void ResizingAPinnedOrbReSavesItsSpotAndSize()
    {
        var before = OrbweaverSettings.OrbSize;
        using var scratch = new Scratch();
        scratch.Write("session-a", title: "pinned");
        var key = SessionManager.PositionKeyFor(
            new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = "/Users/user/project", Title = "pinned" },
            "session-a");
        try
        {
            OrbweaverSettings.OrbSize = 1.0;
            var manager = Scan(scratch);
            var window = WindowFor(manager, "session-a");
            window.PinAt(new PixelPoint(300, 200));
            manager.RememberOrbPosition(window);

            OrbweaverSettings.OrbSize = 2.0;
            manager.ReapplyOrbSizes();

            var saved = OrbweaverSettings.OrbPositionFor(key)!;
            Assert.Equal(2.0, saved.Size);
            Assert.Equal(window.Position, new PixelPoint(saved.X, saved.Y));
        }
        finally
        {
            OrbweaverSettings.OrbSize = before;
            OrbweaverSettings.ClearOrbPosition(key);
        }
    }

    [AvaloniaFact]
    public void ResizingNeverLetsAPinnedOrbOverwriteASpotItSharesWithAnother()
    {
        // The drag code's rule: two pinned orbs on one key (a lead and a member
        // dragged together) never have the member's spot written over the
        // lead's. The saved entry keeps the size it was saved at, so it restores
        // correctly without the re-save.
        var before = OrbweaverSettings.OrbSize;
        using var scratch = new Scratch();
        scratch.Write("second", title: "shared name");
        var key = SessionManager.PositionKeyFor(
            new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = "/Users/user/project", Title = "shared name" },
            "first");
        try
        {
            OrbweaverSettings.OrbSize = 1.0;
            var manager = Scan(scratch);
            var second = WindowFor(manager, "second");
            second.PinAt(new PixelPoint(500, 250));

            // Injected after the scan, not before: "first" has no status file,
            // so a scan would prune it (the first version of this test did
            // exactly that, and then measured a lone pinned orb).
            var first = new OrbWindow("first");
            first.PinAt(new PixelPoint(400, 250));
            first.PositionKey = key;
            WindowsDict(manager)["first"] = first;
            OrbweaverSettings.SetOrbPosition(key, 400, 250, 1.0);

            OrbweaverSettings.OrbSize = 2.0;
            manager.ReapplyOrbSizes();

            Assert.Equal(key, second.PositionKey);
            Assert.Same(first, WindowFor(manager, "first"));
            Assert.Equal(new OrbweaverSettings.OrbPlacement(400, 250, 1.0), OrbweaverSettings.OrbPositionFor(key));

            // The control: take the sibling away and the same call does re-save.
            WindowsDict(manager).Remove("first");
            OrbweaverSettings.OrbSize = 1.5;
            manager.ReapplyOrbSizes();
            Assert.Equal(1.5, OrbweaverSettings.OrbPositionFor(key)!.Size);
        }
        finally
        {
            OrbweaverSettings.OrbSize = before;
            OrbweaverSettings.ClearOrbPosition(key);
        }
    }

    [AvaloniaFact]
    public void TheSizeMenuOnAManagedOrbReLaysTheStackOut()
    {
        var before = OrbweaverSettings.OrbSize;
        string? soundKey = null;
        try
        {
            OrbweaverSettings.OrbSize = 1.0;
            using var scratch = new Scratch();
            scratch.Write("session-a");
            var manager = Scan(scratch);
            var window = WindowFor(manager, "session-a");
            soundKey = window.SoundKey;

            window.RebuildSizeSubmenu();
            var item = window.FindControl<MenuItem>("SizeMenuItem")!.Items.OfType<MenuItem>()
                .Single(i => Equals(i.Header, "200%"));
            item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Equal(2.0, window.OrbSize);

            // Re-laid out by the manager, not just resized around its centre:
            // the stack's right edge is back a margin in from the screen.
            var screen = window.Screens.Primary ?? window.Screens.All.First();
            Assert.Equal(screen.WorkingArea.Right - (int)(24 * screen.Scaling),
                window.Position.X + SessionManager.OrbPixels(window, screen.Scaling));
        }
        finally
        {
            OrbweaverSettings.OrbSize = before;
            if (soundKey is not null) OrbweaverSettings.SetOrbSize(soundKey, null);
        }
    }

    // OrbArrangementAnimationTests.CompleteTheGlide's technique: rewind the
    // start so the next tick reports the glide finished, and fire that tick.
    private static void FinishArrangeAnimation(SessionManager manager)
    {
        typeof(SessionManager).GetField("_arrangeAnimStart", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(manager, Environment.TickCount64 - 700);
        typeof(SessionManager).GetMethod("OnArrangeAnimTick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(manager, new object?[] { null, EventArgs.Empty });
    }

    private static OrbWindow WindowFor(SessionManager manager, string sessionId)
    {
        var field = typeof(SessionManager).GetField(
            "_windows", BindingFlags.NonPublic | BindingFlags.Instance);
        var windows = (IDictionary<string, OrbWindow>)field!.GetValue(manager)!;
        return windows[sessionId];
    }

    private static string PositionKeyOf(SessionManager manager, string sessionId) =>
        WindowFor(manager, sessionId).PositionKey;

    // --- chat ---------------------------------------------------------------

    [AvaloniaFact]
    public void AnUntrackedSessionHasNoConversationBehindIt()
    {
        // Null is the whole of the "this orb's click means something else"
        // signal — the orb deliberately knows nothing about which source a
        // session came from or whether the feature is on.
        using var scratch = new Scratch();
        var manager = new SessionManager(scratch.Dir);

        Assert.Null(manager.RemoteChatFor("never-existed"));
    }

    // A local session's conversation is cached — the same object comes back
    // on a second click — and gated by the same chat-enabled setting the
    // composer reads, checked before the cache so turning the feature off
    // does not hand back a session built while it was on.
    [AvaloniaFact]
    public void RemoteChatForALocalSessionIsGatedThenCachedOnceCreated()
    {
        var before = OrbweaverSettings.ClaudeCodeChatEnabled;
        try
        {
            using var scratch = new Scratch();
            var transcriptPath = Path.Combine(scratch.Dir, "transcript.jsonl");
            File.WriteAllText(transcriptPath, "");

            File.WriteAllText(Path.Combine(scratch.Dir, "session-a.txt"),
                System.Text.Json.JsonSerializer.Serialize(new SessionStatus
                {
                    State = "idle",
                    Cwd = "/Users/user/project",
                    SessionPid = LivePid,
                    TermProgram = "iTerm.app",
                    Tty = "/dev/ttys004",
                    TranscriptPath = transcriptPath,
                }));

            var manager = new SessionManager(scratch.Dir);
            manager.ScanAndUpdate();

            OrbweaverSettings.ClaudeCodeChatEnabled = false;
            Assert.Null(manager.RemoteChatFor("session-a"));

            OrbweaverSettings.ClaudeCodeChatEnabled = true;
            var chat = manager.RemoteChatFor("session-a");
            Assert.IsType<LocalCliChatSession>(chat);

            var again = manager.RemoteChatFor("session-a");
            Assert.Same(chat, again);
        }
        finally
        {
            OrbweaverSettings.ClaudeCodeChatEnabled = before;
        }
    }

    // --- the auto-colour marker's own failure path ---

    [AvaloniaFact]
    public void TheAutoColourMarkerFailsQuietlyWhenItsDirectoryDoesNotExist()
    {
        // "Worst case the colour setting does not take effect, which is not
        // worth interrupting a scan for" — proved by handing it a directory
        // that was never created, so the write it attempts has nowhere to
        // land.
        var before = OrbweaverSettings.AutoColorSessions;
        try
        {
            var missingDir = Path.Combine(Path.GetTempPath(), "cb-scan-missing-" + Guid.NewGuid());
            var manager = new SessionManager(missingDir);

            OrbweaverSettings.AutoColorSessions = true;
            manager.SyncAutoColorMarker();

            Assert.False(Directory.Exists(missingDir));
        }
        finally
        {
            OrbweaverSettings.AutoColorSessions = before;
        }
    }

    // --- ResetSessionToIdle's other two paths ---

    [AvaloniaFact]
    public void ResettingAGatewaySessionDirectlyIsDeclinedRatherThanWritingAFile()
    {
        // There is no status file to rewrite for a gateway session, and
        // "openclaw:agent:main.txt" is not a path this app should ever write
        // to. ResetAllReachesEverySessionAndAGatewayOneIsLeftToItsOwner drives
        // this through ResetAll, but its gateway entry is never in _order —
        // it is injected straight into _statuses, exactly as this test does —
        // so ResetAll's loop never actually calls ResetSessionToIdle on it.
        // This calls it directly.
        using var scratch = new Scratch();
        var manager = new SessionManager(scratch.Dir);

        Statuses(manager)["openclaw:agent:main"] = new SessionStatus
        {
            Source = SessionSource.OpenClaw, State = "generating",
        };

        manager.ResetSessionToIdle("openclaw:agent:main");

        Assert.Equal("generating", manager.StatusFor("openclaw:agent:main")!.State);
        Assert.Empty(Directory.EnumerateFiles(scratch.Dir, "openclaw*"));
    }

    [AvaloniaFact]
    public void ResettingASessionWhoseDirectoryIsGoneStillUpdatesStatusInMemory()
    {
        // Both the read and the write it attempts fail here — the directory
        // was never created — and neither failure should stop the in-memory
        // status from reflecting "idle", which is what the orb itself reads.
        var missingDir = Path.Combine(Path.GetTempPath(), "cb-scan-missing-" + Guid.NewGuid());
        var manager = new SessionManager(missingDir);

        manager.ResetSessionToIdle("never-existed");

        Assert.Equal("idle", manager.StatusFor("never-existed")!.State);
        Assert.False(Directory.Exists(missingDir));
    }

    // --- keeping an orb reachable ---

    [AvaloniaFact]
    public void AnOrbLeftOffscreenByAChangedDesktopIsBroughtBackOnTheNextScan()
    {
        // Nothing moved the orb; the desktop underneath it changed shape —
        // simulated here by pushing it somewhere no configured screen could
        // possibly cover, the same as a monitor being unplugged would.
        using var scratch = new Scratch();
        scratch.Write("session-a");

        var manager = Scan(scratch);
        var window = WindowFor(manager, "session-a");
        window.Position = new PixelPoint(-999_999, -999_999);

        manager.ScanAndUpdate();

        Assert.NotEqual(new PixelPoint(-999_999, -999_999), window.Position);
    }

    [AvaloniaFact]
    public void ASiblingAlreadyPinnedAtASavedPositionStopsASecondOrbStackingOnIt()
    {
        // The sibling is injected directly into _windows rather than produced
        // by a second real session file: two ClaudeCode sessions sharing this
        // process's one certainly-alive pid would collide in Superseded
        // before either reached RestoreOrbPosition, which the class comment
        // above already explains is why every fixture here uses one pid.
        // What is under test is RestoreOrbPosition's own collision check, and
        // it only needs a window already sitting in the dictionary it reads.
        using var scratch = new Scratch();
        scratch.Write("second", title: "shared name");

        var key = SessionManager.PositionKeyFor(
            new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = "/Users/user/project", Title = "shared name" },
            "first");

        var manager = new SessionManager(scratch.Dir);

        var sibling = new OrbWindow("first");
        sibling.PinAt(new PixelPoint(400, 250));
        sibling.PositionKey = key;
        WindowsDict(manager)["first"] = sibling;

        manager.ScanAndUpdate();

        var second = WindowFor(manager, "second");
        Assert.Equal(key, second.PositionKey);
        Assert.False(second.IsPinned);
    }

    private static Dictionary<string, OrbWindow> WindowsDict(SessionManager manager)
    {
        var field = typeof(SessionManager).GetField(
            "_windows", BindingFlags.NonPublic | BindingFlags.Instance);
        return (Dictionary<string, OrbWindow>)field!.GetValue(manager)!;
    }

    // --- reachability ---

    [AvaloniaFact]
    public void ASessionWithNoTerminalAtAllNeverBecomesAnOrb()
    {
        // Codex specifically: a Claude Code session in the same shape would
        // ask BackgroundJobs for real before answering (see the class comment
        // on why that is out of bounds here), but Codex has no background-job
        // concept to ask about and the rule short-circuits before it would.
        using var scratch = new Scratch();
        File.WriteAllText(Path.Combine(scratch.Dir, "headless.txt"),
            System.Text.Json.JsonSerializer.Serialize(new SessionStatus
            {
                Cli = "codex",
                SessionPid = LivePid,
                // No cwd, no tty, no term_program, no tmux pane, no term_pid.
            }));

        var manager = new SessionManager(scratch.Dir);
        manager.ScanAndUpdate();

        Assert.DoesNotContain("headless", OrbIds(manager));
    }

    // CB-265. The same blind Claude Code file, once as Claude Desktop's and
    // once as each other entrypoint's. Only Desktop's becomes an orb; the
    // others are the negative controls, and sit beside the Codex case above.
    // The listing is a seam, never the machine's real daemon.
    [AvaloniaTheory]
    [InlineData(SessionStatus.DesktopEntrypoint, true)]
    [InlineData("cli", false)]
    [InlineData("sdk-cli", false)]
    [InlineData("", false)]
    public void AClaudeDesktopFileWithNoTerminalBecomesAnOrbAndItsTwinsDoNot(
        string entrypoint, bool becomesAnOrb)
    {
        // One scratch per case: all of them record this process's pid, and two
        // files sharing a pid supersede each other, which is a different rule.
        using var scratch = new Scratch();
        scratch.Write("blind", entrypoint: entrypoint, tty: "", termProgram: "");

        var manager = Manager(scratch, jobListing: () => Listing());
        manager.ScanAndUpdate();

        Assert.Equal(becomesAnOrb, OrbIds(manager).Contains("blind"));
    }

    // --- broadcasts that touch every orb ---

    [AvaloniaFact]
    public void ReapplyingStateColorsAndGlyphsReachesEveryOrbWithoutThrowing()
    {
        // A colour or glyph setting change is not a session change, so
        // nothing on the scan path notices one — these are what the settings
        // window calls instead, and both simply walk every window.
        using var scratch = new Scratch();
        scratch.Write("a");
        scratch.Write("b", cli: "codex");
        var manager = Scan(scratch);

        manager.ReapplyStateColors();
        manager.ReapplyGlyphs();
    }

    // A speech-state change is broadcast to every orb's flyout and to the
    // chat panel, on the UI thread — private because only TextToSpeech's own
    // event should ever raise it, so reached here through reflection the same
    // way OrbIds reaches _windows.
    [AvaloniaFact]
    public void ASpeechStateChangeIsPostedToEveryOrbsFlyout()
    {
        using var scratch = new Scratch();
        scratch.Write("a");
        var manager = Scan(scratch);

        var method = typeof(SessionManager).GetMethod(
            "OnSpeakStateChanged", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        method!.Invoke(manager, new object[] { TextToSpeech.SpeakState.Speaking });
        Dispatcher.UIThread.RunJobs();
    }

    // --- the tray, when one is attached ---

    [AvaloniaFact]
    public void AttachedTrayIsFedTheStackInDisplayOrder()
    {
        // SessionScanTests never calls Start(), so _tray is null for every
        // other case in this file and UpdateTray's own call is a no-op on a
        // null. Attaching one the same way TrayRemoteItemTests does exercises
        // the call for real: a NativeMenu built under the headless platform is
        // not guaranteed, so a TrayController that cannot construct here is
        // reported as "couldn't check" rather than silently passing.
        using var scratch = new Scratch();
        scratch.Write("a");
        var manager = Scan(scratch);

        TrayController tray;
        try
        {
            tray = new TrayController();
        }
        catch
        {
            return;
        }

        typeof(SessionManager).GetField("_tray", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(manager, tray);

        manager.ScanAndUpdate();
    }

    // --- background leads with no terminal of their own ---

    [AvaloniaFact]
    public void ALeadWithALiveAgentKeepsItsOrbDespiteHavingNoTerminal()
    {
        // Two entries share this process's one certainly-alive pid without
        // colliding in Superseded because they name different CLIs — the
        // same trick ATeamsMemberIsGatheredBehindItsLeadAndFollowsItWhenDragged
        // uses. AgentTeam.Of caches by pid alone, so seeding it once here
        // answers for both: the Codex entry reads it as "I have a live agent
        // lead" (populating leadsWithLiveAgents) and the ClaudeCode entry
        // reads it as "that lead is me" (excluded from being its own member
        // by the self-check in the source).
        //
        // Neither BackgroundJobs.IsLiveJob nor AgentTeamViewer.TryAdopt's real
        // ps/lsof scan is reached: the member keeps a terminal so
        // WantsAgentViewer never asks for it, the lead's Cwd is empty so
        // TryAdopt's own first check declines before any process walk, and
        // both entries carry a real pid, so JudgeReachability's unconditional
        // "no pid" branch — which would ask BackgroundJobs for real — never
        // fires for either.
        var cache = (Dictionary<int, (AgentTeam.Membership Value, long Stamp)>)typeof(AgentTeam)
            .GetField("Cache", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        lock (cache) cache.Clear();
        try
        {
            lock (cache)
            {
                cache[LivePid] = (new AgentTeam.Membership("lead-id", "", ""), Environment.TickCount64);
            }

            using var scratch = new Scratch();
            scratch.Write("codex-member", cli: "codex"); // has a terminal by default
            File.WriteAllText(Path.Combine(scratch.Dir, "lead-id.txt"),
                System.Text.Json.JsonSerializer.Serialize(new SessionStatus
                {
                    State = "idle",
                    SessionPid = LivePid,
                    // No cwd, no terminal fields at all: a background lead
                    // running under `claude daemon run`.
                }));

            // The listing is handed over rather than fetched, which it did not
            // used to need to be. The lead below names no terminal, and the scan
            // now asks the daemon about exactly that shape of session — see
            // SessionPresence.WorthAskingTheDaemon — so without this seam this
            // test would run `claude agents --json` against whatever daemon the
            // machine happens to be running. Empty rather than null: this lead
            // is not a job, and saying so is what the rest of the test is about.
            var manager = new SessionManager(scratch.Dir, () => Listing());
            manager.ScanAndUpdate();

            // The member keeps its own terminal-based orb as normal...
            Assert.Contains("codex-member", OrbIds(manager));

            // ...and the lead gets one too, despite naming no terminal at
            // all, because leadsWithLiveAgents exempts it: "agents on screen
            // pointing at nothing is a worse lie than an orb you might not be
            // able to click."
            Assert.Contains("lead-id", OrbIds(manager));
        }
        finally
        {
            lock (cache) cache.Clear();
        }
    }

    // --- nothing to show: no terminal and no transcript (CB-9) ---

    // Driven through a real scan and a real File.Exists against a real path,
    // which is the half a unit test cannot state: the rule is answered by the
    // disk here, not by a predicate handed in. Which of the two terminal-less
    // rules returns the verdict is pinned in ScanVerdictTests
    // (NothingToShowIsAnsweredBeforeNoTerminal) — what matters here is the
    // end-to-end outcome a user sees, and that nothing else in the scan
    // undoes it.
    //
    // Nothing reaches BackgroundJobs.IsLiveJob: every entry carries a real
    // pid, so the "no pid" branch that would shell out never fires. Same
    // reasoning as the lead test above.
    [AvaloniaFact]
    public void AStatusFileNamingATranscriptThatIsNotThereGetsNoOrb()
    {
        using var scratch = new Scratch();

        scratch.Write("unprompted-job", tty: "", termProgram: "",
                      transcriptPath: scratch.MissingTranscript("unprompted-job"));

        Assert.DoesNotContain("unprompted-job", OrbIds(Scan(scratch)));
    }

    [AvaloniaFact]
    public void ASessionInARealTerminalKeepsItsOrbBeforeItHasWrittenAnything()
    {
        // The regression that would hurt most, and the reason the rule requires
        // *both* halves: every session is transcript-less for its first moment,
        // and an interactive one must not flicker off screen while it is. Its
        // click lands in the terminal either way, so the orb has earned its
        // place before the conversation exists.
        using var scratch = new Scratch();

        scratch.Write("fresh-terminal",
                      transcriptPath: scratch.MissingTranscript("fresh-terminal"));

        Assert.Contains("fresh-terminal", OrbIds(Scan(scratch)));
    }

    [AvaloniaFact]
    public void ATranscriptOnDiskKeepsTheOrbTheMissingOneWouldHaveCost()
    {
        // The same file, the same absent terminal fields, one thing changed:
        // the transcript is really there. Written to disk rather than asserted
        // about, so this is File.Exists answering.
        using var scratch = new Scratch();

        scratch.Write("has-a-transcript", tty: "/dev/ttys004",
                      transcriptPath: scratch.WriteTranscript("has-a-transcript"));

        Assert.Contains("has-a-transcript", OrbIds(Scan(scratch)));
    }

    // --- a transcript that is not where the status file says ---

    // The respawned-worker shape, end to end. The daemon relaunches a finished
    // job's worker from the job's original directory, so the hook records a
    // transcript_path computed from that directory while the conversation lives
    // in the projects directory keyed by where the session actually ran. On a
    // real machine that was job b0633b77: a 3.6MB transcript, an orb wearing
    // fallback letters, and a chat panel that opened blank forever.
    //
    // Every seam is handed over: the listing (this session has no terminal, so
    // the scan asks the daemon about it), the attach scan (same trigger), and
    // the hunt (the real one walks this machine's own projects directories).
    [AvaloniaFact]
    public void AMislocatedTranscriptIsReFoundBeforeAnythingReadsThePath()
    {
        using var scratch = new Scratch();

        var real = scratch.WriteTranscript("done-job",
            "{\"type\":\"custom-title\",\"customTitle\":\"blank-orbs\"}");

        scratch.Write("done-job", tty: "/dev/ttys009", termProgram: "", title: "",
                      transcriptPath: scratch.MissingTranscript("done-job"));

        var manager = new SessionManager(
            scratch.Dir,
            () => new Dictionary<string, string> { ["done-job"] = "done" },
            () => new HashSet<string>(),
            id => id == "done-job" ? real : null);
        manager.ScanAndUpdate();

        // The finished job keeps its dimmed orb — the repair is what satisfies
        // the nothing-to-show rule that would otherwise have dropped it...
        Assert.Contains("done-job", OrbIds(manager));

        var status = manager.StatusFor("done-job");
        Assert.NotNull(status);

        // ...the chat panel receives the corrected path through the same status
        // object, so its tail lands on the real conversation...
        Assert.Equal(real, status!.TranscriptPath);

        // ...and the identity read used it too: the orb wears the name the
        // session was given rather than letters derived from a directory.
        Assert.Equal("blank-orbs", status.Title);
    }

    [AvaloniaFact]
    public void AWorkerWithNoConversationAnywhereGetsNoOrbDespiteItsPty()
    {
        // The other phantom the same screenshot held: the daemon runs every
        // background worker under a pty host, so a never-prompted one records a
        // real /dev/ttysNN with no window behind it, no transcript anywhere,
        // and no row in the daemon's listing. Session de995bd9 live: untitled,
        // idle, tty ttys006, drawn as a blank orb whose chat said "No pane to
        // type into". The tty must not count as a terminal here.
        using var scratch = new Scratch();

        scratch.Write("phantom", tty: "/dev/ttys006", termProgram: "", title: "",
                      transcriptPath: scratch.MissingTranscript("phantom"));

        var manager = new SessionManager(
            scratch.Dir,
            () => new Dictionary<string, string>(),
            () => new HashSet<string>(),
            _ => null);
        manager.ScanAndUpdate();

        Assert.DoesNotContain("phantom", OrbIds(manager));
    }

    // --- untitled siblings do not share a slot (CB-10) ---

    [AvaloniaFact]
    public void TwoUntitledSessionsInOneDirectoryGetDifferentPositionKeys()
    {
        // Three live untitled sessions in one directory shared a single
        // bare-cwd key on a real machine, so RestoreOrbPosition refused a slot
        // to two of them and three orbs read as two on screen.
        //
        // The two name different CLIs so they can share this process's one
        // certainly-alive pid without one superseding the other — the same
        // trick ALeadWithALiveAgentKeepsItsOrbDespiteHavingNoTerminal uses, and
        // free here because an untitled session keys on its id whichever CLI it
        // is, so the Codex prefix cannot affect the answer.
        using var scratch = new Scratch();

        scratch.Write("untitled-a", title: "", cwd: "/Users/user/evidence");
        scratch.Write("untitled-b", title: "", cwd: "/Users/user/evidence", cli: "codex");

        var manager = Scan(scratch);

        Assert.Contains("untitled-a", OrbIds(manager));
        Assert.Contains("untitled-b", OrbIds(manager));

        var keys = PositionKeys(manager);
        Assert.Equal("untitled-a", keys["untitled-a"]);
        Assert.Equal("untitled-b", keys["untitled-b"]);
        Assert.NotEqual(keys["untitled-a"], keys["untitled-b"]);
    }

    [AvaloniaFact]
    public void TwoTitledSessionsInOneDirectoryStillKeyOnDirectoryAndTitle()
    {
        // Unchanged, which is the compatibility half: every position already
        // saved on a real machine has to keep matching the session it was
        // saved for. These are the two names the original collision was found
        // with.
        using var scratch = new Scratch();

        scratch.Write("titled-a", title: "job-lawyer", cwd: "/Users/user/evidence");
        scratch.Write("titled-b", title: "makayla-lawyer", cwd: "/Users/user/evidence",
                      cli: "codex");

        var keys = PositionKeys(Scan(scratch));

        // Different CLIs again, to share the one live pid — which also puts the
        // Codex prefix through a real scan rather than a direct call.
        Assert.Equal("/Users/user/evidence\njob-lawyer", keys["titled-a"]);
        Assert.Equal("codex\n/Users/user/evidence\nmakayla-lawyer", keys["titled-b"]);
    }

    // --- a name the status file never caught (CB-11) ---

    [AvaloniaFact]
    public void AnUntitledSessionTakesItsNameFromItsOwnTranscript()
    {
        // The fork case, end to end: the status file records no title because
        // the hook fired once at fork time and lost the race with Claude Code's
        // own append, and never fired again. The transcript has carried the name
        // since its second row.
        using var scratch = new Scratch();

        var transcript = scratch.WriteTranscript("forked-job",
            "{\"type\":\"custom-title\",\"customTitle\":\"evidence (2)\"}",
            "{\"type\":\"agent-color\",\"agentColor\":\"teal\"}");

        scratch.Write("forked-job", title: "", transcriptPath: transcript);

        var status = Scan(scratch).StatusFor("forked-job");

        Assert.NotNull(status);
        Assert.Equal("evidence (2)", status!.Title);
        Assert.Equal("teal", status.Color);

        // Which is the visible payoff: it stops wearing the letters its parent
        // is already wearing.
        Assert.Equal("E2", OrbGlyph.For(status.Title, twoLetter: true));
    }

    [AvaloniaFact]
    public void AStatusFileThatAlreadyHasANameIsLeftAlone()
    {
        // Never an overwrite. The hook read the same file with the same
        // precedence, so if the two ever disagreed the status file is the more
        // recent reading — and this must not be able to move an orb that was
        // already right.
        using var scratch = new Scratch();

        var transcript = scratch.WriteTranscript("named-job",
            "{\"type\":\"custom-title\",\"customTitle\":\"from the transcript\"}");

        scratch.Write("named-job", title: "from the hook", transcriptPath: transcript);

        var status = Scan(scratch).StatusFor("named-job");

        Assert.NotNull(status);
        Assert.Equal("from the hook", status!.Title);
    }

    [AvaloniaFact]
    public void ATranscriptWithNoNameInItLeavesTheOrbAsItIs()
    {
        // The unchanged path, which is most sessions: nothing found, nothing
        // written, and the orb keeps falling back to the folder name exactly as
        // it does today.
        using var scratch = new Scratch();

        var transcript = scratch.WriteTranscript("nameless-job");
        scratch.Write("nameless-job", title: "", transcriptPath: transcript);

        var status = Scan(scratch).StatusFor("nameless-job");

        Assert.NotNull(status);
        Assert.Equal("", status!.Title);
    }

    // The key each orb actually carries, read off the windows the scan built
    // rather than recomputed — the same reasoning OrbIds records for reaching
    // _windows at all.
    private static Dictionary<string, string?> PositionKeys(SessionManager manager)
    {
        var field = typeof(SessionManager).GetField(
            "_windows", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);

        var windows = (System.Collections.IDictionary)field!.GetValue(manager)!;
        var keys = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (System.Collections.DictionaryEntry entry in windows)
            keys[(string)entry.Key] = ((OrbWindow)entry.Value!).PositionKey;

        return keys;
    }

    // --- background jobs: presence, and the hygiene sweep -------------------
    //
    // The scan's half of CB-13. The rules are pure and covered case by case in
    // tests/UnitTests (SessionPresenceTests, SweepRulesTests, JobPhaseTests);
    // what is left — and what the bug actually was — is whether the scan wires
    // them to anything. Fifteen orbs breathing on an idle machine was not a
    // wrong rule, it was a right answer that was parsed and then discarded.
    //
    // Every test below hands over the daemon's listing through the constructor
    // seam rather than letting the scan fetch one. That is not only about speed:
    // the machine this suite runs on is the one that exhibited the bug, and its
    // real listing names the user's real background sessions. A test that read
    // it would be asserting about those.

    private static Dictionary<string, string> Listing(params (string SessionId, string State)[] rows)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (sessionId, state) in rows) map[sessionId] = state;
        return map;
    }

    private static SessionManager Manager(
        Scratch scratch,
        Func<Dictionary<string, string>?>? jobListing = null,
        TimeSpan? sweepGrace = null,
        Func<HashSet<string>?>? attachClients = null,
        Func<int, SessionDependents.Verdict>? dependents = null,
        bool? onWindows = null,
        Func<string, string?>? transcriptHunt = null,
        Action<int, SessionDependents.Verdict>? terminate = null,
        Func<string, string?, bool>? stopJob = null)
    {
        // Both CLIs on, for the reason Scan above states at length.
        OrbweaverSettings.ClaudeCodeEnabled = true;
        OrbweaverSettings.CodexEnabled = true;

        // Nobody attached unless a test says so. Handed over rather than scanned
        // for the same reason the listing is: the real one walks the process
        // table, and on this machine that table holds the user's own attached
        // sessions.
        // Nothing underneath any pid unless a test says so, and handed over for
        // the reason the two above are: the real one walks this machine's
        // process table, which on a developer's Mac holds their own daemon and
        // their own background jobs, and on a CI runner holds neither.
        var manager = new SessionManager(
            scratch.Dir, jobListing,
            attachClients ?? (() => new HashSet<string>(StringComparer.Ordinal)),
            transcriptHunt: transcriptHunt,
            dependents: dependents ?? (_ => SessionDependents.Nothing),
            onWindows: onWindows,
            terminate: terminate,
            // Never the real `claude stop`, which would ask this machine's own
            // daemon about this machine's own jobs (CB-264). A test that wants
            // the daemon to say yes says so.
            stopJob: stopJob ?? ((_, _) => false));
        if (sweepGrace is not null) manager.SweepGrace = sweepGrace.Value;
        return manager;
    }

    // A status file shaped the way a background worker's is: a real pid of its
    // own, and no terminal anywhere — not even a tty, because the hook's walk
    // runs under a daemon that has no controlling terminal to find. That shape
    // is load-bearing in both directions and worth stating: it is what makes a
    // *live* job depend on the daemon's exemption for its orb (JudgeReachability
    // would otherwise call it a dead click), and it is what already drops a
    // *finished* one's orb, which is the behaviour the sweep is bolted onto.
    //
    // The empty cwd is what keeps AgentTeamViewer.TryAdopt's real ps/lsof scan
    // out of this, exactly as the sibling test above does — WantsAgentViewer
    // fires for this shape by design, and TryAdopt's own first check is what
    // declines.
    private static void WriteBackgroundFile(
        Scratch scratch, string sessionId, string state = "idle", DateTime? written = null)
    {
        var path = Path.Combine(scratch.Dir, sessionId + ".txt");

        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new SessionStatus
        {
            State = state,
            SessionPid = LivePid,
        }));

        if (written is not null) File.SetLastWriteTimeUtc(path, written.Value);
    }

    // CB-194. The shape the Windows hook writes for a Claude Code session
    // running inside WSL: no pid, because the hook's walk up the Windows process
    // tree ends in the interop bridge; Linux paths, because that is where the
    // session is; and Windows Terminal, which WT_SESSION carries through WSLENV.
    // The transcript is on the Linux side and does not exist from here, which
    // is why every test using this hands over a hunt that finds nothing rather
    // than letting the scan walk this machine's real projects directories.
    private static void WriteWslFile(Scratch scratch, string sessionId, string cwd = "/home/k/project")
    {
        var transcript = cwd.StartsWith('/')
            ? $"/home/k/.claude/projects/-home-k-project/{sessionId}.jsonl"
            : $@"C:\Users\k\.claude\projects\C--Users-k-project\{sessionId}.jsonl";

        scratch.Write(
            sessionId, state: "generating", cli: "claude", cwd: cwd, pid: 0,
            termProgram: "WindowsTerminal", tty: "", transcriptPath: transcript);
    }

    // A listing that was read and names nobody: what the Windows daemon answers
    // about every WSL session, since the daemon that runs those is in the VM.
    private static Dictionary<string, string>? NobodyListed() => Listing();

    [AvaloniaFact]
    public void AWslSessionGetsAnOrbOnWindowsThoughTheWindowsListingDoesNotNameIt()
    {
        using var scratch = new Scratch();
        WriteWslFile(scratch, "wsl-1");

        var manager = Manager(scratch, NobodyListed, onWindows: true, transcriptHunt: _ => null);
        manager.ScanAndUpdate();

        Assert.Equal(new[] { "wsl-1" }, OrbIds(manager));

        // Drawn as the terminal session it is — Unknown is not a job phase, so
        // nothing downstream badges or dims it as a background one.
        var status = manager.StatusFor("wsl-1");
        Assert.NotNull(status);
        Assert.Equal(LocalSessionShape.Terminal, status!.Shape);
        Assert.Equal(OrbPresence.Present, status.Presence);
    }

    // The negative controls, on the same listing. Each is the rule the fix must
    // not switch off: a pid-less file the local daemon *can* answer for, and
    // does not name, is a subagent or a leftover and still goes.
    [AvaloniaFact]
    public void TheSameFileOnAMacIsStillDroppedAsNotALiveJob()
    {
        using var scratch = new Scratch();
        WriteWslFile(scratch, "wsl-1");

        var manager = Manager(scratch, NobodyListed, onWindows: false, transcriptHunt: _ => null);
        manager.ScanAndUpdate();

        Assert.Empty(OrbIds(manager));
    }

    [AvaloniaFact]
    public void APidlessNativeWindowsFileIsStillDroppedAsNotALiveJob()
    {
        using var scratch = new Scratch();
        WriteWslFile(scratch, "win-1", cwd: @"C:\Users\k\project");

        var manager = Manager(scratch, NobodyListed, onWindows: true, transcriptHunt: _ => null);
        manager.ScanAndUpdate();

        Assert.Empty(OrbIds(manager));
    }

    [AvaloniaFact]
    public void ABlockedBackgroundJobIsDimmedAndBadgedRatherThanRemoved()
    {
        // The orb on the screenshot this ticket came from. "blocked" is a pooled
        // worker between turns: alive, resumable, and doing nothing — and its
        // own status file says "idle", exactly as a job mid-turn's does.
        using var scratch = new Scratch();
        WriteBackgroundFile(scratch, "bg-parked");

        var manager = Manager(scratch, () => Listing(("bg-parked", "blocked")));
        manager.ScanAndUpdate();

        // Kept, first of all. Parking dims an orb and must never remove one.
        Assert.Contains("bg-parked", OrbIds(manager));

        var status = manager.StatusFor("bg-parked");
        Assert.NotNull(status);
        Assert.Equal(LocalSessionShape.Background, status!.Shape);
        Assert.Equal(OrbPresence.NeedsInput, status.Presence);
        Assert.Equal(SessionKind.Background, status.Kind);
    }

    [AvaloniaFact]
    public void AWorkingBackgroundJobIsBadgedButNotDimmed()
    {
        // The wedged session on the same screenshot, and the reason the badge is
        // not conditional: this one is genuinely at work as far as the daemon is
        // concerned, and the orb has to say so.
        using var scratch = new Scratch();
        WriteBackgroundFile(scratch, "bg-working", state: "generating");

        var manager = Manager(scratch, () => Listing(("bg-working", "working")));
        manager.ScanAndUpdate();

        var status = manager.StatusFor("bg-working");
        Assert.NotNull(status);
        Assert.Equal(LocalSessionShape.Background, status!.Shape);
        Assert.Equal(OrbPresence.Present, status.Presence);
        Assert.Equal(SessionKind.Background, status.Kind);
    }

    [AvaloniaFact]
    public void AResumedJobUnDimsOnTheNextScanWhileTheListingIsStillCatchingUp()
    {
        // The cache-lag fix, end to end. The daemon's listing is cached for ten
        // seconds; the hook rewrites the status file the instant a turn starts.
        // So the file is the fresher source, and the same "blocked" row must
        // stop parking the orb as soon as the file says work resumed — otherwise
        // an orb sits dim for ten seconds while the user watches the work happen.
        using var scratch = new Scratch();
        WriteBackgroundFile(scratch, "bg-resumed");

        var manager = Manager(scratch, () => Listing(("bg-resumed", "blocked")));
        manager.ScanAndUpdate();
        Assert.Equal(OrbPresence.NeedsInput, manager.StatusFor("bg-resumed")!.Presence);

        WriteBackgroundFile(scratch, "bg-resumed", state: "generating");
        manager.ScanAndUpdate();

        var status = manager.StatusFor("bg-resumed");
        Assert.Equal(LocalSessionShape.Background, status!.Shape);
        Assert.Equal(OrbPresence.Present, status.Presence);
    }

    [AvaloniaFact]
    public void AListingThatCouldNotBeReadParksNothingAndRemovesNothing()
    {
        // Fail open, at the level that matters: the `claude` CLI being briefly
        // unavailable must not dim every background orb on screen at once, and
        // must not delete any file either.
        using var scratch = new Scratch();
        WriteBackgroundFile(scratch, "bg-unknown");

        var manager = Manager(scratch, () => null, sweepGrace: TimeSpan.Zero);
        manager.ScanAndUpdate();
        manager.ScanAndUpdate();

        var status = manager.StatusFor("bg-unknown");
        Assert.NotNull(status);
        Assert.Equal(OrbPresence.Present, status!.Presence);

        // An unreadable listing keeps the orb (BackgroundJobs.IsLive answers
        // true) and cannot be evidence of anything, so the file survives even
        // with no grace period at all.
        Assert.True(File.Exists(Path.Combine(scratch.Dir, "bg-unknown.txt")));
        Assert.Contains("bg-unknown", OrbIds(manager));
    }

    // --- the two ways the daemon question used to be dodged ------------------
    //
    // Both defeat paths QA found, as fixtures. The gate that decides whether to
    // spend a subprocess was written around "this file names no terminal", and a
    // background session's file can name one two different ways.

    [AvaloniaFact]
    public void ABackgroundSessionThatBorrowedASiblingsTerminalIsStillDimmed()
    {
        // Path one: InheritTerminalInfo donates terminal fields between files
        // that share a (pid, source), and the Agent-View-dispatched background
        // session is exactly that shape — dispatching one does not fork a
        // process, it starts a second conversation inside the `claude` process
        // already running. So the job's file *acquires* a terminal from its
        // interactive sibling before the phase question is put, and used to read
        // as an ordinary session from that point on.
        //
        // Both files name this process's own pid, which is the one pid on the
        // machine that is certainly alive — and the job's file is the *older* of
        // the two, which is the real shape rather than a convenience:
        // BackgroundJobs' own comment describes this session as one that "lost
        // SessionManager.Superseded's pid tie-break", and it survives that only
        // because the daemon still lists it. Written the other way round, the
        // *interactive* file is the one Superseded drops, which is a real
        // limitation of that rule and not what this test is about.
        // Two seconds, not two minutes: the default "Keep orbs for" is five
        // minutes, and an mtime older than that expires the orb before any of
        // this is reached — which is a rule this test has no business exercising.
        // All that is wanted is an ordering.
        using var scratch = new Scratch();
        WriteBackgroundFile(scratch, "dispatched-job",
            written: DateTime.UtcNow - TimeSpan.FromSeconds(2));
        scratch.Write("interactive", title: "the terminal");

        var manager = Manager(scratch, () => Listing(("dispatched-job", "blocked")));
        manager.ScanAndUpdate();

        var job = manager.StatusFor("dispatched-job");
        Assert.NotNull(job);

        // It did inherit — which is the point of the fixture, and what made the
        // old gate refuse to ask about it.
        Assert.Equal("iTerm.app", job!.TermProgram);

        Assert.Equal(LocalSessionShape.Background, job.Shape);
        Assert.Equal(OrbPresence.NeedsInput, job.Presence);
        Assert.Equal(SessionKind.Background, job.Kind);

        // And the sibling is untouched: a terminal session is never parked, and
        // sharing a pid with a job does not make it one.
        var sibling = manager.StatusFor("interactive");
        Assert.NotNull(sibling);
        Assert.Equal(LocalSessionShape.Terminal, sibling!.Shape);
        Assert.Equal(OrbPresence.Present, sibling.Presence);
    }

    [AvaloniaFact]
    public void ABackgroundSessionWhoseHookInheritedTermProgramIsStillDimmed()
    {
        // Path two, and the subtler one: the hook does not *decide* term_program,
        // it interpolates $TERM_PROGRAM out of its environment
        // (OrbweaverHook.sh). A daemon started from inside a terminal passes
        // that down to every job it hosts, so a background file can name a
        // terminal with no sibling and no inheritance involved at all. This
        // machine's daemon happens to be launchd-parented, which is why the
        // field was empty in the forensics this branch was built from.
        //
        // Closed by asking about every Claude Code session once the listing has
        // been paid for by *something* — here, the second file below, which is
        // the ordinary no-terminal background shape.
        using var scratch = new Scratch();

        File.WriteAllText(Path.Combine(scratch.Dir, "env-job.txt"),
            System.Text.Json.JsonSerializer.Serialize(new SessionStatus
            {
                State = "idle",
                SessionPid = LivePid,
                TermProgram = "iTerm.app",   // inherited by the daemon, not resolved
                Tty = "/dev/ttys004",
            }));

        WriteBackgroundFile(scratch, "plain-job");

        var manager = Manager(
            scratch, () => Listing(("env-job", "blocked"), ("plain-job", "blocked")));
        manager.ScanAndUpdate();

        var inherited = manager.StatusFor("env-job");
        Assert.NotNull(inherited);
        Assert.Equal(LocalSessionShape.Background, inherited!.Shape);
        Assert.Equal(OrbPresence.NeedsInput, inherited.Presence);
    }

    [AvaloniaFact]
    public void AMachineWithNothingBackgroundIshOnItNeverAsksTheDaemon()
    {
        // The other side of that, and the reason the gate exists at all: the
        // listing is a `claude agents --json` subprocess, and a machine with
        // nothing but terminal sessions must not spawn one every ten seconds
        // forever to be told what it already knows.
        //
        // Counted rather than inferred — the whole point is that the call does
        // not happen, and a test that only checked the outcome would pass just as
        // well if it happened and was ignored.
        using var scratch = new Scratch();
        scratch.Write("terminal-a");
        scratch.Write("terminal-b", cli: "codex");

        var asked = 0;
        var manager = Manager(scratch, () => { asked++; return Listing(); });
        manager.ScanAndUpdate();

        Assert.Equal(0, asked);

        // Unknown all round, which is the answer that means nobody asked — and
        // no rule acts on it, so nothing is dimmed and nothing is swept.
        Assert.Equal(OrbPresence.Present, manager.StatusFor("terminal-a")!.Presence);
        Assert.Equal(LocalSessionShape.Terminal, manager.StatusFor("terminal-a")!.Shape);
    }

    [AvaloniaFact]
    public void TheDaemonIsAskedAtMostOncePerScanHoweverManySessionsThereAre()
    {
        // One listing for the whole pass. The cache behind it is ten seconds
        // wide while the scan runs every two, so asking per rule could answer two
        // rules differently about one session in a single pass — which was
        // survivable while every consumer wanted the same bool, and is not now
        // that the same listing also decides whether an orb is dimmed.
        using var scratch = new Scratch();
        WriteBackgroundFile(scratch, "job-a");
        WriteBackgroundFile(scratch, "job-b");
        scratch.Write("terminal");

        var asked = 0;
        var manager = Manager(
            scratch,
            () => { asked++; return Listing(("job-a", "blocked"), ("job-b", "working")); });

        manager.ScanAndUpdate();

        Assert.Equal(1, asked);
        Assert.Equal(OrbPresence.NeedsInput, manager.StatusFor("job-a")!.Presence);
        Assert.Equal(OrbPresence.Present, manager.StatusFor("job-b")!.Presence);
    }

    // --- somebody is in it ---------------------------------------------------

    [AvaloniaFact]
    public void AParkedJobWithAnAttachClientIsDrawnAsPresent()
    {
        // The contradiction the user hit within minutes of the first build: they
        // ran `claude attach` on all three parked sessions, sat in them, and the
        // orbs stayed grey. Nothing this app was watching had changed — the
        // status file records the *worker's* ancestry and never the viewer's, so
        // the tty stays empty, and the daemon still says "blocked" because from
        // its side nothing happened. The person's presence exists in one place,
        // which is the process table.
        //
        // The client's argv is the short job id, exactly as `claude attach
        // 0e043819` writes it, which is why the match is by prefix.
        using var scratch = new Scratch();
        WriteBackgroundFile(scratch, "0e043819-3c45-4f1a-9c2b-8d4e5f6a7b8c");

        var manager = Manager(
            scratch,
            () => Listing(("0e043819-3c45-4f1a-9c2b-8d4e5f6a7b8c", "blocked")),
            attachClients: () => new HashSet<string>(StringComparer.Ordinal) { "0e043819" });

        manager.ScanAndUpdate();

        var status = manager.StatusFor("0e043819-3c45-4f1a-9c2b-8d4e5f6a7b8c");
        Assert.NotNull(status);
        Assert.Equal(LocalSessionShape.Background, status!.Shape);
        Assert.Equal(OrbPresence.Present, status.Presence);

        // Still a background job, and still badged as one: the gear says what the
        // session *is*, which somebody looking at it does not change.
        Assert.Equal(SessionKind.Background, status.Kind);
    }

    [AvaloniaFact]
    public void AnUnrelatedAttachClientDoesNotUndimAnything()
    {
        using var scratch = new Scratch();
        WriteBackgroundFile(scratch, "bg-parked");

        var manager = Manager(
            scratch, () => Listing(("bg-parked", "blocked")),
            attachClients: () => new HashSet<string>(StringComparer.Ordinal) { "5f6960b2" });

        manager.ScanAndUpdate();

        Assert.Equal(OrbPresence.NeedsInput, manager.StatusFor("bg-parked")!.Presence);
    }

    [AvaloniaFact]
    public void AProcessScanThatFailedLeavesEveryOrbAsItWas()
    {
        // Fail open, in the one direction that matters: a scan that could not be
        // done must not dim a session somebody is sitting in. Wrong the other way
        // leaves a genuinely parked orb bright, which is this branch's original
        // bug in its mildest form — and mild beats arguing with the person at the
        // screen.
        using var scratch = new Scratch();
        WriteBackgroundFile(scratch, "bg-parked");

        var manager = Manager(
            scratch, () => Listing(("bg-parked", "blocked")), attachClients: () => null);

        manager.ScanAndUpdate();

        Assert.Equal(OrbPresence.Present, manager.StatusFor("bg-parked")!.Presence);
    }

    [AvaloniaFact]
    public void TheProcessTableIsNotWalkedForAMachineWithNoBackgroundSessions()
    {
        // The scan costs a `ps`, so it is only paid for when the answer could
        // change a rendering — which is only when something here is a background
        // session at all. Counted rather than inferred, the same way the daemon
        // listing's own gate is.
        using var scratch = new Scratch();
        scratch.Write("terminal-a");

        var asked = 0;
        var manager = Manager(
            scratch, () => Listing(),
            attachClients: () => { asked++; return new HashSet<string>(StringComparer.Ordinal); });

        manager.ScanAndUpdate();

        Assert.Equal(0, asked);
    }

    // --- a finished job lingers ----------------------------------------------

    [AvaloniaFact]
    public void AFinishedJobIsDrawnAsFinishedRatherThanVanishing()
    {
        // Reported as a bug in exactly those words: "one appears and disappears
        // instead of greying out". A `done` job's orb used to go the instant the
        // daemon said so, while the user was looking at it — and a thing that
        // disappears without being dismissed reads as a fault rather than as a
        // finish.
        using var scratch = new Scratch();
        WriteBackgroundFile(scratch, "bg-finished");

        var manager = Manager(scratch, () => Listing(("bg-finished", "done")));
        manager.ScanAndUpdate();

        Assert.Contains("bg-finished", OrbIds(manager));

        var status = manager.StatusFor("bg-finished");
        Assert.Equal(LocalSessionShape.Background, status!.Shape);
        Assert.Equal(OrbPresence.Finished, status.Presence);
        Assert.Equal(SessionKind.Background, status.Kind);
    }

    [AvaloniaFact]
    public void DismissingAFinishedJobTakesItAwayImmediately()
    {
        // The user's own way out, for a finished orb they would rather not look
        // at for ten minutes. Deleting the file is the whole of it — and unlike
        // every other session, this one will not write itself back, because
        // nothing is running to fire a hook.
        using var scratch = new Scratch();
        WriteBackgroundFile(scratch, "bg-finished");

        var manager = Manager(scratch, () => Listing(("bg-finished", "done")));
        manager.ScanAndUpdate();
        Assert.Contains("bg-finished", OrbIds(manager));

        manager.DismissSession("bg-finished");
        manager.ScanAndUpdate();

        Assert.DoesNotContain("bg-finished", OrbIds(manager));
        Assert.False(File.Exists(Path.Combine(scratch.Dir, "bg-finished.txt")));
    }

    // A subagent, or a file that outlived its session: still dropped, and this is
    // the distinction the whole reversal turns on. "The listing was read and does
    // not name this" is a fact about the session; "the listing says it finished"
    // is a fact about a job that existed.
    [AvaloniaFact]
    public void ASessionTheListingDoesNotNameStillLosesItsOrb()
    {
        using var scratch = new Scratch();
        WriteBackgroundFile(scratch, "not-a-job");

        var manager = Manager(scratch, () => Listing(("something-else", "blocked")));
        manager.ScanAndUpdate();

        Assert.DoesNotContain("not-a-job", OrbIds(manager));
    }

    // --- the sweep ---

    // A pid that was never allocated: 2147483646 is far above any platform's
    // pid_max, so it cannot name a process on the machine running this. Used
    // rather than spawning and reaping a real child, which
    // ProcessLivenessTests does because it is asking about a *reaped* pid
    // specifically — here all that is wanted is a pid the kernel says nothing
    // is behind, and inventing one nothing can ever collide with is both
    // cheaper and safer. Nothing in this suite signals it.
    private const int NeverAllocatedPid = int.MaxValue - 1;

    [AvaloniaFact]
    public void ADeadPidsFileIsSweptOnceTheGraceHasPassed()
    {
        // The Ctrl+C case. SessionEnd never fired, so nothing has ever deleted
        // this file — the orb goes on the first scan and the file stayed for
        // good, on every machine, until this sweep existed.
        using var scratch = new Scratch();
        scratch.Write("ctrl-c-ed", pid: NeverAllocatedPid);
        var path = Path.Combine(scratch.Dir, "ctrl-c-ed.txt");

        var manager = Manager(scratch, () => Listing(), sweepGrace: TimeSpan.Zero);

        // The first pass records the sighting and deletes nothing, whatever the
        // grace is: evidence has to survive two consecutive scans before a file
        // goes, which is what a momentarily unreadable pid is protected by.
        manager.ScanAndUpdate();
        Assert.DoesNotContain("ctrl-c-ed", OrbIds(manager));
        Assert.True(File.Exists(path));

        manager.ScanAndUpdate();
        Assert.False(File.Exists(path));
    }

    [AvaloniaFact]
    public void AFinishedJobsFileIsSweptEvenThoughItsWorkerIsStillAlive()
    {
        // The case no liveness rule can ever reach, and the reason the sweep
        // consults the job phase at all: a finished job's pooled worker is kept
        // alive on purpose, so its pid answers forever. Before this, every job
        // anyone ever ran left a file behind permanently.
        using var scratch = new Scratch();
        WriteBackgroundFile(scratch, "bg-done");
        var path = Path.Combine(scratch.Dir, "bg-done.txt");

        var manager = Manager(scratch, () => Listing(("bg-done", "done")), sweepGrace: TimeSpan.Zero);

        manager.ScanAndUpdate();
        Assert.True(File.Exists(path));

        // Its orb is still there, and this is the reversal: a `done` job's orb
        // used to go the instant the daemon said so, and the user watched one
        // appear and vanish while looking at it — which reads as a fault rather
        // than as a finish. It now lingers, dimmed and marked as finished, for
        // exactly as long as its file does.
        Assert.Contains("bg-done", OrbIds(manager));
        Assert.Equal(OrbPresence.Finished, manager.StatusFor("bg-done")!.Presence);

        manager.ScanAndUpdate();
        Assert.False(File.Exists(path));

        // The orb follows one tick behind the file, not in the same pass: the
        // sweep runs *after* the removal pass, so by the time the file goes this
        // scan has already drawn the orb from it. Two seconds later there is no
        // file to draw and the orb goes the ordinary way — through the same
        // removal pass that handles a SessionEnd. Deliberately not special-cased
        // into the sweep: an orb removed by the thing that deletes files would be
        // a second removal path, and this one is late by one tick rather than
        // wrong.
        manager.ScanAndUpdate();
        Assert.DoesNotContain("bg-done", OrbIds(manager));
    }

    [AvaloniaFact]
    public void AJobThatComesBackRestartsTheGraceClock()
    {
        // Evidence that goes away resets the clock rather than being remembered.
        // Without this a job that finishes, is resumed, and finishes again would
        // be swept on the strength of the accumulated total of its quiet spells —
        // and a resumed job's file is the only place its identity lives.
        using var scratch = new Scratch();
        WriteBackgroundFile(scratch, "bg-flapping");
        var path = Path.Combine(scratch.Dir, "bg-flapping.txt");

        var state = "done";
        var manager = Manager(
            scratch, () => Listing(("bg-flapping", state)), sweepGrace: TimeSpan.Zero);

        manager.ScanAndUpdate();            // sighting recorded
        state = "working";
        manager.ScanAndUpdate();            // ...and forgotten again
        state = "done";
        manager.ScanAndUpdate();            // sighting recorded afresh

        Assert.True(File.Exists(path));

        // Only now, on a second consecutive sighting, does it go.
        manager.ScanAndUpdate();
        Assert.False(File.Exists(path));
    }

    [AvaloniaFact]
    public void AQuietSessionIsNeverSweptHoweverLongItHasBeenQuiet()
    {
        // Expiry is the user's own display setting, not evidence about the
        // session — and this is the one that would be a disaster: the file is
        // the only place a live session's terminal coordinates and colour live,
        // and the hook writes nothing more until its next event. Deleting it
        // would take the orb away and leave the session running.
        var before = OrbweaverSettings.OrbLifetimeMinutes;
        try
        {
            OrbweaverSettings.OrbLifetimeMinutes = 5;

            using var scratch = new Scratch();
            scratch.Write("quiet", written: DateTime.UtcNow - TimeSpan.FromHours(2));
            var path = Path.Combine(scratch.Dir, "quiet.txt");

            var manager = Manager(scratch, () => Listing(), sweepGrace: TimeSpan.Zero);
            manager.ScanAndUpdate();
            manager.ScanAndUpdate();

            // Orb gone, file kept.
            Assert.DoesNotContain("quiet", OrbIds(manager));
            Assert.True(File.Exists(path));
        }
        finally
        {
            OrbweaverSettings.OrbLifetimeMinutes = before;
        }
    }

    [AvaloniaFact]
    public void ADeadPidsFileSurvivesUntilTheGraceHasActuallyPassed()
    {
        // The default ten minutes, so the second sighting is not yet due. The
        // grace exists because the evidence can be momentarily wrong in one
        // direction that matters — a recycled or briefly unreadable pid — and ten
        // minutes of a file nobody is looking at costs nothing, while deleting a
        // file the app was wrong about costs the user their session's identity.
        using var scratch = new Scratch();
        scratch.Write("recently-dead", pid: NeverAllocatedPid);
        var path = Path.Combine(scratch.Dir, "recently-dead.txt");

        var manager = Manager(scratch, () => Listing());

        manager.ScanAndUpdate();
        manager.ScanAndUpdate();
        manager.ScanAndUpdate();

        Assert.True(File.Exists(path));
    }

    // --- orphaned team members ---

    // The green cluster on the screenshot: real claude processes in a detached
    // tmux socket, with status files carrying tmux fields, so KnowsATerminal
    // keeps them forever. Their arrows had already silently vanished with the
    // lead they pointed at — TeamLinks draws nothing to a lead that is not on
    // screen — which left the orb as the only thing still claiming anything
    // about them.
    //
    // Membership is seeded into AgentTeam's cache rather than read off a real
    // process, the same way the sibling test above does it: the real answer comes
    // from a pid's command line, which a test cannot arrange without spawning a
    // `claude` process carrying agent-team arguments.
    private static Dictionary<int, (AgentTeam.Membership Value, long Stamp)> TeamCache() =>
        (Dictionary<int, (AgentTeam.Membership Value, long Stamp)>)typeof(AgentTeam)
            .GetField("Cache", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    [AvaloniaFact]
    public void ATeamMemberWhoseLeadHasGoneIsDimmed()
    {
        var cache = TeamCache();
        lock (cache) cache.Clear();
        try
        {
            lock (cache)
            {
                cache[LivePid] = (
                    new AgentTeam.Membership("absent-lead", "QA", ""), Environment.TickCount64);
            }

            using var scratch = new Scratch();
            scratch.Write("orphan");

            var manager = Manager(scratch, () => Listing());
            manager.ScanAndUpdate();

            var status = manager.StatusFor("orphan");
            Assert.NotNull(status);
            Assert.Equal("absent-lead", status!.Lead);
            Assert.Equal(LocalSessionShape.Teammate, status.Shape);

            // Dimmed with no mark, deliberately: nothing is waiting on the user
            // and nothing has finished, so there is nothing to say beyond the
            // dimming. The two marks belong to the daemon's own vocabulary, and
            // the daemon has never heard of this session.
            Assert.Equal(OrbPresence.Parked, status.Presence);

            // Dimmed, not removed — for the same reason a parked job is kept.
            Assert.Contains("orphan", OrbIds(manager));
        }
        finally
        {
            lock (cache) cache.Clear();
        }
    }

    [AvaloniaFact]
    public void ATeamMemberWhoseLeadIsOnScreenIsNotDimmed()
    {
        // The other half, and the one that would be the worse failure: dimming a
        // live team says four agents at work are doing nothing.
        //
        // Two files on one pid without colliding in Superseded, because they name
        // different CLIs — the trick the sibling tests use. AgentTeam.Of caches
        // by pid alone, so the one seeded membership answers for both: the Codex
        // entry never consults it (Lead is only derived for Claude Code), and the
        // Claude Code entry reads its lead out of it.
        var cache = TeamCache();
        lock (cache) cache.Clear();
        try
        {
            lock (cache)
            {
                cache[LivePid] = (
                    new AgentTeam.Membership("present-lead", "QA", ""), Environment.TickCount64);
            }

            using var scratch = new Scratch();
            scratch.Write("present-lead", cli: "codex");
            scratch.Write("member");

            var manager = Manager(scratch, () => Listing());
            manager.ScanAndUpdate();

            var status = manager.StatusFor("member");
            Assert.NotNull(status);
            Assert.Equal(LocalSessionShape.Teammate, status!.Shape);
            Assert.Equal(OrbPresence.Present, status.Presence);
        }
        finally
        {
            lock (cache) cache.Clear();
        }
    }

    // --- dismiss and end ---

    [AvaloniaFact]
    public void DismissingAnOrbDeletesItsFileAndTheNextScanTakesTheOrb()
    {
        // Deleting the file is the whole of it: in the app the watcher's Deleted
        // event drives the debounced rescan, which is driven directly here for
        // the reason this suite never calls Start().
        using var scratch = new Scratch();
        scratch.Write("dismiss-me");

        var manager = Scan(scratch);
        Assert.Contains("dismiss-me", OrbIds(manager));

        manager.DismissSession("dismiss-me");
        Assert.False(File.Exists(Path.Combine(scratch.Dir, "dismiss-me.txt")));

        manager.ScanAndUpdate();
        Assert.DoesNotContain("dismiss-me", OrbIds(manager));
        Assert.Null(manager.StatusFor("dismiss-me"));
    }

    [AvaloniaFact]
    public void DismissingASessionThatLivesSomewhereElseTouchesNothing()
    {
        // A gateway session's orb comes from a socket, and the path this would
        // build from its namespaced key is not one this app should write to.
        // Seeded through _statuses directly rather than by standing up a gateway,
        // the same way OrbIds reaches _windows: what is being tested is the
        // guard, and the guard reads exactly this dictionary.
        using var scratch = new Scratch();
        var manager = Manager(scratch, () => Listing());

        var statuses = (Dictionary<string, SessionStatus>)typeof(SessionManager)
            .GetField("_statuses", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;

        const string key = "openclaw:agent:main:discord:channel:1";
        statuses[key] = new SessionStatus { Source = SessionSource.OpenClaw };

        // A file named to prove the guard returns before any delete: were the
        // key spliced into a path, this is what it would have found.
        var decoy = Path.Combine(scratch.Dir, "decoy.txt");
        File.WriteAllText(decoy, "{}");

        manager.DismissSession(key);

        Assert.True(File.Exists(decoy));
    }

    [AvaloniaFact]
    public void TheSweepForgetsAnIdWhoseFileGoesAwayByOtherMeans()
    {
        // A file can leave the directory while the sweep is still counting down
        // on it: SessionEnd fires, or the user dismisses the orb. Without this
        // the dictionary keeps the id for the life of the process — a slow leak
        // keyed on every session the app has ever seen die, and a stale clock if
        // the same id ever came back.
        //
        // The default ten-minute grace, deliberately, so nothing is swept and
        // the only thing under test is the forgetting.
        using var scratch = new Scratch();
        scratch.Write("gone-elsewhere", pid: NeverAllocatedPid);

        var manager = Manager(scratch, () => Listing());
        manager.ScanAndUpdate();

        var deadSince = (Dictionary<string, DateTime>)typeof(SessionManager)
            .GetField("_deadSince", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;

        Assert.Contains("gone-elsewhere", deadSince.Keys);

        scratch.Delete("gone-elsewhere");
        manager.ScanAndUpdate();

        Assert.Empty(deadSince);
    }

    [AvaloniaFact]
    public void AStatusFileThatCannotBeDeletedIsLeftAloneRatherThanThrowing()
    {
        // Both the sweep and Dismiss go through one delete that swallows its
        // failure, because there is nowhere to report one: a file this process
        // may not delete is simply found again on the next scan.
        //
        // Provoked with a read-only status directory, which is a POSIX
        // permission — on Windows a read-only directory does not stop a delete,
        // so there is no equivalent to arrange and this returns instead of
        // asserting something untrue. That leaves the catch measured on the
        // macOS and Linux legs and unmeasured on the Windows one, which is the
        // same shape as every other platform split in this repo.
        if (OperatingSystem.IsWindows()) return;

        using var scratch = new Scratch();
        scratch.Write("undeletable");
        var path = Path.Combine(scratch.Dir, "undeletable.txt");

        var manager = Scan(scratch);
        Assert.Contains("undeletable", OrbIds(manager));

        // Readable and listable, not writable: the scan can still read the file,
        // and only the unlink fails.
        File.SetUnixFileMode(scratch.Dir,
            UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            manager.DismissSession("undeletable");

            Assert.True(File.Exists(path));
        }
        finally
        {
            File.SetUnixFileMode(scratch.Dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // ...and the orb is still there on the next scan, which is the point of
        // failing this way rather than optimistically removing it.
        manager.ScanAndUpdate();
        Assert.Contains("undeletable", OrbIds(manager));
    }

    [AvaloniaFact]
    public void DismissingAnIdTheManagerHasNeverSeenIsHarmless()
    {
        // No status, so no guard to consult — it falls through to a delete of a
        // file that is not there, which is the catch's whole job.
        using var scratch = new Scratch();
        var manager = Manager(scratch, () => Listing());

        manager.DismissSession("never-heard-of-it");
    }

    [AvaloniaFact]
    public async Task EndingASessionRefusesEveryShapeItCannotEnd()
    {
        // The one irreversible thing in the app, so what is asserted here is the
        // refusals. An id with no status at all, and a session with no pid
        // recorded (a hook older than that field) — neither reaches
        // SessionTerminator, which is what makes this safe to run at all.
        using var scratch = new Scratch();
        var manager = Manager(scratch, () => Listing());

        var statuses = (Dictionary<string, SessionStatus>)typeof(SessionManager)
            .GetField("_statuses", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;

        statuses["pidless"] = new SessionStatus { Source = SessionSource.ClaudeCode, SessionPid = 0 };
        statuses["gateway"] = new SessionStatus { Source = SessionSource.OpenClaw, SessionPid = 4321 };

        await manager.EndSession("never-heard-of-it");
        await manager.EndSession("pidless");
        await manager.EndSession("gateway");
    }

    [AvaloniaFact]
    public async Task EndingASessionSignalsThePidTheStatusNames()
    {
        // The one path that does reach SessionTerminator, driven with a pid
        // nothing can be behind: 2147483646 is far above any platform's pid_max,
        // so the signal lands on ESRCH (or, on Windows, an ArgumentException from
        // GetProcessById) and is swallowed — which is also the documented
        // "already gone counts as success" behaviour.
        //
        // This is deliberately not driven with a real pid. A test that ended a
        // real process on the machine running it would be a worse bug than the
        // one this ticket is about, and the machine this suite runs on has the
        // user's own background sessions on it.
        using var scratch = new Scratch();
        var manager = Manager(scratch, () => Listing());

        var statuses = (Dictionary<string, SessionStatus>)typeof(SessionManager)
            .GetField("_statuses", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;

        statuses["ends"] = new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            SessionPid = NeverAllocatedPid,
        };

        await manager.EndSession("ends");

        // Nothing is removed from screen by the call itself — the orb goes when
        // the next scan sees the pid stop answering, which is the same path any
        // other ending session takes.
        Assert.Null(manager.StatusFor("no-orb-was-touched"));
    }

    // CB-26. A pid with a live `claude daemon run` underneath it is the husk a
    // backgrounded turn left behind: it is the window the user is reading a
    // running job in, and on Windows the tree kill would take the daemon and
    // every other job on the machine with it.
    //
    // What is asserted is the refusal and its arithmetic, not the syscall — the
    // pid handed over is one nothing can be behind, so a guard that failed to
    // refuse would still not end anything on this machine. The seam is what
    // makes the case constructible at all: asking the real process table would
    // be asking about whichever daemon happens to be running beside the suite.
    [AvaloniaFact]
    public async Task EndingASessionThatIsHostingTheDaemonIsRefused()
    {
        using var scratch = new Scratch();

        // Which thread each read ran on, by managed id rather than by asking
        // Dispatcher.UIThread from the pool — CB-183 is what reading that
        // property off a stray thread once cost this suite.
        var uiThread = Environment.CurrentManagedThreadId;
        var asked = new List<int>();
        var askedOn = new List<int>();
        var manager = Manager(scratch, () => Listing(), dependents: pid =>
        {
            lock (asked)
            {
                asked.Add(pid);
                askedOn.Add(Environment.CurrentManagedThreadId);
            }
            return new SessionDependents.Verdict(DaemonBelow: true, JobsBelow: 3);
        });

        var statuses = (Dictionary<string, SessionStatus>)typeof(SessionManager)
            .GetField("_statuses", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;

        statuses["husk"] = new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            SessionPid = NeverAllocatedPid,
        };

        await manager.EndSession("husk");

        // Asked about the session's own pid, and only that one — the whole
        // point of the rule is that this pid is not the ordinary case, so a
        // guard that asked about the terminal's pid instead would be answering
        // a different question correctly.
        Assert.Equal(new[] { NeverAllocatedPid }, asked);

        // And the menu row for it says so, from the same rule.
        var verdict = await manager.DependentsOfAsync("husk");
        Assert.True(SessionDependents.BlocksTermination(verdict));
        Assert.Equal("Can't end this: it is your view of 3 background jobs",
            SessionDependents.Explain(verdict));

        // CB-228: neither read ran on the UI thread. On Windows each is a WMI
        // query over the whole process table, 218-273 ms measured, and both the
        // right-click and the End click used to wait for it here.
        Assert.Equal(2, askedOn.Count);
        Assert.DoesNotContain(uiThread, askedOn);
    }

    // The other half of the acceptance, and the half the guard is most likely to
    // break: an ordinary session with nothing underneath it still reaches the
    // terminator exactly as it did before.
    [AvaloniaFact]
    public async Task EndingAnOrdinarySessionStillAsksAndStillProceeds()
    {
        using var scratch = new Scratch();

        var asked = new List<int>();
        var manager = Manager(scratch, () => Listing(), dependents: pid =>
        {
            asked.Add(pid);
            return SessionDependents.Nothing;
        });

        var statuses = (Dictionary<string, SessionStatus>)typeof(SessionManager)
            .GetField("_statuses", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;

        statuses["ordinary"] = new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            SessionPid = NeverAllocatedPid,
        };

        await manager.EndSession("ordinary");

        Assert.Equal(new[] { NeverAllocatedPid }, asked);
        Assert.Equal("End this session",
            SessionDependents.Explain(await manager.DependentsOfAsync("ordinary")));
    }

    // --- CB-228: the read and the kill run off the UI thread ----------------
    //
    // Thread identity is compared by managed id, never by asking
    // Dispatcher.UIThread from a pool thread — CB-183 is what reading that
    // property off a stray thread once cost this suite.

    private static Dictionary<string, SessionStatus> StatusesOf(SessionManager manager) =>
        (Dictionary<string, SessionStatus>)typeof(SessionManager)
            .GetField("_statuses", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;

    private static SessionStatus Endable(int pid = NeverAllocatedPid) =>
        new() { Source = SessionSource.ClaudeCode, SessionPid = pid };

    // End to end from the orb: the End row's click goes through the orb's
    // manager, the guard reads on a pool thread, and the kill is handed the
    // pid on a pool thread too. The kill is the seam, so nothing is signalled.
    [AvaloniaFact]
    public async Task TheEndClickReadsAndKillsOffTheUiThread()
    {
        using var scratch = new Scratch();

        var uiThread = Environment.CurrentManagedThreadId;
        var readOn = -1;
        var killed = new TaskCompletionSource<(int Pid, int Thread)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = Manager(scratch, () => Listing(),
            dependents: _ =>
            {
                readOn = Environment.CurrentManagedThreadId;
                return SessionDependents.Nothing;
            },
            terminate: (pid, _) => killed.TrySetResult((pid, Environment.CurrentManagedThreadId)));

        StatusesOf(manager)["clicked"] = Endable();
        var orb = new OrbWindow("clicked") { CurrentManager = () => manager };

        orb.EndSession_Click(null, new Avalonia.Interactivity.RoutedEventArgs());

        var (pid, killOn) = await killed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(NeverAllocatedPid, pid);
        Assert.NotEqual(uiThread, readOn);
        Assert.NotEqual(uiThread, killOn);
    }

    // The husk is still refused after the move — and refused before the kill
    // is reached, not by the kill's own belt-and-braces check.
    [AvaloniaFact]
    public async Task ARefusedSessionIsNeverHandedToTheKill()
    {
        using var scratch = new Scratch();

        var kills = 0;
        var manager = Manager(scratch, () => Listing(),
            dependents: _ => new SessionDependents.Verdict(DaemonBelow: true, JobsBelow: 2),
            terminate: (_, _) => Interlocked.Increment(ref kills));
        StatusesOf(manager)["husk"] = Endable();

        await manager.EndSession("husk");

        Assert.Equal(0, kills);
    }

    // The re-check the move made necessary. While the read runs, a scan can
    // drop the session or give its id a new pid; the kill must not then land
    // on the pid the click saw. The gate holds the read open while the test
    // changes the status underneath it, on the UI thread, as a scan would.
    [AvaloniaTheory]
    [InlineData("dropped")]
    [InlineData("new pid")]
    [InlineData("no longer endable")]
    public async Task ASessionThatChangedDuringTheReadIsNotKilled(string change)
    {
        using var scratch = new Scratch();

        using var gate = new ManualResetEventSlim(false);
        var kills = 0;
        var manager = Manager(scratch, () => Listing(),
            dependents: _ =>
            {
                Assert.True(gate.Wait(TimeSpan.FromSeconds(10)));
                return SessionDependents.Nothing;
            },
            terminate: (_, _) => Interlocked.Increment(ref kills));
        var statuses = StatusesOf(manager);
        statuses["moving"] = Endable();

        var ending = manager.EndSession("moving");
        Assert.False(ending.IsCompleted);

        switch (change)
        {
            case "dropped": statuses.Remove("moving"); break;
            case "new pid": statuses["moving"] = Endable(NeverAllocatedPid - 1); break;
            default: statuses["moving"] = new SessionStatus { Source = SessionSource.OpenClaw, SessionPid = NeverAllocatedPid }; break;
        }

        gate.Set();
        await ending;

        Assert.Equal(0, kills);
    }

    // A read that throws signals nothing: the failure escapes before the kill
    // is reached, the direction an irreversible action should fail in.
    [AvaloniaFact]
    public async Task AReadThatThrowsEndsNothing()
    {
        using var scratch = new Scratch();

        var kills = 0;
        var manager = Manager(scratch, () => Listing(),
            dependents: _ => throw new InvalidOperationException("no WMI"),
            terminate: (_, _) => Interlocked.Increment(ref kills));
        StatusesOf(manager)["broken"] = Endable();

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EndSession("broken"));
        Assert.Equal(0, kills);
    }

    // QA's finding: two Ends for one session, the second while the first is
    // still reading, used to both pass StillEndable and kill twice. The second
    // is now a no-op while the first is in flight.
    [AvaloniaFact]
    public async Task ADoubleEndKillsOnce()
    {
        using var scratch = new Scratch();

        var kills = 0;
        var manager = Manager(scratch, () => Listing(),
            dependents: _ => { Thread.Sleep(150); return SessionDependents.Nothing; },
            terminate: (_, _) => Interlocked.Increment(ref kills));
        StatusesOf(manager)["dbl"] = Endable();

        var first = manager.EndSession("dbl");
        var second = manager.EndSession("dbl");
        await Task.WhenAll(first, second);

        Assert.Equal(1, kills);

        // And the guard is released once the first finishes: a later End —
        // the next gesture, not a double click — is not swallowed by it.
        await manager.EndSession("dbl");
        Assert.Equal(2, kills);
    }

    // --- CB-264: a background job is stopped by its daemon -----------------
    //
    // Signalling a parked job's worker only makes the daemon resume it on a new
    // pid, which is the orb the user ended coming straight back. These pin the
    // routing; which job id the daemon is handed is BackgroundJobsStopTests'.

    private static SessionStatus EndableJob(int pid = NeverAllocatedPid) =>
        new() { Source = SessionSource.ClaudeCode, SessionPid = pid, TranscriptPath = "/x/projects/p/job-1.jsonl" };

    [AvaloniaFact]
    public async Task AParkedJobIsStoppedByItsDaemonAndNeverSignalled()
    {
        using var scratch = new Scratch();

        var uiThread = Environment.CurrentManagedThreadId;
        var stops = new List<(string Session, string? Transcript, int Thread)>();
        var kills = 0;
        var manager = Manager(scratch, () => Listing(("job-1", "blocked")),
            terminate: (_, _) => Interlocked.Increment(ref kills),
            stopJob: (session, transcript) =>
            {
                lock (stops) stops.Add((session, transcript, Environment.CurrentManagedThreadId));
                return true;
            });
        StatusesOf(manager)["job-1"] = EndableJob();

        await manager.EndSession("job-1");

        var stop = Assert.Single(stops);
        Assert.Equal("job-1", stop.Session);
        Assert.Equal("/x/projects/p/job-1.jsonl", stop.Transcript);
        Assert.NotEqual(uiThread, stop.Thread);
        Assert.Equal(0, kills);
    }

    // A daemon that could not be asked, or did not know the job, leaves the
    // session to the signal it always got — never to nothing.
    [AvaloniaFact]
    public async Task AJobTheDaemonDidNotStopFallsBackToTheSignal()
    {
        using var scratch = new Scratch();

        var stops = 0;
        var killed = new List<int>();
        var manager = Manager(scratch, () => Listing(("job-1", "blocked")),
            terminate: (pid, _) => { lock (killed) killed.Add(pid); },
            stopJob: (_, _) => { Interlocked.Increment(ref stops); return false; });
        StatusesOf(manager)["job-1"] = EndableJob();

        await manager.EndSession("job-1");

        Assert.Equal(1, stops);
        Assert.Equal(new[] { NeverAllocatedPid }, killed);
    }

    // A listing that names the session as nothing is a terminal session, and
    // costs the click no subprocess.
    [AvaloniaFact]
    public async Task ATerminalSessionNeverAsksTheDaemon()
    {
        using var scratch = new Scratch();

        var stops = 0;
        var kills = 0;
        var manager = Manager(scratch, () => Listing(("someone-else", "blocked")),
            terminate: (_, _) => Interlocked.Increment(ref kills),
            stopJob: (_, _) => { Interlocked.Increment(ref stops); return true; });
        StatusesOf(manager)["terminal"] = EndableJob();

        await manager.EndSession("terminal");

        Assert.Equal(0, stops);
        Assert.Equal(1, kills);
    }

    // No listing at all says nothing about the session, so the daemon is still
    // asked — Stop reads a fresh listing of its own before it acts.
    [AvaloniaFact]
    public async Task AnUnreadableListingStillAsksTheDaemon()
    {
        using var scratch = new Scratch();

        var stops = 0;
        var kills = 0;
        var manager = Manager(scratch, () => null,
            terminate: (_, _) => Interlocked.Increment(ref kills),
            stopJob: (_, _) => { Interlocked.Increment(ref stops); return true; });
        StatusesOf(manager)["job-1"] = EndableJob();

        await manager.EndSession("job-1");

        Assert.Equal(1, stops);
        Assert.Equal(0, kills);
    }

    // The re-check, again, for the new hop: a stop that failed while a scan
    // gave the id a new pid must not fall back to signalling the pid the click
    // saw.
    [AvaloniaFact]
    public async Task ASessionThatChangedDuringTheStopIsNotKilled()
    {
        using var scratch = new Scratch();

        using var gate = new ManualResetEventSlim(false);
        using var asked = new ManualResetEventSlim(false);
        var kills = 0;
        var manager = Manager(scratch, () => Listing(("job-1", "blocked")),
            terminate: (_, _) => Interlocked.Increment(ref kills),
            stopJob: (_, _) =>
            {
                asked.Set();
                Assert.True(gate.Wait(TimeSpan.FromSeconds(10)));
                return false;
            });
        var statuses = StatusesOf(manager);
        statuses["job-1"] = EndableJob();

        var ending = manager.EndSession("job-1");
        await Task.Run(() => Assert.True(asked.Wait(TimeSpan.FromSeconds(10))));

        statuses["job-1"] = EndableJob(NeverAllocatedPid - 1);
        gate.Set();
        await ending;

        Assert.Equal(0, kills);
    }

    // The click observes its task: a read that throws is written to crash.log
    // under the gesture's name, rather than vanishing into a discarded task.
    [AvaloniaFact]
    public async Task AFailedEndFromTheClickIsWrittenToTheCrashLog()
    {
        using var scratch = new Scratch();
        var logDir = Path.Combine(scratch.Dir, "logs");
        using var scope = CrashLog.ScopeForTests(logDir);

        var kills = 0;
        var manager = Manager(scratch, () => Listing(),
            dependents: _ => throw new InvalidOperationException("WMI said no"),
            terminate: (_, _) => Interlocked.Increment(ref kills));
        StatusesOf(manager)["failing"] = Endable();
        var orb = new OrbWindow("failing") { CurrentManager = () => manager };

        await orb.EndSessionRecordingFailureAsync();

        Assert.Equal(0, kills);
        var log = File.ReadAllText(CrashLog.Path_);
        Assert.Contains("End this session", log);
        Assert.Contains("WMI said no", log);
    }

    // The menu's real Opening handler, against a manager the orb was given: the
    // row starts checking, the read is the manager's, on a pool thread, and the
    // husk's refusal lands on the row once it answers.
    [AvaloniaFact]
    public async Task OpeningTheMenuAsksTheManagerOffTheUiThread()
    {
        using var scratch = new Scratch();

        var uiThread = Environment.CurrentManagedThreadId;
        using var gate = new ManualResetEventSlim(false);
        var readOn = -1;
        var manager = Manager(scratch, () => Listing(), dependents: _ =>
        {
            readOn = Environment.CurrentManagedThreadId;
            Assert.True(gate.Wait(TimeSpan.FromSeconds(10)));
            return new SessionDependents.Verdict(DaemonBelow: true, JobsBelow: 2);
        });
        StatusesOf(manager)["opened"] = Endable();

        var orb = new OrbWindow("opened") { CurrentManager = () => manager };
        orb.UpdateFrom(new SessionStatus { Source = SessionSource.ClaudeCode, State = "idle", SessionPid = NeverAllocatedPid });
        var row = orb.FindControl<MenuItem>("EndSessionItem")!;

        orb.SessionMenu_Opening(null, new System.ComponentModel.CancelEventArgs());

        // The handler has returned, with the read still held open by the gate
        // — which is only possible because it is not waiting for it.
        Assert.False(row.IsEnabled);
        Assert.Equal(SessionDependents.CheckingHeader, row.Header);

        gate.Set();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Equals(row.Header, SessionDependents.CheckingHeader) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        Assert.NotEqual(uiThread, readOn);
        Assert.False(row.IsEnabled);
        Assert.Equal("Can't end this: it is your view of 2 background jobs", row.Header);
    }

    // A session the menu would never offer the row for is not asked about at
    // all. Cheap to get wrong in the direction that costs a `ps` of the whole
    // process table every time a gateway orb's menu opens, for a row that is
    // not on it.
    [AvaloniaFact]
    public async Task ASessionThatCannotBeEndedIsNotAskedWhatIsUnderneathIt()
    {
        using var scratch = new Scratch();

        var asked = 0;
        var manager = Manager(scratch, () => Listing(), dependents: _ =>
        {
            asked++;
            return SessionDependents.Nothing;
        });

        var statuses = (Dictionary<string, SessionStatus>)typeof(SessionManager)
            .GetField("_statuses", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;

        statuses["pidless"] = new SessionStatus { Source = SessionSource.ClaudeCode, SessionPid = 0 };
        statuses["gateway"] = new SessionStatus { Source = SessionSource.OpenClaw, SessionPid = 4321 };

        Assert.Equal(SessionDependents.Nothing, await manager.DependentsOfAsync("pidless"));
        Assert.Equal(SessionDependents.Nothing, await manager.DependentsOfAsync("gateway"));
        Assert.Equal(SessionDependents.Nothing, await manager.DependentsOfAsync("never-heard-of-it"));

        await manager.EndSession("pidless");
        await manager.EndSession("gateway");

        Assert.Equal(0, asked);
    }

    // --- PaneClaimsByOthers -----------------------------------------------

    // Seeded straight into _statuses rather than scanned in from files, and the
    // reason is the thing this method exists to work around. Several sessions with
    // *different* recorded panes is the whole point of the rule, and a test cannot
    // write that through a scan: the only pid a test can be sure is alive is its
    // own, so every scratch file would share one — and InheritTerminalInfo
    // deliberately donates terminal fields between files sharing a pid, so the two
    // sessions arrive holding the same pane and the fixture destroys itself. The
    // dictionary is already reached this way by the tests above; what is asserted
    // here is the projection over it, which is all this method is.
    private static SessionManager Seeded(
        Scratch scratch, params (string Id, string Pane)[] sessions)
    {
        var manager = Manager(scratch);
        var statuses = Statuses(manager);

        foreach (var (id, pane) in sessions)
        {
            statuses[id] = new SessionStatus
            {
                Cwd = "/Users/user/project",
                TmuxPane = pane,
                Title = "conversation " + id,
            };
        }

        return manager;
    }

    // The disambiguator the pane-title viewer scan needs. A conversation title is
    // shared by every member of an agent team, so several panes can match one
    // session's title — four panes with one identical title for three sessions, on
    // the machine this was built for. A pane another session's file claims is the
    // one most likely to be one of those, and excluding it costs nothing: that
    // session is reached by its own recorded coordinates long before the viewer
    // path is asked.
    [AvaloniaFact]
    public void ThePanesOtherSessionsClaimAreListedAndThisOnesIsNot()
    {
        using var scratch = new Scratch();

        var claimed = Seeded(scratch, ("mine", "%6"), ("theirs", "%53"), ("also", "%21"))
            .PaneClaimsByOthers("mine");

        Assert.Contains("%53", claimed.Keys);
        Assert.Contains("%21", claimed.Keys);

        // Its own pane is not "claimed by another" — the point is to exclude panes
        // belonging to other conversations, and a session must never exclude
        // itself from its own candidates.
        Assert.DoesNotContain("%6", claimed.Keys);
    }

    // A session that records no pane contributes nothing, rather than an empty
    // string that would then match a pane id nobody has.
    [AvaloniaFact]
    public void ASessionWithNoPaneClaimsNothing()
    {
        using var scratch = new Scratch();

        var claimed = Seeded(scratch, ("mine", "%6"), ("paneless", ""))
            .PaneClaimsByOthers("mine");

        Assert.Empty(claimed);
    }

    // No id given — the shape the chat panel's button can produce — means every
    // recorded pane counts as another session's. That is the conservative
    // direction: with nothing to exclude itself by, the viewer scan should decline
    // to focus a pane rather than guess which one is its own.
    [AvaloniaFact]
    public void WithNoSessionIdEveryRecordedPaneCountsAsAnothers()
    {
        using var scratch = new Scratch();

        var claimed = Seeded(scratch, ("one", "%6"), ("two", "%53"))
            .PaneClaimsByOthers(null);

        Assert.Equal(2, claimed.Count);
    }

    // The claimant's own title comes back with the pane, because a claim is only
    // worth honouring while the pane still shows that conversation — the scan
    // pairs this with the pane's current title. See
    // SessionPresence.ClaimStillHolds.
    [AvaloniaFact]
    public void EachClaimCarriesTheClaimantsOwnTitle()
    {
        using var scratch = new Scratch();

        var claimed = Seeded(scratch, ("mine", "%6"), ("theirs", "%53"))
            .PaneClaimsByOthers("mine");

        Assert.Equal("conversation theirs", claimed["%53"]);
    }

    // An untitled claimant still claims, with an empty title — which
    // ClaimStillHolds reads as "no evidence either way" and honours. A null title
    // must not reach it as a null.
    [AvaloniaFact]
    public void AnUntitledClaimantClaimsWithAnEmptyTitle()
    {
        using var scratch = new Scratch();
        var manager = Manager(scratch);
        Statuses(manager)["theirs"] = new SessionStatus { TmuxPane = "%6", Title = null! };

        Assert.Equal("", manager.PaneClaimsByOthers("mine")["%6"]);
    }

    // --- AcknowledgeClickOn --------------------------------------------------

    // The hop the chat panel's button needs: it knows its session id and not its
    // orb, and it has to acknowledge for the same reason the click does, since the
    // two share one destination through RunFallback.
    [AvaloniaFact]
    public void TheOrbForASessionAcknowledgesThroughTheManager()
    {
        using var scratch = new Scratch();
        scratch.Write("live");

        var manager = Scan(scratch);
        var orb = WindowFor(manager, "live");

        Assert.False(orb.IsAcknowledging);

        manager.AcknowledgeClickOn("live");

        Assert.True(orb.IsAcknowledging);
    }

    // Silent for a session with no orb — one can be typed at from the panel after
    // its orb has gone, and there is nothing to flash — and for no id at all.
    [AvaloniaTheory]
    [InlineData("no-such-session")]
    [InlineData("")]
    [InlineData(null)]
    public void AcknowledgingASessionWithNoOrbIsSilent(string? sessionId)
    {
        using var scratch = new Scratch();

        Manager(scratch).AcknowledgeClickOn(sessionId);
    }

    // --- this app's own relay gets no orb ------------------------------------

    // The relay is a Claude Code session like any other — its hook fires, it
    // writes a status file — so the scan drew it a grey orb with an empty chat
    // behind it. Correct and useless: it is plumbing, not a conversation, and it
    // hid inside the dead-orb noise the earlier rounds cleared out.
    //
    // Asserted at the scan, which is where it is dropped, and that placement is
    // the point rather than an implementation detail: nothing downstream ever
    // learns the session exists, so there is no orb to click, no tray row, and no
    // right-click menu with End and Dismiss on it to be pointed at something the
    // user did not start.
    [AvaloniaFact]
    public void ThisAppsOwnRelayIsNotGivenAnOrb()
    {
        using var scratch = new Scratch();
        scratch.Write("ordinary", cwd: "/Users/user/Source/Claude-Buddy");
        scratch.Write("relay", cwd: "/Users/w/rc-cwd/claude-buddy-rc--claude-host-mbp");

        var ids = OrbIds(Scan(scratch));

        Assert.Contains("ordinary", ids);
        Assert.DoesNotContain("relay", ids);
    }

    // Every relay this app has ever launched, not only the one whose name it
    // would build today: a different account directory or a renamed Mac changes
    // the tail of the name, and the still-running relay from before must not
    // reappear as an orb. That is the prefix's whole job — see
    // MachineNames.IsRelayName.
    [AvaloniaFact]
    public void ARelayFromAnEarlierProfileOrMachineIsAlsoSuppressed()
    {
        using var scratch = new Scratch();
        scratch.Write("stale", cwd: "/Users/w/rc-cwd/claude-buddy-rc--claude-board-some-other-mac");

        Assert.Empty(OrbIds(Scan(scratch)));
    }

    // And the line that has to hold: a person's own remote-control session is not
    // this app's plumbing, and silently hiding it would be worse than the orb this
    // round removes.
    [AvaloniaFact]
    public void SomebodyElsesRemoteSessionKeepsItsOrb()
    {
        using var scratch = new Scratch();
        scratch.Write("theirs", cwd: "/Users/user/Source/job-hunter");

        Assert.Contains("theirs", OrbIds(Scan(scratch)));
    }

    // --- CLIs this app started for itself -----------------------------------

    // The live scan's own copy of the internal-session filter, which is the one
    // the screen is drawn from.
    //
    // Covered here as well as through HeadlessSnapshot because they are two
    // separate lines in two separate methods, and only this one reaches a
    // window. The mirror's copy being right says nothing about the copy a user
    // sees, and a filter wired into only one of them would look exactly like
    // this feature working — right up until somebody watched their own screen.
    // The pair below is the same shape as the relay's directly above, for the
    // same reason: a rule that hid everything would pass the first of them on
    // its own.
    [AvaloniaFact]
    public void TheSummariserThisAppSpawnedGetsNoOrb()
    {
        using var scratch = new Scratch();
        scratch.Write("summariser");

        // Scratch.Write records this process's pid, which is the one pid on the
        // machine that is certainly alive — so the liveness rule keeps the
        // session and the drop under test is the only thing that can remove it.
        // Cleared in a finally because the set is process-wide: every scan test
        // writes this same pid, and leaving it claimed would empty theirs too.
        InternalSessions.Clear();
        InternalSessions.Remember(LivePid);
        try
        {
            Assert.Empty(OrbIds(Scan(scratch)));
        }
        finally
        {
            InternalSessions.Clear();
        }
    }

    // The direction that matters more: an ordinary session of identical shape,
    // which this app did not start, still gets its orb.
    [AvaloniaFact]
    public void AnIdenticalSessionThisAppDidNotStartKeepsItsOrb()
    {
        using var scratch = new Scratch();
        scratch.Write("theirs");

        InternalSessions.Clear();

        Assert.Contains("theirs", OrbIds(Scan(scratch)));
    }

    // --- CB-255 §1: the legacy status folder, watched beside the new one ---
    //
    // A session alive across the upgrade still calls the pre-Orbweaver hook,
    // which still writes the pre-Orbweaver folder. The live scan reads both
    // and keeps one file per session, newest first; the rule itself is
    // NewestPerSession, covered in tests/UnitTests/StatusFolderMergeTests.
    // These prove the scan is wired to it, and that the two things that write
    // or delete a session's file by id find the file where it actually is.

    private static SessionManager ScanBoth(Scratch current, Scratch legacy)
    {
        OrbweaverSettings.ClaudeCodeEnabled = true;
        OrbweaverSettings.CodexEnabled = true;

        var manager = new SessionManager(current.Dir, null, legacyStatusDir: legacy.Dir);
        manager.ScanAndUpdate();
        return manager;
    }

    [AvaloniaFact]
    public void ASessionOnlyTheLegacyHookWroteGetsAnOrb()
    {
        using var current = new Scratch();
        using var legacy = new Scratch();
        current.Write("after-upgrade", cli: "codex");
        legacy.Write("before-upgrade");

        var manager = ScanBoth(current, legacy);

        Assert.Equal(
            new[] { "after-upgrade", "before-upgrade" },
            OrbIds(manager).OrderBy(id => id, StringComparer.Ordinal));
    }

    // The negative control: the same legacy file, scanned by a manager that
    // was not handed the legacy folder, draws nothing.
    [AvaloniaFact]
    public void WithoutTheLegacyFolderItsSessionsAreNotRead()
    {
        using var current = new Scratch();
        using var legacy = new Scratch();
        legacy.Write("before-upgrade");

        Assert.Empty(OrbIds(Scan(current)));
    }

    [AvaloniaFact]
    public void TheNewerCopyOfAResumedSessionIsTheOneOnScreen()
    {
        using var current = new Scratch();
        using var legacy = new Scratch();
        var now = DateTime.UtcNow;
        current.Write("resumed", state: "idle", title: "stale", written: now.AddSeconds(-30));
        legacy.Write("resumed", state: "generating", title: "fresh", written: now);

        var manager = ScanBoth(current, legacy);

        Assert.Equal("fresh", manager.StatusFor("resumed")!.Title);
        Assert.Single(OrbIds(manager));

        // And the loser is left alone on disk.
        Assert.True(File.Exists(Path.Combine(current.Dir, "resumed.txt")));
    }

    [AvaloniaFact]
    public void TheAutoColourMarkerIsKeptInBothFolders()
    {
        var before = OrbweaverSettings.AutoColorSessions;
        try
        {
            using var current = new Scratch();
            using var legacy = new Scratch();

            OrbweaverSettings.AutoColorSessions = true;
            var manager = ScanBoth(current, legacy);
            Assert.True(File.Exists(Path.Combine(current.Dir, ".auto-color")));
            Assert.True(File.Exists(Path.Combine(legacy.Dir, ".auto-color")));

            OrbweaverSettings.AutoColorSessions = false;
            manager.ScanAndUpdate();
            Assert.False(File.Exists(Path.Combine(current.Dir, ".auto-color")));
            Assert.False(File.Exists(Path.Combine(legacy.Dir, ".auto-color")));
        }
        finally
        {
            OrbweaverSettings.AutoColorSessions = before;
        }
    }

    // A legacy folder that is not there — every old session has restarted —
    // neither stops the new folder's marker nor gets created to hold one.
    [AvaloniaFact]
    public void AMissingLegacyFolderNeitherBlocksTheMarkerNorIsCreated()
    {
        var before = OrbweaverSettings.AutoColorSessions;
        try
        {
            using var current = new Scratch();
            var missing = Path.Combine(Path.GetTempPath(), "cb-scan-missing-" + Guid.NewGuid());

            OrbweaverSettings.AutoColorSessions = true;
            new SessionManager(current.Dir, null, legacyStatusDir: missing).SyncAutoColorMarker();

            Assert.True(File.Exists(Path.Combine(current.Dir, ".auto-color")));
            Assert.False(Directory.Exists(missing));
        }
        finally
        {
            OrbweaverSettings.AutoColorSessions = before;
        }
    }

    // Resetting a legacy session rewrites the legacy file. Rebuilding the
    // path from the new folder would instead write a second, newer copy
    // there — which would then win every scan and hide the real one.
    [AvaloniaFact]
    public void ResettingALegacySessionRewritesTheLegacyFile()
    {
        using var current = new Scratch();
        using var legacy = new Scratch();
        legacy.Write("old", state: "waiting", title: "kept");

        var manager = ScanBoth(current, legacy);
        manager.ResetSessionToIdle("old");

        var onDisk = System.Text.Json.JsonSerializer.Deserialize<SessionStatus>(
            File.ReadAllText(Path.Combine(legacy.Dir, "old.txt")));
        Assert.Equal("idle", onDisk!.State);
        Assert.Equal("kept", onDisk.Title);
        Assert.False(File.Exists(Path.Combine(current.Dir, "old.txt")));
    }

    [AvaloniaFact]
    public void DismissingALegacySessionDeletesTheLegacyFile()
    {
        using var current = new Scratch();
        using var legacy = new Scratch();
        legacy.Write("old");

        var manager = ScanBoth(current, legacy);
        manager.DismissSession("old");

        Assert.False(File.Exists(Path.Combine(legacy.Dir, "old.txt")));
    }

    // Dismissing a session with a copy in each folder removes both. Deleting
    // only the winner would hand the screen back to the older copy on the
    // very next scan — a dismissed orb that comes straight back, still dead.
    [AvaloniaFact]
    public void DismissingASessionWithACopyInEachFolderRemovesBoth()
    {
        using var current = new Scratch();
        using var legacy = new Scratch();
        var now = DateTime.UtcNow;
        current.Write("resumed", written: now);
        legacy.Write("resumed", written: now.AddSeconds(-30));

        var manager = ScanBoth(current, legacy);
        manager.DismissSession("resumed");

        Assert.False(File.Exists(Path.Combine(current.Dir, "resumed.txt")));
        Assert.False(File.Exists(Path.Combine(legacy.Dir, "resumed.txt")));

        manager.ScanAndUpdate();
        Assert.Empty(OrbIds(manager));
    }

    // A session no read has seen yet still resolves to the new folder, which
    // is where any file this app creates for it belongs.
    [AvaloniaFact]
    public void ASessionNoScanHasSeenIsResetInTheNewFolder()
    {
        using var current = new Scratch();
        using var legacy = new Scratch();

        var manager = new SessionManager(current.Dir, null, legacyStatusDir: legacy.Dir);
        manager.ResetSessionToIdle("unseen");

        Assert.True(File.Exists(Path.Combine(current.Dir, "unseen.txt")));
        Assert.False(File.Exists(Path.Combine(legacy.Dir, "unseen.txt")));
    }
}
