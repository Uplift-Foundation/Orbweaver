using System;
using Xunit;

namespace Orbweaver.Tests;

// SessionPresence's classifier: which shape of local session a status file
// describes, whether anything is on the other end of it, and which of the two
// lifecycle actions its orb may offer.
//
// Every case below is one of the orbs on the screenshot this ticket was filed
// from — fifteen orbs on a machine the user considered idle, all breathing, all
// identical. The rules are what tell them apart, and the mistakes they can make
// are directional: parking an orb that is genuinely working says a session at
// work is doing nothing, which is worse than the bug being fixed. So the matrix
// below is deliberately as interested in what must *not* be parked as in what
// must.
public class SessionPresenceTests
{
    private static SessionStatus Status(
        string state = "idle", string lead = "", int pid = 4321,
        SessionSource source = SessionSource.ClaudeCode) =>
        new() { State = state, Lead = lead, SessionPid = pid, Source = source };

    // --- ShapeOf -------------------------------------------------------------

    // The job phase decides first and unconditionally: it is the only signal
    // from outside this app, and if the daemon lists a session as a job then it
    // is one whatever the file looks like.
    // Looped rather than a [Theory] here and below: JobPhase is internal, and a
    // public xUnit test method may not take an internal parameter type. The enum
    // belongs beside BackgroundJobs rather than being widened to suit the
    // harness.
    [Fact]
    public void AnySessionTheDaemonListsAsAJobIsABackgroundSession()
    {
        foreach (var phase in new[] { JobPhase.Working, JobPhase.Parked, JobPhase.Done })
        {
            Assert.Equal(LocalSessionShape.Background, SessionPresence.ShapeOf(Status(), phase));
        }
    }

    // A finished job is still a background session. The rules that drop its orb
    // live in JudgeReachability and the sweep, and neither of them needs this to
    // lie about what it was.
    [Fact]
    public void AFinishedJobIsStillABackgroundSessionRatherThanATerminalOne()
    {
        Assert.Equal(
            LocalSessionShape.Background,
            SessionPresence.ShapeOf(Status(), JobPhase.Done));
    }

    // A background worker is started by the daemon and carries no
    // --parent-session-id, so nothing ever writes a Lead onto one — but the
    // order is asserted rather than left to that being permanently true.
    [Fact]
    public void AJobThatSomehowCarriesALeadIsStillABackgroundSession()
    {
        Assert.Equal(
            LocalSessionShape.Background,
            SessionPresence.ShapeOf(Status(lead: "lead-1"), JobPhase.Parked));
    }

    [Fact]
    public void ASessionWithALeadAndNoJobRowIsATeammate()
    {
        Assert.Equal(
            LocalSessionShape.Teammate,
            SessionPresence.ShapeOf(Status(lead: "lead-1"), JobPhase.NotAJob));
    }

    [Fact]
    public void ASessionWithNoLeadAndNoJobRowIsATerminalSession()
    {
        Assert.Equal(
            LocalSessionShape.Terminal,
            SessionPresence.ShapeOf(Status(), JobPhase.NotAJob));
    }

    // An unreadable listing must not reclassify anything. Unknown falls through
    // to the team test exactly as NotAJob does, which is what keeps a momentary
    // CLI failure from turning every teammate on screen into a terminal session
    // — and, one rule down, from un-parking every orphan in the same tick.
    [Fact]
    public void AnUnreadableListingLeavesTheTeamAndTerminalReadingUntouched()
    {
        Assert.Equal(
            LocalSessionShape.Teammate,
            SessionPresence.ShapeOf(Status(lead: "lead-1"), JobPhase.Unknown));

        Assert.Equal(
            LocalSessionShape.Terminal,
            SessionPresence.ShapeOf(Status(), JobPhase.Unknown));
    }

    // --- WorthAskingTheDaemon ------------------------------------------------

    // The gate that keeps the subprocess off a quiet machine. `claude agents
    // --json` is cached for ten seconds and the scan runs every two, so a rule
    // that asked about every session would spawn a `claude` process every ten
    // seconds forever — on a machine with nothing but terminal sessions, in
    // service of a question whose answer cannot change anything.
    [Fact]
    public void OnlyASessionTheDaemonCouldKnowAboutIsWorthAsking()
    {
        // A background worker's file: a pid of its own, and no terminal, because
        // the daemon that runs it has none to inherit.
        Assert.True(SessionPresence.WorthAskingTheDaemon(
            Status(), knowsATerminal: false, sharesItsPid: false));

        // A hook older than the session_pid field. Asked whatever else is true,
        // which is the rule that predates this one.
        Assert.True(SessionPresence.WorthAskingTheDaemon(
            Status(pid: 0), knowsATerminal: true, sharesItsPid: false));

        // An ordinary session: a window on this machine, its own pid, and nothing
        // else sharing it. Almost every session almost all of the time, and the
        // one case that must not spend a subprocess.
        Assert.False(SessionPresence.WorthAskingTheDaemon(
            Status(), knowsATerminal: true, sharesItsPid: false));
    }

    // CB-265. Claude Desktop's sessions have a pid and no terminal, which is
    // exactly the first shape above — and the daemon has never heard of them,
    // so asking would spawn `claude agents --json` for an answer that cannot
    // matter. The "cli" twin is the control that the gate itself is unchanged.
    [Fact]
    public void AClaudeDesktopSessionIsNeverWorthAskingTheDaemonAbout()
    {
        var desktop = Status();
        desktop.Entrypoint = SessionStatus.DesktopEntrypoint;
        var cli = Status();
        cli.Entrypoint = "cli";

        Assert.False(SessionPresence.WorthAskingTheDaemon(
            desktop, knowsATerminal: false, sharesItsPid: false));
        Assert.False(SessionPresence.WorthAskingTheDaemon(
            desktop, knowsATerminal: true, sharesItsPid: true));
        Assert.True(SessionPresence.WorthAskingTheDaemon(
            cli, knowsATerminal: false, sharesItsPid: false));
    }

    [Fact]
    public void AClaudeDesktopSessionCanBeDismissedButNotEnded()
    {
        var desktop = Status();
        desktop.Entrypoint = SessionStatus.DesktopEntrypoint;

        Assert.True(SessionPresence.CanDismiss(desktop));
        Assert.False(SessionPresence.CanEndSession(desktop));

        // Controls: same pid, any other entrypoint, End is offered.
        foreach (var entrypoint in new[] { "cli", "sdk-cli", "" })
        {
            var other = Status();
            other.Entrypoint = entrypoint;
            Assert.True(SessionPresence.CanEndSession(other));
        }
    }

