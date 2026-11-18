using Xunit;

namespace Orbweaver.Tests;

// CB-363: the FILE frame's pure pieces — the stamp, the HELLO flag, the
// progress count, what a reply means to the near side, and how a far path
// becomes a cache name.
public class PeerFileProtocolTests
{
    // --- the stamp ------------------------------------------------------------

    [Theory]
    [InlineData("1700000000000-14", 1700000000000L, 14L)]
    [InlineData("0-0", 0L, 0L)]
    [InlineData("-5-3", -5L, 3L)]   // an mtime before 1970 is still a stamp
    [InlineData("-5-10", -5L, 10L)]
    public void AStampRoundTrips(string stamp, long mtime, long size)
    {
        Assert.Equal((mtime, size), MirrorProtocol.ParseFileStamp(stamp));
        Assert.Equal(stamp, MirrorProtocol.FileStamp(mtime, size));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("a-1")]
    [InlineData("1-b")]
    [InlineData("1--3")]
    [InlineData("1-+3")]
    [InlineData("-")]
    [InlineData("5")]
    [InlineData("5-")]
    [InlineData("9999999999999999999-1")]
    [InlineData("1-2-3")]
    [InlineData("1-+2")]
    public void AnythingElseIsNoStamp(string? stamp) => Assert.Null(MirrorProtocol.ParseFileStamp(stamp));

    // --- the HELLO flag ---------------------------------------------------------

    [Fact]
    public void OnlyRfEqualsOneSaysFiles()
    {
        Assert.True(MirrorProtocol.SaysItServesFiles(new Dictionary<string, string> { ["rf"] = "1" }));
        Assert.False(MirrorProtocol.SaysItServesFiles(new Dictionary<string, string> { ["rf"] = "0" }));
        Assert.False(MirrorProtocol.SaysItServesFiles(new Dictionary<string, string> { ["pr"] = "1" }));
        Assert.False(MirrorProtocol.SaysItServesFiles(null));
    }

    // The refusal codes survive the grammar as written: BuildFrame refuses
    // only `;` and `=`, which is the check the plan asked for before using
    // underscores.
    [Fact]
    public void TheUnderscoredCodesSurviveAFrame()
    {
        foreach (var code in new[] { MirrorProtocol.ErrForbiddenPath, MirrorProtocol.ErrNotFound, MirrorProtocol.ErrTooLarge,
                     MirrorProtocol.ErrUnreadable, MirrorProtocol.ErrRateLimited, MirrorProtocol.ErrBadFrame, MirrorProtocol.ErrBusy })
        {
            var frame = MirrorProtocol.TryParseFrame(MirrorProtocol.BuildFrame(MirrorProtocol.Err, "x",
                new Dictionary<string, string> { ["code"] = code }));
            Assert.Equal(code, frame!.Get("code"));
        }
    }

    // --- progress --------------------------------------------------------------

    private static MirrorProtocol.MirrorFrame Piece(int seq, int of, byte[] bytes, string? whole = null)
    {
        var fields = new Dictionary<string, string> { ["seq"] = seq.ToString(), ["of"] = of.ToString() };
        if (whole is not null) fields["H"] = whole;
        return MirrorProtocol.TryParseFrame(MirrorProtocol.BuildFrame(MirrorProtocol.Chunk, "t", fields, bytes))!;
    }

    [Fact]
    public void ReceivedCountsVerifiedBytesOnceEvenWhenAPieceArrivesTwice()
    {
        byte[] a = [1, 2, 3], b = [4, 5];
        var assembly = new MirrorProtocol.MirrorAssembly();
        Assert.Equal(0, assembly.Received);
        assembly.Offer(Piece(0, 2, a));
        Assert.Equal(3, assembly.Received);
        assembly.Offer(Piece(0, 2, a));
        Assert.Equal(3, assembly.Received);
        var done = assembly.Offer(Piece(1, 2, b, MirrorProtocol.Hash([.. a, .. b])));
        Assert.Equal(MirrorProtocol.AssemblyState.Complete, done.State);
        Assert.Equal(5, assembly.Received);
    }

    // --- what a reply means ---------------------------------------------------------

    private static Dictionary<string, string> Stamp(long size, long mt = 7, string x = "0") =>
        new() { ["sz"] = size.ToString(), ["mt"] = mt.ToString(), ["x"] = x };

    [Fact]
    public void BytesCarryTheirStampAndExecBit()
    {
        var r = Assert.IsType<PeerFileResult.Bytes>(RemoteMirrorClient.FileResultOf(true, null, null, Stamp(3, 9, "1"), [1, 2, 3]));
        Assert.Equal((9L, 3L, true), (r.MtimeMs, r.Size, r.Exec));
        Assert.False(Assert.IsType<PeerFileResult.Bytes>(
            RemoteMirrorClient.FileResultOf(true, null, null, new Dictionary<string, string> { ["sz"] = "1", ["mt"] = "1" }, [1])).Exec);
    }

