using Xunit;

namespace Orbweaver.Tests;

public class TestBootstrapTests
{
    // CB-466: the suite never sends the anonymous install ping.
    [Fact]
    public void TheInstallPingIsSwitchedOff() =>
        Assert.Equal("0", Environment.GetEnvironmentVariable("ORBWEAVER_INSTALL_PING"));

    [Fact]
    public void TheAssemblyTempDirectoryExists()
    {
        Assert.True(Directory.Exists(Path.GetTempPath()));
    }
}
