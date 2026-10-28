using System.Text.RegularExpressions;

namespace Orbweaver
{
    // CB-287: the icon store for usage orbs. This first commit carries only the
    // file-name rule the resolver needs; import, load and delete land next.
    internal static class AccountOrbIcons
    {
        // The only shape Import ever mints: 12 hex of the account key's hash,
        // a dash, 16 hex of the content hash, ".png". Anything else in
        // settings.json — "../../x", an absolute path, a backslash — is refused
        // before any disk access, so a hand-edited file cannot point the app at
        // an arbitrary file.
        private static readonly Regex Minted = new(
            @"^[0-9a-f]{12}-[0-9a-f]{16}\.png$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static bool IsSafeFileName(string? fileName) =>
            fileName is not null && Minted.IsMatch(fileName);
    }
}
