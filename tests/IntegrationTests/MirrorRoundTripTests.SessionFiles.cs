using Xunit;

namespace Orbweaver.Tests;

// CB-363: the panel's side of FILE through the same real client and server pair
// MirrorRoundTripTests.Files.cs uses. RemoteControlChatSession is handed the
// harness's real RemoteMirrorClient the way the app hands it the direct link's
// (RemoteControlSessions.UseMirrorClientForTests), and its default seams — the
// SpeaksFiles check and the FetchFileAsync call — run unreplaced. Nothing is
// faked but the two SendFrame delegates, so what is asserted is what a click in
// a chat panel would be given: a path in the cache holding the far bytes, or
// the refusal a person would read.
public partial class MirrorRoundTripTests
{
    private RemoteControlChatSession SessionOver(Harness harness)
    {
        RemoteControlSessions.UseMirrorClientForTests(".claude", harness.Client);
        return new RemoteControlChatSession("rc:.claude:job-hunter", ".claude", "job-hunter")
        {
            FileCache = new PeerFileCache(Path.Combine(_dir, "peer-files"), marker: new NoMark()),
        };
    }

    [Fact]
    public async Task ASessionFetchesARealFileThroughTheRealClientAndCachesTheSameBytes()
    {
        var (harness, tree) = await FileHarnessAsync();
        using var _ = tree;
        try
        {
            var bytes = Random(MirrorProtocol.PictureChunkBytes + 99);
            var path = tree.Write("report.pdf", bytes);
            var session = SessionOver(harness);
            var reports = new Reports();

            var result = await session.FetchFileAsync(path, reports, CancellationToken.None);

            Assert.True(result.Ok);
            Assert.False(result.Executable);
            Assert.StartsWith(Path.Combine(_dir, "peer-files"), result.LocalPath);
            Assert.EndsWith("report.pdf", result.LocalPath);
            Assert.Equal(bytes, File.ReadAllBytes(result.LocalPath!));
            Assert.Equal(2, reports.Seen.Count);
            Assert.Equal(bytes.LongLength, reports.Seen[^1].Got);
        }
        finally { RemoteControlSessions.ResetForTests(); }
    }

    [Fact]
    public async Task ASessionTurnsTheFarSidesRefusalsIntoCodesThePanelHasSentencesFor()
    {
        var (harness, tree) = await FileHarnessAsync();
        using var _ = tree;
        try
        {
            var session = SessionOver(harness);

            // Control: the same session and the same settings serve a real file,
            // so the refusals below are the far side's and not an empty setup.
            Assert.True((await session.FetchFileAsync(tree.Write("ok.pdf", "ok"u8.ToArray()), null, CancellationToken.None)).Ok);

            var outside = await session.FetchFileAsync(Path.Combine(tree.Outside, "secret.txt"), null, CancellationToken.None);
            Assert.Equal("forbidden_path", outside.Refusal);
            Assert.Null(outside.LocalPath);

            var missing = await session.FetchFileAsync(Path.Combine(tree.Project, "gone.pdf"), null, CancellationToken.None);
            Assert.Equal("not_found", missing.Refusal);
        }
        finally { RemoteControlSessions.ResetForTests(); }
    }

    [Fact]
    public async Task ASessionOnAFarSideThatDoesNotServeFilesIsToldToUpdateWithoutAnyRequestSent()
    {
        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", WriteTranscript("files.jsonl", Conversation(2)));
        Assert.True(await harness.HandshakeAsync("job-hunter"));
        try
        {
            var session = SessionOver(harness);
            var before = harness.ToServer.Count;

            var result = await session.FetchFileAsync("/any/thing.pdf", null, CancellationToken.None);

            Assert.Equal("update", result.Refusal);
            Assert.Equal(before, harness.ToServer.Count);
        }
        finally { RemoteControlSessions.ResetForTests(); }
    }

    [Fact]
    public async Task ASecondClickOnAnUnchangedFileCrossesNoBytesAndOpensTheSameCachedCopy()
    {
        var (harness, tree) = await FileHarnessAsync();
        using var _ = tree;
        try
        {
            var bytes = Random(MirrorProtocol.PictureChunkBytes + 7);
            var path = tree.Write("report.pdf", bytes);
            var session = SessionOver(harness);

            var first = await session.FetchFileAsync(path, null, CancellationToken.None);
            harness.ForgetFramesSoFar();
            var second = await session.FetchFileAsync(path, null, CancellationToken.None);

            Assert.True(first.Ok);
            Assert.True(second.Ok);
            Assert.Equal(first.LocalPath, second.LocalPath);
            Assert.Equal(0, harness.ChunkFrames);

            // Control: a changed file is pulled again, so "same" is not simply
            // what a second click always gets.
            var changed = Random(MirrorProtocol.PictureChunkBytes + 8);
            tree.Write("report.pdf", changed);
            var third = await session.FetchFileAsync(path, null, CancellationToken.None);
            Assert.True(third.Ok);
            Assert.NotEqual(first.LocalPath, third.LocalPath);
            Assert.Equal(changed, File.ReadAllBytes(third.LocalPath!));
            Assert.True(harness.ChunkFrames > 0);
        }
        finally { RemoteControlSessions.ResetForTests(); }
    }
}
