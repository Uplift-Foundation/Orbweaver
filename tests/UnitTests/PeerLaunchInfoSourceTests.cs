using Xunit;

namespace Orbweaver.UnitTests;

public class PeerLaunchInfoSourceTests
{
    private static PeerLaunchInfoSource.Seams Seams(bool allowed, List<string>? calls = null)
    {
        T Note<T>(string name, T value) { calls?.Add(name); return value; }

        return new PeerLaunchInfoSource.Seams(
            () => allowed,
            () => Note("os", "macos"),
            () => Note("home", "/Users/a"),
            () => Note("terminal", true),
            cli => Note("cli", cli != NewChatCli.Grok),
            () => Note<IReadOnlyList<RelayAccounts.Account>>("accounts", [new("claude", "default", "Default")]),
            () => Note<IReadOnlyList<string>>("recents", ["/Users/a/x"]));
    }

    [Fact]
    public void AnAllowedDeskDescribesItself()
    {
        var info = new PeerLaunchInfoSource(Seams(true)).Build();

        Assert.True(info.Allowed);
        Assert.Equal("macos", info.Os);
        Assert.Equal("/Users/a", info.Home);
        Assert.True(info.Terminal);
        Assert.Equal(
            [new PeerLaunchCli("claude", true), new PeerLaunchCli("codex", true), new PeerLaunchCli("grok", false)],
            info.Clis);
        Assert.Equal([new PeerLaunchAccount("claude", "default", "Default")], info.Accounts);
        Assert.Equal(["/Users/a/x"], info.Recents);
    }

    [Fact]
    public void ADeskWithTheSwitchOffDisclosesItsOsAndReadsNothingElse()
    {
        var calls = new List<string>();

        var info = new PeerLaunchInfoSource(Seams(false, calls)).Build();

        Assert.Equal(new PeerLaunchInfo(false, "macos"), info);
        // Negative control: the allowed path above does call these, so an empty
        // list here is the gate holding and not a setup that never reads them.
        Assert.Equal(["os"], calls);
    }
}
