using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;

namespace Orbweaver
{
    // Where the usage readings come from, as an interface, so the windows above
    // can be driven in a test without launching anything.
    //
    // The same reason RemoteChat.cs gives for IRemoteChatSession existing: a
    // surface whose data arrives from another process is untestable until the
    // arrival is a seam. UiTests hands the orbs a fake implementing this and
    // asserts on what a user would see.
    internal interface IUsageSource
    {
        IReadOnlyList<AccountUsage> Read();
    }

    // Which accounts to ask, and what to call them.
    //
    // Pure and separated from the launching for the usual reason, but also
    // because the naming rule has a trap in it worth a test of its own — see
    // AccountFilePath.
    internal static class UsageAccounts
    {
        // The file holding an account's identity.
        //
        // **The default account's is a sibling of its config directory, not
        // inside it.** Claude Code reads `(CLAUDE_CONFIG_DIR ?? homedir) +
        // "/.claude.json"`, so the account this app runs under is described by
        // `~/.claude.json` while `~/.claude-work` is described by
        // `~/.claude-work/.claude.json`. The trap is that `~/.claude/.claude.json`
        // also exists, is a different and older file, and has no oauthAccount in
        // it at all — so a reasonable-looking Path.Combine(configDir,
        // ".claude.json") for the default account finds a real file, parses it
        // successfully, and silently comes back with no name.
        internal static string AccountFilePath(string home, string? configDir) =>
            Path.Combine(configDir ?? home, ".claude.json");

        // What to call an account when its identity file cannot be read, or has
        // no account in it — an account that has never been logged in, which is
        // an ordinary state and not an error.
        //
        // The directory name with its leading dot and its "claude-" prefix
        // removed, because every one of these directories starts with both and a
        // row of orbs reading "claude-work", "claude-board" says nothing that the
        // orbs' own presence does not.
        internal static string FallbackLabel(string? configDir)
        {
            if (string.IsNullOrWhiteSpace(configDir)) return "default";

            var name = Path.GetFileName(configDir.TrimEnd(Path.DirectorySeparatorChar,
                                                          Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) return "default";

            name = name.TrimStart('.');
            if (name.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
                && name.Length > "claude-".Length)
            {
                name = name["claude-".Length..];
            }

            return name.Length == 0 ? "default" : name;
        }

        // The account's own name for itself, out of its identity file.
        //
        // The email's local part rather than the display name: two accounts at
        // the same organisation share a display name often enough
        // ("Repo Owner" twice tells you nothing), while the local part is
        // what actually distinguishes them and is what the person typed to log
        // in. Falls back through displayName to the directory name.
        internal static string LabelFrom(string? json, string? configDir)
        {
            var fallback = FallbackLabel(configDir);
            if (string.IsNullOrWhiteSpace(json)) return fallback;

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("oauthAccount", out var account)
                    || account.ValueKind != JsonValueKind.Object)
                {
                    return fallback;
                }

                if (account.TryGetProperty("emailAddress", out var email)
                    && email.ValueKind == JsonValueKind.String)
                {
                    var address = email.GetString();
                    if (!string.IsNullOrWhiteSpace(address))
                    {
                        var at = address.IndexOf('@');
                        var local = at > 0 ? address[..at] : address;
                        if (local.Length > 0) return local;
                    }
                }

                if (account.TryGetProperty("displayName", out var display)
                    && display.ValueKind == JsonValueKind.String)
                {
                    var name = display.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }

                return fallback;
            }
            catch (JsonException)
            {
                return fallback;
            }
        }

