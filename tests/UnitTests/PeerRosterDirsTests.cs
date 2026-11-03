using Xunit;

namespace Orbweaver.UnitTests;

// CB-300: which Claude Code config dirs the peer link reads `claude agents`
// from. The link read a settings key nothing could write any more, so a profile
// added in Settings → Claude Code was never offered to a paired machine.
public class PeerRosterDirsTests
{
    private const string Home = "/home/u";

    private static string[] Dirs(string[] rc, string[] cc) =>
        PeerRosterDirs.For(Home, rc, cc).ToArray();

    // The mini's exact shape on 2026-10-30.
    [Fact]
    public void AProfileAddedInSettingsIsOfferedAlongsideTheOldList()
    {
        var rc = new[] { ".claude", ".claude-board" };
        var cc = new[] { ".claude", ".claude-board", ".claude-loginid" };

        Assert.Equal(new[] { ".claude", ".claude-board", ".claude-loginid" }, Dirs(rc, cc));

        // The control, and the bug as data: the old list alone lacks it.
        Assert.DoesNotContain(".claude-loginid", rc);
    }

    // Windows: the accessor answers [.claude] for an empty key.
    [Fact]
    public void AnEmptyOldKeyStillOffersTheDefaultAndTheConfiguredProfiles()
    {
        Assert.Equal(new[] { ".claude", ".claude-work" }, Dirs(new[] { ".claude" }, new[] { ".claude-work" }));
    }

    // The first entry is the label both halves of the link carry and
    // MergeRosters' tie-break, so it must not move for an existing install.
    [Fact]
    public void TheOldListComesFirstInItsOwnOrder()
    {
        var result = Dirs(new[] { ".claude-board", ".claude" }, new[] { ".claude-new", ".claude" });

        Assert.Equal(".claude-board", result[0]);
        Assert.Equal(new[] { ".claude-board", ".claude", ".claude-new" }, result);

        // A profiles-first ordering would have changed [0].
        Assert.NotEqual(".claude-new", result[0]);
    }

    [Fact]
    public void OneDirectorySpelledFiveWaysIsOneEntryAndKeepsItsFirstSpelling()
    {
        var spellings = new[] { ".claude", "./.claude", ".claude/", Home + "/.claude", ".CLAUDE" };

        Assert.Equal(new[] { ".claude" }, Dirs(spellings, spellings));
    }

    [Fact]
    public void TwoDifferentDirectoriesStayTwo()
    {
        Assert.Equal(new[] { ".claude-a", ".claude-b", ".claude" }, Dirs(new[] { ".claude-a", ".claude-b" }, Array.Empty<string>()));
    }

    [Fact]
    public void BlankEntriesAreSkippedAndPaddingIsTrimmed()
    {
        var result = Dirs(new[] { "", "   ", "  .claude-x " }, new[] { "\t", ".claude-y  " });

        Assert.Equal(new[] { ".claude-x", ".claude", ".claude-y" }, result);
    }

    [Fact]
    public void TheDefaultIsAddedWhenTheOldListHoldsOnlyAnotherDirectory()
    {
        Assert.Equal(new[] { ".claude-board", ".claude" }, Dirs(new[] { ".claude-board" }, Array.Empty<string>()));
    }

    [Fact]
    public void AnAbsolutePathToTheDefaultIsNotASecondDirectory()
    {
        // Resolved path, so the absolute spelling of ~/.claude dedups against
        // the default added after the old list.
        Assert.Equal(new[] { Home + "/.claude" }, Dirs(new[] { Home + "/.claude" }, new[] { ".claude" }));
    }

    // ClaudeProfile.Resolve does no tilde expansion, so this pins what the
    // spellings do rather than assuming it.
    [Fact]
    public void DotSegmentsCollapseButALiteralTildeStaysDistinct()
    {
        Assert.Equal(new[] { ".claude" }, Dirs(new[] { ".claude", ".claude/." }, Array.Empty<string>()));
        Assert.Equal(2, Dirs(new[] { "~/.claude" }, Array.Empty<string>()).Length);
        Assert.Contains("~/.claude", Dirs(new[] { "~/.claude" }, Array.Empty<string>()));
        Assert.Contains(".claude", Dirs(new[] { "~/.claude" }, Array.Empty<string>()));
    }
}
