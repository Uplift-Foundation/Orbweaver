using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-363: what each session says about its files. A local CLI session has
// nothing to fetch and hands the path back (one class backs Claude Code, Codex
// and Grok, so one test per source pins that all three get it); a Remote
// Control session fetches over the peer link through two seams, so every arm of
// its answer is decided here without a far machine.
[Collection("Settings")]
public class ChatSessionFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb363-session-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class ThrowingMarker : IDownloadMarker
    {
        public void Mark(string path, string origin) => throw new InvalidOperationException("no mark");
    }

    private RemoteControlChatSession Peer(
        Func<string, bool>? speaks = null,
        Func<string, string, string?, IProgress<(long Got, long Total)>?, CancellationToken, Task<PeerFileResult>>? fetch = null,
        IDownloadMarker? marker = null) =>
        new("rc:.claude:evidence", ".claude", "evidence")
        {
            PeerSpeaksFiles = speaks ?? (_ => true),
            FetchPeerFile = fetch,
            FileCache = new PeerFileCache(Path.Combine(_dir, "cache"), marker: marker ?? new NoopMarker()),
        };

    private sealed class NoopMarker : IDownloadMarker
    {
        public void Mark(string path, string origin) { }
    }

    [AvaloniaTheory]
    [InlineData(SessionSource.ClaudeCode)]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.Grok)]
    public async Task ALocalSessionOfAnyCliHandsTheOwnPathBack(SessionSource source)
    {
        var session = new LocalCliChatSession("s1", new SessionStatus { Source = source, State = "idle", Title = "t" });
        IRemoteChatFiles files = session;
        Assert.Equal(ChatFileHost.ThisMachine, files.FileHost);
        Assert.Null(files.FileHostName);

        var result = await files.FetchFileAsync("/Users/a/x.pdf", null, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal("/Users/a/x.pdf", result.LocalPath);
        Assert.False(result.Executable);
        session.Dispose();
    }

    [AvaloniaFact]
    public void ARemoteSessionLivesOnAPeer() =>
        Assert.Equal(ChatFileHost.Peer, ((IRemoteChatFiles)Peer()).FileHost);

    [AvaloniaFact]
    public async Task AnOldPeerIsToldToUpdateAndNothingIsRequested()
    {
        var asked = 0;
        var session = Peer(speaks: _ => false, fetch: (_, _, _, _, _) => { asked++; throw new InvalidOperationException(); });
        var result = await session.FetchFileAsync("/a/x.pdf", null, CancellationToken.None);
        Assert.Equal("update", result.Refusal);
        Assert.Equal(0, asked);
    }

    [AvaloniaFact]
    public async Task AFilePeerAsksForTheRouteAndCachesTheBytes()
    {
        string? askedName = null, askedPath = null;
        var session = Peer(fetch: (name, path, _, _, _) =>
        {
            askedName = name;
            askedPath = path;
            return Task.FromResult<PeerFileResult>(new PeerFileResult.Bytes(new byte[] { 1, 2, 3 }, 1700000000000, 3, Exec: false));
        });

        var result = await session.FetchFileAsync("/Users/a/x.pdf", null, CancellationToken.None);

        Assert.Equal("evidence", askedName);
        Assert.Equal("/Users/a/x.pdf", askedPath);
        Assert.True(result.Ok);
        Assert.False(result.Executable);
        Assert.StartsWith(Path.Combine(_dir, "cache"), result.LocalPath);
        Assert.EndsWith("x.pdf", result.LocalPath);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(result.LocalPath!));
    }

    [AvaloniaFact]
    public async Task TheServersExecutableBitSurvivesIntoTheResult()
    {
        var session = Peer(fetch: (_, _, _, _, _) =>
            Task.FromResult<PeerFileResult>(new PeerFileResult.Bytes(new byte[] { 1 }, 5, 1, Exec: true)));
        var result = await session.FetchFileAsync("/a/tool.pdf", null, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.True(result.Executable);
    }

    [AvaloniaFact]
    public async Task ProgressAndCancellationReachTheTransport()
    {
        IProgress<(long Got, long Total)>? seen = null;
        CancellationToken seenToken = default;
        var session = Peer(fetch: (_, _, _, progress, ct) =>
        {
            seen = progress;
            seenToken = ct;
            return Task.FromResult<PeerFileResult>(new PeerFileResult.Error(MirrorProtocol.ErrNotFound, null));
        });
        var reports = new List<(long, long)>();
        var progress = new Progress<(long Done, long Total)>(reports.Add);
        using var cts = new CancellationTokenSource();

        await session.FetchFileAsync("/a/x.pdf", progress, cts.Token);

        Assert.Same(progress, seen);
        Assert.Equal(cts.Token, seenToken);
    }

    [AvaloniaTheory]
    [InlineData(MirrorProtocol.ErrForbiddenPath, "forbidden_path")]
    [InlineData(MirrorProtocol.ErrNotFound, "not_found")]
    [InlineData(MirrorProtocol.ErrTooLarge, "too_large")]
    [InlineData(MirrorProtocol.ErrUnreadable, "unreadable")]
    [InlineData(MirrorProtocol.ErrRateLimited, "rate_limited")]
    [InlineData(MirrorProtocol.ErrBusy, "busy")]
    [InlineData(MirrorProtocol.ErrNoSession, "failed")]
    [InlineData(MirrorProtocol.ErrUnreachable, "failed")]
    [InlineData(MirrorProtocol.ErrBadHash, "failed")]
    [InlineData(MirrorProtocol.ErrUnsupported, "failed")]
    [InlineData(MirrorProtocol.ErrBadFrame, "failed")]
    [InlineData("something_new", "failed")]
    public async Task AFarRefusalKeepsAnActionableCodeAndFlattensTheRest(string wire, string expected)
    {
        var session = Peer(fetch: (_, _, _, _, _) =>
            Task.FromResult<PeerFileResult>(new PeerFileResult.Error(wire, "far sentence")));
        var result = await session.FetchFileAsync("/a/x.pdf", null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(expected, result.Refusal);
        Assert.Null(result.LocalPath);
    }

    [AvaloniaFact]
    public async Task ARepeatClickOffersTheCachedStampAndOpensTheCachedCopyOnSame()
    {
        var offered = new List<string?>();
        PeerFileResult next = new PeerFileResult.Bytes(new byte[] { 7, 7 }, 1700000000000, 2, Exec: false);
        var session = Peer(fetch: (_, _, have, _, _) =>
        {
            offered.Add(have);
            return Task.FromResult(next);
        });

        var first = await session.FetchFileAsync("/a/x.pdf", null, CancellationToken.None);
        next = new PeerFileResult.Same();
        var second = await session.FetchFileAsync("/a/x.pdf", null, CancellationToken.None);

        Assert.Null(offered[0]);
        Assert.Equal("1700000000000-2", offered[1]);
        Assert.True(second.Ok);
        Assert.Equal(first.LocalPath, second.LocalPath);
        Assert.False(second.Executable);
    }

    [AvaloniaFact]
    public async Task ASameAnswerCarriesTheFarExecutableBitSoARepeatClickStillReveals()
    {
        PeerFileResult next = new PeerFileResult.Bytes(new byte[] { 7 }, 5, 1, Exec: false);
        var session = Peer(fetch: (_, _, _, _, _) => Task.FromResult(next));
        await session.FetchFileAsync("/a/x.pdf", null, CancellationToken.None);

        // The far file gained an exec bit without its mtime or size changing.
        next = new PeerFileResult.Same(Exec: true);
        var again = await session.FetchFileAsync("/a/x.pdf", null, CancellationToken.None);

        Assert.True(again.Ok);
        Assert.True(again.Executable);
    }

    [AvaloniaFact]
    public async Task ASameAnswerToARequestThatOfferedNothingIsRefused()
    {
        var session = Peer(fetch: (_, _, _, _, _) => Task.FromResult<PeerFileResult>(new PeerFileResult.Same()));
        var result = await session.FetchFileAsync("/a/x.pdf", null, CancellationToken.None);
        Assert.Equal("failed", result.Refusal);
    }

    [AvaloniaFact]
    public async Task ACacheThatCannotMarkTheFileIsAFailureNotAnOpenOfAnUnmarkedCopy()
    {
        var session = Peer(
            fetch: (_, _, _, _, _) => Task.FromResult<PeerFileResult>(new PeerFileResult.Bytes(new byte[] { 1 }, 5, 1, false)),
            marker: new ThrowingMarker());
        var result = await session.FetchFileAsync("/a/x.pdf", null, CancellationToken.None);
        Assert.Equal("failed", result.Refusal);
        Assert.Null(result.LocalPath);
    }

    [AvaloniaFact]
    public async Task WithNoMirrorClientInstalledThePeerIsTreatedAsOldAndNothingThrows()
    {
        RemoteControlSessions.ResetForTests();
        var session = new RemoteControlChatSession("rc:.claude:evidence", ".claude", "evidence");
        var result = await session.FetchFileAsync("/a/x.pdf", null, CancellationToken.None);
        Assert.Equal("update", result.Refusal);
    }

    [AvaloniaFact]
    public async Task WithTheSpeaksCheckPassingButNoClientTheFetchFailsRatherThanThrows()
    {
        RemoteControlSessions.ResetForTests();
        var session = new RemoteControlChatSession("rc:.claude:evidence", ".claude", "evidence")
        {
            PeerSpeaksFiles = _ => true,
        };
        var result = await session.FetchFileAsync("/a/x.pdf", null, CancellationToken.None);
        Assert.Equal("failed", result.Refusal);
    }
}
