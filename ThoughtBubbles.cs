using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Orbweaver
{
    // CB-302: orb thought bubbles, opened and closed by us rather than by
    // Avalonia's ToolTipService.
    //
    // Avalonia 12.1.1 re-runs its tooltip hit test on every rendered frame:
    // TopLevel.SceneInvalidated calls UpdateToolTip, which — for any TopLevel
    // whose IsPointerOver is still true — hit-tests that TopLevel's *last known*
    // pointer position and hands the result to ToolTipService.Update. There is
    // one service per process with a single "current tip" slot, and Update
    // closes whatever tip holds that slot whenever the hit resolves to a
    // different control. An orb window that missed its LeaveWindow keeps
    // IsPointerOver true forever, pinned to a point inside its own Root, so
    // every breath, pulse and avatar frame it draws re-asserts its own bubble.
    // Two such orbs take the slot from each other every frame: a bubble that
    // flickers across several orbs with the pointer on none of them, and the
    // orb you actually hover having its bubble stolen within a frame. Measured
    // on a real MacBook (docs in the CB-302 ticket): the dominant path into
    // tooltip open/close was SceneInvalidated, not pointer input.
    //
    // ToolTip.ServiceEnabled = false on the orb's Window is the one lever that
    // makes Update return before it touches the slot, so a stale orb can no
    // longer close or open anyone's tip. The bubble itself still opens through
    // ToolTip.IsOpen, which does not go through the service at all; this class
    // decides when. Do not move orb bubbles back onto the service — the flicker
    // comes straight back with the first two stale windows.
    //
    // UI thread only, like everything else that touches a window.
    internal static class ThoughtBubbles
    {
        // How often an open bubble checks the real cursor. A missed leave has
        // to be caught by something other than the leave; a tenth of a second is
        // well under what reads as "stuck", and the timer only runs while a
        // bubble is actually open.
        internal static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(100);

        private static DispatcherTimer? _watch;

        // The one anchor whose bubble is open, or null. One at a time by
        // construction: Show closes the previous one first.
        internal static Control? Open { get; private set; }

        // Whether the watch is running, for tests: it must stop once nothing is
        // open, or every orb ever hovered leaves a 10 Hz timer behind.
        internal static bool Watching => _watch?.IsEnabled == true;

        // Wires one orb's bubble: the window is taken off the tooltip service,
        // its context menu is put back on it (ServiceEnabled inherits, and the
        // menu's own item tips — Reset idle, End session, the OpenClaw rows —
        // are ordinary service tips that must keep working), and the anchor's
        // own hover drives Show and Hide.
        //
        // On the Window rather than the anchor: a stale hit can land on window
        // chrome outside the anchor, and a walk that found no Tip there would
        // resolve to null and close someone else's tip just the same.
        internal static void Attach(Window window, Control anchor)
        {
            ToolTip.SetServiceEnabled(window, false);
            if (anchor.ContextMenu is { } menu) ToolTip.SetServiceEnabled(menu, true);

            var show = new DispatcherTimer();
            show.Tick += (_, _) => OnShowTick(show, window, anchor);

            anchor.PointerEntered += (_, _) =>
            {
                // The same delay the service used, read at the time so a style
                // that changes it still applies.
                show.Interval = TimeSpan.FromMilliseconds(ToolTip.GetShowDelay(anchor));
                show.Stop();
                show.Start();
            };
            anchor.PointerExited += (_, _) => Dismiss(show, anchor);

            // The service closed tips on any button down; keep that, so a drag
            // never carries a bubble with it. handledEventsToo because the
            // window's own press handling captures the pointer.
            anchor.AddHandler(
                InputElement.PointerPressedEvent,
                (_, _) => Dismiss(show, anchor),
                RoutingStrategies.Bubble,
                handledEventsToo: true);

            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.IsVisibleProperty && !window.IsVisible) Dismiss(show, anchor);
            };
            window.Closed += (_, _) => Dismiss(show, anchor);
        }

        internal static void OnShowTick(DispatcherTimer show, Window window, Control anchor)
        {
            show.Stop();
            if (RealHover.ShouldOpen(anchor.IsPointerOver, CursorProbe.Read(), RealHover.FrameOf(window)))
                Show(anchor);
        }

        private static void Dismiss(DispatcherTimer show, Control anchor)
        {
            show.Stop();
            Hide(anchor);
        }

        internal static void Show(Control anchor)
        {
            if (Open is { } old && old != anchor) ToolTip.SetIsOpen(old, false);

            Open = anchor;
            ToolTip.SetIsOpen(anchor, true);

            // IsOpen refuses itself when there is no Tip to show (an orb that has
            // not had its first UpdateFrom yet); nothing is open then.
            if (!ToolTip.GetIsOpen(anchor))
            {
                Open = null;
                _watch?.Stop();
                return;
            }

            if (_watch is null)
            {
                _watch = new DispatcherTimer { Interval = WatchInterval };
                _watch.Tick += (_, _) => OnWatchTick();
            }
            _watch.Start();
        }

        // A no-op unless this anchor's bubble is the open one, so a late exit
        // from one orb can never close the bubble another orb now owns.
        internal static void Hide(Control anchor)
        {
            if (Open != anchor) return;

            Open = null;
            _watch?.Stop();
            ToolTip.SetIsOpen(anchor, false);
        }

        internal static void OnWatchTick()
        {
            if (Open is not { } anchor)
            {
                _watch?.Stop();
                return;
            }

            if (TopLevel.GetTopLevel(anchor) is not Window window ||
                !RealHover.ShouldStayOpen(anchor.IsPointerOver, CursorProbe.Read(), RealHover.FrameOf(window)))
            {
                Hide(anchor);
            }
        }
    }
}
