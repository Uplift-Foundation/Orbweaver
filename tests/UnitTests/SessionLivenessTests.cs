using Orbweaver;
using Xunit;

namespace Orbweaver.UnitTests;

// Telling a conversation from a terminal somebody walked away from.
//
// **Written from the two real transcripts that caused this**, not from
// memory — the same rule the transcript parsers here already follow, and for
// the same reason. The abandoned session's file had been written six minutes
// earlier and its process was alive; what made it abandoned was that the last
// row anyone had *said* was 23 hours old, buried under bridge housekeeping
// that carries no timestamp at all.
//
// The fixtures below are that shape, trimmed: a turn, then the rows Remote
// Control's bridge appends to a session it is attached to.
public class SessionLivenessTests
{
    private static readonly DateTime Now =
        new(2026, 8, 31, 3, 7, 0, DateTimeKind.Utc);

    private static string Turn(string role, string at) =>
        $$"""{"type":"{{role}}","timestamp":"{{at}}"}""";

    // What the bridge writes to a session nobody is using. No timestamp, which
    // is the whole reason file mtime is not the answer.
    private const string BridgeRow = """{"type":"bridge-session","sessionId":"dc6b769b"}""";

    private const string ModeRow = """{"type":"mode","mode":"default"}""";

    // --- reading the last turn -------------------------------------------------

    [Fact]
    public void ATurnIsFoundEvenWhenBookkeepingFollowsIt()
    {
        // Exactly the abandoned session's tail: the conversation ended, and
        // then the bridge kept writing. Reading only the final row would find
        // nothing; reading mtime would say "six minutes ago".
        var lines = new[]
        {
            Turn("user", "2026-08-29T04:24:05.027Z"),
            Turn("assistant", "2026-08-29T04:24:08.729Z"),
            BridgeRow, ModeRow, BridgeRow, BridgeRow,
        };

        Assert.Equal(
            new DateTime(2026, 8, 29, 4, 24, 8, 729, DateTimeKind.Utc),
            SessionLiveness.LastTurnAt(lines));
    }

    [Fact]
    public void OnlyUserAndAssistantRowsCount()
    {
        // Every other row type in a real transcript is tooling: `mode`,
        // `agent-name`, `queue-operation`, `file-history-snapshot`. Several are
        // written to a session nobody is using, which is the point.
        var lines = new[]
        {
            """{"type":"queue-operation","timestamp":"2026-08-31T03:06:59.000Z"}""",
            """{"type":"file-history-snapshot","timestamp":"2026-08-31T03:06:59.000Z"}""",
            """{"type":"agent-name","timestamp":"2026-08-31T03:06:59.000Z"}""",
            ModeRow,
        };

        Assert.Null(SessionLiveness.LastTurnAt(lines));
    }

    [Fact]
    public void TheNewestTurnWinsWhateverOrderTheyArriveIn()
    {
        // Rows are appended in order in practice. Not depending on that costs
        // one comparison.
        var lines = new[]
        {
            Turn("assistant", "2026-08-31T03:07:07.499Z"),
            Turn("user", "2026-08-29T04:24:05.027Z"),
        };

        Assert.Equal(
            new DateTime(2026, 8, 31, 3, 7, 7, 499, DateTimeKind.Utc),
            SessionLiveness.LastTurnAt(lines));
    }

    [Fact]
    public void NothingAtAllIsNotATime()
    {
        Assert.Null(SessionLiveness.LastTurnAt(Array.Empty<string>()));
    }

    // --- lines that are not what they should be --------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankLinesAreSkipped(string? blank)
    {
        Assert.Null(SessionLiveness.TurnTime(blank));
    }

    [Fact]
    public void ATornLineIsNotAFault()
    {
        // The ordinary case at the *start* of a tail read, where the window
        // lands mid-row. TailLines drops the first partial line itself, so this
        // is belt and braces — but a throw here would take down the answer to a
        // peer's roster question.
        Assert.Null(SessionLiveness.TurnTime("""{"type":"user","timesta"""));
    }

    [Fact]
    public void AJsonArrayIsNotARow()
    {
        // Valid JSON, wrong shape. Reached if a file is ever something other
        // than JSONL.
        Assert.Null(SessionLiveness.TurnTime("""["user","2026-08-31T03:07:07.499Z"]"""));
    }

