using System.Collections.Generic;

namespace Orbweaver
{
    // The text that gets handed to `osascript`, and the small pure decisions
    // around it.
    //
    // Split out of TerminalFocuser for the reason CLAUDE.md gives for
    // OrbArrangement, OrbGlyph and the transcript parsers, and for the reason
    // TeamLinkGeometry was split out of TeamLinks: TerminalFocuser is 600 lines
    // of tmux, ps and osascript subprocesses that a headless runner must not
    // execute, and these few functions were the only part of it that decides
    // anything without touching the OS. Left in there they were untestable by
    // association.
    //
    // They are worth reaching, because this is a file where being wrong does not
    // show up as a wrong label. The output below is a script that selects a
    // window and sends keystrokes into a live terminal session, so a builder
    // that drops a clause does not display something incorrect — it presses
    // something, somewhere else. Two of the loops here exist because exactly
    // that was reported: orbs going to the wrong desktop on the first click and
    // the right one on the second.
    //
    // Nothing here runs a process. Every comment below is the original
    // reasoning, moved with the code it explains.
    internal static class TerminalScripts
    {
        // The tmux argument list, with the socket pinned when the status file
        // recorded one.
        //
        // -S pins the server: several can coexist (plain tmux, tmuxinator, a -L
        // named socket), and the pane id is only unique within one.
        internal static string[] TmuxArgs(string? socket, params string[] args)
        {
            if (string.IsNullOrEmpty(socket)) return args;

            var full = new string[args.Length + 2];
            full[0] = "-S";
            full[1] = socket;
            args.CopyTo(full, 2);
            return full;
        }

        // One argument, safe to hand to `sh -c`.
        //
        // Single-quoted, with any embedded quote closed and reopened the shell
        // way, so a directory with a space or an apostrophe still arrives as one
        // word. AgentTeamViewer has carried two inline copies of this rule since
        // `claude attach` was wired up; this is the same rule stated where it can
        // be tested, and the builder below is the first caller.
        internal static string ShellQuote(string value) =>
            "'" + value.Replace("'", "'\\''") + "'";

        // `command`, prefixed with a `cd` guard when a directory was recorded —
        // as one line rather than a multi-line script, because the callers below
        // hand this straight into an AppleScript string literal rather than a
        // file on disk.
        //
        // Same rule as TmuxAttachScript's own cd guard, stated once rather than
        // three times: skipped with no cwd, because `cd ''` fails and `|| exit 1`
        // would take the whole command down with it. `command` is trusted as-is
        // — every caller either already built it with ShellQuote (ClaudeCommand)
        // or is TmuxAttachCommand's own output, which begins "unset TMUX; exec
        // …" and must not be wrapped in a second `exec` of its own.
        //
        // `cd --`, not a bare `cd`, because the line can meet an interactive
        // shell — iTerm2 runs it under `-l -i`, and Terminal.app's `do script`
        // always has — and there `cd` is whatever the user's .zshrc made it.
        // zoxide's `init --cmd cd` is a real case, measured on a real Mac: its
        // one-argument form falls through to a fuzzy match when the directory
        // is not there, so a recorded cwd that had been deleted landed in a
        // similarly named project instead and the command ran there, the
        // guard never firing. Its two-argument `--` form goes straight to the
        // builtin, and `--` is POSIX, accepted by sh, bash, dash, ksh and zsh
        // alike. Not `builtin cd`, which dash lacks, and not `command cd`,
        // which zsh resolves to /usr/bin/cd — a program that succeeds without
        // moving anything. It also keeps a directory named with a leading dash
        // from being read as an option.
        internal static string ShellCommandLine(string? cwd, string command) =>
            string.IsNullOrEmpty(cwd) ? command : "cd -- " + ShellQuote(cwd) + " || exit 1; " + command;

        // One tmux client, as `list-clients` describes it.
        internal readonly record struct TmuxClient(
            string Tty, string Session, string Activity, bool ControlMode);

        // Which client to bring the user to, and whether it has to be switched
        // first.
        internal readonly record struct ClientChoice(TmuxClient Client, bool NeedsSwitch);

