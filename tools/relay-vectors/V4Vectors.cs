using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orbweaver;
using static Orbweaver.RelayCrypto;

// CB-316: frames-v4-vectors.json, written by the shipped desktop code.
//
// The architect authored the file with contracts/make_v4_vectors.py (Python)
// and check_v4_vectors.py checks it independently; this is the third opinion
// the plan asks for. Every computed value comes out of the code the app runs
// — TranscriptWatch's cut and chunker, LaunchGate's body, RelayUsage's frame,
// rounding and limit edge, RelayDelta.Finished, RelayLink.DoneE, PushSeal and
// RelayCrypto — from the same fixed inputs, and is written in Python's
// json.dumps(indent=2, ensure_ascii=False) layout so the result is
// byte-comparable with the committed file.
//
// ECDSA is randomized, so a fresh run can never reproduce a signature. Given
// the committed file, the signatures in it are verified and reused instead,
// which makes a regeneration byte-equal to the committed file everywhere but
// `_source` — the comparison V4VectorsReproduceTests makes in the unit suite.
internal static class V4Vectors
{
    private const string Desktop = "dsk_5f3c9a0e1b7d2468";
    private const string Phone = "ph_a1b2c3d4e5f60718";
    private const string SessionId = "0f6c4f2a";
    private const string Machine = "MacBook Pro";
    private const long Mint = 1794111792;
    private const long T0 = 1794124800;

    internal const string GeneratedBy =
        "Uplift-Foundation/Orbweaver tools/relay-vectors (V4Vectors.cs: TranscriptWatch, LaunchGate, RelayUsage, RelayLink.DoneE, PushSeal, RelayCrypto)";

    // contractsDir holds session-, answer- and push-vectors.json; signatures, if
    // given, is a committed frames-v4-vectors.json whose signatures are reused.
    internal static string Build(string contractsDir, string sha, string? signatures = null, string? generatedBy = null)
    {
        JsonElement Load(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(contractsDir, name))).RootElement;
        var session = Load("session-vectors.json");
        var answer = Load("answer-vectors.json");
        var push = Load("push-vectors.json");
        var reuse = signatures is not null && File.Exists(signatures) ? JsonDocument.Parse(File.ReadAllText(signatures)).RootElement : (JsonElement?)null;

        var key = Convert.FromHexString(session.GetProperty("epoch_keys").EnumerateArray()
            .First(e => e.GetProperty("epoch").GetInt32() == 0).GetProperty("key").GetString()!);
        var ckKey = Convert.FromHexString(session.GetProperty("ck_key").GetString()!);
        var idJson = answer.GetProperty("inputs").GetProperty("phone_identity");
        var kaJson = push.GetProperty("inputs").GetProperty("phone_agreement");
        var identity = new KeyPair(Convert.FromHexString(idJson.GetProperty("private").GetString()!), Convert.FromHexString(idJson.GetProperty("public").GetString()!));
        var agreement = new KeyPair(Convert.FromHexString(kaJson.GetProperty("private").GetString()!), Convert.FromHexString(kaJson.GetProperty("public").GetString()!));
        var s = new Sealer(key);
        var ckSession = Ck(ckKey, SessionId);
        var ckUsage = Ck(ckKey, RelayLink.UsageCk);

        var lines = FixtureLines();
        var total = lines.Count;
        var wire = lines.Select(l => TranscriptWatch.Line(l.Role, l.Text, l.At, l.Error)).ToList();

        var v = new JsonObject
        {
            ["_source"] = new JsonObject
            {
                ["generated_by"] = generatedBy ?? GeneratedBy,
                ["orbweaver_sha"] = sha,
                ["spec"] = "CB-316 architect plan (docs/design/milestone-4-plan.md); contracts/frames-v4.json sealed_plaintext",
                ["encoding"] = "hex is lowercase; ct is standard base64 with padding of nonce(12) || ciphertext || tag(16) as in session-vectors.json; nonce, sig and e are base64url without padding; every plaintext is the canonical form (keys in frames-v4.json's order, the shared escaper, bare integers, no whitespace)",
            },
            ["_rules"] = "Sealed with session-vectors.json's epoch-0 key and ck key, direction 0 = desktop to phone, 1 = phone to desktop, counters from 100. window: the last 200 parsed lines, chunked greedily so each canonical window is at most 48000 UTF-8 bytes and adding its next line would exceed that; a line's text over 8000 UTF-8 bytes is the longest scalar-boundary prefix of at most 7997 bytes plus U+2026, with cut:true; from_line counts parsed lines from the start of the transcript; total is the parsed-line count when sent; live is false on backfill and pages, true on tail; reset:true means drop everything held. Rows are compared by role and text, never at: a last row rewritten in place is re-sent as a tail window from its own index, and the phone replaces by index (window.restated_last_row, window.diff_rule). launch: body = canonical {cwd,cli,persona,model,parent_session_id (\"\" when absent),first_message_sha256 (lowercase hex of UTF-8 first_message),nonce,expires_at}; message = UTF-8('orbweaver-launch-v1') || body; sig = P-256 ECDSA SHA-256 raw r||s by the phone's identity key (pairing id_pub); expires_at = mint + 120. ECDSA is randomized: verify sig, never byte-compare it. Replies carry ref = the request envelope's id.",
            ["inputs"] = new JsonObject
            {
                ["desktop_id"] = Desktop, ["phone_id"] = Phone, ["machine"] = Machine, ["session_id"] = SessionId,
                ["session_key_epoch0"] = Hex(key), ["ck_key"] = Hex(ckKey),
                ["ck_session"] = ckSession, ["ck_usage"] = ckUsage,
                ["phone_identity"] = JsonNode.Parse(idJson.GetRawText()),
                ["phone_agreement"] = JsonNode.Parse(kaJson.GetRawText()),
                ["_note"] = "session_key_epoch0 and ck_key are session-vectors.json's; phone_identity is answer-vectors.json's; phone_agreement is push-vectors.json's. ck_usage = Ck(ck_key, \"usage\").",
            },
            ["limits"] = new JsonObject
            {
                ["max_window_plaintext_bytes"] = TranscriptWatch.MaxWindowBytes, ["max_line_text_bytes"] = TranscriptWatch.MaxLineBytes,
                ["backfill_lines"] = TranscriptWatch.BackfillLines, ["max_input_text_bytes"] = RelayText.MaxBytes,
                ["launch_ttl_seconds"] = 120, ["tail_debounce_ms"] = (int)TranscriptWatcher.Debounce.TotalMilliseconds,
            },
        };

