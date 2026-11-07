using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Orbweaver
{
    // Every Claude Code config directory on this machine: the default ~/.claude
    // plus each extra account the user runs out of a CLAUDE_CONFIG_DIR alias.
    //
    // Pulled out into its own file because two separate rules need the same
    // list and were each getting it wrong in a different way. TranscriptReader
    // has built it inline twice since before this existed, which is where the
    // shape comes from — a ".claude" first, then ClaudeCodeProfileDirs, all
    // relative to $HOME — and it is deliberately not refactored to call this:
    // it works, and rewriting a working transcript hunt is not this branch's
    // business. Anything new asks here.
    //
    // home is a parameter with the real one as its default, the same seam
    // TranscriptReader.FindTranscriptFor uses, so a test can point the walk at
    // a temp directory rather than at the machine's own accounts.
    internal static class ClaudeConfigRoots
    {
        internal static IReadOnlyList<string> All(string? home = null)
        {
            home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var roots = new List<string> { Path.Combine(home, ".claude") };

            foreach (var extra in OrbweaverSettings.ClaudeCodeProfileDirs)
            {
                // A blank entry is what a half-finished settings edit leaves
                // behind, and Path.Combine would quietly answer $HOME for it —
                // which is not a config root, but *is* a directory that exists,
                // so it would be asked and would fail rather than being skipped.
                if (string.IsNullOrWhiteSpace(extra)) continue;

                var full = Path.Combine(home, extra.Trim());

                // Someone listing ".claude" alongside the default is not an
                // error worth refusing, but it must not double the work: every
                // caller here either launches a subprocess per root or stats a
                // file per root, and the second answer can only ever repeat the
                // first.
                if (!roots.Contains(full, StringComparer.Ordinal)) roots.Add(full);
            }

            return roots;
        }

        // --- discovery (CB-307) ---------------------------------------------------
        //
        // Warren's rule: a session under any Claude Code config dir behaves like
        // one under ~/.claude — orbs, waiting, served to a peer, forms, typing —
        // with no per-dir list to keep. So the dirs are found, not listed: every
        // ~/.claude and ~/.claude-<name> that holds a projects/ or sessions/
        // folder, which Claude Code creates in a config dir it has run in.
        // Measured on the MacBook 2026-11-07: .claude, .claude-board,
        // .claude-loginid and .claude-sundaysky all hold both;
        // .claude-memory-merge-backup and .claude-work hold neither and are not
        // config dirs. The settings lists still count — a dir named there is
        // always used, found or not — so discovery only ever adds.
        //
        // Deliberately not used for credentials (ClaudeCliCredentials keeps
        // All): each root there is a Keychain read, and an account nobody listed
        // is exactly the one whose first read can raise a Keychain prompt over a
        // headless app — the CB-301 freeze.

        // Whether a directory under home is a Claude Code config dir, by name
        // and by what it holds.
        internal static bool IsConfigDirName(string name) =>
            name == ".claude" || (name.StartsWith(".claude-", StringComparison.Ordinal) && name.Length > ".claude-".Length);

        internal static bool LooksLikeConfigDir(string path, Func<string, bool> dirExists) =>
            dirExists(Path.Combine(path, "projects")) || dirExists(Path.Combine(path, "sessions"));

        // The config dirs found under home, as names relative to it (".claude",
        // ".claude-board"), ~/.claude first and the rest in name order. The two
        // seams are the file system, so the rule is tested on real shapes
        // without a home of its own.
        internal static IReadOnlyList<string> Discover(
            string home, Func<string, IEnumerable<string>> childDirs, Func<string, bool> dirExists)
        {
            IEnumerable<string> names;
            try { names = childDirs(home).Select(Path.GetFileName).Where(n => n is not null)!; }
            catch { return Array.Empty<string>(); }

            return names
                .Where(n => IsConfigDirName(n!))
                .Where(n => LooksLikeConfigDir(Path.Combine(home, n!), dirExists))
                .OrderBy(n => n == ".claude" ? 0 : 1)
                .ThenBy(n => n, StringComparer.Ordinal)
                .Select(n => n!)
                .ToList();
        }

        // The configured roots (All) with every discovered one after them, as
        // full paths. What every session consumer asks: the status of a waiting
        // session, the registry a message goes through, a persona's user dirs,
        // the dirs a peer is served from.
        internal static IReadOnlyList<string> AllWithDiscovered(string? home = null)
        {
            home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var roots = All(home).ToList();
            foreach (var name in DiscoveredCached(home))
            {
                var full = Path.Combine(home, name);
                if (!roots.Contains(full, StringComparer.Ordinal)) roots.Add(full);
            }
            return roots;
        }

        // Discovery is a directory listing of home, and some callers ask per
        // session per scan (SessionPark), so the answer is held for a while. A
        // new account shows up within that window; nothing here needs it
        // sooner.
        internal static readonly TimeSpan DiscoveryHeldFor = TimeSpan.FromSeconds(30);
        internal static Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;
        internal static Func<string, IEnumerable<string>> ChildDirs { get; set; } = Directory.EnumerateDirectories;
        internal static Func<string, bool> DirExists { get; set; } = Directory.Exists;

        private static readonly object Gate = new();
        private static readonly Dictionary<string, (DateTime At, IReadOnlyList<string> Names)> Held = new(StringComparer.Ordinal);

        internal static IReadOnlyList<string> DiscoveredCached(string home)
        {
            var now = Now();
            lock (Gate)
                if (Held.TryGetValue(home, out var held) && now - held.At < DiscoveryHeldFor) return held.Names;

            var names = Discover(home, ChildDirs, DirExists);
            lock (Gate) Held[home] = (now, names);
            return names;
        }

        internal static void ForgetDiscovered() { lock (Gate) Held.Clear(); }

        // The config root a transcript lives under: the parent of the "projects"
        // segment, so ~/.claude/projects/<slug>/<id>.jsonl gives ~/.claude. The
        // layout is fixed — projects, a slug, the file — so the segment is read
        // at that position rather than searched for, which keeps a home directory
        // or slug that is itself called "projects" from shifting the answer. Null
        // when the path is not that shape, which callers read as "unknown, use the
        // default account". Both separators are accepted so a Windows path reads
        // the same in a test on a Mac.
        internal static string? RootOfTranscript(string? transcriptPath)
        {
            if (string.IsNullOrWhiteSpace(transcriptPath)) return null;

            var parts = transcriptPath.Split('/', '\\');
            var at = parts.Length - 3;
            if (at < 1 || parts[at] != "projects") return null;

            var sep = transcriptPath.Contains('/') ? "/" : "\\";
            var root = string.Join(sep, parts[..at]);
            return root.Length == 0 ? sep : root;
        }
    }
}