    // The third shape, and the one that was missing. An Agent-View-dispatched
    // background session does not fork a process — it starts a second
    // conversation inside the `claude` process already running — so its file
    // names a live interactive session's pid. BackgroundJobs' own comment names
    // it as one of exactly two shapes only the daemon can settle, and the first
    // version of this gate refused to ask about it.
    //
    // Refused twice over, in fact: InheritTerminalInfo donates terminal fields
    // between files sharing a pid, so such a file *acquires* a terminal before
    // this is asked and then reads as an ordinary session. Which is also why the
    // clause needs no pre-inheritance snapshot beside it — the donation only
    // happens inside a (pid, source) group, so anything it could have touched
    // shares its pid by definition.
    [Fact]
    public void AFileSharingItsPidWithAnotherIsAlwaysWorthAsking()
    {
        Assert.True(SessionPresence.WorthAskingTheDaemon(
            Status(), knowsATerminal: true, sharesItsPid: true));

        Assert.True(SessionPresence.WorthAskingTheDaemon(
            Status(pid: 0), knowsATerminal: true, sharesItsPid: true));
    }

    // Codex has no background jobs to be one of, and a gateway or bridged
    // session is not on this machine at all — asking the local daemon about one
    // is asking about a session it has never heard of, once per scan, forever.
    [Fact]
    public void TheDaemonIsNeverAskedAboutAnythingButClaudeCode()
    {
        var others = new[]
        {
            SessionSource.Codex, SessionSource.Grok, SessionSource.OpenClaw, SessionSource.RemoteControl,
        };

        foreach (var source in others)
        {
            Assert.False(SessionPresence.WorthAskingTheDaemon(
                Status(source: source), knowsATerminal: false, sharesItsPid: false));

            Assert.False(SessionPresence.WorthAskingTheDaemon(
                Status(source: source, pid: 0), knowsATerminal: false, sharesItsPid: false));

            Assert.False(SessionPresence.WorthAskingTheDaemon(
                Status(source: source), knowsATerminal: true, sharesItsPid: true));
        }
    }

    // --- PresenceOf: background sessions -------------------------------------

    private static OrbPresence Presence(
        LocalSessionShape shape = LocalSessionShape.Background,
        string state = "idle",
        JobPhase phase = JobPhase.Parked,
        bool leadSeen = false,
        bool leadIsLiveJob = false,
        bool attached = false) =>
        SessionPresence.PresenceOf(shape, state, phase, leadSeen, leadIsLiveJob, attached);

    // The daemon's own word for a blocked job is "needs input", and several of
    // the ones this was written for are literally holding a question — so a
    // parked job is not merely quiet, and the mark is the difference between
    // "nothing here" and "something here for you".
    [Fact]
    public void ABackgroundJobBetweenTurnsNeedsInput()
    {
        Assert.Equal(OrbPresence.NeedsInput, Presence());
    }

    [Fact]
    public void ABackgroundJobMidTurnIsPresent()
    {
        Assert.Equal(
            OrbPresence.Present,
            Presence(state: "generating", phase: JobPhase.Working));
    }

    // A job that is over. Marked differently from one that needs input because
    // the two are opposite instructions — one wants you, one wants nothing ever
    // again — and it is on its way off the screen anyway: the sweep has the same
    // ten minutes' evidence by the time this is drawn.
    [Fact]
    public void AFinishedJobIsFinishedWhateverElseIsTrue()
    {
        Assert.Equal(OrbPresence.Finished, Presence(phase: JobPhase.Done));

        // Including with somebody attached to it, which is reading rather than
        // working, and including a file that says it is mid-turn — "done" is the
        // daemon's last word on a job and there is nothing after it.
        Assert.Equal(OrbPresence.Finished, Presence(phase: JobPhase.Done, attached: true));
        Assert.Equal(
            OrbPresence.Finished,
            Presence(state: "generating", phase: JobPhase.Done));
    }

    // The contradiction the user hit within minutes: they attached to all three
    // parked sessions and the orbs stayed grey. Attaching changes nothing this
    // app was watching — the status file records the worker's ancestry and never
    // the viewer's, and the daemon still says "blocked" — so the only place the
    // person's presence exists is the process table.
    [Fact]
    public void AParkedJobSomebodyIsAttachedToIsPresent()
    {
        Assert.Equal(OrbPresence.Present, Presence(attached: true));
    }

    // The cache-lag fix, and the reason the file's own state is consulted at all.
    //
    // The daemon's listing is cached for ten seconds; the hook rewrites the
    // status file the instant a job resumes. So when the two disagree, the file
    // is the fresher of them — and requiring it to still say "idle" is what makes
    // coming back to life prompt (the next 2s scan) even though going quiet lags.
    // That asymmetry is deliberate: an orb still dim while work visibly resumes
    // is a lie about the thing the user is watching happen.
    [Theory]
    [InlineData("generating")]
    [InlineData("waiting")]
    public void ABlockedRowWhoseFileSaysWorkResumedIsPresent(string state)
    {
        Assert.Equal(OrbPresence.Present, Presence(state: state));
    }

    // Fail open. Unknown is "there was no listing", and no orb may change what it
    // looks like on the strength of a question that could not be asked.
    [Fact]
    public void OnlyBlockedEverMarksABackgroundOrbAsNeedingInput()
    {
        var phases = new[] { JobPhase.Unknown, JobPhase.NotAJob, JobPhase.Working };

        foreach (var phase in phases)
        {
            Assert.Equal(OrbPresence.Present, Presence(phase: phase));
        }
    }

    // --- PresenceOf: teammates and terminals ---------------------------------

    // The orphan case: a real claude process in a detached tmux socket, whose
    // lead has gone. Dimmed with no mark, deliberately — nothing is waiting on
    // the user and nothing has finished, so there is nothing to say beyond the
    // dimming itself. Its arrows have already silently vanished, which is why the
    // orb is the last thing left saying anything about it.
    [Fact]
    public void ATeammateWhoseLeadHasGoneIsParkedWithNoMark()
    {
        Assert.Equal(
            OrbPresence.Parked,
            Presence(shape: LocalSessionShape.Teammate, phase: JobPhase.NotAJob));
    }

