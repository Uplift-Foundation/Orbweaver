using Xunit;

namespace Orbweaver.Tests;

// CB-281: the upward .git walk over a fake tree. A real disk is the seam's other side;
// RepoRoots.Cached is the only part that touches it and is tested with an injected Fs.
[Collection("Settings")]
public class RepoRootsTests
{
    private static RepoFs Tree(string[] dirs, Dictionary<string, string>? files = null)
    {
        files ??= new();
        var set = new HashSet<string>(dirs, StringComparer.Ordinal);
        return new RepoFs(set.Contains, files.ContainsKey, p => files.TryGetValue(p, out var t) ? t : null);
    }

    [Fact]
    public void ADirectoryHoldingDotGitIsItsOwnRoot() =>
        Assert.Equal("/r", RepoRoots.Find("/r", Tree(new[] { "/r/.git" })));

    [Fact]
    public void ASubfolderWalksUpToTheRepository() =>
        Assert.Equal("/r", RepoRoots.Find("/r/src/deep/", Tree(new[] { "/r/.git" })));

    [Fact]
    public void NoRepositoryAnywhereIsNull() =>
        Assert.Null(RepoRoots.Find("/a/b", Tree(Array.Empty<string>())));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AnEmptyCwdIsNull(string? cwd) => Assert.Null(RepoRoots.Find(cwd, Tree(Array.Empty<string>())));

    [Fact]
    public void ALinkedWorktreeFoldsIntoTheMainRepository()
    {
        var fs = Tree(new[] { "/main/.git" },
            new() { ["/wt/feature/.git"] = "gitdir: /main/.git/worktrees/feature\n" });
        Assert.Equal("/main", RepoRoots.Find("/wt/feature/sub", fs));
    }

    [Fact]
    public void ARelativeWorktreePointerIsTakenAgainstTheFolderHoldingIt()
    {
        var fs = Tree(Array.Empty<string>(),
            new() { ["/main/.claude/worktrees/x/.git"] = "gitdir: ../../../.git/worktrees/x" });
        Assert.Equal("/main", RepoRoots.Find("/main/.claude/worktrees/x", fs));
    }

    [Fact]
    public void ADotSegmentInAPointerIsIgnored()
    {
        var fs = Tree(Array.Empty<string>(), new() { ["/w/.git"] = "gitdir: /m/./.git/worktrees/w" });
        Assert.Equal("/m", RepoRoots.Find("/w", fs));
    }

    [Fact]
    public void ASubmodulePointerLeavesTheHoldingFolderAsItsOwnRoot()
    {
        var fs = Tree(new[] { "/main/.git" }, new() { ["/main/vendor/lib/.git"] = "gitdir: /main/.git/modules/lib" });
        Assert.Equal("/main/vendor/lib", RepoRoots.Find("/main/vendor/lib/src", fs));
    }

    [Theory]
    [InlineData("not a pointer")]
    [InlineData("gitdir:")]
    [InlineData("")]
    public void AGarbledPointerIsTheHoldingFolder(string text) =>
        Assert.Equal("/g", RepoRoots.Find("/g", Tree(Array.Empty<string>(), new() { ["/g/.git"] = text })));

    [Fact]
    public void AnUnreadablePointerIsTheHoldingFolder()
    {
        var fs = new RepoFs(_ => false, p => p == "/u/.git", _ => null);
        Assert.Equal("/u", RepoRoots.Find("/u", fs));
    }

    [Fact]
    public void ADeletedWorktreeFoldsIntoTheRepositoryItSatInside() =>
        Assert.Equal("/main", RepoRoots.Find("/main/.claude/worktrees/gone", Tree(new[] { "/main/.git" })));

