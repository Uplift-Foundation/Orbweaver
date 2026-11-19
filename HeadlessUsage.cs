using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orbweaver
{
    // The usage poll for a Mac that has not unlocked yet (CB-391).
    //
    // A Mac that launches locked, or at the login window, parks its main thread
    // in MacOSScreenLock.WaitForUnlock. Only RelayService.ServeEarly runs ahead
    // of that, and the usage poll used to hang off SessionManager's
    // DispatcherTimer, which only exists once the UI does. So the relay
    // connected, the roster arrived through the headless scan, and the phone
    // never heard a single usage reading from that Mac: 0 `usage accounts`
    // lines across 2h42m of the mini's mirror log, where a polling app writes
    // one every one to five minutes.
    //
    // This is the same poll with no window in it: a UsagePump ticked from a
    // plain Timer, readings merged by AccountOrbs' own static rule and
    // projected by the one projection both halves share. RelayService starts
    // one from ServeEarly, so only while the relay is on — before the unlock
    // the phone is the only thing that could see a reading, and a locked Mac
    // with the relay off has no reason to start `claude -p` every minute. At
    // the unlock Handoff points the pump at the orbs and gives them the pump,
    // the readings and the Grok schedule, so the cadence carries straight
    // across and no read is ever doubled (plan amendment 11430).
    //
    // No Dispatcher, no Avalonia, no settings writes anywhere on this path —
    // the dispatcher is not pumping before the unlock, and every settings
    // setter defers its save through a DispatcherTimer. Every dependency comes
    // in through Seams, so the unit suite drives it with no process, no clock
    // and no settings behind it.
    internal sealed class HeadlessUsage : IDisposable
    {
        internal sealed record Seams(
            IUsageSource Source,
            Func<bool> AnyEnabled,
            Func<(bool Usage, bool AutoRefresh)> GrokSwitches,
            Func<IReadOnlyDictionary<string, AccountUsage>, IReadOnlyList<RelayUsage.Account>> Project,
            Action<IReadOnlyList<RelayUsage.Account>> Send,
            Action RefreshGrok,
            Action<Action> OffThread,
            Func<DateTimeOffset> Now,
            Action<string> Log);

        // The wiring the app runs with, around whatever source it is given.
        // Every arm but the source and the Grok refresh is ordinary glue and is
        // exercised by the UI suite through `Production(fake) with { … }`; the
        // two that start real `claude`, `codex` and Grok processes are the
        // excluded methods below.
        internal static Seams Production(IUsageSource source) => new(
            source,
            AccountOrbs.AnyEnabled,
            () => (OrbweaverSettings.GrokAccountUsageEnabled, OrbweaverSettings.GrokAutoRefreshEnabled),
            readings => AccountOrbs.RelayAccountsOf(readings),
            RelayService.OnUsage,
            RealGrokRefresh,
            work => Task.Run(work),
            () => DateTimeOffset.UtcNow,
            line => MirrorLog.Say("relay", line));

        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static IUsageSource RealSource() =>
            new CompositeUsageSource(new UsagePoller(), new GrokUsagePoller(), new CodexUsagePoller());

        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static void RealGrokRefresh() => GrokUsageRefresher.Refresh();

        private readonly object _gate = new();
        private readonly Seams _seams;
        private readonly UsagePump _pump;
        private readonly GrokUsageRefreshScheduler _grok = new();
        private readonly Dictionary<string, AccountUsage> _readings = new(StringComparer.Ordinal);

        private Timer? _timer;
        private bool _listening;

        // Set by Handoff: where a read that took the headless sink an instant
        // before the swap goes instead. Non-null means handed over.
        private Action<IReadOnlyList<AccountUsage>>? _deliver;

        internal HeadlessUsage(Seams seams)
        {
            _seams = seams;
            _pump = new UsagePump(seams.Source, seams.OffThread,
                e => seams.Log($"headless usage read failed: {e.GetType().Name}"));
            _pump.SwapSink(OnReadings);
        }

        internal UsagePump Pump => _pump;

        internal IReadOnlyDictionary<string, AccountUsage> ReadingsForTests
        {
            get { lock (_gate) return new Dictionary<string, AccountUsage>(_readings, StringComparer.Ordinal); }
        }

        // The timer, and the icon-bundle event a hash reaches the phone by.
        // The first tick waits (5 s in production) so an ordinary unlocked
        // launch, whose Attach follows within a second or two, hands over an
        // untouched pump rather than one with a read already in flight.
        internal void Start(TimeSpan firstTick, TimeSpan every)
        {
            lock (_gate)
            {
                if (_deliver is not null || _timer is not null) return;
                PersonaImageStore.Accounts.Built += Reproject;
                _listening = true;
                _timer = new Timer(_ => Tick(), null, firstTick, every);
            }
        }

        // One timer tick. Never throws — it runs on a Timer that discards
        // exceptions, and a timer that has silently stopped is undiagnosable
        // (the rule RelayService.HeadlessTick already follows).
        internal void Tick()
        {
            try
            {
                if (!_seams.AnyEnabled())
                {
                    // AccountOrbs.Tick closes every orb here. With no orbs to
                    // close, the equivalent is to forget what was read, so a
                    // switch turned back on starts clean.
                    lock (_gate) if (_deliver is null) _readings.Clear();
                    return;
                }

                var now = _seams.Now();
                bool refresh;
                lock (_gate)
                {
                    if (_deliver is not null) return;

                    // The same two switches and the same twenty-minute floor
                    // SessionManager.Start uses; the scheduler goes to it at
                    // the unlock so the floor carries.
                    var (usage, auto) = _seams.GrokSwitches();
                    refresh = _grok.Tick(now, usage, auto);
                }

                _pump.Tick(now);

                // Off this thread for SessionManager's reason: the refresh
                // holds a real Grok process open for seconds. It is a pty
                // launch with no window in it, so a locked Mac can run it.
                if (refresh) _seams.OffThread(_seams.RefreshGrok);
            }
            catch (Exception e) { Fail(e); }
        }

        // A finished read: merged by AccountOrbs' rule, the cadence moved by
        // the same rule Apply uses, and the projection sent to the relay —
        // which logs `relay usage accounts …` and lets the tracker on the
        // early client decide whether the phone needs a frame.
        //
        // The merge is under the gate Handoff takes. A read that took this
        // sink just before the swap and arrives just after it would otherwise
        // be merged into readings Handoff has already copied, and the orbs
        // would never see it; instead it goes on to them.
        private void OnReadings(IReadOnlyList<AccountUsage> readings)
        {
            try
            {
                Action<IReadOnlyList<AccountUsage>>? deliver;
                IReadOnlyList<RelayUsage.Account>? accounts = null;
                lock (_gate)
                {
                    deliver = _deliver;
                    if (deliver is null)
                    {
                        var changed = AccountOrbs.MergeReadings(_readings, readings);
                        _pump.Interval = UsagePollCadence.Next(_pump.Interval, changed);
                        accounts = _seams.Project(_readings);
                    }
                }

                if (deliver is not null) deliver(readings);
                else _seams.Send(accounts!);
            }
            catch (Exception e) { Fail(e); }
        }

        // An account icon's bundle finished building, which is the first
        // moment its hash can be offered to the phone. AccountOrbs answers the
        // same event the same way once it owns the readings.
        internal void Reproject()
        {
            try
            {
                IReadOnlyList<RelayUsage.Account> accounts;
                lock (_gate)
                {
                    if (_deliver is not null || _readings.Count == 0) return;
                    accounts = _seams.Project(_readings);
                }

                _seams.Send(accounts);
            }
            catch (Exception e) { Fail(e); }
        }

        // The unlock (plan amendment 11430). Under the gate: stop the timer,
        // stop listening, and swap the pump's sink to `deliver` under the
        // pump's own lock, so a read in flight lands in exactly one place.
        // Returns what the orbs and SessionManager take over, or null if this
        // was already handed over.
        internal UsageHandoff? Handoff(Action<IReadOnlyList<AccountUsage>> deliver)
        {
            lock (_gate)
            {
                if (_deliver is not null) return null;
                _deliver = deliver;
                StopLocked();
                _pump.SwapSink(deliver);
                return new UsageHandoff(
                    _pump,
                    new Dictionary<string, AccountUsage>(_readings, StringComparer.Ordinal),
                    _grok);
            }
        }

        public void Dispose()
        {
            lock (_gate) StopLocked();
        }

        private void StopLocked()
        {
            _timer?.Dispose();
            _timer = null;
            if (_listening) PersonaImageStore.Accounts.Built -= Reproject;
            _listening = false;
        }

        private void Fail(Exception e) => _seams.Log($"headless usage failed: {e.GetType().Name}");
    }

    // What the orbs and SessionManager take over at the unlock: the pump
    // itself (its floor and cadence are never copied), the readings already
    // taken, and the Grok schedule so its twenty-minute floor carries.
    internal sealed record UsageHandoff(
        UsagePump Pump,
        IReadOnlyDictionary<string, AccountUsage> Readings,
        GrokUsageRefreshScheduler Grok);
}
