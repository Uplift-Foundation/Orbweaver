using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Orbweaver
{
    // Re-running the hook installers from inside the app.
    //
    // Adding a profile in the Settings window has to *do* something. The list
    // it edits is only consulted when an installer runs, so without this an
    // added account sits there looking configured and produces no orbs until
    // the next time someone happens to re-run setup by hand — the same
    // "correctly configured, silently doing nothing" failure the installers
    // themselves go out of their way to avoid.
    //
    // Windows already had half of this in WslIntegration, which is where it
    // belonged when the feature was Windows-only. This is the other half, and
    // the dispatch, so the Settings window can stop being platform-shaped.
    internal static class HookInstaller
    {
        // How long to wait. The macOS installers do a handful of file reads and
        // one osascript per settings file, so they finish in well under a
        // second; this is a backstop against a hung osascript rather than a
        // real budget. Windows' own timeouts live in WslIntegration, which has
        // to account for a WSL VM cold-booting.
        internal const int TimeoutMs = 20_000;

        // What every installer exits with when the app's saved profile list exists
        // but could not be read or parsed. The default profile is still wired
        // first; it is the extra ones that were not. Distinct so the card can say
        // so rather than a bare "exited with code 3", and so an unreadable list
        // stops looking like an empty one (CB-258).
        internal const int SavedListUnreadableExit = 3;

        // Re-wire every CLI. Used by settings that mean the same thing to both,
        // where re-running only one leaves the other wired to an older hook and
        // an older set of flags — which is exactly how the colour setting
        // shipped broken for Codex: the toggle re-ran Claude Code's installer
        // alone, so Codex kept a hook copy without the flag and without the
        // code the flag turns on.
        //
        // Nothing here reads the results: the colour toggle has no status line
        // to put them on. Each run still lands in hook-installer.log, which is
        // what matters when one of them fails.
        // Excluded from coverage: runs both installer scripts as subprocesses.
        [ExcludeFromCodeCoverage]
        public static void ReapplyAll()
        {
            ReapplyClaudeCode();
            ReapplyCodex();
            ReapplyGrok();
        }

        // Re-wire every Claude Code account the app knows about. The result is
        // what the Settings card turns into its status line (CB-258).
        // Excluded from coverage: runs the shipped bash installer, or
        // WslIntegration's Windows equivalent.
        [ExcludeFromCodeCoverage]
        public static HookInstallResult ReapplyClaudeCode()
        {
            if (OperatingSystem.IsWindows())
            {
                // Native wiring plus every already-wired distro, which is a
                // Windows-only concern and already has a home.
                return WslIntegration.ReapplyProfiles();
            }

            return RunScript("install-macos-hooks.sh");
        }

        // Re-wire every Codex home the app knows about.
        // Excluded from coverage: runs the shipped Codex installer as a
        // subprocess.
        [ExcludeFromCodeCoverage]
        public static HookInstallResult ReapplyCodex()
        {
            if (OperatingSystem.IsWindows())
            {
                return RunPowerShell("install-codex-hooks.ps1");
            }

            return RunScript("install-codex-hooks.sh");
        }

        // Excluded from coverage: platform dispatch over the two runners below.
        [ExcludeFromCodeCoverage]
        public static HookInstallResult ReapplyGrok()
        {
            if (OperatingSystem.IsWindows())
            {
                return RunPowerShell("install-grok-hooks.ps1");
            }

            return RunScript("install-grok-hooks.sh");
        }

        // The argument lists, in one place so the app and the test that runs the
        // real installers cannot drift apart (CB-258).
        //
        // They used to carry the colour setting too, as --auto-color and
        // -AutoColor. The installers stopped accepting those on 2026-08-20, when
        // the setting moved to the .auto-color marker beside the status files
        // (SessionManager.SyncAutoColorMarker, which the hook reads and which
        // needs no re-wiring). Nobody removed the callers' half, and every
        // installer rejects an unknown option before wiring anything — exit 2,
        // "unknown option: --auto-color" — so for six weeks every reapply the app
        // ran for a user with the setting on failed, and the failure was
        // swallowed. Adding a profile in Settings is how it was found.
        internal static string[] ScriptArguments(string script) => new[] { script };

        internal static string[] PowerShellArguments(string script) =>
            new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script };

        // baseDirectory is Resolve's own seam, passed through so a test can put
        // a fake installer where the real one would be found; environment is
        // Run's, so a test can aim the real installers at a scratch HOME.
        internal static HookInstallResult RunScript(
            string name, string? baseDirectory = null,
            IReadOnlyDictionary<string, string?>? environment = null)
        {
            var script = Resolve(name, baseDirectory);
            if (script is null) return Finish(name, HookInstallResult.NotFound(name));

            return Run("/bin/bash", ScriptArguments(script), name, environment: environment);
        }

        // Excluded from coverage: the only line that is not Resolve, Run or
        // PowerShellArguments names Windows PowerShell's exe, which only Windows
        // has. The argument list and the not-found arm are the code RunScript's
        // tests already run, and the Windows leg runs the real installers through
        // this.
        [ExcludeFromCodeCoverage]
        internal static HookInstallResult RunPowerShell(
            string name, string? baseDirectory = null,
            IReadOnlyDictionary<string, string?>? environment = null)
        {
            var script = Resolve(name, baseDirectory);
            if (script is null) return Finish(name, HookInstallResult.NotFound(name));

            return Run(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                PowerShellArguments(script), name, environment: environment);
        }

        // Where the installers live, in both layouts this app runs from.
        //
        // Inside the .app they sit in Contents/Resources, beside the hook
        // script; AppContext.BaseDirectory is Contents/MacOS, so Resources is
        // its sibling. From a source build there is no bundle, so this walks up
        // looking for the repo's tools/ — the same two-layout resolution
        // WslIntegration does for its own script, and the same order: installed
        // wins, because a stale clone next to an installed app should not be
        // what runs.
        //
        // baseDirectory is a parameter with the real one as its default, so the
        // order below can be asserted against a temp directory. The order is the
        // part worth asserting rather than the file reads: "installed wins" is a
        // decision, and the failure it prevents — a stale clone next to an
        // installed app being what actually runs — is silent, because both
        // scripts exist and both appear to work.
        internal static string? Resolve(string name, string? baseDirectory = null)
        {
            baseDirectory ??= AppContext.BaseDirectory;

            var resources = Path.Combine(baseDirectory, "..", "Resources", name);
            if (File.Exists(resources)) return Path.GetFullPath(resources);

            var alongside = Path.Combine(baseDirectory, "tools", name);
            if (File.Exists(alongside)) return alongside;

            var dir = new DirectoryInfo(baseDirectory);
            for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tools", name);
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        // What a finished run is called in the log, and what comes back.
        internal static HookInstallResult Finish(string label, HookInstallResult result)
        {
            HookInstallerLog.Record(label, result);
            return result;
        }

        // Runs one installer and keeps what it said. See HookInstallerLog for why
        // this stopped swallowing output: the installers are chatty by design,
        // and there is still nowhere in the Settings window to put a page of it,
        // so the card gets one line (StatusMessage) and the log gets the page.
        //
        // environment overrides the child's variables; a null value removes one.
        // It exists so a test can point the real installers at a scratch HOME and
        // ORBWEAVER_SETTINGS_DIR without touching the test process's own.
        internal static HookInstallResult Run(
            string file, string[] arguments, string label,
            int timeoutMs = TimeoutMs,
            IReadOnlyDictionary<string, string?>? environment = null)
        {
            try
            {
                var psi = new ProcessStartInfo(file)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                foreach (var argument in arguments) psi.ArgumentList.Add(argument);
                foreach (var (key, value) in environment ?? new Dictionary<string, string?>())
                {
                    if (value is null) psi.Environment.Remove(key);
                    else psi.Environment[key] = value;
                }

                // Null only when an existing process was reused, which a fresh
                // ProcessStartInfo never does; thrown into the catch below so
                // there is one place that turns a failure to start into a result.
                using var process = Process.Start(psi)
                    ?? throw new InvalidOperationException($"{file} could not be started.");

                // Both pipes drained concurrently and only then waited on: a
                // child that fills one pipe blocks on the write, and reading the
                // other first (what this did before) would sit there until the
                // timeout on a run that was otherwise fine.
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(timeoutMs))
                {
                    try { ProcessTreeKill.Kill(process); } catch { }

                    // Whatever it had said by then is the best clue to where it
                    // hung; the kill closes the pipes, so this is a short wait.
                    Task.WaitAll(new Task[] { output, error }, 2_000);
                    return Finish(label, new HookInstallResult(
                        HookInstallOutcome.TimedOut, label,
                        Output: output.IsCompletedSuccessfully ? output.Result : "",
                        Error: error.IsCompletedSuccessfully ? error.Result : ""));
                }

                var code = process.ExitCode;
                return Finish(label, new HookInstallResult(
                    code == 0 ? HookInstallOutcome.Ok : HookInstallOutcome.Failed,
                    label, code, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult()));
            }
            catch (Exception ex)
            {
                return Finish(label, new HookInstallResult(
                    HookInstallOutcome.Threw, label, Error: ex.Message));
            }
        }

        // The one line the Settings card shows.
        //
        // Pure, and separate from the card that displays it, because every branch
        // is a decision about what a person is told: success, and four different
        // reasons it did not happen, each needing different next steps.
        //
        // wired is the card's own check of the profile's settings.json after a run
        // that exited 0 (see IsWired). Exit 0 alone is not proof — the installer
        // exits 0 having wired nothing extra when it could not read the saved
        // list, which is the failure CB-258 could not rule out — so null means
        // "not checked" and false means "checked and the hooks are not there".
        internal static string StatusMessage(HookInstallResult result, string profileName, bool? wired = null)
        {
            if (result.Outcome == HookInstallOutcome.Ok)
            {
                return wired == false
                    ? $"The installer ran, but {profileName} still has no {Brand.DisplayName} hooks. Details: {HookInstallerLog.Path_}"
                    : $"Wired hooks into {profileName}.";
            }

            var reason = result.Outcome switch
            {
                HookInstallOutcome.ScriptNotFound => $"the installer {result.Script} was not found",
                HookInstallOutcome.TimedOut => $"the installer did not finish within {TimeoutMs / 1000} seconds",
                HookInstallOutcome.Failed when result.ExitCode == SavedListUnreadableExit =>
                    "the saved profile list could not be read, so no extra profiles were wired"
                    + Detail(result.Error),
                HookInstallOutcome.Failed => $"the installer exited with code {result.ExitCode}"
                    + Detail(result.Error),
                _ => result.Error
            };

            return $"Couldn't wire hooks into {profileName}: {reason}. Details: {HookInstallerLog.Path_}";
        }

        // " (last stderr line)", or nothing when stderr said nothing. A method
        // rather than a pattern-match in each arm above, whose null case could
        // never happen and so could never be covered.
        private static string Detail(string error) =>
            LastLine(error) is var line && line.Length > 0 ? $" ({line})" : "";

        // The last non-blank line of an installer's stderr, which is where a
        // script that dies under set -e says what it died on.
        private static string LastLine(string text)
        {
            var line = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
            return line.Length <= 160 ? line : line[..160] + "…";
        }

        // Whether a Claude Code profile's settings.json carries our hook entries.
        // The marker is the hook script's own name, which is what the installers
        // themselves strip and re-add. homeDirectory is a parameter so a test can
        // point at a scratch directory.
        //
        // Either name, on either platform (CB-256). This used to look only for
        // ClaudeBuddyHook, so from phase 2 on a profile the installer had just
        // wired — which names OrbweaverHook and strips the old script — read as
        // un-wired, and the Settings card said the install had failed. Not
        // WslIntegration.SettingsTextMentionsHook, although it also knows both
        // names: that one is for a WSL distro and counts only the .ps1 on
        // purpose, while a native profile on a Mac carries the .sh.
        //
        // On Windows a profile can live in the native home, in a WSL distro's home
        // (as \\wsl.localhost\... UNC paths), or in both, and one saved name can be
        // WSL-only with no native directory at all; so "wired" means wired in any
        // of them. Checking only the native home would report a perfectly wired
        // WSL-only profile as unwired. Off Windows the WSL list is empty.
        internal static bool IsWired(string profileName) =>
            IsWiredInAny(profileName,
                new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }
                    .Concat(WslIntegration.GetWslHomeUncPaths()));

        internal static bool IsWiredInAny(string profileName, IEnumerable<string> homeDirectories) =>
            homeDirectories.Any(home => IsWiredIn(profileName, home));

        internal static bool IsWiredIn(string profileName, string homeDirectory)
        {
            try
            {
                var path = Path.Combine(homeDirectory, profileName, "settings.json");
                return File.Exists(path) && SettingsTextMentionsHook(File.ReadAllText(path));
            }
            catch
            {
                return false;
            }
        }

        private static readonly string[] HookScriptNames =
        [
            Brand.HookScriptShell, Brand.HookScriptPowerShell,
            Brand.Legacy.HookScriptShell, Brand.Legacy.HookScriptPowerShell,
        ];

        internal static bool SettingsTextMentionsHook(string text) =>
            HookScriptNames.Any(name => text.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    internal enum HookInstallOutcome { Ok, ScriptNotFound, Failed, TimedOut, Threw }

    // Everything one installer run produced, for the card's status line
    // (HookInstaller.StatusMessage) and the log (HookInstallerLog).
    internal sealed record HookInstallResult(
        HookInstallOutcome Outcome, string Script, int? ExitCode = null,
        string Output = "", string Error = "")
    {
        internal static HookInstallResult NotFound(string script) =>
            new(HookInstallOutcome.ScriptNotFound, script);
    }
}
