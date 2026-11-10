using Xunit;

namespace Orbweaver.Tests;

// The real installer scripts, run as subprocesses against a scratch HOME, to pin
// where they read the app's saved profile list from (CB-258).
//
// The three bash installers hardcoded $HOME/Library/Application Support/
// ClaudeBuddy/settings.json and ignored ORBWEAVER_SETTINGS_DIR, which the app
// and install-hooks.sh both honour — so a test instance pointed at a scratch
// settings directory wired the *real* saved list instead. Each case therefore
// plants two lists: one in the scratch settings directory the variable names, and
// a decoy at the default location under the scratch HOME, and asserts on which
// profile ended up wired. The negative control runs without the variable and
// expects the decoy, which is what proves the decoy would have been picked if
// the variable were being ignored.
//
// macOS only: the installers read the list with osascript (JXA). The Windows
// installer's own copy of the seam is exercised on the Windows leg below.
[Collection("Settings")]
public class HookInstallerScriptsTests : IDisposable
{
    private static readonly string RepoRoot = FindRepoRoot();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cb-hookscripts-" + Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly string _settingsDir;
    private readonly IDisposable _logScope;

    public HookInstallerScriptsTests()
    {
        _home = Path.Combine(_root, "home");
        _settingsDir = Path.Combine(_root, "settings");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_settingsDir);
        _logScope = CrashLog.ScopeForTests(Path.Combine(_root, "logs"));
    }

    public void Dispose()
    {
        _logScope.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tools", "install-macos-hooks.sh")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find tools/install-macos-hooks.sh by walking up from " + AppContext.BaseDirectory);
    }

    // The list the variable names, and the decoy at the default location.
    private void PlantLists(string key, string wanted, string decoy)
    {
        File.WriteAllText(Path.Combine(_settingsDir, "settings.json"), $$"""{"{{key}}":["{{wanted}}"]}""");

        var defaultDir = Path.Combine(_home, "Library", "Application Support", "ClaudeBuddy");
        Directory.CreateDirectory(defaultDir);
        File.WriteAllText(Path.Combine(defaultDir, "settings.json"), $$"""{"{{key}}":["{{decoy}}"]}""");
    }

    private HookInstallResult RunInstaller(string script, bool withSettingsDirVariable) =>
        RunInstaller(script, withSettingsDirVariable ? Spelling.New : Spelling.None);

    // Which spelling of the settings-dir variable the installer is handed
    // (CB-256). The old one must still redirect it, and when both are set to
    // different places the new one must win — so Both points the legacy name
    // at a third directory holding a list of its own.
    public enum Spelling { None, New, Legacy, Both }

    internal const string NewName = BrandEnv.Prefix + BrandEnv.SettingsDir;
    internal const string LegacyName = BrandEnv.LegacyPrefix + BrandEnv.SettingsDir;

    private string LegacyDir => Path.Combine(_root, "legacy-settings");

    private Dictionary<string, string?> SettingsDirVariables(Spelling spelling) => new()
    {
        [NewName] = spelling is Spelling.New or Spelling.Both ? _settingsDir : null,
        [LegacyName] = spelling switch
        {
            Spelling.Legacy => _settingsDir,
            Spelling.Both => LegacyDir,
            _ => null,
        },
    };

    // The list a Both run must ignore: the one only the legacy name points at.
    private void PlantLegacyOnlyList(string key, string loser)
    {
        Directory.CreateDirectory(LegacyDir);
        File.WriteAllText(Path.Combine(LegacyDir, "settings.json"), $$"""{"{{key}}":["{{loser}}"]}""");
    }

    private HookInstallResult RunInstaller(string script, Spelling spelling)
    {
        var environment = SettingsDirVariables(spelling);
        environment["HOME"] = _home;
        environment["TMPDIR"] = _root + Path.DirectorySeparatorChar;
        return HookInstaller.Run("/bin/bash", new[] { Path.Combine(RepoRoot, "tools", script) }, script,
            environment: environment);
    }

    private string Home(params string[] parts) => Path.Combine(new[] { _home }.Concat(parts).ToArray());

    [MacOnlyFact]
    public void ClaudeCodeInstallerWiresTheProfileInTheSettingsDirNotTheDefaultOne()
    {
        PlantLists("claudeCodeProfileDirs", ".claude-wanted", ".claude-decoy");

        var result = RunInstaller("install-macos-hooks.sh", withSettingsDirVariable: true);

        Assert.Equal(HookInstallOutcome.Ok, result.Outcome);
        Assert.Contains("OrbweaverHook", File.ReadAllText(Home(".claude-wanted", "settings.json")));
        // The Settings card's own check, on what the real installer wrote: an
        // OrbweaverHook.sh profile, which IsWiredIn used to call un-wired.
        Assert.True(HookInstaller.IsWiredIn(".claude-wanted", _home));
        Assert.False(Directory.Exists(Home(".claude-decoy")));
    }

    [MacOnlyFact]
    public void ClaudeCodeInstallerFallsBackToTheDefaultLocationWithoutTheVariable()
    {
        PlantLists("claudeCodeProfileDirs", ".claude-wanted", ".claude-decoy");

        var result = RunInstaller("install-macos-hooks.sh", withSettingsDirVariable: false);

        Assert.Equal(HookInstallOutcome.Ok, result.Outcome);
        Assert.Contains("OrbweaverHook", File.ReadAllText(Home(".claude-decoy", "settings.json")));
        Assert.False(Directory.Exists(Home(".claude-wanted")));
    }

    [MacOnlyFact]
    public void CodexInstallerWiresTheHomeInTheSettingsDirNotTheDefaultOne()
    {
        PlantLists("codexHomes", ".codex-wanted", ".codex-decoy");

        var result = RunInstaller("install-codex-hooks.sh", withSettingsDirVariable: true);

        Assert.Equal(HookInstallOutcome.Ok, result.Outcome);
        Assert.True(File.Exists(Home(".codex-wanted", "hooks.json")));
        Assert.False(Directory.Exists(Home(".codex-decoy")));
    }

    [MacOnlyFact]
    public void CodexInstallerFallsBackToTheDefaultLocationWithoutTheVariable()
    {
        PlantLists("codexHomes", ".codex-wanted", ".codex-decoy");

        RunInstaller("install-codex-hooks.sh", withSettingsDirVariable: false);

        Assert.True(File.Exists(Home(".codex-decoy", "hooks.json")));
        Assert.False(Directory.Exists(Home(".codex-wanted")));
    }

    [MacOnlyFact]
    public void GrokInstallerWiresTheHomeInTheSettingsDirNotTheDefaultOne()
    {
        PlantLists("grokHomes", ".grok-wanted", ".grok-decoy");

        var result = RunInstaller("install-grok-hooks.sh", withSettingsDirVariable: true);

        Assert.Equal(HookInstallOutcome.Ok, result.Outcome);
        Assert.True(File.Exists(Home(".grok-wanted", "hooks", "orbweaver.json")));
        Assert.False(Directory.Exists(Home(".grok-decoy")));
    }

    [MacOnlyFact]
    public void GrokInstallerFallsBackToTheDefaultLocationWithoutTheVariable()
    {
        PlantLists("grokHomes", ".grok-wanted", ".grok-decoy");

        RunInstaller("install-grok-hooks.sh", withSettingsDirVariable: false);

        Assert.True(File.Exists(Home(".grok-decoy", "hooks", "orbweaver.json")));
        Assert.False(Directory.Exists(Home(".grok-wanted")));
    }

    // What each bash installer saves its list under, and the file that says a
    // profile got wired.
    private string WiredFile(string script, string profile) =>
        script.Contains("macos") ? Home(profile, "settings.json")
        : script.Contains("codex") ? Home(profile, "hooks.json")
        : Home(profile, "hooks", "orbweaver.json");

    private static (string key, string prefix) ListFor(string script) =>
        script.Contains("macos") ? ("claudeCodeProfileDirs", ".claude")
        : script.Contains("codex") ? ("codexHomes", ".codex")
        : ("grokHomes", ".grok");

    // CB-256: the pre-rename spelling still redirects every bash installer, and
    // with both set to different directories the new spelling's list is the one
    // wired. Each run also plants the default-location decoy, so a script that
    // ignored both names would wire that instead and fail here.
    [MacOnlyTheory]
    [InlineData("install-macos-hooks.sh", Spelling.Legacy)]
    [InlineData("install-macos-hooks.sh", Spelling.Both)]
    [InlineData("install-codex-hooks.sh", Spelling.Legacy)]
    [InlineData("install-codex-hooks.sh", Spelling.Both)]
    [InlineData("install-grok-hooks.sh", Spelling.Legacy)]
    [InlineData("install-grok-hooks.sh", Spelling.Both)]
    public void EachBashInstallerHonoursTheOldSpellingAndPrefersTheNewOne(string script, Spelling spelling)
    {
        var (key, prefix) = ListFor(script);
        PlantLists(key, prefix + "-wanted", prefix + "-decoy");
        PlantLegacyOnlyList(key, prefix + "-legacy");

        var result = RunInstaller(script, spelling);

        Assert.True(result.Outcome == HookInstallOutcome.Ok, $"{script}: {result.Outcome} exit {result.ExitCode}: {result.Error}");
        Assert.True(File.Exists(WiredFile(script, prefix + "-wanted")), $"{script} under {spelling} did not wire the list the variable names");
        Assert.False(Directory.Exists(Home(prefix + "-decoy")));
        Assert.False(Directory.Exists(Home(prefix + "-legacy")));
    }

    // The cause of CB-258 itself, found by a real click: HookInstaller appended
    // --auto-color for any user with the colour setting on, every installer
    // rejects an unknown option with exit 2 before wiring anything, and the
    // failure was swallowed. These run each real installer through the app's own
    // RunScript, which builds its argument list from the one function the app
    // uses, with the setting both on and off, and assert a clean exit and a
    // wired profile. [Collection("Settings")] because the setting is process-wide.
    [MacOnlyTheory]
    [InlineData("install-macos-hooks.sh", true)]
    [InlineData("install-macos-hooks.sh", false)]
    [InlineData("install-codex-hooks.sh", true)]
    [InlineData("install-codex-hooks.sh", false)]
    [InlineData("install-grok-hooks.sh", true)]
    [InlineData("install-grok-hooks.sh", false)]
    public void EveryInstallerRunsCleanlyThroughTheAppsOwnRunnerWhateverTheColourSetting(
        string script, bool autoColor)
    {
        var key = script.Contains("macos") ? "claudeCodeProfileDirs"
            : script.Contains("codex") ? "codexHomes" : "grokHomes";
        File.WriteAllText(Path.Combine(_settingsDir, "settings.json"), $$"""{"{{key}}":[".cb258-profile"]}""");

        var was = OrbweaverSettings.AutoColorSessions;
        OrbweaverSettings.AutoColorSessions = autoColor;
        try
        {
            var result = HookInstaller.RunScript(script, baseDirectory: RepoRoot,
                environment: new Dictionary<string, string?>
                {
                    ["HOME"] = _home,
                    ["TMPDIR"] = _root + Path.DirectorySeparatorChar,
                    ["ORBWEAVER_SETTINGS_DIR"] = _settingsDir
                });

            Assert.True(result.Outcome == HookInstallOutcome.Ok, $"{script}: {result.Outcome} exit {result.ExitCode}: {result.Error}");
            Assert.True(Directory.Exists(Home(".cb258-profile")), $"{script} wired nothing for the saved profile");
        }
        finally
        {
            OrbweaverSettings.AutoColorSessions = was;
        }
    }

    // The Windows twins, through the same runner. Not runnable off Windows and not
    // run here: the Windows leg of CI is where they are verified.
    [WindowsOnlyTheory]
    [InlineData("install-codex-hooks.ps1")]
    [InlineData("install-grok-hooks.ps1")]
    public void TheWindowsCodexAndGrokInstallersRunCleanlyThroughTheAppsOwnRunner(string script)
    {
        var result = HookInstaller.RunPowerShell(script, baseDirectory: RepoRoot,
            environment: new Dictionary<string, string?>
            {
                ["USERPROFILE"] = _home,
                ["HOME"] = _home,
                ["CODEX_HOME"] = null,
                ["GROK_HOME"] = null,
                ["ORBWEAVER_SETTINGS_DIR"] = _settingsDir
            });

        Assert.True(result.Outcome == HookInstallOutcome.Ok, $"{script}: {result.Outcome} exit {result.ExitCode}: {result.Error}");
    }

    // CB-316: the wired -TempDir must not end in a backslash. GetTempPath()
    // does, and `...\Temp\"` reads as an escaped quote under PowerShell's -File
    // parsing, so the hook got a TempDir with a stray quote and wrote no status
    // file (measured on the Windows box). Pinned on the command Codex runs.
    [WindowsOnlyFact]
    public void TheCodexHookIsWiredWithATempDirThatDoesNotEndInABackslash()
    {
        var result = HookInstaller.RunPowerShell("install-codex-hooks.ps1", baseDirectory: RepoRoot,
            environment: new Dictionary<string, string?>
            {
                ["USERPROFILE"] = _home,
                ["HOME"] = _home,
                ["CODEX_HOME"] = null,
                ["GROK_HOME"] = null,
                ["ORBWEAVER_SETTINGS_DIR"] = _settingsDir
            });
        Assert.True(result.Outcome == HookInstallOutcome.Ok, $"{result.Outcome}: {result.Error}");

        var hooks = File.ReadAllText(Path.Combine(_home, ".codex", "hooks.json"));
        var commands = System.Text.Json.JsonDocument.Parse(hooks).RootElement.GetProperty("hooks").EnumerateObject()
            .SelectMany(e => e.Value.EnumerateArray()).SelectMany(g => g.GetProperty("hooks").EnumerateArray())
            .Select(h => h.GetProperty("command").GetString()!).ToList();
        Assert.NotEmpty(commands);
        var temp = Path.GetTempPath().TrimEnd('\\');
        Assert.All(commands, c =>
        {
            Assert.Contains("-TempDir \"" + temp + "\"", c);
            Assert.DoesNotContain("\\\"", c);
        });
    }

    // The second bug found while testing the seam: the Grok installer re-invoked
    // itself for each extra home with ${UNINSTALL:+--uninstall}, and UNINSTALL is
    // 0 for an install — "0" being a non-empty string — so installing *unwired*
    // every extra Grok home. Asserted on the file existing rather than on the exit
    // code, which was 0 throughout.
    [MacOnlyFact]
    public void AnInstallLeavesAnExtraGrokHomeWiredRatherThanRemovingIt()
    {
        PlantLists("grokHomes", ".grok-wanted", ".grok-decoy");

        var result = RunInstaller("install-grok-hooks.sh", withSettingsDirVariable: true);

        Assert.DoesNotContain("Removed Orbweaver hooks", result.Output);
        Assert.Contains("Wired Orbweaver hooks", result.Output);
    }

    // The silent failure this ticket could not rule out: a list that cannot be
    // read used to look exactly like a list with nothing in it — exit 0, empty
    // stderr, only the default profile wired. Now it is exit 3 with the reason on
    // stderr, after the default profile has still been wired.
    [MacOnlyFact]
    public void AnUnparseableSavedListExitsThreeWithTheReasonAndStillWiresTheDefaultProfile()
    {
        File.WriteAllText(Path.Combine(_settingsDir, "settings.json"), "{ this is not json");

        var result = RunInstaller("install-macos-hooks.sh", withSettingsDirVariable: true);

        Assert.Equal(HookInstallOutcome.Failed, result.Outcome);
        Assert.Equal(HookInstaller.SavedListUnreadableExit, result.ExitCode);
        Assert.Contains("could not read the saved profile list", result.Error);
        Assert.Contains("OrbweaverHook", File.ReadAllText(Home(".claude", "settings.json")));
        Assert.Contains("      warning: could not read the saved profile list", File.ReadAllText(HookInstallerLog.Path_));
        Assert.Contains("the saved profile list could not be read",
            HookInstaller.StatusMessage(result, ".claude-wanted"));
    }

    [MacOnlyFact]
    public void AnUnparseableSavedListExitsThreeFromTheCodexAndGrokInstallersToo()
    {
        File.WriteAllText(Path.Combine(_settingsDir, "settings.json"), "{ this is not json");

        var codex = RunInstaller("install-codex-hooks.sh", withSettingsDirVariable: true);
        var grok = RunInstaller("install-grok-hooks.sh", withSettingsDirVariable: true);

        Assert.Equal(HookInstaller.SavedListUnreadableExit, codex.ExitCode);
        Assert.Equal(HookInstaller.SavedListUnreadableExit, grok.ExitCode);
        Assert.Contains("could not read the saved profile list", codex.Error);
        Assert.Contains("could not read the saved profile list", grok.Error);
    }

    // The other half of the same arm: osascript itself failing on the list read
    // (what a different process context could do), as opposed to a bad file. A
    // shim that fails only when handed the saved settings file, and otherwise
    // defers to the real osascript, so the default profile's merge still works.
    [MacOnlyFact]
    public void AnOsascriptThatFailsOnlyOnTheListReadExitsThree()
    {
        PlantLists("claudeCodeProfileDirs", ".claude-wanted", ".claude-decoy");
        var shims = Path.Combine(_root, "shims");
        Directory.CreateDirectory(shims);
        var shim = Path.Combine(shims, "osascript");
        File.WriteAllText(shim,
            "#!/bin/bash\n" +
            "for a in \"$@\"; do case \"$a\" in *ClaudeBuddy/settings.json|*/settings/settings.json) exit 1;; esac; done\n" +
            "exec /usr/bin/osascript \"$@\"\n");
        File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var result = HookInstaller.Run("/bin/bash", new[] { Path.Combine(RepoRoot, "tools", "install-macos-hooks.sh") },
            "install-macos-hooks.sh",
            environment: new Dictionary<string, string?>
            {
                ["HOME"] = _home,
                ["TMPDIR"] = _root + Path.DirectorySeparatorChar,
                ["ORBWEAVER_SETTINGS_DIR"] = _settingsDir,
                ["PATH"] = shims + ":/usr/bin:/bin"
            });

        Assert.Equal(HookInstaller.SavedListUnreadableExit, result.ExitCode);
        Assert.Contains("could not read the saved profile list", result.Error);
        Assert.Contains("OrbweaverHook", File.ReadAllText(Home(".claude", "settings.json")));
        Assert.False(Directory.Exists(Home(".claude-wanted")));
    }

    // A missing settings file is the common case (nobody has added a profile) and
    // stays silent: no warning, exit 0.
    [MacOnlyFact]
    public void AMissingSavedListIsNotAnError()
    {
        var result = RunInstaller("install-macos-hooks.sh", withSettingsDirVariable: true);

        Assert.Equal(HookInstallOutcome.Ok, result.Outcome);
        Assert.DoesNotContain("saved profile list", result.Error);
    }

    // A name with a slash is refused, non-fatally, and the refusal reaches the log.
    [MacOnlyFact]
    public void AProfileNamedWithASlashIsSkippedOnStderrAndThatLandsInTheLog()
    {
        File.WriteAllText(Path.Combine(_settingsDir, "settings.json"),
            """{"claudeCodeProfileDirs":["../escape",".claude-ok"]}""");

        var result = RunInstaller("install-macos-hooks.sh", withSettingsDirVariable: true);

        Assert.Equal(HookInstallOutcome.Ok, result.Outcome);
        Assert.Contains("Skipping profile '../escape'", result.Error);
        Assert.Contains("Skipping profile '../escape'", File.ReadAllText(HookInstallerLog.Path_));
        Assert.Contains("OrbweaverHook", File.ReadAllText(Home(".claude-ok", "settings.json")));
    }

    // Skipped unless asked for: run against the installed app bundle rather than
    // the repo, to answer "does the shipped layout resolve and wire from a
    // scratch HOME" without a click (CB-258). CB258_BUNDLE_PROBE names the
    // bundle's Contents/MacOS directory.
    [MacOnlyFact]
    public void TheInstalledBundleResolvesItsInstallerAndWiresAScratchProfile()
    {
        var macOs = Environment.GetEnvironmentVariable("CB258_BUNDLE_PROBE");
        if (string.IsNullOrEmpty(macOs)) return;

        var resolved = HookInstaller.Resolve("install-macos-hooks.sh", macOs);
        Assert.Equal(Path.GetFullPath(Path.Combine(macOs, "..", "Resources", "install-macos-hooks.sh")), resolved);

        // The list goes in both places: the installed bundle may predate the
        // ORBWEAVER_SETTINGS_DIR seam (this ticket's own fix), in which case
        // only the default location under the scratch HOME is read.
        File.WriteAllText(Path.Combine(_settingsDir, "settings.json"), """{"claudeCodeProfileDirs":[".claude-cb258-test"]}""");
        var defaultDir = Path.Combine(_home, "Library", "Application Support", "ClaudeBuddy");
        Directory.CreateDirectory(defaultDir);
        File.Copy(Path.Combine(_settingsDir, "settings.json"), Path.Combine(defaultDir, "settings.json"));
        var result = HookInstaller.Run("/bin/bash", new[] { resolved! }, "install-macos-hooks.sh",
            environment: new Dictionary<string, string?>
            {
                ["HOME"] = _home,
                ["TMPDIR"] = _root + Path.DirectorySeparatorChar,
                ["ORBWEAVER_SETTINGS_DIR"] = _settingsDir
            });

        Console.Error.WriteLine($"PROBE outcome={result.Outcome} exit={result.ExitCode} stderr=[{result.Error}]");
        Assert.Equal(HookInstallOutcome.Ok, result.Outcome);
        Assert.True(HookInstaller.IsWiredIn(".claude-cb258-test", _home));
    }

    // The Windows installer's copy of the seam, in each spelling (CB-256): the
    // new name, the pre-rename one, and both pointed at different lists, where
    // the new name's must win. Runs on the Windows leg only.
    //
    // It also asserts HookInstaller.IsWiredIn on what the real installer wrote,
    // which is the check the Settings card makes after a run. That check used to
    // look only for the pre-rename script name, so this profile — wired with
    // OrbweaverHook.ps1 — read as un-wired; the only test that asserted it
    // returned early unless an installed bundle was on the machine.
    [WindowsOnlyTheory]
    [InlineData(Spelling.New)]
    [InlineData(Spelling.Legacy)]
    [InlineData(Spelling.Both)]
    public void WindowsInstallerWiresTheProfileInTheSettingsDirNotTheAppDataOne(Spelling spelling)
    {
        var appData = Path.Combine(_root, "appdata");
        Directory.CreateDirectory(Path.Combine(appData, "ClaudeBuddy"));
        File.WriteAllText(Path.Combine(_settingsDir, "settings.json"), """{"claudeCodeProfileDirs":[".claude-wanted"]}""");
        File.WriteAllText(Path.Combine(appData, "ClaudeBuddy", "settings.json"), """{"claudeCodeProfileDirs":[".claude-decoy"]}""");
        PlantLegacyOnlyList("claudeCodeProfileDirs", ".claude-legacy");

        var environment = SettingsDirVariables(spelling);
        environment["USERPROFILE"] = _home;
        environment["APPDATA"] = appData;

        var result = HookInstaller.Run(
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            new[]
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                Path.Combine(RepoRoot, "tools", "install-windows-hooks.ps1"),
                "-InstallDir", Path.Combine(_root, "install"),
                "-SettingsPath", Path.Combine(_home, ".claude", "settings.json")
            },
            "install-windows-hooks.ps1",
            environment: environment);

        Assert.Equal(HookInstallOutcome.Ok, result.Outcome);
        Assert.Contains("OrbweaverHook", File.ReadAllText(Home(".claude-wanted", "settings.json")));
        Assert.True(HookInstaller.IsWiredIn(".claude-wanted", _home));
        Assert.False(Directory.Exists(Home(".claude-decoy")));
        Assert.False(Directory.Exists(Home(".claude-legacy")));
    }
}

public sealed class MacOnlyFactAttribute : FactAttribute
{
    public MacOnlyFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "the macOS installers read the saved list with osascript";
    }
}

public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "drives Windows PowerShell 5.1";
    }
}

public sealed class MacOnlyTheoryAttribute : TheoryAttribute
{
    public MacOnlyTheoryAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "the macOS installers read the saved list with osascript";
    }
}

public sealed class WindowsOnlyTheoryAttribute : TheoryAttribute
{
    public WindowsOnlyTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "drives Windows PowerShell 5.1";
    }
}
