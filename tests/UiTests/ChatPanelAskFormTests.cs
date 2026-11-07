using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UiTests;

// CB-307 item 3: a question form (AskUserQuestion) in the panel. The whole
// question as the title, "Question n of m" when the screen says which tab is
// current, option buttons, and a text box for the option that asks for typed
// text. Nothing here ever presses Escape: on some of these screens Escape does
// something other than cancel.
[Collection("Settings")]
public class ChatPanelAskFormTests : IDisposable
{
    private readonly List<string> _toClean = new();

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    // Prompts and free text, the way RemoteControlChatSession will offer them;
    // answering records what it was handed and clears nothing, so each test says
    // what it expects the panel to have done on its own.
    private sealed class FormSession : IRemoteChatSession, IRemoteChatPrompts, IRemoteChatFreeText
    {
        public string SessionId { get; } = "ask-form-" + Guid.NewGuid();
        public string DisplayName => "Evidence";
        public RemoteChatState State => RemoteChatState.Connected;
        public IReadOnlyList<ChatTurn> History { get; } = new List<ChatTurn>();
        public event Action<ChatTurn>? TurnAdded { add { } remove { } }
        public event Action<ChatTurn>? TurnUpdated { add { } remove { } }
        public event Action<RemoteChatState>? StateChanged { add { } remove { } }
        public Task<ChatSendOutcome> SendAsync(string text) => Task.FromResult(ChatSendOutcome.Sent);
        public void Cancel() { }

        public ChatPrompt? Prompt { get; private set; }
        public event Action? PromptChanged;
        public List<ChatPromptOption> Answered { get; } = new();
        public List<(ChatPromptOption Option, string Text)> Typed { get; } = new();

        public void Show(ChatPrompt? prompt)
        {
            Prompt = prompt;
            PromptChanged?.Invoke();
        }

        public Task AnswerAsync(ChatPromptOption option)
        {
            Answered.Add(option);
            return Task.CompletedTask;
        }

        public Task AnswerFreeTextAsync(ChatPromptOption option, string text)
        {
            Typed.Add((option, text));
            return Task.CompletedTask;
        }

        public void AnswerElsewhere() { }
    }

    private static ChatPrompt Form(string? progress = "Question 2 of 3") => new(
        "Which of the two exhibits should the declaration lead with, given the page limit?",
        new[]
        {
            new ChatPromptOption("1", "Exhibit A"),
            new ChatPromptOption("2", "Exhibit B"),
            new ChatPromptOption("3", "Type something.", FreeText: true),
        },
        Progress: progress);