        // ---- watch -------------------------------------------------------------
        const string watchId = "01JCA0WATCH0000000000000AB";
        v["watch"] = new JsonObject
        {
            ["subscribe"] = With(s.Box(Phone, Desktop, "action", new CanonicalJson().Str("t", "watch").Str("session_id", SessionId).ToString()), ("envelope_id", watchId)),
            ["ok_watching"] = s.Box(Desktop, Phone, "action", RelayReplies.Ok(watchId, RelayReplies.Watching, SessionId, total), ckSession),
            ["page"] = With(s.Box(Phone, Desktop, "action", new CanonicalJson().Str("t", "watch").Str("session_id", SessionId).Num("before", 30).ToString()), ("envelope_id", "01JCA0WATCH0000000000000AC")),
            ["unsubscribe"] = With(s.Box(Phone, Desktop, "action", new CanonicalJson().Str("t", "unwatch").Str("session_id", SessionId).ToString()), ("envelope_id", "01JCA0WATCH0000000000000AD")),
            ["ok_unwatched"] = s.Box(Desktop, Phone, "action", RelayReplies.Ok("01JCA0WATCH0000000000000AD", RelayReplies.Unwatched, SessionId), ckSession),
            ["err_unknown_session"] = s.Box(Desktop, Phone, "action", RelayReplies.Err("01JCA0WATCH0000000000000AE", RelayReplies.UnknownSession, "deadbeef")),
            ["err_bad_frame"] = s.Box(Desktop, Phone, "action", RelayReplies.Err("01JCA0WATCH0000000000000AF", RelayReplies.BadFrame, SessionId), ckSession),
            ["_bad_frame_input"] = new JsonObject { ["t"] = "watch", ["session_id"] = SessionId, ["before"] = 0 },
        };

        // ---- window ------------------------------------------------------------
        var appended = new List<(string Role, string Text, long At, bool Error)>
        {
            ("user", "Now fix it.", T0 + 7 * total, false),
            ("assistant", "On it \u2014 editing the assertion.", T0 + 7 * total + 4, false),
            ("system", "Edit: tests/UnitTests/File61.cs", T0 + 7 * total + 9, false),
        };
        var grown = wire.Concat(appended.Select(a => TranscriptWatch.Line(a.Role, a.Text, a.At))).ToList();
        JsonArray Windows(IEnumerable<string> windows)
        {
            var arr = new JsonArray();
            foreach (var w in windows)
            {
                var doc = JsonDocument.Parse(w).RootElement;
                arr.Add(With(s.Box(Desktop, Phone, "chat", w, ckSession),
                    ("from_line", doc.GetProperty("from_line").GetInt32()), ("line_count", doc.GetProperty("lines").GetArrayLength())));
            }
            return arr;
        }
        v["window"] = new JsonObject
        {
            ["lines_input"] = new JsonArray(lines.Select(LineNode).ToArray()),
            ["_note"] = "lines_input is the parser's output (ChatTranscript/CodexTranscript/GrokTranscript rows: role user|assistant|system, text, at), not a transcript file; the parsers have their own suites. Everything below is derived from it.",
            ["cut_lines"] = new JsonArray(wire.Select((l, i) => (l, i)).Where(x => x.l.Cut).Select(x => (JsonNode)x.i).ToArray()),
            ["backfill"] = Windows(TranscriptWatch.Backfill(SessionId, wire)),
            ["page_before_30"] = Windows(TranscriptWatch.Page(SessionId, wire, 30)),
            ["tail"] = Windows(TranscriptWatch.Tail(SessionId, grown, total)),
            ["tail_appended_input"] = new JsonArray(appended.Select(LineNode).ToArray()),
            // Mobile #31: a last row rewritten in place, alone and with a row
            // after it — the desk's own Diff decides where each window starts.
            ["restated_last_row"] = Restated(s, ckSession, grown,
                appended.SkipLast(1).Append(appended[^1] with { Text = "Edit: tests/UnitTests/File61.cs (+3 \u22121)" }).ToList(), wire),
            ["restated_and_appended"] = Restated(s, ckSession, grown,
                appended.SkipLast(1).Append(appended[^1] with { Text = "Edit: tests/UnitTests/File61.cs (+3 \u22121)" })
                    .Append(("assistant", "Fixed. Rerunning.", T0 + 7 * total + 15, false)).ToList(), wire),
            ["diff_rule"] = new JsonObject
            {
                ["_rule"] = "rows compared by role and text, never at; shrink or a changed first row -> reset; else start = previous count, minus one when the previous last row changed in place; rows from start -> tail; none -> nothing",
                ["cases"] = DiffCases(),
            },
            ["reset_to_5"] = Windows(TranscriptWatch.Backfill(SessionId, wire.Take(5).ToList(), reset: true)),
        };

