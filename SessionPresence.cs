using System;
using System.Collections.Generic;

namespace Orbweaver
{
    // What shape of local session a status file describes.
    //
    // Not a second copy of SessionSource, which says which CLI wrote the file:
    // every value here is a Claude Code session, and what separates them is
    // whose lifecycle they follow. Claude Code grew these three while the app
    // went on drawing all of them as the first one, which is what put fifteen
    // breathing orbs on screen for a machine with nothing running on it.
    public enum LocalSessionShape
    {
        // Somebody at a keyboard. Its process lives as long as the terminal
        // does, and it is never parked — a terminal session between turns is
        // still a terminal session, sitting there waiting for you.
        Terminal,

        // A background job, running inside a pooled `claude bg-spare` worker
        // that is kept alive after the turn ends so the job can be resumed.
        // "Alive" therefore says almost nothing about it, which is why the
        // daemon's own phase has to be asked.
        Background,

        // A member of an agent team, spawned by a lead that named itself on the
        // member's command line. Outlives its lead: the process is real, the
        // status file is real, and nothing about either says the thing it was
        // answering to has gone.
        Teammate
    }

    // What an orb should say about whether anything is on the other end of this
    // session — the third axis, beside identity (its colour) and state (its
    // fill).
    //
    // Four answers rather than the bool this started as, because the three ways
    // of being quiet are not the same thing to a person looking at the screen and
    // the difference is exactly what the original bug was. A daemon calls a job
    // between turns "blocked", and several of the ones on the machine this was
    // written for were literally holding a question — so "dim" undersold them,
    // and "dim, with nothing to distinguish it from a job that has finished for
    // good" was the same mistake one level down.
    public enum OrbPresence
    {
        // Someone or something is on the other end: a session at work, a session
        // waiting at a keyboard, or a parked job with a `claude attach` client
        // sitting in it. Full brightness, ordinary motion.
        Present,

        // Quiet, with nothing more to say about it. An orphaned team member is
        // the only thing here: its lead has gone, and no daemon knows anything
        // about it that would justify a mark. Dimmed.
        Parked,

        // A background job the daemon is holding for you. "Needs input" is the
        // daemon's own word for it, and several are questions waiting on an
        // answer, so it is dimmed — it is not competing with live work — and
        // marked, because "there is something here for you" is the opposite of
        // what plain dimming says.
        NeedsInput,

        // A background job that is over. Dimmed and marked differently from
        // NeedsInput, because the two are opposite instructions: one wants you,
        // and one wants nothing ever again. It stays on screen only as long as
        // its status file does — the sweep deletes that after the grace period,
        // and the orb goes with it, so a finished job fades from the screen at
        // the same moment it stops existing on disk.
        Finished
    }

    // Whether a session is *present* — someone or something is on the other end
    // of it right now — and which lifecycle affordances it can be offered.
    //
    // Pure, with no window and no settings behind it, for the reason OrbGlyph
    // and OrbArrangement are: these rules were arrived at by looking at a
    // screenshot and counting orbs, and the next person to change one of them
    // needs to be able to ask what it answers without a machine in a particular
    // state. It also matters that the mistakes here are directional and quiet.
    // Parking an orb that is genuinely working says a working session is idle;
    // failing to park one says nothing new but leaves the bug in place. So
    // every rule below is written to fail towards "present", the same way
    // BackgroundJobs.IsLive fails towards "keep the orb".
    internal static class SessionPresence
    {
        // Which of the three shapes this is.
        //
        // The job phase decides first and unconditionally, because it is the
        // only one of the three signals that comes from outside this app: if
        // the daemon lists this session as a job, it is a job whatever else the
        // file looks like. Working, Parked and Done are all Background — a
        // finished job is still a background session, and the rules that drop
        // its orb are elsewhere and say so.
        //
        // Unknown and NotAJob both fall through to the team test, which is what
        // keeps an unreadable listing from reclassifying every teammate on
        // screen as a terminal session.
        //
        // Lead can't collide with a job: a background worker is started by the
        // daemon and carries no --parent-session-id, so nothing ever writes a
        // Lead onto one. The order is stated anyway rather than left to that
        // being permanently true.
        internal static LocalSessionShape ShapeOf(SessionStatus status, JobPhase phase)
        {
            if (phase is JobPhase.Working or JobPhase.Parked or JobPhase.Done)
            {
                return LocalSessionShape.Background;
            }

            return string.IsNullOrEmpty(status.Lead)
                ? LocalSessionShape.Terminal
                : LocalSessionShape.Teammate;
        }

