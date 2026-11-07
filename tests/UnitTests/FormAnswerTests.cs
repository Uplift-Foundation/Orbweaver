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
