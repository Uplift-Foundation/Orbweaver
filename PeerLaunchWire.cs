using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbweaver
{
    // The codec for the four CB-357 peer messages: pure, no link and no
    // settings behind it, so every malformed shape can be fed to it directly.
    //
    // Everything arriving here is a message from another process, and for
    // `launch-info` from a machine the sender will draw in a window, so each
    // parser answers null for anything that is not exactly the documented
    // shape and bounds the lists and strings it accepts. A null is "treat as
    // no answer", never a half-read body.
    internal static class PeerLaunchWire
    {
        internal const int MaxClis = 8;
        internal const int MaxAccounts = 64;
        internal const int MaxRecents = 64;
        internal const int MaxText = 4096;

        // The body of `launch`. Names are the wire's, snake_case like the
        // phone's frames. `Persona`, `Model`, `ParentSessionId` and
        // `FirstMessage` are in the shape because the receiver's gate checks
        // them; the desktop window sends them empty.
        internal sealed record LaunchRequest(
            [property: JsonPropertyName("cwd")] string Cwd,
            [property: JsonPropertyName("cli")] string Cli,
            [property: JsonPropertyName("persona")] string Persona,
            [property: JsonPropertyName("model")] string Model,
            [property: JsonPropertyName("account")] string Account,
            [property: JsonPropertyName("parent_session_id")] string ParentSessionId,
            [property: JsonPropertyName("first_message")] string FirstMessage,
            [property: JsonPropertyName("nonce")] string Nonce,
            [property: JsonPropertyName("expires_at")] long ExpiresAt,
            [property: JsonPropertyName("sig")] string Sig,
            [property: JsonPropertyName("cert")] string Cert,
            // CB-397: unsigned; only says this desk can show the trust question.
            [property: JsonPropertyName("can_trust"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool CanTrust = false,
            // CB-434: signed when non-empty (LaunchGate.Body). Omitted for a new
            // chat, so a launch body is byte-identical to what an older desk reads.
            [property: JsonPropertyName("resume_session_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResumeSessionId = null);

        private sealed record ResultBody(
            [property: JsonPropertyName("ok")] bool Ok,
            [property: JsonPropertyName("code")] string? Code,
            [property: JsonPropertyName("session_id")] string? SessionId,
            [property: JsonPropertyName("why")] string? Why,
            [property: JsonPropertyName("trust"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PeerLaunchTrust? Trust = null);

        // CB-397: the body of `launch-trust`. The folder, CLI and account are
        // not here; see PeerTrustAnswer.
        internal sealed record TrustRequest(
            [property: JsonPropertyName("token")] string Token,
            [property: JsonPropertyName("answer")] string Answer,
            [property: JsonPropertyName("nonce")] string Nonce,
            [property: JsonPropertyName("expires_at")] long ExpiresAt,
            [property: JsonPropertyName("sig")] string Sig,
            [property: JsonPropertyName("cert")] string Cert);

        internal static JsonElement EncodeTrust(TrustRequest request) => PeerProtocol.BodyOf(request);

        internal static TrustRequest? ParseTrust(JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object) return null;

            TrustRequest? request;
            try { request = body.Deserialize<TrustRequest>(PeerProtocol.Json); }
            catch (JsonException) { return null; }

            var whole = request is not null
                && Sane(request.Token, 64, true) && Sane(request.Nonce, 256, true)
                && Sane(request.Sig, MaxText, true) && Sane(request.Cert, 32 * 1024, true)
                && request.Answer is PeerTrustAnswer.Trust or PeerTrustAnswer.Cancel
                && request.ExpiresAt > 0;

            return whole ? request : null;
        }

        // --- launch-info ---------------------------------------------------------

        internal static JsonElement EncodeInfo(PeerLaunchInfo info) =>
            info.Allowed
                ? PeerProtocol.BodyOf(info)
                // Switch off: nothing but the OS goes out, whatever the caller
                // built. An anonymous object rather than the record, because a
                // record's non-nullable `terminal` would still be written.
                : PeerProtocol.BodyOf(new { allowed = false, os = info.Os });

        internal static PeerLaunchInfo? ParseInfo(JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object) return null;

            PeerLaunchInfo? info;
            try { info = body.Deserialize<PeerLaunchInfo>(PeerProtocol.Json); }
            catch (JsonException) { return null; }

            if (info is null || !Sane(info.Os, 32, required: true)) return null;

            // A refusal discloses nothing else, and what a refusing desk
            // sends beyond that is not believed.
            if (!info.Allowed) return new PeerLaunchInfo(false, info.Os);

            if (!Sane(info.Home, MaxText, required: false)) return null;

            var clis = info.Clis ?? [];
            var accounts = info.Accounts ?? [];
            var recents = info.Recents ?? [];

            if (clis.Count > MaxClis || accounts.Count > MaxAccounts || recents.Count > MaxRecents)
                return null;

            if (clis.Any(c => c is null || !Sane(c.Cli, 32, required: true))) return null;

            if (accounts.Any(a => a is null || !Sane(a.Cli, 32, required: true)
                    || !Sane(a.Id, 256, required: true) || !Sane(a.Label, 256, required: true)))
                return null;

            if (recents.Any(r => !Sane(r, MaxText, required: true))) return null;

            return info with { Clis = clis, Accounts = accounts, Recents = recents };
        }

        // --- launch --------------------------------------------------------------

        internal static JsonElement EncodeLaunch(LaunchRequest request) => PeerProtocol.BodyOf(request);

        // Null for a body that is not a complete request. Which refusal code
        // that earns is the receiver's decision (`bad_frame`), made after its
        // setting check, so this only says whether the shape is whole.
        internal static LaunchRequest? ParseLaunch(JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object) return null;

            LaunchRequest? request;
            try { request = body.Deserialize<LaunchRequest>(PeerProtocol.Json); }
            catch (JsonException) { return null; }

            if (request is null) return null;

            // Every field must be present: a missing one is a bad frame, never a
            // default. Four of them may be empty (the desktop window sends no
            // persona, model, parent or first message) but not absent.
            var whole = request.Persona is not null && request.Model is not null
                && request.ParentSessionId is not null && request.FirstMessage is not null
                && Sane(request.Cwd, MaxText, true) && Sane(request.Cli, 32, true)
                && Sane(request.Account, 256, true) && Sane(request.Nonce, 256, true)
                && Sane(request.Sig, MaxText, true) && Sane(request.Cert, 32 * 1024, true)
                && request.FirstMessage.Length <= 16 * 1024
                && request.ExpiresAt > 0
                && (string.IsNullOrEmpty(request.ResumeSessionId) || PastSessions.IsSafeId(request.ResumeSessionId));

            return whole ? request : null;
        }

        // --- launch-result -------------------------------------------------------

        internal static JsonElement EncodeResult(PeerLaunchResult result) =>
            PeerProtocol.BodyOf(new ResultBody(result.Ok, result.Code, result.SessionId, result.Why, result.Trust));

        internal static PeerLaunchResult? ParseResult(JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object) return null;

            ResultBody? parsed;
            try { parsed = body.Deserialize<ResultBody>(PeerProtocol.Json); }
            catch (JsonException) { return null; }

            if (parsed is null || !Sane(parsed.Code, 64, required: true)
                || !Sane(parsed.SessionId, 256, required: false)
                || !Sane(parsed.Why, MaxText, required: false))
                return null;

            // A question belongs to trust_needed and to nothing else, and it
            // is believed only whole: a half-read question is no answer.
            var trust = parsed.Trust;
            if (parsed.Code == PeerLaunchCodes.TrustNeeded)
            {
                if (trust is null || !Sane(trust.Token, 64, true) || !Sane(trust.Cli, 32, true)
                    || !Sane(trust.Cwd, MaxText, true) || trust.ExpiresAt <= 0)
                    return null;
            }
            else trust = null;

            return new PeerLaunchResult(parsed.Ok, parsed.Code!, parsed.SessionId, parsed.Why, trust);
        }

        // --- list-dir ------------------------------------------------------------

        internal const int MaxDirItems = 500;

        private sealed record ListDirRequestBody([property: JsonPropertyName("path")] string? Path);

        private sealed record DirBody(
            [property: JsonPropertyName("ok")] bool Ok,
            [property: JsonPropertyName("code")] string? Code,
            [property: JsonPropertyName("path")] string? Path,
            [property: JsonPropertyName("parent")] string? Parent,
            [property: JsonPropertyName("truncated")] bool Truncated,
            [property: JsonPropertyName("items")] IReadOnlyList<PeerDirItem>? Items);

        internal static JsonElement EncodeListDirRequest(string path) =>
            PeerProtocol.BodyOf(new ListDirRequestBody(path));

        // Null unless the path is present and shaped as the phone's list_dir
        // path is (BrowseRules.PathShapeOk). Empty is the top level.
        internal static string? ParseListDirRequest(JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object) return null;

            ListDirRequestBody? parsed;
            try { parsed = body.Deserialize<ListDirRequestBody>(PeerProtocol.Json); }
            catch (JsonException) { return null; }

            // Deserializing an object never yields null, so only the path's own absence is a case.
            return parsed!.Path is { } p && BrowseRules.PathShapeOk(p) ? p : null;
        }

        internal static JsonElement EncodeDir(PeerDirResult result) =>
            PeerProtocol.BodyOf(result.Listing is { } l && result.Ok
                ? new DirBody(true, result.Code, l.Path, l.Parent, l.Truncated, l.Items)
                // A refusal carries its code and nothing else.
                : new DirBody(false, result.Code, null, null, false, null));

        internal static PeerDirResult? ParseDir(JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object) return null;

            DirBody? parsed;
            try { parsed = body.Deserialize<DirBody>(PeerProtocol.Json); }
            catch (JsonException) { return null; }

            if (!Sane(parsed!.Code, 64, required: true)) return null;
            if (!parsed.Ok) return PeerDirResult.Fail(parsed.Code!);

            var items = parsed.Items ?? [];

            if (!Sane(parsed.Path, MaxText, required: false) || !Sane(parsed.Parent, MaxText, required: false)
                || items.Count > MaxDirItems
                || items.Any(i => i is null || !Sane(i.Name, 1024, required: true)
                    || !Sane(i.Path, MaxText, required: false) || !Sane(i.Drive, 8, required: false)))
                return null;

            return new PeerDirResult(true, parsed.Code!,
                new PeerDirListing(parsed.Path ?? "", parsed.Parent ?? "", parsed.Truncated, items));
        }

        // Present (when required), bounded, and free of control characters:
        // these strings end up in a window and in a signature.
        private static bool Sane(string? value, int max, bool required) =>
            value is null
                ? !required
                : (!required || value.Length > 0) && value.Length <= max && value.All(c => !char.IsControl(c));
    }
}
