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
        // The one anchor whose bubble is open, or null. One at a time by
        // construction: Show closes the previous one first.
        internal static Control? Open { get; private set; }

        // Whether the open bubble is on HoverWatch, for tests: it must come off
        // once nothing is open, or every orb ever hovered leaves an entry behind.
        internal static bool Watching => Open is { } open && HoverWatch.IsWatching(open);

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

            // Set by a press and cleared by the next exit: the service never
            // brought a bubble back after a click until the pointer left and came
            // back, and a drag must not grow one either.
            var pressed = false;

            void Arm()
            {
                // The same delay the service used, read at the time so a style
                // that changes it still applies.
                show.Interval = TimeSpan.FromMilliseconds(ToolTip.GetShowDelay(anchor));
                show.Stop();
                show.Start();
            }

            anchor.PointerEntered += (_, _) =>
            {
                pressed = false;
                Arm();
            };

            // A window that missed its leave never gets another PointerEntered:
            // its IsPointerOver is already true, so coming back onto it raises
            // none. Under the service, the next real move re-showed its bubble;
            // a real move over it is still proof the pointer is here, so it arms
            // the same delay — once, not on every move, and never mid-press.
            anchor.PointerMoved += (_, e) =>
            {
                if (pressed || show.IsEnabled || Open == anchor) return;
                if (e.GetCurrentPoint(anchor).Properties is { IsLeftButtonPressed: false, IsRightButtonPressed: false, IsMiddleButtonPressed: false })
                    Arm();
            };

            anchor.PointerExited += (_, _) =>
            {
                pressed = false;
                Dismiss(show, anchor);
            };

            // The service closed tips on any button down; keep that, so a drag
            // never carries a bubble with it. handledEventsToo because the
            // window's own press handling captures the pointer.
            anchor.AddHandler(
                InputElement.PointerPressedEvent,
                (_, _) =>
                {
                    pressed = true;
                    Dismiss(show, anchor);
                },
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
            if (RealHover.CanOpen(window, anchor)) Show(anchor);
        }

        private static void Dismiss(DispatcherTimer show, Control anchor)
        {
            show.Stop();
            Hide(anchor);
        }

        internal static void Show(Control anchor)
        {
            if (Open is { } old && old != anchor) Hide(old);

            ToolTip.SetIsOpen(anchor, true);

            // IsOpen refuses itself when there is no Tip to show (an orb that has
            // not had its first UpdateFrom yet); nothing is open then.
            if (!ToolTip.GetIsOpen(anchor)) return;

            Open = anchor;
            HoverWatch.Register(anchor, () => StillHovered(anchor), () => Hide(anchor));
        }

        // A no-op unless this anchor's bubble is the open one, so a late exit
        // from one orb can never close the bubble another orb now owns.
        internal static void Hide(Control anchor)
        {
            if (Open != anchor) return;

            Open = null;
            HoverWatch.Unregister(anchor);
            ToolTip.SetIsOpen(anchor, false);
        }

        // An anchor that has left its window has nothing to be hovered in.
        internal static bool StillHovered(Control anchor) =>
            TopLevel.GetTopLevel(anchor) is Window window && RealHover.IsOver(window, anchor);
    }
}
