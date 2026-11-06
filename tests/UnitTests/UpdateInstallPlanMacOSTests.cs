using Xunit;

namespace Orbweaver.Tests;

// CB-308: the pure half of the macOS updater. Argv is asserted literally so a
// quoting or flag regression fails here rather than on somebody's Mac.
public class UpdateInstallPlanMacOSTests
{
    [Theory]
    [InlineData("/Applications/Orbweaver.app/Contents/MacOS/Orbweaver", "/Applications/Orbweaver.app")]
    [InlineData("/Users/me/My Apps/Orbweaver.app/Contents/MacOS/Orbweaver", "/Users/me/My Apps/Orbweaver.app")]
    [InlineData("/A/Outer.app/Contents/Inner.app/Contents/MacOS/x", "/A/Outer.app/Contents/Inner.app")]
    public void Finds_the_innermost_app_bundle(string processPath, string expected) =>
        Assert.Equal(expected, UpdateInstallPlan.MacAppBundleFor(processPath));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/usr/local/bin/dotnet")]
    [InlineData("dotnet")]
    [InlineData("relative/dir/dotnet")]
    public void A_loose_binary_has_no_bundle(string? processPath) =>
        Assert.Null(UpdateInstallPlan.MacAppBundleFor(processPath));

    [Fact]
    public void Preflight_accepts_a_writable_installed_bundle()
    {
        var (target, refusal) = UpdateInstallPlan.Preflight("/Applications/Orbweaver.app/Contents/MacOS/Orbweaver", _ => true);
        Assert.Equal("/Applications/Orbweaver.app", target);
        Assert.Null(refusal);
    }

    [Fact]
    public void Preflight_refuses_a_loose_binary_with_instructions()
    {
        var (target, refusal) = UpdateInstallPlan.Preflight("/usr/local/bin/dotnet", _ => true);
        Assert.Null(target);
        Assert.Contains("not running from an app bundle", refusal);
    }

    [Fact]
    public void Preflight_refuses_a_translocated_bundle_even_when_writable()
    {
        var (target, refusal) = UpdateInstallPlan.Preflight(
            "/private/var/folders/x/AppTranslocation/ABC/d/Orbweaver.app/Contents/MacOS/Orbweaver", _ => true);
        Assert.Null(target);
        Assert.Contains("Applications folder", refusal);
    }

    [Fact]
    public void Preflight_refuses_when_the_parent_folder_is_not_writable_and_names_it()
    {
        string? asked = null;
        var (target, refusal) = UpdateInstallPlan.Preflight(
            "/Applications/Orbweaver.app/Contents/MacOS/Orbweaver", p => { asked = p; return false; });
        Assert.Null(target);
        Assert.Equal("/Applications", asked);
        Assert.Contains("/Applications", refusal);
    }

