using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Orbweaver
{
    // What a cloud panel offers when the box starts with "/" (CB-306).
    //
    // The list comes from the session itself: the CLI in the cloud worker
    // publishes `slash_commands` and `terminal_slash_commands` on its
    // `system/init` row (measured: 101 and 4 on 2026-11-03), the second being
    // the commands only a terminal can run, which a remote UI is meant to hide.
    // Each name is then asked of SlashRouting.Resolve, because the CLI's own
    // list says what *exists* and the routing table says what can run from
    // here — over a cloud session that is the control-request four, and
    // everything else (every skill, /compact, /color) is kept as Unavailable:
    // the panel does not offer it, and typed in full it is refused with its
    // reason rather than sent as prose.
    //
    // Pure, so the parse and the filter are unit cases with no session behind
    // them.
    internal static class CloudSlashCatalog
    {
        // The init row's two lists, names as the CLI writes them (no slash).
        // Null when the row carries no `slash_commands` array at all — which
        // says "this init told us nothing", not "the session has no commands",
        // so the caller keeps the list it already had.
        internal static (IReadOnlyList<string> Commands, IReadOnlyList<string> Terminal)? ParseInit(string? rowJson)
        {
            if (string.IsNullOrWhiteSpace(rowJson)) return null;

            try
            {
                using var doc = JsonDocument.Parse(rowJson);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                var commands = Names(root, "slash_commands");
                return commands is null ? null : (commands, Names(root, "terminal_slash_commands") ?? new List<string>());
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static List<string>? Names(JsonElement root, string property)
        {
            if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
                return null;

            return array.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!.Trim().TrimStart('/'))
                .Where(n => n.Length > 0)
                .ToList();
        }

        // The four a cloud session runs by control request. Offered whether or
        // not init lists them: the routing table, not the CLI's list, says they
        // run here, and nobody has captured an init to show they are in it.
        internal static readonly string[] ControlNames = { "rename", "model", "permissions", "think" };

        // The whole catalogue: init's names, Claude Code's built-in names and the
        // four above (so /compact is known, and refused, even before an init has
        // arrived), each with the
        // route a cloud channel gives it. Terminal-only names are Unavailable
        // unless the table has a control request for them. The panel hides
        // Unavailable ones from autocomplete and the session uses the same list
        // to know which "/word" is a command at all.
        internal static IReadOnlyList<SlashCommand> Catalogue(
            IReadOnlyList<string> commands, IReadOnlyList<string> terminal)
        {
            var hidden = new HashSet<string>(terminal, StringComparer.OrdinalIgnoreCase);

            return ControlNames
                .Concat(SlashCommandCatalog.ClaudeCodeBuiltins.Select(c => c.Name.TrimStart('/')))
                .Concat(commands)
                .Concat(terminal)
                .Select(n => "/" + n)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(n =>
                {
                    var route = SlashRouting.Resolve(SlashChannel.Cloud, n, "").Kind;
                    return new SlashCommand(n, DescriptionFor(n),
                        hidden.Contains(n.TrimStart('/')) && route != SlashRouteKind.Control
                            ? SlashRouteKind.Unavailable
                            : route);
                })
                .ToList();
        }

        private static string DescriptionFor(string name) => name.ToLowerInvariant() switch
        {
            "/rename" => "Rename this session",
            "/model" => "Change the model",
            "/permissions" => "Change the permission mode",
            "/think" => "Set the thinking budget",
            _ => "",
        };
    }
}
