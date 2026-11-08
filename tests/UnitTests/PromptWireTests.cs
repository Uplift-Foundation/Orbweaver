using Orbweaver;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-307 addendum item 3: a dialog as it crosses the link. Read off the real
// (redacted) captures in PaneFixtures, never an invented screen.
public class PromptWireTests
{
    [Fact]
    public void AFormCrossesAsItsOptionsTabsAndKeys()
    {
        var prompt = MirrorProtocol.PromptFromScreen(PaneFixtures.MiniAskForm)!;

        Assert.Equal(new[] { "1", "2", "3", "4", "5", "6" }, prompt.Options.Select(o => o.Key));
        Assert.Equal(new[] { "answered", "open", "open", "submit" }, prompt.Tabs!.Select(t => t.State));
        // Which question of several is showing cannot be read (QA's captures).
        Assert.Equal(-1, prompt.Current);
        Assert.Equal("5", prompt.FreeTextKey);
        Assert.Equal("6", prompt.ChatAboutKey);
        Assert.Matches("^[0-9a-f]{16}$", prompt.Fingerprint);
    }

    [Fact]
    public void AnOrdinaryDialogCrossesWithNoTabs()
    {
        var prompt = MirrorProtocol.PromptFromScreen(PaneFixtures.MiniExitDialog)!;

        Assert.Equal(3, prompt.Options.Count);
        Assert.Empty(prompt.Tabs!);
        Assert.Equal(-1, prompt.Current);
        Assert.Null(prompt.FreeTextKey);
        Assert.Null(prompt.ChatAboutKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoScreenIsNoPrompt(string? screen) => Assert.Null(MirrorProtocol.PromptFromScreen(screen));

    [Fact]
    public void APromptScreenIsNoPrompt() => Assert.Null(MirrorProtocol.PromptFromScreen(PaneFixtures.MacPromptAfterEsc));

    [Fact]
    public void TheSameScreenAlwaysHasTheSameFingerprint() =>
        Assert.Equal(MirrorProtocol.PromptFromScreen(PaneFixtures.MiniAskForm)!.Fingerprint,
            MirrorProtocol.PromptFromScreen(PaneFixtures.MiniAskForm)!.Fingerprint);

    [Fact]
    public void ADifferentLabelTitleOrTabIsADifferentDialog()
    {
        var options = new[] { new MirrorProtocol.WireOption("1", "Yes"), new MirrorProtocol.WireOption("2", "No") };
        var fp = MirrorProtocol.PromptFingerprint("Proceed?", options, 0);

        Assert.NotEqual(fp, MirrorProtocol.PromptFingerprint("Proceed?", options, 1));
        Assert.NotEqual(fp, MirrorProtocol.PromptFingerprint("Continue?", options, 0));
        Assert.NotEqual(fp, MirrorProtocol.PromptFingerprint("Proceed?",
            new[] { options[0], new MirrorProtocol.WireOption("2", "Never") }, 0));

        // The answered tab moving on (☐ to ☒) is a different dialog too.
        Assert.NotEqual(MirrorProtocol.PromptFromScreen(PaneFixtures.MiniAskForm)!.Fingerprint,
            MirrorProtocol.PromptFromScreen(PaneFixtures.MiniAskForm.Replace("☐ xxx xxxx", "☒ xxx xxxx"))!.Fingerprint);
    }

    [Fact]
    public void AnOversizedDialogIsBounded()
    {
        var label = new string('y', 500);
        var screen = " Pick one\n" + string.Join("\n",
            Enumerable.Range(1, 15).Select(n => $" {(n == 1 ? "❯ " : "  ")}{n}. {label}")) + "\n Esc to cancel";

        var prompt = MirrorProtocol.PromptFromScreen(screen)!;

        Assert.Equal(MirrorProtocol.MaxPromptOptions, prompt.Options.Count);
        Assert.All(prompt.Options, o => Assert.Equal(MirrorProtocol.MaxPromptText, o.Label.Length));
    }

    [Fact]
    public void ALongQuestionIsClipped()
    {
        var screen = PaneFixtures.MacModelSwitchDialog.Replace("│ can change this later in /config.",
            "│ " + new string('q', 900));

        Assert.Equal(MirrorProtocol.MaxPromptTitle, MirrorProtocol.PromptFromScreen(screen)!.Title.Length);
    }

    [Fact]
    public void APromptRoundTripsAndGarbageIsNothing()
    {
        var prompt = MirrorProtocol.PromptFromScreen(PaneFixtures.MiniAskForm)!;
        var back = MirrorProtocol.DecodePrompt(MirrorProtocol.EncodePrompt(prompt))!;

        Assert.Equal(prompt.Fingerprint, back.Fingerprint);
        Assert.Equal(prompt.Options, back.Options);
        Assert.Equal(prompt.Tabs, back.Tabs);

        Assert.Null(MirrorProtocol.DecodePrompt(null));
        Assert.Null(MirrorProtocol.DecodePrompt("{not json"u8.ToArray()));
        Assert.Null(MirrorProtocol.DecodePrompt("{\"title\":\"x\"}"u8.ToArray()));
    }

    // --- the arms QA's captures unblocked --------------------------------------

    [Fact]
    public void AMultiSelectQuestionCarriesItsTicksAndSubmitRow()
    {
        var prompt = MirrorProtocol.PromptFromScreen(PaneFixtures.QaMultiSelectOneTicked)!;

        Assert.True(prompt.MultiSelect);
        Assert.True(prompt.HasSubmitRow);
        Assert.Equal(new[] { "1" }, prompt.Checked);
        Assert.Equal("Cheese", prompt.Options[0].Label);
    }

    [Fact]
    public void ATickOrATabChangeIsADifferentDialog()
    {
        Assert.NotEqual(MirrorProtocol.PromptFromScreen(PaneFixtures.QaMultiSelect)!.Fingerprint,
            MirrorProtocol.PromptFromScreen(PaneFixtures.QaMultiSelectOneTicked)!.Fingerprint);
        Assert.NotEqual(MirrorProtocol.PromptFromScreen(PaneFixtures.QaFormQ1)!.Fingerprint,
            MirrorProtocol.PromptFromScreen(PaneFixtures.QaFormQ1.Replace("☐ Color", "☒ Color"))!.Fingerprint);

        // Where the cursor sits is not: moving it is not answering.
        Assert.Equal(MirrorProtocol.PromptFromScreen(PaneFixtures.QaFormQ1)!.Fingerprint,
            MirrorProtocol.PromptFromScreen(PaneFixtures.QaCursorOnFreeText)!.Fingerprint);
    }

    [Fact]
    public void TheReviewScreenCarriesItsAnswersAndBothKeysByLabel()
    {
        var prompt = MirrorProtocol.PromptFromScreen(PaneFixtures.QaReview)!;

        Assert.Equal("1", prompt.SubmitAnswersKey);
        Assert.Equal("2", prompt.CancelKey);
        Assert.Equal(new MirrorProtocol.WireReviewItem("Pick toppings", "Cheese, Ham, Olives"), prompt.Review![1]);

        var back = MirrorProtocol.DecodePrompt(MirrorProtocol.EncodePrompt(prompt))!;
        Assert.Equal(prompt.Review, back.Review);
        Assert.Equal(prompt.CancelKey, back.CancelKey);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("two\nlines", false)]
    [InlineData("ends\r", false)]
    [InlineData("my own answer", true)]
    public void OnlyOneLineOfRealTextIsUsableFreeText(string? text, bool usable) =>
        Assert.Equal(usable, FormAnswer.IsUsableFreeText(text));

    [Theory]
    [InlineData("my own answer", "my own answer", true)]
    [InlineData("my own…", "my own answer", true)]
    [InlineData("my own…", "your own answer", false)]
    [InlineData("…", "anything", false)]
    [InlineData("Type something.", "my own answer", false)]
    public void TheRowReadsBackAsWhatWasTyped(string label, string text, bool shown) =>
        Assert.Equal(shown, FormAnswer.ShowsTyped(label, text));

    // --- the client's reading of a reply (coverage QA asked for) ---------------

    [Fact]
    public void APromptReplyMapsToItsPromptOrItsCode()
    {
        var good = MirrorProtocol.EncodePrompt(MirrorProtocol.PromptFromScreen(PaneFixtures.QaFormQ1)!);

        Assert.NotNull(RemoteMirrorClient.PromptOutcomeOf(true, null, good).Prompt);
        Assert.Equal(new RemoteMirrorClient.PromptOutcome(null, MirrorProtocol.ErrNoPrompt),
            RemoteMirrorClient.PromptOutcomeOf(false, MirrorProtocol.ErrNoPrompt, null));
        // Never answered: no code at all.
        Assert.Equal(MirrorProtocol.ErrUnsupported, RemoteMirrorClient.PromptOutcomeOf(false, null, null).Error);
        // An OK whose payload is not a prompt.
        Assert.Equal(new RemoteMirrorClient.PromptOutcome(null, MirrorProtocol.ErrUnsupported),
            RemoteMirrorClient.PromptOutcomeOf(true, null, "nope"u8.ToArray()));
    }

    [Fact]
    public void AnAnswerReplyMapsToNothingOrItsCode()
    {
        Assert.Null(RemoteMirrorClient.AnswerErrorOf(true, null));
        Assert.Equal(MirrorProtocol.ErrBadKey, RemoteMirrorClient.AnswerErrorOf(false, MirrorProtocol.ErrBadKey));
        Assert.Equal(MirrorProtocol.ErrUnsupported, RemoteMirrorClient.AnswerErrorOf(false, null));
    }

    [Fact]
    public void OnlyAnExplicitPrOneSaysAFarBuddyAnswersPrompts()
    {
        Assert.True(MirrorProtocol.SaysItAnswersPrompts(new Dictionary<string, string> { ["pr"] = "1" }));
        Assert.False(MirrorProtocol.SaysItAnswersPrompts(new Dictionary<string, string> { ["pr"] = "0" }));
        Assert.False(MirrorProtocol.SaysItAnswersPrompts(new Dictionary<string, string>()));
        Assert.False(MirrorProtocol.SaysItAnswersPrompts(null));
    }
}
