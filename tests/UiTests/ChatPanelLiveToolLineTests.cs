using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orbweaver.Tests;
using Xunit;

namespace Orbweaver.UiTests;

// CB-270 Stage 3: a real panel bound to a real OpenClawChatSession while the
// captured native run streams into it. The unit cases check the turns; what only
// this checks is that they reach the screen in the order a person reads them,
// that a failed tool re-inks a row already drawn, and that opening and closing
// the panel is what holds and releases the message subscription.
[Collection("Settings")]
public class ChatPanelLiveToolLineTests : IDisposable
{
    private readonly List<string> _toClean = new();

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
        OpenClawSessions.SetGatewayForTests(null);
    }

    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    private static void FlushRender()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static Avalonia.Controls.Controls Rows(ChatPanel panel) =>
        panel.FindControl<ItemsControl>("Turns")!.ItemsPanelRoot!.Children;

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static OpenClawChatSession Session()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var chat = new OpenClawChatSession(
            $"openclaw:agent:main:dashboard:{id}", $"agent:main:dashboard:{id}", "main");
        chat.HasMore = false;
        _ = id;
        return chat;
    }

    private static IEnumerable<(string Name, JsonElement Payload)> Run(string runId) =>
        OpenClawNativeRunFixture.Events.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(Json)
            .Select(e => (e.GetProperty("name").GetString()!, e.GetProperty("payload")))
            .Where(e => e.Item2.TryGetProperty("runId", out var r) && r.GetString() == runId
                        && e.Item1 is "agent" or "session.tool");

    private static string BodyText(Control row) =>
        string.Join("", row.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

    private static TextBlock BodyBlock(Control row) =>
        row.GetVisualDescendants().OfType<TextBlock>().First(t => !string.IsNullOrEmpty(t.Text));

    private static Color InkOf(Control row) => ((ISolidColorBrush)BodyBlock(row).Foreground!).Color;

    private ChatPanel Open(OpenClawChatSession session)
    {
        _toClean.Add(session.SessionId);
        ChatPanel.OpenFor(NewOrb(), session);
        FlushRender();
        return ChatPanelTestAccess.Instance!;
    }

    // --- the order on screen ---

    [AvaloniaFact]
    public void TheRunReadsStepToolStepAndTheLastStepGrowsInPlace()
    {
        OpenClawSessions.SetGatewayForTests(null);
        var session = Session();
        var panel = Open(session);

        var events = Run("run-tools-0001").ToList();
        var toolAt = events.FindIndex(e => e.Name == "session.tool");

        foreach (var (name, payload) in events.Take(toolAt)) session.OnAgentEvent(name, payload);
        FlushRender();
        Assert.Single(Rows(panel));

        foreach (var (name, payload) in events.Skip(toolAt).Take(4)) session.OnAgentEvent(name, payload);
        FlushRender();

        // Step, then the tool line, then the start of the next step.
        var rows = Rows(panel);
        Assert.Equal(3, rows.Count);
        Assert.Contains("Hey Boss! I'm Nova", BodyText(rows[0]));
        Assert.Contains("· mcp__openclaw__exec", BodyText(rows[1]));
        var partial = BodyText(rows[2]);

        foreach (var (name, payload) in events.Skip(toolAt + 4)) session.OnAgentEvent(name, payload);
        FlushRender();

        // The same three rows, the last one longer: updated in place.
        Assert.Equal(3, Rows(panel).Count);
        Assert.Contains("probe ok, 18 files", BodyText(Rows(panel)[2]));
        Assert.True(BodyText(Rows(panel)[2]).Length > partial.Length);
    }

    [AvaloniaFact]
    public void AToolLineIsCentredInTheSystemInk()
    {
        OpenClawSessions.SetGatewayForTests(null);
        var session = Session();
        var panel = Open(session);

        foreach (var (name, payload) in Run("run-tools-0001")) session.OnAgentEvent(name, payload);
        FlushRender();

        var tool = Rows(panel)[1];
        Assert.Equal(Color.Parse("#8CFFFFFF"), InkOf(tool));
        Assert.Null(ToolTip.GetTip(tool.GetVisualDescendants().OfType<ContentControl>().First()));
    }

    // A failed tool re-inks a row that is already on screen, and says why on
    // hover. Nothing else about the row moves.
    [AvaloniaFact]
    public void AFailedToolTurnsItsRowRedAndSaysSo()
    {
        OpenClawSessions.SetGatewayForTests(null);
        var session = Session();
        var panel = Open(session);

        session.OnAgentEvent("session.tool", Json(
            """{"data":{"phase":"start","name":"Bash","toolCallId":"t1","args":{"command":"false"}}}"""));
        FlushRender();
        var before = BodyText(Rows(panel)[0]);

        session.OnAgentEvent("session.tool", Json(
            """{"data":{"phase":"result","name":"Bash","toolCallId":"t1","isError":true}}"""));
        FlushRender();

        var row = Rows(panel)[0];
        Assert.Single(Rows(panel));
        Assert.Equal(before, BodyText(row));
        Assert.Equal(Color.Parse("#E0D93B3B"), InkOf(row));
        Assert.Equal("The tool reported an error",
            ToolTip.GetTip(row.GetVisualDescendants().OfType<ContentControl>().First()));
    }

    // CB-270: at the end of a run the live rows are replaced by what history
    // says, and the panel has to drop the same rows or it would show both. A
    // panel joined partway through the run holds a truncated first bubble; after
    // the reconciliation its rows are the model's, one for one.
    [AvaloniaFact]
    public void ARunsLiveRowsAreReplacedByHistoryAndThePanelFollows()
    {
        OpenClawSessions.SetGatewayForTests(null);
        var session = Session();
        var panel = Open(session);
        session.SetHistory(new[] { new HistoryTurn(ChatRole.User, "go", null, "", DateTimeOffset.UtcNow, null, null) });
        FlushRender();

        var events = Run("run-tools-0001").ToList();
        foreach (var (name, payload) in events.Skip(8)) session.OnAgentEvent(name, payload);
        FlushRender();
        var live = Rows(panel).Count;
        Assert.True(live >= 2);

        var final = events.Where(e => e.Name == "agent")
            .Select(e => e.Payload.GetProperty("data"))
            .Last(d => d.TryGetProperty("text", out var t) && t.GetString()!.Length > 100)
            .GetProperty("text").GetString()!.Split("\n\n");
        var toolLine = ChatTranscript.ToolSummary("mcp__openclaw__exec",
            Json("""{"command":"ls /home/op/workspace | wc -l","title":"Count workspace files"}"""));

        session.ApplyFetchedTail(new[]
        {
            new HistoryTurn(ChatRole.User, "go", null, "", DateTimeOffset.UtcNow, null, null),
            new HistoryTurn(ChatRole.Assistant, final[0], null, "", DateTimeOffset.UtcNow, null, null),
            new HistoryTurn(ChatRole.System, toolLine, null, "", DateTimeOffset.UtcNow, null, null),
            new HistoryTurn(ChatRole.Assistant, final[1], null, "", DateTimeOffset.UtcNow, null, null),
        });
        FlushRender();

        Assert.Equal(4, session.History.Count);
        Assert.Equal(4, Rows(panel).Count);
        Assert.Contains(final[0], BodyText(Rows(panel)[1]));
        Assert.Contains(toolLine, BodyText(Rows(panel)[2]));
        Assert.Contains(final[1], BodyText(Rows(panel)[3]));
    }

    // CB-270: a room merges its members' transcripts and rebuilds when one
    // changes. A reconciliation that only removes (the page holds less than the
    // live drawing did) raises no TurnAdded, so the room has to hear the removal
    // itself or keep showing a row that is gone.
    [AvaloniaFact]
    public void ARoomRebuildsWhenAMembersLiveRowsAreRemoved()
    {
        OpenClawSessions.SetGatewayForTests(null);
        var member = new OpenClawChatSession(
            "openclaw:agent:quill:discord:channel:900", "agent:quill:discord:channel:900", "Quill");
        member.HasMore = false;
        member.SetHistory(new[] { new HistoryTurn(ChatRole.User, "go", null, "", DateTimeOffset.UtcNow, null, null, true) });

        var room = new OpenClawRoomChatSession("openclaw:room:discord:900:" + Guid.NewGuid().ToString("N")[..8], "#lobby");
        room.SetMembers(new[] { (member, "Quill", "#7f7") }.ToList());
        room.Rebuild();

        member.OnAgentEvent("agent", Json("""{"runId":"r","stream":"assistant","data":{"text":"a partial live reply"}}"""));
        FlushRender();
        Assert.Contains(room.History, t => t.Text.Contains("a partial live reply"));

        // The end is settled first, so the rebuild its own change queues has run and
        // the removal below is the only thing left that could prompt another.
        member.OnAgentEvent("chat", Json("""{"state":"final"}"""));
        FlushRender();
        member.ApplyFetchedTail(new[] { new HistoryTurn(ChatRole.User, "go", null, "", DateTimeOffset.UtcNow, null, null, true) });
        FlushRender();

        Assert.DoesNotContain(room.History, t => t.Text.Contains("a partial live reply"));
    }

    // --- the subscription, from the panel's own open and close ---

    private static async Task<FakeGatewaySocket> ConnectedAsync(string? refuse = null)
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
                : request.Method == refuse
                    ? FakeGatewaySocket.Error(request.Id, "refused", "the gateway said no", null)
                    : FakeGatewaySocket.Ok(request.Id, new { messages = Array.Empty<object>() });

        var gateway = new OpenClawGateway("gw.local", 4443, "gw-token",
            (_, _, _, _) => Task.FromResult(
                new OpenClawSocket.Connection(socket, Stream.Null, "fp-abc")),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        await gateway.ConnectAsync(null, CancellationToken.None);
        OpenClawSessions.SetGatewayForTests(gateway);
        return socket;
    }

    private static async Task<List<string>> MethodsAsync(FakeGatewaySocket socket, string method, int count)
    {
        for (var i = 0; i < 200
             && socket.Requests.Count(r => r.Method == method) < count; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        return socket.Requests.Where(r => r.Method == method)
            .Select(r => r.Params.GetProperty("key").GetString()!).ToList();
    }

    [AvaloniaFact]
    public async Task OpeningAPanelSubscribesAndClosingItUnsubscribes()
    {
        var socket = await ConnectedAsync();
        var session = Session();

        Open(session);
        Assert.Equal(new[] { session.GatewayKey },
            await MethodsAsync(socket, "sessions.messages.subscribe", 1));
        Assert.Empty(socket.Requests.Where(r => r.Method == "sessions.messages.unsubscribe"));

        ChatPanel.HideFor(session.SessionId);
        FlushRender();

        Assert.Equal(new[] { session.GatewayKey },
            await MethodsAsync(socket, "sessions.messages.unsubscribe", 1));
    }

    // A subscribe the gateway refuses is said once, in the conversation that
    // asked, and the panel carries on without live updates.
    [AvaloniaFact]
    public async Task ARefusedSubscribeIsNotedInThePanel()
    {
        var socket = await ConnectedAsync(refuse: "sessions.messages.subscribe");
        var session = Session();

        var panel = Open(session);
        await MethodsAsync(socket, "sessions.messages.subscribe", 1);
        for (var i = 0; i < 200 && session.History.Count == 0; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        FlushRender();

        var row = Assert.Single(Rows(panel));
        Assert.Contains("Live updates aren't available for this conversation", BodyText(row));
    }
}
