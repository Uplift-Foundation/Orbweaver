using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// CB-270 Stage 3: what a chat panel on a native session does with the gateway's
// own message stream. A run arrives as deltas (only its first and last assistant
// events carry data.text), tool starts and results, a lifecycle end and a chat
// final; the panel has to rebuild the reply, split it at each tool so the live
// view equals history's [step, tool line, step], complete it when the run ends,
// and stop refetching history on every delta.
//
// The replay cases use the scrubbed capture in OpenClawNativeRunFixture.
[Collection("Settings")]
public class OpenClawLiveRunTests : IDisposable
{
    public void Dispose() => OpenClawSessions.SetGatewayForTests(null);

    private static OpenClawChatSession Session() =>
        new("openclaw:agent:main:dashboard:one", "agent:main:dashboard:one", "main");

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement Delta(string delta, string? runId = null) =>
        Json("{" + Run(runId) + "\"stream\":\"assistant\",\"data\":{\"delta\":" + JsonSerializer.Serialize(delta) + "}}");

    private static JsonElement Snapshot(string text, string? runId = null) =>
        Json("{" + Run(runId) + "\"stream\":\"assistant\",\"data\":{\"text\":" + JsonSerializer.Serialize(text) + "}}");

    private static string Run(string? runId) => runId is null ? "" : $"\"runId\":\"{runId}\",";

    private static JsonElement Tool(string phase, string? id = "t1", string? name = "Grep", string? extra = null) =>
        Json("{\"data\":{\"phase\":\"" + phase + "\""
            + (name is null ? "" : $",\"name\":\"{name}\"")
            + (id is null ? "" : $",\"toolCallId\":\"{id}\"")
            + (extra is null ? "" : "," + extra)
            + "}}");

    private static string[] Texts(OpenClawChatSession s) => s.History.Select(t => t.Text).ToArray();

    // The fixture, as (name, payload) in capture order.
    private static List<(string Name, JsonElement Payload)> Events() =>
        OpenClawNativeRunFixture.Events.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(Json)
            .Select(e => (e.GetProperty("name").GetString()!, e.GetProperty("payload")))
            .ToList();

    private static void Replay(OpenClawChatSession session, string runId)
    {
        foreach (var (name, payload) in Events())
        {
            if (payload.TryGetProperty("runId", out var r) && r.GetString() == runId)
            {
                session.OnAgentEvent(name, payload);
            }
        }
    }

    // --- rebuilding the reply from deltas ---

    [Fact]
    public void DeltasAreAppendedToTheFirstSnapshotInOneBubble()
    {
        var session = Session();

        session.OnAgentEvent("agent", Snapshot("Hey Boss! I'm"));
        session.OnAgentEvent("agent", Delta(" Nova"));
        session.OnAgentEvent("agent", Delta(" here"));

        Assert.Equal(new[] { "Hey Boss! I'm Nova here" }, Texts(session));
    }

    // The final event of a run carries the whole text, and takes over from the
    // accumulated deltas rather than being added to them.
    [Fact]
    public void AFinalSnapshotReplacesTheAccumulatedText()
    {
        var session = Session();

        session.OnAgentEvent("agent", Snapshot("Hey"));
        session.OnAgentEvent("agent", Delta(" there"));
        session.OnAgentEvent("agent", Snapshot("Hey there, Boss"));

        Assert.Equal(new[] { "Hey there, Boss" }, Texts(session));
    }

    // Neither text nor a delta: nothing to draw, and no turn made for it.
    [Theory]
    [InlineData("""{"stream":"assistant","data":{}}""")]
    [InlineData("""{"stream":"assistant","data":{"text":"","delta":""}}""")]
    public void AnEventWithNoTextAndNoDeltaDoesNothing(string json)
    {
        var session = Session();

        session.OnAgentEvent("agent", Json(json));

        Assert.Empty(session.History);
    }

    // --- the split at a tool ---

    [Fact]
    public void AToolStartClosesTheBubbleAndTheTextAfterItOpensANewOne()
    {
        var session = Session();

        session.OnAgentEvent("agent", Snapshot("Step one"));
        session.OnAgentEvent("session.tool", Tool("start", extra: "\"args\":{\"command\":\"ls\"}"));
        session.OnAgentEvent("agent", Delta("\n\nStep"));
        session.OnAgentEvent("agent", Delta(" three"));

        Assert.Equal(new[] { "Step one", "· Grep  ls", "Step three" }, Texts(session));
        Assert.True(session.History[0].IsComplete);
        Assert.False(session.History[2].IsComplete);
    }

