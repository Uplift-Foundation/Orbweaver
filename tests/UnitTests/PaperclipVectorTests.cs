using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using static Orbweaver.RelayCrypto;

namespace Orbweaver.Tests;

/// <summary>
/// CB-428: frames-v16-vectors.json from Orbweaver-Mobile contracts/ (see Fixtures/relay/SOURCE.md) is what the
/// phones' notification extension and prompt screens are built against. Every prompt frame in it is rebuilt by
/// the shipped PromptFrames, every signed answer is decided by the shipped AnswerGate, the resolved and refusal
/// frames are rebuilt, the why table and the prefs frames are read by the shipped parsers, and the sealed e is
/// opened with the vector's phone key and compared with what BrokeE writes. The firing table replays through
/// the link in RelayLinkTests.
/// </summary>
public class PaperclipVectorTests
{
    internal static readonly JsonNode V = JsonNode.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", "frames-v16-vectors.json")))!;

    private static string S(JsonNode? n) => n!.GetValue<string>();

    [Fact]
    public void Every_prompt_vector_is_rebuilt_byte_for_byte_by_the_shipped_frame_builder_and_screen_hash()
    {
        var P = V["prompt"]!;
        var minted = P["minted_at"]!.GetValue<long>();
        Assert.Equal(86400, PaperclipPrompts.Lifetime);
        foreach (var pos in P["positives"]!.AsArray())
        {
            var f = pos!["frame"]!;
            var item = pos["item"]!;
            var hash = PaperclipPrompts.ScreenHash(S(item["kind"]), S(item["id"]));
            Assert.Equal(S(f["screen_hash"]), hash);
            var built = PromptFrames.Prompt(S(f["request_id"]), S(f["session_id"]), "permission", "paperclip", S(f["tool"]), S(f["preview"]),
                PaperclipPrompts.Options, hash, minted + PaperclipPrompts.Lifetime);
            Assert.True(JsonNode.DeepEquals(f, JsonNode.Parse(built)), pos["name"] + ": " + built);
        }
    }

    [Fact]
    public void The_long_summary_vector_is_cut_by_the_shipped_rule_and_an_item_polled_again_keeps_its_hash()
    {
        var p = V["prompt"]!["positives"]!.AsArray().Single(x => S(x!["name"]) == "long-summary-cut")!["frame"]!["preview"]!.GetValue<string>();
        Assert.Equal(p, PromptFrames.Cut("Q4 plan\n" + new string('x', 4500), PromptFrames.MaxPreview));
        var s = V["prompt"]!["same_item_same_hash"]!;
        Assert.Equal(PaperclipPrompts.ScreenHash("approval", S(s["id"])), PaperclipPrompts.ScreenHash(S(s["a"]), S(s["id"])));
    }

    [Fact]
    public void Every_signed_answer_in_the_vectors_is_decided_by_the_shipped_gate_to_approve_or_decline_and_nothing_else()
    {
        var P = V["prompt"]!;
        var phonePub = Convert.FromHexString(S(V["inputs"]!["phone_identity"]!["public"]));
        var now = DateTimeOffset.FromUnixTimeSeconds(P["minted_at"]!.GetValue<long>());
        var answers = P["answers"]!.AsArray();
        Assert.Equal(6, answers.Count);
        foreach (var a in answers)
        {
            var frame = P["positives"]!.AsArray().Single(x => S(x!["name"]) == S(a!["prompt"]))!["frame"]!;
            var f = a["answer_frame"]!;
            var rid = S(f["request_id"]);
            Assert.Equal(S(a["body"]), AnswerSigning.Body(rid, S(f["session_id"]), S(f["action"]), "", S(f["nonce"]), f["expires_at"]!.GetValue<long>()));
            Assert.Equal(S(a["message"]), Convert.ToHexString(AnswerSigning.Message(S(a["body"]))).ToLowerInvariant());
            var pending = new PendingPrompt(rid, S(f["session_id"]), "permission", S(frame["screen_hash"]), frame["expires_at"]!.GetValue<long>(), false,
                [PaperclipPrompts.ApproveKey, PaperclipPrompts.DeclineKey]);
            var answer = new PhoneAnswer(rid, S(f["session_id"]), S(f["action"]), "", "", S(f["nonce"]), f["expires_at"]!.GetValue<long>(), S(f["screen_hash"]), S(f["sig"]));
            var d = AnswerGate.DecideExternal(pending, answer, true, PaperclipPrompts.Options, S(V["inputs"]!["phone_id"]), phonePub, now, new NonceWindow());
            Assert.True(d.Press, a["name"] + ": " + d.Code);
            Assert.Equal(S(a["action"]) == "approve" ? PaperclipPrompts.ApproveKey : PaperclipPrompts.DeclineKey, d.Key);
            // The paired negative: the same signed answer for a prompt that is no longer listed is refused at rule 6.
            Assert.Equal(AnswerGate.StalePrompt, AnswerGate.DecideExternal(pending, answer, false, PaperclipPrompts.Options, S(V["inputs"]!["phone_id"]), phonePub, now, new NonceWindow()).Code);
        }
    }

