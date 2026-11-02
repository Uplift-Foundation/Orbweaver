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
// Only Avalonia, OrbWindow's long-standing surface and the CursorProbe seam are
// used here, so the file compiles against OrbWindow.axaml.cs as it was before
// the fix. That is how the negative control was run (see the PR): with the old
// OrbWindow and AccountOrbWindow restored, the bubble test sees B's bubble
// closed and A's opened by A's own frames, and both flyout tests see A's
// flyout up or built with the cursor on B.
[Collection("Settings")]
public class StaleHoverRegressionTests : IDisposable
{
    public void Dispose()
    {
        CursorProbe.Read = () => null;
        foreach (var orb in _made)
        {
            ToolTip.SetIsOpen(orb.Root, false);
            orb.HideFlyout();
            orb.Hide();
        }
    }

    private readonly List<OrbWindow> _made = new();

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

    private static Point CentreOfRoot(OrbWindow orb) =>
        orb.Root.TranslatePoint(new Point(orb.Root.Bounds.Width / 2, orb.Root.Bounds.Height / 2), orb)!.Value;

    private static PixelPoint ScreenCentre(OrbWindow orb) => orb.PointToScreen(CentreOfRoot(orb));

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
}
