using System.Diagnostics;
using Xunit;

namespace Orbweaver.Tests;

// The process a verdict turns into (CB-363). Argv only, and the policy run
// again: each row that starts a process sits beside one that must not.
public class FileLinkLauncherTests
{
    private static readonly FileFacts Plain = new(true, false, false);

    private static ProcessStartInfo? Info(FileLinkVerdict v, string path, bool mac, bool win) =>
        FileLinkLauncher.StartInfoFor(v, path, mac, win);

    [Fact]
    public void MacOpenIsOpenWithThePathAsOneArgument()
    {
        var info = Info(FileLinkVerdict.Open, "/a/My \"File\".pdf", mac: true, win: false)!;
        Assert.Equal("/usr/bin/open", info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Equal(["/a/My \"File\".pdf"], info.ArgumentList);
    }

    [Fact]
    public void MacRevealIsOpenDashR()
    {
        var info = Info(FileLinkVerdict.Reveal("x"), "/a/x.pdf", true, false)!;
        Assert.Equal("/usr/bin/open", info.FileName);
        Assert.Equal(["-R", "/a/x.pdf"], info.ArgumentList);
    }

    [Fact]
    public void WindowsOpenIsShellExecuteOnThePath()
    {
        var info = Info(FileLinkVerdict.Open, "C:\\a b\\x.pdf", false, true)!;
        Assert.Equal("C:\\a b\\x.pdf", info.FileName);
        Assert.True(info.UseShellExecute);
        Assert.Empty(info.ArgumentList);
    }

    [Fact]
    public void WindowsRevealIsExplorerSelect()
    {
        var info = Info(FileLinkVerdict.Reveal("x"), "C:\\a b\\x.pdf", false, true)!;
        Assert.Equal("explorer.exe", info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Equal(["/select,C:\\a b\\x.pdf"], info.ArgumentList);
    }

    // The policy runs again: an Open verdict on a denied extension is
    // downgraded to a reveal, on both platforms; the control is a pdf.
    [Fact]
    public void AnOpenVerdictOnADeniedExtensionIsDowngradedToReveal()
    {
        Assert.Equal(["-R", "/a/x.exe"], Info(FileLinkVerdict.Open, "/a/x.exe", true, false)!.ArgumentList);
        Assert.Equal("explorer.exe", Info(FileLinkVerdict.Open, "C:\\a\\x.exe", false, true)!.FileName);
        Assert.Equal(["/a/x.pdf"], Info(FileLinkVerdict.Open, "/a/x.pdf", true, false)!.ArgumentList);
    }

    [Fact]
    public void ARefusalStartsNothing() =>
        Assert.Null(Info(FileLinkVerdict.Refuse("no"), "/a/x.pdf", true, false));

    [Fact]
    public void AnUnsupportedOsStartsNothing() =>
        Assert.Null(Info(FileLinkVerdict.Open, "/a/x.pdf", false, false));

    [Theory]
    [InlineData("relative.pdf")]
    [InlineData("//server/share/x.pdf")]
    [InlineData("/a/x\u202E.pdf")]
    [InlineData("/a/x:stream")]
    public void AMalformedPathStartsNothing(string path) =>
        Assert.Null(Info(FileLinkVerdict.Open, path, true, false));

    [Fact]
    public void APathOfTheOtherPlatformStartsNothing()
    {
        Assert.Null(Info(FileLinkVerdict.Open, "C:\\a\\x.pdf", true, false));
        Assert.Null(Info(FileLinkVerdict.Open, "/a/x.pdf", false, true));
    }

    [Fact]
    public void AWindowsTrailingDotStartsNothingEvenOnAnOpenVerdict()
    {
        Assert.Null(Info(FileLinkVerdict.Open, "C:\\a\\x.exe.", false, true));
        Assert.NotNull(Info(FileLinkVerdict.Open, "C:\\a\\x.pdf", false, true));
    }

    // --- Plan: decide and build in one step ---

    [Fact]
    public void PlanOpensAnOrdinaryFile()
    {
        Assert.True(FileLinkLauncher.Plan("/a/x.pdf", Plain, true, false, out var info, out var verdict));
        Assert.Equal(FileLinkAction.Open, verdict.Action);
        Assert.Equal(["/a/x.pdf"], info!.ArgumentList);
    }

    [Theory]
    [InlineData("/a/x.exe")]
    [InlineData("/a/x.pdf.exe")]
    public void PlanRevealsADeniedFile(string path)
    {
        Assert.True(FileLinkLauncher.Plan(path, Plain, true, false, out var info, out var verdict));
        Assert.Equal(FileLinkAction.Reveal, verdict.Action);
        Assert.Equal("-R", info!.ArgumentList[0]);
    }

    [Fact]
    public void PlanRevealsADirectoryAndAnExecutable()
    {
        Assert.True(FileLinkLauncher.Plan("/a/d", new FileFacts(true, true, false), true, false, out _, out var d));
        Assert.Equal(FileLinkAction.Reveal, d.Action);
        Assert.True(FileLinkLauncher.Plan("/a/x.pdf", new FileFacts(true, false, true), true, false, out _, out var e));
        Assert.Equal(FileLinkAction.Reveal, e.Action);
    }

    [Fact]
    public void PlanRefusesAMissingFileAndAMalformedPath()
    {
        Assert.False(FileLinkLauncher.Plan("/a/x.pdf", new FileFacts(false, false, false), true, false, out var info, out var missing));
        Assert.Equal(FileLinkAction.Refuse, missing.Action);
        Assert.Null(info);

        Assert.False(FileLinkLauncher.Plan("relative.pdf", Plain, true, false, out _, out var bad));
        Assert.Equal(FileLinkAction.Refuse, bad.Action);
        Assert.NotEmpty(bad.Reason);
    }
}