    [Fact]
    public void ATeammateWhoseLeadIsOnThisScanIsPresent()
    {
        Assert.Equal(
            OrbPresence.Present,
            Presence(shape: LocalSessionShape.Teammate, phase: JobPhase.NotAJob, leadSeen: true));
    }

    // A team led from a background job is the ordinary case, not an exception:
    // the lead has no status file of its own to be seen through, so the daemon's
    // listing is the only place it exists. Dimming its members would dim a live
    // team.
    [Fact]
    public void ATeammateWhoseLeadIsALiveJobIsPresent()
    {
        Assert.Equal(
            OrbPresence.Present,
            Presence(shape: LocalSessionShape.Teammate, phase: JobPhase.NotAJob,
                leadIsLiveJob: true));
    }

    // A teammate's own state is not consulted, unlike a background session's:
    // there is no cached listing in the way here — the lead either is on this
    // scan or is not — and a member that is mid-turn while its lead has gone is
    // still orphaned. Asserted so the two rules do not quietly converge.
    [Theory]
    [InlineData("idle")]
    [InlineData("generating")]
    [InlineData("waiting")]
    public void AnOrphanedTeammateIsParkedWhateverItsFileSays(string state)
    {
        Assert.Equal(
            OrbPresence.Parked,
            Presence(shape: LocalSessionShape.Teammate, state: state, phase: JobPhase.NotAJob));
    }

    // Somebody at a keyboard. A terminal session between turns is still a
    // terminal session — sitting there waiting for you is the whole of what it
    // does — so nothing about this shape is ever dimmed, whatever else is true.
    [Fact]
    public void ATerminalSessionIsAlwaysPresent()
    {
        var cases = new (JobPhase Phase, string State)[]
        {
            (JobPhase.NotAJob, "idle"),
            (JobPhase.Unknown, "idle"),
            (JobPhase.NotAJob, "generating"),

            // Including the two combinations that could not happen — a terminal
            // session the daemon calls blocked or done — because ShapeOf would
            // have called those Background, and this rule must not be the thing
            // relied on to notice.
            (JobPhase.Parked, "idle"),
            (JobPhase.Done, "idle"),
        };

        foreach (var (phase, state) in cases)
        {
            Assert.Equal(
                OrbPresence.Present,
                Presence(shape: LocalSessionShape.Terminal, state: state, phase: phase));
        }
    }

    // --- HasAttachClient -----------------------------------------------------

    // `claude attach` accepts the short job id and echoes it back that way, so a
    // window opened by hand with `claude attach bd7919f8` has to count as session
    // bd7919f8-…. Compared by prefix in both directions for that reason, the same
    // way AgentTeamViewer already compares them.
    [Fact]
    public void AnAttachClientIsMatchedByEitherFormOfTheId()
    {
        var shortForm = new[] { "0e043819" };

        Assert.True(SessionPresence.HasAttachClient(
            shortForm, "0e043819-3c45-4f1a-9c2b-8d4e5f6a7b8c"));

        var fullForm = new[] { "0e043819-3c45-4f1a-9c2b-8d4e5f6a7b8c" };
        Assert.True(SessionPresence.HasAttachClient(fullForm, "0e043819"));
    }

    [Fact]
    public void AnUnrelatedAttachClientDoesNotCount()
    {
        Assert.False(SessionPresence.HasAttachClient(
            new[] { "5f6960b2" }, "0e043819-3c45-4f1a-9c2b-8d4e5f6a7b8c"));
    }

    [Fact]
    public void WithNothingAttachedNothingMatches()
    {
        Assert.False(SessionPresence.HasAttachClient(
            Array.Empty<string>(), "0e043819-3c45-4f1a-9c2b-8d4e5f6a7b8c"));
    }

    // A scan that could not be done answers "attached", and the direction is the
    // whole point. Wrong-true leaves a genuinely parked orb bright, which is this
    // branch's original bug in its mildest form. Wrong-false dims a session the
    // user is sitting in and typing at — the contradiction that prompted the rule.
    // Only one of the two ways to be wrong argues with the person at the screen.
    [Fact]
    public void AScanThatCouldNotBeDoneCountsAsAttached()
    {
        Assert.True(SessionPresence.HasAttachClient(null, "0e043819"));

        // And it reaches all the way through: nothing is dimmed on the strength
        // of a question that could not be asked.
        Assert.Equal(OrbPresence.Present, Presence(attached: true));
    }

    // Nothing was asked, so nothing matches — rather than every attach client on
    // the machine matching an empty id by prefix, which is what a bare
    // StartsWith would do.
    [Fact]
    public void AnEmptySessionIdMatchesNothing()
    {
        Assert.False(SessionPresence.HasAttachClient(new[] { "0e043819" }, ""));
    }

    // --- KnownAttachClient ---------------------------------------------------

    // The click's half of the same question, and the direction is the whole
    // reason it is a second function rather than a second caller. It is the one
    // authority on "is anything already attached" for a click — it replaced
    // AgentTeamViewer.AttachedAlready, which asked the identical question of the
    // identical `ps -eo args=` population and differed only in being uncached.
    [Fact]
    public void TheClickSeesTheSameMatchesTheDimmingRuleDoes()
    {
        const string full = "0e043819-3c45-4f1a-9c2b-8d4e5f6a7b8c";

        Assert.True(SessionPresence.KnownAttachClient(new[] { "0e043819" }, full));
        Assert.False(SessionPresence.KnownAttachClient(new[] { "5f6960b2" }, full));
        Assert.False(SessionPresence.KnownAttachClient(Array.Empty<string>(), full));
        Assert.False(SessionPresence.KnownAttachClient(new[] { "0e043819" }, ""));
    }

    // And where the two part company. A scan that could not be done means
    // "attached" for dimming, because being wrong there dims a session somebody
    // is typing into; it means "not attached" for a click, because being wrong
    // there raises an app with no such window and creates nothing — a gesture
    // that does nothing, which is the complaint the whole click ladder exists to
    // answer. One duplicate pane is visible and closable; a dead click is
    // neither.
    [Fact]
    public void AScanThatCouldNotBeDoneStopsTheDimmingAndNotTheClick()
    {
        Assert.True(SessionPresence.HasAttachClient(null, "0e043819"));
        Assert.False(SessionPresence.KnownAttachClient(null, "0e043819"));
    }

    // --- SameJobId -----------------------------------------------------------

