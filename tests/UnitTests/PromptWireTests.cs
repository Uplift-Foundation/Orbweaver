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
        Assert.Equal(1, prompt.Current);
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
}
