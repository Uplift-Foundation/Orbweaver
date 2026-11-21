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

    // ---- QA's live pane from the mini (2.1.330), 30 seconds after the form appeared --------

    // The footer here reads "select · ↑/↓ to navigate · Esc to cancel" and the scroll hint
    // "↑ 1 more l…"; the top is partly scrolled with option 1 still visible, and the labels are
    // lowercase. The transcript row is not beside the capture, so the call is built from the
    // labels the pane itself draws (the question and "apple", "banana", "cherry"), which is what
    // the live row held; nothing here reads a row QA has not supplied.
    private static AskFormTranscript.Call LiveCall => new("toolu_live",
        [new AskFormTranscript.CallQuestion("Which fruit do you prefer?", "Fruit", false, ["apple", "banana", "cherry"])]);

    [Fact]
    public void The_live_twelve_column_pane_is_offered_whole_through_the_form_reader_and_the_mirror()
    {
        var live = Cb407Captures.Screen("mirror-form-12col-run4-t30-screen");

        Assert.True(AskFormTranscript.HasFormFooter(live));
        var form = AskFormTranscript.ReadForm(live, LiveCall)!;
        Assert.Equal("Which fruit do you prefer?", form.Prompt.Title);
        Assert.Equal(["apple", "banana", "cherry", "Type something.", "Chat about this"], form.Prompt.Options.Select(o => o.Label));
        Assert.Equal(("4", "5"), (form.FreeTextKey, form.ChatAboutKey));

        var wire = MirrorProtocol.PromptFromScreen(live, LiveCall)!;
        Assert.Equal(form.Prompt.Title, wire.Title);
        Assert.Equal(["apple", "banana", "cherry", "Type something.", "Chat about this"], wire.Options.Select(o => o.Label));
        Assert.DoesNotContain(wire.Options, o => o.Label.Contains("  ") || o.Label == "Chat" || o.Label.StartsWith("apple apple"));
        // On the old code this screen read as the glued dialog: title "prefer?", "apple apple".
        Assert.Equal("prefer?", ChatTranscript.ParseDialog(live)!.Title);
    }

    // ---- the fallthrough that served the garbled text -----------------------------

    // A shape neither form reader places, invented by wrapping and not by editing the
    // capture's content: the free-text row drawn the way a cursor row cut short at this width
    // draws it ("❯ 4. Type s…", the shape the 24-column capture already shows), with no header row
    // on screen and option 1 visible, so ParseAskForm and Tall both decline. The transcript
    // says this is an AskUserQuestion, so it must not be served as the plain dialog the screen
    // reads as (labels glued to their description rows, one row of the title): nothing is
    // offered, to the mirror or to the phone, and the person is sent to the terminal.
    private static string Unplaceable =>
        Narrow.Replace("  4. Type\n     somethi\n     ng.", "❯ 4. Type s…");

    [Fact]
    public async Task A_form_neither_reader_places_is_not_served_as_a_glued_together_dialog()
    {
        var screen = Unplaceable;
        Assert.NotEqual(Narrow, screen);
        var call = AskFormTranscript.CallFor(screen, NarrowRows);
        Assert.NotNull(call);
        Assert.Null(ChatTranscript.ParseAskForm(screen));
        Assert.Null(AskFormTranscript.ReadForm(screen, call));
        Assert.NotNull(ChatTranscript.ParseDialog(screen));     // the plausible text that used to be served

        Assert.Null(MirrorProtocol.PromptFromScreen(screen, call));
        Assert.Null(PromptFingerprint.Of(screen, call));

        var sent = new List<string>();
        var p = new PromptPipeline(new PromptPipeline.Seams(_ => Task.FromResult<string?>(screen), (_, _) => Task.FromResult(true),
            (_, _) => Task.FromResult(true), () => DateTimeOffset.FromUnixTimeSeconds(1794111792), _ => Task.CompletedTask, _ => { }),
            new Sender(sent), "Test Mac");
        await p.OnScannedAsync([("s1", new SessionStatus { State = "waiting", Source = SessionSource.ClaudeCode, TranscriptPath = NarrowRows })]);
        Assert.Empty(sent);
    }

    // Controls: with no transcript call the same screen reads through ParseDialog as it always
    // did; a genuine numbered permission dialog, with or without an unrelated call, is
    // untouched; an overlay is not refused by the rule.
    [Fact]
    public void Without_a_call_the_screen_still_reads_as_the_dialog_it_looks_like()
    {
        var served = MirrorProtocol.PromptFromScreen(Unplaceable, null);
        Assert.NotNull(served);
        Assert.Equal(5, served!.Options.Count);
        Assert.NotNull(PromptFingerprint.Of(Unplaceable, null));
    }

    [Fact]
    public void A_permission_dialog_reads_through_ParseDialog_as_before_with_or_without_a_call()
    {
        var dialog = PaneFixtures.MiniExitDialog;
        var unrelated = AskFormTranscript.PendingCall([System.IO.File.ReadAllLines(NarrowRows)[0]]);

        Assert.Equal(PromptFingerprint.Of(dialog, null)!.Hash, PromptFingerprint.Of(dialog, unrelated)!.Hash);
        Assert.Equal(MirrorProtocol.PromptFromScreen(dialog, null)!.Fingerprint, MirrorProtocol.PromptFromScreen(dialog, unrelated)!.Fingerprint);
        Assert.Equal(3, MirrorProtocol.PromptFromScreen(dialog, unrelated)!.Options.Count);
    }

    [Fact]
    public void An_overlay_is_served_as_itself_even_with_a_call_pending()
    {
        var unrelated = AskFormTranscript.PendingCall([System.IO.File.ReadAllLines(NarrowRows)[0]]);
        Assert.NotNull(MirrorProtocol.PromptFromScreen(PaneFixtures.MobileOverlay80x50, unrelated)!.Body);
    }

    // ---- why no call was found, as a fixed word ----------------------------------

    private static string Row => System.IO.File.ReadAllLines(NarrowRows)[0];

    private string Temp(IEnumerable<string> rows)
    {
        var path = Path.Combine(Path.GetTempPath(), "cb407-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(path, rows);
        _temps.Add(path);
        return path;
    }

    private readonly List<string> _temps = new();

    [Fact]
    public void Each_reason_no_call_is_found_has_its_own_fixed_word()
    {
        Assert.Equal(AskFormTranscript.CallFound, AskFormTranscript.CallForWhy(Narrow, NarrowRows).Why);
        Assert.Equal(AskFormTranscript.NoScreen, AskFormTranscript.CallForWhy(null, NarrowRows).Why);
        Assert.Equal(AskFormTranscript.NoScreen, AskFormTranscript.CallForWhy("", NarrowRows).Why);
        Assert.Equal(AskFormTranscript.NoTranscriptPath, AskFormTranscript.CallForWhy(Narrow, null).Why);
        Assert.Equal(AskFormTranscript.NoTranscriptPath, AskFormTranscript.CallForWhy(Narrow, "").Why);
        Assert.Equal(AskFormTranscript.NoFooter, AskFormTranscript.CallForWhy(PaneFixtures.MacPromptAfterEsc, NarrowRows).Why);
        Assert.Equal(AskFormTranscript.TranscriptMissing, AskFormTranscript.CallForWhy(Narrow, NarrowRows + ".missing").Why);
        Assert.Equal(AskFormTranscript.NoPendingCall, AskFormTranscript.CallForWhy(Narrow, Temp(["{\"type\":\"user\"}"])).Why);
        Assert.All(new[] { AskFormTranscript.NoScreen, AskFormTranscript.NoTranscriptPath, AskFormTranscript.NoFooter,
            AskFormTranscript.TranscriptMissing, AskFormTranscript.NoPendingCall, AskFormTranscript.CallFound },
            w => Assert.Matches("^[a-z-]+$", w));
        foreach (var t in _temps) File.Delete(t);
    }

    // The call is read from the transcript's last 256 KB. A long session keeps the pending call
    // inside that window because nothing but small rows is written while a form waits; the same
    // row with more than a window of rows after it is outside it, which reads as no pending call.
    [Fact]
    public void The_pending_call_is_found_after_a_long_transcript_and_lost_only_beyond_the_window()
    {
        var filler = "{\"type\":\"user\",\"x\":\"" + new string('a', 2000) + "\"}";
        var many = Enumerable.Repeat(filler, 400);                 // about 800 KB, three windows

        var after = Temp(many.Concat([Row]));
        Assert.Equal(AskFormTranscript.CallFound, AskFormTranscript.CallForWhy(Narrow, after).Why);

        var buried = Temp([Row, .. many]);                         // the row first, a window and more after it
        Assert.Equal(AskFormTranscript.NoPendingCall, AskFormTranscript.CallForWhy(Narrow, buried).Why);

        // A window that starts in the middle of a row drops that torn row and reads the rest.
        var torn = Temp(many.Take(150).Concat([Row]).Concat(many.Take(20)));
        Assert.Equal(AskFormTranscript.CallFound, AskFormTranscript.CallForWhy(Narrow, torn).Why);
        foreach (var t in _temps) File.Delete(t);
    }
}

