using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Orbweaver.UnitTests;

// Test-side views of OpenClawHistoryShape: the rollup-only reading of
// TextsToDrop (the stage-3 rule's set), and the two page-edge scans the split
// simulations use. Production does the seam's work inside PrependHistory, over
// turns, so these exist only to state the expected result in terms of messages.
internal static class HistoryShapeTestHelpers
{
    internal static HashSet<int> RollupsToDrop(JsonElement messages)
    {
        var all = messages.ValueKind == JsonValueKind.Array ? messages.EnumerateArray().ToList() : new List<JsonElement>();
        return OpenClawHistoryShape.TextsToDrop(messages).Where(i => OpenClawHistoryShape.IsRollup(all[i])).ToHashSet();
    }

    internal readonly record struct SpanStep(int Index, string Text);

    private static string Role(JsonElement m) =>
        m.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";

    private static string TextOf(JsonElement m)
    {
        if (!m.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return "";
        return string.Join("\n\n", c.EnumerateArray()
            .Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "text"
                        && b.TryGetProperty("text", out var x) && x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
            .Select(b => b.GetProperty("text").GetString()));
    }

    private static bool HasText(JsonElement m) => TextOf(m).Trim().Length > 0;

    internal static List<SpanStep> OpenTailSteps(JsonElement messages)
    {
        var steps = new List<SpanStep>();
        var i = 0;
        foreach (var m in messages.EnumerateArray())
        {
            if (Role(m) == "user" || OpenClawHistoryShape.IsRollup(m)) steps.Clear();
            else if (Role(m) == "assistant" && HasText(m)) steps.Add(new SpanStep(i, TextOf(m)));
            i++;
        }
        return steps;
    }

    internal static (List<SpanStep> LeadSteps, int RollupIndex) HeadSpan(JsonElement messages)
    {
        var lead = new List<SpanStep>();
        var i = 0;
        foreach (var m in messages.EnumerateArray())
        {
            if (Role(m) == "user") return (lead, -1);
            if (OpenClawHistoryShape.IsRollup(m)) return (lead, i);
            if (Role(m) == "assistant" && HasText(m)) lead.Add(new SpanStep(i, TextOf(m)));
            i++;
        }
        return (lead, -1);
    }
}