    [Fact]
    public void Argv_builders_are_literal()
    {
        Assert.Equal(["attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", "/m p", "/d m.dmg"],
            UpdateInstallPlan.MacHdiutilAttach("/d m.dmg", "/m p").Args);
        Assert.Equal("/usr/bin/hdiutil", UpdateInstallPlan.MacHdiutilAttach("a", "b").File);
        Assert.Equal(["detach", "/m"], UpdateInstallPlan.MacHdiutilDetach("/m", false).Args);
        Assert.Equal(["detach", "-force", "/m"], UpdateInstallPlan.MacHdiutilDetach("/m", true).Args);
        Assert.Equal(["--verify", "--deep", "--strict", "/a.app"], UpdateInstallPlan.MacCodesignVerify("/a.app").Args);
        Assert.Equal(["-dv", "--verbose=4", "/a.app"], UpdateInstallPlan.MacCodesignDisplay("/a.app").Args);
        Assert.Equal("/usr/sbin/spctl", UpdateInstallPlan.MacSpctl("/a.app").File);
        Assert.Equal(["--assess", "--type", "execute", "/a.app"], UpdateInstallPlan.MacSpctl("/a.app").Args);
        Assert.Equal(["-extract", "CFBundleIdentifier", "raw", "-o", "-", "/a.app/Contents/Info.plist"],
            UpdateInstallPlan.MacPlistRead("/a.app", "CFBundleIdentifier").Args);
        Assert.Equal(["/from.app", "/to.app"], UpdateInstallPlan.MacCopyBundle("/from.app", "/to.app").Args);
        Assert.Equal("/usr/bin/open", UpdateInstallPlan.MacOpenFinisher("/s.app", "/h.json").File);
        Assert.Equal(["-n", "/s.app", "--args", "--finish-update", "/h.json"], UpdateInstallPlan.MacOpenFinisher("/s.app", "/h.json").Args);
        Assert.Equal(["submit", "-l", "lbl", "--", "/s/exe", "--finish-update", "/h.json"],
            UpdateInstallPlan.MacLaunchctlSubmit("lbl", "/s/exe", "/h.json").Args);
        Assert.Equal(["unload", "/p.plist"], UpdateInstallPlan.MacLaunchctlUnload("/p.plist").Args);
        Assert.Equal(["load", "/p.plist"], UpdateInstallPlan.MacLaunchctlLoad("/p.plist").Args);
        Assert.Equal("/bin/launchctl", UpdateInstallPlan.MacLaunchctlLoad("/p.plist").File);
        Assert.Equal(["-u", "CLAUDE_CONFIG_DIR", "/usr/bin/open", "-a", "/a.app"], UpdateInstallPlan.MacOpenInstalled("/a.app").Args);
        var repair = UpdateInstallPlan.MacKeepAliveRepair("/a.app");
        Assert.Equal("/a.app/Contents/Resources/install-hooks.sh", repair.File);
        Assert.Equal(["--keepalive-only"], repair.Args);
        Assert.Equal(["-axo", "pid=,uid=,comm="], UpdateInstallPlan.MacPsList().Args);
    }

    [Fact]
    public void Reads_the_team_id_out_of_codesign_output()
    {
        const string real = "Executable=/x\nIdentifier=io.github.wtvamp.claudebuddy\nAuthority=Developer ID Application: UPLIFT FOUNDATION (5AQ4ULRG3Z)\nTeamIdentifier=5AQ4ULRG3Z\nSealed Resources version=2\n";
        Assert.Equal("5AQ4ULRG3Z", UpdateInstallPlan.MacParseTeamId(real));
    }

    [Theory]
    [InlineData("Signature=adhoc\nTeamIdentifier=not set\n")]
    [InlineData("TeamIdentifier=\n")]
    [InlineData("no team line at all")]
    [InlineData("")]
    public void An_ad_hoc_or_absent_team_is_null(string output) =>
        Assert.Null(UpdateInstallPlan.MacParseTeamId(output));

    [Fact]
    public void Finds_exactly_one_real_app_and_ignores_the_applications_symlink()
    {
        var (app, error) = UpdateInstallPlan.MacFindSingleApp([
            ("Orbweaver.app", true, false), ("Applications", true, true), (".DS_Store", false, false)]);
        Assert.Equal("Orbweaver.app", app);
        Assert.Null(error);
    }

    [Fact]
    public void A_symlinked_app_does_not_count()
    {
        var (app, error) = UpdateInstallPlan.MacFindSingleApp([("Evil.app", true, true)]);
        Assert.Null(app);
        Assert.Contains("no app bundle", error);
    }

    [Fact]
    public void A_file_named_like_an_app_does_not_count()
    {
        var (app, _) = UpdateInstallPlan.MacFindSingleApp([("Fake.app", false, false)]);
        Assert.Null(app);
    }

    [Fact]
    public void Two_apps_are_refused_and_both_are_named()
    {
        var (app, error) = UpdateInstallPlan.MacFindSingleApp([("A.app", true, false), ("B.app", true, false)]);
        Assert.Null(app);
        Assert.Contains("A.app", error);
        Assert.Contains("B.app", error);
    }

