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

    [Fact]
    public void AMultiQuestionFormSaysWhichQuestionItIs()
    {
        // Four tabs, the last being Submit: three questions, the second showing.
        Assert.Equal("Question 2 of 3", Prompt(PaneFixtures.MiniAskForm).Progress);
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
        Assert.Equal("Question 2 of 3", form.Progress);
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

        Assert.Equal(new[] { "\u2610 Cheese", "\u2610 Ham", "\u2610 Olives", "\u2610 Type something" },
            options.Take(4).Select(o => o.Label));

        // A digit toggles a row without leaving the question, so the keys are
        // exactly the digits the terminal shows.
        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, options.Select(o => o.Key));
    }

    [Fact]
    public void ACheckedRowShowsAsChecked()
    {
        var options = Prompt(AskFormFixtures.MultiSelectTwoChecked).Options;

        Assert.Equal(new[] { "\u2611 Cheese", "\u2611 Ham", "\u2610 Olives" },
            options.Take(3).Select(o => o.Label));
    }

    [Fact]
    public void OnlyTheBracketPrefixIsRewrittenAndOnlyAtTheStart()
    {
        Assert.Equal("Chat about this", AskFormView.CheckboxLabel("Chat about this"));
        Assert.Equal("Red [ ] later", AskFormView.CheckboxLabel("Red [ ] later"));
        Assert.Equal("[ ]no space", AskFormView.CheckboxLabel("[ ]no space"));
        Assert.Equal("\u2610 Cheese", AskFormView.CheckboxLabel("[ ] Cheese"));
        Assert.Equal("\u2611 Cheese", AskFormView.CheckboxLabel("[\u2714] Cheese"));
    }

    // The Submit tab: two explicit labelled buttons, armed so neither the
    // irreversible Submit answers nor the form-declining Cancel can fire on a
    // press that is not a deliberate click.
    [Fact]
    public void TheSubmitTabIsTwoLabelledButtonsAndIsArmed()
    {
        var prompt = Prompt(AskFormFixtures.SubmitTab);

        Assert.Equal("Ready to submit your answers?", prompt.Title);
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
        Assert.False(AskFormView.OnSubmitTab(form with { CurrentTab = -1 }));
        Assert.False(AskFormView.OnSubmitTab(form with { CurrentTab = 99 }));
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
}
