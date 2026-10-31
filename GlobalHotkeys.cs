using System.Diagnostics.CodeAnalysis;
using Avalonia.Threading;

namespace Orbweaver
{
    // Wires HotkeyRegistry's pure bindings to a real platform hook: the one
    // class that picks which native implementation to start and registers
    // what HotkeyActions.Plan says with it. What each action *does*, which
    // setting overrides its chord, and who keeps a contested chord are
    // HotkeyActions' and HotkeyRegistry's business — kept out of this class
    // because this class cannot be covered and those decisions can.
    //
    // Excluded from coverage as a whole: every path through Start() ends in
    // a real OS-level key registration (Carbon on macOS, RegisterHotKey on
    // Windows), which is exactly the class of thing this repo's CLAUDE.md
    // says can't be exercised from a headless runner — there is no window
    // server session in CI to press a key into. HotkeyRegistryTests and
    // HotkeyActionsTests cover everything up to that call: which combo each
    // action resolves to, the collision rule, and what a delivered press
    // does.
    [ExcludeFromCodeCoverage]
    internal static class GlobalHotkeys
    {
        private static IGlobalHotkeyHook? _hook;

        // Called once from App.axaml.cs after the tray and SessionManager are
        // up, so ToggleOrbsVisible has something to toggle by the time a key
        // could possibly be pressed. A no-op on any platform without a real
        // hook yet, rather than throwing — a hotkey nobody can press is a far
        // smaller problem than an app that won't start on a platform this
        // hasn't been ported to.
        public static void Start()
        {
            _hook = CreateHook();
            HotkeyActions.RegisterAll(_hook);
        }

        public static void Stop() => _hook?.Dispose();

        private static IGlobalHotkeyHook? CreateHook()
        {
            if (OperatingSystem.IsMacOS()) return new MacOSGlobalHotkeyHook();
            if (OperatingSystem.IsWindows()) return new WindowsGlobalHotkeyHook();
            return null;
        }
    }

    // What each HotkeyAction does and which setting overrides its chord —
    // the half of the hotkey feature a headless suite can reach, pulled out of
    // GlobalHotkeys so the class-wide coverage exclusion there doesn't swallow
    // it. HotkeyActionsTests drives Dispatch with no key ever pressed.
    internal static class HotkeyActions
    {
        // The hotkey does exactly what the tray item of the same name does,
        // by calling the same method, so the two can never drift apart.
        internal static Action HandlerFor(HotkeyAction action) => action switch
        {
            HotkeyAction.ToggleOrbsVisible => TrayController.ToggleOrbsVisible,
            HotkeyAction.OpenNewChat => TrayController.OpenNewChat,
            HotkeyAction.ToggleUsageOrbsVisible => TrayController.ToggleUsageOrbsVisible,
            HotkeyAction.ToggleMute => TrayController.ToggleMute,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };

        internal static string? OverrideFor(HotkeyAction action) => action switch
        {
            HotkeyAction.ToggleOrbsVisible => OrbweaverSettings.ToggleOrbsHotkey,
            HotkeyAction.OpenNewChat => OrbweaverSettings.NewChatHotkey,
            HotkeyAction.ToggleUsageOrbsVisible => OrbweaverSettings.ToggleUsageOrbsHotkey,
            HotkeyAction.ToggleMute => OrbweaverSettings.ToggleMuteHotkey,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };

        // The chord an action is actually registered under — after collision
        // resolution, so a rebound or displaced key is reported as it is — or
        // null when nothing registers. The chat panel's tooltip names this so
        // it never advertises a key that is not bound. Pure: HotkeyRegistry.Plan
        // writes nothing, unlike Plan() below, which logs to hotkeys.log.
        internal static HotkeyCombo? Bound(HotkeyAction action) =>
            HotkeyRegistry.Plan(OverrideFor).First(b => b.Action == action).Combo;

        // The registrations GlobalHotkeys.Start makes, from the live settings,
        // with anything that lost a collision written to hotkeys.log first.
        // Here rather than in Start so the logging is covered: Start is only
        // the loop that hands the result to an OS hook.
        internal static IReadOnlyList<HotkeyBinding> Plan()
        {
            var plan = HotkeyRegistry.Plan(OverrideFor);
            foreach (var binding in plan)
            {
                if (binding.Note is { } note) HotkeyLog.Record(note);
            }

            return plan;
        }

        // Hands every planned registration to a hook, and writes one
        // hotkeys.log line for each the OS refused. Without that line a chord
        // another app already holds is a dead hotkey with no trace anywhere,
        // which from outside is indistinguishable from the parser defects
        // CB-197 fixed. Takes the hook as a parameter so a test can pass one
        // that refuses; GlobalHotkeys.Start passes the real one and is the
        // only excluded part.
        //
        // A hook that throws is logged the same way and the loop carries on:
        // one action's registration failing must not cost the others, and
        // this runs during app startup, where an escaping exception would
        // take the whole app down over a hotkey.
        internal static void RegisterAll(IGlobalHotkeyHook? hook)
        {
            if (hook is null) return;

            foreach (var binding in Plan())
            {
                if (binding.Combo is not { } combo) continue;
                var action = binding.Action;

                bool registered;
                try
                {
                    registered = hook.Register(action, combo, () => Dispatch(action));
                }
                catch
                {
                    registered = false;
                }

                if (!registered) HotkeyLog.Record(RefusedNote(action, combo));
            }
        }

        internal static string RefusedNote(HotkeyAction action, HotkeyCombo combo) =>
            $"{action}: {HotkeyRegistry.Format(combo)} could not be registered with the operating system — another app may already hold it.";

        // Posted, never run inline. Neither hook promises the UI thread in so
        // many words: Carbon's application event target and the Windows
        // hook's hidden window both happen to be serviced by the thread
        // Avalonia runs its loop on today, but that is a property of how each
        // was started rather than of the API. Opening a window is UI-thread
        // work, and Post costs nothing when the caller already is that thread.
        // It also moves the work out of the native callback itself, so a
        // window being created never runs inside Carbon's event dispatch or a
        // WndProc.
        internal static void Dispatch(HotkeyAction action)
        {
            var handler = HandlerFor(action);
            Dispatcher.UIThread.Post(handler);
        }
    }

    // What GlobalHotkeys needs from a platform: register one combo against
    // one callback, saying whether the OS accepted it, and tear everything
    // down. Small on purpose — the two
    // implementations differ completely in how they get a callback to fire
    // (Carbon's application event target vs. a hidden window's WndProc), and
    // this interface is only the sliver both can honestly implement.
    internal interface IGlobalHotkeyHook : IDisposable
    {
        // False when the chord could not be registered: a key the hook has no
        // code for, or the OS refusing it — typically because another app
        // already holds it. Never throws by contract, though RegisterAll
        // guards against one that does.
        bool Register(HotkeyAction action, HotkeyCombo combo, Action callback);
    }
}
