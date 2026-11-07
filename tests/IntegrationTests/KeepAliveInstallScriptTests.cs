using System.Diagnostics;
using System.Xml;
using Xunit;

namespace Orbweaver.Tests;

// CB-49: tools/install-hooks.sh is also where the crash keep-alive
// LaunchAgent gets written and torn down, on macOS -- see that script's own
// header comment for why it lives there rather than in a separate tool. This
// drives it as a real subprocess, the same pattern HookScriptShTests uses
// for OrbweaverHook.sh, and stops short of the one thing that genuinely
// can't be exercised headlessly: a real `launchctl load`/`unload` against
// this machine's actual launchd session. ORBWEAVER_KEEPALIVE_DRY_RUN
// suppresses that call so these can run on CI without registering or
// unregistering anything real; ORBWEAVER_LAUNCHAGENTS_DIR and
// ORBWEAVER_KEEPALIVE_APP_CANDIDATES redirect the plist path and the
// "which app is installed" search away from this machine's real
// ~/Library/LaunchAgents and /Applications, the same test-seam pattern
// ORBWEAVER_SETTINGS_DIR already uses elsewhere in this repo.
//
// CB-256: the script reads each of those four through `brand_env`, which
// falls back to the pre-rename CLAUDE_BUDDY_ spelling. Every case here sets
// the new spelling; the legacy one appears only in the cases that pin the
// fallback, one per variable, plus one where both are set and the new wins.
// The executable inside the bundle was renamed too (Contents/MacOS/Orbweaver,
// was ClaudeBuddy), so the resolver's cases cover a bundle holding either.
//
// Not covered here, and named rather than silently skipped: an actual
// `launchctl load` succeeding or failing against a real launchd session, and
// the Windows Scheduled Task side of CB-49 (tools/Orbweaver.iss), which
// needs a real Inno Setup compile and a real Windows Task Scheduler to
// verify and which this environment has neither of.
public class KeepAliveInstallScriptTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string Script = Path.Combine(RepoRoot, "tools", "install-hooks.sh");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tools", "install-hooks.sh")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find tools/install-hooks.sh by walking up from " + AppContext.BaseDirectory);
    }

    private sealed record ScriptResult(int ExitCode, string Stdout, string Stderr);

    // The four variables install-hooks.sh reads through brand_env.
    private static readonly string[] Shimmed =
        ["SETTINGS_DIR", "LAUNCHAGENTS_DIR", "KEEPALIVE_DRY_RUN", "KEEPALIVE_APP_CANDIDATES"];

    // The child inherits this process's environment, and TestBootstrap has
    // set a settings dir in it (under one spelling or the other, depending on
    // which unit landed first) — and a developer may have either exported in
    // the shell running the suite. So both spellings of all four shimmed
    // variables are removed first, and a case gets exactly the ones it names.
    // A null value removes a variable outright.
    private static ScriptResult Run(string[] args, IDictionary<string, string?>? env = null) =>
        RunScript(Script, args, env);

    // Any copy of the script — the repo's, or one planted inside a fake bundle.
    private static ScriptResult RunScript(string script, string[] args, IDictionary<string, string?>? env = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(script);
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var name in Shimmed)
        {
            psi.Environment.Remove("ORBWEAVER_" + name);
            psi.Environment.Remove("CLAUDE_BUDDY_" + name);
        }
        if (env is not null)
            foreach (var (key, value) in env)
                if (value is null) psi.Environment.Remove(key);
                else psi.Environment[key] = value;

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return new ScriptResult(proc.ExitCode, stdout, stderr);
    }

    [MacKeepAliveFact]
    public void PrintKeepalivePlist_NamesBundleIdThrottleAndExecutable()
    {
        var result = Run(["--print-keepalive-plist", "/Applications/Orbweaver.app/Contents/MacOS/Orbweaver"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
        // Must match build-macos-app.sh's BUNDLE_ID -- a mismatch here would
        // mean the plist install-hooks.sh writes doesn't reconcile with any
        // app build-macos-app.sh actually installs. Unchanged by the CB-255
        // rename, deliberately: the LaunchAgent's label is the bundle id.
        Assert.Contains("<string>io.github.wtvamp.claudebuddy</string>", result.Stdout);
        Assert.Contains("<integer>60</integer>", result.Stdout);
        Assert.Contains("/Applications/Orbweaver.app/Contents/MacOS/Orbweaver", result.Stdout);
    }

    // CB-255: the keep-alive's log goes beside the app's own logs, which moved
    // from Logs/ClaudeBuddy to Logs/Orbweaver.
    [MacKeepAliveFact]
    public void PrintKeepalivePlist_LogsUnderTheOrbweaverLogsFolder()
    {
        var result = Run(["--print-keepalive-plist", "/tmp/fake/Orbweaver"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/Library/Logs/Orbweaver/keepalive.log</string>", result.Stdout);
        Assert.DoesNotContain("Logs/ClaudeBuddy", result.Stdout);
    }

    // ---- CB-255: which settings.json, with no override set ------------------
    //
    // HOME is a temp folder and ORBWEAVER_SETTINGS_DIR is removed, so the
    // script reads Application Support under the fake home exactly as it would
    // on a real Mac. The app candidates stay pinned to a fake bundle so only
    // the settings path varies.

    private static string Support(string home, string folder) =>
        Path.Combine(home, "Library", "Application Support", folder);

    private static void WriteSettings(string home, string folder, bool serve)
    {
        Directory.CreateDirectory(Support(home, folder));
        File.WriteAllText(Path.Combine(Support(home, folder), "settings.json"),
            $"{{ \"remoteControlServeOnLaunch\": {(serve ? "true" : "false")} }}");
    }

    private static ScriptResult KeepaliveOnlyUnderHome(string home, string launchAgents, string app) =>
        Run(["--keepalive-only"], new Dictionary<string, string?>
        {
            ["HOME"] = home,
            ["ORBWEAVER_LAUNCHAGENTS_DIR"] = launchAgents,
            ["ORBWEAVER_KEEPALIVE_DRY_RUN"] = "1",
            ["ORBWEAVER_KEEPALIVE_APP_CANDIDATES"] = app,
        });

    // Upgrade day: the app has not launched since the rename, so its settings
    // are still in the legacy folder. Reading only the new one would take
    // Serve on launch as off and tear the user's keep-alive down.
    [MacKeepAliveFact]
    public void KeepaliveOnly_ReadsTheLegacySettingsWhenTheOrbweaverFolderHasNone()
    {
        using var home = new TempDir();
        using var launchAgentsDir = new TempDir();
        var exePath = WriteFakeAppBundle(home.Path);
        WriteSettings(home.Path, "ClaudeBuddy", serve: true);

        var result = KeepaliveOnlyUnderHome(home.Path, launchAgentsDir.Path, Path.Combine(home.Path, "Fake.app"));

        Assert.Equal(0, result.ExitCode);
        var plistPath = Path.Combine(launchAgentsDir.Path, "io.github.wtvamp.claudebuddy.plist");
        Assert.True(File.Exists(plistPath), result.Stdout + result.Stderr);
        Assert.Contains(exePath, File.ReadAllText(plistPath));
    }

    // Both folders have settings: the new one wins, as it does in the app's
    // own migration. Its "false" removes a stale agent although the legacy
    // file still says true.
    [MacKeepAliveFact]
    public void KeepaliveOnly_TheOrbweaverSettingsWinOverTheLegacyOnes()
    {
        using var home = new TempDir();
        using var launchAgentsDir = new TempDir();
        WriteFakeAppBundle(home.Path);
        WriteSettings(home.Path, "ClaudeBuddy", serve: true);
        WriteSettings(home.Path, "Orbweaver", serve: false);
        var stalePlist = Path.Combine(launchAgentsDir.Path, "io.github.wtvamp.claudebuddy.plist");
        File.WriteAllText(stalePlist, "<stale/>");

        var result = KeepaliveOnlyUnderHome(home.Path, launchAgentsDir.Path, Path.Combine(home.Path, "Fake.app"));

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(stalePlist), "the legacy settings were read although the Orbweaver ones exist");
    }

    // The control for the two above: the new folder alone is read, so a pass
    // there cannot come from the script reading only the legacy one.
    [MacKeepAliveFact]
    public void KeepaliveOnly_ReadsTheOrbweaverSettings()
    {
        using var home = new TempDir();
        using var launchAgentsDir = new TempDir();
        WriteFakeAppBundle(home.Path);
        WriteSettings(home.Path, "Orbweaver", serve: true);

        var result = KeepaliveOnlyUnderHome(home.Path, launchAgentsDir.Path, Path.Combine(home.Path, "Fake.app"));

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(launchAgentsDir.Path, "io.github.wtvamp.claudebuddy.plist")));
    }

    // CB-255: with no candidate override, Orbweaver.app is looked for before
    // Claude Buddy.app. Both are planted under the fake home's Applications;
    // the assertion allows a real /Applications/Orbweaver.app on the machine
    // running the suite to win (it is ahead of ~/Applications), but never a
    // Claude Buddy.app while an Orbweaver.app exists.
    [MacKeepAliveFact]
    public void KeepaliveOnly_PrefersOrbweaverAppOverTheLegacyBundle()
    {
        using var home = new TempDir();
        using var launchAgentsDir = new TempDir();
        var applications = Path.Combine(home.Path, "Applications");
        WriteFakeAppBundle(applications, "Claude Buddy.app");
        WriteFakeAppBundle(applications, "Orbweaver.app");
        WriteSettings(home.Path, "Orbweaver", serve: true);

        var result = Run(["--keepalive-only"], new Dictionary<string, string?>
        {
            ["HOME"] = home.Path,
            ["ORBWEAVER_LAUNCHAGENTS_DIR"] = launchAgentsDir.Path,
            ["ORBWEAVER_KEEPALIVE_DRY_RUN"] = "1",
        });

        Assert.Equal(0, result.ExitCode);
        var plist = File.ReadAllText(Path.Combine(launchAgentsDir.Path, "io.github.wtvamp.claudebuddy.plist"));
        Assert.Contains("/Orbweaver.app/Contents/MacOS/Orbweaver", plist);
        Assert.DoesNotContain("Claude Buddy.app", plist);
    }

    [MacKeepAliveFact]
    public void PrintKeepalivePlist_RestartsOnCrashOnly_NotOnADeliberateQuit()
    {
        var result = Run(["--print-keepalive-plist", "/tmp/fake/Orbweaver"]);

        // KeepAlive as a dict with SuccessfulExit=false, not a bare <true/>:
        // a bare KeepAlive relaunches after ANY exit, including the app's own
        // normal Quit (App.axaml.cs's Shutdown(), which exits 0) -- exactly
        // the "app that will not stay quit" CB-49 itself warns against.
        Assert.Contains("<key>SuccessfulExit</key>", result.Stdout);
        Assert.Contains("<false/>", result.Stdout);
    }

    [MacKeepAliveFact]
    public void PrintKeepalivePlist_AbandonsTheProcessGroup_SoTheUpdaterFinisherSurvivesAppExit()
    {
        // CB-308: launchd otherwise kills everything left in the app's process
        // group when the app exits, and the update finisher is launched from
        // the app.
        var result = Run(["--print-keepalive-plist", "/tmp/fake/Orbweaver"]);

        Assert.Equal(0, result.ExitCode);
        var doc = System.Xml.Linq.XDocument.Parse(
            result.Stdout.Replace("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">", ""));
        var keys = doc.Descendants("dict").First().Elements().ToList();
        var at = keys.FindIndex(e => e.Name == "key" && e.Value == "AbandonProcessGroup");
        Assert.True(at >= 0, "AbandonProcessGroup key missing");
        Assert.Equal("true", keys[at + 1].Name.LocalName);
        // Control: a neighbouring boolean-less key is not mistaken for it.
        var throttle = keys.FindIndex(e => e.Name == "key" && e.Value == "ThrottleInterval");
        Assert.Equal("integer", keys[throttle + 1].Name.LocalName);
    }

    [MacKeepAliveFact]
    public void PrintKeepalivePlist_IsWellFormedXml()
    {
        var result = Run(["--print-keepalive-plist", "/tmp/fake/Orbweaver"]);

        Assert.Equal(0, result.ExitCode);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(result.Stdout), settings);
        while (reader.Read()) { } // throws XmlException on malformed input
    }

    [MacKeepAliveFact]
    public void KeepaliveOnly_WhenServeOnLaunchIsFalse_RemovesAnyStaleAgentAndWritesNothing()
    {
        using var settingsDir = new TempDir();
        using var launchAgentsDir = new TempDir();
        File.WriteAllText(Path.Combine(settingsDir.Path, "settings.json"),
            "{ \"remoteControlServeOnLaunch\": false }");
        var stalePlist = Path.Combine(launchAgentsDir.Path, "io.github.wtvamp.claudebuddy.plist");
        File.WriteAllText(stalePlist, "<stale/>");

        var result = Run(["--keepalive-only"], new Dictionary<string, string?>
        {
            ["ORBWEAVER_SETTINGS_DIR"] = settingsDir.Path,
            ["ORBWEAVER_LAUNCHAGENTS_DIR"] = launchAgentsDir.Path,
            ["ORBWEAVER_KEEPALIVE_DRY_RUN"] = "1",
        });

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(stalePlist),
            "turning the setting off should remove a stale agent instead of leaving it registered");
    }

    [MacKeepAliveFact]
    public void KeepaliveOnly_WhenSettingsFileIsMissing_DoesNotEnableIt()
    {
        // A fresh install has no settings.json at all yet -- this is the
        // default-off case the ticket asks for, distinct from an explicit
        // "false".
        using var settingsDir = new TempDir();
        using var launchAgentsDir = new TempDir();
        using var fakeApp = new TempDir();
        WriteFakeAppBundle(fakeApp.Path);

        var result = Run(["--keepalive-only"], new Dictionary<string, string?>
        {
            ["ORBWEAVER_SETTINGS_DIR"] = settingsDir.Path,
            ["ORBWEAVER_LAUNCHAGENTS_DIR"] = launchAgentsDir.Path,
            ["ORBWEAVER_KEEPALIVE_DRY_RUN"] = "1",
            ["ORBWEAVER_KEEPALIVE_APP_CANDIDATES"] = Path.Combine(fakeApp.Path, "Fake.app"),
        });

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(launchAgentsDir.Path, "io.github.wtvamp.claudebuddy.plist")));
    }

    [MacKeepAliveFact]
    public void KeepaliveOnly_WhenServeOnLaunchIsTrue_AndAnAppIsFound_WritesAnAgentPointingAtIt()
    {
        using var settingsDir = new TempDir();
        using var launchAgentsDir = new TempDir();
        using var fakeApp = new TempDir();
        var exePath = WriteFakeAppBundle(fakeApp.Path);
        File.WriteAllText(Path.Combine(settingsDir.Path, "settings.json"),
            "{ \"remoteControlServeOnLaunch\": true }");

        var result = Run(["--keepalive-only"], new Dictionary<string, string?>
        {
            ["ORBWEAVER_SETTINGS_DIR"] = settingsDir.Path,
            ["ORBWEAVER_LAUNCHAGENTS_DIR"] = launchAgentsDir.Path,
            ["ORBWEAVER_KEEPALIVE_DRY_RUN"] = "1",
            ["ORBWEAVER_KEEPALIVE_APP_CANDIDATES"] = Path.Combine(fakeApp.Path, "Fake.app"),
        });

        Assert.Equal(0, result.ExitCode);
        var plistPath = Path.Combine(launchAgentsDir.Path, "io.github.wtvamp.claudebuddy.plist");
        Assert.True(File.Exists(plistPath));
        Assert.Contains(exePath, File.ReadAllText(plistPath));
    }

    [MacKeepAliveFact]
    public void KeepaliveOnly_WhenServeOnLaunchIsTrue_AndNoAppIsFound_SkipsWithoutError()
    {
        using var settingsDir = new TempDir();
        using var launchAgentsDir = new TempDir();
        File.WriteAllText(Path.Combine(settingsDir.Path, "settings.json"),
            "{ \"remoteControlServeOnLaunch\": true }");

        var result = Run(["--keepalive-only"], new Dictionary<string, string?>
        {
            ["ORBWEAVER_SETTINGS_DIR"] = settingsDir.Path,
            ["ORBWEAVER_LAUNCHAGENTS_DIR"] = launchAgentsDir.Path,
            ["ORBWEAVER_KEEPALIVE_DRY_RUN"] = "1",
            // A candidate list pointing nowhere real, rather than the
            // hard-coded /Applications default -- otherwise this test's
            // result depends on whether the machine running it happens to
            // have Claude Buddy.app installed.
            ["ORBWEAVER_KEEPALIVE_APP_CANDIDATES"] =
                Path.Combine(launchAgentsDir.Path, "Nowhere.app"),
        });

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(launchAgentsDir.Path, "io.github.wtvamp.claudebuddy.plist")));
    }

    [MacKeepAliveFact]
    public void Uninstall_RemovesTheAgentEvenWhenServeOnLaunchIsStillTrue()
    {
        // Uninstalling the CLI hooks (--uninstall) is this repo's uninstall
        // path (see the DMG's "Read Me First.txt" and install-hooks.sh's own
        // header) -- the agent has to come out here regardless of what the
        // setting currently says, since the app it points at is on its way
        // out too.
        using var settingsDir = new TempDir();
        using var launchAgentsDir = new TempDir();
        File.WriteAllText(Path.Combine(settingsDir.Path, "settings.json"),
            "{ \"remoteControlServeOnLaunch\": true }");
        var plistPath = Path.Combine(launchAgentsDir.Path, "io.github.wtvamp.claudebuddy.plist");
        File.WriteAllText(plistPath, "<placeholder/>");

        var result = Run(["--keepalive-only", "--uninstall"], new Dictionary<string, string?>
        {
            ["ORBWEAVER_SETTINGS_DIR"] = settingsDir.Path,
            ["ORBWEAVER_LAUNCHAGENTS_DIR"] = launchAgentsDir.Path,
            ["ORBWEAVER_KEEPALIVE_DRY_RUN"] = "1",
        });

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(plistPath));
    }

    // ---- CB-256: the executable rename, as the resolver sees it ------------

    private static string PlistIn(string launchAgents) =>
        Path.Combine(launchAgents, "io.github.wtvamp.claudebuddy.plist");

    private static string ServingSettings(string dir, bool serve = true)
    {
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            $"{{ \"remoteControlServeOnLaunch\": {(serve ? "true" : "false")} }}");
        return dir;
    }

    // A phase-2 Orbweaver.app holds Contents/MacOS/ClaudeBuddy and nothing
    // called Orbweaver. A checkout's copy of the script run against it — a
    // manual `install-hooks.sh --keepalive-only` — has to find it rather than
    // skip a keep-alive the user opted in to.
    [MacKeepAliveFact]
    public void KeepaliveOnly_ACandidateBundleHoldingOnlyClaudeBuddyStillResolves()
    {
        using var settingsDir = new TempDir();
        using var launchAgentsDir = new TempDir();
        using var apps = new TempDir();
        var exePath = WriteFakeAppBundle(apps.Path, "Orbweaver.app", executable: "ClaudeBuddy");

        var result = Run(["--keepalive-only"], new Dictionary<string, string?>
        {
            ["ORBWEAVER_SETTINGS_DIR"] = ServingSettings(settingsDir.Path),
            ["ORBWEAVER_LAUNCHAGENTS_DIR"] = launchAgentsDir.Path,
            ["ORBWEAVER_KEEPALIVE_DRY_RUN"] = "1",
            ["ORBWEAVER_KEEPALIVE_APP_CANDIDATES"] = Path.Combine(apps.Path, "Orbweaver.app"),
        });

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(PlistIn(launchAgentsDir.Path)), result.Stdout + result.Stderr);
        Assert.Contains($"<string>{exePath}</string>", File.ReadAllText(PlistIn(launchAgentsDir.Path)));
    }

    // A bundle holding both names — only a hand-assembled one would — points
    // launchd at the new executable, which is the one a phase-3 build runs.
    [MacKeepAliveFact]
    public void KeepaliveOnly_PrefersTheOrbweaverExecutableWithinABundle()
    {
        using var settingsDir = new TempDir();
        using var launchAgentsDir = new TempDir();
        using var apps = new TempDir();
        WriteFakeAppBundle(apps.Path, "Orbweaver.app", executable: "ClaudeBuddy");
        var current = WriteFakeAppBundle(apps.Path, "Orbweaver.app", executable: "Orbweaver");

        var result = Run(["--keepalive-only"], new Dictionary<string, string?>
        {
            ["ORBWEAVER_SETTINGS_DIR"] = ServingSettings(settingsDir.Path),
            ["ORBWEAVER_LAUNCHAGENTS_DIR"] = launchAgentsDir.Path,
            ["ORBWEAVER_KEEPALIVE_DRY_RUN"] = "1",
            ["ORBWEAVER_KEEPALIVE_APP_CANDIDATES"] = Path.Combine(apps.Path, "Orbweaver.app"),
        });

        Assert.Equal(0, result.ExitCode);
        var plist = File.ReadAllText(PlistIn(launchAgentsDir.Path));
        Assert.Contains($"<string>{current}</string>", plist);
        Assert.DoesNotContain("MacOS/ClaudeBuddy", plist);
    }

    // The bundled arm: a copy of the script in Contents/Resources keeps alive
    // the bundle it sits in, whose executable is Contents/MacOS/Orbweaver.
    // The candidates point nowhere, so the plist can only have come from the
    // bundled arm.
    [MacKeepAliveFact]
    public void KeepaliveOnly_FromInsideABundle_PointsAtThatBundlesOrbweaver()
    {
        using var settingsDir = new TempDir();
        using var launchAgentsDir = new TempDir();
        using var apps = new TempDir();
        var exePath = WriteFakeAppBundle(apps.Path, "Elsewhere.app", executable: "Orbweaver");
        var bundled = BundledScript(apps.Path, "Elsewhere.app");

        var result = RunScript(bundled, ["--keepalive-only"], new Dictionary<string, string?>
        {
            ["ORBWEAVER_SETTINGS_DIR"] = ServingSettings(settingsDir.Path),
            ["ORBWEAVER_LAUNCHAGENTS_DIR"] = launchAgentsDir.Path,
            ["ORBWEAVER_KEEPALIVE_DRY_RUN"] = "1",
            ["ORBWEAVER_KEEPALIVE_APP_CANDIDATES"] = Path.Combine(apps.Path, "Nowhere.app"),
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"<string>{exePath}</string>", File.ReadAllText(PlistIn(launchAgentsDir.Path)));
    }

    // Its control: the bundled arm names only the new executable, so a bundle
    // holding just ClaudeBuddy — which no build ships with this script in it —
    // is not taken for the one to keep alive, and with no candidate either
    // the script says so and writes nothing.
    [MacKeepAliveFact]
    public void KeepaliveOnly_FromInsideABundle_DoesNotTakeAClaudeBuddyExecutable()
    {
        using var settingsDir = new TempDir();
        using var launchAgentsDir = new TempDir();
        using var apps = new TempDir();
        WriteFakeAppBundle(apps.Path, "Elsewhere.app", executable: "ClaudeBuddy");
        var bundled = BundledScript(apps.Path, "Elsewhere.app");

        var result = RunScript(bundled, ["--keepalive-only"], new Dictionary<string, string?>
        {
            ["ORBWEAVER_SETTINGS_DIR"] = ServingSettings(settingsDir.Path),
            ["ORBWEAVER_LAUNCHAGENTS_DIR"] = launchAgentsDir.Path,
            ["ORBWEAVER_KEEPALIVE_DRY_RUN"] = "1",
            ["ORBWEAVER_KEEPALIVE_APP_CANDIDATES"] = Path.Combine(apps.Path, "Nowhere.app"),
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("couldn't find an installed Orbweaver.app", result.Stdout);
        Assert.False(File.Exists(PlistIn(launchAgentsDir.Path)));
    }

    // ---- CB-256: the CLAUDE_BUDDY_ fallback, one case per variable ----------
    //
    // In each, the variable under test is set only under its legacy spelling
    // and the other three under the new one, so a pass can come only from the
    // fallback. launchctl is a stub on PATH that records being called, so a
    // dry-run flag the script failed to read would show up as a call rather
    // than as a real registration in this machine's launchd.

    private sealed record Seams(
        string Home, string Settings, string LaunchAgents, string Candidates, string Exe, string Calls, string Path);

    private static Seams MakeSeams(TempDir root, string tag = "")
    {
        var settings = Directory.CreateDirectory(Path.Combine(root.Path, "settings" + tag)).FullName;
        ServingSettings(settings);
        var launchAgents = Directory.CreateDirectory(Path.Combine(root.Path, "agents" + tag)).FullName;
        var exe = WriteFakeAppBundle(root.Path, $"App{tag}.app");
        var bin = Directory.CreateDirectory(Path.Combine(root.Path, "bin" + tag)).FullName;
        var calls = Path.Combine(root.Path, "launchctl-calls" + tag);
        WriteExecutable(Path.Combine(bin, "launchctl"), $"#!/bin/sh\necho \"$*\" >> '{calls}'\n");
        return new Seams(root.Path, settings, launchAgents,Path.Combine(root.Path, $"App{tag}.app"), exe, calls,
            bin + ":" + Environment.GetEnvironmentVariable("PATH"));
    }

    // HOME is the scratch root, so a variable the script failed to read falls
    // back to an empty fake home rather than to this machine's real settings
    // or LaunchAgents.
    private static Dictionary<string, string?> NewSpellings(Seams seams) => new()
    {
        ["HOME"] = seams.Home,
        ["PATH"] = seams.Path,
        ["ORBWEAVER_SETTINGS_DIR"] = seams.Settings,
        ["ORBWEAVER_LAUNCHAGENTS_DIR"] = seams.LaunchAgents,
        ["ORBWEAVER_KEEPALIVE_DRY_RUN"] = "1",
        ["ORBWEAVER_KEEPALIVE_APP_CANDIDATES"] = seams.Candidates,
    };

    // One variable moved to its legacy spelling, and the plist the run wrote.
    private static (ScriptResult Result, string? Plist) RunWithLegacy(Seams seams, string suffix)
    {
        var env = NewSpellings(seams);
        env["CLAUDE_BUDDY_" + suffix] = env["ORBWEAVER_" + suffix];
        env.Remove("ORBWEAVER_" + suffix);
        var result = Run(["--keepalive-only"], env);
        var plist = PlistIn(seams.LaunchAgents);
        return (result, File.Exists(plist) ? File.ReadAllText(plist) : null);
    }

    [MacKeepAliveFact]
    public void LegacySpelling_SettingsDir_IsStillRead()
    {
        using var root = new TempDir();
        var seams = MakeSeams(root);

        var (result, plist) = RunWithLegacy(seams, "SETTINGS_DIR");

        // Read: Serve on launch is on there, so the agent is written. Not
        // read, the fake home has no settings and the agent is not.
        Assert.Equal(0, result.ExitCode);
        Assert.True(plist is not null, result.Stdout + result.Stderr);
    }

    [MacKeepAliveFact]
    public void LegacySpelling_LaunchAgentsDir_IsStillRead()
    {
        using var root = new TempDir();
        var seams = MakeSeams(root);

        var (result, plist) = RunWithLegacy(seams, "LAUNCHAGENTS_DIR");

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(plist);
        Assert.Contains(seams.LaunchAgents, result.Stdout);
    }

    [MacKeepAliveFact]
    public void LegacySpelling_KeepaliveDryRun_IsStillRead()
    {
        using var root = new TempDir();
        var seams = MakeSeams(root);

        var (result, plist) = RunWithLegacy(seams, "KEEPALIVE_DRY_RUN");

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(plist);
        Assert.Contains("(dry run, not loaded)", result.Stdout);
        Assert.False(File.Exists(seams.Calls), "launchctl was called although the legacy dry-run flag was set");
    }

    [MacKeepAliveFact]
    public void LegacySpelling_KeepaliveAppCandidates_IsStillRead()
    {
        using var root = new TempDir();
        var seams = MakeSeams(root);

        var (result, plist) = RunWithLegacy(seams, "KEEPALIVE_APP_CANDIDATES");

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(plist);
        Assert.Contains($"<string>{seams.Exe}</string>", plist);
    }

    // Both spellings of all four at once, each pair pointing at a different
    // value: the new spelling wins every time. The legacy values are chosen
    // so that any one of them winning is visible — its settings say Serve on
    // launch is off (the agent would be removed, not written), its agents
    // folder and its bundle are different ones, and its dry-run value is 0
    // (launchctl would be called).
    [MacKeepAliveFact]
    public void BothSpellings_TheOrbweaverOneWins()
    {
        using var root = new TempDir();
        var current = MakeSeams(root, "-new");
        var legacy = MakeSeams(root, "-old");
        ServingSettings(legacy.Settings, serve: false);

        var env = NewSpellings(current);
        env["CLAUDE_BUDDY_SETTINGS_DIR"] = legacy.Settings;
        env["CLAUDE_BUDDY_LAUNCHAGENTS_DIR"] = legacy.LaunchAgents;
        env["CLAUDE_BUDDY_KEEPALIVE_DRY_RUN"] = "0";
        env["CLAUDE_BUDDY_KEEPALIVE_APP_CANDIDATES"] = legacy.Candidates;
        var result = Run(["--keepalive-only"], env);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(PlistIn(legacy.LaunchAgents)), "the legacy agents folder won");
        var plist = File.ReadAllText(PlistIn(current.LaunchAgents));
        Assert.Contains($"<string>{current.Exe}</string>", plist);
        Assert.DoesNotContain(legacy.Exe, plist);
        Assert.Contains("(dry run, not loaded)", result.Stdout);
        Assert.False(File.Exists(current.Calls), "the legacy dry-run value won and launchctl was called");
    }

    // An empty new spelling is unset, as it is in the app's BrandEnv: the
    // legacy value is used rather than an empty one. Exercised on the agents
    // folder, where taking "" would mean writing under the filesystem root.
    [MacKeepAliveFact]
    public void AnEmptyOrbweaverSpelling_FallsBackToTheLegacyOne()
    {
        using var root = new TempDir();
        var seams = MakeSeams(root);

        var env = NewSpellings(seams);
        env["ORBWEAVER_LAUNCHAGENTS_DIR"] = "";
        env["CLAUDE_BUDDY_LAUNCHAGENTS_DIR"] = seams.LaunchAgents;
        var result = Run(["--keepalive-only"], env);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(PlistIn(seams.LaunchAgents)), result.Stdout + result.Stderr);
    }

    // A copy of the script at <root>/<bundle>/Contents/Resources/install-hooks.sh,
    // as build-macos-app.sh ships it.
    private static string BundledScript(string root, string bundle)
    {
        var resources = Directory.CreateDirectory(Path.Combine(root, bundle, "Contents", "Resources")).FullName;
        var copy = Path.Combine(resources, "install-hooks.sh");
        WriteExecutable(copy, File.ReadAllText(Script));
        return copy;
    }

    // A minimal .app bundle: just enough for resolve_app_executable's
    // executable-exists check in install-hooks.sh to find something real.
    // `executable` is Orbweaver for a phase-3 bundle, ClaudeBuddy for a
    // phase-2 Orbweaver.app or a pre-CB-255 Claude Buddy.app (CB-256).
    private static string WriteFakeAppBundle(string root, string name = "Fake.app", string executable = "Orbweaver")
    {
        var macosDir = Path.Combine(root, name, "Contents", "MacOS");
        Directory.CreateDirectory(macosDir);
        var exePath = Path.Combine(macosDir, executable);
        WriteExecutable(exePath, "#!/bin/sh\n");
        return exePath;
    }

    private static void WriteExecutable(string path, string content)
    {
        File.WriteAllText(path, content);
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("cb-keepalive-").FullName;
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
