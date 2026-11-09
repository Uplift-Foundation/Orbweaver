using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orbweaver;
using static Orbweaver.RelayCrypto;

// CB-321: frames-v5-vectors.json, written by the shipped desktop code.
//
// The architect authored the file with Orbweaver-Mobile contracts/
// make_v5_vectors.py and check_v5_vectors.py checks it independently; this is
// the desktop's opinion. The pictures' frames are inputs — PNG bytes the
// vectors carry as hex, drawn with zlib in Python, so no encoder is involved
// in reproducing them — and so are the persona ids (the vectors derive theirs
// from the name; the desktop's own derivation is from its files and only the
// grammar is pinned). Everything else comes out of the code the app runs:
// image_hash (PersonaImageBundle.HashOf), the personas frame and the window
// chunker (PersonaFrames), the roster (RelayRoster.RosterJson), the request
// and refusals (PersonaFrames.Request, RelayReplies.Err), the grammar and ms
// tables (PersonaFrames), the cks and the boxes (RelayCrypto). The phone-only
// rule tables (cache_rule, assembly) and the prose are carried over from the
// committed file. Written in Python's json.dumps(indent=2, ensure_ascii=False)
// layout, so a regeneration is byte-equal to the committed file everywhere but
// `_source` — the comparison V5VectorsReproduceTests makes.
internal static class V5Vectors
{
    private const string Desktop = "dsk_5f3c9a0e1b7d2468";
    private const string Phone = "ph_a1b2c3d4e5f60718";
    private const long T0 = 1794124800;

    internal const string GeneratedBy =
        "Uplift-Foundation/Orbweaver tools/relay-vectors (V5Vectors.cs: PersonaFrames, PersonaImageBundle.HashOf, RelayRoster.RosterJson, RelayReplies, RelayCrypto)";

    // contractsDir holds session-vectors.json; committed is a frames-v5-vectors.json
    // whose picture frames, persona ids, prose and phone rule tables are reused.
    internal static string Build(string contractsDir, string committed, string sha, string? generatedBy = null)
    {
        var session = JsonDocument.Parse(File.ReadAllText(Path.Combine(contractsDir, "session-vectors.json"))).RootElement;
        var old = JsonNode.Parse(File.ReadAllText(committed))!;
        var key = Convert.FromHexString(session.GetProperty("epoch_keys").EnumerateArray()
            .First(e => e.GetProperty("epoch").GetInt32() == 0).GetProperty("key").GetString()!);
        var ckKey = Convert.FromHexString(session.GetProperty("ck_key").GetString()!);
        var s = new Sealer(key);
        var ckPersonas = Ck(ckKey, PersonaFrames.PersonasCk);

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
            ["inputs"] = new JsonObject
            {
                ["desktop_id"] = Desktop, ["phone_id"] = Phone,
                ["session_key_epoch0"] = Hex(key), ["ck_key"] = Hex(ckKey),
                ["ck_personas"] = ckPersonas,
                ["_note"] = Text(old["inputs"]!["_note"]),
            },
            ["limits"] = new JsonObject
            {
                ["max_window_plaintext_bytes"] = PersonaFrames.MaxWindowBytes, ["max_frame_bytes"] = PersonaFrames.MaxFrameBytes,
                ["max_frames"] = PersonaFrames.MaxFrames, ["min_side"] = PersonaFrames.MinSide, ["max_side"] = PersonaFrames.MaxSide,
                ["min_frame_ms"] = PersonaFrames.MinFrameMs, ["max_frame_ms"] = PersonaFrames.MaxFrameMs,
                ["max_source_bytes"] = PersonaFrames.MaxSourceBytes, ["max_image_bytes"] = PersonaFrames.MaxImageBytes,
                ["image_windows_per_second"] = PersonaFrames.ImageWindowsPerSecond, ["fetch_timeout_seconds"] = PersonaFrames.FetchTimeoutSeconds,
            },
        };

        // ---- pictures: the frames are inputs, everything about them computed ----
        var pictures = old["pictures"]!.AsArray().Select(p => new Picture(
            Text(p!["name"]), Text(p["_note"]), Convert.FromHexString(Text(p["source_hex"])),
            p["w"]!.GetValue<int>(), p["h"]!.GetValue<int>(),
            p["frames"]!.AsArray().Select(f => new PersonaFrames.Frame(f!["ms"]!.GetValue<int>(), Convert.FromHexString(Text(f["png_hex"])))).ToList()))
            .ToList();
        v["pictures"] = new JsonArray(pictures.Select(p => (JsonNode)new JsonObject
        {
            ["name"] = p.Name, ["_note"] = p.Note,
            ["source_hex"] = Hex(p.Source), ["source_bytes"] = p.Source.Length,
            ["image_hash"] = p.Hash,
            ["w"] = p.W, ["h"] = p.H,
            ["frames_total"] = p.Frames.Count,
            ["frames"] = new JsonArray(p.Frames.Select(f => (JsonNode)new JsonObject
            {
                ["ms"] = f.Ms, ["png_hex"] = Hex(f.Data), ["bytes"] = f.Data.Length,
            }).ToArray()),
        }).ToArray());
        var byName = pictures.ToDictionary(p => p.Name);

