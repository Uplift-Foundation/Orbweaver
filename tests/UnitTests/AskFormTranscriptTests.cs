using System.Text.Json;
using Xunit;
using Call = Orbweaver.AskFormTranscript.Call;
using CallQuestion = Orbweaver.AskFormTranscript.CallQuestion;

namespace Orbweaver.Tests;

/// <summary>
/// CB-327 items 4 and 5: an AskUserQuestion form read with the transcript's help.
/// The screens and transcript rows are real (tests/TranscriptTests/Captures/cb327,
/// ask-*): one scratch Claude Code 2.1.325 session, captured while each form was
/// pending, rows filtered to the conversation's own.
/// </summary>
public class AskFormTranscriptTests
{
    private static string[] Rows(string file) => File.ReadAllLines(Cb327Captures.PathOf(file));

    private static Call VenueCall => AskFormTranscript.PendingCall(Rows("ask-venue-pending.jsonl"))!;
    private static Call MixedCall => AskFormTranscript.PendingCall(Rows("ask-mixed-pending.jsonl"))!;

    private static string Screen(string name) => Cb327Captures.Load(name);

    // ---- the transcript ------------------------------------------------------

    [Fact]
    public void The_pending_call_carries_every_question_header_label_and_description()
    {
        var call = VenueCall;
        Assert.Equal("toolu_01Fr1mefnojeNxHwSB4iRsCu", call.ToolUseId);
        Assert.Equal(["Venue", "Catering", "Schedule", "Transport"], call.Questions.Select(q => q.Header));
        var venue = call.Questions[0];
        Assert.Equal("Which venue style should we book for the team offsite?", venue.Question);
        Assert.False(venue.MultiSelect);
        Assert.Equal("Modern downtown hotel conference suite near restaurants and nightlife", venue.Options[1]);
    }

    [Fact]
    public void A_call_answered_by_its_tool_result_is_not_pending() =>
        Assert.Null(AskFormTranscript.PendingCall(Rows("ask-venue-answered.jsonl")));

    [Fact]
    public void The_latest_call_is_the_pending_one_and_multiSelect_is_read()
    {
        var call = MixedCall;
        Assert.NotEqual(VenueCall.ToolUseId, call.ToolUseId);
        Assert.Equal(["Colour", "Suites", "Ship"], call.Questions.Select(q => q.Header));
        Assert.Equal([false, true, false], call.Questions.Select(q => q.MultiSelect));
    }

    private static string ToolUse(string id, object input, string name = "AskUserQuestion") =>
        JsonSerializer.Serialize(new { type = "assistant", message = new { content = new object[] { new { type = "tool_use", id, name, input } } } });

    private static string ToolResult(string id) =>
        JsonSerializer.Serialize(new { type = "user", message = new { content = new object[] { new { type = "tool_result", tool_use_id = id } } } });

    private static object Input(params object[] questions) => new { questions };

    private static readonly object Good = new { question = "Q?", header = "H", options = new[] { new { label = "A", description = "a" } } };

    [Fact]
    public void Rows_that_are_not_an_ask_are_skipped_and_a_result_for_another_call_clears_nothing()
    {
        var call = AskFormTranscript.PendingCall([
            "{ torn",
            "[1,2]",
            "{\"type\":\"user\"}",
            "{\"message\":\"text\"}",
            "{\"message\":{}}",
            "{\"message\":{\"content\":\"plain\"}}",
            "{\"message\":{\"content\":[1]}}",
            ToolUse("t1", Input(Good)),
            ToolUse("t2", Input(Good), name: "Bash"),
            ToolResult("t2"),
        ])!;
        Assert.Equal("t1", call.ToolUseId);
    }

    [Fact]
    public void A_call_with_no_id_is_skipped() =>
        Assert.Null(AskFormTranscript.PendingCall([
            JsonSerializer.Serialize(new { message = new { content = new object[] { new { type = "tool_use", name = "AskUserQuestion", input = Input(Good) } } } }),
        ]));

