using Xunit;

namespace Orbweaver.UnitTests;

// CB-300: a live session the roster never named left no trace at all — it was
// not in `wanted`, so it was never looked up, never unresolved, never logged.
public class PeerRosterGapTests
{
    private const string Home = "/Users/u";

    private static (string SessionId, SessionStatus Status) Session(
        string id, string? transcript, int pid = 0, string lead = "",
        SessionSource source = SessionSource.ClaudeCode, string entrypoint = "") =>
        (id, new SessionStatus
        {
            SessionPid = pid,
            TranscriptPath = transcript,
            Lead = lead,
            Source = source,
            Entrypoint = entrypoint
        });

    private static string Under(string root, string id = "x") => $"{Home}/{root}/projects/slug/{id}.jsonl";

    private static IReadOnlyList<RemoteMirrorServer.RosterGap> Gaps(
        IReadOnlyList<(string, SessionStatus)> sessions,
        string[] asked,
        AgentRoster.Entry[]? agents = null,
        Func<SessionStatus, bool>? eligible = null) =>
        RemoteMirrorServer.RosterGaps(
            agents ?? Array.Empty<AgentRoster.Entry>(), sessions, asked, Home, eligible ?? (_ => true));

    // --- RowMatches -----------------------------------------------------------

    [Fact]
    public void ARowMatchesBySessionIdIgnoringCase()
    {
        Assert.True(RemoteMirrorServer.RowMatches(new("n", "ABC", 0), Session("abc", null)));
    }

    [Fact]
    public void ARowMatchesByProcessId()
    {
        Assert.True(RemoteMirrorServer.RowMatches(new("n", "other", 42), Session("abc", null, pid: 42)));
    }

    [Fact]
    public void ProcessIdZeroNeverMatches()
    {
        Assert.False(RemoteMirrorServer.RowMatches(new("n", "other", 0), Session("abc", null, pid: 0)));
    }

    [Fact]
    public void NeitherIdNorPidMatching_IsNoMatch()
    {
        Assert.False(RemoteMirrorServer.RowMatches(new("n", "other", 7), Session("abc", null, pid: 8)));
    }

    // --- the mini ---------------------------------------------------------------

    [Fact]
    public void TwentyThreeSessionsUnderAnUnaskedDirAreOneActionableGap()
    {
        var sessions = Enumerable.Range(0, 23)
            .Select(i => Session($"s{i}", Under(".claude-loginid", $"s{i}"))).ToList();

        var gap = Assert.Single(Gaps(sessions, new[] { ".claude", ".claude-board" }));

        Assert.Equal("~/.claude-loginid", gap.Root);
        Assert.Equal(23, gap.Sessions);
        Assert.False(gap.Asked);
        Assert.True(gap.Actionable);
    }

    [Fact]
    public void TheSameSessionsUnderAnAskedDirAreAskedAndNeverActionable()
    {
        var sessions = Enumerable.Range(0, 23)
            .Select(i => Session($"s{i}", Under(".claude-loginid", $"s{i}"))).ToList();

        var gap = Assert.Single(Gaps(sessions, new[] { ".claude", ".claude-loginid" }));

        Assert.True(gap.Asked);
        Assert.False(gap.Actionable);
        Assert.Equal(23, gap.Sessions);
    }

    [Fact]
    public void AnAskedDirIsRecognisedWhateverItIsSpelledAs()
    {
        var gap = Assert.Single(Gaps(
            new[] { Session("s", Under(".claude-x")) }, new[] { "./.claude-x/" }));

        Assert.True(gap.Asked);
    }

    [Fact]
    public void ASessionTheRosterNamedByIdIsNotAGap()
    {
        var gaps = Gaps(new[] { Session("S1", Under(".a")) }, new[] { ".claude" },
            new[] { new AgentRoster.Entry("n", "s1", 0) });

        Assert.Empty(gaps);
    }

    [Fact]
    public void ASessionTheRosterNamedByPidIsNotAGap()
    {
        var gaps = Gaps(new[] { Session("s1", Under(".a"), pid: 9) }, new[] { ".claude" },
            new[] { new AgentRoster.Entry("n", "different", 9) });

        Assert.Empty(gaps);
    }

