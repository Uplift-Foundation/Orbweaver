using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-302's regression pins, written to fail on the code before the fix.
//
// The defect needs an orb window whose IsPointerOver is stuck true after the
// pointer left it. Headless reproduces that exactly: a real headless MouseMove
// into orb A and then into orb B gives A its PointerEntered but never a
// LeaveWindow, because each TopLevel tracks its own pointer-over and nothing
// tells A the pointer moved to a different window. That is the state a missed
// leave leaves behind on a real Mac, and each test asserts it was reached
// before asserting anything else, so a headless platform that one day sends the
// leave turns these into a loud failure rather than a quiet pass.
//
// Only Avalonia, the windows' long-standing surface, AccountOrbs' existing test
// hooks and the CursorProbe seam are used here, so the file compiles against the
// windows as they were before the fix. That is how the negative control was run
// (see the PR): with OrbWindow, AccountOrbWindow, OrbFlyout, AccountOrbs and
// UsageCard restored to bf4ae301, every pin fails — B's bubble closed by A's
// frames, A's flyout and card left up or opened with the cursor on B — and
// every paired control passes, which is what shows the pins measure the fix
// rather than a setup that can never open anything.
[Collection("Settings")]
public class StaleHoverRegressionTests : IDisposable
{
    public void Dispose()
    {
        try
        {
            foreach (var orb in _made)
            {
                ToolTip.SetIsOpen(orb.Root, false);
                orb.HideFlyout();
                orb.Hide();
            }
            foreach (var accounts in _accounts) accounts.CloseAll();
        }
        finally
        {
            CursorProbe.Read = () => null;
        }
    }

    private readonly List<OrbWindow> _made = new();
    private readonly List<AccountOrbs> _accounts = new();

    private static SessionStatus Status(string state, string title) => new()
    {
        Source = SessionSource.ClaudeCode,
        State = state,
        Title = title,
        Cwd = "/Users/user/" + title,
    };

    // Two orbs well apart, shown, laid out and given a bubble each. A short show
    // delay, honoured by the service and by ThoughtBubbles alike, so the wait
    // below is tens of milliseconds rather than the default 400.
    private (OrbWindow A, OrbWindow B) TwoOrbs()
    {
        var a = new OrbWindow(Guid.NewGuid().ToString());
        var b = new OrbWindow(Guid.NewGuid().ToString());
        _made.Add(a);
        _made.Add(b);

        a.UpdateFrom(Status("idle", "alpha"));
        b.UpdateFrom(Status("idle", "bravo"));
        a.Show();
        b.Show();
        a.Position = new PixelPoint(100, 100);
        b.Position = new PixelPoint(400, 100);
        ToolTip.SetShowDelay(a.Root, 30);
        ToolTip.SetShowDelay(b.Root, 30);
        Flush();
        return (a, b);
    }

    private static Point CentreOfRoot(OrbWindow orb) => CentreOf(orb, orb.Root);

    private static Point CentreOf(Window window, Control root) =>
        root.TranslatePoint(new Point(root.Bounds.Width / 2, root.Bounds.Height / 2), window)!.Value;

    // The Root's centre in Window.Position's space, which is what the real
    // cursor probe reports and what RealHover.FrameOf measures against. Built
    // from Position rather than PointToScreen: the headless window's
    // PointToScreen does not follow a Position set after Show, which the paired
    // controls below caught — with it, "the cursor on B" was not on B at all.
    private static PixelPoint ScreenCentre(OrbWindow orb) => ScreenCentre(orb, orb.Root);

    private static PixelPoint ScreenCentre(Window window, Control root)
    {
        var c = CentreOf(window, root);
        var scale = OrbWindow.UnitsPerDip(window);
        return window.Position + new PixelVector((int)(c.X * scale), (int)(c.Y * scale));
    }

