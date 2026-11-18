using System.Text;
using Xunit;

namespace Orbweaver.Tests;

// CB-363: the FILE frame's decisions with no disk behind them — the
// folder-picker's model filesystem and a reader that answers what each test
// needs. The real disk, links and all, is MirrorRoundTripTests.Files.cs's.
public class PeerFileRulesTests
{
    private const string Home = "/Users/dev";

    private static FakeBrowseFileSystem Fs() =>
        new FakeBrowseFileSystem(Home, false, ["/", "/Users"], ["/System", "/Users/dev/Library"])
            .Add("/").Add("/Users").Add(Home).Add("/Users/dev/Source").Add("/Users/dev/Source/app")
            .Add("/Users/dev/Source/app/report.pdf", BrowseKind.File)
            .Add("/Users/dev/Documents").Add("/Users/dev/Documents/tax.pdf", BrowseKind.File)
            .Add("/Users/dev/Library").Add("/Users/dev/Library/x.pdf", BrowseKind.File)
            .Add("/System");

    private sealed class Reader(Dictionary<string, byte[]> files) : IPeerFileSource
    {
        public Func<string, Stream?>? OpenOverride;
        public Action? OnOpen;
        public long? StatSize;
        public bool Exec;

        public PeerFileStat Stat(string canonical) =>
            files.TryGetValue(canonical, out var b)
                ? new PeerFileStat(PeerFileKind.Regular, StatSize ?? b.Length, 1700000000000, Exec)
                : canonical.EndsWith("/app") ? new PeerFileStat(PeerFileKind.Directory, 0, 0, false) : default;

        public Stream? Open(string canonical)
        {
            OnOpen?.Invoke();
            if (OpenOverride is { } o) return o(canonical);
            return files.TryGetValue(canonical, out var b) ? new MemoryStream(b) : null;
        }
    }

    private static readonly byte[] Pdf = "%PDF-1.7 hello"u8.ToArray();

    private static (PeerFileEnv Env, Reader Reader) Env(BrowseSettings? settings = null, IBrowseFileSystem? fs = null)
    {
        var reader = new Reader(new() { ["/Users/dev/Source/app/report.pdf"] = Pdf, ["/Users/dev/Documents/tax.pdf"] = Pdf });
        return (new PeerFileEnv(settings ?? new BrowseSettings([], false), fs ?? Fs(), reader), reader);
    }

    // --- the frame's shape -----------------------------------------------------

    [Fact]
    public void ThePathIsStrictUtf8OrNothing()
    {
        Assert.Equal("/a/ü.pdf", PeerFileRules.PathOf(Encoding.UTF8.GetBytes("/a/ü.pdf")));
        Assert.Null(PeerFileRules.PathOf([0xc3, 0x28]));
        Assert.Null(PeerFileRules.PathOf(null));
    }

    [Theory]
    [InlineData("/a/report.pdf", true)]
    [InlineData(@"C:\a\report.pdf", true)]
    [InlineData("", false)]
    [InlineData("/a/\u202ereport.pdf", false)]   // right-to-left override
    [InlineData("/a/re\u200dport.pdf", false)]   // zero-width joiner
    [InlineData("/a/\u0007.pdf", false)]
    [InlineData("/a/\u007f.pdf", false)]
    public void ShapeRefusesEmptyControlAndFormatCharacters(string path, bool ok) =>
        Assert.Equal(ok, PeerFileRules.ShapeOk(path));

    [Fact]
    public void ShapeRefusesAPathOverTheListDirLimit() =>
        Assert.False(PeerFileRules.ShapeOk("/" + new string('a', BrowseLimits.MaxPathBytes)));

    // --- the session's folder as a root ---------------------------------------------

