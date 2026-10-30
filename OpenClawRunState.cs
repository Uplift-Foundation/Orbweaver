using System.Text.Json;

namespace Orbweaver
{
    // One entry in a picker: the id that goes on the wire and the label a person
    // reads. They differ on purpose in the gateway's own data (qwen3.6-plus
    // labels the id "low" as "on"), so the picker sends the id and shows the
    // label, and never one for the other.
    internal sealed record OpenClawRunOption(string Id, string Label);

    // What a native session is running on, read off its sessions.list row
    // (CB-270 Stage 4): the model, the thinking level and fast mode, each with
    // what applies when it is unset.
    //
    // Null is meaningful on these fields and is kept: a native row reads
    // thinkingLevel null beside thinkingDefault "adaptive", and fastMode null
    // beside effectiveFastMode false. Null means "nobody chose, the default
    // applies", which is a different fact from a chosen value that happens to
    // equal the default, so it is never replaced by it here.
    //
    // Tolerant in the way OrbweaverSettings.Text is: a field of the wrong type
    // costs that field and nothing else, because the gateway's rows have
    // surprised this app before and one odd value must not hide the picker.
    internal sealed record OpenClawRunState(
        string? Model,
        string? ModelProvider,
        string? ThinkingLevel,
        string? ThinkingDefault,
        IReadOnlyList<OpenClawRunOption> ThinkingLevels,
        bool? FastMode,
        bool EffectiveFastMode,
        bool SelectionLocked)
    {
        // The level in force, by id: the one chosen, else the model's default.
        internal string? EffectiveThinking => ThinkingLevel ?? ThinkingDefault;

        // That level as a person reads it. An id the model's own list does not
        // carry is shown as itself rather than hidden.
        internal string? EffectiveThinkingLabel
        {
            get
            {
                var id = EffectiveThinking;
                if (id is null) return null;

                return ThinkingLevels.FirstOrDefault(o => o.Id == id)?.Label ?? id;
            }
        }

        // Null when the row names no model: there is nothing to show a picker for.
        internal static OpenClawRunState? From(JsonElement row)
        {
            var model = Text(row, "model");
            if (model is null) return null;

            return new OpenClawRunState(
                model,
                Text(row, "modelProvider"),
                Text(row, "thinkingLevel"),
                Text(row, "thinkingDefault"),
                LevelsOf(row, "thinkingLevels", "thinkingOptions"),
                Flag(row, "fastMode"),
                Flag(row, "effectiveFastMode") == true,
                Flag(row, "runtimeSelectionLocked") == true);
        }

        // The gateway's own list wins: objects with an id and a label. A list of
        // bare strings is the older shape, where the id is the label. Entries that
        // are neither are skipped, so one bad entry does not take the list.
        internal static IReadOnlyList<OpenClawRunOption> LevelsOf(
            JsonElement owner, string objectsName, string? stringsName = null)
        {
            var fromObjects = Options(owner, objectsName);
            if (fromObjects.Count > 0 || stringsName is null) return fromObjects;

            return Options(owner, stringsName);
        }

        private static List<OpenClawRunOption> Options(JsonElement owner, string name)
        {
            var list = new List<OpenClawRunOption>();

            if (owner.ValueKind != JsonValueKind.Object
                || !owner.TryGetProperty(name, out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var id = item.SafeString();
                    if (!string.IsNullOrWhiteSpace(id)) list.Add(new OpenClawRunOption(id, id));
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    var id = Text(item, "id");
                    if (id is not null) list.Add(new OpenClawRunOption(id, Text(item, "label") ?? id));
                }
            }

            return list;
        }

        internal static string? Text(JsonElement owner, string name)
        {
            if (owner.ValueKind != JsonValueKind.Object
                || !owner.TryGetProperty(name, out var v)
                || v.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var text = v.SafeString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        internal static bool? Flag(JsonElement owner, string name) =>
            owner.ValueKind == JsonValueKind.Object
            && owner.TryGetProperty(name, out var v)
            && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean()
                : null;
    }
}
