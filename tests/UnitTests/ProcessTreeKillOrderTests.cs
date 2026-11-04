using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace Orbweaver.Tests;

// CB-306: the order ProcessTreeKill signals in, held apart from any process so
// every shape is a dictionary rather than a real tree. The thing that matters
// about the order is that it is a plain snapshot walked root-first and that it
// terminates; the SIGSTOP the framework's own tree kill sends never appears in
// it at all, which the live test in IntegrationTests is what shows.
public class ProcessTreeKillOrderTests
{
    private static readonly Regex TreeKillCall = new(@"\.Kill\(\s*(entireProcessTree:\s*)?true\s*\)");

    private static Func<int, IReadOnlyList<int>> Tree(params (int Parent, int[] Kids)[] edges)
    {
        var map = edges.ToDictionary(e => e.Parent, e => (IReadOnlyList<int>)e.Kids);
        return pid => map.TryGetValue(pid, out var kids) ? kids : [];
    }

    [Fact]
    public void ALoneRootIsKilledAlone()
    {
        Assert.Equal(new[] { 10 }, ProcessTreeKill.KillOrder(10, Tree()));
    }

    [Fact]
    public void ARootWithEmptyChildListsIsKilledAlone()
    {
        Assert.Equal(new[] { 10 }, ProcessTreeKill.KillOrder(10, Tree((10, []))));
    }

    [Fact]
    public void ANestedTreeIsKilledParentsBeforeChildrenBreadthFirst()
    {
        // 1 -> 2, 3 ; 2 -> 4 ; 3 -> 5, 6 ; 4 -> 7
        var order = ProcessTreeKill.KillOrder(1, Tree(
            (1, [2, 3]), (2, [4]), (3, [5, 6]), (4, [7])));

        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7 }, order);
    }

    [Fact]
    public void EveryParentComesBeforeEachOfItsChildren()
    {
        var edges = new (int, int[])[] { (1, [2, 3]), (2, [4, 5]), (5, [6]) };
        var order = ProcessTreeKill.KillOrder(1, Tree(edges));

        foreach (var (parent, kids) in edges)
            foreach (var kid in kids)
                Assert.True(order.ToList().IndexOf(parent) < order.ToList().IndexOf(kid));
    }

    [Fact]
    public void APidListedUnderTwoParentsIsKilledOnce()
    {
        var order = ProcessTreeKill.KillOrder(1, Tree((1, [2, 3]), (2, [4]), (3, [4])));

        Assert.Equal(new[] { 1, 2, 3, 4 }, order);
    }

    [Fact]
    public void ACycleBackToTheRootOrWithinTheTreeTerminates()
    {
        var order = ProcessTreeKill.KillOrder(1, Tree((1, [2]), (2, [3, 1]), (3, [2])));

        Assert.Equal(new[] { 1, 2, 3 }, order);
    }

    // Kill(Process, false) is the framework's own tree kill — the arm Windows
    // takes — and runs on macOS too, so both arms are reachable from one machine.
    [Fact]
    public void KillOfAnAlreadyExitedProcessOnTheLibprocArmDoesNothing()
    {
        using var process = Process.Start(OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sh", "-c \"exit 0\""))!;
        process.WaitForExit();

        ProcessTreeKill.Kill(process, useLibproc: true);

        Assert.True(process.HasExited);
    }

    [Fact]
    public void KillOnTheFrameworkArmEndsALiveProcess()
    {
        using var process = Process.Start(OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul") { CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sleep", "60"))!;

        ProcessTreeKill.Kill(process, useLibproc: false);

        Assert.True(process.WaitForExit(5000));
    }

    // The rule this fix exists for, as something that fails: the framework's
    // tree kill stops processes before killing them, and that is what took
    // Orbweaver down with SIGHUP. Nothing in the app may call it directly.
    [Fact]
    public void NoProductionCodeCallsTheFrameworkTreeKillDirectly()
    {
        var root = FindRepoRoot();
        var pattern = TreeKillCall;
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative == "ProcessTreeKill.cs") continue;
            var top = relative.Split('/')[0];
            if (top is "tests" or "obj" or "bin" or ".git" || relative.Contains("/obj/") || relative.Contains("/bin/"))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//")) continue;
                if (pattern.IsMatch(lines[i])) offenders.Add($"{relative}:{i + 1}");
            }
        }

        Assert.True(offenders.Count == 0,
            "use ProcessTreeKill.Kill instead of Process.Kill(true) at: " + string.Join(", ", offenders));
    }

    // The scan above is only worth anything if it can fail: this is its
    // negative control, run against the pattern with a line that must match.
    [Theory]
    [InlineData("try { p.Kill(true); } catch { }")]
    [InlineData("p.Kill(entireProcessTree: true);")]
    [InlineData("p.Kill( entireProcessTree:true )")]
    public void TheScanPatternMatchesTheShapesItIsMeantToRefuse(string line)
    {
        Assert.Matches(TreeKillCall, line);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProcessTreeKill.cs"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find ProcessTreeKill.cs above " + AppContext.BaseDirectory);
    }
}
