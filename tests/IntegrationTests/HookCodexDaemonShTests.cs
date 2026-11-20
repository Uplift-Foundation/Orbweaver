using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-316: Codex 0.167+ runs its turns, and so its hooks, inside a shared
// `codex app-server` daemon. The hook's walk used to stop at the first `codex`
// ancestor — the daemon — and record the daemon's pid, and the daemon's own
// TMUX/TMUX_PANE as the session's pane. Measured on a real machine: a pane that
// did not exist, on another socket. A phone typing into "this session's pane"
// would have typed into someone else's.
//
// Staged here as a real process tree: a tty-less parent titled `codex` with
// `app-server` on its command line and a bogus TMUX in its environment, runs
// the hook; separate fake TUIs titled `codex` sit in the payload's folder. The
// paired controls are an ordinary (non-daemon) codex parent, which must still
// be recorded with its pane exactly as before, and two TUIs in one folder,
// which must record nothing rather than guess.
public class HookCodexDaemonShTests
{
    private static readonly string HookScript = FindHook();

    private static string FindHook()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrbweaverHook.sh"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("no OrbweaverHook.sh"), "OrbweaverHook.sh");
    }

    // The same tty-less, argv[0]-titled parent HookPidWalkShTests uses, running
    // the hook as its child.
    private const string FakeParentPy = """
        import os
        import sys

        os.setsid()

        pidfile, title, hook, agent, state = sys.argv[1:6]
        decoys = sys.argv[6:]

        script = 'printf %s "$$" > "$1"; bash "$2" "$3" "$4"'

        os.execv("/bin/bash", [title, "-c", script, "_", pidfile, hook, agent, state] + decoys)
        """;

    // A stand-in TUI: titled `codex`, sitting in a folder, doing nothing.
    private const string FakeTuiPy = """
        import os
        import sys

        os.chdir(sys.argv[1])
        os.execv("/bin/sleep", ["codex", "30"])
        """;

    private sealed record Run(int ExitCode, string Stdout, string Stderr, int ParentPid, SessionStatus? Status, string Log = "");

    private static string Python() => PythonUnixFactAttribute.Python3Path()
        ?? throw new InvalidOperationException("python3 disappeared after the skip check");

    private static Process StartTui(string dir, string folder)
    {
        var script = Path.Combine(dir, "tui-" + Guid.NewGuid().ToString("N")[..6] + ".py");
        File.WriteAllText(script, FakeTuiPy);
        var psi = new ProcessStartInfo(Python()) { UseShellExecute = false };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(folder);
        var p = Process.Start(psi)!;
        // Wait until it has exec'd into its title, or the hook could see python.
        for (var i = 0; i < 50; i++)
        {
            var ps = Process.Start(new ProcessStartInfo("/bin/ps", ["-o", "comm=", "-p", p.Id.ToString()]) { RedirectStandardOutput = true })!;
            var comm = ps.StandardOutput.ReadToEnd().Trim();
            ps.WaitForExit();
            if (Path.GetFileName(comm) == "codex") break;
            Thread.Sleep(50);
        }
        return p;
    }

    private static Run RunHook(string tmp, string title, string[] decoys, string folder, Action<ProcessStartInfo>? configure = null)
    {
        var fake = Path.Combine(tmp, "fake-parent.py");
        var pidFile = Path.Combine(tmp, "parent.pid");
        File.WriteAllText(fake, FakeParentPy);
        var psi = new ProcessStartInfo(Python())
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in new[] { fake, pidFile, title, HookScript, "codex", "idle" }.Concat(decoys)) psi.ArgumentList.Add(a);
        psi.Environment["TMPDIR"] = tmp;
        psi.Environment["ORBWEAVER_STATUS_ROOT"] = tmp;
        psi.Environment["CODEX_HOME"] = Directory.CreateDirectory(Path.Combine(tmp, "codex-home")).FullName;
        // The daemon's inherited terminal: a pane that is not this session's.
        psi.Environment["TMUX"] = "/tmp/not-this-session.sock,1,0";
        psi.Environment["TMUX_PANE"] = "%42";
        psi.Environment.Remove("ITERM_SESSION_ID");
        psi.Environment.Remove("GROK_SESSION_ID");
        psi.Environment.Remove("GROK_HOOK_EVENT");
        psi.Environment.Remove("GROK_WORKSPACE_ROOT");
        configure?.Invoke(psi);

        using var p = Process.Start(psi)!;
        try
        {
            p.StandardInput.Write(JsonSerializer.Serialize(new { session_id = "cx1", cwd = folder, transcript_path = "" }));
            p.StandardInput.Close();
        }
        catch (IOException) { }
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        var file = Path.Combine(tmp, "orbweaver", "cx1.txt");
        var status = File.Exists(file) ? JsonSerializer.Deserialize<SessionStatus>(File.ReadAllText(file)) : null;
        var parent = File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) ? pid : 0;
        var logFile = Path.Combine(tmp, "orbweaver", "hook.log");
        return new Run(p.ExitCode, stdout, stderr, parent, status, File.Exists(logFile) ? File.ReadAllText(logFile) : "");
    }

    private static void Silent(Run r)
    {
        Assert.Equal((0, "", ""), (r.ExitCode, r.Stdout, r.Stderr));
        Assert.NotNull(r.Status);
    }

    [PythonUnixFact]
    public void Under_the_daemon_the_one_tui_in_the_folder_is_the_session_and_the_daemons_pane_is_dropped()
    {
        var tmp = Directory.CreateTempSubdirectory("cb316-cxd-").FullName;
        var folder = Directory.CreateDirectory(Path.Combine(tmp, "project")).FullName;
        var elsewhere = Directory.CreateDirectory(Path.Combine(tmp, "other")).FullName;
        using var tui = StartTui(tmp, folder);
        using var other = StartTui(tmp, elsewhere);      // a TUI in another folder must not be chosen
        try
        {
            var r = RunHook(tmp, "codex", ["app-server", "--listen", "unix://"], folder);
            Silent(r);
            Assert.Equal(tui.Id, r.Status!.SessionPid);
            Assert.NotEqual(r.ParentPid, r.Status.SessionPid);
            Assert.Equal(("", "", ""), (r.Status.TmuxPane, r.Status.TmuxSocket, r.Status.TermProgram));
        }
        finally
        {
            tui.Kill();
            other.Kill();
        }
    }

    [PythonUnixFact]
    public void Two_tuis_in_one_folder_record_no_pid_and_no_pane()
    {
        var tmp = Directory.CreateTempSubdirectory("cb316-cxd-").FullName;
        var folder = Directory.CreateDirectory(Path.Combine(tmp, "project")).FullName;
        using var a = StartTui(tmp, folder);
        using var b = StartTui(tmp, folder);
        try
        {
            var r = RunHook(tmp, "codex", ["app-server", "--listen", "unix://"], folder);
            Silent(r);
            Assert.Equal(0, r.Status!.SessionPid);
            Assert.Equal("", r.Status.TmuxPane);
        }
        finally
        {
            a.Kill();
            b.Kill();
        }
    }

    // The control: a codex parent that is not the daemon is still the session,
    // and its own TMUX is still its pane — the fix must not have been bought by
    // dropping every Codex pane.
    [PythonUnixFact]
    public void An_ordinary_codex_parent_keeps_its_pid_and_its_pane()
    {
        var tmp = Directory.CreateTempSubdirectory("cb316-cxd-").FullName;
        var folder = Directory.CreateDirectory(Path.Combine(tmp, "project")).FullName;
        var r = RunHook(tmp, "codex", ["--model", "gpt-5"], folder);
        Silent(r);
        Assert.Equal(r.ParentPid, r.Status!.SessionPid);
        Assert.Equal(("%42", "/tmp/not-this-session.sock"), (r.Status.TmuxPane, r.Status.TmuxSocket));
    }

    // ---- CB-394: the Codex-daemon lookups are bounded, and every run is logged.

    // A tool on PATH ahead of the real one. `exec sleep` so the hung process is
    // the one the hook's deadline kills, not a shell with a child left behind.
    private static string FakeBin(string tmp, string name, string body)
    {
        var bin = Directory.CreateDirectory(Path.Combine(tmp, "fakebin")).FullName;
        var path = Path.Combine(bin, name);
        File.WriteAllText(path, "#!/bin/bash\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return bin;
    }

    private static Action<ProcessStartInfo> Path1(string bin, string deciseconds = "5") => psi =>
    {
        psi.Environment["PATH"] = bin + Path.PathSeparator + psi.Environment["PATH"];
        psi.Environment["ORBWEAVER_HOOK_LOOKUP_TIMEOUT_DS"] = deciseconds;
    };

    [PythonUnixFact]
    public void A_hanging_tmux_still_writes_the_status_within_the_deadline_with_no_pane()
    {
        var tmp = Directory.CreateTempSubdirectory("cb394-").FullName;
        var folder = Directory.CreateDirectory(Path.Combine(tmp, "project")).FullName;
        var bin = FakeBin(tmp, "tmux", "exec sleep 60");
        using var tui = StartTui(tmp, folder);
        try
        {
            var sw = Stopwatch.StartNew();
            var r = RunHook(tmp, "codex", ["app-server", "--listen", "unix://"], folder, Path1(bin));
            sw.Stop();
            Silent(r);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"hook took {sw.Elapsed}");
            Assert.Equal(tui.Id, r.Status!.SessionPid);                 // ps and lsof still answered
            Assert.Equal(("", "", "", ""), (r.Status.TmuxPane, r.Status.TmuxSocket, r.Status.TmuxBin, r.Status.TermProgram));
            Assert.Contains(" start ", r.Log);
            Assert.Contains(" end ", r.Log);
            Assert.Contains("timed_out=tmux", r.Log);
        }
        finally { tui.Kill(); }
    }

    // The control: a tmux that answers fills the pane fields, so the test above
    // is about the hang and not about a setup that never reached tmux.
    [PythonUnixFact]
    public void A_tmux_that_answers_fills_the_pane_fields()
    {
        var tmp = Directory.CreateTempSubdirectory("cb394-").FullName;
        var folder = Directory.CreateDirectory(Path.Combine(tmp, "project")).FullName;
        using var tui = StartTui(tmp, folder);
        try
        {
            var bin = FakeBin(tmp, "tmux", $"echo '{tui.Id} %7 /tmp/cb394.sock'");
            var r = RunHook(tmp, "codex", ["app-server", "--listen", "unix://"], folder, Path1(bin));
            Silent(r);
            Assert.Equal(tui.Id, r.Status!.SessionPid);
            Assert.Equal(("%7", "/tmp/cb394.sock", "tmux"), (r.Status.TmuxPane, r.Status.TmuxSocket, r.Status.TermProgram));
            Assert.Contains("timed_out=none", r.Log);
        }
        finally { tui.Kill(); }
    }

    // A hung lsof means the folder's TUIs cannot be counted, so no pid is
    // recorded; the second TUI also exercises the skip-after-timeout path.
    [PythonUnixFact]
    public void A_hanging_lsof_records_no_pid_and_still_writes_the_status()
    {
        var tmp = Directory.CreateTempSubdirectory("cb394-").FullName;
        var folder = Directory.CreateDirectory(Path.Combine(tmp, "project")).FullName;
        var elsewhere = Directory.CreateDirectory(Path.Combine(tmp, "other")).FullName;
        var bin = FakeBin(tmp, "lsof", "exec sleep 60");
        using var tui = StartTui(tmp, folder);
        using var other = StartTui(tmp, elsewhere);
        try
        {
            var r = RunHook(tmp, "codex", ["app-server", "--listen", "unix://"], folder, Path1(bin));
            Silent(r);
            Assert.Equal(0, r.Status!.SessionPid);
            Assert.Equal("", r.Status.TmuxPane);
            Assert.Contains("timed_out=lsof", r.Log);
        }
        finally { tui.Kill(); other.Kill(); }
    }

    // A tmux that answers with nothing for the TUI's pid makes the ancestor
    // walk step through ps -o ppid (the bounded ppid lookup) until it runs out.
    [PythonUnixFact]
    public void A_tmux_that_knows_no_ancestor_leaves_the_pane_empty()
    {
        var tmp = Directory.CreateTempSubdirectory("cb394-").FullName;
        var folder = Directory.CreateDirectory(Path.Combine(tmp, "project")).FullName;
        var bin = FakeBin(tmp, "tmux", "echo '1 %9 /tmp/none.sock'");
        using var tui = StartTui(tmp, folder);
        try
        {
            var r = RunHook(tmp, "codex", ["app-server", "--listen", "unix://"], folder, Path1(bin, "not-a-number"));
            Silent(r);
            Assert.Equal(tui.Id, r.Status!.SessionPid);
            Assert.Equal("", r.Status.TmuxPane);
        }
        finally { tui.Kill(); }
    }

    // Size-bounded: an oversize log moves aside and a fresh one starts.
    [PythonUnixFact]
    public void The_run_log_is_size_bounded()
    {
        var tmp = Directory.CreateTempSubdirectory("cb394-").FullName;
        var folder = Directory.CreateDirectory(Path.Combine(tmp, "project")).FullName;
        Directory.CreateDirectory(Path.Combine(tmp, "orbweaver"));
        File.WriteAllText(Path.Combine(tmp, "orbweaver", "hook.log"), new string('x', 70_000) + "\n");
        var r = RunHook(tmp, "codex", ["--model", "gpt-5"], folder);
        Silent(r);
        Assert.True(File.Exists(Path.Combine(tmp, "orbweaver", "hook.log.1")));
        Assert.DoesNotContain("xxxx", r.Log);
        Assert.Equal(2, r.Log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);   // start + end
    }
}
