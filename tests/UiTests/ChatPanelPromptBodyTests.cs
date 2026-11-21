using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UiTests;

// CB-400: a prompt that carries a Body, the rows of a screen that is not a question
// (the /mobile QR overlay), drawn in the panel with one Close button. The body is
// drawn the way the terminal draws it, which is what makes a QR a QR: monospace, never
// wrapped, rows that touch, on a white card. A prompt without a body must show none
// of it, because every ordinary permission bar goes through the same code.
[Collection("Settings")]
public class ChatPanelPromptBodyTests : IDisposable
{
    private readonly List<string> _toClean = new();

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);

        // This class moves the process-wide scale for one case; put it back for the next class.
        OrbweaverSettings.ChatTextScale = ChatZoom.Default;
        ChatPanel.ReapplyTextScale();
        Flush();
    }

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class PromptSession : IRemoteChatSession, IRemoteChatPrompts
    {
        public string SessionId { get; } = "prompt-body-" + Guid.NewGuid();
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

        public void AnswerElsewhere() { }
    }

    private static readonly string[] QrRows =
    {
        "Get the Claude app on your phone",
        "",
        "Scan the QR code, or visit claude.ai/mobile",
        "",
        "    ▄▄▄▄▄ ▄▄▄ ▄▄▄▄▄",
        "    █ ▄▄▄ █ ▀█ █ ▄▄▄ █",
        "    █ ███ █ ▄▀ █ ███ █",
    };

    private static ChatPrompt Overlay(IReadOnlyList<string>? body = null) =>
        new("Get the Claude app on your phone",
            new[] { new ChatPromptOption(OverlayScreen.CloseKey, OverlayScreen.CloseLabel) },
            Body: body ?? QrRows);

    private (ChatPanel Panel, PromptSession Session) Open(ChatPrompt? prompt)
    {
        var session = new PromptSession();
        _toClean.Add(session.SessionId);
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), session);
        Flush();
        session.Show(prompt);
        Flush();
        return (ChatPanelTestAccess.Instance!, session);
    }

    private static TextBlock BodyText(ChatPanel panel) => panel.FindControl<TextBlock>("PromptBody")!;
    private static Border BodyCard(ChatPanel panel) => panel.FindControl<Border>("PromptBodyCard")!;

    private static Border Option(ChatPanel panel, int index) =>
        ((Control)panel.FindControl<ItemsControl>("PromptOptions")!.ItemsPanelRoot!.Children[index])
            .GetVisualDescendants().OfType<Border>().First();

    private static void Click(ChatPanel panel, Border option)
    {
        var at = option.TranslatePoint(new Point(2, 2), panel) ?? new Point(2, 2);
        option.RaiseEvent(new PointerPressedEventArgs(
            option, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true), panel, at, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
    }

    [AvaloniaFact]
    public void ABodyIsDrawnAsOneUnwrappedMonospaceBlockOnAWhiteCard()
    {
        var (panel, _) = Open(Overlay());

        var text = BodyText(panel);
        Assert.True(BodyCard(panel).IsVisible);
        Assert.Equal(string.Join("\n", QrRows), text.Text);
        Assert.Equal(ChatPanel.MonoFamily, text.FontFamily);
        Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);

        // Rows that touch: the line height is the font size, not the font's own.
        Assert.Equal(text.FontSize, text.LineHeight);

        // Standard QR polarity plus a quiet zone: dark modules on a white card.
        Assert.Equal(Colors.White, ((ISolidColorBrush)BodyCard(panel).Background!).Color);
        Assert.Equal(new Thickness(8), BodyCard(panel).Padding);
        Assert.NotEqual(Colors.White, ((ISolidColorBrush)text.Foreground!).Color);
    }

    [AvaloniaFact]
    public void TheBodyScrollsBothWaysInsteadOfWrapping()
    {
        var (panel, _) = Open(Overlay());

        var scroll = panel.FindControl<ScrollViewer>("PromptBodyScroll")!;
        Assert.Equal(ScrollBarVisibility.Auto, scroll.HorizontalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
    }

    [AvaloniaFact]
    public void ClosingIsOneButtonAndOneClickAnswersWithEscapeExactlyOnce()
    {
        var (panel, session) = Open(Overlay());

        Assert.Single(panel.FindControl<ItemsControl>("PromptOptions")!.ItemsPanelRoot!.Children);
        Assert.Contains(Option(panel, 0).GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Close");

        Click(panel, Option(panel, 0));

        var answered = Assert.Single(session.Answered);
        Assert.Equal(OverlayScreen.CloseKey, answered.Key);
        Assert.Equal("Escape", answered.Key);
    }

    // A real tall screen (/mcp, 50 rows by 100 columns): the body is bounded and scrolls, and
    // Close is inside the panel without scrolling it.
    [AvaloniaFact]
    public void ATallScreenIsBoundedAndCloseStaysOnScreen()
    {
        var body = TallOverlayCapture.McpBody();
        Assert.True(body.Count >= 40);
        var (panel, _) = Open(new ChatPrompt("MCP servers",
            new[] { new ChatPromptOption(OverlayScreen.CloseKey, OverlayScreen.CloseLabel) }, Body: body));
        Flush();

        var scroll = panel.FindControl<ScrollViewer>("PromptBodyScroll")!;
        Assert.True(scroll.Bounds.Height <= 180.5, $"body height {scroll.Bounds.Height}");
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "the 50 rows should overflow the bound and scroll");

        var close = Option(panel, 0);
        var top = close.TranslatePoint(new Point(0, 0), panel)!.Value;
        var bottom = close.TranslatePoint(new Point(0, close.Bounds.Height), panel)!.Value;
        Assert.True(top.Y >= 0 && bottom.Y <= panel.Bounds.Height,
            $"Close spans {top.Y}..{bottom.Y} in a panel {panel.Bounds.Height} tall");
        Assert.True(close.Bounds.Width > 0 && close.IsEffectivelyVisible);

        // A QR-sized body is not clipped by the bound: 21 rows at the body's line height.
        Assert.True(21 * BodyText(panel).LineHeight <= scroll.MaxHeight);
    }

    [AvaloniaFact]
    public void ClearingThePromptHidesAndEmptiesTheBody()
    {
        var (panel, session) = Open(Overlay());
        Assert.True(BodyCard(panel).IsVisible);

        session.Show(null);
        Flush();

        Assert.False(BodyCard(panel).IsVisible);
        Assert.Null(BodyText(panel).Text);
        Assert.False(panel.FindControl<Border>("PromptBox")!.IsVisible);
    }

    [AvaloniaFact]
    public void AnOrdinaryPromptShowsNoBodyEvenAfterAnOverlay()
    {
        var (panel, session) = Open(Overlay());

        session.Show(new ChatPrompt("Run it?", new[] { new ChatPromptOption("1", "Yes") }));
        Flush();

        Assert.True(panel.FindControl<Border>("PromptBox")!.IsVisible);
        Assert.False(BodyCard(panel).IsVisible);
        Assert.Null(BodyText(panel).Text);
    }

    // The control for the control above: a panel that never saw an overlay.
    [AvaloniaFact]
    public void AnOrdinaryPromptOnAFreshPanelShowsNoBody()
    {
        var (panel, _) = Open(new ChatPrompt("Run it?", new[] { new ChatPromptOption("1", "Yes") }));

        Assert.False(BodyCard(panel).IsVisible);
    }

    // The body is part of what is being asked: a changed QR under the same title
    // is a different screen, so the arming clock must restart for it.
    [AvaloniaFact]
    public void AChangedBodyIsADifferentPromptForTheArmingClock()
    {
        var now = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        var previous = ChatPanel.PromptClock;
        ChatPanel.PromptClock = () => now;
        try
        {
            var (panel, session) = Open(Overlay() with { ArmedActivation = true });
            now += ChatPanel.PromptArmDelay;

            session.Show(Overlay(new[] { "Get the Claude app on your phone", "different" }) with { ArmedActivation = true });
            Flush();

            // Pressed after the arming window of the first, but inside that of the second.
            Click(panel, Option(panel, 0));
            var release = Option(panel, 0);
            release.RaiseEvent(new PointerReleasedEventArgs(
                release, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true), panel,
                release.TranslatePoint(new Point(2, 2), panel) ?? new Point(2, 2), 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                KeyModifiers.None, MouseButton.Left));

            Assert.Empty(session.Answered);
        }
        finally { ChatPanel.PromptClock = previous; }
    }

    [AvaloniaFact]
    public void TheBodyScalesWithTheChatTextAndKeepsItsRowsTouching()
    {
        var (panel, _) = Open(Overlay());
        var before = BodyText(panel).FontSize;

        OrbweaverSettings.ChatTextScale = 1.5;
        ChatPanel.ReapplyTextScale();
        Flush();

        var text = BodyText(panel);
        Assert.True(text.FontSize > before);
        Assert.Equal(text.FontSize, text.LineHeight);
    }

    // The panel is hidden and shown again with a prompt up: a hidden panel must not keep a QR.
    [AvaloniaFact]
    public void HidingThePanelClearsTheBody()
    {
        var (panel, session) = Open(Overlay());

        ChatPanel.HideFor(session.SessionId);
        Flush();

        Assert.False(BodyCard(panel).IsVisible);
        Assert.Null(BodyText(panel).Text);
    }
}