    // A then B, with real headless pointer input. Leaves A stale.
    private static void HoverAThenB(OrbWindow a, OrbWindow b)
    {
        a.MouseMove(CentreOfRoot(a));
        Flush();
        b.MouseMove(CentreOfRoot(b));
        Flush();

        Assert.True(b.Root.IsPointerOver);
        Assert.True(a.Root.IsPointerOver);   // the stale state the bug needs
    }

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    // Real wall-clock time for the real DispatcherTimers, bounded. RunJobs alone
    // never fires a DispatcherTimer under the headless dispatcher — timers are
    // promoted only while a dispatcher frame is actually running — so each step
    // pushes a short frame and lets the loop run.
    internal static void PumpFor(int ms)
    {
        var frame = new DispatcherFrame();
        DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromMilliseconds(ms));
        Dispatcher.UIThread.PushFrame(frame);
        Flush();
    }

    internal static void PumpUntil(Func<bool> done, int timeoutMs = 3000)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < timeoutMs) PumpFor(15);
    }

    // Frames on A and nothing else: a state change starts its breath, and
    // every forced render tick is a SceneInvalidated on A's TopLevel — the path
    // the MacBook trace showed driving the churn.
    private static void FramesOn(OrbWindow a, int count)
    {
        for (var i = 0; i < count; i++)
        {
            a.UpdateFrom(Status(i % 2 == 0 ? "generating" : "waiting", "alpha"));
            a.Root.InvalidateVisual();
            Flush();
        }
    }

    // The production case on a Mac, where the cursor can be read: the real
    // cursor is on B, so A must never open its bubble at any point — not while
    // B's delay runs, not on any frame after. A recorder on A's IsOpen catches
    // an open that something else closed again before an assertion looked.
    [AvaloniaFact]
    public void WithTheCursorOnBAStaleOrbNeverOpensItsBubble()
    {
        var (a, b) = TwoOrbs();
        var aOpened = false;
        a.Root.PropertyChanged += (_, e) =>
        {
            if (e.Property == ToolTip.IsOpenProperty && e.NewValue is true) aOpened = true;
        };
        HoverAThenB(a, b);
        var onB = ScreenCentre(b);
        CursorProbe.Read = () => onB;

        PumpFor(150);   // several show delays, before B has necessarily opened
        PumpUntil(() => ToolTip.GetIsOpen(b.Root));
        Assert.True(ToolTip.GetIsOpen(b.Root));

        for (var frame = 0; frame < 20; frame++)
        {
            FramesOn(a, 1);
            Assert.True(ToolTip.GetIsOpen(b.Root), $"B's bubble closed on frame {frame}");
        }

        Assert.False(aOpened, "A's bubble opened at some point with the cursor on B");
    }

    // The fallback, with no cursor to read (another platform, or the probe
    // unavailable): A may be armed on its stale flag, so this pins the
    // one-at-a-time rule and the service being off — B stays open and A is not
    // open on any frame drawn after B's bubble is up.
    [AvaloniaFact]
    public void AStaleOrbsFramesNeitherCloseTheHoveredBubbleNorOpenTheirOwn()
    {
        var (a, b) = TwoOrbs();
        HoverAThenB(a, b);

        PumpUntil(() => ToolTip.GetIsOpen(b.Root));
        Assert.True(ToolTip.GetIsOpen(b.Root));

        for (var frame = 0; frame < 20; frame++)
        {
            FramesOn(a, 1);
            Assert.True(ToolTip.GetIsOpen(b.Root), $"B's bubble closed on frame {frame}");
            Assert.False(ToolTip.GetIsOpen(a.Root), $"A's bubble opened on frame {frame}");
        }
    }

    // Control: a real hover back onto A does open A's bubble and close B's, so
    // the pin above is not passing because nothing can open A at all. A is
    // still marked pointer-over, so this move raises no PointerEntered.
    [AvaloniaFact]
    public void ControlARealMoveBackOntoTheStaleOrbOpensItsBubble()
    {
        var (a, b) = TwoOrbs();
        HoverAThenB(a, b);
        PumpUntil(() => ToolTip.GetIsOpen(b.Root));

        a.MouseMove(CentreOfRoot(a) + new Vector(1, 1));
        PumpUntil(() => ToolTip.GetIsOpen(a.Root));

        Assert.True(ToolTip.GetIsOpen(a.Root));
        Assert.False(ToolTip.GetIsOpen(b.Root));
    }

    // The screenshot's flyouts: A's flyout opened while the pointer was on A,
    // the pointer moved to B, and A never heard. With the real cursor on B, A's
    // flyout comes down within the watch interval plus the hide grace.
    [AvaloniaFact]
    public void AStaleOrbsFlyoutComesDownOnceTheCursorIsOnAnotherOrb()
    {
        var (a, b) = TwoOrbs();
        a.MouseMove(CentreOfRoot(a));
        Flush();
        a.EnsureFlyoutShown();
        Assert.True(a.Flyout!.IsVisible);

        b.MouseMove(CentreOfRoot(b));
        Flush();
        Assert.True(a.Root.IsPointerOver);   // stale

        var onB = ScreenCentre(b);
        CursorProbe.Read = () => onB;

        PumpUntil(() => !a.Flyout!.IsVisible, timeoutMs: 1500);

        Assert.False(a.Flyout!.IsVisible);
    }

    // Control: the same stale A with the real cursor still on it keeps its
    // flyout through the same wait, so the pin is not a flyout that hides on
    // its own.
    [AvaloniaFact]
    public void ControlAFlyoutStaysUpWhileTheCursorIsReallyOnItsOrb()
    {
        var (a, b) = TwoOrbs();
        a.MouseMove(CentreOfRoot(a));
        Flush();
        a.EnsureFlyoutShown();
        b.MouseMove(CentreOfRoot(b));
        Flush();

        var onA = ScreenCentre(a);
        CursorProbe.Read = () => onA;
        PumpFor(600);

        Assert.True(a.Flyout!.IsVisible);
    }

    // And a stale orb never *opens* a flyout for a cursor that is somewhere
    // else, however its show timer got started.
    [AvaloniaFact]
    public void AStaleOrbDoesNotOpenAFlyoutForACursorThatIsElsewhere()
    {
        var (a, b) = TwoOrbs();
        HoverAThenB(a, b);

        var onB = ScreenCentre(b);
        CursorProbe.Read = () => onB;

        a.ScheduleFlyoutShow();
        a.OnFlyoutShowTick(null, EventArgs.Empty);

        Assert.True(a.Flyout is null || !a.Flyout.IsVisible);
    }

    // Control: the same stale A with the cursor really on it does open.
    [AvaloniaFact]
    public void ControlAStaleOrbWithTheCursorOnItOpensItsFlyout()
    {
        var (a, b) = TwoOrbs();
        HoverAThenB(a, b);

        var onA = ScreenCentre(a);
        CursorProbe.Read = () => onA;

        a.ScheduleFlyoutShow();
        a.OnFlyoutShowTick(null, EventArgs.Empty);

        Assert.True(a.Flyout?.IsVisible);
    }

    // --- account orbs and their usage cards --------------------------------------

    private sealed class NoUsage : IUsageSource
    {
        public IReadOnlyList<AccountUsage> Read() => Array.Empty<AccountUsage>();
    }

    // Two account orbs well apart, A stale after real moves into A then B.
    private (AccountOrbs Accounts, AccountOrbWindow A, AccountOrbWindow B) TwoStaleAccountOrbs()
    {
        var accounts = new AccountOrbs(new NoUsage());
        _accounts.Add(accounts);
        accounts.Apply(new[]
        {
            ThoughtBubblesTests.Usage(null, "alpha"),
            ThoughtBubblesTests.Usage("/tmp/cb302-bravo", "bravo"),
        }, ThoughtBubblesTests.Now);
        accounts.SetVisible(true);
        var a = accounts.Orbs[string.Empty];
        var b = accounts.Orbs["/tmp/cb302-bravo"];
        a.Position = new PixelPoint(100, 400);
        b.Position = new PixelPoint(400, 400);
        Flush();

        a.MouseMove(CentreOf(a, a.Root));
        Flush();
        b.MouseMove(CentreOf(b, b.Root));
        Flush();
        Assert.True(a.Root.IsPointerOver);   // stale
        return (accounts, a, b);
    }

    // The card version of the stuck flyout: A's card is open, the pointer went
    // to B and A never heard.
    [AvaloniaFact]
    public void AStaleAccountOrbsCardComesDownOnceTheCursorIsOnAnotherOrb()
    {
        var (accounts, a, b) = TwoStaleAccountOrbs();
        accounts.OpenCardForTests(a.AccountKey);
        Assert.True(accounts.Cards.ContainsKey(a.AccountKey));

        var onB = ScreenCentre(b, b.Root);
        CursorProbe.Read = () => onB;
        PumpUntil(() => !accounts.Cards.ContainsKey(a.AccountKey), timeoutMs: 1500);

        Assert.False(accounts.Cards.ContainsKey(a.AccountKey));
    }

    [AvaloniaFact]
    public void ControlACardStaysWhileTheCursorIsReallyOnItsOrb()
    {
        var (accounts, a, _) = TwoStaleAccountOrbs();
        accounts.OpenCardForTests(a.AccountKey);

        var onA = ScreenCentre(a, a.Root);
        CursorProbe.Read = () => onA;
        PumpFor(600);

        Assert.True(accounts.Cards.ContainsKey(a.AccountKey));
    }

    // And a stale account orb handed a PointerEntered (the pre-processor
    // re-fires one when the visual under a stale point changes) does not open
    // a card for a cursor that is on another orb.
    [AvaloniaFact]
    public void AStaleAccountOrbDoesNotOpenACardForACursorThatIsElsewhere()
    {
        var (accounts, a, b) = TwoStaleAccountOrbs();
        var onB = ScreenCentre(b, b.Root);
        CursorProbe.Read = () => onB;

        RaiseEntered(a.Root);
        PumpFor(700);

        Assert.False(accounts.Cards.ContainsKey(a.AccountKey));
    }

    [AvaloniaFact]
    public void ControlAStaleAccountOrbWithTheCursorOnItOpensItsCard()
    {
        var (accounts, a, _) = TwoStaleAccountOrbs();
        var onA = ScreenCentre(a, a.Root);
        CursorProbe.Read = () => onA;

        RaiseEntered(a.Root);
        PumpUntil(() => accounts.Cards.ContainsKey(a.AccountKey), timeoutMs: 1500);

        Assert.True(accounts.Cards.ContainsKey(a.AccountKey));
    }

    private static void RaiseEntered(Control target)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        target.RaiseEvent(new PointerEventArgs(
            InputElement.PointerEnteredEvent, target, pointer, target, new Point(10, 10), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
            KeyModifiers.None));
    }
}
