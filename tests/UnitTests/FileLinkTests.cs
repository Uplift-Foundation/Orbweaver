using Xunit;

namespace Orbweaver.Tests;

// Which destinations name a file, and which file (CB-363).
//
// Chat text is untrusted, so every accepted form sits beside a control that
// differs in one thing and must be refused: a parser that accepted everything
// would pass every positive row on its own.
public class FileLinkTests
{
    private const string Home = "/Users/someone/Documents/GTD/Evidence/01_CASES/X/01_Filings/";

    // The three of the six links an agent really emitted that matter to the
    // parser (home directory replaced), exactly as written.
    [Theory]
    [InlineData("vscode://file//Users/someone/Documents/GTD/Evidence/01_CASES/X/01_Filings/Opp/1_Response_Opposing_Extension_and_Alternative_Motion.pdf",
        Home + "Opp/1_Response_Opposing_Extension_and_Alternative_Motion.pdf")]
    [InlineData("vscode://file/Users/someone/Documents/GTD/Evidence/01_CASES/X/01_Filings/Stoel_Motion/2026.11.17%20Motion%20for%20Extension%20of%20Time%20(Adoreal-Thompson)%20env14901649.pdf",
        Home + "Stoel_Motion/2026.11.17 Motion for Extension of Time (Adoreal-Thompson) env14901649.pdf")]
    [InlineData("vscode://file//Users/someone/Documents/GTD/Evidence/01_CASES/X/01_Filings/Opp/README.md",
        Home + "Opp/README.md")]
    public void TheRealAgentLinksParse(string dest, string expected)
    {
        var link = FileLink.TryParse(dest);
        Assert.NotNull(link);
        Assert.Equal(expected, link!.Value.Path);
        Assert.False(link.Value.IsWindowsPath);
    }

    [Theory]
    [InlineData("vscode://file/Users/x/a.pdf", "/Users/x/a.pdf", false)]
    [InlineData("vscode://file//Users/x/a.pdf", "/Users/x/a.pdf", false)]
    [InlineData("VSCODE://FILE/Users/x/a.pdf", "/Users/x/a.pdf", false)]
    [InlineData("file:///Users/x/a.pdf", "/Users/x/a.pdf", false)]
    [InlineData("file://localhost/Users/x/a.pdf", "/Users/x/a.pdf", false)]
    [InlineData("FILE://LOCALHOST/Users/x/a.pdf", "/Users/x/a.pdf", false)]
    [InlineData("/Users/x/a.pdf", "/Users/x/a.pdf", false)]
    [InlineData("/Users/x/My File.pdf", "/Users/x/My File.pdf", false)]
    [InlineData("C:\\Users\\x\\a.pdf", "C:\\Users\\x\\a.pdf", true)]
    [InlineData("C:/Users/x/a.pdf", "C:\\Users\\x\\a.pdf", true)]
    [InlineData("c:/Users/x/a.pdf", "c:\\Users\\x\\a.pdf", true)]
    [InlineData("file:///C:/Users/x/a.pdf", "C:\\Users\\x\\a.pdf", true)]
    [InlineData("vscode://file/C:/Users/x/a.pdf", "C:\\Users\\x\\a.pdf", true)]
    [InlineData("vscode://file//C:/Users/x/a.pdf", "C:\\Users\\x\\a.pdf", true)]
    public void EveryFormParses(string dest, string path, bool windows)
    {
        var link = FileLink.TryParse(dest);
        Assert.Equal(new FileLink(path, windows), link);
    }

    // The line and column are an editor's cursor, dropped.
    [Theory]
    [InlineData("/a/b.cs:12", "/a/b.cs")]
    [InlineData("/a/b.cs:12:3", "/a/b.cs")]
    [InlineData("vscode://file/a/b.cs:12:3", "/a/b.cs")]
    [InlineData("C:\\a\\b.cs:12:3", "C:\\a\\b.cs")]
    [InlineData("/a/b12", "/a/b12")]
    public void ALineAndColumnSuffixIsDropped(string dest, string path) =>
        Assert.Equal(path, FileLink.TryParse(dest)!.Value.Path);

    // Controls for the order: an *encoded* colon is not a suffix (the suffix
    // comes off before decoding), so it decodes to a colon in the name and the
    // stream rule refuses it; a colon with no digits is not a suffix either.
    [Theory]
    [InlineData("/a/b.cs%3A12")]
    [InlineData("/a/b.cs:")]
    [InlineData("/a/b.cs:x")]
    public void OnlyARealTrailingNumberIsASuffix(string dest) =>
        Assert.Null(FileLink.TryParse(dest));

    [Fact]
    public void ThreeNumericGroupsKeepTheThirdAndFailTheStreamRule() =>
        Assert.Null(FileLink.TryParse("/a/b.cs:1:2:3"));

    // Decode exactly once: %2520 is a literal "%20" in the name.
    [Fact]
    public void DecodesOnce()
    {
        Assert.Equal("/a/100%20done.pdf", FileLink.TryParse("/a/100%2520done.pdf")!.Value.Path);
        Assert.Equal("/a/100 done.pdf", FileLink.TryParse("/a/100%20done.pdf")!.Value.Path);
    }

