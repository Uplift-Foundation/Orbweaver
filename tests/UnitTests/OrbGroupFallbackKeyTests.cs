using Xunit;

namespace Orbweaver.Tests;

// The one rule for the bare-directory second chance on a membership lookup —
// Claude Code only — shared by the menu's read and T3's team fill.
public class OrbGroupFallbackKeyTests
{
    private static IReadOnlyDictionary<string, SessionStatus> Of(params (string Id, SessionStatus S)[] rows) =>
        rows.ToDictionary(r => r.Id, r => r.S, StringComparer.Ordinal);

    [Fact]
    public void AClaudeCodeOrbFallsBackToItsDirectoryAndOthersDoNot()
    {
        var statuses = Of(
            ("claude", new SessionStatus { Cwd = "/work/repo", Title = "t", Source = SessionSource.ClaudeCode }),
            ("codex", new SessionStatus { Cwd = "/work/repo", Title = "t", Source = SessionSource.Codex }),
            ("grok", new SessionStatus { Cwd = "/work/repo", Title = "t", Source = SessionSource.Grok }));

        Assert.Equal("/work/repo", OrbGroupMenu.FallbackKeyOf("claude", statuses));
        Assert.Null(OrbGroupMenu.FallbackKeyOf("codex", statuses));
        Assert.Null(OrbGroupMenu.FallbackKeyOf("grok", statuses));
    }

    [Fact]
    public void NoFallbackWhenTheOrbIsMissingHasNoDirectoryOrKeysOnItsDirectory()
    {
        var statuses = Of(
            ("nodir", new SessionStatus { Cwd = "", Source = SessionSource.ClaudeCode }),
            ("bare", new SessionStatus { Cwd = "/work/repo", Title = "", Source = SessionSource.ClaudeCode }));

        Assert.Null(OrbGroupMenu.FallbackKeyOf("absent", statuses));
        Assert.Null(OrbGroupMenu.FallbackKeyOf("nodir", statuses));
        // Untitled and in a cwd: the key already is the directory, so there is nothing further to try.
        Assert.Equal(
            SessionManager.DirectoryKeyFor(statuses["bare"]) == SessionManager.PositionKeyFor(statuses["bare"], "bare"),
            OrbGroupMenu.FallbackKeyOf("bare", statuses) is null);
    }
}
