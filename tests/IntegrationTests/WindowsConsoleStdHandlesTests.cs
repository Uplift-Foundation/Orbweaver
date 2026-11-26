using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Orbweaver.Tests;

/// <summary>
/// CB-476: a console read must leave this process's standard handles as it
/// found them. AttachConsole fills a console-less process's handles with the
/// attached console's, and FreeConsole leaves them pointing at it. Every later
/// UseShellExecute=false start (a named-account launch, Codex, Grok) then
/// inherited dead handles, and its conhost exited at once (measured on the
/// Windows PC). This test puts the test host in the app's state (no console,
/// no standard handles), reads a windowless child's console with the real
/// WindowsConsoleRead.Read, and requires the handles to be null again
/// afterwards. Before the fix they held the dead console's handles. The test
/// host's own handles and console are put back however it ends.
///
/// Windows only, and skipped in CI. On the GitHub windows-latest runner the
/// test host cannot get into the console-less state: AttachConsole on the
/// child still fails with error 5 after FreeConsole (CI at 3da391e). So the
/// evidence for this test is the Windows PC run, where it passes on the fix
/// and fails on 339eaefca with Expected [0, 0, 0], Actual [1196, 1412, 1436].
/// Run it on a Windows desktop with `dotnet test tests/IntegrationTests
/// --filter FullyQualifiedName~WindowsConsoleStdHandlesTests`.
/// </summary>
public class WindowsConsoleStdHandlesTests
{
    private sealed class DesktopWindowsFact : FactAttribute
    {
        public DesktopWindowsFact()
        {
            if (!OperatingSystem.IsWindows()) Skip = "a Windows console behaviour";
            else if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                Skip = "the CI runner's test host cannot detach from its console (AttachConsole error 5 at 3da391e); proven on the Windows PC instead";
        }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] private static extern bool SetStdHandle(int which, IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint pid);

    private static readonly int[] Ids = { -10, -11, -12 };

    [DesktopWindowsFact]
    public void A_console_read_leaves_a_console_less_process_with_no_standard_handles()
    {
        if (!OperatingSystem.IsWindows()) return;

        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = false };
        start.ArgumentList.Add("/k");
        start.ArgumentList.Add("echo cb476-std-handles");
        using var child = Process.Start(start)!;

        var saved = Ids.Select(GetStdHandle).ToArray();
        var hadConsole = GetConsoleWindow() != IntPtr.Zero;
        try
        {
            // The app's state: no console of its own, no standard handles.
            FreeConsole();
            foreach (var id in Ids) SetStdHandle(id, IntPtr.Zero);

            string? screen = null;
            var error = 0;
            for (var i = 0; i < 50 && screen is null; i++)
            {
                screen = WindowsConsoleRead.Read((uint)child.Id, new object(), out error);
                if (screen is null) Thread.Sleep(100);
            }
            var after = Ids.Select(GetStdHandle).ToArray();

            Assert.True(screen is not null, "the child's console was never read, error " + error);
            Assert.Equal(new[] { IntPtr.Zero, IntPtr.Zero, IntPtr.Zero }, after);
        }
        finally
        {
            for (var i = 0; i < Ids.Length; i++) SetStdHandle(Ids[i], saved[i]);
            // ATTACH_PARENT_PROCESS: back on the console the test host started on, if it had one.
            if (hadConsole) AttachConsole(unchecked((uint)-1));
            try { child.Kill(true); } catch (Exception) { }
        }
    }
}
