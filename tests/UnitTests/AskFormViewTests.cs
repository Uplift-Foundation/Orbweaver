using System.Linq;
using Xunit;

namespace Orbweaver.Tests;

// AskFormView: what a panel shows for a question form (CB-307 item 3), over the
// parser's reading of the real captures in PaneFixtures. The fixtures are the
// mini's own form with its letters redacted, and QA's single-question dialog.
public class AskFormViewTests
{
    private static ChatPrompt Prompt(string screen) =>
        AskFormView.ToPrompt(ChatTranscript.ParseAskForm(screen)!);

    // The parser cannot tell which tab of a multi-question form is showing (the
    // bar's ☒ flips as soon as one box is ticked), so a real form claims no
    // question number rather than a wrong one.
    [Fact]
    public void ARealMultiQuestionFormClaimsNoProgressBecauseTheScreenCannotSay()
    {
        Assert.Null(Prompt(PaneFixtures.MiniAskForm).Progress);
    }

    // The rule itself, for when a form does say: four tabs, the last being
    // Submit, are three questions, and the second one showing is "2 of 3".
    [Fact]
    public void WhenTheCurrentTabIsKnownItSaysQuestionNOfM()
    {
        var form = ChatTranscript.ParseAskForm(PaneFixtures.MiniAskForm)! with { CurrentTab = 1 };

        Assert.Equal("Question 2 of 3", AskFormView.ToPrompt(form).Progress);
    }

    [Fact]
    public void ASingleQuestionFormClaimsNoProgress()
    {
        Assert.Null(Prompt(PaneFixtures.MacModelSwitchDialog).Progress);
    }

    [Fact]
    public void TheSubmitTabClaimsNoQuestionNumber()
    {
        var allAnswered = PaneFixtures.MiniAskForm.Replace("☐", "☒");

        Assert.Null(Prompt(allAnswered).Progress);
    }

    [Fact]
    public void ABarWithNoCurrentTabClaimsNothing()
    {
        var noneOpenNoSubmit = PaneFixtures.MiniAskForm.Replace("☐", "☒").Replace("✔ Submit", "☒ Submit");

        Assert.Null(Prompt(noneOpenNoSubmit).Progress);
    }

    [Fact]
    public void AFormWithNoBarAtAllClaimsNothing()
    {
        Assert.Null(Prompt(PaneFixtures.MiniOpenclawQuestionDialog).Progress);
    }

    [Fact]
    public void OnlyTheTypeSomethingOptionIsMarkedFreeText()
    {
        var options = Prompt(PaneFixtures.MiniAskForm).Options;

        Assert.Equal(new[] { "5" }, options.Where(o => o.FreeText).Select(o => o.Key));
        Assert.Equal(6, options.Count);
    }

    [Fact]
    public void TheWholeQuestionIsTheTitle()
    {
        var title = Prompt(PaneFixtures.MiniAskForm).Title;

        Assert.StartsWith("xxxxx xx xxx xxxxx", title);
        Assert.EndsWith("xx xxxxxx xxxxxxx.", title);
    }

    // The two arms of ReadPrompt: a form reads as a form, and everything else
    // reads exactly as it always did.
    [Fact]
    public void AFormReadsAsAFormAndAnythingElseAsAnOrdinaryDialog()
    {
        var form = LocalCliChatSession.ReadPrompt(PaneFixtures.MiniAskForm)!;
        Assert.Null(form.Progress);
        Assert.Contains(form.Options, o => o.FreeText);

        var dialog = LocalCliChatSession.ReadPrompt(PaneFixtures.MiniExitDialog)!;
        Assert.Null(dialog.Progress);
        Assert.DoesNotContain(dialog.Options, o => o.FreeText);
        Assert.Equal(ChatTranscript.ParseDialog(PaneFixtures.MiniExitDialog)!.Options.Count, dialog.Options.Count);

        Assert.Null(LocalCliChatSession.ReadPrompt(PaneFixtures.MacPromptAfterEsc));
    }

