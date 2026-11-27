using System.Diagnostics.CodeAnalysis;
using Avalonia.Threading;

namespace Orbweaver
{
    // The tug: a team member's thread shivers when its session is doing something,
    // the way a spider's strand does when there is prey on it.
    //
    // Two things move it. A member that is generating gets a tug every couple of
    // seconds for as long as it is, so a team at work is visibly alive along its
    // silk; and a member that has just started waiting on the user gets one bigger
    // tug, once, because that is the moment worth pulling on the lead for.
    //
    // The decisions — when to tug, how hard, and what the thread looks like at any
    // moment of a tug — are pure and take the clock as an argument. Only the timer
    // that keeps frames coming while anything is moving is not, and it is excluded
    // from coverage for the same reason every other real timer in this app is.
    internal static class ThreadTug
    {
        // Seconds between tugs on a member that stays in the generating state.
        internal const double Period = 2.4;

        // How long one tug rings for, and how many swings it makes in that time.
        internal const double Duration = 0.9;
        internal const double Swings = 3;

        // Peak sideways displacement of a thread's middle, in DIPs, for an
        // ordinary tug. A waiting tug is this times WaitingStrength.
        internal const double Amplitude = 3.5;
        internal const double WaitingStrength = 1.8;

        // How fast a tug dies away: the ring is e^-Decay down by the time it ends.
        private const double Decay = 3.5;

        // How hard to tug now, as a multiple of Amplitude, or 0 for not at all.
        //
        // `previous` is the member's state when it was last asked about, and
        // `sinceLastTugSeconds` how long ago it last tugged (infinity if never).
        // Generating tugs on a beat; arriving at waiting tugs once, harder.
        internal static double Strength(string? previous, string? state, double sinceLastTugSeconds)
        {
            if (state == "waiting") return previous == "waiting" ? 0 : WaitingStrength;
            if (state != "generating") return 0;

            return sinceLastTugSeconds >= Period ? 1 : 0;
        }

        // The sideways offset of a thread's middle, signed, `sinceTugSeconds`
        // after a tug of the given strength. A damped swing: it starts at rest,
        // goes out one side first, and has stopped by Duration.
        internal static double Offset(double sinceTugSeconds, double strength)
        {
            if (strength <= 0 || sinceTugSeconds < 0 || sinceTugSeconds >= Duration) return 0;

            var t = sinceTugSeconds / Duration;
            return strength * Amplitude * Math.Exp(-Decay * t) * Math.Sin(Math.PI * 2 * Swings * t);
        }

        // How strongly a thread is lit by a tug, 0 to 1: bright at the pull and
        // fading with the ring. What a tugged thread is drawn a little stronger by.
        internal static double Glow(double sinceTugSeconds, double strength)
        {
            if (strength <= 0 || sinceTugSeconds < 0 || sinceTugSeconds >= Duration) return 0;

            return Math.Min(1, strength) * Math.Exp(-Decay * sinceTugSeconds / Duration);
        }

        // ---- the part that is not pure ---------------------------------------

        private sealed class Tugged
        {
            public string? State;
            public long LastTugMs = long.MinValue;
            public double Strength;

            // The user has dealt with this member: it stays quiet for as long as
            // it stays in the state it was acknowledged in.
            public bool Acked;
        }

        private static readonly Dictionary<object, Tugged> Members = new();
        private static Func<long> _clock = () => Environment.TickCount64;

        // A live timer, which only exists to call Refresh while something is
        // moving and stops itself the moment nothing is. Drive, which owns it, is
        // what is excluded from coverage.
        private static DispatcherTimer? _timer;

        // Test seam: where "now" comes from, in milliseconds. Null restores it.
        internal static Func<long>? Clock { set => _clock = value ?? (() => Environment.TickCount64); }

