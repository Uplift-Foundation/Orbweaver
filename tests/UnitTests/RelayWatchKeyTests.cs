using System.Text;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-323 watch-key addendum against frames-v7-watch-key-vectors.json: every
/// key_case through RelayWatchKey.Decide (the decision, and the stored state
/// after), every answer_case through AnswerGate (pass means the answer goes on
/// to rule 2), the signed bodies and messages byte for byte, and the replies'
/// canonical form. The vectors were written by the architect's generator and
/// re-derived by check_v7_watch_key_vectors.py; this is the desktop's opinion.
/// </summary>
public class RelayWatchKeyTests
{
    private static readonly JsonElement V = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", "frames-v7-watch-key-vectors.json"))).RootElement;

    private static readonly JsonElement Inputs = V.GetProperty("inputs");
    private static readonly string PhoneId = Inputs.GetProperty("phone_id").GetString()!;
    private static readonly byte[] IdPub = RelayCrypto.FromHex(Inputs.GetProperty("id_pub").GetString())!;

    private static WatchKeyState State(JsonElement s) => new(
        s.GetProperty("watch_pub").ValueKind == JsonValueKind.Null ? null : RelayCrypto.FromHex(s.GetProperty("watch_pub").GetString()),
        s.GetProperty("watch_at").ValueKind == JsonValueKind.Null ? null : s.GetProperty("watch_at").GetInt64());

    public static TheoryData<string> KeyCases() => new(V.GetProperty("key_cases").EnumerateArray().Select(c => c.GetProperty("name").GetString()!));
    public static TheoryData<string> AnswerCases() => new(V.GetProperty("answer_cases").EnumerateArray().Select(c => c.GetProperty("name").GetString()!));

    private static JsonElement Case(string list, string name) =>
        V.GetProperty(list).EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    [Fact]
    public void The_vectors_are_the_ones_this_suite_was_written_against()
    {
        Assert.Equal(26, V.GetProperty("key_cases").GetArrayLength());
        Assert.Equal(14, V.GetProperty("answer_cases").GetArrayLength());
        Assert.Equal(RelayWatchKey.Domain, Inputs.GetProperty("key_domain").GetString());
    }

    [Theory]
    [MemberData(nameof(KeyCases))]
    public void Every_key_case_decides_and_stores_as_the_vectors_say(string name)
    {
        var c = Case("key_cases", name);
        var before = State(c.GetProperty("before"));
        var d = RelayWatchKey.Decide(c.GetProperty("frame"), PhoneId, IdPub, before);
        Assert.Equal(c.GetProperty("expect").GetString(), d.Code ?? "ok");
        Assert.True(d.After.Same(State(c.GetProperty("after"))), name);
        Assert.Equal(c.GetProperty("op").GetString(), d.Op);
    }

    // Each case whose frame is well formed names the body its signer signed;
    // where the desktop's own rebuild of it is meant to verify, it is the same
    // bytes, domain included.
    [Theory]
    [InlineData("add_first")]
    [InlineData("remove")]
    [InlineData("readd")]
    public void The_desktop_rebuilds_the_signed_body_and_message_byte_for_byte(string name)
    {
        var c = Case("key_cases", name);
        var f = c.GetProperty("frame");
        var body = RelayWatchKey.Body(c.GetProperty("op").GetString()!, f.GetProperty("kind").GetString()!, f.GetProperty("pub").GetString()!,
            PhoneId, f.GetProperty("at").GetInt64());
        Assert.Equal(c.GetProperty("signed_body").GetString(), body);
        Assert.Equal(c.GetProperty("message").GetString(), RelayCrypto.Hex(RelayWatchKey.Message(body)));
    }

    [Fact]
    public void Replies_are_canonical_and_echo_op_and_at()
    {
        var ok = RelayWatchKey.Decide(Case("key_cases", "add_first").GetProperty("frame"), PhoneId, IdPub, WatchKeyState.None);
        Assert.Equal("""{"t":"key.ok","op":"add","kind":"watch","at":1794111700}""", ok.Reply());
        var stale = RelayWatchKey.Decide(Case("key_cases", "remove_stale").GetProperty("frame"), PhoneId, IdPub,
            State(Case("key_cases", "remove_stale").GetProperty("before")));
        Assert.Equal($$"""{"t":"key.err","op":"remove","kind":"watch","at":{{stale.At}},"code":"stale"}""", stale.Reply());
    }

    [Fact]
    public void A_bad_frame_echoes_at_zero_when_at_cannot_be_read()
    {
        var d = RelayWatchKey.Decide(Case("key_cases", "add_at_string").GetProperty("frame"), PhoneId, IdPub, WatchKeyState.None);
        Assert.Equal("""{"t":"key.err","op":"add","kind":"watch","at":0,"code":"bad_frame"}""", d.Reply());
    }

    [Theory]
    [InlineData("""{"t":"key.add","kind":"watch","pub":7,"at":1,"sig":"x"}""", "bad_key")]          // pub not a string
    [InlineData("""{"t":"key.add","kind":3,"pub":"04","at":1,"sig":"x"}""", "bad_key")]            // kind not a string
    [InlineData("""{"t":"key.add","kind":"watch","pub":"04","at":1.5,"sig":"x"}""", "bad_frame")]  // at not an integer
    [InlineData("""{"t":"key.remove","kind":"watch","pub":"04","at":1}""", "bad_frame")]           // four keys
    public void Shapes_the_vectors_do_not_spell_out_still_take_the_first_failing_rule(string frame, string code) =>
        Assert.Equal(code, RelayWatchKey.Decide(JsonDocument.Parse(frame).RootElement, PhoneId, IdPub, WatchKeyState.None).Code);

