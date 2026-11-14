using System.Linq;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-327: the box-top rule, the Claude prompt arm called on its own, and the
// form parser's reading of a pane too narrow to keep a form on few rows. The
// real captures are checked, one per file, in tests/TranscriptTests/
// Cb327Captures.cs; these are the arms those captures do not reach, built from
// the captures' own shapes.
public class NarrowPaneTests
{
    // --- IsBoxTop: every arm of both shapes ---

    [Theory]
    [InlineData("──────────────────────────────")]                      // (a) a long run, nothing after
    [InlineData("─────────── long-session-name ─")]                      // (a) with a title
    [InlineData("━━━━━━━━━━━━━━")]                                       // (a) heavy rule
    [InlineData("═══════════ x ─")]                                      // (a) double rule
    [InlineData("─── long-session-name ─")]                             // (b) 23 columns
    [InlineData("long-session-name ─")]                                  // (b) 20 columns, no leading rule
    [InlineData("long-session-n… ─")]                                    // (b) 18 columns, name cut
    [InlineData("── a ──")]                                              // (b) a longer tail
    [InlineData("━ a ━")]                                                // (b) heavy
    public void AcceptsABoxTop(string line) => Assert.True(PaneScreen.IsBoxTop(line), line);

    [Theory]
    [InlineData("")]                                                     // nothing at all
    [InlineData("hello there")]                                          // no rule tail
    [InlineData("───")]                                                  // all rule, too short for (a), nothing before the tail
    [InlineData("name─")]                                                // a rule tail with no space before it
    [InlineData("─── ─")]                                                // only rule characters either side
    [InlineData("──x ─")]                                                // a leading run not followed by a space
    [InlineData("❯ hi ─")]                                               // an echoed prompt
    [InlineData("> hi ─")]                                               // …on a console that draws >
    [InlineData("foo ─── x ─")]                                          // text before the leading run
    [InlineData("──── ─")]                                               // a title-less edge
    [InlineData("… ─")]                                                  // a title with no letter or digit
    [InlineData("a ─ b ─")]                                              // a rule character inside the title
    public void RefusesAnythingElse(string line) => Assert.False(PaneScreen.IsBoxTop(line), line);

    // --- the Claude arm, called straight on the echo screens ---

    private static (PaneReading Reading, string Check) Claude(string screen)
    {
        var lines = ChatTranscript.ScreenLines(screen).Select(l => l.Trim()).ToArray();
        return PaneScreen.ClaudePrompt(lines, ChatTranscript.WindowStart(lines));
    }

    [Theory]
    [InlineData("modelswitch-w20")]
    [InlineData("modelswitch-w23")]
    [InlineData("modelswitch-w60")]
    [InlineData("model-w23")]
    [InlineData("model-w32")]
    [InlineData("permissions-w23")]
    [InlineData("trust-w32")]
    public void TheEchoedPromptAboveADialogIsNeverAPrompt(string capture)
    {
        // ParseDialog is bypassed on purpose: this is the rule the dialog check
        // would otherwise answer for.
        Assert.NotEqual(PaneReading.Prompt, Claude(Cb327Captures.Load(capture)).Reading);
    }

    [Fact]
    public void TheEarlierEchoFixturesAreNeverAPromptEither()
    {
        Assert.NotEqual(PaneReading.Prompt, Claude(PaneFixtures.MacModelSwitchDialog).Reading);
        Assert.NotEqual(PaneReading.Prompt, Claude(PaneFixtures.MacModelSwitchDialogShortEcho).Reading);
    }

    [Theory]
    [InlineData("idle-named-w12")]
    [InlineData("idle-named-w20")]
    [InlineData("idle-named-w23")]
    [InlineData("idle-named-draft-w23")]
    [InlineData("idle-after-turns-w20")]
    public void ANarrowTitledBoxIsAPromptThroughTheClaudeArm(string capture)
    {
        var (reading, check) = Claude(Cb327Captures.Load(capture));
        Assert.Equal((PaneReading.Prompt, "prompt"), (reading, check));
    }

    [Fact]
    public void ATitledEdgeAloneIsNotEnough()
    {
        // The edge passes, but nothing is ruled under the mark: still refused.
        var (reading, check) = Claude("long-session-name ─\n❯ hi\nno rule here\n");
        Assert.Equal((PaneReading.Unknown, "no-box-bottom"), (reading, check));
    }

    [Fact]
    public void ATitledEdgeWithOptionsUnderTheBoxIsRefused()
    {
        var (reading, check) = Claude("long-session-name ─\n❯ hi\n───────────────────────\n  1. Yes\n");
        Assert.Equal((PaneReading.Unknown, "options-under-box"), (reading, check));
    }