        // Forgets every member, so one test's orbs are not the next one's.
        internal static Action<string>? Trace;
        private static void T(string what) => Trace?.Invoke(what + " clock=" + _clock() + " rm=" + MotionPolicy.ReduceMotion + "\n" + Environment.StackTrace);

        internal static string Describe(object member) =>
            Members.TryGetValue(member, out var e)
                ? $"present Acked={e.Acked} State={e.State} LastTugMs={e.LastTugMs} Strength={e.Strength} count={Members.Count}"
                : $"absent count={Members.Count}";

        internal static void Reset()
        {
            if (Members.Count > 0) T("Reset");
            Members.Clear();
        }

        // Tells the tug what state a member is in now, and starts one if that is
        // what its state calls for. Cheap enough to call on every refresh.
        internal static void Observe(object member, string? state)
        {
            if (MotionPolicy.ReduceMotion)
            {
                T("Observe-reduce-motion-remove");
                Members.Remove(member);
                return;
            }

            var now = _clock();

            if (!Members.TryGetValue(member, out var entry)) Members[member] = entry = new Tugged();

            // A new state is a new thing to tug about: acknowledging a member
            // silences the state it was in, not the member for good.
            if (entry.Acked && entry.State != state) { T($"unack {entry.State}->{state}"); entry.Acked = false; }

            var since = entry.LastTugMs == long.MinValue ? double.PositiveInfinity : (now - entry.LastTugMs) / 1000.0;
            var strength = entry.Acked ? 0 : Strength(entry.State, state, since);

            if (strength > 0)
            {
                entry.LastTugMs = now;
                entry.Strength = strength;
            }

            entry.State = state;
        }

        // The user has interacted with this member — clicked its orb, opened its
        // chat, used its flyout — so the tug has done its job. Whatever is ringing
        // stops now, and it stays quiet until the member's state changes: an agent
        // that is still generating after you have looked at it does not keep
        // pulling on you, and one that finishes and starts again does.
        //
        // A member nothing has observed yet has no state to be quiet in, so there
        // is nothing to acknowledge.
        internal static void Acknowledge(object member)
        {
            if (!Members.TryGetValue(member, out var entry)) { T("Acknowledge-unknown"); return; }

            T("Acknowledge");
            entry.Acked = true;
            entry.LastTugMs = long.MinValue;
            entry.Strength = 0;
        }

        // Keeps only the members still on screen. Without it a member that left —
        // an agent that ended — would sit here in whatever state it was last seen
        // in, and one last seen generating would ask for frames for ever.
        internal static void Retain(IEnumerable<object> present)
        {
            var keep = new HashSet<object>(present);

            foreach (var member in Members.Keys.Where(m => !keep.Contains(m)).ToList()) { T("Retain-remove"); Members.Remove(member); }
        }

        // Where a member's thread is right now, as (offset, glow).
        internal static (double Offset, double Glow) Now(object member)
        {
            if (!Members.TryGetValue(member, out var entry) || entry.LastTugMs == long.MinValue) return (0, 0);

            var since = (_clock() - entry.LastTugMs) / 1000.0;
            return (Offset(since, entry.Strength), Glow(since, entry.Strength));
        }

        // Whether anything needs another frame: a member still generating (its
        // next tug is coming) or a tug still ringing.
        internal static bool NeedsFrames()
        {
            var now = _clock();

            foreach (var entry in Members.Values)
            {
                if (entry.State == "generating" && !entry.Acked) return true;
                if (entry.LastTugMs != long.MinValue && (now - entry.LastTugMs) / 1000.0 < Duration) return true;
            }

            return false;
        }

        // Keeps frames coming while anything moves, and lets go when nothing
        // does, so an idle team costs no timer at all.
        [ExcludeFromCodeCoverage]
        internal static void Drive(Action refresh)
        {
            if (!NeedsFrames())
            {
                _timer?.Stop();
                return;
            }

            if (_timer is { IsEnabled: true }) return;

            _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => refresh());
            _timer.Start();
        }
    }
}
