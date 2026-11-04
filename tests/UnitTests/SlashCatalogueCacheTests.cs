using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Xunit;

namespace Orbweaver.Tests;

// SlashCatalogueCache (CB-306's second reopen): the roster must never wait on
// the filesystem, so every case here is driven by a scanner that can be made to
// block for ever, the shape the Mac mini's ~/Documents opendir actually took.
public class SlashCatalogueCacheTests : IDisposable
{
    private static readonly IReadOnlyList<SlashCommand> Builtins = new[] { new SlashCommand("/compact", "") };
    private static readonly IReadOnlyList<SlashCommand> Scanned = new[] { new SlashCommand("/compact", ""), new SlashCommand("/ship-it", "") };

    private readonly ManualResetEventSlim _release = new(false);
    private DateTime _now = new(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc);
    private int _scans;

    // Released on the way out so no test leaves a parked thread behind it.
    public void Dispose() => _release.Set();

    private SlashCatalogueCache Cache(bool block = false, bool inline = false, Func<IReadOnlyList<SlashCommand>>? scan = null) =>
        new(new SlashCatalogueCache.Seams(
            (_, _) =>
            {
                Interlocked.Increment(ref _scans);
                if (block) _release.Wait();
                return scan?.Invoke() ?? Scanned;
            },
            _ => Builtins,
            () => _now,
            work =>
            {
                if (inline) work();
                else new Thread(() => work()) { IsBackground = true }.Start();
            }));

