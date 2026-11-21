using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Orbweaver.Tests;

// CB-404: the two process seams behind a Codex session's version before its first
// turn, against real processes. The rule they serve (the session's own
// executable, never PATH's codex) is RunSourcesTests'; this is the proof that
// the OS answer is the image the process is running, and that `--version` is
// asked of that exact file.
public class RunSourcesLiveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cb404-codex-").FullName;
    private readonly List<Process> _started = [];

    public void Dispose()
    {
        foreach (var p in _started)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            p.Dispose();
        }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static bool Windows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private Process Start(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        _started.Add(p);
        return p;
    }

    // A process read in the instant after it is started can answer nothing; a
    // session's codex is long past that. Up to two seconds, polled.
    private static string? Resolved(int pid)
    {
        for (var i = 0; i < 40; i++)
        {
            if (RunSourcesLive.ExecutableOf(pid) is { } image) return image;
            Thread.Sleep(50);
        }
        return null;
    }

    [Fact]
    public void Another_process_resolves_to_the_image_it_runs()
    {
        // Not a renamed copy: macOS kills a copied Apple platform binary at launch
        // (measured: exit 137), so the child runs from where it lives and the
        // check is that the OS names that file. The codex-name rule on top is
        // RunSourcesTests'.
        var p = Windows ? Start(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n", "30", "127.0.0.1") : Start("/bin/sleep", "30");
        var image = Resolved(p.Id);

        Assert.NotNull(image);
        Assert.Equal(Windows ? "ping.exe" : "sleep", Path.GetFileName(image!), ignoreCase: Windows);
        Assert.Null(RunSources.CodexExecutable(image));
    }

    [Fact]
    public void A_codex_launcher_script_resolves_to_its_interpreter_which_is_not_a_codex()
    {
        // The negative control: what a shim would look like, by name. Its image is the shell.
        if (Windows) return;
        var script = Path.Combine(_dir, "codex");
        File.WriteAllText(script, "#!/bin/sh\nsleep 30\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var p = Start(script);
        var image = Resolved(p.Id);

        Assert.NotNull(image);
        Assert.Null(RunSources.CodexExecutable(image));
    }

    [Fact]
    public void A_pid_that_is_not_running_resolves_to_nothing()
    {
        var p = Windows ? Start("cmd.exe", "/c", "exit") : Start("/usr/bin/true");
        p.WaitForExit();
        Assert.Null(RunSourcesLive.ExecutableOf(p.Id));
    }

    [Fact]
    public void Version_is_asked_of_the_exact_file()
    {
        var codex = Path.Combine(_dir, Windows ? "codex.cmd" : "codex");
        File.WriteAllText(codex, Windows ? "@echo codex-cli 0.150.0\r\n" : "#!/bin/sh\necho codex-cli 0.150.0\n");
        if (!Windows) File.SetUnixFileMode(codex, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Assert.Equal(new Version(0, 150, 0), RunSources.ParseCliVersion(RunSourcesLive.VersionText(codex)));
        Assert.Null(RunSourcesLive.VersionText(Path.Combine(_dir, "missing")));
    }
}
