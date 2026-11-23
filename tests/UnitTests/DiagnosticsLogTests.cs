using Xunit;

namespace Orbweaver.Tests;

// The file the app's own slow and failed lines go to (CB-421). The Windows build is a
// WinExe with no console, so the slow-scan line written to stderr never reached
// anywhere a person could read; these pin that it reaches a file, on every
// platform, bounded, and without ever throwing.
public class DiagnosticsLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb421-diaglog-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _scope;

    public DiagnosticsLogTests() => _scope = CrashLog.ScopeForTests(_dir);

    public void Dispose()
    {
        _scope.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Path_ => DiagnosticsLog.PathIn(_dir);

    [Fact]
    public void ALineIsAppendedWithATimestamp_InTheLogDirectory_BesideTheOtherLogs()
    {
        DiagnosticsLog.Write(new DateTimeOffset(2026, 11, 23, 6, 7, 8, 123, TimeSpan.Zero), "slow scan phase: apply took 2000 ms (6 sessions)");
        DiagnosticsLog.Write(new DateTimeOffset(2026, 11, 23, 6, 7, 9, 0, TimeSpan.Zero), "second");
        var lines = File.ReadAllLines(Path_);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("2026-11-23 06:07:08.123 +00:00  slow scan phase: apply took 2000 ms (6 sessions)", lines[0]);
        Assert.EndsWith("  second", lines[1]);
        Assert.Equal("diagnostics.log", Path.GetFileName(Path_));
    }

    [Fact]
    public void TheDefaultSlowPhaseSink_WritesToTheFile_NotToAConsole()
    {
        SlowPhase.DefaultSink("slow scan phase: agent teams (pids) took 1500 ms (3 sessions)");
        Assert.Contains("slow scan phase: agent teams (pids) took 1500 ms (3 sessions)", File.ReadAllText(Path_));
    }

    [Fact]
    public void ThePaperclipTransportsFailureLog_WritesToTheFileToo()
    {
        PaperclipHttp.DefaultLog("GET /api/companies/c/agents -> 500, 4 bytes, 12 ms");
        Assert.Contains("paperclip: GET /api/companies/c/agents -> 500", File.ReadAllText(Path_));
    }

    [Fact]
    public void PastTheLimit_TheFileIsMovedAsideToDotOne_AndAFreshOneStarts()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, new string('x', (int)DiagnosticsLog.MaxBytes + 10));
        DiagnosticsLog.Write("fresh");
        Assert.True(File.Exists(Path_ + ".1"));
        Assert.Contains("fresh", File.ReadAllText(Path_));
        Assert.True(new FileInfo(Path_).Length < 1000);
        // Another rotation replaces the old one rather than piling up.
        File.WriteAllText(Path_, new string('y', (int)DiagnosticsLog.MaxBytes + 10));
        DiagnosticsLog.Write("fresher");
        Assert.StartsWith("yyyy", File.ReadAllText(Path_ + ".1"));
    }

    [Fact]
    public void RotateIfFull_DoesNothingForAMissingOrSmallFile()
    {
        DiagnosticsLog.RotateIfFull(Path.Combine(_dir, "nope.log"));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "small");
        DiagnosticsLog.RotateIfFull(Path_);
        Assert.False(File.Exists(Path_ + ".1"));
    }

    [Fact]
    public void ADirectoryThatCannotBeWrittenTo_NeverThrows()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "cb421-diaglog-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "a file where a directory should be");
        try
        {
            using var scope = CrashLog.ScopeForTests(Path.Combine(blocker, "sub"));
            DiagnosticsLog.Write("nothing can be written here");   // must not throw
        }
        finally { File.Delete(blocker); }
    }
}
