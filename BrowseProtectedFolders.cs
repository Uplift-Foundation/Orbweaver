namespace Orbweaver
{
    // CB-357: the folders macOS keeps behind a privacy prompt (TCC), which the folder picker must
    // never read inside.
    //
    // A listing draws three markers per child (git, CLAUDE.md, persona), and each is a read inside
    // that child. Inside Desktop, Documents, Downloads and the rest of this list, or inside a
    // removable or network volume, an app that has not been granted access makes macOS ask the user,
    // and on a headless or unattended Mac nobody can answer: the read blocks until the listing's
    // read bound turns the whole folder into `unreadable`. So those children, and anything under
    // them, list as a plain folder with no badge. Every other child keeps its badges.
    //
    // Decided on the canonical path, after links are resolved, so a link inside a root that points
    // at ~/Documents is held to it. macOS only: Windows has no such prompt, and a folder called
    // Documents there is read as usual.
    internal static class ProtectedFolders
    {
        // Names under the home folder, and the mount point of removable and network volumes.
        internal static readonly string[] HomeNames =
            ["Desktop", "Documents", "Downloads", "Library", "Movies", "Music", "Pictures"];
        internal const string Volumes = "/Volumes";

        // `roots` are the folders the desk was told to browse. A volume the user chose as a root is
        // not held back: they answered its prompt when they chose it, and the shared v11 vectors
        // (second_root) say a root on an external volume lists with its badges. Anything else under
        // /Volumes is.
        internal static bool IsProtected(IBrowseFileSystem fs, string canonical, IEnumerable<string> roots)
        {
            if (fs.IsWindows) return false;
            if (BrowseRules.Within(canonical, Volumes, fs) && !roots.Any(r => BrowseRules.Within(canonical, r, fs)))
                return true;

            foreach (var name in HomeNames)
                if (BrowseRules.Within(canonical, BrowseRules.Join(fs.Home, name, false), fs)) return true;

            return false;
        }
    }
}