/// <summary>
/// CB-407: the mirror host's status is the one the hook wrote, read the way the host reads it
/// (SessionManager.HeadlessSnapshot, the route a locked machine serves from), not one built by
/// hand with the transcript path already set. The path must survive that read, or no
/// transcript-dependent form fix could work through the mirror.
/// </summary>
[Collection("Settings")]
public class MirrorHostStatusCarriesTheTranscriptPathTests
{
    private static string Rows => Cb407Captures.PathOf("mirror-form-12col-transcript-row.jsonl");

    [Fact]
    public void A_status_file_read_the_way_the_host_reads_it_keeps_its_transcript_path_and_finds_the_call()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb407-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // The hook's own shape: a JSON status with the transcript_path key, state waiting.
            var json = "{\"state\":\"waiting\",\"title\":\"cb407-mini\",\"cwd\":\"/tmp/cb407-mini\",\"session_pid\":4242,"
                       + "\"transcript_path\":" + System.Text.Json.JsonSerializer.Serialize(Rows) + "}";
            File.WriteAllText(Path.Combine(dir, "d2bc3479-98d3-412f-a563-bbef27837afc.txt"), json);

            var sessions = SessionManager.HeadlessSnapshot(dir, () => new Dictionary<string, string>(), isRunning: _ => true,
                nowUtc: DateTime.UtcNow, honourOrbLifetime: false, onWindows: false, recordStatus: (_, _) => null);

            var status = Assert.Single(sessions).Status;
            Assert.Equal(Rows, status.TranscriptPath);

            var screen = Cb407Captures.Screen("mirror-form-12col-pane");
            var (call, why) = AskFormTranscript.CallForWhy(screen, status.TranscriptPath);
            Assert.Equal(AskFormTranscript.CallFound, why);
            Assert.Equal("Which fruit do you prefer?", MirrorProtocol.PromptFromScreen(screen, call)!.Title);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