    [Fact]
    public void A_remove_whose_signature_is_not_base64url_is_a_bad_signature()
    {
        var c = Case("key_cases", "remove");
        var f = JsonNodeOf(c.GetProperty("frame"));
        f["sig"] = "!!not base64!!";
        Assert.Equal("bad_signature", RelayWatchKey.Decide(JsonDocument.Parse(f.ToJsonString()).RootElement, PhoneId, IdPub, State(c.GetProperty("before"))).Code);
    }

    private static System.Text.Json.Nodes.JsonObject JsonNodeOf(JsonElement e) => System.Text.Json.Nodes.JsonNode.Parse(e.GetRawText())!.AsObject();

    [Fact]
    public void State_sameness_compares_key_bytes_and_at()
    {
        var a = new WatchKeyState([1, 2], 5);
        Assert.True(a.Same(new WatchKeyState([1, 2], 5)));
        Assert.False(a.Same(new WatchKeyState([1, 3], 5)));
        Assert.False(a.Same(new WatchKeyState([1, 2], 6)));
        Assert.False(a.Same(new WatchKeyState(null, 5)));
        Assert.False(new WatchKeyState(null, 5).Same(a));
        Assert.True(WatchKeyState.None.Same(new WatchKeyState(null, null)));
    }

    [Theory]
    [InlineData("key.add", true)]
    [InlineData("key.remove", true)]
    [InlineData("pair.revoke", false)]
    [InlineData(null, false)]
    public void Only_key_add_and_key_remove_are_key_frames(string? t, bool expected) =>
        Assert.Equal(expected, RelayWatchKey.IsKeyFrame(t));

    [Fact]
    public void A_frame_with_no_t_is_a_bad_frame_read_as_an_add()
    {
        var d = RelayWatchKey.Decide(JsonDocument.Parse("""{"kind":"watch","pub":"04","at":1,"sig":"x","x":1}""").RootElement, PhoneId, IdPub, WatchKeyState.None);
        Assert.Equal(("add", "bad_frame"), (d.Op, d.Code));
    }

    // A hand-edited file can hold a watch key with no watch_at. A remove signed
    // by that key is then accepted: there is nothing it could be stale against.
    [Fact]
    public void A_remove_of_a_stored_key_with_no_at_is_accepted()
    {
        var c = Case("key_cases", "remove");
        var before = State(c.GetProperty("before"));
        var d = RelayWatchKey.Decide(c.GetProperty("frame"), PhoneId, IdPub, before with { At = null });
        Assert.Null(d.Code);
        Assert.True(d.After.Same(State(c.GetProperty("after"))));
    }

    // ---- AnswerGate rules 1 and 1w ----------------------------------------------------

    [Theory]
    [MemberData(nameof(AnswerCases))]
    public void Every_answer_case_passes_rule_1_and_1w_or_is_refused_as_the_vectors_say(string name)
    {
        var c = Case("answer_cases", name);
        var answer = RelayLink.ParseAnswer(c.GetProperty("frame"))!;
        var watch = State(c.GetProperty("state")).Public;
        var d = AnswerGate.Decide(null, answer, null, PhoneId, IdPub, DateTimeOffset.FromUnixTimeSeconds(1794111800), new NonceWindow(), watch);
        // Nothing is pending, so an answer that passes rules 1 and 1w is
        // refused by rule 2 — which is how "goes on to rule 2" is observed.
        var expect = c.GetProperty("expect").GetString();
        if (expect == "pass") Assert.Equal((2, AnswerGate.NotPending), (d.Rule, d.Code));
        else Assert.Equal((1, expect), (d.Rule, d.Code));
    }

    [Fact]
    public void The_answer_signed_body_is_frames_v3s_unchanged()
    {
        var c = Case("answer_cases", "watch_approve");
        var a = RelayLink.ParseAnswer(c.GetProperty("frame"))!;
        var body = AnswerSigning.Body(a.RequestId, a.SessionId, a.Action, a.Scope, a.Nonce, a.ExpiresAt);
        Assert.Equal(c.GetProperty("signed_body").GetString(), body);
        Assert.Equal(c.GetProperty("message").GetString(), RelayCrypto.Hex(AnswerSigning.Message(body)));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("\"watch\"", "watch")]
    [InlineData("\"phone\"", "phone")]
    [InlineData("3", "")]
    [InlineData("null", "")]
    public void The_key_marker_is_read_as_absent_a_name_or_no_known_name(string? json, string? expected)
    {
        var frame = """{"t":"answer","request_id":"r","session_id":"s","action":"approve","scope":"","note":"","nonce":"n","expires_at":1,"screen_hash":"h","sig":"x" """
            + (json is null ? "}" : $",\"key\":{json}}}");
        Assert.Equal(expected, RelayLink.ParseAnswer(JsonDocument.Parse(frame).RootElement)!.Key);
    }

    [Fact]
    public void A_watch_answer_on_an_older_call_site_with_no_watch_key_is_a_bad_signature()
    {
        var a = RelayLink.ParseAnswer(Case("answer_cases", "watch_approve").GetProperty("frame"))!;
        var d = AnswerGate.Decide(null, a, null, PhoneId, IdPub, DateTimeOffset.FromUnixTimeSeconds(1794111800), new NonceWindow());
        Assert.Equal((1, AnswerGate.BadSignature), (d.Rule, d.Code));
        _ = Encoding.UTF8;
    }
}
