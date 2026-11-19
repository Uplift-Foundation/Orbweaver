using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-363: whether a file link is live depends on the session behind the bubble. A session that
/// cannot open files flattens the span to its label; web links are unaffected either way.
/// </summary>
public class ChatLinksFilesFlagTests
{
    private const string FileUrl = "file:///Users/a/report.pdf";
    private const string WebUrl = "https://example.com/a";

    private static ChatMarkdown.MdSpan Link(string url) =>
        new("label", ChatMarkdown.MdStyle.Link, url);

    [Fact]
    public void AFileLinkIsLiveOnlyWhereTheSessionCanOpenFiles()
    {
        Assert.True(ChatLinks.IsLive(Link(FileUrl), files: true));
        Assert.False(ChatLinks.IsLive(Link(FileUrl), files: false));
        Assert.True(ChatLinks.IsLive(Link(FileUrl)));
    }

    [Fact]
    public void AWebLinkIsLiveEitherWay_Control()
    {
        Assert.True(ChatLinks.IsLive(Link(WebUrl), files: true));
        Assert.True(ChatLinks.IsLive(Link(WebUrl), files: false));
    }

    [Fact]
    public void AFlattenedFileLinkIsDrawnAsPlainTextAndHasNoRange()
    {
        var spans = new[] { new ChatMarkdown.MdSpan("see ", ChatMarkdown.MdStyle.Normal, null), Link(FileUrl) };

        Assert.Equal(ChatMarkdown.MdStyle.Normal, ChatLinks.StyleOf(spans[1], files: false));
        Assert.Empty(ChatLinks.RangesOf(spans, files: false));

        Assert.Equal(ChatMarkdown.MdStyle.Link, ChatLinks.StyleOf(spans[1], files: true));
        var range = Assert.Single(ChatLinks.RangesOf(spans, files: true));
        Assert.Equal(4, range.Start);
        Assert.Equal(FileUrl, range.Url);
    }
}
