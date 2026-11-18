using Xunit;

namespace Orbweaver.Tests;

// CB-363: PeerFileCache and PeerFileSource against a real temp directory —
// file modes, the OS's own download mark read back off the file, renames,
// and the sweep — the parts a model filesystem cannot speak for.
public sealed class PeerFileCacheTests : IDisposable
{
    private sealed class CacheUnixFact : FactAttribute
    {
        public CacheUnixFact() { if (OperatingSystem.IsWindows()) Skip = "needs POSIX directory permissions"; }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cb363-cache-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows() && Directory.Exists(_root))
            foreach (var d in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories).Prepend(_root))
                File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private sealed class Marks : IDownloadMarker
    {
        public readonly List<(string Path, string Origin)> Seen = new();
        public bool Throw;

        public Exception? Throws;

        public void Mark(string path, string origin)
        {
            if (Throws is not null) throw Throws;
            if (Throw) throw new IOException("xattr refused");
            Seen.Add((path, origin));
        }
    }

    private PeerFileCache Cache(IDownloadMarker? marker = null, long max = PeerFileCache.MaxTotalBytes) =>
        new(_root, () => _now, marker ?? new Marks(), max);

    // --- storing ---------------------------------------------------------------

    [Fact]
    public void AStoredFileIsPrivateNotExecutableMarkedBeforeItsRenameAndFoundAgain()
    {
        var marks = new Marks();
        var cache = Cache(marks);
        var stored = cache.Store("mini", "/Users/x/Response.pdf", 1700000000000, "%PDF"u8.ToArray(), "mini")!;

        Assert.Equal("%PDF"u8.ToArray(), File.ReadAllBytes(stored.LocalPath));
        Assert.Equal(Path.Combine(_root, PeerFileCache.Key("mini", "/Users/x/Response.pdf"), "1700000000000-4", "Response.pdf"),
            stored.LocalPath);
        Assert.Equal("1700000000000-4", stored.Have);

        // Marked while it was still the temp file, so the final name never existed unmarked.
        var (markedPath, origin) = Assert.Single(marks.Seen);
        Assert.NotEqual(stored.LocalPath, markedPath);
        Assert.EndsWith(".part", markedPath);
        Assert.Equal("mini", origin);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(stored.LocalPath)!, "*.part"));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(stored.LocalPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(stored.LocalPath)!));
        }

        Assert.Equal(stored, cache.Newest("mini", "/Users/x/Response.pdf"));
        Assert.Null(cache.Newest("windows", "/Users/x/Response.pdf"));
    }

    // The real mark, read back off the file by the OS's own attribute API.
    [Fact]
    public void TheRealMarkIsReallyThere()
    {
        var stored = new PeerFileCache(_root).Store("mini", "/a/b.pdf", 1, [1, 2], "mini")!;

        if (OperatingSystem.IsMacOS())
            Assert.StartsWith("0081;", OsDownloadMarker.ReadQuarantine(stored.LocalPath));
        else if (OperatingSystem.IsWindows())
            Assert.Contains("ZoneId=3", File.ReadAllText(stored.LocalPath + ":Zone.Identifier"));

        Assert.Null(OperatingSystem.IsMacOS() ? OsDownloadMarker.ReadQuarantine(Path.Combine(_root, "nope")) : null);
    }

    // The real marker says so when the OS refuses the mark, which is what
    // makes Store drop the file; there is no mark on a file that is not there.
    [Fact]
    public void TheRealMarkerThrowsWhenTheOsRefuses()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) return; // neither mark exists elsewhere
        Assert.ThrowsAny<IOException>(() => OsDownloadMarker.Instance.Mark(Path.Combine(_root, "no", "such.pdf"), "mini"));
    }

    // A file the mark cannot be written to is not kept, and nothing is left behind.
    [Fact]
    public void AFileThatCannotBeMarkedIsNotStored()
    {
        var cache = Cache(new Marks { Throw = true });
        Assert.Null(cache.Store("mini", "/a/b.pdf", 1, [1, 2], "mini"));
        Assert.Null(cache.Newest("mini", "/a/b.pdf"));
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    // Not only an IOException: a Windows stream name the OS calls invalid is
    // an ArgumentException, and it must not leak the temp file either.
    [Fact]
    public void AMarkThatFailsAnyOtherWayIsNotStoredEither()
    {
        foreach (var e in new Exception[] { new ArgumentException("bad stream name"), new NotSupportedException("no ADS here") })
        {
            var cache = Cache(new Marks { Throws = e });
            Assert.Null(cache.Store("mini", "/a/b.pdf", 1, [1, 2], "mini"));
            Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
        }
    }

    // A copy that is used every day is never old, however long ago it was
    // fetched: the caller's Touch on a `same` hit is what keeps it.
    [Fact]
    public void ACopyReusedWithinTheMonthSurvivesTheSweep()
    {
        var cache = Cache();
        var stored = cache.Store("mini", "/a/b.pdf", 1, [1], "mini")!;
        _now = _now.AddDays(25);
        cache.Touch(cache.Newest("mini", "/a/b.pdf")!);
        _now = _now.AddDays(25);

        Assert.Equal(0, cache.Sweep());
        Assert.True(File.Exists(stored.LocalPath));

        _now = _now.AddDays(6);
        Assert.Equal(1, cache.Sweep());
    }

    [Fact]
    public void ARootThatIsAFileStoresNothing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_root)!);
        File.WriteAllText(_root, "in the way");
        try
        {
            Assert.Null(Cache().Store("mini", "/a/b.pdf", 1, [1], "mini"));
        }
        finally
        {
            File.Delete(_root);
        }
    }

    [Fact]
    public void StoringTheSameStampAgainReplacesIt()
    {
        var cache = Cache();
        cache.Store("mini", "/a/b.pdf", 1, [1, 2], "mini");
        var again = cache.Store("mini", "/a/b.pdf", 1, [3, 4], "mini")!;
        Assert.Equal(new byte[] { 3, 4 }, File.ReadAllBytes(again.LocalPath));
    }

    // --- finding -----------------------------------------------------------------

    // Newest by the far file's mtime; an entry whose name is not a stamp, or
    // whose file is missing or the wrong length, is not a copy.
    [Fact]
    public void NewestIsTheLatestCompleteCopy()
    {
        var cache = Cache();
        cache.Store("mini", "/a/b.pdf", 5, [1, 2], "mini");
        var newest = cache.Store("mini", "/a/b.pdf", 9, [1, 2, 3], "mini")!;
        cache.Store("mini", "/a/b.pdf", 7, [1], "mini");

        var keyDir = Path.Combine(_root, PeerFileCache.Key("mini", "/a/b.pdf"));
        Directory.CreateDirectory(Path.Combine(keyDir, "not-a-stamp"));
        var truncated = Path.Combine(keyDir, "20-10");
        Directory.CreateDirectory(truncated);
        File.WriteAllBytes(Path.Combine(truncated, "b.pdf"), [1]);
        Directory.CreateDirectory(Path.Combine(keyDir, "30-1"));

        Assert.Equal(newest, cache.Newest("mini", "/a/b.pdf"));
    }

    [Fact]
    public void TouchCountsAgeFromLastUseAndForgivesAMissingCopy()
    {
        var cache = Cache();
        var stored = cache.Store("mini", "/a/b.pdf", 1, [1], "mini")!;
        _now = _now.AddDays(10);
        cache.Touch(stored);
        Assert.Equal(_now, Directory.GetLastWriteTimeUtc(Path.GetDirectoryName(stored.LocalPath)!));

        cache.Touch(new PeerFileCache.CachedFile(Path.Combine(_root, "gone", "1-1", "b.pdf"), 1, 1));
    }

    // --- sweeping ---------------------------------------------------------------------

    [Fact]
    public void TheSweepDropsOldCopiesStaleTempsAndEmptyKeysAndKeepsTheRest()
    {
        var cache = Cache();
        var old = cache.Store("mini", "/a/old.pdf", 1, [1], "mini")!;
        _now = _now.AddDays(31);
        var fresh = cache.Store("mini", "/a/new.pdf", 1, [1], "mini")!;

        var freshDir = Path.GetDirectoryName(fresh.LocalPath)!;
        var staleTemp = Path.Combine(freshDir, ".abc.part");
        var liveTemp = Path.Combine(freshDir, ".def.part");
        File.WriteAllBytes(staleTemp, [0]);
        File.SetLastWriteTimeUtc(staleTemp, _now - TimeSpan.FromHours(2));
        File.WriteAllBytes(liveTemp, [0]);
        File.SetLastWriteTimeUtc(liveTemp, _now);
        Directory.SetLastWriteTimeUtc(freshDir, _now); // writing the temps moved it to the real clock
        Directory.CreateDirectory(Path.Combine(_root, "emptykey"));

        Assert.Equal(1, cache.Sweep());

        Assert.False(File.Exists(old.LocalPath));
        Assert.False(Directory.Exists(Path.Combine(_root, PeerFileCache.Key("mini", "/a/old.pdf"))));
        Assert.True(File.Exists(fresh.LocalPath));
        Assert.False(File.Exists(staleTemp));
        Assert.True(File.Exists(liveTemp));
        Assert.False(Directory.Exists(Path.Combine(_root, "emptykey")));
    }

    [Fact]
    public void OverTheSizeCapTheLeastRecentlyUsedGoFirst()
    {
        var cache = Cache(max: 10);
        var a = cache.Store("mini", "/a/a.bin", 1, new byte[6], "mini")!;
        _now = _now.AddMinutes(1);
        var b = cache.Store("mini", "/a/b.bin", 1, new byte[6], "mini")!;
        _now = _now.AddMinutes(1);
        var c = cache.Store("mini", "/a/c.bin", 1, new byte[3], "mini")!;
        _now = _now.AddMinutes(1);
        cache.Touch(a);

        Assert.Equal(1, cache.Sweep());
        Assert.True(File.Exists(a.LocalPath));
        Assert.False(File.Exists(b.LocalPath));
        Assert.True(File.Exists(c.LocalPath));
    }

    [Fact]
    public void SweepingNothingIsNothing() => Assert.Equal(0, Cache().Sweep());

    // A copy the sweep cannot delete is not counted as gone, and a temp it
    // cannot delete is left; neither stops the sweep.
    [CacheUnixFact]
    public void WhatTheSweepCannotDeleteItLeaves()
    {
        var cache = Cache();
        var stuck = cache.Store("mini", "/a/stuck.pdf", 1, [1], "mini")!;
        var entry = Path.GetDirectoryName(stuck.LocalPath)!;
        var temp = Path.Combine(entry, ".x.part");
        File.WriteAllBytes(temp, [0]);
        File.SetLastWriteTimeUtc(temp, _now - TimeSpan.FromDays(40));
        Directory.SetLastWriteTimeUtc(entry, _now - TimeSpan.FromDays(40));
        File.SetUnixFileMode(entry, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        File.SetUnixFileMode(Path.GetDirectoryName(entry)!, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Assert.Equal(0, cache.Sweep());
        Assert.True(File.Exists(stuck.LocalPath));
        Assert.True(File.Exists(temp));
    }

    // --- where it lives ----------------------------------------------------------------------

    [Fact]
    public void TheCacheLivesBesideTheSettingsAndMovesWithThem()
    {
        Assert.Equal(Path.Combine("/scratch", "peer-files"),
            PeerFileCache.DefaultRoot(name => name == BrandEnv.Name(BrandEnv.SettingsDir) ? "/scratch" : null));
        // Brand is in two assemblies this project sees, so the shape is asserted rather than the name.
        var real = PeerFileCache.DefaultRoot(_ => null);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), real);
        Assert.Equal("peer-files", Path.GetFileName(real));
        // Shared latches its root once, from whatever ORBWEAVER_SETTINGS_DIR was
        // when it was first touched; other test classes point that variable
        // elsewhere, so comparing it with the live environment is a race on
        // test order (it went red on CI exactly that way). Only its folder name
        // is asserted here; the env-to-path mapping is pinned above with an
        // injected environment, and production never changes the variable
        // after startup.
        Assert.Equal("peer-files", Path.GetFileName(PeerFileCache.Shared.Root));
    }

    // --- the real stat ----------------------------------------------------------------------

    [Fact]
    public void TheRealStatTellsAFileAFolderAndNothingApart()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "f.txt");
        File.WriteAllText(file, "hello");
        var source = PeerFileSource.Instance;

        var stat = source.Stat(file);
        Assert.Equal(PeerFileKind.Regular, stat.Kind);
        Assert.Equal(5, stat.Size);
        Assert.Equal(new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds(), stat.MtimeMs);
        Assert.False(stat.Exec);
        Assert.Equal(PeerFileKind.Directory, source.Stat(_root).Kind);
        Assert.Equal(PeerFileKind.Missing, source.Stat(Path.Combine(_root, "nope")).Kind);
        Assert.Equal(PeerFileKind.Missing, source.Stat("bad\0path").Kind);

        using (var s = source.Open(file)) Assert.NotNull(s);
        Assert.Null(source.Open(Path.Combine(_root, "nope")));

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.OtherExecute);
            Assert.True(source.Stat(file).Exec);
            Assert.Equal(PeerFileKind.Missing, PeerFileSource.UnixKind(Path.Combine(_root, "nope")));
        }
    }
}
