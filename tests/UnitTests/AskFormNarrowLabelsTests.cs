using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-407, after the twelve-column fix: a form whose top is still on screen in a pane about
/// twelve columns wide wraps its fixed rows ("Type somethi ng.", "Chat" / "about this"). The
/// form reader missed the free-text row, declined, and the screen was served as a plain dialog
/// with its wrapped words (title "prefer?", options "Apple Apple"). The screens are real
/// captures from a mini session at 12 and 80 columns (Captures/cb407).
/// </summary>
public class AskFormNarrowLabelsTests
{
    private static string Narrow => Cb407Captures.Screen("mirror-form-12col-pane");
    private static string Wide => Cb407Captures.Screen("mirror-form-80col-pane");
    private static string NarrowRows => Cb407Captures.PathOf("mirror-form-12col-transcript-row.jsonl");
    private static string WideRows => Cb407Captures.PathOf("mirror-form-80col-transcript-row.jsonl");

    private static MirrorProtocol.WirePrompt Wire(string screen, string rows) =>
        MirrorProtocol.PromptFromScreen(screen, AskFormTranscript.CallFor(screen, rows))!;

    [Fact]
    public void The_form_reader_finds_both_fixed_rows_through_their_wrapping_and_gives_them_whole_labels()
    {
        var form = ChatTranscript.ParseAskForm(Narrow)!;

        Assert.Equal(("4", "5"), (form.FreeTextKey, form.ChatAboutKey));
        Assert.Equal("Type something.", form.Prompt.Options[3].Label);
        Assert.Equal("Chat about this", form.Prompt.Options[4].Label);
    }

    [Fact]
    public void The_served_prompt_has_the_transcripts_question_and_labels_at_12_columns_as_at_80()
    {
        var narrow = Wire(Narrow, NarrowRows);
        var wide = Wire(Wide, WideRows);

        Assert.Equal("Which fruit do you prefer?", narrow.Title);
        Assert.Equal(["Apple", "Banana", "Cherry", "Type something.", "Chat about this"], narrow.Options.Select(o => o.Label));
        Assert.Equal(wide.Title, narrow.Title);
        Assert.Equal(wide.Options.Select(o => (o.Key, o.Label)), narrow.Options.Select(o => (o.Key, o.Label)));
        // The mirror's fingerprint is the real 80-column capture's, and the garbled one it
        // served before (44e556c55dbd55e9) is gone.
        Assert.Equal(wide.Fingerprint, narrow.Fingerprint);
        Assert.NotEqual("44e556c55dbd55e9", narrow.Fingerprint);
    }

    // The phone's prompt frame takes its question and labels from the same read.
    [Fact]
    public async Task The_phones_prompt_frame_carries_the_same_question_and_labels()
    {
        var sent = new List<string>();
        var p = new PromptPipeline(new PromptPipeline.Seams(_ => Task.FromResult<string?>(Narrow), (_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult(true), () => DateTimeOffset.FromUnixTimeSeconds(1794111792), _ => Task.CompletedTask, _ => { }),
            new Sender(sent), "Test Mac");

        await p.OnScannedAsync([("s1", new SessionStatus { State = "waiting", Source = SessionSource.ClaudeCode, TranscriptPath = NarrowRows })]);

        var frame = JsonDocument.Parse(Assert.Single(sent)).RootElement;
        Assert.Equal("question", frame.GetProperty("kind").GetString());
        Assert.Equal("Which fruit do you prefer?", frame.GetProperty("preview").GetString());
        Assert.Equal(["Apple", "Banana", "Cherry"], frame.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("label").GetString()));
        Assert.True(frame.GetProperty("text").GetBoolean());
    }

    private sealed class Sender(List<string> sent) : IPromptSender
    {
        bool IPromptSender.Prompt(string r, string s, string p, string? push) { sent.Add(p); return true; }
        void IPromptSender.Resolved(string r, string s, string by, string? push) { }
        void IPromptSender.Refusal(string ph, string r, string s, string code) { }
        bool IPromptSender.PromptTo(string ph, string r, string s, string p) => true;
    }

    // Controls: the letters-only match recognises only what it should.
    [Theory]
    [InlineData("Type somethi ng.", "Type something", true)]
    [InlineData("Type", "Type something", false)]               // too little to say it is that row
    [InlineData("Ty", "Type something", false)]
    [InlineData("Choose something", "Type something", false)]
    [InlineData("Chat", "Chat about this", false)]              // a start of the whole is a start only the other way round
    public void Letters_start_compares_letters_only_and_wants_the_whole_of_what_it_looks_for(string text, string wanted, bool expected) =>
        Assert.Equal(expected, ChatTranscript.LettersStart(text, wanted));

    [Fact]
    public void A_start_of_the_chat_row_counts_only_as_the_last_row_under_the_free_text_row()
    {
        Assert.True(ChatTranscript.LettersStart("Chat about this", "Chat"));
        Assert.True(ChatTranscript.LettersStart("Chat about this", "Chat about this"));
        Assert.False(ChatTranscript.LettersStart("Cha", "Chat about this"));

        // Not last: a "Chat" that is not the row right under the free-text row stays an ordinary choice.
        var notLast = Narrow.Replace("  5. Chat\n  about this", "  5. Chat\n  6. Another\n  about this");
        Assert.NotEqual("Chat about this", ChatTranscript.ParseAskForm(notLast)?.Prompt.Options.FirstOrDefault(o => o.Key == "5")?.Label);
    }

    // The existing forms are untouched: a free-text label that is whole is not rewritten, and
    // the 80-column capture reads exactly as before.
    [Fact]
    public void A_form_whose_fixed_rows_did_not_wrap_reads_as_before()
    {
        var form = ChatTranscript.ParseAskForm(Wide)!;
        Assert.Equal(("4", "5"), (form.FreeTextKey, form.ChatAboutKey));
        Assert.Equal("Type something.", form.Prompt.Options[3].Label);
        Assert.Equal("Chat about this", form.Prompt.Options[4].Label);
    }
}
