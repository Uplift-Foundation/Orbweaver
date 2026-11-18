using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace Orbweaver.Tests;

// CB-363: the FILE frame end to end — the real RemoteMirrorClient asking the
// real RemoteMirrorServer for a file on a real disk (BrowseFileSystem,
// PeerFileSource) through the real MirrorProtocol, with only the two
// SendFrame delegates standing in for PeerLink, exactly as the rest of this
// class does.
//
// The tree lives under the user's profile rather than the temp dir, for the
// reason BrowseAdversarialTests gives: on macOS the temp dir is under
// /private/var, a system folder, so a tree there is refused for the right
// reason and tests nothing. Its own "home" is handed to the BrowseFileSystem,
// so the real ~/Library is never in play. Every refusal below is paired with
// a control that the same settings serve a real file, so a refusal cannot be
// an empty tree or a broken setup.
public partial class MirrorRoundTripTests
{
    private sealed class FilesWindowsFact : FactAttribute
    {
        public FilesWindowsFact() { if (!OperatingSystem.IsWindows()) Skip = "8.3 short names exist only on the Windows leg"; }
    }

    private sealed class FilesUnixFact : FactAttribute
    {
        public FilesUnixFact() { if (OperatingSystem.IsWindows()) Skip = "needs an exec bit / a filesystem FIFO, which NTFS has neither of"; }
    }

    private sealed class FileTree : IDisposable
    {
        private static string Base() => OperatingSystem.IsWindows()
            ? Path.GetTempPath()
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public readonly string Root = Path.Combine(Base(), "cb363-files-" + Guid.NewGuid().ToString("N"));
        public readonly string Home, Project, Other, Denied, Outside;
        public BrowseSettings Settings;
        public readonly BrowseFileSystem Fs;
        public IPeerFileSource Source = PeerFileSource.Instance;

        public FileTree()
        {
            Home = Path.Combine(Root, "home");
            Project = Path.Combine(Home, "proj");
            Other = Path.Combine(Home, "other");
            Denied = Path.Combine(Project, "denied");
            Outside = Path.Combine(Root, "outside");
            foreach (var d in new[] { Project, Path.Combine(Project, "sub"), Other, Denied, Outside }) Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(Project, "sub", "notes.txt"), "notes");
            File.WriteAllText(Path.Combine(Other, "a.txt"), "in a configured root");
            File.WriteAllText(Path.Combine(Home, "secret.txt"), "home, toggle off");
            File.WriteAllText(Path.Combine(Outside, "secret.txt"), "outside everything");
            File.WriteAllText(Path.Combine(Denied, "x.txt"), "in a system folder");

            Fs = new BrowseFileSystem(Home, deniedExact: [], deniedTrees: [Denied]);
            Settings = new BrowseSettings([Other], Home: false);
        }

        public PeerFileEnv Env() => new(Settings, Fs, Source);

