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

        private DateTimeOffset _lastPoll = DateTimeOffset.MinValue;

        // Starts fast for the reason AccountOrbs always did: a freshly started
        // poll knows nothing about whether the account is mid-burst.
        private TimeSpan _interval = UsagePollCadence.Fast;

        private bool _polling;
        private Action<IReadOnlyList<AccountUsage>> _sink = _ => { };

        // offThread defaults to the pool, because a read starts a process per
        // account and waits seconds for each. A test hands in an inline or a
        // captured runner instead, so nothing here ever has to be slept on.
        internal UsagePump(IUsageSource source, Action<Action>? offThread = null)
        {
            _source = source;
            _offThread = offThread ?? (work => Task.Run(work));
        }

        // Where a finished read goes. Read when the read finishes, not when it
        // starts, so a sink swapped while a read is in flight — the unlock
        // handover — receives that read rather than the one it replaced.
        internal Action<IReadOnlyList<AccountUsage>> Sink
        {
            get { lock (_gate) return _sink; }
            set { lock (_gate) _sink = value; }
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
            catch { readings = Array.Empty<AccountUsage>(); }

            Action<IReadOnlyList<AccountUsage>> sink;
            lock (_gate)
            {
                _polling = false;
                sink = _sink;
            }

            sink(readings);
        }
    }

    // What HeadlessUsage gives AccountOrbs at the unlock: the pump itself (not
    // its numbers), the readings already taken, the Grok refresh schedule so
    // its twenty-minute floor carries, and Late, which holds a read that was
    // already on its way to the headless sink when the handover happened.
    internal sealed record UsageHandoff(
        UsagePump Pump,
        IReadOnlyDictionary<string, AccountUsage> Readings,
        GrokUsageRefreshScheduler Grok,
        UsageLateReadings Late);

    // A read that finished between the handover and the adopter pointing the
    // pump at itself. The pump reads its sink once a read completes, so a read
    // that completes in that gap still goes to the headless sink, which then
    // passes it here. Held until Attach names a target, then delivered: never
    // dropped, never sent to the phone a second time.
    internal sealed class UsageLateReadings
    {
        private readonly object _gate = new();
        private readonly List<IReadOnlyList<AccountUsage>> _held = new();
        private Action<IReadOnlyList<AccountUsage>>? _target;

        internal void Accept(IReadOnlyList<AccountUsage> readings)
        {
            Action<IReadOnlyList<AccountUsage>> target;
            lock (_gate)
            {
                if (_target is null)
                {
                    _held.Add(readings);
                    return;
                }
                target = _target;
            }

            target(readings);
        }

        internal void Attach(Action<IReadOnlyList<AccountUsage>> target)
        {
            List<IReadOnlyList<AccountUsage>> held;
            lock (_gate)
            {
                _target = target;
                held = new List<IReadOnlyList<AccountUsage>>(_held);
                _held.Clear();
            }

            foreach (var readings in held) target(readings);
        }
    }
}