        // Choose the client — never "any client".
        //
        // With one client attached the distinction does not exist, which is why it
        // survived this long. With two it decides everything: two clients are two
        // terminal windows of the same application, and every step downstream is
        // aimed by the chosen client's tty — the switch-client, the app bundle
        // lookup, and the per-tty window-and-tab selection that brings the right
        // *window* forward rather than merely the right app. Choose wrong and the
        // user watches the wrong window come to the front, which is
        // indistinguishable from the app ignoring them.
        //
        // Two arms, in this order:
        //
        // 1. A client already attached to the session holding the target pane.
        //    That one, and **no switch-client at all** — it is already looking at
        //    the right session, so switching it would be a no-op at best and
        //    yanking a second client off its own session at worst.
        // 2. Otherwise the most recently active client, switched to the target
        //    session. Most-recent because a person with several terminals open is
        //    working in the one they touched last, and moving *that* one is the
        //    least surprising way to show them something.
        //
        // Ties within an arm break the same way, and `client_activity` is a unix
        // timestamp, so an ordinal string comparison is right for equal-width
        // integers and does not care about the format.
        //
        // Pure and named because it was inline in six hundred lines of subprocess
        // calls, where the only way to ask which client it would pick was to have
        // two of them attached and click an orb — which is how "any client" went
        // unnoticed until a machine had two.
        internal static ClientChoice? ChooseClient(
            IReadOnlyList<TmuxClient> clients, string targetSession)
        {
            var onSession = new List<TmuxClient>();
            var elsewhere = new List<TmuxClient>();

            foreach (var client in clients)
            {
                (string.Equals(client.Session, targetSession, StringComparison.Ordinal)
                    ? onSession
                    : elsewhere).Add(client);
            }

            if (MostRecentClient(onSession) is { } here) return new ClientChoice(here, false);

            return MostRecentClient(elsewhere) is { } there
                ? new ClientChoice(there, NeedsSwitch: true)
                : null;
        }

        // Whether the target session has a client attached at all, even one
        // ChooseClient had to pass over because its tty came back empty.
        //
        // CB-158: a click on an orb whose session had a client attached the
        // whole time still opened a brand new iTerm tab, every time. ChooseClient
        // returning null already covers two different facts under one answer —
        // "nobody is attached to this session" and "somebody is attached, but
        // list-clients reported no tty for them" — and FocusTmux treated both as
        // the first: paneAliveButDetached, the flag that sends the click straight
        // into AttachSocket's `open -a`, which stops for neither reason nor for
        // an existing window. The second fact was silently making the exact
        // mistake this file's own comment already names for a different guard —
        // "Nobody wants the same chat in two windows next to each other!!" — just
        // reached through the client's tty instead of the pane's title.
        //
        // A client with an empty tty is real, not a parsing artifact:
        // MostRecentClient's own comment already says list-clients can produce
        // one, which is why it is skipped there in the first place — skipped
        // because nothing downstream can aim a window-selection script at it,
        // not because it means the session is unattended. This function asks the
        // second question separately, so FocusTmux can tell "truly nobody here,
        // open a terminal" apart from "someone is here and a new terminal would
        // duplicate them, so do nothing further" — the same shape of caution
        // ClickFallback.None already uses for a coordinate that could not be
        // resolved.
        //
        // Pure and separate from ChooseClient rather than folded into its return
        // value, for the same reason paneAliveButDetached is threaded out of
        // FocusCore as a second answer instead of a richer bool: it is not a
        // kind of choice, it is a fact about what was seen on the way to making
        // one, and the caller needs it exactly once, after the choice has
        // already come back empty.
        internal static bool AttachedWithNoUsableTty(
            IReadOnlyList<TmuxClient> clients, string targetSession)
        {
            var onTargetSession = false;

            foreach (var client in clients)
            {
                if (!string.Equals(client.Session, targetSession, StringComparison.Ordinal))
                {
                    continue;
                }

                onTargetSession = true;
                if (!string.IsNullOrEmpty(client.Tty)) return false; // ChooseClient will have found this one
            }

            return onTargetSession;
        }

        // The client a person is most likely sitting at: the one touched last.
        //
        // Its own function because two callers want it and one of them is not
        // choosing between sessions at all. PlaceInTmux asks "where is the user"
        // in order to split a pane beside them, and it used to answer that by
        // taking the *first line* `list-clients` happened to print — the same
        // "any client" mistake round eleven found in ResolveClient, surviving one
        // file away because the two picked their client by different code.
        //
        // Clients with no tty are skipped: nothing downstream can aim at one, and
        // `list-clients` can produce them.
        //
        // `client_activity` is a unix timestamp, so an ordinal string comparison
        // is right for equal-width integers and does not care about the format.
        internal static TmuxClient? MostRecentClient(IEnumerable<TmuxClient> clients)
        {
            TmuxClient? best = null;

            foreach (var client in clients)
            {
                if (string.IsNullOrEmpty(client.Tty)) continue;

                if (best is null
                    || string.CompareOrdinal(client.Activity, best.Value.Activity) > 0)
                {
                    best = client;
                }
            }

            return best;
        }

        // The `-F` every client listing asks for, and the parse that reads it
        // back. One pair, because two call sites were building the format string
        // and splitting the tabs separately, and a field added to one would have
        // been read out of position by the other.
        internal const string ClientListFormat =
            "#{client_tty}\t#{client_session}\t#{client_activity}\t#{client_control_mode}";

        internal static List<TmuxClient> ParseClients(string listing)
        {
            var clients = new List<TmuxClient>();

            foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 2) continue;

