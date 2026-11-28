using Xunit;

namespace Orbweaver.Tests;

// CB-294: ApplyPersona walks the mapped cwd, puts the mapped config dir first
// and keys the candidate list on it. The real mapper answers only on Windows,
// so a fake stands in for it and this runs on every host.
[Collection("Settings")]
public class SessionManagerWslPersonaTests : IDisposable
{
    private readonly string _sessionId = "cb294-wsl-" + Guid.NewGuid();
    private readonly string _root = Path.Combine(
        Path.GetPathRoot(Path.GetTempPath())!, "cb294-wsl-" + Guid.NewGuid());

    public void Dispose() => LocalPersonas.Forget(_sessionId);

    private static SessionStatus Status(string cwd, string? transcript) => new()
    {
        Source = SessionSource.ClaudeCode,
        State = "idle",
        Cwd = cwd,
        TranscriptPath = transcript,
        Title = "cb294",
    };

    private static Dictionary<(string Cwd, SessionSource Source, string Agent, string ConfigDir), IReadOnlyList<LocalPersona.Candidate>> Pass() => new();

    [Fact]
    public void AWslSessionWalksTheMappedCwdWithTheMappedConfigDirFirstAndKeyedOnIt()
    {
        var mappedCwd = Path.Combine(_root, "mapped-project");
        var mappedConfig = Path.Combine(_root, "mapped-config");
        var machineDir = Path.Combine(_root, "machine-config");
        var linuxCwd = "/home/u/project";
        var linuxTranscript = "/home/u/.claude-x/projects/p/s.jsonl";
        var seen = new List<(string, string?)>();

        var manager = new SessionManager(
            Path.GetTempPath(), null,
            userConfigDirs: () => new[] { machineDir },
            wslPaths: (cwd, transcript) =>
            {
                seen.Add((cwd, transcript));
                return (mappedCwd, mappedConfig);
            });

        var pass = Pass();
        manager.ApplyPersona(_sessionId, Status(linuxCwd, linuxTranscript), pass);

        Assert.Equal((linuxCwd, (string?)linuxTranscript), Assert.Single(seen));
        var entry = Assert.Single(pass);
        Assert.Equal((mappedCwd, SessionSource.ClaudeCode, "", mappedConfig), entry.Key);

        var paths = entry.Value.Select(c => c.Path).ToList();
        Assert.Contains(Path.Combine(mappedCwd, "CLAUDE.md"), paths);
        Assert.DoesNotContain(Path.Combine(linuxCwd, "CLAUDE.md"), paths);
        var config = paths.IndexOf(Path.Combine(mappedConfig, "CLAUDE.md"));
        var machine = paths.IndexOf(Path.Combine(machineDir, "CLAUDE.md"));
        Assert.True(config >= 0 && machine >= 0 && config < machine, string.Join("|", paths));
    }

    // Negative control: a mapper that leaves the paths as they were (what it
    // does for a Windows or native session) changes nothing.
    [Fact]
    public void ASessionTheMapperLeavesAloneIsUnchanged()
    {
        var cwd = Path.Combine(_root, "plain-project");
        var machineDir = Path.Combine(_root, "machine-config");
        var manager = new SessionManager(
            Path.GetTempPath(), null,
            userConfigDirs: () => new[] { machineDir },
            wslPaths: (c, _) => (c, null));

        var pass = Pass();
        manager.ApplyPersona(_sessionId, Status(cwd, null), pass);

        var entry = Assert.Single(pass);
        Assert.Equal((cwd, SessionSource.ClaudeCode, "", ""), entry.Key);
        var paths = entry.Value.Select(c => c.Path).ToList();
        Assert.Contains(Path.Combine(cwd, "CLAUDE.md"), paths);
        Assert.Contains(Path.Combine(machineDir, "CLAUDE.md"), paths);
        Assert.DoesNotContain(paths, p => p.Contains("mapped-config"));
    }
}