        public string Write(string relative, byte[] bytes)
        {
            var path = Path.Combine(Project, relative);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // Reports synchronously, so the test sees every report before the fetch
    // returns. Progress<T> posts them, which would race the assertions.
    private sealed class Reports : IProgress<(long Got, long Total)>
    {
        public readonly List<(long Got, long Total)> Seen = new();
        public void Report((long Got, long Total) value) { lock (Seen) Seen.Add(value); }
    }

    private async Task<(Harness Harness, FileTree Tree)> FileHarnessAsync(FileTree? tree = null, bool sessionInProject = true)
    {
        tree ??= new FileTree();
        var harness = new Harness(_dir, files: tree.Env);
        harness.AddSession("job-hunter", WriteTranscript("files.jsonl", Conversation(2)),
            s => s.Cwd = sessionInProject ? tree.Project : tree.Home);
        Assert.True(await harness.HandshakeAsync("job-hunter"));
        return (harness, tree);
    }

    private static string CodeOf(PeerFileResult r) =>
        r is PeerFileResult.Error e ? e.Code : throw new Xunit.Sdk.XunitException("expected a refusal, got " + r);

    private static PeerFileResult.Bytes BytesOf(PeerFileResult r) =>
        r as PeerFileResult.Bytes ?? throw new Xunit.Sdk.XunitException("expected bytes, got " + r);

    private static byte[] Random(int n)
    {
        var b = new byte[n];
        new System.Random(363).NextBytes(b);
        return b;
    }

    // --- the claim ------------------------------------------------------------

    // A file larger than one piece crosses as several, arrives byte for byte,
    // carries the far file's own stamp, and reports progress as it goes.
    [Fact]
    public async Task AFileInTheSessionsFolderArrivesInPiecesAsTheSameBytesWithProgress()
    {
        var (harness, tree) = await FileHarnessAsync();
        using var _ = tree;
        var bytes = Random(MirrorProtocol.PictureChunkBytes * 2 + 1234);
        var path = tree.Write("report.pdf", bytes);
        harness.ForgetFramesSoFar();
        var reports = new Reports();

        Assert.True(harness.Client.SpeaksFiles("job-hunter"));
        var got = BytesOf(await harness.Client.FetchFileAsync("job-hunter", path, null, reports));

        Assert.Equal(bytes, got.Data);
        Assert.Equal(bytes.LongLength, got.Size);
        Assert.Equal(new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds(), got.MtimeMs);
        Assert.False(got.Exec);
        Assert.Equal(3, harness.ChunkFrames);
        Assert.Equal(3, reports.Seen.Count);
        Assert.All(reports.Seen, r => Assert.Equal(bytes.LongLength, r.Total));
        Assert.Equal(bytes.LongLength, reports.Seen[^1].Got);
        Assert.True(reports.Seen.Select(r => r.Got).SequenceEqual(reports.Seen.Select(r => r.Got).Order()));
    }

    // `have` with the far file's current stamp is answered `same` and no
    // bytes cross; a stale stamp gets the bytes.
    [Fact]
    public async Task AStampThatStillMatchesIsAnsweredSameAndAStaleOneGetsTheBytes()
    {
        var (harness, tree) = await FileHarnessAsync();
        using var _ = tree;
        var path = tree.Write("report.pdf", Encoding.UTF8.GetBytes("v1"));
        var first = BytesOf(await harness.Client.FetchFileAsync("job-hunter", path, null));
        harness.ForgetFramesSoFar();

        var same = await harness.Client.FetchFileAsync("job-hunter", path, MirrorProtocol.FileStamp(first.MtimeMs, first.Size));
        Assert.IsType<PeerFileResult.Same>(same);
        Assert.Equal(0, harness.ChunkFrames);

        var stale = await harness.Client.FetchFileAsync("job-hunter", path, MirrorProtocol.FileStamp(first.MtimeMs - 1, first.Size));
        Assert.Equal("v1"u8.ToArray(), BytesOf(stale).Data);
    }

    // An executable file is still served — whether to open or reveal it is
    // the near side's call — but says so.
    [FilesUnixFact]
    public async Task AnExecutableFileSaysSo()
    {
        var (harness, tree) = await FileHarnessAsync();
        using var _ = tree;
        var path = tree.Write("run.sh", "#!/bin/sh\n"u8.ToArray());
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.True(BytesOf(await harness.Client.FetchFileAsync("job-hunter", path, null)).Exec);
    }

    // --- scope -------------------------------------------------------------------

    // `..` is resolved on the serving side: out of the root is refused, and
    // the control — a `..` that stays inside — is served.
    [Fact]
    public async Task DotDotOutOfTheRootIsRefusedAndDotDotThatStaysInsideIsServed()
    {
        var (harness, tree) = await FileHarnessAsync();
        using var _ = tree;
        var sep = Path.DirectorySeparatorChar;

        var escape = tree.Project + sep + ".." + sep + "secret.txt";
        Assert.Equal(MirrorProtocol.ErrForbiddenPath, CodeOf(await harness.Client.FetchFileAsync("job-hunter", escape, null)));

        var inside = tree.Project + sep + "sub" + sep + ".." + sep + "sub" + sep + "notes.txt";
        Assert.Equal("notes"u8.ToArray(), BytesOf(await harness.Client.FetchFileAsync("job-hunter", inside, null)).Data);
    }

    // A link inside the root that points out of it is judged where it points.
    [Fact]
    public async Task ASymlinkOutOfTheRootIsRefusedAndOneInsideItIsServed()
    {
        var (harness, tree) = await FileHarnessAsync();
        using var _ = tree;
        var outLink = Path.Combine(tree.Project, "escape.txt");
        var inLink = Path.Combine(tree.Project, "alias.txt");
        var dirLink = Path.Combine(tree.Project, "up");
        try
        {
            File.CreateSymbolicLink(outLink, Path.Combine(tree.Outside, "secret.txt"));
            File.CreateSymbolicLink(inLink, Path.Combine(tree.Project, "sub", "notes.txt"));
            Directory.CreateSymbolicLink(dirLink, tree.Root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; // Windows without the symlink privilege; the junction case is BrowseAdversarialTests'
        }

        Assert.Equal(MirrorProtocol.ErrForbiddenPath, CodeOf(await harness.Client.FetchFileAsync("job-hunter", outLink, null)));
        Assert.Equal(MirrorProtocol.ErrForbiddenPath,
            CodeOf(await harness.Client.FetchFileAsync("job-hunter", Path.Combine(dirLink, "outside", "secret.txt"), null)));
        Assert.Equal("notes"u8.ToArray(), BytesOf(await harness.Client.FetchFileAsync("job-hunter", inLink, null)).Data);
    }

    // Outside every root is forbidden_path whether or not the file exists, so
    // a refusal cannot be used to map the disk. Inside, missing and a folder
    // are both not_found.
    [Fact]
    public async Task OutsideIsForbiddenWhetherOrNotItExistsAndInsideMissingOrAFolderIsNotFound()
    {
        var (harness, tree) = await FileHarnessAsync();
        using var _ = tree;
        async Task<string> Code(string p) => CodeOf(await harness.Client.FetchFileAsync("job-hunter", p, null));

        Assert.Equal(MirrorProtocol.ErrForbiddenPath, await Code(Path.Combine(tree.Outside, "secret.txt")));
        Assert.Equal(MirrorProtocol.ErrForbiddenPath, await Code(Path.Combine(tree.Outside, "no-such.txt")));
        Assert.Equal(MirrorProtocol.ErrForbiddenPath, await Code("relative/notes.txt"));
        Assert.Equal(MirrorProtocol.ErrNotFound, await Code(Path.Combine(tree.Project, "no-such.txt")));
        Assert.Equal(MirrorProtocol.ErrNotFound, await Code(Path.Combine(tree.Project, "sub")));
        Assert.Equal("notes"u8.ToArray(),
            BytesOf(await harness.Client.FetchFileAsync("job-hunter", Path.Combine(tree.Project, "sub", "notes.txt"), null)).Data);
    }

    // A denied tree inside a root is refused like anything outside.
    [Fact]
    public async Task AFileInASystemFolderInsideARootIsForbidden()
    {
        var (harness, tree) = await FileHarnessAsync();
        using var _ = tree;
        Assert.Equal(MirrorProtocol.ErrForbiddenPath,
            CodeOf(await harness.Client.FetchFileAsync("job-hunter", Path.Combine(tree.Denied, "x.txt"), null)));
        Assert.Equal("notes"u8.ToArray(),
            BytesOf(await harness.Client.FetchFileAsync("job-hunter", Path.Combine(tree.Project, "sub", "notes.txt"), null)).Data);
    }

    // Too large is answered from the size alone, before a byte is read: the
    // file here is sparse, so reading it would be slow and is not what happens.
    [Fact]
    public async Task AFileOverTheCapIsTooLargeAndOneAtTheCapIsNotRefusedForSize()
    {
        var (harness, tree) = await FileHarnessAsync();
        using var _ = tree;
        var big = Path.Combine(tree.Project, "big.bin");
        using (var f = File.Create(big)) f.SetLength(MirrorProtocol.MaxFileBytes + 1);

        Assert.Equal(MirrorProtocol.ErrTooLarge, CodeOf(await harness.Client.FetchFileAsync("job-hunter", big, null)));

        var stat = PeerFileSource.Instance.Stat(big);
        using (var f = File.OpenWrite(big)) f.SetLength(MirrorProtocol.MaxFileBytes);
        var decision = PeerFileRules.Decide(big, null, tree.Project, tree.Env());
        Assert.Null(decision.Err);
        Assert.Equal(MirrorProtocol.MaxFileBytes + 1, stat.Size);
    }

    // The configured roots count, the session's folder counts, and a session
    // started in the home folder does not make the home folder readable —
    // only the home switch does.
    [Fact]
    public async Task ASessionInTheHomeFolderDoesNotOpenTheHomeFolderButTheHomeSwitchDoes()
    {
        var tree = new FileTree();
        using var _ = tree;
        var (harness, _) = await FileHarnessAsync(tree, sessionInProject: false);
        var secret = Path.Combine(tree.Home, "secret.txt");

        Assert.Equal(MirrorProtocol.ErrForbiddenPath, CodeOf(await harness.Client.FetchFileAsync("job-hunter", secret, null)));
        Assert.Equal(MirrorProtocol.ErrForbiddenPath,
            CodeOf(await harness.Client.FetchFileAsync("job-hunter", Path.Combine(tree.Project, "sub", "notes.txt"), null)));
        Assert.Equal("in a configured root"u8.ToArray(),
            BytesOf(await harness.Client.FetchFileAsync("job-hunter", Path.Combine(tree.Other, "a.txt"), null)).Data);

        tree.Settings = tree.Settings with { Home = true };
        Assert.Equal("home, toggle off"u8.ToArray(), BytesOf(await harness.Client.FetchFileAsync("job-hunter", secret, null)).Data);
    }

    // A FIFO reads as an ordinary empty file to .NET, and opening one blocks
    // until somebody writes to it. It is not_found, answered promptly.
    [FilesUnixFact]
    public async Task AFifoInsideARootIsNotFoundRatherThanAHungRead()
    {
        var (harness, tree) = await FileHarnessAsync();
        using var _ = tree;
        var fifo = Path.Combine(tree.Project, "pipe");
        Assert.Equal(0, mkfifo(fifo, Convert.ToInt32("600", 8)));

        var fetch = harness.Client.FetchFileAsync("job-hunter", fifo, null);
        Assert.Same(fetch, await Task.WhenAny(fetch, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.Equal(MirrorProtocol.ErrNotFound, CodeOf(await fetch));
        Assert.Equal(PeerFileKind.Other, PeerFileSource.Instance.Stat(fifo).Kind);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, int mode);

    // On NTFS a short name is canonicalised to the long one before scope is
    // checked: outside stays outside, inside is the file it names.
    [FilesWindowsFact]
    public async Task AShortNameIsJudgedAsTheLongNameItStandsFor()
    {
        var (harness, tree) = await FileHarnessAsync();
        using var _ = tree;
        var outsideLong = Path.Combine(tree.Outside, "a-long-outside-name.txt");
        var insideLong = Path.Combine(tree.Project, "a-long-inside-name.txt");
        File.WriteAllText(outsideLong, "o");
        File.WriteAllText(insideLong, "i");
        var outsideShort = ShortName(outsideLong);
        var insideShort = ShortName(insideLong);
        Assert.NotNull(outsideShort); // short names disabled on the volume fails loudly rather than passing for the wrong reason
        Assert.NotEqual(outsideLong, outsideShort);

        Assert.Equal(MirrorProtocol.ErrForbiddenPath, CodeOf(await harness.Client.FetchFileAsync("job-hunter", outsideShort!, null)));
        Assert.Equal("i"u8.ToArray(), BytesOf(await harness.Client.FetchFileAsync("job-hunter", insideShort!, null)).Data);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint len);

    private static string? ShortName(string p)
    {
        var sb = new StringBuilder(260);
        return GetShortPathName(p, sb, 260) == 0 ? null : sb.ToString();
    }

    // --- the frame loop -------------------------------------------------------------

    private static MirrorProtocol.MirrorFrame FileFrame(string id, string? path, string? name = "job-hunter",
        IReadOnlyDictionary<string, string>? extra = null)
    {
        var fields = new Dictionary<string, string>(extra ?? new Dictionary<string, string>());
        if (name is not null) fields["n"] = MirrorProtocol.Encode(name);
        return MirrorProtocol.TryParseFrame(MirrorProtocol.BuildFrame(MirrorProtocol.ReadFile, id, fields,
            path is null ? null : Encoding.UTF8.GetBytes(path)))!;
    }

    private static string? ErrCodeIn(IEnumerable<string> lines, string id) =>
        lines.Select(MirrorProtocol.TryParseFrame)
            .FirstOrDefault(f => f is not null && f.Type == MirrorProtocol.Err && f.Id == id)?.Get("code");

    // The refusals the frame loop answers itself: a path that is not strict
    // UTF-8, empty, has a control or a format character, a missing name, and a
    // payload that failed its hash.
    [Fact]
    public async Task AMalformedFileFrameIsBadFrame()
    {
        var (harness, tree) = await FileHarnessAsync();
        using var _ = tree;
        var good = Path.Combine(tree.Project, "sub", "notes.txt");

        await harness.Server.HandleAsync(Harness.NearRelay, FileFrame("e1", ""));
        await harness.Server.HandleAsync(Harness.NearRelay, FileFrame("e2", good + "‮"));
        await harness.Server.HandleAsync(Harness.NearRelay, FileFrame("e3", good + "\n"));
        await harness.Server.HandleAsync(Harness.NearRelay, FileFrame("e4", good, name: null));
        await harness.Server.HandleAsync(Harness.NearRelay, FileFrame("e5", null));
        var bad = MirrorProtocol.TryParseFrame(MirrorProtocol.BuildFrame(MirrorProtocol.ReadFile, "e6",
            new Dictionary<string, string> { ["n"] = MirrorProtocol.Encode("job-hunter") }, [0xff, 0xfe]))!;
        await harness.Server.HandleAsync(Harness.NearRelay, bad);
        var unhashed = MirrorProtocol.TryParseFrame(MirrorProtocol.BuildFrame(MirrorProtocol.ReadFile, "e7",
            new Dictionary<string, string> { ["n"] = MirrorProtocol.Encode("job-hunter") }, Encoding.UTF8.GetBytes(good))
            .Replace(";h=", ";h=00"))!;
        await harness.Server.HandleAsync(Harness.NearRelay, unhashed);

        foreach (var id in new[] { "e1", "e2", "e3", "e4", "e5", "e6", "e7" })
            Assert.Equal(MirrorProtocol.ErrBadFrame, ErrCodeIn(harness.ToClient, id));

        Assert.Equal("notes"u8.ToArray(), BytesOf(await harness.Client.FetchFileAsync("job-hunter", good, null)).Data);
    }

    // One file in flight per peer; the second is busy until the first is
    // done, and then is served.
    [Fact]
    public async Task ASecondFileWhileOneIsInFlightIsBusyAndServedAfterwards()
    {
        var tree = new FileTree();
        using var _ = tree;
        var gate = new BlockingSource(PeerFileSource.Instance);
        tree.Source = gate;
        var (harness, _) = await FileHarnessAsync(tree);
        var path = Path.Combine(tree.Project, "sub", "notes.txt");

        await harness.Server.HandleAsync(Harness.NearRelay, FileFrame("b1", path));
        Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(10)));
        await harness.Server.HandleAsync(Harness.NearRelay, FileFrame("b2", path));
        Assert.Equal(MirrorProtocol.ErrBusy, ErrCodeIn(harness.ToClient, "b2"));

        gate.Release.Set();
        await harness.Server.FileInFlight;
        Assert.Null(ErrCodeIn(harness.ToClient, "b1"));
        gate.Release.Set();
        Assert.Equal("notes"u8.ToArray(), BytesOf(await harness.Client.FetchFileAsync("job-hunter", path, null)).Data);
    }

    private sealed class BlockingSource(IPeerFileSource inner) : IPeerFileSource
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();

        public PeerFileStat Stat(string canonical)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
            return inner.Stat(canonical);
        }

        public Stream? Open(string canonical) => inner.Open(canonical);
    }

