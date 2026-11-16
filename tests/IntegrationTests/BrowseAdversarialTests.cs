using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-339 QA: the folder-picker path rules attacked against a REAL temp tree (BrowseFileSystem over a temp "home"), not the fake the
/// vectors use. Every refusal is paired with a positive control that a real child of the same root is listed through the same code
/// path, so a refusal can't be an empty tree or a broken setup. Written against the API sent to the desktop engineer in QA's message:
/// BrowseRules.Resolve(path, hidden, BrowseSettings, recentCwds, IBrowseFileSystem), BrowseChunker.Chunk, BrowseRateLimiter,
/// new BrowseFileSystem(home).
/// </summary>
public sealed class BrowseAdversarialTests : IDisposable
{
    private sealed class QaWindowsFact : FactAttribute { public QaWindowsFact() { if (!OperatingSystem.IsWindows()) Skip = "needs NTFS: junctions, 8.3 short names and case-insensitive spelling only exist on the Windows leg"; } }
    private sealed class QaUnixFact : FactAttribute { public QaUnixFact() { if (OperatingSystem.IsWindows()) Skip = "needs POSIX permissions (chmod 000)"; } }

    // macOS: the temp dir lives under /private/var, which IS on the desk's system-folder list (/private, /var, /tmp), so a tree there is correctly
    // refused and tests nothing. Build it under the user's profile instead (the desk is given the tree's own "home", so ~/Library etc. don't apply).
    private static string TestBase() => OperatingSystem.IsWindows() ? Path.GetTempPath() : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly string _base = Path.Combine(TestBase(), "cb339-browse-qa-" + Guid.NewGuid().ToString("N"));
    private readonly string _home, _src, _outside;
    private readonly BrowseFileSystem _fs;

    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public BrowseAdversarialTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _out = output;
        _home = Path.Combine(_base, "home"); _src = Path.Combine(_home, "Source"); _outside = Path.Combine(_base, "outside");
        foreach (var d in new[] { "proj-a/.git", "proj-b", ".hidden-dir", "deep/er" }) Directory.CreateDirectory(Path.Combine(_src, d));
        File.WriteAllText(Path.Combine(_src, "proj-b", "CLAUDE.md"), "x");
        foreach (var d in new[] { "Documents", "Desktop", "Library", ".ssh", "AppData", "notes" }) Directory.CreateDirectory(Path.Combine(_home, d));
        Directory.CreateDirectory(Path.Combine(_outside, "secret-child"));
        _fs = new BrowseFileSystem(_home);
    }

    public void Dispose() { try { Directory.Delete(_base, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private BrowseSettings Roots(params string[] roots) => new(roots, Home: false);
    private BrowseSettings HomeOn => new([_src], Home: true);

    private BrowseOutcome Go(string path, BrowseSettings s, bool hidden = false) => BrowseRules.Resolve(path, hidden, s, _fs);
    private static string[] Names(BrowseOutcome o) => (o.Listing ?? throw new Xunit.Sdk.XunitException("expected a listing, got " + o.Err)).Items.Select(e => e.Name).ToArray();
    private static string Code(BrowseOutcome o) => o.Err ?? throw new Xunit.Sdk.XunitException("expected a refusal, got a listing of " + o.Listing!.Path);
    private static BrowseListing Lst(BrowseOutcome o) => o.Listing ?? throw new Xunit.Sdk.XunitException("expected a listing, got " + o.Err);
    private static void IsListing(BrowseOutcome o) => Assert.True(o.Listing is not null, "expected a listing, got " + o.Err);

    /// <summary>The positive control every refusal test calls: the same settings, a real child, listed.</summary>
    private void Control(BrowseSettings s, string? path = null)
    {
        var o = Go(path ?? _src, s);
        IsListing(o);
        Assert.Contains("proj-a", Names(o));
        Assert.Contains("proj-b", Names(o));
    }

    // ---- positive baseline -------------------------------------------------------------------------------------

    [Fact]
    public void A_real_root_lists_its_real_children_with_markers_and_hides_dot_folders_until_asked()
    {
        var s = Roots(_src);
        var o = Go(_src, s);
        Assert.Equal(["deep", "proj-a", "proj-b"], Names(o).Order().ToArray());
        Assert.True(Lst(o).Items.Single(e => e.Name == "proj-a").Git);
        Assert.True(Lst(o).Items.Single(e => e.Name == "proj-b").Claude);
        Assert.DoesNotContain(".hidden-dir", Names(o));
        Assert.Contains(".hidden-dir", Names(Go(_src, s, hidden: true)));      // control for the hide: it is only hidden, not missing
        Assert.Equal(["er"], Names(Go(Path.Combine(_src, "deep"), s)));        // drilling in works
    }

    // ---- traversal ---------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> TraversalForms() => new[]
    {
        "{src}/../Documents", "{src}/proj-a/../../Documents", "{src}/deep/er/../../../Documents", "{src}/%2e%2e/Documents", "{src}/%2E%2E/Documents",
        "{src}/..%2fDocuments", "{src}/%2e%2e%2fDocuments", "{src}/..\\Documents", "{src}/./../Documents", "{src}//..//Documents", "{src}/proj-a/..;/..",
        "{src}/..%00/Documents", "{src}/\u202E../Documents", "{src}/..%c0%af/Documents", "{src}/%252e%252e/Documents", "~/../outside", "{src}/../../outside",
    }.Select(p => new object[] { p });

    [Theory, MemberData(nameof(TraversalForms))]
    public void Traversal_in_any_spelling_never_lists_a_folder_outside_the_root(string template)
    {
        var s = Roots(_src);
        var path = template.Replace("{src}", _src);
        var o = Go(path, s);
        if (o.Listing is { } l)   // a listing is only acceptable if it is the root itself or a child of it, never a sibling of the root
            Assert.StartsWith(Path.GetFullPath(_src), Path.GetFullPath(l.Path), StringComparison.Ordinal);
        else Assert.Contains(Code(o), new[] { "forbidden_path", "unknown_path", "bad_frame" });
        Assert.DoesNotContain("Documents", o.Listing is null ? [] : Names(o));
        Control(s);
    }

    [Fact]
    public void An_escape_that_lands_on_a_real_outside_folder_and_one_that_lands_on_nothing_get_the_same_refusal()
    {
        var s = Roots(_src);
        var real = Go(Path.Combine(_src, "..", "Documents"), s);
        var missing = Go(Path.Combine(_src, "..", "no-such-folder-at-all"), s);
        var farMissing = Go(Path.Combine(_base, "never", "existed"), s);
        var farReal = Go(_outside, s);
        Assert.All(new[] { real, missing, farMissing, farReal }, o => Assert.Equal("forbidden_path", Code(o)));   // one answer: no oracle for mapping the disk
        Assert.Equal("unknown_path", Code(Go(Path.Combine(_src, "no-such-child"), s)));                          // control: INSIDE the root a missing folder is unknown_path
        Control(s);
    }

    [Fact]
    public void A_path_with_a_prefix_that_merely_looks_like_the_root_is_outside_it()
    {
        var sibling = _src + "-evil"; Directory.CreateDirectory(Path.Combine(sibling, "x"));
        var s = Roots(_src);
        Assert.Equal("forbidden_path", Code(Go(sibling, s)));    // "Source-evil" starts with "Source" as a string
        Assert.Equal("forbidden_path", Code(Go(Path.Combine(sibling, "x"), s)));
        Control(s);
    }

    // ---- symlinks ----------------------------------------------------------------------------------------------

    private bool TryLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
    }

    [Fact]
    public void A_symlink_inside_a_root_that_points_outside_is_refused_directly_and_through_a_chain()
    {
        var out1 = Path.Combine(_src, "link-out"); var out2 = Path.Combine(_src, "deep", "link-chain"); var inside = Path.Combine(_src, "link-in");
        if (!TryLink(out1, _outside)) { Assert.True(OperatingSystem.IsWindows(), "symlink creation failed on a POSIX runner, which is a setup bug"); return; }   // Windows without Developer Mode can't make one: the junction test covers that leg
        Assert.True(TryLink(out2, out1));                      // a link to a link
        Assert.True(TryLink(inside, Path.Combine(_src, "proj-a")));
        var s = Roots(_src);
        Assert.Equal("forbidden_path", Code(Go(out1, s)));
        Assert.Equal("forbidden_path", Code(Go(out2, s)));
        Assert.Equal("forbidden_path", Code(Go(Path.Combine(out1, "secret-child"), s)));
        IsListing(Go(inside, s));           // control: a link that stays inside the root is fine, so the refusals above are about escaping, not about links
        Control(s);
    }

    [Fact]
    public void A_symlinked_root_is_judged_by_where_it_really_points()
    {
        var linkRoot = Path.Combine(_base, "root-link");
        if (!TryLink(linkRoot, _src)) { Assert.True(OperatingSystem.IsWindows()); return; }
        var s = Roots(linkRoot);
        IsListing(Go(Path.Combine(linkRoot, "proj-a"), s));        // control: the link's own children list
        Assert.Equal("forbidden_path", Code(Go(_outside, s)));
        Assert.Equal("forbidden_path", Code(Go(Path.Combine(linkRoot, "..", "outside"), s)));
    }

    [Fact]
    public void A_listing_does_not_offer_a_symlink_that_escapes_as_a_folder_to_enter()
    {
        var out1 = Path.Combine(_src, "link-out");
        if (!TryLink(out1, _outside)) { Assert.True(OperatingSystem.IsWindows()); return; }
        var o = Go(_src, Roots(_src));
        Assert.DoesNotContain("link-out", Names(o));          // an offered row that always refuses is a dead end; omitting it also hides that it exists
        Assert.Contains("proj-a", Names(o));
    }

    // ---- system folders and the home toggle -------------------------------------------------------------------

    [Fact]
    public void With_the_home_toggle_off_home_and_its_other_folders_are_not_browsable()
    {
        var s = Roots(_src);
        foreach (var p in new[] { _home, Path.Combine(_home, "Documents"), Path.Combine(_home, "Desktop"), "~", "~/Documents" })
            Assert.Equal("forbidden_path", Code(Go(p, s)));
        Control(s);
    }

    [Fact]
    public void With_the_home_toggle_on_home_is_browsable_but_system_folders_never_are()
    {
        var s = HomeOn;
        var home = Go(_home, s);
        Assert.Contains("Documents", Names(home)); Assert.Contains("notes", Names(home)); Assert.Contains("Source", Names(home));   // control: the toggle really widened it
        var notSystemHere = OperatingSystem.IsWindows() ? "Library" : "AppData";          // each is a system folder on one OS only: the other must still list
        Assert.Contains(notSystemHere, Names(Go(_home, s)));
        foreach (var sys in OperatingSystem.IsWindows() ? new[] { "AppData", ".ssh" } : new[] { "Library", ".ssh" })
        {
            Assert.DoesNotContain(sys, Names(Go(_home, s, hidden: true)));                        // omitted from the listing, even with hidden shown
            Assert.Equal("forbidden_path", Code(Go(Path.Combine(_home, sys), s)));                // and refused if typed
        }
        Assert.Contains("proj-a", Names(Go("~/Source", s)));                                      // ~ expands against the injected home
        Control(s);
    }

    [Fact]
    public void A_folder_that_merely_shares_a_system_folders_name_inside_a_root_is_not_a_system_folder()
    {
        // The system list is ~/Library, /Library and so on, by exact location. ~/Source/Library is the user's own project folder.
        Directory.CreateDirectory(Path.Combine(_src, "Library", "x"));
        var s = Roots(_src);
        Assert.Contains("Library", Names(Go(_src, s)));
        IsListing(Go(Path.Combine(_src, "Library"), s));
        Control(s);
    }

    [Fact]
    public void Setting_a_system_folder_or_the_filesystem_root_as_a_project_root_widens_nothing()
    {
        var sysRoot = OperatingSystem.IsWindows() ? @"C:\Windows" : "/System";
        // The contract allows a drive root as a project root (its system folders stay hidden), so the assertion is only that the system
        // folder itself never lists, whether it is the root, inside a root, or one of several roots.
        Assert.Equal("forbidden_path", Code(Go(sysRoot, Roots(sysRoot))));
        Assert.Equal("forbidden_path", Code(Go(sysRoot, Roots(sysRoot, _src))));
        IsListing(Go(_src, Roots(sysRoot, _src)));    // control: the legitimate root beside it still lists
    }

    // ---- unreadable and timeouts ------------------------------------------------------------------------------

    [QaUnixFact]
    public void An_unreadable_folder_inside_a_root_is_unreadable_not_a_hang_or_a_crash()
    {
        var locked = Path.Combine(_src, "locked"); Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            if (Environment.UserName == "root") return;   // root ignores mode bits; a named gap, not a pass, on a root runner
            Assert.Equal("unreadable", Code(Go(locked, Roots(_src))));
            Control(Roots(_src));
        }
        finally { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    // ---- cap, truncated, chunking -----------------------------------------------------------------------------

    [Fact]
    public void More_than_500_children_are_capped_in_sort_order_and_flagged_truncated_while_499_are_not()
    {
        var big = Path.Combine(_src, "big"); Directory.CreateDirectory(big);
        for (var i = 0; i < 600; i++) Directory.CreateDirectory(Path.Combine(big, $"d{i:D4}"));
        var o = Lst(Go(big, Roots(_src)));
        Assert.Equal(500, o.Items.Count);
        Assert.True(o.Truncated);
        Assert.Equal("d0000", o.Items[0].Name);                      // first 500 in sort order
        var small = Path.Combine(_src, "small"); Directory.CreateDirectory(small);
        for (var i = 0; i < 499; i++) Directory.CreateDirectory(Path.Combine(small, $"d{i:D4}"));
        var c = Lst(Go(small, Roots(_src)));                 // control: just under the cap is whole and not truncated
        Assert.Equal(499, c.Items.Count); Assert.False(c.Truncated);
    }

    [Fact]
    public void A_huge_listing_is_chunked_under_the_plaintext_limit_and_the_chunks_rebuild_it_exactly_and_the_envelope_fits_the_relay()
    {
        var big = Path.Combine(_src, "wide"); Directory.CreateDirectory(big);
        for (var i = 0; i < 500; i++) Directory.CreateDirectory(Path.Combine(big, new string((char)('a' + i % 26), 200) + $"-{i:D4}"));
        var l = Lst(Go(big, Roots(_src)));
        var chunks = BrowseFrames.Dir("01JCB0QA000000000000000099", l);
        Assert.True(chunks.Count >= 2, "500 long names cannot fit in one 40,000-byte chunk");
        var names = new List<string>();
        foreach (var c in chunks) foreach (var e in JsonDocument.Parse(c).RootElement.GetProperty("entries").EnumerateArray()) names.Add(e.GetProperty("name").GetString()!);
        Assert.Equal(l.Items.Select(e => e.Name), names);             // no entry lost, duplicated or reordered across chunks
        // MEASURED: seal-sized random ct for each real chunk, written with the desk's own writer options (relaxed) and with the default escaper.
        foreach (var c in chunks)
        {
            var plain = Encoding.UTF8.GetByteCount(c);
            var ct = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(plain + 28));
            int Wire(System.Text.Json.JsonWriterOptions o) { using var ms = new MemoryStream(); using (var w = new Utf8JsonWriter(ms, o)) { w.WriteStartObject(); w.WriteNumber("v", 2); w.WriteString("id", "01JCB0QA000000000000000099"); w.WriteString("from", "dsk_5f3c9a0e1b7d2468"); w.WriteString("to", "ph_a1b2c3d4e5f60718"); w.WriteNumber("ts", 1794000000L); w.WriteString("class", "action"); w.WriteString("push", "none"); w.WriteString("ck", ""); w.WriteString("ct", ct); w.WriteEndObject(); } return (int)ms.Length; }
            var relaxed = Wire(new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var def = Wire(new());
            _out.WriteLine($"chunk plaintext {plain} B -> envelope {relaxed} B relaxed (the desk's writer), {def} B default-escaped; relay limit 65536");
            Assert.True(relaxed <= 65536, $"relaxed envelope {relaxed} B exceeds the relay's limit");
        }
        Assert.All(chunks, c => Assert.True(Encoding.UTF8.GetByteCount(c) <= 40000));
        // wire size: sealing adds 28 bytes then base64 (x4/3); the envelope around it adds ~230 bytes. The relay closes anything over 65,536.
        var worst = chunks.Max(c => Encoding.UTF8.GetByteCount(c));
        var ctChars = (worst + 28 + 2) / 3 * 4;
        Assert.True(ctChars + 400 <= 64 * 1024, $"a {worst}-byte chunk seals to {ctChars} base64 chars; with the envelope it must stay under 65,536");
        // and with every + and / written as a six-byte escape (what a default System.Text.Json writer does), the worst case a desk can send:
        var escapedWorst = ctChars + ctChars / 32 * 5 + 400;
        Assert.True(escapedWorst <= 64 * 1024, $"escaped worst case {escapedWorst} bytes");
    }

    [Fact]
    public void Names_with_quotes_backslashes_and_unicode_survive_canonical_encoding_unescaped_by_the_relaxed_rules()
    {
        var odd = new[] { "we\"ird\\name", "R\u00e9sum\u00e9 \u65e5\u672c", "a+b/c" };
        foreach (var n in odd) { try { Directory.CreateDirectory(Path.Combine(_src, n)); } catch (Exception e) when (e is IOException or ArgumentException) { return; } }
        var l = Lst(Go(_src, Roots(_src)));
        var json = BrowseFrames.Dir("01JCB0QA000000000000000098", l).Single();
        foreach (var n in odd.Where(n => !n.Contains('/'))) Assert.Contains(JsonSerializer.Serialize(n, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })[1..^1], json);
    }

    // ---- rate limit -------------------------------------------------------------------------------------------

    [Fact]
    public void The_rate_limit_is_a_burst_of_8_then_4_a_second_per_phone_and_a_refusal_costs_nothing()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_794_000_000);
        var rl = new BrowseRateLimiter();
        for (var i = 0; i < 8; i++) Assert.True(rl.TryAccept("ph_a", now.ToUnixTimeMilliseconds()), $"request {i + 1} of the burst");
        Assert.False(rl.TryAccept("ph_a", now.ToUnixTimeMilliseconds()));
        Assert.False(rl.TryAccept("ph_a", now.ToUnixTimeMilliseconds()));                            // refused twice: still no debt
        Assert.True(rl.TryAccept("ph_b", now.ToUnixTimeMilliseconds()));                             // control: another phone is unaffected
        now += TimeSpan.FromMilliseconds(249); Assert.False(rl.TryAccept("ph_a", now.ToUnixTimeMilliseconds()));
        now += TimeSpan.FromMilliseconds(1);   Assert.True(rl.TryAccept("ph_a", now.ToUnixTimeMilliseconds()));    // exactly one credit back at 250 ms
        Assert.False(rl.TryAccept("ph_a", now.ToUnixTimeMilliseconds()));
        now += TimeSpan.FromSeconds(30);                                // credit is capped at the burst, not banked
        for (var i = 0; i < 8; i++) Assert.True(rl.TryAccept("ph_a", now.ToUnixTimeMilliseconds()));
        Assert.False(rl.TryAccept("ph_a", now.ToUnixTimeMilliseconds()));
    }

    // ---- Windows: junctions, 8.3 names, case, device and stream names -----------------------------------------

    private static bool Cmd(string args)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c " + args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit(10000); return p.ExitCode == 0;
    }

    [QaWindowsFact]
    public void A_junction_out_of_a_root_is_refused_and_a_junction_inside_is_not()
    {
        var jOut = Path.Combine(_src, "junction-out"); var jIn = Path.Combine(_src, "junction-in");
        Assert.True(Cmd($"mklink /J \"{jOut}\" \"{_outside}\""));
        Assert.True(Cmd($"mklink /J \"{jIn}\" \"{Path.Combine(_src, "proj-a")}\""));
        var s = Roots(_src);
        Assert.Equal("forbidden_path", Code(Go(jOut, s)));
        Assert.Equal("forbidden_path", Code(Go(Path.Combine(jOut, "secret-child"), s)));
        Assert.DoesNotContain("junction-out", Names(Go(_src, s)));
        IsListing(Go(jIn, s));                       // control
        Control(s);
    }

    [QaWindowsFact]
    public void Case_variants_and_trailing_dot_and_space_name_the_same_folder_and_report_its_real_spelling()
    {
        var s = Roots(_src);
        var upper = Go(_src.ToUpperInvariant(), s);
        IsListing(upper);                            // NTFS is case-insensitive: the root by another spelling IS the root
        Assert.Equal(_src, Lst(upper).Path, ignoreCase: false);   // and the reply carries the filesystem's own spelling
        IsListing(Go(Path.Combine(_src, "PROJ-A"), s));
        foreach (var trailing in new[] { ".", " " })      // Win32 strips these; refusing is safe, resolving inside is fine, listing anywhere else is not
        {
            var o = Go(Path.Combine(_src, "proj-a") + trailing, s);
            if (o.Listing is { } l) Assert.Equal(Path.Combine(_src, "proj-a"), l.Path); else Assert.Equal("forbidden_path", Code(o));
        }
        Assert.Equal("forbidden_path", Code(Go(_outside.ToUpperInvariant(), s)));   // a case change does not move a path inside
        Control(s);
    }

    [QaWindowsFact]
    public void An_8_3_short_name_of_an_outside_folder_is_still_outside()
    {
        var longOutside = Path.Combine(_base, "outside-long-folder-name"); Directory.CreateDirectory(longOutside);
        var s = Roots(_src);
        var shortName = GetShort(longOutside);
        Assert.NotNull(shortName);                                      // if the volume has short names disabled this fails loudly rather than passing for the wrong reason
        Assert.NotEqual(longOutside, shortName);
        Assert.Equal("forbidden_path", Code(Go(shortName!, s)));
        var longInside = Path.Combine(_src, "inside-long-folder-name"); Directory.CreateDirectory(longInside);
        var shortInside = GetShort(longInside)!;
        var o = Go(shortInside, s);                                     // control: the short spelling of an INSIDE folder is that folder
        IsListing(o);
        Assert.Equal(longInside, Lst(o).Path);
        Control(s);
    }

    [QaWindowsFact]
    public void An_8_3_alias_of_a_system_folder_does_not_get_round_the_system_list_when_a_drive_root_is_a_project_root()
    {
        var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))!;
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfShort = GetShort(pf);
        Assert.NotNull(pfShort);
        Assert.NotEqual(pf, pfShort);                                    // the volume has short names, so the alias is a different spelling of the same folder
        var s = Roots(drive);
        Assert.Equal("forbidden_path", Code(Go(pf, s)));                 // control: the long name is refused as a system folder
        Assert.Equal("forbidden_path", Code(Go(pfShort!, s)));          // the alias of the same folder must be refused too
        Assert.Equal("forbidden_path", Code(Go(Path.Combine(pfShort!, "Common Files"), s)));
        var o = Go(_src, Roots(drive));                                  // control: the drive root really admits ordinary folders under it
        IsListing(o);
    }

    [QaWindowsFact]
    public void Device_stream_and_extended_prefix_spellings_are_refused_or_resolved_inside_never_outside()
    {
        var s = Roots(_src);
        foreach (var p in new[] { @"\\?\" + _outside, @"\\.\" + _outside, _outside + "::$DATA", Path.Combine(_src, "proj-a") + "::$INDEX_ALLOCATION", @"\\localhost\C$\Windows", "CON", "NUL", @"C:\Windows", @"C:\Windows\System32" })
        {
            var o = Go(p, s);
            if (o.Listing is { } l) Assert.StartsWith(_src, l.Path, StringComparison.OrdinalIgnoreCase);
            else Assert.Contains(Code(o), new[] { "forbidden_path", "bad_frame", "unknown_path" });
        }
        Control(s);
    }

    [QaWindowsFact]
    public void The_hidden_attribute_hides_a_folder_on_windows_and_system_folders_are_omitted_with_the_home_toggle_on()
    {
        var h = Path.Combine(_src, "attr-hidden"); Directory.CreateDirectory(h);
        File.SetAttributes(h, FileAttributes.Hidden);
        Assert.DoesNotContain("attr-hidden", Names(Go(_src, Roots(_src))));
        Assert.Contains("attr-hidden", Names(Go(_src, Roots(_src), hidden: true)));     // control
        Assert.DoesNotContain("AppData", Names(Go(_home, HomeOn, hidden: true)));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint len);
    private static string? GetShort(string p) { var sb = new StringBuilder(260); return GetShortPathName(p, sb, 260) == 0 ? null : sb.ToString(); }
}
