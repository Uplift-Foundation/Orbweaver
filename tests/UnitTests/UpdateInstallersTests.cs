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
        var got = UpdateInstallers.For(true, false, () => mac, () => throw new InvalidOperationException("windows built"));
        Assert.Same(mac, got);
    }

    [Fact]
    public void Windows_gets_the_windows_arm_and_never_builds_the_mac_one()
    {
        var win = new Stub();
        Assert.Same(win, UpdateInstallers.For(false, true, () => throw new InvalidOperationException("mac built"), () => win));
    }

    [Fact]
    public void Windows_arm_may_answer_no_installer() =>
        Assert.Null(UpdateInstallers.For(false, true, () => new Stub(), () => null));

    [Fact]
    public void An_unsupported_platform_has_no_installer_and_builds_nothing() =>
        Assert.Null(UpdateInstallers.For(false, false,
            () => throw new InvalidOperationException("mac built"), () => throw new InvalidOperationException("windows built")));
}
