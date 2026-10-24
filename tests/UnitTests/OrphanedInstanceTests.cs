using Orbweaver;
using Xunit;

namespace Orbweaver.UnitTests;

// Claude Desktop's own updater relaunches the bundle it just updated and drops
// both profile selectors, so an instance this app launched onto a profile ends
// up on Default while still running from that profile's coloured clone. CB-7.
//
// The rule is pure on purpose. Getting it wrong in the permissive direction
// reports a correct Default instance as broken; getting it wrong in the other
// leaves the failure invisible, which is where it started. Neither is
// observable without constructing processes, so the decision lives in a
// function that takes the three facts it needs and nothing else.

// Settings, because AnOrphanAppearingChangesTheDigest drives
// ClaudeDesktopManager.Adopt, which publishes the same process-wide snapshot
// ClaudeDesktopManagerTests asserts on. Unserialised, an Adopt landing between
// that class's Publish(pid: 9) and its read of it turned "Expected 9" into
// "Actual 0" (CB-269).
[Xunit.Collection("Settings")]
public class OrphanedInstanceTests
{
    private const string Root = "/Users/x/Library/Application Support/Orbweaver/bundles";
    private const string DefaultFolder = "Claude";

    private static string Clone(string folder) => $"{Root}/{folder}/Claude.app";

    private static ClaudeInstance Instance(int pid, string? userDataDir, string? bundle) =>
        new(pid, userDataDir, bundle);

    // ---- OrphanedCloneFolder --------------------------------------------

    [Fact]
    public void ACloneWithNoSelectorIsOrphanedOntoDefault()
    {
        // The observed case: a process running from Board's clone with 39 files
        // open under Application Support/Claude and none under Claude-Board.
        Assert.Equal(
            "Claude-Board",
            ClaudeDesktopManager.OrphanedCloneFolder(
                Instance(26126, null, Clone("Claude-Board")), Root, DefaultFolder));
    }

    // The exception that makes this a rule rather than the one-liner the ticket
    // proposed. LaunchMac gives Default a tinted clone once a colour is picked
    // for it and launches it with *neither* selector on purpose, so this exact
    // shape is the ordinary, correct Default instance — and it was running on
    // the machine the bug was found on, alongside the real orphan.
    [Fact]
    public void DefaultsOwnCloneWithNoSelectorIsNotOrphaned()
    {
        Assert.Null(ClaudeDesktopManager.OrphanedCloneFolder(
            Instance(451, null, Clone(DefaultFolder)), Root, DefaultFolder));
    }

    [Fact]
    public void ACloneCarryingItsSelectorIsWhereItBelongs()
    {
        Assert.Null(ClaudeDesktopManager.OrphanedCloneFolder(
            Instance(1582, "/Users/x/Library/Application Support/Claude-Board", Clone("Claude-Board")),
            Root,
            DefaultFolder));
    }

    // An empty userData value is not the same as no value: it means a selector
    // was passed and carried nothing, which the launcher now refuses to emit.
    // Either way it is not the absence this rule keys on.
    [Fact]
    public void AnEmptySelectorIsStillASelector()
    {
        Assert.Null(ClaudeDesktopManager.OrphanedCloneFolder(
            Instance(7, "", Clone("Claude-Board")), Root, DefaultFolder));
    }

    [Fact]
    public void TheInstalledBundleWithNoSelectorIsJustDefault()
    {
        // A Dock launch of /Applications/Claude.app. Not under the bundle root,
        // so not a clone, so nothing to report.
        Assert.Null(ClaudeDesktopManager.OrphanedCloneFolder(
            Instance(9, null, "/Applications/Claude.app"), Root, DefaultFolder));
    }

    // Null on Windows, per ClaudeInstance — which is how this feature ends up
    // macOS-only without a platform guard anywhere in the rule.
    [Fact]
    public void NoBundlePathIsNoAnswer()
    {
        Assert.Null(ClaudeDesktopManager.OrphanedCloneFolder(
            Instance(9, null, null), Root, DefaultFolder));
        Assert.Null(ClaudeDesktopManager.OrphanedCloneFolder(
            Instance(9, null, ""), Root, DefaultFolder));
    }

    // A bundle sitting *beside* the root, or nested a level deeper than a clone
    // ever is, must not be read as one. The root is a real directory in the
    // user's Application Support and neighbouring paths share its prefix, so a
    // StartsWith test here would be wrong in a way nothing else would catch.
    [Theory]
    [InlineData("/Users/x/Library/Application Support/Orbweaver/bundles-old/Claude-Board/Claude.app")]
    [InlineData("/Users/x/Library/Application Support/Orbweaver/Claude.app")]
    [InlineData("/Users/x/Library/Application Support/Orbweaver/bundles/Claude-Board/nested/Claude.app")]
    [InlineData("/Applications/Claude.app/Contents/Frameworks/Claude.app")]
    public void OnlyABundleDirectlyUnderTheRootCounts(string bundle)
    {
        Assert.Null(ClaudeDesktopManager.OrphanedCloneFolder(
            Instance(9, null, bundle), Root, DefaultFolder));
    }

