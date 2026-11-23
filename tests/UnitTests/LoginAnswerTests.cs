using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-412: every /login answer is a verified press, and a code is typed only into
// an empty field, read back by its tail, and never kept.
public class LoginAnswerTests
{
    private static readonly SessionStatus Status = new() { Source = SessionSource.ClaudeCode, State = "waiting", Title = "" };

    private static string Picker => Cb412Captures.Screen("login-picker-w100");
    private static string Url => Cb412Captures.Screen("login-url-w100");
    private static string Typed => Cb412Captures.Screen("login-code-typed-w100");
    private static string Error => Cb412Captures.Screen("login-badcode-error-w100");
    private static string Hash(string screen) => LoginScreen.Read(screen)!.Hash;

    // A scripted pane: what the screen is now, every key pressed, every text typed.
    private sealed class Pane(string screen)
    {
        public string Screen = screen;
        public List<string> Pressed = new(), Typed = new();
        public bool TypeOk = true, PressOk = true;
        public Func<string, string?> AfterType = _ => null;
        public Func<string, string?> AfterPress = _ => null;
        public int Delays;

        public Task<string?> Capture(SessionStatus _) => Task.FromResult<string?>(Screen);

        public Task<bool> Type(SessionStatus _, string text)
        {
            Typed.Add(text);
            if (AfterType(text) is { } next) Screen = next;
            return Task.FromResult(TypeOk);
        }

        public Task<(bool Pressed, string? Before)> CaptureAndPress(SessionStatus _, string key, Func<string?, bool> approved)
        {
            var before = Screen;
            if (!approved(before)) return Task.FromResult((false, (string?)before));
            if (!PressOk) return Task.FromResult((false, (string?)before));
            Pressed.Add(key);
            if (AfterPress(key) is { } next) Screen = next;
            return Task.FromResult((true, (string?)before));
        }

        public Task Delay(TimeSpan _) { Delays++; return Task.CompletedTask; }
    }

    private static Task<string?> Press(Pane p, string hash, string key) =>
        LoginAnswer.PressAsync(Status, p.CaptureAndPress, hash, key);

    private static Task<string?> Submit(Pane p, string hash, string code) =>
        LoginAnswer.SubmitCodeAsync(Status, p.Capture, p.Type, p.CaptureAndPress, p.Delay, hash, code);

    // --- PressAsync ---

    [Fact]
    public async Task A_press_on_the_screen_that_was_shown_goes_through_once()
    {
        var pane = new Pane(Picker);
        Assert.Null(await Press(pane, Hash(Picker), "1"));
        Assert.Equal(new[] { "1" }, pane.Pressed);
    }

    [Fact]
    public async Task A_press_after_the_screen_changed_sends_no_key()
    {
        var pane = new Pane(Error);
        Assert.Equal("prompt-changed", await Press(pane, Hash(Picker), "1"));
        Assert.Empty(pane.Pressed);
    }

    [Fact]
    public async Task A_press_when_the_login_screen_is_gone_sends_no_key()
    {
        var pane = new Pane(Cb412Captures.Screen("login-interrupted-w100"));
        Assert.Equal("no-prompt", await Press(pane, Hash(Picker), "1"));
        Assert.Empty(pane.Pressed);
    }

    [Fact]
    public async Task A_press_the_terminal_refused_on_the_right_screen_is_press_failed()
    {
        var pane = new Pane(Picker) { PressOk = false };
        Assert.Equal("press-failed", await Press(pane, Hash(Picker), "1"));
    }

    [Fact]
    public async Task A_press_nothing_could_read_for_is_no_prompt()
    {
        var code = await LoginAnswer.PressAsync(Status, (_, _, ok) => Task.FromResult<(bool, string?)>((false, null)), "h", "1");
        Assert.Equal("no-prompt", code);
    }

    // --- CodeAllowed ---