        // ---- input -------------------------------------------------------------
        const string inputId = "01JCA0INPUT000000000000AB1";
        const string text = "Please rerun `dotnet test \"tests/UnitTests\"` \u2014 only the red ones.\nThen summarise.";
        string Bad(string t) => RelayText.Typeable(t) ? "ok" : RelayReplies.BadInput;
        v["input"] = new JsonObject
        {
            ["typed"] = With(s.Box(Phone, Desktop, "action", new CanonicalJson().Str("t", "input").Str("session_id", SessionId).Str("text", text).ToString()), ("envelope_id", inputId)),
            ["ok_typed"] = s.Box(Desktop, Phone, "action", RelayReplies.Ok(inputId, RelayReplies.Typed, SessionId), ckSession),
            ["refusals"] = new JsonArray(
                s.Box(Desktop, Phone, "action", RelayReplies.Err(inputId, RelayReplies.NotAtPrompt, SessionId, TypingGate.WaitingWhy), ckSession),
                s.Box(Desktop, Phone, "action", RelayReplies.Err(inputId, RelayReplies.NotAtPrompt, SessionId, TypingGate.DialogWhy("Bash command")), ckSession),
                s.Box(Desktop, Phone, "action", RelayReplies.Err(inputId, RelayReplies.NotAtPrompt, SessionId, TypingGate.UnreadableWhy), ckSession),
                s.Box(Desktop, Phone, "action", RelayReplies.Err(inputId, RelayReplies.Untypeable, SessionId), ckSession),
                s.Box(Desktop, Phone, "action", RelayReplies.Err(inputId, RelayReplies.TypingFailed, SessionId, "kitty refused the send (remote control off)"), ckSession),
                s.Box(Desktop, Phone, "action", RelayReplies.Err(inputId, RelayReplies.UnknownSession, "deadbeef")),
                s.Box(Desktop, Phone, "action", RelayReplies.Err(inputId, RelayReplies.BadInput, SessionId), ckSession)),
            ["bad_input_cases"] = new JsonArray(
                new[]
                {
                    ("empty", ""), ("escape-byte", "ls\u001b[A"), ("carriage-return", "ls\r"), ("over-8000-bytes", new string('y', 8001)),
                    ("newline-and-tab-allowed", "line one\n\tindented"), ("exactly-8000-bytes", new string('y', 8000)),
                    ("non-ascii-counted-in-bytes", new string('λ', 4001)),
                    ("c1-csi-u009b", "ls\u009b[A"), ("c1-u0080", "ls\u0080"), ("c1-u009f", "ls\u009f"),
                    ("u00a0-nbsp-allowed", "ls\u00a0-la"), ("del-u007f", "ls\u007f"),
                }.Select(c => (JsonNode)new JsonObject { ["name"] = c.Item1, ["text"] = c.Item2, ["expect"] = Bad(c.Item2) }).ToArray()),
        };

