using Xunit;

namespace Orbweaver.Tests;

// CB-294: a WSL session's persona lookup is handed paths this host can open.
public class LocalPersonaWslPathsTests
{
    private const string Cwd = "/home/ana/proj";
    private const string Transcript = "/home/ana/.claude-x/projects/-home-ana-proj/s.jsonl";
    private const string MappedCwd = @"\\wsl.localhost\Ubuntu\home\ana\proj";
    private const string MappedConfig = @"\\wsl.localhost\Ubuntu\home\ana\.claude-x";
    private static readonly string[] Distros = { "Ubuntu" };

    [Fact]
    public void WslSessionIsWalkedAndReadThroughWslLocalhost()
    {
        var (cwd, config) = LocalPersona.MapWslPaths(
            Cwd, Transcript, true, Distros, p => p.StartsWith(@"\\wsl.localhost\Ubuntu\"));

        Assert.Equal(MappedCwd, cwd);
        Assert.Equal(MappedConfig, config);
    }

    [Fact]
    public void WindowsCwdIsPassedThroughUnchanged()
    {
        var (cwd, config) = LocalPersona.MapWslPaths(
            @"C:\work\proj", @"C:\Users\ana\.claude\projects\p\s.jsonl", true, Distros, _ => true);

        Assert.Equal(@"C:\work\proj", cwd);
        Assert.Null(config);
    }

    [Fact]
    public void NothingIsMappedOffWindows()
    {
        var (cwd, config) = LocalPersona.MapWslPaths(Cwd, Transcript, false, Distros, _ => true);

        Assert.Equal(Cwd, cwd);
        Assert.Null(config);
    }

    [Fact]
    public void PathsNoDistroHasStayAsRecorded()
    {
        var (cwd, config) = LocalPersona.MapWslPaths(Cwd, Transcript, true, Distros, _ => false);

        Assert.Equal(Cwd, cwd);
        Assert.Null(config);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/home/ana/no-projects-part.jsonl")]
    [InlineData("/projects/s.jsonl")]
    public void ATranscriptWithoutAConfigDirAddsNone(string? transcript)
    {
        var (cwd, config) = LocalPersona.MapWslPaths(Cwd, transcript, true, Distros, _ => true);

        Assert.Equal(MappedCwd, cwd);
        Assert.Null(config);
    }
}
