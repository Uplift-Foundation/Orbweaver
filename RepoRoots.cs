namespace Orbweaver
{
    // CB-281: the git repository a working directory belongs to, for "Auto-group
    // orbs by Directory". The PM's call: group by repository root, so a Claude
    // Code worktree (<repo>/.claude/worktrees/<name>) and a sibling checkout made
    // with `git worktree add` both fold into the repository they came from.
    //
    // The walk is pure over a small filesystem seam, so every rule is a test with
    // a fake tree and nothing touches the disk. RepoRoots.Cached is the disk-
    // backed, time-limited wrapper SessionManager uses, since a scan asks for
    // every orb every few seconds.
    internal sealed record RepoFs(
        Func<string, bool> DirectoryExists,
        Func<string, bool> FileExists,
        Func<string, string?> ReadText)
    {
        internal static readonly RepoFs Real = new(
            Directory.Exists, File.Exists,
            path =>
            {
                try { return File.ReadAllText(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
            });
    }

    internal static class RepoRoots
    {
        // How long an answer is kept. A cwd does not change repository under a
        // running session, but a `git init` can happen in a folder that had none.
        internal static readonly TimeSpan HeldFor = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The root of the repository containing <paramref name="cwd"/>, or null when it is
        /// in none (or is empty). Walks up looking for <c>.git</c>, a directory or a file. A
        /// <c>.git</c> file is a linked worktree's pointer (<c>gitdir: &lt;main&gt;/.git/worktrees/&lt;n&gt;</c>),
        /// and resolves to the main repository; any other pointer (a submodule) leaves the
        /// directory holding it as its own root. A path that no longer exists still walks up
        /// by name, so a deleted worktree folds into the repository it sat inside.
        /// </summary>
        internal static string? Find(string? cwd, RepoFs fs)
        {
            if (string.IsNullOrWhiteSpace(cwd)) return null;

            // The loop ends when ParentOf runs out of parents (the root), which is the
            // one way out besides a hit.
            for (string? dir = Trim(cwd.Trim()); dir is not null; dir = ParentOf(dir))
            {
                var dotGit = dir.EndsWith('/') || dir.EndsWith('\\') ? dir + ".git" : dir + Separator(dir) + ".git";
                if (fs.DirectoryExists(dotGit)) return dir;
                if (fs.FileExists(dotGit)) return MainRootOfPointer(dir, fs.ReadText(dotGit)) ?? dir;
            }

            return null;
        }

        // "gitdir: /repo/.git/worktrees/name" -> "/repo". Relative pointers are
        // taken against the folder holding the .git file. Null when the text is
        // not a worktree pointer.
        private static string? MainRootOfPointer(string holder, string? text)
        {
            const string prefix = "gitdir:";
            var line = (text ?? "").Split('\n', 2)[0].Trim();
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;

            var target = line[prefix.Length..].Trim().Replace('\\', '/');
            if (target.Length == 0) return null;
            if (!IsAbsolute(target)) target = holder.Replace('\\', '/') + "/" + target;
            target = Collapse(target);

            const string marker = "/.git/worktrees/";
            var at = target.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return at > 0 ? target[..at] : null;
        }

        private static bool IsAbsolute(string path) =>
            path.StartsWith('/') || (path.Length >= 2 && path[1] == ':');

        // Resolves "." and ".." segments, which a relative pointer contains.
        private static string Collapse(string path)
        {
            var lead = path.StartsWith('/') ? "/" : "";
            var kept = new List<string>();
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".") continue;
                if (part == ".." && kept.Count > 0 && kept[^1] != "..") kept.RemoveAt(kept.Count - 1);
                else kept.Add(part);
            }

            return lead + string.Join('/', kept);
        }

        private static string Trim(string path)
        {
            while (path.Length > 1 && (path.EndsWith('/') || path.EndsWith('\\')) && !path.EndsWith(":/") && !path.EndsWith(":\\"))
                path = path[..^1];
            return path;
        }

        private static char Separator(string path) => path.Contains('\\') && !path.Contains('/') ? '\\' : '/';

        // The parent folder by name only, for either separator, or null at the root.
        private static string? ParentOf(string dir)
        {
            var cut = Math.Max(dir.LastIndexOf('/'), dir.LastIndexOf('\\'));
            if (cut < 0) return null;
            if (cut == 0) return dir.Length > 1 ? dir[..1] : null;
            // "c:\" is a root: the cut is right after the drive colon.
            if (cut == 2 && dir[1] == ':') return dir.Length > 3 ? dir[..3] : null;
            return dir[..cut];
        }

        // ---- cached, disk-backed --------------------------------------------

        internal static Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;
        internal static RepoFs Fs { get; set; } = RepoFs.Real;

        private static readonly object Gate = new();
        private static readonly Dictionary<string, (DateTime At, string? Root)> Held = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The same answer as <see cref="Find"/> over the real disk, held for <see cref="HeldFor"/>.</summary>
        internal static string? Cached(string? cwd)
        {
            if (string.IsNullOrWhiteSpace(cwd)) return null;

            var now = Now();
            lock (Gate)
                if (Held.TryGetValue(cwd, out var held) && now - held.At < HeldFor) return held.Root;

            var root = Find(cwd, Fs);
            lock (Gate)
            {
                if (Held.Count > 512) Held.Clear();
                Held[cwd] = (now, root);
            }
            return root;
        }

        internal static void Forget() { lock (Gate) Held.Clear(); }
    }
}