    // --- the real multi-select and Submit-tab captures (CB-307 item 3) ---------

    [Fact]
    public void AMultiSelectIsShownAsACheckList()
    {
        var options = Prompt(AskFormFixtures.MultiSelectQ2).Options;

        Assert.Equal(new[] { "\u2610 Cheese", "\u2610 Ham", "\u2610 Olives" }, options.Take(3).Select(o => o.Label));

        // A digit toggles a row without leaving the question, so the keys are
        // exactly the digits the terminal shows. The "Type something" row (4)
        // is not offered: typing into a multi-select's row was never captured.
        // "Chat about this" (5) is, unboxed, and Submit is its own button.
        Assert.Equal(new[] { "1", "2", "3", "5", AskFormView.CommitKey }, options.Select(o => o.Key));
        Assert.Equal("Chat about this", options[3].Label);
        Assert.DoesNotContain(options, o => o.FreeText);
    }

    [Fact]
    public void ACheckedRowShowsAsChecked()
    {
        var options = Prompt(AskFormFixtures.MultiSelectTwoChecked).Options;

        Assert.Equal(new[] { "\u2611 Cheese", "\u2611 Ham", "\u2610 Olives" },
            options.Take(3).Select(o => o.Label));
    }

    // The Submit tab: two explicit labelled buttons, armed so neither the
    // irreversible Submit answers nor the form-declining Cancel can fire on a
    // press that is not a deliberate click.
    [Fact]
    public void TheSubmitTabIsTwoLabelledButtonsAndIsArmed()
    {
        var prompt = Prompt(AskFormFixtures.SubmitTab);

        Assert.StartsWith("Ready to submit your answers?", prompt.Title);
        Assert.Equal(new[] { ("1", "Submit answers"), ("2", "Cancel") },
            prompt.Options.Select(o => (o.Key, o.Label)));
        Assert.True(prompt.ArmedActivation);
        Assert.Null(prompt.Progress);
    }

    [Fact]
    public void AQuestionIsNeverArmed()
    {
        Assert.False(Prompt(AskFormFixtures.SingleSelectQ1).ArmedActivation);
        Assert.False(Prompt(AskFormFixtures.MultiSelectQ2).ArmedActivation);
        Assert.False(Prompt(PaneFixtures.MiniAskForm).ArmedActivation);
    }

    [Fact]
    public void OnSubmitTabIsFalseWhenTheCurrentTabIsUnknownOrOutOfRange()
    {
        var form = ChatTranscript.ParseAskForm(AskFormFixtures.SubmitTab)!;

        Assert.True(AskFormView.OnSubmitTab(form));

        // The review screen is the Submit tab whether or not the tab is known.
        Assert.True(AskFormView.OnSubmitTab(form with { CurrentTab = -1 }));

        // Without the review marker, only a current tab that is the Submit tab
        // counts, and an unknown or out-of-range one does not.
        var plain = form with { IsReview = false };
        Assert.True(AskFormView.OnSubmitTab(plain));
        Assert.False(AskFormView.OnSubmitTab(plain with { CurrentTab = -1 }));
        Assert.False(AskFormView.OnSubmitTab(plain with { CurrentTab = 99 }));
    }

    // A real screen with a typed answer: the row's label became the text, which
    // is not the free-text option any more (the parser says so), and is shown
    // as an ordinary row.
    [Fact]
    public void ATypedFreeTextRowIsNotOfferedAsAnotherTextBox()
    {
        Assert.DoesNotContain(Prompt(AskFormFixtures.FreeTextTyped).Options, o => o.FreeText);
    }

    // The same prompt, whether it was read locally or came over the link.
    [Fact]
    public void TheWireAndTheLocalReadingAgree()
    {
        var local = Prompt(AskFormFixtures.SubmitTab);
        var wire = MirrorProtocol.PromptFromScreen(AskFormFixtures.SubmitTab)!;

        var overTheLink = AskFormView.FromWire(wire);

        Assert.Equal(local.Title, overTheLink.Title);
        Assert.Equal(local.ArmedActivation, overTheLink.ArmedActivation);
        Assert.Equal(local.Options.Select(o => o.Label), overTheLink.Options.Select(o => o.Label));
    }

