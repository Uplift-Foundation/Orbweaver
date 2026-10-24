using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// Everything in ClaudeDesktopManager that is a decision rather than a call into
// the OS.
//
// The whole feature rests on one hazard, stated at the top of the file it tests:
// two Chromium processes on one userData directory corrupt leveldb and SQLite,
// and Claude Desktop takes no single-instance lock of its own. Most of what is
// checked below is what stands between the user and that — which directory a
// running process is actually on, whether two of them are on the same one,
// whether a symlink has produced a second menu row for one directory that would
// defeat the launch guard. The rest is the config.json rewrite, which has the
// profile's stored login inside it.
//
// One class on purpose. xUnit runs different classes in parallel and this file's
// subject is static: a published snapshot, a transient table, and an
// environment variable naming the profile root. Tests inside one class run
// sequentially, so one class is the isolation.
[Collection("Settings")]
public class ClaudeDesktopManagerTests
{
    // ---- scratch -------------------------------------------------------

    private const string RootVariable = "ORBWEAVER_PROFILE_ROOT";

    // A profile root nobody else owns, pointed at through the env-var seam
    // ClaudeDesktopManager already has for exactly this (ProfileRoot's own
    // comment). Restored afterwards, because it is process-wide.
    private sealed class Scratch : IDisposable
    {
        private readonly string? _before = Environment.GetEnvironmentVariable(RootVariable);

        public Scratch()
        {
            Root = Path.Combine(Path.GetTempPath(), "cb-profiles-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Environment.SetEnvironmentVariable(RootVariable, Root);
        }

        public string Root { get; }

        public string Profile(string folder, params string[] markers)
        {
            var directory = Path.Combine(Root, folder);
            Directory.CreateDirectory(directory);

            foreach (var marker in markers)
            {
                var path = Path.Combine(directory, marker);

                // "Local Storage/leveldb" and "Crashpad" are directory markers;
                // everything else in MarkerFiles is a file.
                if (marker.Contains(Path.DirectorySeparatorChar) || marker == "Crashpad")
                {
                    Directory.CreateDirectory(path);
                }
                else
                {
                    File.WriteAllText(path, "{}");
                }
            }

            return directory;
        }

        // Two markers is the threshold LooksLikeProfile applies to a populated
        // directory, so this is "a directory the feature will adopt".
        public string RealProfile(string folder) => Profile(folder, "config.json", "Cookies");

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(RootVariable, _before);
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    private static ProfileView View(
        string directory,
        bool isDefault = false,
        bool isRunning = false,
        int pid = 0,
        int instanceCount = 0,
        ProfileActivity activity = ProfileActivity.None,
        string? message = null,
        string themeMode = ClaudeDesktopManager.SystemTheme) =>
        new(Path.GetFileName(directory), directory, isDefault, isRunning, pid,
            activity, message, themeMode, instanceCount);

    // ---- ProfileRoot ---------------------------------------------------

    [Fact]
    public void ProfileRootHonoursTheScratchOverrideAndOtherwiseUsesThePlatformPath()
    {
        using (var scratch = new Scratch())
        {
            Assert.Equal(scratch.Root, ClaudeDesktopManager.ProfileRoot);
        }

        // Back to the real one. Windows resolves %APPDATA% straight from
        // SpecialFolder.ApplicationData; macOS is built from the home directory,
        // because ApplicationData there is ~/.config rather than
        // ~/Library/Application Support, which is where Claude Desktop actually
        // keeps a profile.
        var expected = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support");

        Assert.Equal(expected, ClaudeDesktopManager.ProfileRoot);
    }

    // ---- Canonicalise --------------------------------------------------

    [Fact]
    public void CanonicaliseReturnsNullForSomethingThatIsNotThere()
    {
        using var scratch = new Scratch();

        Assert.Null(ClaudeDesktopManager.Canonicalise(Path.Combine(scratch.Root, "absent")));
    }

    [Fact]
    public void CanonicaliseReturnsAnAbsolutePathWithNoTrailingSeparator()
    {
        using var scratch = new Scratch();
        var directory = scratch.RealProfile("Claude-work");

        var canonical = ClaudeDesktopManager.Canonicalise(directory + Path.DirectorySeparatorChar);

        Assert.NotNull(canonical);
        Assert.True(Path.IsPathRooted(canonical));
        Assert.False(canonical!.EndsWith('/'));
    }

    // Filesystem root is the one path where trimming the trailing separator
    // would leave nothing at all, which is what the `full.Length > 1` guard is
    // for. Only meaningful where "/" is the root.
    [Fact]
    public void CanonicaliseLeavesTheFilesystemRootAlone()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.Equal("/", ClaudeDesktopManager.Canonicalise("/"));
    }

    // A symlink and the directory it points at are one profile, not two. Getting
    // this wrong is not cosmetic: two rows for one directory defeat the launch
    // guard, and the launch guard is what stops two Chromium processes sharing a
    // leveldb.
    [Fact]
    public void CanonicaliseFollowsASymlinkToItsTarget()
    {
        using var scratch = new Scratch();
        var real = scratch.RealProfile("Claude-real");
        var link = Path.Combine(scratch.Root, "Claude-link");
        Directory.CreateSymbolicLink(link, real);

        Assert.Equal(
            ClaudeDesktopManager.Canonicalise(real),
            ClaudeDesktopManager.Canonicalise(link));
    }

    // A path the OS refuses to describe at all. Every filesystem walk in this
    // file swallows its own failures rather than letting one bad directory take
    // the tray menu down with it, and this is the cheapest way to be one.
    [Fact]
    public void CanonicaliseAndIsSymlinkSwallowAnUnusablePath()
    {
        var nonsense = "cb-\0-not-a-path";

        Assert.Null(ClaudeDesktopManager.Canonicalise(nonsense));
        Assert.False(ClaudeDesktopManager.IsSymlink(nonsense));
    }

