using Orbweaver;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-307: the bounded read both sides of the parked rule share. What matters
// is how often it touches the disk, so every test counts the reader's calls.
public class LastTurnCacheTests
{
    private const string Path = "/fake/session.jsonl";

    private static readonly DateTime Spoke =
        new(2026, 11, 5, 23, 32, 39, DateTimeKind.Utc);

    private static string Turn(DateTime at) =>
        $$"""{"type":"user","timestamp":"{{at:yyyy-MM-ddTHH:mm:ss.fffZ}}"}""";

    private sealed class FakeDisk
    {
        public long? Length = 100;
        public List<string> Lines = new() { Turn(Spoke) };
        public int Reads;
        public bool ThrowOnLength;
        public bool ThrowOnRead;

        public LastTurnCache Cache() => new(
            _ => ThrowOnLength ? throw new IOException("stat") : Length,
            _ =>
            {
                Reads++;
                if (ThrowOnRead) throw new IOException("read");
                return Lines.ToList();
            });
    }

    [Fact]
    public void TheFirstAskReadsTheFile()
    {
        var disk = new FakeDisk();

        Assert.Equal(Spoke, disk.Cache().LastTurnFor(Path));
        Assert.Equal(1, disk.Reads);
    }

    [Fact]
    public void AnUnchangedLengthIsAnsweredWithoutReadingAgain()
    {
        var disk = new FakeDisk();
        var cache = disk.Cache();

        cache.LastTurnFor(Path);
        Assert.Equal(Spoke, cache.LastTurnFor(Path));
        Assert.Equal(1, disk.Reads);
    }

    [Fact]
    public void ALengthChangeReadsAgainAndSeesTheNewTurn()
    {
        var disk = new FakeDisk();
        var cache = disk.Cache();
        cache.LastTurnFor(Path);

        var later = Spoke.AddHours(21);
        disk.Lines.Add(Turn(later));
        disk.Length = 200;

        Assert.Equal(later, cache.LastTurnFor(Path));
        Assert.Equal(2, disk.Reads);
    }

    [Fact]
    public void AMissingFileIsNoAnswerAndIsNotCached()
    {
        var disk = new FakeDisk { Length = null };
        var cache = disk.Cache();

        Assert.Null(cache.LastTurnFor(Path));
        Assert.Equal(0, disk.Reads);

        // It appears: picked up on the very next ask, not after a change.
        disk.Length = 100;
        Assert.Equal(Spoke, cache.LastTurnFor(Path));
    }

    [Fact]
    public void AStatThatThrowsIsNoAnswer()
    {
        var disk = new FakeDisk { ThrowOnLength = true };

        Assert.Null(disk.Cache().LastTurnFor(Path));
        Assert.Equal(0, disk.Reads);
    }

    [Fact]
    public void AReadThatThrowsIsNoAnswerAndIsTriedAgainNextTime()
    {
        var disk = new FakeDisk { ThrowOnRead = true };
        var cache = disk.Cache();

        Assert.Null(cache.LastTurnFor(Path));

        disk.ThrowOnRead = false;
        Assert.Equal(Spoke, cache.LastTurnFor(Path));
        Assert.Equal(2, disk.Reads);
    }

    [Fact]
    public void AFileWithNoTurnIsCachedAsNoTurn()
    {
        // Bookkeeping only: a real answer ("nothing said"), so it is cached
        // like any other until the file grows.
        var disk = new FakeDisk { Lines = new() { """{"type":"bridge-session"}""" } };
        var cache = disk.Cache();

        Assert.Null(cache.LastTurnFor(Path));
        Assert.Null(cache.LastTurnFor(Path));
        Assert.Equal(1, disk.Reads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoPathIsNoAnswer(string? path)
    {
        var disk = new FakeDisk();

        Assert.Null(disk.Cache().LastTurnFor(path));
        Assert.Equal(0, disk.Reads);
    }

    [Fact]
    public void PathsAreCachedSeparately()
    {
        var disk = new FakeDisk();
        var cache = disk.Cache();

        cache.LastTurnFor("/a.jsonl");
        cache.LastTurnFor("/b.jsonl");
        cache.LastTurnFor("/a.jsonl");

        Assert.Equal(2, disk.Reads);
    }
}
