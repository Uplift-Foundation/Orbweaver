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
            _read = read ?? ReadFile;
        }

        private static string? ReadFile(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        /// <summary>
        /// The account's home for a local session: the Claude config root (the parent of
        /// <c>projects</c>, via <see cref="ClaudeConfigRoots.RootOfTranscript"/>), or for Codex and
        /// Grok the home that owns the rollout. Both layouts are the documented ones
        /// (<c>~/.codex/sessions/yyyy/mm/dd/rollout-*.jsonl</c>,
        /// <c>~/.grok/sessions/&lt;cwd&gt;/&lt;id&gt;/updates.jsonl</c>): the file is four folders under
        /// the home. ASSUMED, not run: that a session's TranscriptPath is that file. A path of any
        /// other shape, or any other CLI, gives null, which reads as "unknown".
        /// </summary>
        internal static string? HomeOf(SessionSource source, string? transcriptPath) => source switch
        {
            SessionSource.ClaudeCode => ClaudeConfigRoots.RootOfTranscript(transcriptPath),
            SessionSource.Codex or SessionSource.Grok => HomeUnderSessions(transcriptPath),
            _ => null
        };

        private static string? HomeUnderSessions(string? transcriptPath)
        {
            if (string.IsNullOrWhiteSpace(transcriptPath)) return null;

            var parts = transcriptPath.Split('/', '\\');
            // [... home..., "sessions", a, b, c, file]
            var at = parts.Length - 5;
            if (at < 1 || parts[at] != "sessions") return null;

            var home = string.Join(transcriptPath.Contains('\\') && !transcriptPath.Contains('/') ? "\\" : "/", parts[..at]);
            return home.Length == 0 ? null : home;
        }

        /// <summary>
        /// The account's identity and the name to show, or null when it cannot be told: no home,
        /// a CLI with no account of its own (OpenClaw, remote), or a Claude profile that has never
        /// logged in. The key holds the full email for Claude (it only ever goes into a hash); Codex
        /// and Grok key on the label their auth file gives, which is the email's local part, so two
        /// accounts sharing a local part would share a group (named in the PR).
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

        private (string Key, string Label)? Read(SessionSource source, string home)
        {
            switch (source)
            {
                case SessionSource.ClaudeCode:
                {
                    // <home>/.claude is the default account, whose identity file is
                    // ~/.claude.json, a sibling (CB-42), not one inside it.
                    var isDefault = OrbAutoGroups.NormalizePath(home) == OrbAutoGroups.NormalizePath(_home + "/.claude");
                    var json = _read(UsageAccounts.AccountFilePath(_home, isDefault ? null : home));
                    return UsageAccounts.EmailFrom(json) is { } email
                        ? ("claude\n" + email.ToLowerInvariant(), UsageAccounts.LabelFrom(json, home))
                        : null;
                }
                case SessionSource.Codex:
                {
                    var json = _read(CodexUsageAccounts.AuthFilePath(home));
                    return json is null ? null : ("codex\n" + CodexUsageAccounts.LabelFrom(json, home).ToLowerInvariant(),
                                                  CodexUsageAccounts.LabelFrom(json, home));
                }
                default:
                {
                    var json = _read(GrokUsageAccounts.AuthFilePath(home));
                    return json is null ? null : ("grok\n" + GrokUsageAccounts.LabelFrom(json, home).ToLowerInvariant(),
                                                  GrokUsageAccounts.LabelFrom(json, home));
                }
            }
        }
    }
}
