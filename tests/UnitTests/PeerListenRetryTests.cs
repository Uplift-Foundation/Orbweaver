using Orbweaver;
using Xunit;

// CB-306: the policy and loop that keep trying to bind the peer link, and the
// mirror log's rotation (it used to stop writing at 1 MB).
public class PeerListenRetryTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(7, 30)]
    [InlineData(10000, 30)]
    public void DelayDoublesFromOneSecondToTheCeiling(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), PeerListenRetry.Delay(failures));

    [Fact]
    public async Task ReturnsZeroWhenTheFirstAttemptBinds()
    {
        var failures = await PeerListenRetry.RunAsync(
            () => { }, (_, _, _) => Assert.Fail("no failure expected"),
            (_, _) => Task.CompletedTask, CancellationToken.None);

        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task RetriesUntilItBindsAndReportsEachFailure()
    {
        var calls = 0;
        var reported = new List<(int, TimeSpan, string)>();
        var waited = new List<TimeSpan>();

        var failures = await PeerListenRetry.RunAsync(
            () => { if (++calls < 4) throw new InvalidOperationException("in use " + calls); },
            (n, next, ex) => reported.Add((n, next, ex.Message)),
            (d, _) => { waited.Add(d); return Task.CompletedTask; },
            CancellationToken.None);

        Assert.Equal(3, failures);
        Assert.Equal(4, calls);
        Assert.Equal(
            new[] { (1, TimeSpan.FromSeconds(1), "in use 1"), (2, TimeSpan.FromSeconds(2), "in use 2"),
                    (3, TimeSpan.FromSeconds(4), "in use 3") }, reported);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, waited);
    }

    [Fact]
    public async Task StopsWhenCancelledDuringTheWait()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;

        var failures = await PeerListenRetry.RunAsync(
            () => { calls++; throw new InvalidOperationException("in use"); },
            (_, _, _) => { },
            (_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            cts.Token);

        Assert.Equal(1, calls);
        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task DoesNotAttemptAtAllWhenAlreadyCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var calls = 0;

        var failures = await PeerListenRetry.RunAsync(
            () => calls++, (_, _, _) => { }, (_, _) => Task.CompletedTask, cts.Token);

        Assert.Equal(0, calls);
        Assert.Equal(0, failures);
    }

    [Fact]
    public void MirrorLogRotatesPastTheLimitKeepingOneGeneration()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(dir, "mirror-log.txt");

            MirrorLog.RotateIfFull(path, 10); // missing file: nothing to do
            Assert.False(File.Exists(path + ".1"));

            File.WriteAllText(path, "short");
            MirrorLog.RotateIfFull(path, 10); // under the limit: left alone
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".1"));

            File.WriteAllText(path, "0123456789ABCDEF");
            MirrorLog.RotateIfFull(path, 10);
            Assert.False(File.Exists(path));
            Assert.Equal("0123456789ABCDEF", File.ReadAllText(path + ".1"));

            File.WriteAllText(path, "second-generation-over");
            MirrorLog.RotateIfFull(path, 10); // replaces the older .1
            Assert.Equal("second-generation-over", File.ReadAllText(path + ".1"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
