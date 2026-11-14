using Xunit;

namespace Orbweaver.Tests;

// Which text in a chat message becomes a link, and where it goes (CB-284).
//
// Every positive case here sits beside a control that differs from it in one
// thing and must *not* produce a link, because a parser that links everything
// would pass every positive case on its own. The rule under test is the
// contract ChatPanel relies on: a Link span always carries a URL the launcher
// would open, and nothing else is drawn as a link.
//
// Spans are rendered as "Style:text->url" so a failure prints the whole line
// rather than one mismatched field.
public class ChatMarkdownLinkTests
{
    private static string Render(string text) =>
        string.Join(" | ", ChatMarkdown.Inline(text).Select(s =>
            s.Url is null ? $"{s.Style}:{s.Text}" : $"{s.Style}:{s.Text}->{s.Url}"));

    private static ChatMarkdown.MdSpan[] Links(string text) =>
        ChatMarkdown.Inline(text).Where(s => s.Style == ChatMarkdown.MdStyle.Link).ToArray();

    // --- the contract -------------------------------------------------------

    // Whatever the input, a Link span has a URL, the URL passes the launcher's
    // rule, and no non-Link span has one. Run over every input in this file's
    // other cases plus some hostile ones.
    [Theory]
    [InlineData("read [the findings](https://example.com/x) first")]
    [InlineData("see https://example.com, then [x](file:///etc/hosts)")]
    [InlineData("<https://example.com> and <file:///x> and `https://code.example`")]
    [InlineData("[a](javascript:alert(1)) https://ok.example/(b) xhttps://no.example")]
    [InlineData("https://google.com@evil.example and [g](https://google.com@evil.example)")]
    public void EveryLinkSpanCarriesAnOpenableUrlAndNothingElseDoes(string text)
    {
        foreach (var span in ChatMarkdown.Inline(text))
        {
            if (span.Style == ChatMarkdown.MdStyle.Link)
                Assert.True(CloudSessionLink.IsOpenable(span.Url), Render(text));
            else
                Assert.Null(span.Url);
        }
    }

    // --- [label](dest) ------------------------------------------------------

    [Fact]
    public void AMarkdownLinkKeepsItsLabelAndDestination()
    {
        Assert.Equal("Normal:see  | Link:docs->https://example.com/docs | Normal: now",
            Render("see [docs](https://example.com/docs) now"));
    }

    // Wikipedia's disambiguated titles are the common real case. Stopping at
    // the first ')' used to cut the URL and leave ")" in the prose.
    [Fact]
    public void AParenthesisedDestinationIsBalancedNotCutAtTheFirstClose()
    {
        Assert.Equal(
            "Link:A->https://en.wikipedia.org/wiki/A_(b) | Normal: done",
            Render("[A](https://en.wikipedia.org/wiki/A_(b)) done"));
    }

    // Control: the same opener with no closer at all is not a link, and the
    // text is left exactly as written — except that the bare URL inside it is
    // still found, which is what a streamed "[label](https://exa" looks like.
    [Fact]
    public void AnUnbalancedDestinationIsNotAMarkdownLink()
    {
        Assert.Equal(
            "Normal:[A]( | Link:https://en.wikipedia.org/wiki/A_(b->https://en.wikipedia.org/wiki/A_(b",
            Render("[A](https://en.wikipedia.org/wiki/A_(b"));
    }

    [Fact]
    public void AnAngleBracketedDestinationLosesItsBrackets()
    {
        Assert.Equal("Link:x->https://example.com/a", Render("[x](<https://example.com/a>)"));
    }

    [Fact]
    public void ATitleAfterTheDestinationIsDropped()
    {
        Assert.Equal("Link:x->https://example.com/a", Render("[x](https://example.com/a \"The title\")"));
        Assert.Equal("Link:x->https://example.com/a", Render("[x](  https://example.com/a\t't')"));
    }

