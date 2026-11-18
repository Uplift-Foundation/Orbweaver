using Xunit;

namespace Orbweaver.Tests;

// Process-wide thread-pool state is changed here, so this class runs alone:
// DisableParallelization on the collection keeps every other test from timing
// out for a reason nobody could trace back to this file.
[CollectionDefinition("PoolStarvation", DisableParallelization = true)]
public class PoolStarvationCollection { }

// CB-370. Output a child had already written must not depend on the thread
// pool. The readers used to be async loops whose completions need a pool
// thread, so on a busy pool (CI runs this suite in parallel) the bytes sat in
// the pipe past the timeout arm's wait and the result came back empty — seen on
// win-x64 twice in an hour, and reproduced on a real Windows PC by exactly this:
// every pool worker blocked.
[Collection("PoolStarvation")]
public class HookInstallerStarvedPoolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cb-hookpool-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _logScope;

    public HookInstallerStarvedPoolTests()
    {
        Directory.CreateDirectory(_root);
        _logScope = CrashLog.ScopeForTests(Path.Combine(_root, "logs"));
    }

    public void Dispose()
    {
        _logScope.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void WhatAChildSaidSurvivesAThreadPoolWithNoFreeWorkers()
    {
        ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
        ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
        using var release = new ManualResetEventSlim();
        try
        {
            // Min raised so blockers start at once, then max pinned to what is
            // busy so the pool cannot grow a thread to rescue the reader. A pool
            // that merely has to inject one is slow, not starved.
            var blockers = Environment.ProcessorCount * 2;
            ThreadPool.SetMinThreads(minWorkers + blockers, minIo);
            Occupy(blockers, release);
            ThreadPool.SetMinThreads(Environment.ProcessorCount, minIo);
            ThreadPool.GetAvailableThreads(out var free, out _);
            ThreadPool.SetMaxThreads(Math.Max(Environment.ProcessorCount, maxWorkers - free), maxIo);

            // Another worker can still go idle after the pin (a runner thread
            // finishing its own item), and then the pool is not starved. That is
            // detected, not assumed: a marker item that can only run on a free
            // worker, and an attempt where it ran proves nothing, so it is
            // refilled and tried again. The test fails only if no attempt ever
            // saw a starved pool, which is "could not starve the pool", not a
            // verdict on Run.
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                ThreadPool.GetAvailableThreads(out var idle, out _);
                if (idle > 0) Occupy(idle, release);

                var dir = Path.Combine(_root, "attempt" + attempt);
                Directory.CreateDirectory(dir);
                var ready = Path.Combine(dir, "ready");
                var (file, args) = OperatingSystem.IsWindows()
                    ? ("cmd.exe", new[] { "/c", $"echo started& cd /d {dir}& type nul > ready& ping -n 30 127.0.0.1 > nul" })
                    : ("/bin/sh", new[] { "-c", $"echo started; : > '{ready}'; sleep 30" });

                var markerRan = false;
                ThreadPool.QueueUserWorkItem(_ => Volatile.Write(ref markerRan, true));

                var result = HookInstaller.Run(file, args, "fake-installer",
                    waitForExit: HookInstallerRunTests.TimesOutOnce(ready));

                if (Volatile.Read(ref markerRan)) continue;

                Assert.Equal(HookInstallOutcome.TimedOut, result.Outcome);
                Assert.Contains("started", result.Output);
                return;
            }
            Assert.Fail("could not starve the pool");
        }
        finally
        {
            release.Set();
            ThreadPool.SetMaxThreads(maxWorkers, maxIo);
            ThreadPool.SetMinThreads(minWorkers, minIo);
        }
    }

    // Parks n pool workers on release and returns once all of them are running.
    private static void Occupy(int n, ManualResetEventSlim release)
    {
        using var occupied = new CountdownEvent(n);
        for (var i = 0; i < n; i++)
            ThreadPool.QueueUserWorkItem(_ => { occupied.Signal(); release.Wait(); });
        Assert.True(occupied.Wait(TimeSpan.FromSeconds(30)), "the pool was not saturated");
    }

    [Fact]
    public void ADrainKeepsWhatItReadAndHoldsOnToAFailureUntilFinished()
    {
        var into = new System.Text.StringBuilder();
        var drain = new HookInstaller.PipeDrain(
            new StreamReader(new ThrowsAfter("partial")), into);
        drain.Start();
        drain.Thread.Join();

        Assert.Equal("partial", into.ToString());
        Assert.Throws<IOException>(() => drain.Finish());
    }

    [Fact]
    public void ADrainThatReachesEndOfStreamFinishesCleanly()
    {
        var into = new System.Text.StringBuilder();
        var drain = new HookInstaller.PipeDrain(
            new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("all of it"))), into);
        Assert.True(drain.Thread.IsBackground);
        drain.Start();
        drain.Finish();

        Assert.Equal("all of it", into.ToString());
    }

    private sealed class ThrowsAfter : Stream
    {
        private byte[]? _first;
        public ThrowsAfter(string first) => _first = System.Text.Encoding.UTF8.GetBytes(first);
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_first is null) throw new IOException("pipe closed under the read");
            var n = Math.Min(count, _first.Length);
            Array.Copy(_first, buffer, n);
            _first = null;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
