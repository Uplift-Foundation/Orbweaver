using System.Security.Cryptography;
using System.Text;

namespace Orbweaver
{
    // CB-281: the dependency-free half of the auto-group classifier: the attribute enum,
    // derived ids and the settings wire format. Kept apart from OrbAutoGroups.cs (which
    // needs SessionSource and UsageAccounts) because OrbGroupTable.cs and
    // OrbweaverSettings.cs name these, and tools/openclaw-probe compiles those two in by
    // file without the rest of the app.
    /// <summary>What the orbs are grouped by. <see cref="Off"/> is the default.</summary>
    internal enum AutoGroupBy { Off, Directory, Cli, Profile, Account }

    internal static partial class OrbAutoGroups
    {
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