    // A destination the launcher refuses keeps its label as plain text. Every
    // one of these is something Windows' ShellExecute would happily act on, or
    // a relative path that would resolve against nothing. Each sits beside the
    // https control in the first row.
    [Theory]
    [InlineData("https://example.com/x", true)]
    [InlineData("http://example.com/x", true)]
    [InlineData("file:///etc/hosts", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("vscode://file/x", false)]
    [InlineData("docs/openclaw-findings.md", false)]
    [InlineData("\\\\server\\share", false)]
    [InlineData("//server/share", false)]
    [InlineData("https://google.com@evil.example", false)]
    [InlineData("", false)]
    public void OnlyAnOpenableDestinationMakesALink(string dest, bool linked)
    {
        var text = $"see [the label]({dest}) now";

        if (linked)
        {
            Assert.Equal($"Normal:see  | Link:the label->{dest} | Normal: now", Render(text));
        }
        else
        {
            // One Normal span: the markup gone, the label kept, nothing that
            // looks clickable.
            Assert.Equal("Normal:see the label now", Render(text));
        }
    }

    // --- bare URLs ----------------------------------------------------------

    [Theory]
    [InlineData("https://example.com/a is first", "", " is first")]
    [InlineData("go to https://example.com/a now", "go to ", " now")]
    [InlineData("the end is https://example.com/a", "the end is ", "")]
    public void ABareUrlIsFoundAnywhereInTheLine(string text, string before, string after)
    {
        var expected = new List<string>();
        if (before.Length > 0) expected.Add("Normal:" + before);
        expected.Add("Link:https://example.com/a->https://example.com/a");
        if (after.Length > 0) expected.Add("Normal:" + after);

        Assert.Equal(string.Join(" | ", expected), Render(text));
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("HTTPS://Example.com/x")]
    [InlineData("Http://example.com/x")]
    public void EitherSchemeInAnyCaseIsABareUrl(string url)
    {
        Assert.Equal($"Link:{url}->{url}", Render(url));
    }

    // Controls: the scheme in the middle of a word, a scheme with no host, and
    // a scheme the launcher refuses are all plain text.
    [Theory]
    [InlineData("xhttps://example.com")]
    [InlineData("9http://example.com")]
    [InlineData("https://")]
    [InlineData("see https:// alone")]
    [InlineData("ftp://example.com")]
    [InlineData("httpx://example.com")]
    [InlineData("https://google.com@evil.example")]
    public void NotEveryHttpIsABareUrl(string text)
    {
        Assert.Equal("Normal:" + text, Render(text));
    }

    // Each of these belongs to the sentence, not the address, and falls back
    // into the plain text after the link.
    [Theory]
    [InlineData(".")]
    [InlineData(",")]
    [InlineData(";")]
    [InlineData(":")]
    [InlineData("!")]
    [InlineData("?")]
    [InlineData("'")]
    [InlineData("\"")]
    [InlineData("\u2019")]
    [InlineData("\u201D")]
    [InlineData(")")]
    [InlineData("]")]
    [InlineData("}")]
    [InlineData(").")]
    [InlineData("!?\u201D")]
    public void TrailingPunctuationIsTrimmed(string tail)
    {
        Assert.Equal(
            $"Link:https://example.com/a->https://example.com/a | Normal:{tail} next",
            Render($"https://example.com/a{tail} next"));
    }

    // Controls for the bracket rule: a closer the URL itself opened is part of
    // it, and stays — Wikipedia's parens, a query's brackets, a template's
    // braces.
    [Theory]
    [InlineData("https://en.wikipedia.org/wiki/A_(b)")]
    [InlineData("https://example.com/q?a[0]")]
    [InlineData("https://example.com/{id}")]
    public void ABracketTheUrlOpenedIsKept(string url)
    {
        Assert.Equal($"Link:{url}->{url}", Render(url));
    }

    // The two together: a URL with its own parens, inside prose parens, then
    // a full stop. Only the sentence's ")" and "." come off.
    [Fact]
    public void BalancedParensSurviveInsideProseParens()
    {
        Assert.Equal(
            "Normal:(see  | Link:https://en.wikipedia.org/wiki/A_(b)->https://en.wikipedia.org/wiki/A_(b) | Normal:).",
            Render("(see https://en.wikipedia.org/wiki/A_(b))."));
    }

    // A punctuation character inside the URL is not trailing and stays.
    [Fact]
    public void PunctuationInsideTheUrlIsKept()
    {
        Assert.Equal("Link:https://example.com/a.b,c!d->https://example.com/a.b,c!d",
            Render("https://example.com/a.b,c!d"));
    }

    [Theory]
    [InlineData("https://example.com/a b", " b")]
    [InlineData("https://example.com/a<b", "<b")]
    [InlineData("https://example.com/a>b", ">b")]
    [InlineData("https://example.com/a\tb", "\tb")]
    public void ABareUrlStopsAtWhitespaceAndAngleBrackets(string text, string rest)
    {
        Assert.Equal($"Link:https://example.com/a->https://example.com/a | Normal:{rest}", Render(text));
    }

    // A backtick ends the URL, and then the code span after it is code.
    [Fact]
    public void ABareUrlStopsAtABacktick()
    {
        Assert.Equal("Link:https://example.com/a->https://example.com/a | Code:b",
            Render("https://example.com/a`b`"));
    }

    // The reason underscores were never emphasis, and the reason the URL arm
    // runs before '*' could see the star inside one.
    [Fact]
    public void UnderscoresAndStarsInsideAUrlSurvive()
    {
        Assert.Equal(
            "Link:https://example.com/snake_case_name/a*b->https://example.com/snake_case_name/a*b",
            Render("https://example.com/snake_case_name/a*b"));
    }

    // --- <autolinks> --------------------------------------------------------

    [Fact]
    public void AnAutolinkIsALink()
    {
        Assert.Equal("Normal:at  | Link:https://example.com/a->https://example.com/a | Normal: now",
            Render("at <https://example.com/a> now"));
    }

    // Controls: angle brackets around anything the launcher would refuse, and
    // ones with nothing in them, are text exactly as written.
    [Theory]
    [InlineData("a <not a url> b")]
    [InlineData("a <file:///x> b")]
    [InlineData("a <> b")]
    [InlineData("a < b")]
    public void AngleBracketsAroundAnythingElseAreText(string text)
    {
        Assert.Equal("Normal:" + text, Render(text));
    }

    // --- code is never linked -----------------------------------------------

    [Fact]
    public void AUrlInsideInlineCodeIsCode()
    {
        var span = Assert.Single(ChatMarkdown.Inline("`https://example.com/a`"));

        Assert.Equal(ChatMarkdown.MdStyle.Code, span.Style);
        Assert.Equal("https://example.com/a", span.Text);
        Assert.Null(span.Url);

        // Control: the same text without the backticks is a link.
        Assert.Single(Links("https://example.com/a"));
    }

    [Fact]
    public void AUrlInsideAFencedBlockIsOneCodeBlock()
    {
        var block = Assert.Single(ChatMarkdown.Parse("```\ncurl https://example.com/a\n```"));

        Assert.Equal(ChatMarkdown.MdKind.Code, block.Kind);
        Assert.Equal("curl https://example.com/a", block.Text);
    }

    // --- known limits -------------------------------------------------------

    // Spans do not nest, so a URL inside emphasis is emphasis. Pinned so that
    // changing it is a decision rather than an accident.
    [Fact]
    public void AUrlInsideBoldIsNotLinked()
    {
        Assert.Equal("Bold:https://example.com/a", Render("**https://example.com/a**"));
    }
}