        // The full email, for the card, which has room to be precise where the
        // orb does not. Null when the account has never been logged in.
        internal static string? EmailFrom(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("oauthAccount", out var account)
                    || account.ValueKind != JsonValueKind.Object
                    || !account.TryGetProperty("emailAddress", out var email)
                    || email.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                var address = email.GetString();
                return string.IsNullOrWhiteSpace(address) ? null : address;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Every account to ask, this app's own first.
        //
        // null leads, meaning "reach the default account" — and for the usage
        // poll that is always literally true, because UsageProcess removes
        // CLAUDE_CONFIG_DIR from the child's environment on a null read (CB-113)
        // rather than letting it inherit. The rest come from
        // BackgroundJobs.ExtraAccountDirs rather than being re-derived here, so a
        // settings list naming an account explicitly does not ask it twice.
        //
        // **This deliberately does not pass the inherited CLAUDE_CONFIG_DIR on,
        // unlike BackgroundJobs (CB-114).** There the null read inherits the
        // variable, so the enumeration has to know which account that reached.
        // Here the null read never inherits it, so telling ExtraAccountDirs the
        // null read reached `~/.claude-board` was wrong twice over (CB-295): it
        // added `~/.claude` explicitly, and `CLAUDE_CONFIG_DIR=~/.claude` reads
        // the un-onboarded `~/.claude/.claude.json` and answers
        // rate_limits_available:false — a phantom "claude" orb saying "No
        // subscription limits on this account" — while holding the inherited
        // dir out of the list as already asked, which the poll never did. The
        // two rules are one pair: if UsageProcess ever goes back to inheriting
        // on a null read, this has to pass the inherited value again.
        internal static List<string?> ConfigDirs(
            string home, IReadOnlyList<string> extras)
        {
            var dirs = new List<string?> { null };
            foreach (var dir in BackgroundJobs.ExtraAccountDirs(home, extras))
            {
                dirs.Add(dir);
            }
            return dirs;
        }
    }

    // One reading per account, by asking the CLI.
    //
    // **Nothing here reads a credential.** Claude Code is asked for the answer
    // over its own control protocol and handles its own auth, refresh and
    // storage, which is what makes this work identically on the three platforms
    // this app ships to. The alternative — reading the OAuth token out of the
    // login keychain — was rejected on a specific hazard rather than on taste:
    // refreshing that token rotates the refresh token and must be written back
    // under a cross-process lock with a compare-and-swap, so a second writer
    // that loses the race can log the user out of the account it was trying to
    // report on.
    //
    // The request charges nothing. It is answered from Claude Code's own cache
    // of the usage endpoint and makes no model call — measured at
    // total_cost_usd 0 and total_api_duration_ms 0 — but it does start a process
    // and take a couple of seconds, which is why callers are expected to pace
    // themselves rather than asking whenever they would like to know.
    //
    // **How often to ask is UsagePollCadence's decision, and this class used to
    // hold a constant that said otherwise.** That constant, MinimumInterval, was
    // five minutes, and it justified itself like this:
    //
    //     Claude Code caches the underlying fetch with a five-minute write
    //     guard, so asking more often than this cannot produce a newer number —
    //     it only spends a process launch to be told the same thing.
    //
    // That is an assertion of fact about somebody else's program, it was never
    // checked, and it is false. Calling exactly the command below in a loop,
    // under the app's own environment, returns a fresher figure far sooner:
    // five-hour utilization read 24 and then 23 **twelve seconds later**, and 15
    // against 18 sixty-five seconds apart; a second account moved 48 to 49
    // across that same gap, so it is not one account behaving oddly. If the
    // write guard existed, none of those pairs could differ. The one-point
    // granularity is the API reporting an integer, and the twelve-second 24-to-
    // 23 is that integer crossing 23.5 — which is itself evidence the value
    // behind it is being re-read on that timescale rather than held.
    //
    // The constant is gone rather than corrected, because the number it held is
    // now UsagePollCadence.Slow and two copies of one interval is how the next
    // person changes one and not the other. What it cost while it stood is the
    // reason this paragraph is longer than the constant was: it read like a
    // finding rather than an assumption, so nobody weighed the cadence against a
    // real cost — and an investigation into account orbs a user reported as
    // frozen lost hours downstream of it, because a comment stating that fresher
    // data does not exist rules out the cheapest explanation first.
    //
    // What is true, and what UsagePollCadence trades against: one full
    // CompositeUsageSource.Read() is three subprocesses — a `claude -p` per
    // Claude account plus one `codex app-server`; Grok is read off disk — and
    // ran 4.5s to 7.7s of wall clock across thirteen rounds, median 5.6s, on a
    // machine at load average 5.5 across 14 cores. The rings are still not a
    // live readout and were never designed to be. That is now a statement about
    // what a poll costs, not a claim about what the CLI would refuse to tell us.
    //
    // One caution for anyone changing that cadence, and the reconciliation of it
    // with the cadence that already did: RunOne below gives up at 20s and
    // CodexAppServerUsage.Ask at 15s, and a dropped source is not a gap on
    // screen — AccountOrbs.Apply keeps the reading it already had, deliberately,
    // so a network blink does not look like an account being deleted. A cadence
    // fast enough to start missing its own deadline would therefore make the orb
    // *less* truthful rather than more, which means the tail matters here and
    // the median does not. UsagePollCadence already polls as often as every
    // sixty seconds and does **not** run into this, because AccountOrbs._polling
    // stops a read in flight from ever being joined by a second: each read still
    // gets the whole 20s or 15s regardless of the interval. See the comment on
    // UsagePollCadence.Fast, which has the argument in full — a cadence changes
    // how often a read starts, never how long it may take.
    internal sealed class UsagePoller : IUsageSource
    {
        // Generous, and deliberately not the five seconds BackgroundJobs uses
        // for `claude agents --json`. This call was measured at ~2.4s and is
        // dominated by a transcript scan the CLI performs for its own /usage
        // display, with no flag to skip it; a machine with a large history or a
        // cold cache will be slower. Five seconds here would time out on exactly
        // the machines that most need the answer.
        private const int TimeoutMs = 20000;

