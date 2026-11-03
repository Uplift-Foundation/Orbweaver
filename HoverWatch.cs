using Avalonia.Threading;

namespace Orbweaver
{
    // CB-302: one process-wide look at the real cursor, for every hover surface
    // that is open right now — an orb's flyout, an account orb's usage card, a
    // thought bubble.
    //
    // Each of those comes down when its window reports PointerExited. A window
    // that missed its LeaveWindow never reports one, and its IsPointerOver stays
    // true, so the surface stayed up with the pointer across the screen: the
    // eight open flyouts in the ticket's screenshot. RealHover makes every
    // confirmation ask the real cursor, but a confirmation only runs when
    // something schedules it, and nothing did. This is the something.
    //
    // It runs only while at least one surface is registered, and it decides
    // nothing itself: each owner says whether it is still hovered and what to do
    // when it is not, which is its own existing scheduled hide, grace period and
    // re-check included. With no cursor to read (another platform, or a test
    // that has not set one), a tick does nothing, which is exactly the behaviour
    // before this existed.
    internal static class HoverWatch
    {
        internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(150);

        private static readonly Dictionary<object, (Func<bool> StillHovered, Action Lost)> Watched =
            new(ReferenceEqualityComparer.Instance);

        private static DispatcherTimer? _timer;

        internal static bool Running => _timer?.IsEnabled == true;

        internal static bool IsWatching(object owner) => Watched.ContainsKey(owner);

        // Registering again replaces the owner's entry; one entry per owner.
        internal static void Register(object owner, Func<bool> stillHovered, Action lost)
        {
            Watched[owner] = (stillHovered, lost);

            if (_timer is null)
            {
                _timer = new DispatcherTimer { Interval = Interval };
                _timer.Tick += (_, _) => Tick();
            }
            if (!_timer.IsEnabled) _timer.Start();
        }

        internal static void Unregister(object owner)
        {
            Watched.Remove(owner);
            if (Watched.Count == 0) _timer?.Stop();
        }

        // Tests only: other suites leave orbs with a flyout up and never hide
        // them, so "the timer stops when the last owner leaves" can only be
        // asserted from a known-empty start.
        internal static void ResetForTests()
        {
            Watched.Clear();
            _timer?.Stop();
        }

        internal static void Tick()
        {
            if (Watched.Count == 0)
            {
                _timer?.Stop();
                return;
            }

            if (CursorProbe.Read() is null) return;

            // A snapshot: a Lost handler may unregister itself or another owner.
            foreach (var (owner, entry) in Watched.ToList())
            {
                if (!Watched.ContainsKey(owner)) continue;
                if (!entry.StillHovered()) entry.Lost();
            }
        }
    }
}