    // cp -Rc and the path a process reports can differ by a trailing slash, and
    // the comparison is ordinal.
    [Fact]
    public void ATrailingSlashOnEitherSideDoesNotChangeTheAnswer()
    {
        Assert.Equal(
            "Claude-Board",
            ClaudeDesktopManager.OrphanedCloneFolder(
                Instance(1, null, Clone("Claude-Board") + "/"), Root, DefaultFolder));

        Assert.Equal(
            "Claude-Board",
            ClaudeDesktopManager.OrphanedCloneFolder(
                Instance(1, null, Clone("Claude-Board")), Root + "/", DefaultFolder));
    }

    // Paths with nothing above them. None of these can come off a real clone,
    // but the string arrives from another process via proc_pidpath rather than
    // from the function that built it, so the walk up to the root has to have an
    // answer for each of them rather than throwing inside a menu rebuild.
    [Theory]
    [InlineData("/")]
    [InlineData("/Claude.app")]
    [InlineData("Claude.app")]
    [InlineData("")]
    [InlineData(Root + "//Claude.app")]
    [InlineData(Root + "/./Claude.app")]
    public void APathWithNoRoomAboveItIsNotAClone(string bundle)
    {
        Assert.Null(ClaudeDesktopManager.OrphanedCloneFolder(
            Instance(9, null, bundle), Root, DefaultFolder));
    }

    // ---- Windows-shaped paths ---------------------------------------------

    // On a real Windows machine the rule never fires — BundlePath is null, per
    // ClaudeInstance — but the integration suite feeds it the host-native
    // layout ClaudeDesktopBundles actually writes, so the parse has to hold
    // for a drive-rooted path too. Pinned here as well so each CI leg verifies
    // the other platform's shapes: the rule is a function of nothing but its
    // arguments, and these cases are what keep it that way.
    private const string WinRoot = @"C:\Users\x\AppData\Local\Orbweaver\bundles";

    [Fact]
    public void ADriveRootedCloneParsesTheSameAsAPosixOne()
    {
        Assert.Equal(
            "Claude-Board",
            ClaudeDesktopManager.OrphanedCloneFolder(
                Instance(1, null, WinRoot + @"\Claude-Board\Claude.app"),
                WinRoot, DefaultFolder));
    }

    [Fact]
    public void MixedSeparatorsDoNotChangeTheAnswer()
    {
        // Windows APIs accept either separator and .NET emits whichever the
        // caller concatenated, so a real path can arrive wearing both at once.
        Assert.Equal(
            "Claude-Board",
            ClaudeDesktopManager.OrphanedCloneFolder(
                Instance(1, null, WinRoot + "/Claude-Board/Claude.app"),
                WinRoot, DefaultFolder));
    }

    [Fact]
    public void ADifferentDriveIsADifferentRoot()
    {
        Assert.Null(ClaudeDesktopManager.OrphanedCloneFolder(
            Instance(1, null, @"D:" + WinRoot[2..] + @"\Claude-Board\Claude.app"),
            WinRoot, DefaultFolder));
    }

    // A drive letter names the same volume whatever its case; every other
    // component keeps the ordinal comparison the POSIX cases already pin.
    [Fact]
    public void TheDriveLetterIsTheOnePartComparedWithoutCase()
    {
        Assert.Equal(
            "Claude-Board",
            ClaudeDesktopManager.OrphanedCloneFolder(
                Instance(1, null, "c:" + WinRoot[2..] + @"\Claude-Board\Claude.app"),
                WinRoot, DefaultFolder));
    }

    // The Windows counterparts of "no room above it": a drive-relative path
    // ("C:Claude.app" is relative to C:'s current directory, a thing only cmd
    // remembers), a ".." that climbs above the drive root, and a bare drive.
    [Theory]
    [InlineData(@"C:Claude.app")]
    [InlineData(@"C:\..\Claude.app")]
    [InlineData(@"C:")]
    [InlineData(WinRoot + @"\.\Claude.app")]
    [InlineData(WinRoot + @"\\Claude.app")]
    public void AWindowsPathWithNoRoomAboveItIsNotAClone(string bundle)
    {
        Assert.Null(ClaudeDesktopManager.OrphanedCloneFolder(
            Instance(9, null, bundle), WinRoot, DefaultFolder));
    }

    // ---- MapOrphans ------------------------------------------------------

    [Fact]
    public void MapOrphansKeepsOneProcessPerProfileAndIgnoresTheRest()
    {
        var map = ClaudeDesktopManager.MapOrphans(
            new[]
            {
                Instance(451, null, Clone(DefaultFolder)),                 // correct Default
                Instance(26126, null, Clone("Claude-Board")),              // the orphan
                Instance(26200, null, Clone("Claude-Board")),              // a second one
                Instance(1582, "/somewhere/Claude-work", Clone("Claude-work")),
                Instance(9, null, "/Applications/Claude.app")
            },
            Root,
            DefaultFolder);

        // First wins, matching MapInstances, so the menu and the pid Quit acts
        // on never name different processes.
        Assert.Equal(26126, Assert.Contains("Claude-Board", map));
        Assert.Single(map);
    }

