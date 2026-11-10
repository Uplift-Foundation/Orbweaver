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

    private sealed record Run(int ExitCode, string Stdout, string Stderr, int ParentPid, SessionStatus? Status);

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

    private static Run RunHook(string tmp, string title, string[] decoys, string folder)
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
        return new Run(p.ExitCode, stdout, stderr, parent, status);
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
}
