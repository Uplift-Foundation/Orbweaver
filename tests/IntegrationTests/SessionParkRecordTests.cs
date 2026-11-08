using System.IO;
using Xunit;

namespace Orbweaver.Tests;

// SessionPark's I/O half against real files on disk: finding the record under
// whichever account owns it, the stat, and the cache keyed on it. The decision
// over the record's text is pure and covered in tests/UnitTests/SessionParkTests;
// what can only be asserted here is that a second account's sessions directory
// is searched at all — the whole bug — and that a record which changes is
// re-read rather than answered from a stale cache.
//
// In the Settings collection because pointing the walk at a scratch account
// means writing ClaudeCodeProfileDirs, which is one static for the process.
[Collection("Settings")]
public class SessionParkRecordTests
{
    private const string Owner = "746496c9-d663-42fc-96bd-92a67b843f48";

    private static string Record(string sessionId, string? parkedJobId) =>
        parkedJobId is null
            ? $$"""{"pid":70580,"sessionId":"{{sessionId}}","kind":"interactive","status":"idle"}"""
            : $$"""{"pid":70580,"sessionId":"{{sessionId}}","kind":"interactive","status":"idle","parkedJobId":"{{parkedJobId}}"}""";

    // The default account, which is where a single-account machine's records
    // live and the only place the app would have looked before this existed.
    [Fact]
    public void ARecordUnderTheDefaultAccountIsFound()
    {
        var home = NewHome();
        WriteRecord(home, ".claude", 70580, Record(Owner, "e4f5c5e4"));

        SessionPark.ClearCacheForTests();
        Assert.True(SessionPark.IsParked(70580, Owner, home));
    }

    // The bug, in one test. The session doing the asking belonged to a second
    // account, so its record was never under ~/.claude at all and the rule had
    // nothing to read — which reads as "not parked", which leaves the husk orb
    // on screen beside the fork's.
    [Fact]
    public void ARecordUnderASecondAccountIsFound()
    {
        var home = NewHome();
        Directory.CreateDirectory(Path.Combine(home, ".claude", "sessions"));
        WriteRecord(home, ".claude-board", 70580, Record(Owner, "e4f5c5e4"));

        OrbweaverSettings.AddClaudeCodeProfileDir(".claude-board");
        try
        {
            SessionPark.ClearCacheForTests();
            Assert.True(SessionPark.IsParked(70580, Owner, home));
        }
        finally
        {
            OrbweaverSettings.RemoveClaudeCodeProfileDir(".claude-board");
        }
    }

    // CB-307 reversed what this used to pin. Warren's rule: every Claude Code
    // config dir behaves like ~/.claude with no setting to keep, so a second
    // account nobody configured is found by discovery (a ~/.claude-<name>
    // holding sessions/ or projects/) and its record is read.
    [Fact]
    public void ASecondAccountNobodyConfiguredIsFoundByDiscovery()
    {
        var home = NewHome();
        WriteRecord(home, ".claude-board", 70580, Record(Owner, "e4f5c5e4"));

        SessionPark.ClearCacheForTests();
        Assert.True(SessionPark.IsParked(70580, Owner, home));
    }

    // ...and the control that it is discovery, not an accidental deep walk:
    // the same record under a folder that is not named like a config dir is
    // never searched.
    [Fact]
    public void AFolderNotNamedLikeAConfigDirIsNotSearched()
    {
        var home = NewHome();
        WriteRecord(home, "claude-board", 70580, Record(Owner, "e4f5c5e4"));
        WriteRecord(home, ".claudeboard", 70580, Record(Owner, "e4f5c5e4"));

        SessionPark.ClearCacheForTests();
        Assert.False(SessionPark.IsParked(70580, Owner, home));
    }

    [Fact]
    public void NoRecordAtAllLeavesTheOrbAlone()
    {
        var home = NewHome();
        Directory.CreateDirectory(Path.Combine(home, ".claude", "sessions"));

        SessionPark.ClearCacheForTests();
        Assert.False(SessionPark.IsParked(70580, Owner, home));
    }

    // The orb has to come back when the user returns to the window, and the
    // only thing that says so is the record being rewritten without the field.
    // A cache that answered from the first read would strand the session
    // invisible for as long as the app ran.
    [Fact]
    public void ARecordThatChangesIsReReadAndTheOrbComesBack()
    {
        var home = NewHome();
        var path = WriteRecord(home, ".claude", 70580, Record(Owner, "e4f5c5e4"));

        SessionPark.ClearCacheForTests();
        Assert.True(SessionPark.IsParked(70580, Owner, home));

        // Asked again unchanged: the cache answers, and answers the same.
        Assert.True(SessionPark.IsParked(70580, Owner, home));

        // Coming back out of the agents view clears the field. Length differs
        // as well as mtime, which is what the cache key actually compares —
        // mtime alone has a resolution a fast test can lose.
        File.WriteAllText(path, Record(Owner, null));
        Assert.False(SessionPark.IsParked(70580, Owner, home));
    }

    [Fact]
    public void AMalformedRecordOnDiskLeavesTheOrbAlone()
    {
        // A record caught mid-write is the ordinary case: these files are
        // rewritten by a live session while the scan reads them.
        var home = NewHome();
        WriteRecord(home, ".claude", 70580, @"{""sessionId"":""746");

        SessionPark.ClearCacheForTests();
        Assert.False(SessionPark.IsParked(70580, Owner, home));
    }

