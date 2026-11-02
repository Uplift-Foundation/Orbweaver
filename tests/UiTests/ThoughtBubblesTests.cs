using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-302: ThoughtBubbles (who owns the one open bubble, and when it opens and
// closes), its wiring into OrbWindow and AccountOrbWindow, and the flyout's
// real-cursor watch. StaleHoverRegressionTests holds the pins that fail on the
// old code; this file covers the rest of what the fix added.
//
// Every test leaves the process-wide state as it found it: no bubble open, no
// watch running, and CursorProbe back to "unknown".
[Collection("Settings")]
public class ThoughtBubblesTests : IDisposable
{
    private readonly List<Window> _shown = new();

    public void Dispose()
    {
        CursorProbe.Read = () => null;
        if (ThoughtBubbles.Open is { } open) ThoughtBubbles.Hide(open);
        foreach (var w in _shown)
        {
            if (w is OrbWindow orb) orb.HideFlyout();
            w.Hide();
        }
    }

    private static SessionStatus Status(string title) => new()
    {
        Source = SessionSource.ClaudeCode,
        State = "idle",
        Title = title,
        Cwd = "/Users/user/" + title,
    };

    private OrbWindow ShownOrb(string title, int x, int y = 100)
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status(title));
        orb.Show();
        orb.Position = new PixelPoint(x, y);
        ToolTip.SetShowDelay(orb.Root, 20);
        _shown.Add(orb);
        Flush();
        return orb;
    }

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Raise(Control target, RoutedEvent routedEvent)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        target.RaiseEvent(new PointerEventArgs(
            routedEvent, target, pointer, target, new Point(10, 10), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
            KeyModifiers.None));
    }

    // A right-button press, so OrbWindow's own left-button drag handling stays
    // out of it; the service closed tips on any button, and so does this.
    private static void RightPress(Control target)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        target.RaiseEvent(new PointerPressedEventArgs(
            target, pointer, target, new Point(10, 10), 0,
            new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed),
            KeyModifiers.None));
    }

    private static PixelPoint Inside(Window w) => w.Position + new PixelVector(5, 5);
    private static PixelPoint FarAway => new(5000, 5000);

    // --- the service is off for the orb, on for its menu ------------------------

    [AvaloniaFact]
    public void AnOrbIsOffTheTooltipServiceAndItsMenuIsBackOnIt()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());

        Assert.False(ToolTip.GetServiceEnabled(orb));
        // The control for the override: Root inherits the window's false, so the
        // menu's true below is the explicit re-enable and not a default.
        Assert.False(ToolTip.GetServiceEnabled(orb.Root));
        Assert.True(ToolTip.GetServiceEnabled(orb.Root.ContextMenu!));
        Assert.True(ToolTip.GetServiceEnabled(orb.EndSessionItem));
        Assert.True(ToolTip.GetServiceEnabled(orb.ResetIdleItem));
    }

    [AvaloniaFact]
    public void AnAccountOrbIsOffTheTooltipServiceAndItsMenuIsBackOnIt()
    {
        var orb = new AccountOrbWindow("cb302");

        Assert.False(ToolTip.GetServiceEnabled(orb));
        Assert.False(ToolTip.GetServiceEnabled(orb.Root));
        Assert.True(ToolTip.GetServiceEnabled(orb.Root.ContextMenu!));
    }

    // A window with no context menu at all is still taken off the service.
    [AvaloniaFact]
    public void AttachingAnAnchorWithNoMenuStillDisablesTheService()
    {
        var anchor = new Border();
        var window = new Window { Content = anchor };

        ThoughtBubbles.Attach(window, anchor);

        Assert.False(ToolTip.GetServiceEnabled(window));
        Assert.Null(anchor.ContextMenu);
    }

    // --- one bubble at a time -----------------------------------------------------

    [AvaloniaFact]
    public void ShowingASecondBubbleClosesTheFirst()
    {
        var a = ShownOrb("alpha", 100);
        var b = ShownOrb("bravo", 400);

        ThoughtBubbles.Show(a.Root);
        Assert.True(ToolTip.GetIsOpen(a.Root));

        ThoughtBubbles.Show(b.Root);

        Assert.False(ToolTip.GetIsOpen(a.Root));
        Assert.True(ToolTip.GetIsOpen(b.Root));
        Assert.Same(b.Root, ThoughtBubbles.Open);
    }

    // A late exit from an orb that no longer owns the bubble closes nothing.
    [AvaloniaFact]
    public void HidingAnAnchorThatIsNotOpenDoesNothing()
    {
        var a = ShownOrb("alpha", 100);
        var b = ShownOrb("bravo", 400);
        ThoughtBubbles.Show(b.Root);

        ThoughtBubbles.Hide(a.Root);

        Assert.True(ToolTip.GetIsOpen(b.Root));
        Assert.True(ThoughtBubbles.Watching);
    }

    [AvaloniaFact]
    public void HidingTheOpenBubbleClosesItAndStopsTheWatch()
    {
        var a = ShownOrb("alpha", 100);
        ThoughtBubbles.Show(a.Root);
        Assert.True(ThoughtBubbles.Watching);

        ThoughtBubbles.Hide(a.Root);

        Assert.False(ToolTip.GetIsOpen(a.Root));
        Assert.Null(ThoughtBubbles.Open);
        Assert.False(ThoughtBubbles.Watching);
    }

    // An orb that has not had its first UpdateFrom has no Tip, and IsOpen
    // refuses itself; nothing is recorded as open and no watch is left running.
    [AvaloniaFact]
    public void ShowingAnAnchorWithNoTipOpensNothing()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());

        ThoughtBubbles.Show(orb.Root);

        Assert.Null(ThoughtBubbles.Open);
        Assert.False(ToolTip.GetIsOpen(orb.Root));
        Assert.False(ThoughtBubbles.Watching);
    }

    // --- hover opens after the delay, and leaving, pressing or hiding closes ------

    [AvaloniaFact]
    public void EnteringOpensTheBubbleOnceTheDelayElapses()
    {
        var orb = ShownOrb("alpha", 100);

        Raise(orb.Root, InputElement.PointerEnteredEvent);
        Assert.False(ToolTip.GetIsOpen(orb.Root));   // not before the delay

        StaleHoverRegressionTests.PumpUntil(() => ToolTip.GetIsOpen(orb.Root));

        Assert.True(ToolTip.GetIsOpen(orb.Root));
        Assert.Same(orb.Root, ThoughtBubbles.Open);
    }

    [AvaloniaFact]
    public void LeavingBeforeTheDelayMeansItNeverOpens()
    {
        var orb = ShownOrb("alpha", 100);

        Raise(orb.Root, InputElement.PointerEnteredEvent);
        Raise(orb.Root, InputElement.PointerExitedEvent);
        StaleHoverRegressionTests.PumpFor(120);

        Assert.False(ToolTip.GetIsOpen(orb.Root));
    }

    // The delay elapsing is not enough when the real cursor is somewhere else.
    [AvaloniaFact]
    public void TheDelayElapsingWithTheCursorElsewhereOpensNothing()
    {
        var orb = ShownOrb("alpha", 100);
        CursorProbe.Read = () => FarAway;

        Raise(orb.Root, InputElement.PointerEnteredEvent);
        StaleHoverRegressionTests.PumpFor(120);

        Assert.False(ToolTip.GetIsOpen(orb.Root));
    }

    [AvaloniaFact]
    public void LeavingClosesAnOpenBubble()
    {
        var orb = ShownOrb("alpha", 100);
        ThoughtBubbles.Show(orb.Root);

        Raise(orb.Root, InputElement.PointerExitedEvent);

        Assert.False(ToolTip.GetIsOpen(orb.Root));
    }

    [AvaloniaFact]
    public void PressingClosesAnOpenBubble()
    {
        var orb = ShownOrb("alpha", 100);
        ThoughtBubbles.Show(orb.Root);

        RightPress(orb.Root);

        Assert.False(ToolTip.GetIsOpen(orb.Root));
        Assert.Null(ThoughtBubbles.Open);
    }

    [AvaloniaFact]
    public void HidingTheWindowClosesItsBubble()
    {
        var orb = ShownOrb("alpha", 100);
        ThoughtBubbles.Show(orb.Root);

        orb.Hide();

        Assert.False(ToolTip.GetIsOpen(orb.Root));
        Assert.Null(ThoughtBubbles.Open);
    }

    // Any other property change on the window leaves the bubble alone.
    [AvaloniaFact]
    public void MovingTheWindowLeavesItsBubbleOpen()
    {
        var orb = ShownOrb("alpha", 100);
        ThoughtBubbles.Show(orb.Root);

        orb.Opacity = 0.5;

        Assert.True(ToolTip.GetIsOpen(orb.Root));
        orb.Opacity = 1;
    }

    [AvaloniaFact]
    public void ClosingTheWindowClosesItsBubble()
    {
        var orb = new AccountOrbWindow("cb302-close");
        ToolTip.SetTip(orb.Root, "account");
        orb.Show();
        Flush();
        ThoughtBubbles.Show(orb.Root);
        Assert.Same(orb.Root, ThoughtBubbles.Open);

        orb.Close();

        Assert.Null(ThoughtBubbles.Open);
    }

    // The account orb's wiring, through a real hover rather than Show directly.
    [AvaloniaFact]
    public void AnAccountOrbOpensOnHoverAndClosesOnLeave()
    {
        var orb = new AccountOrbWindow("cb302-hover");
        ToolTip.SetTip(orb.Root, "account");
        ToolTip.SetShowDelay(orb.Root, 20);
        orb.Show();
        _shown.Add(orb);
        Flush();

        Raise(orb.Root, InputElement.PointerEnteredEvent);
        StaleHoverRegressionTests.PumpUntil(() => ToolTip.GetIsOpen(orb.Root));
        Assert.True(ToolTip.GetIsOpen(orb.Root));

        Raise(orb.Root, InputElement.PointerExitedEvent);
        Assert.False(ToolTip.GetIsOpen(orb.Root));
    }

    // --- the watch ------------------------------------------------------------------

    [AvaloniaFact]
    public void TheWatchKeepsABubbleOpenWhileTheCursorIsOnTheOrb()
    {
        var orb = ShownOrb("alpha", 100);
        ThoughtBubbles.Show(orb.Root);
        CursorProbe.Read = () => Inside(orb);

        ThoughtBubbles.OnWatchTick();

        Assert.True(ToolTip.GetIsOpen(orb.Root));
    }

    // The missed leave: nothing told the orb the pointer went, the cursor did.
    [AvaloniaFact]
    public void TheWatchClosesABubbleOnceTheCursorHasLeft()
    {
        var orb = ShownOrb("alpha", 100);
        ThoughtBubbles.Show(orb.Root);
        CursorProbe.Read = () => FarAway;

        ThoughtBubbles.OnWatchTick();

        Assert.False(ToolTip.GetIsOpen(orb.Root));
        Assert.False(ThoughtBubbles.Watching);
    }

    // With no cursor to read it falls back to IsPointerOver, both ways.
    [AvaloniaFact]
    public void WithNoCursorTheWatchKeepsABubbleThePointerIsOver()
    {
        var orb = ShownOrb("alpha", 100);
        Raise(orb.Root, InputElement.PointerEnteredEvent);
        ThoughtBubbles.Show(orb.Root);

        ThoughtBubbles.OnWatchTick();

        Assert.True(ToolTip.GetIsOpen(orb.Root));
    }

    [AvaloniaFact]
    public void WithNoCursorTheWatchClosesABubbleThePointerIsNotOver()
    {
        var orb = ShownOrb("alpha", 100);
        Assert.False(orb.Root.IsPointerOver);
        ThoughtBubbles.Show(orb.Root);

        ThoughtBubbles.OnWatchTick();

        Assert.False(ToolTip.GetIsOpen(orb.Root));
    }

    // The real timer reaches the tick on its own.
    [AvaloniaFact]
    public void TheRealWatchTimerClosesAStrandedBubble()
    {
        var orb = ShownOrb("alpha", 100);
        ThoughtBubbles.Show(orb.Root);
        CursorProbe.Read = () => FarAway;

        StaleHoverRegressionTests.PumpUntil(() => !ToolTip.GetIsOpen(orb.Root), timeoutMs: 1000);

        Assert.False(ToolTip.GetIsOpen(orb.Root));
    }

    [AvaloniaFact]
    public void ATickWithNothingOpenStopsTheWatch()
    {
        ThoughtBubbles.OnWatchTick();

        Assert.False(ThoughtBubbles.Watching);
        Assert.Null(ThoughtBubbles.Open);
    }

    // An anchor that has left its window has nothing to measure against.
    [AvaloniaFact]
    public void ATickForAnAnchorNoLongerInAWindowClosesIt()
    {
        var anchor = new Border();
        ToolTip.SetTip(anchor, "detached");
        var window = new Window { Content = anchor, Width = 80, Height = 80 };
        window.Show();
        _shown.Add(window);
        Flush();
        ThoughtBubbles.Show(anchor);
        Assert.Same(anchor, ThoughtBubbles.Open);

        window.Content = null;
        Flush();
        ThoughtBubbles.OnWatchTick();

        Assert.Null(ThoughtBubbles.Open);
    }

    // --- FrameOf ----------------------------------------------------------------------

    [AvaloniaFact]
    public void AWindowsFrameIsItsPositionAndItsSize()
    {
        var orb = ShownOrb("alpha", 300, 250);

        var frame = RealHover.FrameOf(orb);

        Assert.Equal(new PixelPoint(300, 250), frame.Position);
        Assert.Equal((int)Math.Round(orb.Bounds.Width), frame.Width);
        Assert.Equal((int)Math.Round(orb.Bounds.Height), frame.Height);
        Assert.True(frame.Width > 0 && frame.Height > 0);
    }

    // --- the flyout watch ---------------------------------------------------------------

    [AvaloniaFact]
    public void ShowingTheFlyoutStartsItsWatchAndHidingItStopsIt()
    {
        var orb = ShownOrb("alpha", 100);

        orb.EnsureFlyoutShown();
        Assert.True(orb.FlyoutWatching);

        orb.HideFlyout();
        Assert.False(orb.FlyoutWatching);
    }

    [AvaloniaFact]
    public void AWatchTickWithTheFlyoutGoneStopsTheWatch()
    {
        var orb = ShownOrb("alpha", 100);
        orb.EnsureFlyoutShown();
        orb.Flyout!.Hide();

        orb.OnFlyoutWatchTick(null, EventArgs.Empty);

        Assert.False(orb.FlyoutWatching);
    }

    [AvaloniaFact]
    public void AWatchTickWithTheCursorOnTheOrbOrTheFlyoutChangesNothing()
    {
        var orb = ShownOrb("alpha", 100);
        orb.EnsureFlyoutShown();

        CursorProbe.Read = () => Inside(orb);
        orb.OnFlyoutWatchTick(null, EventArgs.Empty);
        Assert.Null(HideTimerOf(orb));

        CursorProbe.Read = () => Inside(orb.Flyout!);
        orb.OnFlyoutWatchTick(null, EventArgs.Empty);
        Assert.Null(HideTimerOf(orb));
        Assert.True(orb.Flyout!.IsVisible);
    }

    // The cursor off both windows hands over to the ordinary hide, and a tick
    // while that hide is pending does not push it back.
    [AvaloniaFact]
    public void AWatchTickWithTheCursorOffBothSchedulesTheHideOnce()
    {
        var orb = ShownOrb("alpha", 100);
        orb.EnsureFlyoutShown();
        CursorProbe.Read = () => FarAway;

        orb.OnFlyoutWatchTick(null, EventArgs.Empty);
        var timer = HideTimerOf(orb)!;
        Assert.True(timer.IsEnabled);

        // The pending-hide arm: a second tick leaves the scheduled hide alone.
        orb.OnFlyoutWatchTick(null, EventArgs.Empty);
        Assert.True(timer.IsEnabled);

        orb.OnFlyoutHideTick(null, EventArgs.Empty);
        Assert.False(orb.Flyout!.IsVisible);
        Assert.False(orb.FlyoutWatching);
    }

    // The flyout's frame only counts while it is on screen.
    [AvaloniaFact]
    public void AHiddenFlyoutsFrameDoesNotKeepItHovered()
    {
        var orb = ShownOrb("alpha", 100);
        orb.EnsureFlyoutShown();
        var onFlyout = Inside(orb.Flyout!);
        CursorProbe.Read = () => onFlyout;
        Assert.True(orb.FlyoutStillHovered());

        orb.Flyout!.Hide();

        // Only true if the orb's own frame happens to contain the point.
        Assert.Equal(RealHover.Inside(onFlyout, RealHover.FrameOf(orb)), orb.FlyoutStillHovered());
    }

    // And the show gate lets a real hover through when the cursor agrees.
    [AvaloniaFact]
    public void TheShowTickOpensTheFlyoutWhenTheCursorIsReallyOnTheOrb()
    {
        var orb = ShownOrb("alpha", 100);
        Raise(orb.Root, InputElement.PointerEnteredEvent);
        CursorProbe.Read = () => Inside(orb);

        orb.ScheduleFlyoutShow();
        orb.OnFlyoutShowTick(null, EventArgs.Empty);

        Assert.True(orb.Flyout?.IsVisible);
    }

    // A closed orb takes its watch down with it.
    [AvaloniaFact]
    public void ClosingTheOrbStopsTheFlyoutWatch()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(Status("closing"));
        orb.Show();
        Flush();
        orb.EnsureFlyoutShown();
        Assert.True(orb.FlyoutWatching);

        orb.Close();

        Assert.False(orb.FlyoutWatching);
    }

    private static DispatcherTimer? HideTimerOf(OrbWindow orb) =>
        (DispatcherTimer?)typeof(OrbWindow)
            .GetField("_hideFlyoutTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(orb);
}