    [Fact]
    public void IsSymlinkSeparatesALinkFromARealDirectory()
    {
        using var scratch = new Scratch();
        var real = scratch.RealProfile("Claude-real");
        var link = Path.Combine(scratch.Root, "Claude-link");
        Directory.CreateSymbolicLink(link, real);

        Assert.False(ClaudeDesktopManager.IsSymlink(real));
        Assert.True(ClaudeDesktopManager.IsSymlink(link));
        Assert.False(ClaudeDesktopManager.IsSymlink(Path.Combine(scratch.Root, "absent")));
    }

    // ---- LooksLikeProfile ----------------------------------------------

    // An empty directory is adoptable because New profile creates them empty:
    // refuse it and the profile you just made never appears.
    [Fact]
    public void AnEmptyDirectoryIsAdoptable()
    {
        using var scratch = new Scratch();

        Assert.True(ClaudeDesktopManager.LooksLikeProfile(scratch.Profile("Claude-new")));
    }

    // Two markers, not one. One is far too easy to hit by accident — plenty of
    // Electron apps write a "Preferences" — and adopting somebody else's folder
    // means offering to launch Claude Desktop on it.
    [Fact]
    public void OneMarkerIsNotEnoughAndTwoIs()
    {
        using var scratch = new Scratch();

        Assert.False(ClaudeDesktopManager.LooksLikeProfile(scratch.Profile("Claude-one", "Preferences")));
        Assert.True(ClaudeDesktopManager.LooksLikeProfile(
            scratch.Profile("Claude-two", "Preferences", "Local State")));
    }

    // Directory markers count towards the same total as file markers — a profile
    // that has been used has Local Storage/leveldb and Crashpad whether or not
    // it happens to have any of the named files.
    [Fact]
    public void DirectoryMarkersCountTowardsTheSameTotal()
    {
        using var scratch = new Scratch();

        Assert.True(ClaudeDesktopManager.LooksLikeProfile(
            scratch.Profile("Claude-dirs", Path.Combine("Local Storage", "leveldb"), "Crashpad")));
    }

    [Fact]
    public void ADirectoryThatIsNotThereDoesNotLookLikeAProfile()
    {
        using var scratch = new Scratch();

        Assert.False(ClaudeDesktopManager.LooksLikeProfile(Path.Combine(scratch.Root, "absent")));
    }

    // ---- Discover ------------------------------------------------------

    [Fact]
    public void DiscoverReturnsNothingWhenTheProfileRootIsMissing()
    {
        using var scratch = new Scratch();
        Directory.Delete(scratch.Root);

        Assert.Empty(ClaudeDesktopManager.Discover());
    }

    // Every exclusion Discover applies, in one place, because each of them has a
    // specific reason and losing any one of them is a different bug: "-3p" is
    // Claude Desktop's own live sidecar config directory, "-dev" is an
    // unpackaged build, the case-sensitive match is what keeps unrelated
    // vendors out on a case-insensitive volume, and a folder with no markers is
    // somebody else's.
    [Theory]
    [InlineData("Claude-3p")]
    [InlineData("Claude-dev")]
    [InlineData("claude-lowercase")]
    [InlineData("CLAUDE-SHOUTING")]
    [InlineData("Cursor")]
    [InlineData("ClaudeSomething")]
    public void DiscoverSkipsWhatIsNotAProfileDirectory(string folder)
    {
        using var scratch = new Scratch();
        scratch.RealProfile(folder);

        Assert.Empty(ClaudeDesktopManager.Discover());
    }

    [Fact]
    public void DiscoverSkipsAClaudeNamedFolderWithoutEnoughMarkers()
    {
        using var scratch = new Scratch();
        scratch.Profile("Claude-someone-elses", "Preferences");

        Assert.Empty(ClaudeDesktopManager.Discover());
    }

    // Default first, then the rest by name and case-insensitively — the menu
    // reads top to bottom and "Default" is the one people look for.
    [Fact]
    public void DiscoverPutsDefaultFirstAndSortsTheRestByName()
    {
        using var scratch = new Scratch();
        scratch.RealProfile("Claude-zebra");
        scratch.RealProfile("Claude-apple");
        scratch.RealProfile("Claude");
        scratch.RealProfile("Claude-Banana");

        var found = ClaudeDesktopManager.Discover().Select(p => p.Name).ToArray();

        Assert.Equal(new[] { "Claude", "Claude-apple", "Claude-Banana", "Claude-zebra" }, found);
    }

    // A symlinked alias beside the real directory is one profile. The sort puts
    // real directories first precisely so the dedupe — which is first-one-wins
    // — keeps the real folder's name rather than the alias's; without it the
    // filesystem's arbitrary ordering decides which name the menu shows.
    [Fact]
    public void ASymlinkedAliasDoesNotBecomeASecondProfile()
    {
        using var scratch = new Scratch();
        var real = scratch.RealProfile("Claude-real");
        Directory.CreateSymbolicLink(Path.Combine(scratch.Root, "Claude-alias"), real);

        var found = ClaudeDesktopManager.Discover();

        var only = Assert.Single(found);
        Assert.Equal("Claude-real", only.Name);
    }

    // ---- names ---------------------------------------------------------

    [Fact]
    public void TheDefaultFolderIsCalledDefaultAndTheRestDropTheirPrefix()
    {
        Assert.Equal("Default", ClaudeDesktopManager.DisplayNameFor("Claude"));
        Assert.Equal("work", ClaudeDesktopManager.DisplayNameFor("Claude-work"));
        Assert.Equal("Profile-1", ClaudeDesktopManager.DisplayNameFor("Claude-Profile-1"));
    }

    [Fact]
    public void DefaultDirectoryFallsBackToTheUnresolvedPathWhenItIsNotThere()
    {
        using var scratch = new Scratch();

        Assert.Equal(Path.Combine(scratch.Root, "Claude"), ClaudeDesktopManager.DefaultDirectory());

        var real = scratch.RealProfile("Claude");
        Assert.Equal(ClaudeDesktopManager.Canonicalise(real), ClaudeDesktopManager.DefaultDirectory());
    }

