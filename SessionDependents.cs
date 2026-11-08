using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;

namespace Orbweaver
{
    // What is running *underneath* a session's pid, asked before anything is
    // signalled to it.
    //
    // SessionTerminator's header says why signalling the session's own pid is
    // safe: "Not TermPid, which is the terminal the user is typing in; not the
    // daemon, which hosts every other background job on the machine. One worker
    // hosts exactly one session." Both exclusions are true of every ordinary
    // session and both are false of one shape — the husk a backgrounded turn
    // leaves behind, where the interactive session that forked the turn is
    // simultaneously the window the user is reading and the ancestor of the
    // daemon that now owns the work.
    //
    // Measured on a real Mac (macOS 27.0, Claude Buddy 0.4.2-beta), read off
    // `ps`, with the husk at the bottom rather than the top:
    //
    //   claude --session-id … --fork-session --resume …   <- the live job
    //    └─ claude --bg-pty-host
    //        └─ claude daemon run
    //            └─ claude                                <- the husk, in a tmux pane
    //
    // Ending the husk on that machine killed the husk alone: the daemon was
    // reparented to launchd and the job survived with its transcript intact. So
    // nothing was lost, and the gesture still destroyed the only window onto
    // live work, silently, from an orb presenting itself as stale. The Windows
    // arm is the serious half — `Kill(entireProcessTree: true)` against a pid
    // whose descendants are the daemon and every job under it, with
    // TerminateProcess semantics: no SessionEnd, no transcript flush. That arm
    // is **inferred from the code and this process tree and has not been
    // reproduced on Windows**; it is stated here rather than in a commit
    // message so the next person reading this file knows which half was
    // measured.
    //
    // Pure, and split from the read the same way SessionPresence is split from
    // the scan: Inspect takes a process table and answers about it, so every
    // arm of the rule has a case, and only Snapshot below touches the machine.
    internal static class SessionDependents
    {
        // One row of the process table: the three columns every platform can
        // answer with, and nothing this rule does not use.
        internal readonly record struct ProcessRow(int Pid, int ParentPid, string Command);

        // What was found below a pid.
        //
        // Two fields rather than a bool because they answer different halves of
        // the acceptance: whether to refuse at all, and what to *say*. A daemon
        // with no job under it is still a refusal — the daemon hosts whatever
        // starts next — but it is a different sentence from "this closes your
        // view of four running jobs", and the count is the part a user can act
        // on.
        internal readonly record struct Verdict(bool DaemonBelow, int JobsBelow);

        // "Nothing underneath", which is the answer for every ordinary session
        // and for every question this could not ask at all. Named, rather than
        // `default`, for the reason AgentTeam.None is: the failure direction is
        // load-bearing and should be readable at the call site.
        //
        // **A read that failed answers this**, and that direction is deliberate
        // and is the opposite of the one SessionPresence.HasAttachClient takes.
        // There, failing open leaves an orb bright. Here, failing closed would
        // mean an unreadable process table silently removed "End this session"
        // from every orb on the machine — an action the user has today, gone
        // with no explanation, because `ps` did not answer once. Wrong-empty
        // costs exactly what the app does today, which this ticket is narrowing
        // rather than widening; wrong-blocked breaks a working feature for
        // every session at once.
        internal static readonly Verdict Nothing = new(false, 0);

        // Whether this verdict means the pid must not be signalled.
        //
        // The daemon is the whole test, not the job count. A daemon below the
        // pid is what makes both of SessionTerminator's stated exclusions false
        // at once, and it is true whether or not a job happens to be running
        // inside it this second — a daemon with nothing in it right now is
        // still the thing the next background turn will be handed to.
        internal static bool BlocksTermination(Verdict verdict) => verdict.DaemonBelow;

        // --- what a command line is -------------------------------------------

