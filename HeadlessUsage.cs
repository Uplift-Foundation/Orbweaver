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
    // plain Timer, readings merged and projected by AccountOrbs' own static
    // rules, and the result handed to the relay the way AccountOrbs.Apply hands
    // it. Started by ServeEarly, so only while the relay is on — before the
    // unlock the phone is the only thing that could see a reading, and a
    // locked Mac with the relay off has no reason to start `claude -p` every
    // minute. At the unlock RelayService.Attach takes the pump, the readings
    // and the Grok schedule off this class and gives them to the orbs, so the
    // cadence carries straight across and no read is ever doubled.
    //
    // No Dispatcher, no Avalonia, no settings writes anywhere on this path —
    // the dispatcher is not pumping before the unlock, and every settings
    // setter defers its save through a DispatcherTimer.
    internal static class HeadlessUsage
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<string, AccountUsage> Readings = new(StringComparer.Ordinal);

        private static Timer? _timer;
        private static UsagePump? _pump;
        private static GrokUsageRefreshScheduler? _grok;
        private static UsageLateReadings? _late;
        private static bool _handedOff;

        // Seams, each replaced by tests: the source a read goes to, where it
        // runs, the Grok refresh, the clock, and the tick period. The defaults
        // start real `claude`, `codex` and Grok processes, which no test may.
        internal static Func<IUsageSource> Source { get; set; } = RealSource;
        internal static Action<Action> OffThread { get; set; } = DefaultOffThread;
        internal static Action GrokRefresh { get; set; } = RealGrokRefresh;
        internal static Func<DateTimeOffset> Now { get; set; } = DefaultNow;

        // Where a projection goes: the relay, the same call AccountOrbs'
        // UsageApplied reaches after the unlock.
        internal static Action<IReadOnlyList<RelayUsage.Account>> Send { get; set; } = RelayService.OnUsage;

        // SessionManager's two-second tick, so both sides of the unlock are
        // asked "is it time?" equally often.
        internal static TimeSpan Every { get; set; } = DefaultEvery;

        private static readonly TimeSpan DefaultEvery = TimeSpan.FromSeconds(2);

        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static IUsageSource RealSource() =>
            new CompositeUsageSource(new UsagePoller(), new GrokUsagePoller(), new CodexUsagePoller());

        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        internal static void RealGrokRefresh() => GrokUsageRefresher.Refresh();

        private static void DefaultOffThread(Action work) => Task.Run(work);

        private static DateTimeOffset DefaultNow() => DateTimeOffset.UtcNow;

        internal static bool Started { get { lock (Gate) return _pump is not null && !_handedOff; } }

        // From ServeEarly, inside its own guard. Once only, and never after a
        // handover: a feed restarted behind the orbs' back would be a second
        // poll against the same accounts.
        internal static void Start()
        {
            lock (Gate)
            {
                if (_pump is not null || _handedOff) return;
                var pump = new UsagePump(Source(), OffThread);
                pump.Sink = readings => OnReadings(pump, readings);
                _pump = pump;
                _grok = new GrokUsageRefreshScheduler();
                PersonaImageStore.Accounts.Built += OnIconBuilt;
                _timer = new Timer(_ => Tick(), null, Every, Every);
            }
        }

        // One timer tick. Never throws — it runs on a Timer that discards
        // exceptions, and a timer that has silently stopped is undiagnosable
        // (the rule RelayService.HeadlessTick already follows).
        internal static void Tick()
        {
            try
            {
                if (!AccountOrbs.AnyEnabled()) return;

                var now = Now();
                UsagePump pump;
                bool refresh;
                lock (Gate)
                {
                    if (_handedOff || _pump is null) return;
                    pump = _pump;

                    // The same two switches SessionManager.Start asks. The
                    // refresh is a pty launch with no window in it, so a locked
                    // Mac can run it as well as an unlocked one.
                    refresh = _grok!.Tick(
                        now,
                        OrbweaverSettings.GrokAccountUsageEnabled,
                        OrbweaverSettings.GrokAutoRefreshEnabled);
                }

                pump.Tick(now);

                // Off this thread for SessionManager's reason: the refresh holds
                // a real Grok process open for seconds.
                if (refresh) OffThread(GrokRefresh);
            }
            catch (Exception e) { Say(e); }
        }

        // A finished read: merged and projected exactly as AccountOrbs.Apply
        // does it, the cadence moved by the same rule, and the result given to
        // the relay — which is what logs `relay usage accounts …` and decides,
        // through the tracker the early client already holds, whether the
        // phone needs a frame at all.
        //
        // A read that finishes after the handover goes on to the orbs through
        // Late instead, so it is neither dropped nor sent from here as well.
        private static void OnReadings(UsagePump pump, IReadOnlyList<AccountUsage> readings)
        {
            try
            {
                IReadOnlyList<RelayUsage.Account>? accounts = null;
                UsageLateReadings? late;
                lock (Gate)
                {
                    late = _late;
                    if (late is null)
                    {
                        var changed = AccountOrbs.MergeReadings(Readings, readings);
                        pump.Interval = UsagePollCadence.Next(pump.Interval, changed);
                        accounts = AccountOrbs.RelayAccountsOf(Readings);
                    }
                }

                if (late is not null) late.Accept(readings);
                else Send(accounts!);
            }
            catch (Exception e) { Say(e); }
        }

        // An account icon's bundle finished building, which is the first
        // moment its hash can be offered to the phone. AccountOrbs answers the
        // same event the same way once it owns the readings.
        private static void OnIconBuilt()
        {
            try
            {
                IReadOnlyList<RelayUsage.Account> accounts;
                lock (Gate)
                {
                    if (_handedOff || Readings.Count == 0) return;
                    accounts = AccountOrbs.RelayAccountsOf(Readings);
                }

                Send(accounts);
            }
            catch (Exception e) { Say(e); }
        }

        // The unlock. Stops the timer and gives back everything the orbs need
        // to carry on where this left off, or null when the feed never started
        // (the relay was off, or the Mac was unlocked at launch and Attach ran
        // before ServeEarly's timer did anything worth handing over).
        internal static UsageHandoff? Handoff()
        {
            lock (Gate)
            {
                if (_handedOff) return null;
                _handedOff = true;
                _timer?.Dispose();
                _timer = null;
                if (_pump is null) return null;

                PersonaImageStore.Accounts.Built -= OnIconBuilt;
                _late = new UsageLateReadings();
                return new UsageHandoff(
                    _pump,
                    new Dictionary<string, AccountUsage>(Readings, StringComparer.Ordinal),
                    _grok!,
                    _late);
            }
        }

        private static void Say(Exception e) =>
            MirrorLog.Say("relay", $"headless usage failed: {e.GetType().Name}");

        // Called from RelayService.ResetHeadlessForTests, so a timer started by
        // one test cannot tick into the next.
        internal static void ResetForTests()
        {
            lock (Gate)
            {
                _timer?.Dispose();
                _timer = null;
                if (_pump is not null && !_handedOff) PersonaImageStore.Accounts.Built -= OnIconBuilt;
                _pump = null;
                _grok = null;
                _late = null;
                _handedOff = false;
                Readings.Clear();
            }

            Source = RealSource;
            OffThread = DefaultOffThread;
            GrokRefresh = RealGrokRefresh;
            Now = DefaultNow;
            Send = RelayService.OnUsage;
            Every = DefaultEvery;
        }

        // A test's way to raise the icon-built event's handler without
        // building a real bundle.
        internal static void IconBuiltForTests() => OnIconBuilt();

        internal static IReadOnlyDictionary<string, AccountUsage> ReadingsForTests
        {
            get { lock (Gate) return new Dictionary<string, AccountUsage>(Readings, StringComparer.Ordinal); }
        }

        internal static UsagePump? PumpForTests { get { lock (Gate) return _pump; } }

    }
}
