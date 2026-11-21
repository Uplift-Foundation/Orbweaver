namespace Orbweaver
{
    // CB-397: the physical spelling of a folder, so a launch asked for through
    // a link (/tmp/x on a Mac) is recognised when the CLI reports its real
    // path (/private/tmp/x). .NET has no realpath, and
    // PersonaFiles.CanonicalDirectory only resolves a link in the last
    // component, so this walks the components from the root.
    //
    // Pure over one seam: "does this path exist, and if it is a link, where
    // does it point". Anything unexpected (an exception, a missing component,
    // a loop) returns the string as it was given: this only ever adds a
    // second spelling to compare with, never changes what is started.
    internal static class FolderResolver
    {
        private const int MaxLinks = 40;

        internal static string Resolve(string path, Func<string, (bool Exists, string? Target)> inspect)
        {
            try
            {
                var budget = MaxLinks;
                return Walk(path, inspect, ref budget) ?? path;
            }
            catch (Exception)
            {
                return path;
            }
        }

        private static string? Walk(string path, Func<string, (bool Exists, string? Target)> inspect, ref int budget)
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return null;

            var current = root;
            foreach (var part in path[root.Length..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                var (exists, target) = inspect(current);
                if (!exists) return null;
                if (target is null) continue;
                if (--budget < 0) return null;

                // A relative target is relative to the folder holding the link.
                var next = Path.IsPathRooted(target)
                    ? target
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current) ?? root, target));
                // The target may itself pass through links, or be one.
                if (Walk(next, inspect, ref budget) is not { } resolved) return null;
                current = resolved;
            }
            return current;
        }

        // The real filesystem call: one line into System.IO.
        [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
        private static (bool Exists, string? Target) InspectReal(string path)
        {
            var info = new DirectoryInfo(path);
            return (info.Exists || File.Exists(path), info.LinkTarget);
        }

        internal static string ResolveReal(string path) => Resolve(path, InspectReal);
    }
}
