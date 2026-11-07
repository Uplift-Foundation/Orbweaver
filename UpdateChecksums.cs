using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Orbweaver
{
    // SHA256SUMS.txt, as `sha256sum *.dmg *.exe *.zip` writes it in release.yml:
    // "<64 hex>  <name>" — two spaces in text mode, space and "*" in binary mode —
    // one line per file, LF endings (CB-308). Parsed strictly: a line that is not
    // that shape means the file is not what it claims, and the updater would
    // rather refuse than guess which hash belongs to which name.
    internal static partial class UpdateChecksums
    {
        [GeneratedRegex(@"^([0-9A-Fa-f]{64}) [ *](.+)$")]
        private static partial Regex Line();

        // Name -> lowercase hex. Blank lines are skipped, CRLF is tolerated (a
        // hand-edited or proxied copy), names are kept exactly, and the same name
        // twice with different hashes is rejected as ambiguous.
        internal static IReadOnlyDictionary<string, string> Parse(string text)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;

                var m = Line().Match(line);
                if (!m.Success)
                    throw new UpdateException(UpdateFailure.ChecksumUnavailable,
                        $"{UpdateAssets.ChecksumsName} is malformed; refusing to trust it.");

                var name = m.Groups[2].Value;
                var hash = m.Groups[1].Value.ToLowerInvariant();
                if (map.TryGetValue(name, out var seen) && seen != hash)
                    throw new UpdateException(UpdateFailure.ChecksumUnavailable,
                        $"{UpdateAssets.ChecksumsName} lists {name} twice with different hashes.");
                map[name] = hash;
            }
            return map;
        }

        // The published hash for one file, or throws: a missing entry is a failure,
        // never "nothing to compare, so fine".
        internal static string Expect(string text, string name)
        {
            if (Parse(text).TryGetValue(name, out var hash)) return hash;
            throw new UpdateException(UpdateFailure.ChecksumUnavailable,
                $"{UpdateAssets.ChecksumsName} has no entry for {name}.");
        }

        // Streams the file through SHA-256 (installers are tens of MB; none of it
        // is held in memory) and compares case-insensitively.
        internal static async Task VerifyAsync(string path, string expectedHex, CancellationToken cancellationToken)
        {
            string actual;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));

            if (!string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException(UpdateFailure.ChecksumMismatch,
                    "The downloaded update does not match its published checksum, so it was discarded and nothing was installed.");
        }
    }
}