    // Whitespace alone is not a bubble: the joiner the stream puts after a tool
    // waits for words.
    [Fact]
    public void WhitespaceAfterAToolDoesNotOpenABubble()
    {
        var session = Session();

        session.OnAgentEvent("agent", Snapshot("Step one"));
        session.OnAgentEvent("session.tool", Tool("start"));
        session.OnAgentEvent("agent", Delta("\n\n"));

        Assert.Equal(new[] { "Step one", "· Grep" }, Texts(session));
    }

    // The final snapshot after a tool is the whole rollup, and the bubble in
    // flight is still only the part past its own start.
    [Fact]
    public void TheFinalSnapshotAfterAToolLeavesTheSecondBubbleAtItsStep()
    {
        var session = Session();

        session.OnAgentEvent("agent", Snapshot("Step one"));
        session.OnAgentEvent("session.tool", Tool("start"));
        session.OnAgentEvent("agent", Delta("\n\nStep three"));
        session.OnAgentEvent("agent", Snapshot("Step one\n\nStep three"));

        Assert.Equal(new[] { "Step one", "· Grep", "Step three" }, Texts(session));
    }

    // A snapshot that disagrees with the steps already drawn cannot say where the
    // current bubble starts, so it is ignored rather than guessed at. Shorter
    // than the closed text is the plainest case of it.
    [Fact]
    public void ASnapshotThatDisagreesWithTheClosedStepsIsIgnored()
    {
        var session = Session();

        session.OnAgentEvent("agent", Snapshot("Step one"));
        session.OnAgentEvent("session.tool", Tool("start"));
        session.OnAgentEvent("agent", Snapshot("Step"));
        session.OnAgentEvent("agent", Snapshot("A different step one\n\nStep three"));
        session.OnAgentEvent("agent", Delta("\n\nStep two"));

        Assert.Equal(new[] { "Step one", "· Grep", "Step two" }, Texts(session));
    }

    // A tool with no reply text in flight just adds its line.
    [Fact]
    public void AToolStartWithNoBubbleOpenJustAddsTheLine()
    {
        var session = Session();

        session.OnAgentEvent("session.tool", Tool("start"));

        Assert.Equal(new[] { "· Grep" }, Texts(session));
    }

    // Thinking is its own turn and a tool does not close it.
    [Fact]
    public void AToolStartDoesNotCloseAThinkingTurn()
    {
        var session = Session();

        session.OnAgentEvent("agent", AgentThinking("hmm"));
        session.OnAgentEvent("session.tool", Tool("start"));

        Assert.False(session.History[0].IsComplete);
    }

    private static JsonElement AgentThinking(string text) =>
        Json("{\"stream\":\"thinking\",\"data\":{\"text\":" + JsonSerializer.Serialize(text) + "}}");

    // --- a result ---

    [Fact]
    public void AnErrorResultMarksTheLineWithoutChangingItsText()
    {
        var session = Session();
        var updates = new List<ChatTurn>();
        session.OnAgentEvent("session.tool", Tool("start"));
        session.TurnUpdated += updates.Add;
        var before = session.History[0].Text;

        session.OnAgentEvent("session.tool", Tool("result", extra: "\"isError\":true"));

        Assert.True(session.History[0].IsError);
        Assert.Equal(before, session.History[0].Text);
        Assert.Single(session.History);
        Assert.Contains(session.History[0], updates);
    }

    [Fact]
    public void ACleanResultChangesNothing()
    {
        var session = Session();
        session.OnAgentEvent("session.tool", Tool("start"));

        session.OnAgentEvent("session.tool", Tool("result", extra: "\"isError\":false"));

        Assert.False(session.History[0].IsError);
        Assert.Single(session.History);
    }

    // isError as anything but a boolean is not an error.
    [Fact]
    public void AnErrorFlagThatIsNotABooleanIsNotAnError()
    {
        var session = Session();
        session.OnAgentEvent("session.tool", Tool("start"));

        session.OnAgentEvent("session.tool", Tool("result", extra: "\"isError\":\"true\""));

        Assert.False(session.History[0].IsError);
    }