    // The bucket is per peer and is the folder picker's: a burst of eight,
    // then rate_limited with the clock standing still.
    [Fact]
    public async Task ABurstPastTheBucketIsRateLimited()
    {
        var (harness, tree) = await FileHarnessAsync();
        using var _ = tree;
        for (var i = 0; i < BrowseLimits.RateBurst + 1; i++)
        {
            await harness.Server.HandleAsync(Harness.NearRelay, FileFrame("r" + i, ""));
            await harness.Server.FileInFlight;
        }

        Assert.Equal(MirrorProtocol.ErrBadFrame, ErrCodeIn(harness.ToClient, "r" + (BrowseLimits.RateBurst - 1)));
        Assert.Equal(MirrorProtocol.ErrRateLimited, ErrCodeIn(harness.ToClient, "r" + BrowseLimits.RateBurst));

        harness.Server.Now = () => LiveAt.AddSeconds(5);
        Assert.Equal("notes"u8.ToArray(),
            BytesOf(await harness.Client.FetchFileAsync("job-hunter", Path.Combine(tree.Project, "sub", "notes.txt"), null)).Data);
    }

    // A file's pieces are not kept for RESEND, so eight large files cannot
    // pin memory; a RESEND for one is answered bad-hash.
    [Fact]
    public async Task AFileTransferIsNotRetainedForResend()
    {
        var (harness, tree) = await FileHarnessAsync();
        using var _ = tree;
        var path = Path.Combine(tree.Project, "sub", "notes.txt");
        await harness.Server.HandleAsync(Harness.NearRelay, FileFrame("k1", path));
        await harness.Server.FileInFlight;
        Assert.Null(ErrCodeIn(harness.ToClient, "k1"));

        await harness.Server.HandleAsync(Harness.NearRelay, MirrorProtocol.TryParseFrame(MirrorProtocol.BuildFrame(
            MirrorProtocol.Resend, "k1", new Dictionary<string, string> { ["seq"] = "0" }))!);
        Assert.Equal(MirrorProtocol.ErrBadHash, ErrCodeIn(harness.ToClient, "k1"));
    }