    // An input the parser does not recognise is no call at all, and leaves the
    // one before it pending.
    [Theory]
    [InlineData("{\"questions\":\"no\"}")]
    [InlineData("{\"other\":1}")]
    [InlineData("\"text\"")]
    [InlineData("{\"questions\":[]}")]
    [InlineData("{\"questions\":[1]}")]
    [InlineData("{\"questions\":[{\"header\":\"H\",\"options\":[]}]}")]
    [InlineData("{\"questions\":[{\"question\":\"Q\"}]}")]
    [InlineData("{\"questions\":[{\"question\":\"Q\",\"options\":{}}]}")]
    [InlineData("{\"questions\":[{\"question\":\"Q\",\"options\":[1]}]}")]
    [InlineData("{\"questions\":[{\"question\":\"Q\",\"options\":[{\"description\":\"d\"}]}]}")]
    public void A_malformed_input_is_not_a_call(string input)
    {
        var bad = "{\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"t2\",\"name\":\"AskUserQuestion\",\"input\":" + input + "}]}}";
        Assert.Equal("t1", AskFormTranscript.PendingCall([ToolUse("t1", Input(Good)), bad])!.ToolUseId);
        Assert.Null(AskFormTranscript.PendingCall([bad]));
    }

    [Fact]
    public void A_missing_header_reads_as_empty()
    {
        var q = AskFormTranscript.PendingCall([
            "{\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"t\",\"name\":\"AskUserQuestion\",\"input\":{\"questions\":[{\"question\":\"Q\",\"header\":5,\"multiSelect\":\"yes\",\"options\":[{\"label\":\"A\"}]}]}}]}}",
        ])!.Questions[0];
        Assert.Equal(("", "A", false), (q.Header, q.Options[0], q.MultiSelect));
    }

    // ---- reading the file ----------------------------------------------------

    [Fact]
    public void The_file_is_read_only_for_a_screen_with_a_form_footer()
    {
        var path = Cb327Captures.PathOf("ask-venue-pending.jsonl");
        var tall = Screen("ask-venue-tall-w23h30");
        Assert.Equal(JsonSerializer.Serialize(VenueCall), JsonSerializer.Serialize(AskFormTranscript.CallFor(tall, path)));
        Assert.Null(AskFormTranscript.CallFor(null, path));
        Assert.Null(AskFormTranscript.CallFor(tall, ""));
        Assert.Null(AskFormTranscript.CallFor(tall, null));
        Assert.Null(AskFormTranscript.CallFor(PaneFixtures.MiniExitDialog, path));          // no footer: not read
        Assert.Null(AskFormTranscript.CallFor(tall, path + ".missing"));
    }

    // ---- item 4: the screen's form with the transcript's words ----------------

    [Fact]
    public void Without_a_call_the_screens_form_is_returned_as_read()
    {
        var screen = Screen("ask-mixed-colour-w32");
        Assert.Equal(JsonSerializer.Serialize(ChatTranscript.ParseAskForm(screen)),
            JsonSerializer.Serialize(AskFormTranscript.ReadForm(screen, null)));
    }

    [Fact]
    public void A_narrow_form_takes_its_tabs_question_and_labels_from_the_call()
    {
        var screen = Screen("ask-mixed-colour-w32");
        Assert.Contains(ChatTranscript.ParseAskForm(screen)!.Tabs, t => t.Name.EndsWith('…'));   // what the screen alone gives

        var form = AskFormTranscript.ReadForm(screen, MixedCall)!;
        Assert.Equal("Which colour should the orb be?", form.Prompt.Title);
        Assert.Equal(["Colour", "Suites", "Ship", "Submit"], form.Tabs.Select(t => t.Name));
        Assert.Equal(AskTabState.Submit, form.Tabs[^1].State);
        Assert.Equal(0, form.CurrentTab);
        Assert.Equal(["Red", "Green", "Blue", "Type something.", "Chat about this"], form.Prompt.Options.Select(o => o.Label));
        Assert.Equal(("4", "5", false), (form.FreeTextKey, form.ChatAboutKey, form.MultiSelect));
    }

