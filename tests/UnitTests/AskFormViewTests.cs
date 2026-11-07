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
}