    // The log says which request and how it went, never which file.
    [Fact]
    public async Task TheFileLogNamesTheRequestAndTheOutcomeButNeverThePath()
    {
        var (harness, tree) = await FileHarnessAsync();
        using var _ = tree;
        var lines = new List<string>();
        harness.Server.FileLog = (what, detail) => { lock (lines) lines.Add(what + " " + detail); };
        var served = Path.Combine(tree.Project, "sub", "notes.txt");
        var refused = Path.Combine(tree.Outside, "secret.txt");

        var ok = BytesOf(await harness.Client.FetchFileAsync("job-hunter", served, null));
        await harness.Client.FetchFileAsync("job-hunter", refused, null);
        await harness.Client.FetchFileAsync("job-hunter", served, MirrorProtocol.FileStamp(ok.MtimeMs, ok.Size));

        Assert.Equal(3, lines.Count);
        Assert.Contains(lines, l => l.Contains("-> ok bytes=5"));
        Assert.Contains(lines, l => l.Contains("-> err code=forbidden_path"));
        Assert.Contains(lines, l => l.EndsWith("-> same"));
        Assert.All(lines, l => Assert.DoesNotContain("notes", l));
        Assert.All(lines, l => Assert.DoesNotContain("secret", l));
        Assert.All(lines, l => Assert.DoesNotContain(tree.Root, l));
    }

