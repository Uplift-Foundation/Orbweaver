using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-407 cause 2: in a pane about twelve columns wide (a team lead squeezed by its
/// teammates) an AskUserQuestion form's footer wraps so its first row is "Enter to", and
/// "Type something." and "Chat about this" wrap too, so the transcript was never consulted
/// and no form was offered. The screens are real tmux captures of that, 12 and 24 columns
/// (Captures/cb407); the 24-column ones are the controls that always worked.
/// </summary>
public class AskFormWrappedFooterTests
{
    private static string Rollout => Cb407Captures.PathOf("askform-rollout.jsonl");
    private static string Merge => Cb407Captures.PathOf("askform-merge.jsonl");

    private static (AskFormTranscript.Call? Call, PromptFingerprint? Fp, AskForm? Form) Read(string screen, string rows)
    {
        var call = AskFormTranscript.CallFor(screen, rows);
        return (call, PromptFingerprint.Of(screen, call), call is null ? null : AskFormTranscript.ReadForm(screen, call));
    }

    [Theory]
    [InlineData("askform-12x23", "askform-rollout.jsonl", 6, "5", "6", null)]
    [InlineData("askform-24x23", "askform-rollout.jsonl", 6, "5", "6", null)]
    [InlineData("askform-single-12x23", "askform-merge.jsonl", 5, "4", "5", "4")]   // the cursor is on the "Type s…" row
    [InlineData("askform-single-24x23", "askform-merge.jsonl", 5, "4", "5", null)]
    public void A_form_is_offered_with_its_options_from_the_transcript_at_12_and_24_columns(
        string screen, string rows, int options, string freeText, string chatAbout, string? cursor)
    {
        var (call, fp, form) = Read(Cb407Captures.Screen(screen), Cb407Captures.PathOf(rows));

        Assert.NotNull(call);
        Assert.NotNull(fp);
        Assert.NotNull(form);
        Assert.Equal(options, form!.Prompt.Options.Count);
        Assert.Equal((freeText, chatAbout), (form.FreeTextKey, form.ChatAboutKey));
        Assert.Equal(cursor, form.CursorKey);
        // The choices are the transcript's, in full, however the screen wrapped them; the two
        // fixed rows carry their whole labels, not "Type" and "Chat".
        Assert.Equal(call!.Questions[0].Options, form.Prompt.Options.Take(options - 2).Select(o => o.Label));
        Assert.Equal(AskFormTranscript.TypeRowLabel, form.Prompt.Options[options - 2].Label.Replace("Type s…", AskFormTranscript.TypeRowLabel));
        Assert.Equal(ChatTranscript.ChatAboutLabel, form.Prompt.Options[^1].Label);
        Assert.Equal(form.Prompt.Title, call.Questions[0].Question);
    }

    // The same question hashes the same whatever the width, so a phone's answer to the form
    // read at 24 columns is not refused because the pane was later squeezed (and vice versa).
    [Fact]
    public void The_form_is_the_same_prompt_at_12_and_24_columns()
    {
        var narrow = Read(Cb407Captures.Screen("askform-12x23"), Rollout).Fp!;
        var wide = Read(Cb407Captures.Screen("askform-24x23"), Rollout).Fp!;
        Assert.Equal(wide.Canonical, narrow.Canonical);
        Assert.Equal(wide.Hash, narrow.Hash);
    }

    [Fact]
    public void The_form_footer_is_found_across_wrapped_rows_and_only_there()
    {
        Assert.Equal(1, AskFormTranscript.FooterIndex(["text", "Enter to", "select ·", "Tab/Arrow", "keys to"]));
        Assert.Equal(1, AskFormTranscript.FooterIndex(["text", "Enter to select ·", "Tab/Arrow keys to"]));
        Assert.Equal(1, AskFormTranscript.FooterIndex(["text", "Ent", "er to", "sel", "ect ·"]));          // wrapped between letters
        Assert.Equal(-1, AskFormTranscript.FooterIndex(["text", "Enter to", "go somewhere"]));            // "Enter to" is not the footer
        Assert.Equal(-1, AskFormTranscript.FooterIndex(["text", "Enter to"]));                             // cut off
        Assert.Equal(-1, AskFormTranscript.FooterIndex(["Enter to select"[..8], "", "Esc to cancel"]));
        Assert.Equal(-1, AskFormTranscript.FooterIndex([]));
        Assert.False(AskFormTranscript.HasFormFooter(["only", "prose"]));
        Assert.True(AskFormTranscript.HasFormFooter(["Enter to", "select"]));
    }

    // Controls: nothing that is not a form becomes one.
    [Fact]
    public void A_permission_prompt_is_not_a_form_at_any_width()
    {
        Assert.Null(AskFormTranscript.CallFor(PromptVectorsShim.FramesV3Screen, Rollout));
        Assert.Null(AskFormTranscript.CallFor(PaneFixtures.MiniExitDialog, Rollout));      // "Enter to confirm", not "Enter to select"
    }

    [Fact]
    public void A_screen_whose_text_wraps_Enter_to_is_not_a_form()
    {
        var prose = "     the build\n     failed. Press\n     Enter to\n     go back\n❯ ";
        Assert.Null(AskFormTranscript.CallFor(prose, Rollout));
        Assert.Null(PromptFingerprint.Of(prose, AskFormTranscript.PendingCall([System.IO.File.ReadAllLines(Rollout)[0]])));
    }

    [Theory]
    [InlineData("somethi\n", "Type\n")]            // "Type something." no longer reads across its rows
    [InlineData("about this", "about that")]       // "Chat about this" is something else
    public void A_twelve_column_screen_whose_fixed_labels_do_not_read_across_their_rows_is_not_a_form(string from, string to)
    {
        var screen = Cb407Captures.Screen("askform-12x23");
        Assert.NotNull(Read(screen, Rollout).Fp);
        Assert.Null(Read(screen.Replace(from, to), Rollout).Fp);
    }

    [Fact]
    public void A_twelve_column_form_from_another_call_is_not_offered()
    {
        // The rollout screen with the merge transcript: the questions do not agree.
        Assert.Null(Read(Cb407Captures.Screen("askform-12x23"), Merge).Fp);
    }

    // ---- answering at twelve columns -------------------------------------------

    private static SessionStatus Status(string rows) =>
        new() { State = "waiting", Source = SessionSource.ClaudeCode, TranscriptPath = Cb407Captures.PathOf(rows) };

    private static string FingerprintOf(string screen, string rows) =>
        MirrorProtocol.PromptFromScreen(screen, AskFormTranscript.CallFor(screen, Cb407Captures.PathOf(rows)))!.Fingerprint;

    // The form read at twelve columns is answerable: a digit it offers is pressed once, through
    // the same re-read the other widths use.
    [Fact]
    public async Task An_answer_at_twelve_columns_presses_the_digit_it_names_once()
    {
        var screen = Cb407Captures.Screen("askform-12x23");
        var pressed = new List<string>();

        var code = await FormAnswer.AnswerAsync(Status("askform-rollout.jsonl"), _ => Task.FromResult<string?>(screen),
            (_, key) => { pressed.Add(key); return Task.FromResult(true); }, null,
            FingerprintOf(screen, "askform-rollout.jsonl"), "3", null, null);

        Assert.Null(code);
        Assert.Equal(["3"], pressed);
    }

    // Controls: once the screen is another form, or none, the same answer presses nothing; a
    // key the form never offered is refused; and the answer made at 12 columns still lands when
    // the pane has been widened to 24, because the form is the same prompt.
    [Fact]
    public async Task A_stale_or_unoffered_answer_at_twelve_columns_presses_nothing()
    {
        var screen = Cb407Captures.Screen("askform-12x23");
        var fp = FingerprintOf(screen, "askform-rollout.jsonl");
        var pressed = new List<string>();
        Task<bool> Press(SessionStatus _, string key) { pressed.Add(key); return Task.FromResult(true); }

        // another form on screen (the merge question): a different prompt, so the answer is refused
        Assert.Equal(MirrorProtocol.ErrPromptChanged, await FormAnswer.AnswerAsync(Status("askform-rollout.jsonl"),
            _ => Task.FromResult<string?>(Cb407Captures.Screen("askform-single-12x23")), Press, null, fp, "3", null, null));
        // the prompt gone
        Assert.Equal(MirrorProtocol.ErrNoPrompt, await FormAnswer.AnswerAsync(Status("askform-rollout.jsonl"),
            _ => Task.FromResult<string?>(PaneFixtures.MacPromptAfterEsc), Press, null, fp, "3", null, null));
        // a key the form does not have
        Assert.Equal(MirrorProtocol.ErrBadKey, await FormAnswer.AnswerAsync(Status("askform-rollout.jsonl"),
            _ => Task.FromResult<string?>(screen), Press, null, fp, "9", null, null));
        Assert.Empty(pressed);

        var widened = Cb407Captures.Screen("askform-24x23");
        Assert.Null(await FormAnswer.AnswerAsync(Status("askform-rollout.jsonl"),
            _ => Task.FromResult<string?>(widened), Press, null, fp, "3", null, null));
        Assert.Equal(["3"], pressed);
    }

    // ---- with the CB-400 overlays on the tree -----------------------------------

    // An overlay (`/mobile`, `/mcp`'s detail page) carries an "Esc to ..." footer too, and the
    // wrapped-footer matcher must not take it for a form: no transcript is consulted, and the
    // prompt is the overlay's own with its Close or Go back button.
    [Theory]
    [InlineData(nameof(PaneFixtures.MobileOverlay80x50), "Close")]
    [InlineData(nameof(PaneFixtures.McpDetail), "Go back")]
    public void An_overlay_is_still_an_overlay_and_not_a_form(string name, string label)
    {
        var screen = (string)typeof(PaneFixtures).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;

        Assert.Null(AskFormTranscript.CallFor(screen, Rollout));
        var fp = PromptFingerprint.Of(screen, null)!;
        Assert.True(fp.Overlay);
        Assert.Equal(label, Assert.Single(fp.Prompt.Options).Label);
        Assert.Null(AskFormTranscript.ReadForm(screen, AskFormTranscript.PendingCall([System.IO.File.ReadAllLines(Rollout)[0]])));
    }

    // And the other way round, which is the one that matters: a question form squeezed to
    // twelve columns (its footer ends "Esc to cancel") is a form, never an overlay with a
    // Cancel button, which would press Esc and abandon the question. Before this fix it was
    // neither: no call was read and nothing was offered, so the phone saw nothing to answer.
    [Theory]
    [InlineData("askform-12x23", "askform-rollout.jsonl")]
    [InlineData("askform-single-12x23", "askform-merge.jsonl")]
    public void A_twelve_column_form_is_a_form_and_not_an_overlay(string screenName, string rows)
    {
        var screen = Cb407Captures.Screen(screenName);
        var call = AskFormTranscript.CallFor(screen, Cb407Captures.PathOf(rows));

        Assert.Null(OverlayScreen.Read(screen));
        Assert.Null(ChatTranscript.ParseDialog(screen));
        var fp = PromptFingerprint.Of(screen, call)!;
        Assert.False(fp.Overlay);
        Assert.Contains(fp.Prompt.Options, o => o.Label == AskFormTranscript.TypeRowLabel || o.Label.StartsWith("Type s", StringComparison.Ordinal));
        Assert.DoesNotContain(fp.Prompt.Options, o => o.Key == OverlayScreen.CloseKey);
        Assert.DoesNotContain(fp.Prompt.Options, o => o.Label == OverlayScreen.CancelLabel);
        Assert.Null(PromptFingerprint.Of(screen, null));           // with no call it is still nothing, as before
    }
}
