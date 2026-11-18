using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-363: every sentence a file link says about itself. Pure strings, so each outcome is a case
/// with a paired control (the named machine against the fallback, a known code against an unknown
/// one).
/// </summary>
public class ChatFileLinkTextTests
{
    [Theory]
    [InlineData(null, "that machine")]
    [InlineData("", "that machine")]
    [InlineData("   ", "that machine")]
    [InlineData("Mac Mini", "Mac Mini")]
    public void TheMachineIsNamedOnlyOnceTheRosterHasNamedIt(string? name, string expected) =>
        Assert.Equal(expected, ChatFileLinkText.MachineOr(name));

    [Fact]
    public void ATooltipNamesAPeerOrThisComputer()
    {
        Assert.Equal("/Users/a/x.pdf\non Mac Mini", ChatFileLinkText.Tooltip("/Users/a/x.pdf", ChatFileHost.Peer, "Mac Mini"));
        Assert.Equal("/Users/a/x.pdf\non that machine", ChatFileLinkText.Tooltip("/Users/a/x.pdf", ChatFileHost.Peer, null));
        Assert.Equal("/Users/a/x.pdf\non this computer", ChatFileLinkText.Tooltip("/Users/a/x.pdf", ChatFileHost.ThisMachine, "Mac Mini"));
    }

    [Theory]
    [InlineData("/Users/a/x.pdf", "x.pdf")]
    [InlineData(@"C:\Users\a\x.pdf", "x.pdf")]
    [InlineData(@"C:/Users/a/x.pdf", "x.pdf")]
    [InlineData("x.pdf", "x.pdf")]
    [InlineData("/Users/a/", "/Users/a/")]
    public void TheFileNameSplitsOnEitherSeparatorOnEitherOs(string path, string expected) =>
        Assert.Equal(expected, ChatFileLinkText.FileName(path));

    [Fact]
    public void FetchingShowsAPercentageOnlyOnceTheTotalIsKnown()
    {
        Assert.Equal("Fetching x.pdf from Mac Mini…", ChatFileLinkText.Fetching("/a/x.pdf", "Mac Mini", 0, 0));
        Assert.Equal("Fetching x.pdf from Mac Mini… 42%", ChatFileLinkText.Fetching("/a/x.pdf", "Mac Mini", 42, 100));
        Assert.Equal("Fetching x.pdf from that machine… 100%", ChatFileLinkText.Fetching("/a/x.pdf", null, 500, 100));
        Assert.Equal("Fetching x.pdf from Mac Mini… 0%", ChatFileLinkText.Fetching("/a/x.pdf", "Mac Mini", -5, 100));
    }

    [Theory]
    [InlineData("update", "Update Orbweaver on Mac Mini to open files from it.")]
    [InlineData("forbidden_path", "Mac Mini doesn't share that folder.")]
    [InlineData("not_found", "That file no longer exists on Mac Mini.")]
    [InlineData("too_large", "That file is too large to open from here (50 MB limit).")]
    [InlineData("rate_limited", "Too many file requests — try again in a moment.")]
    [InlineData("busy", "Mac Mini is still sending another file — try again in a moment.")]
    [InlineData("unreadable", "Mac Mini couldn't read that file.")]
    [InlineData("failed", "Couldn't fetch the file from Mac Mini.")]
    [InlineData("refused", "That kind of link can't be opened.")]
    [InlineData("missing", "That file no longer exists.")]
    public void EachKnownRefusalHasItsOwnSentence(string code, string expected) =>
        Assert.Equal(expected, ChatFileLinkText.Refusal(code, "Mac Mini"));

    [Theory]
    [InlineData("some_future_code")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnknownCodeNeverReachesTheReader(string? code)
    {
        var text = ChatFileLinkText.Refusal(code, "Mac Mini");
        Assert.Equal("Couldn't open that file.", text);
        Assert.DoesNotContain("future", text);
    }

    [Fact]
    public void ARefusalFallsBackToThatMachineWithoutARosterName() =>
        Assert.Equal("that machine doesn't share that folder.", ChatFileLinkText.Refusal("forbidden_path", null));

    [Fact]
    public void ARevealedFileSaysSoRatherThanDoingNothing() =>
        Assert.Equal("x.sh isn't opened from chat — it's shown in your file manager instead.",
            ChatFileLinkText.Revealed("/a/x.sh"));
}
