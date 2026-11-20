using System.Runtime.CompilerServices;
using Xunit;

namespace Orbweaver.Tests;

// CB-356: a compaction read off a real file on disk by the phone's watcher,
// which re-parses the whole transcript on every change. The rows are the real
// capture with three attachment rows between the boundary and the summary.
public class CompactionTranscriptFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb356-" + Guid.NewGuid().ToString("N"));

    public CompactionTranscriptFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string Capture(string name, [CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "TranscriptTests", "Captures", "cb356", name + ".jsonl"));

    [Fact]
    public void ThePhoneGetsOneCompactionLineWithTheSummaryAsItsBody()
    {
        var path = Path.Combine(_dir, "compacted.jsonl");
        File.Copy(Capture("auto-attachments-between"), path);

        var lines = TranscriptWatcher.ParseFile(path, SessionSource.ClaudeCode)!;

        Assert.Equal(new[] { "assistant", "system", "assistant" }, lines.Select(l => l.Role));
        var line = lines[1];
        Assert.Equal("Conversation compacted automatically · 169k → 16k tokens", line.Text);
        Assert.Equal("compaction", line.Kind);
        Assert.StartsWith("## 1. Primary Request and Intent", line.Body);
        Assert.False(line.BodyCut);
        Assert.Null(lines[0].Kind);
        Assert.Null(lines[2].Body);
    }

    // The negative control on the same seam: a user quoting the summary's
    // opening is still the user's line, with no kind.
    [Fact]
    public void AUserQuotingTheSummaryIsStillAUserLine()
    {
        var path = Path.Combine(_dir, "quoted.jsonl");
        File.Copy(Capture("synthetic-control-user-quotes-summary"), path);

        var lines = TranscriptWatcher.ParseFile(path, SessionSource.ClaudeCode)!;

        Assert.Equal("user", lines[1].Role);
        Assert.Null(lines[1].Kind);
        Assert.StartsWith("This session is being continued", lines[1].Text);
    }
}
