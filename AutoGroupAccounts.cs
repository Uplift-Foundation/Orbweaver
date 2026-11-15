namespace Orbweaver
{
    // CB-281: which account an orb's session runs under, for "Auto-group orbs by
    // Account". Held per (CLI, home) for a short while because a scan asks for
    // every orb, and answering reads a small identity file.
    //
    // HomeOf says where a session's account lives; Of reads who it is. The clock,
    // the file read and the home are injectable, so no test touches a real disk.
    internal sealed class AutoGroupAccounts
    {
        internal static readonly TimeSpan HeldFor = TimeSpan.FromSeconds(30);

        private readonly string _home;
        private readonly Func<DateTime> _now;
        private readonly Func<string, string?> _read;
        private readonly object _gate = new();
        private readonly Dictionary<string, (DateTime At, (string Key, string Label)? Account)> _held =
            new(StringComparer.OrdinalIgnoreCase);

        internal AutoGroupAccounts(string home, Func<DateTime>? now = null, Func<string, string?>? read = null)
        {
            _home = home;
            _now = now ?? (() => DateTime.UtcNow);
            _read = read ?? (path => ReadFile(path));
        }

        // `readAll` is a seam so the failure path is a test with a reader that throws,
        // rather than a permission trick that behaves differently per platform.
        internal static string? ReadFile(string path, Func<string, string>? readAll = null)
        {
            try { return File.Exists(path) ? (readAll ?? File.ReadAllText)(path) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        /// <summary>
        /// The account's home for a local session: the Claude config root (the parent of
        /// <c>projects</c>, via <see cref="ClaudeConfigRoots.RootOfTranscript"/>), or for Codex and
        /// Grok the home that owns the rollout. Both layouts are the documented ones
        /// (<c>~/.codex/sessions/yyyy/mm/dd/rollout-*.jsonl</c>,
        /// <c>~/.grok/sessions/&lt;cwd&gt;/&lt;id&gt;/updates.jsonl</c>): the file sits under the
        /// home's <c>sessions</c> folder at a fixed depth. ASSUMED, not run: that a session's TranscriptPath is that file. A path of any
        /// other shape, or any other CLI, gives null, which reads as "unknown".
        /// </summary>
        internal static string? HomeOf(SessionSource source, string? transcriptPath) => source switch
        {
            SessionSource.ClaudeCode => ClaudeConfigRoots.RootOfTranscript(transcriptPath),
            SessionSource.Codex => HomeUnderSessions(transcriptPath, 5),
            SessionSource.Grok => HomeUnderSessions(transcriptPath, 4),
            _ => null
        };

        // `fromEnd` is where the "sessions" segment sits counted from the file: Codex has
        // yyyy/mm/dd between it and the file (5), Grok has <cwd>/<id> (4).
        private static string? HomeUnderSessions(string? transcriptPath, int fromEnd)
        {
            if (string.IsNullOrWhiteSpace(transcriptPath)) return null;

            var parts = transcriptPath.Split('/', '\\');
            var at = parts.Length - fromEnd;
            if (at < 1 || parts[at] != "sessions") return null;

            var home = string.Join(transcriptPath.Contains('\\') && !transcriptPath.Contains('/') ? "\\" : "/", parts[..at]);
            return home.Length == 0 ? null : home;
        }

        /// <summary>
        /// The account's identity and the name to show, or null when it cannot be told: no home,
        /// a CLI with no account of its own (OpenClaw, remote), or a Claude profile that has never
        /// logged in. The key holds the full email for Claude (it only ever goes into a hash); Codex
        /// and Grok too, from their auth file. The name shown is the email's local part unless two
        /// accounts seen by this reader share it, when the full email is shown.
        /// </summary>
        internal (string Key, string Label)? Of(SessionSource source, string? home)
        {
            if (source is not (SessionSource.ClaudeCode or SessionSource.ClaudeCloud
                               or SessionSource.Codex or SessionSource.Grok)) return null;
            if (source == SessionSource.ClaudeCloud || string.IsNullOrWhiteSpace(home)) return null;

            var id = (int)source + "|" + home;
            var now = _now();
            lock (_gate)
                if (_held.TryGetValue(id, out var held) && now - held.At < HeldFor) return held.Account;

            var account = Read(source, home);
            lock (_gate) _held[id] = (now, account);
            return account;
        }

        // Which full emails have been seen under each short label, so a label two
        // accounts share ("a" for a@x.com and a@y.com) is shown as the full email.
        private readonly Dictionary<string, HashSet<string>> _byLabel = new(StringComparer.OrdinalIgnoreCase);

        private (string Key, string Label)? Read(SessionSource source, string home)
        {
            string? email, label;
            string cli;
            switch (source)
            {
                case SessionSource.ClaudeCode:
                {
                    // <home>/.claude is the default account, whose identity file is
                    // ~/.claude.json, a sibling (CB-42), not one inside it.
                    var isDefault = OrbAutoGroups.NormalizePath(home) == OrbAutoGroups.NormalizePath(_home + "/.claude");
                    var json = _read(UsageAccounts.AccountFilePath(_home, isDefault ? null : home));
                    cli = "claude";
                    email = UsageAccounts.EmailFrom(json);
                    label = UsageAccounts.LabelFrom(json, home);
                    break;
                }
                case SessionSource.Codex:
                {
                    var json = _read(CodexUsageAccounts.AuthFilePath(home));
                    cli = "codex";
                    email = AuthEmailFrom(json);
                    label = CodexUsageAccounts.LabelFrom(json, home);
                    break;
                }
                default:
                {
                    var json = _read(GrokUsageAccounts.AuthFilePath(home));
                    cli = "grok";
                    email = AuthEmailFrom(json);
                    label = GrokUsageAccounts.LabelFrom(json, home);
                    break;
                }
            }

            if (email is null) return null;

            // The key is the full, lower-cased email: a@x.com and a@y.com are
            // different accounts. It only ever goes into the id hash.
            var key = cli + "\n" + email.ToLowerInvariant();
            lock (_gate)
            {
                if (!_byLabel.TryGetValue(label, out var keys)) _byLabel[label] = keys = new(StringComparer.Ordinal);
                keys.Add(key);
                return (key, keys.Count > 1 ? email : label);
            }
        }

        // The email in a Codex or Grok auth.json: a top-level "email", or one inside a
        // top-level object (Grok keeps it there). Never the tokens or the API key.
        internal static string? AuthEmailFrom(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("tokens") || property.NameEquals("OPENAI_API_KEY")) continue;

                    var holder = property.Value;
                    string? text = null;
                    if (property.NameEquals("email") && holder.ValueKind == System.Text.Json.JsonValueKind.String)
                        text = holder.GetString();
                    else if (holder.ValueKind == System.Text.Json.JsonValueKind.Object
                             && holder.TryGetProperty("email", out var inner)
                             && inner.ValueKind == System.Text.Json.JsonValueKind.String)
                        text = inner.GetString();

                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
            }
            catch (System.Text.Json.JsonException) { }

            return null;
        }
    }
}