        // ---- launch ------------------------------------------------------------
        var nonceRaw = Convert.FromHexString("5a0d1f6b8c2e4a7d9b3f1c0e2d4b6a8c");
        var launches = new[]
        {
            ("claude-minimal", new LaunchFields("/Users/dev/Source/demo-app", "claude", "", "", "", "Add a Watch complication target to the update feed fixture and run the tests.", "", 0)),
            ("claude-full", new LaunchFields("/Users/dev/Source/Résumé-Demo", "claude", "", "opus", SessionId, "Résumé the failing tests \u2014 paths like C:\\Users\\w and \"quotes\" stay literal.", "", 0)),
            ("codex-model", new LaunchFields("/Users/dev/Source/demo-sdk", "codex", "", "gpt-5.5-codex", "", "Run the unit tests.", "", 0)),
            ("grok-no-message", new LaunchFields("C:\\Users\\dev\\Source\\Orbweaver", "grok", "", "", "", "", "", 0)),
        };
        string SigFor(Func<JsonElement, string?> pick, byte[] message, KeyPair signer, byte[]? verifyKey = null)
        {
            if (reuse is { } r && pick(r) is { } old && RelayCrypto.Verify(verifyKey ?? signer.Public, message, FromBase64Url(old))) return old;
            return Base64Url(Sign(signer, message));
        }
        var positives = new JsonArray();
        var baseFields = default(LaunchFields)!;
        string baseSig = "", baseBody = "", baseEnv = "";
        for (var i = 0; i < launches.Length; i++)
        {
            var (name, partial) = launches[i];
            var nonce = Base64Url([.. nonceRaw[..^1], (byte)i]);
            var f = partial with { Nonce = nonce, ExpiresAt = Mint + 120 };
            var body = LaunchGate.Body(f);
            var message = LaunchGate.Message(body);
            var index = i;
            var sig = SigFor(r => r.GetProperty("launch").GetProperty("positives")[index].GetProperty("sig").GetString(), message, identity);
            var envId = "01JCA0LAUNCH0000000000000" + i;
            var newSid = $"9c1e{i:00}2b";
            // The frame's box first, then its replies: the generator's order,
            // which decides every counter after it.
            var launchBox = With(s.Box(Phone, Desktop, "action", LaunchFrame(f, sig)), ("envelope_id", envId));
            var replies = new JsonArray();
            positives.Add(new JsonObject
            {
                ["name"] = name, ["fields"] = FieldsNode(f), ["body"] = body, ["body_utf8"] = Hex(Encoding.UTF8.GetBytes(body)),
                ["message"] = Hex(message), ["sig"] = sig, ["launch_frame"] = launchBox, ["replies"] = replies,
            });
            replies.Add(s.Box(Desktop, Phone, "action", RelayReplies.Ok(envId, RelayReplies.Launched, newSid), Ck(ckKey, newSid)));
            if (f.FirstMessage.Length > 0)
                replies.Add(s.Box(Desktop, Phone, "action", RelayReplies.Ok(envId, RelayReplies.Typed, newSid), Ck(ckKey, newSid)));
            positives[i]!["reply_count"] = replies.Count;
            if (i == 1) { baseFields = f; baseSig = sig; baseBody = body; baseEnv = envId; }
        }

        var negatives = new JsonArray();
        void Neg(string name, string body, string sig, string note, (string, JsonNode)? extra = null)
        {
            var n = new JsonObject { ["name"] = name, ["body"] = body, ["sig"] = sig, ["expect"] = "verify_fails", ["_note"] = note };
            if (extra is { } x) n[x.Item1] = x.Item2;
            negatives.Add(n);
        }
        string NegSig(string name, byte[] message, KeyPair signer) =>
            SigFor(r => r.GetProperty("launch").GetProperty("negatives").EnumerateArray()
                .FirstOrDefault(n => n.GetProperty("name").GetString() == name) is { ValueKind: JsonValueKind.Object } n ? n.GetProperty("sig").GetString() : null,
                message, signer);
        Neg("cwd-changed", LaunchGate.Body(baseFields with { Cwd = "/Users/dev/Source/demo-mobile" }), baseSig,
            "the claude-full signature over a body that now names another folder");
        Neg("parent-omitted-in-body", LaunchGate.Body(baseFields with { ParentSessionId = "" }), baseSig,
            "the same signature, but the body built with parent_session_id \"\" where the frame carried one; the desktop rebuilds the body from the frame, so this mismatch is bad_signature");
        Neg("message-changed", LaunchGate.Body(baseFields with { FirstMessage = baseFields.FirstMessage + " " }), baseSig,
            "one trailing space in first_message changes first_message_sha256");
        Neg("no-domain-prefix", baseBody, NegSig("no-domain-prefix", Encoding.UTF8.GetBytes(baseBody), identity),
            "a signature over the bare body without orbweaver-launch-v1");
        Neg("answer-domain-prefix", baseBody, NegSig("answer-domain-prefix", [.. Encoding.UTF8.GetBytes(AnswerSigning.Domain), .. Encoding.UTF8.GetBytes(baseBody)], identity),
            "a signature under the v3 answer domain over a launch body: the domains are separate on purpose");
        Neg("agreement-key-signed", baseBody, NegSig("agreement-key-signed", LaunchGate.Message(baseBody), agreement),
            "signed with the phone's agreement key instead of its identity key");
        var lib = PyJson.Compact(JsonNode.Parse(baseBody)!);
        Neg("library-escaped-body", lib, NegSig("library-escaped-body", LaunchGate.Message(lib), identity),
            "the right signature over the wrong bytes: a library's \\u00e9 for é; the desktop's rebuilt body has literal UTF-8, so verification fails there",
            ("body_differs_from_canonical", lib != baseBody));
        Neg("whitespace-body", baseBody.Replace(",", ", "), baseSig, "a pretty-printed body");

