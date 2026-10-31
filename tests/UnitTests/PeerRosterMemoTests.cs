using Xunit;

namespace Orbweaver.UnitTests;

// CB-300: each hello from each peer used to spawn `claude agents` once per dir
// before the roster hash was even compared.
public class PeerRosterMemoTests
{
    private sealed class Fixture
    {
        public DateTime Clock = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        public List<string> Dirs = new() { ".a", ".b" };
        public List<string> Reads = new();
        public HashSet<string> Throwing = new();
        public Dictionary<string, string> Failing = new();
        public List<string> Said = new();

        public RemoteMirrorServer.RosterMemo Memo { get; }

        public Fixture()
        {
            Memo = new RemoteMirrorServer.RosterMemo(
                () => Dirs.ToList(),
                dir =>
                {
                    lock (Reads) Reads.Add(dir);
                    if (Throwing.Contains(dir)) throw new IOException("gone");
                    if (Failing.TryGetValue(dir, out var why))
                        return new AgentRoster.ReadResult(Array.Empty<AgentRoster.Entry>(), why);
                    return new AgentRoster.ReadResult(
                        new[] { new AgentRoster.Entry("n" + dir, "id" + dir, 0) }, null);
                },
                () => Clock,
                TimeSpan.FromSeconds(5),
                new RosterReadLog((what, detail) => Said.Add(what + " " + detail)),
                "/home/u");
        }
    }

    [Fact]
    public void TwoCallsInsideTheTtlReadEachDirOnce()
    {
        var f = new Fixture();

        var first = f.Memo.Read();
        f.Clock += TimeSpan.FromSeconds(4);
        var second = f.Memo.Read();

        Assert.Equal(new[] { ".a", ".b" }, f.Reads);
        Assert.Same(first, second);
        Assert.Equal(2, first.Count);
    }

    [Fact]
    public void PastTheTtlItReadsAgain()
    {
        var f = new Fixture();

        f.Memo.Read();
        f.Clock += TimeSpan.FromSeconds(5);
        f.Memo.Read();

        Assert.Equal(4, f.Reads.Count);
    }

    [Fact]
    public void ChangingTheDirListInsideTheTtlReadsImmediately()
    {
        var f = new Fixture();

        Assert.Equal(2, f.Memo.Read().Count);
        f.Dirs.Add(".c");
        f.Clock += TimeSpan.FromSeconds(1);

        Assert.Equal(3, f.Memo.Read().Count);
        Assert.Equal(new[] { ".a", ".b", ".c" }, f.Memo.LastDirs);
    }

    [Fact]
    public void ADirThatThrowsDoesNotTakeTheOthersWithIt()
    {
        var f = new Fixture();
        f.Throwing.Add(".a");

        var merged = f.Memo.Read();

        Assert.Equal("id.b", Assert.Single(merged).SessionId);
        // The dir was still asked, so the gap analysis counts it as asked.
        Assert.Equal(new[] { ".a", ".b" }, f.Memo.LastDirs);
    }

    [Fact]
    public void LastDirsIsEmptyBeforeTheFirstRead()
    {
        Assert.Empty(new Fixture().Memo.LastDirs);
    }

    // Order is the merge tie-break, so a reorder is a different answer.
    [Fact]
    public void ReorderingTheDirListInsideTheTtlReadsAgain()
    {
        var f = new Fixture();
        f.Memo.Read();
        f.Dirs.Reverse();

        f.Memo.Read();

        Assert.Equal(4, f.Reads.Count);
        Assert.Equal(new[] { ".b", ".a" }, f.Memo.LastDirs);
    }

    // The gap analysis asks LastDirs after Read: it is what that read used,
    // not a fresh evaluation that settings may have changed since.
    [Fact]
    public void LastDirsIsWhatWasReadEvenIfSettingsChangeAfterwards()
    {
        var f = new Fixture();
        f.Memo.Read();

        f.Dirs.Add(".added-after");

        Assert.Equal(new[] { ".a", ".b" }, f.Memo.LastDirs);
    }

    [Fact]
    public async Task ConcurrentHellosInsideOneWindowCostOneReadPerDir()
    {
        var f = new Fixture();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => f.Memo.Read())));

        Assert.Equal(2, f.Reads.Count);
    }

    // --- a failed read is said, once ----------------------------------------------

    [Fact]
    public void AFailedReadIsSaidWithTheDirAndTheReasonAndStillGivesAnEmptyRoster()
    {
        var f = new Fixture();
        f.Failing[".a"] = "timeout";

        var merged = f.Memo.Read();

        Assert.Equal("id.b", Assert.Single(merged).SessionId);
        Assert.Equal(new[] { "roster-read-failed dir=.a reason=timeout" }, f.Said);
    }

    // The control: a read that worked says nothing, and an empty answer is not
    // a failure.
    [Fact]
    public void ASuccessfulReadSaysNothing()
    {
        var f = new Fixture();

        f.Memo.Read();

        Assert.Empty(f.Said);
    }

    [Fact]
    public void AThrowingReaderIsSaidByTypeOnly()
    {
        var f = new Fixture();
        f.Throwing.Add(".a");

        f.Memo.Read();

        Assert.Equal(new[] { "roster-read-failed dir=.a reason=threw:IOException" }, f.Said);
    }

    [Fact]
    public void AMemoHitDoesNotSayItAgain()
    {
        var f = new Fixture();
        f.Failing[".a"] = "exit=1";

        f.Memo.Read();
        f.Memo.Read();

        Assert.Single(f.Said);
    }

    [Fact]
    public void FiveConsecutiveFailingReadsPastTheTtlSayOnce()
    {
        var f = new Fixture();
        f.Failing[".a"] = "exit=1";

        for (var i = 0; i < 5; i++)
        {
            f.Memo.Read();
            f.Clock += TimeSpan.FromSeconds(6);
        }

        Assert.Equal(10, f.Reads.Count);
        Assert.Single(f.Said);
    }

    [Fact]
    public void ADifferentReasonIsSaidAgain()
    {
        var f = new Fixture();
        f.Failing[".a"] = "timeout";
        f.Memo.Read();

        f.Failing[".a"] = "exit=2";
        f.Clock += TimeSpan.FromSeconds(6);
        f.Memo.Read();

        Assert.Equal(2, f.Said.Count);
        Assert.EndsWith("reason=exit=2", f.Said[1]);
    }

    [Fact]
    public void ARecoveryFollowedByANewFailureIsSaidAgain()
    {
        var f = new Fixture();
        f.Failing[".a"] = "timeout";
        f.Memo.Read();

        f.Failing.Clear();
        f.Clock += TimeSpan.FromSeconds(6);
        f.Memo.Read();

        f.Failing[".a"] = "timeout";
        f.Clock += TimeSpan.FromSeconds(6);
        f.Memo.Read();

        Assert.Equal(2, f.Said.Count);
    }

    [Fact]
    public void AnAbsoluteDirIsLoggedHomeRelativeOrByKindNeverByPath()
    {
        Assert.Equal(".claude-x", RemoteMirrorServer.DirLabel(" .claude-x ", "/home/u"));
        Assert.Equal("~/.claude-x", RemoteMirrorServer.DirLabel("/home/u/.claude-x", "/home/u"));
        Assert.Equal("(outside home)", RemoteMirrorServer.DirLabel("/mnt/bob/.claude", "/home/u"));
        Assert.Equal("(outside home)", RemoteMirrorServer.DirLabel(@"C:\other\bob\.claude", "/home/u"));
        Assert.Equal("~/.w", RemoteMirrorServer.DirLabel(@"C:\Users\u\.w", @"C:\Users\u"));
    }
}
