using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Orbweaver.Tests;

// CB-356 on a cloud session. The panel reads the cloud's events through the
// same ChatTranscript.Map, so a page that holds both rows pairs inside Map. The
// stream maps one event per call, so the summary arrives on its own after its
// boundary and has to fold into the boundary's row; and every later history
// read reconciles by uuid, so a re-read of the pair or of either half must land
// on that one row rather than adding or undoing anything.
//
// **Not captured, so assumed:** what a real cloud session's events hold for a
// compaction. docs/claude-cloud-findings.md records a `system/compact_boundary`
// event and "a replayed user row"; whether that row carries isCompactSummary
// was never captured, and tools/claude-cloud-probe is not for an agent to run.
// These tests use the local transcript's row shapes (Captures/cb356). If the
// cloud's summary row lacks the flag it stays a user bubble, which is the
// negative control's behaviour by design rather than a guess.
public partial class ClaudeCloudEventsTests
{
    private static readonly string[] Compacted = Cb356Captures.Lines("auto-adjacent");

    private static string BoundaryRow => Compacted.Single(l => l.Contains("\"compact_boundary\""));

    private static string SummaryRow => Compacted.Single(l => l.Contains("\"isCompactSummary\":true"));

    private static readonly CompactionNote Merged = new("auto", 245582, 20628, 12751,
        CompactionNote.Strip(System.Text.Json.JsonDocument.Parse(SummaryRow).RootElement
            .GetProperty("message").GetProperty("content").GetString()!), HasBoundary: true);

