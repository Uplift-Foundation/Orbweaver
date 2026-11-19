using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Orbweaver.Tests;

// CB-339: BrowseRules over a REAL tree on both runners. The unit suite proves the decisions over a
// model; these prove the model is what a real filesystem does: a real symbolic link (a junction on
// Windows) out of a root is refused with a real child listing beside it as the control, names come
// back in the filesystem's own spelling, and a folder the OS will not open is `unreadable`. A
// system-folder list built for this scratch tree stands in for the machine's own, because on a Mac
// the temp directory is under /private, which the real list (rightly) denies.
[Collection("Settings")]
public class BrowseFileSystemTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "cb339-browse-" + Guid.NewGuid());
    private readonly BrowseFileSystem _fs;
    private readonly string _root;               // the scratch tree's canonical path

    public BrowseFileSystemTests()
    {
        Directory.CreateDirectory(_tmp);
        var probe = new BrowseFileSystem(_tmp, [], []);
        _root = BrowseRules.Canonical(probe, _tmp)!;
        _fs = new BrowseFileSystem(_root, [], [Path.Combine(_root, "Source", "system-like")]);
    }

    public void Dispose()
    {
        // A folder this test made unreadable must be readable again before it can be deleted.
        if (!OperatingSystem.IsWindows() && Directory.Exists(Path.Combine(_tmp, "Source", "locked")))
            File.SetUnixFileMode(Path.Combine(_tmp, "Source", "locked"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { _tmp }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Link(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        // A junction needs no privilege, and it is what a Windows user's "out" folder usually is.
        var run = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
        run.WaitForExit();
        Assert.Equal(0, run.ExitCode);
    }

    private BrowseSettings Roots(string folder) => new([Path.Combine(_root, folder)], false);

    private BrowseOutcome Ask(string path, string root = "Source", bool hidden = false) =>
        BrowseRules.Resolve(path, hidden, Roots(root), _fs);

    private string P(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    // ---- links ------------------------------------------------------------------------------

    [Fact]
    public void A_real_link_out_of_a_root_is_refused_with_a_real_child_listed_beside_it_as_the_control()
    {
        Dir("Source", "app");
        Dir("Documents", "secret");
        Link(Path.Combine(_tmp, "Source", "escape"), Path.Combine(_tmp, "Documents"));
        Link(Path.Combine(_tmp, "Source", "inside"), Path.Combine(_tmp, "Source", "app"));

        // The control: the same settings list a real child and follow a link that stays inside.
        Assert.Equal(P("Source", "app"), Ask(P("Source", "app")).Listing!.Path);
        var inside = Ask(P("Source", "inside"));
        Assert.Equal(P("Source", "app"), inside.Listing!.Path);

        // The link out is refused, and so is reaching it by `..`. The folder it points at exists.
        Assert.True(Directory.Exists(P("Documents", "secret")));
        var escape = Ask(P("Source", "escape"));
        Assert.Equal(("forbidden_path", P("Documents")), (escape.Err, escape.Canonical));
        Assert.Equal("forbidden_path", Ask(P("Source", "escape", "secret")).Err);
        Assert.Equal("forbidden_path", Ask(P("Source", "..", "Documents")).Err);

        // The folder's listing offers the real folder and the inside link, never the way out.
        Assert.Equal(["app", "inside"], Ask(P("Source")).Listing!.Items.Select(i => i.Name));
    }

    [Fact]
    public void A_link_that_points_nowhere_or_at_a_file_is_not_a_folder_and_is_not_listed()
    {
        Dir("Source", "app");
        File.WriteAllText(Path.Combine(_tmp, "Source", "readme.txt"), "x");
        if (OperatingSystem.IsWindows())
        {
            // A junction to a missing folder cannot be made; a file symlink needs a privilege a
            // runner may not have, so the Windows leg checks the plain file and the control.
            Assert.Equal(["app"], Ask(P("Source")).Listing!.Items.Select(i => i.Name));
            return;
        }
        File.CreateSymbolicLink(Path.Combine(_tmp, "Source", "to-file"), Path.Combine(_tmp, "Source", "readme.txt"));
        Directory.CreateSymbolicLink(Path.Combine(_tmp, "Source", "dangling"), Path.Combine(_tmp, "Source", "gone"));
        Assert.Equal(["app"], Ask(P("Source")).Listing!.Items.Select(i => i.Name));
        Assert.Equal("unknown_path", Ask(P("Source", "dangling")).Err);                       // inside the root, not a folder
    }

    [Fact]
    public void The_temp_directory_is_reached_through_the_link_a_mac_really_has_and_resolves_to_its_real_path()
    {
        // The positive control that real link resolution ran: on a Mac $TMPDIR is /var/folders, and
        // /var is a link to /private/var, so the canonical form is not the path that was typed.
        if (!OperatingSystem.IsMacOS()) return;
        Assert.StartsWith("/var/", _tmp);
        Assert.StartsWith("/private/var/", _root);
    }

    // ---- spelling and names -----------------------------------------------------------------

    [Fact]
    public void A_request_in_another_case_arrives_at_the_stored_spelling_on_a_volume_that_ignores_case()
    {
        Dir("Source", "MixedCase");
        var wrong = P("source", "mixedcase");
        if (!Directory.Exists(wrong)) return;                     // a case-sensitive volume: nothing to prove
        var outcome = Ask(wrong);
        Assert.Equal(P("Source", "MixedCase"), outcome.Canonical);
        Assert.NotNull(outcome.Listing);
    }

    [Fact]
    public void A_name_with_wildcard_characters_is_found_by_that_name_and_not_by_a_pattern_that_would_match_it()
    {
        if (OperatingSystem.IsWindows()) return;                  // * and ? are not legal in a Windows name
        Dir("Source", "a*b");
        Dir("Source", "axb");
        Assert.Equal("a*b", _fs.Lookup(P("Source"), "a*b")!.Value.Name);
        Assert.Null(_fs.Lookup(P("Source"), "a?c"));
        Assert.Equal("axb", _fs.Lookup(P("Source"), "axb")!.Value.Name);
    }

    [Fact]
    public void A_missing_name_and_a_missing_parent_are_null_not_an_exception()
    {
        Dir("Source");
        Assert.Null(_fs.Lookup(P("Source"), "nope"));
        Assert.Null(_fs.Lookup(P("Source", "no-such-parent"), "x"));
        Assert.False(_fs.DirectoryExists(P("Source", "nope")));
        Assert.True(_fs.DirectoryExists(P("Source")));
    }

    [Fact]
    public void A_file_is_not_a_folder_to_DirectoryExists_or_to_a_listing()
    {
        Dir("Source");
        File.WriteAllText(Path.Combine(_tmp, "Source", "readme.txt"), "x");
        Assert.False(_fs.DirectoryExists(P("Source", "readme.txt")));
        Assert.Equal("unknown_path", Ask(P("Source", "readme.txt")).Err);
    }

    [Fact]
    public void A_windows_name_the_os_would_trim_is_refused_on_a_real_volume_too()
    {
        if (!OperatingSystem.IsWindows()) return;
        Dir("Source", "app");
        foreach (var slippery in new[] { "app.", "app ", ".. " })
            Assert.Equal("forbidden_path", Ask(P("Source") + "\\" + slippery).Err);
        Assert.NotNull(Ask(P("Source", "app")).Listing);          // the control
    }

    [Fact]
    public void A_windows_8_3_short_name_arrives_at_the_long_name()
    {
        if (!OperatingSystem.IsWindows()) return;
        var longName = Dir("Source", "A Long Folder Name");
        var shortName = ShortName(longName);
        if (shortName is null || shortName.Equals(Path.GetFileName(longName), StringComparison.OrdinalIgnoreCase)) return;   // 8.3 names switched off on this volume
        var outcome = Ask(Path.Combine(P("Source"), shortName));
        Assert.Equal(P("Source", "A Long Folder Name"), outcome.Canonical);
        Assert.NotNull(outcome.Listing);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, char[] buffer, uint size);

    private static string? ShortName(string longPath)
    {
        var buffer = new char[520];
        var n = GetShortPathName(longPath, buffer, (uint)buffer.Length);
        return n == 0 ? null : Path.GetFileName(new string(buffer, 0, (int)n));
    }

    // ---- hidden, markers, caps, unreadable --------------------------------------------------

    [Fact]
    public void A_dot_folder_is_hidden_until_asked_for_and_the_windows_attribute_hides_a_plain_name_too()
    {
        Dir("Source", "visible");
        Dir("Source", ".dotted");
        var attr = Dir("Source", "attr");
        if (OperatingSystem.IsWindows()) File.SetAttributes(attr, FileAttributes.Directory | FileAttributes.Hidden);

        var plain = Ask(P("Source")).Listing!.Items.Select(i => i.Name).ToList();
        var all = Ask(P("Source"), hidden: true).Listing!.Items.ToList();
        Assert.Contains("visible", plain);
        Assert.DoesNotContain(".dotted", plain);
        Assert.Contains(all, i => i.Name == ".dotted" && i.Hidden);
        Assert.Equal(OperatingSystem.IsWindows(), all.Single(i => i.Name == "attr").Hidden);
        Assert.Equal(OperatingSystem.IsWindows(), !plain.Contains("attr"));
        Assert.False(_fs.HiddenAttribute(P("Source"), "visible"));
        Assert.False(_fs.HiddenAttribute(P("Source"), "no-such-folder"));
    }

    [Fact]
    public void The_three_markers_are_read_off_the_real_folders()
    {
        Dir("Source", "plain");
        var repo = Dir("Source", "repo");
        Dir("Source", "repo", ".git");
        File.WriteAllText(Path.Combine(repo, "CLAUDE.md"), "Build with dotnet build.");
        var worktree = Dir("Source", "worktree");
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: elsewhere");
        var persona = Dir("Source", "persona");
        File.WriteAllText(Path.Combine(persona, "CLAUDE.md"), "Her name is Leota");

        var items = Ask(P("Source")).Listing!.Items.ToDictionary(i => i.Name);
        Assert.Equal((false, false, false), (items["plain"].Git, items["plain"].Claude, items["plain"].Persona));
        Assert.Equal((true, true, false), (items["repo"].Git, items["repo"].Claude, items["repo"].Persona));
        Assert.Equal((true, false, false), (items["worktree"].Git, items["worktree"].Claude, items["worktree"].Persona));
        Assert.Equal((false, true, true), (items["persona"].Git, items["persona"].Claude, items["persona"].Persona));
    }

    [Fact]
    public void An_enumeration_stops_at_the_cap_and_says_there_was_more()
    {
        for (var i = 0; i < 5; i++) Dir("Source", "many", "d" + i);
        var (some, more) = _fs.Enumerate(P("Source", "many"), 3)!.Value;
        Assert.Equal((3, true), (some.Count, more));
        var (all, none) = _fs.Enumerate(P("Source", "many"), 5)!.Value;
        Assert.Equal((5, false), (all.Count, none));
        Assert.Null(_fs.Enumerate(P("Source", "no-such-folder"), 5));
    }

    [Fact]
    public void A_folder_the_os_will_not_open_is_unreadable_and_still_listed_in_its_parent_with_no_markers()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;     // an ACL or root cannot be made to refuse here
        var locked = Dir("Source", "locked");
        Dir("Source", "locked", "inner");
        File.SetUnixFileMode(locked, UnixFileMode.None);

        var inParent = Ask(P("Source")).Listing!.Items.Single(i => i.Name == "locked");
        Assert.Equal((false, false, false), (inParent.Git, inParent.Claude, inParent.Persona));
        Assert.Equal("unreadable", Ask(P("Source", "locked")).Err);

        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Equal(["inner"], Ask(P("Source", "locked")).Listing!.Items.Select(i => i.Name));   // the control
    }

    [Fact]
    public void A_system_folder_inside_a_root_is_never_listed_or_entered()
    {
        Dir("Source", "app");
        Dir("Source", "system-like", "deep");
        Assert.Equal(["app"], Ask(P("Source")).Listing!.Items.Select(i => i.Name));
        Assert.Equal("forbidden_path", Ask(P("Source", "system-like")).Err);
        Assert.Equal("forbidden_path", Ask(P("Source", "system-like", "deep")).Err);
    }

    // ---- the machine's own defaults ---------------------------------------------------------

    [Fact]
    public void The_default_filesystem_denies_this_machines_own_system_folders_and_roots_exist()
    {
        var real = new BrowseFileSystem();
        Assert.Equal(Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), real.Home);
        Assert.Equal(OperatingSystem.IsWindows(), real.IsWindows);
        Assert.True(real.CaseInsensitive);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(Environment.GetFolderPath(Environment.SpecialFolder.Windows), real.DeniedTrees);
            Assert.True(real.DirectoryExists(Path.GetPathRoot(Environment.SystemDirectory)!));
        }
        else
        {
            Assert.Contains("/System", real.DeniedTrees);
            Assert.Contains("/", real.DeniedExact);
            Assert.True(real.DirectoryExists("/"));
            Assert.True(real.DirectoryExists("/Users") || real.DirectoryExists("/home"));
        }
        Assert.Equal("forbidden_path", BrowseRules.Resolve(OperatingSystem.IsWindows() ? Environment.SystemDirectory : "/System", true,
            new BrowseSettings([], true), real).Err);
    }
}
