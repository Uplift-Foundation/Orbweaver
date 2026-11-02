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
    private readonly List<AccountOrbs> _accounts = new();

    // A known-empty watch per test; other classes leave flyouts registered.
    public ThoughtBubblesTests() => HoverWatch.ResetForTests();

    public void Dispose()
    {
        try
        {
            if (ThoughtBubbles.Open is { } open) ThoughtBubbles.Hide(open);
            foreach (var w in _shown)
            {
                if (w is OrbWindow orb) orb.HideFlyout();
                w.Hide();
            }
            foreach (var a in _accounts) a.CloseAll();
        }
        finally
        {
            CursorProbe.Read = () => null;
            HoverWatch.ResetForTests();
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

        HoverWatch.Tick();

        Assert.True(ToolTip.GetIsOpen(orb.Root));
    }

    // The missed leave: nothing told the orb the pointer went, the cursor did.
    [AvaloniaFact]
    public void TheWatchClosesABubbleOnceTheCursorHasLeft()
    {
        var orb = ShownOrb("alpha", 100);
        Raise(orb.Root, InputElement.PointerEnteredEvent);   // stale-true flag
        ThoughtBubbles.Show(orb.Root);
        CursorProbe.Read = () => FarAway;

        HoverWatch.Tick();

        Assert.False(ToolTip.GetIsOpen(orb.Root));
        Assert.False(ThoughtBubbles.Watching);
        Assert.False(HoverWatch.IsWatching(orb.Root));
    }

    // With no cursor to read the watch does nothing at all, even for a bubble
    // the pointer is not over: exits drive it, exactly as before.
    [AvaloniaFact]
    public void WithNoCursorTheWatchDoesNothing()
    {
        var orb = ShownOrb("alpha", 100);
        Assert.False(orb.Root.IsPointerOver);
        ThoughtBubbles.Show(orb.Root);

        HoverWatch.Tick();

        Assert.True(ToolTip.GetIsOpen(orb.Root));
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
    public void TheWatchTimerRunsOnlyWhileSomethingIsRegistered()
    {
        var first = new object();
        var second = new object();
        Assert.False(HoverWatch.Running);

        HoverWatch.Register(first, () => true, () => { });
        HoverWatch.Register(second, () => true, () => { });
        Assert.True(HoverWatch.Running);

        HoverWatch.Unregister(first);
        Assert.True(HoverWatch.Running);

        HoverWatch.Unregister(second);
        Assert.False(HoverWatch.Running);
    }

    [AvaloniaFact]
    public void ATickWithNothingRegisteredStopsTheTimer()
    {
        HoverWatch.Register(this, () => true, () => { });
        HoverWatch.ResetForTests();
        HoverWatch.Register(this, () => true, () => { });
        HoverWatch.Unregister(this);

        HoverWatch.Tick();

        Assert.False(HoverWatch.Running);
    }

    // A lost owner's handler may take another owner off the watch mid-tick;
    // the snapshot skips the one that left rather than calling it.
    [AvaloniaFact]
    public void AnOwnerUnregisteredMidTickIsNotAsked()
    {
        var first = new object();
        var second = new object();
        var secondAsked = false;
        CursorProbe.Read = () => FarAway;
        HoverWatch.Register(first, () => false, () => HoverWatch.Unregister(second));
        HoverWatch.Register(second, () => { secondAsked = true; return false; }, () => { });

        HoverWatch.Tick();

        Assert.False(secondAsked);
        HoverWatch.Unregister(first);
    }

    // An anchor that has left its window has nothing to be hovered in.
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
        CursorProbe.Read = () => FarAway;
        HoverWatch.Tick();

        Assert.Null(ThoughtBubbles.Open);
    }

    // --- a stale orb hovered again ------------------------------------------------------

    // A window already marked pointer-over raises no PointerEntered when the
    // pointer comes back, so a real move over it arms the delay instead.
    [AvaloniaFact]
    public void AMoveOverAnOrbWithNoEnterStillOpensItsBubble()
    {
        var orb = ShownOrb("alpha", 100);
        Raise(orb.Root, InputElement.PointerEnteredEvent);
        StaleHoverRegressionTests.PumpUntil(() => ToolTip.GetIsOpen(orb.Root));
        ThoughtBubbles.Hide(orb.Root);   // closed without an exit: still pointer-over

        RaiseMove(orb.Root, RawInputModifiers.None);
        StaleHoverRegressionTests.PumpUntil(() => ToolTip.GetIsOpen(orb.Root));

        Assert.True(ToolTip.GetIsOpen(orb.Root));
    }

    // Not after a press until the pointer has left, and never with a button down.
    [AvaloniaFact]
    public void AMoveAfterAPressOrWithAButtonDownDoesNotReopen()
    {
        var orb = ShownOrb("alpha", 100);
        ThoughtBubbles.Show(orb.Root);
        RightPress(orb.Root);

        RaiseMove(orb.Root, RawInputModifiers.None);
        StaleHoverRegressionTests.PumpFor(80);
        Assert.False(ToolTip.GetIsOpen(orb.Root));

        // The exit clears the press; a move with a button down still arms nothing.
        Raise(orb.Root, InputElement.PointerExitedEvent);
        RaiseMove(orb.Root, RawInputModifiers.LeftMouseButton);
        RaiseMove(orb.Root, RawInputModifiers.RightMouseButton);
        RaiseMove(orb.Root, RawInputModifiers.MiddleMouseButton);
        StaleHoverRegressionTests.PumpFor(80);
        Assert.False(ToolTip.GetIsOpen(orb.Root));
    }

    // While open, or while the delay is already running, a move changes nothing.
    [AvaloniaFact]
    public void AMoveWhileOpenOrAlreadyArmedChangesNothing()
    {
        var orb = ShownOrb("alpha", 100);
        Raise(orb.Root, InputElement.PointerEnteredEvent);
        StaleHoverRegressionTests.PumpUntil(() => ToolTip.GetIsOpen(orb.Root));
        RaiseMove(orb.Root, RawInputModifiers.None);
        Assert.True(ToolTip.GetIsOpen(orb.Root));

        ThoughtBubbles.Hide(orb.Root);
        ToolTip.SetShowDelay(orb.Root, 60);
        RaiseMove(orb.Root, RawInputModifiers.None);
        RaiseMove(orb.Root, RawInputModifiers.None);   // already armed
        StaleHoverRegressionTests.PumpUntil(() => ToolTip.GetIsOpen(orb.Root));
        Assert.True(ToolTip.GetIsOpen(orb.Root));
    }

    // --- CB-104 survives: a new title while open swaps content, no reopen ----------------

    [AvaloniaFact]
    public void ANewTitleWhileOpenSwapsTheContentWithoutClosing()
    {
        var orb = ShownOrb("alpha", 100);
        ThoughtBubbles.Show(orb.Root);
        var before = orb.CurrentThoughtBubble;

        // ToolTip's own ToolTipProperty is internal, so watch IsOpen instead: a
        // close-and-reopen would pass through false.
        var closed = false;
        orb.Root.PropertyChanged += (_, e) =>
        {
            if (e.Property == ToolTip.IsOpenProperty && e.NewValue is false) closed = true;
        };

        orb.UpdateFrom(Status("renamed"));
        Flush();

        Assert.True(ToolTip.GetIsOpen(orb.Root));
        Assert.NotSame(before, orb.CurrentThoughtBubble);
        Assert.False(closed);
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

    // --- the flyout on the watch ---------------------------------------------------------

    [AvaloniaFact]
    public void ShowingTheFlyoutPutsItOnTheWatchAndHidingItTakesItOff()
    {
        var orb = ShownOrb("alpha", 100);

        orb.EnsureFlyoutShown();
        Assert.True(orb.FlyoutWatching);
        Assert.True(HoverWatch.Running);

        orb.HideFlyout();
        Assert.False(orb.FlyoutWatching);
        Assert.False(HoverWatch.Running);
    }

    [AvaloniaFact]
    public void AFlyoutThatWentAwayAnotherWayLeavesTheWatch()
    {
        var orb = ShownOrb("alpha", 100);
        orb.EnsureFlyoutShown();
        orb.Flyout!.Hide();
        CursorProbe.Read = () => FarAway;

        HoverWatch.Tick();

        Assert.False(orb.FlyoutWatching);
    }

    [AvaloniaFact]
    public void TheCursorOnTheOrbOrTheFlyoutKeepsItUp()
    {
        var orb = ShownOrb("alpha", 100);
        orb.EnsureFlyoutShown();

        CursorProbe.Read = () => Inside(orb);
        HoverWatch.Tick();
        Assert.Null(HideTimerOf(orb));

        var onFlyout = Inside(orb.Flyout!);
        CursorProbe.Read = () => onFlyout;
        HoverWatch.Tick();
        Assert.Null(HideTimerOf(orb));
        Assert.True(orb.Flyout!.IsVisible);
    }

    // Recording keeps the flyout up wherever the cursor is: it is the only way
    // to stop.
    [AvaloniaFact]
    public void RecordingKeepsTheFlyoutUpWithTheCursorAway()
    {
        var orb = ShownOrb("alpha", 100);
        orb.EnsureFlyoutShown();
        CursorProbe.Read = () => FarAway;
        var recording = typeof(OrbWindow).GetField(
            "_recording", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        recording.SetValue(orb, true);
        try
        {
            HoverWatch.Tick();
            Assert.Null(HideTimerOf(orb));
            Assert.True(orb.Flyout!.IsVisible);
        }
        finally
        {
            recording.SetValue(orb, false);
        }
    }

    [AvaloniaFact]
    public void WithNoCursorTheFlyoutWatchDoesNothing()
    {
        var orb = ShownOrb("alpha", 100);
        orb.EnsureFlyoutShown();

        HoverWatch.Tick();

        Assert.Null(HideTimerOf(orb));
        Assert.True(orb.Flyout!.IsVisible);
    }

    // The cursor off both windows hands over to the ordinary hide, and a tick
    // while that hide is pending does not push it back.
    [AvaloniaFact]
    public void TheCursorOffBothSchedulesTheHideOnce()
    {
        var orb = ShownOrb("alpha", 100);
        orb.EnsureFlyoutShown();
        CursorProbe.Read = () => FarAway;

        HoverWatch.Tick();
        var timer = HideTimerOf(orb)!;
        Assert.True(timer.IsEnabled);

        HoverWatch.Tick();   // the pending-hide arm
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
        Assert.True(orb.Flyout!.IsPointerOverFlyout);

        orb.Flyout!.Hide();

        Assert.Equal(RealHover.Inside(onFlyout, RealHover.FrameOf(orb)), orb.FlyoutStillHovered());
    }

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

    [AvaloniaFact]
    public void ClosingTheOrbTakesItsFlyoutOffTheWatch()
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

    // --- the usage card on the watch -------------------------------------------------------

    private sealed class FakeUsageSource : IUsageSource
    {
        public IReadOnlyList<AccountUsage> Read() => Array.Empty<AccountUsage>();
    }

    internal static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    internal static AccountUsage Usage(string? configDir, string label) => new(
        ConfigDir: configDir,
        Label: label,
        Available: true,
        SubscriptionType: "team",
        Session: new UsageWindow(10, Now.AddHours(3)),
        Weekly: new UsageWindow(40, Now.AddDays(3)),
        Extra: null,
        ReadAt: Now,
        Source: AccountUsageSource.ClaudeCode);

    private (AccountOrbs Accounts, AccountOrbWindow Orb, UsageCard Card) OneOpenCard()
    {
        var accounts = new AccountOrbs(new FakeUsageSource());
        _accounts.Add(accounts);
        accounts.Apply(new[] { Usage(null, "solo") }, Now);
        accounts.SetVisible(true);
        var orb = accounts.Orbs[string.Empty];
        orb.Position = new PixelPoint(700, 100);
        Flush();
        accounts.OpenCardForTests(string.Empty);
        Flush();
        return (accounts, orb, accounts.Cards[string.Empty]);
    }

    [AvaloniaFact]
    public void AnOpenCardIsOnTheWatchAndAClosedOneIsNot()
    {
        var (accounts, _, card) = OneOpenCard();
        Assert.True(HoverWatch.IsWatching(card));

        accounts.SetVisible(false);

        Assert.False(HoverWatch.IsWatching(card));
    }

    [AvaloniaFact]
    public void TheCursorOffTheOrbAndCardClosesTheCardAfterTheGrace()
    {
        var (accounts, _, card) = OneOpenCard();
        CursorProbe.Read = () => FarAway;

        HoverWatch.Tick();
        HoverWatch.Tick();   // a pending hide is not pushed back
        StaleHoverRegressionTests.PumpUntil(() => !accounts.Cards.ContainsKey(string.Empty), timeoutMs: 1500);

        Assert.False(accounts.Cards.ContainsKey(string.Empty));
        Assert.False(HoverWatch.IsWatching(card));
    }

    [AvaloniaFact]
    public void TheCursorOnTheOrbOrTheCardOrAPinKeepsTheCard()
    {
        var (accounts, orb, card) = OneOpenCard();

        CursorProbe.Read = () => Inside(orb);
        Assert.True(accounts.CardStillHovered(string.Empty));

        var onCard = Inside(card);
        CursorProbe.Read = () => onCard;
        Assert.True(card.IsPointerOverCard);
        Assert.True(accounts.CardStillHovered(string.Empty));

        CursorProbe.Read = () => FarAway;
        Assert.False(accounts.CardStillHovered(string.Empty));
        Assert.False(accounts.CardStillHovered("no-such-account"));

        accounts.TogglePin(string.Empty);
        HoverWatch.Tick();
        StaleHoverRegressionTests.PumpFor(300);
        Assert.True(accounts.Cards.ContainsKey(string.Empty));
    }

    // Unpinning closes the card at once only if the pointer is really gone.
    [AvaloniaFact]
    public void UnpinningAsksTheRealCursor()
    {
        var (accounts, orb, _) = OneOpenCard();
        accounts.TogglePin(string.Empty);

        CursorProbe.Read = () => Inside(orb);
        accounts.TogglePin(string.Empty);
        Assert.True(accounts.Cards.ContainsKey(string.Empty));

        accounts.TogglePin(string.Empty);
        CursorProbe.Read = () => FarAway;
        accounts.TogglePin(string.Empty);
        Assert.False(accounts.Cards.ContainsKey(string.Empty));
    }

    private static void RaiseMove(Control target, RawInputModifiers buttons)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        target.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent, target, pointer, target, new Point(10, 10), 0,
            new PointerPointProperties(buttons, PointerUpdateKind.Other),
            KeyModifiers.None));
    }

    private static DispatcherTimer? HideTimerOf(OrbWindow orb) =>
        (DispatcherTimer?)typeof(OrbWindow)
            .GetField("_hideFlyoutTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(orb);
}
