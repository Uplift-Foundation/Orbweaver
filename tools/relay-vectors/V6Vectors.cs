using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orbweaver;
using static Orbweaver.RelayCrypto;

// CB-321 addendum 2: frames-v6-vectors.json, written by the shipped desktop code.
//
// The architect authored the file with Orbweaver-Mobile contracts/
// make_v6_vectors.py, and check_v6_vectors.py checks it independently; this is
// the desktop's opinion. The inputs are inputs — the personas, the amber's
// request ids, previews, options and its tuned session label, the done title,
// the envelope ids, the limit's account (read off the committed no_persona
// plaintext, the only place the file states it) — and so are the phone's own records (each vector's `nse`,
// what the notification extension should hand iOS), which the desktop has
// nothing to compute in. Everything a desktop sends comes out of the code the
// app runs: the persona object (PushPersona.From, with the name Cut), the
// amber plaintext and its cuts (PromptFrames.FitPush), the done and limit
// plaintexts (RelayLink.DoneE, RelayUsage.LimitE) and which of them is sent
// (RelayLink.PushPlaintext), and the sealed boxes (PushSeal.SealWith with
// make_v4_vectors' fixed ephemeral key and nonce per label). The negatives'
// personas are ones the desktop refuses to build, so their plaintexts are the
// desktop's done plaintext with the committed bad persona spliced in as its
// last key — the shape a hostile or broken sender would produce. Written in
// json.dumps(indent=1, ensure_ascii=False) layout, so a regeneration is
// byte-equal to the committed file everywhere but `_source`, the comparison
// V6VectorsReproduceTests makes.
internal static class V6Vectors
{
    private const string Desktop = "dsk_5f3c9a0e1b7d2468";
    private const string Phone = "ph_a1b2c3d4e5f60718";

    internal const string GeneratedBy =
        "Uplift-Foundation/Orbweaver tools/relay-vectors (V6Vectors.cs: PushPersona, PromptFrames.FitPush, RelayLink.DoneE, RelayUsage.LimitE, RelayLink.PushPlaintext, PushSeal)";

