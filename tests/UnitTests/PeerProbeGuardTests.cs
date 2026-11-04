using Xunit;

namespace Orbweaver.UnitTests;

// CB-306: peer-probe dials with this machine's own identity, and the far
// side's PeerLink.Adopt replaces any connection under that machine name — so a
// probe beside a live app knocks the app off the link. These pin the decision
// to refuse, the explicit way past it, and where the probe's log goes.
public class PeerProbeGuardTests
{
    [Fact]
    public void RefusesWhileTheAppIsRunningAndNoEvict()
    {
        var v = PeerProbeGuard.Decide(appRunning: true, evict: false);

        Assert.False(v.Run);
        Assert.Contains("refusing", v.Message);
        Assert.Contains("--evict", v.Message);
    }

    [Fact]
    public void EvictRunsButWarnsItWillDisconnectTheApp()
    {
        var v = PeerProbeGuard.Decide(appRunning: true, evict: true);

        Assert.True(v.Run);
        Assert.Contains("disconnect", v.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunsSilentlyWhenTheAppIsNotRunning(bool evict)
    {
        var v = PeerProbeGuard.Decide(appRunning: false, evict);

        Assert.True(v.Run);
        Assert.Null(v.Message);
    }

    [Fact]
    public void SplitEvictFindsTheFlagAnywhereAndKeepsPositionals()
    {
        var (evict, rest) = PeerProbeGuard.SplitEvict(["host", "--evict", "mini"]);

        Assert.True(evict);
        Assert.Equal(["host", "mini"], rest);
    }

    [Fact]
    public void SplitEvictIsFalseWithoutTheFlag()
    {
        var (evict, rest) = PeerProbeGuard.SplitEvict(["host", "mini"]);

        Assert.False(evict);
        Assert.Equal(["host", "mini"], rest);
    }

    [Fact]
    public void TheProbeLogsToItsOwnFileNotTheApps()
    {
        Assert.NotEqual(MirrorLog.DefaultFileName, MirrorLog.ProbeFileName);
        Assert.Equal(Path.Combine("/d", MirrorLog.ProbeFileName),
            MirrorLog.PathFor("/d", true, MirrorLog.ProbeFileName));
        Assert.Equal(Path.Combine("/d", MirrorLog.DefaultFileName),
            MirrorLog.PathFor("/d", true, MirrorLog.DefaultFileName));
    }

    [Theory]
    [InlineData("/d", false)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void NoLogWhenNotAskedForOrNoDirectory(string? dir, bool asked)
    {
        Assert.Null(MirrorLog.PathFor(dir, asked, MirrorLog.ProbeFileName));
    }

    [Fact]
    public void UseFileSwitchesTheNameAndCanBeRestored()
    {
        // Only the name is switched; the resolved path is cached on first write,
        // which is why the probe calls this first thing in Main.
        try
        {
            MirrorLog.UseFile(MirrorLog.ProbeFileName);
            Assert.Equal(MirrorLog.ProbeFileName, MirrorLog.CurrentFileName);
        }
        finally
        {
            MirrorLog.UseFile(MirrorLog.DefaultFileName);
        }

        Assert.Equal(MirrorLog.DefaultFileName, MirrorLog.CurrentFileName);
    }
}
