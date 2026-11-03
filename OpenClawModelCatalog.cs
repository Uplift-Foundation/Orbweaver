using System.Text.Json;

namespace Orbweaver
{
    // One model the gateway can run, from models.list (CB-270 Stage 4).
    //
    // ThinkingLevels is that model's own list: the sets differ per model (one
    // advertises xhigh, which the gateway's refusal text for a bad level does not
    // list; most local models offer only off and ultra), so the picker is built
    // from this and never from an error message.
    internal sealed record OpenClawModel(
        string Id,
        string Name,
        string? Provider,
        bool Available,
        IReadOnlyList<OpenClawRunOption> ThinkingLevels,
        string? ThinkingDefault);

    internal static class OpenClawModelCatalog
    {
        // Accepts {models:[...]} as the gateway answers, or a bare array. An entry
        // with no id is skipped; everything else about an entry is optional.
        internal static IReadOnlyList<OpenClawModel> Parse(JsonElement result)
        {
            var models = new List<OpenClawModel>();

            var array = result.ValueKind == JsonValueKind.Array ? result
                : result.ValueKind == JsonValueKind.Object
                  && result.TryGetProperty("models", out var inner)
                  && inner.ValueKind == JsonValueKind.Array
                    ? inner
                    : default;

            if (array.ValueKind != JsonValueKind.Array) return models;

            foreach (var entry in array.EnumerateArray())
            {
                var id = OpenClawRunState.Text(entry, "id");
                if (id is null) continue;

                models.Add(new OpenClawModel(
                    id,
                    OpenClawRunState.Text(entry, "name") ?? id,
                    OpenClawRunState.Text(entry, "provider"),
                    // Only an explicit true is offered. A model the gateway has not
                    // vouched for is one a patch would be refused for.
                    OpenClawRunState.Flag(entry, "available") == true,
                    OpenClawRunState.LevelsOf(entry, "thinkingLevels"),
                    OpenClawRunState.Text(entry, "thinkingDefault")));
            }

            return models;
        }

        // What a person may pick, in the gateway's order.
        internal static IReadOnlyList<OpenClawModel> Offered(IEnumerable<OpenClawModel> models) =>
            models.Where(m => m.Available).ToList();
    }
}