        var env = new LaunchGate.Environment(false, cli => Array.IndexOf(LaunchGate.Clis, cli) >= 0, cwd => cwd.StartsWith('/'), sid => sid == SessionId);
        JsonObject Rule(string name, LaunchFields f, string? note = null)
        {
            var nonces = new NonceWindow();
            var now = DateTimeOffset.FromUnixTimeSeconds(Mint);
            if (name == "replayed-nonce") nonces.Add("ph", f.Nonce, now);
            var r = new JsonObject { ["name"] = name, ["fields"] = FieldsNode(f), ["now"] = Mint, ["expect"] = LaunchGate.Rules(f, "ph", now, nonces, env) ?? "ok" };
            if (note is not null) r["_note"] = note;
            return r;
        }
        var b = baseFields;
        v["launch"] = new JsonObject
        {
            ["positives"] = positives,
            ["negatives"] = negatives,
            ["rule_negatives"] = new JsonArray(
                Rule("expired", b with { ExpiresAt = Mint - 1 }),
                Rule("expiry-too-far", b with { ExpiresAt = Mint + 301 }, "expires_at more than 300 s ahead of the desktop's clock is refused too"),
                Rule("expiry-at-limit", b with { ExpiresAt = Mint + 300 }),
                Rule("replayed-nonce", b, "the same nonce seen from this phone inside the hour, answer or launch"),
                Rule("unknown-cli", b with { Cli = "gemini" }),
                Rule("relative-cwd", b with { Cwd = "Source/Orbweaver" }),
                Rule("bad-model-chars", b with { Model = "gpt 5; rm -rf" }),
                Rule("model-65-chars", b with { Model = new string('m', 65) }),
                Rule("model-64-chars", b with { Model = new string('m', 64) }),
                Rule("persona-set", b with { Persona = "Pixel" }),
                Rule("parent-on-grok", b with { Cli = "grok" }),
                Rule("message-over-8000-bytes", b with { FirstMessage = new string('y', 8001) })),
            ["check_order"] = new JsonArray(new[]
            {
                RelayReplies.BadFrame, RelayReplies.BadSignature, RelayReplies.Expired, RelayReplies.Replayed, RelayReplies.UnknownCli,
                RelayReplies.UnknownCwd, RelayReplies.BadModel, RelayReplies.UnsupportedPersona, RelayReplies.UnknownParent,
                RelayReplies.UnsupportedParent, RelayReplies.BadInput, RelayReplies.NoTerminal, RelayReplies.LaunchFailed, RelayReplies.NoSession,
            }.Select(c => (JsonNode)c).ToArray()),
            ["refusals"] = new JsonArray(new[]
                {
                    RelayReplies.BadSignature, RelayReplies.Expired, RelayReplies.Replayed, RelayReplies.UnknownCli, RelayReplies.UnknownCwd,
                    RelayReplies.BadModel, RelayReplies.UnsupportedPersona, RelayReplies.UnknownParent, RelayReplies.UnsupportedParent, RelayReplies.NoSession,
                }.Select(c => (JsonNode)s.Box(Desktop, Phone, "action", RelayReplies.Err(baseEnv, c)))
                .ToList()   // sealed now: the Appends below are evaluated eagerly and would take the earlier counters
                .Append(s.Box(Desktop, Phone, "action", RelayReplies.Err(baseEnv, RelayReplies.NoTerminal, null, TerminalScripts.NoTmuxWhy)))
                .Append(s.Box(Desktop, Phone, "action", RelayReplies.Err(baseEnv, RelayReplies.LaunchFailed, null, "Couldn't open a terminal for Codex.")))
                .Append(s.Box(Desktop, Phone, "action", RelayReplies.Err(baseEnv, RelayReplies.BadInput)))
                .ToArray()),
        };

        // ---- usage -------------------------------------------------------------
        List<RelayUsage.Account> Accounts(int claudePct) => RelayUsage.Order(new[]
        {
            new AccountUsage("/c", "user@example.com", true, "Max",
                new UsageWindow(claudePct, DateTimeOffset.FromUnixTimeSeconds(Mint + 5400)),
                new UsageWindow(31, DateTimeOffset.FromUnixTimeSeconds(Mint + 432000)), null,
                DateTimeOffset.FromUnixTimeSeconds(Mint), AccountUsageSource.ClaudeCode),
            new AccountUsage(null, "codex (default)", true, null,
                new UsageWindow(17, DateTimeOffset.FromUnixTimeSeconds(Mint + 9000)), null, null,
                DateTimeOffset.FromUnixTimeSeconds(Mint - 60), AccountUsageSource.Codex),
            new AccountUsage(null, "grok", true, null, null, null, null,
                DateTimeOffset.FromUnixTimeSeconds(Mint - 120), AccountUsageSource.Grok),
        }.Select(r => RelayUsage.From(r)));
        var atLimit = Accounts(81);
        var limitPush = PushVector("cb316-limit-eph", agreement.Public, RelayUsage.LimitE(atLimit[0]));
        const string limitEnv = "01JCA0LIMIT000000000000AB1";
        var tracker = new RelayUsageTracker();
        var readings = new[] { (40, 5400), (79, 5400), (80, 5400), (85, 5400), (78, 5400), (81, 5400), (90, 23400), (95, 23400) };
        v["usage"] = new JsonObject
        {
            ["frame"] = s.Box(Desktop, Phone, "state", RelayUsage.Frame(Accounts(62)), ckUsage),
            ["_rounding"] = new JsonArray(new[] { 61.5, 61.49, 0.4, 100.0, 104.0, -1.0 }
                .Select(p => (JsonNode)new JsonObject { ["percent"] = p, ["pct"] = RelayUsage.Pct(p) }).ToArray()),
            ["limit_frame"] = With(s.Box(Desktop, Phone, "state", RelayUsage.Frame(atLimit), ckUsage, "limit", limitPush["e"]!.GetValue<string>()), ("envelope_id", limitEnv)),
            ["limit_e"] = limitPush,
            ["limit_apns_body"] = Apns("limit", "An account is near its limit", "active", limitEnv, ckUsage, limitPush["e"]!.GetValue<string>()),
            ["limit_rule"] = new JsonObject
            {
                ["_rule"] = "fires when a reading's window_5h_pct is >= 80 and the previous reading for that account was absent, below 80, or had a different resets_at",
                ["readings"] = new JsonArray(readings.Select(r => (JsonNode)new JsonObject
                {
                    ["pct"] = r.Item1, ["resets_at"] = Mint + r.Item2,
                    ["fires"] = tracker.Next([new RelayUsage.Account("a", "claude", "", r.Item1, Mint + r.Item2, null, null, 0, true)]).Any(x => x.Crossing is not null),
                }).ToArray()),
                ["first_reading_at_82_fires"] = new RelayUsageTracker().Next([new RelayUsage.Account("a", "claude", "", 82, Mint + 5400, null, null, 0, true)]).Single().Crossing is not null,
            },
        };