    // ---- MapInstances --------------------------------------------------

    // No override in the environment means the app resolved its own default
    // location, which is what a Dock launch does — and what this app
    // deliberately does for Default. Mapping it anywhere else would leave a
    // Dock-launched Default invisible to the launch guard.
    [Fact]
    public void AnInstanceWithNoOverrideIsTheDefaultProfile()
    {
        using var scratch = new Scratch();
        scratch.RealProfile("Claude");

        var running = ClaudeDesktopManager.MapInstances(new[] { new ClaudeInstance(4321, null) });

        var entry = Assert.Single(running);
        Assert.Equal(ClaudeDesktopManager.DefaultDirectory(), entry.Key);
        Assert.Equal(4321, entry.Value.Pid);
        Assert.Equal(1, entry.Value.Count);
    }

    [Fact]
    public void AnInstanceWithAnOverrideIsMappedToItsCanonicalDirectory()
    {
        using var scratch = new Scratch();
        var real = scratch.RealProfile("Claude-work");
        var link = Path.Combine(scratch.Root, "Claude-alias");
        Directory.CreateSymbolicLink(link, real);

        // Two processes, one directory reached two ways. This is the case
        // TryAdd used to hide: the menu showed a single "running" row and
        // nothing suggested anything was wrong, while two Chromiums were on one
        // leveldb.
        var running = ClaudeDesktopManager.MapInstances(new[]
        {
            new ClaudeInstance(100, real),
            new ClaudeInstance(200, link)
        });

        var entry = Assert.Single(running);
        Assert.Equal(ClaudeDesktopManager.Canonicalise(real), entry.Key);
        Assert.Equal(2, entry.Value.Count);

        // The first pid, because that is the one Focus and Quit act on.
        Assert.Equal(100, entry.Value.Pid);
    }

    // A directory that no longer exists cannot be canonicalised, and the
    // instance still has to be counted: a process on a directory somebody has
    // since renamed is exactly the situation where the menu must not quietly
    // decide nothing is running.
    [Fact]
    public void AnOverridePointingAtSomethingGoneIsStillCounted()
    {
        using var scratch = new Scratch();
        var missing = Path.Combine(scratch.Root, "Claude-deleted");

        var running = ClaudeDesktopManager.MapInstances(new[] { new ClaudeInstance(7, missing) });

        var entry = Assert.Single(running);
        Assert.Equal(Path.GetFullPath(missing), entry.Key.TrimEnd('/'));
    }

    [Fact]
    public void AnUnusableOverrideIsDroppedRatherThanThrowing()
    {
        using var scratch = new Scratch();

        var running = ClaudeDesktopManager.MapInstances(new[] { new ClaudeInstance(7, "cb-\0-bad") });

        Assert.Empty(running);
    }

    // ---- the transient state machine -----------------------------------
    //
    // What each menu row says while something is in flight. There is no
    // automatic escalation anywhere in here, and the source says why: SIGTERM
    // is not graceful for Electron and a refusal to quit is often legitimate,
    // so the app offers Force quit and makes the user mean it.

    // A scan has to be installed before any of these, because SetTransient
    // recomposes the published snapshot and recomposing with no scan yet asks
    // for a real one.
    //
    // Whether the scan says the directory is running matters more than it looks:
    // SetTransient recomposes immediately, and Compose runs the state machine —
    // so setting Quitting against a scan that says nothing is running clears it
    // on the spot, which is correct behaviour and not a test artefact.
    private static void SeedScan(string directory, bool running = false)
    {
        var live = new Dictionary<string, ClaudeDesktopManager.InstanceGroup>(StringComparer.Ordinal);
        if (running) live[directory] = new ClaudeDesktopManager.InstanceGroup(1234, 1);

        ClaudeDesktopManager.Adopt(new ClaudeDesktopManager.ScanResult(
            Installed: true,
            DefaultDirectory: ClaudeDesktopManager.DefaultDirectory(),
            Profiles: new[] { (Name: Path.GetFileName(directory), Directory: directory) },
            Running: live));
    }

    [Fact]
    public void ADirectoryWithNothingInFlightHasNoActivity()
    {
        using var scratch = new Scratch();
        var directory = scratch.RealProfile("Claude-quiet");
        SeedScan(directory);

        Assert.Equal(
            (ProfileActivity.None, (string?)null),
            ClaudeDesktopManager.ResolveTransient(directory, isRunning: false, Environment.TickCount64));
    }

    // Launching is sticky for 30 seconds because Claude Desktop takes seconds to
    // show a window, and without the sticky state the user clicks again and gets
    // a second instance on the same directory.
    [Fact]
    public void LaunchingHoldsUntilTheInstanceAppears()
    {
        using var scratch = new Scratch();
        var directory = scratch.RealProfile("Claude-launching");
        SeedScan(directory);

        try
        {
            ClaudeDesktopManager.SetTransient(directory, ProfileActivity.Launching, 30_000);

            Assert.Equal(ProfileActivity.Launching,
                ClaudeDesktopManager.ResolveTransient(directory, false, Environment.TickCount64).Item1);

            // The instance turning up clears it, and clears it for good.
            Assert.Equal(ProfileActivity.None,
                ClaudeDesktopManager.ResolveTransient(directory, true, Environment.TickCount64).Item1);
            Assert.Equal(ProfileActivity.None,
                ClaudeDesktopManager.ResolveTransient(directory, false, Environment.TickCount64).Item1);
        }
        finally
        {
            ClaudeDesktopManager.ClearTransient(directory);
        }
    }

    // A launch that never produces a process gives up rather than saying
    // "Launching…" for the rest of the session.
    [Fact]
    public void LaunchingGivesUpWhenItsWindowExpires()
    {
        using var scratch = new Scratch();
        var directory = scratch.RealProfile("Claude-stuck");
        SeedScan(directory);

        try
        {
            ClaudeDesktopManager.SetTransient(directory, ProfileActivity.Launching, 30_000);

            Assert.Equal(ProfileActivity.None,
                ClaudeDesktopManager.ResolveTransient(directory, false, long.MaxValue).Item1);
        }
        finally
        {
            ClaudeDesktopManager.ClearTransient(directory);
        }
    }

