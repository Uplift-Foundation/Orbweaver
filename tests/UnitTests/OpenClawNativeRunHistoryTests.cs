using System.Linq;
using System.Text.Json;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UnitTests;

// CB-270, end to end over the real TurnsFromHistory: the §1.1 measurement made
// permanent. Develop at 550cee45 drew the reply of a tool-using run twice (its
// step texts, then the rollup repeating them) and drew no tool line at all.
public class OpenClawNativeRunHistoryTests
{
    private const string Key = "agent:main:dashboard:0a1b2c3d-0000-4000-8000-000000000001";

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static System.Collections.Generic.List<HistoryTurn> Turns(string page) =>
        OpenClawSessions.TurnsFromHistory(Json(page), Key);

    [Fact]
    public void AToolUsingRunDrawsItsReplyOnceWithTheToolLineBetweenTheSteps()
    {
        var turns = Turns(OpenClawNativeRunFixtures.ToolRunPage);

        Assert.Equal(
            new[] { ChatRole.User, ChatRole.Assistant, ChatRole.System, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant },
            turns.Select(t => t.Role).ToArray());

        Assert.Single(turns, t => t.Text == "probe ok, 18 files");
        Assert.Equal(1, turns.Count(t => t.Text.Contains("probe ok, 18 files")));
        Assert.StartsWith("Hey Boss!", turns[1].Text);
    }

    [Fact]
    public void TheToolLineIsExactlyWhatTheLiveSessionWillBuildFromTheSameCall()
    {
        var turns = Turns(OpenClawNativeRunFixtures.ToolRunPage);
        var args = Json("""{"command":"ls /home/op/workspace | wc -l","title":"Count workspace files"}""");

        Assert.Equal(ChatTranscript.ToolSummary("mcp__openclaw__exec", args), turns[2].Text);
        Assert.Equal("· mcp__openclaw__exec  ls /home/op/workspace | wc -l", turns[2].Text);
        Assert.False(turns[2].IsError);
        Assert.Null(turns[2].ImageUrl);
    }

    [Fact]
    public void TheToolResultBodyIsNeverDrawn() =>
        Assert.DoesNotContain(Turns(OpenClawNativeRunFixtures.ToolRunPage), t => t.Text == "18");

    [Fact]
    public void TheNoToolRunStillRendersItsLoneRollupOnce()
    {
        var turns = Turns(OpenClawNativeRunFixtures.ToolRunPage);

        Assert.Single(turns, t => t.Text == "probe ok, deliver true");
        Assert.Equal(ChatRole.Assistant, turns[^1].Role);
    }

    [Fact]
    public void AFailedToolIsFlaggedFromTheResultInTheSameMessage()
    {
        const string page = """
            [{"role":"user","content":"go"},
             {"role":"assistant","content":[
                {"type":"toolcall","id":"a","name":"exec","arguments":{"command":"false"}},
                {"type":"tool_result","tool_use_id":"a","is_error":true,"content":"exit 1"}]},
             {"role":"assistant","content":[{"type":"text","text":"it failed"}]},
             {"role":"assistant","api":"cli","content":[{"type":"text","text":"it failed"}]}]
            """;

        var turns = Turns(page);

        Assert.Equal(new[] { ChatRole.User, ChatRole.System, ChatRole.Assistant }, turns.Select(t => t.Role).ToArray());
        Assert.True(turns[1].IsError);
        Assert.Equal("· exec  false", turns[1].Text);
    }

    // Negative control: a page with no rollups and no toolcall blocks is
    // byte-identical to what the parser produced before this change.
    [Fact]
    public void APageWithNoRollupsAndNoToolCallsIsUnchanged()
    {
        const string page = """
            [{"role":"user","content":"hi","timestamp":1793285895796},
             {"role":"assistant","content":[{"type":"text","text":"hello"}],"timestamp":1793285896000},
             {"role":"assistant","content":[{"type":"text","text":"and again"}],"timestamp":1793285897000}]
            """;

        var turns = Turns(page);

        Assert.Equal(new[] { "hi", "hello", "and again" }, turns.Select(t => t.Text).ToArray());
        Assert.All(turns, t => Assert.False(t.IsError));
        Assert.DoesNotContain(turns, t => t.Role == ChatRole.System);
    }

    // The rule is positional and does not depend on who spoke: a Discord-shaped
    // page where two people talk to an agent whose run has steps and a rollup.
    [Fact]
    public void ADiscordShapedPageDropsTheRollupWhoeverSpoke()
    {
        const string page = """
            [{"role":"user","content":"[Discord Guild #general channel id:1] alice: question one"},
             {"role":"user","content":"[Discord Guild #general channel id:1] bob: question two"},
             {"role":"assistant","content":[{"type":"text","text":"working"}]},
             {"role":"assistant","content":[{"type":"toolcall","id":"a","name":"Read","arguments":{"file_path":"/x/Foo.cs"}}]},
             {"role":"assistant","content":[{"type":"text","text":"done"}]},
             {"role":"assistant","api":"cli","content":[{"type":"text","text":"working\n\ndone"}]}]
            """;

        var turns = Turns(page);

        Assert.DoesNotContain(turns, t => t.Text.Contains("working") && t.Text.Contains("done"));
        Assert.Equal(new[] { "working", "· Read  Foo.cs", "done" },
            turns.Where(t => t.Role != ChatRole.User).Select(t => t.Text).ToArray());
    }

    [Fact]
    public void AToolCallMessageWithNoTimestampStillDrawsItsLine()
    {
        const string page = """
            [{"role":"assistant","content":[{"type":"toolcall","id":"a","name":"exec","arguments":{"command":"ls"}}]}]
            """;

        var turn = Assert.Single(Turns(page));

        Assert.Equal("· exec  ls", turn.Text);
    }

    // Constructed from the captured message shapes (not itself a capture): the
    // gateway injects runs into a main session with no user turn between them,
    // and a rollup closes the run it summarises, so every reply must survive
    // once and the tool lines must stay in order.
    [Fact]
    public void RunsWithNoUserTurnBetweenThemEachDrawTheirReplyOnceInOrder()
    {
        const string page = """
            [{"role":"user","content":"go"},
             {"role":"assistant","api":"anthropic-messages","content":[{"type":"text","text":"first start"}]},
             {"role":"assistant","api":"anthropic-messages","content":[{"type":"toolcall","id":"a","name":"exec","arguments":{"command":"one"}}]},
             {"role":"assistant","api":"anthropic-messages","content":[{"type":"text","text":"first done"}]},
             {"role":"assistant","api":"cli","idempotencyKey":"cli-assistant:r1","content":[{"type":"text","text":"first start\n\nfirst done"}]},
             {"role":"assistant","api":"cli","idempotencyKey":"cli-assistant:r2","content":[{"type":"text","text":"second, no tools"}]},
             {"role":"assistant","api":"anthropic-messages","content":[{"type":"text","text":"third start"}]},
             {"role":"assistant","api":"anthropic-messages","content":[{"type":"toolcall","id":"b","name":"exec","arguments":{"command":"two"}}]},
             {"role":"assistant","api":"anthropic-messages","content":[{"type":"text","text":"third done"}]},
             {"role":"assistant","api":"cli","idempotencyKey":"cli-assistant:r3","content":[{"type":"text","text":"third start\n\nthird done"}]}]
            """;

        var texts = Turns(page).Select(t => t.Text).ToArray();

        Assert.Equal(new[]
        {
            "go", "first start", "· exec  one", "first done",
            "second, no tools",
            "third start", "· exec  two", "third done"
        }, texts);
    }
}