        // Whether this session is a reason to ask the daemon anything.
        //
        // The lookup is a subprocess (`claude agents --json`, cached for ten
        // seconds), and JudgeReachability already records the rule this keeps:
        // an ordinary session never pays for it. Fetching the listing on every
        // pass regardless is, on a machine with nothing but terminal sessions, a
        // `claude` process spawned every ten seconds forever in service of a
        // question whose answer cannot change anything.
        //
        // Three shapes are worth asking about, and the third was missing:
        //
        // - No pid recorded: a hook older than that field, or a shape that has
        //   none.
        // - A status file that names no terminal — what a background worker's
        //   file looks like when its daemon has none to pass down.
        // - **A file that shares its pid with another file.** This is the second
        //   of the two shapes BackgroundJobs' own comment says only the daemon
        //   can settle: an Agent-View-dispatched background session does not fork
        //   a process, it starts a second conversation inside the one `claude`
        //   process already running, so its file names a live interactive
        //   session's pid. Left out, the gate above refused to ask about exactly
        //   the case that was documented as needing to be asked — and refused
        //   twice over, because InheritTerminalInfo donates terminal fields
        //   between files sharing a pid, so such a file *acquires* a terminal
        //   before this is asked and then reads as an ordinary session.
        //
        //   That donation is also why this clause subsumes the inheritance
        //   problem rather than needing a pre-inheritance snapshot beside it:
        //   InheritTerminalInfo only ever moves fields within one (pid, source)
        //   group, so anything it could have touched shares its pid by
        //   definition and is being asked about regardless of what it now names.
        //
        //   It is free, too. Two files on one pid is precisely the situation
        //   Superseded already resolves by asking the daemon, so the listing is
        //   in hand for this pass before the question is put.
        //
        // knowsATerminal and sharesItsPid are passed in rather than derived: the
        // first has to be answered at a particular moment — before the
        // agent-viewer adoption, which can hand a terminal to a session that had
        // none — and the second is a fact about the *other* files in the scan,
        // which a single status knows nothing about.
        //
        // What this still does not reach is a background job whose own hook
        // wrote a terminal into its file. The hook interpolates $TERM_PROGRAM
        // from the environment, so a daemon started from inside a terminal
        // passes it down to every job under it. Named rather than papered over:
        // the obvious extra clause is "no tty", and the Windows hook records no
        // tty at all, so that clause would make every session on Windows spawn a
        // subprocess every ten seconds. Such a session's click also lands on the
        // terminal its daemon was started from, which is a real window rather
        // than nothing. The scan closes most of this gap another way — see the
        // note on asking about everything once the listing has been paid for.
        internal static bool WorthAskingTheDaemon(
            SessionStatus status, bool knowsATerminal, bool sharesItsPid) =>
            status.Source == SessionSource.ClaudeCode
            && !status.IsDesktopHosted
            && (status.SessionPid <= 0 || !knowsATerminal || sharesItsPid);

        // Which of the four an orb should be drawn as.
        //
        // Parked is not the same claim as gone, which is why nothing here removes
        // an orb: the session is resumable, it is still worth clicking, and the
        // user asked for it to stay on screen. What was wrong before this existed
        // is only that all of them looked like work in progress.
        //
        // `state` is the status file's own word, and for a background session it
        // is load-bearing rather than decorative. The daemon's listing is cached
        // for ten seconds while the hook rewrites the file the instant a job
        // resumes, so the file is the fresher of the two sources: requiring it to
        // still say "idle" is what makes coming back to life prompt (next 2s
        // scan) even though going quiet lags (up to the cache plus a scan). The
        // lag is one way round on purpose. An orb that stays dim for ten seconds
        // after work resumes is a lie about the thing the user is watching
        // happen; an orb that takes ten seconds to go dim is a lie about nothing
        // happening, which nobody is watching for.
        //
        // `attached` is whether a `claude attach` client is sitting in this job.
        // A parked session someone is looking at is not quiet — the whole point
        // of attaching is that you are in it — and dimming it while the user
        // types was the plainest contradiction on the screen: they attached to
        // all three and watched them stay grey. The daemon still says "blocked",
        // because from its side nothing has changed; the person's presence is the
        // thing it does not know about and the process table does.
        //
        // leadSeen / leadIsLiveJob are the orphan rule, and both have to be
        // false. A teammate whose lead is on this scan is part of a live team. A
        // teammate whose lead is a background job the daemon still lists is also
        // part of a live team — the lead simply has no status file of its own to
        // be seen through, which is the ordinary case for a team led from a job.
        // Only when neither holds is the member answering to something that is
        // not there any more, and an orphaned teammate is the one shape here
        // whose arrows have already silently vanished, so the orb is the last
        // thing left saying anything about it.
        internal static OrbPresence PresenceOf(
            LocalSessionShape shape, string state, JobPhase phase,
            bool leadSeen, bool leadIsLiveJob, bool attached) => shape switch
        {
            LocalSessionShape.Background => BackgroundPresence(state, phase, attached),

            LocalSessionShape.Teammate =>
                !leadSeen && !leadIsLiveJob ? OrbPresence.Parked : OrbPresence.Present,

            // Including Terminal, said as the default rather than as a case so a
            // fourth shape added later is present until someone decides
            // otherwise, rather than dim because a switch was not revisited.
            // Somebody at a keyboard is never dimmed: a terminal session between
            // turns is still a terminal session, sitting there waiting for you.
            _ => OrbPresence.Present
        };

