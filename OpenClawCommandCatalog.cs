using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Orbweaver
{
    // One row of the gateway's commands.list (CB-306): the primary text alias and
    // whatever else the gateway also accepts for it (`/t` for `/think`).
    internal sealed record OpenClawCommand(
        string Name, IReadOnlyList<string> Aliases, string Description, bool AcceptsArgs);

    // What commands.list says, and which of it this device may run.
    //
    // OpenClaw is the one channel where a slash command needs no machinery of
    // ours: chat.send hands text beginning "/" to the gateway's own command
    // interpreter, which runs it (a context-free command answers with a card
    // rather than a model turn). So this is only the catalogue — autocomplete
    // from the gateway's own list, and a refusal in front of the two cases that
    // would otherwise go out as a message and fail or do something odd.
    //
    // Measured 2026-11-03 against openclaw 2026.10.10, from the gateway host's
    // own CLI because the Mac probe cannot reach the gateway (Local Network
    // consent on dotnet): 105 rows, scope operator.read, each
    // {name, nativeName, textAliases[], description, category, source, scope,
    // acceptsArgs, args?}. `scope` there is "both" or "text" — where the command
    // is available, not an operator scope — so the permission a command needs
    // is not in the list at all. It is in the gateway's method table, and the
    // only one this device is known to lack is sessions.reset's operator.admin,
    // which is what /reset runs. See docs/openclaw-findings.md.
    //
    // Pure and windowless: parse, filter and decide take values and return
    // values.
    internal static class OpenClawCommandCatalog
    {
        internal const string ListMethod = "commands.list";
        internal const string AdminScope = "operator.admin";

        // The permission a command needs beyond chat.send's own, by text alias.
        // A table of what was measured, not a guess at the other 100: a command
        // missing from it is offered, and the gateway is still the judge of it.
        private static readonly IReadOnlyDictionary<string, string> RequiredScopes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["/reset"] = AdminScope,
            };

        // Accepts {commands:[...]} as the gateway answers, or a bare array. A row
        // with no text alias cannot be typed, so it is skipped.
        internal static IReadOnlyList<OpenClawCommand> Parse(JsonElement result)
        {
            var commands = new List<OpenClawCommand>();

            var array = result.ValueKind == JsonValueKind.Array ? result
                : result.ValueKind == JsonValueKind.Object
                  && result.TryGetProperty("commands", out var inner)
                  && inner.ValueKind == JsonValueKind.Array
                    ? inner
                    : default;

            if (array.ValueKind != JsonValueKind.Array) return commands;

            foreach (var row in array.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object
                    || !row.TryGetProperty("textAliases", out var aliasList)
                    || aliasList.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var aliases = aliasList.EnumerateArray()
                    .Where(a => a.ValueKind == JsonValueKind.String)
                    .Select(a => a.GetString()!)
                    .Where(a => a.Length > 1 && a[0] == '/')
                    .ToList();

                if (aliases.Count == 0) continue;

                commands.Add(new OpenClawCommand(
                    aliases[0],
                    aliases,
                    OpenClawRunState.Text(row, "description") ?? "",
                    OpenClawRunState.Flag(row, "acceptsArgs") == true));
            }

            return commands;
        }

        // The permission the command needs, or null when it needs nothing extra.
        internal static string? ScopeFor(OpenClawCommand command) =>
            command.Aliases.Select(a => RequiredScopes.GetValueOrDefault(a)).FirstOrDefault(s => s is not null);

        private static bool Holds(string? required, IReadOnlyCollection<string> scopes) =>
            required is null || scopes.Contains(required);

        // What autocomplete offers: every command this device may run, once, under
        // its primary alias, in the gateway's order.
        internal static IReadOnlyList<SlashCommand> Offered(
            IEnumerable<OpenClawCommand> commands, IReadOnlyCollection<string> scopes) =>
            commands
                .Where(c => Holds(ScopeFor(c), scopes))
                .Select(c => new SlashCommand(c.Name, c.Description, SlashRouteKind.PassThrough))
                .ToList();

        internal static string UnlistedReason =>
            "the gateway doesn't list it among its commands";

        internal static string ScopeReason(string scope) =>
            $"it needs the {scope} permission, which this device wasn't paired with";

        // Null to send the line as typed, or the reason it must not be.
        //
        // With a catalogue, a command the gateway does not list is refused rather
        // than sent: it would reach the model as prose, or fail at the gateway
        // with nothing explaining why. Without one — a gateway that does not
        // advertise commands.list, or has not answered yet — nothing is known to
        // be missing, so the line goes as it always did and the gateway is the
        // judge; only the one permission that is known is still checked.
        internal static string? RefusalFor(
            string name, IReadOnlyList<OpenClawCommand> commands, IReadOnlyCollection<string> scopes)
        {
            var listed = commands.FirstOrDefault(c =>
                c.Aliases.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)));

            if (listed is null && commands.Count > 0) return UnlistedReason;

            var required = listed is not null
                ? ScopeFor(listed)
                : RequiredScopes.GetValueOrDefault(name);

            return Holds(required, scopes) ? null : ScopeReason(required!);
        }
    }
}
