using Xunit;

namespace Orbweaver.Tests;

// The corners of ChatMarkdown that tests/TranscriptTests' suite — compiled into
// this assembly as TranscriptSuiteTests — does not reach.
//
// Same category as the transcript parsers next to it, and it fails the same
// quiet way: this is the only thing standing between a reply and the bubble it
// is drawn in, so a rule that mis-fires does not throw, it silently shows the
// wrong thing. The cases below are the ones the file's own comments make
// promises about — a half-written delimiter stays literal, a table keeps its
// columns, a rule draws nothing — plus the two branches nothing was executing
// at all: a block quote, and a '[' that turns out not to start a link.
public class ChatMarkdownGapTests
{
    private static ChatMarkdown.MdBlock[] Blocks(string source) => ChatMarkdown.Parse(source).ToArray();

    private static ChatMarkdown.MdSpan[] Spans(string text) => ChatMarkdown.Inline(text).ToArray();

    // --- blocks -----------------------------------------------------------

    [Fact]
    public void AQuotedLineBecomesAQuoteBlockWithTheMarkerRemoved()
    {
        var blocks = Blocks("before\n\n> quoted thing\n\nafter");

        Assert.Equal(3, blocks.Length);
        Assert.Equal(ChatMarkdown.MdKind.Quote, blocks[1].Kind);
        Assert.Equal("quoted thing", blocks[1].Text);
    }

    // "> " with the space, not ">" alone: the source requires it, and a lone
    // '>' is far more likely to be a shell prompt or a diff marker than a
    // quotation.
    [Fact]
    public void AGreaterThanWithNoSpaceIsOrdinaryText()
    {
        var block = Assert.Single(Blocks(">not quoted"));

        Assert.Equal(ChatMarkdown.MdKind.Paragraph, block.Kind);
        Assert.Equal(">not quoted", block.Text);
    }

    // A quote interrupts the paragraph above it rather than being swallowed
    // into it — which is what FlushParagraph before the Add is there for.
    [Fact]
    public void AQuoteFlushesThePendingParagraphFirst()
    {
        var blocks = Blocks("a sentence\n> quoted");

        Assert.Equal(2, blocks.Length);
        Assert.Equal(ChatMarkdown.MdKind.Paragraph, blocks[0].Kind);
        Assert.Equal("a sentence", blocks[0].Text);
        Assert.Equal(ChatMarkdown.MdKind.Quote, blocks[1].Kind);
    }

    // Every level the file accepts, and the two forms it refuses. Seven hashes
    // is not a level-seven heading anywhere, and '#' with no space after it is
    // a hashtag — both have to come out as prose or a reply mentioning #wins
    // grows a title.
    [Theory]
    [InlineData("# one", 1)]
    [InlineData("## two", 2)]
    [InlineData("### three", 3)]
    [InlineData("#### four", 4)]
    [InlineData("##### five", 5)]
    [InlineData("###### six", 6)]
    public void HeadingLevelsOneToSixAreAccepted(string source, int depth)
    {
        var block = Assert.Single(Blocks(source));

        Assert.Equal(ChatMarkdown.MdKind.Heading, block.Kind);
        Assert.Equal(depth, block.Depth);
    }

    [Theory]
    [InlineData("####### seven")]
    [InlineData("#nospace")]
    public void SevenHashesAndAHashtagAreNotHeadings(string source)
    {
        Assert.Equal(ChatMarkdown.MdKind.Paragraph, Assert.Single(Blocks(source)).Kind);
    }

    // A heading with nothing after the hashes: `level >= trimmed.Length` is the
    // guard, and without it this indexes past the end.
    [Fact]
    public void HashesWithNothingAfterThemAreNotAHeading()
    {
        Assert.Equal(ChatMarkdown.MdKind.Paragraph, Assert.Single(Blocks("###")).Kind);
    }