    // A failure nobody planned for is unreadable, and the peer is free again.
    [Fact]
    public async Task AServeThatThrowsIsUnreadableAndFreesThePeer()
    {
        var tree = new FileTree();
        using var _ = tree;
        var throwing = true;
        var harness = new Harness(_dir, files: () => throwing ? throw new InvalidOperationException("settings") : tree.Env());
        harness.AddSession("job-hunter", WriteTranscript("files.jsonl", Conversation(2)), s => s.Cwd = tree.Project);
        Assert.True(await harness.HandshakeAsync("job-hunter"));
        var path = Path.Combine(tree.Project, "sub", "notes.txt");

        Assert.Equal(MirrorProtocol.ErrUnreadable, CodeOf(await harness.Client.FetchFileAsync("job-hunter", path, null)));
        throwing = false;
        Assert.Equal("notes"u8.ToArray(), BytesOf(await harness.Client.FetchFileAsync("job-hunter", path, null)).Data);
    }

    // A far side that does not serve files never says rf=1, and refuses FILE
    // as unsupported rather than leaving it to time out.
    [Fact]
    public async Task AFarSideWithoutFilesSaysNothingAndRefusesUnsupported()
    {
        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", WriteTranscript("files.jsonl", Conversation(2)));
        Assert.True(await harness.HandshakeAsync("job-hunter"));

        Assert.False(harness.Client.SpeaksFiles("job-hunter"));
        Assert.Equal(MirrorProtocol.ErrUnsupported,
            CodeOf(await harness.Client.FetchFileAsync("job-hunter", "/any/thing.pdf", null)));
    }