        private static OrbPresence BackgroundPresence(string state, JobPhase phase, bool attached)
        {
            // Finished first, and regardless of the file's state or of anyone
            // being attached: a job that is over is over, and an attach client
            // sitting in a finished job is reading rather than working. Unlike
            // the others this one is also on its way out — the sweep has the same
            // ten minutes' evidence by now — so what it says has to be "this is
            // done" rather than "this is idle".
            if (phase == JobPhase.Done) return OrbPresence.Finished;

            if (phase != JobPhase.Parked) return OrbPresence.Present;

            // The file overrides the listing, and a person overrides both.
            if (!string.Equals(state, "idle", StringComparison.Ordinal)) return OrbPresence.Present;

            return attached ? OrbPresence.Present : OrbPresence.NeedsInput;
        }

        // Whether this status file could be the husk a backgrounded turn leaves
        // behind — the gate on TranscriptHandoff's transcript read, not the
        // answer itself. See TranscriptHandoff for the shape of the bug.
        //
        // Claude Code only, because backgrounding a turn is its feature; Codex
        // has no equivalent, and for a gateway or bridged session the transcript
        // this would read is on another machine or nowhere.
        //
        // The phase gate is what keeps the fork itself out. A forked job's
        // transcript *inherits* the parent's rows, backgrounding marker
        // included, so for a scan or two after the fork — before its first
        // answer lands — the job's own tail can end with the inherited marker.
        // A session the daemon lists as a job is alive by the daemon's own
        // word, whatever its transcript's tail happens to hold. Unknown passes
        // the gate deliberately: it is the answer on a machine where nothing
        // made the listing worth fetching, which is exactly what is left once
        // the fork finishes and its file is swept — and the husk must stay
        // hidden then, or the duplicate this rule removes would come back as a
        // lone stale orb.
        //
        // No transcript path means nothing to read, not evidence either way —
        // the same reading JudgeReachability gives an empty path.
        internal static bool CouldBeABackgroundedHusk(SessionStatus status, JobPhase phase) =>
            status.Source == SessionSource.ClaudeCode
            && phase is JobPhase.NotAJob or JobPhase.Unknown
            && !string.IsNullOrEmpty(status.TranscriptPath);

        // Whether the daemon has *ruled out* this session being a background job.
        //
        // The distinction matters because the answer gates an orb's existence.
        // "Not a job" is a fact — the listing was read and this session is not on
        // it, so it is a subagent or a file that outlived its session, and an orb
        // for it is a dead click. Every other answer, Unknown included, leaves the
        // orb alone: a job mid-turn, a job holding a question, a job that has
        // finished but whose file is still here, and a CLI that could not be
        // asked at all.
        //
        // "Finished but still here" is the new one, and it is a deliberate
        // reversal. A `done` job used to lose its orb the instant the daemon said
        // so, on the reasoning that a finished job has nothing to show — true of
        // clicking it, and the wrong thing to do to a screen: the user watched an
        // orb appear and vanish while they were looking at it and reported it as a
        // bug, because a thing that disappears without being dismissed reads as a
        // fault rather than as a finish. It now stays, dimmed and marked as
        // finished, for exactly as long as its status file does — which the sweep
        // deletes on the same ten minutes' evidence, so the orb goes when the file
        // goes and the two never disagree.
        internal static bool RuledOutAsAJob(JobPhase phase) => phase == JobPhase.NotAJob;

