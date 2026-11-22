using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-412: the /login reader against real tmux captures (Captures/cb412).
public class LoginScreenTests
{
    private static LoginScreen? Read(string name) => LoginScreen.Read(Cb412Captures.Screen(name));

    [Fact]
    public void Picker_reads_three_numbered_options()
    {
        var s = Read("login-picker-w100")!;
        Assert.Equal(LoginStep.Picker, s.Step);
        Assert.Equal(new[] { "1", "2", "3" }, s.Options.Select(o => o.Key));
        Assert.Contains("Claude account", s.Options[0].Label);
        Assert.Null(s.Url);
    }

    [Theory]
    [InlineData("login-console-picker-w100")]
    [InlineData("login-thirdparty-picker-w100")]
    [InlineData("login-picker-after-retry-w100")]
    public void Sub_pickers_read_as_pickers(string name)
    {
        var s = Read(name)!;
        Assert.Equal(LoginStep.Picker, s.Step);
        Assert.NotEmpty(s.Options);
    }

    [Fact]
    public void Opening_browser_has_no_options()
    {
        var s = Read("login-opening-browser-w100")!;
        Assert.Equal(LoginStep.Opening, s.Step);
        Assert.Empty(s.Options);
    }

    [Fact]
    public void Url_unwrapped_and_wrapped_give_the_same_url()
    {
        var wide = Read("login-url-w100")!;
        var narrow = Read("login-url-w60")!;
        Assert.Equal(LoginStep.Url, wide.Step);
        Assert.StartsWith("https://claude.ai/oauth/authorize?", wide.Url);
        Assert.Equal(79, wide.Url!.Length);
        Assert.Equal(wide.Url, narrow.Url);
        Assert.True(wide.FieldEmpty);
        Assert.Null(wide.FieldTail);
        Assert.Equal(wide.Hash, narrow.Hash);
        Assert.Empty(wide.Options);
    }

    [Fact]
    public void Url_survives_row_padding()
    {
        var padded = string.Join("\n", Cb412Captures.Screen("login-url-w60").Split('\n').Select(l => l.Length > 0 && l[0] == 'h' || char.IsLetter(l.FirstOrDefault()) && l.StartsWith("leEX") ? l + "   " : l));
        Assert.Equal(Read("login-url-w100")!.Url, LoginScreen.Read(padded)!.Url);
    }

    [Fact]
    public void Typed_code_gives_tail_and_does_not_change_the_hash()
    {
        var typed = Read("login-code-typed-w100")!;
        Assert.False(typed.FieldEmpty);
        Assert.Equal("lstate", typed.FieldTail);
        Assert.Equal(Read("login-url-w100")!.Hash, typed.Hash);
    }

    [Fact]
    public void Error_screen_offers_try_again_with_joined_text()
    {
        var s = Read("login-badcode-error-w100")!;
        Assert.Equal(LoginStep.Error, s.Step);
        Assert.Equal("Enter", s.Options.Single().Key);
        Assert.Equal("Try again", s.Options.Single().Label);
        Assert.Contains("wasn't accepted. It may be incomplete, expired, already used, or from an earlier sign-in attempt.", s.Title);
        Assert.DoesNotContain("  ", s.Title);
    }

    [Theory]
    [InlineData("login-interrupted-w100")]
    [InlineData("onboarding-picker-w100")]
    [InlineData("onboarding-url-w100")]
    [InlineData("onboarding-url-w40")]
    [InlineData("onboarding-badcode-error-w100")]
    public void Unknown_and_onboarding_screens_are_null(string name) => Assert.Null(Read(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("just some text\n❯ hello")]
    public void Garbage_is_null(string? screen) => Assert.Null(LoginScreen.Read(screen));

    [Fact]
    public void A_rule_below_login_means_a_live_conversation()
    {
        var screen = Cb412Captures.Screen("login-picker-w100") + "\n" + new string('─', 40) + "\n❯ \n";
        Assert.Null(LoginScreen.Read(screen));
    }

    [Theory]
    [InlineData("https://claude.ai/x y")]
    [InlineData("http://claude.ai/oauth")]
    public void A_non_https_or_spaced_url_is_null(string url)
    {
        var screen = $"{new string('─', 40)}\n  Login\n\n{url}\n\n  Paste code here if prompted\n  ╭────╮\n  │    │\n  ╰────╯\n\n  Esc to cancel\n";
        Assert.Null(LoginScreen.Read(screen));
    }

    [Fact]
    public void An_overlong_url_is_null()
    {
        var screen = $"{new string('─', 40)}\n  Login\n\nhttps://claude.ai/{new string('a', 2100)}\n\n  Paste code here if prompted\n  ╭────╮\n  │    │\n  ╰────╯\n\n  Esc to cancel\n";
        Assert.Null(LoginScreen.Read(screen));
    }

    [Fact]
    public void Url_without_a_box_is_null()
    {
        var screen = $"{new string('─', 40)}\n  Login\n\nhttps://claude.ai/oauth\n\n  Paste code here if prompted\n\n  Esc to cancel\n";
        Assert.Null(LoginScreen.Read(screen));
    }

    [Fact]
    public void Url_missing_before_paste_row_is_null()
    {
        var screen = $"{new string('─', 40)}\n  Login\n\n  Paste code here if prompted\n  ╭────╮\n  │    │\n  ╰────╯\n\n  Esc to cancel\n";
        Assert.Null(LoginScreen.Read(screen));
    }

    [Fact]
    public void Error_without_retry_footer_is_null()
    {
        var screen = $"{new string('─', 40)}\n  Login\n\n  ✘ OAuth error: nope\n\n  Esc to cancel\n";
        Assert.Null(LoginScreen.Read(screen));
    }

    [Fact]
    public void ToPrompt_puts_the_url_only_in_Link()
    {
        var url = Read("login-url-w100")!;
        var open = LoginScreen.ToPrompt(url, canPress: true);
        Assert.Equal(url.Url, open.Link);
        Assert.True(open.CodeEntry);
        Assert.Empty(open.Options);
        Assert.DoesNotContain(open.Body!, l => l.Contains("https://"));
        Assert.DoesNotContain("https://", open.Title);

        var fixedRoute = LoginScreen.ToPrompt(url, canPress: false);
        Assert.Equal(url.Url, fixedRoute.Link);
        Assert.False(fixedRoute.CodeEntry);

        var opening = LoginScreen.ToPrompt(Read("login-opening-browser-w100")!, true);
        Assert.Empty(opening.Options);
        Assert.Null(opening.Link);

        var picker = Read("login-picker-w100")!;
        Assert.Equal(3, LoginScreen.ToPrompt(picker, true).Options.Count);
        Assert.Empty(LoginScreen.ToPrompt(picker, false).Options);

        var err = LoginScreen.ToPrompt(Read("login-badcode-error-w100")!, true);
        Assert.Single(err.Options);
        Assert.NotNull(err.Body);
    }

    [Fact]
    public void Hash_differs_between_steps()
    {
        var hashes = new[] { "login-picker-w100", "login-url-w100", "login-badcode-error-w100", "login-opening-browser-w100" }
            .Select(n => Read(n)!.Hash).Distinct().Count();
        Assert.Equal(4, hashes);
    }
}