    // All three rule characters, because IsRule tests each separately and a
    // missing arm would only show up for whichever one the author happened not
    // to type. Three characters minimum — "--" is an ordinary word.
    [Theory]
    [InlineData("---")]
    [InlineData("***")]
    [InlineData("___")]
    public void ARuleSeparatesParagraphsAndDrawsNothing(string rule)
    {
        var blocks = Blocks($"above\n{rule}\nbelow");

        Assert.Equal(2, blocks.Length);
        Assert.Equal("above", blocks[0].Text);
        Assert.Equal("below", blocks[1].Text);
        Assert.All(blocks, b => Assert.Equal(ChatMarkdown.MdKind.Paragraph, b.Kind));
    }

    [Fact]
    public void TwoDashesAreTooShortToBeARule()
    {
        var block = Assert.Single(Blocks("--"));

        Assert.Equal(ChatMarkdown.MdKind.Paragraph, block.Kind);
        Assert.Equal("--", block.Text);
    }

    // A table is kept verbatim in one code block — the file's stated reason is
    // that reflowing one into a paragraph destroys it. The row after the table
    // has to survive too: the loop steps i past the last row and then backs it
    // up by one, and an off-by-one there eats the next line.
    [Fact]
    public void ATableCollapsesToOneCodeBlockAndDoesNotEatTheLineAfterIt()
    {
        var blocks = Blocks("| a | b |\n|---|---|\n| 1 | 2 |\nafter the table");

        Assert.Equal(2, blocks.Length);
        Assert.Equal(ChatMarkdown.MdKind.Code, blocks[0].Kind);
        Assert.Equal("| a | b |\n|---|---|\n| 1 | 2 |", blocks[0].Text);
        Assert.Equal("after the table", blocks[1].Text);
    }

    [Fact]
    public void ATableFlushesThePendingParagraphFirst()
    {
        var blocks = Blocks("intro\n| a |");

        Assert.Equal(2, blocks.Length);
        Assert.Equal("intro", blocks[0].Text);
        Assert.Equal(ChatMarkdown.MdKind.Code, blocks[1].Kind);
    }

    // An unterminated fence is what a reply looks like *while it is being
    // written*, which is the normal case here rather than a malformed document
    // — so it still has to become a code block, with a language if one was
    // given, rather than a wall of prose with backticks in it.
    [Fact]
    public void AnUnterminatedFenceStillBecomesACodeBlockWithItsLanguage()
    {
        var block = Assert.Single(Blocks("```python\nprint(1)\nprint(2"));

        Assert.Equal(ChatMarkdown.MdKind.Code, block.Kind);
        Assert.Equal("python", block.Marker);
        Assert.Equal("print(1)\nprint(2", block.Text);
    }

    // Dedent strips blank lines off both ends first, so a fence containing
    // nothing but blank lines has nothing left — and the empty-body guard is
    // what stops the common-indent scan running over an empty list and
    // returning int.MaxValue as a substring index.
    [Fact]
    public void AFenceHoldingOnlyBlankLinesBecomesAnEmptyCodeBlock()
    {
        var block = Assert.Single(Blocks("```\n\n   \n\n```"));

        Assert.Equal(ChatMarkdown.MdKind.Code, block.Kind);
        Assert.Equal("", block.Text);
    }

    // The common indent is measured over the *non-blank* lines only, so a blank
    // line shorter than that indent cannot be sliced at it. TrimStart is the
    // fallback, and without it this throws ArgumentOutOfRangeException on a
    // perfectly ordinary code block with a blank line in the middle.
    [Fact]
    public void ABlankLineShorterThanTheCommonIndentSurvivesTheDedent()
    {
        var block = Assert.Single(Blocks("```\n    first\n  \n    second\n```"));

        Assert.Equal("first\n\nsecond", block.Text);
    }

    // Nothing to strip is its own path (common == 0), and the body has to come
    // back unchanged rather than losing a character to an unguarded slice.
    [Fact]
    public void AnUnindentedFenceIsLeftExactlyAsWritten()
    {
        var block = Assert.Single(Blocks("```\nfirst\n  second\n```"));

        Assert.Equal("first\n  second", block.Text);
    }

