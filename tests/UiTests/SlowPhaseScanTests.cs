using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// SessionManager's wiring of the slow-phase diagnostic: a fast pass writes
// nothing, a slow fake probe writes exactly one line naming it, and the line
// carries no path. The clock is a fake that the probe itself advances, so none of
// this sleeps.
[Collection("Settings")]
public class SlowPhaseScanTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "cb421-slowphase-" + Guid.NewGuid());
    private TimeSpan _now;
    private readonly bool _claudeCode = OrbweaverSettings.ClaudeCodeEnabled;

    public SlowPhaseScanTests()
    {
        Directory.CreateDirectory(_scratch);
        OrbweaverSettings.ClaudeCodeEnabled = true;
    }

    public void Dispose()
    {
        OrbweaverSettings.ClaudeCodeEnabled = _claudeCode;
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private const string SecretCwd = "/Users/someone/very-private-project-name";

    // One live local session, so there is a pid for the team probe to cover.
    private void LocalRow()
    {
        File.WriteAllText(Path.Combine(_scratch, "one.txt"), System.Text.Json.JsonSerializer.Serialize(new SessionStatus
        {
            State = "idle", Cwd = SecretCwd, Title = "private title", SessionPid = Environment.ProcessId,
            TermProgram = "iTerm.app", Tty = "/dev/ttys004",
        }));
    }

    private SessionManager Manager(TimeSpan teamsTakes, List<string> lines) =>
        new(_scratch, jobListing: () => null,
            teams: pids => { _now += teamsTakes; return new Dictionary<int, AgentTeam.Membership>(); })
        {
            ScanClock = () => _now,
            SlowPhaseSink = lines.Add,
        };

    [AvaloniaFact]
    public void AFastPass_WritesNothing()
    {
        LocalRow();
        var lines = new List<string>();
        Manager(TimeSpan.FromMilliseconds(40), lines).ScanAndUpdate();
        Assert.Empty(lines);
    }

    [AvaloniaFact]
    public void ASlowTeamProbe_WritesExactlyOneLine_NamingItWithItsCount_AndNoPathOrTitle()
    {
        LocalRow();
        var lines = new List<string>();
        Manager(TimeSpan.FromSeconds(2), lines).ScanAndUpdate();

        var line = Assert.Single(lines);
        Assert.Equal("slow scan phase: agent teams (pids) took 2000 ms (1 session)", line);
        Assert.DoesNotContain("someone", line);
        Assert.DoesNotContain("private", line);
    }

    [AvaloniaFact]
    public async Task ThroughTheProductionPath_EveryPhaseThatRanIsNamed_IncludingTheApply()
    {
        LocalRow();
        var lines = new List<string>();
        var manager = Manager(TimeSpan.Zero, lines);
        // A clock that moves two seconds every time it is read makes every phase slow.
        manager.ScanClock = () => _now += TimeSpan.FromSeconds(2);
        await manager.ScheduleScan();

        Assert.Contains(lines, l => l.StartsWith("slow scan phase: read status files took"));
        Assert.Contains(lines, l => l.StartsWith("slow scan phase: agent teams (pids) took"));
        Assert.Contains(lines, l => l.StartsWith("slow scan phase: agent teams (files) took"));
        Assert.Contains(lines, l => l.StartsWith("slow scan phase: apply took"));
        Assert.All(lines, l => { Assert.DoesNotContain("someone", l); Assert.DoesNotContain("private", l); });
    }
}