        // Whether this machine's `claude agents` listing can speak for this
        // session at all — asked before its answer is read, so that a session
        // it cannot know about gets Unknown rather than a verdict.
        //
        // The one it cannot is a Claude Code session running inside WSL, seen
        // from the Windows app. That session's daemon lives in the Linux VM, so
        // the Windows listing never names it, and "not on the listing" read as
        // NotAJob — which RuledOutAsAJob above turns into NotALiveJob for any
        // file naming no process. The hook records no process for exactly these
        // sessions, on purpose: its walk up the Windows process tree dead-ends in
        // the interop bridge, and OrbweaverHook.ps1 says in as many words that
        // the app treats an unrecorded pid as "can't check" and keeps the orb.
        // The two rules together dropped every WSL orb on every scan (CB-194),
        // while the same session started from a Windows prompt drew normally,
        // because there the walk finds claude.exe and the pid rule never fires.
        //
        // Recognised by a POSIX-absolute transcript path or cwd. On Windows a
        // native session reports drive-letter paths and nothing else, so a
        // leading '/' can only have come from inside WSL — the same signal the
        // hook already keys its own WSL fallback on. The transcript first
        // because it is the path Claude Code itself chose; the cwd as well
        // because a hook wired by hand from the README's snippet may not pass
        // a transcript at all.
        //
        // Narrow on purpose, and each clause is load-bearing:
        //
        // - Windows only, taken as an argument rather than read here. On a Mac
        //   every path starts with '/', so without it this would switch off the
        //   subagent rule for everyone; as an argument, both CI legs can assert
        //   both answers.
        // - Claude Code only. Codex and Grok have their own pid-less rule, which
        //   never consults the listing, and nothing here should touch it.
        // - No recorded pid. A session that names its process is a session this
        //   machine can see, and whatever the listing says about it stands.
        internal static bool LocalDaemonCanAnswerFor(SessionStatus status, bool onWindows)
        {
            if (!onWindows) return true;
            if (status.Source != SessionSource.ClaudeCode) return true;
            if (status.SessionPid > 0) return true;

            return !(IsPosixAbsolute(status.TranscriptPath) || IsPosixAbsolute(status.Cwd));
        }

        private static bool IsPosixAbsolute(string? path) =>
            !string.IsNullOrEmpty(path) && path[0] == '/';

        // Whether some `claude attach` client is sitting in this session.
        //
        // `attachedIds` is what the process scan found — the argv[2] of every
        // `claude attach <id>` running on this machine — or null for a scan that
        // could not be done at all. Compared by prefix in both directions, the
        // way AgentTeamViewer already compares them: attach accepts the short job
        // id and echoes it back that way, so a window opened by hand with
        // `claude attach bd7919f8` has to count as session bd7919f8-….
        //
        // A scan that failed answers **true**, and the direction is deliberate.
        // Wrong-true leaves a genuinely parked orb at full brightness, which is
        // the bug this branch started from, in its mildest form. Wrong-false dims
        // a session the user is sitting in and typing at — the contradiction that
        // prompted this rule in the first place. Of the two ways to be wrong,
        // only one of them argues with the person looking at the screen.
        internal static bool HasAttachClient(
            IReadOnlyCollection<string>? attachedIds, string sessionId) =>
            AttachClientFound(attachedIds, sessionId) ?? true;

        // The same question asked by a *click*, which answers a failed scan the
        // other way round — and this is now the one authority on it, rather than
        // one of three overlapping ones.
        //
        // The direction first, because it is the part that is easy to get wrong by
        // sharing. For dimming, wrong-true leaves a genuinely parked orb bright,
        // which is this branch's original bug in its mildest form, while
        // wrong-false dims a session the user is sitting in and typing at — so an
        // unreadable process table means "assume attached". For a click, wrong-true
        // means raising an app that has no such window and *not* creating the one
        // the user asked for: a gesture that does nothing, which is the complaint
        // the whole click ladder exists to answer. So here an unreadable process
        // table means "assume not attached", and the click creates a pane. A
        // duplicate is visible and closable; a dead click is neither.
        //
        // It replaced AgentTeamViewer.AttachedAlready, which asked the identical
        // question of the identical population — `ps -eo args=`, matched on
        // argv[1]=="attach" — and differed only in being uncached and in not
        // sharing SameJobId. Two rules that mostly agree about whether a window
        // already exists is the drift this file keeps writing comments against,
        // and the failure it drifts into is a second window opened onto a
        // conversation somebody is already reading.
        internal static bool KnownAttachClient(
            IReadOnlyCollection<string>? attachedIds, string sessionId) =>
            AttachClientFound(attachedIds, sessionId) ?? false;

        // Whether the scan found a client for this session, or null for a scan
        // that could not be done at all. Split out so each policy above is one
        // line beside its own reasoning, rather than one rule with a direction
        // that only suits whichever caller was written first.
        private static bool? AttachClientFound(
            IReadOnlyCollection<string>? attachedIds, string sessionId)
        {
            if (attachedIds is null) return null;
            if (string.IsNullOrEmpty(sessionId)) return false;

            foreach (var id in attachedIds)
            {
                if (SameJobId(sessionId, id)) return true;
            }

            return false;
        }

        // --- a pane that is already showing a session -------------------------

