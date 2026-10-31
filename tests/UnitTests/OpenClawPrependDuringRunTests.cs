using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Orbweaver.Tests;

// CB-270 stage 3b: a scroll-back can land while a run is being drawn, and it now
// leaves some of the older page's steps out as well as inserting rows. The run's end
// reconciles against the newest page by references (the page's last row, the
// run's first row), not by an index recorded earlier, so rows shifting or
// leaving above it must not change where the reconciliation lands.
[Collection("Settings")]
public class OpenClawPrependDuringRunTests : IDisposable
{
    public void Dispose() => OpenClawSessions.SetGatewayForTests(null);

    private const string FixtureKey = "agent:main:dashboard:0a1b2c3d-0000-4000-8000-000000000001";

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static List<(string Name, JsonElement Payload)> Run() =>
        OpenClawNativeRunFixture.Events.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(Json)
            .Select(e => (e.GetProperty("name").GetString()!, e.GetProperty("payload")))
            .Where(e => e.Item2.TryGetProperty("runId", out var r) && r.GetString() == "run-tools-0001")
            .Where(e => e.Item1 is "agent" or "session.tool" or "chat")
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

    private static HistoryTurn T(ChatRole role, string text, bool fromRollup = false) =>
        new(role, text, null, "", DateTime.UtcNow, null, null, FromRollup: fromRollup);

    private static void Feed(OpenClawChatSession session, IEnumerable<(string Name, JsonElement Payload)> events)
    {
        foreach (var (name, original) in events)
        {
            var payload = Json(original.GetRawText().Replace(FixtureKey, session.GatewayKey));
            OpenClawSessions.OnEvent(name, payload, DateTime.UtcNow);
            session.OnAgentEvent(name, payload);
        }
    }

    // The newest page opens cut: its head rollup holds a step that sits on an older
    // page (the page already dropped the lead step it contains). While the run
    // streams, the user scrolls back, the older page's covered trailing step is left
    // out of what is inserted, and then the run ends and reconciles. The transcript
    // must be the older page, the head rollup, and the run's history, each once.
    [Fact]
    public void AScrollBackThatFiltersAnOlderStepDuringARunStillLetsTheRunReconcile()
    {
        OpenClawSessions.SetGatewayForTests(null);
        var key = $"agent:main:dashboard:{Guid.NewGuid()}";
        var session = new OpenClawChatSession("openclaw:" + key, key, "main");
        var (step1, step3) = Steps();

        session.SetHistory(new List<HistoryTurn>
        {
            new(ChatRole.Assistant, "pre1\n\npre2\n\nmore", null, "", DateTime.UtcNow, null, null, FromRollup: true, SeamRollup: true),
            T(ChatRole.User, "go")
        });

        var events = Run();
        Feed(session, events.Take(14));

        // Mid-run: scroll back to an older page ending in the run's first step.
        session.PrependHistory(new List<HistoryTurn>
        {
            T(ChatRole.User, "q"),
            new(ChatRole.Assistant, "pre1", null, "", DateTime.UtcNow, null, null, OpenTail: true)
        });

        Assert.Equal(new[] { "q", "pre1\n\npre2\n\nmore" }, session.History.Take(2).Select(t => t.Text).ToArray());

        Feed(session, events.Skip(14));

        session.ApplyFetchedTail(new List<HistoryTurn>
        {
            new(ChatRole.Assistant, "pre1\n\npre2\n\nmore", null, "", DateTime.UtcNow, null, null, FromRollup: true),
            T(ChatRole.User, "go"),
            T(ChatRole.Assistant, step1),
            T(ChatRole.System, ToolLine()),
            T(ChatRole.Assistant, step3)
        });

        Assert.Equal(new[] { "q", "pre1\n\npre2\n\nmore", "go", step1, ToolLine(), step3 },
            session.History.Select(t => t.Text).ToArray());
    }

    // A scroll-back that only inserts (nothing to decide at the seam) while the run
    // streams shifts every index; the reconciliation still lands on the same row.
    [Fact]
    public void AnInsertOnlyScrollBackDuringARunLeavesTheReconciliationWhereItWas()
    {
        OpenClawSessions.SetGatewayForTests(null);
        var key = $"agent:main:dashboard:{Guid.NewGuid()}";
        var session = new OpenClawChatSession("openclaw:" + key, key, "main");
        var (step1, step3) = Steps();

        session.SetHistory(new List<HistoryTurn> { T(ChatRole.User, "go") });
        var events = Run();
        Feed(session, events.Take(14));

        session.PrependHistory(new List<HistoryTurn> { T(ChatRole.User, "earlier"), T(ChatRole.Assistant, "reply") });

        Feed(session, events.Skip(14));
        session.ApplyFetchedTail(new List<HistoryTurn>
        {
            T(ChatRole.User, "go"), T(ChatRole.Assistant, step1), T(ChatRole.System, ToolLine()), T(ChatRole.Assistant, step3)
        });

        Assert.Equal(new[] { "earlier", "reply", "go", step1, ToolLine(), step3 },
            session.History.Select(t => t.Text).ToArray());
    }
}
