using Xunit;

namespace Orbweaver.Tests;

// HookInstaller.Run and RunScript against real subprocesses, and the log they
// leave (CB-258). The thing being pinned is that a run which fails, hangs, is
// not found or cannot start now *says so* — before this it was swallowed whole,
// and a profile added in Settings that was never wired looked the same as one
// that was.
//
// The fake installers are one-line shell/cmd commands rather than files, since
// what is under test is what Run keeps of a child's exit code and streams, not
// any particular script. The real scripts are driven separately below
// (HookInstallerScriptsTests).
[Collection("LogDir")]
public class HookInstallerRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cb-hookrun-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _logScope;

    public HookInstallerRunTests()
    {
        Directory.CreateDirectory(_root);
        _logScope = CrashLog.ScopeForTests(Path.Combine(_root, "logs"));
    }

    public void Dispose()
    {
        _logScope.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // A command that prints "out" to stdout and "err" to stderr and exits with
    // the given code, in whichever shell this OS has.
    private static (string File, string[] Args) Fake(int exitCode) =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", $"echo out& echo err 1>&2& exit {exitCode}" })
            : ("/bin/sh", new[] { "-c", $"echo out; echo err >&2; exit {exitCode}" });

    private string Log() =>
        File.Exists(HookInstallerLog.Path_) ? File.ReadAllText(HookInstallerLog.Path_) : "";

    [Fact]
    public void ASuccessfulRunKeepsExitCodeAndOutputAndIsLogged()
    {
        var (file, args) = Fake(0);

        var result = HookInstaller.Run(file, args, "fake-installer");

        Assert.Equal(HookInstallOutcome.Ok, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("out", result.Output);
        Assert.Contains("err", result.Error);
        Assert.Contains("fake-installer  Ok  exit 0", Log());
    }

    [Fact]
    public void ANonzeroExitIsAFailureWithItsStderrAndIsLogged()
    {
        var (file, args) = Fake(3);

        var result = HookInstaller.Run(file, args, "fake-installer");

        Assert.Equal(HookInstallOutcome.Failed, result.Outcome);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("err", result.Error);

        var log = Log();
        Assert.Contains("fake-installer  Failed  exit 3", log);
        Assert.Contains("      err", log);
    }

    // A wait that reports a timeout as soon as the child has made `ready`
    // exist, for the hang tests below. The kill and everything after it are
    // real; what this replaces is the one-second wall clock the hang test used
    // to race (CB-293), which a cold CI runner could lose by not having said
    // "started" yet. A child that exits before it is ready gets `true`, so
    // the run comes back Ok or Failed and the test fails saying so.
    //
    // The Windows children reach `ready` by `cd /d` into its directory rather
    // than by quoting its path: ArgumentList escapes an embedded quote as \",
    // which cmd does not understand, so `type nul > "<path>"` silently wrote
    // nowhere. `cd /d` takes the rest of its command unquoted, spaces and all.
    private static Func<System.Diagnostics.Process, bool> TimesOutOnce(string ready) => process =>
    {
        var limit = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(ready))
        {
            if (process.HasExited) return true;
            if (limit.Elapsed > TimeSpan.FromSeconds(60)) return false;
            Thread.Sleep(10);
        }
        return false;
    };

    [Fact]
    public void AChildThatHangsIsKilledAndReportedAsTimedOutWithWhatItSaid()
    {
        var ready = Path.Combine(_root, "ready");
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", $"echo started& cd /d {_root}& type nul > ready& ping -n 30 127.0.0.1 > nul" })
            : ("/bin/sh", new[] { "-c", $"echo started; : > '{ready}'; sleep 30" });

        // A handle of our own on the child, since Run disposes its own: TimedOut
        // and a null exit code come from the wait alone, so without this a Run
        // that never killed anything would pass too.
        System.Diagnostics.Process? child = null;
        var wait = TimesOutOnce(ready);
        var result = HookInstaller.Run(file, args, "fake-installer",
            waitForExit: p => { child = System.Diagnostics.Process.GetProcessById(p.Id); return wait(p); });

        Assert.Equal(HookInstallOutcome.TimedOut, result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.Contains("started", result.Output);
        Assert.Contains("fake-installer  TimedOut", Log());
        using (child)
        {
            Assert.NotNull(child);
            // An upper bound for a kill that has already been sent, not a race:
            // a living child (`ping -n 30`, `sleep 30`) fails it by 25 seconds.
            Assert.True(child.WaitForExit(5_000), "the timed-out child was not killed");
        }
    }

    // What the timeout arm is for, when the kill cannot close the pipes: a
    // descendant orphaned before the kill is outside the tree it takes, keeps
    // stdout open, and the readers never reach end-of-stream. What the child
    // had already said must still come back. Before CB-293 Run kept output only
    // from a reader that had *finished*, so this returned "" — the hang it was
    // meant to explain, explained by nothing.
    [Fact]
    public void WhatAChildSaidSurvivesAnOrphanHoldingThePipesOpen()
    {
        var ready = Path.Combine(_root, "ready");
        var (file, args) = OperatingSystem.IsWindows()
            // The inner cmd starts ping and exits at once, so ping's parent is
            // gone and Kill(true) — which walks parent ids — never reaches it.
            ? ("cmd.exe", new[] { "/c", $"echo started& cmd /c start /b ping -n 8 127.0.0.1& cd /d {_root}& type nul > ready& ping -n 30 127.0.0.1 > nul" })
            : ("/bin/sh", new[] { "-c", $"echo started; (sleep 6 &); : > '{ready}'; sleep 30" });

        var result = HookInstaller.Run(file, args, "fake-installer", waitForExit: TimesOutOnce(ready));

        Assert.Equal(HookInstallOutcome.TimedOut, result.Outcome);
        Assert.Contains("started", result.Output);
    }

    // The drain-order fix: the old Run read stdout to the end and only then
    // stderr, so a child that fills the stderr pipe (about 64 KB) before closing
    // stdout blocked on its write and the run sat there until the timeout. Here
    // stderr is 300 KB written *before* any stdout, with a timeout far longer
    // than the run needs, so a regression shows up as TimedOut rather than Ok.
    [Fact]
    public void AChildThatFloodsStderrBeforeWritingStdoutDoesNotStallTheRun()
    {
        if (OperatingSystem.IsWindows()) return; // sh and head; the drain order is not OS-specific

        var result = HookInstaller.Run("/bin/sh",
            new[] { "-c", "head -c 300000 /dev/zero | tr '\\0' x >&2; echo done" },
            "fake-installer", timeoutMs: 15_000);

        Assert.Equal(HookInstallOutcome.Ok, result.Outcome);
        Assert.Equal(300_000, result.Error.Length);
        Assert.Contains("done", result.Output);
    }

    // The kill takes the child's tree, but a descendant that has already been
    // orphaned (reparented away) is outside it and keeps the pipes open, so the
    // readers never finish. The result must still come back, with empty output
    // rather than a hang or a throw.
    [Fact]
    public void AnOrphanedDescendantHoldingThePipesOpenDoesNotHangTheTimeoutResult()
    {
        if (OperatingSystem.IsWindows()) return; // relies on sh reparenting a backgrounded subshell

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = HookInstaller.Run("/bin/sh", new[] { "-c", "(sleep 6 &); sleep 30" },
            "fake-installer", timeoutMs: 500);

        // The half-second timeout plus the two-second wait for readers that never
        // finish: proof the not-completed arm ran, since empty output alone is what
        // a child that said nothing would give too.
        Assert.True(clock.Elapsed > TimeSpan.FromSeconds(2), $"returned after {clock.Elapsed}");
        Assert.Equal(HookInstallOutcome.TimedOut, result.Outcome);
        Assert.Equal("", result.Output);
        Assert.Equal("", result.Error);
    }

    [Fact]
    public void AProgramThatCannotBeStartedIsReportedNotThrown()
    {
        var missing = Path.Combine(_root, "no-such-installer");

        var result = HookInstaller.Run(missing, Array.Empty<string>(), "fake-installer");

        Assert.Equal(HookInstallOutcome.Threw, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Contains("fake-installer  Threw", Log());
    }

    [Fact]
    public void AnEnvironmentOverrideReachesTheChildAndANullValueRemovesOne()
    {
        const string removed = "CB258_REMOVED_VARIABLE";
        Environment.SetEnvironmentVariable(removed, "inherited");
        try
        {
            var (file, args) = OperatingSystem.IsWindows()
                ? ("cmd.exe", new[] { "/c", $"echo set=%CB258_SET_VARIABLE% gone=%{removed}%" })
                : ("/bin/sh", new[] { "-c", $"echo set=$CB258_SET_VARIABLE gone=${removed}" });

            var result = HookInstaller.Run(file, args, "fake-installer", environment:
                new Dictionary<string, string?> { ["CB258_SET_VARIABLE"] = "yes", [removed] = null });

            Assert.Equal(HookInstallOutcome.Ok, result.Outcome);
            Assert.Contains("set=yes", result.Output);
            // cmd leaves an unset %VAR% unexpanded; sh expands it to nothing.
            Assert.DoesNotContain("inherited", result.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable(removed, null);
        }
    }

    [Fact]
    public void AMissingScriptIsReportedAsNotFoundAndLogged()
    {
        var empty = Path.Combine(_root, "empty-app", "Contents", "MacOS");
        Directory.CreateDirectory(empty);

        var result = HookInstaller.RunScript("install-macos-hooks.sh", baseDirectory: empty);

        Assert.Equal(HookInstallOutcome.ScriptNotFound, result.Outcome);
        Assert.Equal("install-macos-hooks.sh", result.Script);
        Assert.Contains("install-macos-hooks.sh  ScriptNotFound", Log());
    }

    [Fact]
    public void AFoundScriptIsRunWithNothingButItsOwnPath()
    {
        if (OperatingSystem.IsWindows()) return; // RunScript's interpreter is /bin/bash

        var macOs = Path.Combine(_root, "app", "Contents", "MacOS");
        var resources = Path.Combine(_root, "app", "Contents", "Resources");
        Directory.CreateDirectory(macOs);
        Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Combine(resources, "fake-installer.sh"), "echo \"args:$*\"\n");

        var result = HookInstaller.RunScript("fake-installer.sh", baseDirectory: macOs);

        Assert.Equal("args:", result.Output.Trim());
    }

    [Fact]
    public void TheArgumentListsCarryNoColourFlag()
    {
        // The flag the installers stopped accepting on 2026-08-20 and the app kept
        // passing until CB-258: every installer exits 2 on it.
        Assert.Equal(new[] { "/x/install.sh" }, HookInstaller.ScriptArguments("/x/install.sh"));
        Assert.Equal(
            new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "/x/install.ps1" },
            HookInstaller.PowerShellArguments("/x/install.ps1"));
    }

    [Fact]
    public void AProfileWiredOnlyInASecondHomeCountsAsWired()
    {
        // The WSL-only shape: no native directory at all, hooks in a distro's home.
        var native = Path.Combine(_root, "native");
        var wsl = Path.Combine(_root, "wsl-home");
        Directory.CreateDirectory(native);
        Directory.CreateDirectory(Path.Combine(wsl, ".claude-work"));
        File.WriteAllText(Path.Combine(wsl, ".claude-work", "settings.json"), "OrbweaverHook.ps1");

        Assert.True(HookInstaller.IsWiredInAny(".claude-work", new[] { native, wsl }));
    }

    [Fact]
    public void AProfileWiredNativelyCountsAsWiredWhateverTheOtherHomesHold()
    {
        var native = Path.Combine(_root, "native");
        var wsl = Path.Combine(_root, "wsl-home");
        Directory.CreateDirectory(Path.Combine(native, ".claude-work"));
        Directory.CreateDirectory(wsl);
        File.WriteAllText(Path.Combine(native, ".claude-work", "settings.json"), "OrbweaverHook.sh");

        Assert.True(HookInstaller.IsWiredInAny(".claude-work", new[] { native, wsl }));
    }

    // CB-256, the phase-2 bug: IsWiredIn looked only for "ClaudeBuddyHook", and
    // every phase-2 installer writes OrbweaverHook.* and strips the old name, so
    // a freshly wired profile on an upgraded machine read as un-wired and the
    // Settings card reported a failure. Each of the four script names counts —
    // both platforms, both brands — since one profile's settings.json carries
    // the .sh on a Mac and the .ps1 on Windows. Plain files under a scratch
    // home, so this runs on both CI legs with nothing installed. Spelled out
    // rather than read off Brand: they are what the installers write, and this
    // assembly also sees SingleInstanceProbe's copy of Brand.
    [Theory]
    [InlineData("OrbweaverHook.sh")]
    [InlineData("OrbweaverHook.ps1")]
    [InlineData("ClaudeBuddyHook.sh")]
    [InlineData("ClaudeBuddyHook.ps1")]
    public void AProfileNamingEitherHookScriptOnEitherPlatformIsWired(string script)
    {
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Path.Combine(home, ".claude", "settings.json"),
            $$$"""{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"/x/{{{script}}}"}]}]}}""");

        Assert.True(HookInstaller.IsWiredIn(".claude", home));
    }

    // The negative control for the cases above: a settings.json with somebody
    // else's hook in it, which the same scratch layout must report un-wired —
    // so a check that answered true for any file would fail here.
    [Fact]
    public void AProfileWithSomeoneElsesHookIsNotWired()
    {
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Path.Combine(home, ".claude", "settings.json"),
            """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"/x/orbweaver/other-hook.sh"}]}]}}""");

        Assert.False(HookInstaller.IsWiredIn(".claude", home));
    }

    [Fact]
    public void AProfileWiredInNoHomeIsNotWired()
    {
        Assert.False(HookInstaller.IsWiredInAny(".claude-work",
            new[] { Path.Combine(_root, "a"), Path.Combine(_root, "b") }));
        Assert.False(HookInstaller.IsWiredInAny(".claude-work", Array.Empty<string>()));
    }

    [Fact]
    public void IsWiredReadsTheRealHomeAndFindsNothingForAProfileThatDoesNotExist()
    {
        Assert.False(HookInstaller.IsWired(".cb258-no-such-profile-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void IsWiredIsFalseRatherThanThrowingWhenTheFileCannotBeRead()
    {
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(Path.Combine(home, ".locked"));
        var settings = Path.Combine(home, ".locked", "settings.json");
        File.WriteAllText(settings, "OrbweaverHook.sh");

        // Held open with no sharing, so the read throws an IOException on every
        // OS — the shape of a settings file mid-write by Claude Code itself.
        using var hold = new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.False(HookInstaller.IsWiredIn(".locked", home));
    }

    [Fact]
    public void TheLogRotatesAtItsCeilingInsteadOfDroppingTheNewestEntry()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HookInstallerLog.Path_)!);
        File.WriteAllText(HookInstallerLog.Path_, new string('x', (int)HookInstallerLog.MaxBytes));

        HookInstallerLog.Record("fake-installer", HookInstallResult.NotFound("fake-installer"));

        Assert.StartsWith(new string('x', 10), File.ReadAllText(HookInstallerLog.Path_ + ".1"));
        var fresh = Log();
        Assert.Contains("fake-installer  ScriptNotFound", fresh);
        Assert.DoesNotContain("xxxxxxxxxx", fresh);
    }

    [Fact]
    public void ALogDirectoryThatCannotBeCreatedIsSurvived()
    {
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "a file where the log directory should go");

        using var scope = CrashLog.ScopeForTests(Path.Combine(blocker, "logs"));

        var result = HookInstaller.Run(Fake(0).File, Fake(0).Args, "fake-installer");

        Assert.Equal(HookInstallOutcome.Ok, result.Outcome);
    }
}
