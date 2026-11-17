using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orbweaver
{
    // CB-340 (frames-v12 account_id): the one value that says "this is the same
    // account" on every PC, so the phone can draw one usage card instead of one
    // per machine.
    //
    // Pure. Each reader takes the text of an identity file the poller already
    // reads for the label, and returns the account id or null. Nothing here
    // touches the disk, the clock or the network.
    //
    // **Never an email.** The id is the first 16 bytes of an HMAC over the CLI
    // and two provider ids (frames-v12 account_id.derivation), so a relay, a log
    // or a phone's cache never holds anything an attacker could look up. The key
    // is a fixed domain separator rather than a secret, because PCs share no
    // secret and the id has to come out equal on all of them; what keeps it from
    // being reversed is that the provider ids are random UUIDs, not that the key
    // is hidden. An id containing '@' is refused outright, so an email cannot
    // become an identity even through a reader bug.
    //
    // Conservative on purpose: every id that is available goes into the message,
    // so when a CLI offers less on one machine than another the failure is two
    // cards for one account, never one card for two.
    internal static class AccountIdentity
    {
        private static readonly byte[] Key = Encoding.UTF8.GetBytes("orbweaver/account-id/v1");

        internal const int HexLength = 32;

        // The frames-v12 derivation. id1 is required; id2 is the empty string
        // when absent. Returns null when there is nothing to derive from.
        internal static string? Derive(string cli, string? id1, string? id2)
        {
            var first = Clean(id1);
            if (first.Length == 0 || first.Contains('@')) return null;

            var message = Encoding.UTF8.GetBytes(cli + "\n" + first + "\n" + Clean(id2));
            return Convert.ToHexString(HMACSHA256.HashData(Key, message)).ToLowerInvariant()[..HexLength];
        }

        // The first six hex, which is all the desktop's log may say about an
        // account (frames-v12 desktop_rules.log).
        internal static string Short(string accountId) => accountId.Length <= 6 ? accountId : accountId[..6];

        // ASCII-trim and ASCII-lowercase, as the contract says, so a provider
        // that changes its id's case does not split an account in two.
        private static string Clean(string? id)
        {
            if (id is null) return "";
            var trimmed = id.Trim(' ', '\t', '\r', '\n', '\f', '\v');
            var chars = trimmed.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
                if (chars[i] is >= 'A' and <= 'Z') chars[i] = (char)(chars[i] + 32);
            return new string(chars);
        }

        // Claude: oauthAccount.accountUuid and organizationUuid out of
        // <config dir or home>/.claude.json. A profile that was never logged in
        // has no oauthAccount, so it has no id and is never merged.
        internal static string? Claude(string? json)
        {
            if (Parse(json) is not { } root
                || root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("oauthAccount", out var account)
                || account.ValueKind != JsonValueKind.Object)
                return null;

            return Derive("claude", Str(account, "accountUuid"), Str(account, "organizationUuid"));
        }

        // Codex: tokens.account_id, and the id_token's chatgpt_user_id claim.
        //
        // Read-only. CodexUsageAccounts.LabelFrom skips `tokens` entirely because
        // a second *writer* that loses a compare-and-swap on a rotating token
        // logs the user out; reading two identifier strings out of the file
        // touches no token's lifecycle, and nothing from `tokens` is logged,
        // stored or sent. access_token, refresh_token and OPENAI_API_KEY are
        // never read.
        internal static string? Codex(string? json)
        {
            if (Parse(json) is not { } root
                || root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tokens", out var tokens)
                || tokens.ValueKind != JsonValueKind.Object)
                return null;

            string? userId = null;
            if (Str(tokens, "id_token") is { } jwt) userId = ChatGptUserId(jwt);
            return Derive("codex", Str(tokens, "account_id"), userId);
        }

        // Grok: user_id and team_id out of the object-valued entry of
        // ~/.grok/auth.json that GrokUsageAccounts reads the email from (the
        // first object carrying a non-blank email).
        internal static string? Grok(string? json)
        {
            if (Parse(json) is not { } root || root.ValueKind != JsonValueKind.Object) return null;

            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                if (string.IsNullOrWhiteSpace(Str(property.Value, "email"))) continue;
                return Derive("grok", Str(property.Value, "user_id"), Str(property.Value, "team_id"));
            }

            return null;
        }

        // The chatgpt_user_id claim of a JWT's payload: the middle segment,
        // base64url, JSON. Looked for at the top level, then under the
        // "https://api.openai.com/auth" object, because the nesting has not been
        // confirmed (docs/account-identity-findings.md). Null for anything that
        // does not decode, which makes the id a different one rather than none.
        internal static string? ChatGptUserId(string jwt)
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2 || DecodeSegment(parts[1]) is not { } payload) return null;

            try
            {
                using var document = JsonDocument.Parse(payload);
                var claims = document.RootElement;
                if (claims.ValueKind != JsonValueKind.Object) return null;

                if (Str(claims, "chatgpt_user_id") is { } top) return top;
                if (claims.TryGetProperty("https://api.openai.com/auth", out var auth)
                    && auth.ValueKind == JsonValueKind.Object)
                    return Str(auth, "chatgpt_user_id");
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static byte[]? DecodeSegment(string segment)
        {
            var s = segment.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            try { return Convert.FromBase64String(s); }
            catch (FormatException) { return null; }
        }

        private static JsonElement? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                // Cloned so the element outlives the document it came from.
                using var document = JsonDocument.Parse(json);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? Str(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