    [Fact]
    public void A_multi_select_question_is_matched_by_its_labels_and_keeps_its_boxes()
    {
        var form = AskFormTranscript.ReadForm(Screen("ask-mixed-suites-w32"), MixedCall)!;
        Assert.Equal("Which suites should run before the release is cut?", form.Prompt.Title);
        Assert.Equal(1, form.CurrentTab);
        Assert.True(form.MultiSelect);
        Assert.True(form.HasSubmitRow);
        Assert.Equal("Integration tests over real subprocesses", form.Prompt.Options[1].Label);
    }

    private static AskForm FormOf(params string[] labels) => new(
        new ChatPrompt("screen title", labels.Select((l, i) => new ChatPromptOption((i + 1).ToString(), l))
            .Append(new ChatPromptOption((labels.Length + 1).ToString(), "Type something."))
            .Append(new ChatPromptOption((labels.Length + 2).ToString(), "Chat about this")).ToList()),
        [new AskTab("A…", AskTabState.Answered), new AskTab("B…", AskTabState.Open), new AskTab("Submit", AskTabState.Submit)],
        -1, (labels.Length + 1).ToString(), (labels.Length + 2).ToString());

    private static Call CallOf(params string[][] questions) => new("t",
        questions.Select((q, i) => new CallQuestion("Question " + i, "H" + i, false, q)).ToList());

    [Fact]
    public void The_screens_tab_states_are_kept_when_its_bar_had_one_tab_per_question()
    {
        var form = AskFormTranscript.Merge(FormOf("Yes", "No"), CallOf(["Red", "Blue", "Green"], ["Yes", "No"]));
        Assert.Equal([AskTabState.Answered, AskTabState.Open, AskTabState.Submit], form.Tabs.Select(t => t.State));
        Assert.Equal(["H0", "H1", "Submit"], form.Tabs.Select(t => t.Name));
        Assert.Equal((1, "Question 1"), (form.CurrentTab, form.Prompt.Title));
    }

    [Fact]
    public void A_single_question_call_has_no_Submit_tab_and_unmatched_bars_read_Open()
    {
        var tabs = AskFormTranscript.TabsFor(CallOf(["Yes", "No"]), [new AskTab("x", AskTabState.Answered), new AskTab("y", AskTabState.Open)]);
        Assert.Equal([new AskTab("H0", AskTabState.Open)], tabs);
    }

    // Each way the screen and the call can disagree leaves the screen's form
    // exactly as it was read.
    [Fact]
    public void A_form_the_call_does_not_match_is_left_as_read()
    {
        var form = FormOf("Yes", "No");
        Assert.Same(form, AskFormTranscript.Merge(form, CallOf(["Red", "Blue", "Green"])));          // count
        Assert.Same(form, AskFormTranscript.Merge(form, CallOf(["Maybe", "Never"])));                // labels
        Assert.Same(form, AskFormTranscript.Merge(form, CallOf(["Yes", "No"], ["Yes", "No"])));      // two questions fit
        var review = form with { IsReview = true };
        Assert.Same(review, AskFormTranscript.Merge(review, CallOf(["Yes", "No"])));                 // the review has no question

        var renumbered = form with { Prompt = form.Prompt with { Options = [
            new ChatPromptOption("2", "Yes"), new ChatPromptOption("1", "No"),
            new ChatPromptOption("3", "Type something."), new ChatPromptOption("4", "Chat about this")] } };
        Assert.Same(renumbered, AskFormTranscript.Merge(renumbered, CallOf(["No", "Yes"])));
    }

    [Theory]
    [InlineData("Book a lakeside retreat", "Book a lakeside retreat center with lodging", true)]       // wrapped
    [InlineData("Book a lakeside…", "Book a lakeside retreat", true)]                                 // cut
    [InlineData("Book  a\nlakeside retreat Everyone stays", "Book a lakeside retreat", true)]         // swallowed a description
    [InlineData("Book a mountain cabin", "Book a lakeside retreat", false)]
    [InlineData("…", "Book", false)]
    [InlineData("Book", "", false)]
    [InlineData("a team- building day", "a team-building day out", true)]                              // broke at a hyphen
    public void A_label_agrees_when_one_runs_into_the_other(string screen, string label, bool agrees) =>
        Assert.Equal(agrees, AskFormTranscript.Agrees(screen, label));