                clients.Add(new TmuxClient(
                    Tty: parts[0].Trim(),
                    Session: parts[1].Trim(),
                    Activity: parts.Length > 2 ? parts[2].Trim() : "",
                    ControlMode: parts.Length > 3 && parts[3].Trim() == "1"));
            }

            return clients;
        }

        // How to name a session when asking a command that wants a *pane*.
        //
        // Measured, because the obvious form is silently wrong and this cost a
        // round to find. `display-message -p -t <session>` does not take a
        // target-session — it takes a target-pane — so a bare name is parsed as a
        // *window index in the current session*. On a server whose sessions are
        // called "0" and "1", which is tmux's own default naming, asking about
        // session "1" answered `0:1`: the right-looking answer for the wrong
        // session, with no error. Both clients on that machine therefore resolved
        // to the same window, and a pane split "beside the user" could land in
        // somebody else's session.
        //
        //     -t "1"    -> 0:1     wrong, silently
        //     -t "=1"   -> :       empty
        //     -t "1:"   -> 1:1     right
        //     -t "=1:"  -> 1:1     right, and exact
        //
        // The trailing colon makes it a pane target — "that session's active
        // pane" — and "=" forces an exact match rather than a prefix one. Which
        // is exactly what the relay's own tmux naming built and explained as
        // its pane target (deleted with it in 937de9ec), for the same two
        // measured reasons. This is
        // the third call site on this branch found not to be using a rule the
        // codebase had already written down.
        internal static string PaneTargetForSession(string session) => "=" + session + ":";

        // Where a fresh `claude attach` should be put.
        //
        // The whole of round 6a, and it came out of one sentence: "it's taking me
        // to a different tmux window - not this one." Every previous answer moved
        // the *user* — a new tmux window, a new terminal window, a switch-client —
        // and the complaint was never that the destination was wrong. It was that
        // being moved is wrong. So the session comes to them instead: a pane
        // split into the window they are already looking at.
        //
        // Three answers, in descending order of how little they disturb:
        //
        // - BesideTheUser: they are attached to tmux and we know which window
        //   their client is showing. Split it. Nothing moves, and "this one"
        //   stays this one.
        // - ItsOwnTmuxWindow: they are attached somewhere but the window could
        //   not be resolved. A new window in their session is still inside the
        //   thing they use to move around, which is what this app chose before
        //   6a and remains the right consolation prize.
        // - ATerminalWindow: nothing is attached to their tmux at all, so there
        //   is no screen to split or to make a window on — a window created in a
        //   detached server is the same nowhere the orb already pointed at.
        //
        // Pure, and split out from PlaceInTmux for the reason ClickRouting was
        // split out of TerminalFocuser: the old form of this decision was three
        // early returns interleaved with the subprocesses that answered them, so
        // the only way to ask what it did was to click an orb and watch.
        internal enum AttachPlacement
        {
            BesideTheUser,
            ItsOwnTmuxWindow,
            ATerminalWindow
        }

        // attachedSession is the first session name `list-clients` gave back, or
        // empty for a server with no client anywhere. activeWindow is
        // "<session>:<index>" for the window that client is showing, or empty when
        // that second question could not be answered — it is a separate lookup and
        // it can fail on its own, which is exactly the middle case above.
        internal static AttachPlacement PlacementFor(string? attachedSession, string? activeWindow)
        {
            if (string.IsNullOrEmpty(attachedSession)) return AttachPlacement.ATerminalWindow;

            return string.IsNullOrEmpty(activeWindow)
                ? AttachPlacement.ItsOwnTmuxWindow
                : AttachPlacement.BesideTheUser;
        }

        // Where a *new chat* goes, which is not where an attach goes.
        //
        // PlacementFor's split answers "bring this session to me without moving
        // me", and it is right for an orb: the conversation already exists and
        // the user wants it beside what they are doing. A new chat is the
        // opposite request. It starts fresh work rather than joining existing
        // work, and splitting it into the window the user is in halves the
        // space of whatever they were doing to make room for something
        // unrelated — reported exactly that way the first time the fixed iTerm2
        // launch put a New chat beside the user's tmux window rather than in a
        // window of its own.
        //
        // So there are two answers rather than three: a client attached
        // anywhere gets a new window in that client's session — still inside
        // the thing they use to move between windows — and no client gets a
        // terminal window, for the same reason PlacementFor's last arm gives.
        // The active window is not asked about at all, because nothing here
        // would do anything different with it.
        internal static AttachPlacement NewChatPlacementFor(string? attachedSession) =>
            string.IsNullOrEmpty(attachedSession)
                ? AttachPlacement.ATerminalWindow
                : AttachPlacement.ItsOwnTmuxWindow;

        // The two rules as TerminalLauncher applies them, named so a test can
        // hold each caller to its own: PlaceInTmux (every orb attach and
        // AgentTeamViewer launch) takes the first, PlaceInOwnTmuxWindow (New
        // chat) the second. Same shape, so one placement body serves both; the
        // second ignores the active window because it never needs one. Here
        // rather than in TerminalLauncher because that class is excluded from
        // coverage as a whole, and these are decisions.
        internal static readonly Func<string?, string?, AttachPlacement> OrbAttachPlacement = PlacementFor;

        internal static readonly Func<string?, string?, AttachPlacement> NewChatPlacement =
            (session, _) => NewChatPlacementFor(session);

        // The tmux arguments a placement stands for, or null for a placement
        // that is not tmux's to make.
        //
        // Split out of TerminalLauncher's placement, where it was a switch
        // inside a method excluded from coverage for running tmux — the one
        // part of it that decides anything, and the part that now has two rules
        // feeding it.
        internal static string[]? TmuxPlacementArgs(
            AttachPlacement placement, string? session, string? activeWindow, string? cwd, string command) =>
            placement switch
            {
                AttachPlacement.BesideTheUser => TmuxSplitArgs(null, activeWindow!, cwd, command),
                AttachPlacement.ItsOwnTmuxWindow => TmuxNewWindowArgs(null, session!, cwd, command),
                _ => null
            };

        // `split-window` into the window the user is looking at.
        //
        // -h so the conversation lands beside their work rather than under it: a
        // chat is read in lines, and half the height of a terminal is fewer lines
        // than half its width is columns.
        //
        // -P -F '#{pane_id}' so the new pane's id comes back on stdout, which is
        // what the caller hands to the ordinary pane-focusing path — the same
        // contract new-window already had, and the reason neither of these
        // selects or raises anything itself.
        //
        // -c is omitted rather than passed empty when no cwd was recorded: `-c ''`
        // fails and would take the split with it, which is the same trap
        // TmuxAttachScript's `cd` guard documents one screen down.
        //
        // -c is not trusted to land, though, in either builder: the command
        // carries ShellCommandLine's `cd -- '<dir>' || exit 1` guard as well.
        // tmux format-expands -c — it is what makes `-c "#{pane_current_path}"`
        // work — so a directory whose name contains `#{…}` or `#(…)` became a
        // different path, and when the path it becomes does not exist, tmux does
        // not fail: it starts the pane in $HOME. Measured on tmux 3.7c, a real
        // directory named `fmt#{session_name}x` started the CLI in the home
        // directory, as did a cwd deleted after it was recorded, and the pane id
        // came back as if nothing were wrong. `##` does not escape it there.
        // The shell's cd reads the name literally, lands exactly or exits, and
        // -c stays only as the pane's starting point for tmux's own bookkeeping.
        internal static string[] TmuxSplitArgs(
            string? socket, string target, string? cwd, string command)
        {
            var args = new List<string> { "split-window", "-h", "-t", target };

            if (!string.IsNullOrEmpty(cwd))
            {
                args.Add("-c");
                args.Add(cwd);
            }

            args.Add("-P");
            args.Add("-F");
            args.Add("#{pane_id}");
            args.Add(ShellCommandLine(cwd, command));

            return TmuxArgs(socket, args.ToArray());
        }

        // A new window in the user's own session, for when their active window
        // could not be resolved.
        //
        // "<session>:" with the colon, not the bare name. Bare, tmux reads the
        // target as a *window* and refuses with "index N in use" the moment that
        // index is taken; the trailing colon names the session and lets it pick
        // the next free index. That was a real failure, and stating it here rather
        // than in a comment beside an argument list is the point of extracting
        // this at all.
        // CB-316: where a phone's launch goes on a Mac with no attached tmux
        // client — a detached session named orbweaver, created the first time
        // and given a new window by every later launch, so a person can
        // `tmux attach -t orbweaver` and find every phone-started session in
        // one place. -P -F '#{pane_id}' as the other builders, so the caller
        // learns the pane either way.
        internal const string DetachedSession = "orbweaver";

        // The one launch failure that is no_terminal rather than launch_failed.
        internal const string NoTmuxWhy = "tmux is not installed";

        internal static string[] TmuxDetachedArgs(bool sessionExists, string cwd, string command) =>
            sessionExists
                ? TmuxNewWindowArgs(null, DetachedSession, cwd, command)
                : ["new-session", "-d", "-s", DetachedSession, "-c", cwd, "-P", "-F", "#{pane_id}", ShellCommandLine(cwd, command)];

        internal static string[] TmuxNewWindowArgs(
            string? socket, string session, string? cwd, string command)
        {
            var args = new List<string> { "new-window", "-t", session + ":" };

            if (!string.IsNullOrEmpty(cwd))
            {
                args.Add("-c");
                args.Add(cwd);
            }

            args.Add("-P");
            args.Add("-F");
            args.Add("#{pane_id}");
            args.Add(ShellCommandLine(cwd, command));

            return TmuxArgs(socket, args.ToArray());
        }

        // The shell script that puts a terminal onto an existing tmux server.
        //
        // For a session whose pane is alive in a server nothing is attached to —
        // an agent-team member in a detached `claude-swarm-<pid>` socket, which
        // is the shape the user reported as an orb that "does nothing".
        //
        // **The target is mandatory, and this used to be a plain `attach`.** The
        // reasoning for leaving it off was that the caller has already run
        // select-window and select-pane against the pane, so the client lands on
        // the right teammate rather than on whatever the session last had
        // current. That is true, and it is true about the wrong thing: the selects
        // aim *within* a session, while an untargeted attach chooses *which
        // session*, and tmux chooses the most recently used one.
        //
        // Correct for a server with one session, silently wrong for a server with
        // two — and the swarm server has two, because this app's own
        // remote-control relay cohabits it. The relay is the busier of the pair by
        // a wide margin, so an untargeted attach effectively always landed there:
        // measured, `claude-buddy-rc--…` last active at 1787877105 against
        // `claude-swarm` at 1787874827. The user saw an iTerm tab open, land in
        // the relay, and vanish within a beat.
        //
        // So the session is a required parameter rather than an optional one. An
        // untargeted attach is not a degraded answer here, it is an attach to
        // whatever happened to be busy — which on this server is plumbing — and a
        // signature that cannot express it is the only reliable way to keep it
        // from coming back.
        //
        // "=" forces an exact match, the same rule and the same reason as
        // TmuxNames' targets: tmux resolves a target by prefix, and this server
        // holds names where one could be a prefix of another. Verified that
        // attach accepts the form, since not every tmux command does — `attach -t
        // '=claude-swarm'` reaches "open terminal failed: not a terminal", so the
        // target resolved and only the missing tty stopped it, while
        // `-t '=no-such-session'` answers "can't find session". (`show-options
        // -t` rejects the "=" form outright, which is why this was worth checking
        // rather than assuming.)
        //
        // A script file rather than AppleScript's `do script`, for the reason
        // AgentTeamViewer.AttachSession records: `do script` is Terminal.app's
        // own vocabulary, while `open -a <app> <executable file>` is understood
        // by every terminal this app names, so one path covers all of them.
        //
        // An absolute tmux path, never a bare `tmux` resolved by a login shell:
        // `zsh -lc` skips .zshrc, which is where a PATH addition for Homebrew
        // normally lives, so a bare name silently fails whenever the app was
        // launched from Finder. See ClaudeBinary, and the identical note in
        // AgentTeamViewer.
        //
        // The `cd` is for after the attach ends rather than for the attach
        // itself: detach or exit and the window drops to a shell, and the useful
        // place to land is the directory whose orb was clicked. Skipped when no
        // cwd was recorded, because `cd ''` fails and would take the attach with
        // it — the one thing this script exists to do.
        internal static string TmuxAttachScript(
            string tmuxBinary, string? socket, string session, string? cwd)
        {
            // Built with a loop rather than a LINQ chain, which is not a style
            // preference: everything else in this file is plain string building,
            // and a deferred Select/Prepend puts a compiler-generated state
            // machine on the line, which reads in a coverage report as a branch
            // nothing took while the line itself is plainly executed.
            var script = "#!/bin/sh\n";
            if (!string.IsNullOrEmpty(cwd)) script += "cd -- " + ShellQuote(cwd) + " || exit 1\n";

            return script + TmuxAttachCommand(tmuxBinary, socket, session) + "\n";
        }

        // The same attach as one shell command, for the arrival that is a *pane*
        // rather than a window.
        //
        // Owner's own words at the round-thirteen fork, choosing between the two:
        // "Splits in beside you showing what THAT agent is doing." So the socket
        // arm arrives the way the background arm already does — the content comes
        // to him — and a separate terminal window is the fallback for when there
        // is no tmux to split.
        //
        // `unset TMUX` first, and it earns its place twice over. The pane this
        // runs in belongs to the user's own tmux server, so it inherits a TMUX
        // pointing at *that* server while the command attaches to a different
        // one; leaving it set means every tmux command typed in the new pane
        // afterwards talks to the wrong server. It also sidesteps the
        // nested-session guard entirely rather than depending on a reading of
        // when tmux applies it — a distinction this branch has been caught by
        // before, and one that costs nothing to be immune to.
        //
        // No `exec` wrapper here: both callers supply their own context, and the
        // script's `exec` is what makes the terminal window's shell *become* tmux
        // rather than wait behind it.
        internal static string TmuxAttachCommand(string tmuxBinary, string? socket, string session)
        {
            // Built with a loop rather than a LINQ chain, which is not a style
            // preference: everything else in this file is plain string building,
            // and a deferred Select/Prepend puts a compiler-generated state
            // machine on the line, which reads in a coverage report as a branch
            // nothing took while the line itself is plainly executed.
            var parts = new List<string> { ShellQuote(tmuxBinary) };
            foreach (var arg in TmuxArgs(socket, "attach", "-t", "=" + session))
            {
                parts.Add(ShellQuote(arg));
            }

            return "unset TMUX; exec " + string.Join(" ", parts);
        }

        // The last path segment, used to name a Windows Terminal tab after the
        // session's directory.
        internal static string LeafOf(string cwd)
        {
            if (string.IsNullOrEmpty(cwd)) return "";

            var trimmed = cwd.TrimEnd('\\', '/');
            if (trimmed.Length == 0) return "";

            var cut = trimmed.LastIndexOfAny(new[] { '\\', '/' });
            var leaf = cut < 0 ? trimmed : trimmed[(cut + 1)..];

            // "C:" is a drive, not a directory anyone named.
            return leaf.EndsWith(':') ? "" : leaf;
        }

        // Backslashes first, then quotes. The order matters: escaping quotes
        // first would then have their new backslashes escaped again, doubling
        // them.
        internal static string EscapeForAppleScript(string text) =>
            text.Replace("\\", "\\\\").Replace("\"", "\\\"");

        // Which of AgentTeamViewer's three "open a terminal window of its own"
        // call sites goes through AppleScript's own "run this" verb, and which
        // still writes a script file for `open -a` to launch.
        //
        // CB-80: `open -a <app> <script>` hands iTerm2 an *executable file to
        // open*, which is exactly what its Automation gate exists to ask about
        // — and it asked on every single click rather than once, because the
        // file this app writes has a fresh mtime each time even when its name
        // doesn't change. `create window with default profile command "…"` asks
        // iTerm2 to *run a command* instead, which is a different verb the gate
        // does not cover — confirmed by CB-79's probes, which this rule exists
        // to carry into code. Terminal.app's equivalent is `do script`.
        //
        // Ghostty and WezTerm have no comparable scripting surface and neither
        // warns today, so they fall back to the script-file mechanism this
        // exists to avoid for the two apps that have a better one — the null
        // here is what tells AgentTeamViewer's launcher to take that path.
        internal static string? RunScriptFor(string appBundlePath, string? cwd, string command) =>
            RunScriptFor(appBundlePath, cwd, command,
                LoginShellFor(Environment.GetEnvironmentVariable("SHELL"), File.Exists));

        // The same, with the shell iTerm2 is to run the line under passed in
        // rather than read from the environment, so the tests do not depend on
        // the machine they run on. Terminal.app ignores it: `do script` types
        // the line into the session's own shell, which is already the user's.
        internal static string? RunScriptFor(
            string appBundlePath, string? cwd, string command, string loginShell)
        {
            var line = ShellCommandLine(cwd, command);

            return Path.GetFileName(appBundlePath) switch
            {
                "iTerm.app" => ITermRunScript(ITermCommand(loginShell, line)),
                "Terminal.app" => TerminalRunScript(line),
                _ => null
            };
        }

        // The shell iTerm2 runs the line under: the user's own $SHELL when it is
        // one that can parse the line, /bin/zsh otherwise.
        //
        // $SHELL rather than a fixed /bin/zsh, because the point of a login
        // shell here is the user's PATH, and a bash user keeps theirs in
        // .bash_profile/.bashrc, which zsh never reads. But only a POSIX-family
        // shell: the line is sh syntax — `cd -- '…' || exit 1; exec …`, `'\''` for
        // an embedded apostrophe, and TmuxAttachCommand's `unset TMUX` — and
        // fish, nushell or xonsh would misparse some of it, so for those
        // /bin/zsh, which every macOS since 10.15 ships and defaults to, is the
        // better answer than a line that fails. The path also has to be plain —
        // absolute, and nothing in it but the characters a real shell path
        // uses — because it goes to iTerm2 inside single quotes, and
        // ITermCommand below explains why no other character is safe there.
        // And it has to exist: a command iTerm2 cannot exec does not fail, it
        // quietly opens a plain shell instead (see ITermRunScript), which is the
        // defect this exists to fix.
        internal const string FallbackLoginShell = "/bin/zsh";

        static readonly HashSet<string> PosixShells = new(StringComparer.Ordinal)
        {
            "sh", "bash", "zsh", "dash", "ksh", "mksh"
        };

        internal static string LoginShellFor(string? shellEnv, Func<string, bool> exists)
        {
            if (string.IsNullOrEmpty(shellEnv) || shellEnv[0] != '/') return FallbackLoginShell;

            foreach (var c in shellEnv)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-'))
                    return FallbackLoginShell;
            }

            return PosixShells.Contains(Path.GetFileName(shellEnv)) && exists(shellEnv)
                ? shellEnv
                : FallbackLoginShell;
        }

        // What iTerm2 is actually handed as `command`: a shell, asked to run the
        // line.
        //
        // iTerm2's `command` is not a shell command. It splits the string into
        // argv itself and execs the first word directly, with no shell in
        // between — so the line ShellCommandLine builds, `cd -- '<dir>' || exit 1;
        // exec '<bin>'`, was being exec'd as a program named "cd". The session
        // died at once, `create window` answered `missing value`, and the tty
        // lookup after it failed with -1728: a blank window, and "Couldn't open a
        // terminal for Claude Code". That is every iTerm2 launch since CB-80.
        //
        // `-l -i` because the point is the user's own environment. Measured on
        // a real Mac: `zsh -lc` alone did not put ~/.local/bin — where `claude`
        // itself installs — or nvm's node on PATH, because those are set in
        // .zshrc, which only an interactive shell reads. `-lic` got the full
        // PATH, and it is also exactly what Terminal.app's `do script` path
        // runs the same line under, so the two terminals now agree.
        //
        // The line itself travels base64-encoded, and that is not decoration.
        // iTerm2's own argv splitting was measured on 3.7.3 rather than assumed,
        // and it is not POSIX: single quotes keep `$`, `"`, `;` and `|` literal,
        // but backslash sequences are interpreted even *inside* them — `'a\nb'`
        // arrives as a newline, `'a\eb'` as an escape character, `'a\'` does
        // not close the quote, and doubling the backslash does not reliably
        // protect it (`'a\\nb'` still became a newline while `'c\\\\d'`
        // kept two). Outside quotes, `a\\b` becomes `ab` and a bare `~` expands.
        // There is no escaping rule for that to be worth stating, and a
        // directory can legally contain a backslash. So none reaches it: the
        // base64 alphabet has no backslash, quote or tilde, and everything
        // around it — `$(…)`, `"`, `%`, `|` inside one pair of single quotes —
        // was measured to arrive untouched. The shell decodes it and evals it,
        // which is the POSIX layer ShellQuote was already written for.
        //
        // eval of a command substitution, not a pipe into the shell: `claude`
        // needs the session's tty on stdin, and a pipe would take it away.
        internal static string ITermCommand(string loginShell, string line) =>
            "'" + loginShell + "' -l -i -c 'eval \"$(printf %s " +
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(line)) +
            " | /usr/bin/base64 -D)\"'";

        // `create window with default profile command` is iTerm2's "run this",
        // not "open this file" — the verb switch CB-80 exists for. `command` is
        // exec'd as argv with no shell, so what arrives here is ITermCommand's
        // output rather than the bare line. Returns the new session's tty on
        // stdout, so the caller has real confirmation a window was actually
        // created rather than only that osascript launched (the same
        // distinction RunOsaScript's own comment draws for the fire-and-forget
        // focus path).
        //
        // A window, not the command: the tty says nothing about whether the
        // command ran. Handed the old bare line, iTerm2 3.7.3 was seen doing two
        // different things on the same Mac — the diagnosis that found this bug
        // got `missing value` for the window and -1728 from the tty lookup,
        // while six later runs of the identical script each got a tty back and
        // exit 0, with iTerm2 having quietly started a plain login shell in ~
        // instead. Either way Claude Code never started. Which is why getting
        // `command` right is the whole fix, and a returned tty cannot be read as
        // proof of it.
        internal static string ITermRunScript(string command) => $$"""
            tell application "iTerm"
                set w to (create window with default profile command "{{EscapeForAppleScript(command)}}")
                tell current session of w
                    return tty
                end tell
            end tell
            """;

        // `do script` without an "in" clause opens a brand-new window and runs
        // `line` in it, the same arrival `open -a Terminal.app <script>` gave —
        // but as a command Terminal.app is asked to run rather than a file it is
        // asked to open, which is the distinction that avoids iTerm2's gate and,
        // as far as CB-79's probes went, Terminal.app never raised the dialog
        // either way.
        internal static string TerminalRunScript(string line) => $$"""
            tell application "Terminal"
                set t to do script "{{EscapeForAppleScript(line)}}"
                return tty of t
            end tell
            """;

        // property is "id" (a session UUID recorded by the hook) or "tty" (the
        // live tty of an attached tmux client). Both are iTerm2 session
        // properties; a no-match still activates, which is better than nothing.
        //
        // Activate, *wait*, then select. The order and the wait are both
        // load-bearing, and this supersedes an earlier reading of the same
        // behaviour — worth spelling out, because the obvious experiment gives
        // the wrong answer.
        //
        // What macOS actually does: activating an app raises whichever of its
        // windows are on the Space you're looking at, and only follows the app
        // to another Space when it has none here. Ordering a specific window
        // front (`select w`) *does* pull you to its Space — but only if the app
        // is already active. If an activation is still in flight, it lands
        // afterwards and raises the local window instead, undoing the select.
        //
        // That's why the first reading was "activate must come last": tested
        // from a desktop with no terminal window on it, where activation alone
        // switches Spaces and select-then-activate therefore appears to work.
        // From a desktop that *does* have a terminal window, the same script
        // needed two clicks — the first activating, the second selecting with
        // the app already active. Both observations come from the same rule.
        //
        // So: activate, wait for the activation to actually land, then select.
        //
        // Waited for rather than guessed at. This used to be `delay 0.2`, on the
        // reasoning that a fifth of a second is unnoticeable and is the gap a
        // double click was inserting by hand — both true, and it still lost the
        // race often enough to be reported as "clicking an orb doesn't switch
        // desktops any more". Measured on an idle machine, `activate` took 145ms,
        // 167ms and 531ms on three consecutive runs of the same script: the
        // spread is the problem, not the average. Every run slower than the delay
        // selects the window while the activation is still in flight, and then
        // the activation lands and raises the local window instead — the exact
        // failure described above, except intermittent, which is why it reads as
        // "sometimes it works".
        //
        // Polling `frontmost` costs nothing when activation is quick — measured
        // at 141ms end to end, faster than the fixed delay it replaces, because
        // it stops as soon as the app is really there — and keeps waiting when it
        // isn't. The cap is a backstop, not a timeout anyone should reach: if the
        // app never comes forward, selecting a window is going to fail anyway,
        // and hanging the click is worse than trying and missing.
        //
        // `frontmost of application "X"` is answered by the app itself, so this
        // needs no permission beyond the Automation grant the activate already
        // required. Both `delay` and the repeat sit outside the tell block on
        // purpose: inside one they are dispatched to the application, which
        // doesn't understand them, and the whole script fails with "Can't
        // continue delay".
        internal const int ActivationPollTicks = 40;      // x 50ms = 2s ceiling

        internal static string ActivateThenSettle(string app) => $$"""
            tell application "{{app}}" to activate
            repeat {{ActivationPollTicks}} times
                if frontmost of application "{{app}}" then exit repeat
                delay 0.05
            end repeat
            """;

        // How many times to re-assert a selection that has not taken yet.
        //
        // Settling on `frontmost of application` is not quite enough, and the
        // gap is one step past the note above: that property flips true when the
        // app becomes active, which is *before* macOS has finished raising the
        // window activation itself brings forward. A select landing in that gap
        // is overruled a moment later by the app's own most-recently-used
        // window — and when that window is on another desktop, you are taken to
        // the wrong desktop as well as the wrong window.
        //
        // Reported as orbs going somewhere wrong on the first click and right
        // on the second, which is the same rule from the other side: by the
        // second click the app is already frontmost, so activation raises
        // nothing and there is no raise left to overrule the select.
        //
        // Checked against `frontmost of w`, which is the window's own answer and
        // reflects what is actually in front. `id of current window` was tried
        // first and is useless here — it is iTerm's internal notion of current
        // and reads as correct while a different window is on screen, which is
        // exactly the failure being chased.
        //
        // Deliberately *not* `set frontmost of w to true` alongside the select.
        // That was in the first version of this and measured worse than the code
        // it replaced — three failures in six against none — so it is out, and
        // the loop is only a loop.
        internal const int SelectionVerifyTicks = 12;     // x 50ms = 600ms ceiling

        internal static string ITermSelectScript(string property, string value) => $$"""
            {{ActivateThenSettle("iTerm")}}
            tell application "iTerm"
                repeat with w in windows
                    repeat with t in tabs of w
                        repeat with s in sessions of t
                            if {{property}} of s is "{{value}}" then
                                repeat {{SelectionVerifyTicks}} times
                                    select w
                                    select t
                                    select s
                                    if frontmost of w then return
                                    delay 0.05
                                end repeat
                                return
                            end if
                        end repeat
                    end repeat
                end repeat
            end tell
            """;

        // Accepts either form the two paths produce: a bare "ttys004" from the
        // hook, or a "/dev/ttys004" client tty from tmux.
        //
        // Activate, settle, then select — same Spaces rule as ITermSelectScript,
        // where it's explained.
        internal static string TerminalSelectScript(string tty) => $$"""
            {{ActivateThenSettle("Terminal")}}
            tell application "Terminal"
                repeat with w in windows
                    repeat with t in tabs of w
                        if tty of t is "{{(tty.StartsWith("/dev/") ? tty : "/dev/" + tty)}}" then
                            -- Re-asserted until it takes, for the reason
                            -- SelectionVerifyTicks gives. Terminal windows
                            -- answer `frontmost` too, so the check is the same
                            -- question asked of the same kind of object.
                            repeat {{SelectionVerifyTicks}} times
                                set selected of t to true
                                set index of w to 1
                                if frontmost of w then return
                                delay 0.05
                            end repeat
                            return
                        end if
                    end repeat
                end repeat
            end tell
            """;
    }
}