    // A session nobody offered has no machine to ask, and the far side's
    // name for an unknown session adds no root.
    [Fact]
    public async Task AnUnknownSessionIsNoSessionHereAndAddsNoRootThere()
    {
        var (harness, tree) = await FileHarnessAsync();
        using var _ = tree;
        var path = Path.Combine(tree.Project, "sub", "notes.txt");

        Assert.Equal(MirrorProtocol.ErrNoSession, CodeOf(await harness.Client.FetchFileAsync("nobody", path, null)));
        Assert.Null(harness.Client.MachineFor("nobody"));
        Assert.Equal(Harness.FarRelay, harness.Client.MachineFor("job-hunter"));

        await harness.Server.HandleAsync(Harness.NearRelay, FileFrame("u1", path, name: "nobody"));
        await harness.Server.FileInFlight;
        Assert.Equal(MirrorProtocol.ErrForbiddenPath, ErrCodeIn(harness.ToClient, "u1"));
    }

    // Cancelled before asking, or while waiting, throws rather than
    // returning a result a panel could act on.
    [Fact]
    public async Task CancellingAFetchThrows()
    {
        var tree = new FileTree();
        using var _ = tree;
        var gate = new BlockingSource(PeerFileSource.Instance);
        tree.Source = gate;
        var (harness, _) = await FileHarnessAsync(tree);
        var path = Path.Combine(tree.Project, "sub", "notes.txt");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Client.FetchFileAsync("job-hunter", path, null, ct: new CancellationToken(true)));

