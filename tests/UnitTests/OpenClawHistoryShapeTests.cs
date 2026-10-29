using System.Linq;
using System.Text.Json;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-270: the pairing rule that stops a tool-using run being drawn twice (its
// step texts, then the gateway's rollup repeating them), and the reader of the
// `toolcall` blocks that become one line each. Pure functions over JSON, so
// every arm gets a case and nothing needs a window.
public class OpenClawHistoryShapeTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private const string User = """{"role":"user","content":"go"}""";
    private const string Step = """{"role":"assistant","api":"anthropic-messages","content":[{"type":"text","text":"step"}]}""";
    private const string ToolOnlyStep = """{"role":"assistant","api":"anthropic-messages","content":[{"type":"toolcall","id":"t1","name":"exec","arguments":{"command":"ls"}}]}""";
    private const string Rollup = """{"role":"assistant","api":"cli","idempotencyKey":"cli-assistant:r","content":[{"type":"text","text":"step"}]}""";

    private static JsonElement Page(params string[] messages) => Json("[" + string.Join(",", messages) + "]");

    private static int[] Dropped(params string[] messages) =>
        OpenClawHistoryShape.RollupsToDrop(Page(messages)).OrderBy(i => i).ToArray();

    // ---- IsRollup -------------------------------------------------------

    [Fact]
    public void AnApiOfCliMarksARollup() =>
        Assert.True(OpenClawHistoryShape.IsRollup(Json("""{"role":"assistant","api":"cli","content":[]}""")));

    [Fact]
    public void ACliAssistantIdempotencyKeyMarksARollup() =>
        Assert.True(OpenClawHistoryShape.IsRollup(Json("""{"role":"assistant","idempotencyKey":"cli-assistant:r1","content":[]}""")));

    [Fact]
    public void AnOrdinaryStepIsNotARollup() =>
        Assert.False(OpenClawHistoryShape.IsRollup(Json(Step)));

    [Fact]
    public void AnAssistantWithAnUnrelatedKeyIsNotARollup() =>
        Assert.False(OpenClawHistoryShape.IsRollup(Json("""{"role":"assistant","idempotencyKey":"run-1:user","content":[]}""")));

    [Fact]
    public void AUserRowIsNeverARollupEvenWithTheMarker() =>
        Assert.False(OpenClawHistoryShape.IsRollup(Json("""{"role":"user","api":"cli","content":"x"}""")));

    [Fact]
    public void ANonObjectIsNotARollup() =>
        Assert.False(OpenClawHistoryShape.IsRollup(Json("\"x\"")));

    // ---- RollupsToDrop: every arm of the rule ---------------------------

    [Fact]
    public void AStepPlusRollupSpanDropsTheRollup() =>
        Assert.Equal(new[] { 2 }, Dropped(User, Step, Rollup));

    [Fact]
    public void ALoneRollupIsKept() =>
        Assert.Empty(Dropped(User, Rollup));

    [Fact]
    public void ASpanWhoseStepsCarryNoTextKeepsItsRollup() =>
        Assert.Empty(Dropped(User, ToolOnlyStep, Rollup));

    [Fact]
    public void ARollupWithNoPrecedingUserTurnIsOneSpanWithItsSteps() =>
        Assert.Equal(new[] { 1 }, Dropped(Step, Rollup));

    [Fact]
    public void ALoneRollupWithNoPrecedingUserTurnIsKept() =>
        Assert.Empty(Dropped(Rollup));

    [Fact]
    public void TwoRunsBackToBackAreJudgedSeparately() =>
        // Run one has steps (rollup dropped); run two is a lone rollup (kept).
        Assert.Equal(new[] { 2 }, Dropped(User, Step, Rollup, User, Rollup));

    [Fact]
    public void TwoToolRunsBackToBackEachDropTheirOwnRollup() =>
        Assert.Equal(new[] { 2, 5 }, Dropped(User, Step, Rollup, User, Step, Rollup));

    // A rollup closes the run it summarises, so one with no text step before it
    // is a run of its own and is kept; the steps after it are the next run's.
    [Fact]
    public void ARollupWithNoTextStepBeforeItIsKeptEvenIfStepsFollow() =>
        Assert.Empty(Dropped(User, Rollup, Step));

    // ---- runs with no user turn between them (CB-270 QA) ----------------
    // Constructed from the captured shapes (the fixture's step and `cli`
    // rollup messages), not captured as such: the gateway does produce runs
    // with no user turn between them (heartbeat and cron injection into a main
    // session), but no such page was in the Phase 0 captures.

    [Fact]
    public void AToolRunThenANoToolRunWithNoUserBetweenKeepsRunBsReply() =>
        // step, rollup A (dropped); lone rollup B (kept).
        Assert.Equal(new[] { 2 }, Dropped(User, Step, Rollup, Rollup));

    [Fact]
    public void ANoToolRunThenAToolRunWithNoUserBetweenKeepsBoth() =>
        // lone rollup A (kept); step, rollup B (dropped).
        Assert.Equal(new[] { 4 }, Dropped(User, Rollup, ToolOnlyStep, Step, Rollup));

    [Fact]
    public void TwoToolRunsWithNoUserBetweenDropEachOwnRollup() =>
        Assert.Equal(new[] { 2, 5 }, Dropped(User, Step, Rollup, ToolOnlyStep, Step, Rollup));

    [Fact]
    public void AHeartbeatShapedMainPageJudgesEachInjectedRunOnItsOwn() =>
        // No user turns at all: a no-tool run, a tool run, a no-tool run.
        Assert.Equal(new[] { 3 }, Dropped(Rollup, Step, Step, Rollup, Rollup));

    [Fact]
    public void StepsAfterTheLastRollupAreARunInFlightAndAreLeftAlone() =>
        Assert.Equal(new[] { 2 }, Dropped(User, Step, Rollup, Step));

    [Fact]
    public void AWhatsAppShapedPageOfLoneRollupsKeepsEveryOne() =>
        Assert.Empty(Dropped(User, Rollup, User, Rollup, User, Rollup, User, Rollup));

    [Fact]
    public void APageWithNoRollupsDropsNothing() =>
        Assert.Empty(Dropped(User, Step, Step, User, Step));

    [Fact]
    public void AStepWithOnlyWhitespaceTextDoesNotCount() =>
        Assert.Empty(Dropped(User,
            """{"role":"assistant","content":[{"type":"text","text":"  \n"}]}""", Rollup));

    [Fact]
    public void AnAssistantStepWithStringContentCounts() =>
        Assert.Equal(new[] { 2 }, Dropped(User, """{"role":"assistant","content":"hello"}""", Rollup));

    [Fact]
    public void AnAssistantStepWithBlankStringContentDoesNotCount() =>
        Assert.Empty(Dropped(User, """{"role":"assistant","content":"   "}""", Rollup));

    [Fact]
    public void AStepWithNoContentOrAnOddContentShapeDoesNotCount() =>
        Assert.Empty(Dropped(User,
            """{"role":"assistant"}""", """{"role":"assistant","content":5}""", Rollup));

    [Fact]
    public void AMessageThatIsNotAnAssistantDoesNotCountAsAStep() =>
        Assert.Empty(Dropped(User, """{"role":"system","content":"note"}""", Rollup));

    [Fact]
    public void AnEmptyPageAndANonArrayDropNothing()
    {
        Assert.Empty(OpenClawHistoryShape.RollupsToDrop(Json("[]")));
        Assert.Empty(OpenClawHistoryShape.RollupsToDrop(Json("{}")));
    }

    [Fact]
    public void TheRealFixtureDropsTheToolRunsRollupAndKeepsTheOtherOne()
    {
        var drop = OpenClawHistoryShape.RollupsToDrop(Json(OpenClawNativeRunFixtures.ToolRunPage));

        // 0 user, 1 step, 2 toolcall step, 3 step, 4 rollup, 5 user, 6 lone rollup.
        Assert.Equal(new[] { 4 }, drop.OrderBy(i => i).ToArray());
    }

    // ---- ToolCalls ------------------------------------------------------

    private static (string? Id, string? Name, JsonElement Args, bool IsError)[] Calls(string content) =>
        OpenClawHistoryShape.ToolCalls(Json(content)).ToArray();

    [Fact]
    public void TheFixturesToolCallIsReadWithItsArgumentsAndNoError()
    {
        var page = Json(OpenClawNativeRunFixtures.ToolRunPage);
        var content = page[2].GetProperty("content");

        var call = Assert.Single(OpenClawHistoryShape.ToolCalls(content));

        Assert.Equal("toolu_01FIXTURE0000000000000001", call.Id);
        Assert.Equal("mcp__openclaw__exec", call.Name);
        Assert.Equal("ls /home/op/workspace | wc -l", call.Args.GetProperty("command").GetString());
        Assert.False(call.IsError);
    }

    [Fact]
    public void ASnakeCaseErrorFlagOnTheResultMarksTheCall() =>
        Assert.True(Assert.Single(Calls("""
            [{"type":"toolcall","id":"a","name":"x","arguments":{}},
             {"type":"tool_result","tool_use_id":"a","is_error":true,"content":"boom"}]
            """)).IsError);

    [Fact]
    public void ACamelCaseErrorFlagOnTheResultMarksTheCall() =>
        Assert.True(Assert.Single(Calls("""
            [{"type":"toolcall","id":"a","name":"x","arguments":{}},
             {"type":"tool_result","tool_use_id":"a","isError":true,"content":"boom"}]
            """)).IsError);

    [Fact]
    public void AResultForADifferentCallDoesNotMarkThisOne() =>
        Assert.False(Assert.Single(Calls("""
            [{"type":"toolcall","id":"a","name":"x","arguments":{}},
             {"type":"tool_result","tool_use_id":"zzz","is_error":true}]
            """)).IsError);

    [Fact]
    public void AFalseErrorFlagOrANonResultBlockDoesNotMarkTheCall() =>
        Assert.False(Assert.Single(Calls("""
            [{"type":"toolcall","id":"a","name":"x","arguments":{}},
             {"type":"text","text":"no"},
             {"type":"tool_result","tool_use_id":"a","is_error":false}]
            """)).IsError);

    [Fact]
    public void ACallWithNoIdIsNeverAnError() =>
        Assert.False(Assert.Single(Calls("""
            [{"type":"toolcall","name":"x","arguments":{}},
             {"type":"tool_result","is_error":true}]
            """)).IsError);

    [Fact]
    public void ACallWithNoArgumentsYieldsDefaultArgs()
    {
        var call = Assert.Single(Calls("""[{"type":"toolcall","id":"a","name":"x"}]"""));

        Assert.Equal(JsonValueKind.Undefined, call.Args.ValueKind);
    }

    [Fact]
    public void ANonArrayContentYieldsNoCalls()
    {
        Assert.Empty(Calls("\"plain text\""));
        Assert.Empty(Calls("""[{"type":"text","text":"hi"}]"""));
    }
}