        // ---- done --------------------------------------------------------------
        var donePush = PushVector("cb316-done-eph", agreement.Public, RelayLink.DoneE(SessionId, "orbweaver", Machine));
        const string doneEnv = "01JCA0DONE0000000000000AB1";
        var delta = Encoding.UTF8.GetString(RelayRoster.DeltaJson(7, new RelayDelta(SessionId, RelayRoster.Idle, null, DateTimeOffset.FromUnixTimeSeconds(Mint + 30), false, RelayRoster.Generating)));
        var transitions = new[] { ("generating", "idle"), ("waiting", "idle"), ("idle", "generating"), ("generating", "waiting"), ("generating", "ended"), ("idle", "idle") };
        v["done"] = new JsonObject
        {
            ["delta_frame"] = With(s.Box(Desktop, Phone, "state", delta, ckSession, "done", donePush["e"]!.GetValue<string>()), ("envelope_id", doneEnv)),
            ["e"] = donePush,
            ["apns_body"] = Apns("done", "A session finished", "passive", doneEnv, ckSession, donePush["e"]!.GetValue<string>()),
            ["transitions"] = new JsonArray(transitions.Select(t => (JsonNode)new JsonObject
            {
                ["from"] = t.Item1, ["to"] = t.Item2,
                ["fires"] = new RelayDelta(SessionId, t.Item2, null, null, t.Item2 == RelayRoster.Ended, t.Item1 == t.Item2 ? null : t.Item1).Finished,
            }).ToArray()),
        };

        // ---- hello.push_mute (the relay's half; data, not desktop code) --------
        v["hello_push_mute"] = new JsonObject
        {
            ["frame"] = new JsonObject
            {
                ["t"] = "hello", ["device_id"] = Phone, ["token"] = "<token from /v1/register>", ["platform"] = "ios", ["app_version"] = "2.1.0",
                ["proto"] = 2, ["apns_token"] = "<64 to 200 lowercase hex characters>", ["push_mute"] = new JsonArray("done"),
            },
            ["accepted"] = new JsonArray(new JsonArray(), new JsonArray("done"), new JsonArray("limit"), new JsonArray("done", "limit")),
            ["refused_bad_hello"] = new JsonArray(new JsonArray("amber"), new JsonArray("resolve"), new JsonArray("done", "done"), new JsonArray("DONE"), "done"),
            ["_note"] = "phone-only; a desktop sending push_mute gets err bad_hello like apns_token. A muted class still forwards the envelope and answers ok push_muted.",
        };

