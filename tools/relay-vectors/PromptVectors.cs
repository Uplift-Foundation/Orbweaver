using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Orbweaver.RelayCrypto;

namespace Orbweaver;

// CB-315's three contract files (plan #11248 §2, frames-v3.json
// sealed_plaintext): prompt-vectors.json, answer-vectors.json and
// push-vectors.json, out of RelayPrompt.cs — the code the desktop runs. Same
// rules as the CB-314 files beside them: every key is SHA-256 of a label, every
// nonce is fixed, so a rerun reproduces every byte except ECDSA signatures,
// which are randomized; a signature vector gives the message bytes and one
// valid signature to verify, and nobody byte-compares a signature.
internal static class PromptVectors
{
    private const string DesktopId = "dsk_5f3c9a0e1b7d2468";
    private const string PhoneId = "ph_a1b2c3d4e5f60718";
    private const string RequestId = "01JC9R0A5N2Q7W8X3E4T6Y9ZBD";
    private const string SessionId = "0f6c4f2a";
    private const long ExpiresAt = 1794113592;

    private static byte[] Label(string label) => SHA256.HashData(Encoding.UTF8.GetBytes("orbweaver-vector:" + label));
    private static JsonObject Pair(KeyPair k) => new() { ["private"] = Hex(k.Private), ["public"] = Hex(k.Public) };
    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    internal static void Write(string outDir, string sha, string fixturesDir)
    {
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var source = new JsonObject
        {
            ["generated_by"] = "Uplift-Foundation/Orbweaver tools/relay-vectors (RelayPrompt.cs, RelayCrypto.cs)",
            ["orbweaver_sha"] = sha,
            ["spec"] = "CB-315 architect plan #11248 rev 2 §2; Orbweaver-Mobile contracts/frames-v3.json sealed_plaintext",
            ["encoding"] = "hex is lowercase; ct is standard base64 with padding of nonce(12) || ciphertext || tag(16) as in session-vectors.json; " +
                           "screen_hash, nonce, sig and e are base64url without padding",
        };

        var dskKa = KeyPairFromPrivate(Label("desktop-agreement"));
        var phKa = KeyPairFromPrivate(Label("phone-agreement"));
        var phId = KeyPairFromPrivate(Label("phone-identity"));
        var epochKey = EpochKey(SessionBase(SharedSecret(dskKa, phKa.Public), DesktopId, PhoneId), 0);

        File.WriteAllText(Path.Combine(outDir, "prompt-vectors.json"),
            Prompts(source, epochKey, fixturesDir).ToJsonString(options) + "\n");
        File.WriteAllText(Path.Combine(outDir, "answer-vectors.json"),
            Answers(source, epochKey, phId).ToJsonString(options) + "\n");
        File.WriteAllText(Path.Combine(outDir, "push-vectors.json"),
            Pushes(source, phKa).ToJsonString(options) + "\n");
    }

    // ---- prompt-vectors.json ------------------------------------------------

    internal const string FramesV3Screen =
        "────────────────────────────────────────────────────────────\n" +
        " Bash command\n" +
        "\n" +
        "   dotnet test tests/UnitTests\n" +
        "   Run the unit tests\n" +
        "\n" +
        " Do you want to proceed?\n" +
        " ❯ 1. Yes\n" +
        "   2. Yes, and don't ask again for commands that start with dotnet test\n" +
        "   3. No, and tell Claude what to do differently\n" +
        "\n" +
        " Esc to cancel · Tab to amend\n";

    internal static List<PromptOption> OptionsOf(PromptFingerprint fp, string kind) =>
        fp.Prompt.Options.Select(o =>
        {
            var c = OptionKinds.Classify(kind, o.Label, o.Mode);
            return new PromptOption(o.Key, o.Label, c.Kind, c.Scope);
        }).ToList();