        // The glyph Claude Code's TUI puts in front of the conversation title
        // when it writes the tmux pane title.
        //
        // U+2733, EIGHT SPOKED ASTERISK, followed by a space. Observed rather
        // than documented — nothing in Claude Code promises this — so it was
        // measured before it was relied on: 24 samples over ten seconds across
        // three panes, one of them mid-turn and two idle, and every one of the 72
        // readings was the same glyph. It is a brand mark, not a state
        // indicator, and it does not animate. That was worth establishing,
        // because a spinner in this position would have made the prefix useless.
        //
        // Requiring it is the conservative direction and the direction matters.
        // A pane title is writable by any program — `printf '\033]2;...\007'` is
        // all it takes — so a prefix this specific is what separates a real
        // Claude Code pane from an editor someone opened on a file with the same
        // name. If a future version changes the glyph, this rule stops matching
        // and the click falls through to the attach ladder: the user gets the
        // duplicate pane this feature exists to avoid, which is visible,
        // closable and immediately reported. The opposite mistake — matching
        // something that is not a session — sends a click to another program's
        // window and says nothing.
        internal const string PaneTitleGlyph = "\u2733 ";

        // Whether a tmux pane's title says it is showing this session's
        // conversation.
        //
        // Only the *title* half of the question, and named so. It is not
        // sufficient on its own and cannot be made so: on the machine this was
        // built for, four panes carried the identical title
        // "\u2733 Claude desktop app multiple profiles bug" for three different
        // sessions, because every member of an agent team inherits the team
        // session's title. So this is a filter that produces candidates, and what
        // narrows them is the process behind the pane and, for the riskier of the
        // two outcomes, which panes other sessions already claim.
        //
        // An exact suffix rather than a contains: the title is the whole of what
        // the TUI puts after the glyph, and "contains" would match a pane whose
        // title merely mentions this session's name — a shell in a directory of
        // that name, a log file being tailed.
        internal static bool TitleSaysViewing(string? paneTitle, string? sessionTitle)
        {
            if (string.IsNullOrEmpty(paneTitle) || string.IsNullOrEmpty(sessionTitle)) return false;

            // A session whose own title is a glyph-prefixed string would
            // otherwise let a bare title match; and the two halves must not
            // overlap, or "\u2733 x" would match session title "\u2733 x" with
            // nothing in between.
            if (paneTitle.Length <= PaneTitleGlyph.Length) return false;

            return paneTitle.StartsWith(PaneTitleGlyph, StringComparison.Ordinal)
                && paneTitle.EndsWith(sessionTitle, StringComparison.Ordinal)
                && paneTitle.Length >= PaneTitleGlyph.Length + sessionTitle.Length;
        }

        // Whether an argv[0] names the Claude Code binary — the belt to the
        // title's braces, asked of the process behind a candidate pane.
        //
        // Two shapes, both observed live, and neither is what the obvious rule
        // would catch. An interactive session launched from a shell runs as plain
        // `claude`; a team member runs as
        // `/Users/…/.local/share/claude/versions/2.1.246`, whose file *name* is a
        // version number and whose `pane_current_command` tmux reports is
        // "2.1.246". So a filename test alone misses every teammate, and this
        // takes the versioned install path as the second form.
        //
        // Kept beside the title rule rather than in the scan that uses it,
        // because "what counts as the Claude binary" is precisely the sort of
        // thing that gets a second, differently-wrong copy — ViewerPids has the
        // filename half of it already, for the narrower job of spotting
        // `claude agents`.
        internal static bool LooksLikeClaudeBinary(string? argv0)
        {
            if (string.IsNullOrEmpty(argv0)) return false;

            if (System.IO.Path.GetFileName(argv0) is "claude" or "claude.exe") return true;

            return argv0.Contains("/claude/versions/", StringComparison.Ordinal)
                || argv0.Contains("\\claude\\versions\\", StringComparison.Ordinal);
        }

        // Whether a pane claim is still evidence about who is *reading* that pane.
        //
        // A status file records which session's process lives in a pane. That is
        // not the same fact as which conversation the pane is showing, and the
        // machine this was built for proved it by displaying both at once: the
        // claude process in the user's pane is the recorded session_pid of session
        // e95dffe6, and the pane was photographed rendering a *different* session's
        // conversation. One client, several conversations, one of them on screen —
        // which is the same thing WorthAskingTheDaemon's comment already says from
        // the other side, where an Agent-View-dispatched session starts a second
        // conversation inside a process that is already running.
        //
        // So the claim was real as a process-residence fact and stale as viewer
        // evidence, simultaneously. Two rules were proposed for it and the machine
        // killed both: corroborate-by-title-but-keep-an-untitled-claim left the
        // pane excluded (that claimant's recorded title is empty), and
        // confirm-by-session_pid-in-the-tree left it excluded too (the pid is
        // genuinely there). Either way the user's own pane stayed out of
        // candidacy and the click went on making a duplicate beside it.
        //
        // **The displayed title trumps the resident claim.** The TUI titles what it
        // is *showing*; the status file records what is *living there*. When they
        // disagree, the title is the one talking about a viewer, so a claim earns
        // its exclusion only while it agrees with the title — which is to say, only
        // in the case where the title cannot tell the two apart anyway.
        //
        // That keeps the exclusion doing the job it was added for. Every member of
        // an agent team inherits the team session's title, so a teammate's claim on
        // its own pane matches that pane's title and still excludes it; without
        // that, clicking a lead would focus a teammate's conversation. And it
        // releases the case above, where the titles differ and the title is right.
        //
        // A named rule rather than the bare call, because "does this claim still
        // describe a viewer" and "does this title say viewing" are different
        // questions that happen to have the same answer, and the next person to
        // change either needs to see which one they are changing.
        internal static bool ClaimStillHolds(string? claimantTitle, string? paneTitle) =>
            TitleSaysViewing(paneTitle, claimantTitle);