    // --- the multi-select's Submit button, the review lines, and the wire --------

    // The terminal's Submit row is unnumbered, so there is no key to press: the
    // panel gets an explicit button whose key is a sentinel no screen can make.
    [Fact]
    public void AMultiSelectGetsAnExplicitSubmitButtonLast()
    {
        var options = Prompt(AskFormFixtures.MultiSelectQ2).Options;

        var submit = options[^1];
        Assert.Equal((AskFormView.CommitKey, "Submit"), (submit.Key, submit.Label));
        Assert.False(submit.FreeText);
    }

    [Fact]
    public void ASingleSelectOrAReviewHasNoCommitButton()
    {
        Assert.DoesNotContain(Prompt(AskFormFixtures.SingleSelectQ1).Options, o => o.Key == AskFormView.CommitKey);
        Assert.DoesNotContain(Prompt(AskFormFixtures.SubmitTab).Options, o => o.Key == AskFormView.CommitKey);
    }

    [Fact]
    public void TheReviewListsWhatIsAboutToBeSent()
    {
        var title = Prompt(AskFormFixtures.SubmitTab).Title;

        Assert.StartsWith("Ready to submit your answers?", title);
        Assert.Contains("\u25cf Pick a color \u2192 my own answer", title);
        Assert.Contains("\u25cf Pick toppings \u2192 Cheese, Ham, Olives", title);
    }

    // What came over the link reads exactly like what was read locally, for the
    // multi-select and the review alike.
    [Fact]
    public void AMultiSelectOverTheLinkReadsLikeTheLocalOne()
    {
        var local = Prompt(AskFormFixtures.MultiSelectTwoChecked);
        var wire = MirrorProtocol.PromptFromScreen(AskFormFixtures.MultiSelectTwoChecked)!;

        Assert.True(wire.MultiSelect);
        Assert.True(wire.HasSubmitRow);

        var overTheLink = AskFormView.FromWire(wire);

        Assert.Equal(local.Options.Select(o => (o.Key, o.Label)), overTheLink.Options.Select(o => (o.Key, o.Label)));
        Assert.Equal(local.Title, overTheLink.Title);
    }

    [Fact]
    public void AReviewOverTheLinkIsArmedAndListsTheAnswers()
    {
        var wire = MirrorProtocol.PromptFromScreen(AskFormFixtures.SubmitTab)!;

        var overTheLink = AskFormView.FromWire(wire);

        Assert.True(overTheLink.ArmedActivation);
        Assert.Contains("Cheese, Ham, Olives", overTheLink.Title);
        Assert.Equal(new[] { "Submit answers", "Cancel" }, overTheLink.Options.Select(o => o.Label));
    }

    // The wire's Review is an empty list on every screen but the review, which
    // once made every peer form read as a review and arm all of its buttons.
    // An ordinary question over the link is not armed, and the real review is.
    [Fact]
    public void AnOrdinaryQuestionOverTheLinkIsNotArmedAndTheReviewIs()
    {
        foreach (var screen in new[]
                 {
                     AskFormFixtures.SingleSelectQ1, AskFormFixtures.MultiSelectQ2,
                     AskFormFixtures.FreeTextTyped, PaneFixtures.MiniAskForm,
                 })
        {
            var wire = MirrorProtocol.PromptFromScreen(screen)!;

            Assert.NotNull(wire.Review);
            Assert.Empty(wire.Review!);
            Assert.False(AskFormView.FromWire(wire).ArmedActivation);
        }

        Assert.True(AskFormView.FromWire(MirrorProtocol.PromptFromScreen(AskFormFixtures.SubmitTab)!).ArmedActivation);
    }
}
