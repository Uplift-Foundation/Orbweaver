using System.Diagnostics;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-476: the real process table WindowsConsolePane finds a console's client
/// in. A child this test starts itself, with no window, must be listed under
/// this process with its image name, which is the parent link the console
/// lookup walks from conhost to cmd.exe. Windows only; a no-op elsewhere.
/// </summary>
public class WindowsConsolePaneSnapshotTests
{
    [Fact]
    public void The_snapshot_lists_a_child_under_its_parent_with_its_image_name()
    {
        if (!OperatingSystem.IsWindows()) return;

        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("ping -n 30 127.0.0.1 >nul");
        using var child = Process.Start(start)!;
        try
        {
            var rows = WindowsConsolePane.Snapshot();
            Assert.Contains(new WindowsConsolePane.Row(child.Id, Environment.ProcessId, "cmd.exe"), rows);
            Assert.Equal(child.Id, WindowsConsolePane.Client(Environment.ProcessId,
                rows.Where(r => r.Pid == child.Id)));
        }
        finally
        {
            try { child.Kill(true); } catch (Exception) { }
        }
    }
}