    [Fact]
    public void BytesThatDisagreeWithTheirOwnStampAreBadHash()
    {
        Assert.Equal(MirrorProtocol.ErrBadHash,
            Assert.IsType<PeerFileResult.Error>(RemoteMirrorClient.FileResultOf(true, null, null, Stamp(4), [1, 2, 3])).Code);
        Assert.Equal(MirrorProtocol.ErrBadHash,
            Assert.IsType<PeerFileResult.Error>(RemoteMirrorClient.FileResultOf(true, null, null, new Dictionary<string, string> { ["sz"] = "3" }, [1, 2, 3])).Code);
    }

    // The far exec bit rides on `same`, so a cached copy is judged as the far
    // file is; a far side that sends no `x` reads as not executable.
    [Fact]
    public void SameCarriesTheFarExecBit()
    {
        Assert.True(Assert.IsType<PeerFileResult.Same>(RemoteMirrorClient.FileResultOf(true, null, null,
            new Dictionary<string, string> { ["same"] = "1", ["x"] = "1" }, null)).Exec);
        Assert.False(Assert.IsType<PeerFileResult.Same>(RemoteMirrorClient.FileResultOf(true, null, null,
            new Dictionary<string, string> { ["same"] = "1", ["x"] = "0" }, null)).Exec);
        Assert.False(new PeerFileResult.Same().Exec);
    }

    // --- the near side's own cap -------------------------------------------------------

    private const long Cap = MirrorProtocol.MaxFileBytes;
    private static readonly long MaxPieces = Cap / MirrorProtocol.PictureChunkBytes;

    [Fact]
    public void AFirstPieceWithinTheCapIsRecordedAsTheDeclaredSize()
    {
        Assert.Equal((null, 1000L), RemoteMirrorClient.FileOverCap(-1, 1000, 1, Cap));
        Assert.Equal((null, Cap), RemoteMirrorClient.FileOverCap(-1, Cap, MaxPieces, Cap));
        Assert.Equal((null, 1000L), RemoteMirrorClient.FileOverCap(1000, 1000, 1, Cap));
        Assert.Equal((null, 0L), RemoteMirrorClient.FileOverCap(-1, 0, 1, 1));
    }

    [Theory]
    [InlineData(-1L, 1_000_000_000_000L, 1L)]   // declares a terabyte
    [InlineData(-1L, 10L, 1_000_000L)]            // more pieces than the cap could need
    [InlineData(-1L, -1L, 1L)]                    // no size at all
    [InlineData(1000L, 2000L, 1L)]                // a size that moved between pieces
    public void APieceThatBreaksTheCapIsTooLargeAndDeclaresNothingNew(long declared, long size, long of)
    {
        var (refusal, kept) = RemoteMirrorClient.FileOverCap(declared, size, of, Cap);
        Assert.Equal(MirrorProtocol.ErrTooLarge, refusal);
        Assert.Equal(declared, kept);
    }

    [Fact]
    public void AnOkWithSameIsSameAndAnyOtherEmptyOkIsUnsupported()
    {
        Assert.IsType<PeerFileResult.Same>(RemoteMirrorClient.FileResultOf(true, null, null, new Dictionary<string, string> { ["same"] = "1" }, null));
        Assert.Equal(MirrorProtocol.ErrUnsupported,
            Assert.IsType<PeerFileResult.Error>(RemoteMirrorClient.FileResultOf(true, null, null, new Dictionary<string, string> { ["same"] = "0" }, null)).Code);
        Assert.Equal(MirrorProtocol.ErrUnsupported,
            Assert.IsType<PeerFileResult.Error>(RemoteMirrorClient.FileResultOf(true, null, null, null, null)).Code);
    }

    [Fact]
    public void ARefusalIsItsCodeAndNoAnswerIsUnreachable()
    {
        var refused = Assert.IsType<PeerFileResult.Error>(
            RemoteMirrorClient.FileResultOf(false, MirrorProtocol.ErrTooLarge, "too_large", null, null));
        Assert.Equal((MirrorProtocol.ErrTooLarge, "too_large"), (refused.Code, refused.Reason));
        Assert.Equal(MirrorProtocol.ErrUnreachable,
            Assert.IsType<PeerFileResult.Error>(RemoteMirrorClient.FileResultOf(false, null, "timed out", null, null)).Code);
    }

    // --- the cache's names ---------------------------------------------------------