    [Fact]
    public void QuittingClearsAsSoonAsTheProcessIsGone()
    {
        using var scratch = new Scratch();
        var directory = scratch.RealProfile("Claude-quitting");
        SeedScan(directory, running: true);

        try
        {
            ClaudeDesktopManager.SetTransient(directory, ProfileActivity.Quitting, 20_000);

            Assert.Equal(ProfileActivity.Quitting,
                ClaudeDesktopManager.ResolveTransient(directory, true, Environment.TickCount64).Item1);
            Assert.Equal(ProfileActivity.None,
                ClaudeDesktopManager.ResolveTransient(directory, false, Environment.TickCount64).Item1);
        }
        finally
        {
            ClaudeDesktopManager.ClearTransient(directory);
        }
    }

    // A quit that is refused becomes an offer, not an escalation — and the offer
    // replaces the transient, so it survives the next tick rather than
    // re-deciding from a stale deadline every time.
    [Fact]
    public void AQuitThatIsIgnoredTurnsIntoAnOfferToForceIt()
    {
        using var scratch = new Scratch();
        var directory = scratch.RealProfile("Claude-wontquit");
        SeedScan(directory, running: true);

        try
        {
            ClaudeDesktopManager.SetTransient(directory, ProfileActivity.Quitting, 20_000);

            // Just past the quit window, not long.MaxValue: the escalation
            // computes the offer's own deadline as now + 60s, and a `now` at the
            // top of the range makes that overflow into the past — which would
            // then read as an offer that had already lapsed.
            var expired = Environment.TickCount64 + 20_001;

            Assert.Equal(ProfileActivity.ForceQuitOffered,
                ClaudeDesktopManager.ResolveTransient(directory, true, expired).Item1);

            // Still offered on the following tick, from the replaced transient.
            Assert.Equal(ProfileActivity.ForceQuitOffered,
                ClaudeDesktopManager.ResolveTransient(directory, true, expired + 1).Item1);

            // And gone the moment the process is.
            Assert.Equal(ProfileActivity.None,
                ClaudeDesktopManager.ResolveTransient(directory, false, expired + 2).Item1);
        }
        finally
        {
            ClaudeDesktopManager.ClearTransient(directory);
        }
    }

    // The one place this file behaves differently per platform, and it is
    // deliberate. On macOS a graceful quit works, so a lapsed offer just means
    // "ask nicely again". On Windows nothing can end the app except the offer,
    // so letting it expire stranded the instance: the row fell back to Quit,
    // that click could no longer find a window to close, and there was no route
    // left to the only thing that does work.
    [Fact]
    public void AnExpiredForceQuitOfferLapsesOnMacOsAndStandsOnWindows()
    {
        using var scratch = new Scratch();
        var directory = scratch.RealProfile("Claude-offered");
        SeedScan(directory, running: true);

        try
        {
            ClaudeDesktopManager.SetTransient(directory, ProfileActivity.ForceQuitOffered, 60_000);

            var expected = OperatingSystem.IsWindows()
                ? ProfileActivity.ForceQuitOffered
                : ProfileActivity.None;

            Assert.Equal(expected,
                ClaudeDesktopManager.ResolveTransient(directory, true, long.MaxValue).Item1);
        }
        finally
        {
            ClaudeDesktopManager.ClearTransient(directory);
        }
    }

    [Fact]
    public void AnErrorCarriesItsMessageUntilItExpires()
    {
        using var scratch = new Scratch();
        var directory = scratch.RealProfile("Claude-error");
        SeedScan(directory);

        try
        {
            ClaudeDesktopManager.SetTransient(directory, ProfileActivity.Error, 20_000, "couldn't launch");

            Assert.Equal((ProfileActivity.Error, "couldn't launch"),
                ClaudeDesktopManager.ResolveTransient(directory, false, Environment.TickCount64));

            Assert.Equal((ProfileActivity.None, (string?)null),
                ClaudeDesktopManager.ResolveTransient(directory, false, long.MaxValue));
        }
        finally
        {
            ClaudeDesktopManager.ClearTransient(directory);
        }
    }

    // ---- Compose -------------------------------------------------------

    [Fact]
    public void ComposeTurnsAScanIntoTheRowsTheMenuDraws()
    {
        using var scratch = new Scratch();
        var defaultDirectory = scratch.RealProfile("Claude");
        var work = scratch.RealProfile("Claude-work");
        File.WriteAllText(Path.Combine(work, "config.json"), """{"userThemeMode":"dark"}""");

        var running = new Dictionary<string, ClaudeDesktopManager.InstanceGroup>(StringComparer.Ordinal)
        {
            [ClaudeDesktopManager.Canonicalise(work)!] = new(2222, 2)
        };

        var snapshot = ClaudeDesktopManager.Compose(new ClaudeDesktopManager.ScanResult(
            Installed: true,
            DefaultDirectory: ClaudeDesktopManager.Canonicalise(defaultDirectory)!,
            Profiles: new[]
            {
                ("Claude", ClaudeDesktopManager.Canonicalise(defaultDirectory)!),
                ("Claude-work", ClaudeDesktopManager.Canonicalise(work)!)
            },
            Running: running));

        Assert.True(snapshot.AppInstalled);
        Assert.Equal(2, snapshot.Profiles.Count);

        var first = snapshot.Profiles[0];
        Assert.Equal("Default", first.DisplayName);
        Assert.True(first.IsDefault);
        Assert.False(first.IsRunning);
        Assert.Equal(0, first.Pid);
        Assert.Equal(0, first.InstanceCount);
        Assert.Equal(ClaudeDesktopManager.SystemTheme, first.ThemeMode);

        var second = snapshot.Profiles[1];
        Assert.Equal("work", second.DisplayName);
        Assert.False(second.IsDefault);
        Assert.True(second.IsRunning);
        Assert.Equal(2222, second.Pid);
        Assert.Equal(2, second.InstanceCount);
        Assert.Equal("dark", second.ThemeMode);
    }