    [Fact]
    public void ATeamMemberIsNotAGap()
    {
        Assert.Empty(Gaps(new[] { Session("m", Under(".a"), lead: "lead") }, new[] { ".claude" }));
    }

    [Fact]
    public void AnAbandonedSessionIsNotAGap()
    {
        Assert.Empty(Gaps(new[] { Session("s", Under(".a")) }, new[] { ".claude" }, eligible: _ => false));
    }

    [Theory]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.Grok)]
    public void CodexAndGrokAreNotGaps(SessionSource source)
    {
        Assert.Empty(Gaps(new[] { Session("s", Under(".a"), source: source) }, new[] { ".claude" }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/Users/u/odd/shape.jsonl")]
    public void ANullOrOddTranscriptPathIsNoTranscriptAndNotActionable(string? path)
    {
        var gap = Assert.Single(Gaps(new[] { Session("s", path) }, new[] { ".claude" }));

        Assert.Equal(RemoteMirrorServer.NoTranscriptRoot, gap.Root);
        Assert.False(gap.Asked);
        Assert.False(gap.Actionable);
    }

    [Fact]
    public void AWslRootIsNotActionableAndKeepsItsPath()
    {
        var path = @"\\wsl.localhost\Ubuntu\home\u\.claude\projects\s\x.jsonl";

        var gap = Assert.Single(Gaps(new[] { Session("s", path) }, new[] { ".claude" }));

        Assert.False(gap.Actionable);
        Assert.False(gap.Asked);
        Assert.StartsWith(@"\\wsl.localhost", gap.Root);
    }

    [Fact]
    public void ADesktopHostedSessionIsNotActionable()
    {
        var gap = Assert.Single(Gaps(
            new[] { Session("s", Under(".a"), entrypoint: SessionStatus.DesktopEntrypoint) },
            new[] { ".claude" }));

        Assert.False(gap.Actionable);
    }

    [Fact]
    public void ARootHoldingADesktopSessionAndAPlainOneIsActionableForTheSakeOfTheOtherOne()
    {
        var gap = Assert.Single(Gaps(
            new[]
            {
                Session("d", Under(".a", "d"), entrypoint: SessionStatus.DesktopEntrypoint),
                Session("p", Under(".a", "p"))
            },
            new[] { ".claude" }));

        Assert.Equal(2, gap.Sessions);
        Assert.True(gap.Actionable);
    }

    // A Windows transcript path, proved on whatever runner this is: separators
    // are unified rather than handed to Path.GetFullPath, which would resolve
    // it against the working directory on a Mac.
    [Fact]
    public void AWindowsSeparatorTranscriptUnderHomeResolvesAsked()
    {
        const string winHome = @"C:\Users\u";
        var path = @"C:\Users\u\.claude-work\projects\s\x.jsonl";

        var asked = Assert.Single(RemoteMirrorServer.RosterGaps(
            Array.Empty<AgentRoster.Entry>(), new[] { Session("s", path) },
            new[] { ".claude-work" }, winHome, _ => true));
        var unasked = Assert.Single(RemoteMirrorServer.RosterGaps(
            Array.Empty<AgentRoster.Entry>(), new[] { Session("s", path) },
            new[] { ".claude" }, winHome, _ => true));

        Assert.True(asked.Asked);
        Assert.Equal("~/.claude-work", asked.Root);
        Assert.False(unasked.Asked);
        Assert.True(unasked.Actionable);
    }

    [Fact]
    public void AnAbsoluteAskedDirIsComparedAsGiven()
    {
        var gap = Assert.Single(Gaps(
            new[] { Session("s", Under(".a")) }, new[] { "/Users/u/.a", "  ", "" }));

        Assert.True(gap.Asked);
    }

    [Fact]
    public void ParentSegmentsInAnAskedDirAreFolded()
    {
        var gap = Assert.Single(Gaps(
            new[] { Session("s", Under(".a")) }, new[] { "x/../.a" }));

        Assert.True(gap.Asked);
    }

    [Fact]
    public void GapsAreOrderedLargestFirst()
    {
        var gaps = Gaps(
            new[]
            {
                Session("1", Under(".small", "1")),
                Session("2", Under(".big", "2")),
                Session("3", Under(".big", "3"))
            },
            new[] { ".claude" });

        Assert.Equal(new[] { "~/.big", "~/.small" }, gaps.Select(g => g.Root));
    }

    [Theory]
    [InlineData("~/.claude-loginid", "~/.claude-loginid")]
    [InlineData("(no transcript)", "(no transcript)")]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\someone\.claude", "(outside home)")]
    [InlineData("/mnt/elsewhere/.claude", "(outside home)")]
    public void TheLogNeverCarriesAPathOutsideHome(string root, string label)
    {
        Assert.Equal(label, RemoteMirrorServer.LogLabel(root));
    }

    // --- the status line --------------------------------------------------------

    private static RemoteMirrorServer.RosterGap G(string root, int n, bool actionable = true, bool asked = false) =>
        new(root, n, asked, actionable);

    [Fact]
    public void OneActionableFolderIsNamedWithItsCount()
    {
        var text = PeerSessions.GapSentence(new[] { G("~/.claude-loginid", 23) });

        Assert.Equal(
            " — 23 sessions under ~/.claude-loginid aren't offered; add that folder under Claude Code profiles.",
            text);
    }

    [Fact]
    public void OneSessionReadsInTheSingular()
    {
        Assert.Equal(
            " — 1 session under ~/.a isn't offered; add that folder under Claude Code profiles.",
            PeerSessions.GapSentence(new[] { G("~/.a", 1) }));
    }

    [Fact]
    public void SeveralFoldersNameTheLargestAndCountTheRest()
    {
        Assert.Equal(
            " — 10 sessions under ~/.big and 1 more folder aren't offered; add those folders under Claude Code profiles.",
            PeerSessions.GapSentence(new[] { G("~/.small", 3), G("~/.big", 7) }));

        Assert.Contains("and 2 more folders", PeerSessions.GapSentence(
            new[] { G("~/.a", 1), G("~/.b", 2), G("~/.c", 3) }));
    }

    [Fact]
    public void WithNothingActionableTheSentenceIsEmpty()
    {
        Assert.Equal("", PeerSessions.GapSentence(null));
        Assert.Equal("", PeerSessions.GapSentence(Array.Empty<RemoteMirrorServer.RosterGap>()));
        Assert.Equal("", PeerSessions.GapSentence(new[]
        {
            G("~/.a", 5, actionable: false, asked: true),
            G("(no transcript)", 2, actionable: false)
        }));
    }

    // Byte-identical to the five-argument answers for every branch, whatever
    // the gaps hold, unless one is actionable.
    [Theory]
    [InlineData(false, false, 0, 0, 0)]
    [InlineData(true, false, 0, 0, 0)]
    [InlineData(true, true, 3, 2, 0)]
    [InlineData(true, true, 3, 0, 2)]
    [InlineData(true, true, 3, 0, 0)]
    public void TheStatusTextIsUnchangedWhenNothingIsActionable(
        bool enabled, bool running, int listening, int connected, int seen)
    {
        var plain = PeerSessions.StatusText(enabled, running, listening, connected, seen);
        var inert = new[] { G("~/.a", 5, actionable: false, asked: true) };

        Assert.Equal(plain, PeerSessions.StatusText(enabled, running, listening, connected, seen, null));
        Assert.Equal(plain, PeerSessions.StatusText(enabled, running, listening, connected, seen, inert));
    }

    [Fact]
    public void AnActionableGapIsAppendedOnlyWhileTheLinkIsRunning()
    {
        var gaps = new[] { G("~/.a", 4) };

        Assert.EndsWith("under ~/.a aren't offered; add that folder under Claude Code profiles.",
            PeerSessions.StatusText(true, true, 3, 1, 0, gaps));
        Assert.StartsWith("Connected to 1 machine, listening on 3 —",
            PeerSessions.StatusText(true, true, 3, 1, 0, gaps));

        // Off and starting say nothing about sessions.
        Assert.Equal("Off", PeerSessions.StatusText(false, true, 3, 1, 0, gaps));
        Assert.Equal("Starting…", PeerSessions.StatusText(true, false, 0, 0, 0, gaps));
    }
}
