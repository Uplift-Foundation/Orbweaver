using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-391: the pre-unlock usage poll with every dependency faked — no process,
// no clock, no settings, no relay. The UI suite drives the same class through
// RelayService's real wiring; this one owns the decisions, with a case per
// outcome and a negative control beside each positive.
//
// Reads go through a captured runner, so "a read in flight" is a state a test
// stands in rather than a race it has to win. The one interleaving a runner
// cannot reach — a read that took the headless sink an instant before the
// handover swapped it, and calls it an instant after — is reproduced exactly
// by taking the sink off the pump before Handoff and calling it after.
public class HeadlessUsageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 4, 0, 0, TimeSpan.Zero);

    private readonly FakeSource _source = new();
    private readonly Held _held = new();
    private readonly List<IReadOnlyList<RelayUsage.Account>> _sent = new();
    private readonly List<string> _log = new();
    private DateTimeOffset _now = T0;
    private bool _enabled = true;
    private (bool Usage, bool Auto) _grok = (false, false);
    private int _refreshes;
    private bool _inline = true;
    private Action<IReadOnlyList<RelayUsage.Account>>? _send;
    private Func<DateTimeOffset>? _clock;

    private sealed class FakeSource : IUsageSource
    {
        private int _reads;
        internal IReadOnlyList<AccountUsage> Readings { get; set; } = [];
        internal Exception? Throw { get; set; }
        internal int Reads => Volatile.Read(ref _reads);

        public IReadOnlyList<AccountUsage> Read()
        {
            Interlocked.Increment(ref _reads);
            if (Throw is not null) throw Throw;
            return Readings;
        }
    }

    private sealed class Held
    {
        internal readonly List<Action> Pending = new();
        internal void Run(Action work) => Pending.Add(work);
        internal void Release()
        {
            var work = Pending.ToArray();
            Pending.Clear();
            foreach (var w in work) w();
        }
    }

    private static IReadOnlyList<RelayUsage.Account> Project(IReadOnlyDictionary<string, AccountUsage> readings) =>
        readings.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => RelayUsage.From(p.Value)).ToList();

    private HeadlessUsage Make() => new(new HeadlessUsage.Seams(
        _source,
        () => _enabled,
        () => _grok,
        Project,
        a => (_send ?? _sent.Add)(a),
        () => _refreshes++,
        work => { if (_inline) work(); else _held.Run(work); },
        () => (_clock ?? (() => _now))(),
        _log.Add));

    private static AccountUsage Usage(string? dir, double session = 10, string label = "board") =>
        new(dir, label, true, "team", new UsageWindow(session, T0.AddHours(3)),
            new UsageWindow(40, T0.AddDays(3)), null, T0);

    // ---- the poll ---------------------------------------------------------------

    [Fact]
    public void A_due_tick_reads_once_and_sends_the_projection()
    {
        _source.Readings = [Usage("/a", label: "parked")];
        var feed = Make();

        feed.Tick();

        Assert.Equal(1, _source.Reads);
        Assert.Equal("parked", Assert.Single(Assert.Single(_sent)).Name);
    }

    [Fact]
    public void A_second_tick_inside_the_interval_reads_nothing()
    {
        var feed = Make();
        feed.Tick();
        _now = T0 + UsagePollCadence.Fast - TimeSpan.FromSeconds(1);

        feed.Tick();

        Assert.Equal(1, _source.Reads);
    }

    [Fact]
    public void A_tick_while_a_read_is_in_flight_starts_no_second_read()
    {
        _inline = false;
        var feed = Make();
        feed.Tick();
        _now = T0.AddHours(1);

        feed.Tick();

        Assert.Single(_held.Pending);
        _held.Release();
        Assert.Equal(1, _source.Reads);
    }

    [Fact]
    public void The_cadence_backs_off_on_the_same_numbers_and_resets_on_a_change()
    {
        _source.Readings = [Usage("/a", session: 10)];
        var feed = Make();

        feed.Tick();
        Assert.Equal(UsagePollCadence.Fast, feed.Pump.Interval);

        _now = T0 + UsagePollCadence.Fast;
        feed.Tick();
        Assert.Equal(TimeSpan.FromSeconds(120), feed.Pump.Interval);

        _now += TimeSpan.FromSeconds(120);
        _source.Readings = [Usage("/a", session: 25)];
        feed.Tick();
        Assert.Equal(UsagePollCadence.Fast, feed.Pump.Interval);
    }

    [Fact]
    public void A_throwing_source_is_an_empty_read_and_is_logged()
    {
        _source.Throw = new InvalidOperationException("no claude");
        var feed = Make();

        feed.Tick();

        Assert.Contains("headless usage read failed: InvalidOperationException", _log);
        Assert.Empty(Assert.Single(_sent));
        Assert.False(feed.Pump.Polling);
    }

    // Negative control: every source off is no read, nothing sent, and what
    // was held is forgotten.
    [Fact]
    public void Every_source_off_reads_nothing_sends_nothing_and_drops_held_readings()
    {
        _source.Readings = [Usage("/a")];
        var feed = Make();
        feed.Tick();
        _sent.Clear();

        _enabled = false;
        _now = T0.AddHours(1);
        feed.Tick();

        Assert.Equal(1, _source.Reads);
        Assert.Empty(_sent);
        Assert.Empty(feed.ReadingsForTests);
    }

    // ---- Grok (plan decision 11428 §1) ---------------------------------------

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Grok_refresh_needs_both_switches(bool usage, bool auto)
    {
        _grok = (usage, auto);
        Make().Tick();
        Assert.Equal(0, _refreshes);
    }

    [Fact]
    public void Grok_refresh_runs_once_with_both_switches_and_not_again_inside_the_floor()
    {
        _grok = (true, true);
        var feed = Make();

        feed.Tick();
        Assert.Equal(1, _refreshes);

        _now = T0 + GrokUsageRefresher.MinimumInterval - TimeSpan.FromSeconds(1);
        feed.Tick();
        Assert.Equal(1, _refreshes);

        _now = T0 + GrokUsageRefresher.MinimumInterval;
        feed.Tick();
        Assert.Equal(2, _refreshes);
    }

    // ---- reprojection ------------------------------------------------------------

    [Fact]
    public void Reproject_sends_the_held_readings_and_nothing_when_none_are_held()
    {
        var feed = Make();
        feed.Reproject();
        Assert.Empty(_sent);

        _source.Readings = [Usage("/a")];
        feed.Tick();
        feed.Reproject();

        Assert.Equal(2, _sent.Count);
        Assert.Equal(_sent[0].Select(a => a.Shape), _sent[1].Select(a => a.Shape));
    }

    // ---- never throws ------------------------------------------------------------

    [Fact]
    public void A_throwing_send_clock_or_reprojection_is_logged_and_swallowed()
    {
        _source.Readings = [Usage("/a")];
        _send = _ => throw new System.IO.IOException("relay down");
        var feed = Make();

        feed.Tick();
        feed.Reproject();
        _clock = () => throw new TimeoutException("clock");
        feed.Tick();

        Assert.Equal(2, _log.Count(l => l == "headless usage failed: IOException"));
        Assert.Contains("headless usage failed: TimeoutException", _log);
    }

    // ---- the timer ---------------------------------------------------------------

    [Fact]
    public void Start_ticks_on_its_own_once_and_Dispose_stops_it()
    {
        var feed = Make();
        feed.Start(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(10));
        feed.Start(TimeSpan.Zero, TimeSpan.FromMilliseconds(1));    // already started: ignored

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_source.Reads == 0 && DateTime.UtcNow < deadline) Thread.Sleep(5);
        feed.Dispose();

        Assert.Equal(1, _source.Reads);                              // one read: the floor held it
    }

    [Fact]
    public void Start_after_Handoff_starts_nothing()
    {
        var feed = Make();
        feed.Handoff(_ => { });

        feed.Start(TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        Thread.Sleep(50);

        Assert.Equal(0, _source.Reads);
    }

    // ---- the handover (plan amendment 11430) ---------------------------------------

    [Fact]
    public void Handoff_while_idle_returns_the_pump_readings_and_scheduler_and_stops_the_feed()
    {
        _source.Readings = [Usage("/a")];
        var feed = Make();
        feed.Tick();

        var h = feed.Handoff(_ => { });

        Assert.NotNull(h);
        Assert.Same(feed.Pump, h!.Pump);
        Assert.True(h.Readings.ContainsKey("/a"));
        Assert.False(h.Pump.Polling);
        Assert.Equal(UsagePollCadence.Fast, h.Pump.Interval);

        _now = T0.AddHours(1);
        feed.Tick();
        feed.Reproject();
        Assert.Equal(1, _source.Reads);
        Assert.Single(_sent);
        Assert.Null(feed.Handoff(_ => { }));                       // only once
    }

    // The scheduler handed over is the one that already refreshed, so the
    // receiving side does not refresh again inside the floor; at the floor
    // it does, which shows the handover did not just switch refresh off.
    [Fact]
    public void Handoff_carries_the_Grok_last_refresh()
    {
        _grok = (true, true);
        var feed = Make();
        feed.Tick();
        Assert.Equal(1, _refreshes);

        var grok = feed.Handoff(_ => { })!.Grok;

        Assert.False(grok.Tick(T0.AddMinutes(5), true, true));
        Assert.True(grok.Tick(T0 + GrokUsageRefresher.MinimumInterval, true, true));
    }

    // Race, order 1: the read finishes before the handover. It is merged
    // headless and sent, and Handoff's copy of the readings carries it.
    [Fact]
    public void A_read_finishing_before_Handoff_is_sent_headless_and_carried_in_the_readings()
    {
        _inline = false;
        _source.Readings = [Usage("/a")];
        var feed = Make();
        feed.Tick();
        var delivered = 0;

        _held.Release();
        var h = feed.Handoff(_ => delivered++);

        Assert.Single(_sent);
        Assert.True(h!.Readings.ContainsKey("/a"));
        Assert.Equal(0, delivered);
    }

    // Race, order 2: the read finishes after the swap, so the pump calls the
    // new sink. It reaches Deliver once and nothing is sent headless.
    [Fact]
    public void A_read_finishing_after_Handoff_reaches_the_new_sink_once_and_is_not_sent()
    {
        _inline = false;
        _source.Readings = [Usage("/a")];
        var feed = Make();
        feed.Tick();
        var delivered = new List<IReadOnlyList<AccountUsage>>();

        var h = feed.Handoff(delivered.Add);
        _held.Release();

        Assert.Single(delivered);
        Assert.Empty(_sent);
        Assert.Empty(h!.Readings);
    }

    // Race, order 3, the one Ines named: the read took the headless sink an
    // instant before the swap and calls it an instant after Handoff copied
    // the readings. The merge is under the gate Handoff took, so it sees the
    // handover and passes the read on rather than merging it where nobody
    // will look again.
    [Fact]
    public void A_read_that_took_the_old_sink_before_the_swap_still_reaches_Deliver_once()
    {
        var feed = Make();
        var old = feed.Pump.CurrentSinkForTests;
        var delivered = new List<IReadOnlyList<AccountUsage>>();

        feed.Handoff(delivered.Add);
        Assert.NotSame(old, feed.Pump.CurrentSinkForTests);
        old([Usage("/a")]);

        Assert.Single(delivered);
        Assert.Empty(_sent);
        Assert.Empty(feed.ReadingsForTests);
    }

    // ---- the relay's tracker across the halves ---------------------------------------

    // The tracker lives on the one early client, so the orbs' first projection
    // of the same readings after the unlock is no change; a moved number is
    // exactly one frame.
    [Fact]
    public void The_tracker_sees_the_same_projection_twice_as_no_change()
    {
        var tracker = new RelayUsageTracker();
        var frames = 0;
        _send = a => frames += tracker.Next(a).Count;
        _source.Readings = [Usage("/a", session: 10)];
        var feed = Make();
        feed.Tick();
        Assert.Equal(1, frames);

        var h = feed.Handoff(_ => { })!;
        frames += tracker.Next(Project(h.Readings)).Count;
        Assert.Equal(1, frames);

        frames += tracker.Next(Project(new Dictionary<string, AccountUsage> { ["/a"] = Usage("/a", session: 30) })).Count;
        Assert.Equal(2, frames);
    }
}