    private (ChatPanel Panel, FormSession Session) Open(ChatPrompt prompt)
    {
        var session = new FormSession();
        _toClean.Add(session.SessionId);
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), session);
        Flush();
        session.Show(prompt);
        Flush();
        return (ChatPanelTestAccess.Instance!, session);
    }

    private static Border Option(ChatPanel panel, int index) =>
        ((Control)panel.FindControl<ItemsControl>("PromptOptions")!.ItemsPanelRoot!.Children[index])
            .GetVisualDescendants().OfType<Border>().First();

    // The panel answers an unarmed prompt on a press, so a press is the click.
    private static void Click(ChatPanel panel, Border option)
    {
        var at = option.TranslatePoint(new Avalonia.Point(2, 2), panel) ?? new Avalonia.Point(2, 2);
        option.RaiseEvent(new PointerPressedEventArgs(
            option, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true), panel, at, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
    }

    private static TextBlock Progress(ChatPanel panel) => panel.FindControl<TextBlock>("PromptProgress")!;
    private static Control FreeText(ChatPanel panel) => panel.FindControl<Control>("PromptFreeText")!;
    private static TextBox FreeTextBox(ChatPanel panel) => panel.FindControl<TextBox>("PromptFreeTextBox")!;

    [AvaloniaFact]
    public void TheWholeQuestionAndWhichOneItIsAreShown()
    {
        var (panel, _) = Open(Form());

        Assert.Equal(
            "Which of the two exhibits should the declaration lead with, given the page limit?",
            panel.FindControl<TextBlock>("PromptTitle")!.Text);
        Assert.True(Progress(panel).IsVisible);
        Assert.Equal("Question 2 of 3", Progress(panel).Text);
    }

    // The bar alone cannot always say which tab is current, and then nothing is
    // claimed rather than a guess.
    [AvaloniaFact]
    public void WithNoProgressNothingIsClaimed()
    {
        var (panel, _) = Open(Form(progress: null));

        Assert.False(Progress(panel).IsVisible);
    }

    [AvaloniaFact]
    public void AnOrdinaryOptionIsAnsweredByItsKeyAndNoTextBoxAppears()
    {
        var (panel, session) = Open(Form());

        Click(panel, Option(panel, 0));
        Flush();

        Assert.Equal("1", Assert.Single(session.Answered).Key);
        Assert.False(FreeText(panel).IsVisible);
    }

    // Choosing the free-text option answers nothing yet: it opens the box.
    [AvaloniaFact]
    public void TheTypeSomethingOptionOpensATextBoxAndAnswersNothingYet()
    {
        var (panel, session) = Open(Form());

        Click(panel, Option(panel, 2));
        Flush();

        Assert.True(FreeText(panel).IsVisible);
        Assert.Empty(session.Answered);
        Assert.Empty(session.Typed);
    }

    [AvaloniaFact]
    public void SubmittingTheTextSendsItWithThatOptionAndClosesTheBox()
    {
        var (panel, session) = Open(Form());
        Click(panel, Option(panel, 2));
        Flush();

        FreeTextBox(panel).Text = "  lead with Exhibit B  ";
        panel.SubmitFreeText();
        Flush();

        var (option, text) = Assert.Single(session.Typed);
        Assert.Equal("3", option.Key);
        Assert.Equal("lead with Exhibit B", text);
        Assert.False(FreeText(panel).IsVisible);
        Assert.Empty(session.Answered);
    }

    [AvaloniaFact]
    public void EmptyTextIsNotSent()
    {
        var (panel, session) = Open(Form());
        Click(panel, Option(panel, 2));
        Flush();

        FreeTextBox(panel).Text = "   ";
        panel.SubmitFreeText();

        Assert.Empty(session.Typed);
        Assert.True(FreeText(panel).IsVisible);
    }

    [AvaloniaFact]
    public void EnterInTheBoxSendsIt()
    {
        var (panel, session) = Open(Form());
        Click(panel, Option(panel, 2));
        Flush();
        FreeTextBox(panel).Text = "B";

        FreeTextBox(panel).RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter,
        });
        Flush();

        Assert.Equal("B", Assert.Single(session.Typed).Text);
    }

    // Escape is never handled: a form screen where Escape does something other
    // than cancel must not be sent one by a key pressed in a text box.
    [AvaloniaFact]
    public void EscapeInTheBoxSendsNothingAndLeavesItOpen()
    {
        var (panel, session) = Open(Form());
        Click(panel, Option(panel, 2));
        Flush();
        FreeTextBox(panel).Text = "B";

        FreeTextBox(panel).RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape,
        });
        Flush();

        Assert.Empty(session.Typed);
        Assert.Empty(session.Answered);
        Assert.True(FreeText(panel).IsVisible);
    }

    // The next tab replaces the question: a box left open for the old one must
    // not carry its half-typed text onto the new one.
    [AvaloniaFact]
    public void ANewQuestionClosesTheBoxAndDropsTheText()
    {
        var (panel, session) = Open(Form());
        Click(panel, Option(panel, 2));
        Flush();
        FreeTextBox(panel).Text = "half typed";

        session.Show(new ChatPrompt("Which month?",
            new[] { new ChatPromptOption("1", "June"), new ChatPromptOption("2", "July") },
            Progress: "Question 3 of 3"));
        Flush();

        Assert.False(FreeText(panel).IsVisible);
        Assert.Equal("", FreeTextBox(panel).Text);
        Assert.Equal("Question 3 of 3", Progress(panel).Text);
    }

    [AvaloniaFact]
    public void WhenTheFormIsAnsweredEverythingGoes()
    {
        var (panel, session) = Open(Form());
        Click(panel, Option(panel, 2));
        Flush();

        session.Show(null);
        Flush();

        Assert.False(panel.FindControl<Border>("PromptBox")!.IsVisible);
        Assert.False(Progress(panel).IsVisible);
        Assert.False(FreeText(panel).IsVisible);
    }

    // A session that cannot carry text offers no box: its free-text-labelled
    // option is just an option, and pressing it presses its key.
    private sealed class NoTextSession : IRemoteChatSession, IRemoteChatPrompts
    {
        public string SessionId { get; } = "no-text-" + Guid.NewGuid();
        public string DisplayName => "Plain";
        public RemoteChatState State => RemoteChatState.Connected;
        public IReadOnlyList<ChatTurn> History { get; } = new List<ChatTurn>();
        public event Action<ChatTurn>? TurnAdded { add { } remove { } }
        public event Action<ChatTurn>? TurnUpdated { add { } remove { } }
        public event Action<RemoteChatState>? StateChanged { add { } remove { } }
        public Task<ChatSendOutcome> SendAsync(string text) => Task.FromResult(ChatSendOutcome.Sent);
        public void Cancel() { }
        public ChatPrompt? Prompt { get; set; }
        public event Action? PromptChanged;
        public List<ChatPromptOption> Answered { get; } = new();
        public void Raise() => PromptChanged?.Invoke();
        public Task AnswerAsync(ChatPromptOption option) { Answered.Add(option); return Task.CompletedTask; }
        public void AnswerElsewhere() { }
    }

    [AvaloniaFact]
    public void ASessionThatCannotCarryTextOffersNoBox()
    {
        var session = new NoTextSession();
        _toClean.Add(session.SessionId);
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), session);
        Flush();
        session.Prompt = Form();
        session.Raise();
        Flush();
        var panel = ChatPanelTestAccess.Instance!;

        Click(panel, Option(panel, 2));
        Flush();

        Assert.False(FreeText(panel).IsVisible);
        Assert.Equal("3", Assert.Single(session.Answered).Key);
    }

    // --- Send is live only for text that says something -------------------------

    private static bool SendEnabled(ChatPanel panel) =>
        panel.FindControl<Border>("PromptFreeTextSend")!.IsEnabled;

    // On the far screen an empty "Type something." row declines the whole form
    // when Enter lands on it, so blank text must never be sendable.
    [AvaloniaFact]
    public void SendIsDisabledUntilThereIsRealText()
    {
        var (panel, session) = Open(Form());
        Click(panel, Option(panel, 2));
        Flush();

        Assert.False(SendEnabled(panel));

        FreeTextBox(panel).Text = "   ";
        Flush();
        Assert.False(SendEnabled(panel));

        FreeTextBox(panel).Text = "B";
        Flush();
        Assert.True(SendEnabled(panel));

        FreeTextBox(panel).Text = "";
        Flush();
        Assert.False(SendEnabled(panel));
        Assert.Empty(session.Typed);
    }

    [AvaloniaFact]
    public void ABoxReopenedForAnotherQuestionStartsWithSendDisabled()
    {
        var (panel, session) = Open(Form());
        Click(panel, Option(panel, 2));
        FreeTextBox(panel).Text = "typed";
        Flush();
        Assert.True(SendEnabled(panel));

        session.Show(new ChatPrompt("Which month?",
            new[] { new ChatPromptOption("1", "June"), new ChatPromptOption("2", "Type something.", FreeText: true) }));
        Flush();
        Assert.False(SendEnabled(panel));
    }

    // --- the Submit tab: two explicit buttons, Cancel never pressed implicitly ---

    private static ChatPrompt SubmitTab() => AskFormView.ToPrompt(ChatTranscript.ParseAskForm(AskFormFixtures.SubmitTab)!);

    private static Pointer NewPointer() => new(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);

    private static void Press(ChatPanel panel, Border option)
    {
        var at = option.TranslatePoint(new Point(2, 2), panel) ?? new Point(2, 2);
        option.RaiseEvent(new PointerPressedEventArgs(
            option, NewPointer(), panel, at, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
    }

    private static void Release(ChatPanel panel, Border option)
    {
        var at = option.TranslatePoint(new Point(2, 2), panel) ?? new Point(2, 2);
        option.RaiseEvent(new PointerReleasedEventArgs(
            option, NewPointer(), panel, at, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
    }

    private readonly Func<DateTime> _previousClock = ChatPanel.PromptClock;
    private DateTime _now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    [AvaloniaFact]
    public void TheSubmitTabOffersSubmitAnswersAndCancelAsLabelledButtons()
    {
        var (panel, _) = Open(SubmitTab());

        Assert.StartsWith("Ready to submit your answers?", panel.FindControl<TextBlock>("PromptTitle")!.Text);
        var labels = panel.FindControl<ItemsControl>("PromptOptions")!.ItemsPanelRoot!.Children
            .Select(c => ((Control)c).GetVisualDescendants().OfType<TextBlock>().First().Text).ToList();
        Assert.Equal(new[] { "Submit answers", "Cancel" }, labels);
    }

    // Armed: a press alone answers nothing. Cancel declines the whole form, so
    // it is only ever given by a click that is pressed and released on it after
    // the buttons have been up for a moment.
    [AvaloniaFact]
    public void CancelIsNeverPressedByAPressAloneOrAClickBeforeTheButtonsSettle()
    {
        ChatPanel.PromptClock = () => _now;
        try
        {
            var (panel, session) = Open(SubmitTab());

            // Pressed straight away: inside the arming window.
            Press(panel, Option(panel, 1));
            Release(panel, Option(panel, 1));
            Flush();
            Assert.Empty(session.Answered);

            // After the window, a press with no release still answers nothing.
            _now += TimeSpan.FromSeconds(5);
            Press(panel, Option(panel, 1));
            Flush();
            Assert.Empty(session.Answered);

            // Released over a different button than it was pressed on: nothing.
            Release(panel, Option(panel, 0));
            Flush();
            Assert.Empty(session.Answered);
        }
        finally
        {
            ChatPanel.PromptClock = _previousClock;
        }
    }

    [AvaloniaFact]
    public void ADeliberateClickOnSubmitAnswersAnswersOnlyThatButton()
    {
        ChatPanel.PromptClock = () => _now;
        try
        {
            var (panel, session) = Open(SubmitTab());
            _now += TimeSpan.FromSeconds(5);

            Press(panel, Option(panel, 0));
            Release(panel, Option(panel, 0));
            Flush();

            var answered = Assert.Single(session.Answered);
            Assert.Equal("1", answered.Key);
            Assert.Equal("Submit answers", answered.Label);
        }
        finally
        {
            ChatPanel.PromptClock = _previousClock;
        }
    }

    [AvaloniaFact]
    public void ADeliberateClickOnCancelAnswersCancelAndNothingElse()
    {
        ChatPanel.PromptClock = () => _now;
        try
        {
            var (panel, session) = Open(SubmitTab());
            _now += TimeSpan.FromSeconds(5);

            Press(panel, Option(panel, 1));
            Release(panel, Option(panel, 1));
            Flush();

            Assert.Equal("2", Assert.Single(session.Answered).Key);
        }
        finally
        {
            ChatPanel.PromptClock = _previousClock;
        }
    }

    // --- the remaining arms of the free-text box ----------------------------------

    private static void PressSend(ChatPanel panel)
    {
        var send = panel.FindControl<Border>("PromptFreeTextSend")!;
        var at = send.TranslatePoint(new Point(2, 2), panel) ?? new Point(2, 2);
        send.RaiseEvent(new PointerPressedEventArgs(
            send, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true), panel, at, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
    }

    [AvaloniaFact]
    public void ClickingSendSendsTheText()
    {
        var (panel, session) = Open(Form());
        Click(panel, Option(panel, 2));
        Flush();
        FreeTextBox(panel).Text = "lead with B";
        Flush();

        PressSend(panel);
        Flush();

        Assert.Equal("lead with B", Assert.Single(session.Typed).Text);
        Assert.False(FreeText(panel).IsVisible);
    }

    // Nothing is open, so there is nothing to send.
    [AvaloniaFact]
    public void SubmittingWithNoBoxOpenDoesNothing()
    {
        var (panel, session) = Open(Form());

        panel.SubmitFreeText();

        Assert.Empty(session.Typed);
    }

    [AvaloniaFact]
    public void ABoxWhoseTextIsNullSendsNothing()
    {
        var (panel, session) = Open(Form());
        Click(panel, Option(panel, 2));
        Flush();

        FreeTextBox(panel).Text = null;
        panel.SubmitFreeText();

        Assert.Empty(session.Typed);
        Assert.True(FreeText(panel).IsVisible);
    }
}