    // A name chosen in settings wins over the folder name. Empty means "no
    // choice", not "call it nothing" — a profile with a blank row would be
    // unclickable in practice.
    [Fact]
    public void AChosenNameWinsOverTheFolderNameAndAnEmptyOneDoesNot()
    {
        using var scratch = new Scratch();
        var work = scratch.RealProfile("Claude-work");
        var canonical = ClaudeDesktopManager.Canonicalise(work)!;

        try
        {
            OrbweaverSettings.Update("Claude-work", p => p.Name = "Day job");

            var named = ClaudeDesktopManager.Compose(new ClaudeDesktopManager.ScanResult(
                true, "unrelated", new[] { ("Claude-work", canonical) },
                new Dictionary<string, ClaudeDesktopManager.InstanceGroup>(StringComparer.Ordinal)));

            Assert.Equal("Day job", named.Profiles[0].DisplayName);

            OrbweaverSettings.Update("Claude-work", p => p.Name = "");

            var unnamed = ClaudeDesktopManager.Compose(new ClaudeDesktopManager.ScanResult(
                true, "unrelated", new[] { ("Claude-work", canonical) },
                new Dictionary<string, ClaudeDesktopManager.InstanceGroup>(StringComparer.Ordinal)));

            Assert.Equal("work", unnamed.Profiles[0].DisplayName);
        }
        finally
        {
            OrbweaverSettings.RemoveProfile("Claude-work");
        }
    }

    // ---- Digest --------------------------------------------------------

    // The digest is TrayController's rebuild signature: the menu only repaints
    // when this string changes. Anything that shows in the menu and is *not* in
    // here can change on screen and never be redrawn — which is why the
    // settings-derived values are in it, and why the instance count is in it
    // even though it is a count.
    [Fact]
    public void AnUninstalledAppDigestsToOff()
    {
        Assert.Equal("cd=off", ClaudeDesktopManager.DigestOf(
            new DesktopSnapshot(false, new[] { View("/tmp/Claude-work") })));
    }

    [Fact]
    public void TheDigestIsIndependentOfTheOrderTheProfilesArriveIn()
    {
        var a = View("/tmp/Claude-a");
        var b = View("/tmp/Claude-b");

        Assert.Equal(
            ClaudeDesktopManager.DigestOf(new DesktopSnapshot(true, new[] { a, b })),
            ClaudeDesktopManager.DigestOf(new DesktopSnapshot(true, new[] { b, a })));
    }

    [Theory]
    [InlineData("running")]
    [InlineData("activity")]
    [InlineData("message")]
    [InlineData("theme")]
    [InlineData("instances")]
    public void EveryPieceOfARowThatCanChangeChangesTheDigest(string what)
    {
        var before = View("/tmp/Claude-x", isRunning: false, pid: 0, instanceCount: 0,
            activity: ProfileActivity.None, message: null, themeMode: "system");

        var after = what switch
        {
            "running" => before with { IsRunning = true },
            "activity" => before with { Activity = ProfileActivity.Launching },
            "message" => before with { Message = "couldn't launch" },
            "theme" => before with { ThemeMode = "dark" },
            _ => before with { InstanceCount = 2 }
        };

        Assert.NotEqual(
            ClaudeDesktopManager.DigestOf(new DesktopSnapshot(true, new[] { before })),
            ClaudeDesktopManager.DigestOf(new DesktopSnapshot(true, new[] { after })));
    }

    // Change a colour or hide a swatch and the menu has to repaint, which it
    // only does when this string changes.
    [Fact]
    public void ChangingAProfilesColourOrSwatchChangesTheDigest()
    {
        using var scratch = new Scratch();
        var directory = scratch.RealProfile("Claude-tinted");
        var snapshot = new DesktopSnapshot(true, new[] { View(directory) });

        try
        {
            var before = ClaudeDesktopManager.DigestOf(snapshot);

            OrbweaverSettings.Update("Claude-tinted", p => p.Color = "purple");
            var recoloured = ClaudeDesktopManager.DigestOf(snapshot);
            Assert.NotEqual(before, recoloured);

            OrbweaverSettings.Update("Claude-tinted", p => p.ShowSwatch = false);
            Assert.NotEqual(recoloured, ClaudeDesktopManager.DigestOf(snapshot));
        }
        finally
        {
            OrbweaverSettings.RemoveProfile("Claude-tinted");
        }
    }

    // ---- Publish and Recompose ----------------------------------------

    [Fact]
    public void PublishingReplacesBothTheSnapshotAndTheDigestTogether()
    {
        using var scratch = new Scratch();
        var directory = scratch.RealProfile("Claude-published");

        ClaudeDesktopManager.Publish(new DesktopSnapshot(true, new[] { View(directory, isRunning: true, pid: 9) }));

        Assert.True(ClaudeDesktopManager.Snapshot.AppInstalled);
        Assert.Equal(9, Assert.Single(ClaudeDesktopManager.Snapshot.Profiles).Pid);

        // The tray reads the snapshot and the digest separately, so a mismatched
        // pair means a menu that does not match its own rebuild signature.
        Assert.Equal(
            ClaudeDesktopManager.DigestOf(ClaudeDesktopManager.Snapshot),
            ClaudeDesktopManager.Digest());
    }