    [Fact]
    public void Ps_parse_matches_the_exe_for_this_user_and_skips_self_and_strangers()
    {
        const string exe = "/Applications/My Apps/Orbweaver.app/Contents/MacOS/Orbweaver";
        var ps = string.Join('\n',
            $"  101   501 {exe}",
            $"  102   501 {exe}",          // self
            $"  103   0   {exe}",           // another user
            "  104   501 /usr/bin/other",
            "  garbage",
            "  105",
            "  x 501 " + exe,
            "  106 y " + exe,
            "");
        Assert.Equal([101], UpdateInstallPlan.MacParsePs(ps, exe, selfPid: 102, uid: 501));
    }

    [Fact]
    public void Handoff_round_trips_and_rejects_junk()
    {
        var h = new MacHandoff(UpdateInstallPlan.MacHandoffSchema, "/Applications/Orbweaver.app", "/s/Orbweaver.app", 42, "/p.plist", "1.2.3", "/m");
        Assert.Equal(h, UpdateInstallPlan.MacParseHandoff(UpdateInstallPlan.MacSerializeHandoff(h)));
        Assert.Null(UpdateInstallPlan.MacParseHandoff("not json"));
        Assert.Null(UpdateInstallPlan.MacParseHandoff("null"));
        Assert.Null(UpdateInstallPlan.MacParseHandoff("""{"Schema":1,"Installed":"","Staged":"s","MarkerDir":"m"}"""));
        // An unknown schema still parses, so the finisher can name it and use MarkerDir.
        Assert.Equal(99, UpdateInstallPlan.MacParseHandoff("""{"Schema":99,"Installed":"i","Staged":"s","MarkerDir":"m","Future":true}""")!.Schema);
        // No Schema at all reads as 0, which is not the current one.
        Assert.Equal(0, UpdateInstallPlan.MacParseHandoff("""{"Installed":"i","Staged":"s","MarkerDir":"m"}""")!.Schema);
        Assert.Equal(1, UpdateInstallPlan.MacHandoffSchema);
    }

    [Fact]
    public void Old_and_new_paths_sit_beside_the_installed_bundle()
    {
        var at = new DateTimeOffset(2026, 11, 6, 10, 15, 0, TimeSpan.FromHours(-8));
        Assert.Equal("/Applications/Orbweaver.app.old-20261106T181500Z", UpdateInstallPlan.MacOldPath("/Applications/Orbweaver.app", at));
        Assert.Equal("/Applications/Orbweaver.app.new", UpdateInstallPlan.MacNewPath("/Applications/Orbweaver.app"));
    }

    [Fact]
    public void The_integration_suite_literals_match_the_real_brand()
    {
        // tests/IntegrationTests cannot name Brand (SingleInstanceProbe
        // compiles a second one in), so it uses these literals.
        Assert.Equal("io.github.wtvamp.claudebuddy", Brand.MacBundleId);
        Assert.Equal("Orbweaver", Brand.AssemblyName);
    }

    [Theory]
    [InlineData("1.0.4")]
    [InlineData("1.0.10")]
    [InlineData("1.1.0")]
    [InlineData("2.0.0")]
    public void A_release_from_the_finisher_onward_is_installable(string version) =>
        Assert.Null(UpdateInstallPlan.MacFinisherRefusal(version));

    [Theory]
    [InlineData("1.0.3")]
    [InlineData("1.0.4-beta")]
    [InlineData("0.5.11-beta")]
    [InlineData("not a version")]
    [InlineData("")]
    public void A_release_that_predates_the_finisher_is_refused_and_says_where_to_go(string version)
    {
        var why = UpdateInstallPlan.MacFinisherRefusal(version);
        Assert.NotNull(why);
        Assert.Contains("releases page", why);
    }
}
