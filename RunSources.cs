using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Orbweaver
{
    // CB-404: where RunControlsHost's readings and catalogues come from on a real
    // desk — what each session is running, what it could run, and which Codex it
    // is — composed from RunReadings' pure parsers over the files this app
    // already tails.
    //
    // **Everything answers from cache.** The mirror asks on its peer pump while a
    // HELLO is answered, and one slow read there froze the whole link once
    // (CB-306's second reopen). So a file is parsed again only when its length
    // or write time moved, and anything that needs a process (Claude's
    // list_models, Codex's model/list, `codex --version`) is started in the
    // background and answered from whatever it last said. A first ask for a
    // Claude catalogue gets the four aliases until the probe lands; a Codex one
    // gets nothing, and the model list shows only the running model.
    internal sealed class RunSources
    {
        internal sealed record Seams(
            // A file's length and last write, or null when it is not there.
            Func<string, (long Length, DateTime Written)?> Stamp,
            Func<string, string[]> Tail,
            Func<string, string?> FirstLine,
            Func<string, string?> ReadAll,

            // Claude Code's list_models answer for a config root (null: the
            // default), as the control_response line.
            Func<string?, Task<string?>> ClaudeModels,

            // Codex app-server's model/list result for a CODEX_HOME (null: the default).
            Func<string?, Task<string?>> CodexModels,

            // Before a Codex session's first turn: the executable its process
            // is running (null when it cannot be resolved), and that exact
            // file's `--version`. Never PATH's codex, which can be a different
            // version from the one in the pane.
            Func<int, string?> ExecutableOf,
            Func<string, Task<Version?>> VersionOf,

            Func<string> Home,
            Func<DateTimeOffset> Now);

        // A probe that answered is asked again after an hour (a new model
        // appears rarely); one that failed after five minutes (a CLI mid-upgrade
        // should not cost the list for the rest of the day).
        internal static readonly TimeSpan FreshFor = TimeSpan.FromHours(1);
        internal static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

        // Measured on every model list_models returned at 2.1.330.
        internal static readonly IReadOnlyList<string> ClaudeLevels = ["low", "medium", "high", "xhigh", "max"];

        // Until list_models answers: Claude Code's own aliases, which /model and
        // set_model both take (plan section 3).
        internal static readonly RunCatalogue ClaudeFallback = new([
            new RunModel("default", "Default", null, ClaudeLevels),
            new RunModel("opus", "Opus", null, ClaudeLevels),
            new RunModel("sonnet", "Sonnet", null, ClaudeLevels),
            new RunModel("haiku", "Haiku", null, ClaudeLevels),
        ]);

        private readonly Seams _seams;
        private readonly ConcurrentDictionary<string, ((long, DateTime) Stamp, object? Value)> _files = new();
        private readonly ConcurrentDictionary<string, Probe> _probes = new();
        private readonly ConcurrentDictionary<string, Version> _codexVersions = new();

        private sealed class Probe
        {
            public RunCatalogue? Value;
            public DateTimeOffset At;
            public bool Ok, Running;
        }

        internal RunSources(Seams seams) => _seams = seams;

        // ---- readings -------------------------------------------------------------

        internal RunReading? ReadingFor(SessionStatus status)
        {
            var path = status.TranscriptPath;
            if (string.IsNullOrEmpty(path)) return null;
            return status.Source switch
            {
                SessionSource.ClaudeCode => Parsed(path, p => RunReadings.FromClaudeTranscript(_seams.Tail(p))),
                SessionSource.Codex => Parsed(path, p => RunReadings.FromCodexRollout(_seams.Tail(p)).Reading),
                SessionSource.Grok => GrokSummaryOf(path) is { } summary
                    ? Parsed(summary, p => RunReadings.FromGrokSummary(_seams.ReadAll(p)))
                    : null,
                _ => null,
            };
        }

        // Grok keeps summary.json beside the updates.jsonl the hook names.
        internal static string? GrokSummaryOf(string transcriptPath) =>
            Path.GetDirectoryName(transcriptPath) is { Length: > 0 } dir ? Path.Combine(dir, "summary.json") : null;

        // Parsed again only when the file's length or write time moved.
        private T? Parsed<T>(string path, Func<string, T> parse) where T : class
        {
            if (_seams.Stamp(path) is not { } stamp) return null;
            if (_files.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Value as T;
            var value = parse(path);
            _files[path] = (stamp, value);
            return value;
        }

        // ---- the Codex version ----------------------------------------------------

        private readonly ConcurrentDictionary<(string Session, int Pid), string> _executables = new();
        private readonly ConcurrentDictionary<string, Probe> _versionProbes = new();
        private readonly ConcurrentDictionary<string, Version> _executableVersions = new();

        // The rollout's session_meta when there is one (its first line, which
        // never changes once written). Before the first turn there is no
        // rollout, so the version is asked of the executable the session's own
        // process is running, in the background. PATH's codex is never used: a
        // 0.164+ on PATH beside an older one in the pane would send the typed
        // form into the old picker. Null, which the host shows as "checking
        // Codex version…" with nothing typed, until one of those answers or
        // when the process cannot be resolved at all.
        internal Version? CodexVersionFor(SessionStatus status)
        {
            if (status.Source != SessionSource.Codex) return null;
            var path = status.TranscriptPath;
            if (!string.IsNullOrEmpty(path))
            {
                if (_codexVersions.TryGetValue(path, out var known)) return known;
                if (_seams.FirstLine(path) is { } head
                    && RunReadings.FromCodexRollout([head]).CliVersion is { } v)
                    return _codexVersions[path] = v;
            }

            if (status.SessionPid <= 0) return null;
            // Only a resolved image is kept: the OS can miss a process briefly (a
            // read that races its start), and a miss cached would leave the
            // controls off for the life of the session.
            var key = (status.SessionId, status.SessionPid);
            if (!_executables.TryGetValue(key, out var exe))
            {
                exe = CodexExecutable(_seams.ExecutableOf(status.SessionPid));
                if (exe is null) return null;
                _executables[key] = exe;
            }
            StartVersionProbe(exe);
            return _executableVersions.TryGetValue(exe, out var version) ? version : null;
        }

        // Only a file named codex counts: the hook records the native codex
        // process, and anything else (a node launcher, a shell) would answer
        // `--version` about itself.
        // Split on both separators by hand, so a Windows image path reads the same on either OS.
        internal static string? CodexExecutable(string? path)
        {
            if (path is null) return null;
            var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
            return name.Equals("codex", StringComparison.OrdinalIgnoreCase)
                || name.Equals("codex.exe", StringComparison.OrdinalIgnoreCase) ? path : null;
        }

        private void StartVersionProbe(string exe)
        {
            var p = _versionProbes.GetOrAdd(exe, _ => new Probe());
            lock (_probes)
            {
                if (!Due(p)) return;
                p.Running = true;
            }
            LastProbe = VersionAsync(p, exe);
        }

        // The last background ask started, so a test can wait on it instead of
        // sleeping. Nothing in production reads it.
        internal Task LastProbe { get; private set; } = Task.CompletedTask;

        private async Task VersionAsync(Probe p, string exe)
        {
            Version? v = null;
            try { v = await _seams.VersionOf(exe).ConfigureAwait(false); }
            catch { }
            lock (_probes)
            {
                if (v is not null) _executableVersions[exe] = v;
                Settle(p, ok: v is not null);
            }
        }

        // `codex --version` prints "codex-cli 0.154.0".
        private static readonly Regex CliVersion = new(@"(\d+)\.(\d+)\.(\d+)", RegexOptions.Compiled);

        internal static Version? ParseCliVersion(string? text) =>
            text is not null && CliVersion.Match(text) is { Success: true } m
                ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))
                : null;

        // ---- catalogues -----------------------------------------------------------

        internal RunCatalogue? CatalogueFor(SessionStatus status)
        {
            switch (status.Source)
            {
                case SessionSource.ClaudeCode:
                    var root = ClaudeConfigRoots.RootOfTranscript(status.TranscriptPath);
                    return Probed("claude:" + root, () => _seams.ClaudeModels(root), RunReadings.ClaudeModelList)
                        ?? ClaudeFallback;

                case SessionSource.Codex:
                    var home = CodexHomeOf(status.TranscriptPath);
                    return Probed("codex:" + home, () => _seams.CodexModels(home), RunReadings.CodexModelList);

                case SessionSource.Grok:
                    return Parsed(Path.Combine(_seams.Home(), ".grok", "models_cache.json"),
                        p => RunReadings.GrokModelsCache(_seams.ReadAll(p)));

                default:
                    return null;
            }
        }

        // A rollout lives at <CODEX_HOME>/sessions/yyyy/mm/dd/rollout-….jsonl.
        // Null (the default home) when the path says nothing.
        internal static string? CodexHomeOf(string? rolloutPath)
        {
            for (var dir = string.IsNullOrEmpty(rolloutPath) ? null : Path.GetDirectoryName(rolloutPath);
                 !string.IsNullOrEmpty(dir);
                 dir = Path.GetDirectoryName(dir))
            {
                if (Path.GetFileName(dir) == "sessions") return Path.GetDirectoryName(dir);
            }
            return null;
        }

        // The last answer, starting a background ask when none is running and
        // the last is stale or failed. An empty catalogue is a failed ask.
        private RunCatalogue? Probed(string key, Func<Task<string?>> ask, Func<string?, RunCatalogue> parse)
        {
            var p = _probes.GetOrAdd(key, _ => new Probe());
            bool start;
            lock (_probes)
            {
                start = Due(p);
                if (start) p.Running = true;
            }
            if (start) LastProbe = AskAsync(p, ask, parse);
            lock (_probes) return p.Value;
        }

        private async Task AskAsync(Probe p, Func<Task<string?>> ask, Func<string?, RunCatalogue> parse)
        {
            RunCatalogue? catalogue = null;
            try { catalogue = parse(await ask().ConfigureAwait(false)); }
            catch { }
            var ok = catalogue is { Models.Count: > 0 };
            lock (_probes)
            {
                if (ok) p.Value = catalogue;
                Settle(p, ok);
            }
        }

        // Under the lock.
        private bool Due(Probe p) =>
            !p.Running && (p.At == default || _seams.Now() - p.At > (p.Ok ? FreshFor : RetryAfter));

        private void Settle(Probe p, bool ok)
        {
            p.Running = false;
            p.Ok = ok;
            p.At = _seams.Now();
        }
    }

    // The real disk and the real CLIs. Excluded from coverage: each seam is one
    // call into a file or a process; RunSources itself is driven with fakes.
    [ExcludeFromCodeCoverage]
    internal static class RunSourcesLive
    {
        // Set by every test assembly's TestBootstrap in a [ModuleInitializer],
        // before any test can construct a session and read its run options:
        // the probes below start real `claude -p`, `codex app-server` and
        // `codex --version` processes, and a suite must never launch one. The
        // TextToSpeech.SilenceForTests pattern: a guard at the boundary, not a
        // seam each caller has to remember. Production never sets it.
        internal static bool ProbesOffForTests;

        internal static readonly RunSources Shared = new(new RunSources.Seams(
            path =>
            {
                try
                {
                    var f = new FileInfo(path);
                    return f.Exists ? (f.Length, f.LastWriteTimeUtc) : null;
                }
                catch { return null; }
            },
            TranscriptReader.TailLines,
            path =>
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs);
                    return reader.ReadLine();
                }
                catch { return null; }
            },
            path =>
            {
                try { return File.ReadAllText(path); }
                catch { return null; }
            },
            root => ProbesOffForTests ? Task.FromResult<string?>(null) : Task.Run(() => ClaudeListModels(root)),
            home => ProbesOffForTests ? Task.FromResult<string?>(null) : Task.Run(() => CodexBinary.Path is { } codex
                ? CodexAppServerUsage.Ask(codex, home, CodexAppServerUsage.ModelListRequest)
                : null),
            pid => ProbesOffForTests ? null : ExecutableOf(pid),
            exe => ProbesOffForTests ? Task.FromResult<Version?>(null) : Task.Run(() => RunSources.ParseCliVersion(VersionText(exe))),
            () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            () => DateTimeOffset.UtcNow));

        internal const string ListModelsRequest =
            "{\"type\":\"control_request\",\"request_id\":\"cb404-models\",\"request\":{\"subtype\":\"list_models\"}}";

        // A throwaway `claude -p` on stream-json, hooks off and no turn, so it
        // spends no tokens and draws no orb. Measured at 2.1.330: list_models
        // answers a control_response carrying the models, and an unknown subtype
        // answers `error` "Unsupported control request subtype". stdin is left
        // open until the answer arrives, and the process is killed after it.
        private static string? ClaudeListModels(string? configRoot)
        {
            if (ClaudeBinary.Path is not { } claude) return null;
            Process? process = null;
            try
            {
                var psi = new ProcessStartInfo(claude)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetTempPath(),
                };
                foreach (var a in new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json",
                             "--verbose", "--settings", "{\"disableAllHooks\":true}" })
                    psi.ArgumentList.Add(a);
                if (configRoot is not null) psi.Environment["CLAUDE_CONFIG_DIR"] = configRoot;

                process = Process.Start(psi);
                if (process is null) return null;
                _ = process.StandardError.ReadToEndAsync();
                process.StandardInput.WriteLine(ListModelsRequest);
                process.StandardInput.Flush();

                var reader = Task.Run(() =>
                {
                    string? line;
                    while ((line = process.StandardOutput.ReadLine()) is not null)
                        if (line.Contains("\"control_response\"") && line.Contains("cb404-models")) return line;
                    return null;
                });
                return reader.Wait(20000) ? reader.Result : null;
            }
            catch { return null; }
            finally
            {
                if (process is not null)
                {
                    try { ProcessTreeKill.Kill(process); } catch { }
                    process.Dispose();
                }
            }
        }

        // The process's own image, through the OS (proc_pidpath on macOS, the
        // process image on Windows, /proc/<pid>/exe on Linux), never by name.
        internal static string? ExecutableOf(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return p.MainModule?.FileName;
            }
            catch { return null; }
        }

        internal static string? VersionText(string exe)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, "--version")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                });
                if (p is null) return null;
                var text = p.StandardOutput.ReadToEndAsync();
                return p.WaitForExit(10000) ? text.Result : null;
            }
            catch { return null; }
        }
    }
}