        // Whether this command line is running the Claude Code binary.
        //
        // SessionPresence.LooksLikeClaudeBinary is the authority on the two
        // shapes observed live — plain `claude`, and the versioned install path
        // a team member runs as — and it is asked here rather than re-derived,
        // because a second differently-wrong copy of "what counts as the Claude
        // binary" is exactly the drift that file's own comments keep arguing
        // against.
        //
        // **Of argv[0] only, which a test caught being wrong.** Asking it of
        // every token reads `/bin/zsh -c cd ~/claude && ls` as a Claude process,
        // because that rule is a filename test and the user's directory is
        // called claude. It is written for argv[0] and it is right there; a
        // shell's arguments are not argv[0] and are full of paths.
        //
        // The script arm is the exception, and it is the Windows-inferred one. A
        // Windows install can run the CLI through Node rather than through a
        // shim, in which case argv[0] is node and the token naming Claude is the
        // script path — `…\@anthropic-ai\claude-code\cli.js`. Matched as "a .js
        // path with claude in it", which a directory of that name cannot satisfy
        // because a directory is not a .js file. Not reproduced on Windows; it
        // is here because leaving it out would make the guard silently do
        // nothing on precisely the platform where not guarding costs the most.
        internal static bool NamesTheClaudeBinary(string? command)
        {
            if (string.IsNullOrEmpty(command)) return false;

            var tokens = Tokens(command);
            if (tokens.Length == 0) return false;

            if (SessionPresence.LooksLikeClaudeBinary(tokens[0])) return true;

            foreach (var token in tokens)
            {
                if (token.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                    && token.Contains("claude", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // `claude daemon run` — the process that hosts every background job for
        // one account.
        //
        // Adjacency rather than "mentions daemon anywhere", and the difference
        // is the whole false-positive surface of this rule. A session working
        // in a directory called `daemon`, or one whose title mentions it, puts
        // the word on its own command line by way of `--resume` and `--cwd`
        // arguments; the two tokens side by side are the subcommand and nothing
        // else. Getting this wrong in that direction hides "End this session"
        // from an ordinary orb, which is a working feature disappearing for no
        // reason the user can see.
        internal static bool IsDaemon(string? command) =>
            NamesTheClaudeBinary(command) && HasAdjacentTokens(command!, "daemon", "run");

        // The pty host the daemon puts between itself and a job's worker. Not a
        // job, and counting it as one would report every machine as having one
        // more job than it has.
        internal static bool IsPtyHost(string? command) =>
            NamesTheClaudeBinary(command) && HasToken(command!, "--bg-pty-host");

        // A background job's own worker, as opposed to everything else that can
        // be a Claude process under a daemon.
        //
        // Subagents are excluded by their team flags, which is the one
        // exclusion that changes the number a user reads. A job that has
        // spawned an agent team has one conversation in it and four processes
        // under it, and "ending this closes your view of 5 background jobs"
        // would be wrong in the direction that makes the warning untrustworthy
        // — the same flags AgentTeam already reads off a process, asked here
        // for the opposite purpose.
        internal static bool IsJobWorker(string? command)
        {
            if (!NamesTheClaudeBinary(command)) return false;
            if (IsDaemon(command) || IsPtyHost(command)) return false;

            return !HasToken(command!, "--parent-session-id")
                && !HasToken(command!, "--agent-name")
                && !HasToken(command!, "--agent-id");
        }

        // --- the tree ---------------------------------------------------------

        // What is below this pid.
        //
        // Two walks rather than one, and the second starts at the daemon rather
        // than at the pid. Counting job-shaped processes anywhere under the
        // session would count the session's own subprocesses — an ordinary
        // interactive session with a `claude` child is an ordinary thing — and
        // the number this produces is shown to a person. A job is a job because
        // the daemon is hosting it, so the daemon's subtree is what is counted.
        //
        // Both walks carry a visited set, which is not defensive decoration: a
        // process table is sampled rather than snapshotted atomically, so a pid
        // recycled between two rows can name itself or its own ancestor as a
        // parent, and an unguarded walk over that never returns.
        internal static Verdict Inspect(IReadOnlyList<ProcessRow> table, int pid)
        {
            if (pid <= 0 || table.Count == 0) return Nothing;

            var children = new Dictionary<int, List<ProcessRow>>();

            foreach (var row in table)
            {
                // A row that parents itself is the degenerate form of the cycle
                // above and is dropped rather than walked: keeping it would put
                // the pid in its own descendant set, where a session running
                // `claude daemon run` in its own process would block itself.
                if (row.Pid <= 0 || row.ParentPid == row.Pid) continue;

                if (!children.TryGetValue(row.ParentPid, out var list))
                {
                    list = new List<ProcessRow>();
                    children[row.ParentPid] = list;
                }

                list.Add(row);
            }

            var daemons = new List<int>();

            foreach (var row in Descendants(children, new[] { pid }))
            {
                if (IsDaemon(row.Command)) daemons.Add(row.Pid);
            }

            if (daemons.Count == 0) return Nothing;

            var jobs = 0;

            foreach (var row in Descendants(children, daemons))
            {
                if (IsJobWorker(row.Command)) jobs++;
            }

            return new Verdict(true, jobs);
        }

        // Every process below the given roots, each visited once. The roots
        // themselves are not yielded — the question is always about what is
        // *under* a pid, never about the pid itself.
        private static List<ProcessRow> Descendants(
            Dictionary<int, List<ProcessRow>> children, IReadOnlyList<int> roots)
        {
            var found = new List<ProcessRow>();
            var seen = new HashSet<int>(roots);
            var queue = new Queue<int>(roots);

            while (queue.Count > 0)
            {
                if (!children.TryGetValue(queue.Dequeue(), out var kids)) continue;

                foreach (var kid in kids)
                {
                    if (!seen.Add(kid.Pid)) continue;

                    found.Add(kid);
                    queue.Enqueue(kid.Pid);
                }
            }

            return found;
        }

        // --- what the menu says ------------------------------------------------

        // The header "End this session" wears once something is underneath it.
        //
        // The count is in the sentence rather than in a tooltip because the
        // tooltip is what a user reads *after* deciding to look, and this is
        // the row they are aiming at. Three shapes, because "0 jobs" is a real
        // answer — a daemon between turns with nothing in it — and phrasing it
        // as "0 background jobs" would read as "this is safe", which is the one
        // thing it must not say.
        internal static string Explain(Verdict verdict) => verdict switch
        {
            { DaemonBelow: false } => "End this session",
            { JobsBelow: 0 } => "Can't end this: it is hosting the background daemon",
            { JobsBelow: 1 } => "Can't end this: it is your view of 1 background job",
            var v => $"Can't end this: it is your view of {v.JobsBelow} background jobs"
        };

        // And why, at length, for the row that is now disabled.
        //
        // Said as what the user would lose rather than as what the app refuses,
        // because the orb this appears on is one that presents itself as stale:
        // "it looks finished and it is the window onto work that is not" is the
        // fact the person is missing, and the refusal follows from it.
        internal static string ExplainTip(Verdict verdict) =>
            verdict.DaemonBelow
                ? "This session handed its turn to a background job and is still the window you are "
                  + "reading it in. Ending it would close that window while the work carries on, and "
                  + "on Windows would stop the background daemon and every job under it. Close the "
                  + "terminal, or end the job from its own orb."
                : "Stops the process behind this session. This cannot be undone.";

        // While the answer is being read (CB-228).
        //
        // The read is a full process-table walk — a WMI query on Windows,
        // 218-273 ms measured on the Windows PC — so it runs off the UI thread
        // and the menu opens before it returns. Until it does, the row is
        // disabled and says so. Not enabled-and-plain: the read's whole purpose
        // is to find the one shape where the row must refuse, and a row that
        // turned from "End this session" into a refusal a quarter of a second
        // after the menu appeared would move under a cursor already travelling
        // towards it. Starting refused and becoming available is the order in
        // which a late answer costs nothing. EndSession asks again in any case,
        // so this wording is a promise about the row, not the guard.
        internal const string CheckingHeader = "Checking for background jobs…";

        internal const string CheckingTip =
            "Looking at what is running under this session before offering to end it.";

        // And when the read failed or did not come back (CB-228). Disabled,
        // and saying so: the read exists to find the one shape where ending
        // the session must be refused, so not knowing is not permission. This
        // is a different case from the one Nothing's comment argues for — that
        // is SessionDependents.Of finding the table unreadable, which it
        // answers as Nothing deliberately and which stays as it is. This is the
        // read itself throwing or hanging past ReadTimeout, which Of's own
        // catch should make impossible and which, if it happens anyway, means
        // something is wrong that the row should not paper over.
        internal const string UnknownHeader = "Couldn't check for background jobs";

        internal const string UnknownTip =
            $"{Brand.DisplayName} could not read what is running under this session, so it is not "
            + "offering to end it. Close this menu and open it again to retry.";

        // How long the row waits for an answer before saying it has none. Twice
        // `ps`'s own five-second timeout in TryRun, and forty times the slowest
        // WMI read measured on the Windows PC.
        internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

        // Which of several overlapping reads may still write to the row.
        //
        // A menu can be closed and reopened inside one read's quarter-second,
        // and the reads do not finish in the order they started: the second
        // open's read can hit SessionDependents' two-second cache while the
        // first is still walking WMI. The row must show the newest answer
        // rather than the last one to arrive, so each read takes a ticket and
        // only the newest ticket is honoured. Closing the menu retires every
        // ticket outstanding, so a read finishing after the menu is gone writes
        // nothing.
        //
        // UI-thread only, like the row it guards, so a plain counter is enough.
        internal sealed class GuardRequests
        {
            private int _generation;

            internal int Begin() => ++_generation;

            internal void Retire() => _generation++;

            internal bool IsCurrent(int ticket) => ticket == _generation;
        }

        // --- reading the machine ------------------------------------------------

        // A live process table's shape changes constantly, so this is a cache
        // with a very short fuse rather than a memo: two seconds is long enough
        // that opening the menu and then clicking the row share one read, and
        // short enough that it cannot describe a machine the user has since
        // changed. Deliberately much shorter than AgentTeam's minute — that one
        // caches a fact that cannot change for a live pid, and this one caches
        // a fact that can change every second.
        private const long CacheMs = 2_000;

        private static readonly object Gate = new();
        private static IReadOnlyList<ProcessRow>? _table;
        private static long _stamp;

        // Excluded from coverage: the cache is keyed on the wall clock and what
        // it wraps is a read of the live process table. The decision it carries
        // is Inspect above, which is pure and covered per arm.
        [ExcludeFromCodeCoverage]
        public static Verdict Of(int pid)
        {
            if (pid <= 0) return Nothing;

            var table = Table();
            return table is null ? Nothing : Inspect(table, pid);
        }

        // The cached table, for a caller that wants to walk it itself — the
        // Claude Desktop click path (CB-265) climbs ppid links through it.
        // Excluded from coverage for Table's own reason: it is the clock and a
        // live process read, and what is done with the rows is covered where
        // that happens.
        [ExcludeFromCodeCoverage]
        internal static IReadOnlyList<ProcessRow>? CurrentTable() => Table();

        // Excluded from coverage: the clock.
        [ExcludeFromCodeCoverage]
        private static IReadOnlyList<ProcessRow>? Table()
        {
            lock (Gate)
            {
                if (_table is not null && Environment.TickCount64 - _stamp < CacheMs) return _table;
            }

            var fresh = Snapshot();

            lock (Gate)
            {
                // Unlike BackgroundJobs.States, a failed read *does* clear the
                // previous answer. That cache decides whether an orb is drawn
                // and a stale answer keeps the screen still; this one decides
                // whether an irreversible action is refused, and a stale answer
                // would refuse on evidence about a machine that has moved on.
                _table = fresh;
                _stamp = Environment.TickCount64;
                return _table;
            }
        }

        // Excluded from coverage: runs `ps`, or queries WMI, against the live
        // machine. What is done with the answer is ParsePs and Inspect, both
        // pure and both covered.
        [ExcludeFromCodeCoverage]
        private static IReadOnlyList<ProcessRow>? Snapshot()
        {
            try
            {
                if (OperatingSystem.IsWindows()) return WindowsTable();

                return TryRun("/bin/ps", out var listing, "-eo", "pid=,ppid=,args=")
                    ? ParsePs(listing)
                    : null;
            }
            catch
            {
                // No `ps`, no WMI, or a table this app may not read. Answers
                // "nothing known", which Of turns into Nothing — see the note
                // on that field for why this fails open rather than closed.
                return null;
            }
        }

        // `ps -eo pid=,ppid=,args=` — three columns, the third of which holds
        // spaces and must not be split.
        //
        // The columns are right-aligned and padded, so the line is trimmed
        // before it is split and the split is bounded at three: an unbounded
        // split would hand back the first word of the command and the rest of
        // it would be unreachable, which would make every rule above answer
        // false for every process.
        internal static List<ProcessRow> ParsePs(string listing)
        {
            var rows = new List<ProcessRow>();

            foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(
                    (char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                if (!int.TryParse(parts[0], out var pid)) continue;
                if (!int.TryParse(parts[1], out var parent)) continue;

                rows.Add(new ProcessRow(pid, parent, parts[2]));
            }

            return rows;
        }

        // Excluded from coverage: a WMI query, for the reason
        // AgentTeam.WindowsArguments gives — System.Management reaches COM to
        // ask Win32_Process, which has no equivalent on the macOS runner and no
        // seam on the Windows one.
        //
        // **Not reproduced on Windows.** Win32_Process is the documented place
        // this triple lives and it is what AgentTeam already queries for a
        // command line, but the shape of a Claude Code process tree on Windows
        // has been read off this repository's code and a macOS `ps`, never off
        // a Windows machine. One known hazard is written down rather than
        // guessed at: ParentProcessId is not cleared when a parent exits, so a
        // recycled pid can appear to parent a process it never started. That
        // can only make this guard refuse a session it need not have, which is
        // the harmless direction, and Inspect's visited set is what keeps the
        // resulting cycle from hanging the walk.
        [ExcludeFromCodeCoverage]
        [SupportedOSPlatform("windows")]
        private static List<ProcessRow> WindowsTable()
        {
            var rows = new List<ProcessRow>();

            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, CommandLine FROM Win32_Process");

            // Bounded (CB-228), as `ps` already is by TryRun's five seconds.
            // The read is off the UI thread now and the row gives up on it
            // after ReadTimeout, but giving up does not cancel it — and WMI's
            // default timeout is infinite, so a wedged WMI service would leave
            // a pool thread blocked here for every menu open. This applies per
            // WMI call, which on a healthy box is the whole quarter-second.
            searcher.Options.Timeout = TimeSpan.FromSeconds(8);

            foreach (var row in searcher.Get())
            {
                using var process = (System.Management.ManagementObject)row;

                var pid = process["ProcessId"];
                var parent = process["ParentProcessId"];
                if (pid is null || parent is null) continue;

                rows.Add(new ProcessRow(
                    Convert.ToInt32(pid),
                    Convert.ToInt32(parent),
                    process["CommandLine"] as string ?? ""));
            }

            return rows;
        }

        // Excluded from coverage: starts a subprocess. Both pipes are drained
        // before waiting, for the reason BackgroundJobs.ReadOne states — a
        // blocking ReadToEnd makes the timeout unreachable, and `ps` on a busy
        // machine prints more than a pipe buffer holds.
        [ExcludeFromCodeCoverage]
        private static bool TryRun(string file, out string output, params string[] args)
        {
            output = "";

            try
            {
                var psi = new ProcessStartInfo(file)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                foreach (var arg in args) psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi);
                if (process is null) return false;

                var outTask = process.StandardOutput.ReadToEndAsync();
                var errTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(5000))
                {
                    try { ProcessTreeKill.Kill(process); } catch { }
                    return false;
                }

                output = outTask.GetAwaiter().GetResult();
                errTask.GetAwaiter().GetResult();

                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        // --- tokens ------------------------------------------------------------
        // A command line as `ps` prints it, split on whitespace. Quoting is not
        // honoured and does not need to be: every token this asks about is a
        // flag or a subcommand, none of which is ever quoted, and a quoted path
        // containing a space splits into fragments that match nothing.

        private static string[] Tokens(string command) =>
            command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        private static bool HasToken(string command, string token)
        {
            foreach (var candidate in Tokens(command))
            {
                if (string.Equals(candidate, token, StringComparison.Ordinal)) return true;

                // `--flag=value`, which is the other form every one of these can
                // take. Matched on the prefix plus the separator so `--agent-id`
                // does not match `--agent-id-thing`.
                if (candidate.StartsWith(token + "=", StringComparison.Ordinal)) return true;
            }

            return false;
        }

        private static bool HasAdjacentTokens(string command, string first, string second)
        {
            var tokens = Tokens(command);

            for (var i = 0; i + 1 < tokens.Length; i++)
            {
                if (string.Equals(tokens[i], first, StringComparison.Ordinal)
                    && string.Equals(tokens[i + 1], second, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
