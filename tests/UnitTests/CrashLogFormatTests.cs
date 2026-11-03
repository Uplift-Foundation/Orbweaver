using Xunit;

namespace Orbweaver.Tests;

// What one crash entry says (CB-44).
//
// Worth asserting sentence by sentence, because the entire value of this file
// is that somebody reading it months later learns what happened. The two .ips
// reports this replaces contained a stack of question marks and a timestamp,
// and cost a day between them; an entry that named the exception would have
// cost a `cat`.
public class CrashLogFormatTests
{
    private static readonly DateTimeOffset When =
        new(2026, 8, 28, 18, 48, 40, TimeSpan.FromHours(-7));

    private static Exception Thrown(string message = "the calling thread cannot access this object")
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (Exception caught)
        {
            // Thrown rather than constructed, so there is a real stack on it —
            // a constructed exception has StackTrace null, which is the arm
            // below rather than the ordinary case.
            return caught;
        }
    }

    [Fact]
    public void Names_when_where_and_what_on_the_first_two_lines()
    {
        var entry = CrashLog.Format(When, "AppDomain.UnhandledException", Thrown());
        var lines = entry.Split('\n');

        Assert.StartsWith("=== 2026-08-28 18:48:40.000 -07:00", lines[0]);
        Assert.Contains("AppDomain.UnhandledException", lines[0]);
        Assert.Contains("pid " + Environment.ProcessId, lines[0]);

        Assert.Contains("System.InvalidOperationException", lines[1]);
        Assert.Contains("the calling thread cannot access this object", lines[1]);
    }

    [Fact]
    public void Carries_the_stack_when_there_is_one()
    {
        var entry = CrashLog.Format(When, "AppDomain.UnhandledException", Thrown());

        Assert.Contains(nameof(CrashLogFormatTests), entry);
        Assert.Contains("   at ", entry);
    }

    [Fact]
    public void Survives_an_exception_that_was_never_thrown()
    {
        // A constructed exception has no stack. The entry still has to be
        // readable rather than ending mid-sentence.
        var entry = CrashLog.Format(When, "TaskScheduler.UnobservedTaskException",
            new TimeoutException("the relay never answered"));

        Assert.Contains("System.TimeoutException", entry);
        Assert.Contains("the relay never answered", entry);
        Assert.EndsWith("\n", entry);
    }

    [Fact]
    public void Says_so_when_the_runtime_hands_over_something_that_is_not_an_exception()
    {
        // AppDomain.UnhandledException's ExceptionObject is typed `object`, and
        // a non-Exception is legal there. "Something went wrong and it was not
        // an Exception" is still more than the nothing this replaces.
        var entry = CrashLog.Format(When, "AppDomain.UnhandledException", error: null);

        Assert.Contains("AppDomain.UnhandledException", entry);
        Assert.Contains("no exception object", entry);
    }

    [Fact]
    public void Unwraps_the_inner_exception_that_usually_is_the_answer()
    {
        // TargetInvocationException over the real fault is the shape this app
        // hits most, and the outer one says nothing useful.
        var entry = CrashLog.Format(
            When,
            "Dispatcher.UnhandledException",
            new InvalidOperationException(
                "an error occurred",
                new UnauthorizedAccessException("Local Network permission")));

        Assert.Contains("System.InvalidOperationException: an error occurred", entry);
        Assert.Contains("---> System.UnauthorizedAccessException: Local Network permission", entry);
    }

    [Fact]
    public void Stops_unwrapping_rather_than_following_a_cycle_of_wrappers()
    {
        // Five deep is more than any real stack of wrappers and is a bound
        // rather than a judgement: an entry is worth nothing if writing it takes
        // long enough for the process to be killed first.
        Exception error = new InvalidOperationException("innermost");

        for (var i = 0; i < 40; i++) error = new InvalidOperationException("wrapper " + i, error);

        var entry = CrashLog.Format(When, "AppDomain.UnhandledException", error);

        Assert.Equal(5, entry.Split("--->").Length - 1);
        Assert.DoesNotContain("innermost", entry);
    }

    [Fact]
    public void Starts_every_entry_with_its_own_marker()
    {
        // The file is append-only and holds several crashes, so an entry has to
        // be findable by eye. `===` at column zero, and everything else
        // indented, is the whole of that.
        var entry = CrashLog.Format(When, "AppDomain.UnhandledException", Thrown());

        Assert.StartsWith("===", entry);
        Assert.All(
            entry.Split('\n').Skip(1).Where(l => l.Length > 0),
            line => Assert.StartsWith(" ", line));
    }

    // --- where it writes -----------------------------------------------------

    [Fact]
    public void Writes_where_the_platform_keeps_logs_when_nothing_overrides_it()
    {
        // Read-only, and worth pinning: the default path is what the README
        // tells people to look at, and it is the one thing here no test on a
        // scratch directory can check. macOS keeps logs in ~/Library/Logs;
        // Windows keeps them under %LOCALAPPDATA%.
        var was = Environment.GetEnvironmentVariable("ORBWEAVER_LOG_DIR");

        try
        {
            Environment.SetEnvironmentVariable("ORBWEAVER_LOG_DIR", null);

            var expected = OperatingSystem.IsWindows()
                ? Path.Combine("Orbweaver", "Logs")
                : Path.Combine("Library", "Logs", "Orbweaver");

            Assert.EndsWith(expected, CrashLog.Directory);
            Assert.EndsWith("crash.log", CrashLog.Path_);
            Assert.EndsWith("crash.log.1", CrashLog.PreviousPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBWEAVER_LOG_DIR", was);
        }
    }

    [Fact]
    public void Prefers_the_scratch_directory_a_test_or_a_user_points_it_at()
    {
        var was = Environment.GetEnvironmentVariable("ORBWEAVER_LOG_DIR");

        try
        {
            var scratch = Path.Combine(Path.GetTempPath(), "cb-log-seam");
            Environment.SetEnvironmentVariable("ORBWEAVER_LOG_DIR", scratch);

            Assert.Equal(scratch, CrashLog.Directory);

            // An empty value is not an override — it is a variable somebody
            // exported and left blank, and treating it as "write to the current
            // directory" would scatter crash logs wherever Buddy was launched.
            Environment.SetEnvironmentVariable("ORBWEAVER_LOG_DIR", "");
            Assert.NotEqual("", CrashLog.Directory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBWEAVER_LOG_DIR", was);
        }
    }

    // ---- the stacks of what an unobserved task wrapped --------------------------------

    private static ObjectDisposedException ThrownOnADisposedSemaphore()
    {
        var gate = new SemaphoreSlim(1, 1);
        gate.Dispose();

        try
        {
            gate.Release();
            throw new InvalidOperationException("Release did not throw");
        }
        catch (ObjectDisposedException caught)
        {
            return caught;
        }
    }

    // What a faulted task nobody observed arrives as: the finalizer thread throws the
    // aggregate, so it has no frames of its own, and the fault's stack exists only on
    // the exception inside it. 471 entries said "disposed SemaphoreSlim" and not which.
    [Fact]
    public void Records_the_stack_of_the_exception_an_unobserved_task_wrapped()
    {
        var inner = ThrownOnADisposedSemaphore();
        var wrapper = new AggregateException("A Task's exception(s) were not observed", inner);
        Assert.Null(wrapper.StackTrace);

        var entry = CrashLog.Format(When, "TaskScheduler.UnobservedTaskException", wrapper);

        Assert.Contains("stack of System.ObjectDisposedException:", entry);
        Assert.Contains(nameof(ThrownOnADisposedSemaphore), entry);
        Assert.Equal(1, entry.Split("stack of").Length - 1);
    }

    [Fact]
    public void Records_the_stack_of_every_member_of_an_aggregate()
    {
        var wrapper = new AggregateException(Thrown("first"), ThrownOnADisposedSemaphore());

        var entry = CrashLog.Format(When, "TaskScheduler.UnobservedTaskException", wrapper);

        Assert.Contains("stack of System.InvalidOperationException:", entry);
        Assert.Contains("stack of System.ObjectDisposedException:", entry);
    }

    [Fact]
    public void Records_the_stack_of_an_inner_exception_in_a_chain()
    {
        var entry = CrashLog.Format(When, "Dispatcher.UnhandledException",
            new InvalidOperationException("an error occurred", Thrown("the real fault")));

        Assert.Contains("stack of System.InvalidOperationException:", entry);
    }

    [Fact]
    public void Leaves_out_the_stack_of_an_inner_exception_that_was_never_thrown()
    {
        var entry = CrashLog.Format(When, "Dispatcher.UnhandledException",
            new InvalidOperationException("an error occurred", new UnauthorizedAccessException("never thrown")));

        Assert.DoesNotContain("stack of", entry);
    }

    [Fact]
    public void Bounds_how_many_inner_stacks_one_entry_carries()
    {
        var wrapper = new AggregateException(Enumerable.Range(0, 30).Select(i => Thrown("member " + i)));

        var entry = CrashLog.Format(When, "TaskScheduler.UnobservedTaskException", wrapper);

        Assert.Equal(8, entry.Split("stack of").Length - 1);
    }
}