        private const string ControlRequest =
            "{\"type\":\"control_request\",\"request_id\":\"cb-usage\"," +
            "\"request\":{\"subtype\":\"get_usage\"}}";

        public IReadOnlyList<AccountUsage> Read()
        {
            if (!OrbweaverSettings.AccountUsageEnabled)
                return Array.Empty<AccountUsage>();

            var claude = ClaudeBinary.Path;
            if (claude is null) return Array.Empty<AccountUsage>();

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var readings = new List<AccountUsage>();

            foreach (var configDir in
                     UsageAccounts.ConfigDirs(home, OrbweaverSettings.ClaudeCodeProfileDirs))
            {
                var label = UsageAccounts.LabelFrom(
                    ReadAccountFile(home, configDir), configDir);

                var stdout = RunOne(claude, configDir);
                var usage = UsageParse.FromStream(
                    stdout, configDir, label, DateTimeOffset.UtcNow);

                // A failed read contributes nothing rather than a blank reading.
                // BackgroundJobs.Merge makes the argument at length: a partial
                // answer is a confident claim about an account nobody managed to
                // ask, and here it would mean drawing an empty gauge for an
                // account that might be at 99%.
                if (usage is not null) readings.Add(usage);
            }

            return readings;
        }

        [ExcludeFromCodeCoverage]
        private static string? ReadAccountFile(string home, string? configDir)
        {
            try
            {
                var path = UsageAccounts.AccountFilePath(home, configDir);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        // How this account's poll is set up, split out for the reason
        // AgentRoster.AgentsProcess is: the environment it does *not* carry is
        // as load-bearing as the environment it does, and until this was its
        // own function the only way to check either was to run a real `claude`
        // against a real account.
        //
        // **This is not AgentRoster.AgentsProcess's rule, on purpose, even
        // though the shape looks identical.** CB-42's "a null configDir leaves
        // the variable alone" is about *launching a session on the user's
        // behalf* — a relay, a background agents query — where inheriting
        // whatever account the user's environment already names is the
        // correct default, because the user is the one who chose it. A usage
        // poll is not launching anything on anyone's behalf; it is answering
        // a specific question an orb has already committed to: "how is
        // *this* named account doing?" The name half of that claim is pinned
        // to disk — UsageAccounts.AccountFilePath reads `~/.claude.json` for
        // the default account regardless of what the environment says — so
        // pinning the data half to the same file is what makes the orb's
        // claim true. Leaving the variable to inherit gives one orb a name
        // from one source and numbers from another, and nothing here would
        // ever notice the two had drifted apart. That drift is CB-113: the
        // default orb was labelled from `~/.claude.json` and reporting
        // whatever `CLAUDE_CONFIG_DIR` the app process happened to be started
        // under — a different, real account's numbers under this account's
        // name.
        //
        // So the null case is explicit here, not merely absent: it removes
        // the variable rather than leaving it. `psi.Environment` starts out
        // seeded from this process's own environment, so a plain assignment
        // for the named case is not enough to sever inheritance for the
        // default one — `Remove` is the only thing that actually does. This
        // is *not* the CB-42 hazard repeated: with no variable at all, Claude
        // Code reads `$HOME/.claude.json` — the very file
        // UsageAccounts.LabelFrom already read the label from — and not the
        // separate, frequently un-onboarded `$HOME/.claude/.claude.json` that
        // ClaudeProfile's comment warns about. Naming that directory
        // explicitly would be the trap; removing the variable lands on the
        // same file the label came from.
        //
        // The one user-visible consequence: someone who runs the whole app
        // under a non-default CLAUDE_CONFIG_DIR sees their default orb's
        // numbers change. Nothing is newly wrong for them — that orb was
        // already labelled from `~/.claude.json`, so this makes an existing,
        // mislabelled reading correct rather than pointing the orb at an
        // account it wasn't already claiming to be.
        internal static ProcessStartInfo UsageProcess(string claude, string? configDir)
        {
            var psi = new ProcessStartInfo(claude)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-p");

            // Mandatory alongside `-p --output-format stream-json`; the CLI
            // refuses the combination without it.
            psi.ArgumentList.Add("--verbose");

            // Keeps a poll that runs every five minutes forever out of
            // ~/.claude/projects, where it would otherwise leave a transcript
            // per account per poll for a conversation that never happened.
            psi.ArgumentList.Add("--no-session-persistence");

            // Stops the user's own SessionStart hooks firing on every poll —
            // **including this app's own**, which would otherwise have the
            // poller manufacturing the orbs it is measuring.
            psi.ArgumentList.Add("--settings");
            psi.ArgumentList.Add("{\"disableAllHooks\":true}");

            psi.ArgumentList.Add("--input-format");
            psi.ArgumentList.Add("stream-json");
            psi.ArgumentList.Add("--output-format");
            psi.ArgumentList.Add("stream-json");

            if (configDir is not null) psi.Environment["CLAUDE_CONFIG_DIR"] = configDir;
            else psi.Environment.Remove("CLAUDE_CONFIG_DIR");

            return psi;
        }

        // One account's answer, as raw stdout.
        //
        // Excluded from coverage: starts the `claude` CLI as a real subprocess.
        // What is excluded is the launch, its timeout and the kill for a CLI that
        // never answers — the JSON it prints is parsed by UsageParse, which is
        // covered against real captured payloads. The same split, for the same
        // reason, as BackgroundJobs.ReadOne. UsageProcess above is the testable
        // seam; nothing about *what* it builds is excluded, only the running of it.
        [ExcludeFromCodeCoverage]
        private static string? RunOne(string claude, string? configDir)
        {
            try
            {
                var psi = UsageProcess(claude, configDir);

                using var process = Process.Start(psi);
                if (process is null) return null;

                // No InternalSessions claim here, deliberately, and this is the
                // note that stops one being added later by analogy with the
                // summariser.
                //
                // UsageProcess already passes `--settings
                // {"disableAllHooks":true}` for precisely this reason — see its
                // own comment, which says it keeps the poller from
                // "manufacturing the orbs it is measuring". So no hook runs, no
                // status file is written, and there is nothing for the scan to
                // pick up. Measured: over a ~40s poll producing real output,
                // $TMPDIR/claude_buddy was never created at all.
                //
                // Claiming the pid anyway would be a guard against a file that
                // cannot exist, sitting inside an [ExcludeFromCodeCoverage]
                // method where nothing would ever notice it rotting. **If that
                // flag is ever removed, claim the pid here the way SpeechSummary
                // does** — that is the condition under which this becomes
                // necessary, rather than merely tidy.

                // Both pipes drained before waiting, and stdin closed so the CLI
                // knows no further requests are coming and exits. A blocking
                // ReadToEnd here would make the timeout below unreachable, and an
                // undrained stderr can deadlock a chatty child — the same two
                // hazards BackgroundJobs.ReadOne documents.
                var outTask = process.StandardOutput.ReadToEndAsync();
                var errTask = process.StandardError.ReadToEndAsync();

                process.StandardInput.WriteLine(ControlRequest);
                process.StandardInput.Close();

                if (!process.WaitForExit(TimeoutMs))
                {
                    try { process.Kill(true); } catch { }
                    return null;
                }

                var stdout = outTask.GetAwaiter().GetResult();
                errTask.GetAwaiter().GetResult();

                return process.ExitCode == 0 ? stdout : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
