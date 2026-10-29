using System;
using System.Collections.Generic;
using Xunit;

namespace Orbweaver.Tests;

// The three rules that decide whether a status file becomes an orb:
// SessionManager.JudgeLiveness, WantsAgentViewer and JudgeReachability.
//
// These are the most-corrected lines in the app and every case below is a bug
// somebody shipped, not a shape invented to walk a branch. The comments in
// SessionManager name them: a Ctrl+C'd session whose orb never went away, an
// orb per subagent that accumulated forever on the "keep orbs forever"
// setting, a Codex file that no rule on any path could remove, a background
// agent that vanished the moment the hook learned to record its own pid. A
// wrong answer here is either an orb that is a dead click or a live session
// with nothing on screen, and neither announces itself.
//
// isRunning and transcriptExists are the two seams: the real ones are a
// kill(2) against a pid this machine may not have and a stat(2) against its
// disk. Passing them in is what makes the rules answerable without either —
// the same reasoning Superseded's own comment records. The daemon's answer
// arrives as a JobPhase value rather than a seam: the scan decides it once
// per pass and hands it in.
public class ScanVerdictTests
{
    private static readonly Func<int, bool> AllAlive = _ => true;
    private static readonly Func<int, bool> AllDead = _ => false;

    private static readonly HashSet<string> Nothing = new(StringComparer.Ordinal);

    private static readonly DateTime Now = new(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

    // A session that is doing everything right: a live process, a terminal to
    // jump to, and a file written a moment ago. Every test below is this with
    // one thing changed, which is what keeps each case about one rule.
    private static SessionStatus Healthy(
        SessionSource source = SessionSource.ClaudeCode,
        string state = "idle",
        int pid = 4321,
        string termProgram = "iTerm.app") =>
        new()
        {
            Source = source,
            State = state,
            SessionPid = pid,
            TermProgram = termProgram,
            Tty = "/dev/ttys004",
        };

    // handedToBackground defaults to "the transcript says nothing", so every
    // case written before the Backgrounded verdict existed still asks what it
    // was asking — the same convention Reachability's transcriptExists keeps.
    private static SessionManager.ScanVerdict Liveness(
        SessionStatus status,
        TimeSpan? staleAfter = null,
        DateTime? written = null,
        ISet<string>? superseded = null,
        Func<int, bool>? isRunning = null,
        Func<bool>? handedToBackground = null) =>
        SessionManager.JudgeLiveness(
            "session-1", status, written ?? Now, Now, staleAfter,
            superseded ?? Nothing, isRunning ?? AllAlive,
            handedToBackground ?? (() => false));

    // phase is what the daemon said, where this used to take a closure that
    // reduced the same answer to a bool. The default is NotAJob — the only answer
    // that rules a session out — so a test that says nothing about the daemon is
    // asking the strictest version of each rule.
    //
    // transcriptExists defaults to "the transcript is there", so every case
    // written before the NothingToShow rule existed still asks what it was
    // asking. A status with no TranscriptPath at all cannot reach that rule
    // anyway — which is most of them below, and is the conservative half of the
    // rule rather than an accident of the fixtures.
    private static SessionManager.ScanVerdict Reachability(
        SessionStatus status,
        ISet<string>? leads = null,
        JobPhase phase = JobPhase.NotAJob,
        string sessionId = "session-1",
        Func<string, bool>? transcriptExists = null) =>
        SessionManager.JudgeReachability(
            sessionId, status, leads ?? Nothing, phase,
            transcriptExists ?? TranscriptIsThere);

    private static readonly Func<string, bool> TranscriptIsThere = _ => true;
    private static readonly Func<string, bool> TranscriptIsGone = _ => false;

    // The shape CB-9 was filed for: a background job with no terminal of its
    // own, live in the daemon's listing, naming a transcript that was never
    // written. Everything about it is real — this is job "evidence" off a real
    // machine, `state: blocked`, `needs: send a prompt to start`.
    private static SessionStatus UnpromptedJob(
        SessionSource source = SessionSource.ClaudeCode,
        string transcriptPath =
            "/Users/x/.claude/projects/-Users-x-Evidence/0e043819.jsonl") =>
        new()
        {
            Source = source,
            State = "idle",
            SessionPid = 57319,
            TranscriptPath = transcriptPath,
        };

    // --- JudgeLiveness -------------------------------------------------------

    [Fact]
    public void AnOrdinarySessionSurvivesBothVerdicts()
    {
        Assert.Equal(SessionManager.ScanVerdict.Keep, Liveness(Healthy()));
        Assert.Equal(SessionManager.ScanVerdict.Keep, Reachability(Healthy()));
    }

    [Fact]
    public void ASupersededIdIsDroppedBeforeAnythingElseIsAsked()
    {
        // Superseded wins over every other reason, including reasons that would
        // also have dropped it — the caller only needs to know it is going, but
        // asking the daemon or the kernel about an id this process has already
        // moved on from is work nobody should be doing.
        var superseded = new HashSet<string>(StringComparer.Ordinal) { "session-1" };

        Assert.Equal(
            SessionManager.ScanVerdict.Superseded,
            Liveness(Healthy(), superseded: superseded, isRunning: AllDead));
    }

    [Fact]
    public void ADeadProcessDropsTheOrbEvenWhenTheSessionIsWaitingOnYou()
    {
        // The Ctrl+C case. `waiting` is exempt from the lifetime timer below
        // because no further hook fires until the user answers — so before this
        // rule existed, an unanswered prompt whose session had been killed sat
        // on screen indefinitely with nothing able to remove it.
        Assert.Equal(
            SessionManager.ScanVerdict.ProcessGone,
            Liveness(Healthy(state: "waiting"), isRunning: AllDead));
    }

    [Fact]
    public void ADeadProcessIsNotEvenAskedAboutWhenNoPidWasRecorded()
    {
        // A hook older than the session_pid field records 0, and liveness
        // cannot be checked at all — the lifetime timer is the only thing left.
        // isRunning is the failing one here to prove it is never consulted.
        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Liveness(Healthy(pid: 0), isRunning: AllDead));
    }

