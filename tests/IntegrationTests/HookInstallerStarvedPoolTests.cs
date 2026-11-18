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
        var ready = Path.Combine(_root, "ready");
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", $"echo started& cd /d {_root}& type nul > ready& ping -n 30 127.0.0.1 > nul" })
            : ("/bin/sh", new[] { "-c", $"echo started; : > '{ready}'; sleep 30" });

        ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
        ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
        using var release = new ManualResetEventSlim();
        var markerRan = false;
        try
        {
            // Min raised so every blocker starts at once; then, with the pool
            // full, max is pinned to what is busy so it cannot grow a thread to
            // rescue the reader. A pool that merely has to inject one is slow,
            // not starved.
            var blockers = Environment.ProcessorCount * 2;
            ThreadPool.SetMinThreads(minWorkers + blockers, minIo);
            using var occupied = new CountdownEvent(blockers);
            for (var i = 0; i < blockers; i++)
                ThreadPool.QueueUserWorkItem(_ => { occupied.Signal(); release.Wait(); });
            Assert.True(occupied.Wait(TimeSpan.FromSeconds(30)), "the pool was not saturated");

            ThreadPool.SetMinThreads(Environment.ProcessorCount, minIo);
            ThreadPool.GetAvailableThreads(out var free, out _);
            ThreadPool.SetMaxThreads(Math.Max(Environment.ProcessorCount, maxWorkers - free), maxIo);

            // A work item that can only run if the pool has a free worker. If
            // it has run by the time Run returns, the pool was not starved and
            // a pass proves nothing.
            ThreadPool.QueueUserWorkItem(_ => markerRan = true);

            var result = HookInstaller.Run(file, args, "fake-installer",
                waitForExit: HookInstallerRunTests.TimesOutOnce(ready));

            Assert.False(Volatile.Read(ref markerRan), "could not starve the pool");
            Assert.Equal(HookInstallOutcome.TimedOut, result.Outcome);
            Assert.Contains("started", result.Output);
        }
        finally
        {
            release.Set();
            ThreadPool.SetMaxThreads(maxWorkers, maxIo);
            ThreadPool.SetMinThreads(minWorkers, minIo);
        }
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
