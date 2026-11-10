using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-323 frames-v7-questions against its vectors: every case's answer through
/// AnswerGate rule 1 (the signature, other's scope, the note rule, the watch
/// key's 1w), and, where rule 1 passes, through Rule7 with the case's pending
/// prompt, fresh options and "Type something." row — the decision (press,
/// bad_signature or forbidden_option) and, for a press, exactly what it presses.
/// The vectors were written by the contract's generator and re-derived by
/// check_v7_questions_vectors.py; this is the desktop's opinion.
/// </summary>
public class QuestionAnswerVectorTests
{
    private static readonly JsonElement V = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay", "frames-v7-questions-vectors.json"))).RootElement;

    private static readonly JsonElement In = V.GetProperty("inputs");
    private static readonly byte[] IdPub = RelayCrypto.FromHex(In.GetProperty("id_pub").GetString())!;
    private static readonly byte[] WatchPub = RelayCrypto.FromHex(In.GetProperty("watch_pub").GetString())!;

    public static TheoryData<string> Cases() => new(V.GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("name").GetString()!));

    private static JsonElement Case(string name) => V.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    private static bool Flag(JsonElement prompt, string name) => prompt.TryGetProperty(name, out var f) && f.GetBoolean();

    [Fact]
    public void The_vectors_are_the_ones_this_suite_was_written_against()
    {
        Assert.Equal(37, V.GetProperty("cases").GetArrayLength());
        Assert.Equal(AnswerGate.MaxNoteBytes, In.GetProperty("note_max_bytes").GetInt32());
        Assert.Equal(AnswerSigning.Domain, In.GetProperty("domain").GetString());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_case_is_decided_as_the_vectors_say(string name)
    {
        var c = Case(name);
        var answer = RelayLink.ParseAnswer(c.GetProperty("frame"))!;
        var expect = c.GetProperty("expect").GetString();
        Assert.Equal(c.GetProperty("signed_body").GetString(),
            AnswerSigning.Body(answer.RequestId, answer.SessionId, answer.Action, answer.Scope, answer.Nonce, answer.ExpiresAt));

        // Rule 1 (and 1w), with nothing pending: an answer that passes them is
        // refused by rule 2, which is how passing is observed.
        var first = AnswerGate.Decide(null, answer, null, "ph_a1b2c3d4e5f60718", IdPub,
            DateTimeOffset.FromUnixTimeSeconds(1794111800), new NonceWindow(), WatchPub);
        if (first.Rule == 1)
        {
            Assert.Equal(expect, first.Code);
            return;
        }
        Assert.Equal((2, AnswerGate.NotPending), (first.Rule, first.Code));

        var prompt = c.GetProperty("prompt");
        var offered = prompt.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("key").GetString()!).ToList();
        var pending = new PendingPrompt(answer.RequestId, answer.SessionId, prompt.GetProperty("kind").GetString()!,
            answer.ScreenHash, answer.ExpiresAt, keys: offered, multi: Flag(prompt, "multi"), text: Flag(prompt, "text"));
        var fresh = c.GetProperty("fresh_options").EnumerateArray()
            .Select(o => new PromptOption(o.GetProperty("key").GetString()!, o.GetProperty("label").GetString()!, o.GetProperty("kind").GetString()!, null))
            .ToList();
        var row = c.GetProperty("fresh_type_row").GetBoolean() ? "9" : null;

        var d = AnswerGate.Rule7(pending, answer, fresh, row);
        if (expect != "press")
        {
            Assert.Equal((7, expect), (d.Rule, d.Code));
            return;
        }
        Assert.Equal(8, d.Rule);
        Assert.True(d.Press);
        if (answer.Action.StartsWith(AnswerGate.OptionsPrefix, StringComparison.Ordinal))
            Assert.Equal(answer.Action[AnswerGate.OptionsPrefix.Length..].Split(','), d.Keys!);
        else if (answer.Action == AnswerGate.Other)
            Assert.Equal((row, answer.Note), (d.Key, d.Text));
        else
            Assert.Equal(answer.Action["option:".Length..], d.Key);
    }

    [Theory]
    [InlineData("options:1", true)]
    [InlineData("options:2,10,11", true)]
    [InlineData("options:9,10", true)]
    [InlineData("options:10,9", false)]
    [InlineData("options:3,3", false)]
    [InlineData("options:", false)]
    [InlineData("options:0", false)]
    [InlineData("options:1,2,", false)]
    public void The_options_grammar_is_ascending_as_integers(string action, bool ok) =>
        Assert.Equal(ok, AnswerGate.OptionKeys(action) is not null);

    [Theory]
    [InlineData("Purple, please", true)]
    [InlineData("", false)]
    [InlineData("a\u0085b", false)]
    [InlineData("a\u007fb", false)]
    [InlineData("a\u001bb", false)]
    public void Other_takes_one_line_with_no_control_characters(string note, bool ok) =>
        Assert.Equal(ok, AnswerGate.NoteAllowed(note));

    // The vectors' "other without text" case also has no row on screen, so it
    // cannot tell the flag's check from the row's. This one can: the row is
    // there, and the prompt went out without text:true.
    [Fact]
    public void Other_needs_the_prompt_to_have_offered_text_even_when_the_row_is_on_screen()
    {
        var c = Case("other_ascii");
        var answer = RelayLink.ParseAnswer(c.GetProperty("frame"))!;
        var fresh = new List<PromptOption> { new("1", "Yes", OptionKinds.Option, null) };
        var offered = new PendingPrompt(answer.RequestId, answer.SessionId, "question", answer.ScreenHash, answer.ExpiresAt, keys: ["1"], text: true);
        var notOffered = new PendingPrompt(answer.RequestId, answer.SessionId, "question", answer.ScreenHash, answer.ExpiresAt, keys: ["1"]);
        Assert.True(AnswerGate.Rule7(offered, answer, fresh, "3").Press);
        Assert.Equal((7, AnswerGate.ForbiddenOption), (AnswerGate.Rule7(notOffered, answer, fresh, "3").Rule, AnswerGate.Rule7(notOffered, answer, fresh, "3").Code));
    }

    // A pending prompt that recorded no offered keys holds options to what the
    // screen shows alone.
    [Fact]
    public void Options_with_no_offered_keys_recorded_are_held_to_the_screen()
    {
        var answer = RelayLink.ParseAnswer(Case("options_two").GetProperty("frame"))!;
        var pending = new PendingPrompt(answer.RequestId, answer.SessionId, "question", answer.ScreenHash, answer.ExpiresAt, multi: true);
        var fresh = new List<PromptOption> { new("1", "Unit", OptionKinds.Option, null), new("3", "UI", OptionKinds.Option, null) };
        Assert.Equal(["1", "3"], AnswerGate.Rule7(pending, answer, fresh, null).Keys!);
    }
}