    // Recompose exists so a click that changes transient state shows up
    // immediately without waiting for — or paying for — another scan.
    [Fact]
    public void SettingATransientRepublishesFromTheLastScanWithoutRescanning()
    {
        using var scratch = new Scratch();
        var directory = scratch.RealProfile("Claude-clicked");
        var canonical = ClaudeDesktopManager.Canonicalise(directory)!;
        SeedScan(canonical);

        try
        {
            Assert.Equal(ProfileActivity.None,
                Assert.Single(ClaudeDesktopManager.Snapshot.Profiles).Activity);

            ClaudeDesktopManager.SetTransient(canonical, ProfileActivity.Launching, 30_000);

            Assert.Equal(ProfileActivity.Launching,
                Assert.Single(ClaudeDesktopManager.Snapshot.Profiles).Activity);

            ClaudeDesktopManager.ClearTransient(canonical);

            Assert.Equal(ProfileActivity.None,
                Assert.Single(ClaudeDesktopManager.Snapshot.Profiles).Activity);
        }
        finally
        {
            ClaudeDesktopManager.ClearTransient(canonical);
        }
    }

    // The one path that cannot be reached by handing a scan in: the very first
    // Recompose, before anything has scanned, which has to ask for one rather
    // than publish nothing. Reached by clearing the remembered scan — the field
    // is private and there is no other way to be "before the first scan" once
    // the process has done one — and then waited out, because the refresh it
    // kicks runs on a background task and would otherwise publish over whatever
    // the next test set up.
    [Fact]
    public void ARecomposeBeforeAnyScanAsksForOne()
    {
        using var scratch = new Scratch();

        var lastScan = typeof(ClaudeDesktopManager)
            .GetField("_lastScan", BindingFlags.NonPublic | BindingFlags.Static)!;
        var refreshing = typeof(ClaudeDesktopManager)
            .GetField("_refreshing", BindingFlags.NonPublic | BindingFlags.Static)!;

        // Nothing else in this assembly kicks a refresh, but the in-flight
        // guard would make this test silently do nothing if one were somehow
        // still running, so it is waited out first rather than assumed.
        WaitForRefreshToSettle(refreshing);

        lastScan.SetValue(null, null);
        ClaudeDesktopManager.Recompose();

        // KickRefresh sets the in-flight flag synchronously before starting its
        // task, so waiting for it to clear is a real join on that task.
        WaitForRefreshToSettle(refreshing);

        Assert.Equal(0, (int)refreshing.GetValue(null)!);
        Assert.NotNull(lastScan.GetValue(null));
    }

    private static void WaitForRefreshToSettle(FieldInfo refreshing)
    {
        var deadline = Environment.TickCount64 + 20_000;
        while ((int)refreshing.GetValue(null)! != 0 && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(10);
        }

        // Fails by name instead of returning and letting the assertion after it
        // report a symptom (CB-269).
        Assert.True((int)refreshing.GetValue(null)! == 0,
            "the background refresh never cleared its in-flight flag.");
    }

    // ---- ReadThemeMode ------------------------------------------------

    [Theory]
    [InlineData("""{"userThemeMode":"dark"}""", "dark")]
    [InlineData("""{"userThemeMode":"light"}""", "light")]
    // A number, not a string: the app itself only ever writes a string, so this
    // is a hand-edited or corrupted file, and guessing at it would put a theme
    // check mark next to something nobody chose.
    [InlineData("""{"userThemeMode":3}""", "system")]
    [InlineData("""{"somethingElse":"dark"}""", "system")]
    [InlineData("not json at all", "system")]
    [InlineData("", "system")]
    public void TheThemeIsReadFromConfigJsonAndFallsBackToSystem(string contents, string expected)
    {
        using var scratch = new Scratch();
        var directory = scratch.Profile("Claude-theme");
        File.WriteAllText(Path.Combine(directory, "config.json"), contents);

        Assert.Equal(expected, ClaudeDesktopManager.ReadThemeMode(directory));
    }

    [Fact]
    public void AProfileWithNoConfigYetMatchesTheSystem()
    {
        using var scratch = new Scratch();

        Assert.Equal(ClaudeDesktopManager.SystemTheme,
            ClaudeDesktopManager.ReadThemeMode(scratch.Profile("Claude-fresh")));
    }

    // ---- WriteThemeMode ----------------------------------------------

    [Fact]
    public void WritingAThemeCreatesTheConfigWhenThereIsNoneYet()
    {
        using var scratch = new Scratch();
        var directory = scratch.Profile("Claude-fresh");

        Assert.Null(ClaudeDesktopManager.WriteThemeMode(directory, "dark"));
        Assert.Equal("dark", ClaudeDesktopManager.ReadThemeMode(directory));
    }

    // The whole reason PreservesKeys exists: this file holds the profile's
    // login, in oauth:tokenCache, and a serialiser quirk that dropped or
    // rewrote it would cost the profile its session with nothing on screen to
    // say so.
    [Fact]
    public void WritingAThemeLeavesEveryOtherKeyByteIdentical()
    {
        using var scratch = new Scratch();
        var directory = scratch.Profile("Claude-loggedin");
        var path = Path.Combine(directory, "config.json");

        File.WriteAllText(path, """
            {"oauth:tokenCache":"AAAABBBBCCCC","deploymentMode":"cloud",
             "nested":{"a":[1,2,{"b":true}]},"userThemeMode":"light"}
            """);

        Assert.Null(ClaudeDesktopManager.WriteThemeMode(directory, "dark"));

        using var after = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal("AAAABBBBCCCC", after.RootElement.GetProperty("oauth:tokenCache").GetString());
        Assert.Equal("cloud", after.RootElement.GetProperty("deploymentMode").GetString());
        Assert.Equal("""{"a":[1,2,{"b":true}]}""",
            after.RootElement.GetProperty("nested").GetRawText());
        Assert.Equal("dark", after.RootElement.GetProperty("userThemeMode").GetString());

        // And no temporary left beside it.
        Assert.False(File.Exists(path + $".{Brand.Slug}.tmp"));
    }

    [Fact]
    public void AConfigThatIsNotAJsonObjectIsRefusedRatherThanReplaced()
    {
        using var scratch = new Scratch();
        var directory = scratch.Profile("Claude-broken");
        var path = Path.Combine(directory, "config.json");

        File.WriteAllText(path, "[1,2,3]");
        Assert.Equal("config unreadable", ClaudeDesktopManager.WriteThemeMode(directory, "dark"));

        // Untouched. Refusing has to mean refusing, not "half done".
        Assert.Equal("[1,2,3]", File.ReadAllText(path));
    }

