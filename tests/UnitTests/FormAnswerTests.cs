using Orbweaver;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-307 item 3: FormAnswer on its own, as LocalCliChatSession calls it —
// a screen that moves as a real one does (QA's captures), and every key and
// typed text recorded. The link's own tests drive the same code through the
// mirror server.
public class FormAnswerTests
{
    private static readonly SessionStatus Status = new() { Title = "qa" };

    private sealed class Pane(string screen)
    {
        public string Screen = screen;
        public List<string> Pressed { get; } = new();
        public List<string> Typed { get; } = new();
        public Func<string, string?> AfterPress = _ => null;
        public Func<string, string?> AfterType = _ => null;

        public Task<string?> Capture(SessionStatus _) => Task.FromResult<string?>(Screen);
        public Task<bool> Press(SessionStatus _, string key)
        {
            Pressed.Add(key);
            if (AfterPress(key) is { } next) Screen = next;
            return Task.FromResult(true);
        }
        public Task<bool> Type(SessionStatus _, string text)
        {
            Typed.Add(text);
            if (AfterType(text) is { } next) Screen = next;
            return Task.FromResult(true);
        }

        public string Fp => MirrorProtocol.PromptFromScreen(Screen)!.Fingerprint;

        public Task<string?> Answer(string? key, string? action = null, string? text = null, string? fp = null) =>
            FormAnswer.AnswerAsync(Status, Capture, Press, Type, fp ?? Fp, key, action, text);
    }

    [Fact]
    public async Task ADigitIsPressedWhenTheFormIsStillTheOneChosenFrom()
    {
        var pane = new Pane(PaneFixtures.QaFormQ1);

        Assert.Null(await pane.Answer("2"));
        Assert.Equal(new[] { "2" }, pane.Pressed);
    }

    [Fact]
    public async Task AStaleOrMissingFormPressesNothing()
    {
        var pane = new Pane(PaneFixtures.QaFormQ1);

        Assert.Equal(MirrorProtocol.ErrPromptChanged, await pane.Answer("1", fp: "0000000000000000"));
        pane.Screen = PaneFixtures.MacPromptAfterEsc;
        Assert.Equal(MirrorProtocol.ErrNoPrompt, await pane.Answer("1", fp: "x"));
        Assert.Empty(pane.Pressed);
    }

    [Fact]
    public async Task FreeTextGoesCursorThenTextThenEnter()
    {
        var pane = new Pane(PaneFixtures.QaFormQ1);
        var downs = 0;
        pane.AfterPress = k => k == "Down" && ++downs == 2 ? PaneFixtures.QaCursorOnFreeText : null;
        pane.AfterType = _ => PaneFixtures.QaFreeTextTyped;

        Assert.Null(await pane.Answer("3", text: "my own answer"));
        Assert.Equal(new[] { "Down", "Down", "Enter" }, pane.Pressed);
        Assert.Equal(new[] { "my own answer" }, pane.Typed);
    }

    // Enter only after the row reads back as the text: a cursor that never
    // reached the row, or text that never showed, stops before Enter.
    // CB-406, end to end on the real team-lead captures: a long answer wraps
    // over four rows of a 35-column pane and is still read back as typed, so
    // Enter is pressed. Failed before the fix (type-failed, text left in the
    // field). The cursor-on-row screen is TeamLeadNarrowForm with its ❯ moved to
    // row 4, which is all the arrows change: the fingerprint ignores it.
    [Fact]
    public async Task ALongAnswerInANarrowTeamLeadsPaneReachesEnter()
    {
        const string typed = "my own answer and this is a much longer reply that will not fit on one row of a narrow pane";
        var onRow = PaneFixtures.TeamLeadNarrowForm.Replace("❯ 1. Red", "  1. Red").Replace("  4. Type something.", "❯ 4. Type something.");
        var pane = new Pane(PaneFixtures.TeamLeadNarrowForm);
        var downs = 0;
        pane.AfterPress = k => k == "Down" && ++downs == 3 ? onRow : null;
        pane.AfterType = _ => PaneFixtures.TeamLeadNarrowFreeTextTyped;
        var said = new List<string>();

        Assert.Null(await FormAnswer.AnswerAsync(Status, pane.Capture, pane.Press, pane.Type, pane.Fp, "4", null, typed, said.Add));
        Assert.Equal(new[] { "Down", "Down", "Down", "Enter" }, pane.Pressed);
        Assert.Empty(said);
    }

