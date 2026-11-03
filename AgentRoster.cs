using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Text.Json;

namespace Orbweaver
{
    // Which of this machine's Claude Code sessions is the one another machine
    // knows by a given name.
    //
    // This exists to answer one question exactly, where getting it nearly right
    // would be worse than not answering. A peer row on the far side carries a
    // *name* — `job-hunter` — and nothing else identifying: no pid, no session
    // id, no machine (`ListAgents` was asked directly and said
    // `"machine":"unknown"`; see docs/remote-control-findings.md). Buddy's own
    // status files are keyed by session id and carry a Title that is whatever
    // /rename last set, which is frequently not that name — the names Claude
    // Code registers look derived from the working directory (`placement-41`,
    // `evidence`) unless someone changed them. So matching a peer name against
    // Title, or against the folder, is guesswork, and a wrong match here means
    // showing one session's private conversation on another session's panel.
    //
    // `claude agents --json` removes the guess. It is the same registry the peer
    // list is drawn from, it runs locally against the same profile, and it
    // prints the session id alongside the name — so name → session id → status
    // file is an exact join rather than a resemblance. Shape, captured from a
    // real run rather than remembered:
    //
    //     [
    //       {
    //         "pid": 77492,
    //         "cwd": "/Users/user/Documents/GTD/Evidence",
    //         "kind": "interactive",
    //         "startedAt": 1787191603916,
    //         "sessionId": "bd79c1fb-a5a9-4691-90e3-45b927c44c4e",
    //         "name": "job-lawyer",
    //         "status": "idle"
    //       },
    //       …
    //     ]
    //
    // Parsing is split from running for the reason everything else in this app
    // is: the parser is pure and has a fixture, and the process call is the part
    // a headless test must not make.
    internal static class AgentRoster
    {
        // One registration, with only the fields anything here uses. `kind` and
        // `status` are deliberately not carried: this is an identity lookup, and
        // liveness is already answered better by the status file the session
        // itself wrote.
        internal readonly record struct Entry(string Name, string SessionId, int Pid);

        // Empty for anything unreadable, which is the same rule ParseAgents next
        // door follows: a registry we cannot read means no session can be
        // matched, and no match means the panel says it has no live view rather
        // than showing the wrong one.
        public static IReadOnlyList<Entry> ParseAgentsJson(string? json)
        {
            var found = new List<Entry>();
            if (string.IsNullOrWhiteSpace(json)) return found;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return found;

                foreach (var row in doc.RootElement.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) continue;

                    var name = Str(row, "name");
                    var sessionId = Str(row, "sessionId");

                    // Both halves are the point. A row missing either cannot
                    // complete the join it exists for, so it is dropped rather
                    // than half-kept.
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (string.IsNullOrWhiteSpace(sessionId)) continue;

                    var pid = 0;
                    if (row.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number)
                        p.TryGetInt32(out pid);

                    found.Add(new Entry(name!, sessionId!, pid));
                }
            }
            catch
            {
                // Not JSON, or not this shape. Nothing to match against.
            }

