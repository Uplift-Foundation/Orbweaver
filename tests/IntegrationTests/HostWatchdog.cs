using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit.Sdk;

[assembly: Orbweaver.Tests.WatchdogTracking]

namespace Orbweaver.Tests;

// CB-382: a test host that hangs must fail the step from the inside, naming the
// test, not wait for a timer that has been seen not to fire. On macOS CI the
// "Integration tests (xUnit)" step has sat until the 30-minute job limit with
// neither `--blame-hang-timeout 5m` nor `timeout-minutes: 10` ending it, and no
// log ever said which test was running. Those timers live outside the host (the
// vstest console and the runner); this one lives in it, so it needs no change to
// the workflow's command line.
//
// The watchdog is a background thread started when the assembly loads. It
// watches "progress", the last time any test started or finished. When nothing
// has moved for the stall limit it prints the tests in flight and the host's
// descendant processes to stderr, then kills the host and its whole process
// tree, so vstest reports a crashed host and the step fails.
//
// The limit is 180 seconds by default, ORBWEAVER_TEST_STALL_SECONDS overrides
// it. Why 180: it has to be under the 5-minute blame timeout (which, when it
// does fire, names the test but cannot be relied on) and far under the step's
// 10 minutes, yet well over the slowest legitimate gap. The longest single test
// here waits tens of seconds on a real socket or process, and other tests run
// in parallel collections, so a whole host with no test starting or finishing
// for three minutes is not slow, it is stuck.
//
// A normal run prints nothing: output starts only once a stall has lasted a
// quarter of the limit.
internal static class HostWatchdog
{
    internal const string StallEnv = BrandEnv.Prefix + BrandEnv.TestStallSeconds;
    internal const int DefaultStallSeconds = 180;

    static readonly object Gate = new();
    static readonly Dictionary<string, int> InFlight = new();
    static long _lastProgressTicks = Stopwatch.GetTimestamp();

    [ModuleInitializer]
    internal static void Start()
    {
        if (Debugger.IsAttached) return;
        var limit = LimitFromEnvironment();
        // A test that spawns a test host of its own (the watchdog's own tests)
        // passes the limit down; a plain `dotnet test` gets the default.
        var thread = new Thread(() => Watch(limit)) { IsBackground = true, Name = "HostWatchdog" };
        thread.Start();
    }

