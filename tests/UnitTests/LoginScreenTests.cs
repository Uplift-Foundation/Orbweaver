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

    private static string[] Rows(string name) => Cb412Captures.Screen(name).Split('\n');
    private static string FullUrl => Read("login-url-w100")!.Url!;

    // The URL as a reader that returns logical lines would give it: one line.
    [Fact]
    public void Url_as_one_unwrapped_line_is_the_full_url()
    {
        var rows = Rows("login-url-w60").ToList();
        var i = rows.FindIndex(r => r.StartsWith("https://"));
        rows[i] += rows[i + 1];
        rows.RemoveAt(i + 1);
        Assert.Equal(FullUrl, LoginScreen.Read(string.Join("\n", rows))!.Url);
    }

    [Fact]
    public void Wrapped_rows_with_a_leading_indent_and_padding_give_the_full_url()
    {
        var rows = Rows("login-url-w60").ToList();
        var i = rows.FindIndex(r => r.StartsWith("https://"));
        rows[i] += "  ";
        rows[i + 1] = "    " + rows[i + 1] + "   ";
        Assert.Equal(FullUrl, LoginScreen.Read(string.Join("\n", rows))!.Url);
    }

    [Fact]
    public void An_indented_continuation_with_no_blank_row_before_the_paste_row_is_not_a_link()
    {
        var rows = Rows("login-url-w60").ToList();
        var i = rows.FindIndex(r => r.StartsWith("https://"));
        rows[i + 1] = "    " + rows[i + 1];
        rows.RemoveAt(i + 2); // the blank row
        Assert.Null(LoginScreen.Read(string.Join("\n", rows)));
    }

    [Fact]
    public void A_url_with_no_continuation_and_no_blank_row_is_not_a_link()
    {
        var rows = Rows("login-url-w60").ToList();
        var i = rows.FindIndex(r => r.StartsWith("https://"));
        rows.RemoveRange(i + 1, 2); // the continuation and the blank row
        Assert.Null(LoginScreen.Read(string.Join("\n", rows)));
    }

    [Fact]
    public void A_screen_cut_off_mid_url_is_not_a_link()
    {
        var rows = Rows("login-url-w60");
        var cut = string.Join("\n", rows.TakeWhile(r => !r.Contains("Paste code")));
        Assert.Null(LoginScreen.Read(cut));
    }

    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("abcdef", "abcdef")]
    [InlineData("***xyz", "xyz")]
    public void A_short_code_is_its_own_tail(string field, string tail)
    {
        var screen = Cb412Captures.Screen("login-url-w100").Replace("│                                                                                              │", "│ " + field.PadRight(94) + "│");
        var s = LoginScreen.Read(screen)!;
        Assert.False(s.FieldEmpty);
        Assert.Equal(tail, s.FieldTail);
    }

    [Fact]
    public void An_error_whose_text_runs_to_the_last_row_is_still_read_when_the_footer_is_above()
    {
        var screen = $"{new string('─', 40)}\n  Login\n\n  Press Enter to retry\n\n  ✘ OAuth error: first\n    wrapped to the end";
        var s = LoginScreen.Read(screen)!;
        Assert.Equal(LoginStep.Error, s.Step);
        Assert.Equal("OAuth error: first wrapped to the end", s.Title);
    }

    // CB-418: Claude Code refuses a code with no '#' locally and redraws the URL step
    // with a ✘ notice under the box. Real iTerm2 read at 60 columns: trailing spaces,
    // the URL wrapped with no indent.
    [Fact]
    public void A_refused_code_notice_leads_the_url_steps_preview_and_changes_the_hash()
    {
        var refused = Read("login-code-shape-iterm2-w60")!;
        Assert.Equal(LoginStep.Url, refused.Step);
        Assert.True(refused.FieldEmpty);
        Assert.StartsWith("https://claude.ai/oauth/authorize?", refused.Url);
        Assert.DoesNotContain(" ", refused.Url);
        Assert.Equal(new[]
        {
            "That doesn't look like the full code. Copy all of it, including the # in the middle.",
            LoginScreen.DeskSentence,
        }, refused.Preview);

        // The same screen without the notice rows is the plain URL step, a different prompt.
        var rows = Cb412Captures.Screen("login-code-shape-iterm2-w60").Split('\n');
        var plain = LoginScreen.Read(string.Join("\n", rows.Where(r => !r.Contains("✘") && !r.Contains("Copy all of it"))))!;
        Assert.Equal(new[] { LoginScreen.DeskSentence }, plain.Preview);
        Assert.Equal(refused.Url, plain.Url);
        Assert.NotEqual(plain.Hash, refused.Hash);

        var prompt = LoginScreen.ToPrompt(refused, canPress: true);
        Assert.Contains(prompt.Body!, l => l.Contains("doesn't look like the full code"));
        Assert.DoesNotContain(prompt.Body!, l => l.Contains("https://"));
        Assert.Equal(refused.Url, prompt.Link);
    }
}