    [Fact]
    public void MapOrphansIsEmptyWhenNothingIsStranded()
    {
        Assert.Empty(ClaudeDesktopManager.MapOrphans(
            new[] { Instance(451, null, Clone(DefaultFolder)) }, Root, DefaultFolder));

        Assert.Empty(ClaudeDesktopManager.MapOrphans(
            System.Array.Empty<ClaudeInstance>(), Root, DefaultFolder));
    }

    // ---- Compose and the digest ------------------------------------------

    [Fact]
    public void ComposePutsTheOrphanOnItsOwnProfilesRow()
    {
        var snapshot = ClaudeDesktopManager.Compose(new ClaudeDesktopManager.ScanResult(
            Installed: true,
            DefaultDirectory: "/p/Claude",
            Profiles: new[] { ("Claude", "/p/Claude"), ("Claude-Board", "/p/Claude-Board") },
            Running: ClaudeDesktopManager.MapInstances(System.Array.Empty<ClaudeInstance>()),
            Orphans: new System.Collections.Generic.Dictionary<string, int> { ["Claude-Board"] = 26126 }));

        var board = Assert.Single(snapshot.Profiles, p => p.DisplayName == "Board");

        // Not running — nothing is on its directory, which is the truth and the
        // whole reason the row was silent before.
        Assert.False(board.IsRunning);
        Assert.Equal(0, board.Pid);
        Assert.Equal(26126, board.OrphanPid);

        Assert.Equal(0, Assert.Single(snapshot.Profiles, p => p.IsDefault).OrphanPid);
    }

    // A scan taken before this existed carries no orphan map at all. "None" is
    // the right reading of that, not a crash.
    [Fact]
    public void AScanWithNoOrphanMapComposesCleanly()
    {
        var snapshot = ClaudeDesktopManager.Compose(new ClaudeDesktopManager.ScanResult(
            Installed: true,
            DefaultDirectory: "/p/Claude",
            Profiles: new[] { ("Claude-Board", "/p/Claude-Board") },
            Running: ClaudeDesktopManager.MapInstances(System.Array.Empty<ClaudeInstance>())));

        Assert.Equal(0, Assert.Single(snapshot.Profiles).OrphanPid);
    }

    // The menu only repaints when the digest changes, so a state the digest
    // cannot see is a state the menu never shows. That is exactly how the
    // duplicate-instance warning was invisible before InstanceCount joined it.
    [Fact]
    public void AnOrphanAppearingChangesTheDigest()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) return;

        ClaudeDesktopManager.ScanResult Scan(int orphanPid) =>
            new(Installed: true,
                DefaultDirectory: "/p/Claude",
                Profiles: new[] { ("Claude-Board", "/p/Claude-Board") },
                Running: ClaudeDesktopManager.MapInstances(System.Array.Empty<ClaudeInstance>()),
                Orphans: orphanPid == 0
                    ? ClaudeDesktopManager.EmptyOrphans
                    : new System.Collections.Generic.Dictionary<string, int> { ["Claude-Board"] = orphanPid });

        ClaudeDesktopManager.Adopt(Scan(0));
        var quiet = ClaudeDesktopManager.Digest();

        ClaudeDesktopManager.Adopt(Scan(26126));
        var stranded = ClaudeDesktopManager.Digest();

        Assert.NotEqual(quiet, stranded);

        // And only whether, never which: a pid in the digest would repaint the
        // whole menu every time the updater cycled a process, which is the rule
        // Digest's own comment states.
        ClaudeDesktopManager.Adopt(Scan(999));
        Assert.Equal(stranded, ClaudeDesktopManager.Digest());
    }

    // ---- StrandedPid -----------------------------------------------------

    private static ProfileView View(bool isRunning, int pid, int orphanPid) =>
        new("Board", "/Users/x/Library/Application Support/Claude-Board", false,
            isRunning, pid, ProfileActivity.None, null, "system", isRunning ? 1 : 0, orphanPid);

    // Platform-independent on purpose: the rule that produces OrphanPid already
    // returns null for Windows' null BundlePath, so this answers off the data
    // and needs no OperatingSystem arm that neither platform's tests can take.
    [Fact]
    public void StrandedPidIsTheOrphanWhenTheProfileItselfIsDown()
    {
        Assert.Equal(26126, ClaudeDesktopManager.StrandedPid(View(false, 0, 26126)));
    }

    // A running profile has its own instance and its own Quit. Answering with
    // the orphan here would make which window closed depend on scan timing.
    [Fact]
    public void StrandedPidIsNothingWhileTheProfileIsRunning()
    {
        Assert.Equal(0, ClaudeDesktopManager.StrandedPid(View(true, 900, 26126)));
    }

    [Fact]
    public void StrandedPidIsNothingWithoutAnOrphan()
    {
        Assert.Equal(0, ClaudeDesktopManager.StrandedPid(View(false, 0, 0)));
        Assert.Equal(0, ClaudeDesktopManager.StrandedPid(View(false, 0, -1)));
    }
}
