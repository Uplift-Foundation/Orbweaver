using System.Security.Cryptography;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>
/// CB-412 frames-v14-login, the gate's half: rule 1 binds a code's note through
/// its scope, rule 1w keeps it off the watch key, and rule 7 takes it only on a
/// prompt minted with login.code, a fresh screen that still reads as the URL
/// step, and a note LoginAnswer allows. One case per way to break one thing.
/// </summary>
public class AnswerGateLoginTests
{
    private const string PhoneId = "ph_a1b2c3d4e5f60718";
    private const string Rid = "01JC9R0A5N2Q7W8X3E4T6Y9ZBD";
    private const string Sid = "0f6c4f2a";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1794111792);
    private static readonly long Expires = Now.ToUnixTimeSeconds() + 1800;
    private static readonly KeyPair Phone = KeyPairFromPrivate(SHA256.HashData("gate-login-phone"u8.ToArray()));
    private static readonly KeyPair Watch = KeyPairFromPrivate(SHA256.HashData("gate-login-watch"u8.ToArray()));

    private static readonly string Url = RelayLoginPromptTests.Capture("login-url-w100");
    private static readonly string Picker = RelayLoginPromptTests.Capture("login-picker-w100");
    private static readonly PromptFingerprint UrlFp = PromptFingerprint.Of(Url)!;

    private static PendingPrompt Pending(bool code = true, bool deskOnly = false, string? hash = null) =>
        new(Rid, Sid, "question", hash ?? UrlFp.Hash, Expires, deskOnly, [], code: code);

    private static PhoneAnswer Code(string note = "abcDEF123-_", string? scope = null, string? hash = null, KeyPair? signer = null, string? key = null, string action = "code")
    {
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(16));
        scope ??= AnswerSigning.TellScope(note);
        var sig = Base64Url(Sign(signer ?? Phone, AnswerSigning.Message(AnswerSigning.Body(Rid, Sid, action, scope, nonce, Expires))));
        return new PhoneAnswer(Rid, Sid, action, scope, note, nonce, Expires, hash ?? UrlFp.Hash, sig, key);
    }

    private static AnswerGate.Decision Decide(PhoneAnswer a, PendingPrompt? p = null, string? fresh = null) =>
        AnswerGate.Decide(p ?? Pending(), a, fresh ?? Url, PhoneId, Phone.Public, Now, new NonceWindow(), Watch.Public);

    private static void Refused(AnswerGate.Decision d, int rule, string code) =>
        Assert.Equal((rule, code, false), (d.Rule, d.Code, d.Press));

    [Fact]
    public void A_code_on_the_url_step_is_taken_and_names_the_note_to_type()
    {
        var d = Decide(Code("abcDEF123-_"));
        Assert.True(d.Press);
        Assert.Equal((8, true, "abcDEF123-_", null), (d.Rule, d.IsLoginCode, d.Text, d.Key));
    }

    [Fact]
    public void The_codes_scope_must_be_the_hash_of_its_note()
    {
        Refused(Decide(Code("abc", scope: AnswerSigning.TellScope("abd"))), 1, AnswerGate.BadSignature);
    }

    [Fact]
    public void A_signature_from_another_key_is_bad()
    {
        Refused(Decide(Code(signer: Watch)), 1, AnswerGate.BadSignature);
    }

    [Fact]
    public void A_watch_signed_code_is_forbidden_at_rule_1()
    {
        Refused(Decide(Code(signer: Watch, key: "watch")), 1, AnswerGate.ForbiddenOption);
    }

    [Fact]
    public void A_code_for_an_earlier_attempt_is_stale_at_rule_5_and_6()
    {
        Refused(Decide(Code(hash: "someOtherHash")), 5, AnswerGate.StalePrompt);
        Refused(Decide(Code(), fresh: Url.Replace("EXAMPLEexample", "ANOTHERvalue1")), 6, AnswerGate.StalePrompt);
    }

    [Fact]
    public void A_code_on_a_prompt_minted_without_login_code_is_forbidden()
    {
        Refused(Decide(Code(), Pending(code: false)), 7, AnswerGate.ForbiddenOption);
        Refused(Decide(Code(), Pending(deskOnly: true)), 7, AnswerGate.ForbiddenOption);
    }

    [Fact]
    public void A_code_while_the_screen_is_the_picker_is_forbidden()
    {
        var fp = PromptFingerprint.Of(Picker)!;
        Refused(Decide(Code(hash: fp.Hash), Pending(hash: fp.Hash), Picker), 7, AnswerGate.ForbiddenOption);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("tab\there")]
    [InlineData("line\nbreak")]
    [InlineData("del\u007f")]
    [InlineData("c1\u0085")]
    public void A_note_that_is_not_one_token_is_forbidden(string note)
    {
        Refused(Decide(Code(note)), 7, AnswerGate.ForbiddenOption);
    }

    [Fact]
    public void A_note_over_1000_bytes_is_forbidden_and_1000_is_taken()
    {
        Assert.True(Decide(Code(new string('a', 1000))).Press);
        Refused(Decide(Code(new string('a', 1001))), 7, AnswerGate.ForbiddenOption);
    }

    [Theory]
    [InlineData("Enter")]
    [InlineData("C-c")]
    public void A_code_that_is_a_key_name_is_taken_as_text_not_pressed_as_a_key(string note)
    {
        var d = Decide(Code(note));
        Assert.Equal((true, note, (string?)null), (d.IsLoginCode, d.Text, d.Key));
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("deny")]
    [InlineData("option:1")]
    [InlineData("option:Enter")]
    public void Any_other_action_on_the_url_step_is_forbidden(string action)
    {
        Refused(Decide(Code("", action: action)), 7, AnswerGate.ForbiddenOption);
    }

    [Fact]
    public void A_code_on_a_form_prompt_is_forbidden_by_the_same_rule()
    {
        var p = new PendingPrompt(Rid, Sid, "question", UrlFp.Hash, Expires, text: true);
        Refused(Decide(Code(), p), 7, AnswerGate.ForbiddenOption);
    }

    [Fact]
    public void A_picker_option_still_presses_its_digit()
    {
        var fp = PromptFingerprint.Of(Picker)!;
        var p = new PendingPrompt(Rid, Sid, "question", fp.Hash, Expires, false, ["1", "2", "3"]);
        var d = AnswerGate.Decide(p, Code("", hash: fp.Hash, action: "option:2"), Picker, PhoneId, Phone.Public, Now, new NonceWindow());
        Assert.Equal((true, "2"), (d.Press, d.Key));
    }
}