            return found;
        }

        // The one session registered under this name, or null.
        //
        // **Null when two sessions share the name**, which is the whole reason
        // this is a method rather than a dictionary lookup. Two sessions in one
        // account can absolutely carry one name — the same person working in two
        // checkouts of one repository gets it without trying — and there is
        // nothing in a peer row that distinguishes them. Picking either would be
        // right half the time, and the failure is silent and private: someone's
        // other conversation, mirrored onto the wrong panel. Refusing is the
        // only honest answer, and the caller turns it into "no live view".
        public static Entry? Resolve(IReadOnlyList<Entry> entries, string name)
        {
            Entry? only = null;

            foreach (var entry in entries)
            {
                if (!entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (only is not null) return null;

                only = entry;
            }

            return only;
        }

        // How that subprocess is set up, split out for the reason the relay's
        // launch line was (deleted in 937de9ec): the environment it does *not* carry
        // is as load-bearing as the environment it does, and until this was its
        // own function the only way to check either was to run a real `claude`
        // against a real account.
        //
        // A null configDir leaves the variable off the child's environment
        // rather than setting it to anything, so the child inherits whatever
        // this process has — which for the default account is exactly the
        // context an ordinary `claude` would use.
        internal static ProcessStartInfo AgentsProcess(string claude, string? configDir)
        {
            var psi = new ProcessStartInfo(claude)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("agents");
            psi.ArgumentList.Add("--json");

            if (configDir is not null) psi.Environment["CLAUDE_CONFIG_DIR"] = configDir;

            return psi;
        }

        // Asks this machine's Claude Code what it has registered, under a
        // specific profile.
        //
        // CLAUDE_CONFIG_DIR is set because the registry is per-account, and
        // reading the wrong account's would answer confidently about sessions
        // that account cannot see.
        //
        // ...and left unset for the default account, for the reason
        // ClaudeProfile gives: naming that directory explicitly is not the same
        // as saying nothing, and the context it names is one nothing has ever
        // onboarded. Here the consequence is milder than the relay's — a read
        // against a context with no credentials answers empty rather than
        // hanging on a wizard — but it is the same wrong account, so a fix in
        // one place and not the other would be half a fix (CB-42).
        // What came back from one `claude agents --json` run, as plain facts,
        // so what they *mean* can be decided (and tested) without a process.
        internal readonly record struct RunOutcome(
            bool BinaryFound, bool Started, bool TimedOut, int ExitCode, string? Stdout, string? Threw);

        // Entries, and why there are none when that is because the read failed.
        // Failure is null for a read that worked — including one that answered
        // an empty list, which is a registry with nothing in it and not a
        // problem. The reason carries no stderr and no path: stderr can hold
        // either, and a log is not the place for them.
        internal readonly record struct ReadResult(IReadOnlyList<Entry> Entries, string? Failure);

        // CB-300: Read used to turn a timeout or a non-zero exit into an empty
        // list and say nothing, so a dir whose `claude agents` was failing
        // looked exactly like one with no sessions, and the roster-read-failed
        // line RemoteMirrorServer wrote for a throw could never fire.
        internal static ReadResult Interpret(RunOutcome run)
        {
            if (!run.BinaryFound) return Fail("no-binary");
            if (run.Threw is not null) return Fail("threw:" + run.Threw);
            if (!run.Started) return Fail("start-failed");
            if (run.TimedOut) return Fail("timeout");
            if (run.ExitCode != 0) return Fail("exit=" + run.ExitCode);

            return new ReadResult(ParseAgentsJson(run.Stdout), null);

            static ReadResult Fail(string why) => new(Array.Empty<Entry>(), why);
        }

        // Excluded from coverage: launches `claude agents --json` as a real
        // subprocess against a real account's config directory. What comes back
        // is turned into entries by ParseAgentsJson and into a verdict by
        // Interpret, both pure and covered — this is only the asking, its
        // timeout, and the kill for a CLI that never answers.
        [ExcludeFromCodeCoverage]
        private static RunOutcome Run(string profileDir, int timeoutMs)
        {
            var claude = ClaudeBinary.Path;
            if (claude is null) return new RunOutcome(false, false, false, 0, null, null);

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            try
            {
                var psi = AgentsProcess(claude, ClaudeProfile.ConfigDirFor(home, profileDir));

                using var process = Process.Start(psi);
                if (process is null) return new RunOutcome(true, false, false, 0, null, null);

                // Both pipes drained before waiting, for two reasons: a
                // blocking read makes the timeout unreachable, and an undrained
                // stderr can deadlock a chatty child.
                var outTask = process.StandardOutput.ReadToEndAsync();
                var errTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(true); } catch { }
                    return new RunOutcome(true, true, true, 0, null, null);
                }

                var stdout = outTask.GetAwaiter().GetResult();
                errTask.GetAwaiter().GetResult();

                return new RunOutcome(true, true, false, process.ExitCode, stdout, null);
            }
            catch (Exception ex)
            {
                return new RunOutcome(true, true, false, 0, null, ex.GetType().Name);
            }
        }

        [ExcludeFromCodeCoverage]
        internal static ReadResult ReadChecked(string profileDir, int timeoutMs = 5000) =>
            Interpret(Run(profileDir, timeoutMs));

        // The empty-list contract every existing caller relies on: a failure is
        // an empty list here, and ReadChecked is for the caller that wants to
        // know why.
        [ExcludeFromCodeCoverage]
        public static IReadOnlyList<Entry> Read(string profileDir, int timeoutMs = 5000) =>
            ReadChecked(profileDir, timeoutMs).Entries;

        private static string? Str(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
