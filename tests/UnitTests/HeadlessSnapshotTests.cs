using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// Covers SessionManager.HeadlessSnapshot — the UI-less composition of the scan
// rules that answers a far machine's roster request before SessionManager has
// started (CB-24) — and RemoteControlSessions.LocalSessions falling back to it.
//
// Every case hands in all four seams (directory, job listing, process
// liveness, clock), because the defaults read this machine: the real temp
// directory, the real process table, and a shell-out to `claude agents
// --json`. Those default arms are the change's named coverage gap, the same
// way the live scan's constructor defaults are.
//
// The two LocalSessions cases mutate process-wide statics (the provider and
// the fallback) and put both back in a finally.
//
// Keeping them in one class is not enough, which is what the collection below
// is for. A class is xunit's parallelism unit, so same-class cases cannot race
// each other — but HeadlessSessionsTests mutates these same two statics, and
// nothing stopped the two classes running at once. That is a real flake, not a
// theoretical one: LocalSessionsAsksTheDiskRatherThanTheOrbList failed once
// here in 1ms while another class held the fallback, then passed on a rerun
// over an identical binary.
//
// "Settings" rather than a collection of its own, deliberately:
// HeadlessSessionsTests is already in it, and a new name would serialise this
// class against nothing. The collection is the repo's existing answer to
// process-wide statics — see SettingsCollection's own comment — and a static
// delegate is the same hazard as a static settings object.
[Collection("Settings")]
public class HeadlessSnapshotTests
{
    private static readonly Func<Dictionary<string, string>?> NoJobs =
        () => new Dictionary<string, string>();

    private static string NewStatusDir()
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "cb-headless-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteStatus(string dir, string id, SessionStatus status) =>
        File.WriteAllText(
            Path.Combine(dir, id + ".txt"), JsonSerializer.Serialize(status));