        // What was found, because the two findings mean opposite things to a
        // click.
        internal enum ViewerVerdict
        {
            // No pane is showing this session. The click carries on down the
            // attach ladder.
            NoneFound,

            // The pane the user is looking at right now is showing it. **The
            // click does nothing**, and that is the entire point of round seven:
            // "Nobody wants the same chat in two windows next to each other!!
            // This is the chat!!" Doing nothing is also the one outcome that
            // cannot be wrong about *which* session it found — whatever that pane
            // holds, the user is already reading it, so a title collision costs
            // nothing here.
            TheUserIsLookingAtIt,

            // A pane elsewhere in tmux is showing it. Focused through the
            // ordinary select-window/select-pane tail — no attach, no split, no
            // duplicate.
            ElsewhereInTmux
        }

        // Whether a verdict means "a pane on screen answers this click".
        //
        // Both found verdicts do, and they do the *same* thing, which is round
        // ten's correction. Being tmux-active is not the same as being on the
        // user's screen: these orbs float over every application, and the terminal
        // is routinely behind a browser or a chat window when one is clicked. The
        // pane can be current in its tmux session and invisible on the desktop at
        // the same moment — so an "already here" that only flashed was telling the
        // truth about tmux and lying about what the person could see. "Mechanically
        // perfect" and "still doesn't work" were both accurate.
        //
        // So the two answers converge: bring that pane to the screen, then say the
        // gesture was handled. For a pane already current, bringing it to the
        // screen is only the application raise — the select-window and select-pane
        // in that tail land on the window and pane that are already chosen, and do
        // nothing. Raising an application that is already frontmost is a no-op too,
        // which is why this needs no idea of what is in front: always raise.
        //
        // The verdicts stay distinct because the distinction is still load-bearing
        // where it was built — "already looking at it" ignores pane claims and
        // "elsewhere" respects them, which is what keeps a title collision from
        // sending a click into someone else's conversation. What round ten changes
        // is only that both answers mean the same thing to the destination.
        internal static bool AnswersTheClick(ViewerVerdict verdict) =>
            verdict is ViewerVerdict.TheUserIsLookingAtIt or ViewerVerdict.ElsewhereInTmux;

        // One pane whose title and process say it is showing a session.
        //
        // Socket is carried because nothing about a pane is unique without it. A
        // pane id is per server, and so is a window id — two servers can both have
        // a `claude-swarm:1` — so a candidate identified by pane alone would be
        // looked for on the wrong server the moment more than one is in scope, and
        // "focus that pane" would silently find nothing and fall through to
        // minting a new one.
        internal readonly record struct ViewerPane(
            string Socket, string Pane, string Window, bool ActiveInItsWindow, bool ClaimedByAnother);

