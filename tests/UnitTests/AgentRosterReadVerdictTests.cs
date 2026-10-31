using Xunit;

namespace Orbweaver.UnitTests;

// CB-300: AgentRoster.Read turned a timeout or a non-zero exit into an empty
// list and said nothing, so a failing dir looked exactly like an empty one.
public class AgentRosterReadVerdictTests
{
    private const string OneRow =
        "[{\"pid\":1,\"sessionId\":\"s1\",\"name\":\"n\",\"status\":\"idle\"}]";

    private static AgentRoster.RunOutcome Ran(
        bool binary = true, bool started = true, bool timedOut = false,
        int exit = 0, string? stdout = OneRow, string? threw = null) =>
        new(binary, started, timedOut, exit, stdout, threw);

    [Fact]
    public void ASuccessfulRunHasEntriesAndNoFailure()
    {
        var result = AgentRoster.Interpret(Ran());

        Assert.Null(result.Failure);
        Assert.Equal("s1", Assert.Single(result.Entries).SessionId);
    }

    // Not a failure: a registry with nothing in it. The gap analysis depends
    // on telling this from a dead dir.
    [Theory]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData(null)]
    public void AnEmptyAnswerIsNotAFailure(string? stdout)
    {
        var result = AgentRoster.Interpret(Ran(stdout: stdout));

        Assert.Null(result.Failure);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void NoBinaryIsAFailure() => Assert.Equal("no-binary", Fails(Ran(binary: false)));

    [Fact]
    public void AProcessThatDidNotStartIsAFailure() => Assert.Equal("start-failed", Fails(Ran(started: false)));

    [Fact]
    public void ATimeoutIsAFailure() => Assert.Equal("timeout", Fails(Ran(timedOut: true)));

    [Fact]
    public void ANonZeroExitIsAFailureEvenWithStdout() => Assert.Equal("exit=3", Fails(Ran(exit: 3)));

    [Fact]
    public void AnExceptionIsAFailureNamedByTypeOnly() =>
        Assert.Equal("threw:IOException", Fails(Ran(threw: "IOException")));

    // Each failure kind keeps the old contract: an empty list, not null.
    [Fact]
    public void EveryFailureStillGivesAnEmptyListNotNull()
    {
        foreach (var run in new[]
        {
            Ran(binary: false), Ran(started: false), Ran(timedOut: true), Ran(exit: 1), Ran(threw: "X")
        })
        {
            var result = AgentRoster.Interpret(run);
            Assert.NotNull(result.Entries);
            Assert.Empty(result.Entries);
        }
    }

    private static string? Fails(AgentRoster.RunOutcome run) => AgentRoster.Interpret(run).Failure;
}