    [Fact]
    public void ABareMarkWithNoEdgeIsRefused()
    {
        Assert.Equal((PaneReading.Unknown, "no-box-top"), Claude("hello\n\n\n❯ hi\n───────────────────────\n"));
    }

    // --- the option gap: the rest of its arms are in TranscriptSuite ---

    [Fact]
    public void AGapIsCrossedOnlyToTheNumberJustBelowIt()
    {
        // 1 and 3 with a described gap: the walk crosses the description, finds a 1
        // where it wants a 2, and stops. The run is just "3. No", one option, no dialog.
        Assert.Null(ChatTranscript.ParseDialog("Pick one\n❯ 1. Yes\n     about\n  3. No\n"));
    }

    // --- the form: tab bars, the question, the head's reach ---

    private const string Tail = "  3. Type something.\n────────────────────────\n  4. Chat about this\n\nEnter to select · Esc to cancel\n";

    [Fact]
    public void ATwoRowBarIsReadByItsMarksAndNamedFromItsSecondRow()
    {
        var form = ChatTranscript.ParseAskForm(
            "────────────────────────\n←  ☐      ☐ ✔      →\n  Venue  Cat     Submit\n\nWhich?\n\n❯ 1. a\n     d\n  2. b\n" + Tail)!;
        Assert.Equal(new[] { "Venue", "Cat", "Submit" }, form.Tabs.Select(t => t.Name));
        Assert.Equal(AskTabState.Submit, form.Tabs[2].State);
        Assert.Equal("Which?", form.Prompt.Title);
    }

    [Fact]
    public void ASecondRowWithTooFewNamesLeavesTheRestEmptyAndSubmitIsStillSubmit()
    {
        var form = ChatTranscript.ParseAskForm(
            "────────────────────────\n←  ☐      ☐ ✔      →\n  Venue\n\nWhich?\n\n❯ 1. a\n  2. b\n" + Tail)!;
        Assert.Equal(new[] { "Venue", "", "Submit" }, form.Tabs.Select(t => t.Name));
    }

    [Fact]
    public void ABarWithEveryNameOnItHasNoSecondRowAndTheLineUnderItIsTheQuestion()
    {
        var form = ChatTranscript.ParseAskForm(
            "────────────────────────\n←  ☒ Color  ☐ Toppings  ✔ Submit  →\nPick a color\n\n❯ 1. Red\n  2. Blue\n" + Tail)!;
        Assert.Equal(new[] { "Color", "Toppings", "Submit" }, form.Tabs.Select(t => t.Name));
        Assert.Equal(AskTabState.Answered, form.Tabs[0].State);
        Assert.Equal("Pick a color", form.Prompt.Title);
    }

    [Theory]
    [InlineData("")]                                   // the line under a bar is empty: no second row
    [InlineData("│ Which?")]                           // …or the question's own frame
    [InlineData("Venue ☐ extra")]                        // …or holds marks: not a row of names
    public void ALineUnderTheBarThatIsNotNamesIsNotTakenForThem(string under)
    {
        var form = ChatTranscript.ParseAskForm(
            "────────────────────────\n←  ☐      ☐ ✔      →\n" + under + "\nWhich?\n\n❯ 1. a\n  2. b\n" + Tail)!;
        Assert.Equal(new[] { "", "", "Submit" }, form.Tabs.Select(t => t.Name));
    }

    [Fact]
    public void ABarDirectlyOverOptionOneHasNoRoomForASecondRow()
    {
        var form = ChatTranscript.ParseAskForm("←  ☐      ☐ ✔      →\n❯ 1. a\n  2. b\n" + Tail)!;
        Assert.Equal(new[] { "", "", "Submit" }, form.Tabs.Select(t => t.Name));
    }

    [Fact]
    public void AFramedQuestionIsStillOnlyItsFramedLines()
    {
        var form = ChatTranscript.ParseAskForm(
            "☐ Venue\nsome other line\n│ Which venue\n│ for the team?\n\n❯ 1. a\n  2. b\n" + Tail)!;
        Assert.Equal("Which venue for the team?", form.Prompt.Title);
    }

    [Fact]
    public void AnUnframedQuestionJoinsEveryLineAndSkipsBlanksAndRules()
    {
        var form = ChatTranscript.ParseAskForm(
            "☐ Venue\nWhich venue\n\n──────────────────────\nfor the team?\n\n❯ 1. a\n  2. b\n" + Tail)!;
        Assert.Equal("Which venue for the team?", form.Prompt.Title);
    }

    [Fact]
    public void AFormKnownOnlyByItsTypeSomethingRowKeepsTheDialogTitle()
    {
        var form = ChatTranscript.ParseAskForm("earlier prose\nmore prose\nWhich?\n\n❯ 1. a\n  2. b\n" + Tail)!;
        Assert.Empty(form.Tabs);
        Assert.Equal("Which?", form.Prompt.Title);
    }

