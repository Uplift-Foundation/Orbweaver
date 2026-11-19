using Xunit;

namespace Orbweaver.Tests;

// File links in chat Markdown (CB-363). The http rules are pinned by
// ChatMarkdownLinkTests and are deliberately not repeated here; what this
// file adds is the shapes a file destination takes, each beside a control that
// must not become a link.
public class ChatMarkdownFileLinkTests
{
    private static string Render(string text) =>
        string.Join(" | ", ChatMarkdown.Inline(text).Select(s =>
            s.Url is null ? $"{s.Style}:{s.Text}" : $"{s.Style}:{s.Text}->{s.Url}"));

    private const string Home = "/Users/someone/Documents/GTD/Evidence/01_CASES/X/01_Filings/";

    // The three real links (the other three in the transcript had the same
    // shapes), as one message, exactly as an agent wrote it.
    [Fact]
    public void TheRealMessageProducesOneLinkPerBullet()
    {
        var one = "[Response](vscode://file/" + Home + "Opp/1_Response_Opposing_Extension_and_Alternative_Motion.pdf)";
        var two = "[Stoel's motion](vscode://file" + Home + "Stoel_Motion/2026.11.17%20Motion%20for%20Extension%20of%20Time%20(Adoreal-Thompson)%20env14901649.pdf)";
        var three = "[Checklist](vscode://file/" + Home + "Opp/README.md)";

        foreach (var (text, label) in new[] { (one, "Response"), (two, "Stoel's motion"), (three, "Checklist") })
        {
            var spans = ChatMarkdown.Inline("- " + text);
            var link = Assert.Single(spans, s => s.Style == ChatMarkdown.MdStyle.Link);
            Assert.Equal(label, link.Text);
            Assert.Equal(text[(text.IndexOf("](") + 2)..^1], link.Url);
            Assert.NotNull(FileLink.TryParse(link.Url));
        }
    }

    [Theory]
    [InlineData("[x](/Users/a/b.pdf)", "/Users/a/b.pdf")]
    [InlineData("[x](file:///Users/a/b.pdf)", "file:///Users/a/b.pdf")]
    [InlineData("[x](C:\\Users\\a\\b.pdf)", "C:\\Users\\a\\b.pdf")]
    [InlineData("[x](C:/Users/a/b.pdf)", "C:/Users/a/b.pdf")]
    [InlineData("[x](vscode://file/Users/a/b.pdf:12:3)", "vscode://file/Users/a/b.pdf:12:3")]
    public void EveryFormInAMarkdownLinkCarriesItsRawDestination(string text, string url) =>
        Assert.Equal($"Link:x->{url}", Render(text));

    // Spaces belong to a file path: without this "/a/My File.pdf" was "/a/My".
    [Fact]
    public void SpacesInAFileDestinationAreKept()
    {
        Assert.Equal("Link:x->/a/My File.pdf", Render("[x](/a/My File.pdf)"));
        Assert.Equal("Link:x->/a/My File.pdf", Render("[x](  /a/My File.pdf  )"));
        Assert.Equal("Link:x->vscode://file/a/My File.pdf", Render("[x](vscode://file/a/My File.pdf)"));
        // The control: an http destination still stops at its first space.
        Assert.Equal("Link:x->https://example.com/My", Render("[x](https://example.com/My File.pdf)"));
    }

    // A quoted title after a file destination is a title; a quote that is part
    // of the name is not.
    [Theory]
    [InlineData("[x](/a/b.pdf \"the title\")", "/a/b.pdf")]
    [InlineData("[x](/a/b.pdf 'the title')", "/a/b.pdf")]
    [InlineData("[x](/a/My File.pdf \"t\")", "/a/My File.pdf")]
    [InlineData("[x](/a/b.pdf\t\"t\")", "/a/b.pdf")]
    [InlineData("[x](</a/My File.pdf> \"t\")", "/a/My File.pdf")]
    [InlineData("[x](</a/My File.pdf>)", "/a/My File.pdf")]
    public void ATitleIsDroppedFromAFileDestination(string text, string url) =>
        Assert.Equal($"Link:x->{url}", Render(text));

    [Theory]
    [InlineData("[x](/a/it's.pdf)", "/a/it's.pdf")]
    [InlineData("[x](/a/\"q\".pdf)", "/a/\"q\".pdf")]
    [InlineData("[x](/a/b\"c\")", "/a/b\"c\"")]            // no space before the quote
    [InlineData("[x](/\"\")", "/\"\"")]                      // too short to hold a title
    public void AQuoteThatIsPartOfTheNameIsNotATitle(string text, string url) =>
        Assert.Equal($"Link:x->{url}", Render(text));

    // Controls: every one of these is a file form that TryParse refuses, or
    // not a file form at all, so the label stays text and nothing is a link.
    [Theory]
    [InlineData("[x](docs/readme.md)", "Normal:x")]
    [InlineData("[x](//server/share/a.pdf)", "Normal:x")]
    [InlineData("[x](\\\\server\\share\\a.pdf)", "Normal:x")]
    [InlineData("[x](file://server/share/a.pdf)", "Normal:x")]
    [InlineData("[x](/a/b.pdf?x=1)", "Normal:x")]
    [InlineData("[x](/a/b%zz.pdf)", "Normal:x")]
    [InlineData("[x](C:\\a.txt:evil.exe)", "Normal:x")]
    [InlineData("[x](/a/b\u202E.pdf)", "Normal:x")]
    [InlineData("[x](<>)", "Normal:x")]
    [InlineData("[x](<no close)", "Normal:x")]
    [InlineData("[x](~/a.pdf)", "Normal:x")]
    public void AnythingTheParserRefusesStaysPlainText(string text, string expected) =>
        Assert.Equal(expected, Render(text));

    // A file path outside a Markdown link is prose, in an autolink or bare.
    [Theory]
    [InlineData("see /Users/a/b.pdf now")]
    [InlineData("see <file:///Users/a/b.pdf> now")]
    [InlineData("see file:///Users/a/b.pdf now")]
    [InlineData("see C:\\Users\\a\\b.pdf now")]
    public void ABarePathOrAutolinkIsNotALink(string text) =>
        Assert.DoesNotContain(ChatMarkdown.Inline(text), s => s.Style == ChatMarkdown.MdStyle.Link);

    // A file link inside code is code, never a link: inline and fenced.
    [Fact]
    public void AFileLinkInsideCodeIsNotLinked()
    {
        var inline = ChatMarkdown.Inline("run `[x](/Users/a/b.pdf)` now");
        Assert.DoesNotContain(inline, s => s.Style == ChatMarkdown.MdStyle.Link);
        Assert.Contains(inline, s => s.Style == ChatMarkdown.MdStyle.Code && s.Text == "[x](/Users/a/b.pdf)");

        var fenced = ChatMarkdown.Parse("```\n[x](/Users/a/b.pdf)\n```");
        var block = Assert.Single(fenced);
        Assert.Equal(ChatMarkdown.MdKind.Code, block.Kind);
        Assert.Equal("[x](/Users/a/b.pdf)", block.Text);
    }

    // http behaviour is unchanged beside a file link in the same line.
    [Fact]
    public void AWebLinkAndAFileLinkInOneLineAreBothLinks() =>
        Assert.Equal("Link:w->https://example.com/a | Normal: and  | Link:f->/a/b.pdf",
            Render("[w](https://example.com/a) and [f](/a/b.pdf)"));
}
