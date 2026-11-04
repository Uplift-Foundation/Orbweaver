using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Orbweaver
{
    // Where the `codex` CLI is, for the parts of this app that shell out to it.
    //
    // Same problem and same answer as ClaudeBinary, whose comment has the full
    // story: launched from Finder or as a login item this app gets the bare
    // system PATH, and handing the command to the user's shell does not fix it
    // because `zsh -lc` never reads .zshrc. So the known install locations are
    // checked directly and PATH is consulted last.
    //
    // Two differences from ClaudeBinary, both because Codex installs
    // differently. The standalone package is the usual install here and puts a
    // symlink in ~/.local/bin pointing into ~/.codex/packages — both are listed,
    // since the symlink is what a user creates and the target is what survives
    // a broken one. And the PATH search tries executable extensions, because on
    // Windows an npm-installed CLI is `codex.cmd` and a bare "codex" exists
    // nowhere on disk; ClaudeBinary does not do this, which is worth its own
    // ticket rather than a silent change here.
    internal static class CodexBinary
    {
        private static readonly object Gate = new();
        private static bool _looked;
        private static string? _path;

        // Null when nothing was found. Every caller treats that as "skip the
        // live read" rather than an error — the rollout fallback still works on
        // a machine where Codex is installed somewhere unusual.
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
            "/opt/homebrew/bin/codex",
            "/usr/local/bin/codex",
            "/usr/bin/codex"
        };

        // Windows resolves a bare name through PATHEXT; every other platform has
        // exactly one form. The empty string first so a real extensionless
        // binary still wins on a Windows machine that has one.
        internal static readonly string[] WindowsExtensions = { "", ".exe", ".cmd", ".bat" };
        internal static readonly string[] UnixExtensions = { "" };

        private static string[] DefaultExtensions =>
            OperatingSystem.IsWindows() ? WindowsExtensions : UnixExtensions;

        // Every input is a parameter with the real one as its default, for the
        // reason ClaudeBinary.Locate spells out: a test that controlled only
        // `home` would find a real `codex` on this developer's Mac and nothing
        // on a CI runner — one test passing for two different reasons.
        // `extensions` is a parameter for a reason the other three are not: it is
        // the only input whose real value depends on the OS the test is running
        // on, so without it the Windows behaviour — where a bare "codex" exists
        // nowhere and `codex.cmd` is the whole answer — could only ever be
        // exercised by the Windows CI leg, which is a slow way to find out you
        // got it wrong.
        internal static string? Locate(
            string? home = null, string? searchPath = null, string[]? systemInstalls = null,
            string[]? extensions = null)
        {
            home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            extensions ??= DefaultExtensions;

            string[] candidates =
            [
                System.IO.Path.Combine(home, ".local", "bin", "codex"),
                System.IO.Path.Combine(
                    home, ".codex", "packages", "standalone", "current", "bin", "codex"),
                .. systemInstalls ?? SystemInstalls
            ];

            foreach (var candidate in candidates)
            {
                var found = FirstThatExists(candidate, extensions);
                if (found is not null) return found;
            }

            var path = searchPath ?? Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return null;

            // Path.PathSeparator rather than ':' — a Windows PATH entry contains
            // a colon, and splitting on one turns "C:/bin;C:/tools" into three
            // strings that are not directories. ClaudeBinary's comment records
            // the CI leg that caught exactly this.
            foreach (var dir in path.Split(
                         System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var combined = SafeCombine(dir, "codex");
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

        // A PATH entry is the user's shell config rather than anything this app
        // controls, so a malformed one steps aside instead of throwing.
        //
        // Excluded from coverage for the reason ClaudeBinary.SafeCombine states
        // at length: the catch is not reachable on .NET 10, because .NET Core
        // dropped Path.Combine's invalid-character check — a PATH entry
        // containing a NUL now combines happily and simply fails File.Exists.
        // AMalformedPathEntryIsSteppedOver asserts the outcome (the good
        // directory after it still answers) and reaches it without this catch.
        // Kept anyway: the runtime's behaviour here has changed once already.
        [ExcludeFromCodeCoverage]
        private static string? SafeCombine(string dir, string file)
        {
            try { return System.IO.Path.Combine(dir, file); }
            catch (ArgumentException) { return null; }
        }
    }

    // Asking Codex for its current rate limits over the app-server protocol.
    //
    // **This is a live read, and that is the whole point of it.** Until CB-85
    // the Codex orb wore whatever the last session happened to have written into
    // a rollout, which on a machine that has not run Codex since the morning is
    // a number hours old — CB-83 made the orb admit that rather than fix it.
    // `account/rateLimits/read` answers now, with no session, no model call, and
    // without this app going anywhere near `auth.json`. Measured on codex-cli
    // 0.151.0, 2 Sep 2026: it returned 100% of the five-hour window while the
    // newest snapshot on disk still read 99% from three hours earlier.
    //
    // The rollout read stays as the fallback rather than being deleted. Codex
    // may not be installed where this can find it, the protocol is marked
    // experimental in `codex app-server --help`, and a stale number honestly
    // labelled is much better than no orb at all.
    internal static class CodexAppServerUsage
    {
        // Generous for a call measured under a second, for the same reason
        // UsagePoller's is: the machines slowest to answer are the ones whose
        // usage the user most wants to see, and this runs on a five-minute
        // timer where a few extra seconds cost nothing.
        internal const int TimeoutMs = 15000;

        // The id the response is matched on. A constant rather than a literal
        // in three places because the matching is the only thing that makes the
        // response findable in a stream that also carries notifications.
        internal const int RequestId = 2;

        internal const string InitializeRequest =
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":" +
            $"{{\"name\":\"{Brand.Slug}\",\"title\":\"{Brand.DisplayName}\",\"version\":\"1\"}}}}}}";

        internal const string RateLimitsRequest =
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"account/rateLimits/read\",\"params\":null}";

        // The one line worth reading out of the stream.
        //
        // Newline-delimited JSON carrying responses *and* notifications, so this
        // scans for the id rather than assuming a position — the same shape, and
        // the same reasoning, as UsageParse.FromStream. A line that will not
        // parse is skipped rather than failing the read, because the stream is
        // an experimental protocol that will grow rows this app has never seen.
        //
        // An error response is not a reading. It comes back as `error` with no
        // `result`, and treating it as an empty reading would draw a confident
        // zero for an account nobody managed to ask.
        internal static string? ResultFrom(string? stdout)
        {
            if (string.IsNullOrWhiteSpace(stdout)) return null;

            foreach (var line in stdout.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] != '{') continue;

                // Cheap reject before parsing: most rows are notifications, and
                // parsing every one to find that out is work for nothing.
                if (!trimmed.Contains("\"result\"", StringComparison.Ordinal)) continue;

                try
                {
                    // No ValueKind guard on the root: the line was already
                    // required to start with '{', and a JSON text that starts
                    // with '{' and parses is an object by construction. A check
                    // for it reads as prudent and is dead — one the tests could
                    // never reach, which is how it was found.
                    using var document = JsonDocument.Parse(trimmed);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("id", out var id)
                        || id.ValueKind != JsonValueKind.Number
                        || !id.TryGetInt32(out var value)
                        || value != RequestId)
                    {
                        continue;
                    }

                    if (!root.TryGetProperty("result", out var result)
                        || result.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    return result.GetRawText();
                }
                catch (JsonException)
                {
                    continue;
                }
            }

            return null;
        }

        // One account's answer, as the raw `result` object.
        //
        // Excluded from coverage: starts `codex app-server` as a real
        // subprocess. What is excluded is the launch, the timeout and the kill;
        // the JSON it prints is parsed by ResultFrom and CodexUsageParse, both
        // covered against payloads captured from a real run. Same split, same
        // reason, as UsagePoller.RunOne and BackgroundJobs.ReadOne.
        //
        // **stdin is deliberately left open.** UsagePoller closes it so the CLI
        // knows no more requests are coming and exits, which is right there and
        // wrong here: `codex app-server` treats a closed stdin as shutdown and
        // exits *before* answering. Measured — the first version of this printed
        // nothing at all. So the requests go out, the response is read, and the
        // process is killed rather than asked to leave.
        [ExcludeFromCodeCoverage]
        internal static string? Ask(string codex, string? codexHome)
        {
            Process? process = null;
            try
            {
                var psi = new ProcessStartInfo(codex)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                psi.ArgumentList.Add("app-server");

                // Which account to answer about. Codex reads this the way Claude
                // Code reads CLAUDE_CONFIG_DIR, and it is the only reason a
                // second Codex account can be polled at all.
                if (codexHome is not null) psi.Environment["CODEX_HOME"] = codexHome;

                process = Process.Start(psi);
                if (process is null) return null;

                // Drained on its own thread before anything blocks: an undrained
                // stderr can deadlock a chatty child, which is the hazard
                // BackgroundJobs.ReadOne documents and this process shares.
                var stderr = process.StandardError.ReadToEndAsync();

                process.StandardInput.WriteLine(InitializeRequest);
                process.StandardInput.WriteLine(RateLimitsRequest);
                process.StandardInput.Flush();

                var reader = Task.Run(() =>
                {
                    string? line;
                    while ((line = process.StandardOutput.ReadLine()) is not null)
                    {
                        var result = ResultFrom(line);
                        if (result is not null) return result;
                    }

                    return null;
                });

                return reader.Wait(TimeoutMs) ? reader.Result : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (process is not null)
                {
                    try { ProcessTreeKill.Kill(process); } catch { }
                    process.Dispose();
                }
            }
        }
    }
}
