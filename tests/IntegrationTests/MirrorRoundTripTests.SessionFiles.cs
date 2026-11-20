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

    // Frames the harness carried after a point, parsed. Snapshotted with
    // ToArray so a late append cannot move the list under the enumeration.
    private static IReadOnlyList<MirrorProtocol.MirrorFrame> FramesAfter(List<string> lines, int from) =>
        lines.ToArray().Skip(from).Select(MirrorProtocol.TryParseFrame).OfType<MirrorProtocol.MirrorFrame>().ToList();

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

            var serverBefore1 = harness.ToServer.Count;
            var clientBefore1 = harness.ToClient.Count;
            var first = await session.FetchFileAsync(path, null, CancellationToken.None);
            var stampOnDisk = MirrorProtocol.FileStamp(
                new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds(), bytes.LongLength);

            Assert.True(first.Ok, $"the first fetch should succeed, got refusal '{first.Refusal}'");

            // Fetch 1's request and its chunks, found by the request's own id.
            // This is the positive control for the id filter used on fetch 2:
            // if chunks were not tied to the request id, "none for this id"
            // below would prove nothing.
            var request1 = Assert.Single(FramesAfter(harness.ToServer, serverBefore1), f => f.Type == MirrorProtocol.ReadFile);
            Assert.False(request1.Fields.ContainsKey(MirrorProtocol.HaveField), "the first request has nothing cached, so it must not offer a stamp");
            Assert.True(
                FramesAfter(harness.ToClient, clientBefore1).Count(f => f.Type == MirrorProtocol.Chunk && f.Id == request1.Id) >= 2,
                "the first fetch's bytes should arrive as CHUNK frames carrying its request id");

            // The cache holds the stamp the far disk has: if these differ, the
            // far mtime moved between the stat and the read, or the stamp was
            // stored in a different resolution than the server compares.
            Assert.Equal(stampOnDisk, session.FileCache.Newest(session.MachineName ?? ".claude", path)?.Have);

            var serverBefore2 = harness.ToServer.Count;
            var clientBefore2 = harness.ToClient.Count;
            var second = await session.FetchFileAsync(path, null, CancellationToken.None);

            Assert.True(second.Ok, $"the second fetch should succeed, got refusal '{second.Refusal}'");
            Assert.Equal(first.LocalPath, second.LocalPath);

            var request2 = Assert.Single(FramesAfter(harness.ToServer, serverBefore2), f => f.Type == MirrorProtocol.ReadFile);
            Assert.True(request2.Fields.TryGetValue(MirrorProtocol.HaveField, out var offered),
                "the second request should offer the cached stamp as `have`");
            Assert.Equal(stampOnDisk, offered);

            var replies2 = FramesAfter(harness.ToClient, clientBefore2).Where(f => f.Id == request2.Id).ToList();
            Assert.Contains(replies2, f => f.Type == MirrorProtocol.Ok
                && f.Fields.TryGetValue(MirrorProtocol.SameField, out var same) && same == "1");
            Assert.DoesNotContain(replies2, f => f.Type == MirrorProtocol.Chunk);

            // Control: a changed file is pulled again, so "same" is not simply
            // what a second click always gets.
            var changed = Random(MirrorProtocol.PictureChunkBytes + 8);
            tree.Write("report.pdf", changed);
            var clientBefore3 = harness.ToClient.Count;
            var serverBefore3 = harness.ToServer.Count;
            var third = await session.FetchFileAsync(path, null, CancellationToken.None);
            Assert.True(third.Ok, $"the third fetch should succeed, got refusal '{third.Refusal}'");
            Assert.NotEqual(first.LocalPath, third.LocalPath);
            Assert.Equal(changed, File.ReadAllBytes(third.LocalPath!));

            var request3 = Assert.Single(FramesAfter(harness.ToServer, serverBefore3), f => f.Type == MirrorProtocol.ReadFile);
            Assert.True(
                FramesAfter(harness.ToClient, clientBefore3).Any(f => f.Type == MirrorProtocol.Chunk && f.Id == request3.Id),
                "the changed file should arrive as CHUNK frames, since its size moved off the cached stamp");
        }
        finally { RemoteControlSessions.ResetForTests(); }
    }

    // The "from <machine>" in the fetching note and every refusal is this name,
    // so it is read off the real session over the real client: null until the
    // roster has offered the session, then whatever machine served it.
    [Fact]
    public async Task ASessionNamesTheMachineOnlyOnceTheRosterHasOfferedIt()
    {
        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", WriteTranscript("files.jsonl", Conversation(2)));
        try
        {
            var session = SessionOver(harness);
            Assert.Null(((IRemoteChatFiles)session).FileHostName);

            Assert.True(await harness.HandshakeAsync("job-hunter"));

            var named = ((IRemoteChatFiles)session).FileHostName;
            Assert.False(string.IsNullOrWhiteSpace(named));
            Assert.Equal(harness.Client.RelayFor("job-hunter"), named);
        }
        finally { RemoteControlSessions.ResetForTests(); }
    }
}
