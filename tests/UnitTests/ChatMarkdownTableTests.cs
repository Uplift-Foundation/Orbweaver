using Xunit;

namespace Orbweaver.Tests;

// CB-289: a table has to stay a table. ChatPanel draws MdKind.Table unwrapped
// in a horizontal scroller; these are the rules that decide what reaches it.
//
// The fixtures are real CLI output (see ChatTableFixtures), except the
// pipe-less one, which is the GFM spec's own example because no real sample
// exists on the machine this was written on.
public class ChatMarkdownTableTests
{
    private static ChatMarkdown.MdBlock[] Blocks(string source) => ChatMarkdown.Parse(source).ToArray();

    // --- predicates -------------------------------------------------------

    [Theory]
    [InlineData("┌──┬──┐", true)]
    [InlineData("│ a │ b │", true)]
    [InlineData("├──┼──┤", true)]
    [InlineData("└──┴──┘", true)]
    [InlineData("╔══╗", true)]
    [InlineData("├── src", true)]
    [InlineData("────────", false)]
    [InlineData("━━━━", false)]
    [InlineData("═══", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    public void IsBoxLine(string line, bool expected) =>
        Assert.Equal(expected, ChatMarkdown.IsBoxLine(line));

    [Theory]
    [InlineData("+----+------+", true)]
    [InlineData("+====+", true)]
    [InlineData("+-+", true)]
    [InlineData("+--", false)]
    [InlineData("+1 for this+", false)]
    [InlineData("+ item", false)]
    [InlineData("+++", false)]
    [InlineData("++", false)]
    [InlineData("", false)]
    public void IsAsciiBorder(string line, bool expected) =>
        Assert.Equal(expected, ChatMarkdown.IsAsciiBorder(line));

    [Theory]
    [InlineData("--- | ---", true)]
    [InlineData("---|---", true)]
    [InlineData("| --- | --- |", true)]
    [InlineData(":--- | ---:", true)]
    [InlineData("---", false)]
    [InlineData("a | b", false)]
    [InlineData("", false)]
    public void IsDelimiterRow(string line, bool expected) =>
        Assert.Equal(expected, ChatMarkdown.IsDelimiterRow(line));

    [Theory]
    [InlineData("a | b", 2)]
    [InlineData("| a | b |", 2)]
    [InlineData("a | b | c", 3)]
    [InlineData(@"a \| b | c", 2)]
    [InlineData(@"| a \|", 1)]
    [InlineData("| a |  | c |", 3)]
    [InlineData("plain", 1)]
    public void CellCount(string row, int expected) =>
        Assert.Equal(expected, ChatMarkdown.CellCount(row));

    // --- box-drawn --------------------------------------------------------

    [Fact]
    public void TheRealBoxTableIsOneVerbatimTableBlock()
    {
        var table = ChatTableFixtures.Join(ChatTableFixtures.BoxUpdateStatus);
        var blocks = Blocks("Here is the status:\n\n" + table + "\n\nThat is all.");

        Assert.Equal(3, blocks.Length);
        Assert.Equal(ChatMarkdown.MdKind.Paragraph, blocks[0].Kind);
        Assert.Equal(ChatMarkdown.MdKind.Table, blocks[1].Kind);
        Assert.Equal(table, blocks[1].Text);
        Assert.Equal("That is all.", blocks[2].Text);
    }

    [Fact]
    public void ABoxTableWithNoClosingBorderIsStillOneTableBlock()
    {
        var table = ChatTableFixtures.Join(ChatTableFixtures.BoxTruncated);

        var block = Assert.Single(Blocks(table));

        Assert.Equal(ChatMarkdown.MdKind.Table, block.Kind);
        Assert.Equal(table, block.Text);
    }

    // Streaming: from the second line on, every prefix of the table is one
    // table block. The first line alone is a paragraph, which is the price of
    // the two-line rule (a lone box glyph in prose is not a table); it is one
    // frame, and the table block replaces it on the next delta.
    [Fact]
    public void EveryPrefixOfAStreamedBoxTableFromTwoLinesOnIsOneTableBlock()
    {
        var lines = ChatTableFixtures.BoxUpdateStatus;

        Assert.Equal(ChatMarkdown.MdKind.Paragraph, Assert.Single(Blocks(lines[0])).Kind);

        for (var n = 2; n <= lines.Length; n++)
        {
            var block = Assert.Single(Blocks(string.Join("\n", lines.Take(n))));
            Assert.Equal(ChatMarkdown.MdKind.Table, block.Kind);
        }
    }

    [Fact]
    public void AnIndentedBoxTableIsTrimmedRowByRow()
    {
        var block = Assert.Single(Blocks("  ┌─┐\n  │a│\n  └─┘"));

        Assert.Equal("┌─┐\n│a│\n└─┘", block.Text);
    }

    [Fact]
    public void ALoneHorizontalRuleOfBoxGlyphsStaysProse()
    {
        var block = Assert.Single(Blocks("────────"));

        Assert.Equal(ChatMarkdown.MdKind.Paragraph, block.Kind);
    }

    // Tree output starts with a box glyph, so it becomes a Table block. That is
    // a decision, not an accident: it is monospace and unwrapped, which is what
    // a tree wants as well.
    [Fact]
    public void TreeOutputBecomesATableBlockOnPurpose()
    {
        var block = Assert.Single(Blocks("├── src\n│   └── a.cs\n└── README"));

        Assert.Equal(ChatMarkdown.MdKind.Table, block.Kind);
    }

    // --- ASCII ------------------------------------------------------------

    [Fact]
    public void TheRealSqliteTableIsOneBlockWithItsBordersIncluded()
    {
        var table = ChatTableFixtures.Join(ChatTableFixtures.AsciiSqlite);

        var block = Assert.Single(Blocks(table));

        Assert.Equal(ChatMarkdown.MdKind.Table, block.Kind);
        Assert.Equal(table, block.Text);
        Assert.Equal(6, block.Text.Split('\n').Length);
    }

    [Fact]
    public void AnAsciiTableFlushesThePendingParagraphAndDoesNotEatTheNextLine()
    {
        var blocks = Blocks("intro\n+--+\n| a |\n+--+\nafter");

        Assert.Equal(3, blocks.Length);
        Assert.Equal("intro", blocks[0].Text);
        Assert.Equal(ChatMarkdown.MdKind.Table, blocks[1].Kind);
        Assert.Equal("+--+\n| a |\n+--+", blocks[1].Text);
        Assert.Equal("after", blocks[2].Text);
    }

    // --- pipe tables ------------------------------------------------------

    // The GFM spec's tables-extension example without outer pipes. No real
    // pipe-less sample exists on the machine this was written on.
    [Fact]
    public void APipeTableWithoutALeadingPipeIsATableWhenADelimiterRowFollows()
    {
        var block = Assert.Single(Blocks("foo | bar\n--- | ---\nbaz | bim"));

        Assert.Equal(ChatMarkdown.MdKind.Table, block.Kind);
        Assert.Equal("foo | bar\n--- | ---\nbaz | bim", block.Text);
    }

    [Fact]
    public void APipelessTableWithAlignmentColonsIsATable()
    {
        var block = Assert.Single(Blocks("a | b\n:--- | ---:\n1 | 2"));

        Assert.Equal(ChatMarkdown.MdKind.Table, block.Kind);
    }

    [Fact]
    public void APipelessTableFlushesTheParagraphAboveAndEndsAtARowWithNoPipe()
    {
        var blocks = Blocks("intro\nfoo | bar\n--- | ---\nbaz | bim\nafter");

        Assert.Equal(3, blocks.Length);
        Assert.Equal("intro", blocks[0].Text);
        Assert.Equal(ChatMarkdown.MdKind.Table, blocks[1].Kind);
        Assert.Equal("foo | bar\n--- | ---\nbaz | bim", blocks[1].Text);
        Assert.Equal("after", blocks[2].Text);
    }

    [Fact]
    public void ALeadingPipeTableStillWorks()
    {
        var block = Assert.Single(Blocks("| a | b |\n|---|---|\n| 1 | 2 |"));

        Assert.Equal(ChatMarkdown.MdKind.Table, block.Kind);
    }

    // --- negative controls ------------------------------------------------

    [Theory]
    [InlineData("a | b\nand then some prose")]
    [InlineData("a | b | c\n--- | ---\n1 | 2")]
    [InlineData("x | y inside a sentence")]
    [InlineData("+1 for this")]
    [InlineData("+ item one\n+ item two")]
    [InlineData("a \\| b\n--- | ---")]
    public void ThingsThatAreNotTablesAreNotTables(string source) =>
        Assert.DoesNotContain(Blocks(source), b => b.Kind == ChatMarkdown.MdKind.Table);

    [Fact]
    public void ABareRuleIsStillASeparatorAndDrawsNothing()
    {
        var blocks = Blocks("one\n\n---\n\ntwo");

        Assert.Equal(2, blocks.Length);
    }

    // --- fences -----------------------------------------------------------

    [Fact]
    public void AFencedBoxTableIsATableAndKeepsItsLanguage()
    {
        var block = Assert.Single(Blocks("```text\n┌─┐\n│a│\n└─┘\n```"));

        Assert.Equal(ChatMarkdown.MdKind.Table, block.Kind);
        Assert.Equal("text", block.Marker);
        Assert.Equal("┌─┐\n│a│\n└─┘", block.Text);
    }

    [Fact]
    public void AFencedTableWithBlankLinesStillCounts()
    {
        var block = Assert.Single(Blocks("```\n+--+\n\n| a |\n+--+\n```"));

        Assert.Equal(ChatMarkdown.MdKind.Table, block.Kind);
    }

    [Fact]
    public void AFenceWithAnyOtherLineStaysCodeAndKeepsWrapping()
    {
        var block = Assert.Single(Blocks("```\n┌─┐\nnot a table row\n└─┘\n```"));

        Assert.Equal(ChatMarkdown.MdKind.Code, block.Kind);
    }

    [Fact]
    public void AFencedPipeLineMixedWithMarkupStaysCode()
    {
        var block = Assert.Single(Blocks("```\n# x\n| y\n```"));

        Assert.Equal(ChatMarkdown.MdKind.Code, block.Kind);
    }

    [Fact]
    public void AFencedOrdinaryCodeBlockStaysCode()
    {
        var block = Assert.Single(Blocks("```csharp\nvar x = 1;\n```"));

        Assert.Equal(ChatMarkdown.MdKind.Code, block.Kind);
        Assert.Equal("csharp", block.Marker);
    }

    // --- lone box lines and block starts (QA, amended plan) ----------------

    [Theory]
    [InlineData("prose\n│ lone\nprose")]
    [InlineData("prose\n├── lone\nprose")]
    [InlineData("│ only")]
    [InlineData("prose\n└── last line of a tree\n")]
    public void ALoneBoxLineInProseIsAParagraphNotATable(string source)
    {
        Assert.DoesNotContain(Blocks(source), b => b.Kind == ChatMarkdown.MdKind.Table);
        Assert.All(Blocks(source), b => Assert.Equal(ChatMarkdown.MdKind.Paragraph, b.Kind));
    }

    [Fact]
    public void ALoneBoxLineInAFenceIsStillATable()
    {
        var block = Assert.Single(Blocks("```\n│ x\n```"));

        Assert.Equal(ChatMarkdown.MdKind.Table, block.Kind);
    }

    [Fact]
    public void TwoBoxLinesAreEnoughForATable()
    {
        var block = Assert.Single(Blocks("├── a\n└── b"));

        Assert.Equal(ChatMarkdown.MdKind.Table, block.Kind);
    }

    [Fact]
    public void AHeadingWithAPipeOverADelimiterRowStaysAHeading()
    {
        var blocks = Blocks("# a | b\n--- | ---\n1 | 2");

        Assert.Equal(ChatMarkdown.MdKind.Heading, blocks[0].Kind);
        Assert.Equal("a | b", blocks[0].Text);
        Assert.DoesNotContain(blocks, b => b.Kind == ChatMarkdown.MdKind.Table);
    }

    [Fact]
    public void ABulletWithAPipeOverADelimiterRowStaysABullet()
    {
        var blocks = Blocks("- a | b\n--- | ---\n1 | 2");

        Assert.Equal(ChatMarkdown.MdKind.Bullet, blocks[0].Kind);
        Assert.DoesNotContain(blocks, b => b.Kind == ChatMarkdown.MdKind.Table);
    }

    [Fact]
    public void AnOrderedItemWithAPipeOverADelimiterRowStaysOrdered()
    {
        var blocks = Blocks("1. a | b\n--- | ---\n1 | 2");

        Assert.Equal(ChatMarkdown.MdKind.Ordered, blocks[0].Kind);
        Assert.DoesNotContain(blocks, b => b.Kind == ChatMarkdown.MdKind.Table);
    }

    [Fact]
    public void AQuoteWithAPipeOverADelimiterRowStaysAQuote()
    {
        var blocks = Blocks("> a | b\n--- | ---");

        Assert.Equal(ChatMarkdown.MdKind.Quote, blocks[0].Kind);
        Assert.DoesNotContain(blocks, b => b.Kind == ChatMarkdown.MdKind.Table);
    }

    // A table that runs straight into a line starting another block, with no
    // blank line between, ends there instead of eating it.
    [Theory]
    [InlineData("- note | x", ChatMarkdown.MdKind.Bullet)]
    [InlineData("# a | b", ChatMarkdown.MdKind.Heading)]
    [InlineData("> a | b", ChatMarkdown.MdKind.Quote)]
    [InlineData("1. a | b", ChatMarkdown.MdKind.Ordered)]
    public void APipelessTableStopsAtALineThatStartsAnotherBlock(string next, ChatMarkdown.MdKind kind)
    {
        var blocks = Blocks("foo | bar\n--- | ---\nbaz | bim\n" + next);

        Assert.Equal(2, blocks.Length);
        Assert.Equal(ChatMarkdown.MdKind.Table, blocks[0].Kind);
        Assert.Equal("foo | bar\n--- | ---\nbaz | bim", blocks[0].Text);
        Assert.Equal(kind, blocks[1].Kind);
    }
}