        // Which candidate answers the click, and how.
        //
        // **The first act is the universe, and it is where round nine's whole
        // correction lives.** A pane on a server with no attached client is not a
        // viewer that ranks last; it is not a viewer at all. Nobody's eyes can be
        // on a detached server, so its panes cannot be "where the user is reading"
        // however exactly their titles match — and every collision this rule has
        // been patched for was manufactured by treating them as if they could be.
        //
        // On the machine that forced this: four panes carried one identical title.
        // One was the user's real viewer on the only attached server; the other
        // three were two teammates and a remote-control relay, all on a detached
        // `claude-swarm-<pid>` socket. Teammates title their own TUIs with the team
        // title, so they are perfect impostors by construction — and the answer was
        // never a better way to tell them apart, it was that they were never
        // candidates. The filter alone leaves exactly the user's own chat.
        //
        // socketsWithClients is therefore not an optimisation and not a tiebreak.
        // Everything below it is choosing between things a person can actually
        // see.
        //
        // watchedWindows is the (socket, window) of each attached client's current
        // window. Empty means somebody is attached and we could not work out to
        // which window — the weaker failure, which still allows the elsewhere
        // answer, because selecting a pane on a server with a client does reach a
        // screen.
        internal static (ViewerVerdict Verdict, ViewerPane? Found) ViewerAmong(
            IReadOnlyList<ViewerPane> matches,
            IReadOnlySet<string> socketsWithClients,
            IReadOnlySet<(string Socket, string Window)> watchedWindows)
        {
            var visible = new List<ViewerPane>();

            foreach (var pane in matches)
            {
                if (socketsWithClients.Contains(pane.Socket)) visible.Add(pane);
            }

            if (visible.Count == 0) return (ViewerVerdict.NoneFound, null);

            foreach (var pane in visible)
            {
                if (pane.ActiveInItsWindow
                    && watchedWindows.Contains((pane.Socket, pane.Window)))
                {
                    return (ViewerVerdict.TheUserIsLookingAtIt, pane);
                }
            }

            // The two rules below are what remains of three rounds of trying to
            // tell impostors apart by inspection, and inside the visible universe
            // they are very nearly vestigial: teammates and relays live on
            // detached swarm sockets by construction, so the collisions that
            // motivated both have already been filtered out above. They are kept
            // rather than deleted because "by construction" is a fact about how
            // Claude Code launches things today, and a teammate attached into a
            // visible server by hand is a thing a person can do.
            //
            // The claim exclusion, first: a pane another session records as its own
            // is refused for *focusing*, though not for the answer above — see
            // ClaimStillHolds for when a claim is still evidence about a viewer at
            // all, and note that the asymmetry survives for the same reason it was
            // built. Being told "you are already looking at it" cannot be wrong
            // about which session it found; being sent somewhere can.
            ViewerPane? best = null;

            foreach (var pane in visible)
            {
                if (pane.ClaimedByAnother) continue;

                // And the tie-break: the pane active in its own window, else the
                // first found. Two sessions with one title, both visible, neither
                // claimed — two leads someone renamed identically and attached to
                // separately. Nothing here can tell those apart, and a title is
                // /rename-assigned by hand, so that is a coincidence a person
                // created rather than a shape the app produces. If it stops being
                // rare the fix is a stronger identity than the title.
                if (best is null || (pane.ActiveInItsWindow && !best.Value.ActiveInItsWindow))
                {
                    best = pane;
                }
            }

            return best is null
                ? (ViewerVerdict.NoneFound, null)
                : (ViewerVerdict.ElsewhereInTmux, best);
        }

        // Whether two ids name the same job.
        //
        // Prefix in both directions, and empty matches nothing. `claude attach`
        // accepts the short job id and echoes it back that way, so a window
        // opened by hand with `claude attach bd7919f8` has to count as session
        // bd7919f8-…; and the app hands it the short form itself.
        //
        // Here rather than inline at each site because there were three copies of
        // it — this one, AgentTeamViewer.AttachedAlready's and
        // ExistingAttachPane's — and they decide whether a click opens a second
        // window onto a conversation somebody is already reading. Three copies of
        // that is three chances for two of them to disagree about the same pair of
        // ids.
        internal static bool SameJobId(string a, string b) =>
            !string.IsNullOrEmpty(a)
            && !string.IsNullOrEmpty(b)
            && (a.StartsWith(b, StringComparison.Ordinal)
                || b.StartsWith(a, StringComparison.Ordinal));

        // Whether "Dismiss this orb" should be offered: it deletes a status
        // file, and only a local CLI session has one. A gateway or bridged
        // session's orb comes from a socket, so there is nothing on disk to
        // delete and the path this would build from a namespaced key
        // ("openclaw:agent:main:…") is not one this app should write to at all —
        // the same reason ResetSessionToIdle refuses those.
        //
        // Hidden rather than disabled for the sessions it cannot serve. A
        // greyed-out row invites the question "why not", and the answer — this
        // conversation lives somewhere else — is not one a menu can give.
        internal static bool CanDismiss(SessionStatus status) => status.IsLocalCli;

        // Whether "End this session" should be offered. Needs a pid to signal on
        // top of everything Dismiss needs: with no pid recorded there is nothing
        // to terminate, and this is the one action in the app that cannot be
        // undone, so it is offered only where it will do exactly what it says.
        //
        // A pid of 0 is a hook older than the session_pid field, not a session
        // without a process — which is the reason this is a separate rule rather
        // than Dismiss with an extra clause. Such a session can still be
        // dismissed; it just cannot be ended.
        internal static bool CanEndSession(SessionStatus status) =>
            status.IsLocalCli && !status.IsDesktopHosted && status.SessionPid > 0;

