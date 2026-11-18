using Xunit;
using static Orbweaver.ChatMarkdown;

namespace Orbweaver.Tests;

// ChatLinks: which character of a line is which link, and when a press and a
// release open one (CB-284). Built against hand-made spans rather than the
// parser, so these cases say what the click handler does with whatever the
// parser hands it — including a Link span the parser promises never to emit.
// Shares ChatLinks.OpenFile, a process-wide seam, with ChatFileLinksTests: the two swap it, so they
// must not run in parallel.
[Collection("ChatLinksOpenFileSeam")]
public class ChatLinksTests
{
    private const string Url = "https://example.com/a_b";
    private const string Other = "https://example.org/";

    private static MdSpan Text(string t) => new(t, MdStyle.Normal);
    private static MdSpan Link(string t, string? url) => new(t, MdStyle.Link, url);

    // --- IsLive ---

    [Fact]
    public void AnOpenableLinkSpanIsLive() =>
        Assert.True(ChatLinks.IsLive(Link("here", Url)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("docs/x.md")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file://server/share/x")]
    public void ALinkSpanWithNothingOpenableBehindItIsNot(string? url) =>
        Assert.False(ChatLinks.IsLive(Link("here", url)));

    // File links (CB-363): live, and told apart from web links by one rule
    // that the renderer and the click handler share.
    [Theory]
    [InlineData("/Users/a/b.pdf")]
    [InlineData("vscode://file//Users/a/b.pdf")]
    [InlineData("C:\\Users\\a\\b.pdf")]
    public void AFileLinkSpanIsLiveAndIsAFileUrl(string url)
    {
        Assert.True(ChatLinks.IsLive(Link("here", url)));
        Assert.True(ChatLinks.IsFileUrl(url));
        Assert.Equal(MdStyle.Link, ChatLinks.StyleOf(Link("here", url)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/a")]
    [InlineData("docs/x.md")]
    public void ANonFileDestinationIsNotAFileUrl(string? url) =>
        Assert.False(ChatLinks.IsFileUrl(url));

    [Fact]
    public void FileLinksAreFoundByRangesOf()
    {
        var ranges = ChatLinks.RangesOf([Text("see "), Link("doc", "/a/b.pdf")]);
        Assert.Equal(new ChatLinks.LinkRange(4, 3, "/a/b.pdf"), Assert.Single(ranges));
    }

    // The seam exists, is settable, and is what a test swaps for a recorder.
    [Fact]
    public void OpenFileIsASettableSeam()
    {
        var before = ChatLinks.OpenFile;
        try
        {
            string? seen = null;
            ChatLinks.OpenFile = p => { seen = p; return true; };
            Assert.True(ChatLinks.OpenFile("/a/b.pdf"));
            Assert.Equal("/a/b.pdf", seen);
        }
        finally { ChatLinks.OpenFile = before; }
    }

    // The style is half the rule: an openable URL on a code span stays code.
    [Fact]
    public void AnOpenableUrlOnANonLinkSpanIsNot() =>
        Assert.False(ChatLinks.IsLive(new MdSpan(Url, MdStyle.Code, Url)));

    // --- StyleOf ---

    [Fact]
    public void ALiveLinkIsDrawnAsALink() =>
        Assert.Equal(MdStyle.Link, ChatLinks.StyleOf(Link("here", Url)));

    // The defensive arm: a Link span the parser promises never to emit.
    [Theory]
    [InlineData(null)]
    [InlineData("docs/x.md")]
    [InlineData("javascript:alert(1)")]
    public void ADeadLinkIsDrawnAsPlainText(string? url) =>
        Assert.Equal(MdStyle.Normal, ChatLinks.StyleOf(Link("here", url)));

    [Theory]
    [InlineData(MdStyle.Normal)]
    [InlineData(MdStyle.Bold)]
    [InlineData(MdStyle.Italic)]
    [InlineData(MdStyle.BoldItalic)]
    [InlineData(MdStyle.Code)]
    public void EveryOtherStyleIsDrawnAsItsOwn(MdStyle style) =>
        Assert.Equal(style, ChatLinks.StyleOf(new MdSpan("x", style, Url)));

    // --- RangesOf ---

    [Fact]
    public void RangesAreOffsetsIntoTheConcatenatedSpanText()
    {
        var spans = new[]
        {
            Text("see "),                        // 0..3
            Link("docs", Url),                   // 4..7
            new MdSpan(" and ", MdStyle.Bold),   // 8..12
            new MdSpan(Other, MdStyle.Code),     // not a link, but it still takes up room
            Link(Other, Other),
        };

        var ranges = ChatLinks.RangesOf(spans);

        Assert.Equal(2, ranges.Count);
        Assert.Equal(new ChatLinks.LinkRange(4, 4, Url), ranges[0]);
        Assert.Equal(new ChatLinks.LinkRange(13 + Other.Length, Other.Length, Other), ranges[1]);
    }

    [Fact]
    public void ADeadLinkSpanTakesUpRoomButIsNoRange()
    {
        var ranges = ChatLinks.RangesOf(new[] { Link("rel", "docs/x.md"), Link("live", Url) });

        Assert.Equal(new[] { new ChatLinks.LinkRange(3, 4, Url) }, ranges);
    }

    // Offsets are UTF-16 code units, which is what the text layout counts in:
    // an emoji outside the BMP is two of them, and a range computed in
    // "characters" would land two short of the link it names.
    [Fact]
    public void RangesCountUtf16UnitsSoASurrogatePairBeforeALinkTakesTwo()
    {
        var ranges = ChatLinks.RangesOf(new[] { Text("🚀 é "), Link("go", Url) });

        Assert.Equal(new[] { new ChatLinks.LinkRange(5, 2, Url) }, ranges);
    }

    [Fact]
    public void ALinkSpanWithNoUrlIsNoRange() =>
        Assert.Empty(ChatLinks.RangesOf(new[] { Link("label", null) }));

    [Fact]
    public void ALineWithNoLinksHasNoRanges() =>
        Assert.Empty(ChatLinks.RangesOf(new[] { Text("plain"), new MdSpan("code", MdStyle.Code) }));

    // --- UrlAt ---

    [Theory]
    [InlineData(3, null)]   // one before
    [InlineData(4, Url)]    // first character
    [InlineData(7, Url)]    // last character
    [InlineData(8, null)]   // one after
    public void UrlAtCoversExactlyTheLinksCharacters(int position, string? expected)
    {
        var ranges = new[] { new ChatLinks.LinkRange(4, 4, Url) };

        Assert.Equal(expected, ChatLinks.UrlAt(ranges, position));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]   // the line's length: one past its last character
    public void UrlAtOffEitherEndOfTheLineIsNull(int position)
    {
        var ranges = new[] { new ChatLinks.LinkRange(0, 8, Url) };

        Assert.Null(ChatLinks.UrlAt(ranges, position));
    }

    [Fact]
    public void UrlAtPicksTheRightOneOfTwoAdjacentLinks()
    {
        var ranges = new[] { new ChatLinks.LinkRange(0, 3, Url), new ChatLinks.LinkRange(3, 3, Other) };

        Assert.Equal(Url, ChatLinks.UrlAt(ranges, 2));
        Assert.Equal(Other, ChatLinks.UrlAt(ranges, 3));
    }

    [Fact]
    public void UrlAtWithNoRangesIsNull()
    {
        Assert.Null(ChatLinks.UrlAt(null, 0));
        Assert.Null(ChatLinks.UrlAt(System.Array.Empty<ChatLinks.LinkRange>(), 0));
    }

    // --- ShouldOpen ---

    [Fact]
    public void PressAndReleaseOnTheSameLinkWithNothingSelectedOpens() =>
        Assert.True(ChatLinks.ShouldOpen(Url, Url, hasSelection: false));

    [Fact]
    public void ASelectionMadeByTheGestureStopsIt() =>
        Assert.False(ChatLinks.ShouldOpen(Url, Url, hasSelection: true));

    [Fact]
    public void PressOnOneLinkAndReleaseOnAnotherDoesNot() =>
        Assert.False(ChatLinks.ShouldOpen(Url, Other, hasSelection: false));

    [Theory]
    [InlineData(null, Url)]   // pressed on text, released on the link
    [InlineData(Url, null)]   // pressed on the link, released on text
    [InlineData(null, null)]  // never near one
    public void EitherEndOffALinkDoesNot(string? pressed, string? released) =>
        Assert.False(ChatLinks.ShouldOpen(pressed, released, hasSelection: false));
}
