using System.IO;
using Xunit;

namespace Orbweaver.Tests;

// The probe's `approvals resolve` mode answers a REAL approval, so one variable
// guards it. Reading that variable through BrandEnv (the repository rule, enforced
// by BrandEnvTests) must not have loosened the guard: unset refuses, and only the
// exact value "1" allows. The probe is an exe the test projects cannot call, so
// the semantics are pinned on the read it makes, and the source is pinned to make
// exactly that read.
public class ProbeResolveGuardTests
{
    private static Func<string, string?> Env(params (string Name, string? Value)[] vars) =>
        name => vars.FirstOrDefault(v => v.Name == name).Value;

    private static bool Allowed(Func<string, string?> env) =>
        BrandEnv.Get(BrandEnv.ProbeAllowResolve, env) == "1";

    [Fact]
    public void UnsetEmptyAndEveryValueButExactlyOneRefuse()
    {
        Assert.False(Allowed(Env()));
        Assert.False(Allowed(Env(("ORBWEAVER_PROBE_ALLOW_RESOLVE", ""))));

        foreach (var value in new[] { "0", "true", "yes", "11", " 1", "1 ", "01", "ON" })
            Assert.False(Allowed(Env(("ORBWEAVER_PROBE_ALLOW_RESOLVE", value))), "'" + value + "' must refuse");

        Assert.True(Allowed(Env(("ORBWEAVER_PROBE_ALLOW_RESOLVE", "1"))));
    }

    // The new spelling wins over the legacy one, as for every other variable, so a
    // stale legacy "1" cannot override an explicit new "0".
    [Fact]
    public void TheNewSpellingWinsOverTheLegacyOne()
    {
        Assert.False(Allowed(Env(("ORBWEAVER_PROBE_ALLOW_RESOLVE", "0"), ("CLAUDE_BUDDY_PROBE_ALLOW_RESOLVE", "1"))));
        Assert.True(Allowed(Env(("CLAUDE_BUDDY_PROBE_ALLOW_RESOLVE", "1"))));
    }

    [Fact]
    public void TheProbesThreeVariablesAreOnTheOneList()
    {
        Assert.Contains("PROBE_SCOPES", BrandEnv.All);
        Assert.Contains("PROBE_CAPS", BrandEnv.All);
        Assert.Contains("PROBE_ALLOW_RESOLVE", BrandEnv.All);
    }

    // The probe makes exactly this read for the guard, once, and reads no variable of
    // its own directly.
    [Fact]
    public void TheProbeGuardsResolveWithTheExactReadAndReadsNothingDirectly()
    {
        var program = File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", "openclaw-probe", "Program.cs"));

        Assert.Equal(1, Count(program, "BrandEnv.Get(BrandEnv.ProbeAllowResolve) != \"1\""));
        Assert.Equal(1, Count(program, "BrandEnv.Get(BrandEnv.ProbeScopes)"));
        Assert.Equal(1, Count(program, "BrandEnv.Get(BrandEnv.ProbeCaps)"));
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", program);
    }

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Orbweaver.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Orbweaver.csproj not found above the test binaries");
    }
}
