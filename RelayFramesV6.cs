using System.Text.Json.Nodes;

namespace Orbweaver
{
    // CB-321 addendum 2 (frames-v6 push_changes.persona): the persona a push
    // speaks as, appended as the last key of the amber, done and limit
    // plaintexts so the phone's notification extension can show it as the
    // sender of an iOS communication notification. An id, a name and the
    // frames-v5 picture hash the phone already fetched the picture by — never
    // the picture, never a path, never a colour.
    //
    // Built only through From, which applies the contract's sending rules: the
    // id must be a frames-v5 persona id, the name is cut to 64 UTF-16 units and
    // a name that is empty or whitespace after the cut is no persona at all,
    // and a hash that is not a frames-v5 hash is left off rather than sent for
    // the phone to refuse (the phone would then ignore the whole persona, which
    // costs the name for the sake of a picture it could not have shown).
    internal sealed record PushPersona(string Id, string Name, string? ImageHash)
    {
        internal const int MaxName = 64;

        internal static PushPersona? From(string? id, string? name, string? imageHash)
        {
            if (!PersonaFrames.IsPersonaId(id) || name is null) return null;
            var cut = PromptFrames.Cut(name, MaxName);
            if (string.IsNullOrWhiteSpace(cut)) return null;
            return new(id!, cut, PersonaFrames.IsHash(imageHash) ? imageHash : null);
        }

        // The session's roster persona (amber, done). A roster persona with no
        // id predates CB-321's v5 keys and cannot be named to the phone.
        internal static PushPersona? Of(RelayPersona? p) => p is null ? null : From(p.Id, p.Name, p.ImageHash);

        // The PC's default persona, the personas frame's default:true row (limit).
        internal static PushPersona? Of(PersonaFrames.Row? r) => r is null ? null : From(r.Id, r.Name, r.ImageHash);

        // The done and limit form: frames-v4 canonical, keys id, name, image_hash.
        internal string Canonical()
        {
            var j = new CanonicalJson().Str("id", Id).Str("name", Name);
            if (ImageHash is not null) j.Str("image_hash", ImageHash);
            return j.ToString();
        }

        // The amber form: the same keys through the JSON library, so the amber
        // plaintext keeps the v3 form push-vectors.json pins (frames-v6 canonical).
        internal JsonObject Json()
        {
            var j = new JsonObject { ["id"] = Id, ["name"] = Name };
            if (ImageHash is not null) j["image_hash"] = ImageHash;
            return j;
        }

        // What a log line may say about it (frames-v6 log_safety): the id's first
        // six hex and whether a picture rode along. Never the name, never the
        // whole id or hash.
        internal string LogTag => $"persona={Id[..6]} avatar={(ImageHash is null ? "no" : "yes")}";

        // The log tag for a push that may or may not carry one: "none" when the
        // session had no persona, "dropped" when it did and the fitting rule
        // took it out to keep e.
        internal static string LogTagFor(PushPersona? wanted, bool sent) =>
            wanted is null ? "persona=none" : sent ? wanted.LogTag : "persona=dropped";
    }
}
