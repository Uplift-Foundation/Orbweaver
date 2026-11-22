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

// CB-412: /login in the panel. The picker as buttons, the sign-in URL as a link plus a code field,
// and the fallback for a route that cannot be read. The code is a credential: the field must be
// empty again before the send is awaited.
[Collection("Settings")]
public class ChatPanelLoginTests : IDisposable
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

    private sealed class LoginSession : IRemoteChatSession, IRemoteChatPrompts, IRemoteChatLoginCode
    {
        public string SessionId { get; } = "login-" + Guid.NewGuid();
        public string DisplayName => "Login";
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
        public List<string> Codes { get; } = new();
        public int ElsewhereCalls { get; private set; }
        // What the field held at the moment the code was handed over.
        public Func<string?>? FieldProbe { get; set; }
        public string? FieldAtSend { get; private set; }

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

        public void AnswerElsewhere() => ElsewhereCalls++;

        public Task SubmitLoginCodeAsync(string code)
        {
            Codes.Add(code);
            FieldAtSend = FieldProbe?.Invoke();
            return Task.CompletedTask;
        }
    }

    private const string Url = "https://claude.ai/oauth/authorize?code=true&client_id=x";

    private static ChatPrompt Picker() => new("Select login method:", new[]
    {
        new ChatPromptOption("1", "Claude account with subscription"),
        new ChatPromptOption("2", "Anthropic Console account"),
        new ChatPromptOption("3", "3rd-party platform"),
    });

    private static ChatPrompt UrlStep(bool code = true) =>
        new("Sign in in your browser", Array.Empty<ChatPromptOption>(), Link: Url, CodeEntry: code);

    private (ChatPanel Panel, LoginSession Session) Open(ChatPrompt? prompt)
    {
        var session = new LoginSession();
        _toClean.Add(session.SessionId);
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), session);
        Flush();
        session.Show(prompt);
        Flush();
        var panel = ChatPanelTestAccess.Instance!;
        session.FieldProbe = () => Box(panel).Text;
        return (panel, session);
    }

    private static TextBlock Link(ChatPanel p) => p.FindControl<TextBlock>("PromptLoginLink")!;
    private static Grid CodeRow(ChatPanel p) => p.FindControl<Grid>("PromptLoginCode")!;
    private static TextBox Box(ChatPanel p) => p.FindControl<TextBox>("PromptLoginCodeBox")!;
    private static Border Send(ChatPanel p) => p.FindControl<Border>("PromptLoginCodeSend")!;

    private static void Press(ChatPanel panel, Control target)
    {
        var at = target.TranslatePoint(new Point(2, 2), panel) ?? new Point(2, 2);
        target.RaiseEvent(new PointerPressedEventArgs(
            target, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true), panel, at, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
    }

    private static void Release(ChatPanel panel, Control target) =>
        target.RaiseEvent(new PointerReleasedEventArgs(
            target, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true), panel,
            target.TranslatePoint(new Point(2, 2), panel) ?? new Point(2, 2), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));

    private static void Enter(TextBox box) =>
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

    [AvaloniaFact]
    public void ThePickerIsOneButtonPerMethodAndNoLinkOrField()
    {
        var (panel, _) = Open(Picker());

        var items = panel.FindControl<ItemsControl>("PromptOptions")!;
        Assert.Equal(3, items.ItemsPanelRoot!.Children.Count);
        Assert.False(Link(panel).IsVisible);
        Assert.False(CodeRow(panel).IsVisible);
    }

    [AvaloniaFact]
    public void PressingAPickerButtonAnswersWithThatOption()
    {
        var previous = ChatPanel.PromptClock;
        var now = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        ChatPanel.PromptClock = () => now;
        try
        {
            var (panel, session) = Open(Picker() with { ArmedActivation = true });
            now += ChatPanel.PromptArmDelay;
            var button = ((Control)panel.FindControl<ItemsControl>("PromptOptions")!.ItemsPanelRoot!.Children[1])
                .GetVisualDescendants().OfType<Border>().First();

            Press(panel, button);
            Release(panel, button);

            Assert.Equal("2", Assert.Single(session.Answered).Key);
        }
        finally { ChatPanel.PromptClock = previous; }
    }

    [AvaloniaFact]
    public void TheUrlStepShowsTheLinkAndTheCodeFieldWithSendDisabled()
    {
        var (panel, _) = Open(UrlStep());

        Assert.True(Link(panel).IsVisible);
        Assert.Equal(Url, Link(panel).Text);
        Assert.True(CodeRow(panel).IsVisible);
        Assert.False(Send(panel).IsEnabled);
    }

    [AvaloniaFact]
    public void ClickingTheLinkOpensItThroughTheLinkHandler()
    {
        var opened = new List<string>();
        var previous = ChatLinks.Open;
        ChatLinks.Open = u => { opened.Add(u); return true; };
        try
        {
            var (panel, _) = Open(UrlStep());
            Press(panel, Link(panel));
            Assert.Equal(Url, Assert.Single(opened));
        }
        finally { ChatLinks.Open = previous; }
    }

    [AvaloniaFact]
    public void ClickingWithNoLinkOpensNothing()
    {
        var opened = new List<string>();
        var previous = ChatLinks.Open;
        ChatLinks.Open = u => { opened.Add(u); return true; };
        try
        {
            var (panel, _) = Open(Picker());
            Press(panel, Link(panel));
            Assert.Empty(opened);
        }
        finally { ChatLinks.Open = previous; }
    }

    [AvaloniaFact]
    public void SendEnablesOnlyForANonBlankCode()
    {
        var (panel, _) = Open(UrlStep());

        Box(panel).Text = "   ";
        Flush();
        Assert.False(Send(panel).IsEnabled);
        Box(panel).Text = "abc";
        Flush();
        Assert.True(Send(panel).IsEnabled);
    }

    [AvaloniaFact]
    public void EnterSubmitsTheCodeAndTheFieldIsEmptyBeforeTheSendIsAwaited()
    {
        var (panel, session) = Open(UrlStep());
        Box(panel).Text = "  CODE-123  ";

        Enter(Box(panel));

        Assert.Equal("CODE-123", Assert.Single(session.Codes));
        Assert.Equal("", session.FieldAtSend);
        Assert.Equal("", Box(panel).Text);
        Assert.False(Send(panel).IsEnabled);
    }

    [AvaloniaFact]
    public void ClickingSendSubmitsTheCode()
    {
        var (panel, session) = Open(UrlStep());
        Box(panel).Text = "xyz";

        Press(panel, Send(panel));

        Assert.Equal("xyz", Assert.Single(session.Codes));
        Assert.Equal("", Box(panel).Text);
    }

    [AvaloniaFact]
    public void AnOtherKeyOrABlankFieldSubmitsNothing()
    {
        var (panel, session) = Open(UrlStep());

        Enter(Box(panel));
        Box(panel).Text = "abc";
        Box(panel).RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A });

        Assert.Empty(session.Codes);
        Assert.Equal("abc", Box(panel).Text);
    }

    [AvaloniaFact]
    public void TheFieldIsEmptiedAndHiddenWhenTheScreenMovesOn()
    {
        var (panel, session) = Open(UrlStep());
        Box(panel).Text = "half-typed";

        session.Show(new ChatPrompt("OAuth error", new[] { new ChatPromptOption("Enter", "Try again") }));
        Flush();

        Assert.False(CodeRow(panel).IsVisible);
        Assert.False(Link(panel).IsVisible);
        Assert.Equal("", Box(panel).Text);
    }

    [AvaloniaFact]
    public void ClearingThePromptHidesTheLoginEntry()
    {
        var (panel, session) = Open(UrlStep());

        session.Show(null);
        Flush();

        Assert.False(CodeRow(panel).IsVisible);
        Assert.False(Link(panel).IsVisible);
    }

    // Terminal.app: readable, not pressable. The link is shown, there is no field.
    [AvaloniaFact]
    public void AnUnpressableRouteShowsTheLinkButNoField()
    {
        var (panel, _) = Open(UrlStep(code: false));

        Assert.True(Link(panel).IsVisible);
        Assert.False(CodeRow(panel).IsVisible);
        Assert.True(panel.FindControl<TextBlock>("PromptElsewhere")!.IsVisible);
    }

    // The control: a session that cannot take a code gets no field even if the prompt asks.
    [AvaloniaFact]
    public void ASessionThatCannotTakeACodeShowsNoField()
    {
        var session = new PlainSession();
        _toClean.Add(session.SessionId);
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), session);
        Flush();
        session.Show(UrlStep());
        Flush();

        Assert.False(CodeRow(ChatPanelTestAccess.Instance!).IsVisible);
        Assert.True(Link(ChatPanelTestAccess.Instance!).IsVisible);
    }

    // The fallback: an unreadable route has no options at all, and says so.
    [AvaloniaFact]
    public void AnUnreadableRouteStillShowsWaitingAndAnswerInTheTerminal()
    {
        var (panel, _) = Open(new ChatPrompt("Waiting for input", Array.Empty<ChatPromptOption>()));

        Assert.True(panel.FindControl<Border>("PromptBox")!.IsVisible);
        Assert.Equal("Waiting for input", panel.FindControl<TextBlock>("PromptTitle")!.Text);
        Assert.Equal("Answer in the terminal", panel.FindControl<TextBlock>("PromptElsewhere")!.Text);
        Assert.False(panel.FindControl<ItemsControl>("PromptOptions")!.IsVisible);
        Assert.False(Link(panel).IsVisible);
        Assert.False(CodeRow(panel).IsVisible);
    }

    private sealed class PlainSession : IRemoteChatSession, IRemoteChatPrompts
    {
        public string SessionId { get; } = "plain-" + Guid.NewGuid();
        public string DisplayName => "Plain";
        public RemoteChatState State => RemoteChatState.Connected;
        public IReadOnlyList<ChatTurn> History { get; } = new List<ChatTurn>();
        public event Action<ChatTurn>? TurnAdded { add { } remove { } }
        public event Action<ChatTurn>? TurnUpdated { add { } remove { } }
        public event Action<RemoteChatState>? StateChanged { add { } remove { } }
        public Task<ChatSendOutcome> SendAsync(string text) => Task.FromResult(ChatSendOutcome.Sent);
        public void Cancel() { }
        public ChatPrompt? Prompt { get; private set; }
        public event Action? PromptChanged;
        public void Show(ChatPrompt? p) { Prompt = p; PromptChanged?.Invoke(); }
        public Task AnswerAsync(ChatPromptOption option) => Task.CompletedTask;
        public void AnswerElsewhere() { }
    }
}
