using System.Diagnostics.CodeAnalysis;

namespace Orbweaver
{
    // Semantic Versioning 2.0 parse and compare, for deciding whether a release
    // is newer than the running build (CB-308).
    //
    // Written rather than borrowed because the obvious existing comparer is wrong
    // for this: NeuralSpeech.VersionOrder gets prerelease ordering wrong, and this
    // project ships both "1.0.3" and "0.5.11-beta", so the one thing the updater
    // must never do is offer a beta to someone on a release, or the reverse.
    // The rules that matter, all from semver.org §11:
    //   1.0.3 > 1.0.3-beta > 0.5.11-beta        (a prerelease sorts below its release)
    //   1.0.0-rc.10 > 1.0.0-rc.9                (numeric identifiers compare as numbers)
    //   1.0.0-1 < 1.0.0-alpha                   (numeric sorts below alphanumeric)
    //   1.0.0-alpha < 1.0.0-alpha.1             (more identifiers wins when the rest ties)
    // Build metadata ("+abc123", which AssemblyInformationalVersion carries) is
    // ignored, exactly as the spec says.
    internal sealed record UpdateVersion(
        long Major, long Minor, long Patch, IReadOnlyList<string> Prerelease)
    {
        // Accepts an optional leading "v" because release tags carry one.
        internal static bool TryParse(string? text, [NotNullWhen(true)] out UpdateVersion? version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var s = text.Trim();
            if (s[0] is 'v' or 'V') s = s[1..];

            var plus = s.IndexOf('+');
            if (plus >= 0) s = s[..plus];

            var dash = s.IndexOf('-');
            var core = dash < 0 ? s : s[..dash];
            var pre = dash < 0 ? null : s[(dash + 1)..];

            var parts = core.Split('.');
            if (parts.Length != 3
                || !long.TryParse(parts[0], out var major) || major < 0
                || !long.TryParse(parts[1], out var minor) || minor < 0
                || !long.TryParse(parts[2], out var patch) || patch < 0
                || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit)))
                return false;

            var ids = Array.Empty<string>();
            if (pre is not null)
            {
                ids = pre.Split('.');
                if (ids.Any(id => id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
                    return false;
            }

            version = new UpdateVersion(major, minor, patch, ids);
            return true;
        }

        // Negative when a is older than b. Total order, so Math.Sign-safe.
        internal static int Compare(UpdateVersion a, UpdateVersion b)
        {
            var c = a.Major.CompareTo(b.Major);
            if (c != 0) return c;
            c = a.Minor.CompareTo(b.Minor);
            if (c != 0) return c;
            c = a.Patch.CompareTo(b.Patch);
            if (c != 0) return c;

            var ap = a.Prerelease;
            var bp = b.Prerelease;
            if (ap.Count == 0 || bp.Count == 0)
                return ap.Count == bp.Count ? 0 : ap.Count == 0 ? 1 : -1;

            for (var i = 0; i < Math.Min(ap.Count, bp.Count); i++)
            {
                c = CompareIdentifier(ap[i], bp[i]);
                if (c != 0) return c;
            }
            return ap.Count.CompareTo(bp.Count);
        }

        private static int CompareIdentifier(string a, string b)
        {
            var aNum = a.All(char.IsAsciiDigit);
            var bNum = b.All(char.IsAsciiDigit);
            if (aNum && bNum)
            {
                // Digit strings of any length, without overflowing: strip leading
                // zeros, then a longer number is larger, equal lengths compare as text.
                var x = a.TrimStart('0');
                var y = b.TrimStart('0');
                return x.Length != y.Length
                    ? x.Length.CompareTo(y.Length)
                    : string.CompareOrdinal(x, y);
            }
            if (aNum) return -1;
            if (bNum) return 1;
            return string.CompareOrdinal(a, b);
        }
    }
}