        return PyJson.Pretty(v) + "\n";
    }

    // ---- fixture: already-parsed lines (make_v4_vectors.py fixture_lines) -------

    private static List<(string Role, string Text, long At, bool Error)> FixtureLines()
    {
        string[] roles = ["user", "assistant", "system"];
        var lines = new List<(string, string, long, bool)>();
        for (var i = 0; i < 230; i++)
        {
            var role = roles[i % 3];
            var text = i switch
            {
                7 => "Run `dotnet test \"tests/UnitTests\"` and tell me what's red \u2014 paths use C:\\Users\\w too",
                8 => "Résumé: 3 échecs, 1 avertissement · 日本語の行 · 🙂 fin.",
                61 => "Bash: dotnet build (exit 1)",
                150 => string.Concat(Enumerable.Repeat($"tool output line {i} ", 420)),
                151 or 152 => string.Concat(Enumerable.Repeat($"tool output line {i} ", 260)),
                215 => new string('λ', 4100),
                216 => new string('x', 8000),
                217 => new string('x', 7998) + "é",
                _ => role switch
                {
                    "user" => $"User line {i}: please look at the failing test.",
                    "assistant" => $"Assistant line {i}: reading the test now.",
                    _ => $"Read: tests/UnitTests/File{i}.cs",
                },
            };
            lines.Add((role, text, T0 + 7 * i, i == 61));
        }
        return lines;
    }

    // A restated tail (make_v4_vectors.py restated_boxes): the rows last sent,
    // the rows now, and the windows the desk's Diff and Tail produce.
    private static JsonObject Restated(Sealer s, string ck, List<TranscriptWatch.WireLine> prev,
        List<(string Role, string Text, long At, bool Error)> newTail, List<TranscriptWatch.WireLine> wire)
    {
        var now = wire.Concat(newTail.Select(a => TranscriptWatch.Line(a.Role, a.Text, a.At))).ToList();
        var change = TranscriptWatch.Diff(prev.Count, prev[0], now, prev[^1]);
        var start = TranscriptWatch.Start(change, prev.Count)
            ?? throw new InvalidOperationException("a restated case must be a tail, was " + change);
        var tailFrom = start - wire.Count;
        var arr = new JsonArray();
        foreach (var w in TranscriptWatch.Tail(SessionId, now, start))
        {
            var doc = JsonDocument.Parse(w).RootElement;
            arr.Add(With(s.Box(Desktop, Phone, "chat", w, ck),
                ("from_line", doc.GetProperty("from_line").GetInt32()), ("line_count", doc.GetProperty("lines").GetArrayLength())));
        }
        return new JsonObject
        {
            ["prev_count"] = prev.Count, ["new_count"] = now.Count,
            ["changed_row"] = LineNode(newTail[tailFrom]),
            ["appended"] = new JsonArray(newTail.Skip(tailFrom + 1).Select(LineNode).ToArray()),
            ["windows"] = arr,
        };
    }

    // make_v4_vectors.py diff_cases, decided by the desk's own Diff.
    private static JsonArray DiffCases()
    {
        (string Role, string Text, long At, bool Error) R(int i, string role = "user", string? text = null, long at = 0) =>
            (role, text ?? $"row {i}", T0 + at, false);
        List<(string Role, string Text, long At, bool Error)> b = [R(0), R(1, "assistant"), R(2, "system")];
        var cases = new (string Name, List<(string Role, string Text, long At, bool Error)> Prev, List<(string Role, string Text, long At, bool Error)> New)[]
        {
            ("unchanged", b, b),
            ("last-at-only-changed", b, [.. b.SkipLast(1), b[^1] with { At = T0 + 99 }]),
            ("appended-two", b, [.. b, R(3), R(4, "assistant")]),
            ("last-changed-in-place", b, [.. b.SkipLast(1), b[^1] with { Text = "row 2 streamed further" }]),
            ("last-role-changed", b, [.. b.SkipLast(1), b[^1] with { Role = "assistant" }]),
            ("last-changed-and-appended", b, [.. b.SkipLast(1), b[^1] with { Text = "row 2 done" }, R(3)]),
            ("shrank", b, b.Take(2).ToList()),
            ("first-text-changed", b, [b[0] with { Text = "rewritten" }, .. b.Skip(1)]),
            ("first-at-only-changed", b, [b[0] with { At = T0 + 5 }, .. b.Skip(1)]),
            ("first-at-changed-and-appended", b, [b[0] with { At = T0 + 5 }, .. b.Skip(1), R(3)]),
            ("empty-to-rows", [], b),
            ("rows-to-empty", b, []),
        };
        var arr = new JsonArray();
        foreach (var (name, prev, now) in cases)
        {
            var p = prev.Select(x => TranscriptWatch.Line(x.Role, x.Text, x.At)).ToList();
            var n = now.Select(x => TranscriptWatch.Line(x.Role, x.Text, x.At)).ToList();
            var change = TranscriptWatch.Diff(p.Count, p.Count > 0 ? p[0] : null, n, p.Count > 0 ? p[^1] : null);
            var start = TranscriptWatch.Start(change, p.Count);
            arr.Add(new JsonObject
            {
                ["name"] = name,
                ["prev"] = new JsonArray(prev.Select(LineNode).ToArray()),
                ["new"] = new JsonArray(now.Select(LineNode).ToArray()),
                ["expect"] = start is not null ? "tail" : change == TranscriptWatch.Change.Reset ? "reset" : "none",
                ["from_line"] = start,
                ["line_count"] = start is { } st ? n.Count - st : null,
            });
        }
        return arr;
    }

    private static JsonNode LineNode((string Role, string Text, long At, bool Error) l)
    {
        var o = new JsonObject { ["role"] = l.Role, ["text"] = l.Text, ["at"] = l.At };
        if (l.Error) o["error"] = true;
        return o;
    }

    private static JsonObject FieldsNode(LaunchFields f)
    {
        var o = new JsonObject { ["cwd"] = f.Cwd, ["cli"] = f.Cli, ["persona"] = f.Persona, ["model"] = f.Model };
        if (f.ParentSessionId.Length > 0) o["parent_session_id"] = f.ParentSessionId;
        o["first_message"] = f.FirstMessage;
        o["nonce"] = f.Nonce;
        o["expires_at"] = f.ExpiresAt;
        return o;
    }

    // The phone's launch frame, as the phone writes it (canonical; parent only
    // when set). The desktop never writes one; this is the vector's input.
    private static string LaunchFrame(LaunchFields f, string sig)
    {
        var j = new CanonicalJson().Str("t", "launch").Str("cwd", f.Cwd).Str("cli", f.Cli).Str("persona", f.Persona).Str("model", f.Model);
        if (f.ParentSessionId.Length > 0) j.Str("parent_session_id", f.ParentSessionId);
        return j.Str("first_message", f.FirstMessage).Str("nonce", f.Nonce).Num("expires_at", f.ExpiresAt).Str("sig", sig).ToString();
    }

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

    private static JsonObject Apns(string push, string body, string level, string envId, string ck, string e) => new()
    {
        ["aps"] = new JsonObject
        {
            ["alert"] = new JsonObject { ["title"] = "Orbweaver", ["body"] = body },
            ["mutable-content"] = 1, ["interruption-level"] = level, ["thread-id"] = Desktop + ":" + ck,
        },
        ["push"] = push, ["from"] = Desktop, ["to"] = Phone, ["id"] = envId, ["e"] = e,
    };

    private static JsonObject With(JsonObject o, params (string Key, JsonNode? Value)[] extra)
    {
        foreach (var (k, val) in extra) o[k] = val;
        return o;
    }

    // make_v4_vectors.py's Sealer: counters from 100 per direction, in call order.
    private sealed class Sealer(byte[] key)
    {
        private readonly ulong[] _counter = [100, 100];

        internal JsonObject Box(string from, string to, string cls, string plaintext, string ck = "", string push = "none", string? e = null)
        {
            var direction = from == Desktop ? DesktopToPhone : PhoneToDesktop;
            var nonce = Nonce(direction, 0, _counter[direction]++);
            var aad = Aad(from, to, cls);
            var o = new JsonObject
            {
                ["from"] = from, ["to"] = to, ["class"] = cls, ["push"] = push, ["ck"] = ck,
                ["plaintext"] = plaintext, ["plaintext_bytes"] = Encoding.UTF8.GetByteCount(plaintext),
                ["aad"] = Hex(aad), ["nonce"] = Hex(nonce), ["ct"] = Seal(key, nonce, Encoding.UTF8.GetBytes(plaintext), aad),
            };
            if (e is not null) o["e"] = e;
            return o;
        }
    }
}

