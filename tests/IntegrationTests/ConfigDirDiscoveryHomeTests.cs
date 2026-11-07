using Xunit;

namespace Orbweaver.Tests;

// CB-307: discovery against a real temporary HOME on disk — the real
// directory listing and the real cache.
[Collection("Settings")]
public class ConfigDirDiscoveryHomeTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "cb307-home-" + Guid.NewGuid().ToString("N"));

    public ConfigDirDiscoveryHomeTests()
    {
        foreach (var (dir, child) in new[]
                 {
                     (".claude", "projects"), (".claude-board", "sessions"), (".claude-sundaysky", "projects"),
                     (".claude-work", "notes"), (".claude-memory-merge-backup", "old"),
                 })
            Directory.CreateDirectory(Path.Combine(_home, dir, child));
        File.WriteAllText(Path.Combine(_home, ".claude.json"), "{}");
        ClaudeConfigRoots.ForgetDiscovered();
    }

    public void Dispose()
    {
        ClaudeConfigRoots.ForgetDiscovered();
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    [Fact]
    public void ARealHomesConfigDirsAreFoundAndJoinTheConfiguredRoots()
    {
        Assert.Equal(new[] { ".claude", ".claude-board", ".claude-sundaysky" }, ClaudeConfigRoots.DiscoveredCached(_home));

        var roots = ClaudeConfigRoots.AllWithDiscovered(_home);
        Assert.Equal(Path.Combine(_home, ".claude"), roots[0]);
        Assert.Contains(Path.Combine(_home, ".claude-sundaysky"), roots);
        Assert.DoesNotContain(Path.Combine(_home, ".claude-work"), roots);
        Assert.Equal(roots.Count, roots.Distinct().Count());
    }

    // Held for a while, so a scan asking per session does not list home each
    // time; a new account appears once the hold lapses.
    [Fact]
    public void ANewAccountAppearsOnceTheHoldLapses()
    {
        var now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        var previous = ClaudeConfigRoots.Now;
        ClaudeConfigRoots.Now = () => now;
        try
        {
            Assert.DoesNotContain(".claude-loginid", ClaudeConfigRoots.DiscoveredCached(_home));

            Directory.CreateDirectory(Path.Combine(_home, ".claude-loginid", "sessions"));
            Assert.DoesNotContain(".claude-loginid", ClaudeConfigRoots.DiscoveredCached(_home));

            now += ClaudeConfigRoots.DiscoveryHeldFor;
            Assert.Contains(".claude-loginid", ClaudeConfigRoots.DiscoveredCached(_home));
        }
        finally
        {
            ClaudeConfigRoots.Now = previous;
        }
    }
}
