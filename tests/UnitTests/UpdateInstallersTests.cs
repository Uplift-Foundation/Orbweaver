using Xunit;

namespace Orbweaver.Tests;

public class UpdateInstallersTests
{
    private sealed class Stub : IUpdateInstaller
    {
        public Task<InstallOutcome> InstallAsync(UpdateRelease r, IProgress<UpdateProgress> p, CancellationToken c) =>
            Task.FromResult(new InstallOutcome(InstallStatus.Refused, "stub"));
    }

    [Fact]
    public void Mac_gets_the_mac_installer_and_never_builds_the_windows_one()
    {
        var mac = new Stub();
        var got = UpdateInstallers.For(true, false, _ => { }, _ => mac, _ => throw new InvalidOperationException("windows built"));
        Assert.Same(mac, got);
    }

    [Fact]
    public void Windows_gets_the_windows_arm_and_never_builds_the_mac_one()
    {
        var win = new Stub();
        Assert.Same(win, UpdateInstallers.For(false, true, _ => { }, _ => throw new InvalidOperationException("mac built"), _ => win));
    }

    [Fact]
    public void Windows_arm_may_answer_no_installer() =>
        Assert.Null(UpdateInstallers.For(false, true, _ => { }, _ => new Stub(), _ => null));

    [Fact]
    public void An_unsupported_platform_has_no_installer_and_builds_nothing() =>
        Assert.Null(UpdateInstallers.For(false, false, _ => { },
            _ => throw new InvalidOperationException("mac built"), _ => throw new InvalidOperationException("windows built")));

    [Fact]
    public void The_log_sink_reaches_the_arm_that_is_chosen()
    {
        Action<string> sink = _ => { };
        Action<string>? seen = null;
        UpdateInstallers.For(true, false, sink, l => { seen = l; return new Stub(); }, _ => null);
        Assert.Same(sink, seen);
    }

    [Fact]
    public void For_the_current_platform_builds_this_machines_installer_without_running_anything()
    {
        // Construction only: nothing is downloaded, mounted or launched until
        // InstallAsync is called, which this does not do.
        var installer = UpdateInstallers.ForCurrentPlatform(_ => { });
        if (OperatingSystem.IsMacOS()) Assert.IsType<MacUpdateInstaller>(installer);
        else if (!OperatingSystem.IsWindows()) Assert.Null(installer);
    }
}
