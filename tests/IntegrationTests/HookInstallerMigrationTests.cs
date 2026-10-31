using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-255 phase 2: every hook installer re-wires a machine from the pre-rename
// ClaudeBuddyHook.{sh,ps1} to OrbweaverHook.{sh,ps1} in one pass -- strip every
// entry naming either script, append fresh ones at the new path -- and leaves
// the old script folder on disk with a .superseded marker for the app's
// LegacyHookCleanup to retire later, because a session that was already
// running keeps calling the old path until it restarts.
//
// Driven as real subprocesses against scratch homes, the same pattern
// HookScriptShTests/HookScriptPs1Tests use for the hook itself. Every config
// is pre-seeded with one of: legacy entries only, new entries only, both, and
// always a foreign tool's hooks -- one of them sharing a group with one of
// ours, so a partial strip is visible. Codex additionally gets an /import-style
// entry whose only mention of us is in commandWindows.
//
// The Windows installers run under Windows PowerShell 5.1 (what Inno and
// HookInstaller invoke) and pwsh, Windows only: they resolve %LOCALAPPDATA%,
// %APPDATA% and %USERPROFILE%, which pwsh on macOS does not have. The macOS
// installers do their JSON surgery in JavaScript for Automation, so they need
// a real /usr/bin/osascript and run on macOS only; elsewhere they report as
// skipped, not passed.
//
// Not covered, and named: whether a real Claude Code / Codex / Grok actually
// reads the new entries (that is the CLIs' own discovery, measured in the
// docs/*-findings.md files, not re-measured here), and the WSL arm of
// install-windows-hooks.ps1, which needs a real distro. Uninstall runs pass a
// -WslDistro that matches nothing so the sweep can never touch this machine's
// real distros.
public class HookInstallerMigrationTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static string Tool(string name) => Path.Combine(RepoRoot, "tools", name);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OrbweaverHook.ps1")) &&
                File.Exists(Path.Combine(dir.FullName, "tools", "install-windows-hooks.ps1")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find OrbweaverHook.ps1 + tools/ by walking up from " + AppContext.BaseDirectory);
    }

    public enum Seed { Legacy, New, Both }

    // ---- shared plumbing ----------------------------------------------------

    private sealed record Result(int ExitCode, string Stdout, string Stderr)
    {
        public override string ToString() => $"exit {ExitCode}\n--- stdout\n{Stdout}\n--- stderr\n{Stderr}";
    }

    private sealed class Scratch : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("orbweaver-installer-").FullName;
        public string Home => Dir("home");
        public string LocalAppData => Dir("local");
        public string AppData => Dir("roaming");

        private string Dir(string name)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static Result Run(string exe, IEnumerable<string> args, IDictionary<string, string?> env)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (key, value) in env)
        {
            if (value is null) psi.Environment.Remove(key);
            else psi.Environment[key] = value;
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start " + exe);
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(TimeSpan.FromSeconds(120)))
        {
            proc.Kill(entireProcessTree: true);
            Assert.Fail($"{exe} did not exit within 120s");
        }
        proc.WaitForExit();
        return new Result(proc.ExitCode, stdout.Result, stderr.Result);
    }

    private static void AssertOk(Result r) => Assert.True(r.ExitCode == 0, r.ToString());

    private sealed record Handler(string Event, string? Matcher, string? Command, string? CommandWindows, bool Async);

    // Tolerant of a lone object where an array is expected, so a serialiser
    // collapsing a one-element array reads as a failure of the count below
    // rather than as a crash here.
    private static IEnumerable<JsonNode> Items(JsonNode? node) => node switch
    {
        JsonArray a => a.Where(n => n is not null)!,
        JsonObject o => new[] { o },
        _ => Array.Empty<JsonNode>(),
    };

    private static List<Handler> Handlers(string configPath)
    {
        var root = JsonNode.Parse(File.ReadAllText(configPath))!;
        var list = new List<Handler>();
        if (root["hooks"] is not JsonObject hooks) return list;
        foreach (var (eventName, groups) in hooks)
            foreach (var group in Items(groups))
                foreach (var h in Items(group["hooks"]))
                    list.Add(new Handler(
                        eventName,
                        group["matcher"]?.GetValue<string>(),
                        h["command"]?.GetValue<string>(),
                        h["commandWindows"]?.GetValue<string>(),
                        h["async"]?.GetValue<bool>() ?? false));
        return list;
    }

    private static bool NamesUs(string? s) =>
        s is not null &&
        (s.Contains("ClaudeBuddyHook.", StringComparison.OrdinalIgnoreCase) ||
         s.Contains("OrbweaverHook.", StringComparison.OrdinalIgnoreCase));

    private static bool IsOurs(Handler h) => NamesUs(h.Command) || NamesUs(h.CommandWindows);

    private static JsonObject Group(string? matcher, params JsonObject[] handlers)
    {
        var g = new JsonObject();
        if (matcher is not null) g["matcher"] = matcher;
        g["hooks"] = new JsonArray(handlers.Select(h => (JsonNode)h).ToArray());
        return g;
    }

    private static JsonObject Cmd(string command, string? commandWindows = null)
    {
        var h = new JsonObject { ["type"] = "command", ["command"] = command };
        if (commandWindows is not null) h["commandWindows"] = commandWindows;
        return h;
    }

    private static void Add(JsonObject hooks, string eventName, JsonObject group)
    {
        if (hooks[eventName] is not JsonArray arr) { arr = new JsonArray(); hooks[eventName] = arr; }
        arr.Add(group);
    }

    private static void Write(string path, JsonNode node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, node.ToJsonString());
    }

    private static void Touch(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // The foreign tool's hooks, which every run must leave byte-identical in
    // command and in count.
    private const string ForeignStop = "node C:/tools/other-tool/on-stop.js";
    private const string ForeignPre = "python3 /opt/other-tool/pre.py";

    private static void AssertForeignUntouched(List<Handler> handlers)
    {
        Assert.Single(handlers, h => h.Command == ForeignStop && h.Event == "Stop");
        Assert.Single(handlers, h => h.Command == ForeignPre && h.Event == "PreToolUse" && h.Matcher == "Bash");
    }

    private static readonly (string Event, string? Matcher, string State)[] ClaudeWanted =
    {
        ("SessionStart", null, "idle"),
        ("UserPromptSubmit", null, "generating"),
        ("PreToolUse", ".*", "generating"),
        ("Stop", null, "idle"),
        ("SessionEnd", null, "ended"),
        ("Notification", "permission_prompt", "waiting"),
        ("Notification", "elicitation_dialog", "waiting"),
        ("Notification", "elicitation_complete", "generating"),
    };

    private static readonly (string Event, string? Matcher, string State)[] CodexWanted =
    {
        ("SessionStart", null, "idle"),
        ("UserPromptSubmit", null, "generating"),
        ("PreToolUse", ".*", "generating"),
        ("PermissionRequest", null, "waiting"),
        ("PostToolUse", ".*", "generating"),
        ("Stop", null, "idle"),
        ("SessionEnd", null, "ended"),
    };

    // A settings.json / hooks.json seeded per the Seed, plus the foreign tool,
    // one of whose handlers shares a Stop group with one of ours. That shared
    // entry uses `mixedOurs`, so the Windows rigs can make it a lower-case
    // spelling and prove the match is case-insensitive there.
    private static JsonObject SeedConfig(
        Seed seed,
        (string Event, string? Matcher, string State)[] wanted,
        Func<string, JsonObject> legacy,
        Func<string, JsonObject> fresh,
        JsonObject mixedOurs,
        bool withModel)
    {
        var hooks = new JsonObject();
        Add(hooks, "Stop", Group(null, Cmd(ForeignStop), mixedOurs));
        Add(hooks, "PreToolUse", Group("Bash", Cmd(ForeignPre)));
        foreach (var (ev, matcher, state) in wanted)
        {
            if (seed is Seed.Legacy or Seed.Both) Add(hooks, ev, Group(matcher, legacy(state)));
            if (seed is Seed.New or Seed.Both) Add(hooks, ev, Group(matcher, fresh(state)));
        }
        var root = new JsonObject();
        if (withModel) root["model"] = "opus";
        root["hooks"] = hooks;
        return root;
    }

    // Exactly the wanted (event, matcher) set, once each, every one ours and at
    // the new path, none at the old.
    private static void AssertExactlyOurs(
        List<Handler> handlers,
        (string Event, string? Matcher, string State)[] wanted,
        Func<Handler, bool> pointsAtNewPath)
    {
        var ours = handlers.Where(IsOurs).ToList();
        Assert.Equal(wanted.Length, ours.Count);
        Assert.All(ours, h =>
        {
            Assert.True(pointsAtNewPath(h), $"not at the new path: {h}");
            Assert.False(NamesLegacy(h), $"legacy entry survived: {h}");
        });
        var expected = wanted.Select(w => $"{w.Event}|{w.Matcher}").OrderBy(s => s, StringComparer.Ordinal);
        var actual = ours.Select(h => $"{h.Event}|{h.Matcher}").OrderBy(s => s, StringComparer.Ordinal);
        Assert.Equal(expected, actual);
    }

    private static bool NamesLegacy(Handler h) =>
        (h.Command?.Contains("ClaudeBuddyHook.", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (h.CommandWindows?.Contains("ClaudeBuddyHook.", StringComparison.OrdinalIgnoreCase) ?? false);

    private static readonly DateTime OldMtime = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    // ---- Windows rigs ---------------------------------------------------------

    public static IEnumerable<object[]> WindowsCases()
    {
        foreach (var shell in new[] { "powershell", "pwsh" })
            foreach (var seed in new[] { Seed.Legacy, Seed.New, Seed.Both })
                yield return new object[] { shell, seed };
    }

    public static IEnumerable<object[]> WindowsShells() =>
        new[] { new object[] { "powershell" }, new object[] { "pwsh" } };

    private static Dictionary<string, string?> WindowsEnv(Scratch s) => new()
    {
        ["USERPROFILE"] = s.Home,
        ["LOCALAPPDATA"] = s.LocalAppData,
        ["APPDATA"] = s.AppData,
        ["CODEX_HOME"] = null,
        ["GROK_HOME"] = null,
        ["CLAUDE_CONFIG_DIR"] = null,
        // TestBootstrap points this at a scratch directory for the whole suite,
        // and an installer that sees it reads that directory and nothing else
        // (CB-258). These cases are about the *fallback* from the Orbweaver data
        // dir to the pre-rename one, which only runs with the variable unset —
        // in both spellings, since the installer honours the old one too.
        [BrandEnv.Name(BrandEnv.SettingsDir)] = null,
        [BrandEnv.LegacyPrefix + BrandEnv.SettingsDir] = null,
    };

    private static Result RunPs(string shell, string script, Scratch s, params string[] args)
    {
        var all = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Tool(script) };
        all.AddRange(args);
        return Run(shell, all, WindowsEnv(s));
    }

    // A -WslDistro no machine has, so -Uninstall's WSL sweep matches nothing.
    private static readonly string NoSuchDistro = "orbweaver-test-no-such-distro-" + Guid.NewGuid().ToString("N");

    private static string WinClaudeLegacy(Scratch s, string state, string spelling = "ClaudeBuddyHook.ps1") =>
        $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(s.LocalAppData, "ClaudeBuddy", spelling)}\" -State {state} -TempDir \"C:\\t\"";

    private static string WinClaudeNew(Scratch s, string state) =>
        $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(s.LocalAppData, "Orbweaver", "OrbweaverHook.ps1")}\" -State {state} -TempDir \"C:\\t\"";

    private static string WinClaudeSettings(Scratch s) => Path.Combine(s.Home, ".claude", "settings.json");
    private static string WinClaudeLegacyDir(Scratch s) => Path.Combine(s.LocalAppData, "ClaudeBuddy");

    private static void SeedWindowsClaude(Scratch s, Seed seed)
    {
        Write(WinClaudeSettings(s), SeedConfig(seed, ClaudeWanted,
            st => Cmd(WinClaudeLegacy(s, st)),
            st => Cmd(WinClaudeNew(s, st)),
            Cmd(WinClaudeLegacy(s, "idle", "claudebuddyhook.PS1")),
            withModel: true));
        // What a pre-rename install left: the script, plus the Logs folder the
        // app's data-dir migration moves out later.
        Touch(Path.Combine(WinClaudeLegacyDir(s), "ClaudeBuddyHook.ps1"));
        Touch(Path.Combine(WinClaudeLegacyDir(s), "Logs", "crash.log"));
    }

    private static bool WinClaudeAtNewPath(Scratch s, Handler h) =>
        h.Command!.Contains($"-File \"{Path.Combine(s.LocalAppData, "Orbweaver", "OrbweaverHook.ps1")}\"", StringComparison.Ordinal);

    private static void AssertWindowsClaudeWired(Scratch s)
    {
        var handlers = Handlers(WinClaudeSettings(s));
        AssertExactlyOurs(handlers, ClaudeWanted, h => WinClaudeAtNewPath(s, h));
        AssertForeignUntouched(handlers);
        Assert.Equal("opus", JsonNode.Parse(File.ReadAllText(WinClaudeSettings(s)))!["model"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(s.LocalAppData, "Orbweaver", "OrbweaverHook.ps1")));
    }

    [WindowsInstallerTheory]
    [MemberData(nameof(WindowsCases))]
    public void WindowsClaudeCode_RewiresToTheNewScript_AndIsIdempotent(string shell, Seed seed)
    {
        using var s = new Scratch();
        SeedWindowsClaude(s, seed);

        AssertOk(RunPs(shell, "install-windows-hooks.ps1", s));
        AssertWindowsClaudeWired(s);
        Assert.True(File.Exists(WinClaudeSettings(s) + ".orbweaver-backup"));
        Assert.False(File.Exists(WinClaudeSettings(s) + ".claudebuddy-backup"));

        // Legacy folder kept, contents untouched, marker added.
        var marker = Path.Combine(WinClaudeLegacyDir(s), ".superseded");
        Assert.True(File.Exists(Path.Combine(WinClaudeLegacyDir(s), "ClaudeBuddyHook.ps1")));
        Assert.True(File.Exists(Path.Combine(WinClaudeLegacyDir(s), "Logs", "crash.log")));
        Assert.True(File.Exists(marker));

        // Second run converges and must not refresh the marker's clock.
        File.SetLastWriteTimeUtc(marker, OldMtime);
        AssertOk(RunPs(shell, "install-windows-hooks.ps1", s));
        AssertWindowsClaudeWired(s);
        Assert.Equal(OldMtime, File.GetLastWriteTimeUtc(marker));
    }

    [WindowsInstallerTheory]
    [MemberData(nameof(WindowsShells))]
    public void WindowsClaudeCode_Uninstall_RemovesBothNames_KeepsForeignAndBothFolders(string shell)
    {
        using var s = new Scratch();
        SeedWindowsClaude(s, Seed.Both);
        AssertOk(RunPs(shell, "install-windows-hooks.ps1", s));
        // Put a legacy entry back, as a session-era leftover, to prove
        // uninstall strips that name too and not just what install wrote.
        var root = JsonNode.Parse(File.ReadAllText(WinClaudeSettings(s)))!.AsObject();
        Add(root["hooks"]!.AsObject(), "SessionEnd", Group(null, Cmd(WinClaudeLegacy(s, "ended"))));
        Write(WinClaudeSettings(s), root);

        AssertOk(RunPs(shell, "install-windows-hooks.ps1", s, "-Uninstall", "-WslDistro", NoSuchDistro));

        var handlers = Handlers(WinClaudeSettings(s));
        Assert.DoesNotContain(handlers, IsOurs);
        AssertForeignUntouched(handlers);
        Assert.True(Directory.Exists(Path.Combine(s.LocalAppData, "Orbweaver")));
        Assert.True(Directory.Exists(WinClaudeLegacyDir(s)));
    }

    [WindowsInstallerTheory]
    [MemberData(nameof(WindowsShells))]
    public void WindowsClaudeCode_FreshMachine_GrowsNoLegacyFolder(string shell)
    {
        using var s = new Scratch();
        AssertOk(RunPs(shell, "install-windows-hooks.ps1", s));
        AssertExactlyOurs(Handlers(WinClaudeSettings(s)), ClaudeWanted, h => WinClaudeAtNewPath(s, h));
        Assert.False(Directory.Exists(WinClaudeLegacyDir(s)));
    }

    // CB-291: the orbweaver-team skill lands in every account's own skills folder
    // — the default one and each saved profile — because each Claude Code account
    // reads only its own config directory. A reinstall replaces the folder
    // outright, and an uninstall removes that one folder and nothing beside it.
    [WindowsInstallerTheory]
    [MemberData(nameof(WindowsShells))]
    public void WindowsClaudeCode_InstallsTheTeamSkillForEveryAccount_AndUninstallRemovesOnlyThat(string shell)
    {
        using var s = new Scratch();
        Write(Path.Combine(s.AppData, "Orbweaver", "settings.json"),
            new JsonObject { ["claudeCodeProfileDirs"] = new JsonArray(".claude-work") });
        var neighbour = Path.Combine(s.Home, ".claude", "skills", "someone-elses", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(neighbour)!);
        File.WriteAllText(neighbour, "not ours");

        AssertOk(RunPs(shell, "install-windows-hooks.ps1", s));

        foreach (var account in new[] { ".claude", ".claude-work" })
        {
            var skill = Path.Combine(s.Home, account, "skills", "orbweaver-team");
            Assert.True(File.Exists(Path.Combine(skill, "SKILL.md")), account);
            Assert.True(File.Exists(Path.Combine(skill, "FORMATS.md")), account);
            Assert.True(File.Exists(Path.Combine(skill, "orbweaver-team.sh")), account);
            Assert.Equal(4, Directory.GetFiles(Path.Combine(skill, "roles"), "*.md").Length);

            // The resolver must reach Git Bash as shipped: a carriage return in
            // its shebang line is a script that cannot run at all.
            Assert.DoesNotContain((byte)'\r', File.ReadAllBytes(Path.Combine(skill, "orbweaver-team.sh")));
        }

        // A reinstall replaces the folder rather than adding to it.
        var stale = Path.Combine(s.Home, ".claude", "skills", "orbweaver-team", "left-by-an-older-version.txt");
        File.WriteAllText(stale, "stale");
        AssertOk(RunPs(shell, "install-windows-hooks.ps1", s));
        Assert.False(File.Exists(stale));

        AssertOk(RunPs(shell, "install-windows-hooks.ps1", s, "-Uninstall", "-WslDistro", NoSuchDistro));

        foreach (var account in new[] { ".claude", ".claude-work" })
            Assert.False(Directory.Exists(Path.Combine(s.Home, account, "skills", "orbweaver-team")), account);
        Assert.True(File.Exists(neighbour));
    }

    // The installers run before the upgraded app has moved %APPDATA%\ClaudeBuddy,
    // so the saved extra profiles must still be found there.
    [WindowsInstallerTheory]
    [MemberData(nameof(WindowsShells))]
    public void WindowsClaudeCode_ReadsSavedProfilesFromTheLegacyDataDir_WhenTheNewOneIsAbsent(string shell)
    {
        using var s = new Scratch();
        Write(Path.Combine(s.AppData, "ClaudeBuddy", "settings.json"),
            new JsonObject { ["claudeCodeProfileDirs"] = new JsonArray(".claude-work") });

        AssertOk(RunPs(shell, "install-windows-hooks.ps1", s));

        var extra = Path.Combine(s.Home, ".claude-work", "settings.json");
        AssertExactlyOurs(Handlers(extra), ClaudeWanted, h => WinClaudeAtNewPath(s, h));
    }

    [WindowsInstallerTheory]
    [MemberData(nameof(WindowsShells))]
    public void WindowsClaudeCode_PrefersTheNewDataDir_OverTheLegacyOne(string shell)
    {
        using var s = new Scratch();
        Write(Path.Combine(s.AppData, "Orbweaver", "settings.json"),
            new JsonObject { ["claudeCodeProfileDirs"] = new JsonArray(".claude-new") });
        Write(Path.Combine(s.AppData, "ClaudeBuddy", "settings.json"),
            new JsonObject { ["claudeCodeProfileDirs"] = new JsonArray(".claude-old") });

        AssertOk(RunPs(shell, "install-windows-hooks.ps1", s));

        Assert.True(File.Exists(Path.Combine(s.Home, ".claude-new", "settings.json")));
        Assert.False(File.Exists(Path.Combine(s.Home, ".claude-old", "settings.json")));
    }

    // ---- Codex, Windows ---------------------------------------------------------

    private static string WinCodexHooks(Scratch s) => Path.Combine(s.Home, ".codex", "hooks.json");
    private static string WinCodexLegacyDir(Scratch s) => Path.Combine(s.Home, ".codex", "claude-buddy");
    private static string WinCodexNewScript(Scratch s) => Path.Combine(s.Home, ".codex", "orbweaver", "OrbweaverHook.ps1");

    private static string WinCodexCommand(string script, string state) =>
        $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Agent codex -State {state} -TempDir \"C:\\t\"";

    private static void SeedWindowsCodex(Scratch s, Seed seed)
    {
        var legacyScript = Path.Combine(WinCodexLegacyDir(s), "ClaudeBuddyHook.ps1");
        var root = SeedConfig(seed, CodexWanted,
            st => Cmd(WinCodexCommand(legacyScript, st), WinCodexCommand(legacyScript, st)),
            st => Cmd(WinCodexCommand(WinCodexNewScript(s), st), WinCodexCommand(WinCodexNewScript(s), st)),
            Cmd(WinCodexCommand(legacyScript.ToLowerInvariant(), "idle")),
            withModel: false);
        var hooks = root["hooks"]!.AsObject();
        // (e) /import-style: the POSIX command is not ours at all; only
        // commandWindows names the legacy script.
        Add(hooks, "SessionStart", Group(null, Cmd("echo imported", WinCodexCommand(legacyScript, "idle"))));
        // An /import from macOS carries the .sh spelling.
        Add(hooks, "Stop", Group(null, Cmd("bash \"$HOME/.claude/claude-buddy/ClaudeBuddyHook.sh\" idle")));
        Write(WinCodexHooks(s), root);
        Touch(legacyScript);
    }

    private static void AssertWindowsCodexWired(Scratch s)
    {
        var handlers = Handlers(WinCodexHooks(s));
        AssertExactlyOurs(handlers, CodexWanted, h =>
            h.Command!.Contains($"-File \"{WinCodexNewScript(s)}\"", StringComparison.Ordinal) &&
            h.CommandWindows == h.Command);
        Assert.True(handlers.Single(h => IsOurs(h) && h.Event == "PermissionRequest").Async);
        Assert.DoesNotContain(handlers, h => h.Command == "echo imported");
        AssertForeignUntouched(handlers);
        Assert.True(File.Exists(WinCodexNewScript(s)));
    }

    [WindowsInstallerTheory]
    [MemberData(nameof(WindowsCases))]
    public void WindowsCodex_RewiresToTheNewScript_AndIsIdempotent(string shell, Seed seed)
    {
        using var s = new Scratch();
        SeedWindowsCodex(s, seed);

        AssertOk(RunPs(shell, "install-codex-hooks.ps1", s));
        AssertWindowsCodexWired(s);
        Assert.True(File.Exists(WinCodexHooks(s) + ".orbweaver-backup"));

        var marker = Path.Combine(WinCodexLegacyDir(s), ".superseded");
        Assert.True(File.Exists(Path.Combine(WinCodexLegacyDir(s), "ClaudeBuddyHook.ps1")));
        Assert.True(File.Exists(marker));

        File.SetLastWriteTimeUtc(marker, OldMtime);
        AssertOk(RunPs(shell, "install-codex-hooks.ps1", s));
        AssertWindowsCodexWired(s);
        Assert.Equal(OldMtime, File.GetLastWriteTimeUtc(marker));
    }

    [WindowsInstallerTheory]
    [MemberData(nameof(WindowsShells))]
    public void WindowsCodex_Uninstall_RemovesBothNames_KeepsForeignAndBothFolders(string shell)
    {
        using var s = new Scratch();
        SeedWindowsCodex(s, Seed.Both);
        AssertOk(RunPs(shell, "install-codex-hooks.ps1", s));
        SeedWindowsCodex(s, Seed.Both);

        AssertOk(RunPs(shell, "install-codex-hooks.ps1", s, "-Uninstall"));

        var handlers = Handlers(WinCodexHooks(s));
        Assert.DoesNotContain(handlers, IsOurs);
        AssertForeignUntouched(handlers);
        Assert.True(Directory.Exists(Path.GetDirectoryName(WinCodexNewScript(s))));
        Assert.True(Directory.Exists(WinCodexLegacyDir(s)));
    }

    // ---- Grok, Windows ---------------------------------------------------------

    private static string WinGrokHooksDir(Scratch s) => Path.Combine(s.Home, ".grok", "hooks");
    private static string WinGrokLegacyDir(Scratch s) => Path.Combine(s.Home, ".grok", "claude-buddy");
    private const string ForeignGrokFile = "other-tool.json";

    private static void SeedGrok(string hooksDir, string legacyDir, string legacyScriptName)
    {
        Touch(Path.Combine(hooksDir, "claude-buddy.json"), "{\"hooks\":{}}");
        Touch(Path.Combine(hooksDir, ForeignGrokFile), "{\"hooks\":{\"Stop\":[]}}");
        Touch(Path.Combine(legacyDir, legacyScriptName));
    }

    private static void AssertGrokWired(string hooksDir, Func<string, bool> atNewPath)
    {
        var handlers = Handlers(Path.Combine(hooksDir, "orbweaver.json"));
        Assert.Equal(6, handlers.Count);
        Assert.All(handlers, h => Assert.True(atNewPath(h.Command!), h.ToString()));
        Assert.False(File.Exists(Path.Combine(hooksDir, "claude-buddy.json")),
            "Grok loads every hooks/*.json; the legacy file left behind fires every event twice");
        Assert.Equal("{\"hooks\":{\"Stop\":[]}}", File.ReadAllText(Path.Combine(hooksDir, ForeignGrokFile)));
    }

    [WindowsInstallerTheory]
    [MemberData(nameof(WindowsShells))]
    public void WindowsGrok_WritesTheNewFile_RemovesTheLegacyOne_AndIsIdempotent(string shell)
    {
        using var s = new Scratch();
        SeedGrok(WinGrokHooksDir(s), WinGrokLegacyDir(s), "ClaudeBuddyHook.ps1");
        var newScript = Path.Combine(s.Home, ".grok", "orbweaver", "OrbweaverHook.ps1");
        bool AtNew(string c) => c.Contains($"-File \"{newScript}\"", StringComparison.Ordinal);

        AssertOk(RunPs(shell, "install-grok-hooks.ps1", s));
        AssertGrokWired(WinGrokHooksDir(s), AtNew);
        Assert.True(File.Exists(newScript));
        var marker = Path.Combine(WinGrokLegacyDir(s), ".superseded");
        Assert.True(File.Exists(marker));
        Assert.True(File.Exists(Path.Combine(WinGrokLegacyDir(s), "ClaudeBuddyHook.ps1")));

        File.SetLastWriteTimeUtc(marker, OldMtime);
        AssertOk(RunPs(shell, "install-grok-hooks.ps1", s));
        AssertGrokWired(WinGrokHooksDir(s), AtNew);
        Assert.Equal(OldMtime, File.GetLastWriteTimeUtc(marker));
    }

    [WindowsInstallerTheory]
    [MemberData(nameof(WindowsShells))]
    public void WindowsGrok_Uninstall_RemovesBothFiles_KeepsForeign(string shell)
    {
        using var s = new Scratch();
        AssertOk(RunPs(shell, "install-grok-hooks.ps1", s));
        SeedGrok(WinGrokHooksDir(s), WinGrokLegacyDir(s), "ClaudeBuddyHook.ps1");

        AssertOk(RunPs(shell, "install-grok-hooks.ps1", s, "-Uninstall"));

        Assert.False(File.Exists(Path.Combine(WinGrokHooksDir(s), "orbweaver.json")));
        Assert.False(File.Exists(Path.Combine(WinGrokHooksDir(s), "claude-buddy.json")));
        Assert.True(File.Exists(Path.Combine(WinGrokHooksDir(s), ForeignGrokFile)));
        Assert.True(Directory.Exists(WinGrokLegacyDir(s)));
    }

    // ---- the umbrella, Windows -----------------------------------------------

    // install-hooks.ps1 names no hook file itself; what matters is that it still
    // drives all three sub-installers to the new layout. Install mode only: its
    // -Uninstall forwards to install-windows-hooks.ps1 without a -WslDistro and
    // would sweep this machine's real WSL distros.
    [WindowsInstallerTheory]
    [MemberData(nameof(WindowsShells))]
    public void WindowsUmbrella_WiresEveryCliToTheNewScript(string shell)
    {
        using var s = new Scratch();
        SeedWindowsClaude(s, Seed.Legacy);
        SeedWindowsCodex(s, Seed.Legacy);
        SeedGrok(WinGrokHooksDir(s), WinGrokLegacyDir(s), "ClaudeBuddyHook.ps1");

        AssertOk(RunPs(shell, "install-hooks.ps1", s));

        AssertWindowsClaudeWired(s);
        AssertWindowsCodexWired(s);
        var grokScript = Path.Combine(s.Home, ".grok", "orbweaver", "OrbweaverHook.ps1");
        AssertGrokWired(WinGrokHooksDir(s), c => c.Contains(grokScript, StringComparison.Ordinal));
    }

    // ---- macOS rigs ------------------------------------------------------------

    public static IEnumerable<object[]> Seeds() =>
        new[] { new object[] { Seed.Legacy }, new object[] { Seed.New }, new object[] { Seed.Both } };

    private static Result RunSh(string script, Scratch s, params string[] args)
    {
        var all = new List<string> { Tool(script) };
        all.AddRange(args);
        return Run("/bin/bash", all, new Dictionary<string, string?>
        {
            ["HOME"] = s.Home,
            ["CODEX_HOME"] = null,
            ["GROK_HOME"] = null,
            ["CLAUDE_CONFIG_DIR"] = null,
            // Unset for the reason WindowsEnv gives: with it set, the installer
            // never reaches the Orbweaver-then-ClaudeBuddy fallback under test.
            [BrandEnv.Name(BrandEnv.SettingsDir)] = null,
            [BrandEnv.LegacyPrefix + BrandEnv.SettingsDir] = null,
        });
    }

    private static string MacSupport(Scratch s, string dataDir) =>
        Path.Combine(s.Home, "Library", "Application Support", dataDir, "settings.json");

    private const string MacClaudeLegacyScript = "\"$HOME/.claude/claude-buddy/ClaudeBuddyHook.sh\"";
    private const string MacClaudeNewScript = "\"$HOME/.claude/orbweaver/OrbweaverHook.sh\"";

    private static string MacClaudeSettings(Scratch s) => Path.Combine(s.Home, ".claude", "settings.json");
    private static string MacClaudeLegacyDir(Scratch s) => Path.Combine(s.Home, ".claude", "claude-buddy");

    private static void SeedMacClaude(Scratch s, Seed seed)
    {
        Write(MacClaudeSettings(s), SeedConfig(seed, ClaudeWanted,
            st => Cmd($"bash {MacClaudeLegacyScript} {st}"),
            st => Cmd($"bash {MacClaudeNewScript} {st}"),
            Cmd($"bash {MacClaudeLegacyScript} idle"),
            withModel: true));
        Touch(Path.Combine(MacClaudeLegacyDir(s), "ClaudeBuddyHook.sh"));
    }

    private static void AssertMacClaudeWired(Scratch s, string settings)
    {
        var handlers = Handlers(settings);
        AssertExactlyOurs(handlers, ClaudeWanted, h => h.Command!.StartsWith($"bash {MacClaudeNewScript} ", StringComparison.Ordinal));
    }

    [MacInstallerTheory]
    [MemberData(nameof(Seeds))]
    public void MacClaudeCode_RewiresToTheNewScript_AndIsIdempotent(Seed seed)
    {
        using var s = new Scratch();
        SeedMacClaude(s, seed);

        AssertOk(RunSh("install-macos-hooks.sh", s));
        AssertMacClaudeWired(s, MacClaudeSettings(s));
        AssertForeignUntouched(Handlers(MacClaudeSettings(s)));
        Assert.Equal("opus", JsonNode.Parse(File.ReadAllText(MacClaudeSettings(s)))!["model"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(s.Home, ".claude", "orbweaver", "OrbweaverHook.sh")));
        Assert.True(File.Exists(MacClaudeSettings(s) + ".orbweaver-backup"));

        var marker = Path.Combine(MacClaudeLegacyDir(s), ".superseded");
        Assert.True(File.Exists(Path.Combine(MacClaudeLegacyDir(s), "ClaudeBuddyHook.sh")));
        Assert.True(File.Exists(marker));

        File.SetLastWriteTimeUtc(marker, OldMtime);
        AssertOk(RunSh("install-macos-hooks.sh", s));
        AssertMacClaudeWired(s, MacClaudeSettings(s));
        AssertForeignUntouched(Handlers(MacClaudeSettings(s)));
        Assert.Equal(OldMtime, File.GetLastWriteTimeUtc(marker));
    }

    [MacInstallerFact]
    public void MacClaudeCode_Uninstall_RemovesBothNames_KeepsForeignAndBothFolders()
    {
        using var s = new Scratch();
        SeedMacClaude(s, Seed.Both);
        AssertOk(RunSh("install-macos-hooks.sh", s));
        SeedMacClaude(s, Seed.Both);

        AssertOk(RunSh("install-macos-hooks.sh", s, "--uninstall"));

        var handlers = Handlers(MacClaudeSettings(s));
        Assert.DoesNotContain(handlers, IsOurs);
        AssertForeignUntouched(handlers);
        Assert.True(Directory.Exists(Path.Combine(s.Home, ".claude", "orbweaver")));
        Assert.True(Directory.Exists(MacClaudeLegacyDir(s)));
    }

    [MacInstallerFact]
    public void MacClaudeCode_FreshMachine_GrowsNoLegacyFolder()
    {
        using var s = new Scratch();
        AssertOk(RunSh("install-macos-hooks.sh", s));
        AssertMacClaudeWired(s, MacClaudeSettings(s));
        Assert.False(Directory.Exists(MacClaudeLegacyDir(s)));
    }

    // CB-291: the same promise on a Mac — the skill beside every account's
    // settings.json, replaced on reinstall, and only that folder removed.
    [MacInstallerFact]
    public void MacClaudeCode_InstallsTheTeamSkillForEveryAccount_AndUninstallRemovesOnlyThat()
    {
        using var s = new Scratch();
        Write(MacSupport(s, "Orbweaver"), new JsonObject { ["claudeCodeProfileDirs"] = new JsonArray(".claude-work") });
        var neighbour = Path.Combine(s.Home, ".claude", "skills", "someone-elses", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(neighbour)!);
        File.WriteAllText(neighbour, "not ours");

        AssertOk(RunSh("install-macos-hooks.sh", s));

        foreach (var account in new[] { ".claude", ".claude-work" })
        {
            var skill = Path.Combine(s.Home, account, "skills", "orbweaver-team");
            Assert.True(File.Exists(Path.Combine(skill, "SKILL.md")), account);
            Assert.True(File.Exists(Path.Combine(skill, "FORMATS.md")), account);
            Assert.Equal(4, Directory.GetFiles(Path.Combine(skill, "roles"), "*.md").Length);
            Assert.True(
                (File.GetUnixFileMode(Path.Combine(skill, "orbweaver-team.sh")) & UnixFileMode.UserExecute) != 0,
                account + ": the resolver should be executable");
        }

        var stale = Path.Combine(s.Home, ".claude", "skills", "orbweaver-team", "left-by-an-older-version.txt");
        File.WriteAllText(stale, "stale");
        AssertOk(RunSh("install-macos-hooks.sh", s));
        Assert.False(File.Exists(stale));

        AssertOk(RunSh("install-macos-hooks.sh", s, "--uninstall"));

        foreach (var account in new[] { ".claude", ".claude-work" })
            Assert.False(Directory.Exists(Path.Combine(s.Home, account, "skills", "orbweaver-team")), account);
        Assert.True(File.Exists(neighbour));
    }

    [MacInstallerFact]
    public void MacClaudeCode_ReadsSavedProfilesFromTheLegacyDataDir_WhenTheNewOneIsAbsent()
    {
        using var s = new Scratch();
        Write(MacSupport(s, "ClaudeBuddy"), new JsonObject { ["claudeCodeProfileDirs"] = new JsonArray(".claude-work") });

        AssertOk(RunSh("install-macos-hooks.sh", s));

        AssertMacClaudeWired(s, Path.Combine(s.Home, ".claude-work", "settings.json"));
    }

    [MacInstallerFact]
    public void MacClaudeCode_PrefersTheNewDataDir_OverTheLegacyOne()
    {
        using var s = new Scratch();
        Write(MacSupport(s, "Orbweaver"), new JsonObject { ["claudeCodeProfileDirs"] = new JsonArray(".claude-new") });
        Write(MacSupport(s, "ClaudeBuddy"), new JsonObject { ["claudeCodeProfileDirs"] = new JsonArray(".claude-old") });

        AssertOk(RunSh("install-macos-hooks.sh", s));

        Assert.True(File.Exists(Path.Combine(s.Home, ".claude-new", "settings.json")));
        Assert.False(File.Exists(Path.Combine(s.Home, ".claude-old", "settings.json")));
    }

    // ---- Codex, macOS ----------------------------------------------------------

    private const string MacCodexLegacyScript = "\"$HOME/.codex/claude-buddy/ClaudeBuddyHook.sh\"";
    private const string MacCodexNewScript = "\"$HOME/.codex/orbweaver/OrbweaverHook.sh\"";
    private static string MacCodexHooks(Scratch s) => Path.Combine(s.Home, ".codex", "hooks.json");
    private static string MacCodexLegacyDir(Scratch s) => Path.Combine(s.Home, ".codex", "claude-buddy");

    private static void SeedMacCodex(Scratch s, Seed seed)
    {
        var root = SeedConfig(seed, CodexWanted,
            st => Cmd($"bash {MacCodexLegacyScript} codex {st}"),
            st => Cmd($"bash {MacCodexNewScript} codex {st}"),
            Cmd($"bash {MacCodexLegacyScript} codex idle"),
            withModel: false);
        // (e) /import-style from a Windows setup: only commandWindows is ours.
        Add(root["hooks"]!.AsObject(), "SessionStart", Group(null,
            Cmd("echo imported", "powershell -File \"C:\\Users\\x\\.codex\\claude-buddy\\ClaudeBuddyHook.ps1\" -Agent codex -State idle")));
        // /import from a Claude Code setup points at ~/.claude.
        Add(root["hooks"]!.AsObject(), "Stop", Group(null, Cmd($"bash {MacClaudeLegacyScript} idle")));
        Write(MacCodexHooks(s), root);
        Touch(Path.Combine(MacCodexLegacyDir(s), "ClaudeBuddyHook.sh"));
    }

    private static void AssertMacCodexWired(Scratch s)
    {
        var handlers = Handlers(MacCodexHooks(s));
        AssertExactlyOurs(handlers, CodexWanted, h => h.Command!.StartsWith($"bash {MacCodexNewScript} codex ", StringComparison.Ordinal));
        Assert.True(handlers.Single(h => IsOurs(h) && h.Event == "PermissionRequest").Async);
        Assert.DoesNotContain(handlers, h => h.Command == "echo imported");
        AssertForeignUntouched(handlers);
    }

    [MacInstallerTheory]
    [MemberData(nameof(Seeds))]
    public void MacCodex_RewiresToTheNewScript_AndIsIdempotent(Seed seed)
    {
        using var s = new Scratch();
        SeedMacCodex(s, seed);

        AssertOk(RunSh("install-codex-hooks.sh", s));
        AssertMacCodexWired(s);
        Assert.True(File.Exists(Path.Combine(s.Home, ".codex", "orbweaver", "OrbweaverHook.sh")));
        Assert.True(File.Exists(MacCodexHooks(s) + ".orbweaver-backup"));

        var marker = Path.Combine(MacCodexLegacyDir(s), ".superseded");
        Assert.True(File.Exists(Path.Combine(MacCodexLegacyDir(s), "ClaudeBuddyHook.sh")));
        Assert.True(File.Exists(marker));

        File.SetLastWriteTimeUtc(marker, OldMtime);
        AssertOk(RunSh("install-codex-hooks.sh", s));
        AssertMacCodexWired(s);
        Assert.Equal(OldMtime, File.GetLastWriteTimeUtc(marker));
    }

    [MacInstallerFact]
    public void MacCodex_Uninstall_RemovesBothNames_KeepsForeignAndBothFolders()
    {
        using var s = new Scratch();
        SeedMacCodex(s, Seed.Both);
        AssertOk(RunSh("install-codex-hooks.sh", s));
        SeedMacCodex(s, Seed.Both);

        AssertOk(RunSh("install-codex-hooks.sh", s, "--uninstall"));

        var handlers = Handlers(MacCodexHooks(s));
        Assert.DoesNotContain(handlers, IsOurs);
        AssertForeignUntouched(handlers);
        Assert.True(Directory.Exists(Path.Combine(s.Home, ".codex", "orbweaver")));
        Assert.True(Directory.Exists(MacCodexLegacyDir(s)));
    }

    // ---- Grok, macOS -----------------------------------------------------------

    private static string MacGrokHooksDir(Scratch s) => Path.Combine(s.Home, ".grok", "hooks");
    private static string MacGrokLegacyDir(Scratch s) => Path.Combine(s.Home, ".grok", "claude-buddy");
    private static bool MacGrokAtNew(string c) => c.StartsWith("bash \"$HOME/.grok/orbweaver/OrbweaverHook.sh\" grok ", StringComparison.Ordinal);

    [MacInstallerFact]
    public void MacGrok_WritesTheNewFile_RemovesTheLegacyOne_AndIsIdempotent()
    {
        using var s = new Scratch();
        SeedGrok(MacGrokHooksDir(s), MacGrokLegacyDir(s), "ClaudeBuddyHook.sh");

        AssertOk(RunSh("install-grok-hooks.sh", s));
        AssertGrokWired(MacGrokHooksDir(s), MacGrokAtNew);
        Assert.True(File.Exists(Path.Combine(s.Home, ".grok", "orbweaver", "OrbweaverHook.sh")));
        var marker = Path.Combine(MacGrokLegacyDir(s), ".superseded");
        Assert.True(File.Exists(marker));

        File.SetLastWriteTimeUtc(marker, OldMtime);
        AssertOk(RunSh("install-grok-hooks.sh", s));
        AssertGrokWired(MacGrokHooksDir(s), MacGrokAtNew);
        Assert.Equal(OldMtime, File.GetLastWriteTimeUtc(marker));
    }

    [MacInstallerFact]
    public void MacGrok_Uninstall_RemovesBothFiles_KeepsForeign()
    {
        using var s = new Scratch();
        AssertOk(RunSh("install-grok-hooks.sh", s));
        SeedGrok(MacGrokHooksDir(s), MacGrokLegacyDir(s), "ClaudeBuddyHook.sh");

        AssertOk(RunSh("install-grok-hooks.sh", s, "--uninstall"));

        Assert.False(File.Exists(Path.Combine(MacGrokHooksDir(s), "orbweaver.json")));
        Assert.False(File.Exists(Path.Combine(MacGrokHooksDir(s), "claude-buddy.json")));
        Assert.True(File.Exists(Path.Combine(MacGrokHooksDir(s), ForeignGrokFile)));
        Assert.True(Directory.Exists(MacGrokLegacyDir(s)));
    }

    // Regression: the profile loop passed ${UNINSTALL:+--uninstall}, which
    // expands for UNINSTALL=0 too, so an install removed every extra Grok
    // home's hooks file instead of writing it. Also the legacy data-dir read.
    [MacInstallerFact]
    public void MacGrok_InstallWiresSavedProfiles_ReadFromTheLegacyDataDir()
    {
        using var s = new Scratch();
        Write(MacSupport(s, "ClaudeBuddy"), new JsonObject { ["grokHomes"] = new JsonArray(".grok-work") });

        AssertOk(RunSh("install-grok-hooks.sh", s));

        Assert.True(File.Exists(Path.Combine(s.Home, ".grok-work", "hooks", "orbweaver.json")));
    }
}

// The Windows installers resolve %LOCALAPPDATA%/%APPDATA%/%USERPROFILE%, so they
// only mean anything on Windows. Both shells are required there: a missing
// pwsh fails rather than skips, as in SapiSpeakScriptTests.
public sealed class WindowsInstallerTheoryAttribute : TheoryAttribute
{
    public WindowsInstallerTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "the Windows hook installers only run on Windows";
    }
}

// The macOS installers' JSON surgery is JavaScript for Automation, which is
// osascript, which is macOS's. bash alone (Linux, Git Bash) cannot run them.
public sealed class MacInstallerFactAttribute : FactAttribute
{
    public MacInstallerFactAttribute() { Skip = MacInstallerSkip.Reason(); }
}

public sealed class MacInstallerTheoryAttribute : TheoryAttribute
{
    public MacInstallerTheoryAttribute() { Skip = MacInstallerSkip.Reason(); }
}

internal static class MacInstallerSkip
{
    internal static string? Reason()
    {
        if (!OperatingSystem.IsMacOS()) return "the macOS hook installers need osascript (JavaScript for Automation)";
        return File.Exists("/usr/bin/osascript") ? null : "no /usr/bin/osascript on this machine";
    }
}