    [Fact]
    public void A_shown_key_outside_the_question_matches_nothing()
    {
        var call = CallOf(["Yes", "No"]);
        Assert.Null(AskFormTranscript.MatchQuestion(call, [("3", "Yes")], 2));
        Assert.Null(AskFormTranscript.MatchQuestion(call, [("x", "Yes")], 2));
        Assert.Equal(0, AskFormTranscript.MatchQuestion(call, [("2", "No")], 2));
    }

    // ---- item 5: a form taller than the pane -----------------------------------

    [Fact]
    public void A_form_taller_than_the_pane_is_minted_from_the_call()
    {
        var screen = Screen("ask-venue-tall-w23h30");
        Assert.Null(ChatTranscript.ParseAskForm(screen));                    // the screen alone has no form

        var form = AskFormTranscript.ReadForm(screen, VenueCall)!;
        Assert.Equal("Which venue style should we book for the team offsite?", form.Prompt.Title);
        Assert.Equal(["1", "2", "3", "4", "5", "6"], form.Prompt.Options.Select(o => o.Key));
        Assert.Equal("Rustic mountain lodge with large meeting room and hiking trails", form.Prompt.Options[0].Label);
        Assert.Equal(["Type something.", "Chat about this"], form.Prompt.Options.Skip(4).Select(o => o.Label));
        Assert.Equal(("5", "6", 0, (string?)null), (form.FreeTextKey, form.ChatAboutKey, form.CurrentTab, form.CursorKey));
        Assert.Equal(["Venue", "Catering", "Schedule", "Transport", "Submit"], form.Tabs.Select(t => t.Name));
        Assert.All(form.Tabs.Take(4), t => Assert.Equal(AskTabState.Open, t.State));
    }

    // The next question has the same number of options; its labels say which.
    [Fact]
    public void The_question_showing_is_told_apart_by_its_visible_labels()
    {
        var form = AskFormTranscript.ReadForm(Screen("ask-catering-tall-w23h30"), VenueCall)!;
        Assert.Equal((1, "How should we handle food and drinks during the offsite?"), (form.CurrentTab, form.Prompt.Title));
    }

    [Fact]
    public void A_tall_multi_select_question_is_not_minted() =>
        Assert.Null(AskFormTranscript.ReadForm(Screen("ask-mixed-suites-tall-w23h22"), MixedCall));

    [Fact]
    public void A_tall_form_from_another_call_is_not_minted() =>
        Assert.Null(AskFormTranscript.ReadForm(Screen("ask-venue-tall-w23h30"), MixedCall));

    private const string TallTail = """
             continuation of option two
          3. Third choice
          4. Type something.
        ───────────────────────
          5. Chat about this

        Enter to select ·
        Tab/Arrow keys to
        """;

    private static Call Three => CallOf(["First choice", "Second choice", "Third choice"]);

    [Fact]
    public void The_cursor_is_named_when_its_row_is_on_screen()
    {
        Assert.Equal("3", AskFormTranscript.Tall(TallTail.Replace("  3. Third", "❯ 3. Third"), Three)!.CursorKey);
        Assert.Null(AskFormTranscript.Tall(TallTail, Three)!.CursorKey);
    }

    // Every shape short of a real tall form is no form.
    [Theory]
    [InlineData("Enter to select ·", "Esc to cancel")]                               // no footer
    [InlineData("  3. Third choice", "  1. Third choice")]                           // the run starts at 1
    [InlineData("  4. Type something.", "  5. Type something.")]                     // a number skipped
    [InlineData("     continuation of option two", "Prose at the margin")]          // the top is not mid-form
    [InlineData("  5. Chat about this", "  5. Something else")]                      // no Chat about this
    [InlineData("  4. Type something.", "  4. Another choice")]                      // no Type something
    [InlineData("  3. Third choice", "  3. Fourth choice")]                          // labels disagree
    public void A_screen_that_is_not_a_tall_form_is_not_minted(string from, string to)
    {
        Assert.NotNull(AskFormTranscript.Tall(TallTail, Three));
        Assert.Null(AskFormTranscript.Tall(TallTail.Replace(from, to), Three));
    }