        // CB-170: the two OpenClaw rows. An OpenClaw conversation has no pid, so
        // CanEndSession is correctly false for every one of them and these are
        // different operations rather than a wider gate on it — requests to the
        // gateway, which the gateway can refuse.
        //
        // Offered only where they will work, the CB-59 rule: connected, the
        // gateway names the method, and this device holds operator.write. The
        // method list alone is not enough — measured, the gateway advertises
        // methods a device cannot call — and in practice the scope is there
        // exactly when "Allow replying to agents" is on. A room orb is a whole
        // channel standing for several sessions, and neither verb means
        // anything for it.
        internal static bool CanInterruptOpenClaw(SessionStatus status, OpenClawActionContext context) =>
            status.Source == SessionSource.OpenClaw && !status.IsRoom
            && context.Connected && context.Key is not null
            && context.Methods.Contains(OpenClawOrbActions.AbortMethod)
            && context.Scopes.Contains(OpenClawOrbActions.WriteScope);

        // End also needs what the archive itself demands: a session id to send
        // as expectedSessionId, and a session that is not an agent's main one,
        // which the gateway refuses to archive. Hidden there rather than offered
        // and refused. It also sends chat.abort first, so it needs that method
        // too.
        internal static bool CanEndOpenClawConversation(SessionStatus status, OpenClawActionContext context) =>
            CanInterruptOpenClaw(status, context)
            && !context.IsMain && context.SessionId is not null
            && context.Methods.Contains(OpenClawOrbActions.PatchMethod);

        // CB-270: Keep on screen and Rename conversation. Both are sessions.patch,
        // so they need the method and write scope the End row does, and a session
        // id to send as expectedSessionId, but not the abort method and not a
        // non-main session: pinning or renaming an agent's main session is
        // allowed, and there is no run to stop first. A room orb stands for
        // several sessions and neither means anything for it. In practice the
        // write scope is there exactly when "Allow replying to agents" is on.
        internal static bool CanPatchOpenClaw(SessionStatus status, OpenClawActionContext context) =>
            status.Source == SessionSource.OpenClaw && !status.IsRoom
            && context.Connected && context.Key is not null && context.SessionId is not null
            && context.Methods.Contains(OpenClawOrbActions.PatchMethod)
            && context.Scopes.Contains(OpenClawOrbActions.WriteScope);

        // --- the hygiene sweep ------------------------------------------------
        // Nothing but the SessionEnd hook's `rm -f` has ever deleted a status
        // file, and SessionEnd only fires on a graceful exit. So a Ctrl+C'd
        // session leaves its file behind for good, and a finished background job
        // leaves one that no liveness rule can ever touch — its worker is still
        // alive by design, so the pid keeps answering. Six dead-pid files and
        // every `done` job's file were sitting in one real status directory when
        // this was written, and with "Keep orbs for" set to Forever nothing in
        // the app would ever have removed any of them.

        // Whether this scan has *evidence* that the session behind a file is
        // over, as opposed to a reason not to draw its orb today.
        //
        // The distinction is the whole safety of the sweep, because the thing on
        // the other side of it is File.Delete. Only three facts qualify: the
        // process that wrote the file has exited, the daemon says the job it
        // ran has finished, and the session's own transcript says its turn was
        // handed to a background job with nothing having happened since. All
        // three are statements about the session — the third is the session's
        // own record of the handoff, and the file it deletes is one no hook
        // will ever write again, because the conversation now fires its hooks
        // under the fork's session id.
        //
        // Every other verdict is a statement about *us* — Expired is the user's
        // display setting, NoTerminal and NotALiveJob are about whether a click
        // could go anywhere, Superseded is about which of several files for one
        // live process is the current one, and Unknown means the CLI could not
        // be asked. Sweeping on any of those would delete a live session's file
        // because an orb was quiet for an hour, and the hook would then write
        // nothing more until the session's next event: the orb would go, and the
        // session would still be there.
        internal static bool EvidenceOfDeath(SessionManager.ScanVerdict verdict, JobPhase phase) =>
            verdict is SessionManager.ScanVerdict.ProcessGone
                    or SessionManager.ScanVerdict.Backgrounded
            || phase == JobPhase.Done;

        // Whether the grace period has run out on a file that has looked dead
        // since `deadSince`.
        //
        // Measured from when this process first saw the evidence, deliberately
        // not from the file's mtime. ResetSessionToIdle rewrites a status file in
        // place, which refreshes its mtime without the session coming back —
        // reading age off disk would mean "Reset this session to idle" quietly
        // granted a dead file another ten minutes, every time it was clicked.
        //
        // The grace exists because the evidence can be momentarily wrong in one
        // direction that matters: a recycled or briefly unreadable pid, or a job
        // the daemon reports done a moment before its resumption is recorded.
        // Ten minutes of a file nobody is looking at costs nothing; deleting a
        // file the app was wrong about costs the user their session's identity.
        internal static bool SweepDue(DateTime deadSince, DateTime now, TimeSpan grace) =>
            now - deadSince >= grace;
    }
}
