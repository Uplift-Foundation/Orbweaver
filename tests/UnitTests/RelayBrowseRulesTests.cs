using System.Text;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-339: the rules the vectors do not reach. The vectors replay the contract's cases; these are
/// the arms the contract leaves to the desk (a link that points nowhere sensible, a name Windows
/// reads as something else, a frame that is exactly full) and the refusals Settings gives. Each
/// refusal sits beside the control that lists under the same settings, because a refusal with
/// nothing beside it proves only that the desk refuses.
/// </summary>
public class RelayBrowseRulesTests
{
    private static FakeBrowseFileSystem Mac() => new("/Users/dev", false, ["/", "/Users", "/Volumes"],
        ["/System", "/private", "/Users/dev/Library", "/Users/dev/.ssh"]);

    private static FakeBrowseFileSystem Win() => new(@"C:\Users\dev", true, [@"C:\Users"],
        [@"C:\Windows", @"C:\Users\dev\AppData"]);

    private static BrowseSettings Roots(params string[] roots) => new(roots, false);

    private static FakeBrowseFileSystem MacTree() => Mac()
        .Add("/").Add("/Users").Add("/Users/dev").Add("/Users/dev/Source").Add("/Users/dev/Source/app")
        .Add("/Users/dev/Documents").Add("/System").Add("/Users/dev/Library");

    // ---- canonical form, the arms the vectors leave ---------------------------------------

    [Fact]
    public void A_relative_link_resolves_against_the_folder_that_holds_it_and_a_chain_is_followed()
    {
        var fs = MacTree()
            .Add("/Users/dev/Source/rel", BrowseKind.Link, target: "app")
            .Add("/Users/dev/Source/up", BrowseKind.Link, target: "../Documents")
            .Add("/Users/dev/Source/hop", BrowseKind.Link, target: "rel");
        Assert.Equal("/Users/dev/Source/app", BrowseRules.Canonical(fs, "/Users/dev/Source/rel"));
        Assert.Equal("/Users/dev/Source/app", BrowseRules.Canonical(fs, "/Users/dev/Source/hop"));
        Assert.Equal("/Users/dev/Documents", BrowseRules.Canonical(fs, "/Users/dev/Source/up"));
        // The control: neither link is a way out of the roots, because the check is on where they end.
        var listed = BrowseRules.Resolve("/Users/dev/Source/rel", false, Roots("/Users/dev/Source"), fs);
        Assert.Equal("/Users/dev/Source/app", listed.Listing!.Path);
        Assert.Equal("forbidden_path", BrowseRules.Resolve("/Users/dev/Source/up", false, Roots("/Users/dev/Source"), fs).Err);
    }

    [Fact]
    public void More_than_forty_link_replacements_is_forbidden_even_when_the_chain_ends()
    {
        var fs = MacTree().Add("/Users/dev/Source/l0", BrowseKind.Link, target: "app");
        for (var i = 1; i <= 41; i++) fs.Add($"/Users/dev/Source/l{i}", BrowseKind.Link, target: $"l{i - 1}");

        Assert.Equal("/Users/dev/Source/app", BrowseRules.Canonical(fs, "/Users/dev/Source/l39"));       // 40 replacements
        Assert.Null(BrowseRules.Canonical(fs, "/Users/dev/Source/l40"));                                  // 41
        Assert.Equal("forbidden_path", BrowseRules.Resolve("/Users/dev/Source/l41", false, Roots("/Users/dev/Source"), fs).Err);
    }

