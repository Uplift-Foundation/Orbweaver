using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace Orbweaver.Tests;

// CB-308: the Windows half of the updater. Argv and decisions are asserted
// literally; the installer is driven end to end against a scratch folder with
// a fake process start, so setup.exe is never run for real.
public class UpdateInstallWindowsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orb-upd-win-" + Guid.NewGuid().ToString("N"));
    private const string Installed = @"C:\Users\ada\AppData\Local\Programs\Orbweaver\Orbweaver.exe";

    public UpdateInstallWindowsTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static UpdateRelease Release(string version = "1.2.0") =>
        new("v" + version, version, false, "", []);

    private sealed class Run
    {
        public List<string> Log = [];
        public List<UpdateProgress> Progress = [];
        public ProcessStartInfo? Started;
        public int Flushes, Shutdowns;
        public List<string> Order = [];
    }

    private UpdateInstallWindows Make(
        Run run, string? processPath = Installed, string current = "1.1.0",
        UpdateInstallWindows.DownloadVerified? download = null, Func<ProcessStartInfo, int>? start = null)
    {
        download ??= (r, dir, p, ct) =>
        {
            var path = Path.Combine(dir, "Orbweaver-" + r.Version + "-win-x64-setup.exe");
            File.WriteAllText(path, "x");
            return Task.FromResult(path);
        };
        start ??= info => { run.Started = info; run.Order.Add("start"); return 4242; };
        return new UpdateInstallWindows(download, processPath, _root, current,
            () => { run.Flushes++; run.Order.Add("flush"); },
            start,
            () => { run.Shutdowns++; run.Order.Add("shutdown"); },
            run.Log.Add);
    }

    private sealed class Sink : IProgress<UpdateProgress>
    {
        private readonly List<UpdateProgress> _l;
        public Sink(List<UpdateProgress> l) => _l = l;
        public void Report(UpdateProgress v) => _l.Add(v);
    }

    // ---- plan ----

    [Fact]
    public void Setup_argv_is_literal_and_ordered() =>
        Assert.Equal(
            new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/RELAUNCH=1", @"/LOG=C:\a b\setup.log" },
            UpdateInstallPlan.WinSetupArgs(@"C:\a b\setup.log"));

    [Fact]
    public void Start_info_runs_setup_directly_without_the_shell()
    {
        var info = UpdateInstallPlan.WinSetupStartInfo(@"C:\u\1.2.0\setup.exe", @"C:\u\1.2.0\setup.log");
        Assert.Equal(@"C:\u\1.2.0\setup.exe", info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.Equal(UpdateInstallPlan.WinSetupArgs(@"C:\u\1.2.0\setup.log"), info.ArgumentList);
    }

    [Theory]
    [InlineData(@"C:\Users\ada\AppData\Local\Programs\Orbweaver\Orbweaver.exe")]
    [InlineData(@"C:\Users\ada\AppData\Local\Apps\Tools\orbweaver.EXE")]
    public void Preflight_accepts_the_installed_exe(string path) =>
        Assert.Null(UpdateInstallPlan.WinPreflight(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    [InlineData(@"C:\x\OrbweaverRenamed.exe")]
    public void Preflight_refuses_anything_else_with_instructions(string? path) =>
        Assert.Contains("installer", UpdateInstallPlan.WinPreflight(path));

    [Theory]
    [InlineData("1.2.0", "1.2.0", "Completed")]
    [InlineData("1.2.0", "1.3.0", "Completed")]
    [InlineData("1.2.0", "1.2.0+abc", "Completed")]
    [InlineData("1.2.0", "1.1.0", "DidNotComplete")]
    [InlineData("1.2.0", "1.2.0-beta", "DidNotComplete")]
    [InlineData("1.2.0", "garbage", "DidNotComplete")]
    [InlineData("garbage", "1.2.0", "DidNotComplete")]
    public void Aftermath_is_judged_by_the_running_version(string to, string current, string expected)
    {
        var pending = new UpdateInstallPlan.WinPendingUpdate("1.1.0", to, @"C:\l.log");
        Assert.Equal(expected, UpdateInstallPlan.WinDecideAftermath(pending, current).Kind.ToString());
    }

    [Fact]
    public void No_pending_update_means_nothing_to_say() =>
        Assert.Equal(UpdateInstallPlan.WinAftermathKind.Nothing, UpdateInstallPlan.WinDecideAftermath(null, "1.1.0").Kind);

    [Fact]
    public void Pending_marker_round_trips_and_rejects_junk()
    {
        var p = new UpdateInstallPlan.WinPendingUpdate("1.1.0", "1.2.0", @"C:\a b\setup.log");
        Assert.Equal(p, UpdateInstallPlan.WinParsePending(UpdateInstallPlan.WinPendingJson(p)));
        Assert.Null(UpdateInstallPlan.WinParsePending(null));
        Assert.Null(UpdateInstallPlan.WinParsePending("  "));
        Assert.Null(UpdateInstallPlan.WinParsePending("{not json"));
        Assert.Null(UpdateInstallPlan.WinParsePending("{\"From\":\"\",\"To\":\"1.2.0\",\"LogPath\":\"x\"}"));
        Assert.Null(UpdateInstallPlan.WinParsePending("null"));
    }

    // ---- InstallAsync ----

    [Fact]
    public async Task Happy_path_flushes_then_starts_setup_then_shuts_down()
    {
        var run = new Run();
        var outcome = await Make(run).InstallAsync(Release(), new Sink(run.Progress), default);

        Assert.Equal(InstallStatus.HandedOff, outcome.Status);
        Assert.Equal(["flush", "start", "shutdown"], run.Order);
        var dir = Path.Combine(_root, "1.2.0");
        Assert.Equal(Path.Combine(dir, "Orbweaver-1.2.0-win-x64-setup.exe"), run.Started!.FileName);
        Assert.Contains("/RELAUNCH=1", run.Started.ArgumentList);
        Assert.Contains("/LOG=" + Path.Combine(dir, "setup.log"), run.Started.ArgumentList);
        Assert.Equal([UpdateStage.Installing, UpdateStage.Restarting], run.Progress.Select(p => p.Stage));
        var pending = UpdateInstallPlan.WinParsePending(File.ReadAllText(Path.Combine(_root, "pending.json")));
        Assert.Equal(new UpdateInstallPlan.WinPendingUpdate("1.1.0", "1.2.0", Path.Combine(dir, "setup.log")), pending);
    }

    [Fact]
    public async Task A_refused_preflight_touches_nothing()
    {
        var run = new Run();
        var outcome = await Make(run, processPath: @"C:\dotnet\dotnet.exe").InstallAsync(Release(), new Sink(run.Progress), default);

        Assert.Equal(InstallStatus.Refused, outcome.Status);
        Assert.Equal(0, run.Flushes + run.Shutdowns);
        Assert.Null(run.Started);
        Assert.False(Directory.Exists(Path.Combine(_root, "1.2.0")));
        Assert.Contains(run.Log, l => l.StartsWith("refused:"));
    }

    [Fact]
    public async Task A_failed_download_fails_without_starting_setup_or_leaving_a_marker()
    {
        var run = new Run();
        var outcome = await Make(run, download: (_, _, _, _) => throw new UpdateException(UpdateFailure.ChecksumMismatch, "bad hash"))
            .InstallAsync(Release(), new Sink(run.Progress), default);

        Assert.Equal(InstallStatus.Failed, outcome.Status);
        Assert.Contains("bad hash", outcome.Message);
        Assert.Null(run.Started);
        Assert.Equal(0, run.Shutdowns);
        Assert.False(File.Exists(Path.Combine(_root, "pending.json")));
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var run = new Run();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Make(run, download: (_, _, _, ct) => throw new OperationCanceledException(ct))
                .InstallAsync(Release(), new Sink(run.Progress), default));
        Assert.Equal(0, run.Shutdowns);
    }

    [Theory]
    [InlineData(typeof(Win32Exception))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(IOException))]
    public async Task A_setup_that_cannot_start_leaves_the_app_running_and_no_marker(Type exceptionType)
    {
        var run = new Run();
        var outcome = await Make(run, start: _ => throw (Exception)Activator.CreateInstance(exceptionType, "denied")!)
            .InstallAsync(Release(), new Sink(run.Progress), default);

        Assert.Equal(InstallStatus.Failed, outcome.Status);
        Assert.Contains("Nothing was changed", outcome.Message);
        Assert.Equal(0, run.Shutdowns);
        Assert.False(File.Exists(Path.Combine(_root, "pending.json")));
    }

    [Fact]
    public async Task An_unwritable_marker_fails_before_setup_starts()
    {
        var run = new Run();
        // A directory where the marker file must go makes the write throw.
        Directory.CreateDirectory(Path.Combine(_root, "pending.json"));
        var outcome = await Make(run).InstallAsync(Release(), new Sink(run.Progress), default);

        Assert.Equal(InstallStatus.Failed, outcome.Status);
        Assert.Null(run.Started);
        Assert.Equal(0, run.Shutdowns);
    }

    // ---- next launch ----

    private static string Marker(string root) => Path.Combine(root, "pending.json");

    private string WritePending(string to = "1.2.0")
    {
        var dir = Path.Combine(_root, to);
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "setup.log");
        File.WriteAllText(log, "log");
        File.WriteAllText(Path.Combine(dir, "setup.exe"), "x");
        File.WriteAllText(Marker(_root), UpdateInstallPlan.WinPendingJson(
            new UpdateInstallPlan.WinPendingUpdate("1.1.0", to, log)));
        return log;
    }

    [Fact]
    public void Launch_after_a_failed_update_reports_it_once_and_keeps_the_log()
    {
        var log = WritePending();
        var lines = new List<string>();

        var first = UpdateInstallWindows.ReconcileAtLaunch(_root, "1.1.0", lines.Add);
        Assert.Equal(log, first!.LogPath);
        Assert.Equal("1.2.0", first.To);
        Assert.True(File.Exists(log));
        Assert.False(File.Exists(Marker(_root)));
        Assert.Contains(lines, l => l.Contains("did not complete"));

        Assert.Null(UpdateInstallWindows.ReconcileAtLaunch(_root, "1.1.0", lines.Add));
    }

    [Fact]
    public void Launch_after_a_good_update_says_nothing_and_removes_the_download()
    {
        var log = WritePending();
        var lines = new List<string>();

        Assert.Null(UpdateInstallWindows.ReconcileAtLaunch(_root, "1.2.0", lines.Add));
        Assert.False(File.Exists(Marker(_root)));
        Assert.False(Directory.Exists(Path.GetDirectoryName(log)!));
        Assert.Contains(lines, l => l.Contains("completed"));
    }

    [Fact]
    public void Launch_with_nothing_pending_does_nothing()
    {
        var lines = new List<string>();
        Assert.Null(UpdateInstallWindows.ReconcileAtLaunch(_root, "1.1.0", lines.Add));
        Assert.Empty(lines);
    }

    [Fact]
    public void Launch_with_an_unreadable_marker_discards_it_silently()
    {
        File.WriteAllText(Marker(_root), "{garbage");
        Assert.Null(UpdateInstallWindows.ReconcileAtLaunch(_root, "1.1.0", _ => { }));
        Assert.False(File.Exists(Marker(_root)));
    }

    [Fact]
    public void Launch_survives_a_marker_that_cannot_be_read()
    {
        // A directory at the marker path: Exists is false for File, so nothing pending.
        Directory.CreateDirectory(Marker(_root));
        Assert.Null(UpdateInstallWindows.ReconcileAtLaunch(_root, "1.1.0", _ => { }));
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void An_unreadable_marker_is_treated_as_nothing_pending(Type ex)
    {
        WritePending();
        var r = UpdateInstallWindows.ReconcileAtLaunch(_root, "1.1.0", _ => { },
            readFile: _ => throw (Exception)Activator.CreateInstance(ex, "locked")!);
        Assert.Null(r);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void A_marker_that_cannot_be_deleted_does_not_stop_the_verdict(Type ex)
    {
        WritePending();
        var r = UpdateInstallWindows.ReconcileAtLaunch(_root, "1.1.0", _ => { },
            deleteFile: _ => throw (Exception)Activator.CreateInstance(ex, "locked")!);
        Assert.Equal("1.2.0", r!.To);

        // An undeletable unreadable marker (the junk-marker path) is also survived.
        File.WriteAllText(Marker(_root), "{garbage");
        Assert.Null(UpdateInstallWindows.ReconcileAtLaunch(_root, "1.1.0", _ => { },
            deleteFile: _ => throw (Exception)Activator.CreateInstance(ex, "locked")!));
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void A_download_folder_that_cannot_be_removed_after_success_is_tolerated(Type ex)
    {
        WritePending();
        var lines = new List<string>();
        Assert.Null(UpdateInstallWindows.ReconcileAtLaunch(_root, "1.2.0", lines.Add,
            deleteDirectory: _ => throw (Exception)Activator.CreateInstance(ex, "locked")!));
        Assert.Contains(lines, l => l.Contains("completed"));
    }
}