    // QA round 2: a refused read-back says why, as numbers and never the text.
    [Theory]
    [InlineData(false, "readback stage=row read=form same-form=1 cursor=1 free=3 shown=15 wanted=0 prefix=0 rows=1")]
    [InlineData(true, "readback stage=typed read=form same-form=1 cursor=3 free=3 shown=15 wanted=13 prefix=0 rows=1")]
    public async Task ARefusedReadBackSaysWhyWithoutTheText(bool cursorArrives, string expected)
    {
        var pane = new Pane(PaneFixtures.QaFormQ1);
        var downs = 0;
        pane.AfterPress = k => cursorArrives && k == "Down" && ++downs == 2 ? PaneFixtures.QaCursorOnFreeText : null;
        var said = new List<string>();

        Assert.Equal(MirrorProtocol.ErrTypeFailed,
            await FormAnswer.AnswerAsync(Status, pane.Capture, pane.Press, pane.Type, pane.Fp, "3", null, "my own answer", said.Add));
        Assert.Equal(expected, Assert.Single(said));
        Assert.DoesNotContain("my own answer", said[0]);
    }

    [Fact]
    public void RowLinesCountsAWrappedRowAndNothingForAMissingOne()
    {
        Assert.Equal(4, FormAnswer.RowLines(PaneFixtures.QaTeamLeadShortPaneTyped, "3"));
        Assert.Equal(0, FormAnswer.RowLines(PaneFixtures.QaTeamLeadShortPaneTyped, "9"));
        Assert.Equal(0, FormAnswer.RowLines(null, "3"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreeTextStopsBeforeEnterWhenAStepIsNotSeen(bool cursorArrives)
    {
        var pane = new Pane(PaneFixtures.QaFormQ1);
        var downs = 0;
        pane.AfterPress = k => cursorArrives && k == "Down" && ++downs == 2 ? PaneFixtures.QaCursorOnFreeText : null;
        pane.AfterType = _ => null;

        Assert.Equal(MirrorProtocol.ErrTypeFailed, await pane.Answer("3", text: "my own answer"));
        Assert.DoesNotContain("Enter", pane.Pressed);
        Assert.Equal(cursorArrives ? 1 : 0, pane.Typed.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a\nb")]
    public async Task TheFreeTextOptionWithoutUsableTextIsRefused(string? text)
    {
        var pane = new Pane(PaneFixtures.QaFormQ1);

        Assert.Equal(MirrorProtocol.ErrBadKey, await pane.Answer("3", text: text));
        Assert.Empty(pane.Pressed);
        Assert.Empty(pane.Typed);
    }

    // The cursor below the row moves Up; an Up the pane refuses stops there.
    [Fact]
    public async Task AnUpThePaneRefusesStopsTheFreeTextBeforeAnything()
    {
        var below = PaneFixtures.QaFormQ1.Replace("❯ 1. Red", "  1. Red").Replace("  4. Chat about this", "❯ 4. Chat about this");
        var pane = new Pane(below);
        var typed = new List<string>();

        Assert.Equal(MirrorProtocol.ErrTypeFailed, await FormAnswer.AnswerAsync(Status, pane.Capture,
            (_, key) => { typed.Add(key); return Task.FromResult(false); }, pane.Type, pane.Fp, "3", null, "green"));
        Assert.Equal(new[] { "Up" }, typed);
        Assert.Empty(pane.Typed);
    }

    [Fact]
    public async Task NoKeyAndNoActionIsRefused()
    {
        var pane = new Pane(PaneFixtures.QaFormQ1);

        Assert.Equal(MirrorProtocol.ErrBadKey, await pane.Answer(null));
        Assert.Empty(pane.Pressed);
    }

    // Every way the free-text sequence can fail to see its step on screen, or
    // fail to make it, stops before Enter.
    [Theory]
    [InlineData("down-fails")]
    [InlineData("form-gone-after-move")]
    [InlineData("other-form-after-move")]
    [InlineData("type-throws")]
    [InlineData("form-gone-after-typing")]
    [InlineData("row-gone-after-typing")]
    [InlineData("cursor-moved-after-typing")]
    public async Task FreeTextStopsBeforeEnterWhenAnyStepFails(string fault)
    {
        var pane = new Pane(PaneFixtures.QaFormQ1);
        var downs = 0;
        pane.AfterPress = k => k != "Down" || ++downs < 2 ? null : fault switch
        {
            "form-gone-after-move" => PaneFixtures.MacPromptAfterEsc,
            "other-form-after-move" => PaneFixtures.QaMultiSelect,
            _ => PaneFixtures.QaCursorOnFreeText,
        };
        pane.AfterType = _ => fault switch
        {
            "form-gone-after-typing" => PaneFixtures.MacPromptAfterEsc,
            "row-gone-after-typing" => PaneFixtures.QaReview,
            "cursor-moved-after-typing" => PaneFixtures.QaFreeTextTyped
                .Replace("  1. Red", "❯ 1. Red").Replace("❯ 3. my own answer", "  3. my own answer"),
            _ => PaneFixtures.QaFreeTextTyped,
        };

        Func<SessionStatus, string, Task<bool>> press = fault == "down-fails"
            ? (_, _) => Task.FromResult(false)
            : pane.Press;
        Func<SessionStatus, string, Task<bool>> type = fault == "type-throws"
            ? (_, _) => throw new InvalidOperationException("tmux")
            : pane.Type;

        Assert.Equal(MirrorProtocol.ErrTypeFailed,
            await FormAnswer.AnswerAsync(Status, pane.Capture, press, type, pane.Fp, "3", null, "my own answer"));
        Assert.DoesNotContain("Enter", pane.Pressed);
    }

    [Fact]
    public async Task WithNoLiteralTypingFreeTextIsUnsupported()
    {
        var pane = new Pane(PaneFixtures.QaFormQ1);

        Assert.Equal(MirrorProtocol.ErrUnsupported,
            await FormAnswer.AnswerAsync(Status, pane.Capture, pane.Press, null, pane.Fp, "3", null, "green"));
        Assert.Empty(pane.Pressed);
    }

    [Fact]
    public async Task CommitIsRightOnAMultiSelectQuestionAndRefusedElsewhere()
    {
        var multi = new Pane(PaneFixtures.QaMultiSelectOneTicked);
        Assert.Null(await multi.Answer(null, MirrorProtocol.ActionCommit));
        Assert.Equal(new[] { "Right" }, multi.Pressed);

        var single = new Pane(PaneFixtures.QaFormQ1);
        Assert.Equal(MirrorProtocol.ErrBadKey, await single.Answer(null, MirrorProtocol.ActionCommit));
        Assert.Equal(MirrorProtocol.ErrBadKey, await single.Answer(null, "escape"));
        Assert.Empty(single.Pressed);
    }

    [Fact]
    public async Task APressThatFailsOrThrowsIsATypeFailure()
    {
        var pane = new Pane(PaneFixtures.QaFormQ1);

        Assert.Equal(MirrorProtocol.ErrTypeFailed, await FormAnswer.AnswerAsync(Status, pane.Capture,
            (_, _) => Task.FromResult(false), pane.Type, pane.Fp, "1", null, null));
        Assert.Equal(MirrorProtocol.ErrTypeFailed, await FormAnswer.AnswerAsync(Status, pane.Capture,
            (_, _) => throw new InvalidOperationException("tmux"), pane.Type, pane.Fp, "1", null, null));
    }

    [Fact]
    public async Task ACaptureThatThrowsIsNoForm() =>
        Assert.Equal(MirrorProtocol.ErrNoPrompt, await FormAnswer.AnswerAsync(Status,
            _ => throw new InvalidOperationException("tmux"), (_, _) => Task.FromResult(true), null, "x", "1", null, null));
}