    // contractsDir holds push-vectors.json and frames-v4-vectors.json; committed is a
    // frames-v6-vectors.json whose inputs, phone records and prose are reused.
    internal static string Build(string contractsDir, string committed, string sha, string? generatedBy = null)
    {
        var push = JsonNode.Parse(File.ReadAllText(Path.Combine(contractsDir, "push-vectors.json")))!;
        var old = JsonNode.Parse(File.ReadAllText(committed))!;
        var phonePub = Convert.FromHexString(Text(push["inputs"]!["phone_agreement"]!["public"]));
        var oldInputs = old["inputs"]!;
        var machine = Text(oldInputs["machine"]);
        var session = Text(oldInputs["session_id"]);

        var v = new JsonObject
        {
            ["_source"] = new JsonObject
            {
                ["generated_by"] = generatedBy ?? GeneratedBy,
                ["orbweaver_sha"] = sha,
                ["spec"] = Text(old["_source"]!["spec"]),
                ["encoding"] = Text(old["_source"]!["encoding"]),
            },
            ["_rules"] = Text(old["_rules"]),
            ["inputs"] = oldInputs.DeepClone(),
            ["limits"] = new JsonObject
            {
                ["max_e_chars"] = PushSeal.MaxE, ["max_apns_body_bytes"] = old["limits"]!["max_apns_body_bytes"]!.GetValue<int>(),
                ["max_persona_name_utf16"] = PushPersona.MaxName, ["persona_id_hex"] = 16, ["image_hash_hex"] = 64,
                ["avatar_max_side"] = PersonaFrames.MaxSide, ["nse_donate_wait_ms"] = old["limits"]!["nse_donate_wait_ms"]!.GetValue<int>(),
            },
        };

        // ---- amber: FitPush on each committed input -----------------------------
        var amber = new JsonObject();
        foreach (var (name, oldCase) in old["amber"]!.AsObject())
        {
            var i = oldCase!["input"]!;
            var options = i["options"]!.AsArray().Select(o => new PromptOption(
                Text(o!["key"]), Text(o["label"]), Text(o["kind"]), o["scope"] is { } scope ? Text(scope) : null)).ToList();
            var fit = PromptFrames.FitPush(Text(i["request_id"]), Text(i["session_id"]), Text(i["label"]), Text(i["tool"]), Text(i["summary"]),
                Text(i["preview"]), i["expires_at"]!.GetValue<long>(), options, Text(i["screen_hash"]), Persona(i["persona"]))
                ?? throw new InvalidOperationException($"amber {name}: no e");
            var box = PushVector($"cb321-amber-{name.Replace('_', '-')}", phonePub, fit.Plaintext);
            box["plaintext_bytes"] = Encoding.UTF8.GetByteCount(fit.Plaintext);
            var o = new JsonObject
            {
                ["input"] = i.DeepClone(),
                ["detail_cut"] = fit.DetailCut, ["labels_cut"] = fit.LabelsCut, ["persona_dropped"] = fit.PersonaDropped,
            };
            foreach (var (k, val) in box.ToList()) { box.Remove(k); o[k] = val; }
            if (oldCase["_note"] is { } note) o["_note"] = Text(note);
            o["nse"] = oldCase["nse"]!.DeepClone();
            amber[name] = o;
        }
        v["amber"] = amber;

        // ---- done: DoneE with the session's persona ------------------------------
        var aurora = Persona(oldInputs["personas"]!["aurora"]);
        var oldDone = old["done"]!;
        var donePlain = Sent(aurora, p => RelayLink.DoneE(session, "orbweaver", machine, p));
        var doneBox = PushVector("cb321-done-persona-eph", phonePub, donePlain);
        var dropTitle = Text(oldDone["persona_dropped"]!["title"]);
        v["done"] = new JsonObject
        {
            ["e"] = doneBox,
            ["apns_body"] = Apns("done", oldDone["apns_body"]!, Text(oldInputs["ck_session"]), Text(doneBox["e"])),
            ["nse"] = oldDone["nse"]!.DeepClone(),
            ["no_persona"] = new JsonObject
            {
                ["plaintext"] = RelayLink.DoneE(session, "orbweaver", machine),
                ["_note"] = Text(oldDone["no_persona"]!["_note"]),
            },
            ["persona_dropped"] = new JsonObject
            {
                ["title"] = dropTitle,
                ["plaintext"] = Sent(aurora, p => RelayLink.DoneE(session, dropTitle, machine, p)),
                ["_note"] = Text(oldDone["persona_dropped"]!["_note"]),
            },
        };

        // ---- limit: LimitE with the PC's default persona -------------------------
        var oldLimit = old["limit"]!;
        var oldLimitPlain = JsonNode.Parse(Text(oldLimit["no_persona"]!["plaintext"]))!;
        var account = new RelayUsage.Account(Text(oldLimitPlain["name"]), Text(oldLimitPlain["cli"]), "",
            oldLimitPlain["window_5h_pct"]!.GetValue<int>(), oldLimitPlain["resets_at"]!.GetValue<long>(), null, null, 0, true);
        var limitBox = PushVector("cb321-limit-persona-eph", phonePub, Sent(aurora, p => RelayUsage.LimitE(account, p)));
        v["limit"] = new JsonObject
        {
            ["e"] = limitBox,
            ["apns_body"] = Apns("limit", oldLimit["apns_body"]!, Text(oldInputs["ck_usage"]), Text(limitBox["e"])),
            ["nse"] = oldLimit["nse"]!.DeepClone(),
            ["no_persona"] = new JsonObject
            {
                ["plaintext"] = RelayUsage.LimitE(account),
                ["_note"] = Text(oldLimit["no_persona"]!["_note"]),
            },
            ["_which"] = Text(oldLimit["_which"]),
        };

        // ---- negatives: personas the desktop never builds, spliced into its done --
        var negatives = new JsonArray();
        foreach (var n in old["negatives"]!.AsArray())
        {
            var name = Text(n!["name"]);
            if (Text(n["expect"]) == "open_fails")
            {
                var flipped = Unb64Url(Text(doneBox["e"]));
                flipped[^1] ^= 0x01;
                negatives.Add(new JsonObject { ["name"] = name, ["expect"] = "open_fails", ["e"] = B64Url(flipped), ["why"] = Text(n["why"]) });
                continue;
            }
            var bad = JsonNode.Parse(Text(n["plaintext"]))!["persona"]!;
            var plain = RelayLink.DoneE(session, "orbweaver", machine);
            plain = plain[..^1] + ",\"persona\":" + Canonical(bad) + "}";
            var box = PushVector($"cb321-neg-{name}", phonePub, plain);
            negatives.Add(new JsonObject
            {
                ["name"] = name, ["expect"] = Text(n["expect"]), ["why"] = Text(n["why"]),
                ["e"] = Text(box["e"]), ["plaintext"] = plain, ["eph"] = box["eph"]!.DeepClone(), ["nonce"] = Text(box["nonce"]),
                ["nse"] = n["nse"]!.DeepClone(),
            });
        }
        v["negatives"] = negatives;

        // ---- rules: the desktop's cut on the committed names ----------------------
        var oldRules = old["rules"]!;
        v["rules"] = new JsonObject
        {
            ["name_cut"] = new JsonArray(oldRules["name_cut"]!.AsArray().Select(c =>
            {
                var o = new JsonObject { ["name"] = Text(c!["name"]), ["sent"] = PromptFrames.Cut(Text(c["name"]), PushPersona.MaxName) };
                if (c["_note"] is { } note) o["_note"] = Text(note);
                return (JsonNode)o;
            }).ToArray()),
            ["persona_order"] = new JsonArray(JsonNode.Parse(Persona(oldInputs["personas"]!["aurora"])!.Canonical())!.AsObject()
                .Select(kv => (JsonNode)JsonValue.Create(kv.Key)!).ToArray()),
            ["persona_is_last_key"] = oldRules["persona_is_last_key"]!.DeepClone(),
            ["which_persona"] = oldRules["which_persona"]!.DeepClone(),
            ["nse_text"] = Text(oldRules["nse_text"]),
        };

        return PyJson.Pretty(v, indent: 1) + "\n";
    }