    // --- RecordStatus (CB-307 addendum) ---------------------------------------

    private static string StatusRecord(string status) =>
        $$"""{"pid":32273,"sessionId":"{{Owner}}","kind":"interactive","status":"{{status}}"}""";

    [Fact]
    public void AWaitingRecordIsReadBackFromDisk()
    {
        var home = NewHome();
        WriteRecord(home, ".claude", 32273, StatusRecord("waiting"));

        SessionPark.ClearCacheForTests();
        Assert.Equal("waiting", SessionPark.RecordStatus(32273, Owner, home));
    }

    // Negative control from the measurement that justifies the rule: a record
    // that is wrong but is not `waiting` (the real `busy`, two days stale, on a
    // pane sitting at its prompt) reads back as exactly what it says, and the
    // callers act on nothing but `waiting`.
    [Fact]
    public void ABusyRecordIsReadBackAsBusyAndNotAsWaiting()
    {
        var home = NewHome();
        WriteRecord(home, ".claude", 81151, StatusRecord("busy"));

        SessionPark.ClearCacheForTests();
        Assert.Equal("busy", SessionPark.RecordStatus(81151, Owner, home));
        Assert.NotEqual("waiting", SessionPark.RecordStatus(81151, Owner, home));
    }

    // Cached on (length, mtime), proved by making the file lie: same length,
    // same mtime, different text. A re-read would see `waiting`; the cache
    // still says `idle`. Then a real rewrite is noticed.
    [Fact]
    public void ARecordIsReadOnceAndReReadWhenItChanges()
    {
        var home = NewHome();
        var path = WriteRecord(home, ".claude", 32273, StatusRecord("idle"));

        SessionPark.ClearCacheForTests();
        Assert.Equal("idle", SessionPark.RecordStatus(32273, Owner, home));

        var written = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, StatusRecord("idle").Replace("idle", "busy"));
        File.SetLastWriteTimeUtc(path, written);
        Assert.Equal("idle", SessionPark.RecordStatus(32273, Owner, home));

        File.WriteAllText(path, StatusRecord("waiting"));
        Assert.Equal("waiting", SessionPark.RecordStatus(32273, Owner, home));

        File.WriteAllText(path, StatusRecord("idle"));
        Assert.Equal("idle", SessionPark.RecordStatus(32273, Owner, home));
    }

    // Every config dir a session can live under: the record is found, and says
    // waiting, whichever account owns it. ~/.claude-board is the one Warren's
    // lawyer session lives under; the others are the machine's own accounts.
    [Fact]
    public void AWaitingRecordIsReadBackUnderEveryConfiguredAccount()
    {
        foreach (var account in new[] { ".claude", ".claude-loginid", ".claude-board" })
        {
            var home = NewHome();
            Directory.CreateDirectory(Path.Combine(home, ".claude", "sessions"));
            WriteRecord(home, account, 32273, StatusRecord("waiting"));

            if (account != ".claude") OrbweaverSettings.AddClaudeCodeProfileDir(account);
            try
            {
                SessionPark.ClearCacheForTests();
                Assert.Equal("waiting", SessionPark.RecordStatus(32273, Owner, home));
            }
            finally
            {
                if (account != ".claude") OrbweaverSettings.RemoveClaudeCodeProfileDir(account);
            }
        }
    }

    // CB-307: an account nobody configured is discovered, so its waiting
    // record is read like ~/.claude's — no setting needed. The control is a
    // folder not named like a config dir, whose record says nothing.
    [Fact]
    public void AnUnconfiguredAccountsRecordIsReadThroughDiscovery()
    {
        var home = NewHome();
        Directory.CreateDirectory(Path.Combine(home, ".claude", "sessions"));
        WriteRecord(home, ".claude-sundaysky", 32273, StatusRecord("waiting"));

        SessionPark.ClearCacheForTests();
        Assert.Equal("waiting", SessionPark.RecordStatus(32273, Owner, home));

        var other = NewHome();
        WriteRecord(other, "claude-sundaysky", 32273, StatusRecord("waiting"));
        SessionPark.ClearCacheForTests();
        Assert.Null(SessionPark.RecordStatus(32273, Owner, other));
    }

    [Fact]
    public void NoRecordMeansNoStatus()
    {
        var home = NewHome();
        Directory.CreateDirectory(Path.Combine(home, ".claude", "sessions"));

        SessionPark.ClearCacheForTests();
        Assert.Null(SessionPark.RecordStatus(32273, Owner, home));
    }

    [Fact]
    public void ARecycledPidsRecordSaysNothingAboutTheAsker()
    {
        var home = NewHome();
        WriteRecord(home, ".claude", 32273,
            StatusRecord("waiting").Replace(Owner, "some-other-session"));

        SessionPark.ClearCacheForTests();
        Assert.Null(SessionPark.RecordStatus(32273, Owner, home));
    }

    private static string NewHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "cb-park-" + Path.GetRandomFileName());
        Directory.CreateDirectory(home);
        return home;
    }

    private static string WriteRecord(string home, string account, int pid, string json)
    {
        var dir = Path.Combine(home, account, "sessions");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, pid + ".json");
        File.WriteAllText(path, json);
        return path;
    }
}