    [Fact]
    public void The_resolved_and_refusal_frames_are_rebuilt_by_the_shipped_builders()
    {
        var P = V["prompt"]!;
        var rid = S(P["positives"]![0]!["frame"]!["request_id"]);
        var sid = S(V["inputs"]!["agent_session_id"]);
        foreach (var r in P["resolved"]!.AsArray()) Assert.Equal(S(r!["plaintext"]), PromptFrames.Resolved(rid, sid, S(r["by"])));
        Assert.Equal(["phone:" + S(V["inputs"]!["phone_id"]), "desk", "gone"], P["resolved"]!.AsArray().Select(r => S(r!["by"])).ToArray());
        foreach (var r in P["refusals"]!.AsArray())
        {
            Assert.Equal(S(r!["plaintext"]), PromptFrames.Refusal(rid, sid, S(r["code"])));
            Assert.Contains(S(r["code"]), AnswerGate.Codes);
        }
    }

    [Fact]
    public void The_sealed_e_for_each_why_opens_with_the_vectors_phone_key_and_equals_what_BrokeE_writes()
    {
        var B = V["broke"]!;
        var priv = Convert.FromHexString(S(V["inputs"]!["phone_agreement"]!["private"]));
        var pub = Convert.FromHexString(S(V["inputs"]!["phone_agreement"]!["public"]));
        foreach (var why in new[] { "run_failed", "timed_out", "error" })
        {
            var e = B["e"]![why]!;
            var opened = Encoding.UTF8.GetString(PushSeal.Open(new KeyPair(priv, pub), S(e["e"]))!);
            Assert.Equal(S(e["plaintext"]), opened);
            Assert.Equal(opened, RelayLink.BrokeE(S(V["inputs"]!["agent_session_id"]), "Ledger bot", S(V["inputs"]!["machine"]), why));
            Assert.True(e["e_length"]!.GetValue<int>() <= PushSeal.MaxE);
        }
        Assert.Equal("broke", S(B["delta_frame"]!["push"]));
    }

    [Fact]
    public void The_why_table_and_every_prefs_frame_are_read_by_the_shipped_parsers()
    {
        foreach (var c in V["broke"]!["why_from_mark_reason"]!.AsArray())
            Assert.Equal(S(c!["why"]), RelayRoster.WhyOf(c["mark_reason"]?.GetValue<string>()));
        foreach (var f in V["broke"]!["prefs"]!["frames"]!.AsArray())
        {
            var prefs = AlertPrefs.Parse(JsonDocument.Parse(S(f!["plaintext"])).RootElement);
            Assert.Equal(f["broke_alerts"]!.GetValue<bool>(), prefs!.Value.BrokeAlerts);
            Assert.Equal(f["broke_alerts"]!.GetValue<bool>(), RelayAlertRules.PushBroke(prefs.Value));
        }
        Assert.Equal(V["broke"]!["prefs"]!["never_sent_prefs"]!["broke_alerts"]!.GetValue<bool>(), RelayAlertRules.PushBroke(AlertPrefs.Default));
    }
}