    [Fact]
    public void ARowWithNoTypeIsNotATurn()
    {
        Assert.Null(SessionLiveness.TurnTime("""{"timestamp":"2026-08-31T03:07:07.499Z"}"""));
    }

    [Fact]
    public void ATypeThatIsNotAStringIsNotATurn()
    {
        Assert.Null(SessionLiveness.TurnTime("""{"type":7,"timestamp":"2026-08-31T03:07:07.499Z"}"""));
    }

    [Fact]
    public void ATurnWithNoTimestampTellsUsNothing()
    {
        // Not the same as "old": a turn we cannot date must not be read as a
        // recent one, and must not be read as an ancient one either.
        Assert.Null(SessionLiveness.TurnTime("""{"type":"assistant"}"""));
    }

    [Fact]
    public void ATimestampThatIsNotAStringIsIgnored()
    {
        Assert.Null(SessionLiveness.TurnTime("""{"type":"user","timestamp":1787979352207}"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    public void AnUnparseableTimestampIsIgnored(string bad)
    {
        Assert.Null(SessionLiveness.TurnTime($$"""{"type":"user","timestamp":"{{bad}}"}"""));
    }

    [Fact]
    public void ATimestampFromAnotherZoneIsMadeComparable()
    {
        // Transcripts are read off other machines over the link, and a clock
        // that agrees about the instant while disagreeing about the offset
        // would otherwise make a live session look hours stale.
        var at = SessionLiveness.TurnTime(
            """{"type":"user","timestamp":"2026-08-30T20:07:07.499-07:00"}""");

        Assert.Equal(new DateTime(2026, 8, 31, 3, 7, 7, 499, DateTimeKind.Utc), at);
    }

    // --- the rule itself -------------------------------------------------------

    [Theory]
    [InlineData("generating")]
    [InlineData("waiting")]
    public void ASessionBeingUsedRightNowIsNeverParkedHoweverOldItsTranscriptIs(string busy)
    {
        // A long tool call writes nothing for minutes and a permission prompt
        // can sit unanswered far longer — neither is parked, and both are
        // exactly when the orb is wanted at full brightness.
        Assert.False(SessionLiveness.IsParked(busy, Now - TimeSpan.FromDays(3), Now));
        Assert.False(SessionLiveness.IsParked(busy, Now - TimeSpan.FromSeconds(1), Now));
    }

    [Fact]
    public void AnIdleSessionThatSpokeSecondsAgoIsNotParked()
    {
        // **This is the case CB-74's filter got wrong**, and the reason this
        // rule reads turns rather than a heartbeat. The live session on the
        // mini reported `idle` at the very moment it was mid-conversation.
        Assert.False(SessionLiveness.IsParked("idle", Now - TimeSpan.FromSeconds(8), Now));
    }

    [Fact]
    public void AnIdleSessionNobodyHasSpokenToSinceYesterdayIsParked()
    {
        // 23 hours, measured — `job-hunter-mac-mini`; 20 hours, measured —
        // `lawyer-search` (CB-307). Both are parked, not hidden.
        Assert.True(SessionLiveness.IsParked("idle", Now - TimeSpan.FromHours(23), Now));
        Assert.True(SessionLiveness.IsParked("idle", Now - TimeSpan.FromHours(20), Now));
    }

    [Fact]
    public void TheBoundaryIsAssertedFromBothSides()
    {
        var window = SessionLiveness.ParkedAfter;

        Assert.False(SessionLiveness.IsParked("idle", Now - window + TimeSpan.FromTicks(1), Now, window));
        Assert.True(SessionLiveness.IsParked("idle", Now - window, Now, window));
        Assert.True(SessionLiveness.IsParked("idle", Now - window - TimeSpan.FromTicks(1), Now, window));
    }

    [Fact]
    public void TheShortOverloadUsesParkedAfter()
    {
        // A window of one minute would park a ten-minute-old turn; the
        // short overload must not, which is only true if it uses ParkedAfter.
        var tenMinutes = Now - TimeSpan.FromMinutes(10);

        Assert.True(SessionLiveness.IsParked("idle", tenMinutes, Now, TimeSpan.FromMinutes(1)));
        Assert.False(SessionLiveness.IsParked("idle", tenMinutes, Now));
        Assert.True(SessionLiveness.IsParked("idle", Now - SessionLiveness.ParkedAfter, Now));
    }

    [Fact]
    public void TheWindowSurvivesLunch()
    {
        // The stated intent, pinned: walking away for an hour is not leaving
        // a session, and an orb that dims over lunch says something untrue.
        Assert.True(SessionLiveness.ParkedAfter >= TimeSpan.FromHours(1));
    }

    [Fact]
    public void ASessionWhoseTranscriptSaidNothingIsNotParked()
    {
        // Fail open. Parking on an unreadable or bookkeeping-only file would
        // make a file this process could not parse look identical to a quiet
        // session — which is the confusion the whole class exists to end.
        Assert.False(SessionLiveness.IsParked("idle", null, Now));
        Assert.False(SessionLiveness.IsParked("idle", null, Now, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("idle")]
    [InlineData("something-a-newer-cli-writes")]
    public void AnUnrecognisedStateFallsBackToTheTranscript(string? state)
    {
        // A state this build does not know is not a claim that the session is
        // busy, so the conversation decides.
        Assert.False(SessionLiveness.IsParked(state, Now - TimeSpan.FromMinutes(1), Now));
        Assert.True(SessionLiveness.IsParked(state, Now - TimeSpan.FromDays(1), Now));
    }

    // --- the duplicate rule ----------------------------------------------------

    private static HashSet<string> Suppressed(params (string Key, string Name, bool Parked)[] rows) =>
        SessionLiveness.SuppressedDuplicates(rows);

    [Fact]
    public void AQuietCopyBesideALiveCopyOfTheSameNameIsSuppressed()
    {
        // The `job-hunter-mac-mini` picture: two identical labels, and the dim
        // one is the one nobody means.
        Assert.Equal(new[] { "old" },
            Suppressed(("live", "job-hunter", false), ("old", "job-hunter", true)));
    }

    [Fact]
    public void TwoQuietCopiesAreBothKept()
    {
        // Nothing to prefer one over the other, so both are drawn dimmed.
        Assert.Empty(Suppressed(("a", "job-hunter", true), ("b", "job-hunter", true)));
    }

    [Fact]
    public void TwoLiveCopiesAreBothKept() =>
        Assert.Empty(Suppressed(("a", "job-hunter", false), ("b", "job-hunter", false)));

    [Fact]
    public void OneLiveCopyMakesEveryQuietCopyRedundant()
    {
        var suppressed = Suppressed(
            ("q1", "job-hunter", true), ("live", "job-hunter", false), ("q2", "job-hunter", true));

        Assert.Equal(new[] { "q1", "q2" }, suppressed.OrderBy(k => k));
    }

    [Fact]
    public void NamesCompareWithoutCase() =>
        Assert.Equal(new[] { "old" },
            Suppressed(("live", "Job-Hunter", false), ("old", "job-hunter", true)));

    [Fact]
    public void ARowWithNoNameIsNeverADuplicate()
    {
        Assert.Empty(Suppressed(("live", "", false), ("old", "", true)));
        Assert.Empty(Suppressed(("live", null!, false), ("old", null!, true)));
    }

    [Fact]
    public void DifferentNamesNeverInteract() =>
        Assert.Empty(Suppressed(("live", "job-hunter", false), ("old", "lawyer-search", true)));

    [Fact]
    public void NoRowsSuppressNothing() => Assert.Empty(Suppressed());

    [Fact]
    public void OnlyALeadlessClaudeCodeSessionIsEligibleForTheDuplicateRule()
    {
        // Members inherit their lead's title, so a quiet member beside a live
        // one is a team rather than a duplicate and must never vanish.
        Assert.True(SessionLiveness.EligibleForDuplicateRule(
            new SessionStatus { Source = SessionSource.ClaudeCode }));
        Assert.False(SessionLiveness.EligibleForDuplicateRule(
            new SessionStatus { Source = SessionSource.ClaudeCode, Lead = "lead-session" }));
        Assert.False(SessionLiveness.EligibleForDuplicateRule(
            new SessionStatus { Source = SessionSource.Codex }));
    }
}