    [Theory]
    [InlineData("abc123", true)]
    [InlineData("a", true)]
    [InlineData("héllo-wörld", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("two words", false)]
    [InlineData("tab\there", false)]
    [InlineData("line\nbreak", false)]
    [InlineData("esc\u001b[A", false)]
    [InlineData("del\u007f", false)]
    [InlineData("c1\u0085x", false)]
    public void CodeAllowed_is_one_clean_token(string? code, bool allowed) => Assert.Equal(allowed, LoginAnswer.CodeAllowed(code));

    [Fact]
    public void CodeAllowed_limits_utf8_bytes_not_characters()
    {
        Assert.True(LoginAnswer.CodeAllowed(new string('a', 1000)));
        Assert.False(LoginAnswer.CodeAllowed(new string('a', 1001)));
        Assert.False(LoginAnswer.CodeAllowed(new string('é', 501)));
        Assert.True(LoginAnswer.CodeAllowed(new string('é', 500)));
    }

    // --- SubmitCodeAsync ---

    private static Pane UrlPane() => new(Url)
    {
        // Typing masks the field the way Claude Code does: all but the last six.
        AfterType = t => Typed.Replace("lstate", t[^Math.Min(6, t.Length)..]),
    };

    [Fact]
    public async Task A_code_is_typed_literally_read_back_and_then_sent_with_Enter()
    {
        var pane = UrlPane();
        Assert.Null(await Submit(pane, Hash(Url), "abcdef-lstate"));
        Assert.Equal(new[] { "abcdef-lstate" }, pane.Typed);
        Assert.Equal(new[] { "Enter" }, pane.Pressed);
    }

    [Theory]
    [InlineData("Enter")]
    [InlineData("C-c")]
    public async Task A_code_that_looks_like_a_key_name_is_typed_not_pressed(string code)
    {
        var pane = UrlPane();
        await Submit(pane, Hash(Url), code);
        Assert.Equal(new[] { code }, pane.Typed);
        Assert.Equal(new[] { "Enter" }, pane.Pressed);
    }

    [Fact]
    public async Task A_short_code_is_read_back_in_full()
    {
        var pane = UrlPane();
        Assert.Null(await Submit(pane, Hash(Url), "ab"));
    }

    [Theory]
    [InlineData("two words")]
    [InlineData("")]
    public async Task A_code_that_is_not_allowed_types_nothing(string code)
    {
        var pane = UrlPane();
        Assert.Equal("type-failed", await Submit(pane, Hash(Url), code));
        Assert.Empty(pane.Typed);
        Assert.Empty(pane.Pressed);
    }

    [Fact]
    public async Task No_login_screen_means_nothing_is_typed()
    {
        var pane = new Pane(Cb412Captures.Screen("login-interrupted-w100"));
        Assert.Equal("no-prompt", await Submit(pane, "h", "abc"));
        Assert.Empty(pane.Typed);
    }

    [Theory]
    [InlineData("login-picker-w100")]
    [InlineData("login-badcode-error-w100")]
    public async Task A_screen_that_is_not_the_url_step_types_nothing(string screen)
    {
        var pane = new Pane(Cb412Captures.Screen(screen));
        Assert.Equal("prompt-changed", await Submit(pane, Hash(Url), "abc"));
        Assert.Empty(pane.Typed);
    }

    [Fact]
    public async Task A_url_screen_with_another_hash_types_nothing()
    {
        var pane = UrlPane();
        Assert.Equal("prompt-changed", await Submit(pane, "an-earlier-attempt", "abc"));
        Assert.Empty(pane.Typed);
    }

    [Fact]
    public async Task A_field_that_is_not_empty_is_never_typed_into()
    {
        var pane = new Pane(Typed);
        Assert.Equal("not-empty", await Submit(pane, Hash(Url), "abc"));
        Assert.Empty(pane.Typed);
        Assert.Empty(pane.Pressed);
    }

    [Fact]
    public async Task A_type_that_failed_presses_no_Enter()
    {
        var pane = UrlPane();
        pane.TypeOk = false;
        Assert.Equal("type-failed", await Submit(pane, Hash(Url), "abcdef"));
        Assert.Empty(pane.Pressed);
    }

    [Fact]
    public async Task Typed_text_that_never_reads_back_waits_two_seconds_then_presses_no_Enter()
    {
        var pane = new Pane(Url); // typing changes nothing on screen
        Assert.Equal("unverified", await Submit(pane, Hash(Url), "abcdef"));
        Assert.Equal(20, pane.Delays);
        Assert.Empty(pane.Pressed);
    }

    [Fact]
    public async Task A_tail_that_arrives_late_is_still_read()
    {
        var pane = new Pane(Url);
        var n = 0;
        Task Delay(TimeSpan _) { if (++n == 3) pane.Screen = Typed; return Task.CompletedTask; }
        var code = await LoginAnswer.SubmitCodeAsync(Status, pane.Capture, pane.Type, pane.CaptureAndPress, Delay, Hash(Url), "xx-lstate");
        Assert.Null(code);
        Assert.Equal(new[] { "Enter" }, pane.Pressed);
    }

    [Fact]
    public async Task The_screen_changing_while_waiting_for_the_read_back_presses_nothing()
    {
        var pane = new Pane(Url) { AfterType = _ => Error };
        Assert.Equal("prompt-changed", await Submit(pane, Hash(Url), "abcdef"));
        Assert.Empty(pane.Pressed);
    }

    [Fact]
    public async Task The_screen_vanishing_while_waiting_for_the_read_back_presses_nothing()
    {
        var pane = new Pane(Url) { AfterType = _ => Cb412Captures.Screen("login-interrupted-w100") };
        Assert.Equal("no-prompt", await Submit(pane, Hash(Url), "abcdef"));
        Assert.Empty(pane.Pressed);
    }

    [Fact]
    public async Task The_screen_changing_between_the_read_back_and_Enter_presses_nothing()
    {
        var pane = UrlPane();
        var code = await LoginAnswer.SubmitCodeAsync(Status, pane.Capture, pane.Type,
            (s, k, ok) => { pane.Screen = Error; return pane.CaptureAndPress(s, k, ok); },
            pane.Delay, Hash(Url), "abcdef");
        Assert.Equal("prompt-changed", code);
        Assert.Empty(pane.Pressed);
    }

    [Fact]
    public async Task An_Enter_the_terminal_refuses_on_the_right_screen_is_press_failed()
    {
        var pane = UrlPane();
        pane.PressOk = false;
        Assert.Equal("press-failed", await Submit(pane, Hash(Url), "abcdef"));
    }

    [Fact]
    public async Task Refusal_codes_never_carry_the_code()
    {
        const string secret = "QA-CANARY-9f3e2c";
        foreach (var pane in new[] { new Pane(Typed), new Pane(Url) { TypeOk = false }, new Pane(Url), new Pane(Error) })
        {
            var code = await Submit(pane, Hash(Url), secret);
            Assert.DoesNotContain(secret, code ?? "");
        }
    }
}
