using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Orbweaver.Tests;

// CB-356 in the two sessions that tail a transcript: the boundary and the
// summary can reach a session in different reads (a live tail that lands
// between the two writes, a page seam that falls between them, a mirror delta
// per write), and the panel must still end up with one row for the pair. And
// a compaction turn must never settle the optimistic bubble of a message the
// user is still waiting on, since it is not the user's echo.
//
// In UiTests for the reason LocalCliChatSessionTests gives: the reads finish on
// the Avalonia dispatcher.
[Collection("Settings")]
public class CompactionSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cb356-session-" + Guid.NewGuid().ToString("N"));

    public CompactionSessionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static string CapturePath([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "TranscriptTests", "Captures", "cb356", "auto-adjacent.jsonl"));

    private static readonly string[] Capture = File.ReadAllLines(CapturePath());
    private static string AssistantBefore => Capture[0];
    private static string BoundaryRow => Capture[1];
    private static string SummaryRow => Capture[2];

    private static CompactionNote Merged =>
        ChatTranscript.Map(Capture).Single(r => r.Turn.Compaction is not null).Turn.Compaction!;

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private static string User(string uuid, string text) =>
        "{\"type\":\"user\",\"uuid\":\"" + uuid + "\",\"timestamp\":\"2026-08-24T12:00:00Z\","
        + "\"message\":{\"role\":\"user\",\"content\":" + Json(text) + "}}";

    private static string Assistant(string uuid, string text) =>
        "{\"type\":\"assistant\",\"uuid\":\"" + uuid + "\",\"timestamp\":\"2026-08-24T12:00:01Z\","
        + "\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":" + Json(text) + "}]}}";

    private string Transcript(IEnumerable<string> rows)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, string.Join('\n', rows) + "\n");
        return path;
    }

    private static void PumpUntil(Func<bool> done, string what)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(done(), $"timed out waiting for {what}");
    }

    private static LocalCliChatSession Started(string path)
    {
        var session = new LocalCliChatSession("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TranscriptPath = path, State = "idle", Title = "",
        });
        session.Start();
        PumpUntil(() => session.State == RemoteChatState.Connected, "the initial load");
        return session;
    }

    private static void Invoke(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);

    private static void SetField(object target, string field, object? value) =>
        target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    private static T GetField<T>(object target, string field) =>
        (T)target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    // --- the local session ----------------------------------------------------

    // The boundary and the summary in two separate writes, each picked up by its
    // own read of the tail: one row, added once and then updated in place.
    [AvaloniaFact]
    public void ATailThatReadsTheTwoHalvesSeparatelyShowsOneRow()
    {
        var path = Transcript(new[] { AssistantBefore });
        var session = Started(path);
        var added = new List<ChatTurn>();
        var updated = new List<ChatTurn>();
        session.TurnAdded += added.Add;
        session.TurnUpdated += updated.Add;

        File.AppendAllText(path, BoundaryRow + "\n");
        Invoke(session, "Pump");
        PumpUntil(() => session.History.Count == 2, "the boundary");
        Assert.False(session.History[1].Compaction!.HasSummary);

        File.AppendAllText(path, SummaryRow + "\n");
        Invoke(session, "Pump");
        PumpUntil(() => session.History[1].Compaction!.HasSummary, "the summary");

        Assert.Equal(2, session.History.Count);
        Assert.Equal(Merged, session.History[1].Compaction);
        Assert.Equal(Merged.Line, session.History[1].Text);
        Assert.Same(session.History[1], Assert.Single(added));
        Assert.Same(session.History[1], Assert.Single(updated));
    }

    // The opening window starts inside the boundary row, so the summary is the
    // first turn shown, as an orphan. Paging back reads the boundary as the
    // older page's last turn: the shown row takes its numbers and the page's
    // copy is not inserted, so there is still exactly one row.
    [AvaloniaFact]
    public async Task APageSeamBetweenTheTwoHalvesStillShowsOneRow()
    {
        var padding = new string('x', 4000);
        var before = Enumerable.Range(0, 50).Select(i => User("u" + i, $"older {i} {padding}"));
        var boundary = JsonNode.Parse(BoundaryRow)!.AsObject();
        boundary["pad"] = new string('p', 150_000);
        var after = Enumerable.Range(0, 112).Select(i => Assistant("a" + i, $"after {i} {padding}"));

        var session = Started(Transcript(before.Append(boundary.ToJsonString()).Append(SummaryRow).Concat(after)));

        // The precondition this test is about: the window split the pair.
        var shown = session.History[0];
        Assert.True(shown.Compaction is { HasBoundary: false, HasSummary: true },
            "the opening window should start between the boundary and the summary");
        Assert.True(session.HasMore);

        var updated = new List<ChatTurn>();
        var prepended = 0;
        session.TurnUpdated += updated.Add;
        session.HistoryPrepended += n => prepended += n;

        Assert.True(await session.LoadOlderAsync(CancellationToken.None));

        Assert.Equal(50, prepended);
        Assert.Same(shown, Assert.Single(session.History, t => t.Compaction is not null));
        Assert.Equal(Merged, shown.Compaction);
        Assert.Equal(Merged.Line, shown.Text);
        Assert.Same(shown, Assert.Single(updated));
        Assert.Equal(ChatRole.User, session.History[0].Role);
    }

    // A message sent from the panel waits as an optimistic bubble until its
    // echo arrives. A compaction is not that echo and must leave it waiting.
    [AvaloniaFact]
    public void ACompactionDoesNotSettleTheLocalPendingBubble()
    {
        var session = new LocalCliChatSession("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TranscriptPath = Transcript(new[] { User("seed", "seed") }), State = "idle",
        });
        var pending = new ChatTurn { Role = ChatRole.User, Text = "Conversation compacted" };
        Invoke(session, "Add", pending);
        SetField(session, "_pending", pending);
        SetField(session, "_pendingRaw", "Conversation compacted");
        SetField(session, "_pendingCaption", "Conversation compacted");
        SetField(session, "_pendingAt", DateTimeOffset.Now);

        var compaction = Assert.Single(ChatTranscript.Map(new[] { BoundaryRow, SummaryRow })).Turn;
        Invoke(session, "Add", compaction);

        Assert.Same(pending, GetField<ChatTurn?>(session, "_pending"));
        Assert.Equal(2, session.History.Count);
        Assert.Same(compaction, session.History[1]);
    }

    // --- the mirror session -----------------------------------------------------

    private const string Far = "job-hunter";

    private static RemoteControlChatSession Mirror() => new("rc-1", ".claude-board", Far);

    private static void Deliver(RemoteControlChatSession session, RemoteMirrorClient.MirrorDelivery mode,
        IEnumerable<string> rows) =>
        Invoke(session, "OnDelivered", new RemoteMirrorClient.MirrorRows(
            Far, mode, MirrorProtocol.TurnsFrom(rows, MirrorProtocol.CliClaudeCode), MirrorProtocol.CliClaudeCode, 1));

    // A far side that sent the two halves as separate turns in one window (an
    // older far Buddy, or a window built across two parses) still draws one row.
    [AvaloniaFact]
    public void AMirrorWindowHoldingTheHalvesAsSeparateTurnsShowsOneRow()
    {
        var session = Mirror();
        var turns = MirrorProtocol.TurnsFrom(new[] { AssistantBefore, BoundaryRow }, MirrorProtocol.CliClaudeCode)
            .Concat(MirrorProtocol.TurnsFrom(new[] { SummaryRow }, MirrorProtocol.CliClaudeCode))
            .ToList();
        Assert.Equal(3, turns.Count);

        Invoke(session, "OnDelivered", new RemoteMirrorClient.MirrorRows(
            Far, RemoteMirrorClient.MirrorDelivery.Window, turns, MirrorProtocol.CliClaudeCode, 1));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Merged, Assert.Single(session.History, t => t.Compaction is not null).Compaction);
        Assert.Equal(3, session.History.Count);   // the live-view banner, the assistant, the compaction
    }

    [AvaloniaFact]
    public void AMirrorDeltaPerHalfFoldsIntoOneRow()
    {
        var session = Mirror();
        Deliver(session, RemoteMirrorClient.MirrorDelivery.Window, new[] { AssistantBefore });
        Dispatcher.UIThread.RunJobs();
        var count = session.History.Count;

        var updated = new List<ChatTurn>();
        session.TurnUpdated += updated.Add;

        Deliver(session, RemoteMirrorClient.MirrorDelivery.Delta, new[] { BoundaryRow });
        Dispatcher.UIThread.RunJobs();
        Deliver(session, RemoteMirrorClient.MirrorDelivery.Delta, new[] { SummaryRow });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(count + 1, session.History.Count);
        Assert.Equal(Merged, session.History[^1].Compaction);
        Assert.Same(session.History[^1], Assert.Single(updated));
    }

    [AvaloniaFact]
    public void ACompactionDoesNotSettleTheMirrorPendingBubble()
    {
        var session = Mirror();
        Deliver(session, RemoteMirrorClient.MirrorDelivery.Window, new[] { AssistantBefore });
        Dispatcher.UIThread.RunJobs();

        var pending = new ChatTurn { Role = ChatRole.User, Text = "Conversation compacted" };
        SetField(session, "_pending", pending);
        SetField(session, "_pendingAt", DateTimeOffset.Now);

        Deliver(session, RemoteMirrorClient.MirrorDelivery.Delta, new[] { BoundaryRow, SummaryRow });
        Dispatcher.UIThread.RunJobs();

        Assert.Same(pending, GetField<ChatTurn?>(session, "_pending"));
        Assert.Equal(Merged, session.History[^1].Compaction);
    }
}