    [Fact]
    public void DecodesMultiByteUtf8AndKeepsLiteralNonAscii()
    {
        Assert.Equal("/a/caf\u00e9.pdf", FileLink.TryParse("/a/caf%C3%A9.pdf")!.Value.Path);
        Assert.Equal("/a/caf\u00e9 \U0001F4C4.pdf", FileLink.TryParse("/a/caf\u00e9%20\U0001F4C4.pdf")!.Value.Path);
    }

    // An encoded ? or # is a character of the name; a raw one is a query.
    [Fact]
    public void EncodedQueryCharactersAreNamesAndRawOnesAreRefused()
    {
        Assert.Equal("/a/what?.pdf", FileLink.TryParse("/a/what%3F.pdf")!.Value.Path);
        Assert.Equal("/a/#1.pdf", FileLink.TryParse("/a/%231.pdf")!.Value.Path);
        Assert.Null(FileLink.TryParse("/a/what?.pdf"));
        Assert.Null(FileLink.TryParse("file:///a/b.pdf#page=2"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a/b.pdf")]                      // relative
    [InlineData("./a.pdf")]
    [InlineData("~/a.pdf")]
    [InlineData("docs/readme.md")]
    [InlineData("C:a.pdf")]                      // drive-relative
    [InlineData("\\a\\b.pdf")]                   // rooted, no drive
    [InlineData("file://server/share/a.pdf")]    // other host
    [InlineData("file://a.pdf")]                 // no authority slash
    [InlineData("file:/a.pdf")]
    [InlineData("vscode://file")]
    [InlineData("vscode://filea/x")]
    [InlineData("vscode://other/x")]
    [InlineData("https://example.com/a.pdf")]
    [InlineData("\\\\server\\share\\a.pdf")]     // UNC
    [InlineData("//server/share/a.pdf")]
    [InlineData("file:////server/share/a.pdf")]
    [InlineData("\\\\?\\C:\\a.pdf")]             // extended-length
    [InlineData("\\\\.\\pipe\\x")]
    [InlineData("C:\\a.txt:evil.exe")]           // alternate data stream
    [InlineData("C:\\a.txt:evil.exe:12")]
    [InlineData("/a/b:c.pdf")]
    [InlineData("/a/b%3Ac.pdf")]
    [InlineData("%2F%2Fserver/share")]
    [InlineData("/%2Fserver/share")]             // decodes to //server
    [InlineData("/a\\b.pdf")]                    // ambiguous separator
    [InlineData("/a%5Cb.pdf")]
    public void ShapesThatAreNotAbsoluteLocalPathsAreRefused(string? dest) =>
        Assert.Null(FileLink.TryParse(dest));

    [Theory]
    [InlineData("/a/%zz.pdf")]                   // not hex
    [InlineData("/a/%4.pdf")]                    // truncated
    [InlineData("/a/%")]
    [InlineData("/a/50%.pdf")]
    [InlineData("/a/%C3.pdf")]                   // invalid UTF-8
    [InlineData("/a/%FF%FE.pdf")]
    [InlineData("/a/%A9")]
    public void BadEscapesAndBadUtf8AreRefused(string dest) =>
        Assert.Null(FileLink.TryParse(dest));

    [Fact]
    public void ALoneSurrogateIsRefused() =>
        Assert.Null(FileLink.TryParse("/a/%41\uD800.pdf"));

    // Control and Format characters, raw or encoded: U+202E reorders what the
    // eye reads, U+200B draws nothing, NUL ends a C string.
    [Theory]
    [InlineData("/a/b\u202Efdp.exe")]
    [InlineData("/a/b%E2%80%AEfdp.exe")]
    [InlineData("/a/b\u200B.pdf")]
    [InlineData("/a/b%E2%80%8B.pdf")]
    [InlineData("/a/b%00.pdf")]
    [InlineData("/a/b%0A.pdf")]
    [InlineData("/a/b\n.pdf")]
    [InlineData("/a/b\t.pdf")]
    public void ControlAndFormatCharactersAreRefused(string dest) =>
        Assert.Null(FileLink.TryParse(dest));

    [Fact]
    public void FromDecodedPathChecksTheSameRulesWithoutDecoding()
    {
        // A decoded path is taken literally: "%41" is a name, not an "A".
        Assert.Equal("/a/%41.pdf", FileLink.FromDecodedPath("/a/%41.pdf")!.Value.Path);
        Assert.Null(FileLink.FromDecodedPath(null));
        Assert.Null(FileLink.FromDecodedPath(""));
        Assert.Null(FileLink.FromDecodedPath("relative.pdf"));
        Assert.Null(FileLink.FromDecodedPath("//host/x"));
        Assert.Null(FileLink.FromDecodedPath("/a/b\0"));
    }

    [Theory]
    [InlineData("/a/b", true)]
    [InlineData("vscode://file/a", true)]
    [InlineData("file:///a", true)]
    [InlineData("C:\\a", true)]
    [InlineData("C:/a", true)]
    [InlineData("a/b", false)]
    [InlineData("https://x/y", false)]
    [InlineData("file://server/a", false)]
    public void LooksLikeFileFormIsTheWeakerQuestion(string dest, bool expected) =>
        Assert.Equal(expected, FileLink.LooksLikeFileForm(dest));

    // The form can be right and the destination still unacceptable: ChatMarkdown
    // needs to know the first without the second to decide about spaces.
    [Fact]
    public void ALooksLikeFileFormDestinationCanStillBeRefused() =>
        Assert.True(FileLink.LooksLikeFileForm("//server/share") && FileLink.TryParse("//server/share") is null);
}