    [Fact]
    public void A_single_numbered_line_is_not_a_run() =>
        Assert.Null(AskFormTranscript.Tall("     text\n  5. Chat about this\n\nEnter to select ·\n", Three));

    // ---- the fingerprint and the wire ------------------------------------------

    [Fact]
    public void A_tall_form_has_a_fingerprint_only_with_its_call_and_one_per_question()
    {
        var venue = Screen("ask-venue-tall-w23h30");
        Assert.Null(PromptFingerprint.Of(venue));
        var fp = PromptFingerprint.Of(venue, VenueCall)!;
        Assert.Equal(fp.Hash, PromptFingerprint.Of(venue, VenueCall)!.Hash);
        Assert.Equal(["Which venue style should we book for the team offsite?"], fp.Preview);
        Assert.StartsWith("Which venue style should we book for the team offsite?\n1. Rustic mountain lodge", fp.Canonical);
        Assert.NotEqual(fp.Hash, PromptFingerprint.Of(Screen("ask-catering-tall-w23h30"), VenueCall)!.Hash);
        Assert.Null(PromptFingerprint.Of(Screen("ask-mixed-suites-tall-w23h22"), MixedCall));
    }

    // The call changes the words a form shows, never the hash of a screen that
    // already had one: a phone holding that hash still matches.
    [Fact]
    public void A_drawn_dialogs_fingerprint_is_the_same_with_or_without_a_call()
    {
        var screen = Screen("ask-mixed-colour-w32");
        Assert.Equal(PromptFingerprint.Of(screen)!.Hash, PromptFingerprint.Of(screen, MixedCall)!.Hash);
    }

    [Fact]
    public void The_wire_prompt_carries_the_calls_words()
    {
        var wire = MirrorProtocol.PromptFromScreen(Screen("ask-venue-tall-w23h30"), VenueCall)!;
        Assert.Equal("Which venue style should we book for the team offsite?", wire.Title);
        Assert.Equal(6, wire.Options.Count);
        Assert.Equal(("5", "6", 0), (wire.FreeTextKey, wire.ChatAboutKey, wire.Current));
        Assert.Null(MirrorProtocol.PromptFromScreen(Screen("ask-venue-tall-w23h30")));
    }

    [Fact]
    public void A_prompt_read_for_the_panel_uses_the_call()
    {
        var prompt = LocalCliChatSession.ReadPrompt(Screen("ask-venue-tall-w23h30"), VenueCall)!;
        Assert.Equal("Rustic mountain lodge with large meeting room and hiking trails", prompt.Options[0].Label);
        Assert.Equal("Question 1 of 4", prompt.Progress);
    }

    // ---- idris's three: wrapped labels, the count, multiSelect after the match --

    // A label's first row alone ("Lakeside cabin") would agree with both
    // questions; the rows under it, joined, agree with one.
    [Fact]
    public void A_wrapped_label_is_matched_on_all_its_rows_not_its_first()
    {
        var screen = TallTail.Replace("  3. Third choice", "  3. Lakeside cabin\n     compound with kitchen\n     A quiet place.");
        var call = CallOf(["First choice", "Second choice", "Lakeside cabin compound with kitchen"],
                          ["Other first", "Other second", "Lakeside cabin resort far away"]);
        Assert.Equal(0, AskFormTranscript.Tall(screen, call)!.CurrentTab);

        // The first row alone would have fit both, and two fits is no match.
        Assert.Null(AskFormTranscript.MatchQuestion(call, [("3", "Lakeside cabin")], 3));
    }