    [Fact]
    public async Task AHistoryPageHoldingTheCompactionShowsOneRow()
    {
        var api = new FakeApi(_ => new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""),
            Envelope(new[] { UserRow("u1", "hello"), BoundaryRow, SummaryRow, AssistantRow("a1", "after") })));

        var chat = Build(api);
        Assert.True(await chat.LoadAsync(CancellationToken.None));

        Assert.Equal(new[] { ChatRole.User, ChatRole.System, ChatRole.Assistant }, chat.History.Select(t => t.Role));
        Assert.Equal(Merged, chat.History[1].Compaction);
        Assert.Equal(Merged.Line, chat.History[1].Text);
    }

    [Fact]
    public void TheSummaryRowIsNotAMessageSomebodyTyped()
    {
        Assert.False(CloudStreamRows.IsTypedMessage(SummaryRow));
        Assert.False(CloudStreamRows.IsTypedMessage("{\"payload\":" + SummaryRow + "}"));

        // The control: the same row without the flag is typed, so the flag is
        // what decides it.
        Assert.True(CloudStreamRows.IsTypedMessage(SummaryRow.Replace("\"isCompactSummary\":true", "\"isCompactSummary\":false")));
    }

    [Fact]
    public async Task OnTheStreamTheSummaryFoldsIntoItsBoundaryAndNoTurnStarts()
    {
        var (chat, _, stream, _) = Streaming();
        var added = new List<ChatTurn>();
        var updated = new List<ChatTurn>();
        chat.TurnAdded += added.Add;
        chat.TurnUpdated += updated.Add;
        chat.PanelOpened();
        var events = await stream.NextOpenAsync();

        events.TryWrite(Durable(40, "system", "compact_boundary", payload: BoundaryRow));
        await Until(() => chat.History.Count == 1);
        Assert.Equal(new CompactionNote("auto", 245582, 20628, 12751, "", true), chat.History[0].Compaction);

        events.TryWrite(Durable(41, "user", payload: SummaryRow));
        events.TryWrite(Durable(42, "assistant", payload: AssistantRow("a1", "marker")));
        await Until(() => chat.History.Any(t => t.Text == "marker"));

        Assert.Equal(2, chat.History.Count);
        Assert.Equal(Merged, chat.History[0].Compaction);
        Assert.Equal(Merged.Line, chat.History[0].Text);
        Assert.Same(chat.History[0], Assert.Single(updated));
        Assert.Equal(2, added.Count);
        Assert.False(chat.CanInterrupt);
        await Close(chat);
    }

    // Nobody listening: the re-read still merges, with no event to raise.
    [Fact]
    public async Task AReadOfThePairFillsInABoundaryWithNobodySubscribed()
    {
        var page = Array.Empty<string>();
        var api = new RoutingApi(_ => new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""), Envelope(page)));
        var stream = new FakeStream();
        var chat = Sender(api, new FakeClock(), stream: stream);
        chat.PanelOpened();
        var events = await stream.NextOpenAsync();

        events.TryWrite(Durable(40, "system", "compact_boundary", payload: BoundaryRow));
        await Until(() => chat.History.Count == 1);

        page = new[] { BoundaryRow, SummaryRow };
        Assert.True(await chat.LoadAsync(CancellationToken.None));

        Assert.Equal(Merged, Assert.Single(chat.History).Compaction);
        await Close(chat);
    }

    // A summary with no uuid still folds in; it just has no second key to file.
    [Fact]
    public async Task ASummaryWithNoUuidStillFoldsIntoItsBoundary()
    {
        var (chat, _, stream, _) = Streaming();
        chat.PanelOpened();
        var events = await stream.NextOpenAsync();
        var node = System.Text.Json.Nodes.JsonNode.Parse(SummaryRow)!.AsObject();
        node.Remove("uuid");

        events.TryWrite(Durable(40, "system", "compact_boundary", payload: BoundaryRow));
        events.TryWrite(Durable(41, "user", payload: node.ToJsonString()));
        await Until(() => chat.History.FirstOrDefault()?.Compaction?.HasSummary == true);

        Assert.Equal(Merged, Assert.Single(chat.History).Compaction);
        await Close(chat);
    }

    [Fact]
    public async Task ALaterReadOfThePairOrOfTheSummaryAloneLandsOnTheSameRow()
    {
        // Empty until the stream has had its say, so the read PanelOpened may
        // make cannot pair the rows before the stream does.
        var page = Array.Empty<string>();
        var api = new RoutingApi(_ => new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""), Envelope(page)));
        var stream = new FakeStream();
        var chat = Sender(api, new FakeClock(), stream: stream);
        chat.PanelOpened();
        var events = await stream.NextOpenAsync();

        events.TryWrite(Durable(40, "system", "compact_boundary", payload: BoundaryRow));
        events.TryWrite(Durable(41, "user", payload: SummaryRow));
        await Until(() => chat.History.FirstOrDefault()?.Compaction?.HasSummary == true);

        var raised = 0;
        chat.TurnAdded += _ => raised++;
        chat.TurnUpdated += _ => raised++;

        page = new[] { BoundaryRow, SummaryRow };
        Assert.True(await chat.LoadAsync(CancellationToken.None));
        page = new[] { SummaryRow };
        Assert.True(await chat.LoadAsync(CancellationToken.None));

        Assert.Equal(0, raised);
        Assert.Equal(Merged, Assert.Single(chat.History).Compaction);
        await Close(chat);
    }

    [Fact]
    public async Task AReadOfThePairFillsInABoundaryTheStreamLeftAlone()
    {
        var page = Array.Empty<string>();
        var api = new RoutingApi(_ => new CloudApiResult(CloudOutcomes.OutcomeFor(200, ""), Envelope(page)));
        var stream = new FakeStream();
        var chat = Sender(api, new FakeClock(), stream: stream);
        chat.PanelOpened();
        var events = await stream.NextOpenAsync();

        events.TryWrite(Durable(40, "system", "compact_boundary", payload: BoundaryRow));
        await Until(() => chat.History.Count == 1);

        var updated = new List<ChatTurn>();
        chat.TurnUpdated += updated.Add;

        page = new[] { BoundaryRow, SummaryRow };
        Assert.True(await chat.LoadAsync(CancellationToken.None));

        Assert.Equal(Merged, Assert.Single(chat.History).Compaction);
        Assert.Same(chat.History[0], Assert.Single(updated));
        await Close(chat);
    }
}
