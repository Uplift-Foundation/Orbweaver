using Xunit;

namespace Orbweaver.Tests;

// CB-372: the live-view banner follows can-type and can-deliver, the same two
// facts ComposerHintFor does. Pure, so a case per outcome.
public class RemoteLiveViewBannerTests
{
    [Fact]
    public void APaneMeansTypedIntoItsTerminal()
    {
        var banner = RemoteControlChatSession.LiveViewBannerFor(canType: true, canDeliver: false, "job-hunter");

        Assert.StartsWith("Live view: this panel mirrors job-hunter's own conversation", banner);
        Assert.EndsWith("Messages you type are typed into its terminal.", banner);
    }

    [Fact]
    public void ThePaneWinsWhenBothAreAvailable()
    {
        Assert.Contains("typed into its terminal",
            RemoteControlChatSession.LiveViewBannerFor(canType: true, canDeliver: true, "x"));
    }

    [Fact]
    public void NoPaneButASocketMeansDeliveredToTheBackgroundJob()
    {
        var banner = RemoteControlChatSession.LiveViewBannerFor(canType: false, canDeliver: true, "job-hunter");

        Assert.Contains("delivered to its background job", banner);
        Assert.DoesNotContain("typed into its terminal", banner);
    }

    [Fact]
    public void NeitherMeansThereIsNothingToSend()
    {
        var banner = RemoteControlChatSession.LiveViewBannerFor(canType: false, canDeliver: false, "job-hunter");

        Assert.Contains("can't be delivered", banner);
        Assert.DoesNotContain("typed into its terminal", banner);
    }

    // The reason the ticket exists: the banner and the composer hint, one line
    // above the other, said different things. Across every combination they
    // agree on whether there is a terminal and whether it is a background job.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void TheBannerAndTheComposerNeverContradictEachOther(bool canType, bool canDeliver)
    {
        var banner = RemoteControlChatSession.LiveViewBannerFor(canType, canDeliver, "job-hunter");
        var hint = RemoteControlChatSession.ComposerHintFor(mirroring: true, canType, canDeliver, "job-hunter");

        var typed = canType;
        var job = !canType && canDeliver;

        Assert.Equal(typed, banner.Contains("typed into its terminal"));
        Assert.Equal(typed, hint.StartsWith("Type into"));
        Assert.Equal(job, banner.Contains("background job"));
        Assert.Equal(job, hint.Contains("background job"));

        // ...and on whether a send can be delivered at all, which is the
        // contradiction CB-372 was reopened for: a banner saying it cannot, over a
        // box inviting it.
        var deliverable = canType || canDeliver;
        Assert.Equal(!deliverable, banner.Contains("can't be delivered"));
        Assert.Equal(!deliverable, hint.StartsWith("Can't message"));
    }
}
