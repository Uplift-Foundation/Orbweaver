using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbweaver
{
    // CB-434: the codec for `past-sessions-get` / `past-sessions`. Pure, and
    // as strict as PeerLaunchWire: everything here came from another process,
    // and the rows are drawn in a window, so a parser answers null for any
    // shape that is not exactly the documented one and bounds what it keeps.
    internal static class PeerResumeWire
    {
        internal sealed record ListRequest(
            [property: JsonPropertyName("cli")] string Cli,
            [property: JsonPropertyName("account")] string Account,
            [property: JsonPropertyName("older")] bool Older);

        private sealed record ListBody(
            [property: JsonPropertyName("ok")] bool Ok,
            [property: JsonPropertyName("code")] string? Code,
            [property: JsonPropertyName("rows")] IReadOnlyList<PastSessionWire>? Rows,
            [property: JsonPropertyName("truncated")] bool Truncated);

        internal static JsonElement EncodeListRequest(ListRequest request) => PeerProtocol.BodyOf(request);

        internal static ListRequest? ParseListRequest(JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object) return null;

            ListRequest? request;
            try { request = body.Deserialize<ListRequest>(PeerProtocol.Json); }
            catch (JsonException) { return null; }

            return request is not null && Sane(request.Cli, 32, true) && Sane(request.Account, 256, true)
                ? request
                : null;
        }

        internal static JsonElement EncodeList(PastSessionServe.Answer answer) =>
            PeerProtocol.BodyOf(answer.Ok
                ? new ListBody(true, answer.Code, answer.Rows, answer.Truncated)
                // A refusal carries its code and nothing else.
                : new ListBody(false, answer.Code, null, false));

        internal static PastSessionServe.Answer? ParseList(JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object) return null;

            ListBody? parsed;
            try { parsed = body.Deserialize<ListBody>(PeerProtocol.Json); }
            catch (JsonException) { return null; }

            if (!Sane(parsed!.Code, 64, true)) return null;
            if (!parsed.Ok) return new PastSessionServe.Answer(parsed.Code!, null, false);

            var rows = parsed.Rows ?? [];
            if (rows.Count > PastSessionServe.MaxRows || rows.Any(r => !RowOk(r))) return null;

            return new PastSessionServe.Answer(parsed.Code!, rows, parsed.Truncated);
        }

        // The id is what a resume would put on a command line over there, so
        // it is held to the same shape PastSessions.IsSafeId demands here.
        private static bool RowOk(PastSessionWire? r) =>
            r is not null
            && Array.IndexOf(LaunchGate.Clis, r.Cli) >= 0
            && PastSessions.IsSafeId(r.Id) && r.Id.Length <= 256
            && Sane(r.Cwd, PeerLaunchWire.MaxText, false)
            && Sane(r.Title, PastSessions.TitleLength * 2, false) && r.Title is not null
            && r.LastActivity >= 0;

        private static bool Sane(string? value, int max, bool required) =>
            value is null
                ? !required
                : (!required || value.Length > 0) && value.Length <= max && value.All(c => !char.IsControl(c));
    }
}