    internal static TimeSpan LimitFromEnvironment() =>
        double.TryParse(Environment.GetEnvironmentVariable(StallEnv),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromSeconds(DefaultStallSeconds);

    internal static void Began(string name)
    {
        lock (Gate)
        {
            InFlight[name] = InFlight.GetValueOrDefault(name) + 1;
            _lastProgressTicks = Stopwatch.GetTimestamp();
        }
    }

    internal static void Ended(string name)
    {
        lock (Gate)
        {
            if (InFlight.TryGetValue(name, out var n))
            {
                if (n <= 1) InFlight.Remove(name); else InFlight[name] = n - 1;
            }
            _lastProgressTicks = Stopwatch.GetTimestamp();
        }
    }

    static void Watch(TimeSpan limit)
    {
        var quiet = TimeSpan.FromTicks(limit.Ticks / 4);
        var nextBeat = quiet;
        var poll = TimeSpan.FromMilliseconds(Math.Clamp(limit.TotalMilliseconds / 20, 50, 1000));
        while (true)
        {
            Thread.Sleep(poll);
            TimeSpan stalled;
            lock (Gate) stalled = Stopwatch.GetElapsedTime(_lastProgressTicks);
            if (stalled < quiet) { nextBeat = quiet; continue; }
            if (stalled < limit)
            {
                if (stalled >= nextBeat)
                {
                    Err($"[host-watchdog] no test has started or finished for {stalled.TotalSeconds:0}s " +
                        $"(limit {limit.TotalSeconds:0}s); in flight: {Names()}");
                    nextBeat += TimeSpan.FromTicks(Math.Min(limit.Ticks / 4, TimeSpan.FromSeconds(60).Ticks));
                }
                continue;
            }
            Fail(stalled, limit);
        }
    }

    static string Names()
    {
        lock (Gate)
            return InFlight.Count == 0
                ? "none (stuck outside a test body: discovery, a fixture, a constructor or Dispose)"
                : string.Join(", ", InFlight.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    static void Fail(TimeSpan stalled, TimeSpan limit)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[host-watchdog] HUNG: no test started or finished for {stalled.TotalSeconds:0}s " +
                      $"(limit {limit.TotalSeconds:0}s, set by {StallEnv}); killing the test host.");
        sb.AppendLine($"[host-watchdog] tests in flight: {Names()}");
        sb.AppendLine($"[host-watchdog] descendants of host pid {Environment.ProcessId}:");
        sb.Append(Descendants());
        Err(sb.ToString());
        try { Process.GetCurrentProcess().Kill(entireProcessTree: true); } catch { }
        Environment.Exit(1);
    }

    static void Err(string text)
    {
        try { Console.Error.WriteLine(text); Console.Error.Flush(); } catch { }
    }

    // One process-table row as "pid ppid rest", one space between the two ids
    // and the rest (which is kept as it came), or null when it does not start
    // with two ids. `ps` pads its columns to the widest value in the table, so
    // a row whose ppid is narrower than the widest reads "3174  3138 ...":
    // printing that as it came made the listing's shape depend on which pids
    // the machine had, and the hang test failed whenever the host's parent pid
    // was narrower than the widest (CB-491).
    internal static (int Pid, int Ppid, string Line)? Row(string raw)
    {
        var f = raw.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 2 || !int.TryParse(f[0], out var pid) || !int.TryParse(f[1], out var ppid))
            return null;
        return (pid, ppid, f.Length == 3 ? $"{pid} {ppid} {f[2].TrimStart()}" : $"{pid} {ppid}");
    }

    // One "pid ppid rest" line per process from the OS's own table, then the
    // subtree under this host, in-process, so the same filter serves both OSes.
    static string Descendants()
    {
        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("powershell.exe",
                    "-NoProfile -Command \"Get-CimInstance Win32_Process | ForEach-Object { '{0} {1} {2}' -f $_.ProcessId, $_.ParentProcessId, $_.CommandLine }\"")
                : new ProcessStartInfo("/bin/ps", "-A -o pid=,ppid=,etime=,stat=,command=");
            psi.RedirectStandardOutput = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using var p = Process.Start(psi)!;
            var read = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(15000) || !read.Wait(2000))
            {
                try { p.Kill(true); } catch { }
                return "  (process listing timed out)\n";
            }
            var rows = new Dictionary<int, (int Ppid, string Line)>();
            foreach (var raw in read.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                if (Row(raw) is { } r)
                    rows[r.Pid] = (r.Ppid, r.Line);
            var keep = new HashSet<int> { Environment.ProcessId };
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var (pid, r) in rows)
                    if (!keep.Contains(pid) && keep.Contains(r.Ppid)) { keep.Add(pid); grew = true; }
            }
            var sb = new StringBuilder();
            foreach (var pid in keep.OrderBy(x => x))
                if (rows.TryGetValue(pid, out var r)) sb.Append("  ").AppendLine(r.Line);
            return sb.Length == 0 ? "  (none found)\n" : sb.ToString();
        }
        catch (Exception e)
        {
            return $"  (process listing failed: {e.GetType().Name}: {e.Message})\n";
        }
    }
}

// Applied to the whole assembly: every test method reports its start and end.
[AttributeUsage(AttributeTargets.Assembly)]
internal sealed class WatchdogTrackingAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest) =>
        HostWatchdog.Began(Name(methodUnderTest));

    public override void After(MethodInfo methodUnderTest) =>
        HostWatchdog.Ended(Name(methodUnderTest));

    static string Name(MethodInfo m) => $"{m.DeclaringType?.FullName}.{m.Name}";
}
