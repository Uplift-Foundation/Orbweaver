using System;
using System.Collections.Generic;
using Xunit;

namespace Orbweaver.Tests;

// CB-391: the usage poll's cadence on its own, with no orbs, no dispatcher and
// no settings behind it. The pump is what crosses the unlock on a Mac that
// launched locked, so its two promises are the ones these pin: a read is never
// started while another is in flight, and a finished read goes to whichever
// sink is current when it finishes. Reads run through a captured runner, so no
// case here waits on a thread.
public class UsagePumpTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeSource : IUsageSource
    {
        internal List<AccountUsage> Readings { get; } = new();
        internal Exception? Throw { get; set; }
        internal int Reads { get; private set; }

        public IReadOnlyList<AccountUsage> Read()
        {
            Reads++;
            if (Throw is not null) throw Throw;
            return Readings;
        }
    }

    // A runner that holds the work until the test says go, so "in flight" is
    // a state a test can stand in rather than a race it has to win.
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

    private static AccountUsage Usage(string? dir, double weekly = 40) =>
        new(dir, "label", true, "team", new UsageWindow(10, Now.AddHours(3)),
            new UsageWindow(weekly, Now.AddDays(3)), null, Now);

    [Fact]
    public void A_fresh_pump_is_due_at_once_and_starts_fast()
    {
        var source = new FakeSource();
        var pump = new UsagePump(source, work => work());

        Assert.Equal(UsagePollCadence.Fast, pump.Interval);
        Assert.True(pump.Tick(Now));
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public void A_due_tick_reads_once_and_the_sink_gets_the_readings()
    {
        var source = new FakeSource();
        source.Readings.Add(Usage("/a"));
        var pump = new UsagePump(source, work => work());
        IReadOnlyList<AccountUsage>? got = null;
        pump.SwapSink(r => got = r);

        pump.Tick(Now);

        Assert.Same(source.Readings, got);
        Assert.False(pump.Polling);
    }

    [Fact]
    public void A_tick_before_the_floor_reads_nothing()
    {
        var source = new FakeSource();
        var pump = new UsagePump(source, work => work());
        pump.Tick(Now);

        Assert.False(pump.Tick(Now + UsagePollCadence.Fast - TimeSpan.FromSeconds(1)));
        Assert.Equal(1, source.Reads);

        Assert.True(pump.Tick(Now + UsagePollCadence.Fast));
        Assert.Equal(2, source.Reads);
    }

    // The overlap guard: however late the clock runs, a read in flight is
    // never joined by a second one.
    [Fact]
    public void A_tick_while_a_read_is_in_flight_starts_no_second_read()
    {
        var source = new FakeSource();
        var held = new Held();
        var pump = new UsagePump(source, held.Run);

        Assert.True(pump.Tick(Now));
        Assert.True(pump.Polling);
        Assert.False(pump.Tick(Now.AddHours(1)));
        Assert.Single(held.Pending);

        held.Release();
        Assert.False(pump.Polling);
        Assert.True(pump.Tick(Now.AddHours(1)));
    }

    [Fact]
    public void A_throwing_source_delivers_an_empty_read_and_clears_the_flag()
    {
        var source = new FakeSource { Throw = new InvalidOperationException("no claude") };
        var pump = new UsagePump(source, work => work());
        IReadOnlyList<AccountUsage>? got = null;
        pump.SwapSink(r => got = r);

        pump.Tick(Now);

        Assert.NotNull(got);
        Assert.Empty(got!);
        Assert.False(pump.Polling);
    }

    [Fact]
    public void Reset_makes_the_next_tick_due_and_the_cadence_fast()
    {
        var source = new FakeSource();
        var pump = new UsagePump(source, work => work());
        pump.Tick(Now);
        pump.Interval = UsagePollCadence.Slow;

        Assert.False(pump.Tick(Now.AddSeconds(1)));
        pump.Reset();

        Assert.Equal(UsagePollCadence.Fast, pump.Interval);
        Assert.True(pump.Tick(Now.AddSeconds(1)));
    }

    // The handover's whole mechanism: the sink is read when the read
    // finishes, so a swap mid-read reaches the new owner.
    [Fact]
    public void A_sink_swapped_mid_read_receives_that_read()
    {
        var source = new FakeSource();
        var held = new Held();
        var pump = new UsagePump(source, held.Run);
        var old = 0;
        var fresh = 0;
        pump.SwapSink(_ => old++);

        pump.Tick(Now);
        pump.SwapSink(_ => fresh++);
        held.Release();

        Assert.Equal(0, old);
        Assert.Equal(1, fresh);
    }

    [Fact]
    public void Interval_round_trips()
    {
        var pump = new UsagePump(new FakeSource());
        pump.Interval = TimeSpan.FromSeconds(240);
        Assert.Equal(TimeSpan.FromSeconds(240), pump.Interval);
    }

    // The default runner is the pool: the read does not happen on the
    // ticking thread, and it does arrive.
    [Fact]
    public void The_default_runner_reads_off_the_calling_thread()
    {
        var source = new FakeSource();
        var pump = new UsagePump(source);
        var done = new System.Threading.ManualResetEventSlim();
        var thread = -1;
        pump.SwapSink(_ => { thread = Environment.CurrentManagedThreadId; done.Set(); });

        pump.Tick(Now);

        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "the pooled read never finished");
        Assert.NotEqual(Environment.CurrentManagedThreadId, thread);
    }

    // A read that throws is reported to the failure hook, and still
    // delivered as an empty read so the flag is cleared.
    [Fact]
    public void A_throwing_source_is_reported_to_the_failure_hook()
    {
        var source = new FakeSource { Throw = new TimeoutException("slow claude") };
        Exception? seen = null;
        var pump = new UsagePump(source, work => work(), e => seen = e);

        pump.Tick(Now);

        Assert.IsType<TimeoutException>(seen);
    }

    // The swap happens under the pump's lock, so CurrentSinkForTests says
    // what a completing read would call at that instant.
    [Fact]
    public void SwapSink_changes_what_a_completing_read_would_call()
    {
        var pump = new UsagePump(new FakeSource());
        Action<IReadOnlyList<AccountUsage>> sink = _ => { };
        pump.SwapSink(sink);
        Assert.Same(sink, pump.CurrentSinkForTests);
    }
}