    [Fact]
    public void A_link_with_no_target_or_an_unusable_windows_target_is_forbidden_not_followed()
    {
        var mac = MacTree().Add("/Users/dev/Source/empty", BrowseKind.Link, target: "");
        Assert.Null(BrowseRules.Canonical(mac, "/Users/dev/Source/empty"));

        var win = Win().Add(@"C:\").Add(@"C:\Users").Add(@"C:\Users\dev").Add(@"C:\Users\dev\p").Add(@"C:\Users\dev\p\ok")
            .Add(@"C:\Users\dev\p\unc", BrowseKind.Link, target: @"\\server\share")
            .Add(@"C:\Users\dev\p\rooted", BrowseKind.Link, target: @"\Windows")
            .Add(@"C:\Users\dev\p\drive", BrowseKind.Link, target: "D:name")
            .Add(@"C:\Users\dev\p\sibling", BrowseKind.Link, target: "ok");
        foreach (var link in new[] { "unc", "rooted", "drive" })
            Assert.Null(BrowseRules.Canonical(win, @"C:\Users\dev\p\" + link));
        Assert.Equal(@"C:\Users\dev\p\ok", BrowseRules.Canonical(win, @"C:\Users\dev\p\sibling"));      // the control
    }

    [Theory]
    [InlineData(@"C:\Users\dev\p\.. ")]
    [InlineData(@"C:\Users\dev\p\ok.")]
    [InlineData(@"C:\Users\dev\p\ok ")]
    [InlineData(@"C:\Users\dev\p\a<b")]
    [InlineData(@"C:\Users\dev\p\a|b")]
    [InlineData(@"C:\Users\dev\p\a*b")]
    public void A_windows_name_the_os_would_read_as_something_else_is_forbidden(string path)
    {
        // `C:\root\.. ` is `C:\root\..` to every Windows API but a string comparison: a lexical
        // "inside the root" would list the root's parent.
        var fs = Win().Add(@"C:\").Add(@"C:\Users").Add(@"C:\Users\dev").Add(@"C:\Users\dev\p").Add(@"C:\Users\dev\p\ok");
        Assert.Equal("forbidden_path", BrowseRules.Resolve(path, false, Roots(@"C:\Users\dev\p"), fs).Err);
        Assert.NotNull(BrowseRules.Resolve(@"C:\Users\dev\p\ok", false, Roots(@"C:\Users\dev\p"), fs).Listing);   // control
    }

    [Theory]
    [InlineData(@"C:\PROGRA~1")]
    [InlineData(@"C:\PROGRA~1\Common Files")]
    [InlineData(@"C:\progra~2\x")]
    [InlineData(@"C:\Users\dev\p\LONGFI~1.TXT")]
    public void A_windows_short_name_the_filesystem_could_not_resolve_is_refused_not_kept_as_written(string path)
    {
        // QA round 1: kept as written, `C:\PROGRA~1` matched neither the denied `C:\Program Files` nor
        // any root check, and the OS opened it. With a drive root as a project root it listed.
        var fs = Win().Add(@"C:\").Add(@"C:\Windows").Add(@"C:\Program Files").Add(@"C:\Program Files\Common Files")
            .Add(@"C:\Users").Add(@"C:\Users\dev").Add(@"C:\Users\dev\p");
        Assert.Equal("forbidden_path", BrowseRules.Resolve(path, true, Roots(@"C:\"), fs).Err);
        // The controls: the long spelling is refused as a system folder by the list, an ordinary tilde
        // name (no digit after it) is an ordinary name, and a short name the filesystem DOES resolve
        // arrives at the long one.
        var denyPrograms = new FakeBrowseFileSystem(@"C:\Users\dev", true, [@"C:\Users"], [@"C:\Windows", @"C:\Program Files"])
            .Add(@"C:\").Add(@"C:\Program Files", alias: "PROGRA~1").Add(@"C:\tilde~a");
        Assert.Equal("forbidden_path", BrowseRules.Resolve(@"C:\Program Files", true, Roots(@"C:\"), denyPrograms).Err);
        Assert.Equal("forbidden_path", BrowseRules.Resolve(@"C:\PROGRA~1", true, Roots(@"C:\"), denyPrograms).Err);
        Assert.Equal(@"C:\Program Files", BrowseRules.Resolve(@"C:\PROGRA~1", true, Roots(@"C:\"), denyPrograms).Canonical);
        Assert.NotNull(BrowseRules.Resolve(@"C:\tilde~a", true, Roots(@"C:\"), denyPrograms).Listing);
    }

    [Fact]
    public void On_a_mac_a_trailing_dot_or_space_is_an_ordinary_name()
    {
        var fs = MacTree().Add("/Users/dev/Source/odd. ");
        Assert.NotNull(BrowseRules.Resolve("/Users/dev/Source/odd. ", false, Roots("/Users/dev/Source"), fs).Listing);
    }

    // ---- listing arms ---------------------------------------------------------------------

    [Fact]
    public void A_name_that_cannot_be_written_as_json_is_skipped_and_a_normal_one_beside_it_is_listed()
    {
        var fs = MacTree().Add("/Users/dev/Source/ok").Add("/Users/dev/Source/bad\ud800name");
        var names = BrowseRules.Resolve("/Users/dev/Source", false, Roots("/Users/dev/Source"), fs).Listing!.Items.Select(i => i.Name);
        Assert.Equal(["app", "ok"], names);
        Assert.Null(BrowseRules.Utf8("bad\ud800name"));
        Assert.NotNull(BrowseRules.Utf8("fine"));
    }

    [Fact]
    public void A_folder_the_desk_stopped_reading_at_the_scan_cap_says_truncated()
    {
        var fs = MacTree();
        for (var i = 0; i < BrowseLimits.MaxScan + 3; i++) fs.Add($"/Users/dev/Source/app/d{i:D5}");
        var listing = BrowseRules.Resolve("/Users/dev/Source/app", false, Roots("/Users/dev/Source"), fs).Listing!;
        Assert.True(listing.Truncated);
        Assert.Equal(BrowseLimits.MaxEntries, listing.Items.Count);
        Assert.Equal("d00000", listing.Items[0].Name);

        // The control: exactly at the cap is not truncated by the scan, and 500 entries are not truncated.
        var small = MacTree();
        for (var i = 0; i < BrowseLimits.MaxEntries; i++) small.Add($"/Users/dev/Source/app/d{i:D3}");
        Assert.False(BrowseRules.Resolve("/Users/dev/Source/app", false, Roots("/Users/dev/Source"), small).Listing!.Truncated);
    }

    [Fact]
    public void A_read_that_throws_reaches_the_caller_so_the_handler_can_say_unreadable()
    {
        var fs = MacTree();
        fs.OnRead = _ => new IOException("boom");
        Assert.Throws<IOException>(() => BrowseRules.Resolve("/Users/dev/Source", false, Roots("/Users/dev/Source"), fs));
    }

    [Fact]
    public void The_top_level_with_no_roots_and_the_toggle_off_is_an_empty_listing_not_an_error()
    {
        var listing = BrowseRules.Resolve("", false, Roots(), MacTree()).Listing!;
        Assert.Equal(("", "", ""), (listing.Path, listing.Parent, listing.Root));
        Assert.Empty(listing.Items);
    }

    [Fact]
    public void With_the_toggle_on_the_home_folder_is_the_outermost_root_and_a_project_root_inside_it_is_listed_separately()
    {
        var fs = MacTree().Add("/Users/dev/Source/app/src");
        var settings = new BrowseSettings(["/Users/dev/Source", "~"], true);                 // `~` as a typed root: the home entry
        var top = BrowseRules.Resolve("", false, settings, fs).Listing!;
        Assert.Equal(["/Users/dev", "/Users/dev/Source"], top.Items.Select(i => i.Path));
        Assert.True(top.Items[0].Home);

        var inner = BrowseRules.Resolve("/Users/dev/Source/app/src", false, settings, fs).Listing!;
        Assert.Equal(("/Users/dev", "/Users/dev/Source/app"), (inner.Root, inner.Parent));
    }

    [Fact]
    public void A_project_root_over_the_cap_is_not_kept_and_a_relative_one_is_ignored()
    {
        var fs = MacTree();
        var roots = new List<string> { "relative/path" };
        for (var i = 0; i < BrowseLimits.MaxRoots + 5; i++)
        {
            fs.Add($"/Users/dev/Source/r{i:D3}");
            roots.Add($"/Users/dev/Source/r{i:D3}");
        }
        var top = BrowseRules.Resolve("", false, new BrowseSettings(roots, false), fs).Listing!;
        Assert.Equal(BrowseLimits.MaxRoots, top.Items.Count);
        Assert.DoesNotContain(top.Items, i => i.Name == "path");
    }

    // ---- automatic roots ------------------------------------------------------------------

    [Fact]
    public void Automatic_roots_skip_what_is_not_a_path_cap_at_the_limit_and_treat_case_as_the_filesystem_does()
    {
        var fs = MacTree();
        var recent = new List<string> { "", "not/absolute", "/Users/dev/Source/App", "/users/DEV/source/app2" };
        Assert.Equal(["/Users/dev/Source"], BrowseRules.DefaultRoots(recent, fs));      // one root, however it was spelled

        var many = Enumerable.Range(0, BrowseLimits.MaxRoots + 10).Select(i => $"/Volumes/v{i:D3}/project").ToList();
        Assert.Equal(BrowseLimits.MaxRoots, BrowseRules.DefaultRoots(many, fs).Count);
    }

    [Fact]
    public void The_effective_settings_are_the_users_list_when_there_is_one_and_the_automatic_roots_when_not()
    {
        var fs = MacTree();
        var automatic = BrowseRules.Effective(null, false, ["/Users/dev/Source/app"], ["/Volumes/Ext/p/x"], fs);
        Assert.Equal(["/Users/dev/Source", "/Volumes/Ext/p"], automatic.Roots);
        var own = BrowseRules.Effective([], true, ["/Users/dev/Source/app"], [], fs);
        Assert.Equal(([], true), (own.Roots, own.Home));                                  // an explicit empty list stays empty
    }

    // ---- what Settings refuses ------------------------------------------------------------

    [Fact]
    public void Settings_refuses_a_root_with_the_reason_and_accepts_the_control()
    {
        var fs = MacTree().Add("/Users/dev/Source/file.txt", BrowseKind.File);
        string? Say(string path, params string[] existing) => BrowseRules.Refusal(path, existing, fs);

        Assert.Null(Say("/Users/dev/Source"));                                                      // the control
        Assert.Null(Say("~/Source"));
        Assert.Contains("path", Say("Source")!);
        Assert.Contains("doesn't exist", Say("/Users/dev/gone")!);
        Assert.Contains("doesn't exist", Say("/Users/dev/Source/file.txt")!);
        Assert.Contains("home folder", Say("~")!);
        Assert.Contains("home folder", Say("/Users/dev")!);
        Assert.Contains("system folder", Say("/System")!);
        Assert.Contains("system folder", Say("/Users/dev/Library")!);
        Assert.Contains("already in the list", Say("/users/DEV/source", "/Users/dev/Source")!);
        Assert.Contains("already in the list", Say("/Users/dev/Source", "~/Source")!);
        Assert.Contains("at most 64", Say("/Users/dev/Source", Enumerable.Range(0, 64).Select(i => "/x" + i).ToArray())!);
    }

    [Fact]
    public void A_windows_drive_that_holds_home_is_refused_and_one_that_does_not_is_not()
    {
        var fs = Win().Add(@"C:\").Add(@"C:\Users").Add(@"C:\Users\dev").Add(@"D:\").Add(@"D:\work");
        Assert.Contains("home folder", BrowseRules.Refusal(@"C:\", [], fs)!);
        Assert.Null(BrowseRules.Refusal(@"D:\", [], fs));
        Assert.Null(BrowseRules.Refusal(@"d:/work", [], fs));
    }

    [Fact]
    public void A_stored_root_is_the_canonical_spelling_and_an_unusable_one_is_stored_as_typed()
    {
        var fs = MacTree();
        Assert.Equal("/Users/dev/Source", BrowseRules.StoredRoot(" ~/source/ ", fs));
        Assert.Equal("nonsense", BrowseRules.StoredRoot(" nonsense ", fs));
    }

    // ---- shape, sort, chunking ------------------------------------------------------------

    [Fact]
    public void A_path_of_exactly_the_cap_is_a_shape_and_one_byte_more_is_not()
    {
        Assert.True(BrowseRules.PathShapeOk("/" + new string('a', BrowseLimits.MaxPathBytes - 1)));
        Assert.False(BrowseRules.PathShapeOk("/" + new string('a', BrowseLimits.MaxPathBytes)));
        Assert.False(BrowseRules.PathShapeOk("/" + new string('é', BrowseLimits.MaxPathBytes / 2)));   // the cap is bytes
        Assert.False(BrowseRules.PathShapeOk("/a\u0001"));
        Assert.False(BrowseRules.PathShapeOk("/a\u007f"));
        Assert.True(BrowseRules.PathShapeOk(""));
    }

    [Fact]
    public void Names_equal_under_the_fold_order_by_their_unfolded_bytes_and_a_prefix_sorts_first()
    {
        Assert.True(BrowseRules.Compare("Apple", "apple") < 0);
        Assert.True(BrowseRules.Compare("apple", "Apple") > 0);
        Assert.True(BrowseRules.Compare("ab", "abc") < 0);
        Assert.Equal(0, BrowseRules.Compare("same", "same"));
        // An unpaired surrogate still orders (by its replacement bytes) rather than throwing.
        Assert.NotEqual(0, BrowseRules.Compare("a\ud800", "a"));
    }

    [Fact]
    public void Every_chunk_is_within_the_cap_and_one_more_entry_would_not_have_fit()
    {
        var items = Enumerable.Range(0, 400).Select(i => new BrowseItem(new string('n', 150) + i.ToString("D3"), Git: i % 2 == 0)).ToList();
        var listing = new BrowseListing("/p", "", "/p", items, true);
        var frames = BrowseFrames.Dir("01REF", listing);
        Assert.True(frames.Count > 1);
        var from = 0;
        foreach (var frame in frames)
        {
            var doc = System.Text.Json.JsonDocument.Parse(frame).RootElement;
            Assert.Equal(from, doc.GetProperty("from_entry").GetInt32());
            Assert.True(Encoding.UTF8.GetByteCount(frame) <= BrowseLimits.MaxDirPlaintextBytes);
            Assert.True(doc.GetProperty("truncated").GetBoolean());
            from += doc.GetProperty("entries").GetArrayLength();
        }
        Assert.Equal(items.Count, from);

        // Maximal: the first frame plus the next entry would have been over the cap.
        var first = System.Text.Json.JsonDocument.Parse(frames[0]).RootElement;
        var withOneMore = Encoding.UTF8.GetByteCount(frames[0]) + 1 + 180;
        Assert.True(withOneMore > BrowseLimits.MaxDirPlaintextBytes, first.GetProperty("entries").GetArrayLength().ToString());
    }

    [Fact]
    public void An_empty_listing_is_one_frame_with_a_zero_total_and_no_truncated_key()
    {
        var frame = Assert.Single(BrowseFrames.Dir("R", new BrowseListing("/p", "", "/p", [], false)));
        Assert.Equal("""{"t":"dir","ref":"R","path":"/p","parent":"","root":"/p","entries_total":0,"from_entry":0,"entries":[]}""", frame);
    }

    [Fact]
    public void An_entrys_flags_are_present_only_when_true_and_in_the_contracts_order()
    {
        var item = new BrowseItem("n", "/p/n", "C:", true, true, true, true, true);
        var frame = Assert.Single(BrowseFrames.Dir("R", new BrowseListing("", "", "", [item], false)));
        Assert.Contains("""{"name":"n","path":"/p/n","drive":"C:","home":true,"git":true,"claude":true,"persona":true,"hidden":true}""", frame);
    }

    [Fact]
    public void The_limiter_forgets_nothing_between_phones_and_never_goes_backwards_in_time()
    {
        var limiter = new BrowseRateLimiter();
        for (var i = 0; i < BrowseLimits.RateBurst; i++) Assert.True(limiter.TryAccept("a", 1000));
        Assert.False(limiter.TryAccept("a", 1000));
        Assert.True(limiter.TryAccept("b", 1000));                       // another phone has its own bucket
        Assert.False(limiter.TryAccept("a", 500));                       // a clock that stepped back gives no credit
    }
}

public class BrowseSettingsSourceTests
{
    [Fact]
    public void Only_local_cli_sessions_with_a_working_folder_feed_the_automatic_roots()
    {
        var sessions = new[]
        {
            new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = "/w/a" },
            new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = "  " },
            new SessionStatus { Source = SessionSource.ClaudeCode, Cwd = null },
            new SessionStatus { Source = SessionSource.OpenClaw, Cwd = "/w/openclaw" },
            new SessionStatus { Source = SessionSource.ClaudeCloud, Cwd = "/w/cloud" },
        };
        Assert.Equal(["/w/a"], BrowseSettingsSource.LiveFolders(sessions));
    }
}