    [Fact]
    public void TheSessionsFolderIsARootUnlessItIsHomeOrAboveOrASystemFolderOrNotAPath()
    {
        var fs = Fs();
        var none = new BrowseSettings(["/Users/dev/Documents"], false);

        Assert.Equal(["/Users/dev/Source/app", "/Users/dev/Documents"],
            PeerFileRules.WithSessionRoot(none, "/Users/dev/Source/app", fs).Roots);

        foreach (var cwd in new string?[] { null, " ", "relative/app", "~/Source", Home, "/Users", "/", "/System", "/Users/dev/Library" })
            Assert.Same(none, PeerFileRules.WithSessionRoot(none, cwd, fs));
    }

    [Fact]
    public void ASessionFolderThatCannotBeCanonicalisedAddsNoRoot()
    {
        var fs = new FakeBrowseFileSystem(@"C:\Users\dev", true, [], []).Add(@"C:\").Add(@"C:\Users").Add(@"C:\Users\dev");
        var none = new BrowseSettings([], false);
        Assert.Same(none, PeerFileRules.WithSessionRoot(none, @"C:\Users\dev\proj. ", fs));
    }

    // --- the decision, in order -------------------------------------------------------

    [Fact]
    public void AFileUnderTheSessionsFolderIsServedWithItsStat()
    {
        var (env, _) = Env();
        var d = PeerFileRules.Decide("/Users/dev/Source/app/report.pdf", null, "/Users/dev/Source/app", env);
        Assert.Null(d.Err);
        Assert.False(d.Same);
        Assert.Equal("/Users/dev/Source/app/report.pdf", d.Canonical);
        Assert.Equal(Pdf.Length, d.Stat.Size);
    }

    // The canonical spelling is what is judged and what is read: another
    // capitalisation of an in-scope file is that file.
    [Fact]
    public void AnotherCapitalisationIsCanonicalisedToTheStoredSpelling()
    {
        var (env, _) = Env();
        var d = PeerFileRules.Decide("/users/DEV/source/app/REPORT.pdf", null, "/Users/dev/Source/app", env);
        Assert.Equal("/Users/dev/Source/app/report.pdf", d.Canonical);
    }

    [Theory]
    [InlineData("/Users/dev/Documents/tax.pdf")]               // exists, out of scope
    [InlineData("/Users/dev/Documents/nothing.pdf")]           // missing, out of scope: the same answer
    [InlineData("/Users/dev/Source/app/../../Documents/tax.pdf")]
    [InlineData("/Users/dev/Library/x.pdf")]
    [InlineData("Source/app/report.pdf")]
    [InlineData("~/Source/app/report.pdf")]
    public void OutOfScopeIsForbiddenWhetherOrNotItExists(string path)
    {
        var (env, _) = Env();
        Assert.Equal(MirrorProtocol.ErrForbiddenPath, PeerFileRules.Decide(path, null, "/Users/dev/Source/app", env).Err);
        Assert.Null(PeerFileRules.Decide("/Users/dev/Source/app/report.pdf", null, "/Users/dev/Source/app", env).Err);
    }

    [Fact]
    public void AnUncanonicalisablePathIsForbidden()
    {
        var fs = new FakeBrowseFileSystem(@"C:\Users\dev", true, [], []).Add(@"C:\").Add(@"C:\Users").Add(@"C:\Users\dev");
        var (env, _) = Env(fs: fs);
        Assert.Equal(MirrorProtocol.ErrForbiddenPath, PeerFileRules.Decide(@"C:\Users\dev\a. \x.pdf", null, null, env).Err);
    }

    [Fact]
    public void TheHomeSwitchAndTheConfiguredRootsAreRootsToo()
    {
        var (env, _) = Env(new BrowseSettings(["/Users/dev/Documents"], false));
        Assert.Null(PeerFileRules.Decide("/Users/dev/Documents/tax.pdf", null, null, env).Err);

        var (homeOn, _) = Env(new BrowseSettings([], true));
        Assert.Null(PeerFileRules.Decide("/Users/dev/Documents/tax.pdf", null, null, homeOn).Err);
        Assert.Equal(MirrorProtocol.ErrForbiddenPath, PeerFileRules.Decide("/Users/dev/Library/x.pdf", null, null, homeOn).Err);
    }

    [Fact]
    public void MissingAndAFolderAreNotFoundInsideScope()
    {
        var (env, _) = Env();
        Assert.Equal(MirrorProtocol.ErrNotFound, PeerFileRules.Decide("/Users/dev/Source/app/gone.pdf", null, "/Users/dev/Source/app", env).Err);
        Assert.Equal(MirrorProtocol.ErrNotFound, PeerFileRules.Decide("/Users/dev/Source/app", null, "/Users/dev/Source/app", env).Err);
    }

    [Fact]
    public void OverTheCapIsTooLargeAndAtTheCapIsNot()
    {
        var (env, reader) = Env();
        reader.StatSize = MirrorProtocol.MaxFileBytes + 1;
        Assert.Equal(MirrorProtocol.ErrTooLarge, PeerFileRules.Decide("/Users/dev/Source/app/report.pdf", null, "/Users/dev/Source/app", env).Err);
        reader.StatSize = MirrorProtocol.MaxFileBytes;
        Assert.Null(PeerFileRules.Decide("/Users/dev/Source/app/report.pdf", null, "/Users/dev/Source/app", env).Err);
    }

    [Theory]
    [InlineData("1700000000000-14", true)]
    [InlineData("1700000000000-15", false)]
    [InlineData("1700000000001-14", false)]
    [InlineData("garbage", false)]
    [InlineData(null, false)]
    public void SameOnlyWhenBothMtimeAndSizeMatch(string? have, bool same)
    {
        var (env, _) = Env();
        var d = PeerFileRules.Decide("/Users/dev/Source/app/report.pdf", have, "/Users/dev/Source/app", env);
        Assert.Null(d.Err);
        Assert.Equal(same, d.Same);
    }

    // --- reading -----------------------------------------------------------------------

    private static PeerFileDecision Served(PeerFileEnv env) =>
        PeerFileRules.Decide("/Users/dev/Source/app/report.pdf", null, "/Users/dev/Source/app", env);

    [Fact]
    public void ReadReturnsExactlyTheBytes()
    {
        var (env, _) = Env();
        var (err, bytes) = PeerFileRules.Read(Served(env), env);
        Assert.Null(err);
        Assert.Equal(Pdf, bytes);
    }

    [Fact]
    public void AnEmptyFileIsServedAsNoBytes()
    {
        var (env, reader) = Env();
        var d = Served(env);
        reader.OpenOverride = _ => new MemoryStream();
        var (err, bytes) = PeerFileRules.Read(d with { Stat = d.Stat with { Size = 0 } }, env);
        Assert.Null(err);
        Assert.Empty(bytes!);
    }

    [Fact]
    public void AFileThatCannotBeOpenedIsUnreadable()
    {
        var (env, reader) = Env();
        var d = Served(env);
        reader.OpenOverride = _ => null;
        Assert.Equal(MirrorProtocol.ErrUnreadable, PeerFileRules.Read(d, env).Err);
    }

    // Shrunk or grown between the stat and the read: neither is sent.
    [Fact]
    public void AFileThatChangedSizeWhileBeingReadIsUnreadable()
    {
        var (env, reader) = Env();
        var d = Served(env);
        reader.OpenOverride = _ => new MemoryStream(Pdf[..5]);
        Assert.Equal(MirrorProtocol.ErrUnreadable, PeerFileRules.Read(d, env).Err);
        reader.OpenOverride = _ => new MemoryStream([.. Pdf, 0x20]);
        Assert.Equal(MirrorProtocol.ErrUnreadable, PeerFileRules.Read(d, env).Err);
    }

    [Fact]
    public void AReadThatFailsIsUnreadable()
    {
        var (env, reader) = Env();
        var d = Served(env);
        reader.OpenOverride = _ => new ThrowingStream();
        Assert.Equal(MirrorProtocol.ErrUnreadable, PeerFileRules.Read(d, env).Err);
    }

    private sealed class ThrowingStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("disk went away");
    }

    // A component swapped for a link between the decision and the open: the
    // canonical path moved, and nothing is sent. The control is the same
    // decision with nothing swapped.
    [Fact]
    public void ACanonicalPathThatMovedBetweenTheCheckAndTheOpenIsUnreadable()
    {
        var moving = new MovingFs(Fs());
        var (env, reader) = Env(fs: moving);
        var d = Served(env);
        Assert.Null(PeerFileRules.Read(d, env).Err);

        reader.OnOpen = () => moving.SwapAppForLink = true;
        Assert.Equal(MirrorProtocol.ErrUnreadable, PeerFileRules.Read(d, env).Err);
    }

    private sealed class MovingFs(FakeBrowseFileSystem inner) : IBrowseFileSystem
    {
        public bool SwapAppForLink;
        public bool RingApp;
        public string Home => inner.Home;
        public bool IsWindows => inner.IsWindows;
        public bool CaseInsensitive => inner.CaseInsensitive;
        public IReadOnlyList<string> DeniedExact => inner.DeniedExact;
        public IReadOnlyList<string> DeniedTrees => inner.DeniedTrees;

        public BrowseEntry? Lookup(string parent, string name) =>
            name != "app" ? inner.Lookup(parent, name)
            : RingApp ? new BrowseEntry("app", BrowseKind.Link, "/Users/dev/Source/app")
            : SwapAppForLink ? new BrowseEntry("app", BrowseKind.Link, "/Users/dev/Documents")
            : inner.Lookup(parent, name);

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public (IReadOnlyList<BrowseEntry> Entries, bool More)? Enumerate(string folder, int cap) => inner.Enumerate(folder, cap);
        public bool HiddenAttribute(string folder, string name) => inner.HiddenAttribute(folder, name);
        public bool PersonaAt(string folder) => inner.PersonaAt(folder);
    }

    // A path that no longer canonicalises at all after the open: the folder
    // became a link to itself, a ring past BrowseLimits.MaxLinkReplacements,
    // which Canonical answers null.
    [Fact]
    public void ACanonicalPathThatStopsResolvingAfterTheOpenIsUnreadable()
    {
        var moving = new MovingFs(Fs());
        var (env, reader) = Env(fs: moving);
        var d = Served(env);
        reader.OnOpen = () => moving.RingApp = true;
        Assert.Equal(MirrorProtocol.ErrUnreadable, PeerFileRules.Read(d, env).Err);
    }

    // --- the real stat's pure parts -------------------------------------------------------

    [Theory]
    [InlineData(0x81A4, "Regular")]
    [InlineData(0x41ED, "Directory")]
    [InlineData(0x11A4, "Other")]   // FIFO, as measured on macOS
    [InlineData(0xC1ED, "Other")]   // socket
    [InlineData(0x21B6, "Other")]   // character device
    public void TheModeSaysWhatKindOfThingItIs(int mode, string kind) =>
        Assert.Equal(kind, PeerFileSource.KindOfMode(mode).ToString());

    [Fact]
    public void AWindowsDeviceIsNotAFile()
    {
        Assert.Equal(PeerFileKind.Other, PeerFileSource.WindowsKind(FileAttributes.Device));
        Assert.Equal(PeerFileKind.Regular, PeerFileSource.WindowsKind(FileAttributes.Archive | FileAttributes.ReadOnly));
    }

    // --- the gate ---------------------------------------------------------------------

    [Fact]
    public void OneInFlightPerPeerAndPeersAreIndependent()
    {
        var gate = new PeerFileGate();
        Assert.True(gate.TryBegin("mini"));
        Assert.False(gate.TryBegin("mini"));
        Assert.True(gate.TryBegin("windows"));
        gate.End("mini");
        Assert.True(gate.TryBegin("mini"));
    }

    [Fact]
    public void TheRateBucketIsPerPeer()
    {
        var gate = new PeerFileGate();
        for (var i = 0; i < BrowseLimits.RateBurst; i++) Assert.True(gate.TryRate("mini", 0));
        Assert.False(gate.TryRate("mini", 0));
        Assert.True(gate.TryRate("windows", 0));
        Assert.True(gate.TryRate("mini", BrowseLimits.RateIntervalMs));
    }
}
