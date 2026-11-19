using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orbweaver
{
    // The usage poll's cadence and nothing else: when the next read is due,
    // whether one is already in flight, and where its readings go.
    //
    // Pulled out of AccountOrbs for CB-391. A Mac that launches locked (or at
    // the login window) parks its main thread in MacOSScreenLock.WaitForUnlock,
    // and AccountOrbs was only ever ticked by SessionManager's DispatcherTimer,
    // which does not exist until the UI starts. So the phone heard nothing
    // about usage from a locked Mac for as long as it stayed locked — 0 lines
    // in 2h42m on the mini. HeadlessUsage now ticks one of these from a plain
    // Timer before the unlock, and hands the same object to AccountOrbs after
    // it. One object on both sides of the unlock is the point: _lastPoll and
    // _interval are never copied, so the UI's first poll lands when the
    // headless one would have, and there is one _polling flag, so two reads
    // can never overlap however the handover interleaves with a read.
    //
    // Pure on purpose — no settings, no Avalonia, no Dispatcher. The caller
    // decides whether usage is wanted at all; this only decides whether it is
    // time. Same split as UsagePollCadence, which decides how long the wait is.
    internal sealed class UsagePump
    {
        private readonly object _gate = new();
        private readonly IUsageSource _source;
        private readonly Action<Action> _offThread;
        private readonly Action<Exception>? _failed;

        private DateTimeOffset _lastPoll = DateTimeOffset.MinValue;

        // Starts fast for the reason AccountOrbs always did: a freshly started
        // poll knows nothing about whether the account is mid-burst.
        private TimeSpan _interval = UsagePollCadence.Fast;

        private bool _polling;
        private Action<IReadOnlyList<AccountUsage>> _sink = _ => { };

        // offThread defaults to the pool, because a read starts a process per
        // account and waits seconds for each. A test hands in an inline or a
        // captured runner instead, so nothing here ever has to be slept on.
        // failed hears a read that threw; the read still counts as empty.
        internal UsagePump(IUsageSource source, Action<Action>? offThread = null, Action<Exception>? failed = null)
        {
            _source = source;
            _offThread = offThread ?? (work => Task.Run(work));
            _failed = failed;
        }

        // Where finished reads go from now on. The sink is read under the same
        // lock when a read finishes, not when it starts, so the swap is atomic
        // with completion: a read in flight at the unlock handover lands in
        // exactly one place — the old sink if it finished first, the new one
        // otherwise.
        internal void SwapSink(Action<IReadOnlyList<AccountUsage>> sink)
        {
            lock (_gate) _sink = sink;
        }

        // The sink a completing read would call right now. Lets a test stand
        // in the one interleaving a runner cannot reach: a read that took the
        // old sink an instant before the swap and calls it an instant after.
        internal Action<IReadOnlyList<AccountUsage>> CurrentSinkForTests
        {
            get { lock (_gate) return _sink; }
        }

        internal TimeSpan Interval
        {
            get { lock (_gate) return _interval; }
            set { lock (_gate) _interval = value; }
        }

        internal bool Polling { get { lock (_gate) return _polling; } }

        // A switch that just came on: the next tick is due at once, and back
        // at the fast cadence, whatever the previous source had backed off to.
        internal void Reset()
        {
            lock (_gate)
            {
                _lastPoll = DateTimeOffset.MinValue;
                _interval = UsagePollCadence.Fast;
            }
        }

        // Starts a read if one is due and none is running. The stamp and the
        // flag are set before the read is handed off, so a second tick racing
        // this one — a timer thread can fire twice before the first returns —
        // sees a read in flight and says no.
        internal bool Tick(DateTimeOffset now)
        {
            lock (_gate)
            {
                if (_polling || now - _lastPoll < _interval) return false;
                _lastPoll = now;
                _polling = true;
            }

            _offThread(Run);
            return true;
        }

        private void Run()
        {
            IReadOnlyList<AccountUsage> readings;
            try { readings = _source.Read(); }
            catch (Exception e)
            {
                readings = Array.Empty<AccountUsage>();
                _failed?.Invoke(e);
            }

            Action<IReadOnlyList<AccountUsage>> sink;
            lock (_gate)
            {
                _polling = false;
                sink = _sink;
            }

            sink(readings);
        }
    }
}