    // A result for a start this client never saw still draws the line, carrying
    // the flag from the payload.
    [Fact]
    public void AResultForAnUnseenStartAddsTheLine()
    {
        var session = Session();

        session.OnAgentEvent("session.tool", Tool("result", "unseen", "Bash", "\"isError\":true"));

        var line = Assert.Single(session.History);
        Assert.Equal("· Bash", line.Text);
        Assert.True(line.IsError);
    }

    // The same, with no id to find the start by.
    [Fact]
    public void AResultWithNoIdButANameAddsTheLine()
    {
        var session = Session();

        session.OnAgentEvent("session.tool", Tool("result", null, "Bash"));

        Assert.Equal(new[] { "· Bash" }, Texts(session));
    }

    // A result that says neither which call nor which tool adds nothing.
    [Fact]
    public void AResultWithNeitherAnIdNorANameAddsNothing()
    {
        var session = Session();

        session.OnAgentEvent("session.tool", Tool("result", null, null));
        session.OnAgentEvent("session.tool", Tool("result", "unseen", null));

        Assert.Empty(session.History);
    }

    // A line learns about its result once: the second result for the same call
    // is treated as an unseen one, which is what it is.
    [Fact]
    public void AToolLinePairedWithItsResultIsNotPairedAgain()
    {
        var session = Session();
        session.OnAgentEvent("session.tool", Tool("start"));
        session.OnAgentEvent("session.tool", Tool("result"));

        session.OnAgentEvent("session.tool", Tool("result", extra: "\"isError\":true"));

        Assert.Equal(2, session.History.Count);
        Assert.False(session.History[0].IsError);
        Assert.True(session.History[1].IsError);
    }

    // --- ending the run ---

    [Theory]
    [InlineData("end")]
    [InlineData("error")]
    public void ALifecycleEndCompletesTheBubble(string phase)
    {
        var session = Session();
        session.OnAgentEvent("agent", Snapshot("Working"));

        session.OnAgentEvent("agent", Json("{\"stream\":\"lifecycle\",\"data\":{\"phase\":\"" + phase + "\"}}"));

        Assert.True(session.History[0].IsComplete);
    }

    [Theory]
    [InlineData("""{"stream":"lifecycle","data":{"phase":"start"}}""")]
    [InlineData("""{"stream":"lifecycle","data":{"phase":"model"}}""")]
    [InlineData("""{"stream":"lifecycle"}""")]
    public void OtherLifecycleRowsDoNotComplete(string json)
    {
        var session = Session();
        session.OnAgentEvent("agent", Snapshot("Working"));

        session.OnAgentEvent("agent", Json(json));

        Assert.False(session.History[0].IsComplete);
    }

    [Theory]
    [InlineData("final")]
    [InlineData("error")]
    [InlineData("aborted")]
    public void AChatEndStateCompletesTheBubble(string state)
    {
        OpenClawSessions.SetGatewayForTests(null);
        var session = Session();
        session.OnAgentEvent("agent", Snapshot("Working"));

        session.OnAgentEvent("chat", Json("{\"state\":\"" + state + "\"}"));

        Assert.True(session.History[0].IsComplete);
    }

    // The next run's first words never overwrite the last run's reply.
    [Fact]
    public void ADifferentRunIdCompletesThePreviousBubble()
    {
        var session = Session();

        session.OnAgentEvent("agent", Snapshot("First reply", "run-a"));
        session.OnAgentEvent("agent", Snapshot("Second", "run-b"));

        Assert.Equal(new[] { "First reply", "Second" }, Texts(session));
        Assert.True(session.History[0].IsComplete);
        Assert.False(session.History[1].IsComplete);
    }

    // After the run ends the accumulated text is gone: a following delta-only
    // event does not resurrect the old reply as the start of a new bubble.
    [Fact]
    public void ARunEndForgetsTheTextSoFar()
    {
        var session = Session();
        session.OnAgentEvent("agent", Snapshot("Done", "run-a"));
        session.OnAgentEvent("agent", Json("""{"stream":"lifecycle","data":{"phase":"end"}}"""));

        session.OnAgentEvent("agent", Delta("Next", "run-b"));

        Assert.Equal(new[] { "Done", "Next" }, Texts(session));
    }

