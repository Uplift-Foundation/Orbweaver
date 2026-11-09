using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbweaver
{
    // The phone's signed `launch` (CB-316, frames-v4 sealed_plaintext.launch):
    // which checks it must pass, in the contract's check_order, before anything
    // is started. Pure — the filesystem, the CLI locator and the roster come in
    // as functions — so every refusal is a unit case and the vectors' signature
    // negatives replay against the shipped verifier.
    internal static class LaunchGate
    {
        internal const string Domain = "orbweaver-launch-v1";
        internal const int TtlSlackSeconds = 300;
        internal const int MaxModelLength = 64;

        internal static readonly string[] Clis = ["claude", "codex", "grok"];

        private static readonly Regex ModelShape = new("^[A-Za-z0-9][A-Za-z0-9._:-]*$", RegexOptions.CultureInvariant);
        private static readonly Regex WindowsAbsolute = new(@"^[A-Za-z]:\\", RegexOptions.CultureInvariant);

        // CB-316 (PM decision): a Codex or Grok first message reaches a Windows
        // desk in argv through conhost.exe, and conhost expands %NAME% as it
        // starts the CLI — measured: %PATH% arrived as the machine's PATH. There
        // is no escape for it, so the expandable shape is refused, defined or
        // not: checkable from the phone, and no environment value can leak. A
        // lone %, "50%" or "% done %" is not that shape and passes verbatim.
        private static readonly Regex Expandable = new("%[A-Za-z_][A-Za-z0-9_]*%", RegexOptions.CultureInvariant);

        internal static bool ExpandsOnWindows(string message) => Expandable.IsMatch(message);

        // UTF-8 of exactly {cwd,cli,persona,model,parent_session_id,
        // first_message_sha256,nonce,expires_at}, canonical, rebuilt from the
        // frame's own fields — so any other byte layout the phone signed fails
        // as bad_signature rather than being trusted.
        internal static string Body(LaunchFields f) =>
            new CanonicalJson().Str("cwd", f.Cwd).Str("cli", f.Cli).Str("persona", f.Persona).Str("model", f.Model)
                .Str("parent_session_id", f.ParentSessionId)
                .Str("first_message_sha256", RelayCrypto.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(f.FirstMessage))))
                .Str("nonce", f.Nonce).Num("expires_at", f.ExpiresAt).ToString();

        internal static byte[] Message(string body) => [.. Encoding.UTF8.GetBytes(Domain), .. Encoding.UTF8.GetBytes(body)];

        internal static bool Verify(byte[] phoneIdPublic, LaunchFields f, string sig) =>
            RelayCrypto.Verify(phoneIdPublic, Message(Body(f)), RelayCrypto.FromBase64Url(sig))
            && RelayCrypto.FromBase64Url(f.Nonce) is { Length: AnswerGate.NonceLength };

        // What the gate needs to know about this desktop.
        internal sealed record Environment(
            bool IsWindows,
            Func<string, bool> CliInstalled,
            Func<string, bool> DirectoryExists,
            Func<string, bool> InRoster);

        // The first failing check's code, or null when the launch may go ahead.
        // The nonce is recorded once the signature and expiry pass, as the
        // answer gate does: a replay of a launch refused for a later rule is
        // still a replay.
        internal static string? Decide(LaunchRequest r, string phoneId, byte[] phoneIdPublic, DateTimeOffset now,
            NonceWindow nonces, Environment env)
        {
            if (!Verify(phoneIdPublic, r.Fields, r.Sig)) return RelayReplies.BadSignature;
            return Rules(r.Fields, phoneId, now, nonces, env);
        }

        // Everything after the signature, in check_order.
        internal static string? Rules(LaunchFields f, string phoneId, DateTimeOffset now, NonceWindow nonces, Environment env)
        {
            var secs = now.ToUnixTimeSeconds();
            // A far-future expiry is a forged one; 300 s absorbs the Windows
            // box's ~1.5 s clock lead many times over.
            if (secs >= f.ExpiresAt || f.ExpiresAt > secs + TtlSlackSeconds) return RelayReplies.Expired;

            if (!nonces.TryAdd(phoneId, f.Nonce, now)) return RelayReplies.Replayed;

            if (Array.IndexOf(Clis, f.Cli) < 0 || !env.CliInstalled(f.Cli)) return RelayReplies.UnknownCli;
            if (!IsAbsolute(f.Cwd, env.IsWindows) || !env.DirectoryExists(f.Cwd)) return RelayReplies.UnknownCwd;
            if (!ModelOk(f.Model)) return RelayReplies.BadModel;
            if (f.Persona.Length > 0) return RelayReplies.UnsupportedPersona;
            if (f.ParentSessionId.Length > 0)
            {
                if (!env.InRoster(f.ParentSessionId)) return RelayReplies.UnknownParent;
                if (f.Cli != "claude") return RelayReplies.UnsupportedParent;
            }
            if (!RelayText.Typeable(f.FirstMessage, allowEmpty: true)) return RelayReplies.BadInput;
            if (env.IsWindows && f.Cli is "codex" or "grok" && ExpandsOnWindows(f.FirstMessage)) return RelayReplies.BadInput;
            return null;
        }

        // No fallback to the app's own directory (which the desk's New Chat
        // dialog has): a phone cannot see which folder it got.
        internal static bool IsAbsolute(string cwd, bool isWindows) =>
            isWindows ? WindowsAbsolute.IsMatch(cwd) : cwd.StartsWith('/');

        // "" is the CLI's default; anything else is passed to the CLI, which is
        // the only authority on its models, so only the charset is checked.
        internal static bool ModelOk(string model) =>
            model.Length == 0 || (model.Length <= MaxModelLength && ModelShape.IsMatch(model));

        internal static NewChatCli CliFor(string cli) => cli switch
        {
            "codex" => NewChatCli.Codex,
            "grok" => NewChatCli.Grok,
            _ => NewChatCli.ClaudeCode,
        };
    }
}