    private static JsonObject Fingerprint(string name, string screen, string? note = null)
    {
        var fp = PromptFingerprint.Of(screen);
        var j = new JsonObject { ["name"] = name, ["screen"] = screen };
        if (note is not null) j["_note"] = note;
        if (fp is null)
        {
            j["dialog"] = false;
            return j;
        }
        j["dialog"] = true;
        j["desk_only"] = FirstRunDialogs.Is(screen);
        j["canonical_text"] = fp.Canonical;
        j["canonical_text_utf8"] = Hex(Encoding.UTF8.GetBytes(fp.Canonical));
        j["screen_hash"] = fp.Hash;
        j["options"] = PromptFrames.OptionsJson(OptionsOf(fp, "permission"));
        return j;
    }

    private static JsonObject Sealed(byte[] key, string from, string to, uint direction, ulong counter, string plaintext)
    {
        var aad = Aad(from, to, "action");
        var nonce = Nonce(direction, 0, counter);
        return new JsonObject
        {
            ["from"] = from, ["to"] = to, ["class"] = "action",
            ["plaintext"] = plaintext,
            ["aad"] = Hex(aad),
            ["nonce"] = Hex(nonce),
            ["ct"] = Seal(key, nonce, Encoding.UTF8.GetBytes(plaintext), aad),
        };
    }