    // Ending a run with nothing on screen still clears what it had built up.
    [Fact]
    public void ARunEndWithNoBubbleStillClearsTheRun()
    {
        var session = Session();
        session.OnAgentEvent("session.tool", Tool("start"));
        session.OnAgentEvent("agent", Json("""{"stream":"lifecycle","data":{"phase":"end"}}"""));

        session.OnAgentEvent("session.tool", Tool("result", extra: "\"isError\":true"));

        // The start was forgotten, so the result is an unseen one and draws a line.
        Assert.Equal(2, session.History.Count);
    }

    // --- the doorbell: refetch on the run's end, not on every delta ---

    private static OpenClawGateway Gateway(WebSocket socket) =>
        new("gw.local", 4443, "gw-token",
            (_, _, _, _) => Task.FromResult(
                new OpenClawSocket.Connection(socket, Stream.Null, "fp-abc")),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

    private static async Task<FakeGatewaySocket> ConnectedAsync()
    {
        var socket = new FakeGatewaySocket();
        socket.PushEvent("connect.challenge", new { nonce = "nonce-1" });
        socket.OnRequest = request =>
            request.Method == "connect"
                ? FakeGatewaySocket.Ok(request.Id, new
                {
                    protocol = 4,
                    server = new { version = "1.2.3" },
                    auth = new { scopes = new[] { "operator.read", "operator.write" } },
                    policy = new { tickIntervalMs = 15_000, maxPayload = 1_048_576 }
                })
                : FakeGatewaySocket.Ok(request.Id, new { messages = Array.Empty<object>() });

        var gateway = Gateway(socket);
        await gateway.ConnectAsync(null, CancellationToken.None);
        OpenClawSessions.SetGatewayForTests(gateway);
        return socket;
    }

    private static int HistoryRequests(FakeGatewaySocket socket) =>
        socket.Requests.Count(r => r.Method == "chat.history");

    private static async Task<int> SettledAsync(FakeGatewaySocket socket, int atLeast)
    {
        for (var i = 0; i < 200 && HistoryRequests(socket) < atLeast; i++) await Task.Delay(10);
        await Task.Delay(50);
        return HistoryRequests(socket);
    }

    // The negative control for the whole gate: a chat delta rings no doorbell.
    [Fact]
    public async Task AChatDeltaDoesNotRefetchHistoryButAFinalDoes()
    {
        var socket = await ConnectedAsync();
        var session = Session();

        session.OnAgentEvent("chat", Json("""{"state":"delta","deltaText":"x"}"""));
        Assert.Equal(0, await SettledAsync(socket, 1));
        Assert.Empty(session.History);

        session.OnAgentEvent("chat", Json("""{"state":"final"}"""));
        Assert.Equal(1, await SettledAsync(socket, 1));
    }

    // --- the captured runs, end to end through the session ---

    [Fact]
    public async Task ReplayingTheToolRunGivesStepToolStepAndRingsTheDoorbellTwice()
    {
        var socket = await ConnectedAsync();
        var session = Session();

        Replay(session, "run-tools-0001");
        await SettledAsync(socket, 2);

        var toolLine = ChatTranscript.ToolSummary("mcp__openclaw__exec",
            Json("""{"command":"ls /home/op/workspace | wc -l","title":"Count workspace files"}"""));

        Assert.Equal(3, session.History.Count);
        Assert.StartsWith("Hey Boss! I'm Nova", session.History[0].Text);
        Assert.Equal(toolLine, session.History[1].Text);
        Assert.Equal("probe ok, 18 files", session.History[2].Text);
        Assert.All(session.History, t => Assert.True(t.IsComplete));
        Assert.False(session.History[1].IsError);

        // One session.message and one chat final, not one per delta (25 before).
        // The capture's second session.message carries no runId and so is not
        // part of this run's replay.
        Assert.Equal(2, HistoryRequests(socket));
    }

    // The second run, straight after, is a bubble of its own, and the first
    // run's reply is untouched.
    [Fact]
    public void TheNextRunDoesNotOverwriteTheLast()
    {
        OpenClawSessions.SetGatewayForTests(null);
        var session = Session();

        Replay(session, "run-tools-0001");
        var first = session.History.Select(t => t.Text).ToArray();
        Replay(session, "run-notools-0002");

        Assert.Equal(first.Concat(new[] { "probe ok, deliver true" }), Texts(session));
    }

    // The guard against a tripled reply at run end: history, drawing the same
    // run as step, tool line, step, matches the live view turn for turn, so the
    // refetch that follows the run appends nothing.
    [Fact]
    public void TheRefetchAtTheEndOfARunAppendsNothingWhenHistoryAgrees()
    {
        OpenClawSessions.SetGatewayForTests(null);
        var session = Session();
        Replay(session, "run-tools-0001");
        var added = new List<ChatTurn>();
        session.TurnAdded += added.Add;

        session.AppendNewTail(session.History.Select(t => new HistoryTurn(
            t.Role, t.Text, null, "", DateTime.UtcNow, null, null)).ToList());

        Assert.Empty(added);
        Assert.Equal(3, session.History.Count);
    }

    // --- a panel opened mid-run (QA: Ines) ---
    //
    // The final history of the captured run, as a page would hold it: the user's
    // turn, step one, the tool line, step three. Step one is the final
    // snapshot's own text, which is what the gateway persisted.
    private static (string Step1, string Step3) Steps()
    {
        var final = Events()
            .Where(e => e.Name == "agent" && e.Payload.GetProperty("runId").GetString() == "run-tools-0001")
            .Select(e => e.Payload.GetProperty("data"))
            .Last(d => d.TryGetProperty("text", out var t) && t.GetString()!.Length > 100)
            .GetProperty("text").GetString()!;

        var parts = final.Split("\n\n");
        return (parts[0], parts[1]);
    }

    private static string ToolLine() =>
        ChatTranscript.ToolSummary("mcp__openclaw__exec",
            Json("""{"command":"ls /home/op/workspace | wc -l","title":"Count workspace files"}"""));

    private static IReadOnlyList<HistoryTurn> Page(params (ChatRole Role, string Text)[] turns) =>
        turns.Select(t => new HistoryTurn(t.Role, t.Text, null, "", DateTime.UtcNow, null, null)).ToList();

    private static List<(string Name, JsonElement Payload)> ToolRunEvents() =>
        Events().Where(e => e.Payload.TryGetProperty("runId", out var r)
                            && r.GetString() == "run-tools-0001"
                            && e.Name is "agent" or "session.tool").ToList();

    // The case QA raised: the panel opens once step one has been persisted, so
    // history already holds it and the stream carries on from the tool. The
    // final snapshot is the whole rollup, and must not put step one back on
    // screen as part of the last bubble.
    [Fact]
    public void APanelOpenedAfterStepOneWasPersistedDrawsEachStepOnce()
    {
        OpenClawSessions.SetGatewayForTests(null);
        var (step1, step3) = Steps();
        var session = Session();
        session.SetHistory(Page((ChatRole.User, "go"), (ChatRole.Assistant, step1)));

        var events = ToolRunEvents();
        foreach (var (name, payload) in events.Skip(events.FindIndex(e => e.Name == "session.tool")))
        {
            session.OnAgentEvent(name, payload);
        }

        Assert.Equal(new[] { "go", step1, ToolLine(), step3 }, Texts(session));

        // And the refetch that follows the run finds nothing to add.
        var added = new List<ChatTurn>();
        session.TurnAdded += added.Add;
        session.AppendNewTail(Page((ChatRole.User, "go"), (ChatRole.Assistant, step1),
            (ChatRole.System, ToolLine()), (ChatRole.Assistant, step3)));

        Assert.Empty(added);
    }

    // The other way round: the panel is bound and the stream is already
    // arriving when the history page lands. What came in before the page is in
    // it or is not coming back, so it is not drawn a second time.
    [Fact]
    public void StreamTextReceivedBeforeTheHistoryPageLandsIsNotDrawnAgain()
    {
        OpenClawSessions.SetGatewayForTests(null);
        var (step1, step3) = Steps();
        var session = Session();

        var events = ToolRunEvents();
        var toolAt = events.FindIndex(e => e.Name == "session.tool");

        // Joined partway through step one, so the first event seen is a delta.
        foreach (var (name, payload) in events.Skip(5).Take(toolAt - 5)) session.OnAgentEvent(name, payload);
        session.SetHistory(Page((ChatRole.User, "go"), (ChatRole.Assistant, step1)));
        foreach (var (name, payload) in events.Skip(toolAt)) session.OnAgentEvent(name, payload);

        Assert.Equal(new[] { "go", step1, ToolLine(), step3 }, Texts(session));
    }
}