        // ---- personas ----------------------------------------------------------
        string IdOf(string name) => Text(old["personas"]!["rows_input"]!.AsArray().First(r => Text(r!["name"]) == name)!["id"]);
        var aurora = new PersonaFrames.Row(IdOf("Aurora"), "Aurora", OrbGlyph.Initials("Aurora"), true, byName["animated_small"].Hash);
        var nova = new PersonaFrames.Row(IdOf("Nova"), "Nova", OrbGlyph.Initials("Nova"), false, byName["still"].Hash);
        var vector = new PersonaFrames.Row(IdOf("Vector"), "Vector", OrbGlyph.Initials("Vector"), false, null);
        var sage = new PersonaFrames.Row(IdOf("Sage Okoro"), "Sage Okoro", OrbGlyph.Initials("Sage Okoro"), false, byName["animated_wide"].Hash);
        PersonaFrames.Row[] rows = [aurora, nova, vector, sage];
        v["personas"] = new JsonObject
        {
            ["frame"] = s.Box(Desktop, Phone, "state", PersonaFrames.Personas(rows), ckPersonas),
            ["empty"] = s.Box(Desktop, Phone, "state", PersonaFrames.Personas([]), ckPersonas),
            ["rows_input"] = new JsonArray(rows.Select(r =>
            {
                var o = new JsonObject { ["id"] = r.Id, ["name"] = r.Name };
                if (r.Default) o["default"] = true;
                if (r.ImageHash is { } hash) o["image_hash"] = hash;
                return (JsonNode)o;
            }).ToArray()),
            ["_note"] = Text(old["personas"]!["_note"]),
        };

        // ---- roster, with the two v5 keys ---------------------------------------
        const string lead = "8a6f0c1e-2b3d-4e5f-9a8b-7c6d5e4f3a2b";
        RelaySession S(string id, string cli, string title, string cwd, string state, long since, string? parent, RelayPersona? persona) =>
            new(id, cli, title, cwd, state, parent, persona) { Since = DateTimeOffset.FromUnixTimeSeconds(since) };
        RelayPersona P(PersonaFrames.Row r, string? color) => new(r.Name, OrbGlyph.Initials(r.Name), color, r.Id, r.ImageHash);
        RelaySession[] sessions =
        [
            S(lead, "claude", "demo-app", "/Users/dev/Source/demo-app", "generating", T0, null, P(aurora, "purple")),
            S("b2c4d6e8-0a1b-4c2d-8e3f-a4b5c6d7e8f9", "claude", "sage-okoro", "/Users/dev/Source/demo-app-wt/sage-okoro", "waiting", T0 + 12, lead, P(sage, "pink")),
            S("c3d5e7f9-1b2c-4d3e-9f4a-b5c6d7e8f9a0", "codex", "demo-sdk", "/Users/dev/Source/demo-sdk", "idle", T0 + 300, null, P(vector, null)),
            S("d4e6f8a0-2c3d-4e4f-8a5b-c6d7e8f9a0b1", "grok", "scratch", "C:\\Users\\dev\\scratch", "idle", T0 + 400, null, null),
        ];
        v["roster"] = new JsonObject
        {
            ["frame"] = s.Box(Desktop, Phone, "state", Encoding.UTF8.GetString(RelayRoster.RosterJson(1, sessions))),
            ["sessions_input"] = new JsonArray(sessions.Select(r =>
            {
                var o = new JsonObject
                {
                    ["session_id"] = r.SessionId, ["cli"] = r.Cli, ["title"] = r.Title, ["cwd"] = r.Cwd, ["state"] = r.State,
                    ["since"] = RelayRoster.Timestamp(r.Since),
                };
                if (r.ParentSessionId is { } parent) o["parent_session_id"] = parent;
                if (r.Persona is { } p)
                {
                    var po = new JsonObject { ["name"] = p.Name };
                    if (p.Color is { } color) po["color"] = color;
                    po["id"] = p.Id;
                    if (p.ImageHash is { } hash) po["image_hash"] = hash;
                    o["persona"] = po;
                }
                return (JsonNode)o;
            }).ToArray()),
            ["_note"] = Text(old["roster"]!["_note"]),
        };