        using var cts = new CancellationTokenSource();
        var fetch = harness.Client.FetchFileAsync("job-hunter", path, null, ct: cts.Token);
        Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(10)));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        gate.Release.Set();
        await harness.Server.FileInFlight;
    }

    // A far side gone quiet is unreachable, not a refusal.
    [Fact]
    public async Task ARequestNobodyAnswersIsUnreachable()
    {
        var tree = new FileTree();
        using var _ = tree;
        var gate = new BlockingSource(PeerFileSource.Instance);
        tree.Source = gate;
        var (harness, _) = await FileHarnessAsync(tree);
        harness.Client.TimeoutOverrideForTests = TimeSpan.FromMilliseconds(200);

        var result = await harness.Client.FetchFileAsync("job-hunter", Path.Combine(tree.Project, "sub", "notes.txt"), null);
        gate.Release.Set();
        await harness.Server.FileInFlight;

        Assert.Equal(MirrorProtocol.ErrUnreachable, CodeOf(result));
    }

    // --- the cache, end to end -------------------------------------------------------

    // What the panel will do: store what came, offer its stamp next time, and
    // reuse the cached copy on `same`.
    [Fact]
    public async Task AFetchedFileIsCachedAndTheNextFetchReusesIt()
    {
        var (harness, tree) = await FileHarnessAsync();
        using var _ = tree;
        var cache = new PeerFileCache(Path.Combine(tree.Root, "cache"), marker: new NoMark());
        var path = tree.Write("report.pdf", Random(1000));
        var machine = harness.Client.MachineFor("job-hunter")!;

        Assert.Null(cache.Newest(machine, path));
        var got = BytesOf(await harness.Client.FetchFileAsync("job-hunter", path, null));
        var stored = cache.Store(machine, path, got.MtimeMs, got.Data, machine)!;
        Assert.Equal(got.Data, File.ReadAllBytes(stored.LocalPath));
        Assert.Equal("report.pdf", Path.GetFileName(stored.LocalPath));

        var cached = cache.Newest(machine, path)!;
        Assert.Equal(stored, cached);
        Assert.IsType<PeerFileResult.Same>(await harness.Client.FetchFileAsync("job-hunter", path, cached.Have));
    }

    private sealed class NoMark : IDownloadMarker
    {
        public void Mark(string path, string origin) { }
    }
}
