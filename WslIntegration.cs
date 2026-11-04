using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Orbweaver
{
    // Backs the Settings window's "WSL integration" section: list a machine's
    // WSL distros, tell whether each one's Claude Code already has Claude
    // Buddy's hooks wired up, and flip that on/off. The PowerShell twin of
    // this logic (tools/install-windows-hooks.ps1's -Wsl/-UninstallWsl) is the
    // one actually doing the work here — this class shells out to it rather
    // than reimplementing the settings.json merge, so there is exactly one
    // place that knows how to edit that file safely.
    [SupportedOSPlatform("windows")]
    internal static class WslIntegration
    {
        private const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

        // Registry-only, no subprocess — same source `wsl.exe -l` itself reads
        // from, and the same technique the PowerShell script's Get-WslDistros
        // uses, so both surfaces agree on what a "distro" is. Called once per
        // Settings-window open; unlike WindowsAppLookup's polled AUMID lookup,
        // this doesn't need a cache.
        // Excluded from coverage: reads the Lxss registry key, which only exists
        // on a Windows host with WSL installed.
        [ExcludeFromCodeCoverage]
        public static IReadOnlyList<string> ListDistros()
        {
            if (!OperatingSystem.IsWindows()) return Array.Empty<string>();

            try
            {
                using var lxss = Registry.CurrentUser.OpenSubKey(LxssKey);
                if (lxss is null) return Array.Empty<string>();

                var names = new List<string>();
                foreach (var subKeyName in lxss.GetSubKeyNames())
                {
                    using var entry = lxss.OpenSubKey(subKeyName);
                    if (entry?.GetValue("DistributionName") is string name
                        && !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
                    {
                        names.Add(name);
                    }
                }

                names.Sort(StringComparer.OrdinalIgnoreCase);
                return names;
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        // "Wired" means the same thing here as it does to the PowerShell side:
        // the settings file's text mentions ClaudeBuddyHook.ps1. Matching that
        // exact definition (rather than, say, re-parsing the JSON and checking
        // structure) means the two surfaces can never disagree about whether a
        // distro is wired. Plain file I/O — no subprocess call needed just to
        // check status. profileDirName defaults to the standard '.claude';
        // any CLAUDE_CONFIG_DIR-style name (see OrbweaverSettings.
        // ClaudeCodeProfileDirs) works the same way.
        // Excluded from coverage: resolves a path through the registry and reads a
        // file inside a live distro; what it looks for in that text is
        // SettingsTextMentionsHook, which is tested.
        [ExcludeFromCodeCoverage]
        public static bool IsWired(string distro, string profileDirName = ".claude")
        {
            if (!OperatingSystem.IsWindows()) return false;

            var path = ResolveSettingsPath(distro, profileDirName);
            if (path is null || !File.Exists(path)) return false;

            try
            {
                return SettingsTextMentionsHook(File.ReadAllText(path));
            }
            catch
            {
                return false;
            }
        }

        // "Wired" means the same thing here as it does to the PowerShell side:
        // the settings file's text mentions the hook script. Split out of
        // IsWired so the definition can be asserted without a WSL distro — the
        // whole reason it is a text match rather than a JSON structure check is
        // that the two surfaces must never disagree about it, and a definition
        // two implementations share is worth a test of its own.
        //
        // Either script name counts since CB-255 (§1): a distro an
        // un-upgraded installer wired still names ClaudeBuddyHook.ps1, its
        // hooks still fire, and calling it un-wired would show a toggle that
        // lies. It is also what makes ReapplyProfiles re-wire such a distro to
        // the new script on the next reapply, since it only touches distros
        // that read as wired. The installers strip on the same two substrings.
        internal static bool SettingsTextMentionsHook(string text) =>
            text.Contains(Brand.HookScriptPowerShell, StringComparison.OrdinalIgnoreCase)
            || text.Contains(Brand.Legacy.HookScriptPowerShell, StringComparison.OrdinalIgnoreCase);

        // Enable or disable hooks for one distro by shelling out to the
        // shipped installer script — same script the installer itself runs,
        // so there's one JSON-merge implementation, not two. Deliberately
        // passes -Force: a distro the user explicitly toggled in a Settings
        // window is a different trust level than the installer's bulk -Wsl
        // (which skips distros where Claude Code wasn't detected, a safer
        // default for an unattended install nobody explicitly asked to touch
        // WSL for at all).
        // Excluded from coverage: runs the shipped PowerShell installer script
        // against a real distro.
        [ExcludeFromCodeCoverage]
        public static bool SetWired(string distro, bool wired)
        {
            if (!OperatingSystem.IsWindows()) return false;

            var script = ResolveInstallerScriptPath();
            if (script is null) return false;

            var args = wired
                ? new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Wsl", "-Force", "-WslDistro", distro }
                : new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-UninstallWsl", "-WslDistro", distro };

            // Generous outer timeout: the script pays the WSL2 VM's cold-boot
            // cost once, up front, capped at 20s (a real cost, not an edge
            // case — the VM shuts down after inactivity, so a toggle right
            // after opening Settings for the first time in a while can
            // genuinely hit this), plus one ~10s per-distro call — which may
            // itself start up to three nested interactive shells trying to
            // resolve PATH (see Get-WslDistroInfo in
            // install-windows-hooks.ps1) — plus PowerShell startup overhead.
            // This must comfortably clear that inner bound rather than race
            // it.
            return TryRun(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", 45_000, args);
        }

        // Call after OrbweaverSettings.Add/RemoveClaudeCodeProfileDir, so an
        // edit to the extra-profile list takes effect immediately rather than
        // silently doing nothing until every already-wired surface is
        // manually re-toggled — matching this window's "changes apply
        // immediately, no OK/Cancel" philosophy. A bare script run always
        // wires native Windows' default profile plus whatever the app has
        // saved (see -ProfileDir's default in the script), so that alone
        // covers native; each currently-wired WSL distro is re-run through
        // SetWired for the same reason, but only distros already opted in —
        // this never wires a distro nobody asked about.
        // Excluded from coverage: runs the installer script for every listed
        // distro.
        [ExcludeFromCodeCoverage]
        public static HookInstallResult ReapplyProfiles()
        {
            const string label = "install-windows-hooks.ps1";
            if (!OperatingSystem.IsWindows())
            {
                return HookInstaller.Finish(label,
                    new HookInstallResult(HookInstallOutcome.Threw, label, Error: "Not Windows."));
            }

            var script = ResolveInstallerScriptPath();
            if (script is null) return HookInstaller.Finish(label, HookInstallResult.NotFound(label));

            // No -Wsl here on purpose: this call's only job is the native
            // side. Fast — no WSL VM involved — so a short timeout is enough.
            var native = HookInstaller.Run(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }, label, 10_000);

            // A distro that would not re-wire is a failure the card has to say,
            // not a bool this loop used to drop (CB-258). Native's own result
            // wins when it failed, since that is the more basic problem.
            var failed = new List<string>();
            foreach (var distro in ListDistros())
            {
                if (IsWired(distro) && !SetWired(distro, true)) failed.Add(distro);
            }

            if (native.Outcome != HookInstallOutcome.Ok || failed.Count == 0) return native;

            return HookInstaller.Finish(label, native with
            {
                Outcome = HookInstallOutcome.Threw,
                Error = $"WSL distro(s) {string.Join(", ", failed)} could not be re-wired."
            });
        }

        // Deliberately not shared with TerminalFocuser.TryRun or
        // ClaudeDesktopManager.Run, which do the identical thing — this
        // project's convention is one small copy per feature so each stays
        // independently deletable, not a shared helper.
        // Excluded from coverage: starts a subprocess and waits on it.
        [ExcludeFromCodeCoverage]
        private static bool TryRun(string exe, int timeoutMs, string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                foreach (var arg in args) psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi);
                if (process is null) return false;

                // Read both pipes concurrently and only then wait — see
                // TerminalFocuser.TryRun for why: a blocking read first would
                // make the timeout unreachable, and undrained stderr can
                // deadlock a chatty child once its pipe buffer fills.
                var outTask = process.StandardOutput.ReadToEndAsync();
                var errTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(timeoutMs))
                {
                    try { ProcessTreeKill.Kill(process); } catch { }
                    return false;
                }

                outTask.GetAwaiter().GetResult();
                errTask.GetAwaiter().GetResult();

                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        // No existing precedent in this codebase for locating tools/* at
        // runtime (the macOS .command wrapper and the Inno shortcuts invoke
        // the script directly; the app itself never has until now).
        // Excluded from coverage: walks the installed app layout on disk looking
        // for the shipped script.
        [ExcludeFromCodeCoverage]
        private static string? ResolveInstallerScriptPath()
        {
            // Installed layout: {app}\tools\install-windows-hooks.ps1,
            // alongside AppContext.BaseDirectory (the Inno [Files] layout).
            var installed = Path.Combine(AppContext.BaseDirectory, "tools", "install-windows-hooks.ps1");
            if (File.Exists(installed)) return installed;

            // Dev/source-build fallback: walk up looking for the repo's tools/.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tools", "install-windows-hooks.ps1");
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        // Mirrors the PowerShell script's Get-WslSettingsPath: resolve the
        // distro's default user's home directory via the registry (DefaultUid,
        // then /etc/passwd for anything other than root), then address
        // <profileDirName>/settings.json from the Windows side over UNC. Only
        // reaches each distro's *default* user, same limitation the script
        // has — a second Linux user account needs the manual README route.
        // Excluded from coverage: probes UNC paths inside a live distro; the two
        // spellings it chooses between are SettingsPathCandidates, which is
        // tested.
        [ExcludeFromCodeCoverage]
        private static string? ResolveSettingsPath(string distro, string profileDirName = ".claude")
        {
            var home = ResolveLinuxHome(distro);
            if (home is null) return null;

            var (viaLocalhost, viaDollar) = SettingsPathCandidates(distro, home, profileDirName);

            if (Directory.Exists(Path.GetDirectoryName(viaLocalhost)) || File.Exists(viaLocalhost))
            {
                return viaLocalhost;
            }

            // Older-build alias, same fallback the PowerShell script uses.
            return viaDollar;
        }

        // The two UNC spellings of one distro's settings file, in preference
        // order. Pure, and split out because getting this wrong is not a crash:
        // a path with a doubled or missing separator simply never exists, so the
        // distro reads as un-wired and the Settings window shows a toggle that
        // does nothing.
        internal static (string ViaLocalhost, string ViaDollar) SettingsPathCandidates(
            string distro, string home, string profileDirName = ".claude")
        {
            var rel = home.TrimStart('/').Replace('/', '\\') + $@"\{profileDirName}\settings.json";

            return ($@"\\wsl.localhost\{distro}\{rel}", $@"\\wsl$\{distro}\{rel}");
        }

        // UNC home-directory paths for every listed distro, e.g.
        // \\wsl.localhost\Ubuntu\home\kmart — lets the Settings window's
        // profile folder picker validate a WSL-only profile (one with no
        // Windows-side counterpart, like a second Linux-only account) the
        // same way it validates a native one: by requiring the picked folder
        // to be a direct child of *some* recognized home directory, not just
        // the Windows one.
        // Excluded from coverage: enumerates distros from the registry and
        // resolves each home over UNC.
        [ExcludeFromCodeCoverage]
        public static IReadOnlyList<string> GetWslHomeUncPaths()
        {
            if (!OperatingSystem.IsWindows()) return Array.Empty<string>();

            var result = new List<string>();
            foreach (var distro in ListDistros())
            {
                var home = ResolveLinuxHome(distro);
                if (home is null) continue;

                var rel = home.TrimStart('/').Replace('/', '\\');
                result.Add($@"\\wsl.localhost\{distro}\{rel}");
            }

            return result;
        }

        // Excluded from coverage: reads the distro's DefaultUid from the registry.
        [ExcludeFromCodeCoverage]
        private static string? ResolveLinuxHome(string distro)
        {
            uint defaultUid;
            try
            {
                using var lxss = Registry.CurrentUser.OpenSubKey(LxssKey);
                if (lxss is null) return null;

                defaultUid = 0;
                var found = false;
                foreach (var subKeyName in lxss.GetSubKeyNames())
                {
                    using var entry = lxss.OpenSubKey(subKeyName);
                    if (!string.Equals(entry?.GetValue("DistributionName") as string, distro, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    defaultUid = Convert.ToUInt32(entry!.GetValue("DefaultUid") ?? 0u);
                    found = true;
                    break;
                }

                if (!found) return null;
            }
            catch
            {
                return null;
            }

            // WSL's own default when a distro has never had its default user
            // changed — no /etc/passwd lookup needed for the common case.
            if (defaultUid == 0) return "/root";

            return ReadHomeFromPasswd(distro, defaultUid);
        }

        // Excluded from coverage: reads /etc/passwd inside a live distro over UNC;
        // the walk itself is HomeFromPasswdLines, which is tested.
        [ExcludeFromCodeCoverage]
        private static string? ReadHomeFromPasswd(string distro, uint uid)
        {
            try
            {
                var passwdPath = $@"\\wsl.localhost\{distro}\etc\passwd";
                if (!File.Exists(passwdPath)) passwdPath = $@"\\wsl$\{distro}\etc\passwd";
                if (!File.Exists(passwdPath)) return null;

                return HomeFromPasswdLines(File.ReadLines(passwdPath), uid);
            }
            catch
            {
                // Falls through to null.
            }

            return null;
        }

        // The home directory of one uid, out of /etc/passwd.
        //
        // Split from the file read because passwd is a format nobody here owns:
        // seven colon-separated fields, home in the sixth. A walk that mis-counts
        // them returns somebody else's directory, and the app would then look for
        // Claude Code's settings in a place that plausibly exists.
        internal static string? HomeFromPasswdLines(IEnumerable<string> lines, uint uid)
        {
            foreach (var line in lines)
            {
                var fields = line.Split(':');
                if (fields.Length >= 6 && uint.TryParse(fields[2], out var lineUid) && lineUid == uid)
                {
                    return fields[5];
                }
            }

            return null;
        }
    }
}
