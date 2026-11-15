using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Orbweaver
{
    // Where to find a team lead that has no terminal of its own.
    //
    // A lead can be a background session: Claude Code runs it inside
    // `claude daemon run`, which has no controlling terminal, and if the
    // interactive session that started it has since gone away that process ends
    // up reparented to launchd with no tty anywhere above it. The hook walks up
    // looking for one, finds nothing, and writes a status file naming no
    // terminal at all — so the app dropped the orb, on the reasonable-sounding
    // grounds that clicking it could go nowhere.
    //
    // That premise is wrong. You watch such a team through `claude agents`,
    // which is a *separate* process sitting in a terminal — a real window, just
    // not one anything in the session's own process tree points at. Nothing
    // else on disk points at it either: ~/.claude/session-env/<id>/ is empty,
    // and the team config records the lead as "in-process" with no pane. The
    // process table is the only place the connection exists.
    //
    // So the connection is made here, by the one thing the two share: the
    // directory. `claude agents` is the view of the team in the directory it
    // was run from, which is the same directory the lead session reports. Two
    // teams led from one directory would be ambiguous — the cost of that is
    // landing on the other team's viewer, which is a window you wanted to see
    // anyway, so it degrades to something harmless rather than wrong.
    // Excluded from coverage, as a class. Every member of it either runs tmux as
    // a real subprocess to find or open a viewer pane, or shells out to `open -a`
    // to bring a terminal application forward — fourteen members already carried
    // the attribute individually, and what was left uncovered was the static
    // fields they share, which only exist because those members do.
    //
    // Marking the class rather than the members also fixes something the
    // per-member version could not: a field initializer belongs to the type
    // initializer, not to any method, so it is reported unhit whenever nothing
    // touches the class at all — which is exactly the situation a fully excluded
    // class is in.
    [ExcludeFromCodeCoverage]
    internal static class AgentTeamViewer
    {
        // The lookup runs `ps` and `lsof`, so it is cached rather than repeated
        // every two-second scan. Short, because the viewer is a thing you open
        // and close by hand and an orb that stays unclickable for a minute
        // after you open one would read as broken.
        private const long CacheMs = 5_000;

        private static readonly object Gate = new();
        private static readonly Dictionary<string, (AgentViewer? Found, long Stamp)> Cache =
            new(StringComparer.Ordinal);

        // Which terminal app a viewer for a directory was opened into, so a
        // later click can bring that app forward instead of opening a second
        // window onto the same team.
        private static readonly Dictionary<string, string> Launched =
            new(StringComparer.Ordinal);

        // The viewer for a directory, or null when none is running there. This
        // used to sit behind TryAdopt, which the scan called on the UI thread
        // and which walked `ps` and `lsof` on every cache miss. The scan now
        // calls this from its background half, only for the directories
        // ScanProbePlan names, and applies the answer on the UI thread through
        // ScanProbes.AdoptViewer. Off macOS there is no `claude agents` window
        // to find, and without a directory there is nothing to match on: those
        // were TryAdopt's first two checks, and they are still this one's.
        //
        // Excluded from coverage: a wall-clock cache around the process scan
        // below.
        [ExcludeFromCodeCoverage]
        internal static AgentViewer? For(string cwd)
        {
            if (!OperatingSystem.IsMacOS() || string.IsNullOrEmpty(cwd)) return null;

            var key = cwd.TrimEnd('/');
            var now = Environment.TickCount64;

            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var cached) && now - cached.Stamp < CacheMs)
                {
                    return cached.Found;
                }
            }

            var viewer = Locate(key);

            lock (Gate)
            {
                Cache[key] = (viewer, now);
            }

            return viewer;
        }

        // Excluded from coverage: walks live pids and reads each one's cwd and
        // tty.
        [ExcludeFromCodeCoverage]
        private static AgentViewer? Locate(string cwd)
        {
            foreach (var pid in ViewerPids())
            {
                if (!string.Equals(CwdOf(pid), cwd, StringComparison.Ordinal)) continue;

                // TMUX is "<socket>,<server pid>,<session index>" — the same
                // shape the hook records, and only the socket is wanted.
                var env = MacOSProcessScan.EnvironmentValues(pid, "TMUX", "TMUX_PANE");

                var tmux = env.GetValueOrDefault("TMUX", "");
                var pane = env.GetValueOrDefault("TMUX_PANE", "");
                var tty = TtyOf(pid);

                if (string.IsNullOrEmpty(tmux) || string.IsNullOrEmpty(pane))
                {
                    // Running outside tmux: the tty alone is enough for the
                    // app to find the window that owns it.
                    if (string.IsNullOrEmpty(tty)) continue;
                    return new AgentViewer("", "", tty);
                }

                var socket = tmux.Split(',')[0];
                return new AgentViewer(socket, pane, tty);
            }

            return null;
        }

        // `<absolute claude> <verb> [arg]`, quoted for `sh -c`. Null when the
        // claude binary cannot be found at all, which is the one failure worth
        // returning rather than papering over: every caller's next move is to
        // hand this to a shell.
        //
        // Absolute, never a bare `claude` resolved by a login shell. That was the
        // original approach and it silently didn't work: `zsh -lc` skips .zshrc
        // (non-interactive), which is where a PATH addition for ~/.local/bin
        // normally lives, so the script died with "command not found" whenever
        // the app was launched from Finder rather than a terminal. See
        // ClaudeBinary.
        // Excluded from coverage: probes the filesystem for the claude binary.
        [ExcludeFromCodeCoverage]
        private static string? ClaudeCommand(string verb, string? argument = null)
        {
            var claude = ClaudeBinary.Path;
            if (claude is null) return null;

            var command = TerminalScripts.ShellQuote(claude) + " " + verb;
            return argument is null ? command : command + " " + TerminalScripts.ShellQuote(argument);
        }

        // Opens a `claude agents` roster, in tmux for someone who lives there and
        // in a terminal window otherwise.
        //
        // In tmux when there is a tmux to go into, for the reason AttachSession
        // gives at the same fork: a bare window for someone whose windows are all
        // inside tmux puts the thing *outside* what they use to move between
        // windows, which is worse than an extra pane inside it.
        // Excluded from coverage: opens a terminal, via AppleScript for the two
        // apps that support it and a script file for the two that don't.
        [ExcludeFromCodeCoverage]
        private static string? OpenAgentsView(string cwd)
        {
            if (ClaudeCommand("agents") is not { } command) return null;

            if (PlaceInTmux(command, cwd) is { Length: > 0 } pane) return pane;

            // cd is for after the roster is quit rather than for the roster
            // itself, the same as every other launch this file makes: the
            // useful place to land is the directory whose orb was clicked.
            LaunchInTerminal(TerminalApp(), cwd, "exec " + command);

            // The window takes a moment to appear, and until it does the
            // process scan above says nothing is open — which is what would
            // otherwise let a second click open a second roster.
            Forget(cwd);
            return null;
        }

        // The `claude agents` view: brought forward if one is running, opened if
        // not.
        //
        // The click destination for a background orb, and it is a decision about
        // vocabulary rather than a mechanism. The user's words, looking at the
        // roster: "don't understand why the orbs can't just match this and attach
        // to this", and then "I don't understand why you can't go straight to
        // it!" The view already groups what needs answering at the top, already
        // knows how to attach to a row, and is where these sessions are managed
        // from — so an orb that lands there lands somewhere the user recognises,
        // rather than in a terminal running one session with no way back to the
        // others.
        //
        // Unfiltered, deliberately: `claude agents --help` offers only `--cwd
        // <path>` and no per-session preselect, and jobs on the machine this was
        // written for span several projects — a filtered view per project would
        // multiply windows for no gain, since the roster is short and sorted with
        // the ones wanting attention first.
        //
        // Returns the pane it went into when it went into tmux, for the caller to
        // select and raise through the ordinary path — the same contract
        // AttachSession has, and for the same reason: this file does not grow its
        // own copy of client resolution and window raising.
        // Excluded from coverage: walks the process table, then either activates
        // a real application or opens a terminal on a script.
        [ExcludeFromCodeCoverage]
        public static string? OpenOrFocusAgentsView(string cwd)
        {
            if (!OperatingSystem.IsMacOS()) return null;

            // Already open somewhere. Focused rather than duplicated: a second
            // roster of the same sessions is not a second thing to look at.
            //
            // Asked of the process table rather than of Launched, so a view the
            // user opened by hand counts — which is the common case, since this
            // is where they manage these sessions from.
            foreach (var pid in ViewerPids())
            {
                var env = MacOSProcessScan.EnvironmentValues(pid, "TMUX", "TMUX_PANE");
                var pane = env.GetValueOrDefault("TMUX_PANE", "");

                // In tmux: hand the pane back, exactly as AttachSession does, so
                // the caller selects it and raises its client's window.
                if (!string.IsNullOrEmpty(pane)) return pane;

                // Outside tmux: its tty is enough for the app to find the window
                // that owns it, which is what ActivateApp's caller does with it.
                var tty = TtyOf(pid);
                if (string.IsNullOrEmpty(tty)) continue;

                ActivateApp(TerminalApp());
                return null;
            }

            return OpenAgentsView(cwd);
        }

        // Every `claude attach <id>` client running on this machine, keyed by the
        // id it was given — or null when the process table could not be read at
        // all, which is a different answer and the caller treats it as one.
        //
        // The point of it is a contradiction the user hit immediately: they
        // attached to all three parked sessions and the orbs stayed grey.
        // Attaching changes nothing this app was looking at — the status file
        // records the *worker's* ancestry and never the viewer's, so the tty
        // stays empty, and the daemon still says "blocked", because from its side
        // nothing has changed. The person's presence exists in exactly one place,
        // which is the process table.
        //
        // Matched on the arguments rather than the executable path, for the
        // reason ViewerPids gives: the path is a version-stamped location that
        // moves under you. Cached on the same clock as the viewer lookup — one
        // `ps` per five seconds rather than one per session per scan.
        // Excluded from coverage: walks the live process table. What is decided
        // with the answer is SessionPresence.HasAttachClient, which is pure and
        // covered per case, including the null.
        [ExcludeFromCodeCoverage]
        // fresh skips the cache read, for a caller answering a gesture the user
        // just made rather than a scan that runs every two seconds. Five seconds
        // of staleness is nothing to the scan and everything to someone who closed
        // their attach window and immediately clicked the orb: they would get an
        // app raised that has no such window, and the click would appear to do
        // nothing. It still *writes* the cache, so a click warms the next scan
        // rather than costing an extra `ps`.
        //
        // A parameter rather than a second method, because a second method is how
        // this file came to hold two scans of the same process table — see
        // SessionPresence.KnownAttachClient for what that cost.
        public static HashSet<string>? AttachedJobIds(bool fresh = false)
        {
            var now = Environment.TickCount64;

            lock (Gate)
            {
                if (!fresh && _attachedStamp != 0 && now - _attachedStamp < CacheMs)
                {
                    return _attached;
                }
            }

            HashSet<string>? found = null;

            if (TryRun("/bin/ps", out var listing, "-eo", "args="))
            {
                found = new HashSet<string>(StringComparer.Ordinal);

                foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var words = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length < 3) continue;
                    if (Path.GetFileName(words[0]) is not ("claude" or "claude.exe")) continue;
                    if (words[1] != "attach") continue;

                    found.Add(words[2]);
                }
            }

            lock (Gate)
            {
                // A failed read does not clear a good answer for the rest of the
                // cache window, the same way BackgroundJobs' listing does not:
                // the process table being briefly unreadable is not evidence that
                // anybody detached.
                if (found is not null)
                {
                    _attached = found;
                    _attachedStamp = now;
                }

                return _attached;
            }
        }

        private static HashSet<string>? _attached;
        private static long _attachedStamp;

        // The tmux pane already *showing* this session's conversation, if there is
        // one — step zero of the click ladder, ahead of everything that creates a
        // window.
        //
        // It exists because of the one thing several rounds assumed was
        // undiscoverable. The socket, file, argv and environment proofs all stand:
        // a session's rv socket has exactly one external peer, `claude daemon run`,
        // so the topology is a star and the viewer mapping is hub-internal, and the
        // interactive client holds no transcript file open. What none of that
        // covered is that the TUI *publishes* the conversation title to the
        // terminal, and tmux keeps it in `#{pane_title}`. Found in the reporter's
        // own screenshot: the answer had been on screen the whole time.
        //
        // **The candidate universe is panes on servers somebody is attached to,
        // and that is the load-bearing part.** A title is per conversation rather
        // than per session — every member of an agent team inherits the team
        // session's title, and a teammate's own TUI titles itself with it — so
        // matching on the title alone finds perfect impostors. Three rounds went
        // into trying to tell them apart. They are all on detached
        // `claude-swarm-<pid>` sockets, which nobody's eyes are on, so the honest
        // answer was that they were never viewers at all. Measured: four panes with
        // one identical title, and the visibility filter leaves exactly the one the
        // user was reading.
        //
        // Excluded from coverage: lists tmux servers, their clients and their
        // panes, and reads pane processes. Every rule it applies is pure and
        // covered per clause — SessionPresence.TitleSaysViewing,
        // LooksLikeClaudeBinary, ClaimStillHolds and ViewerAmong, the last of which
        // owns the visibility filter itself.
        [ExcludeFromCodeCoverage]
        public static (SessionPresence.ViewerVerdict Verdict, SessionPresence.ViewerPane? Found)
            ViewingPane(string? sessionTitle, IReadOnlyDictionary<string, string>? paneClaimsByOthers)
        {
            var none = (SessionPresence.ViewerVerdict.NoneFound, (SessionPresence.ViewerPane?)null);

            if (!OperatingSystem.IsMacOS()) return none;
            if (string.IsNullOrEmpty(sessionTitle)) return none;

            var tmux = ResolveTmux();
            if (tmux is null) return none;

            var matches = new List<SessionPresence.ViewerPane>();
            var withClients = new HashSet<string>(StringComparer.Ordinal);
            var watched = new HashSet<(string Socket, string Window)>();

            foreach (var socket in KnownSockets())
            {
                // Asked first, and of every server, because the answer decides
                // whether that server's panes are in the universe at all. A
                // detached server is a screen nobody is looking at.
                var clients = AttachedClients(tmux, socket);
                if (clients.Count == 0) continue;

                withClients.Add(socket);

                // Every attached client's window, not one of them: the user could
                // be sitting at any of them, and a window missing from this set is
                // a pane the scan will call "elsewhere" and try to move them to
                // while they are already looking at it.
                foreach (var client in clients)
                {
                    if (CurrentWindowOf(tmux, socket, client.Session) is { Length: > 0 } window)
                    {
                        watched.Add((socket, window));
                    }
                }

                CollectMatches(tmux, socket, sessionTitle, paneClaimsByOthers, matches);
            }

            return SessionPresence.ViewerAmong(matches, withClients, watched);
        }

        // Every tmux server this machine might have, as socket paths — with the
        // empty string standing for the default one, the way TerminalScripts.TmuxArgs
        // already reads it.
        //
        // The directory rather than the status files, because the socket that
        // matters most is the one a *person* attached to, and nothing in this app
        // records that. tmux keeps its sockets in one place per user and names them
        // after the server; a stale socket for a dead server simply answers no
        // clients and drops out one line later.
        // Excluded from coverage: reads the tmux socket directory.
        [ExcludeFromCodeCoverage]
        private static IEnumerable<string> KnownSockets()
        {
            // The default server first, and unconditionally: it is where a person
            // who has not thought about sockets ends up, and it is named by no
            // path at all.
            yield return "";

            var dir = Environment.GetEnvironmentVariable("TMUX_TMPDIR") is { Length: > 0 } custom
                ? custom
                : "/tmp/tmux-" + GetUid();

            string[] entries;
            try
            {
                entries = System.IO.Directory.GetFiles(dir);
            }
            catch
            {
                // No directory, or no permission to read it. One server is still
                // better than none, and the default has already been yielded.
                yield break;
            }

            foreach (var entry in entries)
            {
                // "default" is the same server the empty string already named, and
                // asking twice would double every match on it.
                if (Path.GetFileName(entry) == "default") continue;

                yield return entry;
            }
        }

        [ExcludeFromCodeCoverage]
        private static string GetUid()
        {
            // The socket directory is named for the *real* uid, which is what
            // getuid reports. Read through a subprocess rather than a p/invoke to
            // keep this file's OS surface to the two binaries it already runs.
            return TryRun("/usr/bin/id", out var uid, "-u") ? uid.Trim() : "0";
        }

        // Every pane on one server whose title and process say it is showing this
        // session, appended to matches.
        // Excluded from coverage: lists live tmux panes and reads their processes.
        [ExcludeFromCodeCoverage]
        private static void CollectMatches(
            string tmux, string socket, string sessionTitle,
            IReadOnlyDictionary<string, string>? paneClaimsByOthers,
            List<SessionPresence.ViewerPane> matches)
        {
            // pane_title last, and split with a limit: a conversation title
            // contains spaces, and every field before it does not.
            if (!TryRun(tmux, out var listing, TerminalScripts.TmuxArgs(socket,
                    "list-panes", "-a", "-F",
                    "#{pane_id} #{pane_pid} #{pane_active} #{session_name}:#{window_index} #{pane_title}")))
            {
                return;
            }

            foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(' ', 5, StringSplitOptions.None);
                if (parts.Length < 5) continue;

                if (!SessionPresence.TitleSaysViewing(parts[4], sessionTitle)) continue;

                // The belt, paid for only by a pane whose title already matched.
                // Asked of the pane's own process and its children, because an
                // interactive session runs as a child of the pane's shell while a
                // team member is the pane's process itself.
                if (!int.TryParse(parts[1], out var panePid)) continue;
                if (!RunsClaude(panePid)) continue;

                // A claim only counts while it is still evidence about who is
                // *reading* the pane, which the pane's own current title is what
                // settles — see SessionPresence.ClaimStillHolds.
                var claimed =
                    paneClaimsByOthers is not null
                    && paneClaimsByOthers.TryGetValue(parts[0], out var claimantTitle)
                    && SessionPresence.ClaimStillHolds(claimantTitle, parts[4]);

                matches.Add(new SessionPresence.ViewerPane(
                    Socket: socket,
                    Pane: parts[0],
                    Window: parts[3],
                    ActiveInItsWindow: parts[2] == "1",
                    ClaimedByAnother: claimed));
            }
        }

        // Whether this pane is running Claude Code — as its own process, or as a
        // child of the shell it started.
        // Excluded from coverage: reads the live process table.
        [ExcludeFromCodeCoverage]
        private static bool RunsClaude(int panePid)
        {
            if (TryRun("/bin/ps", out var own, "-p", panePid.ToString(), "-o", "args=")
                && SessionPresence.LooksLikeClaudeBinary(FirstWord(own)))
            {
                return true;
            }

            // One level down is enough: a pane runs a shell, and the shell runs
            // the session. Walking further would start counting a session's own
            // subprocesses, which are not what is being displayed.
            if (!TryRun("/bin/ps", out var all, "-eo", "ppid=,args=")) return false;

            foreach (var line in all.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                var split = trimmed.IndexOf(' ');
                if (split <= 0) continue;
                if (!int.TryParse(trimmed[..split], out var ppid) || ppid != panePid) continue;

                if (SessionPresence.LooksLikeClaudeBinary(FirstWord(trimmed[(split + 1)..])))
                {
                    return true;
                }
            }

            return false;
        }

        private static string? FirstWord(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) return null;

            var space = trimmed.IndexOf(' ');
            return space < 0 ? trimmed : trimmed[..space];
        }

        // Every client attached to this server, and the window each one is
        // showing. Empty list means nothing is attached, which is the answer that
        // takes this server's panes out of the viewer universe entirely.
        //
        // *Every* client, not the first one printed. Two clients are two windows a
        // person could be looking at, and picking one of them arbitrarily is the
        // "any client" mistake round eleven found in ResolveClient — which
        // survived here because this path picked its client by different code. On
        // the machine that produced this round, the two clients were on sessions
        // "0" and "1" and only the first was ever considered.
        // Excluded from coverage: runs tmux.
        [ExcludeFromCodeCoverage]
        private static List<TerminalScripts.TmuxClient> AttachedClients(string tmux, string socket)
        {
            if (!TryRun(tmux, out var listing, TerminalScripts.TmuxArgs(
                    socket, "list-clients", "-F", TerminalScripts.ClientListFormat)))
            {
                return new List<TerminalScripts.TmuxClient>();
            }

            return TerminalScripts.ParseClients(listing);
        }

        // The window a session is currently showing, as "<session>:<index>".
        //
        // Through TerminalScripts.PaneTargetForSession, which is not decoration:
        // a bare session name here is parsed as a window index and answers about
        // the wrong session without erroring. That comment has the measurements.
        // Excluded from coverage: runs tmux.
        [ExcludeFromCodeCoverage]
        private static string? CurrentWindowOf(string tmux, string socket, string session)
        {
            if (string.IsNullOrEmpty(session)) return null;

            return TryRun(tmux, out var found, TerminalScripts.TmuxArgs(
                    socket, "display-message", "-p",
                    "-t", TerminalScripts.PaneTargetForSession(session),
                    "#{session_name}:#{window_index}"))
                ? FirstLine(found)
                : null;
        }

        // Opens the one session an orb stands for, in a terminal.
        //
        // TryAdopt can only adopt a window that already exists, which leaves
        // the case this whole file is about only half answered: a background
        // session has no window *anywhere*, so its orb stays inert no matter
        // how good the lookup is.
        //
        // `claude attach <id>` is the answer Claude Code already provides, and
        // it is the reason this doesn't open the `claude agents` list instead.
        // The list was tried first and is the wrong destination twice over: it
        // lands on a roster rather than the session you clicked, and filtering
        // it with --cwd — which reads like "this session's directory" — means
        // "started *under* this path", so for an agent dispatched into a
        // worktree it matches nothing and the view opens on its empty state,
        // which is a new-task prompt. `attach` names the session directly, and
        // its own help is explicit that the session keeps running whether you
        // stay attached or drop out, so a click can never disturb the work.
        //
        // Driven through LaunchInTerminal, which is AppleScript's own "run this
        // command" verb for iTerm2 and Terminal.app and a script file for
        // everything else — see its comment, and CB-80, for why the file-open
        // path used to put a permission dialog in front of the user on every
        // single click rather than once.
        // Returns the tmux pane the session was attached into, when it went
        // into tmux at all — the caller focuses it through the same path it
        // uses for any other pane, rather than this file growing its own copy
        // of client resolution and window raising. Null means it either went
        // into a terminal window of its own or didn't happen.
        // Excluded from coverage: opens a terminal and runs `claude attach` in it.
        [ExcludeFromCodeCoverage]
        public static string? AttachSession(string sessionId, string cwd)
        {
            if (OperatingSystem.IsWindows()) return AttachSessionOnWindows(sessionId, cwd);
            if (!OperatingSystem.IsMacOS()) return null;
            if (string.IsNullOrEmpty(sessionId)) return null;

            var jobId = JobIdOf(sessionId);

            // Step one of the ladder: does a client for this session already
            // exist? One question, asked once, of one population.
            //
            // It used to be asked twice, of two populations that mostly agreed.
            // AttachedAlready ran its own `ps -eo args=` and saw every `claude
            // attach` process on the machine but knew nothing about where any of
            // them was; ExistingAttachPane ran `tmux list-panes -a` plus a `ps`
            // per pane and saw only those running as the direct process of a pane
            // on the default socket; and Launched saw only what this app itself
            // opened, this run. All three were consulted as whether-checks, and
            // the case where the first two disagree — an attach in a window of its
            // own — is exactly the one that decided whether a click opened a
            // duplicate. So *whether* is settled here, once, by
            // SessionPresence.KnownAttachClient over the single scan;
            // ExistingAttachPane is demoted to answering *where*, which no set of
            // argv strings can; and Launched answers only "into which app", which
            // is neither of those questions. It is also no longer paid for on every
            // click: the common case is a set lookup rather than a pane walk.
            //
            // A terminal may be attached to this session and still not be
            // adoptable, which is why existence rather than addressability is the
            // right question: Locate needs a tty to hand back, and a window opened
            // by `open` has none, so it declines one that is plainly there.
            if (SessionPresence.KnownAttachClient(AttachedJobIds(fresh: true), jobId))
            {
                // In a pane: hand it back so the caller selects it, rather than
                // only bringing the app forward. The two look the same when you
                // happen to be looking at that window already and nothing like
                // each other when you aren't, which is why a second click once
                // appeared to do nothing.
                //
                // Focused rather than only selected even when it sits in another of
                // the user's windows, and that is a judgment rather than an
                // oversight. Round 6a's rule is "never move the user", and
                // switching windows is a move — but it is a move to a window they
                // created and have now asked for by clicking, and the case 6a is
                // actually about costs nothing either way: select-window onto the
                // window that is already current changes nothing, so a pane beside
                // them is reached without anything being disturbed.
                if (ExistingAttachPane(jobId) is { Length: > 0 } existing) return existing;

                // Attached, but in a terminal window of its own. Nothing to
                // select, so raising its app is the whole of what can be done —
                // into the app this app opened it into if it remembers, which is
                // the only thing Launched is still consulted for.
                string? remembered;
                lock (Gate) remembered = Launched.GetValueOrDefault(jobId);

                ActivateApp(remembered ?? TerminalApp());
                return null;
            }

            // Nothing is attached, so one has to be made. Into tmux when there is
            // a tmux to go into — *beside* the user if they are in one, which is
            // PlaceInTmux's whole job now. Opening a bare terminal window for
            // someone who lives in tmux puts the session somewhere their usual
            // navigation cannot reach it.
            if (ClaudeCommand("attach", jobId) is { } attachCommand
                && PlaceInTmux(attachCommand, cwd) is { Length: > 0 } pane)
            {
                return pane;
            }

            // attach wants the *job* id, which is the first segment of the
            // session uuid and not the uuid itself — `claude logs` with a full
            // id answers "No job matching", with the short one it prints the
            // session's output. `claude agents --json` shows both side by side
            // ("id": "162e0b4b", "sessionId": "162e0b4b-3c45-..."), which is
            // where the relationship is confirmed rather than assumed.
            //
            // An absolute claude path, not a bare name resolved by a login
            // shell. That was the original approach and it silently didn't
            // work: `zsh -lc` skips .zshrc (non-interactive), which is where a
            // PATH addition for ~/.local/bin normally lives, so the command
            // died with "command not found" whenever the app was launched from
            // Finder rather than a terminal. See ClaudeBinary.
            if (ClaudeCommand("attach", jobId) is not { } ownWindowCommand) return null;

            var app = TerminalApp();
            lock (Gate) Launched[jobId] = app;

            // The cd matters even though attach names the session outright:
            // Ctrl+Z drops you back to a shell in this window, and the useful
            // place to land is the directory whose orb you clicked.
            LaunchInTerminal(app, cwd, "exec " + ownWindowCommand);

            // The window takes a moment to appear and the cache would
            // otherwise keep saying "nothing here" for its full window,
            // leaving the next click to open a second one.
            Forget(cwd);
            return null;
        }

        // Windows has no tmux destination to reuse, so a background or
        // terminal-less session needs a visible terminal of its own. Windows
        // Terminal is preferred because it gives the attach an ordinary tab;
        // cmd.exe is the documented fallback on installations without wt.
        // Both pass the CLI path and arguments separately, never through a
        // shell string, so a directory or CLI path containing spaces remains
        // one argument.
        [ExcludeFromCodeCoverage]
        private static string? AttachSessionOnWindows(string sessionId, string cwd)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;

            var claude = ClaudeBinary.Path;
            if (string.IsNullOrEmpty(claude)) return null;

            var directory = Directory.Exists(cwd) ? cwd : Environment.CurrentDirectory;

            try
            {
                var wt = WindowsAttachStartInfo(claude, directory, sessionId, useWindowsTerminal: true)!;
                Process.Start(wt);
                return null;
            }
            catch
            {
                try
                {
                    var cmd = WindowsAttachStartInfo(claude, directory, sessionId, useWindowsTerminal: false)!;
                    Process.Start(cmd);
                    return null;
                }
                catch
                {
                    return null;
                }
            }
        }

        // The launch description is pure so the command can be checked on any
        // runner. Starting it remains in AttachSessionOnWindows, where a real
        // Windows Terminal is the only honest integration test.
        //
        // The wt/cmd argument layout itself moved to
        // NewChatCommand.GeneralWindowsStartInfo for CB-168, which CB-168's
        // own new-chat launch also needs — this is now a one-line forwarder
        // supplying "attach <jobId>" as the extra arguments, and produces
        // exactly the same ProcessStartInfo it did before the move (see
        // WindowsAttachLaunchTests, unchanged and still green).
        internal static ProcessStartInfo? WindowsAttachStartInfo(
            string? claude, string cwd, string? sessionId, bool useWindowsTerminal)
        {
            if (string.IsNullOrEmpty(claude) || string.IsNullOrEmpty(sessionId)) return null;

            var jobId = JobIdOf(sessionId);
            return NewChatCommand.GeneralWindowsStartInfo(
                claude, cwd, new[] { "attach", jobId }, useWindowsTerminal);
        }

        // Opens a terminal attached to an existing tmux server, for a session
        // whose pane is alive in one nothing is attached to.
        //
        // The other half of "a click on an orb must produce a visible result".
        // AttachSession above answers the session that has no terminal anywhere;
        // this answers the one that has a *pane* and no screen — an agent-team
        // member in a detached `claude-swarm-<pid>` socket, which is what the
        // user was clicking when they reported orbs that "do nothing". Six
        // things went right on that path and the last one had nowhere to go.
        //
        // Here rather than in TerminalFocuser because opening a terminal on a
        // command is this file's mechanism, and a second copy of it there would
        // be a second thing to keep right about which terminal app the user has
        // and how to launch one. The command itself is built by
        // TerminalScripts.TmuxAttachCommand, which is pure and tested; what is
        // left here is the launch, through the same LaunchInTerminal every other
        // call site in this file uses — see its comment and CB-80 for why that
        // is no longer a script file for iTerm2 or Terminal.app.
        //
        // Deliberately no "already open?" guard, unlike AttachSession, and the
        // asymmetry is the point: the moment a client attaches to that server,
        // the ordinary focus path finds it and raises its window, so the second
        // click never reaches this. AttachSession needs a guard because a window
        // running `claude attach` is not discoverable that way.
        //
        // Excluded from coverage: opens a terminal and runs the attach in it.
        [ExcludeFromCodeCoverage]
        public static string? AttachTmuxSocket(
            string tmuxBinary, string? socket, string pane, string cwd)
        {
            if (!OperatingSystem.IsMacOS()) return null;
            if (string.IsNullOrEmpty(tmuxBinary)) return null;
            if (string.IsNullOrEmpty(pane)) return null;

            // Which session that pane is in, because a server can hold more than
            // one and an untargeted attach picks the busiest — see
            // TerminalScripts.TmuxAttachScript for what that cost. Asked here
            // rather than threaded down from FocusTmux, which already knows the
            // answer, because this method is one tmux query away from opening a
            // terminal and the alternative is a parameter carried through two
            // layers that exist for other reasons.
            //
            // Refused rather than guessed when it cannot be answered. An attach
            // with no target is not a lesser version of this; it is an attach to
            // whatever happened to be busy, which on the server this matters for
            // is the app's own relay.
            if (!TryRun(tmuxBinary, out var found, TerminalScripts.TmuxArgs(
                    socket, "display-message", "-p", "-t", pane, "#{session_name}")))
            {
                return null;
            }

            if (FirstLine(found) is not { Length: > 0 } session) return null;

            // Beside the user first, which is the arrival every other answer on
            // this ladder already gives. Owner chose it explicitly at the
            // round-thirteen fork — "Splits in beside you showing what THAT agent
            // is doing" — and it is the same rule round 6a settled for the
            // background arm: the content comes to the user, nothing yanks. The
            // command is targeted exactly as round 14 made it, so the pane lands
            // in the clicked session and not in whichever the server used last.
            //
            // Returns the pane so the caller focuses it through the ordinary tail,
            // the same contract AttachSession has and for the same reason.
            var command = TerminalScripts.TmuxAttachCommand(tmuxBinary, socket, session);

            if (PlaceInTmux(command, cwd) is { Length: > 0 } beside) return beside;

            // No tmux of the user's own to split into. A terminal window of its
            // own is what is left, and it keeps the shape it already had.
            //
            // Null rather than a pane: this went into a window of its own, so
            // there is nothing for the caller to select. Same contract as
            // AttachSession's own terminal fallback.
            LaunchInTerminal(TerminalApp(), cwd, command);
            return null;
        }

        // Runs the attach in a new window of whichever tmux session already has
        // a client, and hands back its pane. Nothing is selected or raised here
        // — returning the pane lets the caller reuse the focus path every other
        // pane goes through, which already knows how to find the client, pick
        // the window and bring its app forward.
        // Excluded from coverage: creates a real tmux window and sends keys to it.
        // Moved to TerminalLauncher.PlaceInTmux for CB-168, verbatim — see
        // that file for the mechanism, and the history of this file (round 6a
        // and round eleven) for why the client and window are chosen the way
        // they are. Forwarder kept here so every call site below is untouched.
        [ExcludeFromCodeCoverage]
        private static string? PlaceInTmux(string command, string cwd) =>
            TerminalLauncher.PlaceInTmux(command, cwd);

        // The first non-blank line of a tmux answer, trimmed. Both lookups above
        // ask for one field and can be handed several lines — `list-clients`
        // genuinely lists, and `display-message -p` appends a newline — and both
        // want the same thing from that.
        private static string? FirstLine(string output)
        {
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0) return trimmed;
            }

            return null;
        }

        // Where tmux actually is. The app can't count on PATH — launched from
        // Finder it gets the bare system one — and unlike a session's status
        // file there's no recorded location to start from here, so this is the
        // same candidate list TerminalFocuser falls back to.
        //
        // Moved to TerminalLauncher.ResolveTmux for CB-168 (QA caught the two
        // copies going byte-identical rather than being the same method — the
        // first fix to one would have silently left the other stale). Kept as
        // a one-line forwarder so ViewingPane and AttachTmuxSocket below don't
        // change.
        [ExcludeFromCodeCoverage]
        private static string? ResolveTmux() => TerminalLauncher.ResolveTmux();

        // Whichever terminal is already running, so the viewer opens where the
        // user's other terminals are rather than waking a second app. Ordered
        // by specificity: Terminal.app is last because it's the fallback that
        // always exists, not a preference.
        // Excluded from coverage: reads the frontmost terminal application from
        // the OS.
        //
        // Moved to TerminalLauncher.TerminalApp for CB-168 — the new-chat
        // feature needs the same "which terminal is running" answer and this
        // is a one-line forwarder rather than a second copy of the `ps` scan,
        // so both features stay in sync automatically.
        [ExcludeFromCodeCoverage]
        private static string TerminalApp() => TerminalLauncher.TerminalApp();

        // The short form `claude attach` and `claude logs` expect. Split rather
        // than a fixed eight characters so an id that isn't a uuid degrades to
        // itself instead of being sliced into nonsense.
        // internal, not private: the short form the daemon uses, and the only
        // thing in this file that decides anything without asking the OS.
        internal static string JobIdOf(string sessionId)
        {
            var dash = sessionId.IndexOf('-');
            return dash > 0 ? sessionId[..dash] : sessionId;
        }

        // The tmux pane already running `claude attach <id>`, if there is one.
        //
        // Found through each pane's own process rather than anything this app
        // remembered, so it still works after a restart, and finds a pane the
        // user opened by hand just as well as one opened from an orb.
        // Excluded from coverage: lists live tmux panes.
        [ExcludeFromCodeCoverage]
        private static string? ExistingAttachPane(string jobId)
        {
            var tmux = ResolveTmux();
            if (tmux is null) return null;

            if (!TryRun(tmux, out var panes, "list-panes", "-a", "-F", "#{pane_id} #{pane_pid}"))
            {
                return null;
            }

            foreach (var line in panes.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                if (!int.TryParse(parts[1], out var pid)) continue;

                if (!TryRun("/bin/ps", out var args, "-p", pid.ToString(), "-o", "args=")) continue;

                var words = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length < 3) continue;
                if (Path.GetFileName(words[0]) is not ("claude" or "claude.exe")) continue;
                if (words[1] != "attach") continue;

                if (SessionPresence.SameJobId(jobId, words[2])) return parts[0];
            }

            return null;
        }

        private static void Forget(string cwd)
        {
            lock (Gate) Cache.Remove(cwd.TrimEnd('/'));
        }

        // Runs `command` in a brand-new window of `app`, on the directory
        // `cwd` names. The one place all three "open a terminal of its own"
        // call sites in this file now go through, so there is one mechanism to
        // get right about how a window gets opened rather than three inline
        // copies of it.
        //
        // CB-80: every one of those three used to write a shell script to a
        // fixed-name temp file and hand it to `/usr/bin/open -a <app> <script>`
        // — asking the terminal to *open an executable file*, which is exactly
        // what iTerm2's Automation gate exists to ask permission for. It asked
        // on every single click rather than once, because the file this app
        // writes gets a fresh mtime on every launch even when its name and
        // contents don't change, and iTerm2 does not remember a decision made
        // about a file that no longer matches what it last saw.
        //
        // TerminalScripts.RunScriptFor answers with an AppleScript that asks
        // the terminal to *run a command* instead — iTerm2's `create window
        // with default profile command` and Terminal.app's `do script`, proven
        // by CB-79's probes not to raise that dialog. Run synchronously through
        // TryRun rather than fire-and-forget, so the return value is real
        // confirmation the window was created rather than merely that
        // osascript launched — the same distinction TerminalFocuser.RunOsaScript
        // draws for the click-focus path, drawn here because this path already
        // waits on other subprocesses and a `Process.Start` that never checked
        // its own success was the original shape of this method.
        //
        // Ghostty and WezTerm have no comparable scripting surface and neither
        // warns today, so RunScriptFor answers null for them and this falls
        // back to the old script-file mechanism — but with a name unique to
        // this call (a GUID) rather than the fixed name each of the three call
        // sites used to write to. That fixed name was a real race: two clicks
        // moments apart could have the second write land on disk before the
        // first `open` had read the file, so the window that opened first ran
        // whatever the second click asked for. attach-tmux-socket.sh's own
        // comment reasoned about exactly this for one caller — kept here as the
        // record of why a GUID replaces it for all three at once, rather than
        // three separate fixed names.
        // Moved to TerminalLauncher.LaunchInTerminal for CB-168, verbatim —
        // see that file for the mechanism and CB-80 for why it exists in this
        // shape. This stays as a one-line forwarder so every call site below
        // is untouched.
        [ExcludeFromCodeCoverage]
        private static bool LaunchInTerminal(string app, string? cwd, string command) =>
            TerminalLauncher.LaunchInTerminal(app, cwd, command);

        // `open -a` on a running app just brings it forward — the same trick
        // TerminalFocuser.ActivateApp uses, kept here rather than shared
        // because that one is private to a class this file must not depend on.
        // Excluded from coverage: activates a real application through osascript.
        [ExcludeFromCodeCoverage]
        private static void ActivateApp(string appBundlePath)
        {
            try
            {
                var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                psi.ArgumentList.Add("-a");
                psi.ArgumentList.Add(appBundlePath);
                Process.Start(psi);
            }
            catch { }
        }

        // Processes running `claude agents`. Matched on the argument rather
        // than the executable path, which is a version-stamped location that
        // changes under you (~/.local/share/claude/versions/<n>).
        // Excluded from coverage: walks the live process table.
        [ExcludeFromCodeCoverage]
        private static IEnumerable<int> ViewerPids()
        {
            if (!TryRun("/bin/ps", out var listing, "-eo", "pid=,args=")) yield break;

            foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                var split = trimmed.IndexOf(' ');
                if (split <= 0) continue;

                if (!int.TryParse(trimmed[..split], out var pid)) continue;

                var command = trimmed[(split + 1)..].Trim();
                var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                // "claude agents", and nothing that merely mentions it — a
                // shell running a script with that name in its command line
                // shouldn't count.
                if (words.Length < 2 || words[1] != "agents") continue;
                if (Path.GetFileName(words[0]) is not ("claude" or "claude.exe")) continue;

                yield return pid;
            }
        }

        // No libproc equivalent worth the struct marshalling here: lsof is
        // asked for one descriptor of one process, and only for the handful of
        // viewers found above.
        // Excluded from coverage: runs lsof against a live pid.
        [ExcludeFromCodeCoverage]
        private static string CwdOf(int pid)
        {
            if (!TryRun("/usr/sbin/lsof", out var listing,
                    "-a", "-p", pid.ToString(), "-d", "cwd", "-Fn"))
            {
                return "";
            }

            foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith('n')) return line[1..].TrimEnd('/');
            }

            return "";
        }

        // Excluded from coverage: runs ps against a live pid.
        [ExcludeFromCodeCoverage]
        private static string TtyOf(int pid)
        {
            if (!TryRun("/bin/ps", out var tty, "-o", "tty=", "-p", pid.ToString())) return "";

            tty = tty.Trim();
            return tty is "" or "??" ? "" : tty;
        }

        // Excluded from coverage: starts a subprocess and reads its output.
        [ExcludeFromCodeCoverage]
        private static bool TryRun(string exe, out string stdout, params string[] args)
        {
            stdout = "";

            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var arg in TerminalScripts.TmuxProcessArgs(exe, args)) psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi);
                if (process is null) return false;

                stdout = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(3000)) return false;

                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
