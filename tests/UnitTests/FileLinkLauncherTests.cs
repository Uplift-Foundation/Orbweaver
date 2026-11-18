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
        Assert.Equal(["--", "/a/My \"File\".pdf"], info.ArgumentList);
    }

    // A name that looks like an option is one argument after "--", and the
    // control shows "--" always precedes the path.
    [Fact]
    public void ADashLeadingNameCannotBeReadAsAnOption()
    {
        var info = Info(FileLinkVerdict.Open, "/a/-a.pdf", true, false)!;
        Assert.Equal(["--", "/a/-a.pdf"], info.ArgumentList);
        Assert.Equal(info.ArgumentList.Count - 2, info.ArgumentList.IndexOf("--"));
    }

    [Fact]
    public void MacRevealIsOpenDashR()
    {
        var info = Info(FileLinkVerdict.Reveal("x"), "/a/x.pdf", true, false)!;
        Assert.Equal("/usr/bin/open", info.FileName);
        Assert.Equal(["-R", "--", "/a/x.pdf"], info.ArgumentList);
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
        Assert.Equal(["-R", "--", "/a/x.exe"], Info(FileLinkVerdict.Open, "/a/x.exe", true, false)!.ArgumentList);
        Assert.Equal("explorer.exe", Info(FileLinkVerdict.Open, "C:\\a\\x.exe", false, true)!.FileName);
        Assert.Equal(["--", "/a/x.pdf"], Info(FileLinkVerdict.Open, "/a/x.pdf", true, false)!.ArgumentList);
        // Off the allowlist, not only the old deny list: .html is shown, not opened.
        Assert.Equal("-R", Info(FileLinkVerdict.Open, "/a/x.html", true, false)!.ArgumentList[0]);
        Assert.Equal("explorer.exe", Info(FileLinkVerdict.Open, "C:\\a\\x.svg", false, true)!.FileName);
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
        Assert.Equal(["--", "/a/x.pdf"], info!.ArgumentList);
    }

    [Theory]
    [InlineData("/a/x.exe")]
    [InlineData("/a/x.pdf.exe")]
    [InlineData("/a/x.html")]
    [InlineData("/a/x.iso")]
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

    // A symlink is launched by its target, so a link swapped after the
    // decision cannot redirect the open; the control is the same call with no
    // target, which launches the path itself.
    [Fact]
    public void PlanLaunchesTheTargetOfASymlink()
    {
        var link = new FileFacts(true, false, false, "/real/y.pdf");
        Assert.True(FileLinkLauncher.Plan("/a/x.pdf", link, true, false, out var info, out _));
        Assert.Equal(["--", "/real/y.pdf"], info!.ArgumentList);

        Assert.True(FileLinkLauncher.Plan("/a/x.pdf", Plain, true, false, out var plain, out _));
        Assert.Equal(["--", "/a/x.pdf"], plain!.ArgumentList);
    }

    [Fact]
    public void PlanRevealsALinkWhoseTargetIsOffTheAllowlist()
    {
        var link = new FileFacts(true, false, false, "/real/y.command");
        Assert.True(FileLinkLauncher.Plan("/a/x.pdf", link, true, false, out var info, out var verdict));
        Assert.Equal(FileLinkAction.Reveal, verdict.Action);
        Assert.Equal(["-R", "--", "/real/y.command"], info!.ArgumentList);
    }

    // --- PlanReveal: show a file whatever the policy would do about opening it ---

    [Fact]
    public void PlanRevealShowsAnOrdinaryFileThatWouldHaveBeenOpened()
    {
        Assert.True(FileLinkLauncher.PlanReveal("/a/x.pdf", Plain, true, false, out var mac));
        Assert.Equal(["-R", "--", "/a/x.pdf"], mac!.ArgumentList);

        Assert.True(FileLinkLauncher.PlanReveal("C:\\a\\x.pdf", Plain, false, true, out var win));
        Assert.Equal("explorer.exe", win!.FileName);

        // The control: opening the same file is an open.
        Assert.True(FileLinkLauncher.Plan("/a/x.pdf", Plain, true, false, out var open, out _));
        Assert.DoesNotContain("-R", open!.ArgumentList);
    }

    [Fact]
    public void PlanRevealRevealsTheTargetOfASymlink()
    {
        Assert.True(FileLinkLauncher.PlanReveal("/a/x.pdf", new FileFacts(true, false, false, "/real/y.pdf"), true, false, out var info));
        Assert.Equal(["-R", "--", "/real/y.pdf"], info!.ArgumentList);
    }

    // What Decide refuses a reveal cannot make safer.
    [Fact]
    public void PlanRevealRefusesWhatThePolicyRefuses()
    {
        Assert.False(FileLinkLauncher.PlanReveal("/a/x.pdf", new FileFacts(false, false, false), true, false, out var missing));
        Assert.Null(missing);
        Assert.False(FileLinkLauncher.PlanReveal("relative.pdf", Plain, true, false, out _));
        Assert.False(FileLinkLauncher.PlanReveal("C:\\a\\x.pdf", Plain, true, false, out _));
        Assert.False(FileLinkLauncher.PlanReveal("/a/x.pdf", Plain, false, false, out _));
    }

    [Fact]
    public void ASpecialFileIsRevealedByPlan()
    {
        Assert.True(FileLinkLauncher.Plan("/a/x.pdf", new FileFacts(true, false, false, Special: true), true, false, out var info, out var verdict));
        Assert.Equal(FileLinkAction.Reveal, verdict.Action);
        Assert.Equal("-R", info!.ArgumentList[0]);
    }
}
