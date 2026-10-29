using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// CB-270 Stage 3: at the end of a run the live drawing is replaced by what
// history says. A panel opened partway through a run holds a truncated first
// bubble and none of the tool lines before it, and appending cannot repair that;
// QA replayed a real three-tool run joining at every event with every persisted
// prefix (288 combinations) and 148 ended differently from the full history.
// These are the same shapes on the scrubbed capture, and a control for the
// deferral and the fallbacks.
[Collection("Settings")]
public class OpenClawRunReconcileTests : IDisposable
{
    public void Dispose() => OpenClawSessions.SetGatewayForTests(null);

    private const string FixtureKey = "agent:main:dashboard:0a1b2c3d-0000-4000-8000-000000000001";

    // A unique key per session: what is running is recorded process-wide, and
    // two cases sharing a key would answer each other's question.
    private static OpenClawChatSession Session()
    {
        OpenClawSessions.SetGatewayForTests(null);
        var key = $"agent:main:dashboard:{Guid.NewGuid()}";
        return new("openclaw:" + key, key, "main");
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

    // Each event goes to the app's own run record, as OnEvent does for every
    // event, and to the chat. The deferral reads the record, so a chat fed alone
    // would never look like it was mid-run.
    private static void Feed(
        OpenClawChatSession session, IEnumerable<(string Name, JsonElement Payload)> events, DateTime? at = null)
    {
        foreach (var (name, original) in events)
        {
            var payload = Json(original.GetRawText().Replace(FixtureKey, session.GatewayKey));
            OpenClawSessions.OnEvent(name, payload, at ?? DateTime.UtcNow);
            session.OnAgentEvent(name, payload);
        }
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

    // A run that starts, draws and never sends its end (a dropped broadcast, a
    // socket that went quiet) must not stop the panel refreshing: once the app's
    // own record no longer calls it generating, the doorbell reconciles. The
    // control is the same run seen as live a moment ago, which defers.
    [Fact]
    public void ARunWhoseEndNeverArrivesStopsDeferringOnceItGoesQuiet()
    {
        var quiet = Session();
        quiet.SetHistory(Page((ChatRole.User, "go")));
        Feed(quiet, Run().Where(e => e.Name is "agent").Take(12), DateTime.UtcNow - OpenClawRunSignal.RunIdle - TimeSpan.FromSeconds(30));
        var drawn = Texts(quiet);
        Assert.True(drawn.Length > 1);

        quiet.ApplyFetchedTail(Full());

        Assert.Equal(Full().Select(t => t.Text), Texts(quiet));

        var live = Session();
        live.SetHistory(Page((ChatRole.User, "go")));
        Feed(live, Run().Where(e => e.Name is "agent").Take(12));
        var before = Texts(live);

        live.ApplyFetchedTail(Full());

        Assert.Equal(before, Texts(live));
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

    // --- typing while the reply streams ---
    //
    // A message typed mid-run is added at once and becomes the last user turn,
    // which the page may not hold yet and which sits after the truncated bubble a
    // mid-run join leaves. The run still has to reconcile, and what was typed
    // must be on screen once, throughout and after.

    private static async Task TypeAsync(OpenClawChatSession session, string text)
    {
        // Replying off: the message is added and a note says why nothing was
        // sent, which is all that matters here (the turn and where it lands).
        OrbweaverSettings.OpenClawReplyEnabled = false;
        await session.SendAsync(text);
    }

    [Fact]
    public async Task AMessageTypedMidRunNotYetOnThePageIsKeptOnceAfterTheFullReply()
    {
        var session = Session();
        var events = Run().Where(e => e.Name is "agent" or "session.tool" or "chat").ToList();

        Feed(session, events.Take(14));
        session.SetHistory(Page((ChatRole.User, "go")));
        Feed(session, events.Skip(14).Take(8));
        await TypeAsync(session, "and one more thing");
        Feed(session, events.Skip(22));

        session.ApplyFetchedTail(Full());

        Assert.Equal(Full().Select(t => t.Text).Append("and one more thing"), Texts(session));
    }

    // The page already holds it, in the middle of the run: the page's copy is
    // the one drawn, once.
    [Fact]
    public async Task AMessageTypedMidRunThatThePageNowHoldsIsDrawnOnce()
    {
        var (step1, step3) = Steps();
        var session = Session();
        var events = Run().Where(e => e.Name is "agent" or "session.tool" or "chat").ToList();

        Feed(session, events.Take(14));
        session.SetHistory(Page((ChatRole.User, "go")));
        Feed(session, events.Skip(14).Take(8));
        await TypeAsync(session, "and one more thing");
        Feed(session, events.Skip(22));

        var page = Page((ChatRole.User, "go"), (ChatRole.Assistant, step1), (ChatRole.User, "and one more thing"),
            (ChatRole.System, ToolLine()), (ChatRole.Assistant, step3));
        session.ApplyFetchedTail(page);

        Assert.Equal(page.Select(t => t.Text), Texts(session));
    }

    // Typed after the page landed and before anything more was drawn, so there is
    // no live row to mark where the run began.
    [Fact]
    public async Task AMessageTypedWhenNothingMoreWasDrawnIsKeptAndTheReplyReconciles()
    {
        var (step1, _) = Steps();
        var session = Session();
        var events = Run().Where(e => e.Name is "agent" or "session.tool" or "chat").ToList();

        Feed(session, events.Take(30));
        session.SetHistory(Page((ChatRole.User, "go"), (ChatRole.Assistant, step1)));
        await TypeAsync(session, "and one more thing");
        Feed(session, events.Where(e => e.Name == "chat" && e.Payload.GetProperty("state").GetString() == "final"));

        session.ApplyFetchedTail(Full());

        Assert.Equal(Full().Select(t => t.Text).Append("and one more thing"), Texts(session));
    }

    // The same message typed twice is two messages: a page that shows one of them
    // accounts for one, and the other is kept.
    [Fact]
    public async Task TwoIdenticalMessagesTypedMidRunAreEachAccountedFor()
    {
        var session = Session();
        var events = Run().Where(e => e.Name is "agent" or "session.tool" or "chat").ToList();

        Feed(session, events.Take(14));
        session.SetHistory(Page((ChatRole.User, "go")));
        Feed(session, events.Skip(14).Take(8));
        await TypeAsync(session, "ok");
        await TypeAsync(session, "ok");
        Feed(session, events.Skip(22));

        session.ApplyFetchedTail(Full().Append(new HistoryTurn(ChatRole.User, "ok", null, "", DateTime.UtcNow, null, null)).ToList());

        Assert.Equal(Full().Select(t => t.Text).Concat(new[] { "ok", "ok" }), Texts(session));
    }

    // --- a reconciliation mid-run, on the idle bound ---
    //
    // Once the run record calls a run quiet, a doorbell reconciles it while its
    // stream may still be going. The bubble in flight is then removed from the
    // transcript, and the session must let go of it: otherwise every later delta
    // updates a turn with no row and the reply is invisible until the real end.

    private static readonly TimeSpan LongAgo = OpenClawRunSignal.RunIdle + TimeSpan.FromSeconds(30);

    [Fact]
    public void TextStreamedAfterAMidRunReconciliationOpensAFreshRow()
    {
        var (step1, _) = Steps();
        var session = Session();
        session.SetHistory(Page((ChatRole.User, "go")));
        var events = Run().Where(e => e.Name is "agent").ToList();
        Feed(session, events.Take(12), DateTime.UtcNow - LongAgo);
        session.ApplyFetchedTail(Page((ChatRole.User, "go"), (ChatRole.Assistant, step1)));
        var added = new List<ChatTurn>();
        session.TurnAdded += added.Add;

        session.OnAgentEvent("agent", Json("""{"runId":"run-tools-0001","stream":"assistant","data":{"delta":" and more words"}}"""));

        var row = Assert.Single(added);
        Assert.Contains("and more words", row.Text);
        Assert.Contains(row, session.History);
    }

    [Fact]
    public void AToolResultAfterAMidRunReconciliationLandsOnARowThatExists()
    {
        var session = Session();
        session.SetHistory(Page((ChatRole.User, "go")));
        Feed(session, new[] { ("session.tool", Json(
            """{"runId":"run-tools-0001","sessionKey":"k","data":{"phase":"start","name":"Bash","toolCallId":"t1"}}""")) },
            DateTime.UtcNow - LongAgo);
        session.ApplyFetchedTail(Page((ChatRole.User, "go"), (ChatRole.System, "· Bash")));

        session.OnAgentEvent("session.tool", Json(
            """{"data":{"phase":"result","name":"Bash","toolCallId":"t1","isError":true}}"""));

        Assert.Contains(session.History, t => t.IsError);
    }
}