    // Everything inside a fence is literal — that is the whole point of one —
    // so a heading, a bullet and a table row in there stay text.
    [Fact]
    public void AFenceSwallowsWhatWouldOtherwiseBeMarkup()
    {
        var block = Assert.Single(Blocks("```\n# not a heading\n- not a bullet\n| not a table\n```"));

        Assert.Equal(ChatMarkdown.MdKind.Code, block.Kind);
        Assert.Equal("# not a heading\n- not a bullet\n| not a table", block.Text);
    }

    [Fact]
    public void WhitespaceOnlySourceProducesNoBlocksAtAll()
    {
        Assert.Empty(Blocks("   \n\n  "));
        Assert.Empty(Blocks(""));
    }

    // Windows line endings arrive from a Windows CLI, and the '\r' is stripped
    // before anything else looks at the text — leaving it in makes every
    // "ends with" and "trimmed length" test in here disagree with itself.
    [Fact]
    public void CarriageReturnsAreStrippedBeforeAnythingElseSeesThem()
    {
        var blocks = Blocks("# Title\r\nbody\r\n");

        Assert.Equal(2, blocks.Length);
        Assert.Equal("Title", blocks[0].Text);
        Assert.Equal("body", blocks[1].Text);
    }

    // --- list items -------------------------------------------------------

    [Theory]
    [InlineData("- dash")]
    [InlineData("* star")]
    [InlineData("+ plus")]
    public void AllThreeBulletCharactersAreBullets(string source)
    {
        var block = Assert.Single(Blocks(source));

        Assert.Equal(ChatMarkdown.MdKind.Bullet, block.Kind);
        Assert.Equal("•", block.Marker);
    }

    // A marker with no content after it is not a list item. "- " alone is far
    // more likely to be a stray character than an empty bullet, and drawing a
    // bullet glyph beside nothing looks like a rendering bug.
    [Fact]
    public void ABulletMarkerWithNothingAfterItIsNotAListItem()
    {
        Assert.Equal(ChatMarkdown.MdKind.Paragraph, Assert.Single(Blocks("- ")).Kind);
    }

    // The marker is kept exactly as written, which is what lets a list that
    // starts at 4 still say 4 — the record's own comment. Both delimiters
    // Markdown allows are accepted.
    [Theory]
    [InlineData("4. four", "4.")]
    [InlineData("4) four", "4)")]
    [InlineData("100. hundred", "100.")]
    public void AnOrderedMarkerIsKeptAsWritten(string source, string marker)
    {
        var block = Assert.Single(Blocks(source));

        Assert.Equal(ChatMarkdown.MdKind.Ordered, block.Kind);
        Assert.Equal(marker, block.Marker);
        Assert.Equal(source[(marker.Length + 1)..], block.Text);
    }

    // Four digits is a year, not a list. Without the cap, "2024. A good year"
    // becomes item 2024 of an ordered list.
    [Fact]
    public void FourDigitsIsAYearRatherThanAListItem()
    {
        var block = Assert.Single(Blocks("2024. a good year"));

        Assert.Equal(ChatMarkdown.MdKind.Paragraph, block.Kind);
        Assert.Equal("2024. a good year", block.Text);
    }

    [Fact]
    public void ADigitRunWithNoDelimiterIsNotAListItem()
    {
        Assert.Equal(ChatMarkdown.MdKind.Paragraph, Assert.Single(Blocks("12 apples")).Kind);
    }

    // Two spaces per level, clamped at three — the bubble is 244pt wide and a
    // fourth indent would leave no room for the text. The clamp is what stops a
    // deeply indented code-ish line from marching off the edge.
    [Theory]
    [InlineData("- top", 0)]
    [InlineData("  - one", 1)]
    [InlineData("    - two", 2)]
    [InlineData("      - three", 3)]
    [InlineData("            - way in", 3)]
    public void IndentBecomesDepthClampedAtThree(string source, int depth)
    {
        Assert.Equal(depth, Assert.Single(Blocks(source)).Depth);
    }

    // --- inline spans -----------------------------------------------------

