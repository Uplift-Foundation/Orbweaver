using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Orbweaver
{
    // Where the `grok` CLI is, for the one part of this app that shells out to
    // it. Same problem and same answer as ClaudeBinary and CodexBinary, whose
    // comments have the full story: this app cannot count on PATH, so known
    // install locations are checked directly and PATH is consulted last.
    //
    // The standalone install here is a symlink at ~/.grok/bin/grok pointing at
    // a platform-specific binary under ~/.grok/downloads — confirmed by reading
    // the real symlink on this machine, which is why that path is checked
    // ahead of any system install.
    internal static class GrokBinary
    {
        private static readonly object Gate = new();
        private static bool _looked;
        private static string? _path;

        public static string? Path
        {
            get
            {
                lock (Gate)
                {
                    if (_looked) return _path;
                    _looked = true;
                    _path = Locate();
                    return _path;
                }
            }
        }

        private static readonly string[] SystemInstalls =
        {
            "/opt/homebrew/bin/grok",
            "/usr/local/bin/grok",
            "/usr/bin/grok"
        };

        internal static readonly string[] WindowsExtensions = { "", ".exe", ".cmd", ".bat" };
        internal static readonly string[] UnixExtensions = { "" };

        private static string[] DefaultExtensions =>
            OperatingSystem.IsWindows() ? WindowsExtensions : UnixExtensions;

        // Every input is a parameter with the real one as its default — see
        // CodexBinary.Locate for why: a test that controlled only `home` would
        // find a real `grok` on this developer's Mac and nothing on a CI
        // runner, one test passing for two different reasons.
        internal static string? Locate(
            string? home = null, string? searchPath = null, string[]? systemInstalls = null,
            string[]? extensions = null)
        {
            home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            extensions ??= DefaultExtensions;

            string[] candidates =
            [
                System.IO.Path.Combine(home, ".grok", "bin", "grok"),
                .. systemInstalls ?? SystemInstalls
            ];

            foreach (var candidate in candidates)
            {
                var found = FirstThatExists(candidate, extensions);
                if (found is not null) return found;
            }

            var path = searchPath ?? Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return null;

            foreach (var dir in path.Split(
                         System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var combined = SafeCombine(dir, "grok");
                if (combined is null) continue;
                var found = FirstThatExists(combined, extensions);
                if (found is not null) return found;
            }

            return null;
        }

        private static string? FirstThatExists(string basePath, string[] extensions)
        {
            foreach (var extension in extensions)
            {
                var candidate = basePath + extension;
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        [ExcludeFromCodeCoverage]
        private static string? SafeCombine(string dir, string file)
        {
            try { return System.IO.Path.Combine(dir, file); }
            catch (ArgumentException) { return null; }
        }
    }

    // Forcing a fresh Grok credits reading by briefly starting and stopping
    // the real Grok CLI, because nothing lighter exists.
    //
    // CB-85 gave Codex a live poll because `codex app-server` answers a JSON-RPC
    // request with no session and no side effects. Grok has no equivalent —
    // measured directly, not assumed: `grok models`, `doctor`, `inspect`,
    // `sessions` and `grok agent stdio` all leave `~/.grok/logs/unified.jsonl`
    // untouched. The *only* thing that writes a fresh `billing: fetched
    // credits config` line is booting the real interactive TUI, which fetches
    // it early in its own startup — before rendering, before any MCP servers
    // it manages are started, and (measured from a directory it had never seen
    // before) before any trust prompt would block it.
    //
    // That makes this a fundamentally different kind of action than every
    // other poller in this file: it starts the user's actual application,
    // however briefly, rather than asking a subprocess a question and reading
    // the answer. Everything below is sized around minimizing that: a pty
    // supplied by `script`, already part of the OS rather than an added
    // dependency; a scratch directory Grok has never seen and will not
    // remember; a short, fixed wait; and a kill of the whole process tree
    // rather than a graceful quit, so nothing has the chance to go further
    // into its own startup than the credits fetch.
    internal static class GrokUsageRefresher
    {
        // How long to hold the pty open before killing it.
        //
        // Measured at 4-6 seconds across several runs, from both a familiar
        // directory and a brand-new scratch one. Doubled for margin — a slower
        // machine, a cold cache, a Grok update that adds a step before the
        // fetch — since killing a little late costs a few seconds of CPU and
        // killing too early costs the reading entirely.
        internal static readonly TimeSpan HoldOpen = TimeSpan.FromSeconds(8);

        // The floor between refreshes.
        //
        // Not UsagePollCadence's sixty seconds to five minutes. That range
        // paces a network read that costs a subprocess of the *CLI*; this is a
        // launch of the user's actual application. Twenty minutes is three cycles an
        // hour — often enough that the number is never far from true for
        // someone glancing at the orb, rare enough that it reads as an
        // occasional background chore rather than Grok visibly starting and
        // stopping every few minutes.
        internal static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(20);

        // Whether it is time to try again — the only part of this class a test
        // can ask without spawning anything.
        //
        // Both switches are required. GrokAccountUsageEnabled is "read what
        // Grok already wrote"; GrokAutoRefreshEnabled is "start Grok to make it
        // write something new". The first is meaningless as a gate on its own
        // — an account nobody is displaying has nothing to refresh for — and
        // the second is meaningless without the first, since nothing would ever
        // read the fresher number it produces.
        internal static bool ShouldRefresh(
            DateTimeOffset now, DateTimeOffset lastRefresh,
            bool accountUsageEnabled, bool autoRefreshEnabled) =>
            accountUsageEnabled && autoRefreshEnabled && now - lastRefresh >= MinimumInterval;

        // A directory Grok has never seen and will not remember seeing.
        //
        // Fixed and reused rather than a fresh temp directory per cycle, so
        // nothing accumulates in $TMPDIR and Grok's own per-cwd session
        // bookkeeping is not asked to track a new key every twenty minutes for
        // a cwd nobody will ever open again.
        internal static string ScratchDirectory =>
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Brand.Slug}-grok-refresh");

        // The refresh itself. Excluded from coverage for the reason
        // CodexAppServerUsage.Ask is: it starts a real subprocess — here, the
        // user's actual Grok Build application — and a test cannot do that
        // without either genuinely launching it or asserting nothing real.
        //
        // Windows is a deliberate no-op rather than a guess. The pty
        // requirement is not macOS-specific — Grok needs *some* real terminal
        // on any platform — but the way to supply one differs: `script` is a
        // one-line answer here because it ships with the OS, while Windows
        // needs the ConPTY API (CreatePseudoConsole), which nothing in this
        // codebase currently wraps and which has not been verified against a
        // real Grok install on Windows. Guessing at that wiring and shipping it
        // unverified is exactly what this repo's own coverage and
        // real-machine-verification rules exist to prevent.
        [ExcludeFromCodeCoverage]
        internal static void Refresh()
        {
            if (!OperatingSystem.IsMacOS()) return;

            var grok = GrokBinary.Path;
            if (grok is null) return;

            Directory.CreateDirectory(ScratchDirectory);

            Process? process = null;
            try
            {
                var psi = new ProcessStartInfo("/usr/bin/script")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                    WorkingDirectory = ScratchDirectory
                };

                // BSD `script`, which is what macOS ships: `script -q outfile
                // command args...`. -q suppresses the "Script started/done"
                // banner it would otherwise write to the pty; /dev/null is the
                // typescript file macOS's version requires as an argument even
                // when nobody wants a transcript.
                psi.ArgumentList.Add("-q");
                psi.ArgumentList.Add("/dev/null");
                psi.ArgumentList.Add(grok);

                process = Process.Start(psi);
                if (process is null) return;

                // Drained rather than left to fill a pipe buffer and block the
                // child — the same anti-deadlock reasoning as every other
                // subprocess call in this app.
                _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
                _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);

                process.WaitForExit((int)HoldOpen.TotalMilliseconds);
            }
            catch
            {
                // Nothing to do with a failure to launch or drain: the next
                // scheduled cycle tries again, and the orb's own staleness
                // labelling is what tells the user this one did not land.
            }
            finally
            {
                if (process is not null)
                {
                    // The whole tree, not just `script` — `script`'s child is
                    // the real Grok process, and killing only the parent would
                    // leave Grok running unsupervised in the background, which
                    // is the one outcome this whole design exists to avoid.
                    try { ProcessTreeKill.Kill(process); } catch { }
                    process.Dispose();
                }
            }
        }
    }

    // The little bit of state ShouldRefresh needs to be asked on a timer
    // instead of just once.
    //
    // Split out of SessionManager rather than living there as two private
    // fields, the same reasoning AccountOrbs itself is split out for: it is
    // small enough to test directly, and SessionManager.Start() — which is
    // where the actual timer lives — is excluded from coverage because it
    // wires up a tray icon, a file watcher and a gateway connection alongside
    // it. Nothing about *when* to refresh should have to hide behind that.
    internal sealed class GrokUsageRefreshScheduler
    {
        private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

        // Advances the internal clock the moment it says yes, before the
        // caller has done anything with the answer. SessionManager's poll
        // timer ticks every two seconds and the refresh itself takes several
        // — without this, two ticks in the gap before the first refresh
        // finishes would both read `now - lastRefresh` as still past the floor
        // and both say yes, starting Grok twice at once.
        internal bool Tick(DateTimeOffset now, bool accountUsageEnabled, bool autoRefreshEnabled)
        {
            if (!GrokUsageRefresher.ShouldRefresh(
                    now, _lastRefresh, accountUsageEnabled, autoRefreshEnabled))
            {
                return false;
            }

            _lastRefresh = now;
            return true;
        }
    }
}
