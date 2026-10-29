using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-270 Stage 3: at the end of a run the live drawing is replaced by what
// history says. A panel opened partway through a run holds a truncated first
// bubble and none of the tool lines before it, and appending cannot repair that;
// QA replayed a real three-tool run joining at every event with every persisted
// prefix (288 combinations) and 148 ended differently from the full history.
// These are the same shapes on the scrubbed capture, and a control for the
// deferral and the fallbacks.
public class OpenClawRunReconcileTests : IDisposable
{
    public void Dispose() => OpenClawSessions.SetGatewayForTests(null);

    private static OpenClawChatSession Session()
    {
        OpenClawSessions.SetGatewayForTests(null);
        return new("openclaw:agent:main:dashboard:one", "agent:main:dashboard:one", "main");
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static List<(string Name, JsonElement Payload)> Run() =>
        OpenClawNativeRunFixture.Events.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(Json)
            .Select(e => (e.GetProperty("name").GetString()!, e.GetProperty("payload")))
            .Where(e => e.Item2.TryGetProperty("runId", out var r) && r.GetString() == "run-tools-0001")
            .ToList();

    private static (string Step1, string Step3) Steps()
    {
        var final = Run().Where(e => e.Name == "agent")
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

    private static IReadOnlyList<HistoryTurn> Full()
    {
        var (step1, step3) = Steps();
        return Page((ChatRole.User, "go"), (ChatRole.Assistant, step1),
            (ChatRole.System, ToolLine()), (ChatRole.Assistant, step3));
    }

    private static void Feed(OpenClawChatSession session, IEnumerable<(string Name, JsonElement Payload)> events)
    {
        foreach (var (name, payload) in events) session.OnAgentEvent(name, payload);
    }

    private static string[] Texts(OpenClawChatSession s) => s.History.Select(t => t.Text).ToArray();

    // The whole run, with the panel's history landing at the given event: what the
    // stream drew before it is gone, what it draws after is partial.
    private static OpenClawChatSession JoinedAt(int eventIndex, params (ChatRole, string)[] persisted)
    {
        var session = Session();
        var events = Run().Where(e => e.Name is "agent" or "session.tool" or "chat").ToList();

        Feed(session, events.Take(eventIndex));
        session.SetHistory(Page(persisted));
        Feed(session, events.Skip(eventIndex));
        return session;
    }

    [Fact]
    public void AJoinMidwayThroughTheFirstStepEndsEqualToHistory()
    {
        var session = JoinedAt(14, (ChatRole.User, "go"));
        var removed = new List<(int From, int Count)>();
        session.TurnsRemoved += (f, c) => removed.Add((f, c));

        session.ApplyFetchedTail(Full());

        Assert.Equal(Full().Select(t => t.Text), Texts(session));
        Assert.Single(removed);
        Assert.Equal(1, removed[0].From);
    }

    [Fact]
    public void AJoinDuringTheClosingReplyEndsEqualToHistory()
    {
        var (step1, _) = Steps();
        var events = Run().Where(e => e.Name is "agent" or "session.tool" or "chat").ToList();
        var late = events.FindIndex(e => e.Name == "agent"
            && e.Payload.GetProperty("data").TryGetProperty("delta", out var d) && d.GetString() == " ok, 18 files");

        var session = JoinedAt(late, (ChatRole.User, "go"), (ChatRole.Assistant, step1), (ChatRole.System, ToolLine()));
        session.ApplyFetchedTail(Full());

        Assert.Equal(Full().Select(t => t.Text), Texts(session));
    }

    // The twin that made appending ambiguous: two tool lines the summary's length
    // cap has cut to the same text. Matching the tail's last turn alone takes the
    // later twin for the earlier and skips what lies between.
    [Fact]
    public void TwoIdenticalToolLinesAreNotConfusedWhenAppending()
    {
        var session = Session();
        session.SetHistory(Page((ChatRole.User, "go"), (ChatRole.Assistant, "one"), (ChatRole.System, "· Bash  same")));

        session.ApplyFetchedTail(Page((ChatRole.User, "go"), (ChatRole.Assistant, "one"),
            (ChatRole.System, "· Bash  same"), (ChatRole.System, "· Bash  same"), (ChatRole.Assistant, "two")));

        Assert.Equal(new[] { "go", "one", "· Bash  same", "· Bash  same", "two" }, Texts(session));
    }

    // A run too long for the page to reach back to the user's turn leaves nothing
    // safe to replace: the append is all that can be said, and nothing is removed.
    [Fact]
    public void AnchorNotOnThePageFallsBackToAppendingAndRemovesNothing()
    {
        var session = Session();
        session.SetHistory(Page((ChatRole.User, "go")));
        Feed(session, Run().Where(e => e.Name is "agent" or "session.tool").Take(8));
        Feed(session, new[] { ("chat", Json("""{"state":"final"}""")) });
        var removed = 0;
        session.TurnsRemoved += (_, c) => removed += c;
        var before = Texts(session);

        session.ApplyFetchedTail(Page((ChatRole.Assistant, "a different tail")));

        Assert.Equal(0, removed);
        Assert.Equal(before, Texts(session));
    }

    // No user turn on this side: nothing to anchor a replacement on.
    [Fact]
    public void ARunWithNoUserTurnInTheTranscriptFallsBackToAppending()
    {
        var session = Session();
        Feed(session, Run().Where(e => e.Name is "agent" or "session.tool").Take(8));
        Feed(session, new[] { ("chat", Json("""{"state":"final"}""")) });
        var removed = 0;
        session.TurnsRemoved += (_, c) => removed += c;

        session.ApplyFetchedTail(Full());

        Assert.Equal(0, removed);
    }

    // While a run is streaming a doorbell does nothing: the page holds a
    // half-persisted run, and the end is what reconciles it.
    [Fact]
    public void ADoorbellMidRunAddsNothing()
    {
        var session = Session();
        session.SetHistory(Page((ChatRole.User, "go")));
        Feed(session, Run().Where(e => e.Name is "agent").Take(3));
        var before = Texts(session);

        session.ApplyFetchedTail(Full());

        Assert.Equal(before, Texts(session));
    }

    // A run that has drawn nothing, a bare lifecycle start, has nothing to defer
    // for: a delivery arriving after it is appended as ever.
    [Fact]
    public void ALifecycleStartAloneDoesNotDeferADelivery()
    {
        var session = Session();
        session.SetHistory(Page((ChatRole.User, "go")));
        Feed(session, new[] { ("agent", Json("""{"runId":"r","stream":"lifecycle","data":{"phase":"start"}}""")) });

        session.ApplyFetchedTail(Page((ChatRole.User, "go"), (ChatRole.Assistant, "delivered")));

        Assert.Equal(new[] { "go", "delivered" }, Texts(session));
    }

    // With no run involved at all, a doorbell is the append it always was.
    [Fact]
    public void ADeliveryWithNoRunIsAppended()
    {
        var session = Session();
        session.SetHistory(Page((ChatRole.User, "go")));

        session.ApplyFetchedTail(Page((ChatRole.User, "go"), (ChatRole.Assistant, "delivered")));

        Assert.Equal(new[] { "go", "delivered" }, Texts(session));
    }

    // A second reconciliation has nothing left to do: the first cleared the run.
    [Fact]
    public void AfterTheRunIsReconciledALaterDoorbellOnlyAppends()
    {
        var session = JoinedAt(14, (ChatRole.User, "go"));
        session.ApplyFetchedTail(Full());
        var removed = 0;
        session.TurnsRemoved += (_, c) => removed += c;

        session.ApplyFetchedTail(Full());

        Assert.Equal(0, removed);
        Assert.Equal(Full().Select(t => t.Text), Texts(session));
    }
}