    // Matching comes first and multiSelect is read off the match, so a screen
    // that fits a multi-select question as well as another is no match at all.
    [Fact]
    public void An_ambiguous_match_that_includes_a_multi_select_question_mints_nothing()
    {
        var single = new CallQuestion("Single?", "S", false, ["First choice", "Second choice", "Third choice"]);
        var multi = single with { Question = "Multi?", MultiSelect = true };
        Assert.Null(AskFormTranscript.Tall(TallTail, new Call("t", [single, multi])));
        Assert.Null(AskFormTranscript.Tall(TallTail, new Call("t", [multi])));
        Assert.NotNull(AskFormTranscript.Tall(TallTail, new Call("t", [single])));

        var form = FormOf("Yes", "No");
        var yesNo = new CallQuestion("Q", "H", false, ["Yes", "No"]);
        Assert.Same(form, AskFormTranscript.Merge(form, new Call("t", [yesNo, yesNo with { MultiSelect = true }])));
    }

    // Every form and dialog fixture in the repository hashes the same with a
    // call as without one: a changed hash would re-mint a prompt already on a
    // phone. The call is built from the screen's own form, so the merge fires.
    [Fact]
    public void Every_existing_fixture_keeps_its_hash_with_a_call()
    {
        var screens = new[] { typeof(PaneFixtures), typeof(AskFormFixtures) }
            .SelectMany(t => t.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (f.Name, Screen: (string)f.GetValue(null)!))
            .Where(x => PromptFingerprint.Of(x.Screen) is not null)
            .ToList();
        Assert.True(screens.Count > 20, $"only {screens.Count} fixtures hash");

        var merged = 0;
        foreach (var (name, screen) in screens)
        {
            var form = ChatTranscript.ParseAskForm(screen);
            var own = form is null ? MixedCall : new Call("t", [new CallQuestion("Q", "H", false,
                form.Prompt.Options.Where(o => o.Key != form.FreeTextKey && o.Key != form.ChatAboutKey).Select(o => o.Label).ToList())]);
            if (form is not null && AskFormTranscript.Merge(form, own) != form) merged++;
            Assert.True(PromptFingerprint.Of(screen)!.Hash == PromptFingerprint.Of(screen, own)!.Hash, name);
        }
        Assert.True(merged > 5, $"the call rewrote only {merged} forms, so the pin proves little");
    }

    // ---- free text on a tall form (measured: Captures/cb327 ask-freetext-*) ----

    private static Call FreeTextCall => AskFormTranscript.PendingCall(Rows("ask-freetext-pending.jsonl"))!;

    [Fact]
    public void The_real_tall_free_text_row_reads_with_the_cursor_on_it_and_then_with_the_text()
    {
        var onRow = AskFormTranscript.ReadForm(Screen("ask-freetext-on-row-w23h30"), FreeTextCall)!;
        Assert.Equal(("5", "5"), (onRow.FreeTextKey, onRow.CursorKey));
        var typed = AskFormTranscript.ReadForm(Screen("ask-freetext-typed-w23h30"), FreeTextCall)!;
        Assert.Equal(("5", "a quiet barn"), (typed.FreeTextKey, typed.Prompt.Options[4].Label));
    }

    // A row above "Chat about this" that is neither "Type something" nor under
    // the cursor is not the free-text row.
    [Fact]
    public void A_free_text_row_without_its_label_needs_the_cursor_on_it() =>
        Assert.Null(AskFormTranscript.ReadForm(Screen("ask-freetext-typed-w23h30").Replace("❯ 5. a quiet barn", "  5. a quiet barn"), FreeTextCall));

