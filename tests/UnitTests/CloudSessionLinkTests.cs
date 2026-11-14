using Xunit;

namespace Orbweaver.Tests;

// How the app asks the OS to open a cloud session's URL.
//
// Both platforms asserted from either runner, because the decision is not a
// platform detail — it is a decision *about* platforms, and a rule that can only
// be checked on the machine it describes is a rule half the CI matrix never
// reads. StartInfoFor takes the two answers as arguments for exactly that.
//
// Nothing here starts a process. The launch itself is the one thing this type
// keeps behind [ExcludeFromCodeCoverage], since there is no version of running
// it that does not open a real browser window on whatever machine ran the suite.
public class CloudSessionLinkTests
{
    private const string Url = "https://claude.ai/code/session_01abc";

    // macOS hands the URL to `open` as an argument, never as the FileName. The
    // distinction is the whole safety property: a string that turns out not to
    // be a URL is then an argument to a known program rather than a program to
    // run.
    [Fact]
    public void MacOsRunsOpenWithTheUrlAsAnArgument()
    {
        var info = CloudSessionLink.StartInfoFor(Url, isMacOs: true, isWindows: false);

        Assert.NotNull(info);
        Assert.Equal("open", info!.FileName);
        Assert.Equal(new[] { Url }, info.ArgumentList);
        Assert.False(info.UseShellExecute);
    }

    // Windows must do the opposite, and it is not a style choice: a URL is a
    // thing for the shell to resolve, and CreateProcess cannot start one. With
    // UseShellExecute false this throws rather than opening anything, which is
    // the same bug SettingsWindow's ms-settings: link was fixed for.
    [Fact]
    public void WindowsHandsTheUrlToTheShell()
    {
        var info = CloudSessionLink.StartInfoFor(Url, isMacOs: false, isWindows: true);

        Assert.NotNull(info);
        Assert.Equal(Url, info!.FileName);
        Assert.True(info.UseShellExecute);
        Assert.Empty(info.ArgumentList);
    }

    // Neither platform is not a guess to be improvised. This app ships for two,
    // and a caller handed null opens nothing — which is the correct behaviour
    // for "we do not know how", and better than launching something plausible.
    [Fact]
    public void AnUnknownPlatformGetsNothing()
    {
        Assert.Null(CloudSessionLink.StartInfoFor(Url, isMacOs: false, isWindows: false));
    }

    // A session whose address never arrived opens nothing, on either platform.
    // Without this the macOS arm would run `open` with no argument, which opens
    // a Finder window.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAbsentUrlGetsNothing(string? url)
    {
        Assert.Null(CloudSessionLink.StartInfoFor(url, isMacOs: true, isWindows: false));
        Assert.Null(CloudSessionLink.StartInfoFor(url, isMacOs: false, isWindows: true));
    }

    // The convenience overload asks the running OS and agrees with whichever
    // arm above applies. Written as an implication rather than as a fixed
    // expectation so it says the same thing on both CI legs.
    [Fact]
    public void TheMachinesOwnAnswerMatchesTheExplicitOne()
    {
        var actual = CloudSessionLink.StartInfoFor(Url);
        var expected = CloudSessionLink.StartInfoFor(
            Url, OperatingSystem.IsMacOS(), OperatingSystem.IsWindows());

        Assert.Equal(expected?.FileName, actual?.FileName);
        Assert.Equal(expected?.UseShellExecute, actual?.UseShellExecute);
    }

    // --- IsOpenable (CB-284) ------------------------------------------------
    //
    // The rule every chat link goes through before a click can launch
    // anything. On Windows the launch is a ShellExecute, which resolves any
    // scheme an installed program registered, so this is an allowlist and
    // each refusal is its own case. Accepted controls first: a rule that
    // refused everything would pass every refusal on its own.

