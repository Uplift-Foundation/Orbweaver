namespace Orbweaver
{
    // CB-281: the pure classifier behind "Auto-group orbs by". See docs/cb281-plan.md
    // section 2. Everything the classifier may look at is handed in; it reads no
    // disk, no settings and no clock, so each rule is a case in a test.
    //
    // CONTRACTS COMMIT: signatures are final, Classify's body lands in the
    // implementation commit that follows.

    /// <summary>
    /// Everything the classifier may look at for one orb, gathered by the caller.
    /// <paramref name="RepoRoot"/> is the git repository root of <paramref name="Cwd"/>
    /// when the caller resolved one (PM decision: Directory groups by repo root, so
    /// worktrees fold into their main repo), null meaning "not in a repository, use the cwd".
    /// </summary>
    internal readonly record struct AutoGroupFacts(
        SessionSource Source, string RemoteCli, string Cwd,
        string? ConfigRoot,
        string? AccountKey,
        string? AccountLabel,
        string? RepoRoot = null);

    internal readonly record struct AutoGroupValue(string Key, string Name);

    internal static partial class OrbAutoGroups
    {
        /// <summary>null = this orb has no value for this attribute (or by == Off): not auto-grouped.</summary>
        internal static AutoGroupValue? Classify(AutoGroupBy by, AutoGroupFacts facts, string home) => by switch
        {
            AutoGroupBy.Cli => ClassifyCli(facts),
            AutoGroupBy.Directory => ClassifyDirectory(facts),
            AutoGroupBy.Profile => ClassifyProfile(facts, home),
            AutoGroupBy.Account => ClassifyAccount(facts),
            _ => null
        };

        private static bool IsClaude(SessionSource source) =>
            source == SessionSource.ClaudeCode || source == SessionSource.ClaudeCloud;

        // The CLI's own name. ClaudeCloud is Claude. A RemoteControl orb is
        // whichever CLI it mirrors, and one the scan could not name is Claude,
        // the default the scan itself uses.
        private static AutoGroupValue ClassifyCli(AutoGroupFacts facts)
        {
            var key = facts.Source switch
            {
                SessionSource.Codex => "codex",
                SessionSource.Grok => "grok",
                SessionSource.OpenClaw => "openclaw",
                // CB-421: a Paperclip company groups as "Paperclip" beside the
                // CLIs, not as "Claude" by default, which is what `_` would say.
                SessionSource.Paperclip => "paperclip",
                SessionSource.RemoteControl => (facts.RemoteCli ?? "").Trim().ToLowerInvariant() switch
                {
                    "codex" => "codex",
                    "grok" => "grok",
                    _ => "claude"
                },
                _ => "claude"
            };

            var name = key switch { "codex" => "Codex", "grok" => "Grok", "openclaw" => "OpenClaw", "paperclip" => "Paperclip", _ => "Claude" };
            return new AutoGroupValue(key, name);
        }

        // The repository root when the caller found one, else the cwd itself.
        // Empty on both is "no value": OpenClaw, remote and cloud orbs have no cwd.
        private static AutoGroupValue? ClassifyDirectory(AutoGroupFacts facts)
        {
            var path = !string.IsNullOrWhiteSpace(facts.RepoRoot) ? facts.RepoRoot : facts.Cwd;
            var tidy = Tidy(path);
            return tidy.Length == 0 ? null : new AutoGroupValue(tidy.ToLowerInvariant(), LeafOf(tidy));
        }

        // A Claude config root. `default` for <home>/.claude, otherwise the
        // directory's own label (.claude-board is "board"). Claude only.
        private static AutoGroupValue? ClassifyProfile(AutoGroupFacts facts, string home)
        {
            if (!IsClaude(facts.Source)) return null;
            var key = NormalizePath(facts.ConfigRoot);
            if (key.Length == 0) return null;

            var isDefault = key == NormalizePath(home + "/.claude");
            return new AutoGroupValue(key, isDefault ? "default" : UsageAccounts.FallbackLabel(facts.ConfigRoot));
        }

        // The account key stays the identity (it may hold a full email and only
        // ever goes into the hash); the name is the label the usage orbs show.
        private static AutoGroupValue? ClassifyAccount(AutoGroupFacts facts) =>
            string.IsNullOrWhiteSpace(facts.AccountKey)
                ? null
                : new AutoGroupValue(
                    facts.AccountKey,
                    string.IsNullOrWhiteSpace(facts.AccountLabel) ? "account" : facts.AccountLabel);

        // One spelling per place: forward slashes, no trailing separator, lower
        // case. The membership map is OrdinalIgnoreCase for the same reason, and
        // Windows paths arrive with either slash. Folding case is right because
        // both shipping platforms (Windows, macOS) default to case-insensitive
        // filesystems; on a case-sensitive one (Linux/WSL) /Repo and /repo would
        // wrongly share a group.
        internal static string NormalizePath(string? path) => Tidy(path).ToLowerInvariant();

        // Same without the case fold, for a name that is shown to the user.
        private static string Tidy(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var text = path.Trim().Replace('\\', '/');
            while (text.Length > 1 && text.EndsWith('/')) text = text[..^1];
            return text;
        }

        // The last segment of a normalised path; a bare root ("/" or "c:") names itself.
        internal static string LeafOf(string normalised)
        {
            var cut = normalised.LastIndexOf('/');
            var leaf = cut < 0 ? normalised : normalised[(cut + 1)..];
            return leaf.Length == 0 ? normalised : leaf;
        }
    }
}