    private static void Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "timed out waiting for the scan");
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void AScanThatNeverReturnsLeavesPeekAnsweringWithTheBuiltinsAtOnce()
    {
        var cache = Cache(block: true);

        var clock = Stopwatch.StartNew();
        var first = cache.Peek(SessionSource.ClaudeCode, "/Users/x/Documents/GTD/Evidence");

        Assert.Same(Builtins, first);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(1, cache.InFlight);
    }

    [Fact]
    public void AStuckScanIsNeverStartedAgainWhileItIsStillStuck()
    {
        var cache = Cache(block: true);

        for (var i = 0; i < 20; i++) cache.Peek(SessionSource.ClaudeCode, "/stuck");
        _now += SlashCatalogueCache.RefreshAfter * 10;
        cache.Peek(SessionSource.ClaudeCode, "/stuck");

        Until(() => Volatile.Read(ref _scans) >= 1);
        Assert.Equal(1, Volatile.Read(ref _scans));
        Assert.Equal(1, cache.InFlight);
    }

    [Fact]
    public void AStuckScanThatFinallyReturnsIsPickedUpByTheNextAsk()
    {
        var cache = Cache(block: true);
        Assert.Same(Builtins, cache.Peek(SessionSource.ClaudeCode, "/slow"));

        _release.Set();
        Until(() => cache.InFlight == 0);

        Assert.Same(Scanned, cache.Peek(SessionSource.ClaudeCode, "/slow"));
    }

    [Fact]
    public void TheCeilingStopsNewScansOnceThatManyAreStuck()
    {
        var cache = Cache(block: true);

        for (var i = 0; i < SlashCatalogueCache.MaxInFlight; i++) cache.Peek(SessionSource.ClaudeCode, "/stuck" + i);
        Until(() => Volatile.Read(ref _scans) == SlashCatalogueCache.MaxInFlight);

        Assert.Same(Builtins, cache.Peek(SessionSource.ClaudeCode, "/one-more"));
        Assert.Equal(SlashCatalogueCache.MaxInFlight, cache.InFlight);
        Thread.Sleep(50);
        Assert.Equal(SlashCatalogueCache.MaxInFlight, Volatile.Read(ref _scans));
    }

    [Fact]
    public void AScanThatFinishesInsideTheWaitIsAnsweredAsAScan()
    {
        var cache = Cache();

        var found = cache.For(SessionSource.ClaudeCode, "/fast", TimeSpan.FromSeconds(10));

        Assert.True(found.Scanned);
        Assert.Same(Scanned, found.Commands);
    }

    [Fact]
    public void TheWaitIsCountedFromTheScansStartSoAHungFolderCostsItOnce()
    {
        var cache = Cache(block: true);
        var wait = TimeSpan.FromMilliseconds(150);

        var clock = Stopwatch.StartNew();
        var first = cache.For(SessionSource.ClaudeCode, "/hung", wait);
        var firstTook = clock.Elapsed;
        Assert.False(first.Scanned);
        Assert.Same(Builtins, first.Commands);
        Assert.True(firstTook >= TimeSpan.FromMilliseconds(100), $"waited only {firstTook}");

        // The same folder asked again, by a caller with the same wait, after
        // that wait has lapsed on the cache's clock: nothing left to wait for.
        // (A panel always asks with PanelCatalogueWait, so this is its case.)
        _now += wait;
        clock.Restart();
        var second = cache.For(SessionSource.ClaudeCode, "/hung", wait);
        Assert.False(second.Scanned);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(100), $"waited {clock.Elapsed}");
    }

    [Fact]
    public void AnAnswerIsCachedUntilTheRefreshIntervalThenRefreshedBehindTheStaleOne()
    {
        var current = Scanned;
        var cache = Cache(inline: true, scan: () => current);

        Assert.Same(Scanned, cache.Peek(SessionSource.ClaudeCode, "/p"));
        _now += SlashCatalogueCache.RefreshAfter - TimeSpan.FromSeconds(1);
        Assert.Same(Scanned, cache.Peek(SessionSource.ClaudeCode, "/p"));
        Assert.Equal(1, _scans);

        current = Builtins;
        _now += TimeSpan.FromSeconds(1);
        Assert.Same(Builtins, cache.Peek(SessionSource.ClaudeCode, "/p"));
        Assert.Equal(2, _scans);
    }

    [Fact]
    public void AStaleAnswerIsStillOfferedWhileItsRefreshIsStuck()
    {
        var blockNext = false;
        var cache = new SlashCatalogueCache(new SlashCatalogueCache.Seams(
            (_, _) => { if (blockNext) _release.Wait(); return Scanned; },
            _ => Builtins,
            () => _now,
            work => new Thread(() => work()) { IsBackground = true }.Start()));

        cache.For(SessionSource.ClaudeCode, "/p", TimeSpan.FromSeconds(10));
        blockNext = true;
        _now += SlashCatalogueCache.RefreshAfter;

        var stale = cache.For(SessionSource.ClaudeCode, "/p", TimeSpan.FromSeconds(30));

        Assert.True(stale.Scanned);
        Assert.Same(Scanned, stale.Commands);
        Assert.Equal(1, cache.InFlight);
    }

    [Fact]
    public void AScanThatThrowsIsAnsweredWithTheBuiltinsAndNotRetriedUntilTheRefresh()
    {
        var cache = Cache(inline: true, scan: () => throw new UnauthorizedAccessException("no"));

        var found = cache.For(SessionSource.ClaudeCode, "/denied", TimeSpan.Zero);
        cache.Peek(SessionSource.ClaudeCode, "/denied");

        Assert.True(found.Scanned);
        Assert.Same(Builtins, found.Commands);
        Assert.Equal(1, _scans);
        Assert.Equal(0, cache.InFlight);
    }

    [Fact]
    public void CodexAndGrokShareOneEntryWhateverTheCwdWhileClaudeCodeIsPerFolder()
    {
        var cache = Cache(inline: true);

        cache.Peek(SessionSource.Codex, "/a");
        cache.Peek(SessionSource.Codex, "/b");
        cache.Peek(SessionSource.Grok, null);
        cache.Peek(SessionSource.Grok, "/c");
        Assert.Equal(2, _scans);

        cache.Peek(SessionSource.ClaudeCode, "/a");
        cache.Peek(SessionSource.ClaudeCode, "/b");
        cache.Peek(SessionSource.ClaudeCode, null);
        Assert.Equal(5, _scans);
    }

    [Fact]
    public void RememberedKeepsOnlyARealScan()
    {
        var cache = Cache(block: true);
        IReadOnlyList<SlashCommand>? kept = null;

        Assert.Same(Builtins, cache.Remembered(ref kept, SessionSource.ClaudeCode, "/r", TimeSpan.Zero));
        Assert.Null(kept);

        _release.Set();
        Until(() => cache.InFlight == 0);

        Assert.Same(Scanned, cache.Remembered(ref kept, SessionSource.ClaudeCode, "/r", TimeSpan.Zero));
        Assert.Same(Scanned, kept);

        // Kept, so the cache is not consulted again even once it moves on.
        var mine = new[] { new SlashCommand("/mine", "") };
        kept = mine;
        Assert.Same(mine, cache.Remembered(ref kept, SessionSource.ClaudeCode, "/r", TimeSpan.Zero));
    }

    [Fact]
    public void BuiltinsForEachCliReadNoDiskAndMatchTheCatalogueFloor()
    {
        Assert.Same(SlashCommandCatalog.ClaudeCodeBuiltins, SlashCommandCatalog.BuiltinsFor(SessionSource.ClaudeCode));
        Assert.NotEmpty(SlashCommandCatalog.BuiltinsFor(SessionSource.Codex));
        Assert.NotEmpty(SlashCommandCatalog.BuiltinsFor(SessionSource.Grok));
        Assert.NotSame(SlashCommandCatalog.BuiltinsFor(SessionSource.Codex), SlashCommandCatalog.BuiltinsFor(SessionSource.Grok));
    }

    [Fact]
    public void TheSharedCacheAnswersAFolderFromItsRealScan()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "slashcache-" + Guid.NewGuid().ToString("N"));
        var commands = System.IO.Path.Combine(dir, ".claude", "commands");
        System.IO.Directory.CreateDirectory(commands);
        System.IO.File.WriteAllText(System.IO.Path.Combine(commands, "ship-it.md"), "Ship it.");
        try
        {
            var found = SlashCatalogueCache.Shared.For(SessionSource.ClaudeCode, dir, TimeSpan.FromSeconds(10));
            Assert.True(found.Scanned);
            Assert.Contains(found.Commands, c => c.Name == "/ship-it");
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }
}