    // The safety check is conservative on purpose, and this is what that costs.
    // A \uXXXX escape in the original is re-serialised as the character it
    // stands for, so the two raw texts differ even though the two values are
    // the same string — and the rewrite is thrown away rather than the
    // original. Refusing to change the theme is the right direction to be wrong
    // in when the alternative is replacing a file that holds the profile's
    // login on the strength of a comparison that has just failed. The temporary
    // goes with it rather than being left beside the real file.
    [Fact]
    public void ARewriteTheSerialiserNormalisedIsThrownAwayRatherThanTheOriginal()
    {
        using var scratch = new Scratch();
        var directory = scratch.Profile("Claude-escaped");
        var path = Path.Combine(directory, "config.json");

        // Deliberately not a raw string literal: the point of the fixture is the
        // two-character sequence backslash-u in the file on disk.
        var original = "{\"oauth:tokenCache\":\"\\u0041\\u0042\\u0043\","
                       + "\"userThemeMode\":\"light\"}";

        File.WriteAllText(path, original);

        Assert.Equal("config rewrite unsafe", ClaudeDesktopManager.WriteThemeMode(directory, "dark"));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.False(File.Exists(path + $".{Brand.Slug}.tmp"));
    }

    [Fact]
    public void AConfigThatIsNotJsonAtAllIsRefused()
    {
        using var scratch = new Scratch();
        var directory = scratch.Profile("Claude-garbage");
        File.WriteAllText(Path.Combine(directory, "config.json"), "{ not json");

        Assert.Equal("couldn't set theme", ClaudeDesktopManager.WriteThemeMode(directory, "dark"));
    }

    [Fact]
    public void AProfileDirectoryThatIsGoneReportsAFailureRatherThanThrowing()
    {
        using var scratch = new Scratch();

        Assert.Equal("couldn't set theme",
            ClaudeDesktopManager.WriteThemeMode(Path.Combine(scratch.Root, "absent"), "dark"));
    }

    // ---- PreservesKeys ------------------------------------------------

    private static string Candidate(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "cb-candidate-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void PreservesKeysAcceptsARewriteThatOnlyChangedTheTheme()
    {
        var candidate = Candidate("""{"a":1,"userThemeMode":"dark"}""");
        try
        {
            Assert.True(ClaudeDesktopManager.PreservesKeys(
                """{"a":1,"userThemeMode":"light"}""", candidate, "dark"));
        }
        finally { File.Delete(candidate); }
    }

    [Fact]
    public void PreservesKeysRefusesARewriteThatLostAKey()
    {
        var candidate = Candidate("""{"userThemeMode":"dark"}""");
        try
        {
            Assert.False(ClaudeDesktopManager.PreservesKeys(
                """{"oauth:tokenCache":"secret","userThemeMode":"light"}""", candidate, "dark"));
        }
        finally { File.Delete(candidate); }
    }

    [Fact]
    public void PreservesKeysRefusesARewriteThatChangedAValue()
    {
        var candidate = Candidate("""{"oauth:tokenCache":"tampered","userThemeMode":"dark"}""");
        try
        {
            Assert.False(ClaudeDesktopManager.PreservesKeys(
                """{"oauth:tokenCache":"secret","userThemeMode":"light"}""", candidate, "dark"));
        }
        finally { File.Delete(candidate); }
    }

    // The mode has to be the one asked for, not merely present: a rewrite that
    // wrote the old value back would leave the menu showing a check mark
    // against a theme the app is not using.
    [Theory]
    [InlineData("""{"userThemeMode":"light"}""")]
    [InlineData("""{"userThemeMode":7}""")]
    [InlineData("{}")]
    public void PreservesKeysRefusesUnlessTheThemeIsTheOneAskedFor(string written)
    {
        var candidate = Candidate(written);
        try
        {
            Assert.False(ClaudeDesktopManager.PreservesKeys("{}", candidate, "dark"));
        }
        finally { File.Delete(candidate); }
    }

    [Fact]
    public void PreservesKeysRefusesWhenEitherSideIsNotAnObject()
    {
        var arrayCandidate = Candidate("[1]");
        var objectCandidate = Candidate("""{"userThemeMode":"dark"}""");
        try
        {
            Assert.False(ClaudeDesktopManager.PreservesKeys("[1]", objectCandidate, "dark"));
            Assert.False(ClaudeDesktopManager.PreservesKeys("{}", arrayCandidate, "dark"));
        }
        finally
        {
            File.Delete(arrayCandidate);
            File.Delete(objectCandidate);
        }
    }

    [Fact]
    public void PreservesKeysRefusesWhenItCannotReadEitherSide()
    {
        Assert.False(ClaudeDesktopManager.PreservesKeys("{}", "cb-\0-bad", "dark"));
        Assert.False(ClaudeDesktopManager.PreservesKeys("not json", "cb-\0-bad", "dark"));
    }

    // ---- LogCandidates ------------------------------------------------

    // The public arm, which asks the machine rather than being told. Both
    // platform shapes are pinned in LastReachableArmsTests, where the platform
    // and the clock are both arguments; this is the one seam where they are
    // not, so all it can honestly assert is that the answer is about this
    // profile and ends where a person can still find something.
    //
    // It used to assert the Default/created split, and the split was the bug —
    // see OnMacOsTheLiveLogDirectoryComesFirstWhereverItIs for what replaced it
    // and why <profile>/Logs stopped being the right first answer.
    [Fact]
    public void LogCandidatesEndAtTheProfileDirectoryItself()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Claude-work");

        var candidates = ClaudeDesktopManager.LogCandidates(directory).ToArray();

        if (OperatingSystem.IsWindows())
        {
            // No split on Windows, and never was: Electron's userData is
            // %APPDATA%\Claude whether or not --user-data-dir was passed.
            Assert.Equal(new[] { Path.Combine(directory, "logs") }, candidates);
            return;
        }

        Assert.Equal(3, candidates.Length);
        Assert.Equal(directory, candidates[^1]);
        Assert.Contains(Path.Combine(directory, "Logs"), candidates);
        Assert.Contains(candidates, path => path.Contains(Path.Combine("Library", "Logs")));
    }

