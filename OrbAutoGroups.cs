using System.Security.Cryptography;
using System.Text;

namespace Orbweaver
{
    // CB-281: the pure classifier behind "Auto-group orbs by". See docs/cb281-plan.md
    // section 2. Everything the classifier may look at is handed in; it reads no
    // disk, no settings and no clock, so each rule is a case in a test.
    //
    // CONTRACTS COMMIT: signatures are final, Classify's body lands in the
    // implementation commit that follows.

    /// <summary>What the orbs are grouped by. <see cref="Off"/> is the default.</summary>
    internal enum AutoGroupBy { Off, Directory, Cli, Profile, Account }

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

    internal static class OrbAutoGroups
    {
        /// <summary>null = this orb has no value for this attribute (or by == Off): not auto-grouped.</summary>
        internal static AutoGroupValue? Classify(AutoGroupBy by, AutoGroupFacts facts, string home) =>
            throw new NotImplementedException("CB-281: lands in the implementation commit");

        /// <summary>"a-dir-1a2b3c4d5e6f": attribute plus the first 6 bytes of SHA-256 over the key.</summary>
        internal static string IdFor(AutoGroupBy by, string key)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            var attribute = by == AutoGroupBy.Directory ? "dir" : Name(by);
            return "a-" + attribute + "-" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        }

        internal static bool IsAuto(string? id) => id is not null && id.StartsWith("a-", StringComparison.Ordinal);

        /// <summary>The attribute encoded in an auto id, or null for anything else.</summary>
        internal static AutoGroupBy? AttributeOf(string? id)
        {
            if (!IsAuto(id)) return null;
            var parts = id!.Split('-');
            if (parts.Length != 3) return null;
            return parts[1] switch
            {
                "dir" => AutoGroupBy.Directory,
                "cli" => AutoGroupBy.Cli,
                "profile" => AutoGroupBy.Profile,
                "account" => AutoGroupBy.Account,
                _ => null
            };
        }

        /// <summary>Settings wire format: "off" "directory" "cli" "profile" "account".</summary>
        internal static string Name(AutoGroupBy by) => by switch
        {
            AutoGroupBy.Directory => "directory",
            AutoGroupBy.Cli => "cli",
            AutoGroupBy.Profile => "profile",
            AutoGroupBy.Account => "account",
            _ => "off"
        };

        /// <summary>Unknown or missing text reads as Off.</summary>
        internal static AutoGroupBy Parse(string? value) =>
            (value ?? "").Trim().ToLowerInvariant() switch
            {
                "directory" => AutoGroupBy.Directory,
                "cli" => AutoGroupBy.Cli,
                "profile" => AutoGroupBy.Profile,
                "account" => AutoGroupBy.Account,
                _ => AutoGroupBy.Off
            };
    }
}
