using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Orbweaver
{
    // CB-315 surface B: the byte-level pieces of the amber loop that the phone
    // and the desktop must agree on exactly. Every function here is pure, so
    // tools/relay-vectors can write Orbweaver-Mobile's prompt-, answer- and
    // push-vectors.json straight out of the code the app runs, and the phone's
    // tests can then fail a build instead of a keypress. The frame shapes are
    // contracts/frames-v3.json's sealed_plaintext section.

    // What a dialog on screen *is*, for the purpose of deciding whether a
    // phone's answer still applies to it. The hash is over a canonical text,
    // not over the raw capture, because the raw capture changes for reasons
    // that do not change the question: the ❯ cursor moving between options, a
    // label wrapping differently, blank rows below the dialog. Anything that
    // does change the question — the command, the path, an option's wording,
    // a new option — changes the text and so the hash, and the gate refuses.
    //
    // The canonical text, agreed with surface C (the readers and parsers):
    //   1. ChatTranscript.ParseDialogCore's cleaned lines (ScreenLines, trimmed,
    //      frame edges │┃|║ stripped, trimmed again) and its option run.
    //   2. The span starts at the dialog block's first line (DialogStart:
    //      after the nearest horizontal rule, frame edge or conversation line
    //      above option 1), never above the dialog window's start.
    //   3. Lines from there up to option 1 that hold a letter or a digit, so
    //      blanks and the ╌ separators inside a dialog drop out.
    //   4. Then one line per parsed option, "{Key}. {Label}" — the parser's own
    //      label, which joins a wrapped label onto its option.
    //   5. Joined with \n, no trailing \n; screen_hash is base64url (no padding)
    //      of SHA-256 over its UTF-8.
    // A pane resized between the prompt and the answer can rewrap the preview
    // and so change the hash. That fails safe — stale_prompt — and is accepted.
    internal sealed record PromptFingerprint(ChatPrompt Prompt, IReadOnlyList<string> Preview, string Canonical, string Hash)
    {
        internal static PromptFingerprint? Of(string? screen)
        {
            if (string.IsNullOrEmpty(screen)) return null;
            var prompt = ChatTranscript.ParseDialogCore(screen, out var cleaned, out var firstOption);
            if (prompt is null || firstOption < 0) return null;

            var start = DialogStart(ChatTranscript.ScreenLines(screen), cleaned, firstOption);

            var preview = new List<string>();
            for (var i = start; i < firstOption; i++)
                if (cleaned[i].Any(char.IsLetterOrDigit)) preview.Add(cleaned[i]);

            var canonical = CanonicalText(preview, prompt.Options);
            return new PromptFingerprint(prompt, preview, canonical, HashOf(canonical));
        }

        // The first line of the dialog block above option 1: the line after the
        // nearest, walking up from option 1, of
        //   (a) a full-width horizontal rule — Claude Code draws one over its
        //       dialogs;
        //   (b) the edge of a ┃/│ frame option 1 sits in — Grok frames its
        //       dialog, and the line just above the frame carries a live "22s"
        //       timer that would otherwise change the hash every second;
        //   (c) a conversation line, one starting with a bullet or spinner glyph
        //       (• ◦ ◆ ⏺ ● ✻ …) — Codex draws its dialog straight under the
        //       transcript, and its "◦ Running …" line flips glyph while it waits.
        // Blank lines do not bound it: Codex leaves two blank rows inside its
        // dialog on one terminal and one on another (CB-313 tmux vs wt fixtures),
        // so a gap rule hashed the command on Windows and dropped it on the Mac.
        // Never above the dialog window's start.
        internal static int DialogStart(string[] raw, string[] cleaned, int firstOption)
        {
            var framed = IsFramed(raw[firstOption]);
            var floor = ChatTranscript.WindowStart(cleaned);
            for (var i = firstOption - 1; i >= floor; i--)
            {
                if (ChatTranscript.IsHorizontalRule(cleaned[i])
                    || (framed && !IsFramed(raw[i]))
                    || (cleaned[i].Length > 0 && ConversationGlyphs.Contains(cleaned[i][0])))
                    return i + 1;
            }
            return floor;
        }

        private const string ConversationGlyphs = "•◦◆◇⏺●✻✶✳✢✽";
        private const string FrameEdges = "┃│║|";

        private static bool IsFramed(string rawLine)
        {
            var t = rawLine.TrimStart();
            return t.Length > 0 && FrameEdges.Contains(t[0]);
        }

        internal static string CanonicalText(IEnumerable<string> preview, IEnumerable<ChatPromptOption> options) =>
            string.Join("\n", preview.Concat(options.Select(o => $"{o.Key}. {o.Label}")));

        internal static string HashOf(string canonical) =>
            RelayCrypto.Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    // frames-v3 options[].kind. Only approve, deny, always, tell and option are
    // answerable from a phone; never is carried but CB-315 sends nothing that
    // presses it, and mode is never pressable (rule 7).
    internal static class OptionKinds
    {
        internal const string Approve = "approve", Deny = "deny", Always = "always", Never = "never",
            Tell = "tell", Mode = "mode", Option = "option";

        // "…commands that start with `touch $HOME/x` (p)" (Codex) and
        // "…commands that start with dotnet test" (Claude Code): the prefix the
        // CLI will remember, which the phone shows and signs as scope.
        private static readonly Regex StartsWith =
            new(@"start(?:s)? with\s+`?(?<p>.+?)`?(?:\s*\([a-z]\))?\s*$", RegexOptions.CultureInvariant);

        // "…always allow access to /private/tmp/x from this project".
        private static readonly Regex AccessTo =
            new(@"allow access to\s+(?<p>.+?)(?:\s+from this project)?\s*$", RegexOptions.CultureInvariant);

        internal sealed record Classified(string Kind, string? Scope);

        // `mode` is the parser's ChatPromptOption.Mode (surface C), which it
        // sets from OptionModes — the one rule. The label is asked of that same
        // rule as well, so an option built without the flag still cannot come
        // out as approve.
        internal static Classified Classify(string kind, string label, bool modeFlag = false)
        {
            if (modeFlag || OptionModes.IsMode(label)) return new(Mode, null);
            if (kind != "permission") return new(Option, null);

            var l = label.Trim();
            // Grok's Windows auto-review dialog: "Always allow: <cmd>" and
            // "Never allow: <cmd>", the command being the scope it remembers.
            if (l.StartsWith("Always allow:", StringComparison.OrdinalIgnoreCase))
                return new(Always, l["Always allow:".Length..].Trim());
            if (l.StartsWith("Never allow:", StringComparison.OrdinalIgnoreCase)) return new(Never, null);
            if (l.StartsWith("No", StringComparison.OrdinalIgnoreCase))
            {
                if (l.Contains("tell", StringComparison.OrdinalIgnoreCase)) return new(Tell, null);
                if (l.Contains("don't ask again", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("never", StringComparison.OrdinalIgnoreCase)) return new(Never, null);
                return new(Deny, null);
            }
            if (l.StartsWith("Yes", StringComparison.OrdinalIgnoreCase))
            {
                if (l.Contains("don't ask again", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("always", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("this session", StringComparison.OrdinalIgnoreCase))
                    return new(Always, ScopeOf(l));
                return new(Approve, null);
            }
            return new(Option, null);
        }

        // The exact prefix when the label names one, otherwise the whole label:
        // either way the phone signs the text it showed, and rule 7 compares it
        // with what the fresh parse says.
        internal static string ScopeOf(string label)
        {
            var m = StartsWith.Match(label);
            if (m.Success) return m.Groups["p"].Value;
            m = AccessTo.Match(label);
            return m.Success ? m.Groups["p"].Value : label;
        }
    }

    // The answer frame's signed body and signature (frames-v3 answer.signed_body).
    internal static class AnswerSigning
    {
        internal const string Domain = "orbweaver-answer-v1";

        // The JSON string escaper both apps hand-write, so neither library's
        // encoder decides the bytes: \" and \\, U+0000–U+001F as \u00xx in
        // lowercase hex (no \n or \t short forms), and everything else — "/",
        // DEL, non-ASCII — literal UTF-8. Agreed with surface D (Celeste), who
        // measured that System.Text.Json and JSONSerialization each escape a
        // different set by default.
        internal static string Escape(string value)
        {
            var sb = new StringBuilder(value.Length + 2);
            foreach (var c in value)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20) sb.Append("\\u00").Append(((int)c).ToString("x2"));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        internal static string Body(string requestId, string sessionId, string action, string scope, string nonce, long expiresAt) =>
            $"{{\"request_id\":\"{Escape(requestId)}\",\"session_id\":\"{Escape(sessionId)}\"," +
            $"\"action\":\"{Escape(action)}\",\"scope\":\"{Escape(scope)}\",\"nonce\":\"{Escape(nonce)}\"," +
            $"\"expires_at\":{expiresAt.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";

        internal static byte[] Message(string body) => [.. Encoding.UTF8.GetBytes(Domain), .. Encoding.UTF8.GetBytes(body)];

        // tell's scope: the reply text bound into the signature without a new field.
        internal static string TellScope(string note) => RelayCrypto.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(note)));

        internal static bool Verify(byte[] phoneIdPublic, string body, string? sigBase64Url) =>
            RelayCrypto.Verify(phoneIdPublic, Message(body), RelayCrypto.FromBase64Url(sigBase64Url));
    }

    // The push ciphertext `e` (CB-311's orbweaver-push-v1, unchanged): a fresh
    // ephemeral P-256 key per push, ECDH to the phone's agreement key, HKDF-SHA256
    // (no salt, info = construction ‖ eph_pub ‖ phone_pub) to a single-use key,
    // ChaCha20-Poly1305 with header = 0x01 ‖ eph_pub as AAD, and the wire form
    // base64url(header ‖ nonce ‖ ct ‖ tag). Byte-compatible with the spike's
    // spikes/ios/vectors/push-v1.json, which the unit tests replay.
    internal static class PushSeal
    {
        internal const string Construction = "orbweaver-push-v1";
        internal const byte Version = 1;
        internal const int HeaderLength = 1 + RelayCrypto.PublicKeyLength;
        // frames-v3 relay_changes.envelope: the relay refuses a longer e.
        internal const int MaxE = 3600;

        internal static byte[] Info(byte[] ephPub, byte[] phonePub) =>
            [.. Encoding.ASCII.GetBytes(Construction), .. ephPub, .. phonePub];

        // Length of the sealed string for a plaintext of `n` bytes, without
        // sealing it: unpadded base64url of header + nonce + n + tag.
        internal static int SealedLength(int n)
        {
            var bytes = HeaderLength + RelayCrypto.NonceLength + n + RelayCrypto.TagLength;
            return (bytes * 4 + 2) / 3;
        }

        internal static string Seal(byte[] phonePublic, byte[] plaintext) =>
            SealWith(RelayCrypto.GenerateKeyPair(), RandomNumberGenerator.GetBytes(RelayCrypto.NonceLength), phonePublic, plaintext);

        // Fixed ephemeral key and nonce: vectors and tests only — a repeated
        // nonce under a repeated key is a break.
        internal static string SealWith(RelayCrypto.KeyPair eph, byte[] nonce, byte[] phonePublic, byte[] plaintext)
        {
            var key = Key(RelayCrypto.SharedSecret(eph, phonePublic), eph.Public, phonePublic);
            byte[] header = [Version, .. eph.Public];
            var ct = new byte[plaintext.Length];
            var tag = new byte[RelayCrypto.TagLength];
            using (var aead = new ChaCha20Poly1305(key)) aead.Encrypt(nonce, plaintext, ct, tag, header);
            return RelayCrypto.Base64Url([.. header, .. nonce, .. ct, .. tag]);
        }

        internal static byte[] Key(byte[] sharedSecret, byte[] ephPub, byte[] phonePub) =>
            HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, RelayCrypto.KeyLength, salt: [], info: Info(ephPub, phonePub));

        // What the phone's notification extension does; here so the tests and
        // the vectors' negatives can prove a seal opens and a tampered one does
        // not. Null on any refusal.
        internal static byte[]? Open(RelayCrypto.KeyPair phone, string? e)
        {
            var bytes = RelayCrypto.FromBase64Url(e);
            if (bytes is null || bytes.Length < HeaderLength + RelayCrypto.NonceLength + RelayCrypto.TagLength) return null;
            if (bytes[0] != Version) return null;
            var header = bytes[..HeaderLength];
            var ephPub = header[1..];
            if (!RelayCrypto.IsValidPublicKey(ephPub)) return null;
            var nonce = bytes[HeaderLength..(HeaderLength + RelayCrypto.NonceLength)];
            var ct = bytes[(HeaderLength + RelayCrypto.NonceLength)..^RelayCrypto.TagLength];
            var tag = bytes[^RelayCrypto.TagLength..];
            var key = Key(RelayCrypto.SharedSecret(phone, ephPub), ephPub, phone.Public);
            var pt = new byte[ct.Length];
            try
            {
                using var aead = new ChaCha20Poly1305(key);
                aead.Decrypt(nonce, ct, tag, pt, header);
                return pt;
            }
            catch (CryptographicException) { return null; }
        }
    }

    // One option as it travels in a prompt frame and in the push plaintext.
    // ScopeTruncated (the parser's ChatPromptOption.ScopeTruncated, #228): an
    // always option whose scope was cut at the screen's edge. The phone cannot
    // show what it would grant, and always outlives the prompt, so it is sent
    // greyed ("Choose at desk") and the gate never presses it.
    internal sealed record PromptOption(string Key, string Label, string Kind, string? Scope, bool ScopeTruncated = false);

    // The sealed prompt frame and the push plaintext, as JSON. The phone parses
    // these rather than byte-comparing them, so a library encoder is fine here;
    // the relaxed one keeps non-ASCII literal, which matters for the push
    // budget (\uXXXX would cost six bytes for every CJK character).
    internal static class PromptFrames
    {
        internal const int MaxPreview = 4000;
        internal const int MaxDetail = 512;
        internal const int MaxLabelAfterCut = 120;
        internal const int Lifetime = 1800;

        private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        internal static JsonArray OptionsJson(IEnumerable<PromptOption> options)
        {
            var a = new JsonArray();
            foreach (var o in options)
            {
                var j = new JsonObject { ["key"] = o.Key, ["label"] = o.Label, ["kind"] = o.Kind };
                if (o.Scope is not null) j["scope"] = o.Scope;
                if (o.ScopeTruncated) j["scope_truncated"] = true;
                a.Add(j);
            }
            return a;
        }

        internal static string Prompt(string requestId, string sessionId, string kind, string cli, string tool,
            string preview, IReadOnlyList<PromptOption> options, string screenHash, long expiresAt) =>
            new JsonObject
            {
                ["t"] = "prompt",
                ["request_id"] = requestId,
                ["session_id"] = sessionId,
                ["kind"] = kind,
                ["cli"] = cli,
                ["tool"] = tool,
                ["preview"] = Cut(preview, MaxPreview),
                ["options"] = OptionsJson(options),
                ["screen_hash"] = screenHash,
                ["expires_at"] = expiresAt,
            }.ToJsonString(Json);

        internal static string Resolved(string requestId, string sessionId, string by) =>
            new JsonObject { ["t"] = "resolved", ["request_id"] = requestId, ["session_id"] = sessionId, ["by"] = by }.ToJsonString(Json);

        internal static string Refusal(string requestId, string sessionId, string code) =>
            new JsonObject { ["t"] = "err", ["request_id"] = requestId, ["session_id"] = sessionId, ["code"] = code }.ToJsonString(Json);

        // The sealed resolve's plaintext (accepted from surface D): t tells it
        // from the amber plaintext, which has none. Small, so no fitting.
        internal static string ResolvedPush(string requestId, string sessionId, string by, string label) =>
            new JsonObject
            {
                ["v"] = 1,
                ["t"] = "resolved",
                ["request_id"] = requestId,
                ["session_id"] = sessionId,
                ["by"] = by,
                ["label"] = label,
            }.ToJsonString(Json);

        internal static string PushPlaintext(string requestId, string sessionId, string label, string tool, string summary,
            string detail, long expiresAt, IReadOnlyList<PromptOption> options, string screenHash) =>
            new JsonObject
            {
                ["v"] = 1,
                ["request_id"] = requestId,
                ["session_id"] = sessionId,
                ["label"] = label,
                ["tool"] = tool,
                ["summary"] = summary,
                ["detail"] = detail,
                ["expires_at"] = expiresAt,
                ["options"] = OptionsJson(options),
                ["screen_hash"] = screenHash,
            }.ToJsonString(Json);

        // #11248 rev 2's fitting rule for the e budget, deterministic so both
        // the vectors and a re-run agree:
        //   0. detail is the preview cut at 512 characters;
        //   1. if the sealed e would exceed 3600 characters, cut detail to the
        //      longest prefix that fits (ending in "…" when cut);
        //   2. if even an empty detail does not fit, cut every label longer than
        //      120 characters to 119 plus "…", then cut detail again as in 1;
        //   3. never drop an option, a kind, a scope or the hash — if it still
        //      does not fit, null: the prompt goes over the socket without e.
        // Returns the plaintext and which cuts were made.
        internal sealed record Fitted(string Plaintext, bool DetailCut, bool LabelsCut);

        internal static Fitted? FitPush(string requestId, string sessionId, string label, string tool, string summary,
            string preview, long expiresAt, IReadOnlyList<PromptOption> options, string screenHash)
        {
            var detail = Cut(preview, MaxDetail);
            string Build(string d, IReadOnlyList<PromptOption> opts) =>
                PushPlaintext(requestId, sessionId, label, tool, summary, d, expiresAt, opts, screenHash);
            bool Fits(string pt) => PushSeal.SealedLength(Encoding.UTF8.GetByteCount(pt)) <= PushSeal.MaxE;

            var full = Build(detail, options);
            if (Fits(full)) return new(full, false, false);

            var fitted = FitDetail(detail, d => Build(d, options), Fits);
            if (fitted is not null) return new(fitted, true, false);

            var cutOptions = options.Select(o => o with { Label = Cut(o.Label, MaxLabelAfterCut) }).ToList();
            // With the labels cut, detail may fit whole again, so whether it was
            // cut is read off the result rather than assumed.
            string? kept = null;
            fitted = FitDetail(detail, d => Build(d, cutOptions), Fits, d => kept = d);
            return fitted is null ? null : new(fitted, kept != detail, true);
        }

        // The longest prefix of detail (plus "…") that fits, by binary search
        // over the prefix length; "" when only an empty detail fits; null when
        // not even that does.
        private static string? FitDetail(string detail, Func<string, string> build, Func<string, bool> fits, Action<string>? chosen = null)
        {
            if (!fits(build(""))) return null;
            int lo = 0, hi = detail.Length;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (fits(build(Cut(detail, mid)))) lo = mid; else hi = mid - 1;
            }
            var d = Cut(detail, lo);
            chosen?.Invoke(d);
            return build(d);
        }

        // At most `max` UTF-16 units, the last being "…" when anything was cut,
        // and never half a surrogate pair. Cut(x, 0) is "".
        internal static string Cut(string text, int max)
        {
            if (text.Length <= max) return text;
            if (max <= 0) return "";
            var keep = max - 1;
            if (keep > 0 && char.IsHighSurrogate(text[keep - 1])) keep--;
            return text[..keep] + "…";
        }
    }
}
