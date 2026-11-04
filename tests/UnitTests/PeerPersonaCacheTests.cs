using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Xunit;

namespace Orbweaver.Tests;

// PeerPersonaCache and the wire half of a peer persona (CB-306's second
// reopen): the pump must never read a persona file, so the read is driven by a
// fake that can be made to block for ever.
public class PeerPersonaCacheTests : IDisposable
{
    private readonly ManualResetEventSlim _release = new(true);
    private readonly List<(SessionSource Source, string Cwd, string Agent, IReadOnlyList<string> Dirs)> _reads = new();
    private RemoteMirrorServer.PersonaRead? _answer = new("Faraday", "Samantha", 1.2, null);
    private Func<string, byte[]?> _known = _ => null;

    public void Dispose() => _release.Set();

    private PeerPersonaCache Cache() => new(
        (source, cwd, agent, dirs) =>
        {
            lock (_reads) _reads.Add((source, cwd, agent, dirs));
            _release.Wait();
            return _answer;
        },
        () => DateTime.UtcNow,
        work => new Thread(() => work()) { IsBackground = true }.Start(),
        id => _known(id));

    private static SessionStatus Status(SessionSource source = SessionSource.ClaudeCode, string cwd = "/Users/x/Documents/GTD/Evidence") =>
        new() { Source = source, Cwd = cwd, Agent = "felicity" };

    private static void Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "timed out waiting for the read");
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void AReadThatNeverReturnsIsAnsweredWithNoPersonaAtOnce()
    {
        _release.Reset();
        var cache = Cache();

        var clock = Stopwatch.StartNew();
        Assert.Null(cache.Peek(Status(), Array.Empty<string>(), false));
        Assert.Null(cache.Peek(Status(), Array.Empty<string>(), false));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(1, cache.InFlight);
        Until(() => { lock (_reads) return _reads.Count == 1; });
    }

    [Fact]
    public void OnceTheReadAnswersThePersonaIsOffered()
    {
        _release.Reset();
        var cache = Cache();
        Assert.Null(cache.Peek(Status(), new[] { "/h/.claude", "/h/.claude-work" }, false));

        _release.Set();
        Until(() => cache.InFlight == 0);

        var persona = cache.Peek(Status(), new[] { "/h/.claude", "/h/.claude-work" }, false);
        Assert.Equal("Faraday", persona!.Name);
        var read = Assert.Single(_reads);
        Assert.Equal(new[] { "/h/.claude", "/h/.claude-work" }, read.Dirs);
        Assert.Equal("felicity", read.Agent);
        Assert.Equal(SessionSource.ClaudeCode, read.Source);
    }

    [Fact]
    public void NoConfigDirsReachTheReadAsAnEmptyListNotOneEmptyName()
    {
        var cache = Cache();
        cache.Peek(Status(), Array.Empty<string>(), false);
        Until(() => cache.InFlight == 0);

        Assert.Empty(Assert.Single(_reads).Dirs);
    }

    [Fact]
    public void AnotherAccountsDirsAreAnotherRead()
    {
        var cache = Cache();
        cache.Peek(Status(), new[] { "/h/.claude" }, false);
        Until(() => cache.InFlight == 0);
        cache.Peek(Status(), new[] { "/h/.claude-work" }, false);
        Until(() => cache.InFlight == 0);

        Assert.Equal(2, _reads.Count);
    }

    [Theory]
    [InlineData(SessionSource.OpenClaw, "/p")]
    [InlineData(SessionSource.ClaudeCode, " ")]
    public void ASessionWithNoLocalFolderIsNeverRead(SessionSource source, string cwd)
    {
        var cache = Cache();

        Assert.Null(cache.Peek(Status(source, cwd), Array.Empty<string>(), false));
        Assert.Equal(0, cache.InFlight);
        Assert.Empty(_reads);
    }

    [Fact]
    public void ReadPeerPersonaReadsNothingForASessionWithNoLocalFolder()
    {
        Assert.Null(RemoteMirrorServer.ReadPeerPersona(SessionSource.RemoteControl, "/p", "", Array.Empty<string>()));
        Assert.Null(RemoteMirrorServer.ReadPeerPersona(SessionSource.ClaudeCode, "", "", Array.Empty<string>()));
    }

    // --- ToPeerPersona: the half the pump runs, with no IO -------------------

    private static readonly PeerAvatarStore.Picture Picture = new(new byte[] { 1, 2, 3 }, "pic-1");

    [Fact]
    public void NoReadMeansNoPersona() =>
        Assert.Null(RemoteMirrorServer.ToPeerPersona(null, true, _ => new byte[1]));

    [Fact]
    public void AnEmptyReadMeansNoPersona() =>
        Assert.Null(RemoteMirrorServer.ToPeerPersona(new RemoteMirrorServer.PersonaRead(null, null, null, null), false, _ => null));

    [Fact]
    public void APersonaWithNoPictureCarriesItsNameAndVoice()
    {
        var peer = RemoteMirrorServer.ToPeerPersona(new("Faraday", "Samantha", 1.2, null), true, _ => null)!;

        Assert.Equal("Faraday", peer.Name);
        Assert.Equal("Samantha", peer.Voice);
        Assert.Null(peer.Avatar);
        Assert.Null(peer.AvatarId);
    }

    [Fact]
    public void AnAskerThatFetchesByIdIsSentTheIdWhileTheStoreStillKnowsIt()
    {
        var peer = RemoteMirrorServer.ToPeerPersona(new("Faraday", null, null, Picture), true, id => id == "pic-1" ? Picture.Bytes : null)!;

        Assert.Equal("pic-1", peer.AvatarId);
        Assert.Null(peer.Avatar);
    }

    [Fact]
    public void AnIdTheStoreHasEvictedIsSentAsBytesInstead()
    {
        var peer = RemoteMirrorServer.ToPeerPersona(new("Faraday", null, null, Picture), true, _ => null)!;

        Assert.Null(peer.AvatarId);
        Assert.Equal(Picture.Bytes, peer.Avatar);
    }

    [Fact]
    public void AnAskerThatCannotFetchByIdIsSentTheBytes()
    {
        var peer = RemoteMirrorServer.ToPeerPersona(new("Faraday", null, null, Picture), false, _ => Picture.Bytes)!;

        Assert.Equal(Picture.Bytes, peer.Avatar);
        Assert.Null(peer.AvatarId);
    }

    [Fact]
    public void TheCachedPictureIsSentAsAnIdThroughTheCache()
    {
        _answer = new("Faraday", null, null, Picture);
        _known = id => id == "pic-1" ? Picture.Bytes : null;
        var cache = Cache();
        cache.Peek(Status(), Array.Empty<string>(), true);
        Until(() => cache.InFlight == 0);

        Assert.Equal("pic-1", cache.Peek(Status(), Array.Empty<string>(), true)!.AvatarId);
    }

    [Fact]
    public void TheSharedCacheReadsTheRealPersonaOffThePump()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "personacache-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "CLAUDE.md"), "## Persona\n**Name:** Faraday\n");
        try
        {
            var status = Status(cwd: dir);
            PeerPersonaCache.Shared.Peek(status, Array.Empty<string>(), false);
            Until(() => PeerPersonaCache.Shared.Peek(status, Array.Empty<string>(), false) is not null);

            Assert.Equal("Faraday", PeerPersonaCache.Shared.Peek(status, Array.Empty<string>(), false)!.Name);
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }
}
