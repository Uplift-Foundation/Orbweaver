using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>
/// CB-412 slice C: /login on its way to a phone (frames-v14-login). Real captures
/// from tests/TranscriptTests/Captures/cb412, a fake pane, and the frames that
/// reach the "relay". The code is a canary throughout: the tests assert where
/// it is NOT as much as where it is.
/// </summary>
public class RelayLoginPromptTests
{
    private const string PhoneId = "ph_a1b2c3d4e5f60718";
    private const string Canary = "QA-CANARY-9f3k2xlstate";
    private static readonly KeyPair Phone = KeyPairFromPrivate(SHA256.HashData("login-phone"u8.ToArray()));
    private const string Cleared = "⏺ Running…\n\n> \n";

    internal static string Capture(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "tests", "TranscriptTests", "Captures", "cb412", name + ".txt");
            if (File.Exists(f)) return File.ReadAllText(f);
        }
        throw new FileNotFoundException(name);
    }

    private sealed class Pane
    {
        public string? Screen;
        public string? OnType;              // what the screen becomes once something is typed
        public string? OnEnter = Cleared;   // ... and once Enter is pressed
        public readonly List<string> Keys = new(), Typed = new();
        public Task<string?> Read(SessionStatus _) => Task.FromResult(Screen);
        public Task<bool> Press(SessionStatus _, string key)
        {
            Keys.Add(key);
            if (key is "1" or "2" or "3") Screen = Cleared;
            if (key == "Enter" && OnEnter is not null) Screen = OnEnter;
            return Task.FromResult(true);
        }
        public Task<bool> Type(SessionStatus _, string text)
        {
            Typed.Add(text);
            if (OnType is not null) Screen = OnType;
            return Task.FromResult(true);
        }
    }

    private sealed class Sender : IPromptSender
    {
        public readonly List<(string Rid, string Prompt, string? Push)> Prompts = new();
        public readonly List<string> QuietPrompts = new(), Loud = new();
        public readonly List<(string Rid, string By)> Resolved = new();
        public readonly List<string> Refusals = new();
        bool IPromptSender.Prompt(string r, string s, string p, string? push) { Loud.Add(r); Prompts.Add((r, p, push)); return true; }
        bool IPromptSender.PromptQuiet(string r, string s, string p) { QuietPrompts.Add(r); Prompts.Add((r, p, null)); return true; }
        void IPromptSender.ResolvedQuiet(string r, string s, string by) => Resolved.Add((r, by));
        void IPromptSender.Resolved(string r, string s, string by, string? push) => Resolved.Add((r, by));
        void IPromptSender.Refusal(string ph, string r, string s, string code) => Refusals.Add(code);
        bool IPromptSender.PromptTo(string ph, string r, string s, string p) => true;
    }

    private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(1794111792);
    private readonly Pane _pane = new();
    private readonly Sender _sent = new();
    private readonly List<string> _log = new();
    private readonly PromptPipeline _p;

    public RelayLoginPromptTests() => _p = Make(null);

    private PromptPipeline Make(Func<SessionStatus, bool>? canPress) =>
        new(new PromptPipeline.Seams(_pane.Read, _pane.Press, _pane.Type, () => _now, _ => Task.CompletedTask, _log.Add, canPress), _sent, "Test Mac");

    private static (string, SessionStatus)[] Snap() => [("s1", new SessionStatus { State = "waiting", Source = SessionSource.ClaudeCode, Title = "orbweaver" })];

    private JsonElement Sent(int i = 0) => JsonDocument.Parse(_sent.Prompts[i].Prompt).RootElement;

    private PhoneAnswer AnswerTo(string action, string note = "", string? scope = null, KeyPair? signer = null)
    {
        var p = Sent();
        var rid = p.GetProperty("request_id").GetString()!;
        var exp = p.GetProperty("expires_at").GetInt64();
        scope ??= note.Length > 0 ? AnswerSigning.TellScope(note) : "";
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(16));
        var sig = Base64Url(Sign(signer ?? Phone, AnswerSigning.Message(AnswerSigning.Body(rid, "s1", action, scope, nonce, exp))));
        return new PhoneAnswer(rid, "s1", action, scope, note, nonce, exp, p.GetProperty("screen_hash").GetString()!, sig);
    }

    private PhoneAnswer AnswerTo2(int i, string action, string note)
    {
        var p = Sent(i);
        var rid = p.GetProperty("request_id").GetString()!;
        var exp = p.GetProperty("expires_at").GetInt64();
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(16));
        var sig = Base64Url(Sign(Phone, AnswerSigning.Message(AnswerSigning.Body(rid, "s1", action, AnswerSigning.TellScope(note), nonce, exp))));
        return new PhoneAnswer(rid, "s1", action, AnswerSigning.TellScope(note), note, nonce, exp, p.GetProperty("screen_hash").GetString()!, sig);
    }

    // ---- the frame ---------------------------------------------------------------

    [Fact]
    public void LoginInfo_carries_a_step_and_only_what_is_allowed()
    {
        Assert.Equal("{\"step\":\"picker\"}", new LoginInfo("picker").Json().ToJsonString());
        Assert.Equal("{\"step\":\"error\"}", new LoginInfo("error", "https://x.test/a").Json().ToJsonString().Replace(",\"url\":\"https://x.test/a\"", ""));
        Assert.Equal("{\"step\":\"url\",\"url\":\"https://claude.ai/oauth/authorize?a\",\"code\":true}",
            new LoginInfo("url", "https://claude.ai/oauth/authorize?a", true).Json().ToJsonString());
    }

    [Theory]
    [InlineData("http://claude.ai/x", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("https://claude.ai/x", true)]
    public void A_url_that_is_not_https_is_left_out(string? url, bool kept)
    {
        Assert.Equal(kept, new LoginInfo("url", url).Json().ContainsKey("url"));
    }

    [Fact]
    public void A_url_over_2048_characters_is_left_out_and_2048_is_kept()
    {
        var at = "https://x.test/" + new string('a', 2048 - 15);
        Assert.Equal(2048, at.Length);
        Assert.True(new LoginInfo("url", at).Json().ContainsKey("url"));
        Assert.False(new LoginInfo("url", at + "a").Json().ContainsKey("url"));
    }

    [Fact]
    public void An_ordinary_prompt_has_no_login_key()
    {
        var f = PromptFrames.Prompt("r", "s", "permission", "claude", "Bash", "p", [], "h", 1);
        Assert.False(JsonDocument.Parse(f).RootElement.TryGetProperty("login", out _));
    }

    // ---- minting ---------------------------------------------------------------------

    [Fact]
    public async Task The_picker_goes_out_as_a_quiet_question_with_its_digits_and_no_push()
    {
        _pane.Screen = Capture("login-picker-w100");
        await _p.OnScannedAsync(Snap());
        var j = Sent();
        Assert.Equal(("question", "Login", true), (j.GetProperty("kind").GetString(), j.GetProperty("tool").GetString(), j.GetProperty("quiet").GetBoolean()));
        Assert.Equal(["1", "2", "3"], j.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("key").GetString()).ToArray());
        Assert.All(j.GetProperty("options").EnumerateArray(), o => Assert.Equal("option", o.GetProperty("kind").GetString()));
        Assert.Equal("picker", j.GetProperty("login").GetProperty("step").GetString());
        Assert.False(j.GetProperty("login").TryGetProperty("code", out _));
        Assert.Contains("Select login method", j.GetProperty("preview").GetString());
        Assert.Null(_sent.Prompts[0].Push);
        Assert.Empty(_sent.Loud);
        Assert.Equal("Login", _p.ActivityFor("s1")!.Command);
    }

    [Theory]
    [InlineData("login-picker-w100")]
    [InlineData("login-console-picker-w100")]
    [InlineData("login-thirdparty-picker-w100")]
    [InlineData("login-picker-after-retry-w100")]
    public async Task Every_picker_is_a_question_never_a_permission(string fixture)
    {
        _pane.Screen = Capture(fixture);
        await _p.OnScannedAsync(Snap());
        Assert.Equal("question", Sent().GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("login-url-w100")]
    [InlineData("login-url-w60")]
    public async Task The_url_step_offers_no_keys_takes_a_code_and_keeps_the_url_out_of_the_preview(string fixture)
    {
        var screen = Capture(fixture);
        _pane.Screen = screen;
        await _p.OnScannedAsync(Snap());
        var raw = _sent.Prompts[0].Prompt;
        var j = Sent();
        var login = j.GetProperty("login");
        Assert.Equal(("url", true), (login.GetProperty("step").GetString(), login.GetProperty("code").GetBoolean()));
        var url = login.GetProperty("url").GetString()!;
        Assert.StartsWith("https://claude.ai/oauth/authorize?", url);
        Assert.DoesNotContain(" ", url);
        Assert.Empty(j.GetProperty("options").EnumerateArray());
        Assert.Equal(PromptPipeline.LoginUrlPreview, j.GetProperty("preview").GetString());
        // The URL is in exactly one place in the frame: login.url.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(raw, "oauth/authorize"));
        Assert.Null(_sent.Prompts[0].Push);
        Assert.DoesNotContain(url, string.Join("\n", _log));
        Assert.DoesNotContain("oauth", _p.ActivityFor("s1")!.Command);
        Assert.True(_p.PendingFor("s1")!.Code);
        Assert.False(_p.PendingFor("s1")!.DeskOnly);
    }

    [Fact]
    public async Task The_url_step_on_a_route_that_cannot_be_pressed_has_the_link_but_no_code()
    {
        var p = Make(_ => false);
        _pane.Screen = Capture("login-url-w100");
        await p.OnScannedAsync(Snap());
        var login = Sent().GetProperty("login");
        Assert.Equal("url", login.GetProperty("step").GetString());
        Assert.StartsWith("https://", login.GetProperty("url").GetString());
        Assert.False(login.TryGetProperty("code", out _));
        Assert.False(p.PendingFor("s1")!.Code);
        Assert.True(p.PendingFor("s1")!.DeskOnly);
    }

    [Fact]
    public async Task The_error_screen_offers_Try_again_on_Enter()
    {
        _pane.Screen = Capture("login-badcode-error-w100");
        await _p.OnScannedAsync(Snap());
        var j = Sent();
        Assert.Equal("error", j.GetProperty("login").GetProperty("step").GetString());
        var o = Assert.Single(j.GetProperty("options").EnumerateArray());
        Assert.Equal(("Enter", "Try again", "option"), (o.GetProperty("key").GetString(), o.GetProperty("label").GetString(), o.GetProperty("kind").GetString()));
        Assert.Contains("OAuth error", j.GetProperty("preview").GetString());
    }

    [Fact]
    public async Task The_opening_browser_moment_mints_nothing_and_settles_a_picker_before_it()
    {
        _pane.Screen = Capture("login-opening-browser-w100");
        await _p.OnScannedAsync(Snap());
        Assert.Empty(_sent.Prompts);

        _pane.Screen = Capture("login-picker-w100");
        await _p.OnScannedAsync(Snap());
        _pane.Screen = Capture("login-opening-browser-w100");
        await _p.OnScannedAsync(Snap());
        Assert.Equal("desk", Assert.Single(_sent.Resolved).By);
    }

    [Fact]
    public async Task A_picker_that_goes_unpressable_is_sent_with_no_options()
    {
        var p = Make(_ => false);
        _pane.Screen = Capture("login-picker-w100");
        await p.OnScannedAsync(Snap());
        Assert.Empty(Sent().GetProperty("options").EnumerateArray());
        Assert.True(p.PendingFor("s1")!.DeskOnly);
    }

    [Fact]
    public async Task A_quiet_login_prompt_that_outlives_its_lifetime_is_minted_again_with_a_fresh_id()
    {
        _pane.Screen = Capture("login-picker-w100");
        await _p.OnScannedAsync(Snap());
        _now = _now.AddSeconds(PromptFrames.Lifetime + 1);
        await _p.OnScannedAsync(Snap());
        Assert.Equal(2, _sent.Prompts.Count);
        Assert.NotEqual(_sent.Prompts[0].Rid, _sent.Prompts[1].Rid);
    }

    // ---- answers -----------------------------------------------------------------------

    [Fact]
    public async Task A_phone_answers_the_picker_with_its_digit()
    {
        _pane.Screen = Capture("login-picker-w100");
        await _p.OnScannedAsync(Snap());
        await _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo("option:1"));
        Assert.Equal(["1"], _pane.Keys);
        Assert.Equal("phone:" + PhoneId, Assert.Single(_sent.Resolved).By);
    }

    [Fact]
    public async Task A_phone_retries_from_the_error_screen_with_Enter()
    {
        _pane.Screen = Capture("login-badcode-error-w100");
        _pane.OnEnter = Capture("login-picker-after-retry-w100");
        await _p.OnScannedAsync(Snap());
        await _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo("option:Enter"));
        Assert.Equal(["Enter"], _pane.Keys);
        Assert.Equal("phone:" + PhoneId, Assert.Single(_sent.Resolved).By);
    }

    [Fact]
    public async Task A_code_is_typed_read_back_and_entered_and_resolves_when_the_url_step_is_gone()
    {
        _pane.Screen = Capture("login-url-w100");
        _pane.OnType = Capture("login-code-typed-w100");
        await _p.OnScannedAsync(Snap());
        await _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo("code", Canary));
        Assert.Equal([Canary], _pane.Typed);
        Assert.Equal(["Enter"], _pane.Keys);
        Assert.Equal("phone:" + PhoneId, Assert.Single(_sent.Resolved).By);
        Assert.Empty(_sent.Refusals);
    }

    [Fact]
    public async Task A_wrong_code_arrives_as_a_new_error_prompt_after_the_phone_is_resolved()
    {
        _pane.Screen = Capture("login-url-w100");
        _pane.OnType = Capture("login-code-typed-w100");
        _pane.OnEnter = Capture("login-badcode-error-w100");
        await _p.OnScannedAsync(Snap());
        await _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo("code", Canary));
        Assert.Equal("phone:" + PhoneId, Assert.Single(_sent.Resolved).By);
        await _p.OnScannedAsync(Snap());
        Assert.Equal("error", Sent(1).GetProperty("login").GetProperty("step").GetString());
    }

    [Fact]
    public async Task A_code_that_does_not_read_back_never_presses_Enter_and_is_stale()
    {
        _pane.Screen = Capture("login-url-w100");   // typing changes nothing on screen
        await _p.OnScannedAsync(Snap());
        await _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo("code", Canary));
        Assert.DoesNotContain("Enter", _pane.Keys);
        Assert.Equal([AnswerGate.StalePrompt], _sent.Refusals);
        Assert.Empty(_sent.Resolved);
    }

    [Fact]
    public async Task A_code_with_the_field_already_holding_text_types_nothing()
    {
        _pane.Screen = Capture("login-code-typed-w100");   // a hash the URL step shares: the field is not hashed
        await _p.OnScannedAsync(Snap());
        await _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo("code", Canary));
        Assert.Empty(_pane.Typed);
        Assert.Empty(_pane.Keys);
        Assert.Equal([AnswerGate.StalePrompt], _sent.Refusals);
    }

    [Fact]
    public async Task A_code_after_the_screen_moved_on_is_refused_and_nothing_is_typed()
    {
        _pane.Screen = Capture("login-url-w100");
        await _p.OnScannedAsync(Snap());
        var a = AnswerTo("code", Canary);
        _pane.Screen = Capture("login-badcode-error-w100");
        await _p.OnAnswerAsync(PhoneId, Phone.Public, a);
        Assert.Empty(_pane.Typed);
        Assert.Empty(_pane.Keys);
        Assert.Equal([AnswerGate.StalePrompt], _sent.Refusals);
    }

    [Fact]
    public async Task A_code_that_is_typed_and_entered_but_leaves_the_url_step_up_is_no_effect_and_can_be_retried()
    {
        _pane.Screen = Capture("login-url-w100");
        _pane.OnType = Capture("login-code-typed-w100");
        _pane.OnEnter = null;                              // Enter changes nothing
        await _p.OnScannedAsync(Snap());
        await _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo("code", Canary));
        Assert.Equal([AnswerGate.StalePrompt], _sent.Refusals);
        Assert.False(_p.PendingFor("s1")!.Answered);
    }

    [Fact]
    public async Task The_code_is_in_no_log_line_frame_or_refusal()
    {
        _pane.Screen = Capture("login-url-w100");
        _pane.OnType = Capture("login-code-typed-w100");
        await _p.OnScannedAsync(Snap());
        await _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo("code", Canary));
        var everything = string.Join("\n", _log) + string.Join("\n", _sent.Prompts.Select(p => p.Prompt))
            + string.Join("\n", _sent.Refusals) + string.Join("\n", _sent.Resolved.Select(r => r.By));
        Assert.DoesNotContain(Canary, everything);
        Assert.DoesNotContain("lstate", everything);
        Assert.Contains(_log, l => l.StartsWith("press ") && l.EndsWith(" code"));
    }

    [Fact]
    public async Task The_urls_query_is_only_in_login_url_never_in_a_log_activity_push_or_refusal()
    {
        const string Opaque = "EXAMPLEexampleEXAMPLEexample";
        _pane.Screen = Capture("login-url-w100");
        await _p.OnScannedAsync(Snap());
        // A refusal too, so there is one to look in: an action the URL step does not take.
        await _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo("approve"));
        var raw = _sent.Prompts[0].Prompt;
        // The control: it is in login.url, and that is the one place in the frame.
        Assert.Contains(Opaque, Sent().GetProperty("login").GetProperty("url").GetString());
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(raw, Opaque));
        var activity = _p.ActivityFor("s1")!;
        Assert.DoesNotContain(Opaque, activity.RequestId + activity.Tool + activity.Command);
        Assert.Null(_sent.Prompts[0].Push);
        Assert.Equal([AnswerGate.ForbiddenOption], _sent.Refusals);
        Assert.DoesNotContain(Opaque, string.Join("\n", _log) + string.Join("\n", _sent.Refusals) + string.Join("\n", _sent.Resolved.Select(r => r.By)));
    }

    [Fact]
    public async Task A_code_answered_for_an_earlier_sign_in_attempt_is_stale()
    {
        _pane.Screen = Capture("login-url-w100");
        await _p.OnScannedAsync(Snap());
        var a = AnswerTo("code", Canary);
        // A new attempt: same screen shape, a different URL, so a different hash.
        _pane.Screen = Capture("login-url-w100").Replace("EXAMPLEexample", "ANOTHERvalue1");
        await _p.OnAnswerAsync(PhoneId, Phone.Public, a);
        Assert.Empty(_pane.Typed);
        Assert.Equal([AnswerGate.StalePrompt], _sent.Refusals);
    }

    [Fact]
    public async Task Wrong_actions_on_the_picker_press_nothing()
    {
        _pane.Screen = Capture("login-picker-w100");
        await _p.OnScannedAsync(Snap());
        await _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo("approve"));
        await _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo("code", Canary));
        Assert.Empty(_pane.Keys);
        Assert.Empty(_pane.Typed);
        Assert.Equal([AnswerGate.ForbiddenOption, AnswerGate.ForbiddenOption], _sent.Refusals);
    }

    [Fact]
    public async Task A_refused_code_shows_the_notice_on_the_phone_and_mints_a_new_prompt()
    {
        _pane.Screen = Capture("login-url-w100");
        _pane.OnType = Capture("login-code-typed-w100");
        _pane.OnEnter = Capture("login-code-shape-iterm2-w60");   // Claude Code refuses it locally
        await _p.OnScannedAsync(Snap());
        await _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo("code", Canary));
        // The old prompt is settled, not stuck, and the refusal is a fresh prompt.
        Assert.Empty(_sent.Refusals);
        Assert.Single(_sent.Resolved);
        await _p.OnScannedAsync(Snap());
        Assert.Equal(2, _sent.Prompts.Count);
        var j = Sent(1);
        var preview = j.GetProperty("preview").GetString()!;
        Assert.StartsWith("That doesn't look like the full code.", preview);
        Assert.EndsWith(PromptPipeline.LoginUrlPreview, preview);
        Assert.DoesNotContain("https://", preview);
        Assert.DoesNotContain("oauth", preview);
        Assert.NotEqual(Sent(0).GetProperty("screen_hash").GetString(), j.GetProperty("screen_hash").GetString());
        Assert.True(j.GetProperty("login").GetProperty("code").GetBoolean());
        Assert.DoesNotContain(Canary, string.Join("\n", _log) + string.Join("\n", _sent.Prompts.Select(p => p.Prompt)));
    }

    // CB-418 (QA 1): a second bad code redraws the identical refused screen, so the same hash.
    [Fact]
    public async Task A_second_refused_code_mints_a_third_prompt_even_though_the_screen_is_identical()
    {
        var refused = Capture("login-code-shape-iterm2-w60");
        _pane.Screen = Capture("login-url-w100");
        _pane.OnType = Capture("login-code-typed-w100");
        _pane.OnEnter = refused;
        await _p.OnScannedAsync(Snap());
        await _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo("code", Canary));
        await _p.OnScannedAsync(Snap());
        Assert.Equal(2, _sent.Prompts.Count);

        // Same screen again after the second attempt, the code typed into its box first.
        _pane.Screen = refused;
        _pane.OnType = string.Join("\n", refused.Split('\n').Select(l =>
            l.Trim().StartsWith('│') && l.Trim().Trim('│').Trim().Length == 0 ? "  │ ********************lstate" + new string(' ', 20) + "│" : l));
        await _p.OnAnswerAsync(PhoneId, Phone.Public, AnswerTo2(1, "code", Canary));
        await _p.OnScannedAsync(Snap());
        Assert.Equal(3, _sent.Prompts.Count);
        Assert.Empty(_sent.Refusals);
        Assert.StartsWith("That doesn't look like the full code.", Sent(2).GetProperty("preview").GetString());
    }
}
