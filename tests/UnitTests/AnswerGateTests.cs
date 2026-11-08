using System.Security.Cryptography;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>
/// CB-315 §4: one case per gate rule, each breaking only that rule, so a check
/// disabled one at a time makes exactly its own case fail; a pass case; and
/// order cases that break two adjacent rules and expect the earlier one.
/// </summary>
public class AnswerGateTests
{
    private const string PhoneId = "ph_a1b2c3d4e5f60718";
    private const string RequestId = "01JC9R0A5N2Q7W8X3E4T6Y9ZBD";
    private const string SessionId = "0f6c4f2a";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1794111792);
    private static readonly long Expires = Now.ToUnixTimeSeconds() + 1800;

    private static readonly KeyPair Phone = KeyPairFromPrivate(SHA256.HashData("gate-test-phone"u8.ToArray()));
    private static readonly KeyPair Other = KeyPairFromPrivate(SHA256.HashData("gate-test-other"u8.ToArray()));

    private static readonly string Screen = PromptVectorsShim.FramesV3Screen;
    private static readonly PromptFingerprint Fp = PromptFingerprint.Of(Screen)!;

    private static PendingPrompt Pending() =>
        new(RequestId, SessionId, "permission", Fp.Hash, Expires);

    private static string NewNonce() => Base64Url(RandomNumberGenerator.GetBytes(16));

    private static PhoneAnswer Answer(string action = "approve", string scope = "", string note = "", string? nonce = null,
        long? expires = null, string? hash = null, string requestId = RequestId, string sessionId = SessionId, KeyPair? signer = null,
        Func<string, string>? tamperBody = null)
    {
        nonce ??= NewNonce();
        var exp = expires ?? Expires;
        var body = AnswerSigning.Body(requestId, sessionId, action, scope, nonce, exp);
        var signed = tamperBody is null ? body : tamperBody(body);
        var sig = Base64Url(Sign(signer ?? Phone, AnswerSigning.Message(signed)));
        return new PhoneAnswer(requestId, sessionId, action, scope, note, nonce, exp, hash ?? Fp.Hash, sig);
    }

    private static AnswerGate.Decision Decide(PhoneAnswer a, PendingPrompt? p = null, string? fresh = null, DateTimeOffset? now = null,
        NonceWindow? nonces = null) =>
        AnswerGate.Decide(p ?? Pending(), a, fresh ?? Screen, PhoneId, Phone.Public, now ?? Now, nonces ?? new NonceWindow());

    private static void Refused(AnswerGate.Decision d, int rule, string code)
    {
        Assert.False(d.Press);
        Assert.Null(d.Key);
        Assert.Equal(rule, d.Rule);
        Assert.Equal(code, d.Code);
    }

    // ---- the pass case -------------------------------------------------------

    [Theory]
    [InlineData("approve", "", "", "1")]
    [InlineData("always", "dotnet test", "", "2")]
    [InlineData("tell", null, "Use the integration tests", "3")]
    public void A_valid_answer_presses_the_one_key_read_off_the_fresh_screen(string action, string? scope, string note, string key)
    {
        var d = Decide(Answer(action, scope ?? AnswerSigning.TellScope(note), note));
        Assert.True(d.Press);
        Assert.Equal(8, d.Rule);
        Assert.Null(d.Code);
        Assert.Equal(key, d.Key);
    }

    // ---- rule 1: signature ---------------------------------------------------

    [Fact]
    public void Rule1_a_signature_by_another_key_is_refused() =>
        Refused(Decide(Answer(signer: Other)), 1, AnswerGate.BadSignature);

    [Fact]
    public void Rule1_a_signature_over_different_bytes_is_refused() =>
        Refused(Decide(Answer(tamperBody: b => b.Replace("approve", "deny"))), 1, AnswerGate.BadSignature);

    [Fact]
    public void Rule1_a_tell_whose_note_does_not_hash_to_its_signed_scope_is_refused() =>
        Refused(Decide(Answer("tell", AnswerSigning.TellScope("one thing"), "another thing")), 1, AnswerGate.BadSignature);

    [Fact]
    public void Rule1_a_note_on_anything_but_tell_is_refused_because_it_is_unsigned() =>
        Refused(Decide(Answer("approve", note: "smuggled")), 1, AnswerGate.BadSignature);

    [Theory]
    [InlineData("AAAAAAAAAAA")]          // 8 bytes
    [InlineData("not base64url!")]       // not a nonce at all
    public void Rule1_a_nonce_that_is_not_16_bytes_is_refused(string nonce) =>
        Refused(Decide(Answer(nonce: nonce)), 1, AnswerGate.BadSignature);

    // ---- rule 2: pending -----------------------------------------------------

    [Fact]
    public void Rule2_no_pending_prompt_is_refused() =>
        Refused(AnswerGate.Decide(null, Answer(), Screen, PhoneId, Phone.Public, Now, new NonceWindow()), 2, AnswerGate.NotPending);

    [Fact]
    public void Rule2_another_request_id_is_refused() =>
        Refused(Decide(Answer(requestId: "01JC9R0A5N2Q7W8X3E4T6Y9ZZZ")), 2, AnswerGate.NotPending);

    [Fact]
    public void Rule2_another_session_is_refused() =>
        Refused(Decide(Answer(sessionId: "deadbeef")), 2, AnswerGate.NotPending);

    [Fact]
    public void Rule2_a_second_answer_after_the_first_was_marked_is_refused()
    {
        var p = Pending();
        var nonces = new NonceWindow();
        Assert.True(Decide(Answer(), p, nonces: nonces).Press);
        p.Answered = true;   // the caller's rule-8 mark
        Refused(Decide(Answer(), p, nonces: nonces), 2, AnswerGate.NotPending);
    }

    // ---- rule 3: expiry ------------------------------------------------------

    [Fact]
    public void Rule3_at_expiry_is_refused() =>
        Refused(Decide(Answer(), now: DateTimeOffset.FromUnixTimeSeconds(Expires)), 3, AnswerGate.Expired);

    [Fact]
    public void Rule3_one_second_before_expiry_passes() =>
        Assert.True(Decide(Answer(), now: DateTimeOffset.FromUnixTimeSeconds(Expires - 1)).Press);

    [Fact]
    public void Rule3_an_expires_at_other_than_the_pending_one_is_refused() =>
        Refused(Decide(Answer(expires: Expires + 60)), 3, AnswerGate.Expired);

    // ---- rule 4: nonce -------------------------------------------------------

    [Fact]
    public void Rule4_a_replayed_nonce_is_refused()
    {
        var nonces = new NonceWindow();
        var a = Answer();
        Assert.True(Decide(a, nonces: nonces).Press);
        Refused(Decide(a, nonces: nonces), 4, AnswerGate.Replayed);
    }

    [Fact]
    public void Rule4_a_nonce_is_remembered_even_when_a_later_rule_refused_the_answer()
    {
        var nonces = new NonceWindow();
        var stale = Answer(hash: "AAAA");
        Refused(Decide(stale, nonces: nonces), 5, AnswerGate.StalePrompt);
        // The same answer with the hash corrected: screen_hash is not in the
        // signed body (the sealed box already protects it), so this reaches
        // rule 4 — and is a replay.
        Refused(Decide(stale with { ScreenHash = Fp.Hash }, nonces: nonces), 4, AnswerGate.Replayed);
    }

    [Fact]
    public void Rule4_the_window_is_one_hour_and_per_phone()
    {
        var w = new NonceWindow();
        w.Add(PhoneId, "n", Now);
        Assert.True(w.Seen(PhoneId, "n", Now.AddMinutes(59)));
        Assert.False(w.Seen("ph_other", "n", Now));
        w.Add(PhoneId, "later", Now.AddMinutes(30));
        Assert.False(w.Seen(PhoneId, "n", Now.AddHours(1)));
        Assert.Equal(1, w.Count);   // only the expired one was pruned
        Assert.True(w.Seen(PhoneId, "later", Now.AddHours(1)));
    }

    // ---- rule 5: echoed hash -------------------------------------------------

    [Fact]
    public void Rule5_an_echoed_hash_other_than_the_pending_one_is_refused() =>
        Refused(Decide(Answer(hash: PromptFingerprint.HashOf("something else"))), 5, AnswerGate.StalePrompt);

    // ---- rule 6: fresh capture -----------------------------------------------

    [Fact]
    public void Rule6_a_changed_screen_is_refused() =>
        Refused(Decide(Answer(), fresh: Screen.Replace("tests/UnitTests", "tests/IntegrationTests")), 6, AnswerGate.StalePrompt);

    [Fact]
    public void Rule6_a_screen_with_no_dialog_is_refused() =>
        Refused(Decide(Answer(), fresh: "$ \n"), 6, AnswerGate.StalePrompt);

    [Fact]
    public void Rule6_a_failed_capture_is_refused() =>
        Refused(AnswerGate.Decide(Pending(), Answer(), null, PhoneId, Phone.Public, Now, new NonceWindow()), 6, AnswerGate.StalePrompt);

    [Fact]
    public void Rule6_the_cursor_on_another_option_is_the_same_screen() =>
        Assert.True(Decide(Answer(), fresh: Screen.Replace(" ❯ 1. Yes", "   1. Yes").Replace("   3. No,", " ❯ 3. No,")).Press);

    // ---- rule 7: option ------------------------------------------------------

    private const string ModeScreen =
        "────────────────────────────────────────\n" +
        " Bash command\n" +
        "   touch x\n" +
        " Do you want to proceed?\n" +
        " ❯ 1. Yes\n" +
        "   2. Yes, and switch to auto mode · auto mode handles these prompts for you\n" +
        "   3. No\n";

    private static PendingPrompt PendingFor(string screen, string kind = "permission")
    {
        var fp = PromptFingerprint.Of(screen)!;
        return new PendingPrompt(RequestId, SessionId, kind, fp.Hash, Expires);
    }

    private static PhoneAnswer AnswerFor(string screen, string action, string scope = "") =>
        Answer(action, scope, hash: PromptFingerprint.Of(screen)!.Hash);

    [Fact]
    public void Rule7_a_mode_option_is_never_pressed() =>
        Refused(Decide(AnswerFor(ModeScreen, "option:2"), PendingFor(ModeScreen), ModeScreen), 7, AnswerGate.ForbiddenOption);

    internal static string Screenshot(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ScreenReadFixtures", name)).Replace("\r", "");

    // Real Grok captures (surface C's fixtures): the parser flags the
    // always-approve option as Mode. It is never pressed, by its key or by
    // approve, which presses the plain "Yes, proceed" instead.
    [Theory]
    [InlineData("tmux-grok-prompt.txt")]
    [InlineData("iterm2-grok-prompt.txt")]
    [InlineData("conhost-grok-prompt.txt")]
    [InlineData("wt-grok-prompt.txt")]
    public void Rule7_a_mode_option_flagged_by_the_parser_is_never_pressed(string fixture)
    {
        var screen = Screenshot(fixture);
        var modeKey = PromptFingerprint.Of(screen)!.Prompt.Options.Single(o => o.Mode).Key;
        Refused(AnswerGate.Decide(PendingFor(screen), AnswerFor(screen, "option:" + modeKey), screen, PhoneId, Phone.Public, Now, new NonceWindow()),
            7, AnswerGate.ForbiddenOption);
        var approve = AnswerGate.Decide(PendingFor(screen), AnswerFor(screen, "approve"), screen, PhoneId, Phone.Public, Now, new NonceWindow());
        Assert.True(approve.Press);
        Assert.NotEqual(modeKey, approve.Key);
    }

    [Theory]
    [InlineData("conhost-codex-updatedialog.txt")]
    [InlineData("wt-codex-hooksdialog.txt")]
    [InlineData("wt-codex-trustdialog.txt")]
    public void Rule7_a_first_run_screen_is_never_pressed(string fixture)
    {
        var screen = Screenshot(fixture);
        Assert.NotNull(PromptFingerprint.Of(screen));   // it does parse as a numbered dialog: that is the danger
        var hash = PromptFingerprint.Of(screen)!.Hash;
        // minted desk-only
        Refused(AnswerGate.Decide(new PendingPrompt(RequestId, SessionId, "permission", hash, Expires, deskOnly: true),
            AnswerFor(screen, "approve"), screen, PhoneId, Phone.Public, Now, new NonceWindow()), 7, AnswerGate.ForbiddenOption);
        // or minted as a prompt somehow: the fresh screen alone refuses it
        Refused(AnswerGate.Decide(PendingFor(screen), AnswerFor(screen, "option:1"), screen, PhoneId, Phone.Public, Now, new NonceWindow()),
            7, AnswerGate.ForbiddenOption);
    }

    [Fact]
    public void Rule7_an_always_whose_scope_does_not_match_is_refused() =>
        Refused(Decide(Answer("always", "dotnet")), 7, AnswerGate.ForbiddenOption);

    [Fact]
    public void Rule7_an_action_with_no_option_on_screen_is_refused() =>
        Refused(Decide(Answer("deny")), 7, AnswerGate.ForbiddenOption);

    [Theory]
    [InlineData("never")]
    [InlineData("mode")]
    [InlineData("option:9")]
    [InlineData("option:1")]
    [InlineData("")]
    public void Rule7_actions_a_phone_cannot_send_are_refused(string action) =>
        Refused(Decide(Answer(action)), 7, AnswerGate.ForbiddenOption);

    [Fact]
    public void Rule7_an_option_of_a_question_presses_its_key()
    {
        const string q = "────────────────────────────────────────\n Which colour?\n ❯ 1. Red\n   2. Blue\n";
        var d = AnswerGate.Decide(PendingFor(q, "question"), AnswerFor(q, "option:2"), q, PhoneId, Phone.Public, Now, new NonceWindow());
        Assert.Equal("2", d.Key);
    }

    [Fact]
    public void Rule7_an_action_matching_two_options_is_refused_not_guessed()
    {
        const string two = "────────────────────────────────────────\n Do you want to proceed?\n ❯ 1. Yes\n   2. Yes, proceed\n   3. No\n";
        Refused(AnswerGate.Decide(PendingFor(two), AnswerFor(two, "approve"), two, PhoneId, Phone.Public, Now, new NonceWindow()),
            7, AnswerGate.ForbiddenOption);
    }

    // ---- rule 8: press once, recapture ---------------------------------------

    [Fact]
    public void Rule8_the_same_dialog_after_the_press_means_no_effect() =>
        Assert.False(AnswerGate.PressTookEffect(Pending(), Screen));

    [Theory]
    [InlineData("$ \n")]
    [InlineData(null)]
    public void Rule8_a_cleared_dialog_or_a_failed_recapture_counts_as_taken(string? recapture) =>
        Assert.True(AnswerGate.PressTookEffect(Pending(), recapture));

    // ---- order ---------------------------------------------------------------

    [Fact]
    public void Order_bad_signature_beats_not_pending() =>
        Refused(AnswerGate.Decide(null, Answer(signer: Other), Screen, PhoneId, Phone.Public, Now, new NonceWindow()), 1, AnswerGate.BadSignature);

    [Fact]
    public void Order_not_pending_beats_expired() =>
        Refused(Decide(Answer(sessionId: "deadbeef"), now: DateTimeOffset.FromUnixTimeSeconds(Expires + 10)), 2, AnswerGate.NotPending);

    [Fact]
    public void Order_expired_beats_replayed()
    {
        var nonces = new NonceWindow();
        var a = Answer();
        nonces.Add(PhoneId, a.Nonce, Now);
        Refused(Decide(a, now: DateTimeOffset.FromUnixTimeSeconds(Expires), nonces: nonces), 3, AnswerGate.Expired);
    }

    [Fact]
    public void Order_replayed_beats_a_stale_echo()
    {
        var nonces = new NonceWindow();
        var a = Answer(hash: "AAAA");
        nonces.Add(PhoneId, a.Nonce, Now);
        Refused(Decide(a, nonces: nonces), 4, AnswerGate.Replayed);
    }

    [Fact]
    public void Order_a_stale_echo_is_rule_5_even_when_the_screen_changed_too() =>
        Refused(Decide(Answer(hash: "AAAA"), fresh: "$ \n"), 5, AnswerGate.StalePrompt);

    [Fact]
    public void Order_a_changed_screen_beats_a_forbidden_option() =>
        Refused(Decide(Answer("never"), fresh: "$ \n"), 6, AnswerGate.StalePrompt);

    [Fact]
    public void Refusal_codes_are_the_frames_v3_six() =>
        Assert.Equal(["stale_prompt", "bad_signature", "forbidden_option", "not_pending", "expired", "replayed"], AnswerGate.Codes);
}
