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

    // The same without the note typing adds when replying is off, for the cases
    // about where the typed message lands and not about its note.
    private static string[] TextsSansNote(OpenClawChatSession s) =>
        Texts(s).Where(t => !t.StartsWith("Replying is off", StringComparison.Ordinal)).ToArray();

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

        Assert.Equal(Full().Select(t => t.Text).Append("and one more thing"), TextsSansNote(session));
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

        Assert.Equal(page.Select(t => t.Text), TextsSansNote(session));
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

        Assert.Equal(Full().Select(t => t.Text).Append("and one more thing"), TextsSansNote(session));
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

        Assert.Equal(Full().Select(t => t.Text).Concat(new[] { "ok", "ok" }), TextsSansNote(session));
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

    // --- a failed send's note stays with the message ---

    // Replying is off, so typing mid-run adds the message and a note saying why
    // nothing was sent. Both survive the run's end, once each.
    [Fact]
    public async Task AFailedSendsNoteIsKeptWithItsMessageAtTheRunsEnd()
    {
        var session = Session();
        var events = Run().Where(e => e.Name is "agent" or "session.tool" or "chat").ToList();

        Feed(session, events.Take(14));
        session.SetHistory(Page((ChatRole.User, "go")));
        Feed(session, events.Skip(14).Take(8));
        await TypeAsync(session, "and one more thing");
        Feed(session, events.Skip(22));

        session.ApplyFetchedTail(Full());

        var texts = Texts(session);
        Assert.Equal(1, texts.Count(t => t == "and one more thing"));
        Assert.Equal(1, texts.Count(t => t.StartsWith("Replying is off")));
        Assert.Equal("and one more thing", texts[^2]);
        Assert.StartsWith("Replying is off", texts[^1]);
        Assert.Equal(Full().Select(t => t.Text), texts.Take(Full().Count));
    }

    // The control: the run's own System lines, its tool lines, are still replaced
    // from the page, and a tool line sitting straight after the typed message is
    // not mistaken for a note.
    [Fact]
    public async Task ARunsOwnToolLineAfterATypedMessageIsStillReplacedFromThePage()
    {
        var session = Session();
        var events = Run().Where(e => e.Name is "agent" or "session.tool" or "chat").ToList();
        var toolAt = events.FindIndex(e => e.Name == "session.tool");

        Feed(session, events.Take(toolAt - 1));
        session.SetHistory(Page((ChatRole.User, "go")));
        await TypeAsync(session, "and one more thing");
        Feed(session, events.Skip(toolAt - 1));

        session.ApplyFetchedTail(Full());

        Assert.Equal(1, Texts(session).Count(t => t == ToolLine()));
    }

    // --- QA's typed-after-lag case (CB-270 Stage 3, found on S3's replays) ---
    //
    // The panel joins just after the run's chat final, with a page that lags and
    // holds only k of the run's turns. The person types before it catches up, and
    // the next page holds the run's remaining turns and, once the gateway has it,
    // the typed message. The remaining turns must appear and the typed message
    // must show once. Anchoring on the typed message, which sits after the run,
    // left the remaining turns out until the panel was reopened.

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task ATypedMessageAfterALaggingJoinDoesNotHideTheRunsRemainingTurns(int k, bool pageHoldsTyped)
    {
        var session = Session();
        var events = Run().Where(e => e.Name is "agent" or "session.tool" or "chat").ToList();
        var finalAt = events.FindIndex(e => e.Name == "chat"
            && e.Payload.GetProperty("state").GetString() == "final");

        Feed(session, events.Take(finalAt + 1));
        session.SetHistory(Full().Take(k).ToList());
        await TypeAsync(session, "typed after the run");

        var page = Full().ToList();
        if (pageHoldsTyped)
        {
            page.Add(new HistoryTurn(ChatRole.User, "typed after the run", null, "", DateTime.UtcNow, null, null));
        }

        session.ApplyFetchedTail(page);

        var texts = TextsSansNote(session);
        Assert.Equal(Full().Select(t => t.Text).Append("typed after the run"), texts);
    }

    // --- a limit belongs to the run its send started ---

    // Send with 10 minutes, the run ends done, and then another client's run times
    // out: no note may name this send's 10 minutes.
    [Fact]
    public async Task AnotherClientsTimeoutIsNotAnnouncedWithThisSendsLimit()
    {
        var session = Session();
        session.TimeoutMinutes = 10;
        await TypeAsync(session, "go");
        session.OnAgentEvent("agent", Json("""{"runId":"mine","stream":"assistant","data":{"text":"working"}}"""));
        session.OnRunStatus("done");

        session.OnAgentEvent("agent", Json("""{"runId":"theirs","stream":"assistant","data":{"text":"someone else"}}"""));
        session.OnRunStatus("timeout");

        Assert.DoesNotContain(session.History, t => t.Text.Contains("10 minutes"));
        Assert.Contains(session.History, t => t.Text == "The run's time limit was reached, so the run was stopped.");
    }

    // KNOWN LIMITATION, named as a follow-up: the confirmed arm of typed-after-lag
    // when this panel saw no run at all, so nothing reconciles and the plain
    // append is all there is. The page already shows the typed message, which
    // becomes the anchor, and the turns the lagging first page was missing sit
    // *before* it: appending after the anchor cannot add them, and they stay
    // missing until the panel is reopened. Closing it means inserting page turns
    // before the anchor, which is a larger change than the narrow case justifies
    // here. This pins today's behaviour so that closing it is a visible change to
    // this test. (With a run drawn, as in the cases above, the reconciliation
    // converges: only the no-run path is affected.)
    [Fact]
    public async Task WhenNoRunWasSeenAPageShowingTheTypedMessageStillLeavesTheEarlierTurnsMissing()
    {
        var session = Session();
        session.SetHistory(Full().Take(2).ToList());
        await TypeAsync(session, "typed after the run");

        var page = Full().ToList();
        page.Add(new HistoryTurn(ChatRole.User, "typed after the run", null, "", DateTime.UtcNow, null, null));
        session.ApplyFetchedTail(page);

        var texts = TextsSansNote(session);
        Assert.Equal(1, texts.Count(t => t == "typed after the run"));
        Assert.DoesNotContain(ToolLine(), texts);
    }

    // QA's mid-run typing with the page placing the typed message after the whole
    // run: the panel joins partway through a run, the person types at once, and the
    // run's end page lists the typed message last. The typed message was taken as
    // the turn the run began after, so the turns between the first turn and it
    // never appeared. A message typed while the run was already streaming is never
    // that turn, wherever a page places it.
    [Theory]
    [InlineData(14)]
    [InlineData(30)]
    [InlineData(48)]
    public async Task AMessageTypedJustAfterAMidRunJoinIsNotTheTurnTheRunBeganAfter(int joinAt)
    {
        var session = Session();
        var events = Run().Where(e => e.Name is "agent" or "session.tool" or "chat").ToList();

        Feed(session, events.Take(joinAt));
        session.SetHistory(Full().Take(1).ToList());
        await TypeAsync(session, "typed mid-run");
        Feed(session, events.Skip(joinAt));

        var page = Full().ToList();
        page.Add(new HistoryTurn(ChatRole.User, "typed mid-run", null, "", DateTime.UtcNow, null, null));
        session.ApplyFetchedTail(page);

        Assert.Equal(Full().Select(t => t.Text).Append("typed mid-run"), TextsSansNote(session));
    }

    // A chat delta alone says a run is going, before anything has drawn: a panel
    // joined partway through can be typed into at that moment, and the message
    // still comes after the run began.
    [Fact]
    public async Task AMessageTypedAfterOnlyAChatDeltaWasSeenIsStillMidRun()
    {
        var session = Session();
        var events = Run().Where(e => e.Name is "agent" or "session.tool" or "chat").ToList();
        var firstDraw = events.FindIndex(e => e.Name == "agent");

        session.SetHistory(Full().Take(1).ToList());
        session.OnAgentEvent("chat", Json("""{"runId":"run-tools-0001","state":"delta"}"""));
        await TypeAsync(session, "typed mid-run");
        Feed(session, events.Skip(firstDraw));

        var page = Full().ToList();
        page.Add(new HistoryTurn(ChatRole.User, "typed mid-run", null, "", DateTime.UtcNow, null, null));
        session.ApplyFetchedTail(page);

        Assert.Equal(Full().Select(t => t.Text).Append("typed mid-run"), TextsSansNote(session));
    }

    // KNOWN LIMITATION, named: a message typed before this panel has seen a single
    // event of a run that was already going cannot be told from one typed before
    // the run began, and a page that places it after the whole run then makes it the
    // anchor. In practice the gateway persists a message where it arrived, so a
    // page does not usually show it after a run it preceded; this pins what the
    // panel does when it does.
    [Fact]
    public async Task AMessageTypedBeforeAnyEventOfARunWasSeenIsTakenAsWhatTheRunBeganAfter()
    {
        var session = Session();
        var events = Run().Where(e => e.Name is "agent" or "session.tool" or "chat").ToList();

        session.SetHistory(Full().Take(1).ToList());
        await TypeAsync(session, "typed mid-run");
        Feed(session, events);

        var page = Full().ToList();
        page.Add(new HistoryTurn(ChatRole.User, "typed mid-run", null, "", DateTime.UtcNow, null, null));
        session.ApplyFetchedTail(page);

        var texts = TextsSansNote(session);
        Assert.Equal(1, texts.Count(t => t == "typed mid-run"));
        Assert.DoesNotContain(ToolLine(), texts);
    }
}