    // The prefix rule itself, now that it is one function rather than three
    // copies of an inline comparison. It decides whether a click opens a *second*
    // window onto a conversation somebody is already reading, which is why the
    // copies were worth collapsing: two of them disagreeing about one pair of ids
    // is a duplicate window or a dead click, and nothing in between.
    [Fact]
    public void TwoFormsOfOneJobIdAreTheSameJob()
    {
        const string full = "0e043819-3c45-4f1a-9c2b-8d4e5f6a7b8c";

        Assert.True(SessionPresence.SameJobId(full, "0e043819"));
        Assert.True(SessionPresence.SameJobId("0e043819", full));
        Assert.True(SessionPresence.SameJobId(full, full));
    }

    [Fact]
    public void DifferentJobIdsAreNotTheSameJob()
    {
        Assert.False(SessionPresence.SameJobId("0e043819", "5f6960b2"));

        // Sharing a leading character is not sharing a prefix in either
        // direction — asserted because a looser rule would match half the
        // machine's jobs to each other.
        Assert.False(SessionPresence.SameJobId("0e043819", "0f043819"));
    }

    // Empty matches nothing, in either position. Without this a bare StartsWith
    // would make an empty id match every job on the machine — and the empty case
    // is real on both sides: a `ps` line whose third word is missing, and an orb
    // whose session id never reached the click.
    [Fact]
    public void AnEmptyIdIsNeverTheSameJobAsAnything()
    {
        Assert.False(SessionPresence.SameJobId("", "0e043819"));
        Assert.False(SessionPresence.SameJobId("0e043819", ""));
        Assert.False(SessionPresence.SameJobId("", ""));
    }

    // And through the collection rule, since that is where an empty id actually
    // arrives from: AttachedJobIds parses argv, and a malformed line can put a
    // blank in the set beside good ones.
    [Fact]
    public void ABlankInTheAttachedSetDoesNotMatchEverything()
    {
        Assert.False(SessionPresence.HasAttachClient(
            new[] { "", "5f6960b2" }, "0e043819-3c45-4f1a-9c2b-8d4e5f6a7b8c"));

        Assert.True(SessionPresence.HasAttachClient(
            new[] { "", "0e043819" }, "0e043819-3c45-4f1a-9c2b-8d4e5f6a7b8c"));
    }

    // --- TitleSaysViewing ----------------------------------------------------

    private const string Glyph = "\u2733 ";
    private const string Work = "Claude desktop app multiple profiles bug";

    // What Claude Code's TUI actually writes, measured off a live pane: the
    // glyph, a space, then the conversation title verbatim.
    [Fact]
    public void APaneTitledByClaudeCodeMatchesItsSession()
    {
        Assert.True(SessionPresence.TitleSaysViewing(Glyph + Work, Work));
    }

    // The false positive this whole clause exists for. A pane title is writable
    // by any program — one `printf` of an OSC 2 escape — so an editor opened on a
    // file of the same name would otherwise be mistaken for the session, and the
    // click would be sent to it. Without the glyph there is no match.
    [Fact]
    public void APaneSomeOtherProgramTitledDoesNotMatch()
    {
        Assert.False(SessionPresence.TitleSaysViewing(Work, Work));
        Assert.False(SessionPresence.TitleSaysViewing("vim " + Work, Work));
        Assert.False(SessionPresence.TitleSaysViewing("~/src \u2014 " + Work, Work));
    }

    // A different glyph is not this glyph. Stated because the observed set is one
    // member and a future version could grow another, and the direction of that
    // failure is the safe one: no match sends the click down the attach ladder,
    // where the worst case is a duplicate pane the user can see and close.
    [Fact]
    public void AnotherGlyphIsNotTheOne()
    {
        Assert.False(SessionPresence.TitleSaysViewing("\u2733" + Work, Work));   // no space
        Assert.False(SessionPresence.TitleSaysViewing("\u273b " + Work, Work));  // a spinner frame
        Assert.False(SessionPresence.TitleSaysViewing("* " + Work, Work));
    }

    // An exact suffix, not a contains: a pane whose title merely mentions the
    // session — a shell sitting in a directory of that name, a log being tailed —
    // is not showing the conversation.
    [Fact]
    public void APaneThatOnlyMentionsTheTitleDoesNotMatch()
    {
        Assert.False(SessionPresence.TitleSaysViewing(Glyph + Work + " (log)", Work));
        Assert.False(SessionPresence.TitleSaysViewing(Glyph + "re: " + Work + " notes", Work));

        // A longer conversation title that ends with the shorter one is a genuine
        // suffix match and is allowed — the app cannot tell those apart, and the
        // process check plus the claimed-pane rule are what narrow it.
        Assert.True(SessionPresence.TitleSaysViewing(Glyph + "re: " + Work, Work));
    }

    // Nothing to match on either side. A session whose title the hook never
    // caught is the common case here, and it must fall through rather than match
    // every Claude pane on the machine.
    [Theory]
    [InlineData(null, "x")]
    [InlineData("", "x")]
    [InlineData("\u2733 x", null)]
    [InlineData("\u2733 x", "")]
    [InlineData(null, null)]
    public void NothingToMatchIsNotAMatch(string? paneTitle, string? sessionTitle)
    {
        Assert.False(SessionPresence.TitleSaysViewing(paneTitle, sessionTitle));
    }

    // The glyph alone, with no title after it — the degenerate case that would
    // otherwise satisfy both StartsWith and EndsWith against a title that is
    // itself the glyph.
    [Fact]
    public void TheGlyphWithNothingAfterItIsNotAMatch()
    {
        Assert.False(SessionPresence.TitleSaysViewing("\u2733 ", "\u2733 "));
        Assert.False(SessionPresence.TitleSaysViewing("\u2733", "\u2733"));
    }

    // --- LooksLikeClaudeBinary -----------------------------------------------

