using Xunit;

namespace Orbweaver.Tests;

/// <summary>CB-314: the client's token bucket under the relay's frame limit.</summary>
public class RelayPacerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_burst_goes_at_once_then_frames_wait_one_interval_each()
    {
        var p = new RelayPacer(T0, rate: 40, burst: 20);
        for (var i = 0; i < 20; i++) Assert.Equal(TimeSpan.Zero, p.Reserve(T0));
        Assert.Equal(TimeSpan.FromMilliseconds(25), p.Reserve(T0));
        Assert.Equal(TimeSpan.FromMilliseconds(50), p.Reserve(T0));
    }

    [Fact]
    public void Tokens_refill_with_time_up_to_the_burst_and_never_past_it()
    {
        var p = new RelayPacer(T0, rate: 40, burst: 20);
        for (var i = 0; i < 20; i++) p.Reserve(T0);
        Assert.Equal(TimeSpan.Zero, p.Reserve(T0.AddMilliseconds(25)));         // one token back
        Assert.Equal(TimeSpan.FromMilliseconds(25), p.Reserve(T0.AddMilliseconds(25)));
        var later = T0.AddMinutes(10);                                           // long idle: a full burst, no more
        for (var i = 0; i < 20; i++) Assert.Equal(TimeSpan.Zero, p.Reserve(later));
        Assert.True(p.Reserve(later) > TimeSpan.Zero);
    }

    [Fact]
    public void A_clock_that_steps_backwards_neither_refills_nor_throws()
    {
        var p = new RelayPacer(T0, rate: 40, burst: 1);
        Assert.Equal(TimeSpan.Zero, p.Reserve(T0));
        Assert.Equal(TimeSpan.FromMilliseconds(25), p.Reserve(T0.AddSeconds(-5)));
    }

    [Fact]
    public void Over_any_second_the_default_stays_under_the_relays_fifty()
    {
        var p = new RelayPacer(T0);
        var at = TimeSpan.Zero;
        var sends = new List<TimeSpan>();
        for (var i = 0; i < 400; i++)
        {
            at += p.Reserve(T0 + at);
            sends.Add(at);
        }
        for (var i = 0; i < sends.Count; i++)
            Assert.True(sends.Count(s => s >= sends[i] && s < sends[i] + TimeSpan.FromSeconds(1)) <= 50, $"window at {sends[i]}");
    }
}
