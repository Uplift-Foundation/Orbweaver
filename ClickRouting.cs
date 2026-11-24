namespace Orbweaver
{
    // Where a click goes when the terminal the hook recorded could not be
    // brought forward — the last thing tried before a click does nothing at all.
    //
    // "Nothing at all" was the actual complaint: "most of these dead orbs when I
    // try to go to terminal they do nothing." Every failure on that path is
    // silent, and there are five of them in a row. For a team member in a
    // detached swarm socket: FocusTmux selects the pane and then returns false
    // because no client is attached anywhere; the app switch has no case for
    // term_program "tmux"; FocusByTty walks up from a tmux-server pty and finds
    // no app bundle; the team-lead fallback lands on a headless background
    // session and fails the same way; the attach fallback was gated on having no
    // pid, which a team member has; and ReportFocusFailure writes to stderr,
    // which a bundled .app has nowhere to show. Six things went right and the
    // user saw nothing move.
    internal enum ClickFallback
    {
        // Nothing further to try, which is not quite the same as "coordinates
        // were recorded". The rule below reaches this for a session that
        // recorded coordinates *and* has a pid, and for one this app will not
        // attach for at all (Codex, or an orb whose id was not passed in) —
        // while a session with coordinates and no pid is answered by
        // AttachBackground, because a hook too old to record a pid is too old to
        // be trusted about the rest.
        //
        // Deliberately nothing in the case that matters, though: a session that
        // named a terminal has one, and opening a *second* window onto it would
        // hide a real failure behind a new window every time. That is the old
        // gate's diagnosis-over-surprise-window reasoning, and it still holds
        // exactly where it was written for.
        None,

        // The `claude agents` roster — focused if one is running, opened if not.
        //
        // **Never returned by FallbackFor.** It is an affordance now, not an
        // answer to a click: the right-click menu's "Open agents view" on a
        // background orb, and nothing else. Reached by naming it, which is why it
        // stays in this enum and in RunFallback's switch rather than becoming a
        // method of its own — the roster is opened through the same
        // one-verb-one-destination path as every other click answer, and a second
        // opener beside RunFallback would be a second place for the pane-focusing
        // tail to be forgotten.
        //
        // It briefly *was* the click default, and that was a misreading of the
        // request. "I don't understand why you can't go straight to it!" was taken
        // to mean the roster, because that is where the user was looking when they
        // said it; live use settled it the other way within the hour. Double-
        // clicking the orb of the session they were mid-conversation with pulled
        // them out of that window and dropped them on the dashboard — "cd is
        // taking me to the wrong window." "It" was always the session. Everywhere
        // else in this app a click means "take me to this session", and a roster
        // of every session is a different thing wearing the same gesture.
        AgentsView,

        // A background session, in any phase: `claude attach <id>` in a window of
        // the user's own tmux, landing them *in the conversation*.
        //
        // The default for these orbs, restored. It also covers the narrower case
        // it started as — a session recording no pid at all, from a hook older
        // than that field — which is a shape nobody can enumerate and so has
        // nothing but its own name to be reached by.
        //
        // Carrying it out never opens a second window onto a conversation someone
        // is already reading: AgentTeamViewer.AttachSession asks first whether a
        // `claude attach` client for this id already exists, hands back its tmux
        // pane if it is in one so the ordinary focus path raises it, and raises
        // its app if it is in a window of its own. That is step one of the ladder
        // and it lives there rather than as a fallback value of its own, because a
        // separate value would dispatch to exactly the same code. The id matching
        // it turns on is SessionPresence.SameJobId, shared with the dimming rule;
        // what is *not* shared is which way a failed process scan falls, and
        // AttachedAlready says why beside itself.
        AttachBackground,

        // The pane is alive in a tmux server nothing is attached to. Open a
        // terminal on that server: the select-window/select-pane that already
        // ran means a plain attach lands on the right pane.
        AttachSocket,

        // No terminal coordinates at all, and not a job the daemon lists —
        // an agent-mode direct child (`claude --session-id <id> --agent
        // <name>`). `claude attach <id>` regardless of pid.
        AttachById
    }

    // Which of those applies. Pure, for the reason everything else on this
    // branch is: TerminalFocuser is six hundred lines of tmux, ps and osascript
    // subprocesses that a headless runner must not execute, and this decision
    // was inline in the middle of it — reachable only by clicking an orb on a
    // real machine and watching what happened, which is precisely how it came
    // to be wrong for a whole class of session without anyone noticing.
    internal static class ClickRouting
    {
        // Whether this session recorded nothing a click could be aimed at.
        //
        // The same four fields JudgeReachability's no-terminal rule reads, plus
        // the tty — and the tty is the difference between this and
        // SessionManager.KnowsATerminal, which deliberately ignores it. The two
        // questions are not the same. KnowsATerminal asks "is there a *window*
        // behind this", and a tty alone can name a tmux server's pty, which
        // belongs to no window. This asks "is there anything to *try*", and a
        // tty alone is enough: FocusByTty walks the process tree above it and
        // can find the app that owns it.
        //
        // That distinction is what keeps AttachById off every ordinary session.
        // Read the other way round it would fire for any session whose only
        // coordinate is a tty, which is most background-ish sessions that do in
        // fact have a window.
        internal static bool NoCoordinatesAtAll(SessionStatus status) =>
            string.IsNullOrEmpty(status.Tty)
            && string.IsNullOrEmpty(status.TermProgram)
            && string.IsNullOrEmpty(status.TermId)
            && string.IsNullOrEmpty(status.TmuxPane)
            && status.TermPid == 0;

        // Whether this session's *own* hook recorded the tmux pane it is running
        // in — as opposed to a pane something else put there on its behalf.
        //
        // The two are already distinguishable and the distinction is deliberate:
        // AgentTeamViewer.TryAdopt fills a terminal-less session's coordinates in
        // from the `claude agents` viewer watching it, and leaves TmuxBin empty on
        // purpose, "because it records where the *hook* found tmux, and this
        // didn't come from a hook". So a pane with a tmux binary beside it is the
        // session saying where it is; a pane without one is this app's guess about
        // where somebody is watching it.
        //
        // That difference decides a click. A session that recorded its own pane
        // has *told* us where its conversation is, and no rule that infers a
        // location — a shape classification, a shared title — should outrank a
        // statement.
        internal static bool RecordedItsOwnPane(SessionStatus status) =>
            !string.IsNullOrEmpty(status.TmuxPane) && !string.IsNullOrEmpty(status.TmuxBin);

        // Whether *somewhere* would reach this session — the question the chat
        // panel asks, where the click asks "what should this gesture do".
        //
        // Answered by running the same rule with paneAliveButDetached false,
        // rather than by a second rule that agrees with it today: the panel must
        // never offer an attach for a session a click would not attach, and the
        // two would drift the first time either changed. False is the honest
        // input here — the panel has not tried to focus anything, so it has not
        // learned that anything is detached, and the socket answer is not one it
        // can offer anyway (that one is about a pane the *click* already
        // selected).
        internal static bool AttachWouldReach(SessionStatus status, string? sessionId) =>
            FallbackFor(status, sessionId, paneAliveButDetached: false)
                is ClickFallback.AttachBackground
                or ClickFallback.AttachById;

        // Whether "take me to this session" means opening a browser.
        //
        // True for a cloud session and nothing else (CB-443: a Paperclip orb opens its panel;
        // "Open in Paperclip" is on the panel and in the orb menu). The address itself is not
        // consulted, deliberately: a cloud session whose URL never arrived
        // should open nothing and stop there rather than fall through to a
        // terminal focus, which would send the click hunting a pane for a
        // conversation that has never been on this disk and land it on whatever
        // happened to match. Opening nothing is the honest failure; opening
        // somebody else's terminal is not.
        //
        // Here rather than inside the orb for the reason everything else in this
        // file is here: the orb knows which session was clicked and nothing
        // else, and a rule that lives next to the Process.Start it guards is a
        // rule no test can reach.
        internal static bool OpensInABrowser(SessionStatus? status) =>
            status?.Source == SessionSource.ClaudeCloud;

        // Whether "take me to this session" means bringing Claude Desktop forward.
        //
        // A session Claude Desktop hosts has no terminal to focus and no browser
        // address: the app that owns it is the destination, and DesktopSessionFocus
        // finds its window at click time. Null-safe for the same reason
        // OpensInABrowser is — the orb can be clicked before its first update.
        internal static bool OpensClaudeDesktop(SessionStatus? status) =>
            status?.IsDesktopHosted == true;

        // Whether this orb should offer "Open agents view" on its right-click menu.
        //
        // The roster's new home. It is a good destination and a bad default: it is
        // where these sessions are managed from, it groups the ones wanting
        // attention at the top, and it knows how to attach to a row — none of
        // which makes it the answer to a gesture that means "take me to this
        // session". A menu item is where an app puts the thing you sometimes want,
        // and a double-click is where it puts the thing you always want.
        //
        // Offered on background orbs alone, which is the set the roster lists.
        // Putting it on every orb would be a menu item that opens a window with
        // the clicked session nowhere in it — Claude Code's own roster is jobs,
        // not terminals — and the two lifecycle items above it are already
        // hidden rather than greyed for exactly that reason.
        internal static bool OffersTheAgentsView(SessionStatus status) =>
            status.Source == SessionSource.ClaudeCode
            && status.Shape == LocalSessionShape.Background;

        // Whether the team lead's window is allowed to answer this click.
        //
        // It is allowed only when nothing else would show the session that was
        // actually clicked, and getting that round the right way is the whole of
        // CB-13's team-orb bug. The lead used to be tried *first*, on the
        // reasoning — stated in Focus, and quoted here because it was wrong
        // rather than merely incomplete — that "a team member whose lead is
        // focusable lands on the lead's window today, which works well and is
        // what the user expects".
        //
        // It does not work well, and the failure is invisible, which is why it
        // survived. The lead's window is, in the normal case, the window the user
        // is already looking at: it is where they started the team from and where
        // they are watching it work. Bringing it forward when it is already
        // forward is indistinguishable from a click that did nothing — and it
        // shows the *lead's* conversation, not the teammate's, so even when it
        // does come forward it is the wrong session.
        //
        // The differential in the bug report falls straight out of the old
        // ordering. A non-team orb has no lead, so its click fell through to the
        // fallback below and opened a window someone could see; a team orb's
        // click was answered by the lead and stopped there. Measured on the
        // reporter's own machine: their teammates' panes were alive in a detached
        // `claude-swarm-<pid>` socket (so AttachSocket was the right answer and
        // was never asked for), while a lead sitting in the default socket had a
        // client attached and was focusable — so every teammate click was
        // swallowed by a window that never moved. The proof it never ran is on
        // disk: attach-tmux-socket.sh, which that arm writes before it does
        // anything else, had never been created.
        //
        // Pure and named rather than an inverted `if` inside Focus, for the
        // reason the rest of this file is pure: the old ordering was reachable
        // only by clicking an orb on a real machine and watching nothing happen,
        // which is exactly how it stayed wrong.
        internal static bool LeadMayAnswer(ClickFallback fallback) =>
            fallback == ClickFallback.None;

        // paneAliveButDetached is what FocusTmux learned on the way past: the
        // pane exists, its server answered, the pane was selected — and no
        // client is attached to it anywhere. It is passed in rather than
        // re-derived because answering it costs three tmux subprocesses, and
        // because it is a fact about a moment that has already gone.
        //
        // Asked only after the recorded terminal *and* the team lead have both
        // failed to come forward. That ordering is the important half of this
        // whole change and belongs to the caller, but it is worth stating here
        // too: a team member whose lead is focusable today lands on the lead's
        // window, which works well and is what the user expects. Opening a
        // terminal on the swarm socket instead would be a surprise window for a
        // click that already did the right thing.
        internal static ClickFallback FallbackFor(
            SessionStatus status, string? sessionId, bool paneAliveButDetached)
        {
            // Claude Desktop owns this session, so none of the terminal answers
            // below apply — and in particular the chat panel must not offer an
            // attach (AttachWouldReach) for a conversation `claude attach` would
            // not find. The click itself never reaches here for these orbs;
            // this is the answer for the panel and the belt to that braces.
            if (status.IsDesktopHosted) return ClickFallback.None;

            var named = !string.IsNullOrEmpty(sessionId);

            // `claude attach` is Claude Code's own verb and there is no Codex
            // equivalent, so both attach-by-id answers are Claude Code's alone.
            // The scan already drops a pid-less Codex session before it can have
            // an orb, so nothing should reach here — this is the belt to that
            // braces, because the failure it prevents is a window opening onto
            // someone else's session.
            var claudeCode = status.Source == SessionSource.ClaudeCode;

            // A background session, in any phase, and ahead of the socket answer
            // below. The shape rather than the phase: a background session is a
            // background session whether it is working, holding a question or
            // finished, and `claude attach` reaches all three. The orb's
            // *rendering* distinguishes them; where the click goes does not need
            // to.
            //
            // Ahead of the socket answer because a parked job can have been
            // adopted into a `claude agents` viewer pane, and if that viewer's
            // server happens to be detached, the socket answer would attach a
            // terminal to the roster's server — landing on a dashboard for a
            // click that asked for a conversation.
            //
            // The second clause is the rule this was widened from, kept because it
            // covers a session the first cannot: a hook older than the session_pid
            // field may not be a job at all, and nothing can enumerate it, so its
            // own name is the only handle on it.
            // Its own pane, measured alive and with nobody attached to its server,
            // outranks everything below — including the background answer, which
            // used to come first and is the reason a teammate's click never
            // reached the socket path built for it.
            //
            // A teammate is classified Background whenever the daemon's listing
            // names it, because ShapeOf gives the job phase priority "first and
            // unconditionally". So a teammate with a live pane of its own answered
            // AttachBackground, which is the arm the title scan lives in — and a
            // teammate inherits its lead's title by construction, so that scan
            // matched the *lead's* viewer and the click landed in the lead's
            // window. Measured beats classified: FocusTmux has just been to this
            // session's own server and confirmed the pane is there with no screen
            // on it, which is the strongest thing anything here knows.
            //
            // Gated on the pane being the session's own, which is what keeps
            // round six's reason for the old order intact. That reason was real: a
            // parked job adopted into a `claude agents` viewer pane would send a
            // click to a dashboard if the viewer's server happened to be detached.
            // An adopted pane carries no tmux binary — see RecordedItsOwnPane —
            // so it does not qualify here and keeps its old place in the order.
            if (paneAliveButDetached && RecordedItsOwnPane(status))
            {
                return ClickFallback.AttachSocket;
            }

            if (claudeCode && named
                && (status.Shape == LocalSessionShape.Background || status.SessionPid <= 0))
            {
                return ClickFallback.AttachBackground;
            }

            // The only one of the three that is not `claude attach`, and so the
            // only one that is not Claude Code's alone: attaching to a tmux
            // server is a tmux operation, and a Codex session in a detached
            // pane is in exactly the same bind for exactly the same reason.
            if (paneAliveButDetached) return ClickFallback.AttachSocket;

            // Nothing recorded at all. This is the agent-mode direct child —
            // `claude --session-id <id> --agent <name>`, which has a real pid
            // and no terminal anywhere — and the reason the diagnosis rule
            // above does not cover it: there is no recorded terminal that
            // failed to resolve, so there is nothing to diagnose and no window
            // for a second one to be confused with.
            //
            // Reached only when the session is not a job the daemon lists,
            // because a job would have been answered by AttachBackground
            // above. An attach that turns out to name nothing prints "No job
            // matching" in a window the user opened on purpose, which is a
            // visible answer — and a visible wrong answer beats an invisible
            // line on a stderr nobody is reading.
            if (claudeCode && named && NoCoordinatesAtAll(status))
            {
                return ClickFallback.AttachById;
            }

            return ClickFallback.None;
        }
    }
}