        // ---- persona_image: request, windows, refusals ----------------------------
        var images = new JsonObject();
        foreach (var (p, i) in pictures.Select((p, i) => (p, i)))
        {
            var ckImage = Ck(ckKey, PersonaFrames.ImageCk(p.Hash));
            var request = With(s.Box(Phone, Desktop, "action", PersonaFrames.Request(p.Hash)), ("envelope_id", "01JCA0IMAGE000000000000AB" + i));
            var windows = new JsonArray(PersonaFrames.Chunk(p.Hash, p.W, p.H, p.Frames)
                .Select(w => (JsonNode)With(s.Box(Desktop, Phone, "chat", w.Json, ckImage), ("from_frame", w.From), ("frame_count", w.Count)))
                .ToArray());
            images[p.Name] = new JsonObject { ["ck_image"] = ckImage, ["request"] = request, ["windows"] = windows };
        }
        var unknown = PersonaImageBundle.HashOf(Encoding.UTF8.GetBytes("a picture this desktop never offered"));
        images["refusals"] = new JsonObject
        {
            ["unknown_request"] = With(s.Box(Phone, Desktop, "action", PersonaFrames.Request(unknown)), ("envelope_id", "01JCA0IMAGE000000000000AC0")),
            ["err_unknown_image"] = s.Box(Desktop, Phone, "action", RelayReplies.Err("01JCA0IMAGE000000000000AC0", RelayReplies.UnknownImage)),
            ["err_bad_frame"] = s.Box(Desktop, Phone, "action", RelayReplies.Err("01JCA0IMAGE000000000000AC1", RelayReplies.BadFrame)),
            ["_bad_frame_input"] = new JsonObject { ["t"] = "persona_image", ["hash"] = unknown[..63] + "G" },
        };
        v["persona_image"] = images;

        // ---- rules: the desktop's verdicts on the committed inputs -----------------
        var oldRules = old["rules"]!;
        JsonArray Table(string name, Func<JsonNode, JsonObject> verdict) =>
            new(oldRules[name]!.AsArray().Select(c => (JsonNode)verdict(c!)).ToArray());
        JsonObject Note(JsonObject o, JsonNode c)
        {
            if (c["_note"] is { } note) o["_note"] = Text(note);
            return o;
        }
        string Expect(bool ok) => ok ? "accepted" : "refused";
        v["rules"] = new JsonObject
        {
            ["hash_grammar"] = Table("hash_grammar", c => Note(new JsonObject { ["value"] = Text(c["value"]), ["valid"] = PersonaFrames.IsHash(Text(c["value"])) }, c)),
            ["persona_id_grammar"] = Table("persona_id_grammar", c => Note(new JsonObject { ["value"] = Text(c["value"]), ["valid"] = PersonaFrames.IsPersonaId(Text(c["value"])) }, c)),
            ["frame_cap"] = Table("frame_cap", c => new JsonObject { ["frame_bytes"] = c["frame_bytes"]!.GetValue<int>(), ["expect"] = Expect(PersonaFrames.FrameAllowed(c["frame_bytes"]!.GetValue<int>())) }),
            ["side"] = Table("side", c => new JsonObject
            {
                ["w"] = c["w"]!.GetValue<int>(), ["h"] = c["h"]!.GetValue<int>(),
                ["expect"] = Expect(PersonaFrames.SideAllowed(c["w"]!.GetValue<int>(), c["h"]!.GetValue<int>())),
            }),
            ["ms_rule"] = new JsonObject
            {
                ["_rule"] = Text(oldRules["ms_rule"]!["_rule"]),
                ["cases"] = new JsonArray(oldRules["ms_rule"]!["cases"]!.AsArray().Select(c => (JsonNode)new JsonObject
                {
                    ["gif_ms"] = c!["gif_ms"]!.GetValue<int>(), ["frames"] = c["frames"]!.GetValue<int>(),
                    ["sent_ms"] = PersonaFrames.SentMs(c["gif_ms"]!.GetValue<int>(), c["frames"]!.GetValue<int>()),
                }).ToArray()),
            },
            // The phone's own rules; the desktop has nothing to compute in them.
            ["cache_rule"] = oldRules["cache_rule"]!.DeepClone(),
            ["assembly"] = oldRules["assembly"]!.DeepClone(),
        };

        return PyJson.Pretty(v) + "\n";
    }

    private sealed record Picture(string Name, string Note, byte[] Source, int W, int H, List<PersonaFrames.Frame> Frames)
    {
        internal string Hash { get; } = PersonaImageBundle.HashOf(Source);
    }

    private static string Text(JsonNode? node) => node!.GetValue<string>();

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private static JsonObject With(JsonObject o, params (string Key, JsonNode Value)[] extra)
    {
        foreach (var (k, value) in extra) o[k] = value;
        return o;
    }

    // Counters from 200, so these never look like the v4 file's (make_v5_vectors.py).
    private sealed class Sealer(byte[] key)
    {
        private readonly ulong[] _counter = [200, 200];

        internal JsonObject Box(string from, string to, string cls, string plaintext, string ck = "")
        {
            var direction = from == Desktop ? DesktopToPhone : PhoneToDesktop;
            var nonce = Nonce(direction, 0, _counter[direction]++);
            var aad = Aad(from, to, cls);
            return new JsonObject
            {
                ["from"] = from, ["to"] = to, ["class"] = cls, ["push"] = "none", ["ck"] = ck,
                ["plaintext"] = plaintext, ["plaintext_bytes"] = Encoding.UTF8.GetByteCount(plaintext),
                ["aad"] = Hex(aad), ["nonce"] = Hex(nonce), ["ct"] = Seal(key, nonce, Encoding.UTF8.GetBytes(plaintext), aad),
            };
        }
    }
}