    // ---- NextProfileName ---------------------------------------------

    [Fact]
    public void TheFirstNewProfileIsNumberOne()
    {
        using var scratch = new Scratch();

        Assert.Equal("Claude-Profile-1", ClaudeDesktopManager.NextProfileName(scratch.Root));
    }

    // The numbering reuses a gap rather than climbing forever, which is exactly
    // why OrbweaverSettings.RemoveProfile has to forget a deleted profile's
    // name and colour: otherwise the next Claude-Profile-2 inherits them.
    [Fact]
    public void TheNumberingFillsInAGapRatherThanClimbing()
    {
        using var scratch = new Scratch();
        scratch.Profile("Claude-Profile-1");
        scratch.Profile("Claude-Profile-3");

        Assert.Equal("Claude-Profile-2", ClaudeDesktopManager.NextProfileName(scratch.Root));
    }

    // ---- CheckDelete -------------------------------------------------

    // The default profile is refused outright: that is Claude Desktop's own data
    // directory, not a profile this app invented, and nothing here should be
    // able to throw it away.
    [Fact]
    public void TheDefaultProfileIsNeverDeletable()
    {
        using var scratch = new Scratch();
        var directory = scratch.RealProfile("Claude");

        Assert.Equal(ClaudeDesktopManager.DeleteOutcome.RefusedDefault,
            ClaudeDesktopManager.CheckDelete(View(directory, isDefault: true)));
    }

    // Deleting the directory out from under a live Electron app corrupts what is
    // left rather than removing it, so a running profile is refused and the
    // caller is told to quit it first. Both signals count: the running flag, and
    // a live pid with an instance behind it.
    [Theory]
    [InlineData(true, 0, 0)]
    [InlineData(false, 1, 4321)]
    public void ARunningProfileIsRefused(bool isRunning, int instances, int pid)
    {
        using var scratch = new Scratch();
        var directory = scratch.RealProfile("Claude-live");

        Assert.Equal(ClaudeDesktopManager.DeleteOutcome.RefusedRunning,
            ClaudeDesktopManager.CheckDelete(
                View(directory, isRunning: isRunning, instanceCount: instances, pid: pid)));
    }

    [Fact]
    public void ADirectoryThatIsMissingOrUnnamedIsAFailureRatherThanARefusal()
    {
        using var scratch = new Scratch();

        Assert.Equal(ClaudeDesktopManager.DeleteOutcome.Failed,
            ClaudeDesktopManager.CheckDelete(View(Path.Combine(scratch.Root, "absent"))));

        Assert.Equal(ClaudeDesktopManager.DeleteOutcome.Failed,
            ClaudeDesktopManager.CheckDelete(new ProfileView(
                "", "", false, false, 0, ProfileActivity.None, null, "system")));
    }

    // Never anything but a direct child of the profile root. The path arrives
    // from a snapshot this file built, so this cannot currently be wrong — which
    // is exactly when a guard is cheap and the absence of one is a bet on that
    // staying true.
    [Fact]
    public void SomethingOutsideTheProfileRootIsRefusedEvenIfItExists()
    {
        using var scratch = new Scratch();
        var nested = Path.Combine(scratch.Root, "Claude-work", "Sessions");
        Directory.CreateDirectory(nested);

        Assert.Equal(ClaudeDesktopManager.DeleteOutcome.Failed,
            ClaudeDesktopManager.CheckDelete(View(nested)));
        Assert.Equal(ClaudeDesktopManager.DeleteOutcome.Failed,
            ClaudeDesktopManager.CheckDelete(View(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void AStoppedProfileInTheRightPlaceIsAllowed()
    {
        using var scratch = new Scratch();
        var directory = scratch.RealProfile("Claude-goner");

        Assert.Equal(ClaudeDesktopManager.DeleteOutcome.Deleted,
            ClaudeDesktopManager.CheckDelete(View(directory)));

        // Allowed, and nothing has happened yet: CheckDelete is the answer, not
        // the act.
        Assert.True(Directory.Exists(directory));
    }

    // ---- ForgetProfile ----------------------------------------------

    [Fact]
    public void ForgettingAProfileDropsItsSavedNameAndColour()
    {
        using var scratch = new Scratch();
        var directory = scratch.RealProfile("Claude-forgotten");

        OrbweaverSettings.Update("Claude-forgotten", p =>
        {
            p.Name = "Old name";
            p.Color = "purple";
        });
        Assert.Equal("Old name", OrbweaverSettings.For("Claude-forgotten").Name);

        ClaudeDesktopManager.ForgetProfile(directory);

        // Back to the defaults a never-seen folder gets — null, meaning "no
        // choice made", rather than "Old name" waiting for the next profile that
        // happens to reuse the number.
        Assert.Null(OrbweaverSettings.For("Claude-forgotten").Name);
        Assert.Null(OrbweaverSettings.For("Claude-forgotten").Color);
    }

    // ---- ProcessAlive ----------------------------------------------

    // Quit's Windows path waits a couple of seconds for a close request to be
    // honoured and only then terminates the tree, so a wrong answer here either
    // kills an app that was already quitting cleanly or leaves one running.
    [Fact]
    public void ProcessAliveKnowsThisProcessAndNotAnImaginaryOne()
    {
        Assert.True(ClaudeDesktopManager.ProcessAlive(Environment.ProcessId));

        // Above every real pid on both platforms, and not a valid pid on
        // either — GetProcessById throws, which is the path that has to read as
        // "gone" rather than propagate.
        Assert.False(ClaudeDesktopManager.ProcessAlive(int.MaxValue));
    }
}