    [Fact]
    public void AWindowsPathWalksUpByBackslash()
    {
        var fs = Tree(new[] { @"C:\src\repo\.git" });
        Assert.Equal(@"C:\src\repo", RepoRoots.Find(@"C:\src\repo\a\b\", fs));
    }

    [Fact]
    public void AWindowsDriveRootCanHoldTheRepositoryAndOtherwiseEndsTheWalk()
    {
        Assert.Equal(@"C:\", RepoRoots.Find(@"C:\a", Tree(new[] { @"C:\.git" })));
        Assert.Null(RepoRoots.Find(@"C:\a", Tree(Array.Empty<string>())));
        Assert.Null(RepoRoots.Find(@"C:\", Tree(Array.Empty<string>())));
    }

    [Fact]
    public void AWindowsWorktreePointerResolvesToTheMainRepository()
    {
        var fs = Tree(Array.Empty<string>(), new() { [@"D:\wt\.git"] = @"gitdir: C:\main\.git\worktrees\wt" });
        Assert.Equal("C:/main", RepoRoots.Find(@"D:\wt", fs));
    }

    [Fact]
    public void TheFilesystemRootCanHoldTheRepositoryAndOtherwiseEndsTheWalk()
    {
        Assert.Equal("/", RepoRoots.Find("/a", Tree(new[] { "/.git" })));
        Assert.Null(RepoRoots.Find("/", Tree(Array.Empty<string>())));
    }

    [Fact]
    public void APathMixingBothSeparatorsIsJoinedWithAForwardSlash() =>
        Assert.Equal(@"C:\a/b", RepoRoots.Find(@"C:\a/b/c", Tree(new[] { @"C:\a/b/.git" })));

    [Fact]
    public void ARelativeCwdWithNoSeparatorEndsTheWalk() =>
        Assert.Null(RepoRoots.Find("rel", Tree(Array.Empty<string>())));

    [Fact]
    public void ARealDirectoryTreeIsReadThroughTheRealSeam()
    {
        var root = Path.Combine(Path.GetTempPath(), "cb281-repo-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            var sub = Directory.CreateDirectory(Path.Combine(root, "a", "b")).FullName;
            File.WriteAllText(Path.Combine(root, "a", ".git"), "gitdir: nowhere");

            Assert.Equal(root, RepoRoots.Find(root, RepoFs.Real));
            // a/.git is a file whose pointer is not a worktree one: a is its own root.
            Assert.Equal(Path.Combine(root, "a"), RepoRoots.Find(sub, RepoFs.Real));

            var gone = Path.Combine(root, "no-such");
            Assert.Equal(root, RepoRoots.Find(gone, RepoFs.Real));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AnUnreadableGitFileIsTreatedAsNoText()
    {
        var root = Path.Combine(Path.GetTempPath(), "cb281-repo-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            // A .git *directory* is a directory; make ReadText fail on a path that is a directory.
            Assert.Null(RepoFs.Real.ReadText(root));
            Assert.Null(RepoFs.Real.ReadText(Path.Combine(root, "missing")));
        }
        finally { Directory.Delete(root, true); }
    }

    // ---- cached ---------------------------------------------------------------

    [Fact]
    public void CachedHoldsAnAnswerThenAsksAgainAfterItExpires()
    {
        var calls = 0;
        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldFs = RepoRoots.Fs;
        var oldNow = RepoRoots.Now;
        try
        {
            RepoRoots.Forget();
            RepoRoots.Fs = new RepoFs(p => { calls++; return p == "/c/.git"; }, _ => false, _ => null);
            RepoRoots.Now = () => clock;

            Assert.Equal("/c", RepoRoots.Cached("/c"));
            var first = calls;
            Assert.Equal("/c", RepoRoots.Cached("/C"));        // case-insensitive hit, no new look
            Assert.Equal(first, calls);

            clock += RepoRoots.HeldFor + TimeSpan.FromSeconds(1);
            Assert.Equal("/c", RepoRoots.Cached("/c"));
            Assert.True(calls > first);

            Assert.Null(RepoRoots.Cached(null));
            Assert.Null(RepoRoots.Cached(" "));
        }
        finally { RepoRoots.Fs = oldFs; RepoRoots.Now = oldNow; RepoRoots.Forget(); }
    }

    [Fact]
    public void CachedStaysBoundedByDroppingEverythingPastFiveHundredTwelve()
    {
        var oldFs = RepoRoots.Fs;
        try
        {
            RepoRoots.Forget();
            RepoRoots.Fs = new RepoFs(_ => false, _ => false, _ => null);
            for (var i = 0; i < 520; i++) Assert.Null(RepoRoots.Cached("/n/" + i));
            Assert.Null(RepoRoots.Cached("/n/again"));
        }
        finally { RepoRoots.Fs = oldFs; RepoRoots.Forget(); }
    }
}
