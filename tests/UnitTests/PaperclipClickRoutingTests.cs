using Xunit;

namespace Orbweaver.Tests;

// CB-421: a Paperclip agent has no terminal to focus and no chat to open, so a
// click is a browser, exactly as a cloud session's is. True even with no
// address, deliberately: falling through would send the click hunting a pane
// for a conversation that has never been on this disk.
public class PaperclipClickRoutingTests
{
    [Fact]
    public void APaperclipOrbOpensInABrowser()
    {
        Assert.True(ClickRouting.OpensInABrowser(new SessionStatus { Source = SessionSource.Paperclip }));
        Assert.True(ClickRouting.OpensInABrowser(new SessionStatus { Source = SessionSource.Paperclip, Url = "" }));
    }

    [Fact]
    public void ItIsNotALocalCli_AndMapsToItsOwnCliName()
    {
        var s = new SessionStatus { Source = SessionSource.Paperclip };
        Assert.False(s.IsLocalCli);
        Assert.Equal("paperclip", MirrorProtocol.CliFor(SessionSource.Paperclip));
    }
}
