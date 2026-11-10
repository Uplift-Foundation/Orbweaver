using Xunit;

namespace Orbweaver.UnitTests;

// CB-315: the seam that reads a session's screen. The reads themselves run real
// terminals and are verified live (see the PR); what is pinned here is every
// decision around them: which reader a route gets, the exact commands each one
// runs, and the shaping of what comes back.
public class ScreenReaderTests
{
    [Theory]
    [InlineData("Tmux", typeof(TmuxScreenReader))]
    [InlineData("ITerm2", typeof(ITerm2ScreenReader))]
    [InlineData("TerminalApp", typeof(TerminalAppScreenReader))]
    [InlineData("WezTerm", typeof(WezTermScreenReader))]
    [InlineData("Kitty", typeof(KittyScreenReader))]
    [InlineData("WindowsConsole", typeof(WindowsConsoleScreenReader))]
    public void EveryRouteTheAppCanTypeIntoHasItsOwnReader(string route, Type reader) =>
        Assert.IsType(reader, ScreenReaders.For(Enum.Parse<TerminalTyping.Route>(route)));

    [Fact]
    public void ARouteWithNoTerminalHasNoReader() =>
        Assert.Null(ScreenReaders.For(TerminalTyping.Route.None));

    [Fact]
    public void ASessionWithNoStatusHasNoReader() =>
        Assert.Null(ScreenReaders.For((SessionStatus?)null));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("a\nb", "a\nb")]
    [InlineData("a\r\nb\r\n", "a\nb\n")]
    [InlineData("a\rb", "a\nb")]
    public void NormaliseLeavesLfOnlyAndKeepsNullAsFailure(string? raw, string? expected) =>
        Assert.Equal(expected, ScreenReaders.Normalise(raw));

    [Fact]
    public void WezTermAsksForThePaneViewportAndNothingElse() =>
        Assert.Equal(new[] { "cli", "get-text", "--pane-id", "7" }, ScreenReaderCommands.WezTerm("7"));

    [Fact]
    public void KittyAsksForTheVisibleScreenNotTheScrollback() =>
        Assert.Equal(new[] { "@", "get-text", "--match", "id:12", "--extent", "screen" }, ScreenReaderCommands.Kitty("12"));

    [Fact]
    public void TerminalAppReadsContentsByItsCodeNotItsNameAndInPlace()
    {
        var script = string.Join("\n", ScreenReaderCommands.TerminalAppScript);
        Assert.Contains("«property pcnt» of tab ti of window wi", script);
        Assert.DoesNotContain("contents of tab", script);
    }

    [Fact]
    public void ITerm2ReadsBySessionIndexNotByLoopVariable()
    {
        var script = string.Join("\n", ScreenReaderCommands.ITerm2Script);
        Assert.Contains("contents of session si of tab ti of window wi", script);
        Assert.DoesNotContain("repeat with s in", script);
    }

    [Fact]
    public void AWindowThatCannotBeAskedDoesNotSpoilTheOthers()
    {
        foreach (var script in new[] { ScreenReaderCommands.TerminalAppScript, ScreenReaderCommands.ITerm2Script })
        {
            var lines = script.ToList();
            var open = lines.IndexOf("try");
            var tabs = lines.FindIndex(l => l.StartsWith("repeat with ti"));
            Assert.InRange(open, 0, tabs - 1);
            var read = lines.FindIndex(l => l.StartsWith("if ((") && l.Contains("is wanted then return"));
            Assert.InRange(open, 0, read - 1);
            Assert.True(lines.IndexOf("end try") > read);
        }
    }

    [Fact]
    public void TheWantedIdGoesThroughArgvAndNeverIntoTheScriptSource()
    {
        var args = ScreenReaderCommands.ITerm2("w0t0p0:ABC\"; do shell script \"x");
        var scriptLines = args.Where((_, i) => i % 2 == 1 && i < args.Length - 3);
        Assert.DoesNotContain(scriptLines, l => l.Contains("do shell script"));
        Assert.Equal("--", args[^3]);
        Assert.Equal("w0t0p0:ABC\"; do shell script \"x", args[^2]);
        Assert.Equal("", args[^1]);
        Assert.All(Enumerable.Range(0, args.Length - 3).Where(i => i % 2 == 0), i => Assert.Equal("-e", args[i]));
    }

    [Fact]
    public void TerminalAppPassesTheDevicePathAsAnArgument()
    {
        var args = ScreenReaderCommands.TerminalApp("/dev/ttys004");
        Assert.Equal("/dev/ttys004", args[^2]);
        Assert.Contains("tty of tab", string.Join("\n", args));
    }

    [Fact]
    public void ConsoleRowsLoseTheirPaddingAndJoinWithLf() =>
        Assert.Equal("one\n  two\n\nthree",
            WindowsConsoleRead.Assemble(new[] { "one      ", "  two  ", "      ", "three" }));

    [Fact]
    public void TmuxPinsItsSocketAndNamesThePane()
    {
        var args = TmuxScreenReader.Args(new SessionStatus { TmuxPane = "%5" });
        Assert.Equal(new[] { "capture-pane", "-p", "-t", "%5" }, args.TakeLast(4));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AReaderGivenNothingToReadSaysUnknown(string? pane)
    {
        Assert.Null(await TmuxScreenReader.Instance.ReadAsync(null));
        Assert.Null(await TmuxScreenReader.Instance.ReadAsync(new SessionStatus { TmuxPane = pane }));
        Assert.Null(await WindowsConsoleScreenReader.Instance.ReadAsync(null));
        Assert.Null(await WindowsConsoleScreenReader.Instance.ReadAsync(new SessionStatus { SessionPid = 0 }));
    }
}