    // FormAnswer on a tall form with the cursor scrolled away: the row's digit,
    // the read-back of the cursor there, the text, its read-back, then Enter.
    // The screen before the digit is the real on-row capture with the cursor
    // mark and the footer as they were before it (seen in the same session; the
    // capture itself was not kept). The two after it are the real captures.
    [Fact]
    public async Task Free_text_on_a_tall_form_reaches_the_row_by_its_digit()
    {
        var onRow = Screen("ask-freetext-on-row-w23h30");
        var before = onRow.Replace("❯ 5. Type something.", "  5. Type something.")
            .Replace("navigate · ctrl+g to\nedit in Vim · Esc to\n", "navigate · Esc to\n");
        Assert.NotEqual(onRow, before);
        Assert.Null(AskFormTranscript.ReadForm(before, FreeTextCall)!.CursorKey);

        var status = new SessionStatus { State = "waiting", Source = SessionSource.ClaudeCode,
            TranscriptPath = Cb327Captures.PathOf("ask-freetext-pending.jsonl") };
        var screen = before;
        var pressed = new List<string>();
        var typed = new List<string>();
        var fp = MirrorProtocol.PromptFromScreen(before, FreeTextCall)!.Fingerprint;

        var code = await FormAnswer.AnswerAsync(status, _ => Task.FromResult<string?>(screen),
            (_, key) => { pressed.Add(key); if (key == "5") screen = onRow; return Task.FromResult(true); },
            (_, text) => { typed.Add(text); screen = Screen("ask-freetext-typed-w23h30"); return Task.FromResult(true); },
            fp, "5", null, "a quiet barn");

        Assert.Null(code);
        Assert.Equal(["5", "Enter"], pressed);
        Assert.Equal(["a quiet barn"], typed);
    }

    // If the digit does not put the cursor on the row, nothing is typed.
    [Fact]
    public async Task A_digit_that_does_not_reach_the_row_types_nothing()
    {
        var before = Screen("ask-venue-tall-w23h30");
        var status = new SessionStatus { State = "waiting", Source = SessionSource.ClaudeCode,
            TranscriptPath = Cb327Captures.PathOf("ask-venue-pending.jsonl") };
        var pressed = new List<string>();
        var fp = MirrorProtocol.PromptFromScreen(before, VenueCall)!.Fingerprint;

        var code = await FormAnswer.AnswerAsync(status, _ => Task.FromResult<string?>(before),
            (_, key) => { pressed.Add(key); return Task.FromResult(true); },
            (_, _) => throw new InvalidOperationException("typed"), fp, "5", null, "a barn");

        Assert.Equal(MirrorProtocol.ErrTypeFailed, code);
        Assert.Equal(["5"], pressed);
    }

    // ---- the arms coverage found unexercised ----------------------------------

    // Keys outside 1..n keep their own labels: a key that is no number, and one
    // below 1, are never rewritten from the call.
    [Fact]
    public void Only_keys_one_to_n_take_the_calls_labels()
    {
        var form = new AskForm(new ChatPrompt("t", [new ChatPromptOption("1", "Yes"), new ChatPromptOption("2", "No"),
            new ChatPromptOption("0", "Type something."), new ChatPromptOption("x", "Chat about this")]), [], -1, "0", "x");
        var merged = AskFormTranscript.Merge(form, CallOf(["Yes please", "No thanks"]));
        Assert.Equal(["Yes please", "No thanks", "Type something.", "Chat about this"], merged.Prompt.Options.Select(o => o.Label));
    }

    // Blank rows above the first visible option are still mid-form.
    [Fact]
    public void A_blank_row_above_the_first_visible_option_is_not_the_margin() =>
        Assert.NotNull(AskFormTranscript.Tall("\n" + TallTail, Three));

    // The digit that should reach the free-text row fails: nothing is typed and
    // the failure is what comes back.
    [Fact]
    public async Task A_failed_digit_press_on_a_tall_form_types_nothing()
    {
        var before = Screen("ask-venue-tall-w23h30");
        var status = new SessionStatus { State = "waiting", Source = SessionSource.ClaudeCode,
            TranscriptPath = Cb327Captures.PathOf("ask-venue-pending.jsonl") };
        var fp = MirrorProtocol.PromptFromScreen(before, VenueCall)!.Fingerprint;

        var reads = 0;
        var code = await FormAnswer.AnswerAsync(status, _ => { reads++; return Task.FromResult<string?>(before); },
            (_, _) => Task.FromResult(false), (_, _) => throw new InvalidOperationException("typed"), fp, "5", null, "a barn");

        Assert.Equal(MirrorProtocol.ErrTypeFailed, code);
        Assert.Equal(1, reads);                                                // returned at the press, before any read-back
    }
}