    // Both shapes observed live on one machine, and neither is what the obvious
    // rule catches. An interactive session runs as plain `claude`; a team member
    // runs as the versioned install path, whose file *name* is a version number —
    // which is also what tmux reports as that pane's current command.
    [Fact]
    public void BothShapesOfTheClaudeBinaryAreRecognised()
    {
        Assert.True(SessionPresence.LooksLikeClaudeBinary("claude"));
        Assert.True(SessionPresence.LooksLikeClaudeBinary("/Users/w/.local/bin/claude"));
        Assert.True(SessionPresence.LooksLikeClaudeBinary(
            "/Users/w/.local/share/claude/versions/2.1.246"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-zsh")]
    [InlineData("vim")]
    [InlineData("/usr/bin/tmux")]
    [InlineData("/Users/w/claude-notes/versions/1.0")]
    public void AnythingElseIsNotTheClaudeBinary(string? argv0)
    {
        Assert.False(SessionPresence.LooksLikeClaudeBinary(argv0));
    }

    // --- ClaimStillHolds -----------------------------------------------------

    // The claim agrees with the pane's title, which is the only case where the
    // title cannot tell the two apart — and so the only case where the claim is
    // worth honouring. It is also the case the exclusion was added for: every
    // member of an agent team inherits the team session's title, so a teammate's
    // claim on its own pane looks exactly like this, and without it clicking a
    // lead would focus a teammate's conversation.
    [Fact]
    public void AClaimThatAgreesWithTheDisplayedTitleHolds()
    {
        Assert.True(SessionPresence.ClaimStillHolds(Work, Glyph + Work));
    }

    // The pane is showing something else, so the process living there is not what
    // is on screen. The displayed title trumps the resident claim: the TUI titles
    // what it *shows*, the status file records what *lives* there, and only the
    // first is talking about a viewer.
    [Fact]
    public void AClaimTheDisplayedTitleContradictsIsReleased()
    {
        Assert.False(SessionPresence.ClaimStillHolds(Work, Glyph + "Something else entirely"));
    }

    // An untitled claimant releases its claim, and this is the case the machine
    // settled by photographing itself. The claude process in the user's pane is
    // the recorded session_pid of a session whose title was never captured, and
    // that pane was rendering a *different* session's conversation — true as
    // process residence and stale as viewer evidence at the same moment.
    //
    // Keeping the claim, which is what this rule did first, left the user's own
    // pane out of candidacy so a click went on opening a duplicate beside the
    // conversation it was clicked from. Confirming it by session_pid-in-the-tree
    // would have done the same, since the pid genuinely is there. Only the title
    // distinguishes them, so only the title decides.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnUntitledClaimantCannotHoldAPaneOutOfCandidacy(string? claimantTitle)
    {
        Assert.False(SessionPresence.ClaimStillHolds(claimantTitle, Glyph + Work));
        Assert.False(SessionPresence.ClaimStillHolds(claimantTitle, "anything at all"));
        Assert.False(SessionPresence.ClaimStillHolds(claimantTitle, null));
    }

    // A pane no longer showing a session at all — someone quit and typed
    // something. Nothing there is evidence about a viewer.
    [Fact]
    public void AClaimOnAPaneNoLongerShowingASessionIsReleased()
    {
        Assert.False(SessionPresence.ClaimStillHolds(Work, "~/src"));
        Assert.False(SessionPresence.ClaimStillHolds(Work, ""));
        Assert.False(SessionPresence.ClaimStillHolds(Work, null));
    }

    // Both of this machine's shapes side by side, which is the pair that has to
    // come out differently for the feature to work at all.
    [Fact]
    public void TheTeammatesPaneStaysExcludedAndTheUsersOwnPaneDoesNot()
    {
        // %53: teammate, title inherited from the team, pane title the same.
        // Indistinguishable by title, so the claim decides.
        Assert.True(SessionPresence.ClaimStillHolds(Work, Glyph + Work));

        // %6: claimant's recorded title empty, pane showing another session's
        // conversation. Distinguishable, so the title decides.
        Assert.False(SessionPresence.ClaimStillHolds("", Glyph + Work));
    }

    // --- AnswersTheClick -----------------------------------------------------

    // Both found verdicts answer the click, and they answer it the same way. Round
    // ten's correction: being tmux-active is not being on the user's screen. These
    // orbs float over every application and the terminal is routinely behind a
    // browser when one is clicked, so a pane can be current in its tmux session and
    // invisible on the desktop at the same moment — which is how "mechanically
    // perfect" and "still doesn't work" were both true of the same click.
    [Fact]
    public void BothFoundVerdictsAnswerTheClick()
    {
        Assert.True(SessionPresence.AnswersTheClick(
            SessionPresence.ViewerVerdict.TheUserIsLookingAtIt));

        Assert.True(SessionPresence.AnswersTheClick(
            SessionPresence.ViewerVerdict.ElsewhereInTmux));
    }

    // And nothing found means the click carries on down the attach ladder, which is
    // the one case that must still create something.
    [Fact]
    public void NothingFoundDoesNotAnswerTheClick()
    {
        Assert.False(SessionPresence.AnswersTheClick(SessionPresence.ViewerVerdict.NoneFound));
    }

    // The invariant the single branch rests on: a verdict that answers the click
    // always carries a pane to act on. If a found verdict could come back with no
    // pane, the branch would fall through to the ladder and mint a window for a
    // session that is already on screen — the exact failure this whole step exists
    // to prevent.
    [Fact]
    public void EveryVerdictThatAnswersTheClickCarriesAPane()
    {
        foreach (var candidates in new[]
                 {
                     TheFourPanes(),                                             // → looking at it
                     new[] { Pane("%7", "placement:1", active: false) },          // → elsewhere
                 })
        {
            var (verdict, found) = SessionPresence.ViewerAmong(
                candidates, Attached(""), Watching(("", "placement:1")));

            Assert.True(SessionPresence.AnswersTheClick(verdict));
            Assert.NotNull(found);
            Assert.NotEqual("", found!.Value.Pane);
        }
    }

    // --- ViewerAmong: the universe first -------------------------------------

    // The machine that forced round nine, as a fixture. Four panes, one identical
    // title. `%6` is the user's real viewer on the only attached server; the other
    // three are two teammates and a remote-control relay on a detached
    // `claude-swarm-<pid>` socket — and teammates title their own TUIs with the
    // team title, so they are perfect impostors by construction.
    private const string Swarm = "/tmp/tmux-501/claude-swarm-78137";

    private static SessionPresence.ViewerPane Pane(
        string id, string window = "placement:1", bool active = true, bool claimed = false,
        string socket = "") =>
        new(socket, id, window, active, claimed);

    private static IReadOnlyList<SessionPresence.ViewerPane> TheFourPanes() => new[]
    {
        Pane("%6", "placement:1", active: true),                                  // his chat
        Pane("%98", "rc:1", active: true, socket: Swarm),                         // the relay
        Pane("%21", "claude-swarm:1", active: false, socket: Swarm, claimed: true),
        Pane("%53", "claude-swarm:1", active: true, socket: Swarm, claimed: true),
    };

    private static IReadOnlySet<string> Attached(params string[] sockets) =>
        new HashSet<string>(sockets, StringComparer.Ordinal);

    private static IReadOnlySet<(string Socket, string Window)> Watching(
        params (string, string)[] windows) => new HashSet<(string, string)>(windows);

    // The whole of round nine in one assertion. Only the default server has a
    // client, so three of the four panes are not viewers at all — not ranked last,
    // not tie-broken, simply not in the universe — and the one that is left is the
    // chat the orb was clicked from.
    [Fact]
    public void OnlyPanesOnAServerSomebodyIsAttachedToAreViewers()
    {
        var (verdict, found) = SessionPresence.ViewerAmong(
            TheFourPanes(),
            Attached(""),
            Watching(("", "placement:1")));

        Assert.Equal(SessionPresence.ViewerVerdict.TheUserIsLookingAtIt, verdict);
        Assert.Equal("%6", found!.Value.Pane);
    }

    // The same four panes with nobody attached anywhere: no viewer at all. A
    // detached server is a screen nobody is looking at, so "focusing" one of its
    // panes would select something invisible and then flash an acknowledgment for
    // having done it. Falling through to the attach ladder is the honest answer.
    [Fact]
    public void WithNobodyAttachedAnywhereThereIsNoViewer()
    {
        var (verdict, found) = SessionPresence.ViewerAmong(
            TheFourPanes(), Attached(), Watching());

        Assert.Equal(SessionPresence.ViewerVerdict.NoneFound, verdict);
        Assert.Null(found);
    }

    // And the case the filter is *not* allowed to swallow: a teammate someone
    // attached to by hand. That server now has a client, so its panes are in the
    // universe — which is why the inner rules are kept rather than deleted. Here
    // the claim exclusion does the work it was built for: both teammate panes are
    // claimed, so neither is focused, and the relay's unclaimed pane is the answer.
    [Fact]
    public void AttachingToASwarmServerBringsItsPanesIntoTheUniverse()
    {
        var (verdict, found) = SessionPresence.ViewerAmong(
            TheFourPanes(),
            Attached(Swarm),
            Watching((Swarm, "claude-swarm:1")));

        // %53 is active in the window being watched — but it is claimed, and the
        // "already looking at it" answer does not consult claims, because it cannot
        // be wrong about which session it found.
        Assert.Equal(SessionPresence.ViewerVerdict.TheUserIsLookingAtIt, verdict);
        Assert.Equal("%53", found!.Value.Pane);
    }

    [Fact]
    public void NoCandidatesIsNoViewer()
    {
        var (verdict, found) = SessionPresence.ViewerAmong(
            Array.Empty<SessionPresence.ViewerPane>(), Attached(""), Watching());

        Assert.Equal(SessionPresence.ViewerVerdict.NoneFound, verdict);
        Assert.Null(found);
    }

    // Same window, but not the pane being watched — a split scrolled away from.
    // That is "elsewhere": selecting it is a real move and a wanted one.
    [Fact]
    public void AnInactivePaneInTheWatchedWindowIsStillElsewhere()
    {
        var (verdict, found) = SessionPresence.ViewerAmong(
            new[] { Pane("%7", "placement:1", active: false) },
            Attached(""),
            Watching(("", "placement:1")));

        Assert.Equal(SessionPresence.ViewerVerdict.ElsewhereInTmux, verdict);
        Assert.Equal("%7", found!.Value.Pane);
    }

    // Attached, but which window could not be resolved. The weaker failure: the
    // server has a client so its panes stay in the universe, and selecting one does
    // reach a screen — it just cannot be called the pane in front of them.
    [Fact]
    public void AnAttachedServerWithNoResolvedWindowStillAllowsElsewhere()
    {
        var (verdict, found) = SessionPresence.ViewerAmong(
            new[] { Pane("%6", "placement:1", active: true) }, Attached(""), Watching());

        Assert.Equal(SessionPresence.ViewerVerdict.ElsewhereInTmux, verdict);
        Assert.Equal("%6", found!.Value.Pane);
    }

    // A window id is per server, so the same name on two servers is two different
    // windows. Watching one must not make the other's pane read as the one in
    // front of the user — which is why the pair is matched and not the string.
    [Fact]
    public void AWindowNameOnAnotherServerIsADifferentWindow()
    {
        var (verdict, _) = SessionPresence.ViewerAmong(
            new[] { Pane("%53", "claude-swarm:1", active: true, socket: Swarm) },
            Attached("", Swarm),
            Watching(("", "claude-swarm:1")));

        Assert.Equal(SessionPresence.ViewerVerdict.ElsewhereInTmux, verdict);
    }

    // The claim exclusion, inside the visible universe. Nearly vestigial there —
    // teammates and relays are on detached sockets by construction — but kept,
    // because "by construction" is a fact about how Claude Code launches things
    // and not a guarantee.
    [Fact]
    public void AVisibleClaimedPaneIsNotFocused()
    {
        var (verdict, found) = SessionPresence.ViewerAmong(
            new[] { Pane("%53", "claude-swarm:1", active: true, claimed: true) },
            Attached(""),
            Watching(("", "placement:1")));

        Assert.Equal(SessionPresence.ViewerVerdict.NoneFound, verdict);
        Assert.Null(found);
    }

    // ...and the asymmetry survives: a claim never blocks the answer that creates
    // nothing. Whatever a pane in front of the user holds, they are reading it.
    [Fact]
    public void AClaimedPaneTheUserIsLookingAtStillStopsTheClick()
    {
        var (verdict, _) = SessionPresence.ViewerAmong(
            new[] { Pane("%6", "placement:1", active: true, claimed: true) },
            Attached(""),
            Watching(("", "placement:1")));

        Assert.Equal(SessionPresence.ViewerVerdict.TheUserIsLookingAtIt, verdict);
    }

    // The tie-break, also inside the visible universe: the pane active in its own
    // window, else the first found.
    [Fact]
    public void AmongSeveralVisibleElsewhereTheActiveOneWins()
    {
        var (verdict, found) = SessionPresence.ViewerAmong(
            new[]
            {
                Pane("%21", "claude-swarm:1", active: false),
                Pane("%53", "claude-swarm:2", active: true),
            },
            Attached(""),
            Watching(("", "placement:1")));

        Assert.Equal(SessionPresence.ViewerVerdict.ElsewhereInTmux, verdict);
        Assert.Equal("%53", found!.Value.Pane);
    }

    [Fact]
    public void WithNoneActiveTheFirstFoundWins()
    {
        var (_, found) = SessionPresence.ViewerAmong(
            new[]
            {
                Pane("%21", "claude-swarm:1", active: false),
                Pane("%53", "claude-swarm:2", active: false),
            },
            Attached(""),
            Watching());

        Assert.Equal("%21", found!.Value.Pane);
    }

    [Fact]
    public void AClaimedPaneDoesNotShadowAnUnclaimedOne()
    {
        var (verdict, found) = SessionPresence.ViewerAmong(
            new[]
            {
                Pane("%53", "claude-swarm:1", active: true, claimed: true),
                Pane("%99", "other:1", active: false),
            },
            Attached(""),
            Watching());

        Assert.Equal(SessionPresence.ViewerVerdict.ElsewhereInTmux, verdict);
        Assert.Equal("%99", found!.Value.Pane);
    }

    // --- RuledOutAsAJob ------------------------------------------------------

    // The gate on an orb's existence, and the reversal this round made: only a
    // listing that was read and did not name the session rules it out. A finished
    // job is no longer ruled out, which is what lets it stay on screen — dimmed
    // and marked as finished — for as long as its status file does.
    [Fact]
    public void OnlyAReadListingThatDoesNotNameTheSessionRulesItOut()
    {
        Assert.True(SessionPresence.RuledOutAsAJob(JobPhase.NotAJob));

        foreach (var phase in new[] { JobPhase.Working, JobPhase.Parked, JobPhase.Done, JobPhase.Unknown })
        {
            Assert.False(SessionPresence.RuledOutAsAJob(phase));
        }
    }


    // --- the two menu items --------------------------------------------------

    // Dismiss deletes a status file, and only a local CLI session has one.
    [Theory]
    [InlineData(SessionSource.ClaudeCode, true)]
    [InlineData(SessionSource.Codex, true)]
    [InlineData(SessionSource.OpenClaw, false)]
    [InlineData(SessionSource.RemoteControl, false)]
    public void DismissIsOfferedForTheSessionsThatHaveAFileOnDisk(
        SessionSource source, bool expected)
    {
        Assert.Equal(expected, SessionPresence.CanDismiss(Status(source: source)));
    }

    // End needs a pid to signal on top of that. Offered nowhere it would not do
    // exactly what it says, because it is the one action here that cannot be
    // undone.
    [Theory]
    [InlineData(SessionSource.ClaudeCode, 4321, true)]
    [InlineData(SessionSource.Codex, 4321, true)]
    [InlineData(SessionSource.OpenClaw, 4321, false)]
    [InlineData(SessionSource.RemoteControl, 4321, false)]
    [InlineData(SessionSource.ClaudeCode, 0, false)]
    [InlineData(SessionSource.ClaudeCode, -1, false)]
    public void EndIsOfferedOnlyWhereThereIsAProcessToEnd(
        SessionSource source, int pid, bool expected)
    {
        Assert.Equal(expected, SessionPresence.CanEndSession(Status(source: source, pid: pid)));
    }

    // A hook older than the session_pid field writes 0, which is why these are
    // two rules rather than one with an extra clause: such a session can still
    // be dismissed from the screen, it just cannot be ended.
    [Fact]
    public void ASessionWithNoRecordedPidCanStillBeDismissed()
    {
        var pidless = Status(pid: 0);

        Assert.True(SessionPresence.CanDismiss(pidless));
        Assert.False(SessionPresence.CanEndSession(pidless));
    }

    // --- CouldBeABackgroundedHusk ---------------------------------------------
    // The gate on TranscriptHandoff's transcript read, not the answer: what it
    // decides is whether a session's transcript is worth statting at all.

    private static SessionStatus WithTranscript(
        SessionSource source = SessionSource.ClaudeCode,
        string transcriptPath = "/Users/w/.claude/projects/-Users-w-project/6d3a9d57.jsonl")
    {
        var status = Status(source: source);
        status.TranscriptPath = transcriptPath;
        return status;
    }

    [Fact]
    public void OnlyASessionTheDaemonDoesNotVouchForCanBeAHusk()
    {
        // The husk's own two answers: NotAJob when the listing was read and it
        // is not on it, Unknown when nothing made the listing worth fetching —
        // which is what is left once the fork finishes and its file is swept,
        // and the husk must stay hidden then too.
        foreach (var phase in new[] { JobPhase.NotAJob, JobPhase.Unknown })
        {
            Assert.True(SessionPresence.CouldBeABackgroundedHusk(WithTranscript(), phase));
        }

        // A session the daemon lists as a job is alive by the daemon's own
        // word. This is what keeps the fork itself out: its transcript
        // *inherits* the parent's rows, marker included, so for a scan or two
        // before its first answer lands the tail alone would misread it.
        foreach (var phase in new[] { JobPhase.Working, JobPhase.Parked, JobPhase.Done })
        {
            Assert.False(SessionPresence.CouldBeABackgroundedHusk(WithTranscript(), phase));
        }
    }

    [Theory]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    public void OnlyClaudeCodeCanLeaveAHusk(SessionSource source)
    {
        // Backgrounding a turn is Claude Code's feature; for a gateway or
        // bridged session the transcript this would read is on another machine
        // or nowhere, and reading a Codex rollout with Claude Code's needles
        // would be answering a question nobody asked.
        Assert.False(SessionPresence.CouldBeABackgroundedHusk(
            WithTranscript(source), JobPhase.NotAJob));
    }

    [Fact]
    public void NoTranscriptPathMeansNothingToReadNotEvidence()
    {
        Assert.False(SessionPresence.CouldBeABackgroundedHusk(
            WithTranscript(transcriptPath: ""), JobPhase.NotAJob));
    }

    // --- LocalDaemonCanAnswerFor ----------------------------------------------
    // Whether this machine's `claude agents` listing is evidence about a session
    // at all. CB-194: a WSL session seen from Windows is never on the Windows
    // listing, and reading that absence as NotAJob dropped every WSL orb.

    private static SessionStatus Wsl(
        int pid = 0,
        string cwd = "/home/k/project",
        string transcriptPath = "/home/k/.claude/projects/-home-k-project/240f91e6.jsonl",
        SessionSource source = SessionSource.ClaudeCode)
    {
        var status = Status(pid: pid, source: source);
        status.Cwd = cwd;
        status.TranscriptPath = transcriptPath;
        return status;
    }

    [Fact]
    public void WindowsCannotAnswerForAPidlessSessionFromInsideWsl()
    {
        // The shape the hook writes for every WSL session: no pid, because its
        // walk up the Windows process tree ends in the interop bridge.
        Assert.False(SessionPresence.LocalDaemonCanAnswerFor(Wsl(), onWindows: true));
    }

    [Fact]
    public void EitherPathAloneIsEnoughToRecogniseWsl()
    {
        // The cwd arm is for a hook wired by hand from the README snippet, which
        // may pass no transcript at all; the transcript arm for anything that
        // reports it without a cwd.
        Assert.False(SessionPresence.LocalDaemonCanAnswerFor(
            Wsl(transcriptPath: ""), onWindows: true));
        Assert.False(SessionPresence.LocalDaemonCanAnswerFor(
            Wsl(cwd: ""), onWindows: true));
    }

    [Fact]
    public void OnAMacTheSameFileIsStillTheListingsToJudge()
    {
        // Every path on a Mac starts with '/'. Without the platform clause this
        // would switch the subagent rule off for everyone — the negative control
        // for the case above.
        Assert.True(SessionPresence.LocalDaemonCanAnswerFor(Wsl(), onWindows: false));
    }

    [Fact]
    public void ANativeWindowsSessionIsStillTheListingsToJudge()
    {
        // Pid-less with drive-letter paths: a subagent or a leftover file on
        // Windows itself, which the listing does know about — the rule
        // RuledOutAsAJob serves must keep firing for it.
        Assert.True(SessionPresence.LocalDaemonCanAnswerFor(
            Wsl(cwd: @"C:\Users\k\project",
                transcriptPath: @"C:\Users\k\.claude\projects\C--Users-k-project\240f91e6.jsonl"),
            onWindows: true));

        // And with no paths at all there is nothing to call WSL.
        Assert.True(SessionPresence.LocalDaemonCanAnswerFor(
            Wsl(cwd: "", transcriptPath: ""), onWindows: true));
    }

    [Fact]
    public void ASessionThatNamesItsProcessIsAlwaysTheListingsToJudge()
    {
        // A recorded pid means this machine can see the process, so whatever
        // the listing says stands, whatever its paths look like.
        Assert.True(SessionPresence.LocalDaemonCanAnswerFor(Wsl(pid: 4321), onWindows: true));
    }

    [Theory]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.Grok)]
    public void CodexAndGrokAreLeftToTheirOwnRule(SessionSource source)
    {
        // Their pid-less rule never consults the listing, and this must not
        // become a way round it.
        Assert.True(SessionPresence.LocalDaemonCanAnswerFor(Wsl(source: source), onWindows: true));
    }

    // --- WithQuiet (CB-307) ----------------------------------------------------

    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Yesterday = Now - TimeSpan.FromHours(23);
    private static readonly DateTime JustNow = Now - TimeSpan.FromMinutes(5);

    [Theory]
    [InlineData(LocalSessionShape.Terminal)]
    [InlineData(LocalSessionShape.Teammate)]
    public void APresentSessionNobodyHasSpokenToForADayIsParked(LocalSessionShape shape)
    {
        // Members park by quiet too: the mirror server parks them, and host and
        // peer have to agree. The duplicate rule, not this one, protects them.
        Assert.Equal(
            OrbPresence.Parked,
            SessionPresence.WithQuiet(OrbPresence.Present, shape, "idle", Yesterday, Now));
    }

    [Theory]
    [InlineData(LocalSessionShape.Terminal)]
    [InlineData(LocalSessionShape.Teammate)]
    public void ARecentlySpokenToSessionStaysPresent(LocalSessionShape shape)
    {
        Assert.Equal(
            OrbPresence.Present,
            SessionPresence.WithQuiet(OrbPresence.Present, shape, "idle", JustNow, Now));
    }

    [Fact]
    public void ASessionWithNoKnownLastTurnStaysPresent()
    {
        Assert.Equal(
            OrbPresence.Present,
            SessionPresence.WithQuiet(OrbPresence.Present, LocalSessionShape.Terminal, "idle", null, Now));
    }

    [Theory]
    [InlineData("generating")]
    [InlineData("waiting")]
    public void ASessionInUseNowIsNeverParkedWhateverItsAge(string state)
    {
        Assert.Equal(
            OrbPresence.Present,
            SessionPresence.WithQuiet(OrbPresence.Present, LocalSessionShape.Terminal, state, Yesterday, Now));
    }

    // A background job's presence is the daemon's, so the clock never touches it.
    [Theory]
    [InlineData(OrbPresence.NeedsInput)]
    [InlineData(OrbPresence.Finished)]
    [InlineData(OrbPresence.Present)]
    public void ABackgroundSessionIsLeftAsTheDaemonSaid(OrbPresence presence)
    {
        Assert.Equal(
            presence,
            SessionPresence.WithQuiet(presence, LocalSessionShape.Background, "idle", Yesterday, Now));
    }

    [Fact]
    public void AnOrphanedTeammateStaysParkedWhetherOrNotItIsQuiet()
    {
        Assert.Equal(
            OrbPresence.Parked,
            SessionPresence.WithQuiet(OrbPresence.Parked, LocalSessionShape.Teammate, "idle", JustNow, Now));
        Assert.Equal(
            OrbPresence.Parked,
            SessionPresence.WithQuiet(OrbPresence.Parked, LocalSessionShape.Teammate, "idle", Yesterday, Now));
    }

    // --- WantsLastTurn (CB-307) ------------------------------------------------

    private static SessionStatus ForLastTurn(
        string state = "idle", string transcript = "/t/x.jsonl",
        SessionSource source = SessionSource.ClaudeCode) =>
        new() { State = state, TranscriptPath = transcript, Source = source };

    [Fact]
    public void AnIdleClaudeCodeSessionWithATranscriptWantsItsLastTurn() =>
        Assert.True(SessionManager.WantsLastTurn(ForLastTurn()));

    [Theory]
    [InlineData("generating")]
    [InlineData("waiting")]
    public void ASessionInUseNowNeverHasItsTranscriptRead(string state) =>
        Assert.False(SessionManager.WantsLastTurn(ForLastTurn(state: state)));

    [Fact]
    public void ASessionWithNoTranscriptHasNothingToRead() =>
        Assert.False(SessionManager.WantsLastTurn(ForLastTurn(transcript: "")));

    [Theory]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.Grok)]
    public void OnlyClaudeCodeSessionsAreAskedForALastTurn(SessionSource source) =>
        Assert.False(SessionManager.WantsLastTurn(ForLastTurn(source: source)));
}
