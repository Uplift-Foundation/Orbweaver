using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Orbweaver.UnitTests;

// Test-side view of OpenClawHistoryShape: the rollup-only reading of TextsToDrop
// (the set of rollups whose text is covered by their steps), kept so the tests of
// that rule and the real-page checks can state their expectations about rollups
// without the steps a kept rollup's repeat removal adds.
internal static class HistoryShapeTestHelpers
{
    internal static HashSet<int> RollupsToDrop(JsonElement messages)
    {
        var all = messages.ValueKind == JsonValueKind.Array ? messages.EnumerateArray().ToList() : new List<JsonElement>();
        return OpenClawHistoryShape.TextsToDrop(messages).Where(i => OpenClawHistoryShape.IsRollup(all[i])).ToHashSet();
    }
}
