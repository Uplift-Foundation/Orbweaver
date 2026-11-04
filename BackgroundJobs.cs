using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Orbweaver
{
    // What the daemon says a background session is *doing*, as opposed to
    // whether it is worth an orb at all.
    //
    // Five answers rather than a bool for the same reason SessionManager's
    // ScanVerdict is an enum: the two "no" answers below are not the same fact.
    // NotAJob is something known about the session — the listing was read and
    // this session is not in it. Unknown is something known about the *CLI* —
    // there was no listing to read. Only the first may change what an orb looks
    // like, and collapsing them is how a briefly unavailable `claude` would dim
    // fifteen orbs at once.
    internal enum JobPhase
    {
        // Read a listing, and this session was not on it: an interactive
        // session, a subagent, or a status file that outlived its session.
        NotAJob,

        // Mid-turn. Indistinguishable from Parked on disk, which is the whole
        // reason this enum exists.
        Working,

        // Between turns: the job's worker is alive and resumable, and nothing
        // is happening in it. The daemon's word for this is "blocked".
        Parked,

        // Finished for good. Its worker stays alive, so nothing about the
        // process says so — see the hygiene sweep in SessionManager.
        Done,

        // No listing. Fail-open: nothing downstream may act on this.
        Unknown
    }

    // What the daemon thinks of a background session, which its status file
    // can't tell you.
    //
    // A background agent's hook writes the same states every other session
    // writes — "idle" when it isn't mid-turn — so a job that has finished for
    // good is indistinguishable on disk from one sitting between turns. The
    // difference only exists in `claude agents`, which carries a separate
    // `state` ("done" once the work is over) alongside the status the hook
    // reports.
    //
    // That difference matters because a finished job has nothing to show: it
    // has no terminal, and `claude attach` on it prints "Attaching…" and exits
    // immediately, so an orb for one is a click that opens a window and closes
    // it again. Better not to have the orb.
    internal static class BackgroundJobs
    {
        // The scan runs every couple of seconds and this shells out, so it is
        // cached well above that. Long enough to be cheap, short enough that an
        // agent finishing doesn't leave a stale orb around for long.
        private const long CacheMs = 10_000;

        // In a holder rather than a field on this class, because it is only ever
        // taken by States(), which is excluded — and a static field initializer
        // does not run until some static field is touched, so this one reads as
        // an uncovered line in a class whose measured half never reaches it.
        // Excluding the holder is the honest description: it belongs to the
        // excluded code, not to the tested code.
        [ExcludeFromCodeCoverage]
        private static class Cache
        {
            internal static readonly object Gate = new();
        }
        private static Dictionary<string, string>? _states;
        private static long _stamp;

        // Which accounts the cached listing covers. A hit needs the same set, so
        // a session showing up in an account the listing did not ask about
        // forces a fresh read rather than a stale "not a job" (CB-217).
        private static string? _statesAccounts;

        // The transcript paths of the Claude Code sessions the scan can see,
        // noted by the scan's background half just before it asks for the
        // listing. Null until the first scan, which means "ask every account".
        private static string?[]? _liveTranscripts;

        // CB-217: called by the scan with every Claude Code session's transcript
        // path, so the listing can skip accounts that own none of them.
        [ExcludeFromCodeCoverage]
        internal static void NoteLiveTranscripts(IEnumerable<string?> transcriptPaths)
        {
            var paths = transcriptPaths.ToArray();
            lock (Cache.Gate) _liveTranscripts = paths;
        }

        // The accounts worth asking, out of accountDirs, in their order.
        //
        // The listing is only ever consulted by session id, and a session
        // belongs to exactly one account, so an account that owns none of the
        // sessions the scan can see cannot change any answer. A Claude Code
        // transcript lives under its account's config directory
        // (<dir>/projects/...), which is how a session is attributed.
        //
        // Conservative wherever attribution fails: no transcripts noted yet, or
        // any one session whose transcript is missing or under none of the
        // known directories, and every account is asked, as before. Skipping
        // then could turn a real job into "not a job".
        internal static List<string> AccountsToAsk(
            IReadOnlyList<string> accountDirs, IReadOnlyList<string?>? transcriptPaths)
        {
            if (transcriptPaths is null) return accountDirs.ToList();

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in transcriptPaths)
            {
                var owner = string.IsNullOrWhiteSpace(path)
                    ? null
                    : accountDirs.FirstOrDefault(dir => IsUnder(path, dir));

                if (owner is null) return accountDirs.ToList();
                wanted.Add(owner);
            }

            return accountDirs.Where(wanted.Contains).ToList();
        }

        internal static bool IsUnder(string path, string dir)
        {
            var root = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return path.Length > root.Length
                && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && (path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar);
        }

        // Whether this session is a background job that is still going.
        //
        // Asked of two shapes of session. The first records no pid at all — a
        // wider group than it sounds: a background agent has none, but neither
        // does a subagent, and neither does a session whose status file outlived
        // it. The second is a status file that lost SessionManager.Superseded's
        // pid tie-break — which happens for real to an Agent View background
        // session, since dispatching one shares its parent's pid rather than
        // getting one of its own. Both shapes write the same "idle" whether they
        // still have work to do or not, so the daemon's own list is the only
        // place to settle it.
        // Excluded from coverage: States() runs `claude agents --json` as a real
        // subprocess to ask the daemon for its listing. The answer, which is the
        // part with a decision in it, is IsLive below — separated for exactly this
        // reason and covered against hand-written listings in BackgroundJobsTests.
        [ExcludeFromCodeCoverage]
        public static bool IsLiveJob(string sessionId) => IsLive(States(), sessionId);

        // The answer, separated from fetching the listing so it can be tested
        // without a daemon to ask. `states` is what Parse returned — null for a
        // listing that could not be read at all.
        //
        // Absent from a listing that was read successfully means not a job at
        // all: a subagent, or a session that ended. "done" means it was one and
        // has finished. Neither has anything left to show, and an orb for
        // either is a click that can only fail.
        //
        // A listing that couldn't be read answers true, not false. This decides
        // whether to *hide* an orb, and no orb should vanish because the CLI
        // was briefly unavailable — the failure the user can't see is the one
        // worth being careful about.
        internal static bool IsLive(Dictionary<string, string>? states, string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return true;
            if (states is null) return true;

            // The session id first, because that is the question being asked and
            // a listing answers it directly. The short job id is a fallback for a
            // row that named no session — see Parse.
            if (!states.TryGetValue(sessionId, out var state)
                && !states.TryGetValue(JobIdOf(sessionId), out state))
            {
                return false;
            }

            return !string.Equals(state, "done", StringComparison.OrdinalIgnoreCase);
        }

        // The same lookup as IsLive, answering the question IsLive throws away:
        // not "is this still worth an orb" but "what is it doing".
        //
        // That distinction is the whole of the parked-orb bug. A background
        // session between turns and one mid-turn both answer IsLive true and
        // both write "idle" into their status file, so fifteen parked workers
        // rendered exactly like fifteen agents at work — same fill, same
        // breathing, no badge, nothing to say the difference. The daemon knew
        // all along: "blocked" for the parked ones, "working" for the busy one.
        //
        // Same key fallback as IsLive (session id first, short job id for a row
        // that named no session) and the same fail-open posture, stated the same
        // way: a listing that could not be read answers Unknown, and every rule
        // built on this treats Unknown as "change nothing".
        //
        // A state this build has never heard of reads as Working, which is
        // exactly what IsLive already does with it — "done" is the only word
        // that removes an orb and "blocked" the only one that dims it, so a
        // daemon that grows a sixth state renders as it always did rather than
        // going quietly still.
        internal static JobPhase Phase(Dictionary<string, string>? states, string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return JobPhase.Unknown;
            if (states is null) return JobPhase.Unknown;

            if (!states.TryGetValue(sessionId, out var state)
                && !states.TryGetValue(JobIdOf(sessionId), out state))
            {
                return JobPhase.NotAJob;
            }

            if (Is(state, "working")) return JobPhase.Working;
            if (Is(state, "blocked")) return JobPhase.Parked;
            if (Is(state, "done")) return JobPhase.Done;

            return JobPhase.Working;
        }

        private static bool Is(string? state, string word) =>
            string.Equals(state, word, StringComparison.OrdinalIgnoreCase);

        // One listing, for one scan.
        //
        // States() is cached for ten seconds and the scan runs every two, so
        // asking it once per rule is asking a question that can change its
        // answer halfway through a pass: the cache can expire between the
        // superseded check and the reachability one, and a session can be a
        // live job for the first and not for the second. That was survivable
        // while every consumer wanted the same bool. It stops being survivable
        // once the same listing also decides whether an orb is dimmed, because
        // then a single pass can both keep an orb and describe it wrongly.
        //
        // Excluded from coverage for the reason States() is: this is the process
        // launch and the clock, with no decision in it. What is decided with the
        // answer is IsLive and Phase above, both covered against hand-written
        // listings in BackgroundJobsTests and JobPhaseTests.
        [ExcludeFromCodeCoverage]
        internal static Dictionary<string, string>? SnapshotForScan() => States();

        // Excluded from coverage: shells out to the `claude` CLI, and the cache
        // it wraps is keyed on Environment.TickCount64. What it decides with the
        // answer is IsLive above, which is tested; what it decides about the
        // *listing* is Parse below, which is also tested. This is the process
        // launch and the clock, and nothing else.
        [ExcludeFromCodeCoverage]
        private static Dictionary<string, string>? States()
        {
            var accounts = Accounts();
            var key = string.Join("\n", accounts.Select(a => a.Dir));

            lock (Cache.Gate)
            {
                if (_states is not null && Environment.TickCount64 - _stamp < CacheMs && _statesAccounts == key)
                {
                    return _states;
                }
            }

            var fresh = Read(accounts);

            lock (Cache.Gate)
            {
                // A failed read doesn't clear a good answer: the listing going
                // missing for one tick is not evidence that anything finished.
                if (fresh is not null)
                {
                    _states = fresh;
                    _stamp = Environment.TickCount64;
                    _statesAccounts = key;
                }
                return _states;
            }
        }

        // Every account this machine has, as (directory, config dir to pass):
        // the inherited one first, passed as null so the CLI resolves it itself,
        // then the extras. Filtered by AccountsToAsk.
        [ExcludeFromCodeCoverage]
        private static List<(string Dir, string? ConfigDir)> Accounts()
        {
            string?[]? transcripts;
            lock (Cache.Gate) transcripts = _liveTranscripts;

            return Accounts(transcripts);
        }

        // The same, filtered by the transcripts given rather than the ones the
        // scan last noted — which is how Stop asks only the account that owns
        // the one session it was handed (CB-264).
        [ExcludeFromCodeCoverage]
        private static List<(string Dir, string? ConfigDir)> Accounts(IReadOnlyList<string?>? transcripts)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var inherited = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            var inheritedDir = string.IsNullOrWhiteSpace(inherited)
                ? Path.Combine(home, ".claude")
                : inherited.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var all = new List<(string Dir, string? ConfigDir)> { (inheritedDir, null) };
            all.AddRange(ExtraAccountDirs(home, OrbweaverSettings.ClaudeCodeProfileDirs, inherited)
                .Select(dir => (dir, (string?)dir)));

            var ask = AccountsToAsk(all.Select(a => a.Dir).ToList(), transcripts);
            return all.Where(a => ask.Contains(a.Dir, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        // Every account's listing, merged into one.
        //
        // The daemon is **per Claude Code config directory**, and so is
        // `claude agents`. One invocation therefore answers for one account and
        // says nothing whatever about the others — not "no jobs", but nothing,
        // which reads here as a session absent from a listing that was read
        // successfully, which is to say "not a job at all".
        //
        // That is the whole of the misread-background-job bug. A machine with a
        // second account (`CLAUDE_CONFIG_DIR=~/.claude-board`, the same
        // arrangement ClaudeCodeProfileDirs already exists to describe) runs two
        // daemons, and this asked only the first. Every background job under the
        // second was classified NotAJob, so SessionPresence.ShapeOf fell through
        // to Terminal and the chat panel told the user to reply in the terminal
        // — for a job the daemon runs precisely so that no terminal has to hold
        // it. Observed live: job `e4f5c5e4` ("makayla-case"), `kind: background`,
        // `state: done` under ~/.claude-board and absent from the default
        // account's listing altogether, while its process was alive the whole
        // time in a pty belonging to ~/.claude-board's own daemon.
        //
        // The null read asks whatever account *this app's own process*
        // inherited, which is not always the default one — CB-114. On a machine
        // where Buddy itself was launched with CLAUDE_CONFIG_DIR=~/.claude-board
        // set (the app inherits it same as any child process would), the null
        // read above asks the board account and ExtraAccountDirs used to seed
        // its de-dup set with a hardcoded ~/.claude on the assumption that the
        // null read had already covered it. It hadn't: ~/.claude was held out of
        // the explicit list *and* missed by the inheriting read, so the default
        // account was never asked at all, while ~/.claude-board was asked twice.
        //
        // ExtraAccountDirs now takes the inherited value explicitly and seeds
        // its de-dup set with whichever account the null read actually reached,
        // adding the default account back in whenever that isn't it. Which
        // account this app itself runs under still stays whatever the
        // environment says it is — the null read is untouched — but the
        // enumeration this asks *besides* that one now knows what it was.
        //
        // Excluded from coverage: the loop is process launches. What it decides
        // with the answers is Merge and ExtraAccountDirs below, both pure and
        // both covered.
        [ExcludeFromCodeCoverage]
        private static Dictionary<string, string>? Read(List<(string Dir, string? ConfigDir)> accounts)
        {
            // Invoked directly, not through a shell. The obvious way to reach a
            // binary that isn't on this app's PATH is to ask the user's shell to
            // find it, and that quietly doesn't work: `zsh -lc` reads .zshenv,
            // .zprofile and .zlogin, but *not* .zshrc, which is only for
            // interactive shells — and .zshrc is where a PATH addition for
            // ~/.local/bin normally lives. So the lookup failed with "command
            // not found" for exactly the launch this was written to survive, and
            // because a failed read is treated as "don't hide anything", every
            // finished session and every subagent got an orb again.
            var claude = ClaudeBinary.Path;
            if (claude is null) return null;

            // CB-217: only the accounts that own a session the scan can see, the
            // inherited one first as before. None at all means nothing is worth
            // asking, and every session is "not a job", exactly as a listing
            // that named none of them would have said.
            if (accounts.Count == 0) return new Dictionary<string, string>(StringComparer.Ordinal);

            Dictionary<string, string>? merged = null;
            var first = true;
            foreach (var (_, configDir) in accounts)
            {
                var one = ReadOne(claude, configDir);
                merged = first ? one : Merge(merged, one);
                first = false;
            }

            return merged;
        }

        // The extra accounts to ask, beyond whichever one this app itself runs
        // under.
        //
        // The same list TranscriptReader and the hook installer already walk —
        // ~/.claude plus ClaudeCodeProfileDirs — with whichever account the null
        // read already reached held out, because Read has already asked it and
        // asking again would be a second subprocess for an answer already in
        // hand. Held out by *path* rather than by name, so a list naming that
        // directory explicitly — which the settings UI permits — doesn't double
        // the work.
        //
        // `inheritedConfigDir` is CLAUDE_CONFIG_DIR as this app's own process
        // sees it — Read's null read asks that account, whatever it is, not
        // necessarily the default one. Unset (null, the default for every
        // caller before CB-114) means Read's null call reached the default
        // account, exactly as it always did. Set to something else means the
        // null call reached *that* account instead, so the default account
        // (~/.claude) is no longer implicitly covered and has to be added back
        // to the explicit list — the fix this ticket is for. Set to the default
        // account itself is the same case as unset, once resolved.
        //
        // Blank entries are skipped rather than resolving to $HOME, which is not
        // a config directory and whose listing would be some third account's or
        // nobody's.
        internal static List<string> ExtraAccountDirs(
            string home, IReadOnlyList<string> extras, string? inheritedConfigDir = null)
        {
            var defaultDir = Path.Combine(home, ".claude");

            // A blank CLAUDE_CONFIG_DIR is the same as an unset one: Claude Code
            // itself falls back to the default account rather than treating an
            // empty string as a real directory, and a trailing separator is
            // trimmed so it still matches defaultDir, which Path.Combine never
            // produces one of.
            var resolvedInherited = string.IsNullOrWhiteSpace(inheritedConfigDir)
                ? defaultDir
                : inheritedConfigDir.Trim().TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // OrdinalIgnoreCase for the reason OrbPositions is keyed that way:
            // Windows paths are, and one account reached under two
            // capitalizations is still one account. Seeded with the account the
            // null read actually reached, not assumed to be ~/.claude.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                resolvedInherited
            };

            var dirs = new List<string>();

            // The default account is asked explicitly whenever it wasn't the one
            // the null read already covered — the fix itself. When it was
            // (the ordinary, single-account machine), seen already holds it and
            // this is a no-op.
            if (seen.Add(defaultDir)) dirs.Add(defaultDir);

            foreach (var extra in extras)
            {
                if (string.IsNullOrWhiteSpace(extra)) continue;

                var full = Path.Combine(home, extra.Trim());
                if (seen.Add(full)) dirs.Add(full);
            }

            return dirs;
        }

        // Two accounts' listings as one, or nothing at all.
        //
        // A null on either side collapses the whole answer to null, and that is
        // the point rather than an oversight. null here means "there was no
        // listing to read", which every rule downstream treats as "change
        // nothing" — orbs stay, phases stay Unknown. A *partial* listing would
        // instead be a confident answer about sessions nobody managed to ask
        // about: the account that answered would be classified correctly, and
        // the account that didn't would have every one of its jobs read as
        // not-a-job — precisely the misclassification this change exists to
        // remove. Better to know nothing for one tick than to be wrong about
        // half the machine.
        //
        // First writer wins on a key collision, which puts the account read
        // first — this app's own — ahead of the rest. Two accounts cannot share
        // a session id, so the only reachable collision is between two short job
        // ids, eight hex characters each, from different daemons. Nothing here
        // could settle such a tie on the merits; preferring the nearer account
        // at least makes it the same tie every time.
        internal static Dictionary<string, string>? Merge(
            Dictionary<string, string>? first, Dictionary<string, string>? second)
        {
            if (first is null || second is null) return null;

            var merged = new Dictionary<string, string>(first, StringComparer.Ordinal);
            foreach (var pair in second) merged.TryAdd(pair.Key, pair.Value);

            return merged;
        }

        // One account's listing.
        //
        // configDir null means "leave the environment alone", which is how the
        // default account is read and why this change cannot move it.
        //
        // Both pipes are drained before waiting, which the single-account
        // version did not do — it called ReadToEnd() and then WaitForExit(5000),
        // and a blocking read makes that timeout unreachable while an undrained
        // stderr can deadlock a chatty child. That was survivable at one launch
        // per scan and stops being survivable at one per account, so this now
        // does what AgentRoster.Read already does, for the reasons stated there.
        //
        // Excluded from coverage: starts the `claude` CLI as a real subprocess.
        // The JSON it prints is parsed by Parse below, which is tested against
        // hand-written listings, so what is excluded here is the launch, its
        // timeout, and the kill for a CLI that never answers.
        [ExcludeFromCodeCoverage]
        private static Dictionary<string, string>? ReadOne(string claude, string? configDir)
        {
            // --json is documented as not needing a tty, which is what makes it
            // usable from a GUI app at all.
            if (Run(claude, configDir, "agents", "--json") is not (0, var stdout)) return null;

            try
            {
                return Parse(stdout);
            }
            catch
            {
                return null;
            }
        }

        // One `claude` invocation under one account: its exit code and stdout,
        // or null for a CLI that could not be started or never answered. Split
        // out of ReadOne when Stop became the second caller (CB-264); the
        // reasons for draining both pipes and for the timeout are ReadOne's,
        // stated above.
        //
        // Excluded from coverage: this is the process launch and nothing else.
        [ExcludeFromCodeCoverage]
        private static (int ExitCode, string Stdout)? Run(string claude, string? configDir, params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(claude)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                foreach (var arg in args) psi.ArgumentList.Add(arg);

                if (configDir is not null) psi.Environment["CLAUDE_CONFIG_DIR"] = configDir;

                using var process = Process.Start(psi);
                if (process is null) return null;

                var outTask = process.StandardOutput.ReadToEndAsync();
                var errTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(5000))
                {
                    try { ProcessTreeKill.Kill(process); } catch { }
                    return null;
                }

                var stdout = outTask.GetAwaiter().GetResult();
                errTask.GetAwaiter().GetResult();

                return (process.ExitCode, stdout);
            }
            catch
            {
                return null;
            }
        }

        // --- CB-264: ending a background job --------------------------------
        //
        // Signalling a job's worker does not end the job. The daemon hosts it,
        // and a job that still has work in front of it — parked on a question,
        // the case that matters, since a parked job is exactly the one somebody
        // gives up on — is resumed on a fresh worker within seconds, orb and
        // all. Measured on a scratch job blocked on a permission prompt: SIGTERM
        // to its pid, listed without one for about nine seconds, then back on a
        // new pid, still blocked, with nothing in the daemon's own log. A
        // finished job given the same signal stays gone, which is why most End
        // clicks looked as though they worked. Observed live first: a parked
        // job ended "several times" from its orb, back every time.
        //
        // `claude stop <id>` is the daemon's own verb for it, and it holds: the
        // same scratch job, stopped that way, left the active listing and read
        // `stopped` under --all. Its conversation is kept, which the CLI states
        // and which is the right default for a gesture with no confirm.

        // Whether End should ask the daemon before reaching for a signal.
        //
        // Only Claude Code has a daemon. A listing that names the session in any
        // state is reason enough — a finished job is harmless to stop — and so
        // is a listing that could not be read, since Unknown says nothing about
        // the session and Stop reads a fresh one of its own before acting.
        // NotAJob, an answer actually read, is the one case worth skipping: a
        // terminal session, which costs the click a pointless subprocess.
        internal static bool WorthAskingTheDaemon(
            SessionSource source, Dictionary<string, string>? states, string sessionId) =>
            source == SessionSource.ClaudeCode && Phase(states, sessionId) != JobPhase.NotAJob;

        // The daemon's short id for the job running this session, off a raw
        // `claude agents --json`. Null when no background row is running it.
        //
        // Read off the row rather than derived, for the reason Parse gives: a
        // resumed job keeps its first session's id while running a new one —
        // the live case was job 80b19c48 running session f5e13eda-…, so
        // JobIdOf would have stopped nothing, or something else. The short id
        // is matched only for a row that names no session, the same fallback
        // Parse keeps, and for no other reason.
        internal static string? JobIdFor(string json, string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

                string? fallback = null;
                foreach (var entry in document.RootElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    if (Text(entry, "id") is not { Length: > 0 } jobId) continue;

                    var named = Text(entry, "sessionId");
                    if (named == sessionId) return jobId;
                    if (string.IsNullOrEmpty(named) && jobId == JobIdOf(sessionId)) fallback ??= jobId;
                }

                return fallback;
            }
        }

        // Asks the daemon that owns this session to stop its job. True only
        // when a daemon named the job and `claude stop` said yes; false sends
        // the caller back to the signal it always used, so a machine where the
        // CLI cannot be reached ends sessions exactly as it did before.
        //
        // Every account the transcript could belong to is asked, through the
        // same attribution the listing uses — one, ordinarily — and the stop
        // goes to the daemon whose listing named the job, never another: two
        // daemons' eight-character ids can collide (see Merge), and a stop
        // sent to the wrong one would end somebody else's job.
        //
        // Excluded from coverage: two process launches. What decides which job
        // is JobIdFor, and which accounts is AccountsToAsk, both covered.
        [ExcludeFromCodeCoverage]
        internal static bool Stop(string sessionId, string? transcriptPath)
        {
            var claude = ClaudeBinary.Path;
            if (claude is null) return false;

            foreach (var (_, configDir) in Accounts(new[] { transcriptPath }))
            {
                if (Run(claude, configDir, "agents", "--json") is not (0, var listing)) continue;
                if (JobIdFor(listing, sessionId) is not { } jobId) continue;

                var stopped = Run(claude, configDir, "stop", jobId) is (0, _);
                if (stopped) Invalidate();
                return stopped;
            }

            return false;
        }

        // A stopped job leaves the listing, and the next scan should see that
        // rather than a cached row for up to ten seconds that would keep its
        // orb up — the same orb the user just asked to be rid of.
        [ExcludeFromCodeCoverage]
        private static void Invalidate()
        {
            lock (Cache.Gate) _stamp = Environment.TickCount64 - CacheMs;
        }

        // `claude agents --json` turned into the states IsLive asks about, keyed
        // by what the caller will name the session as.
        //
        // A background row carries both an `id` — the short job id, which is
        // also the name of the job's directory under ~/.claude/jobs — and a
        // `sessionId`, the full uuid of the session running it. The first
        // version of this keyed the map on the short id and derived the same
        // string from the session id it was asked about, on the assumption that
        // one is the first segment of the other.
        //
        // It usually is, which is what let the assumption survive: a job's id is
        // the first segment of the session id it *started* with. But a job
        // outlives that session — resume it, or let it compact, and it keeps its
        // original id while the work moves on to a session with a new uuid.
        // Observed on a real machine: job `5f6960b2` running session
        // `53bd5d2c-…`, working, with the derived lookup asking about `53bd5d2c`
        // and finding nothing. Absent reads as "not a job at all", so a
        // background session that was busy working had its orb dropped on every
        // scan, and the same miss let SessionManager.Superseded call it stale.
        //
        // So the map is keyed by the session id the row states, rather than by
        // anything derived. The short id is stored only for a row that named no
        // session — an older CLI, or a job the daemon knows about before its
        // session exists — which is what keeps the fallback in IsLive worth
        // having. Storing it as well as the session id would undo the fix by the
        // back door: the *earlier* session of a resumed job would match its own
        // job's row and keep an orb for a conversation that has moved on.
        internal static Dictionary<string, string>? Parse(string json)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;

                // Interactive sessions carry no id at all — only background
                // ones are jobs — so they simply don't appear here, and
                // nothing downstream ever asks about them.
                if (Text(entry, "id") is not { Length: > 0 } jobId) continue;

                var state = Text(entry, "state") ?? "";

                map[Text(entry, "sessionId") is { Length: > 0 } sessionId ? sessionId : jobId] = state;

                // A row whose current session is this app's own remote-control
                // relay counts for its short id as well. The rule above — never
                // store the short id beside a named session — exists because a
                // resumed job's earlier sessions are conversations the work has
                // moved on from, and matching them would resurrect their orbs.
                // A relay is not that: it is a viewport this app itself opened
                // over the job (claude.ai remote control resumes the job into
                // the relay's session, so the row re-points at it), the scan
                // suppresses the relay's own file as plumbing (IsOwnRelayCwd,
                // same test), and the original conversation is still live and
                // still writing its status file. Without this, connecting
                // remote control to a background job made its orb vanish: the
                // job's one row named a session the scan hides, and the session
                // the user was actually talking to missed the lookup and read
                // as not-a-job. Observed live — job aff9cfe4 re-pointed at
                // relay session a16ff9fb, and the chat being steered through
                // that relay lost its orb mid-conversation.
                //
                // The row's cwd is the relay's by construction (RelayCwd runs
                // every relay from a directory named after itself), so the test
                // is the same pure string check the scan already keys on.
                if (MachineNames.LooksLikeALeftoverRelay(Text(entry, "cwd")))
                {
                    map.TryAdd(jobId, state);
                }
            }

            return map;
        }

        private static string? Text(JsonElement entry, string name) =>
            entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        // The short form the daemon uses, which is the first segment of the
        // session id a job started with. Only reached for a row that named no
        // session of its own; split rather than a fixed width so an id that
        // isn't a uuid degrades to itself.
        // internal: AgentTeamViewer carries its own copy of this rule, and the
        // two are used together — one decides which pane to reuse and the other
        // whether the job is still alive — so a test asserts they agree. A
        // session that resolved to two different job ids would be adopted into a
        // pane and then have its orb hidden.
        internal static string JobIdOf(string sessionId)
        {
            var dash = sessionId.IndexOf('-');
            return dash > 0 ? sessionId[..dash] : sessionId;
        }
    }
}
