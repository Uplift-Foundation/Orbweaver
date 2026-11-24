using Xunit;

namespace Orbweaver.Tests;

// CB-421 sent a Paperclip click to a browser. CB-443 gives the agent a panel (its
// current task's thread), so the click no longer opens a browser: the panel
// carries "Open in Paperclip", and so does the orb's menu.
public class PaperclipClickRoutingTests
{
    [Fact]
    public void APaperclipOrbOpensItsPanelNotABrowser()
    {
        Assert.False(ClickRouting.OpensInABrowser(new SessionStatus { Source = SessionSource.Paperclip }));
        Assert.False(ClickRouting.OpensInABrowser(new SessionStatus { Source = SessionSource.Paperclip, Url = "https://p/x" }));
    }

    [Fact]
    public void ItIsNotALocalCli_AndMapsToItsOwnCliName()
    {
        var s = new SessionStatus { Source = SessionSource.Paperclip };
        Assert.False(s.IsLocalCli);
        Assert.Equal("paperclip", MirrorProtocol.CliFor(SessionSource.Paperclip));
    }
}
