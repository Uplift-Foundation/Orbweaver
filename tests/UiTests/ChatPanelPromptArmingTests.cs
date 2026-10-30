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

// CB-270 Stage 6: an approval's options are armed, not instant. They ignore
// presses for a moment after the prompt first shows or its content changes, and
// answer on release over the same option that was pressed. A permission bar read
// off a terminal is not marked and answers on a press as it always has.
[Collection("Settings")]
public class ChatPanelPromptArmingTests : IDisposable
{
    private readonly List<string> _toClean = new();
    private DateTime _now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
    private readonly Func<DateTime> _previousClock = ChatPanel.PromptClock;

    public ChatPanelPromptArmingTests() => ChatPanel.PromptClock = () => _now;

    public void Dispose()
    {
        ChatPanel.PromptClock = _previousClock;
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class PromptSession : IRemoteChatSession, IRemoteChatPrompts
    {
        public string SessionId { get; } = "prompt-arm-" + Guid.NewGuid();
        public string DisplayName => "Prompt";
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

        public void Show(ChatPrompt prompt)
        {
            Prompt = prompt;
            PromptChanged?.Invoke();
        }

        public Task AnswerAsync(ChatPromptOption option)
        {
            Answered.Add(option);
            return Task.CompletedTask;
        }

        public void AnswerElsewhere() { }
    }

    private ChatPrompt Approval(string id, string title = "Nova wants to run: true") =>
        new(title, OpenClawApprovalText.Options(id), ArmedActivation: true);

    private (ChatPanel Panel, PromptSession Session) Open(ChatPrompt prompt)
    {
        var session = new PromptSession();
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

    private static Pointer NewPointer() => new(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);

    private static void Press(ChatPanel panel, Border option)
    {
        var at = option.TranslatePoint(new Point(2, 2), panel) ?? new Point(2, 2);
        option.RaiseEvent(new PointerPressedEventArgs(
            option, NewPointer(), panel, at, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
    }

    private static void Release(ChatPanel panel, Border option, Point? inside = null)
    {
        var at = option.TranslatePoint(inside ?? new Point(2, 2), panel) ?? new Point(2, 2);
        option.RaiseEvent(new PointerReleasedEventArgs(
            option, NewPointer(), panel, at, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
    }

    // Too soon: the same press and release that answers later answers nothing now.
    [AvaloniaFact]
    public void APressAndReleaseBeforeTheDelayAnswersNothing()
    {
        var (panel, session) = Open(Approval("ap-1"));
        _now += ChatPanel.PromptArmDelay - TimeSpan.FromMilliseconds(1);

        Press(panel, Option(panel, 0));
        Release(panel, Option(panel, 0));

        Assert.Empty(session.Answered);
    }

    [AvaloniaFact]
    public void AfterTheDelayAReleaseOverTheSameOptionAnswersItOnce()
    {
        var (panel, session) = Open(Approval("ap-1"));
        _now += ChatPanel.PromptArmDelay;

        Press(panel, Option(panel, 0));
        Assert.Empty(session.Answered); // a press alone answers nothing
        Release(panel, Option(panel, 0));

        var answered = Assert.Single(session.Answered);
        Assert.Equal("deny", answered.Key);
        Assert.Equal("ap-1", answered.ApprovalId);
    }

    [AvaloniaFact]
    public void AReleaseOnADifferentOptionThanWasPressedAnswersNothing()
    {
        var (panel, session) = Open(Approval("ap-1"));
        _now += ChatPanel.PromptArmDelay;

        Press(panel, Option(panel, 0));
        Release(panel, Option(panel, 1));

        Assert.Empty(session.Answered);
    }

    [AvaloniaFact]
    public void AReleaseWithNoPressAnswersNothing()
    {
        var (panel, session) = Open(Approval("ap-1"));
        _now += ChatPanel.PromptArmDelay;

        Release(panel, Option(panel, 1));

        Assert.Empty(session.Answered);
    }

    // The pointer is captured by the pressed option, so a release elsewhere is
    // still delivered to it; it has to be over it.
    [AvaloniaFact]
    public void AReleaseOutsideTheOptionAnswersNothing()
    {
        var (panel, session) = Open(Approval("ap-1"));
        _now += ChatPanel.PromptArmDelay;

        Press(panel, Option(panel, 0));
        Release(panel, Option(panel, 0), inside: new Point(-30, -30));

        Assert.Empty(session.Answered);
    }

    // A new request replacing the one on screen restarts the clock: a click aimed
    // at the old one cannot carry over to the new.
    [AvaloniaFact]
    public void ANewRequestRestartsTheDelayAndDropsAPressInFlight()
    {
        var (panel, session) = Open(Approval("ap-1"));
        _now += TimeSpan.FromSeconds(5);
        Press(panel, Option(panel, 0));

        session.Show(Approval("ap-2", "Nova wants to run: ls"));
        Flush();
        Release(panel, Option(panel, 0));
        Assert.Empty(session.Answered);

        // And a fresh press is held to the new clock too.
        Press(panel, Option(panel, 0));
        Release(panel, Option(panel, 0));
        Assert.Empty(session.Answered);

        _now += ChatPanel.PromptArmDelay;
        Press(panel, Option(panel, 0));
        Release(panel, Option(panel, 0));
        Assert.Equal("ap-2", Assert.Single(session.Answered).ApprovalId);
    }

    // The same request shown again (nothing about it changed) does not re-arm.
    [AvaloniaFact]
    public void TheSamePromptRaisedAgainDoesNotRestartTheDelay()
    {
        var (panel, session) = Open(Approval("ap-1"));
        _now += ChatPanel.PromptArmDelay;

        session.Show(Approval("ap-1"));
        Flush();
        Press(panel, Option(panel, 0));
        Release(panel, Option(panel, 0));

        Assert.Single(session.Answered);
    }

    // The pin the other way: a permission bar read off a terminal is not marked
    // deliberate and answers on a press, immediately, as it always has.
    [AvaloniaFact]
    public void ANonDeliberatePromptStillAnswersOnAPressAtOnce()
    {
        var (panel, session) = Open(new ChatPrompt("Allow this tool call?", new[]
        {
            new ChatPromptOption("1", "Yes"),
            new ChatPromptOption("2", "No"),
        }));

        Press(panel, Option(panel, 0));

        Assert.Equal("1", Assert.Single(session.Answered).Key);
    }

    // A prompt that goes away clears what the arming remembered.
    [AvaloniaFact]
    public void APromptThatWentAwayLeavesNothingToRelease()
    {
        var (panel, session) = Open(Approval("ap-1"));
        _now += ChatPanel.PromptArmDelay;
        var option = Option(panel, 0);
        Press(panel, option);

        session.Show(null!);
        Flush();
        Release(panel, option);

        Assert.Empty(session.Answered);
    }

    // No keyboard path reaches an answer: the options are not focusable, so there
    // is no default focus for a stray Enter to land on, and a key on one does
    // nothing, armed or not.
    [AvaloniaFact]
    public void NoKeyAnswersAnOptionAndTheOptionsAreNotFocusable()
    {
        var (panel, session) = Open(Approval("ap-1"));
        _now += ChatPanel.PromptArmDelay;

        foreach (var index in new[] { 0, 1 })
        {
            var option = Option(panel, index);
            Assert.False(option.Focusable);
            foreach (var key in new[] { Key.Enter, Key.Space, Key.Y, Key.A })
            {
                option.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
            }
        }

        Assert.Empty(session.Answered);
    }
}

