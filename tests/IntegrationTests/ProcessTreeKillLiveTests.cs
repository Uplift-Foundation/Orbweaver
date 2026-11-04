using System.Diagnostics;
using Xunit;

namespace Orbweaver.Tests;

// CB-306 against real processes: a shell, a sleeping child, and a subshell with
// a sleeping grandchild — the shape that made `Process.Kill(true)` stop and
// orphan its way into a SIGHUP. Asserts every pid in the tree is gone within a
// second, which is what the framework call did too; what differs is that
// nothing here is ever stopped, and the SIGHUP itself needs a session-1
// launchd job to reproduce (docs/sighup-findings.md), so it is not asserted.
public class ProcessTreeKillLiveTests
{
    [MacOnlyFact]
    public void KillsTheRootItsChildrenAndItsGrandchildrenWithinASecond()
    {
        var psi = new ProcessStartInfo("/bin/bash")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("sleep 300 & echo $!; ( sleep 300 & echo $!; wait ) & echo $!; wait");
        using var root = Process.Start(psi)!;

        var pids = new List<int> { root.Id };
        for (var i = 0; i < 3; i++)
            pids.Add(int.Parse(root.StandardOutput.ReadLine()!));

        try
        {
            ProcessTreeKill.Kill(root);

            Assert.True(root.WaitForExit(1000), "root survived");
            var deadline = Stopwatch.StartNew();
            List<int> alive;
            while ((alive = pids.Where(IsAlive).ToList()).Count > 0 && deadline.ElapsedMilliseconds < 1000)
                Thread.Sleep(20);
            Assert.True(alive.Count == 0, "survivors: " + string.Join(", ", alive));
        }
        finally
        {
            foreach (var pid in pids.Where(IsAlive)) Signal(pid);
        }
    }

    [WindowsOnlyFact]
    public void KillsTheRootOnTheFrameworkArm()
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul") { CreateNoWindow = true };
        using var root = Process.Start(psi)!;

        ProcessTreeKill.Kill(root);

        Assert.True(root.WaitForExit(2000));
    }

    private static bool IsAlive(int pid)
    {
        using var ps = Process.Start(new ProcessStartInfo("/bin/ps", $"-p {pid} -o stat=")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        var stat = ps.StandardOutput.ReadToEnd().Trim();
        ps.WaitForExit();
        // A reaped pid prints nothing; a zombie ("Z") is dead for our purposes.
        return stat.Length > 0 && !stat.StartsWith('Z');
    }

    private static void Signal(int pid)
    {
        using var k = Process.Start("/bin/kill", $"-9 {pid}")!;
        k.WaitForExit();
    }
}