    [Fact]
    public void TheKeyIsSixteenHexDigitsAndDependsOnMachineAndPath()
    {
        var k = PeerFileCache.Key("mini", "/a/b.pdf");
        Assert.Matches("^[0-9a-f]{16}$", k);
        Assert.Equal(k, PeerFileCache.Key("mini", "/a/b.pdf"));
        Assert.NotEqual(k, PeerFileCache.Key("windows", "/a/b.pdf"));
        Assert.NotEqual(k, PeerFileCache.Key("mini", "/a/c.pdf"));
    }

    [Theory]
    [InlineData("/Users/x/Documents/1_Response.pdf", "1_Response.pdf")]
    [InlineData(@"C:\Users\x\Motion (Stoel).pdf", "Motion (Stoel).pdf")]
    [InlineData("/a/what?<>|*\".pdf", "what______.pdf")]
    [InlineData("/a/\u202eexe.pdf", "exe.pdf")]
    [InlineData("/a/bell\u0007.txt", "bell.txt")]
    [InlineData("/a/trailing. . ", "trailing")]
    [InlineData("/a/  leading.txt", "leading.txt")]
    [InlineData("/a/..", "file")]
    [InlineData("/a/", "file")]
    [InlineData("/a/...", "file")]
    [InlineData("/a/CON.txt", "_CON.txt")]
    [InlineData("/a/lpt1", "_lpt1")]
    [InlineData("/a/.env", "_.env")]
    [InlineData("plain.pdf", "plain.pdf")]
    [InlineData("/a/AUX .txt", "_AUX .txt")]          // Windows reads the stem as AUX
    [InlineData("/a/NUL .", "_NUL")]
    [InlineData("/a/COM\u00b9.txt", "_COM\u00b9.txt")]
    [InlineData("/a/conin$", "_conin$")]
    [InlineData("/a/\ud83d\ude00.png", "\ud83d\ude00.png")]
    public void ASafeNameKeepsTheNameAndExtensionAndNothingDangerous(string path, string expected) =>
        Assert.Equal(expected, PeerFileCache.SafeName(path));

    [Fact]
    public void ALongNameKeepsItsExtensionAndAnAbsurdExtensionIsDropped()
    {
        var name = PeerFileCache.SafeName("/a/" + new string('x', 300) + ".pdf");
        Assert.Equal(120, name.Length);
        Assert.EndsWith(".pdf", name);

        var weird = PeerFileCache.SafeName("/a/" + new string('x', 200) + "." + new string('y', 40));
        Assert.Equal(120, weird.Length);
        Assert.Equal(new string('x', 120), weird);
    }

    // Half a surrogate pair (which a Windows name may hold) is dropped. Built
    // at runtime: in an attribute it would reach the test as U+FFFD.
    [Fact]
    public void ALoneSurrogateIsDropped()
    {
        Assert.Equal("bc.txt", PeerFileCache.SafeName("/a/b" + (char)0xD800 + "c.txt"));
        Assert.Equal("bc.txt", PeerFileCache.SafeName("/a/b" + (char)0xDC00 + "c.txt"));
        Assert.Equal("bc.txt", PeerFileCache.SafeName("/a/bc.txt" + (char)0xD800));   // high at the very end
    }

    // A cut at the length limit never leaves half a surrogate pair, which no
    // file API here would accept.
    [Fact]
    public void TheLengthCutNeverSplitsASurrogatePair()
    {
        var name = PeerFileCache.SafeName("/a/" + new string('x', 119) + "\ud83d\ude00" + "yyyy");
        Assert.Equal(new string('x', 119), name);

        var withExt = PeerFileCache.SafeName("/a/" + new string('x', 115) + "\ud83d\ude00" + "yyyy.pdf");
        Assert.Equal(new string('x', 115) + ".pdf", withExt);

        Assert.Equal("file", PeerFileCache.SafeName("/a/" + new string('.', 1) + new string(' ', 200) + "." + new string('z', 30)));
    }

    // --- the sweep's tolerance of things vanishing under it -------------------------------

    [Fact]
    public void AFolderOrFileThatVanishedIsEmptyAndZero()
    {
        var gone = Path.Combine(Path.GetTempPath(), "cb363-gone-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(PeerFileCache.FilesIn(gone, "*"));
        Assert.Equal(0, PeerFileCache.LengthOrZero(Path.Combine(gone, "f")));
    }

    // --- the download mark's content ---------------------------------------------------------

    [Fact]
    public void TheQuarantineValueIsFlagsHexTimeAndAgent()
    {
        var at = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal($"0081;{new DateTimeOffset(at).ToUnixTimeSeconds():x8};Orbweaver;", OsDownloadMarker.QuarantineValue(at, "Orbweaver"));
        Assert.Contains("ZoneId=3", OsDownloadMarker.ZoneIdentifier());
    }

    [Fact]
    public void ACachedFileOffersItsStampAsHave() =>
        Assert.Equal("12-34", new PeerFileCache.CachedFile("/x", 12, 34).Have);
}