    [Theory]
    [InlineData("https://example.com/a_b?q=1#f")]
    [InlineData("http://example.com")]
    [InlineData("HTTPS://Example.com/x")]
    [InlineData("https://en.wikipedia.org/wiki/A_(b)")]
    [InlineData("https://claude.ai/code/session_01abc")]
    [InlineData("https://example.com:8443/x")]
    [InlineData("https://example.com/a@b")]          // @ in the path is not userinfo
    [InlineData("https://example.com/?to=a@b")]      // nor in the query
    [InlineData("https://example.com#a@b")]          // nor in the fragment
    public void AnHttpUrlWithAHostIsOpenable(string url)
    {
        Assert.True(CloudSessionLink.IsOpenable(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file:///etc/hosts")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    [InlineData("vscode://file/x")]
    [InlineData("myapp://x")]
    [InlineData("ftp://example.com")]
    [InlineData("httpx://example.com")]
    [InlineData("\\\\server\\share")]
    [InlineData("//server/share")]
    [InlineData("/etc/hosts")]
    [InlineData("docs/x.md")]
    [InlineData("https://exa mple.com")]
    [InlineData("https://example.com\n")]
    [InlineData(" https://example.com")]
    [InlineData("https://example.com\t")]
    [InlineData("https://example.com\0")]
    [InlineData("https://example.com\\@evil.com")]
    [InlineData("http://")]
    [InlineData("https://")]
    [InlineData("https:///path")]
    [InlineData("https://.")]
    [InlineData("http:example.com")]
    [InlineData("https://exa_mple:99999/")]
    public void AnythingElseIsNot(string? url)
    {
        Assert.False(CloudSessionLink.IsOpenable(url));
    }

    // Userinfo is refused (PM decision on CB-284, Q4): the part a person reads
    // first is the part the browser ignores. Each refusal beside a control
    // that is the same string without the "@".
    [Theory]
    [InlineData("https://google.com@evil.example", "https://google.com.evil.example")]
    [InlineData("https://user@example.com/", "https://user.example.com/")]
    [InlineData("https://user:pass@example.com/", "https://example.com/user:pass")]
    [InlineData("https://@example.com", "https://example.com")]
    [InlineData("http://a@b", "http://a.b")]
    [InlineData("https://google.com@evil.example?q", "https://google.com.evil.example?q")]
    [InlineData("https://google.com@evil.example#f", "https://google.com.evil.example#f")]
    public void AUrlCarryingUserinfoIsNotOpenable(string withUserinfo, string control)
    {
        Assert.False(CloudSessionLink.IsOpenable(withUserinfo));
        Assert.True(CloudSessionLink.IsOpenable(control));
    }

    // Nothing reaches Process.Start without the rule: every refusal is null on
    // both platform shapes, and the control is not.
    [Theory]
    [InlineData("file:///etc/hosts")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    [InlineData("vscode://file/x")]
    [InlineData("\\\\server\\share")]
    [InlineData("docs/x.md")]
    [InlineData("https://google.com@evil.example")]
    [InlineData("https://example.com\n")]
    public void StartInfoRefusesWhatIsOpenableRefusesOnBothShapes(string url)
    {
        Assert.Null(CloudSessionLink.StartInfoFor(url, isMacOs: true, isWindows: false));
        Assert.Null(CloudSessionLink.StartInfoFor(url, isMacOs: false, isWindows: true));

        Assert.NotNull(CloudSessionLink.StartInfoFor(Url, isMacOs: true, isWindows: false));
        Assert.NotNull(CloudSessionLink.StartInfoFor(Url, isMacOs: false, isWindows: true));
    }

    // A link out of a chat message takes the same macOS path as the cloud
    // link: the URL is an argument to `open`, never the program.
    [Fact]
    public void AChatLinkOnMacOsIsStillAnArgumentNotTheProgram()
    {
        const string chat = "https://en.wikipedia.org/wiki/A_(b)";
        var info = CloudSessionLink.StartInfoFor(chat, isMacOs: true, isWindows: false);

        Assert.Equal("open", info!.FileName);
        Assert.Equal(new[] { chat }, info.ArgumentList);
    }

    // --- QA's adversarial list (CB-284) -------------------------------------

    // Invisible and reordering characters: the tooltip and the browser could
    // disagree about the address. Refused whether they sit in the host, the
    // path or the query. U+00A0, U+2028 and U+0085 are already whitespace or
    // control; U+200B, U+202E and U+FEFF are Format, which is the new rule.
    [Theory]
    [InlineData("https://a.b/\u202E")]
    [InlineData("https://a.b/\u200B")]
    [InlineData("https://ex\u200Bample.com/")]
    [InlineData("https://a.b/?q=\uFEFF")]
    [InlineData("https://a.b/\u2066x\u2069")]
    [InlineData("https://a.b/\u00A0")]
    [InlineData("https://a.b/\u2028")]
    [InlineData("https://a.b/\u0085")]
    public void InvisibleAndBidiCharactersAreRefused(string url)
    {
        Assert.False(CloudSessionLink.IsOpenable(url));

        // Control: a non-ASCII letter is not one of them.
        Assert.True(CloudSessionLink.IsOpenable("https://bücher.example/"));
    }

    [Theory]
    [InlineData("http:/x")]
    [InlineData("https:/example.com")]
    [InlineData("HTTPS:\\\\evil")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("mailto:a@b.c")]
    [InlineData("https://example.com\n")]
    [InlineData("  https://example.com  ")]
    [InlineData("https://user:pw@host/")]
    public void QasRefusalsAreRefused(string url)
    {
        Assert.False(CloudSessionLink.IsOpenable(url));
        Assert.Null(CloudSessionLink.StartInfoFor(url, isMacOs: true, isWindows: false));
        Assert.Null(CloudSessionLink.StartInfoFor(url, isMacOs: false, isWindows: true));
    }

    // Percent-encoded bytes are text in a URL, not the characters they name,
    // and a browser sends them as written. Accepted deliberately.
    [Theory]
    [InlineData("https://a.b/x%0d%0ay")]
    [InlineData("https://a.b/x%00")]
    [InlineData("https://a.b/%E2%80%AE")]
    [InlineData("http://[::1]:8080/a")]
    [InlineData("https://bücher.example/")]
    [InlineData("https://a.b/x_y*z")]
    public void QasAcceptsAreAccepted(string url)
    {
        Assert.True(CloudSessionLink.IsOpenable(url));

        var mac = CloudSessionLink.StartInfoFor(url, isMacOs: true, isWindows: false)!;
        Assert.Equal("open", mac.FileName);
        Assert.Equal(new[] { url }, mac.ArgumentList);
    }

    [Fact]
    public void AVeryLongUrlIsAcceptedWithoutThrowing()
    {
        var url = "https://example.com/" + new string('a', 4000);
        Assert.True(CloudSessionLink.IsOpenable(url));
    }
}
