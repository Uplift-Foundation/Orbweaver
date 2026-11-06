using System.Security.Cryptography;
using Xunit;

namespace Orbweaver.Tests;

// CB-308: the macOS updater's stage / launch / finish sequence, run against
// real scratch folders with every OS tool replaced by FakeMac. Nothing here
// runs hdiutil, codesign, spctl, open or launchctl.
public sealed class UpdateInstallMacOSTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orb-upd-" + Guid.NewGuid().ToString("N"));
    // Literals, not Brand: SingleInstanceProbe compiles its own Brand into this
    // test assembly, so the name is ambiguous here. UnitTests pins that these
    // equal the real Brand values (UpdateInstallPlanMacOSTests).
    private const string BundleId = "io.github.wtvamp.claudebuddy";
    private const string ExeName = "Orbweaver";
    private readonly List<string> _log = [];

    public UpdateInstallMacOSTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Dir(string name) { var p = Path.Combine(_root, name); Directory.CreateDirectory(p); return p; }

    // A recording stand-in for every external tool. Anything not overridden succeeds.
    private sealed class FakeMac
    {
        public readonly List<MacCommand> Calls = [];
        public string Team = UpdateInstallPlan.MacTeamId;
        public string BundleId = UpdateInstallMacOSTests.BundleId;
        public string Version = "2.0.0";
        public string[] MountApps = ["Orbweaver.app"];
        public bool MountSymlink;
        public int Attach, Verify, Spctl, Ditto, Open, Repair;
        public int VerifyFailsAfter = int.MaxValue; // codesign --verify calls that succeed before one fails
        private int _verifies;
        public string Ps = "";
        public Func<MacCommand, (int, string)?>? Hook;

        public (int ExitCode, string Output) Run(MacCommand c, TimeSpan _)
        {
            Calls.Add(c);
            if (Hook?.Invoke(c) is { } h) return h;
            var a = c.Args;
            switch (System.IO.Path.GetFileName(c.File))
            {
                case "hdiutil" when a[0] == "attach":
                    if (Attach == 0)
                    {
                        var mount = a[5];
                        foreach (var app in MountApps) Directory.CreateDirectory(System.IO.Path.Combine(mount, app, "Contents", "MacOS"));
                        if (MountSymlink) Directory.CreateSymbolicLink(System.IO.Path.Combine(mount, "Applications"), "/Applications");
                    }
                    return (Attach, "");
                case "hdiutil": return (0, "");
                case "codesign" when a[0] == "--verify": return (++_verifies > VerifyFailsAfter ? 1 : Verify, Verify != 0 ? "invalid signature" : "");
                case "codesign": return (0, $"Identifier=x\nTeamIdentifier={Team}\n");
                case "spctl": return (Spctl, Spctl != 0 ? "rejected" : "accepted");
                case "plutil": return (0, a[1] == "CFBundleIdentifier" ? BundleId : Version);
                case "ditto":
                    if (Ditto == 0) CopyDir(a[0], a[1]);
                    return (Ditto, "");
                case "open": return (Open, "");
                case "ps": return (0, Ps);
                case "install-hooks.sh": return (Repair, "");
                default: return (0, "");
            }
        }

        public bool Ran(string tool, string firstArg) =>
            Calls.Any(c => System.IO.Path.GetFileName(c.File) == tool && c.Args.Count > 0 && c.Args[0] == firstArg);

        private static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(d.Replace(from, to));
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                File.Copy(f, f.Replace(from, to), true);
        }
    }

    private static string Hash(string dir) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
        string.Join('\n', Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories).Order()
            .Select(f => f + (File.Exists(f) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))) : ""))))));

    private (string? App, string? Error) Stage(FakeMac mac, out string staging)
    {
        staging = Dir("staging");
        var dmg = Path.Combine(_root, "u.dmg");
        File.WriteAllText(dmg, "dmg");
        return UpdateInstallMacOS.Stage(dmg, staging, "2.0.0", mac.Run, _log.Add);
    }

    // ---- Stage ----------------------------------------------------------

    [Fact]
    public void Stage_copies_a_verified_bundle_and_detaches_the_image()
    {
        var mac = new FakeMac { MountSymlink = true };
        var (app, error) = Stage(mac, out var staging);

        Assert.Null(error);
        Assert.Equal(Path.Combine(staging, "Orbweaver.app"), app);
        Assert.True(Directory.Exists(Path.Combine(app!, "Contents", "MacOS")));
        Assert.True(mac.Ran("hdiutil", "detach"));
        Assert.False(mac.Calls.Any(c => c.Args.Contains("-force")));
    }

    [Fact]
    public void Stage_replaces_a_leftover_staged_copy()
    {
        var mac = new FakeMac();
        var staging = Dir("staging");
        Directory.CreateDirectory(Path.Combine(staging, "Orbweaver.app", "stale"));
        var dmg = Path.Combine(_root, "u.dmg"); File.WriteAllText(dmg, "d");
        var (app, _) = UpdateInstallMacOS.Stage(dmg, staging, "2.0.0", mac.Run, _log.Add);
        Assert.False(Directory.Exists(Path.Combine(app!, "stale")));
    }

    [Fact]
    public void Stage_refuses_an_unmountable_image()
    {
        var (app, error) = Stage(new FakeMac { Attach = 1 }, out _);
        Assert.Null(app);
        Assert.Contains("hdiutil exited 1", error);
    }

    [Fact]
    public void Stage_refuses_two_apps_and_still_detaches()
    {
        var mac = new FakeMac { MountApps = ["A.app", "B.app"] };
        var (app, error) = Stage(mac, out _);
        Assert.Null(app);
        Assert.Contains("2 app bundles", error);
        Assert.True(mac.Ran("hdiutil", "detach"));
    }

    [Fact]
    public void Stage_refuses_an_empty_image()
    {
        Assert.Contains("no app bundle", Stage(new FakeMac { MountApps = [] }, out _).Error);
    }

    [Fact]
    public void Stage_refuses_a_bad_signature()
    {
        var (app, error) = Stage(new FakeMac { Verify = 1 }, out _);
        Assert.Null(app);
        Assert.Contains("signature is not valid", error);
    }

    [Theory]
    [InlineData("not set")]
    [InlineData("ABCDE12345")]
    public void Stage_refuses_the_wrong_team(string team)
    {
        var (_, error) = Stage(new FakeMac { Team = team }, out _);
        Assert.Contains("Developer ID team", error);
    }

    [Fact]
    public void Stage_refuses_what_gatekeeper_rejects()
    {
        Assert.Contains("Gatekeeper", Stage(new FakeMac { Spctl = 3 }, out _).Error);
    }

    [Fact]
    public void Stage_refuses_the_wrong_bundle_id()
    {
        Assert.Contains("wrong bundle identifier", Stage(new FakeMac { BundleId = "com.evil.app" }, out _).Error);
    }

    [Fact]
    public void Stage_refuses_a_version_other_than_the_one_offered()
    {
        Assert.Contains("not the 2.0.0", Stage(new FakeMac { Version = "1.0.0" }, out _).Error);
    }

    [Fact]
    public void Stage_refuses_when_the_copy_fails()
    {
        Assert.Contains("ditto exited 1", Stage(new FakeMac { Ditto = 1 }, out _).Error);
    }

    [Fact]
    public void Stage_discards_a_copy_that_no_longer_verifies()
    {
        var mac = new FakeMac { VerifyFailsAfter = 1 };
        var (app, error) = Stage(mac, out var staging);
        Assert.Null(app);
        Assert.Contains("copied app failed", error);
        Assert.False(Directory.Exists(Path.Combine(staging, "Orbweaver.app")));
    }

    [Fact]
    public void Stage_forces_the_detach_when_the_polite_one_fails()
    {
        var mac = new FakeMac { Hook = c => c.Args.Count > 0 && c.Args[0] == "detach" && !c.Args.Contains("-force") ? (1, "busy") : null };
        Stage(mac, out _);
        Assert.Contains(mac.Calls, c => c.Args.Contains("-force"));
    }

    [Fact]
    public void A_refused_stage_leaves_an_installed_bundle_byte_identical()
    {
        var installed = Dir("Applications/Orbweaver.app/Contents/MacOS");
        File.WriteAllText(Path.Combine(installed, "Orbweaver"), "old binary");
        var bundle = Path.Combine(_root, "Applications", "Orbweaver.app");
        var before = Hash(bundle);

        foreach (var mac in new[] { new FakeMac { Team = "X" }, new FakeMac { Verify = 1 }, new FakeMac { Spctl = 1 },
                     new FakeMac { MountApps = ["A.app", "B.app"] }, new FakeMac { BundleId = "no" }, new FakeMac { Version = "9" } })
            Assert.Null(Stage(mac, out _).App);

        Assert.Equal(before, Hash(bundle));
    }

    // ---- Launch ---------------------------------------------------------

    private MacHandoff Handoff(string? plist = null, int schema = UpdateInstallPlan.MacHandoffSchema) => new(
        schema, Path.Combine(_root, "Applications", "Orbweaver.app"), Path.Combine(Dir("staging"), "Orbweaver.app"), 4242, plist, "2.0.0", Dir("marker"));

    [Fact]
    public async Task Launch_writes_the_handoff_and_runs_open_n_on_the_staged_app()
    {
        var mac = new FakeMac();
        var h = Handoff();
        File.WriteAllText(Path.Combine(h.MarkerDir, "failed"), "stale"); // a stale marker must not decide this run
        var polls = 0;
        var outcome = await UpdateInstallMacOS.LaunchAsync(h, mac.Run, () =>
        {
            if (++polls == 2) File.WriteAllText(Path.Combine(h.MarkerDir, "finished"), "2.0.0");
            return Task.CompletedTask;
        }, _log.Add);

        Assert.Equal(InstallStatus.HandedOff, outcome.Status);
        var open = mac.Calls.Single(c => c.File == "/usr/bin/open");
        Assert.Equal(["-n", h.Staged, "--args", "--finish-update", Path.Combine(h.MarkerDir, "handoff.json")], open.Args);
        Assert.Equal(h, UpdateInstallPlan.MacParseHandoff(File.ReadAllText(Path.Combine(h.MarkerDir, "handoff.json"))));
    }

    [Fact]
    public async Task Launch_reports_the_finishers_own_reason_when_it_fails()
    {
        var h = Handoff();
        var outcome = await UpdateInstallMacOS.LaunchAsync(h, new FakeMac().Run, () =>
        {
            File.WriteAllText(Path.Combine(h.MarkerDir, "failed"), "disk full");
            return Task.CompletedTask;
        }, _log.Add);
        Assert.Equal(InstallStatus.Failed, outcome.Status);
        Assert.Equal("disk full", outcome.Message);
    }

    [Fact]
    public async Task Launch_says_nothing_changed_when_the_helper_never_starts()
    {
        var outcome = await UpdateInstallMacOS.LaunchAsync(Handoff(), new FakeMac().Run, () => Task.CompletedTask, _log.Add, maxPolls: 3);
        Assert.Equal(InstallStatus.Failed, outcome.Status);
        Assert.Contains("nothing was changed", outcome.Message);
    }

    [Fact]
    public async Task Launch_fails_cleanly_when_open_itself_fails()
    {
        var outcome = await UpdateInstallMacOS.LaunchAsync(Handoff(), new FakeMac { Open = 1 }.Run, () => Task.CompletedTask, _log.Add);
        Assert.Equal(InstallStatus.Failed, outcome.Status);
        Assert.Contains(_log, l => l.Contains("open -n exited 1"));
    }

    // ---- Finish ---------------------------------------------------------

    private sealed class World
    {
        public readonly HashSet<int> Alive = [4242];
        public bool IgnoresTerm;
        public bool NewRunning;
        public List<(int Pid, bool Force)> Signals = [];
        public Func<string, string, bool>? FailMove;
        public bool FailDelete;
        public List<(string A, string B)> Swaps = [];
        public Func<int, int>? SwapRc;   // call index -> errno (0 = really exchange)
    }

    private (MacHandoff H, FakeMac Mac, World W, UpdateInstallMacOS.FinishDeps D, string Exe) Finish(
        bool newStartsOnItsOwn = true, bool openStartsIt = true, string? plist = null)
    {
        var h = Handoff(plist);
        Directory.CreateDirectory(Path.Combine(h.Installed, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(h.Installed, "Contents", "MacOS", ExeName), "OLD");
        Directory.CreateDirectory(Path.Combine(h.Staged, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(h.Staged, "Contents", "MacOS", ExeName), "NEW");
        File.WriteAllText(Path.Combine(h.MarkerDir, "u.dmg"), "dmg");
        var exe = Path.Combine(h.Installed, "Contents", "MacOS", ExeName);
        var w = new World();
        var mac = new FakeMac();
        mac.Hook = c =>
        {
            if (c.File == "/bin/ps") return (0, (w.Alive.Contains(4242) ? $"4242 501 {exe}\n" : "") + (w.NewRunning ? $"7000 501 {exe}\n" : ""));
            if (c.Args.Count > 0 && c.Args[0] == "--keepalive-only") { if (newStartsOnItsOwn) w.NewRunning = true; return (mac.Repair, ""); }
            if (c.File == "/usr/bin/env" && openStartsIt) { w.NewRunning = true; return (0, ""); }
            return null;
        };
        var d = new UpdateInstallMacOS.FinishDeps(
            mac.Run, _log.Add, () => new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero),
            (pid, force) => { w.Signals.Add((pid, force)); if (force || !w.IgnoresTerm) w.Alive.Remove(pid); },
            pid => w.Alive.Contains(pid), _ => Task.CompletedTask,
            (from, to) => { if (w.FailMove?.Invoke(from, to) == true) throw new IOException("simulated"); Directory.Move(from, to); },
            path => { if (w.FailDelete) throw new IOException("cannot delete"); Directory.Delete(path, true); },
            (a, b) =>
            {
                w.Swaps.Add((a, b));
                var rc = w.SwapRc?.Invoke(w.Swaps.Count - 1) ?? 0;
                if (rc != 0) return rc;
                var tmp = a + ".swaptmp"; Directory.Move(a, tmp); Directory.Move(b, a); Directory.Move(tmp, b);
                return 0;
            },
            Path.Combine(_root, "Trash"), 501, 9999);
        return (h, mac, w, d, exe);
    }

    [Fact]
    public async Task Finish_swaps_the_bundle_trashes_the_old_one_and_writes_the_finished_marker()
    {
        var (h, mac, w, d, _) = Finish();
        Assert.True(await UpdateInstallMacOS.FinishAsync(h, d));

        Assert.Equal("NEW", File.ReadAllText(Path.Combine(h.Installed, "Contents", "MacOS", ExeName)));
        Assert.False(Directory.Exists(h.Staged));
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "Trash")));
        Assert.Equal("2.0.0", File.ReadAllText(Path.Combine(h.MarkerDir, "finished")));
        Assert.Empty(Directory.GetFiles(h.MarkerDir, "*.dmg"));
        Assert.Equal([(4242, false)], w.Signals);
        Assert.Equal(h.Installed + "/Contents/Resources/install-hooks.sh", mac.Calls.Single(c => c.Args.Contains("--keepalive-only")).File);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Finish_refuses_an_unknown_handoff_schema_and_touches_nothing(int schema)
    {
        var plist = Path.Combine(_root, "ka.plist"); File.WriteAllText(plist, "x");
        var (h, mac, w, d, _) = Finish(plist: plist);
        h = h with { Schema = schema };
        var before = Hash(h.Installed);

        Assert.False(await UpdateInstallMacOS.FinishAsync(h, d));

        Assert.Equal(before, Hash(h.Installed));
        Assert.True(Directory.Exists(h.Staged));
        Assert.Empty(w.Signals);
        Assert.Empty(mac.Calls);
        Assert.Contains($"schema {schema}", File.ReadAllText(Path.Combine(h.MarkerDir, "failed")));
        Assert.False(File.Exists(Path.Combine(h.MarkerDir, "finished")));
    }

    [Fact]
    public async Task The_current_schema_is_the_control_that_does_proceed()
    {
        var (h, _, _, d, _) = Finish();
        Assert.Equal(UpdateInstallPlan.MacHandoffSchema, h.Schema);
        Assert.True(await UpdateInstallMacOS.FinishAsync(h, d));
    }

    [Fact]
    public async Task Finish_unloads_the_keepalive_agent_first_when_it_exists()
    {
        var plist = Path.Combine(_root, "ka.plist"); File.WriteAllText(plist, "x");
        var (h, mac, _, d, _) = Finish(plist: plist);
        await UpdateInstallMacOS.FinishAsync(h, d);
        var unload = mac.Calls.FindIndex(c => c.Args.Count > 0 && c.Args[0] == "unload");
        var kill = mac.Calls.FindIndex(c => c.File == "/bin/ps");
        Assert.True(unload >= 0 && unload < kill);
    }

    [Fact]
    public async Task Finish_skips_unloading_a_keepalive_agent_that_is_not_there()
    {
        var (h, mac, _, d, _) = Finish(plist: Path.Combine(_root, "absent.plist"));
        await UpdateInstallMacOS.FinishAsync(h, d);
        Assert.False(mac.Ran("launchctl", "unload"));
    }

    [Fact]
    public async Task Finish_sends_sigkill_to_a_process_that_ignores_sigterm()
    {
        var (h, _, w, d, _) = Finish();
        w.IgnoresTerm = true;
        Assert.True(await UpdateInstallMacOS.FinishAsync(h, d));
        Assert.Equal([(4242, false), (4242, true)], w.Signals);
    }

    [Fact]
    public async Task Finish_also_stops_another_process_running_the_installed_executable()
    {
        var (h, mac, w, d, exe) = Finish();
        w.Alive.Add(5555);
        var baseHook = mac.Hook!;
        mac.Hook = c => c.File == "/bin/ps" ? (0, $"4242 501 {exe}\n" + (w.Alive.Contains(5555) ? $"5555 501 {exe}\n" : "") + (w.NewRunning ? $"7000 501 {exe}\n" : "")) : baseHook(c);
        await UpdateInstallMacOS.FinishAsync(h, d);
        Assert.Contains((5555, false), w.Signals);
    }

    [Fact]
    public async Task Finish_refuses_a_staged_bundle_that_no_longer_verifies_and_changes_nothing()
    {
        var (h, mac, w, d, _) = Finish();
        mac.Verify = 1;
        Assert.False(await UpdateInstallMacOS.FinishAsync(h, d));
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(h.Installed, "Contents", "MacOS", ExeName)));
        Assert.Empty(w.Signals);
        Assert.Contains("signature", File.ReadAllText(Path.Combine(h.MarkerDir, "failed")));
    }

    [Fact]
    public async Task Finish_falls_back_to_copy_and_rename_on_a_cross_volume_move()
    {
        var (h, mac, w, d, _) = Finish();
        w.FailMove = (from, to) => from == h.Staged && to == UpdateInstallPlan.MacNewPath(h.Installed);
        Assert.True(await UpdateInstallMacOS.FinishAsync(h, d));
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(h.Installed, "Contents", "MacOS", ExeName)));
        Assert.True(mac.Ran("ditto", h.Staged));
        Assert.False(Directory.Exists(h.Staged), "the leftover staged copy is removed");
    }

    [Fact]
    public async Task Finish_replaces_a_leftover_dot_new_when_it_falls_back()
    {
        var (h, _, w, d, _) = Finish();
        Directory.CreateDirectory(UpdateInstallPlan.MacNewPath(h.Installed) + "/junk");
        w.FailMove = (from, to) => from == h.Staged && to == UpdateInstallPlan.MacNewPath(h.Installed);
        Assert.True(await UpdateInstallMacOS.FinishAsync(h, d));
        Assert.False(Directory.Exists(UpdateInstallPlan.MacNewPath(h.Installed)));
    }

    [Fact]
    public async Task Finish_restores_the_old_bundle_when_even_the_fallback_copy_fails()
    {
        var (h, mac, w, d, _) = Finish();
        w.FailMove = (from, to) => from == h.Staged && to == UpdateInstallPlan.MacNewPath(h.Installed);
        mac.Ditto = 1;
        Assert.False(await UpdateInstallMacOS.FinishAsync(h, d));
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(h.Installed, "Contents", "MacOS", ExeName)));
        Assert.Contains("restored", File.ReadAllText(Path.Combine(h.MarkerDir, "failed")));
        Assert.True(mac.Calls.Any(c => c.File == "/usr/bin/env"), "the old app is relaunched");
    }

    private const int ENotSup = 45;

    [Fact]
    public async Task Finish_removes_a_half_moved_install_before_restoring_on_the_two_rename_fallback()
    {
        var (h, _, w, d, _) = Finish();
        w.SwapRc = _ => ENotSup;
        var newPath = UpdateInstallPlan.MacNewPath(h.Installed);
        // The second rename "succeeds" far enough to leave a directory at the target, then throws.
        w.FailMove = (from, to) =>
        {
            if (from == newPath && to == h.Installed) { Directory.CreateDirectory(h.Installed); File.WriteAllText(Path.Combine(h.Installed, "half"), "x"); return true; }
            return false;
        };
        Assert.False(await UpdateInstallMacOS.FinishAsync(h, d));
        Assert.False(File.Exists(Path.Combine(h.Installed, "half")));
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(h.Installed, "Contents", "MacOS", ExeName)));
    }

    [Fact]
    public async Task Finish_restores_and_reports_when_the_fallback_swap_and_its_restore_both_fail()
    {
        var (h, _, w, d, _) = Finish();
        w.SwapRc = _ => ENotSup;
        w.FailMove = (from, to) => to == h.Installed;
        Assert.False(await UpdateInstallMacOS.FinishAsync(h, d));
        Assert.Contains(_log, l => l.Contains("restore failed"));
    }

    // ---- The atomic swap ---------------------------------------------------

    [Fact]
    public async Task Finish_exchanges_the_two_paths_once_so_the_installed_path_is_never_empty()
    {
        var (h, _, w, d, _) = Finish();
        var newPath = UpdateInstallPlan.MacNewPath(h.Installed);
        var emptyEver = false;
        var inner = d.Swap;
        d = d with { Swap = (a, b) => { emptyEver |= !Directory.Exists(b); var rc = inner(a, b); emptyEver |= !Directory.Exists(b); return rc; } };

        Assert.True(await UpdateInstallMacOS.FinishAsync(h, d));

        Assert.Equal([(newPath, h.Installed)], w.Swaps);
        Assert.False(emptyEver);
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(h.Installed, "Contents", "MacOS", ExeName)));
        // The old bundle, which the exchange left at .new, went to the Trash.
        Assert.Equal("OLD", File.ReadAllText(Directory.GetDirectories(Path.Combine(_root, "Trash")).Single() + "/Contents/MacOS/" + ExeName));
        Assert.False(Directory.Exists(newPath));
    }

    [Fact]
    public async Task Finish_falls_back_to_two_renames_only_on_enotsup_and_says_so()
    {
        var (h, _, w, d, _) = Finish();
        w.SwapRc = _ => ENotSup;
        Assert.True(await UpdateInstallMacOS.FinishAsync(h, d));
        Assert.Single(w.Swaps);
        Assert.Contains(_log, l => l.Contains("renamex_np unsupported"));
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(h.Installed, "Contents", "MacOS", ExeName)));
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "Trash")));
    }

    [Fact]
    public async Task Finish_also_falls_back_on_eopnotsupp()
    {
        var (h, _, w, d, _) = Finish();
        w.SwapRc = _ => 102;
        Assert.True(await UpdateInstallMacOS.FinishAsync(h, d));
        Assert.Contains(_log, l => l.Contains("errno 102"));
    }

    [Fact]
    public async Task Finish_does_not_fall_back_on_any_other_swap_error_and_leaves_the_install_alone()
    {
        var (h, mac, w, d, _) = Finish();
        w.SwapRc = _ => 13; // EACCES
        var before = Hash(h.Installed);

        Assert.False(await UpdateInstallMacOS.FinishAsync(h, d));

        Assert.Equal(before, Hash(h.Installed));
        Assert.Contains("errno 13", File.ReadAllText(Path.Combine(h.MarkerDir, "failed")));
        Assert.DoesNotContain(_log, l => l.Contains("falling back"));
        Assert.False(Directory.Exists(UpdateInstallPlan.MacNewPath(h.Installed)), "the placed copy is cleaned up");
        Assert.Contains(mac.Calls, c => c.File == "/usr/bin/env"); // old app relaunched
    }

    [Fact]
    public async Task A_new_app_that_will_not_start_is_swapped_back_atomically()
    {
        var (h, _, w, d, _) = Finish(newStartsOnItsOwn: false, openStartsIt: false);
        Assert.False(await UpdateInstallMacOS.FinishAsync(h, d));
        var newPath = UpdateInstallPlan.MacNewPath(h.Installed);
        Assert.Equal([(newPath, h.Installed), (newPath, h.Installed)], w.Swaps);
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(h.Installed, "Contents", "MacOS", ExeName)));
        Assert.False(Directory.Exists(newPath), "no second app is left beside the install");
    }

    [Fact]
    public async Task A_failed_swap_back_is_logged_and_the_old_app_is_still_relaunched()
    {
        var (h, mac, w, d, _) = Finish(newStartsOnItsOwn: false, openStartsIt: false);
        w.SwapRc = i => i == 1 ? 5 : 0;
        Assert.False(await UpdateInstallMacOS.FinishAsync(h, d));
        Assert.Contains(_log, l => l.Contains("swap back failed (errno 5)"));
        // The swap back failed, so `.new` still holds the OLD bundle: it must not be deleted.
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(UpdateInstallPlan.MacNewPath(h.Installed), "Contents", "MacOS", ExeName)));
        Assert.True(mac.Calls.Count(c => c.File == "/usr/bin/env") >= 1);
        Assert.True(File.Exists(Path.Combine(h.MarkerDir, "failed")));
    }

    // ---- Keep-alive is reloaded on every failure after the unload ---------

    private async Task<(FakeMac Mac, MacHandoff H)> FailAfterUnload(Action<FakeMac, World, MacHandoff, UpdateInstallMacOS.FinishDeps, Action<UpdateInstallMacOS.FinishDeps>> arrange, bool withPlist = true)
    {
        var plist = Path.Combine(_root, "ka.plist"); if (withPlist) File.WriteAllText(plist, "x");
        var (h, mac, w, d, _) = Finish(newStartsOnItsOwn: false, openStartsIt: false, plist: plist);
        UpdateInstallMacOS.FinishDeps used = d;
        arrange(mac, w, h, d, nd => used = nd);
        Assert.False(await UpdateInstallMacOS.FinishAsync(h, used));
        return (mac, h);
    }

    private static void AssertLoadedBetweenUnloadAndRelaunch(FakeMac mac, string plist)
    {
        var unload = mac.Calls.FindIndex(c => c.Args.Count > 0 && c.Args[0] == "unload");
        var load = mac.Calls.FindIndex(c => c.Args.Count == 2 && c.Args[0] == "load" && c.Args[1] == plist);
        var relaunch = mac.Calls.FindLastIndex(c => c.File == "/usr/bin/env");
        Assert.True(unload >= 0 && load > unload && relaunch > load, "unload, then load, then relaunch");
    }

    [Fact]
    public async Task Keepalive_is_loaded_again_when_the_move_into_place_fails()
    {
        var (mac, _) = await FailAfterUnload((m, w, h, d, set) => set(d with { Move = (f, t) => throw new InvalidOperationException("move") }));
        AssertLoadedBetweenUnloadAndRelaunch(mac, Path.Combine(_root, "ka.plist"));
    }

    [Fact]
    public async Task Keepalive_is_loaded_again_when_the_atomic_swap_errors()
    {
        var (mac, _) = await FailAfterUnload((m, w, h, d, set) => w.SwapRc = _ => 13);
        AssertLoadedBetweenUnloadAndRelaunch(mac, Path.Combine(_root, "ka.plist"));
    }

    [Fact]
    public async Task Keepalive_is_loaded_again_when_the_new_app_will_not_start()
    {
        var (mac, _) = await FailAfterUnload((m, w, h, d, set) => { });
        AssertLoadedBetweenUnloadAndRelaunch(mac, Path.Combine(_root, "ka.plist"));
    }

    [Fact]
    public async Task Keepalive_is_loaded_again_when_the_post_swap_step_throws()
    {
        var (mac, _) = await FailAfterUnload((m, w, h, d, set) =>
        {
            var baseHook = m.Hook!;
            m.Hook = c => c.Args.Count > 0 && c.Args[0] == "--keepalive-only" ? throw new InvalidOperationException("script vanished") : baseHook(c);
        });
        AssertLoadedBetweenUnloadAndRelaunch(mac, Path.Combine(_root, "ka.plist"));
    }

    [Fact]
    public async Task Keepalive_is_loaded_again_when_stopping_the_old_app_throws()
    {
        var (mac, _) = await FailAfterUnload((m, w, h, d, set) => set(d with { Signal = (p, f) => throw new InvalidOperationException("kill") }));
        AssertLoadedBetweenUnloadAndRelaunch(mac, Path.Combine(_root, "ka.plist"));
    }

    [Fact]
    public async Task Keepalive_is_not_loaded_when_it_was_never_unloaded()
    {
        // The control: no plist on this machine, so nothing was unloaded and nothing may be loaded.
        var (mac, _) = await FailAfterUnload((m, w, h, d, set) => { }, withPlist: false);
        Assert.False(mac.Ran("launchctl", "unload"));
        Assert.False(mac.Ran("launchctl", "load"));
        Assert.Contains(mac.Calls, c => c.File == "/usr/bin/env");
    }

    [Fact]
    public async Task Finish_launches_the_installed_app_itself_when_launchd_did_not()
    {
        var (h, mac, _, d, _) = Finish(newStartsOnItsOwn: false, openStartsIt: true);
        Assert.True(await UpdateInstallMacOS.FinishAsync(h, d));
        Assert.Equal(["-u", "CLAUDE_CONFIG_DIR", "/usr/bin/open", "-a", h.Installed], mac.Calls.Single(c => c.File == "/usr/bin/env").Args);
    }

    [Fact]
    public async Task Finish_puts_the_old_version_back_when_the_new_one_will_not_start()
    {
        var (h, _, _, d, _) = Finish(newStartsOnItsOwn: false, openStartsIt: false);
        Assert.False(await UpdateInstallMacOS.FinishAsync(h, d));
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(h.Installed, "Contents", "MacOS", ExeName)));
        Assert.Contains("did not start", File.ReadAllText(Path.Combine(h.MarkerDir, "failed")));
        Assert.False(File.Exists(Path.Combine(h.MarkerDir, "finished")));
    }

    [Fact]
    public async Task Finish_logs_a_failed_keepalive_reconcile_and_carries_on()
    {
        var (h, mac, _, d, _) = Finish();
        mac.Repair = 2;
        Assert.True(await UpdateInstallMacOS.FinishAsync(h, d));
        Assert.Contains(_log, l => l.Contains("keep-alive reconcile exited 2"));
    }

    [Fact]
    public async Task Finish_leaves_the_old_bundle_in_place_when_the_trash_refuses_it()
    {
        var (h, _, w, d, _) = Finish();
        w.FailMove = (_, to) => to.StartsWith(Path.Combine(_root, "Trash"));
        Assert.True(await UpdateInstallMacOS.FinishAsync(h, d));
        Assert.Contains(_log, l => l.Contains("could not trash it"));
        Assert.True(Directory.Exists(UpdateInstallPlan.MacNewPath(h.Installed)), "the old bundle stays where the exchange left it");
    }

    [Fact]
    public async Task Finish_with_nothing_installed_yet_just_moves_the_new_bundle_in()
    {
        var (h, _, _, d, _) = Finish();
        Directory.Delete(h.Installed, true);
        Assert.True(await UpdateInstallMacOS.FinishAsync(h, d));
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(h.Installed, "Contents", "MacOS", ExeName)));
    }

    [Fact]
    public async Task Finish_survives_no_dmg_to_clean_up()
    {
        var (h, _, _, d, _) = Finish();
        File.Delete(Path.Combine(h.MarkerDir, "u.dmg"));
        Assert.True(await UpdateInstallMacOS.FinishAsync(h, d));
    }

    [Fact]
    public async Task A_failed_cleanup_is_logged_and_the_update_still_succeeds()
    {
        var (h, _, w, d, _) = Finish();
        Directory.CreateDirectory(h.Staged); // still present: the cross-volume case
        // Move works, so staged is consumed; recreate it afterwards via the move hook.
        w.FailMove = null;
        var d2 = d with { Move = (from, to) => { Directory.Move(from, to); if (to == UpdateInstallPlan.MacNewPath(h.Installed)) Directory.CreateDirectory(h.Staged); } };
        w.FailDelete = true;

        Assert.True(await UpdateInstallMacOS.FinishAsync(h, d2));

        Assert.Contains(_log, l => l.Contains("cleanup left files behind"));
        Assert.True(File.Exists(Path.Combine(h.MarkerDir, "finished")));
    }

    [Fact]
    public async Task A_failure_after_the_swap_restores_the_old_bundle_and_relaunches_it()
    {
        var (h, mac, w, d, _) = Finish();
        var baseHook = mac.Hook!;
        mac.Hook = c => c.Args.Count > 0 && c.Args[0] == "--keepalive-only" ? throw new InvalidOperationException("script vanished") : baseHook(c);

        Assert.False(await UpdateInstallMacOS.FinishAsync(h, d));

        Assert.Equal("OLD", File.ReadAllText(Path.Combine(h.Installed, "Contents", "MacOS", ExeName)));
        Assert.Contains("script vanished", File.ReadAllText(Path.Combine(h.MarkerDir, "failed")));
        Assert.Contains(mac.Calls, c => c.File == "/usr/bin/env");
        Assert.Equal(2, w.Swaps.Count);
    }

    [Fact]
    public async Task A_failed_restore_after_a_two_rename_swap_is_logged_not_thrown()
    {
        var (h, mac, w, d, _) = Finish();
        w.SwapRc = _ => ENotSup;
        var baseHook = mac.Hook!;
        mac.Hook = c => c.Args.Count > 0 && c.Args[0] == "--keepalive-only" ? throw new InvalidOperationException("boom") : baseHook(c);
        w.FailMove = (from, to) => to == h.Installed && from.Contains(".old-");

        Assert.False(await UpdateInstallMacOS.FinishAsync(h, d));

        Assert.Contains(_log, l => l.Contains("restore failed"));
        Assert.True(File.Exists(Path.Combine(h.MarkerDir, "failed")));
    }

    [Fact]
    public void Stage_refuses_when_the_plist_cannot_be_read()
    {
        var mac = new FakeMac { Hook = c => System.IO.Path.GetFileName(c.File) == "plutil" ? (1, "") : null };
        var (app, error) = Stage(mac, out _);
        Assert.Null(app);
        Assert.Contains("bundle identifier", error);
    }

    [Fact]
    public void Stage_refuses_when_the_version_cannot_be_read()
    {
        var mac = new FakeMac { Hook = c => System.IO.Path.GetFileName(c.File) == "plutil" && c.Args[1] == "CFBundleShortVersionString" ? (1, "") : null };
        var (app, error) = Stage(mac, out _);
        Assert.Null(app);
        Assert.Contains("reports version", error);
    }

    [Fact]
    public async Task Finish_reports_when_even_the_failed_marker_cannot_be_written()
    {
        var (h, mac, _, d, _) = Finish();
        mac.Verify = 1;
        Directory.Delete(h.MarkerDir, true);
        File.WriteAllText(h.MarkerDir, "a file where the folder should be");
        Assert.False(await UpdateInstallMacOS.FinishAsync(h, d));
        Assert.Contains(_log, l => l.Contains("could not write the failed marker"));
    }

    [Fact]
    public async Task A_launcher_that_cannot_start_does_not_stop_the_failed_marker_being_written()
    {
        // Failure before any swap, with the relaunch of the old app throwing
        // (RealExec throws when a program cannot be started at all).
        var (h, mac, _, d, _) = Finish();
        var baseHook = mac.Hook!;
        mac.Hook = c => c.File == "/usr/bin/env" ? throw new System.ComponentModel.Win32Exception("no such file") : baseHook(c);
        var d2 = d with { Move = (from, to) => throw new InvalidOperationException("cannot move aside") };

        Assert.False(await UpdateInstallMacOS.FinishAsync(h, d2));

        Assert.True(File.Exists(Path.Combine(h.MarkerDir, "failed")));
        Assert.Contains(_log, l => l.Contains("could not be run"));
    }

    // ---- The installer, end to end --------------------------------------

    private MacUpdateInstaller Installer(FakeMac mac, MacUpdateInstaller.DownloadVerified? download = null,
        string process = "/Applications/Orbweaver.app/Contents/MacOS/Orbweaver", bool writable = true, Func<Task>? poll = null) =>
        new(download ?? ((r, dir, p, ct) => { var f = Path.Combine(dir, "u.dmg"); File.WriteAllText(f, "d"); return Task.FromResult(f); }),
            process, _ => writable, Path.Combine(_root, "updates"), null, mac.Run, poll ?? (() => Task.CompletedTask), _log.Add, 4242);

    private static readonly UpdateRelease Release = new("v2.0.0", "2.0.0", false, "", []);

    [Fact]
    public async Task Installer_refuses_a_release_that_predates_the_finisher_before_downloading()
    {
        var called = false;
        var mac = new FakeMac();
        var outcome = await Installer(mac, (r, d, p, c) => { called = true; return Task.FromResult(""); })
            .InstallAsync(new UpdateRelease("v1.0.3", "1.0.3", false, "", []), new Sink(), default);
        Assert.Equal(InstallStatus.Refused, outcome.Status);
        Assert.Contains("releases page", outcome.Message);
        Assert.False(called);
        Assert.Empty(mac.Calls);
    }
    private sealed class Sink : IProgress<UpdateProgress> { public List<UpdateStage> Stages = []; public void Report(UpdateProgress v) => Stages.Add(v.Stage); }

    [Fact]
    public async Task Installer_refuses_without_touching_the_network()
    {
        var called = false;
        var outcome = await Installer(new FakeMac(), (r, d, p, c) => { called = true; return Task.FromResult(""); }, writable: false)
            .InstallAsync(Release, new Sink(), default);
        Assert.Equal(InstallStatus.Refused, outcome.Status);
        Assert.False(called);
    }

    [Fact]
    public async Task Installer_reports_a_failed_download_without_staging()
    {
        var mac = new FakeMac();
        var outcome = await Installer(mac, (r, d, p, c) => throw new IOException("offline")).InstallAsync(Release, new Sink(), default);
        Assert.Equal(InstallStatus.Failed, outcome.Status);
        Assert.Contains("offline", outcome.Message);
        Assert.Empty(mac.Calls);
    }

    [Fact]
    public async Task Installer_lets_cancellation_through()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Installer(new FakeMac(), (r, d, p, c) => throw new OperationCanceledException()).InstallAsync(Release, new Sink(), default));
    }

    [Fact]
    public async Task Installer_reports_a_refused_stage()
    {
        var outcome = await Installer(new FakeMac { Team = "X" }).InstallAsync(Release, new Sink(), default);
        Assert.Equal(InstallStatus.Failed, outcome.Status);
        Assert.Contains("Developer ID team", outcome.Message);
    }

    [Fact]
    public async Task Installer_stages_launches_the_finisher_and_hands_off()
    {
        var mac = new FakeMac();
        var sink = new Sink();
        var marker = Path.Combine(_root, "updates", "2.0.0");
        var outcome = await Installer(mac, poll: () => { File.WriteAllText(Path.Combine(marker, "finished"), "x"); return Task.CompletedTask; })
            .InstallAsync(Release, sink, default);

        Assert.Equal(InstallStatus.HandedOff, outcome.Status);
        Assert.Equal([UpdateStage.Staging, UpdateStage.Restarting], sink.Stages);
        var handoff = UpdateInstallPlan.MacParseHandoff(File.ReadAllText(Path.Combine(marker, "handoff.json")))!;
        Assert.Equal("/Applications/Orbweaver.app", handoff.Installed);
        Assert.Equal(4242, handoff.Pid);
    }
}