// Python's json.dumps, byte for byte, for the two layouts the vectors use:
// indent=2 with ensure_ascii=False (the file), and compact with the default
// ensure_ascii=True (a library-escaped body, one signature negative).
internal static class PyJson
{
    internal static string Pretty(JsonNode node)
    {
        var sb = new StringBuilder();
        Write(sb, node, 0, pretty: true, ascii: false);
        return sb.ToString();
    }

    internal static string Compact(JsonNode node)
    {
        var sb = new StringBuilder();
        Write(sb, node, 0, pretty: false, ascii: true);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonNode? node, int depth, bool pretty, bool ascii)
    {
        string Pad(int d) => pretty ? "\n" + new string(' ', 2 * d) : "";
        switch (node)
        {
            case null:
                sb.Append("null");
                return;
            case JsonObject o:
                if (o.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                var first = true;
                foreach (var (k, v) in o)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(Pad(depth + 1)).Append(Str(k, ascii)).Append(pretty ? ": " : ":");
                    Write(sb, v, depth + 1, pretty, ascii);
                }
                sb.Append(Pad(depth)).Append('}');
                return;
            case JsonArray a:
                if (a.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Pad(depth + 1));
                    Write(sb, a[i], depth + 1, pretty, ascii);
                }
                sb.Append(Pad(depth)).Append(']');
                return;
            case JsonValue v:
                if (v.TryGetValue<string>(out var str)) sb.Append(Str(str, ascii));
                else if (v.TryGetValue<bool>(out var b)) sb.Append(b ? "true" : "false");
                else if (v.TryGetValue<int>(out var i32)) sb.Append(i32.ToString(CultureInfo.InvariantCulture));
                else if (v.TryGetValue<long>(out var i64)) sb.Append(i64.ToString(CultureInfo.InvariantCulture));
                else if (v.TryGetValue<double>(out var d)) sb.Append(Float(d));
                else if (v.TryGetValue<JsonElement>(out var el)) sb.Append(el.ValueKind switch
                {
                    JsonValueKind.String => Str(el.GetString()!, ascii),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => "null",
                    _ => el.GetRawText(),
                });
                else throw new InvalidOperationException("unsupported JSON value");
                return;
        }
    }

    // repr(float): shortest round trip, with ".0" on an integral value.
    private static string Float(double d)
    {
        var r = d.ToString("R", CultureInfo.InvariantCulture);
        return r.Contains('.') || r.Contains('E') ? r : r + ".0";
    }

    private static string Str(string s, bool ascii)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || (ascii && c > 0x7E && c != 0x7F)) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
