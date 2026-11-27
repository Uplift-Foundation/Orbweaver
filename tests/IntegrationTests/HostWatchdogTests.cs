using System.Diagnostics;
using Xunit;

namespace Orbweaver.Tests;

// CB-382: the watchdog, proved end to end. Each test runs the real
// `dotnet test --no-build` command line the CI step uses, against this very
// assembly, filtered to one test, so what is asserted is the host a CI step
// would have: its exit, its stderr and the names in it.
public class HostWatchdogTests
{
    const string HangEnv = BrandEnv.Prefix + BrandEnv.WatchdogSelftestHang;

    // The hanging test the nested host runs. In a normal run the variable is
    // unset and it returns at once.
    [Fact]
    public async Task WatchdogSelfTestHang()
    {
        if (Environment.GetEnvironmentVariable(HangEnv) != "1") return;
        await Task.Delay(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void LimitDefaultsAndIsOverridable()
    {
        var old = Environment.GetEnvironmentVariable(HostWatchdog.StallEnv);
        try
        {
            Environment.SetEnvironmentVariable(HostWatchdog.StallEnv, null);
            Assert.Equal(TimeSpan.FromSeconds(180), HostWatchdog.LimitFromEnvironment());
            Environment.SetEnvironmentVariable(HostWatchdog.StallEnv, "2.5");
            Assert.Equal(TimeSpan.FromSeconds(2.5), HostWatchdog.LimitFromEnvironment());
            Environment.SetEnvironmentVariable(HostWatchdog.StallEnv, "nonsense");
            Assert.Equal(TimeSpan.FromSeconds(180), HostWatchdog.LimitFromEnvironment());
        }
        finally { Environment.SetEnvironmentVariable(HostWatchdog.StallEnv, old); }
    }

    [Fact]
    public void AHungHostIsKilledAndNamesItsTest()
    {
        var (exit, output, took) = RunNested("WatchdogSelfTestHang", hang: true, stallSeconds: 4, timeoutSeconds: 120);

        Assert.NotEqual(0, exit);
        Assert.Contains("[host-watchdog] HUNG", output);
        Assert.Contains("Orbweaver.Tests.HostWatchdogTests.WatchdogSelfTestHang", output);
        Assert.Contains("descendants of host pid", output);
        // The host's own row is in the listing, so the listing really ran. Where
        // `ps` itself is refused (a locked-down sandbox) the watchdog says so in
        // the listing instead; on CI that is not an acceptable answer.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" || !output.Contains("process listing failed"))
            Assert.Matches(@"(?m)^\s+\d+ \d+ .*(dotnet|testhost)", output);
        // Start-up of `dotnet test` is a few seconds on top of the 4s limit; the
        // 5-minute sleep proves it was the watchdog and not the test that ended.
        Assert.True(took < TimeSpan.FromSeconds(90), $"took {took}");
    }

    [Fact]
    public void ANormalRunPrintsNothingFromTheWatchdog()
    {
        var (exit, output, _) = RunNested("TestBootstrapTests", hang: false, stallSeconds: 4, timeoutSeconds: 120);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("host-watchdog", output);
        Assert.Contains("Passed!", output);
    }

    static (int Exit, string Output, TimeSpan Took) RunNested(string filter, bool hang, int stallSeconds, int timeoutSeconds)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        // .../tests/IntegrationTests/bin/<Configuration>/<tfm>/
        var config = dir.Parent!.Name;
        var project = dir.Parent.Parent!.Parent!.FullName;
        var psi = new ProcessStartInfo("dotnet",
            $"test \"{project}\" --no-build -c {config} --filter \"FullyQualifiedName~{filter}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment[HostWatchdog.StallEnv] = stallSeconds.ToString();
        psi.Environment[HangEnv] = hang ? "1" : "0";
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutSeconds * 1000))
        {
            p.Kill(true);
            Assert.Fail("nested dotnet test did not end within the limit; the watchdog did not fire");
        }
        p.WaitForExit();
        return (p.ExitCode, o.Result + e.Result, sw.Elapsed);
    }
}