    [Fact]
    public void AQuietSessionExpiresOnceItIsOlderThanTheLifetime()
    {
        var staleAfter = TimeSpan.FromMinutes(5);

        Assert.Equal(
            SessionManager.ScanVerdict.Expired,
            Liveness(Healthy(), staleAfter, written: Now - TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1))));

        // Exactly at the boundary is not past it.
        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Liveness(Healthy(), staleAfter, written: Now - staleAfter));
    }

    [Fact]
    public void ForeverMeansNoOrbEverExpires()
    {
        // "null means never — an orb then lasts until its status file is
        // deleted (SessionEnd) or you reset it by hand."
        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Liveness(Healthy(), staleAfter: null, written: new DateTime(2020, 1, 1)));
    }

    [Fact]
    public void AWaitingSessionNeverExpiresHoweverLongItHasBeenQuiet()
    {
        // Its mtime is frozen for as long as you are away from the prompt, so
        // the timer would hide the orb exactly when it matters most.
        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Liveness(Healthy(state: "waiting"), TimeSpan.FromMinutes(5),
                     written: new DateTime(2020, 1, 1)));
    }

    [Fact]
    public void AGeneratingGatewaySessionNeverExpiresButAGeneratingLocalOneDoes()
    {
        // The asymmetry is the point, and it is not arbitrary: a local session
        // that is generating is having its file rewritten as it works, so it
        // cannot be caught by the timer in the first place. A gateway session
        // has no equivalent, so `generating` has to be exempted explicitly or
        // an agent mid-answer loses its orb.
        var old = new DateTime(2020, 1, 1);
        var staleAfter = TimeSpan.FromMinutes(5);

        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Liveness(Healthy(SessionSource.OpenClaw, state: "generating", pid: 0), staleAfter, old));

        Assert.Equal(
            SessionManager.ScanVerdict.Expired,
            Liveness(Healthy(state: "generating"), staleAfter, old));

        // And an idle gateway session is not exempt — only `generating` is.
        Assert.Equal(
            SessionManager.ScanVerdict.Expired,
            Liveness(Healthy(SessionSource.OpenClaw, pid: 0), staleAfter, old));
    }

    // --- JudgeLiveness: pinned and dashboard gateway sessions (CB-270) ------

    // The flag is derived from the gateway's pin or a dashboard session; here it
    // is simply set, since OpenClawRetentionTests owns what sets it. The control
    // is the same session without the flag, which expires: without it this test
    // would pass on any fixture that was merely recent.
    [Fact]
    public void AnIdleKeptGatewaySessionNeverExpires()
    {
        var shortest = TimeSpan.FromMinutes(1);
        var old = Now - TimeSpan.FromDays(3);

        var kept = Healthy(SessionSource.OpenClaw, pid: 0);
        kept.KeptRegardlessOfAge = true;

        Assert.Equal(SessionManager.ScanVerdict.Keep, Liveness(kept, shortest, written: old));
        Assert.Equal(SessionManager.ScanVerdict.Expired,
            Liveness(Healthy(SessionSource.OpenClaw, pid: 0), shortest, written: old));
    }

    // The flag only means something to OpenClaw: a Claude Code status file that
    // somehow carried it still expires.
    [Fact]
    public void TheKeptFlagDoesNothingForAnotherSource()
    {
        var status = Healthy(SessionSource.ClaudeCode);
        status.KeptRegardlessOfAge = true;

        Assert.Equal(SessionManager.ScanVerdict.Expired,
            Liveness(status, TimeSpan.FromMinutes(1), written: Now - TimeSpan.FromDays(3)));
    }

    // --- JudgeLiveness: cloud sessions never expire (CB-182) -----------------

    // The bug in one case. A cloud session sitting `idle` was exempt from
    // neither the `waiting` rule nor the `generating` one, so the lifetime
    // clock took its orb away — and the account measured for CB-182 had exactly
    // one non-archived cloud session on it, nineteen hours quiet, which is to
    // say the feature drew nothing at all until somebody typed into the web UI
    // to move its `updated_at` back inside the window.
    //
    // One minute is the shortest "Keep orbs for" the picker offers, so this is
    // the setting the exemption has to survive; anything longer would pass on a
    // fixture that was merely recent.
    [Fact]
    public void AnIdleCloudSessionNeverExpiresHoweverLongItHasBeenQuiet()
    {
        var shortest = TimeSpan.FromMinutes(1);

        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Liveness(Healthy(SessionSource.ClaudeCloud, pid: 0), shortest,
                     written: Now - TimeSpan.FromMinutes(1146)));
    }

    // Every state, not just the two the old rule happened to exempt. A cloud
    // session's `State` is ClaudeCloudRoster.StateFor's answer, which falls to
    // "idle" for a status this version has never heard of — so a shape change on
    // the server must not be able to reintroduce the expiry by arriving as a
    // word the rule does not recognise.
    //
    // **The `generating` arm is a tripwire, and it is not redundant with the
    // idle case above — do not delete it as a duplicate.** Before CB-182 that
    // state had its own protection: JudgeLiveness carried
    // `(Source == OpenClaw || Source == ClaudeCloud) && State == "generating"`,
    // and the ClaudeCloud half of that disjunct is what kept a cloud session
    // mid-answer on screen. CB-182's blanket exemption dominates it on every
    // path, so the disjunct became unreachable and was removed rather than left
    // in a live predicate reading as a live rule.
    //
    // That removal is right, and it leaves the two changes load-bearing for
    // each other. `generating` now has no protection of its own: it passes here
    // for the blanket reason, not the `generating` reason. So if anyone ever
    // narrows or removes the blanket cloud exemption — wanting some cloud
    // retention after all, say — this arm is what fails, and it fails for
    // precisely the case that must not regress silently: a cloud session
    // actively generating, expiring mid-turn, which SessionManager's own
    // comment names as the state where hiding an orb is worst. Restoring the
    // `generating` protection is the fix if that ever goes red; deleting the
    // arm is not.
    [Theory]
    [InlineData("idle")]
    [InlineData("generating")]
    [InlineData("waiting")]
    [InlineData("something-the-server-invented-later")]
    public void ACloudSessionSurvivesInEveryStateAtEveryAge(string state)
    {
        var shortest = TimeSpan.FromMinutes(1);

        foreach (var written in new[]
                 {
                     Now - TimeSpan.FromSeconds(30),          // inside the window
                     Now - TimeSpan.FromMinutes(90),          // well outside it
                     new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                     DateTime.MinValue,                       // what an unparseable
                                                              // updated_at becomes
                 })
        {
            Assert.Equal(
                SessionManager.ScanVerdict.Keep,
                Liveness(Healthy(SessionSource.ClaudeCloud, state, pid: 0), shortest, written));
        }
    }

    // **The negative control, and the only thing that makes the cases above
    // mean anything.** An exemption written one clause too wide would keep a
    // cloud session and every local one beside it, and a test that only ever
    // asserts the cloud answer is exactly as green either way. Same state, same
    // age, same staleAfter — only the source differs, and everything that is
    // not cloud still expires.
    [Theory]
    [InlineData(SessionSource.ClaudeCode)]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.Grok)]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    public void EverySourceButCloudStillExpiresAtTheSameAgeAndSetting(SessionSource source)
    {
        var shortest = TimeSpan.FromMinutes(1);
        var quiet = Now - TimeSpan.FromMinutes(1146);

        Assert.Equal(
            SessionManager.ScanVerdict.Expired,
            Liveness(Healthy(source, pid: 0), shortest, quiet));

        // ...and the cloud arm of the same comparison, so the pair is read
        // together rather than trusted to a test in another part of the file.
        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Liveness(Healthy(SessionSource.ClaudeCloud, pid: 0), shortest, quiet));
    }

    // The exemption is about the clock and nothing else. A cloud session has no
    // pid to check and is never handed to a background job, but Superseded is
    // reachable for one — the scan drops an id it has moved on from before it
    // asks anything else — and skipping expiry must not have made a cloud entry
    // unremovable by every other rule as well.
    [Fact]
    public void ACloudSessionIsStillDroppedForReasonsThatAreNotTheClock()
    {
        var superseded = new HashSet<string>(StringComparer.Ordinal) { "session-1" };

        Assert.Equal(
            SessionManager.ScanVerdict.Superseded,
            Liveness(Healthy(SessionSource.ClaudeCloud, pid: 0), TimeSpan.FromMinutes(1),
                     written: new DateTime(2020, 1, 1), superseded: superseded));
    }

    // --- JudgeLiveness: the backgrounded husk (see TranscriptHandoff) --------

    [Fact]
    public void AHandedOffTurnCostsTheHuskItsOrb()
    {
        // The duplicate-orb bug in one line: the husk is healthy by every other
        // measure — live process, real terminal, file written a moment ago —
        // and the only thing wrong with it is what its own transcript says.
        Assert.Equal(
            SessionManager.ScanVerdict.Backgrounded,
            Liveness(Healthy(state: "generating"), handedToBackground: () => true));
    }

    [Fact]
    public void AHuskFrozenAtWaitingIsNotWaitingOnAnyone()
    {
        // "waiting" is exempt from the lifetime timer because answering the
        // prompt is the only thing that unfreezes it — but a husk's prompt
        // moved to the fork with everything else, so the exemption must not
        // shelter it. The arm sits above the state exemptions for exactly this.
        Assert.Equal(
            SessionManager.ScanVerdict.Backgrounded,
            Liveness(Healthy(state: "waiting"), staleAfter: TimeSpan.FromMinutes(5),
                     written: new DateTime(2020, 1, 1),
                     handedToBackground: () => true));
    }

    [Fact]
    public void AnIdleWindowThatParkedItsConversationAlsoLosesItsOrb()
    {
        // The case TranscriptHandoff alone could not see, and the reason
        // SessionPark exists (CB-30). Opening agents mode from an idle
        // conversation forks it exactly as backgrounding a running turn does,
        // but writes no transcript marker — the CLI's own name for that arm is
        // "idle-fork". So this husk is not frozen mid-generation the way every
        // case above is: it is an ordinary *idle* session, which is precisely
        // what made the duplicate pair impossible to tell apart on screen.
        Assert.Equal(
            SessionManager.ScanVerdict.Backgrounded,
            Liveness(Healthy(state: "idle"), handedToBackground: () => true));
    }

    [Fact]
    public void BackgroundedOutranksExpiredSoTheFileGetsSweptNotJustHidden()
    {
        // Both verdicts hide the orb; only Backgrounded is evidence the sweep
        // may act on (see SessionPresence.EvidenceOfDeath), so the more
        // specific reading has to be the one reported.
        Assert.Equal(
            SessionManager.ScanVerdict.Backgrounded,
            Liveness(Healthy(state: "generating"), staleAfter: TimeSpan.FromMinutes(5),
                     written: new DateTime(2020, 1, 1),
                     handedToBackground: () => true));
    }

    [Fact]
    public void ADroppedSessionNeverPaysForTheTranscriptRead()
    {
        // The closure costs a stat against the disk, which is why it is a
        // closure: a session already superseded, or whose process has exited,
        // is dropped before the question is asked. The counting proves the
        // laziness rather than assuming it.
        var asked = 0;
        Func<bool> counting = () => { asked++; return true; };

        var superseded = new HashSet<string>(StringComparer.Ordinal) { "session-1" };
        Assert.Equal(
            SessionManager.ScanVerdict.Superseded,
            Liveness(Healthy(), superseded: superseded, handedToBackground: counting));

        Assert.Equal(
            SessionManager.ScanVerdict.ProcessGone,
            Liveness(Healthy(), isRunning: AllDead, handedToBackground: counting));

        Assert.Equal(0, asked);
    }

    // --- WantsAgentViewer ----------------------------------------------------

    [Fact]
    public void AnOrdinarySessionWithATerminalIsNeverSentHuntingForAViewer()
    {
        Assert.False(SessionManager.WantsAgentViewer(
            "session-1", Healthy(), Nothing));
    }

    [Fact]
    public void ALeadWithLiveAgentsAndNoTerminalHuntsForItsViewer()
    {
        // A team lead run inside `claude daemon run` has no terminal of its
        // own, and the window you actually watch it in — a `claude agents`
        // process — is nowhere in its process tree, so the hook could never
        // have recorded it.
        var leads = new HashSet<string>(StringComparer.Ordinal) { "session-1" };

        Assert.True(SessionManager.WantsAgentViewer(
            "session-1", Healthy(termProgram: ""), leads));
    }

    [Fact]
    public void ASessionWithNoPidHuntsWithoutTheDaemonBeingAsked()
    {
        // The pid <= 0 arm exists so a session that records no process at all
        // still gets its viewer looked for. isLiveJob is the failing one to
        // show the arm stands on its own.
        Assert.True(SessionManager.WantsAgentViewer(
            "session-1", Healthy(pid: 0, termProgram: ""), Nothing));
    }

    [Fact]
    public void ALiveBackgroundJobThatIsNotALeadDoesNotHunt()
    {
        // The reverse of what this test used to assert, and the reversal is
        // the root cause of the dead background click. Adoption matches a
        // viewer pane by cwd, so a parked job in the same directory as the
        // user's own viewer adopted the very pane the user was sitting in —
        // FocusCore then focused it, returned true, and the click on that
        // job's orb visibly did nothing. Traced live:
        //
        //   Focus id=ed54b99d… shape=Background pane='%2' bin='' tty='ttys008'
        //     FocusCore -> True, detached=False
        //
        // The rescue this arm performed — keeping background orbs past the
        // no-terminal rule — is done properly by the phase exemptions now
        // (RuledOutAsAJob), so a live job that is not a lead has nothing to
        // gain from adoption and everything to lose.
        Assert.False(SessionManager.WantsAgentViewer(
            "session-1", Healthy(termProgram: ""), Nothing));

        // A lead with live agents still hunts — the case adoption exists for.
        var leads = new HashSet<string>(StringComparer.Ordinal) { "session-1" };
        Assert.True(SessionManager.WantsAgentViewer(
            "session-1", Healthy(termProgram: ""), leads));
    }

    [Theory]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    public void OnlyClaudeCodeEverHuntsForAViewer(SessionSource source)
    {
        // There is no `claude agents` window to find for a Codex session, and
        // for a gateway or remote one TryAdopt's cwd-equality match would hand
        // it the tmux pane of unrelated local work — a click that looks like it
        // worked and goes somewhere else entirely.
        Assert.False(SessionManager.WantsAgentViewer(
            "session-1", Healthy(source, pid: 0, termProgram: ""), Nothing));
    }

    [Fact]
    public void TheTtyAloneDoesNotCountAsKnowingATerminal()
    {
        // KnowsATerminal deliberately ignores Tty: it is the one field the
        // hook's walk always fills in, and on its own it can name a tmux pane's
        // pty, which belongs to a detached server rather than to any window. So
        // a tty-only session still goes looking for its viewer.
        Assert.False(SessionManager.KnowsATerminal(new SessionStatus { Tty = "/dev/ttys004" }));

        Assert.True(SessionManager.WantsAgentViewer(
            "session-1", Healthy(pid: 0, termProgram: ""), Nothing));
    }

    [Theory]
    [InlineData("%7", "", "", 0)]
    [InlineData("", "iTerm.app", "", 0)]
    [InlineData("", "", "term-42", 0)]
    [InlineData("", "", "", 4242)]
    public void AnyOneOfTheFourRealTerminalFieldsIsEnough(
        string tmuxPane, string termProgram, string termId, int termPid)
    {
        Assert.True(SessionManager.KnowsATerminal(new SessionStatus
        {
            TmuxPane = tmuxPane, TermProgram = termProgram, TermId = termId, TermPid = termPid
        }));
    }

    // --- JudgeReachability ---------------------------------------------------

    [Fact]
    public void ALocalSessionWithNothingToClickIsDropped()
    {
        var blind = new SessionStatus { Source = SessionSource.ClaudeCode, SessionPid = 4321 };

        Assert.Equal(SessionManager.ScanVerdict.NoTerminal, Reachability(blind));
    }

    // CB-265. Claude Desktop hosts Claude Code with no tty, no TERM_PROGRAM and
    // no pane, measured on a real Mac, so the no-terminal rule dropped every
    // one of its sessions. The entrypoint is what tells them apart from a
    // headless one, and each twin below is the negative control: the same
    // blind file under any other entrypoint is still dropped.
    private static SessionStatus Blind(string entrypoint, string transcript = "") =>
        new()
        {
            Source = SessionSource.ClaudeCode,
            SessionPid = 4321,
            Entrypoint = entrypoint,
            TranscriptPath = transcript,
        };

    [Fact]
    public void AClaudeDesktopSessionKeepsItsOrbWithNoTerminalAtAll()
    {
        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Reachability(Blind(SessionStatus.DesktopEntrypoint)));
    }

    [Theory]
    [InlineData("cli")]
    [InlineData("sdk-cli")]
    [InlineData("")]
    public void TheSameBlindFileUnderAnyOtherEntrypointIsStillDropped(string entrypoint)
    {
        Assert.Equal(
            SessionManager.ScanVerdict.NoTerminal,
            Reachability(Blind(entrypoint)));
    }

    [Fact]
    public void AClaudeDesktopSessionWhoseTranscriptIsMissingIsStillKept()
    {
        // The conversation is in Desktop's Code tab, so a transcript this app
        // cannot find is not "nothing to show".
        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Reachability(Blind(SessionStatus.DesktopEntrypoint, "/gone.jsonl"),
                transcriptExists: TranscriptIsGone));
    }

    [Theory]
    [InlineData("cli")]
    [InlineData("sdk-cli")]
    [InlineData("")]
    public void TheSameMissingTranscriptUnderAnyOtherEntrypointIsNothingToShow(string entrypoint)
    {
        Assert.Equal(
            SessionManager.ScanVerdict.NothingToShow,
            Reachability(Blind(entrypoint, "/gone.jsonl"), transcriptExists: TranscriptIsGone));
    }

    [Fact]
    public void AClaudeDesktopLeadWithLiveAgentsIsKeptAndNeverHuntsForAViewer()
    {
        var leads = new HashSet<string>(StringComparer.Ordinal) { "session-1" };
        var desktop = Blind(SessionStatus.DesktopEntrypoint);

        Assert.Equal(SessionManager.ScanVerdict.Keep, Reachability(desktop, leads));
        Assert.False(SessionManager.WantsAgentViewer("session-1", desktop, leads));

        // Control: the same lead under "cli" does hunt.
        Assert.True(SessionManager.WantsAgentViewer("session-1", Blind("cli"), leads));
        Assert.True(SessionManager.WantsAgentViewer("session-1", Blind(""), leads));
    }

    [Fact]
    public void ALeadWithLiveAgentsKeepsItsOrbWithNoTerminalAtAll()
    {
        // "Agents on screen pointing at nothing is a worse lie than an orb you
        // might not be able to click."
        var lead = new SessionStatus { Source = SessionSource.ClaudeCode, SessionPid = 4321 };
        var leads = new HashSet<string>(StringComparer.Ordinal) { "session-1" };

        Assert.Equal(SessionManager.ScanVerdict.Keep, Reachability(lead, leads));
    }

    [Fact]
    public void ALiveBackgroundJobKeepsItsOrbWithNoTerminalButOnlyForClaudeCode()
    {
        var claude = new SessionStatus { Source = SessionSource.ClaudeCode, SessionPid = 4321 };
        var codex = new SessionStatus { Source = SessionSource.Codex, SessionPid = 4321 };
        var grok = new SessionStatus { Source = SessionSource.Grok, SessionPid = 4321 };

        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Reachability(claude, phase: JobPhase.Parked));

        // Codex and Grok have no Claude daemon jobs to be one of, so the
        // exemption must not reach them however the daemon answers.
        Assert.Equal(
            SessionManager.ScanVerdict.NoTerminal,
            Reachability(codex, phase: JobPhase.Parked));
        Assert.Equal(
            SessionManager.ScanVerdict.NoTerminal,
            Reachability(grok, phase: JobPhase.Parked));
    }

    [Theory]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    public void ARemoteSessionIsNeverDroppedForHavingNoTerminal(SessionSource source)
    {
        // It has no terminal anywhere and never will — that is the point of it.
        // Left ungated this rule alone dropped every gateway orb, every scan.
        var remote = new SessionStatus { Source = source };

        Assert.Equal(SessionManager.ScanVerdict.Keep, Reachability(remote));
    }

    [Fact]
    public void AClaudeCodeFileNamingNoProcessGoesUnlessTheDaemonVouchesForIt()
    {
        // The subagent case: a subagent, a background agent and a status file
        // whose session ended all record no pid and all write "idle", so on
        // disk they are identical. Taking pid-less to mean "background agent"
        // put a permanent orb on screen for every subagent anyone spawned.
        var pidless = new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TermProgram = "iTerm.app"
        };

        Assert.Equal(SessionManager.ScanVerdict.NotALiveJob, Reachability(pidless));
        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Reachability(pidless, phase: JobPhase.Parked));
    }

    [Fact]
    public void ACodexFileNamingNoProcessGoesWhateverTheDaemonSays()
    {
        // The hole this closed: the no-terminal rule requires a pid to fire and
        // the liveness check treats pid 0 as alive, so with the lifetime set to
        // "forever" nothing in the app would ever have removed a pid-less Codex
        // file. `claude agents` has no equivalent to ask about one.
        var codex = new SessionStatus
        {
            Source = SessionSource.Codex, TermProgram = "iTerm.app"
        };

        Assert.Equal(SessionManager.ScanVerdict.NotALiveJob, Reachability(codex));
        Assert.Equal(
            SessionManager.ScanVerdict.NotALiveJob,
            Reachability(codex, phase: JobPhase.Parked));
    }

    [Fact]
    public void APidlessGatewaySessionIsKeptWhateverTheDaemonSaidAboutJobs()
    {
        // A gateway session records no pid and is not a local job, so both
        // job-shaped rules have to leave it alone whatever phase they are handed
        // — the daemon has never heard of it, and the honest answer to "is this a
        // background job" is that the question does not apply.
        //
        // This used to assert that the daemon was not *asked*, by counting calls
        // to a closure. The closure is gone: the phase is decided once per pass
        // and handed in, so "who pays for the lookup" is now a property of the
        // scan rather than of this rule — asserted there, by counting, in
        // SessionScanTests.AMachineWithNothingBackgroundIshOnItNeverAsksTheDaemon.
        var gateway = new SessionStatus { Source = SessionSource.OpenClaw };

        foreach (var phase in new[] { JobPhase.NotAJob, JobPhase.Unknown, JobPhase.Done })
        {
            Assert.Equal(
                SessionManager.ScanVerdict.Keep,
                SessionManager.JudgeReachability(
                    "openclaw:agent:main", gateway, Nothing, phase, TranscriptIsThere));
        }
    }

    // A parked background job keeps its orb, through both verdicts. This is the
    // half of CB-13 that is *not* a change: parking dims an orb and must never
    // remove one, so the session that started this ticket — a pooled worker
    // sitting between turns, alive and resumable, with no terminal of its own —
    // has to survive every rule here exactly as a working job does.
    //
    // Worth pinning because the two facts about it are the same two facts that
    // drop a *finished* job: no terminal, and nothing but the daemon's word to
    // go on. The only thing separating them is which word the daemon said.
    [Fact]
    public void AParkedBackgroundJobKeepsItsOrbJustAsAWorkingOneDoes()
    {
        var parked = new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            State = "idle",
            SessionPid = 4321,
        };

        // "blocked" is live as far as IsLive is concerned — only "done" is not —
        // so the daemon's answer here is the same true a working job gets.
        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Liveness(parked, staleAfter: null));

        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Reachability(parked, phase: JobPhase.Parked));
    }

    [Fact]
    public void AnOrdinarySessionIsKeptWhateverTheDaemonSaid()
    {
        // A session with a terminal and a live process is not the daemon's
        // business either way, so no phase can change what happens to it. Which
        // is also why the scan does not pay for a lookup on its behalf — see
        // SessionPresence.WorthAskingTheDaemon, and the call-counting test in
        // SessionScanTests that pins it.
        foreach (var phase in new[] { JobPhase.NotAJob, JobPhase.Unknown, JobPhase.Done })
        {
            Assert.Equal(
                SessionManager.ScanVerdict.Keep,
                Reachability(Healthy(), phase: phase));
        }
    }

    // The reversal this round made, at the level of the rule that used to drop
    // it. A `done` job's orb went the instant the daemon said so, and the user
    // watched one appear and vanish while looking at it — which reads as a fault,
    // not as a finish. It now survives, to be drawn dimmed and marked as
    // finished, for as long as its status file exists.
    [Fact]
    public void AFinishedJobKeepsItsOrbUntilItsFileGoes()
    {
        var done = new SessionStatus { Source = SessionSource.ClaudeCode, SessionPid = 4321 };

        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Reachability(done, phase: JobPhase.Done));

        // ...where a session the listing genuinely does not name — a subagent, or
        // a file that outlived its session — still goes, which is the distinction
        // the whole rule turns on.
        Assert.Equal(
            SessionManager.ScanVerdict.NoTerminal,
            Reachability(done, phase: JobPhase.NotAJob));
    }

    // And the same for one that recorded no pid at all: a finished job whose file
    // predates the session_pid field is still a finished job, not a subagent.
    [Fact]
    public void AFinishedJobWithNoPidIsAlsoKept()
    {
        var pidless = new SessionStatus { Source = SessionSource.ClaudeCode, SessionPid = 0 };

        Assert.Equal(SessionManager.ScanVerdict.Keep, Reachability(pidless, phase: JobPhase.Done));
        Assert.Equal(
            SessionManager.ScanVerdict.NotALiveJob,
            Reachability(pidless, phase: JobPhase.NotAJob));
    }

    // --- JudgeReachability: nothing to show (CB-9) ----------------------------

    [Fact]
    public void AnUnpromptedBackgroundJobWithNoTranscriptGetsNoOrb()
    {
        // The bug: the live-job exemption on the no-terminal rule waved this
        // through, so the orb stayed and its chat could only ever open blank.
        // The exemption is right about the terminal — a background job has none
        // by nature — and says nothing about whether there is a conversation.
        Assert.Equal(
            SessionManager.ScanVerdict.NothingToShow,
            Reachability(UnpromptedJob(), phase: JobPhase.Parked,
                         transcriptExists: TranscriptIsGone));
    }

    [Fact]
    public void TheSameJobKeepsItsOrbOnceThereIsATranscriptToRead()
    {
        // The other half of the same machine's evidence: job "evidence (2)",
        // identically shaped and identically terminal-less, with 86KB of
        // transcript being written to it. Its chat works, so its orb stays.
        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Reachability(UnpromptedJob(), phase: JobPhase.Parked,
                         transcriptExists: TranscriptIsThere));
    }

    [Fact]
    public void ATerminalIsEnoughOnItsOwnEvenWithNoTranscript()
    {
        // Deliberately narrow: the click still lands, so the orb still earns
        // its place. A session in a real terminal has no transcript for its
        // first moment, and flickering its orb would be the worse bug.
        var inATerminal = UnpromptedJob();
        inATerminal.TermProgram = "iTerm.app";

        var inTmux = UnpromptedJob();
        inTmux.TmuxPane = "%4";

        var onWindows = UnpromptedJob();
        onWindows.TermPid = 9182;

        foreach (var reachable in new[] { inATerminal, inTmux, onWindows })
        {
            Assert.Equal(
                SessionManager.ScanVerdict.Keep,
                Reachability(reachable, phase: JobPhase.Parked,
                             transcriptExists: TranscriptIsGone));
        }
    }

    [Fact]
    public void ATtyAloneIsNotATerminalForThisRule()
    {
        // A deliberate reversal: this case used to sit in the list above. The
        // daemon runs every background worker under a pty host, so its sessions
        // all record a real /dev/ttysNN that no window anywhere shows — and the
        // old "and no tty" clause waved every one of them through. Seen live as
        // session de995bd9: idle, untitled, tty ttys006, no transcript anywhere,
        // not in the daemon's own listing, drawn as a blank orb whose chat
        // could only ever open empty. A tty alone already doesn't count as a
        // terminal for KnowsATerminal; now this rule agrees with it.
        var withATty = UnpromptedJob();
        withATty.Tty = "/dev/ttys006";

        Assert.Equal(
            SessionManager.ScanVerdict.NothingToShow,
            Reachability(withATty, phase: JobPhase.Parked,
                         transcriptExists: TranscriptIsGone));

        // NotAJob too — the live phantom was absent from the listing, which is
        // precisely why no phase-shaped rule could have caught it.
        Assert.Equal(
            SessionManager.ScanVerdict.NothingToShow,
            Reachability(withATty, phase: JobPhase.NotAJob,
                         transcriptExists: TranscriptIsGone));
    }

    [Fact]
    public void ATtyOnlySessionKeepsItsOrbOnceItsTranscriptExists()
    {
        // What protects a session in a terminal that sets no TERM_PROGRAM —
        // ssh in from another machine, an emulator that doesn't announce
        // itself — is its conversation, not its tty. Its transcript appears
        // within the first exchange, so the cost of the reversal above is an
        // orb arriving a scan or two late, not an orb that never arrives.
        var withATty = UnpromptedJob();
        withATty.Tty = "/dev/ttys004";

        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Reachability(withATty, phase: JobPhase.NotAJob,
                         transcriptExists: TranscriptIsThere));
    }

    [Fact]
    public void ALeadWithLiveAgentsIsExemptFromThisRuleToo()
    {
        // Same reasoning as the no-terminal rule's own exemption: agents drawn
        // on screen pointing at nothing is a worse lie than a thin chat.
        var leads = new HashSet<string>(StringComparer.Ordinal) { "session-1" };

        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Reachability(UnpromptedJob(), leads, JobPhase.Parked,
                         transcriptExists: TranscriptIsGone));
    }

    [Fact]
    public void ATranscriptPathTheHookNeverRecordedAnswersKeepNotDrop()
    {
        // "Nowhere recorded" means this app does not know where the
        // conversation would be, not that there isn't one — Codex's
        // transcript_path is nullable and the chat panel falls back to hunting
        // the projects directories by session id. Same reasoning as
        // BackgroundJobs.IsLive answering true for a listing it could not read.
        //
        // The disk is not touched at all in that case, which is also what keeps
        // this O(1) per session per scan.
        var asked = new List<string>();
        var noPath = UnpromptedJob(transcriptPath: "");

        var verdict = SessionManager.JudgeReachability(
            "session-1", noPath, Nothing, JobPhase.Parked,
            path => { asked.Add(path); return false; });

        Assert.Equal(SessionManager.ScanVerdict.Keep, verdict);
        Assert.Empty(asked);
    }

    [Theory]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    public void AGatewaySessionIsNeverDroppedForHavingNoTranscriptOnThisDisk(
        SessionSource source)
    {
        // Its transcript is on the machine running the gateway, so asking this
        // disk about it can only ever answer no. Left ungated this rule alone
        // would drop every gateway orb, every scan — the same trap the
        // no-terminal rule above already carries a comment about.
        Assert.Equal(
            SessionManager.ScanVerdict.Keep,
            Reachability(UnpromptedJob(source), transcriptExists: TranscriptIsGone));
    }

    [Fact]
    public void NothingToShowIsAnsweredBeforeNoTerminal()
    {
        // Both rules fit a session with no terminal and no transcript, and
        // which one answers is the half a regression shows up in — the enum
        // exists so that "dropped" is never the whole story. NothingToShow is
        // the more specific reading and comes first.
        Assert.Equal(
            SessionManager.ScanVerdict.NothingToShow,
            Reachability(UnpromptedJob(), transcriptExists: TranscriptIsGone));
    }

    [Fact]
    public void ACodexSessionWithNeitherIsDroppedForTheSameReason()
    {
        // Codex is exempt from the live-job exemption, not from this rule: it
        // has no background jobs to be one of, but a Codex session with no
        // terminal and no transcript is just as inert.
        Assert.Equal(
            SessionManager.ScanVerdict.NothingToShow,
            Reachability(UnpromptedJob(SessionSource.Codex),
                         phase: JobPhase.Parked,
                         transcriptExists: TranscriptIsGone));
    }
}