    [Fact]
    public void AFormWithNothingBetweenItsHeadAndOptionOneKeepsTheDialogTitle()
    {
        var form = ChatTranscript.ParseAskForm("☐ Venue\n❯ 1. a\n  2. b\n" + Tail)!;
        Assert.Equal("☐ Venue", form.Prompt.Title);
    }

    [Fact]
    public void ABarWithinReachOfOptionOneIsFoundAndOneBeyondItIsNot()
    {
        string Screen(int filler) =>
            "←  ☐ A  ✔ Submit  →\n" + string.Concat(Enumerable.Repeat("a line of the question\n", filler))
            + "❯ 1. a\n  2. b\n" + Tail;

        Assert.Equal(2, ChatTranscript.ParseAskForm(Screen(39))!.Tabs.Count);   // 40 lines up
        Assert.Empty(ChatTranscript.ParseAskForm(Screen(41))!.Tabs);            // 42 lines up
    }

    [Fact]
    public void TheReviewHeadingAboveTheOldWindowIsStillSeen()
    {
        var form = ChatTranscript.ParseAskForm(
            "←  ☒ A  ✔ Submit  →\nReview your answers\n"
            + string.Concat(Enumerable.Repeat("  wrapped\n  again\n", 17))
            + "● Q\n→ A\n\n❯ 1. Submit answers\n  2. Cancel\n")!;
        Assert.True(form.IsReview);
        Assert.Single(form.Review!);
    }

    // --- a list the pane cuts off is not minted ---

    [Theory]
    [InlineData("↓", true)]
    [InlineData("↑", true)]
    [InlineData("                  ↓", true)]
    [InlineData("Fastest for ↑", true)]
    [InlineData("", false)]
    [InlineData("↓/↑ to navigate", false)]
    [InlineData("down↓", false)]
    [InlineData("Enter to select · ↑/↓", false)]
    public void ALineThatIsOnlyAnArrowOrEndsInOneAfterSpacesIsAClip(string line, bool clipped) =>
        Assert.Equal(clipped, ChatTranscript.IsClipped(line));

    [Fact]
    public void AListWithAMarkAtItsFootOrHeadIsNoDialogAndAnUnclippedOneIs()
    {
        const string list = "Pick one\n  1. Default\n  2. Opus\n";
        Assert.NotNull(ChatTranscript.ParseDialog(list));
        Assert.Null(ChatTranscript.ParseDialog(list + "                    ↓\n"));
        Assert.Null(ChatTranscript.ParseDialog("Pick one\n                    ↑\n  1. Default\n  2. Opus\n"));
        Assert.Null(ChatTranscript.ParseDialog("Pick one\n\nsomething ↑\n\n  1. Default\n  2. Opus\n"));
        // Above the title is scrollback, not the list's own mark.
        Assert.NotNull(ChatTranscript.ParseDialog("                    ↑\n" + list));
    }

    // Scrollback with an arrow in it, above a real dialog and above a real form,
    // is the assistant's prose and changes nothing.
    [Theory]
    [InlineData("Run it, then scroll ↓")]
    [InlineData("↑")]
    [InlineData("      ↓")]
    public void AnArrowInScrollbackAboveARealDialogOrFormStillParses(string prose)
    {
        var dialog = prose + "\n" + PaneFixtures.MiniExitDialog;
        Assert.NotNull(ChatTranscript.ParseDialog(dialog));
        Assert.Equal(ChatTranscript.ParseDialog(PaneFixtures.MiniExitDialog)!.Options.Count,
            ChatTranscript.ParseDialog(dialog)!.Options.Count);

        var form = Cb327Captures.Load("form4-w60");
        var withProse = prose + "\n\n" + form + "\n";
        Assert.NotNull(ChatTranscript.ParseAskForm(withProse));
        Assert.Equal(5, ChatTranscript.ParseAskForm(withProse)!.Tabs.Count);
        Assert.Equal(PaneReading.Dialog, PaneScreen.Classify(withProse));
    }

    [Theory]
    [InlineData("model-w100", true)]    // fully on screen: as on develop, a dialog
    [InlineData("model-w60", true)]
    [InlineData("model-w40", true)]
    [InlineData("model-w32-h60", true)]
    [InlineData("model-w32", false)]    // cut off, with an arrow where the list stops
    [InlineData("model-w23", false)]
    public void TheModelPickerIsADialogOnlyWhereItIsFullyOnScreen(string capture, bool dialog)
    {
        var screen = Cb327Captures.Load(capture);
        Assert.Equal(dialog, ChatTranscript.ParseDialog(screen) is not null);
        Assert.NotEqual(PaneReading.Prompt, PaneScreen.Classify(screen));
    }
}
