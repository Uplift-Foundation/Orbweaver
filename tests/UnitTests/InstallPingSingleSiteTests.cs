using Xunit;

namespace Orbweaver.Tests;

// CB-466: suites must never ping. The structural layer is that the real HTTP class
// is constructed in exactly one place, UpdateServices.Start, which the headless
// lifetime never runs. A source scan, like LogDirSingleReadSiteTests, because the
// property is "no other code does this".
public class InstallPingSingleSiteTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Orbweaver.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void TheRealPingHttpClassIsConstructedOnlyInUpdateServicesStart()
    {
        var root = Root();
        var hits = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                     && !p.Contains(Path.DirectorySeparatorChar + ".claude" + Path.DirectorySeparatorChar)
                     && !p.Contains(Path.DirectorySeparatorChar + "tests" + Path.DirectorySeparatorChar))
            .Where(p => File.ReadAllText(p).Contains("new HttpInstallPingHttp("))
            .Select(p => Path.GetRelativePath(root, p))
            .ToList();
        Assert.Equal(["UpdateServices.cs"], hits);
    }

    [Fact]
    public void EveryBootstrapSilencesThePing()
    {
        var root = Root();
        foreach (var suite in new[] { "UnitTests", "IntegrationTests", "UiTests", "UiScreenshots" })
        {
            var text = File.ReadAllText(Path.Combine(root, "tests", suite, "TestBootstrap.cs"));
            Assert.Contains("\"ORBWEAVER_INSTALL_PING\", \"0\"", text);
        }
        Assert.Equal("0", Environment.GetEnvironmentVariable("ORBWEAVER_INSTALL_PING"));
    }

    [Fact]
    public void BothWorkflowsSetTheSilencingAndInternalVariables()
    {
        var root = Root();
        foreach (var wf in new[] { "ci.yml", "release.yml" })
        {
            var text = File.ReadAllText(Path.Combine(root, ".github", "workflows", wf));
            Assert.Contains("ORBWEAVER_INSTALL_PING: \"0\"", text);
            Assert.Contains("ORBWEAVER_INTERNAL: \"1\"", text);
        }
    }
}
