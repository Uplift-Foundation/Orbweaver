using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbweaver
{
    // The direct link carries a deliberately tiny answer from the machine which
    // can read an OpenClaw workspace to one which cannot. It is not a file API:
    // the only transferable value is a previously resolved voice, bound to the
    // gateway certificate pin and the agent id which asked for it.
    internal static class OpenClawPeerIdentity
    {
        internal const int MaxAgents = 64;
        internal const int MaxAgentId = 96;
        internal const int MaxVoice = 256;

        // Mirrors OpenClawWorkspaceIdentity's own bound: a rate this route
        // carries was already validated there before it ever reached a Row,
        // but a peer is a message from another process, not from that
        // parser, so the bound is enforced again on the way in rather than
        // trusted from the wire.
        internal const double MinRate = 0.5;
        internal const double MaxRate = 2.0;

        internal sealed record Request(
            [property: JsonPropertyName("gatewayPin")] string GatewayPin,
            [property: JsonPropertyName("agentIds")] IReadOnlyList<string> AgentIds);
        internal sealed record Row(
            [property: JsonPropertyName("agentId")] string AgentId,
            [property: JsonPropertyName("voice")] string Voice,
            [property: JsonPropertyName("rate")] double? Rate = null);
        internal sealed record Response(
            [property: JsonPropertyName("gatewayPin")] string GatewayPin,
            [property: JsonPropertyName("voices")] IReadOnlyList<Row> Voices);

        internal static bool ValidAgentId(string? value) => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxAgentId && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

        internal static bool ValidPin(string? value) => !string.IsNullOrWhiteSpace(value)
            && value.Length == 64 && value.All(Uri.IsHexDigit);

        internal static bool ValidVoice(string? value) => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxVoice && value.All(c => !char.IsControl(c));

        internal static bool ValidRate(double? value) =>
            value is null || (value is { } rate && rate >= MinRate && rate <= MaxRate);

        internal static Request? RequestFrom(JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object
                || !OnlyProperties(body, "gatewayPin", "agentIds")
                || !body.TryGetProperty("gatewayPin", out var pin)
                || !body.TryGetProperty("agentIds", out var ids)
                || pin.ValueKind != JsonValueKind.String || ids.ValueKind != JsonValueKind.Array)
                return null;

            var requested = ids.EnumerateArray().Select(id => id.ValueKind == JsonValueKind.String ? id.SafeString() : null).ToList();
            if (!ValidPin(pin.SafeString()) || requested.Count > MaxAgents
                || requested.Any(id => !ValidAgentId(id)) || requested.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requested.Count)
                return null;

            return new Request(pin.SafeString()!, requested!);
        }

        internal static Response? ResponseFrom(JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object
                || !OnlyProperties(body, "gatewayPin", "voices")
                || !body.TryGetProperty("gatewayPin", out var pin)
                || !body.TryGetProperty("voices", out var voices)
                || pin.ValueKind != JsonValueKind.String || voices.ValueKind != JsonValueKind.Array || !ValidPin(pin.SafeString()))
                return null;

            var rows = new List<Row>();
            foreach (var row in voices.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object
                    || !OnlyProperties(row, "agentId", "voice", "rate")
                    || !row.TryGetProperty("agentId", out var id)
                    || !row.TryGetProperty("voice", out var voice)
                    || id.ValueKind != JsonValueKind.String || voice.ValueKind != JsonValueKind.String
                    || !ValidAgentId(id.SafeString()) || !ValidVoice(voice.SafeString())) return null;

                double? rate = null;
                if (row.TryGetProperty("rate", out var rateElement))
                {
                    if (rateElement.ValueKind != JsonValueKind.Number
                        || !rateElement.TryGetDouble(out var parsedRate) || !ValidRate(parsedRate))
                        return null;
                    rate = parsedRate;
                }

                rows.Add(new Row(id.SafeString()!, voice.SafeString()!, rate));
            }

            return rows.Count > MaxAgents || rows.Select(r => r.AgentId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Count
                ? null : new Response(pin.SafeString()!, rows);
        }

        private static bool OnlyProperties(JsonElement value, params string[] allowed) =>
            value.EnumerateObject().All(property => property.SafeName() is { } name && allowed.Contains(name, StringComparer.Ordinal));
    }
}
