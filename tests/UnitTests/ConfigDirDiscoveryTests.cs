using Orbweaver;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-307: which dirs under home are Claude Code config dirs, found rather than
// listed. The shapes are the real ones measured on the MacBook 2026-11-07:
// four config dirs holding projects/ and sessions/, and two siblings holding
// neither.
[Collection("Settings")]
public class ConfigDirDiscoveryTests
{
    private const string Home = "/home/u";

    // The MacBook's ~ as it is: name → what it holds.
    private static readonly Dictionary<string, string[]> MacBook = new()
    {
        [".claude"] = new[] { "projects", "sessions" },
        [".claude-board"] = new[] { "projects", "sessions" },
        [".claude-loginid"] = new[] { "projects", "sessions" },
        [".claude-sundaysky"] = new[] { "projects", "sessions" },
        [".claude-memory-merge-backup"] = Array.Empty<string>(),
        [".claude-work"] = Array.Empty<string>(),
        [".cache"] = new[] { "projects" },
    };

    private static IReadOnlyList<string> Discover(Dictionary<string, string[]> tree) =>
        ClaudeConfigRoots.Discover(Home,
            _ => tree.Keys.Select(k => Home + "/" + k),
            path => tree.Any(kv => kv.Value.Any(child => Path.Combine(Home, kv.Key, child) == path)));

    [Fact]
    public void TheMacBooksFourConfigDirsAreFoundAndItsTwoSiblingsAreNot() =>
        Assert.Equal(new[] { ".claude", ".claude-board", ".claude-loginid", ".claude-sundaysky" }, Discover(MacBook));

    [Fact]
    public void EitherProjectsOrSessionsIsEnough()
    {
        var tree = new Dictionary<string, string[]>
        {
            [".claude-only-sessions"] = new[] { "sessions" },
            [".claude-only-projects"] = new[] { "projects" },
            [".claude-neither"] = new[] { "todos" },
        };

        Assert.Equal(new[] { ".claude-only-projects", ".claude-only-sessions" }, Discover(tree));
    }

    [Theory]
    [InlineData(".claude", true)]
    [InlineData(".claude-x", true)]
    [InlineData(".claude-", false)]
    [InlineData(".claudex", false)]
    [InlineData("claude-board", false)]
    [InlineData(".claude.json", true)]
    public void ANameIsADotClaudeOrADotClaudeDash(string name, bool shaped) =>
        Assert.Equal(shaped && name != ".claude.json", ClaudeConfigRoots.IsConfigDirName(name));

    [Fact]
    public void TheDefaultComesFirstAndTheRestInNameOrder()
    {
        var tree = new Dictionary<string, string[]>
        {
            [".claude-zeta"] = new[] { "sessions" },
            [".claude"] = new[] { "sessions" },
            [".claude-alpha"] = new[] { "sessions" },
        };

        Assert.Equal(new[] { ".claude", ".claude-alpha", ".claude-zeta" }, Discover(tree));
    }

    [Fact]
    public void AHomeThatCannotBeListedDiscoversNothing() =>
        Assert.Empty(ClaudeConfigRoots.Discover(Home, _ => throw new UnauthorizedAccessException(), _ => true));

    // The roster: discovered dirs come after both lists, so every existing
    // install keeps its first entry (its label) and its tie-break, and nothing
    // is asked twice.
    [Fact]
    public void ThePeerRosterAddsDiscoveredDirsLastAndOnce()
    {
        var dirs = PeerRosterDirs.For(Home,
            new[] { ".claude", ".claude-board" }, new[] { ".claude-board", ".claude-loginid" },
            new[] { ".claude", ".claude-loginid", ".claude-sundaysky" });

        Assert.Equal(new[] { ".claude", ".claude-board", ".claude-loginid", ".claude-sundaysky" }, dirs);
    }

    [Fact]
    public void WithNoListsAtAllTheDiscoveredDirsAreServed() =>
        Assert.Equal(new[] { ".claude", ".claude-sundaysky" },
            PeerRosterDirs.For(Home, Array.Empty<string>(), Array.Empty<string>(), new[] { ".claude-sundaysky" }));
}