    // The '[' arm returning null and falling through to plain text was the one
    // inline path nothing executed. Each of these is a different way for Link
    // to refuse: no ']', no '(' after it, no ')' at all, and an empty label.
    [Theory]
    [InlineData("see [the docs for details")]
    [InlineData("see [the docs] for details")]
    [InlineData("see [the docs](unclosed for details")]
    [InlineData("see []( ) for details")]
    public void AMalformedLinkIsLeftAsPlainText(string text)
    {
        var span = Assert.Single(Spans(text));

        Assert.Equal(ChatMarkdown.MdStyle.Normal, span.Style);
        Assert.Equal(text, span.Text);
    }

    // The url is kept on the span now (CB-284) — the panel opens it. Every
    // other shape of link is in ChatMarkdownLinkTests.
    [Fact]
    public void AWellFormedLinkKeepsTheLabelAndTheUrl()
    {
        var spans = Spans("read [the findings](https://example.com/x) first");

        Assert.Equal(3, spans.Length);
        Assert.Equal("read ", spans[0].Text);
        Assert.Equal(ChatMarkdown.MdStyle.Link, spans[1].Style);
        Assert.Equal("the findings", spans[1].Text);
        Assert.Equal("https://example.com/x", spans[1].Url);
        Assert.Equal(" first", spans[2].Text);
        Assert.Null(spans[2].Url);
    }

    // Half-written emphasis is the normal state of a streaming reply, and the
    // file's stated rule is that a delimiter with no partner is text. Anything
    // else means the rest of the paragraph disappears into a bold run that
    // never closes.
    [Theory]
    [InlineData("a **half written thing")]
    [InlineData("a *half written thing")]
    [InlineData("a `half written thing")]
    public void AnUnpartneredDelimiterStaysLiteral(string text)
    {
        var span = Assert.Single(Spans(text));

        Assert.Equal(ChatMarkdown.MdStyle.Normal, span.Style);
        Assert.Equal(text, span.Text);
    }

    // Adjacent backticks are an empty code span, which the `close > i + 1`
    // guard refuses: "``" in prose is almost always someone typing a fence and
    // changing their mind, and an empty inline code element draws as a stray
    // grey box.
    [Fact]
    public void EmptyBackticksStayLiteral()
    {
        var span = Assert.Single(Spans("nothing `` here"));

        Assert.Equal(ChatMarkdown.MdStyle.Normal, span.Style);
        Assert.Equal("nothing `` here", span.Text);
    }

    // Code wins over emphasis because it is checked first, so a file name with
    // asterisks in it survives being drawn.
    [Fact]
    public void AsterisksInsideCodeStayLiteral()
    {
        var span = Assert.Single(Spans("`**not bold**`"));

        Assert.Equal(ChatMarkdown.MdStyle.Code, span.Style);
        Assert.Equal("**not bold**", span.Text);
    }

    // Four asterisks: the run counter stops at three, so the delimiter is "***"
    // and the fourth asterisk becomes the first character of the content — with
    // the closing run's fourth asterisk left over as literal text afterwards.
    // Not what a CommonMark renderer does, and deliberately so: the file is a
    // subset chosen from what the two transports actually produce, and the
    // alternative — counting the whole run as the delimiter — makes the far
    // more common "**bold**" case find no closer at all when a stray asterisk
    // lands beside it.
    [Fact]
    public void ARunOfFourAsterisksClampsToTheBoldItalicDelimiter()
    {
        var spans = Spans("****x****");

        Assert.Equal(2, spans.Length);
        Assert.Equal(ChatMarkdown.MdStyle.BoldItalic, spans[0].Style);
        Assert.Equal("*x", spans[0].Text);
        Assert.Equal(ChatMarkdown.MdStyle.Normal, spans[1].Style);
        Assert.Equal("*", spans[1].Text);
    }

    // Underscores are never emphasis here — file_path and snake_case_name are
    // far more common in this app's conversations than _emphasis_.
    [Fact]
    public void UnderscoresAreNeverEmphasis()
    {
        var span = Assert.Single(Spans("_not italic_ and snake_case_name"));

        Assert.Equal(ChatMarkdown.MdStyle.Normal, span.Style);
        Assert.Equal("_not italic_ and snake_case_name", span.Text);
    }

    [Fact]
    public void EmptyTextYieldsNoSpans()
    {
        Assert.Empty(Spans(""));
    }
}
