using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Orbweaver.Tests;

// Clicking a link in a chat bubble (CB-284).
//
// Every click here is a pointer event raised on the bubble block, so it goes
// through SelectableTextBlock's own press and release handling — selection and
// capture included — and the handlers under test only ever see what a real
// click would hand them (see Mouse for why not through the window). The point clicked is
// worked out from the block's own text layout, so these cases say "the middle
// of the third letter of the link" rather than a pixel that happens to land
// there on one machine's font.
//
// What is NOT asserted: that the tooltip popup actually appears, or that a
// browser opens. The first needs a real pointer on a real window server; the
// second is CloudSessionLink.Open, which TestBootstrap replaces with a no-op
// for the whole assembly and these tests replace with a recorder.
[Collection("Settings")]
public class ChatPanelLinkTests : IDisposable
{
    private const string Url = "https://example.com/docs_page";
    private const string Bare = "https://example.org/a_(b)";

    private readonly List<string> _toClean = new();
    private readonly List<string> _opened = new();
    private readonly Func<string, bool> _previousOpener;

    public ChatPanelLinkTests()
    {
        _previousOpener = ChatLinks.Open;
        ChatLinks.Open = url =>
        {
            _opened.Add(url);
            return true;
        };
    }

    public void Dispose()
    {
        ChatLinks.Open = _previousOpener;
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    private static void FlushRender()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private ChatPanel Open(string text, Action<FakeChatSession>? arrange = null) =>
        Open(new ChatTurn { Role = ChatRole.Assistant, Text = text, IsComplete = true }, arrange);

    private ChatPanel Open(ChatTurn turn, Action<FakeChatSession>? arrange = null)
    {
        var id = "links-" + Guid.NewGuid();
        _toClean.Add(id);
        var fake = new FakeChatSession(new[] { turn }) { SessionId = id, DisplayName = "Link Session" };
        arrange?.Invoke(fake);

        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), fake);
        FlushRender();
        return ChatPanelTestAccess.Instance!;
    }

    private static string Rendered(TextBlock block)
    {
        if (!string.IsNullOrEmpty(block.Text)) return block.Text!;
        if (block.Inlines is null) return "";
        return string.Concat(block.Inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
    }

    private static SelectableTextBlock BlockSaying(ChatPanel panel, string text) =>
        panel.FindControl<ItemsControl>("Turns")!
            .GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .First(b => Rendered(b).Contains(text, StringComparison.Ordinal));

    // The centre of one character of a block, in the panel window's
    // coordinates — which is what the headless mouse takes. Offset into the
    // character by `along` (0..1) so a case can name its left or right half.
    private static Point At(ChatPanel panel, SelectableTextBlock block, int index, double along = 0.5)
    {
        var rect = block.TextLayout.HitTestTextRange(index, 1).First();
        var local = new Point(
            block.Padding.Left + rect.X + rect.Width * along,
            block.Padding.Top + rect.Y + rect.Height / 2);

        return block.TranslatePoint(local, panel)!.Value;
    }

    private static Point At(ChatPanel panel, SelectableTextBlock block, string text, int offset = 1, double along = 0.5) =>
        At(panel, block, Rendered(block).IndexOf(text, StringComparison.Ordinal) + offset, along);

    // The pointer events are raised on the bubble block itself rather than
    // through the window's mouse: under the null renderer this suite uses, no
    // glyph is ever drawn, so the window's hit test never lands on a TextBlock
    // at all (measured: it stops at the transcript's ScrollContentPresenter).
    // Raising on the block still runs SelectableTextBlock's own press, move
    // and release handling — selection and capture included — before the
    // panel's. The window-level route, real hit test and all, is exercised
    // once in tests/UiScreenshots, which draws through Skia.
    private sealed class Mouse(ChatPanel panel, SelectableTextBlock block)
    {
        private readonly Pointer _pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);

        // Each event is followed by a dispatcher flush, the way the real
        // dispatcher normally runs its layout pass before it hands over the
        // next pointer event; AClickFastEnoughToLandInOneFrameStillOpens is the
        // case that skips it on purpose.
        public void Down(Point p, MouseButton button = MouseButton.Left, int clicks = 1, bool flush = true)
        {
            block.RaiseEvent(new PointerPressedEventArgs(
                block, _pointer, panel, p, 0,
                new PointerPointProperties(
                    button switch
                    {
                        MouseButton.Left => RawInputModifiers.LeftMouseButton,
                        MouseButton.Middle => RawInputModifiers.MiddleMouseButton,
                        _ => RawInputModifiers.RightMouseButton,
                    },
                    button switch
                    {
                        MouseButton.Left => PointerUpdateKind.LeftButtonPressed,
                        MouseButton.Middle => PointerUpdateKind.MiddleButtonPressed,
                        _ => PointerUpdateKind.RightButtonPressed,
                    }),
                KeyModifiers.None, clicks));
            if (flush) Dispatcher.UIThread.RunJobs();
        }

        public void Move(Point p, bool dragging = false)
        {
            block.RaiseEvent(new PointerEventArgs(
                InputElement.PointerMovedEvent, block, _pointer, panel, p, 0,
                new PointerPointProperties(
                    dragging ? RawInputModifiers.LeftMouseButton : RawInputModifiers.None, PointerUpdateKind.Other),
                KeyModifiers.None));
            Dispatcher.UIThread.RunJobs();
        }

        public void Up(Point p, MouseButton button = MouseButton.Left)
        {
            block.RaiseEvent(new PointerReleasedEventArgs(
                block, _pointer, panel, p, 0,
                new PointerPointProperties(
                    RawInputModifiers.None,
                    button switch
                    {
                        MouseButton.Left => PointerUpdateKind.LeftButtonReleased,
                        MouseButton.Middle => PointerUpdateKind.MiddleButtonReleased,
                        _ => PointerUpdateKind.RightButtonReleased,
                    }),
                KeyModifiers.None, button));
            Dispatcher.UIThread.RunJobs();
        }

        public void Exit() =>
            block.RaiseEvent(new PointerEventArgs(
                InputElement.PointerExitedEvent, block, _pointer, panel, new Point(1, 1), 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
                KeyModifiers.None));

        public void Click(Point p, int clicks = 1, bool flushBetween = true)
        {
            Down(p, clicks: clicks, flush: flushBetween);
            Up(p);
        }
    }

    // --- opening ---

    [AvaloniaFact]
    public void ClickingAMarkdownLinkOpensItsDestinationOnce()
    {
        var panel = Open($"read [the docs]({Url}) first");
        var block = BlockSaying(panel, "the docs");

        new Mouse(panel, block).Click(At(panel, block, "the docs", offset: 3));

        Assert.Equal(new[] { Url }, _opened);
    }

    [AvaloniaFact]
    public void ClickingABareUrlOpensIt()
    {
        var panel = Open($"see {Bare}.");
        var block = BlockSaying(panel, Bare);

        new Mouse(panel, block).Click(At(panel, block, Bare, offset: 10));

        Assert.Equal(new[] { Bare }, _opened);
    }

    // Both halves of a click inside one frame, with no layout pass between
    // them: the press's selection reset has thrown the text layout away, and
    // the release has to rebuild it rather than hit-test an empty one.
    [AvaloniaFact]
    public void AClickFastEnoughToLandInOneFrameStillOpens()
    {
        var panel = Open($"read [the docs]({Url}) first");
        var block = BlockSaying(panel, "the docs");
        var p = At(panel, block, "the docs", offset: 3);

        new Mouse(panel, block).Click(p, flushBetween: false);

        Assert.Equal(new[] { Url }, _opened);
    }

    // A URL longer than the bubble is wide wraps, and every line of it is the
    // link. IsInside, which LinkUnder first used, is false on every line after
    // the first, so this case is the guard against that coming back. One click
    // per wrapped line, found by walking the URL's characters and keeping the
    // first on each new line.
    [AvaloniaFact]
    public void EveryLineOfAWrappedUrlOpensIt()
    {
        const string longUrl = "https://example.com/a/very/long/path/that/cannot/fit/on/one/line/of/a/bubble"
            + "/and/keeps/going/well/past/a/second/line/so/that/it/wraps/three/times";
        var panel = Open($"see {longUrl} now");
        var block = BlockSaying(panel, longUrl);
        var start = Rendered(block).IndexOf(longUrl, StringComparison.Ordinal);

        var perLine = new List<Point>();
        for (var i = 0; i < longUrl.Length; i++)
        {
            var p = At(panel, block, start + i);
            if (perLine.Count == 0 || p.Y > perLine[^1].Y + 1) perLine.Add(p);
        }

        Assert.True(perLine.Count >= 3, $"premise: the URL wraps onto three or more lines, got {perLine.Count}");

        var mouse = new Mouse(panel, block);
        foreach (var p in perLine) mouse.Click(p);

        Assert.Equal(Enumerable.Repeat(longUrl, perLine.Count), _opened);
    }

    // An emoji outside the BMP before a link is two UTF-16 units, and the
    // ranges and the layout have to count it the same way or every click
    // lands two characters off.
    [AvaloniaFact]
    public void ASurrogatePairBeforeALinkDoesNotShiftIt()
    {
        var panel = Open($"🚀🚀 [docs]({Url}) after");
        var block = BlockSaying(panel, "docs");
        var mouse = new Mouse(panel, block);

        mouse.Click(At(panel, block, "after", offset: 1));
        Assert.Empty(_opened);

        mouse.Click(At(panel, block, "docs", offset: 0, along: 0.3));
        mouse.Click(At(panel, block, "docs", offset: 3, along: 0.7));

        Assert.Equal(new[] { Url, Url }, _opened);
    }

    [AvaloniaFact]
    public void AnAutolinkOpensIt()
    {
        var panel = Open($"go to <{Url}> now");
        var block = BlockSaying(panel, Url);

        new Mouse(panel, block).Click(At(panel, block, Url, offset: 9));

        Assert.Equal(new[] { Url }, _opened);
    }

    // An opener that refuses — CloudSessionLink.Open returns false for
    // anything it will not launch — is the end of it, not an exception.
    [AvaloniaFact]
    public void AnOpenerThatRefusesIsNotAnError()
    {
        ChatLinks.Open = url => { _opened.Add(url); return false; };
        var panel = Open($"read [the docs]({Url}) first");
        var block = BlockSaying(panel, "the docs");

        new Mouse(panel, block).Click(At(panel, block, "the docs", offset: 3));

        Assert.Equal(new[] { Url }, _opened);
    }

    // Opening a link must not take focus from the composer, which ChatPanel.axaml
    // keeps as the only focusable thing in the window.
    [AvaloniaFact]
    public void ClickingALinkLeavesTheComposerFocused()
    {
        var panel = Open($"read [the docs]({Url}) first");
        var input = panel.FindControl<TextBox>("Input")!;
        input.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(input, panel.FocusManager?.GetFocusedElement());

        var block = BlockSaying(panel, "the docs");
        new Mouse(panel, block).Click(At(panel, block, "the docs", offset: 3));

        Assert.Equal(new[] { Url }, _opened);
        Assert.Same(input, panel.FocusManager?.GetFocusedElement());
    }

    // The suite's own guard: TestBootstrap swapped the real launcher out before
    // this class's constructor captured what it replaced. Were that line ever
    // dropped, every click test above would still pass against the recorder
    // while some other test opened a real browser.
    [AvaloniaFact]
    public void TheSuitesDefaultOpenerIsNotTheRealLauncher()
    {
        Func<string, bool> real = CloudSessionLink.Open;
        Assert.NotEqual(real.Method, _previousOpener.Method);
        Assert.False(_previousOpener(Url));
    }

    // Negative control for the case above: the same block, the same kind of
    // click, a few characters to the left.
    [AvaloniaFact]
    public void ClickingPlainTextBesideALinkOpensNothing()
    {
        var panel = Open($"read [the docs]({Url}) first");
        var block = BlockSaying(panel, "the docs");

        var mouse = new Mouse(panel, block);
        mouse.Click(At(panel, block, "read", offset: 1));
        mouse.Click(At(panel, block, "first", offset: 2));

        Assert.Empty(_opened);
    }

    // The edges, which is where TextPosition (character plus trailing length)
    // and the character itself disagree: the right half of the link's last
    // letter is the link, and the right half of the space before it is not.
    [AvaloniaFact]
    public void TheLinksEdgesAreExactlyItsOwnLetters()
    {
        var panel = Open($"read [docs]({Url}) first");
        var block = BlockSaying(panel, "docs");

        var mouse = new Mouse(panel, block);

        mouse.Click(At(panel, block, "docs", offset: -1, along: 0.9));
        Assert.Empty(_opened);

        mouse.Click(At(panel, block, "docs", offset: 4, along: 0.1));
        Assert.Empty(_opened);

        mouse.Click(At(panel, block, "docs", offset: 3, along: 0.9));
        Assert.Equal(new[] { Url }, _opened);
    }

    // Past the end of the line, HitTestPoint still answers with the nearest
    // character — the link's last one — so IsInside is what keeps a click in
    // the empty half of a bubble from opening it.
    [AvaloniaFact]
    public void ClickingTheEmptySpaceAfterALineEndingInALinkOpensNothing()
    {
        var panel = Open($"[docs]({Url})");
        var block = BlockSaying(panel, "docs");
        var last = At(panel, block, "docs", offset: 3);

        new Mouse(panel, block).Click(new Point(last.X + 60, last.Y));

        Assert.Empty(_opened);
    }

    [AvaloniaFact]
    public void AUrlInsideInlineCodeIsNotALink()
    {
        var panel = Open($"run `curl {Url}` now");
        var block = BlockSaying(panel, "curl");

        var ibeam = block.Cursor;
        var mouse = new Mouse(panel, block);
        mouse.Move(At(panel, block, Url, offset: 8));

        Assert.Null(ToolTip.GetTip(block));
        Assert.Same(ibeam, block.Cursor);

        mouse.Click(At(panel, block, Url, offset: 8));

        Assert.Empty(_opened);
        Assert.Null(block.Tag);
    }

    [AvaloniaFact]
    public void AUrlInsideAFencedBlockIsNotALink()
    {
        var panel = Open($"```\ncurl {Url}\n```");
        var block = BlockSaying(panel, "curl");

        new Mouse(panel, block).Click(At(panel, block, Url, offset: 8));

        Assert.Empty(_opened);
    }

    // A label the parser refuses to link is drawn as the plain text it is,
    // and opens nothing (Q2/"never a dead link").
    [AvaloniaFact]
    public void ARelativeLinkIsPlainTextAndOpensNothing()
    {
        var panel = Open("see [the findings](docs/x.md) here");
        var block = BlockSaying(panel, "the findings");

        new Mouse(panel, block).Click(At(panel, block, "the findings", offset: 4));

        Assert.Empty(_opened);
        Assert.Null(block.Tag);
    }

    // --- gestures that are not a click on a link ---

    [AvaloniaFact]
    public void PressingOnALinkAndReleasingOnPlainTextOpensNothing()
    {
        var panel = Open($"read [the docs]({Url}) first");
        var block = BlockSaying(panel, "the docs");

        var mouse = new Mouse(panel, block);
        mouse.Down(At(panel, block, "the docs", offset: 3));
        var away = At(panel, block, "first", offset: 2);
        mouse.Move(away, dragging: true);
        mouse.Up(away);

        Assert.Empty(_opened);
        Assert.False(string.IsNullOrEmpty(block.SelectedText));
    }

    [AvaloniaFact]
    public void PressingOnPlainTextAndReleasingOnALinkOpensNothing()
    {
        var panel = Open($"read [the docs]({Url}) first");
        var block = BlockSaying(panel, "the docs");

        var mouse = new Mouse(panel, block);
        mouse.Down(At(panel, block, "read", offset: 1));
        var onLink = At(panel, block, "the docs", offset: 3);
        mouse.Move(onLink, dragging: true);
        mouse.Up(onLink);

        Assert.Empty(_opened);
    }

    [AvaloniaFact]
    public void PressingOnOneLinkAndReleasingOnAnotherOpensNeither()
    {
        var panel = Open($"[one]({Url}) or {Bare}");
        var block = BlockSaying(panel, "one");

        var mouse = new Mouse(panel, block);
        mouse.Down(At(panel, block, "one", offset: 1));
        var other = At(panel, block, Bare, offset: 5);
        mouse.Move(other, dragging: true);
        mouse.Up(other);

        Assert.Empty(_opened);
    }

    // Press and release both on the one link, but dragged across it: that is
    // somebody selecting the URL to copy it, and the selection has to survive.
    [AvaloniaFact]
    public void DragSelectingWithinALinkSelectsAndOpensNothing()
    {
        var panel = Open($"see {Bare} now");
        var block = BlockSaying(panel, Bare);

        var from = At(panel, block, Bare, offset: 2);
        var to = At(panel, block, Bare, offset: Bare.Length - 3);
        var mouse = new Mouse(panel, block);
        mouse.Down(from);
        mouse.Move(to, dragging: true);
        mouse.Up(to);

        Assert.Empty(_opened);
        Assert.False(string.IsNullOrEmpty(block.SelectedText));
    }

    [AvaloniaFact]
    public void ARightOrMiddleClickOnALinkOpensNothing()
    {
        var panel = Open($"read [the docs]({Url}) first");
        var block = BlockSaying(panel, "the docs");
        var p = At(panel, block, "the docs", offset: 3);

        var mouse = new Mouse(panel, block);
        mouse.Down(p, MouseButton.Right);
        mouse.Up(p, MouseButton.Right);
        mouse.Down(p, MouseButton.Middle);
        mouse.Up(p, MouseButton.Middle);

        Assert.Empty(_opened);
    }

    // The second press of a double-click selects the word under it, so the
    // pair opens the page once — on the first click — and not twice.
    [AvaloniaFact]
    public void ADoubleClickOnALinkOpensItOnce()
    {
        var panel = Open($"read [the docs]({Url}) first");
        var block = BlockSaying(panel, "the docs");
        var p = At(panel, block, "the docs", offset: 5);

        var mouse = new Mouse(panel, block);
        mouse.Click(p, clicks: 1);
        mouse.Click(p, clicks: 2);

        Assert.Equal(new[] { Url }, _opened);
    }

    // --- hover ---

    [AvaloniaFact]
    public void HoveringALinkShowsAHandAndItsDestinationAndLeavingPutsThemBack()
    {
        // The label reads like one address and goes to another, which is the
        // case the tooltip exists for: it has to show where the click goes.
        var panel = Open($"try [example.net]({Url}) today");
        var block = BlockSaying(panel, "example.net");
        var ibeam = block.Cursor;

        var mouse = new Mouse(panel, block);
        mouse.Move(At(panel, block, "example.net", offset: 4));

        Assert.Equal(Url, ToolTip.GetTip(block));
        Assert.Same(ChatPanel.LinkCursor, block.Cursor);

        mouse.Move(At(panel, block, "today", offset: 2));

        Assert.Null(ToolTip.GetTip(block));
        Assert.Same(ibeam, block.Cursor);

        // Back onto the link, then off the block altogether.
        mouse.Move(At(panel, block, "example.net", offset: 4));
        Assert.Equal(Url, ToolTip.GetTip(block));

        // Moving along the same link changes nothing, and leaving the block
        // puts both back.
        mouse.Move(At(panel, block, "example.net", offset: 6));
        Assert.Same(ChatPanel.LinkCursor, block.Cursor);

        mouse.Exit();

        Assert.Null(ToolTip.GetTip(block));
        Assert.Same(ibeam, block.Cursor);
    }

    // --- every mode ---

    [AvaloniaFact]
    public void ALinkOpensInAReadOnlyPanel()
    {
        var panel = Open($"read [the docs]({Url}) first", fake => fake.IsReadOnly = true);
        var block = BlockSaying(panel, "the docs");

        new Mouse(panel, block).Click(At(panel, block, "the docs", offset: 3));

        Assert.Equal(new[] { Url }, _opened);
    }

    [AvaloniaFact]
    public void ALinkOpensInARoom()
    {
        var panel = Open($"read [the docs]({Url}) first", fake => fake.IsRoom = true);
        var block = BlockSaying(panel, "the docs");

        new Mouse(panel, block).Click(At(panel, block, "the docs", offset: 3));

        Assert.Equal(new[] { Url }, _opened);
    }

    // A streaming reply rebuilds its blocks as text arrives. A link that was
    // half-written a moment ago has to be clickable once it is whole, on the
    // new block rather than a stale one — and when the text changes again, the
    // same spot opens the new destination, never the old one.
    [AvaloniaFact]
    public void ALinkFinishedByAStreamingUpdateIsClickable()
    {
        const string later = "https://example.net/second";
        var turn = new ChatTurn { Role = ChatRole.Assistant, Text = "read [the docs](https://exa" };
        var panel = Open(turn);

        turn.Text = $"read [the docs]({Url}) first";
        FlushRender();

        var block = BlockSaying(panel, "the docs");
        new Mouse(panel, block).Click(At(panel, block, "the docs", offset: 3));

        turn.Text = $"read [the docs]({later}) first";
        turn.IsComplete = true;
        FlushRender();

        var rebuilt = BlockSaying(panel, "the docs");
        Assert.NotSame(block, rebuilt);
        new Mouse(panel, rebuilt).Click(At(panel, rebuilt, "the docs", offset: 3));

        Assert.Equal(new[] { Url, later }, _opened);
    }

}