    [Fact]
    public void KeepsALiveSessionAndCarriesItsStatusThrough()
    {
        var dir = NewStatusDir();
        try
        {
            WriteStatus(dir, "abc123", new SessionStatus
            {
                State = "idle",
                Title = "job-hunter",
                Cwd = "/tmp/somewhere",
                SessionPid = 4242
            });

            var kept = SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow);

            var one = Assert.Single(kept);
            Assert.Equal("abc123", one.SessionId);
            Assert.Equal("job-hunter", one.Status.Title);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A session with a live pid but no session record on this machine — which
    // is every session in this suite, since the pids are invented — must come
    // back kept. SessionPark is consulted before TranscriptHandoff in the
    // composition above, so a rule that answered "parked" on a missing record
    // would empty this whole file rather than fail one case.
    [Fact]
    public void AnInventedPidWithNoSessionRecordIsNotTreatedAsParked()
    {
        var dir = NewStatusDir();
        try
        {
            WriteStatus(dir, "abc123", new SessionStatus
            {
                State = "idle",
                Title = "job-hunter",
                Cwd = "/tmp/somewhere",
                SessionPid = 999999,
                TranscriptPath = "/tmp/nonexistent/abc123.jsonl"
            });

            var kept = SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow);

            Assert.Single(kept);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DropsThisAppsOwnRelay()
    {
        var dir = NewStatusDir();
        try
        {
            // The own-relay test keys on the *leaf* of the cwd — see
            // MachineNames.LooksLikeALeftoverRelay — so any path whose last
            // segment wears the relay prefix is one.
            WriteStatus(dir, "relay", new SessionStatus
            {
                State = "idle",
                Cwd = "/tmp/anywhere/claude-buddy-rc--claude-somebox",
                SessionPid = 4242
            });

            var kept = SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow);

            Assert.Empty(kept);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DropsASessionWhoseProcessIsGone()
    {
        var dir = NewStatusDir();
        try
        {
            WriteStatus(dir, "gone", new SessionStatus
            {
                State = "idle",
                Cwd = "/tmp/somewhere",
                SessionPid = 4242
            });

            var kept = SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => false, nowUtc: DateTime.UtcNow);

            Assert.Empty(kept);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SkipsAFileStillBeingWritten()
    {
        var dir = NewStatusDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "half.txt"), "{ \"State\": \"id");
            WriteStatus(dir, "whole", new SessionStatus
            {
                State = "idle",
                Cwd = "/tmp/somewhere",
                SessionPid = 4242
            });

            var kept = SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow);

            Assert.Equal("whole", Assert.Single(kept).SessionId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // JSON's literal `null` deserializes without throwing, to a null status —
    // a different arm than the mid-write case above, which lands in the catch.
    [Fact]
    public void SkipsAFileHoldingTheJsonNullLiteral()
    {
        var dir = NewStatusDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "nul.txt"), "null");

            var kept = SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow);

            Assert.Empty(kept);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AnswersEmptyWhenTheDirectoryDoesNotExistYet()
    {
        var kept = SessionManager.HeadlessSnapshot(
            Path.Combine(Path.GetTempPath(), "cb-headless-never-" + Guid.NewGuid().ToString("N")),
            NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow);

        Assert.Empty(kept);
    }

    // --- the husk, and the two rules agreeing about it ---

    // The row a backgrounded turn leaves in its transcript, and one row of the
    // housekeeping Claude Code writes after it. Shortened from the captures in
    // TranscriptHandoffTests, which is where the full fixtures and the reasoning
    // about each field live; what matters here is only that the tail reads as
    // handed off, not why.
    // CB-223: the snapshot a peer is served from now knows each session's
    // team, read in one batch for every Claude Code pid it kept.
    [Fact]
    public void TheSnapshotReadsTeamsInOneBatchForTheClaudeSessionsItKept()
    {
        var dir = NewStatusDir();
        try
        {
            WriteStatus(dir, "lead-1", new SessionStatus { State = "idle", Title = "backlog", Cwd = "/tmp/t", SessionPid = 501 });
            WriteStatus(dir, "member-1", new SessionStatus { State = "idle", Title = "backlog", Cwd = "/tmp/t", SessionPid = 502 });

            var asked = new List<IReadOnlyList<int>>();
            var kept = SessionManager.HeadlessSnapshot(dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow,
                teams: pids =>
                {
                    asked.Add(pids);
                    return new Dictionary<int, AgentTeam.Membership>
                    {
                        [501] = new("lead-1", "red", "lead"),
                        [502] = new("lead-1", "blue", "wren"),
                    };
                });

            var batch = Assert.Single(asked);
            Assert.Equal(new[] { 501, 502 }, batch.OrderBy(p => p));

            var member = kept.Single(k => k.SessionId == "member-1").Status;
            Assert.Equal("lead-1", member.Lead);
            Assert.Equal("wren", member.Agent);
            Assert.Equal("blue", member.AgentColor);
            Assert.Equal("", kept.Single(k => k.SessionId == "lead-1").Status.Lead);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // A team the size of the one this was measured inside — a lead and
    // fourteen members — is still one read, not fifteen.
    [Fact]
    public void AFifteenSessionTeamIsReadInOneBatch()
    {
        var dir = NewStatusDir();
        try
        {
            WriteStatus(dir, "lead", new SessionStatus { State = "idle", Title = "backlog", Cwd = "/tmp/t", SessionPid = 600 });
            for (var i = 1; i <= 14; i++)
                WriteStatus(dir, "member-" + i, new SessionStatus { State = "idle", Title = "backlog", Cwd = "/tmp/t", SessionPid = 600 + i });

            var calls = 0;
            var kept = SessionManager.HeadlessSnapshot(dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow,
                teams: pids =>
                {
                    calls++;
                    Assert.Equal(15, pids.Count);
                    return pids.ToDictionary(p => p, p => p == 600
                        ? new AgentTeam.Membership("lead", "red", "lead")
                        : new AgentTeam.Membership("lead", "blue", "agent-" + (p - 600)));
                });

            Assert.Equal(1, calls);
            Assert.Equal(14, kept.Count(k => k.Status.Lead == "lead"));
            Assert.Equal("", kept.Single(k => k.SessionId == "lead").Status.Lead);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // A Claude Code session with no pid has no process to ask: it stays out
    // of the batch, and it is in no team. The pid-less session is a
    // background job the daemon still names — the one shape in which the
    // snapshot keeps a pid-less Claude status at all (see the case above that
    // pins it, and why its platform is pinned too).
    [Fact]
    public void AClaudeSessionWithNoPidIsLeftOutOfTheTeamRead()
    {
        var dir = NewStatusDir();
        try
        {
            WriteStatus(dir, "member", new SessionStatus
                { State = "idle", Title = "backlog", Cwd = "/tmp/t", Source = SessionSource.ClaudeCode, SessionPid = 701 });
            WriteStatus(dir, "pidless", new SessionStatus
                { State = "idle", Title = "backlog", Cwd = "/tmp/t", Source = SessionSource.ClaudeCode, SessionPid = 0 });
            var jobs = new Dictionary<string, string> { ["pidless"] = "blocked" };

            var asked = new List<IReadOnlyList<int>>();
            var kept = SessionManager.HeadlessSnapshot(dir, () => jobs, isRunning: _ => true, nowUtc: DateTime.UtcNow,
                honourOrbLifetime: false, onWindows: false,
                teams: pids =>
                {
                    asked.Add(pids);
                    return new Dictionary<int, AgentTeam.Membership> { [701] = new("lead-x", "blue", "wren") };
                });

            Assert.Equal(new[] { 701 }, Assert.Single(asked));
            Assert.Contains(kept, k => k.SessionId == "pidless");
            Assert.Equal("", kept.Single(k => k.SessionId == "pidless").Status.Lead);
            Assert.Equal("lead-x", kept.Single(k => k.SessionId == "member").Status.Lead);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // Only Claude Code sessions are asked about: agent teams are Claude Code's,
    // and a Codex session kept beside a member is left out of the batch.
    [Fact]
    public void ACodexSessionIsLeftOutOfTheTeamRead()
    {
        var dir = NewStatusDir();
        var codexWas = OrbweaverSettings.CodexEnabled;
        try
        {
            OrbweaverSettings.CodexEnabled = true;
            WriteStatus(dir, "member", new SessionStatus
                { State = "idle", Title = "backlog", Cwd = "/tmp/t", Source = SessionSource.ClaudeCode, SessionPid = 801 });
            WriteStatus(dir, "codex", new SessionStatus
                { State = "idle", Title = "codex", Cwd = "/tmp/t", Cli = "codex", SessionPid = 802 });

            var asked = new List<IReadOnlyList<int>>();
            var kept = SessionManager.HeadlessSnapshot(dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow,
                teams: pids =>
                {
                    asked.Add(pids);
                    return new Dictionary<int, AgentTeam.Membership>();
                });

            Assert.Contains(kept, k => k.SessionId == "codex");
            Assert.Equal(new[] { 801 }, Assert.Single(asked));
        }
        finally
        {
            OrbweaverSettings.CodexEnabled = codexWas;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // Nothing kept is Claude Code, so nothing is asked — on Windows the team
    // read is a WMI query, and a snapshot of no sessions should cost none.
    [Fact]
    public void WithNoClaudeSessionsKeptTheTeamReadIsNotMade()
    {
        var dir = NewStatusDir();
        try
        {
            var asked = 0;
            var kept = SessionManager.HeadlessSnapshot(dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow,
                teams: _ => { asked++; return new Dictionary<int, AgentTeam.Membership>(); });

            Assert.Empty(kept);
            Assert.Equal(0, asked);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static string BackgroundingRow(string sessionId) =>
        @"{""type"":""system"",""subtype"":""informational"","
      + @"""content"":""Backgrounding after the current tool finishes…"","
      + @"""timestamp"":""2026-08-28T17:53:15.295Z"",""level"":""warning"","
      + "\"sessionId\":\"" + sessionId + "\"}";

    private const string ConversationRow =
        @"{""type"":""assistant"",""message"":{""role"":""assistant"","
      + @"""content"":[{""type"":""text"",""text"":""on it""}]},"
      + @"""timestamp"":""2026-08-28T17:53:11.000Z""}";

    // A transcript on disk, since the rule this exercises stats and reads a real
    // file — it is the closure behind the verdict, not the verdict's own logic,
    // that this is about.
    private static string WriteTranscript(string dir, params string[] rows)
    {
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(path, rows);
        return path;
    }

    // A husk: healthy by every other measure — live process, file written a
    // moment ago, real title — and handed away according to its own transcript.
    private static SessionStatus Husk(string transcriptPath) => new()
    {
        State = "generating",
        Title = "job-hunter",
        Cwd = "/tmp/somewhere",
        SessionPid = 4242,
        TranscriptPath = transcriptPath
    };

    // The verdict the live scan reaches for the same file, built the way
    // SessionManager builds it: the same closure, over the same status.
    private static SessionManager.ScanVerdict LiveScanVerdict(
        string sessionId, SessionStatus status, DateTime written, DateTime now)
    {
        status.Source = SessionManager.SourceOf(status);

        var phase = status.Source == SessionSource.ClaudeCode
            ? BackgroundJobs.Phase(new Dictionary<string, string>(), sessionId)
            : JobPhase.Unknown;

        Func<bool> handedToBackground = () =>
            SessionPresence.CouldBeABackgroundedHusk(status, phase)
            && TranscriptHandoff.EndsBackgrounded(status.TranscriptPath, sessionId);

        return SessionManager.JudgeLiveness(
            sessionId, status, written, now, SessionManager.StaleAfter,
            new HashSet<string>(), _ => true, handedToBackground);
    }

    // The two must not disagree, and this is the one thing they can disagree
    // about.
    //
    // HeadlessSnapshot's own comment says it is composed from the same rules as
    // the live scan, in the same order, "so the two cannot disagree about which
    // sessions exist" — and every rule but this one is a pure function of the
    // file. The husk check is the exception: it is a closure, so a caller can
    // pass one that answers nothing and still compile. That is exactly what
    // happened. JudgeLiveness gained the parameter on one branch and this call
    // site arrived on another, the two merged with no textual conflict, and for
    // three hours `develop` did not build at all.
    //
    // Stubbing it with `() => false` is the version of that mistake that *does*
    // compile, and it is the one this case exists to catch: a far machine would
    // then be served an orb for a session whose conversation has moved to a
    // fork — a duplicate wearing the same title, frozen at "generating", that no
    // hook will ever correct. Which is the bug the husk rule was written for,
    // reintroduced on the one path it did not cover.
    [Fact]
    public void TheHeadlessSnapshotDropsAHuskJustAsTheLiveScanDoes()
    {
        var dir = NewStatusDir();
        try
        {
            var transcript = WriteTranscript(dir, ConversationRow, BackgroundingRow("husk1"));
            var status = Husk(transcript);
            WriteStatus(dir, "husk1", status);

            var now = DateTime.UtcNow;

            // The live scan's answer for this file.
            Assert.Equal(
                SessionManager.ScanVerdict.Backgrounded,
                LiveScanVerdict("husk1", status, now, now));

            // ...and the headless one's, which must agree.
            Assert.Empty(SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: now));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The other direction, so "dropped" above is about the transcript rather
    // than about anything else this status happens to carry. The same session,
    // the same live pid, the same recent write — and a transcript whose last
    // word is conversation, which means the session lived on past whatever the
    // tail held earlier.
    [Fact]
    public void ASessionThatWasNotHandedAwayIsKeptByBoth()
    {
        var dir = NewStatusDir();
        try
        {
            var transcript = WriteTranscript(dir, BackgroundingRow("husk2"), ConversationRow);
            var status = Husk(transcript);
            WriteStatus(dir, "husk2", status);

            var now = DateTime.UtcNow;

            Assert.Equal(
                SessionManager.ScanVerdict.Keep,
                LiveScanVerdict("husk2", status, now, now));

            Assert.Equal("husk2", Assert.Single(SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: now)).SessionId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The gate in front of the transcript read, from the other side.
    //
    // The two cases above both consult the closure — one ends backgrounded and
    // one does not. This is the session where it is never consulted at all: no
    // transcript path, so there is nothing to read and CouldBeABackgroundedHusk
    // says so before TranscriptHandoff is asked. Worth pinning separately
    // because it is the arm that keeps a scan from statting a file for every
    // session that has no transcript to stat, and because "kept" here has to
    // mean "the question did not arise" rather than "the answer was no".
    [Fact]
    public void ASessionWithNoTranscriptIsKeptWithoutTheQuestionBeingAsked()
    {
        var dir = NewStatusDir();
        try
        {
            var status = Husk(transcriptPath: "");
            WriteStatus(dir, "notranscript", status);

            var now = DateTime.UtcNow;

            Assert.Equal(
                SessionManager.ScanVerdict.Keep,
                LiveScanVerdict("notranscript", status, now, now));

            Assert.Equal("notranscript", Assert.Single(SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: now)).SessionId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LocalSessionsFallsBackToTheHeadlessSnapshot()
    {
        RemoteControlSessions.ForgetLocalSessionsForTests();
        var was = RemoteControlSessions.HeadlessFallback;
        try
        {
            RemoteControlSessions.HeadlessFallback = () =>
                new List<(string, SessionStatus)> { ("fallback", new SessionStatus()) };

            Assert.Equal(
                "fallback", Assert.Single(RemoteControlSessions.LocalSessions()).SessionId);
        }
        finally
        {
            RemoteControlSessions.HeadlessFallback = was;
        }
    }

    // **This used to assert that the live orb list beat the scan, and that
    // preference was the bug.** The orb list is what is on screen, and what is
    // on screen has had the user's orb-lifetime preference applied — so an idle
    // session that had stopped being drawn was reported to every other machine
    // as not existing. On a headless Mac the list is filled by a scan on a
    // dispatcher that never pumps, so it was permanently empty.
    //
    // Measured: the mini answered one roster with a session and, an hour later,
    // the same roster with none, from two live status files on its disk
    // throughout. Serving is a question of fact, so the disk is asked.
    [Fact]
    public void LocalSessionsAsksTheDiskRatherThanTheOrbList()
    {
        RemoteControlSessions.ForgetLocalSessionsForTests();
        var was = RemoteControlSessions.HeadlessFallback;
        try
        {
            RemoteControlSessions.HeadlessFallback = () =>
                new List<(string, SessionStatus)> { ("from-disk", new SessionStatus()) };

            Assert.Equal(
                "from-disk", Assert.Single(RemoteControlSessions.LocalSessions()).SessionId);
        }
        finally
        {
            RemoteControlSessions.ForgetLocalSessionsForTests();
            RemoteControlSessions.HeadlessFallback = was;
        }
    }

    // The other half of the same change: orb lifetime is a display preference
    // and an answer to another machine is not a display.
    [Fact]
    public void AnIdleSessionIsStillOfferedWhenItsOrbWouldHaveExpired()
    {
        var dir = NewStatusDir();
        try
        {
            WriteStatus(dir, "idle-but-alive", new SessionStatus
            {
                State = "idle",
                Cwd = "/tmp/somewhere",
                SessionPid = 4242
            });

            var longAfter = DateTime.UtcNow.AddDays(3);

            Assert.Empty(SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: longAfter,
                honourOrbLifetime: true));

            Assert.Single(SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: longAfter,
                honourOrbLifetime: false));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // What is *not* excused by that, which is the part worth pinning: a process
    // that has gone is gone whatever the preference says.
    [Fact]
    public void ADeadProcessIsStillDroppedWithLifetimeIgnored()
    {
        var dir = NewStatusDir();
        try
        {
            WriteStatus(dir, "gone-too", new SessionStatus
            {
                State = "idle",
                Cwd = "/tmp/somewhere",
                SessionPid = 4242
            });

            Assert.Empty(SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => false, nowUtc: DateTime.UtcNow,
                honourOrbLifetime: false));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A pid-less Claude Code record is not proof of a live session. It is kept
    // only while the daemon still names it as a background job; otherwise a
    // headless machine would offer every abandoned subagent to its peers forever
    // when the remote-serving path ignores the display lifetime.
    [Fact]
    public void APidlessStatusTheDaemonDoesNotKnowIsNotOfferedWithLifetimeIgnored()
    {
        var dir = NewStatusDir();
        try
        {
            WriteStatus(dir, "leftover", new SessionStatus
            {
                State = "idle",
                Cwd = "/tmp/somewhere",
                Source = SessionSource.ClaudeCode,
                SessionPid = 0
            });

            // onWindows: false because the fixture is a Mac-shaped path. On
            // Windows a pid-less file with a '/' path is a WSL session, which
            // the listing cannot speak for (CB-194) — the Windows shape of this
            // case, a drive-letter leftover, is
            // APidlessNativeWindowsFileIsStillDroppedAsNotALiveJob below.
            Assert.Empty(SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow,
                honourOrbLifetime: false, onWindows: false));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The inverse keeps the fail-safe intact: a pid-less background worker is
    // real when the daemon names it, even though it has no terminal pid for the
    // headless scan to probe.
    [Fact]
    public void APidlessStatusTheDaemonStillNamesIsOfferedWithLifetimeIgnored()
    {
        var dir = NewStatusDir();
        try
        {
            WriteStatus(dir, "background", new SessionStatus
            {
                State = "idle",
                Cwd = "/tmp/somewhere",
                Source = SessionSource.ClaudeCode,
                SessionPid = 0
            });

            var jobs = new Dictionary<string, string> { ["background"] = "blocked" };

            // Platform pinned for the reason the case above gives: on Windows
            // this file would be kept as WSL rather than because the daemon
            // names it, and the test would pass without asserting its point.
            Assert.Equal("background", Assert.Single(SessionManager.HeadlessSnapshot(
                dir, () => jobs, isRunning: _ => true, nowUtc: DateTime.UtcNow,
                honourOrbLifetime: false, onWindows: false)).SessionId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // CB-194. What the Windows hook writes for a session running inside WSL:
    // no pid, because its walk up the Windows process tree ends in the interop
    // bridge, and Linux paths, because that is where the session is.
    private static SessionStatus WslShaped(string cwd = "/home/k/project") => new()
    {
        State = "idle",
        Cli = "claude",
        Cwd = cwd,
        TermProgram = "WindowsTerminal",
        TermPid = 19148,
        SessionPid = 0,
        TranscriptPath = cwd.StartsWith('/')
            ? "/home/k/.claude/projects/-home-k-project/wsl1.jsonl"
            : @"C:\Users\k\.claude\projects\C--Users-k-project\wsl1.jsonl"
    };

    // The roster a paired machine is served has to agree with the orbs this one
    // draws. NoJobs is a listing that was read and names nobody — exactly what
    // the Windows daemon answers about a WSL session — so before the fix this
    // was NotALiveJob, and the far machine was told the session did not exist.
    [Fact]
    public void AWslSessionIsKeptOnWindowsThoughTheWindowsListingDoesNotNameIt()
    {
        var dir = NewStatusDir();
        try
        {
            WriteStatus(dir, "wsl1", WslShaped());

            var kept = SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow,
                onWindows: true);

            Assert.Equal("wsl1", Assert.Single(kept).SessionId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The negative controls for the case above, on the same file and the same
    // listing: if either of these were kept, the fix would have switched the
    // subagent rule off rather than told WSL apart.
    [Fact]
    public void TheSameFileOnAMacIsStillDroppedAsNotALiveJob()
    {
        var dir = NewStatusDir();
        try
        {
            WriteStatus(dir, "wsl1", WslShaped());

            var kept = SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow,
                onWindows: false);

            Assert.Empty(kept);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void APidlessNativeWindowsFileIsStillDroppedAsNotALiveJob()
    {
        var dir = NewStatusDir();
        try
        {
            WriteStatus(dir, "wsl1", WslShaped(cwd: @"C:\Users\k\project"));

            var kept = SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow,
                onWindows: true);

            Assert.Empty(kept);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // With lifetime ignored — the path that answers a paired machine before the
    // scan has started — a WSL session still goes by the lifetime, because it
    // is the only evidence there is: Windows cannot probe its process and its
    // daemon is in the VM. Without this, a WSL session killed without a
    // SessionEnd would be offered to every peer forever.
    [Fact]
    public void AWslSessionPastTheOrbLifetimeIsNotOfferedEvenWithLifetimeIgnored()
    {
        var dir = NewStatusDir();
        var before = OrbweaverSettings.OrbLifetimeMinutes;
        try
        {
            OrbweaverSettings.OrbLifetimeMinutes = 30;
            WriteStatus(dir, "wsl1", WslShaped());
            File.SetLastWriteTimeUtc(Path.Combine(dir, "wsl1.txt"), DateTime.UtcNow.AddHours(-2));

            Assert.Empty(SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow,
                honourOrbLifetime: false, onWindows: true));
        }
        finally
        {
            OrbweaverSettings.OrbLifetimeMinutes = before;
            Directory.Delete(dir, recursive: true);
        }
    }

    // ...and the controls either side of it: inside the lifetime it is offered,
    // and a native session whose process is alive is still offered past it —
    // ignoring the lifetime is only suspended where nothing else can speak.
    [Fact]
    public void AWslSessionInsideTheOrbLifetimeIsOfferedWithLifetimeIgnored()
    {
        var dir = NewStatusDir();
        var before = OrbweaverSettings.OrbLifetimeMinutes;
        try
        {
            OrbweaverSettings.OrbLifetimeMinutes = 30;
            WriteStatus(dir, "wsl1", WslShaped());

            Assert.Equal("wsl1", Assert.Single(SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow,
                honourOrbLifetime: false, onWindows: true)).SessionId);
        }
        finally
        {
            OrbweaverSettings.OrbLifetimeMinutes = before;
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ALiveNativeSessionPastTheOrbLifetimeIsStillOfferedWithLifetimeIgnored()
    {
        var dir = NewStatusDir();
        var before = OrbweaverSettings.OrbLifetimeMinutes;
        try
        {
            OrbweaverSettings.OrbLifetimeMinutes = 30;
            WriteStatus(dir, "native", new SessionStatus
            {
                State = "idle",
                Cwd = @"C:\Users\k\project",
                TermProgram = "WindowsTerminal",
                SessionPid = 4242
            });
            File.SetLastWriteTimeUtc(Path.Combine(dir, "native.txt"), DateTime.UtcNow.AddHours(-2));

            Assert.Equal("native", Assert.Single(SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow,
                honourOrbLifetime: false, onWindows: true)).SessionId);
        }
        finally
        {
            OrbweaverSettings.OrbLifetimeMinutes = before;
            Directory.Delete(dir, recursive: true);
        }
    }

    // --- CB-307 addendum: a session on a dialog is waiting ---------------------

    // The roster a peer is served before the UI starts says `waiting` for a
    // session whose Claude Code record says so, though its status file still
    // says idle — and says nothing of the kind for a record that merely reads
    // `busy`, the word measured two days wrong on a session at its prompt.
    [Theory]
    [InlineData("waiting", "waiting", true)]
    [InlineData("busy", "idle", false)]
    [InlineData("idle", "idle", false)]
    [InlineData(null, "idle", false)]
    public void ARecordThatSaysWaitingPromotesTheServedState(
        string? record, string expectedState, bool expectedFlag)
    {
        var dir = NewStatusDir();
        try
        {
            WriteStatus(dir, "abc123", new SessionStatus
            {
                State = "idle", Title = "openclaw", Cwd = "/tmp/somewhere", SessionPid = 4242
            });

            var asked = new List<(int Pid, string Id)>();
            var kept = SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow,
                recordStatus: (pid, id) => { asked.Add((pid, id)); return record; });

            var one = Assert.Single(kept);
            Assert.Equal(expectedState, one.Status.State);
            Assert.Equal(expectedFlag, one.Status.RecordWaiting);
            Assert.Equal((4242, "abc123"), Assert.Single(asked));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Promoted before liveness is judged, so a session on a dialog that the
    // lifetime would otherwise expire is still served.
    [Fact]
    public void AWaitingSessionIsKeptByTheLifetimeRuleToo()
    {
        var dir = NewStatusDir();
        try
        {
            WriteStatus(dir, "abc123", new SessionStatus
            {
                State = "idle", Title = "openclaw", Cwd = "/tmp/somewhere", SessionPid = 4242
            });
            var old = DateTime.UtcNow.AddDays(-3);
            File.SetLastWriteTimeUtc(Path.Combine(dir, "abc123.txt"), old);

            var kept = SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow,
                recordStatus: (_, _) => "waiting");

            Assert.Single(kept);

            // Control: the same stale file with no waiting record is expired,
            // so the lifetime rule was live and the promotion is what saved it.
            Assert.Empty(SessionManager.HeadlessSnapshot(
                dir, NoJobs, isRunning: _ => true, nowUtc: DateTime.UtcNow,
                recordStatus: (_, _) => "idle"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A session with no pid has no record to ask about, and nothing is asked.
    [Fact]
    public void ASessionWithNoPidIsNeverAskedForARecord()
    {
        var status = new SessionStatus { State = "idle", Source = SessionSource.ClaudeCode, SessionPid = 0 };
        SessionManager.ApplyRecordWaiting(status, "id", (_, _) => throw new InvalidOperationException("asked"));

        Assert.Equal("idle", status.State);
        Assert.False(status.RecordWaiting);
    }

    // Codex and Grok have no such record; their rows are not promoted.
    [Fact]
    public void OnlyClaudeCodeSessionsAreAskedForARecord()
    {
        var status = new SessionStatus { State = "idle", Source = SessionSource.Codex, SessionPid = 99 };
        SessionManager.ApplyRecordWaiting(status, "id", (_, _) => throw new InvalidOperationException("asked"));

        Assert.Equal("idle", status.State);
    }

    // QA's survivor: with a record that DOES say waiting and a live pid, a Codex
    // or Grok status is still not promoted and RecordWaiting stays false. The
    // control is the same status as Claude Code, which is promoted.
    [Theory]
    [InlineData(SessionSource.Codex, "idle", false)]
    [InlineData(SessionSource.Grok, "idle", false)]
    [InlineData(SessionSource.ClaudeCode, "waiting", true)]
    public void AWaitingRecordPromotesOnlyAClaudeCodeSession(
        SessionSource source, string expectedState, bool expectedFlag)
    {
        var status = new SessionStatus { State = "idle", Source = source, SessionPid = 99 };

        SessionManager.ApplyRecordWaiting(status, "id", (_, _) => "waiting");

        Assert.Equal(expectedState, status.State);
        Assert.Equal(expectedFlag, status.RecordWaiting);
    }
}