    // The CB-313 captures name a real account in their paths. The vectors go
    // to a repository that keeps those out (Orbweaver-Mobile's PR #16 scrub),
    // so the account segment of any home path becomes "someone" before the
    // screen is fingerprinted — the hash is then of the scrubbed screen, which
    // is what the vector carries.
    internal static string Scrub(string screen) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(screen, @"(?i)(C:[\\/]+Users[\\/]+)[^\\/\s`]+", "${1}someone"),
            @"/Users/(?!x+\b)[^/\s`]+", "/Users/someone");

    private static JsonObject Prompts(JsonObject source, byte[] epochKey, string fixturesDir)
    {
        string Fixture(string f) => Scrub(File.ReadAllText(Path.Combine(fixturesDir, f)).Replace("\r", ""));

        var moved = FramesV3Screen.Replace(" ❯ 1. Yes", "   1. Yes").Replace("   3. No, and", " ❯ 3. No, and");
        var changed = FramesV3Screen.Replace("dotnet test tests/UnitTests", "dotnet test tests/IntegrationTests");
        var tall = FramesV3Screen + string.Concat(Enumerable.Repeat("\n", 20));

        var fingerprints = new JsonArray
        {
            Fingerprint("frames-v3-example", FramesV3Screen, "the frames-v3.json prompt example, drawn as Claude Code draws it"),
            Fingerprint("cursor-moved", moved, "control: the ❯ cursor on option 3 instead of 1 — the same question, so the SAME screen_hash as frames-v3-example"),
            Fingerprint("blank-rows-below", tall, "control: twenty blank rows under the dialog — the SAME screen_hash as frames-v3-example"),
            Fingerprint("command-changed", changed, "negative: one word of the command changed — a DIFFERENT screen_hash; the gate refuses an answer to frames-v3-example against this screen (stale_prompt)"),
            Fingerprint("tmux-claude-prompt", Fixture("tmux-claude-prompt.txt"), "CB-313 fixture; option 3 is Claude Code's switch-to-auto-mode, kind mode"),
            Fingerprint("tmux-codex-prompt", Fixture("tmux-codex-prompt.txt"), "CB-313 fixture; option 2's scope is the backticked prefix"),
            Fingerprint("wezterm-codex-prompt-120", Fixture("wezterm-codex-prompt-120.txt"), "CB-313 fixture at 120 columns"),
            Fingerprint("wt-codex-prompt", Fixture("wt-codex-prompt.txt"), "CB-313 fixture, Windows Terminal; the dialog starts under the ◦/• Running line, which flips glyph while it waits and so is not hashed"),
            Fingerprint("tmux-claude-none", Fixture("tmux-claude-none.txt"), "no dialog on screen: no prompt is sent"),
            Fingerprint("conhost-claude-none", Fixture("conhost-claude-none.txt"), "Windows console at conhost's > idle prompt: no prompt is sent"),
            Fingerprint("tmux-claude-prompt-wrapped", Fixture("tmux-claude-prompt-wrapped.txt"), "option 2's label wraps at a hyphen; the parser rejoins it with no space"),
            Fingerprint("iterm2-claude-prompt", Fixture("iterm2-claude-prompt.txt"), "80 columns: option 2's label wraps and is rejoined with one space"),
            Fingerprint("wezterm-claude-prompt", Fixture("wezterm-claude-prompt.txt"), "80 columns: option 2's label wraps and is rejoined with one space"),
            Fingerprint("tmux-grok-prompt", Fixture("tmux-grok-prompt.txt"), "Grok on the Mac: N (○) label radio rows, the marks not part of the label; the always-approve option is kind mode; the live '22s' line above the frame is not hashed"),
            Fingerprint("iterm2-grok-prompt", Fixture("iterm2-grok-prompt.txt"), "Grok on the Mac, iTerm2"),
            Fingerprint("conhost-grok-prompt", Fixture("conhost-grok-prompt.txt"), "Grok on Windows: five options, Always allow: <cmd> (kind always, scope the command, wrapped) and Never allow"),
            Fingerprint("wt-grok-prompt", Fixture("wt-grok-prompt.txt"), "Grok on Windows Terminal"),
            Fingerprint("conhost-codex-updatedialog", Fixture("conhost-codex-updatedialog.txt"), "a first-run screen: it parses as a numbered dialog, so it is sent desk_only (options []) and nothing is pressable"),
            Fingerprint("wt-codex-hooksdialog", Fixture("wt-codex-hooksdialog.txt"), "a first-run screen, desk_only"),
            Fingerprint("wt-codex-trustdialog", Fixture("wt-codex-trustdialog.txt"), "a first-run screen, desk_only"),
        };

        var fp = PromptFingerprint.Of(FramesV3Screen)!;
        var opts = OptionsOf(fp, "permission");
        var preview = string.Join("\n", fp.Preview);
        var prompt = PromptFrames.Prompt(RequestId, SessionId, "permission", "claude", "Bash", preview, opts, fp.Hash, ExpiresAt);

        var resolved = new JsonArray();
        ulong counter = 1;
        foreach (var by in new[] { "desk", "phone:" + PhoneId, "gone" })
            resolved.Add(Sealed(epochKey, DesktopId, PhoneId, DesktopToPhone, counter++, PromptFrames.Resolved(RequestId, SessionId, by)));
        var refusals = new JsonArray();
        foreach (var code in AnswerGate.Codes)
            refusals.Add(Sealed(epochKey, DesktopId, PhoneId, DesktopToPhone, counter++, PromptFrames.Refusal(RequestId, SessionId, code)));

        return new JsonObject
        {
            ["_source"] = source.DeepClone(),
            ["_rules"] = "screen_hash = base64url(SHA-256(UTF-8(canonical_text))). canonical_text: the screen's lines with trailing blank rows dropped, " +
                         "each trimmed, frame edges │┃|║ stripped, trimmed again; from the dialog block's first line up to option 1 — the line after the nearest, walking up from option 1, of " +
                         "a full-width horizontal rule (─━═, 10+ wide), a line outside the ┃│║| frame option 1 sits in (when it sits in one), or a conversation line starting with one of •◦◆◇⏺●✻✶✳✢✽ (blank lines never bound it), " +
                         "never above the bottom 30 drawn lines — keeping only lines holding a letter or digit; then one line per parsed option " +
                         "\"{key}. {label}\" (wrapped labels joined by the parser, the ❯ cursor not part of it); joined with \\n, no trailing \\n. " +
                         "The phone never computes this: it echoes the prompt's screen_hash in its answer. The fixtures are here so every screen reader " +
                         "(surface C) and the desktop agree. Fixture cases are regenerated when surface C's wrapped-label join lands; the hash rule does not change. " +
                         "Envelope for the prompt: class action, push amber, id = request_id, e from push-vectors.json. Resolved: push resolve, id = request_id, no e. " +
                         "Refusals: class action, push none.",
            ["inputs"] = new JsonObject
            {
                ["desktop_id"] = DesktopId,
                ["phone_id"] = PhoneId,
                ["session_key_epoch0"] = Hex(epochKey),
                ["_note"] = "session_key_epoch0 is session-vectors.json's epoch 0 key (same inputs)",
            },
            ["fingerprints"] = fingerprints,
            ["prompt"] = Sealed(epochKey, DesktopId, PhoneId, DesktopToPhone, 0, prompt),
            ["resolved"] = resolved,
            ["refusals"] = refusals,
        };
    }

    // ---- answer-vectors.json ------------------------------------------------

    private sealed record AnswerCase(string Name, string Action, string Scope, string Note, string? Comment = null);

    private static JsonObject Answers(JsonObject source, byte[] epochKey, KeyPair phId)
    {
        var other = KeyPairFromPrivate(Label("phone-identity-other"));
        var nonceBytes = Label("answer-nonce")[..16];
        var nonce = Base64Url(nonceBytes);
        var hash = PromptFingerprint.Of(FramesV3Screen)!.Hash;

        var cases = new List<AnswerCase>
        {
            new("approve", "approve", "", ""),
            new("deny", "deny", "", ""),
            new("always", "always", "dotnet test", "", "scope is the pending option's exact prefix"),
            new("always-quote", "always", "echo \"hi\"", ""),
            new("always-backslash", "always", @"C:\Users\someone\cb313", ""),
            new("always-html-plus-slash", "always", "a<b&c'd+e/f", "", "< & ' + / are literal, never \\u003c or \\/"),
            new("always-non-ascii", "always", "touch ~/ノート 🙂 café", "", "non-ASCII is literal UTF-8, never \\uXXXX"),
            new("always-tab", "always", "printf a\tb", "", "a tab is \\u0009, never \\t"),
            new("always-control", "always", "x\u0001y\u001fz\u007f", "", "U+0001 and U+001F are \\u0001 and \\u001f (lowercase hex); DEL U+007F is literal"),
            new("tell-ascii", "tell", "", "Use the integration tests instead", "scope is the lowercase hex SHA-256 of the UTF-8 note"),
            new("tell-non-ascii", "tell", "", "Bitte nicht — run «ls» first 🙂"),
            new("option", "option:2", "", "", "a question's choice"),
        };

        var positives = new JsonArray();
        ulong counter = 0;
        foreach (var c in cases)
        {
            var scope = c.Action == "tell" ? AnswerSigning.TellScope(c.Note) : c.Scope;
            var body = AnswerSigning.Body(RequestId, SessionId, c.Action, scope, nonce, ExpiresAt);
            var msg = AnswerSigning.Message(body);
            var sig = Base64Url(Sign(phId, msg));
            var frame = AnswerFrame(c.Action, scope, c.Note, nonce, hash, sig);
            var j = new JsonObject
            {
                ["name"] = c.Name,
                ["action"] = c.Action,
                ["scope"] = scope,
                ["note"] = c.Note,
                ["body"] = body,
                ["body_utf8"] = Hex(Encoding.UTF8.GetBytes(body)),
                ["message"] = Hex(msg),
                ["sig"] = sig,
                ["answer_frame"] = Sealed(epochKey, PhoneId, DesktopId, PhoneToDesktop, counter++, frame),
            };
            if (c.Note.Length > 0) j["note_utf8"] = Hex(Encoding.UTF8.GetBytes(c.Note));
            if (c.Comment is not null) j["_note"] = c.Comment;
            positives.Add(j);
        }

        var approveBody = AnswerSigning.Body(RequestId, SessionId, "approve", "", nonce, ExpiresAt);
        var approveSig = Sign(phId, AnswerSigning.Message(approveBody));
        var spaced = approveBody.Replace(",", ", ").Replace(":", ": ");
        var flipped = (byte[])approveSig.Clone();
        flipped[10] ^= 0x01;
        var tellNote = "Use the integration tests instead";
        var tellUpper = AnswerSigning.TellScope(tellNote).ToUpperInvariant();
        var libraryEscaped = JsonSerializer.Serialize(new { request_id = RequestId, session_id = SessionId, action = "always", scope = "a<b&c'd+e/f", nonce, expires_at = ExpiresAt });

        JsonObject Neg(string name, string body, byte[] sig, string why) => new()
        {
            ["name"] = name,
            ["body"] = body,
            ["message"] = Hex(AnswerSigning.Message(body)),
            ["sig"] = Base64Url(sig),
            ["expect"] = "verify_fails_against_canonical",
            ["canonical_body"] = approveBody,
            ["_note"] = why,
        };

        var negatives = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "action-changed",
                ["body"] = approveBody.Replace("\"approve\"", "\"deny\""),
                ["sig"] = Base64Url(approveSig),
                ["expect"] = "verify_fails",
                ["_note"] = "the approve signature over a body that now says deny",
            },
            new JsonObject
            {
                ["name"] = "wrong-key",
                ["body"] = approveBody,
                ["sig"] = Base64Url(Sign(other, AnswerSigning.Message(approveBody))),
                ["signer_public"] = Hex(other.Public),
                ["expect"] = "verify_fails",
                ["_note"] = "signed by another phone's identity key; verify against inputs.phone_identity.public",
            },
            new JsonObject
            {
                ["name"] = "sig-bit-flipped",
                ["body"] = approveBody,
                ["sig"] = Base64Url(flipped),
                ["expect"] = "verify_fails",
            },
            new JsonObject
            {
                ["name"] = "no-domain-prefix",
                ["body"] = approveBody,
                ["sig"] = Base64Url(Sign(phId, Encoding.UTF8.GetBytes(approveBody))),
                ["expect"] = "verify_fails",
                ["_note"] = "signed over the body alone, without UTF-8(\"orbweaver-answer-v1\") in front",
            },
            Neg("whitespace-in-body", spaced, Sign(phId, AnswerSigning.Message(spaced)),
                "a valid signature over the body with spaces after , and : — it verifies over ITS bytes and fails over the canonical body the desktop rebuilds from the frame's fields"),
            Neg("library-escaped-body", libraryEscaped, Sign(phId, AnswerSigning.Message(libraryEscaped)),
                "System.Text.Json's default encoder writes < & ' + as \\u003C \\u0026 \\u0027 \\u002B; a signature over that fails over the canonical body (here canonical_body is the always-html-plus-slash case's body)"),
            new JsonObject
            {
                ["name"] = "tell-scope-uppercase",
                ["note"] = tellNote,
                ["scope"] = tellUpper,
                ["expect"] = "scope_mismatch",
                ["_note"] = "tell scope must be LOWERCASE hex; the desktop compares it to TellScope(note) byte for byte and refuses bad_signature",
            },
            new JsonObject
            {
                ["name"] = "tell-note-changed",
                ["note"] = tellNote + ".",
                ["scope"] = AnswerSigning.TellScope(tellNote),
                ["expect"] = "scope_mismatch",
                ["_note"] = "the note in the frame no longer hashes to the signed scope: refused bad_signature, so a relay cannot rewrite the reply text",
            },
        };
        // library-escaped's canonical body is the always-html-plus-slash body.
        negatives[5]!["canonical_body"] = AnswerSigning.Body(RequestId, SessionId, "always", "a<b&c'd+e/f", nonce, ExpiresAt);

        return new JsonObject
        {
            ["_source"] = source.DeepClone(),
            ["_rules"] = "body = UTF-8 of exactly {\"request_id\":S,\"session_id\":S,\"action\":S,\"scope\":S,\"nonce\":S,\"expires_at\":N}, keys in that order, no whitespace, " +
                         "N a bare decimal integer, every S escaped by the shared escaper: \" -> \\\", \\ -> \\\\, U+0000-U+001F -> \\u00xx (lowercase hex, no short forms), " +
                         "everything else (/, DEL, non-ASCII) literal UTF-8. message = UTF-8(\"orbweaver-answer-v1\") || body. sig = ECDSA P-256 SHA-256, raw r||s, 64 bytes, " +
                         "base64url, by the phone's identity key (pairing id_pub). scope: the exact prefix for always (must equal the pending option's scope), " +
                         "lowercase hex SHA-256 of the UTF-8 note for tell, empty otherwise. ECDSA is randomized: verify sig, never byte-compare it.",
            ["inputs"] = new JsonObject
            {
                ["desktop_id"] = DesktopId,
                ["phone_id"] = PhoneId,
                ["phone_identity"] = Pair(phId),
                ["request_id"] = RequestId,
                ["session_id"] = SessionId,
                ["nonce"] = nonce,
                ["nonce_raw"] = Hex(nonceBytes),
                ["expires_at"] = ExpiresAt,
                ["screen_hash"] = hash,
                ["session_key_epoch0"] = Hex(epochKey),
            },
            ["positives"] = positives,
            ["negatives"] = negatives,
        };
    }

    internal static string AnswerFrame(string action, string scope, string note, string nonce, string hash, string sig) =>
        new JsonObject
        {
            ["t"] = "answer",
            ["request_id"] = RequestId,
            ["session_id"] = SessionId,
            ["action"] = action,
            ["scope"] = scope,
            ["note"] = note,
            ["nonce"] = nonce,
            ["expires_at"] = ExpiresAt,
            ["screen_hash"] = hash,
            ["sig"] = sig,
        }.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    // ---- push-vectors.json --------------------------------------------------

    private static JsonObject Pushes(JsonObject source, KeyPair phKa)
    {
        var fp = PromptFingerprint.Of(FramesV3Screen)!;
        var opts = OptionsOf(fp, "permission");
        var preview = string.Join("\n", fp.Preview);
        const string label = "orbweaver on MacBook Pro";

        var longDetail = "dotnet test " + string.Concat(Enumerable.Range(0, 400).Select(i => $"tests/Suite{i:D3} "));
        var manyLong = Enumerable.Range(1, 14).Select(i =>
            new PromptOption(i.ToString(), $"Option {i}: " + string.Concat(Enumerable.Repeat("a long label word ", 20)), "option", null)).ToList();
        var nineHundred = Enumerable.Range(1, 9).Select(i =>
            new PromptOption(i.ToString(), $"Option {i}: " + new string('x', 110), "option", null)).ToList();
        var bigScope = new List<PromptOption>(nineHundred) { new("10", "Yes, and don't ask again for " + new string('s', 900), "always", new string('s', 2400)) };

        var cases = new (string Name, string Tool, string Summary, string Preview, List<PromptOption> Options, string Note)[]
        {
            ("frames-v3-example", "Bash", "Wants to run a command", preview, opts, "fits untouched"),
            ("non-ascii", "Bash", "Wants to run a command", "echo \"naïve café\" > ~/ノート.txt 🙂", opts, "non-ASCII stays literal in the plaintext (\\uXXXX would cost the budget)"),
            ("detail-cut", "Bash", "Wants to run a command", longDetail, manyLong.Take(5).ToList(), "detail cut to 512, then to the longest prefix that fits; labels untouched"),
            ("both-cuts", "Bash", "Needs an answer", longDetail, manyLong, "fits only after detail is cut AND every label over 120 characters is cut to 119 + …"),
            ("hopeless", "Bash", "Needs an answer", longDetail, bigScope, "does not fit even after both cuts, because a scope is never cut: no e is sent (the prompt still goes over the socket)"),
        };

        var positives = new JsonArray();
        var refused = new JsonArray();
        var n = 0;
        foreach (var c in cases)
        {
            var requestId = RequestId[..^1] + (char)('D' + n);
            var fitted = PromptFrames.FitPush(requestId, SessionId, label, c.Tool, c.Summary, c.Preview, ExpiresAt, c.Options, fp.Hash);
            var input = new JsonObject
            {
                ["request_id"] = requestId,
                ["session_id"] = SessionId,
                ["label"] = label,
                ["tool"] = c.Tool,
                ["summary"] = c.Summary,
                ["preview"] = c.Preview,
                ["expires_at"] = ExpiresAt,
                ["options"] = PromptFrames.OptionsJson(c.Options),
                ["screen_hash"] = fp.Hash,
            };
            if (fitted is null)
            {
                refused.Add(new JsonObject { ["name"] = c.Name, ["input"] = input, ["expect"] = "no_e", ["_note"] = c.Note });
                n++;
                continue;
            }
            var eph = KeyPairFromPrivate(Label("push-eph-" + c.Name));
            var nonce = Label("push-nonce-" + c.Name)[..NonceLength];
            var pt = Encoding.UTF8.GetBytes(fitted.Plaintext);
            var e = PushSeal.SealWith(eph, nonce, phKa.Public, pt);
            var ss = SharedSecret(eph, phKa.Public);
            positives.Add(new JsonObject
            {
                ["name"] = c.Name,
                ["_note"] = c.Note,
                ["input"] = input,
                ["detail_cut"] = fitted.DetailCut,
                ["labels_cut"] = fitted.LabelsCut,
                ["plaintext"] = fitted.Plaintext,
                ["plaintext_bytes"] = pt.Length,
                ["eph"] = Pair(eph),
                ["nonce"] = Hex(nonce),
                ["shared_secret"] = Hex(ss),
                ["key"] = Hex(PushSeal.Key(ss, eph.Public, phKa.Public)),
                ["header"] = Hex([PushSeal.Version, .. eph.Public]),
                ["info"] = Hex(PushSeal.Info(eph.Public, phKa.Public)),
                ["e"] = e,
                ["e_length"] = e.Length,
            });
            n++;
        }

        var good = positives[0]!["e"]!.GetValue<string>();
        var raw = FromBase64Url(good)!;
        byte[] Mut(Action<byte[]> f) { var c = (byte[])raw.Clone(); f(c); return c; }
        var other = KeyPairFromPrivate(Label("phone-agreement-other"));

        return new JsonObject
        {
            ["_source"] = source.DeepClone(),
            ["_rules"] = "CB-311's orbweaver-push-v1, unchanged (spikes/ios/vectors/push-v1.json still holds): ephemeral P-256 per push, " +
                         "shared = ECDH(eph, phone agreement key) raw x, key = HKDF-SHA256(shared, salt empty, info = \"orbweaver-push-v1\" || eph_pub || phone_pub, 32), " +
                         "header = 0x01 || eph_pub (66 bytes) as the AEAD's AAD, e = base64url(header || nonce(12) || ct || tag(16)). " +
                         "e is at most 3600 characters (frames-v3). Fitting (#11248 rev 2): detail = preview cut at 512; if e would exceed 3600, cut detail to the " +
                         "longest prefix that fits (cut text ends in …); if an empty detail still does not fit, cut every label over 120 characters to 119 + … and cut detail " +
                         "again; never drop an option, kind, scope or screen_hash; if it still does not fit, send no e. Lengths are UTF-16 code units, never splitting a surrogate pair.",
            ["inputs"] = new JsonObject { ["phone_agreement"] = Pair(phKa) },
            ["positives"] = positives,
            ["no_e"] = refused,
            ["negatives"] = new JsonArray
            {
                new JsonObject { ["name"] = "tag-flipped", ["e"] = Base64Url(Mut(b => b[^1] ^= 1)), ["expect"] = "open_fails" },
                new JsonObject { ["name"] = "ciphertext-flipped", ["e"] = Base64Url(Mut(b => b[PushSeal.HeaderLength + NonceLength] ^= 1)), ["expect"] = "open_fails" },
                new JsonObject { ["name"] = "header-eph-flipped", ["e"] = Base64Url(Mut(b => b[40] ^= 1)), ["expect"] = "open_fails", ["_note"] = "the header is AAD, and the flipped point is off the curve or a different key" },
                new JsonObject { ["name"] = "version-2", ["e"] = Base64Url(Mut(b => b[0] = 2)), ["expect"] = "open_fails" },
                new JsonObject { ["name"] = "truncated", ["e"] = Base64Url(raw[..(PushSeal.HeaderLength + NonceLength + TagLength - 1)]), ["expect"] = "open_fails" },
                new JsonObject { ["name"] = "other-phone", ["e"] = good, ["phone_agreement"] = Pair(other), ["expect"] = "open_fails", ["_note"] = "the frames-v3-example e, opened with another phone's key" },
            },
        };
    }
}