    // The committed input persona through the desktop's own sending rules.
    private static PushPersona? Persona(JsonNode? p) =>
        p is null ? null : PushPersona.From(Text(p["id"]), Text(p["name"]), p["image_hash"] is { } h ? Text(h) : null);

    // What the desktop sends for a done or limit: with the persona if it fits.
    private static string Sent(PushPersona? persona, Func<PushPersona?, string> plaintext) =>
        Encoding.UTF8.GetString(RelayLink.PushPlaintext(persona, plaintext).Bytes ?? throw new InvalidOperationException("no e"));

    // A bad persona in frames-v4 canonical form: a string, or an object of strings.
    private static string Canonical(JsonNode bad)
    {
        if (bad is JsonValue) return CanonicalJson.Quote(Text(bad));
        var j = new CanonicalJson();
        foreach (var (k, val) in bad.AsObject()) j.Str(k, Text(val));
        return j.ToString();
    }

    // make_v4_vectors.py's push_seal: the ephemeral key and the nonce from the label.
    private static JsonObject PushVector(string label, byte[] phonePublic, string plaintext)
    {
        var eph = KeyPairFromPrivate(SHA256.HashData(Encoding.UTF8.GetBytes(label)));
        var nonce = SHA256.HashData(Encoding.UTF8.GetBytes(label + "-nonce"))[..12];
        var shared = SharedSecret(eph, phonePublic);
        var e = PushSeal.SealWith(eph, nonce, phonePublic, Encoding.UTF8.GetBytes(plaintext));
        return new JsonObject
        {
            ["eph"] = new JsonObject { ["private"] = Hex(eph.Private), ["public"] = Hex(eph.Public) },
            ["shared_secret"] = Hex(shared), ["key"] = Hex(PushSeal.Key(shared, eph.Public, phonePublic)),
            ["nonce"] = Hex(nonce), ["plaintext"] = plaintext, ["e"] = e, ["e_length"] = e.Length,
        };
    }

    // The APNs body the relay would forward: the committed alert text, level
    // and envelope id, with this e and the thread-id from the ck.
    private static JsonObject Apns(string push, JsonNode old, string ck, string e) => new()
    {
        ["aps"] = new JsonObject
        {
            ["alert"] = new JsonObject { ["title"] = "Orbweaver", ["body"] = Text(old["aps"]!["alert"]!["body"]) },
            ["mutable-content"] = 1, ["interruption-level"] = Text(old["aps"]!["interruption-level"]), ["thread-id"] = Desktop + ":" + ck,
        },
        ["push"] = push, ["from"] = Desktop, ["to"] = Phone, ["id"] = Text(old["id"]), ["e"] = e,
    };

    private static string Text(JsonNode? node) => node!.GetValue<string>();

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private static byte[] Unb64Url(string s) =>
        Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/') + new string('=', (4 - s.Length % 4) % 4));

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
